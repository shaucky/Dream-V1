package dream.engine.audio
{
    import flash.media.Sound;
    import flash.utils.ByteArray;
    import flash.utils.Endian;

    /**
     * 音频片段资源：封装解码后的 flash.media.Sound。
     *
     * 加载：由 ResourceManager 从音频文件（.mp3/.wav）读取字节后调用 fromBytes 构造。
     *   - mp3：loadCompressedDataFromByteArray（压缩格式直接解码），帧头解析采样率/声道
     *   - wav：解析 RIFF 头并提取 16/8-bit PCM，经 loadPCMFromByteArray 播放
     *
     * 变速（磁带式）：pitch 通过"改声明采样率"实现（速度与音调同向变化）。
     *   - wav：解析时已持有 float PCM，直接以 sampleRate×pitch 重建 Sound
     *   - mp3：首次变速时惰性 extract 出 float PCM 再重建；失败则退回原速（pitch=1）
     *   - pitch=1 零开销（直接用原 Sound）；变速结果按 pitch 缓存
     *
     * 播放控制（pan/fade/暂停/句柄）在 AudioManager/AudioPlayback。
     */
    public final class AudioClip
    {
        /** 解码后的声音数据（原始采样率）。 */
        public var sound:Sound;

        // 变速支持：float PCM 与源采样率/声道（pitch=1 时零开销，惰性填充）。
        private var _pcm:ByteArray;
        private var _pcmSamples:int = 0;        // 总样本数（所有声道）
        private var _pcmSampleRate:Number = 44100;
        private var _pcmStereo:Boolean = false;
        private var _pcmReady:Boolean = false;  // _pcm 已填充
        private var _pcmFailed:Boolean = false; // 已尝试但失败（如 extract 不支持）
        private var _pitchCache:Object = { };   // pitch → Sound

        public function AudioClip(sound:Sound = null)
        {
            this.sound = sound;
        }

        /** 从文件字节构造。isMp3=true 走压缩解码，false 走 WAV PCM 解析。失败返回 null。 */
        public static function fromBytes(bytes:ByteArray, isMp3:Boolean):AudioClip
        {
            try
            {
                if (isMp3)
                {
                    var s:Sound = new Sound();
                    // AIR 51 签名：loadCompressedDataFromByteArray(bytes, bytesLength)。
                    s.loadCompressedDataFromByteArray(bytes, bytes.length);
                    var h:Object = parseMp3Header(bytes);
                    var clip:AudioClip = new AudioClip(s);
                    clip._pcmSampleRate = h.sampleRate;
                    clip._pcmStereo = h.stereo;
                    return clip;
                }
                var w:Object = parseWav(bytes); // {sound, pcm, totalSamples, sampleRate, stereo}
                var clip2:AudioClip = new AudioClip(w.sound);
                clip2._pcm = w.pcm;
                clip2._pcmSamples = w.totalSamples;
                clip2._pcmSampleRate = w.sampleRate;
                clip2._pcmStereo = w.stereo;
                clip2._pcmReady = true;
                return clip2;
            }
            catch (e:Error)
            {
                return null;
            }
        }

        /**
         * 获取指定变速倍率的 Sound。pitch=1 返回原 Sound（零开销）；
         * 其余按"磁带式"重建并按 pitch 缓存。变速失败（无法取 PCM）时回退原 Sound。
         */
        public function soundWithPitch(pitch:Number):Sound
        {
            if (sound == null) return null;
            if (pitch <= 0) pitch = 0.1;
            if (Math.abs(pitch - 1) < 0.001) return sound;
            var cached:Sound = _pitchCache[pitch];
            if (cached != null) return cached;
            var s:Sound = buildPitched(pitch);
            if (s != null) _pitchCache[pitch] = s;
            return (s != null) ? s : sound;
        }

        /** 磁带式：以 sampleRate×pitch 重新声明 PCM 播放（变快则音调升高）。 */
        private function buildPitched(pitch:Number):Sound
        {
            if (!ensurePcm()) return null;
            var snd:Sound = new Sound();
            // AIR 51 签名：loadPCMFromByteArray(bytes, samples, format, stereo, sampleRate)。
            snd.loadPCMFromByteArray(_pcm, _pcmSamples, "float", _pcmStereo, _pcmSampleRate * pitch);
            return snd;
        }

        /** 惰性构建 float PCM（mp3 走 Sound.extract；wav 解析时已就绪）。 */
        private function ensurePcm():Boolean
        {
            if (_pcmReady) return true;
            if (_pcmFailed || sound == null) return false;
            try
            {
                var buf:ByteArray = new ByteArray();
                // extract 返回每声道样本数；输出为交错 float（-1..1），采样率与源一致。
                var extracted:Number = sound.extract(buf, 0x3FFFFFFF, 0);
                if (extracted <= 0) { _pcmFailed = true; return false; }
                _pcm = buf;
                _pcmSamples = int(extracted) * (_pcmStereo ? 2 : 1);
                _pcmReady = true;
                return true;
            }
            catch (e:Error)
            {
                _pcmFailed = true;
                return false;
            }
        }

        // ── WAV / MP3 解析 ──

        /** 解析 WAV（RIFF / PCM），返回 {sound, pcm, totalSamples, sampleRate, stereo}。 */
        private static function parseWav(bytes:ByteArray):Object
        {
            bytes.position = 0;
            if (bytes.readUTFBytes(4) != "RIFF") throw new Error("Not a RIFF file");
            bytes.position = 8;
            if (bytes.readUTFBytes(4) != "WAVE") throw new Error("Not a WAVE file");

            var channels:int = 1;
            var sampleRate:Number = 44100;
            var bits:int = 16;
            var dataPos:int = -1;
            var dataLen:int = 0;

            // 遍历块：定位 fmt 与 data。
            while (bytes.bytesAvailable > 8)
            {
                var chunkId:String = bytes.readUTFBytes(4);
                var chunkSize:int = bytes.readUnsignedInt();
                if (chunkId == "fmt ")
                {
                    bytes.position += 2; // audioFormat（仅支持 PCM，跳过）
                    channels = bytes.readUnsignedShort();
                    sampleRate = bytes.readUnsignedInt();
                    bytes.position += 6; // byteRate + blockAlign
                    bits = bytes.readUnsignedShort();
                    // fmt 块可能含扩展（chunkSize > 16），跳过剩余。
                    var consumed:int = 16;
                    if (chunkSize > consumed) bytes.position += (chunkSize - consumed);
                }
                else if (chunkId == "data")
                {
                    dataPos = bytes.position;
                    dataLen = chunkSize;
                    break;
                }
                else
                {
                    bytes.position += chunkSize;
                }
            }

            if (dataPos < 0) throw new Error("No data chunk");

            // 提取 PCM → 32-bit float（-1..1），按声道交错写入。
            bytes.position = dataPos;
            bytes.endian = Endian.LITTLE_ENDIAN;
            var bytesPerSample:int = bits / 8;
            var totalSamples:int = dataLen / bytesPerSample;
            var frames:int = totalSamples / channels; // 每声道样本数

            var pcm:ByteArray = new ByteArray();
            if (bits == 16)
            {
                for (var i:int = 0; i < frames; i++)
                {
                    for (var c:int = 0; c < channels; c++)
                        pcm.writeFloat(bytes.readShort() / 32768.0);
                }
            }
            else if (bits == 8)
            {
                for (var j:int = 0; j < frames; j++)
                {
                    for (var c2:int = 0; c2 < channels; c2++)
                        pcm.writeFloat((bytes.readUnsignedByte() - 128) / 128.0);
                }
            }
            else
            {
                throw new Error("Unsupported WAV bit depth: " + bits);
            }

            var snd:Sound = new Sound();
            // AIR 51 签名：loadPCMFromByteArray(bytes, samples, format, stereo, sampleRate)。
            snd.loadPCMFromByteArray(pcm, totalSamples, "float", channels > 1, sampleRate);
            return { sound: snd, pcm: pcm, totalSamples: totalSamples, sampleRate: sampleRate, stereo: channels > 1 };
        }

        /**
         * 解析 MP3 首帧头获取采样率/声道。找不到合法帧时回退默认 44100/立体声。
         * 帧头布局：FF E(版本2位 层2位) (比特率4 采样率2 ...) (声道模式2 ...)。
         */
        private static function parseMp3Header(bytes:ByteArray):Object
        {
            var sampleRate:Number = 44100;
            var stereo:Boolean = true;
            bytes.position = 0;
            while (bytes.bytesAvailable > 4)
            {
                if (bytes.readUnsignedByte() != 0xFF) continue;
                var b1:int = bytes.readUnsignedByte();
                if ((b1 & 0xE0) != 0xE0) continue; // 帧同步 11 位
                var b2:int = bytes.readUnsignedByte();
                var b3:int = bytes.readUnsignedByte();

                var version:int = (b1 >> 3) & 3; // 3=MPEG1 2=MPEG2 0=MPEG2.5
                if (((b1 >> 1) & 3) != 1) { continue; } // 仅 Layer III
                var srIdx:int = (b2 >> 2) & 3;
                if (srIdx == 3) continue; // 非法采样率索引

                if (version == 3)
                    sampleRate = (srIdx == 0) ? 44100 : (srIdx == 1) ? 48000 : 32000;
                else if (version == 2)
                    sampleRate = (srIdx == 0) ? 22050 : (srIdx == 1) ? 24000 : 16000;
                else
                    sampleRate = (srIdx == 0) ? 11025 : (srIdx == 1) ? 12000 : 8000;

                var chMode:int = (b3 >> 6) & 3;
                stereo = chMode != 3; // 3 = mono
                break;
            }
            return { sampleRate: sampleRate, stereo: stereo };
        }
    }
}

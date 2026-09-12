using System;
using System.IO;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 音频文件元数据读取器：读取 .wav/.mp3 的时长/采样率/声道信息（不引入外部依赖）。
    ///   - wav：解析 RIFF/fmt/data 头，时长 = dataSize / byteRate（精确）
    ///   - mp3：扫描帧同步字解析首帧头采样率/声道，按 CBR 估算时长（VBR 文件有偏差）
    /// 读取失败返回全 0。
    /// </summary>
    internal static class AudioMetadataReader
    {
        /// <summary>读取结果。estimated 表示时长为估算值。</summary>
        public readonly record struct AudioMeta(double DurationSeconds, int SampleRate, int Channels, bool Estimated);

        public static AudioMeta Read(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".wav" && ext != ".mp3") return default;
            try
            {
                using var fs = File.OpenRead(path);
                if (fs.Length < 32) return default;
                return ext == ".wav" ? ReadWav(fs) : ReadMp3(fs);
            }
            catch
            {
                return default;
            }
        }

        private static AudioMeta ReadWav(Stream fs)
        {
            var reader = new BinaryReader(fs);
            if (reader.ReadInt32() != 0x46464952) return default; // "RIFF"
            reader.ReadInt32();
            if (reader.ReadInt32() != 0x45564157) return default; // "WAVE"

            int channels = 0, sampleRate = 0, byteRate = 0;
            long dataSize = 0;
            while (fs.Position + 8 <= fs.Length)
            {
                var id = reader.ReadInt32();
                var size = reader.ReadInt32();
                if (id == 0x20746D66) // "fmt "
                {
                    reader.ReadInt16();              // audioFormat（仅支持 PCM）
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    byteRate = reader.ReadInt32();
                    fs.Position += Math.Max(0, size - 16);
                }
                else if (id == 0x61746164) // "data"
                {
                    dataSize = size;
                    break;
                }
                else
                {
                    fs.Position += size;
                }
            }
            if (byteRate <= 0 || dataSize <= 0) return default;
            return new AudioMeta((double)dataSize / byteRate, sampleRate, channels, false);
        }

        private static AudioMeta ReadMp3(Stream fs)
        {
            int sampleRate = 44100, channels = 2, bitrateKbps = 0;
            var reader = new BinaryReader(fs);
            while (fs.Position + 4 <= fs.Length)
            {
                if (reader.ReadByte() != 0xFF) continue;
                if (fs.Position >= fs.Length) break;
                var b1 = reader.ReadByte();
                if ((b1 & 0xE0) != 0xE0) continue; // 帧同步 11 位
                var b2 = reader.ReadByte();
                var b3 = reader.ReadByte();

                var version = (b1 >> 3) & 3;   // 3=MPEG1 2=MPEG2 0=MPEG2.5
                var layer = (b1 >> 1) & 3;
                if (layer != 1) continue;      // 仅 Layer III
                var srIdx = (b2 >> 2) & 3;
                if (srIdx == 3) continue;
                var bitrateIdx = b2 >> 4;
                if (bitrateIdx == 0 || bitrateIdx == 15) continue;

                if (version == 3)
                {
                    sampleRate = srIdx switch { 0 => 44100, 1 => 48000, _ => 32000 };
                    bitrateKbps = new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }[bitrateIdx];
                }
                else
                {
                    // MPEG2 / MPEG2.5：采样率减半或更小。
                    sampleRate = version == 2
                        ? srIdx switch { 0 => 22050, 1 => 24000, _ => 16000 }
                        : srIdx switch { 0 => 11025, 1 => 12000, _ => 8000 };
                    bitrateKbps = new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }[bitrateIdx];
                }

                var chMode = (b3 >> 6) & 3;
                channels = chMode == 3 ? 1 : 2; // 3 = mono
                break;
            }

            if (bitrateKbps <= 0) return default;
            // CBR 估算：时长 ≈ 文件字节数 × 8 / 比特率。
            var duration = fs.Length * 8.0 / (bitrateKbps * 1000.0);
            return new AudioMeta(duration, sampleRate, channels, true);
        }
    }
}

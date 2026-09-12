package game
{
    import flash.events.SampleDataEvent;
    import flash.media.Sound;
    import flash.media.SoundTransform;
    import flash.utils.ByteArray;

    /**
     * 程序化音效：不依赖任何音频资源，用 PCM 合成 8-bit 风格短音
     * （翅膀、得分、撞击）。样本预渲染进 ByteArray，播放时经
     * SampleDataEvent 流式供给（loadPCMFromByteArray 受 40K AMF 限制，不可用）。
     * 首次播放前惰性生成；每次播放新建 Sound 实例，天然支持重叠播放。
     */
    public final class Sfx
    {
        private static const SampleRate:Number = 44100;
        private static const Block:int = 2048;

        private static var _wing:ByteArray;
        private static var _point:ByteArray;
        private static var _hit:ByteArray;

        public static function playWing():void { ensure(); play(_wing, 0.9); }
        public static function playPoint():void { ensure(); play(_point, 0.7); }
        public static function playHit():void { ensure(); play(_hit, 1.0); }

        /** 流式播放预渲染样本：e.position 索引取数，尾部空块自然结束。 */
        private static function play(samples:ByteArray, volume:Number):void
        {
            var total:int = samples.length / 4;
            var s:Sound = new Sound();
            s.addEventListener(SampleDataEvent.SAMPLE_DATA, function(e:SampleDataEvent):void
            {
                for (var i:int = 0; i < Block; i++)
                {
                    var idx:int = e.position + i;
                    if (idx >= total) break;
                    samples.position = idx * 4;
                    e.data.writeFloat(samples.readFloat());
                }
            });
            s.play(0, 0, new SoundTransform(volume));
        }

        private static function ensure():void
        {
            if (_wing != null) return;
            _wing = concat([tone(340, 940, 0.09, 0.35)]);
            _point = concat([
                tone(920, 920, 0.06, 0.3),
                tone(1240, 1240, 0.09, 0.3)]);
            _hit = concat([
                tone(240, 70, 0.16, 0.5),
                tone(160, 60, 0.14, 0.35)]);
        }

        /** 拼接多段样本。 */
        private static function concat(segments:Array):ByteArray
        {
            var merged:ByteArray = new ByteArray();
            merged.endian = "littleEndian";
            for each (var seg:ByteArray in segments)
            {
                seg.position = 0;
                merged.writeBytes(seg);
            }
            return merged;
        }

        /**
         * 生成一段单声道 float32 PCM 音调：
         * 频率 f0 → f1 线性滑动，软方波（基频 + 三次谐波），快攻缓释包络。
         */
        private static function tone(f0:Number, f1:Number, dur:Number, vol:Number):ByteArray
        {
            var n:int = int(dur * SampleRate);
            var bytes:ByteArray = new ByteArray();
            bytes.endian = "littleEndian";
            var phase:Number = 0;
            for (var i:int = 0; i < n; i++)
            {
                var t:Number = i / n;
                var f:Number = f0 + (f1 - f0) * t;
                phase += 2 * Math.PI * f / SampleRate;
                var s:Number = Math.sin(phase) + 0.33 * Math.sin(phase * 3);
                s /= 1.33;
                var attack:Number = Math.min(1, i / (SampleRate * 0.004));
                var env:Number = attack * Math.pow(1 - t, 1.6);
                bytes.writeFloat(s * env * vol);
            }
            return bytes;
        }
    }
}

package dream.engine.audio
{
    /**
     * 混音组：一组音频的音量通道（如 Music / SFX）。
     *
     * 音量链：最终音量 = 句柄音量(volume) × 组音量(group.volume) × 主音量(masterVolume)，
     * 空间音频再叠加距离衰减。AudioManager 内置 "music" 与 "sfx" 两组（getGroup 不存在时自动创建）。
     *
     * 用法：
     *   AudioManager.current.setGroupVolume("sfx", 0.5); // 所有 SFX 减半
     */
    public final class AudioGroup
    {
        public var name:String;

        private var _volume:Number = 1.0;

        public function AudioGroup(name:String)
        {
            this.name = name;
        }

        /** 组音量（0..1）。 */
        public function get volume():Number { return _volume; }
        public function set volume(v:Number):void
        {
            _volume = v;
            if (_volume < 0) _volume = 0;
            if (_volume > 1) _volume = 1;
        }
    }
}

package dream.engine.comm
{
    CONFIG::STUDIO
    {
        import dream.engine.audio.AudioManager;

        /**
         * 音频音量设置处理器：响应 Studio 全局音频面板的主音量/组音量调节。
         *
         * payload（两者可同时出现）：
         *   {master: 0.5}             → 主音量
         *   {group: "sfx", volume: 0.3} → 组音量
         */
        public final class AudioSettingsHandler implements IMessageHandler
        {
            public function AudioSettingsHandler()
            {
            }

            public function get messageType():String
            {
                return MessageTypes.AudioSetVolume;
            }

            public function handle(message:Message, channel:IChannel):void
            {
                var p:Object = message.payload;
                if (p == null) return;
                var am:AudioManager = AudioManager.current;
                if (am == null) return;

                if (p.master != null && !isNaN(p.master))
                    am.masterVolume = Number(p.master);

                if (p.group != null && p.volume != null && !isNaN(p.volume))
                    am.setGroupVolume(String(p.group), Number(p.volume));
            }
        }
    }
}

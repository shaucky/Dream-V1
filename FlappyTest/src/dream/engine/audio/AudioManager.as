package dream.engine.audio
{
    /**
     * 音频管理器：服务定位器（AudioManager.current），封装底层声音播放。
     *
     * 能力：
     *   - 播放/停止/主音量/混音组/OneShot/并发上限（优先级抢占）
     *   - 每帧 update(dt) 驱动淡入淡出（由 DreamEngine 帧循环调用）
     *   - play 支持 pan/pitch/priority，供组件与脚本控制
     *
     * 用法：
     *   var p:AudioPlayback = AudioManager.current.play(clip, true, 0.8, "music");
     *   AudioManager.current.setGroupVolume("sfx", 0.5);
     *   AudioManager.current.playOneShot(clip, 0.8);
     */
    public final class AudioManager
    {
        /** 当前实例（服务定位器），由 DreamEngine 初始化时创建。 */
        public static var current:AudioManager;

        /** 同时播放上限（超过时低优先级句柄被顶替）。 */
        public var maxConcurrentSounds:int = 32;

        private var _masterVolume:Number = 1.0;
        private var _active:Array = []; // 活动播放句柄（AudioPlayback）
        private var _groups:Object = { }; // 组名 → AudioGroup

        public function AudioManager()
        {
            current = this;
            // 内置混音组：音乐与音效。
            ensureGroup("music");
            ensureGroup("sfx");
        }

        /** 主音量（0..1）。变化实时应用到所有活动播放。 */
        public function get masterVolume():Number { return _masterVolume; }
        public function set masterVolume(v:Number):void
        {
            _masterVolume = v;
            if (_masterVolume < 0) _masterVolume = 0;
            if (_masterVolume > 1) _masterVolume = 1;
            for each (var p:AudioPlayback in _active)
                p.refreshVolume();
        }

        /**
         * 获取混音组；不存在则创建并返回（组名自动注册，后续可调组音量）。
         */
        public function getGroup(name:String):AudioGroup
        {
            if (name == null) name = "";
            var g:AudioGroup = _groups[name] as AudioGroup;
            if (g == null) g = ensureGroup(name);
            return g;
        }

        /** 组音量（0..1）。未注册的组返回 1（全音量）。 */
        public function groupVolume(name:String):Number
        {
            var g:AudioGroup = _groups[name] as AudioGroup;
            return g != null ? g.volume : 1;
        }

        /** 设置组音量并实时应用到该组所有活动播放。 */
        public function setGroupVolume(name:String, v:Number):void
        {
            getGroup(name).volume = v;
            for each (var p:AudioPlayback in _active)
            {
                if (p.groupName == name)
                    p.refreshVolume();
            }
        }

        /**
         * 播放音频，返回可控制的播放句柄（可 stop / pause / 调音量 / seek / fade）。
         * clip 无效返回 null。参数：
         *   group    混音组（null/空串不参与组音量）
         *   pan      声像（-1 左 ~ 1 右）
         *   pitch    变速倍率（磁带式；1 正常）
         *   priority 优先级（0~255，并发超限时低者被顶替）
         * 并发超限：若存在优先级更低（同优先级更早）的活动句柄，顶替它；否则本次播放被拒（返回 null）。
         */
        public function play(clip:AudioClip, loop:Boolean = false, volume:Number = 1.0, group:String = null,
                             pan:Number = 0, pitch:Number = 1, priority:int = 128):AudioPlayback
        {
            if (clip == null || clip.sound == null) return null;
            var g:AudioGroup = (group != null && group.length > 0) ? getGroup(group) : null;
            var p:AudioPlayback = new AudioPlayback(clip, loop, volume, this, g, pan, pitch, priority);
            p.start();
            if (p.finished) return null;
            if (_active.length >= maxConcurrentSounds)
            {
                // 顶替最弱的句柄（优先级最低，同优先级取最早）。
                var victim:AudioPlayback = findVictim(priority);
                if (victim == null) { p.stop(); return null; }
                victim.stop();
            }
            _active.push(p);
            return p;
        }

        /**
         * 播放一次性音效（OneShot）：不返回句柄，播放完自动回收。
         * 仍计入活动列表，受主音量/组音量控制，可被 stopAll 统一停止。
         */
        public function playOneShot(clip:AudioClip, volume:Number = 1.0, group:String = "sfx",
                                    pan:Number = 0, pitch:Number = 1, priority:int = 128):void
        {
            play(clip, false, volume, group, pan, pitch, priority);
        }

        /** 停止所有播放（场景切换/销毁时使用）。 */
        public function stopAll():void
        {
            var snapshot:Array = _active.slice();
            for each (var p:AudioPlayback in snapshot)
                p.stop();
        }

        /** 暂停所有播放（保留位置，可全部 resume）。 */
        public function pauseAll():void
        {
            for each (var p:AudioPlayback in _active)
                p.pause();
        }

        /** 恢复所有暂停的播放。 */
        public function resumeAll():void
        {
            for each (var p:AudioPlayback in _active)
                p.resume();
        }

        /** 每帧驱动：推进淡入淡出等时间相关逻辑。由 DreamEngine 帧循环调用。 */
        public function update(dt:Number):void
        {
            // 淡入淡出推进（stop 内部会 unregister，遍历快照避免迭代中修改）。
            var snapshot:Array = _active.slice();
            for each (var p:AudioPlayback in snapshot)
                p.update(dt);
        }

        /** 由 AudioPlayback 完成/停止时调用，从活动列表移除。 */
        internal function unregister(p:AudioPlayback):void
        {
            var i:int = _active.indexOf(p);
            if (i >= 0) _active.splice(i, 1);
        }

        /** 找可被顶替的句柄：优先级最低；同优先级取最早播放的。无弱者返回 null。 */
        private function findVictim(newPriority:int):AudioPlayback
        {
            var victim:AudioPlayback = null;
            for each (var p:AudioPlayback in _active)
            {
                if (p.priority >= newPriority) continue; // 只能顶替更弱者
                if (victim == null || p.priority < victim.priority)
                    victim = p;
            }
            return victim;
        }

        /** 创建并注册混音组（已存在则原样返回）。 */
        private function ensureGroup(name:String):AudioGroup
        {
            var g:AudioGroup = _groups[name] as AudioGroup;
            if (g == null)
            {
                g = new AudioGroup(name);
                _groups[name] = g;
            }
            return g;
        }
    }
}

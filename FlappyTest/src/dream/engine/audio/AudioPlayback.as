package dream.engine.audio
{
    import flash.events.Event;
    import flash.media.Sound;
    import flash.media.SoundChannel;
    import flash.media.SoundTransform;

    /**
     * 播放句柄：封装一次播放（SoundChannel）的控制——stop / 暂停 / 音量 / 声像 / 进度 / 淡入淡出。
     * 由 AudioManager.play 创建；AudioSource 与脚本持有句柄控制播放。
     *
     * 音量链：最终音量 = 句柄音量(volume) × 组音量(group.volume) × 主音量(masterVolume) × 淡入淡出
     * × 空间衰减；master/组音量变化由 AudioManager 通知刷新，fade 由 AudioManager.update(dt) 驱动。
     *
     * 暂停语义：AIR SoundChannel 无 pause，记录当前位置后 stop 通道，resume 时按记录位置重播。
     * 循环播放通过 Sound.play 的 loops 大数实现；变速（pitch）走 AudioClip 磁带式重建。
     */
    public final class AudioPlayback
    {
        private var _clip:AudioClip;
        private var _loop:Boolean;
        private var _volume:Number;
        private var _pan:Number;
        private var _pitch:Number;
        private var _priority:int;
        private var _manager:AudioManager;
        private var _channel:SoundChannel;
        private var _stopped:Boolean;

        // 混音组（null 表示不参与组音量）
        private var _group:AudioGroup;

        // 空间音频参数（blend=0 时全部禁用）
        private var _sx:Number = 0;
        private var _sy:Number = 0;
        private var _spatialBlend:Number = 0;  // 0=纯2D  1=纯3D
        private var _minDistance:Number = 1;
        private var _maxDistance:Number = 10;

        // 暂停：记录暂停位置（毫秒），resume 时从该处重播。
        private var _paused:Boolean = false;
        private var _pausedAt:Number = 0;

        // 淡入淡出（由 AudioManager.update 驱动）：目标音量/时长/起始时间。
        private var _fading:Boolean = false;
        private var _fadeFrom:Number = 1;
        private var _fadeTo:Number = 1;
        private var _fadeElapsed:Number = 0;
        private var _fadeDuration:Number = 0;

        public function AudioPlayback(clip:AudioClip, loop:Boolean, volume:Number, manager:AudioManager,
                                      group:AudioGroup = null, pan:Number = 0, pitch:Number = 1,
                                      priority:int = 128)
        {
            _clip = clip;
            _loop = loop;
            _volume = volume;
            _manager = manager;
            _group = group;
            _pan = pan;
            _pitch = pitch;
            _priority = priority;
        }

        /** 开始播放（初始或 resume）。由 AudioManager 或 pause 恢复时调用。 */
        internal function start():void
        {
            if (_clip == null || _stopped) return;
            var snd:Sound = _clip.soundWithPitch(_pitch);
            if (snd == null) { _stopped = true; return; }
            _channel = snd.play(_pausedAt, _loop ? int.MAX_VALUE : 0);
            if (_channel == null)
            {
                _stopped = true;
                if (_manager != null) _manager.unregister(this);
                return;
            }
            _paused = false;
            applyTransform();
            _channel.addEventListener(Event.SOUND_COMPLETE, onComplete);
        }

        /** 停止播放并回收。重复调用安全。 */
        public function stop():void
        {
            if (_stopped) return;
            _stopped = true;
            _paused = false;
            _fading = false;
            if (_channel != null)
            {
                _channel.removeEventListener(Event.SOUND_COMPLETE, onComplete);
                _channel.stop();
                _channel = null;
            }
            if (_manager != null) _manager.unregister(this);
        }

        /** 暂停（保留播放位置）。暂停中的句柄仍在活动列表，可被 stopAll 停止。 */
        public function pause():void
        {
            if (_stopped || _paused) return;
            if (_channel != null)
            {
                _pausedAt = _channel.position;
                _channel.removeEventListener(Event.SOUND_COMPLETE, onComplete);
                _channel.stop();
                _channel = null;
            }
            _paused = true;
        }

        /** 从暂停位置继续播放。未暂停时无效果。 */
        public function resume():void
        {
            if (_stopped || !_paused) return;
            start();
        }

        /** 是否已停止/结束。 */
        public function get finished():Boolean { return _stopped; }

        /** 是否处于暂停状态。 */
        public function get paused():Boolean { return _paused && !_stopped; }

        /** 当前播放位置（秒）。 */
        public function get position():Number
        {
            if (_stopped) return 0;
            return (_channel != null ? _channel.position : _pausedAt) / 1000.0;
        }

        /** 跳转到指定位置（秒）。暂停中只记位置，播放中重建通道。 */
        public function seek(seconds:Number):void
        {
            if (_stopped) return;
            if (seconds < 0) seconds = 0;
            _pausedAt = seconds * 1000;
            if (_paused) return;
            // 重建通道（保留当前音量/声像/循环）。
            if (_channel != null)
            {
                _channel.removeEventListener(Event.SOUND_COMPLETE, onComplete);
                _channel.stop();
                _channel = null;
            }
            start();
        }

        /** 播放音量（0..1，不含主/组音量）。 */
        public function get volume():Number { return _volume; }
        public function set volume(v:Number):void
        {
            _volume = v;
            refreshVolume();
        }

        /** 声像（-1 左，0 中，1 右）。 */
        public function get pan():Number { return _pan; }
        public function set pan(v:Number):void
        {
            _pan = Math.max(-1, Math.min(1, v));
            refreshVolume();
        }

        /** 变速倍率（磁带式，仅初始播放时生效；暂停恢复/seek 保持）。 */
        public function get pitch():Number { return _pitch; }

        /** 优先级（越大越重要；并发超限时低优先级先被顶替）。 */
        internal function get priority():int { return _priority; }

        /** 淡入：从当前音量线性过渡到目标音量，duration 秒。 */
        public function fadeTo(target:Number, duration:Number):void
        {
            if (_stopped) return;
            _fadeFrom = effectiveFadeBase();
            _fadeTo = Math.max(0, Math.min(1, target));
            _fadeElapsed = 0;
            _fadeDuration = duration > 0 ? duration : 0.001;
            _fading = true;
            refreshVolume();
        }

        /** 淡入（从 0 到当前音量）。 */
        public function fadeIn(duration:Number):void
        {
            fadeTo(effectiveFadeBase(), duration);
            _fadeFrom = 0;
            refreshVolume();
        }

        /** 淡出（到 0），完成后自动停止。 */
        public function fadeOut(duration:Number):void
        {
            fadeTo(0, duration);
        }

        /** 淡入淡出完成回调（AudioManager 驱动帧推进）。 */
        internal function update(dt:Number):void
        {
            if (_stopped || !_fading) return;
            _fadeElapsed += dt;
            if (_fadeElapsed >= _fadeDuration)
            {
                _fadeElapsed = _fadeDuration;
                _fading = false;
                refreshVolume();
                if (_fadeTo <= 0) stop();
                return;
            }
            refreshVolume();
        }

        // ── AudioManager 协作（音量刷新 / 组匹配） ──

        /** 重新应用音量（含主音量/组音量/空间衰减/淡入淡出）。 */
        internal function refreshVolume():void
        {
            if (_channel != null)
                _channel.soundTransform = new SoundTransform(effectiveVolume(), _pan);
        }

        /** 所属混音组名（未设置返回空串）。AudioManager 组音量变更时用于匹配。 */
        internal function get groupName():String
        {
            return _group != null ? _group.name : "";
        }

        /** 设置空间音频参数（blend>0 时生效）。播放中实时应用。 */
        public function setSpatialParams(blend:Number, minDistance:Number, maxDistance:Number):void
        {
            _spatialBlend = blend;
            _minDistance = minDistance;
            _maxDistance = maxDistance;
            refreshVolume();
        }

        /** 更新源世界位置（每帧由 AudioSource 调用，仅 blend>0 时需调用）。 */
        public function setSpatial(x:Number, y:Number):void
        {
            _sx = x;
            _sy = y;
            refreshVolume();
        }

        // ── 内部 ──

        private function applyTransform():void
        {
            if (_channel != null)
                _channel.soundTransform = new SoundTransform(effectiveVolume(), _pan);
        }

        /** 淡入淡出插值前的基准音量（含主/组/空间）。 */
        private function effectiveFadeBase():Number
        {
            var vol:Number = _volume * (_manager != null ? _manager.masterVolume : 1);
            if (_group != null) vol *= _group.volume;
            if (_spatialBlend > 0) vol *= (1 - _spatialBlend) + _spatialBlend * spatialFactor();
            return vol;
        }

        private function effectiveVolume():Number
        {
            var vol:Number = effectiveFadeBase();
            if (_fading)
            {
                var t:Number = _fadeDuration > 0 ? _fadeElapsed / _fadeDuration : 1;
                if (t > 1) t = 1;
                vol *= _fadeFrom + (_fadeTo - _fadeFrom) * t;
            }
            return vol;
        }

        /** 距离衰减因子（0..1）：d ≤ min 全音量，d ≥ max 静音，之间线性。无听者时 1。 */
        private function spatialFactor():Number
        {
            var listener:AudioListener = AudioListener.current;
            if (listener == null) return 1;
            var dx:Number = _sx - listener.positionX;
            var dy:Number = _sy - listener.positionY;
            var dist:Number = Math.sqrt(dx * dx + dy * dy);
            if (dist <= _minDistance) return 1;
            if (dist >= _maxDistance) return 0;
            return 1 - (dist - _minDistance) / (_maxDistance - _minDistance);
        }

        private function onComplete(e:Event):void
        {
            // 单次播放自然结束：回收。循环播放不触发本事件。
            stop();
        }
    }
}

package dream.engine.audio
{
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.Element;
    import dream.engine.render.ResourceManager;
    import dream.engine.transform.Transform;

    /**
     * 音频源组件：挂在元素上播放一段音频。
     *
     * 能力：clip 引用（resource GUID → .mp3/.wav）、playOnStart、loop、volume、
     * 混音组（group）与空间音频（spatialBlend/minDistance/maxDistance）。
     * 播放由运行模式生命周期驱动（start 触发 playOnStart），编辑器模式不发声
     * （与动画/脚本一致：world.running=false 时组件运行时生命周期不执行）。
     *
     * 空间音频：spatialBlend 在 0（纯2D）~ 1（纯3D）间混合距离衰减，
     * 听者位置取 AudioListener.current（通常挂相机）；无听者时不衰减。
     * 源位置每帧从元素 Transform 世界矩阵读取。
     *
     * 用法：
     *   1. 元素添加 AudioSource，Inspector 的 Clip 字段拖入 Project 面板的 .mp3/.wav
     *   2. 运行模式 start 时自动播放（playOnStart=true）
     *   3. 脚本可调用 play()/stop() 显式控制
     *
     * 片段播放：clip 异步加载（ResourceManager.requestAudioByGuid），
     * 未就绪时的播放请求会缓存，加载完成后自动开始（与 AnimStateMachine 加载语义一致）。
     */
    public final class AudioSource extends DreamComponent
    {
        /** 音频资源 GUID（指向 .mp3/.wav）。 */
        public var clipGuid:String;

        /** 运行开始后自动播放。 */
        public var playOnStart:Boolean = true;

        /** 是否循环播放。 */
        public var loop:Boolean = false;

        /** 播放音量（0..1）。 */
        public var volume:Number = 1.0;

        /** 所属混音组（默认 sfx；music 为另一内置组，也可自定义）。 */
        public var group:String = "sfx";

        /** 空间混合：0=纯2D（无衰减），1=纯3D（按距离衰减），中间值线性混合。 */
        public var spatialBlend:Number = 1;

        /** 空间音频：全音量距离（≤此距离不衰减）。 */
        public var minDistance:Number = 1;

        /** 空间音频：静音距离（≥此距离无声）。 */
        public var maxDistance:Number = 10;

        /** 声像（-1 左 ~ 1 右）。 */
        public var pan:Number = 0;

        /** 变速倍率（磁带式；1 正常，>1 更快更高音，<1 更慢更低音）。 */
        public var pitch:Number = 1;

        /** 音量随机抖动幅度（0..1，每次播放独立取值；音效重复时避免机械感）。 */
        public var randomVolume:Number = 0;

        /** 优先级（0~255，越大越重要；并发超限时低优先级先被顶替）。 */
        public var priority:int = 128;

        /** 播放淡入时长（秒；0 不淡入）。 */
        public var fadeInSeconds:Number = 0;

        private var _clip:AudioClip;
        private var _playback:AudioPlayback;
        private var _requestedGuid:String = "";   // 最近请求的 clip GUID（防过期回调）
        private var _requestedPlay:Boolean = false; // clip 未就绪时的播放请求
        private var _previewPlayback:AudioPlayback; // Inspector Preview 按钮试播句柄
        private var _previewRequested:Boolean = false; // clip 未就绪时点了 Preview

        public function AudioSource(clipGuid:String = null)
        {
            this.clipGuid = clipGuid;
        }

        override protected function onLoad():void
        {
            if (clipGuid != null && clipGuid.length > 0)
                loadClip(clipGuid);
        }

        override protected function start():void
        {
            if (playOnStart)
            {
                if (_clip != null) startPlayback();
                else if (clipGuid != null && clipGuid.length > 0)
                {
                    _requestedPlay = true;
                    loadClip(clipGuid);
                }
            }
        }

        override protected function onDisable():void
        {
            stop();
        }

        override protected function onEnterFrame(dt:Number):void
        {
            // 空间音频：每帧同步源世界位置（仅播放中且 blend>0 时）。
            if (_playback != null && spatialBlend > 0)
                updateSpatialPosition();
        }

        override protected function onDestroy():void
        {
            stop();
            stopPreview();
            _clip = null;
        }

        // ── 播放控制 ──

        /** 开始播放（clip 未加载完成时记录请求，加载后自动开始）。 */
        public function play():void
        {
            if (_clip != null)
            {
                startPlayback();
                return;
            }
            _requestedPlay = true;
            if (clipGuid != null && clipGuid.length > 0) loadClip(clipGuid);
        }

        /** 停止播放并释放句柄。 */
        public function stop():void
        {
            _requestedPlay = false;
            if (_playback != null)
            {
                _playback.stop();
                _playback = null;
            }
        }

        /** 是否正在播放。 */
        public function get isPlaying():Boolean
        {
            return _playback != null && !_playback.finished;
        }

        /** 已加载的音频片段；未加载或加载失败返回 null。 */
        public function get clip():AudioClip { return _clip; }

        private function startPlayback():void
        {
            if (_clip == null) return;
            var am:AudioManager = AudioManager.current;
            if (am == null) return;
            if (_playback != null) _playback.stop();

            // 音量随机化：每次播放独立抖动（在 volume±randomVolume×volume 内）。
            var effectiveVol:Number = volume;
            if (randomVolume > 0)
            {
                var jitter:Number = volume * randomVolume * (Math.random() * 2 - 1);
                effectiveVol = Math.max(0, Math.min(1, volume + jitter));
            }

            _playback = am.play(_clip, loop, effectiveVol, group, pan, pitch, priority);
            if (_playback == null) return;
            if (fadeInSeconds > 0) _playback.fadeIn(fadeInSeconds);
            if (spatialBlend > 0)
            {
                _playback.setSpatialParams(spatialBlend, minDistance, maxDistance);
                updateSpatialPosition();
            }
        }

        /** 将源世界位置同步到播放句柄（空间音频用）。 */
        private function updateSpatialPosition():void
        {
            if (_playback == null) return;
            var t:Transform = owner.getComponent(Transform) as Transform;
            if (t != null)
                _playback.setSpatial(t.worldMatrix.tx, t.worldMatrix.ty);
        }

        /**
         * Inspector Preview 按钮：点击在播放/停止间切换，用当前组件参数试播
         * （验证音量/变速/声像/淡入等配置效果）。clip 未就绪时排队，加载后自动试播。
         */
        private function togglePreview():void
        {
            if (_previewPlayback != null && !_previewPlayback.finished)
            {
                _previewPlayback.stop();
                _previewPlayback = null;
                return;
            }
            if (_clip == null)
            {
                _previewRequested = true;
                if (clipGuid != null && clipGuid.length > 0) loadClip(clipGuid);
                return;
            }
            startPreview();
        }

        private function startPreview():void
        {
            var am:AudioManager = AudioManager.current;
            if (am == null || _clip == null || _clip.sound == null) return;

            // 音量随机化：与正式播放一致。
            var effectiveVol:Number = volume;
            if (randomVolume > 0)
            {
                var jitter:Number = volume * randomVolume * (Math.random() * 2 - 1);
                effectiveVol = Math.max(0, Math.min(1, volume + jitter));
            }
            _previewPlayback = am.play(_clip, loop, effectiveVol, null, pan, pitch, priority);
            if (_previewPlayback != null && fadeInSeconds > 0) _previewPlayback.fadeIn(fadeInSeconds);
        }

        private function stopPreview():void
        {
            _previewRequested = false;
            if (_previewPlayback != null)
            {
                _previewPlayback.stop();
                _previewPlayback = null;
            }
        }

        private function loadClip(guid:String):void
        {
            _requestedGuid = guid;
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null) return;
            rm.requestAudioByGuid(guid, onClipLoaded);
        }

        private function onClipLoaded(clip:AudioClip):void
        {
            // 引用已切换：丢弃过期回调（当前 clipGuid 已指向新资源）。
            if (_requestedGuid != clipGuid) return;
            _clip = clip;
            if (_clip == null) return;
            if (_requestedPlay)
            {
                _requestedPlay = false;
                startPlayback();
            }
            if (_previewRequested)
            {
                _previewRequested = false;
                startPreview();
            }
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var fields:Array = [
                new FieldInfo("clip", "Clip", "resource", clipGuid),
                // 试播按钮：点击用当前参数播放/停止（action 字段，Inspector 渲染为按钮）。
                new FieldInfo("preview", "Preview ▸", "action", null),
                new FieldInfo("playOnStart", "Play On Start", "boolean", playOnStart),
                new FieldInfo("loop", "Loop", "boolean", loop),
            ];

            var volumeF:FieldInfo = new FieldInfo("volume", "Volume", "number", volume);
            volumeF.min = 0; volumeF.max = 1; volumeF.step = 0.05;
            fields.push(volumeF);

            var panF:FieldInfo = new FieldInfo("pan", "Pan", "number", pan);
            panF.min = -1; panF.max = 1; panF.step = 0.05;
            fields.push(panF);

            var pitchF:FieldInfo = new FieldInfo("pitch", "Pitch", "number", pitch);
            pitchF.min = 0.1; pitchF.max = 4; pitchF.step = 0.05;
            fields.push(pitchF);

            var randomF:FieldInfo = new FieldInfo("randomVolume", "Random Vol", "number", randomVolume);
            randomF.min = 0; randomF.max = 1; randomF.step = 0.05;
            fields.push(randomF);

            var priorityF:FieldInfo = new FieldInfo("priority", "Priority", "number", priority);
            priorityF.min = 0; priorityF.max = 255; priorityF.step = 1;
            fields.push(priorityF);

            var fadeF:FieldInfo = new FieldInfo("fadeInSeconds", "Fade In", "number", fadeInSeconds);
            fadeF.min = 0; fadeF.max = 60; fadeF.step = 0.1;
            fields.push(fadeF);

            fields.push(new FieldInfo("group", "Group", "string", group));

            var blendF:FieldInfo = new FieldInfo("spatialBlend", "Spatial Blend", "number", spatialBlend);
            blendF.min = 0; blendF.max = 1; blendF.step = 0.05;
            fields.push(blendF);

            var minF:FieldInfo = new FieldInfo("minDistance", "Min Distance", "number", minDistance);
            minF.min = 0; minF.max = 1000; minF.step = 0.1;
            fields.push(minF);

            var maxF:FieldInfo = new FieldInfo("maxDistance", "Max Distance", "number", maxDistance);
            maxF.min = 0; maxF.max = 10000; maxF.step = 1;
            fields.push(maxF);

            return fields;
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "clip":
                    // null/空串 → 清空；否则切换资源（重新加载）。
                    var effective:String = (value == null) ? null : String(value);
                    clipGuid = (effective == null || effective.length == 0) ? null : effective;
                    stop();
                    stopPreview();
                    _clip = null;
                    if (clipGuid != null && clipGuid.length > 0)
                        loadClip(clipGuid);
                    break;
                case "preview":
                    // Preview 按钮：切换试播。
                    togglePreview();
                    break;
                case "playOnStart":
                    playOnStart = Boolean(value);
                    break;
                case "loop":
                    loop = Boolean(value);
                    break;
                case "volume":
                    volume = Number(value);
                    // 正在播放时实时应用新音量。
                    if (_playback != null) _playback.volume = volume;
                    break;
                case "pan":
                    pan = Number(value);
                    if (_playback != null) _playback.pan = pan;
                    break;
                case "pitch":
                    var pv:Number = Number(value);
                    if (pv <= 0) pv = 0.1;
                    pitch = pv;
                    // 变速在句柄创建时固定：重建播放。
                    if (_playback != null && !_playback.finished)
                    {
                        _playback.stop();
                        _playback = null;
                        startPlayback();
                    }
                    break;
                case "randomVolume":
                    randomVolume = Math.max(0, Math.min(1, Number(value)));
                    break;
                case "priority":
                    priority = int(Number(value));
                    break;
                case "fadeInSeconds":
                    fadeInSeconds = Math.max(0, Number(value));
                    if (_playback != null && !_playback.finished)
                        _playback.fadeIn(fadeInSeconds > 0 ? fadeInSeconds : 0.001);
                    break;
                case "group":
                    var g:String = (value == null) ? "" : String(value);
                    if (g == group) break;
                    group = g;
                    // 组绑定在创建句柄时固定，变化需重建。
                    if (_playback != null && !_playback.finished)
                    {
                        _playback.stop();
                        _playback = null;
                        startPlayback();
                    }
                    break;
                case "spatialBlend":
                    spatialBlend = Math.max(0, Math.min(1, Number(value)));
                    if (_playback != null) _playback.setSpatialParams(spatialBlend, minDistance, maxDistance);
                    break;
                case "minDistance":
                    minDistance = Number(value);
                    if (_playback != null) _playback.setSpatialParams(spatialBlend, minDistance, maxDistance);
                    break;
                case "maxDistance":
                    maxDistance = Number(value);
                    if (_playback != null) _playback.setSpatialParams(spatialBlend, minDistance, maxDistance);
                    break;
            }
        }
    }
}

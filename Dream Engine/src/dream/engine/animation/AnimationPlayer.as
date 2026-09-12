package dream.engine.animation
{
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.Element;
    import dream.engine.render.ResourceManager;
    import dream.engine.transform.Transform;

    import flash.utils.getQualifiedClassName;

    /**
     * 动画播放组件：播放单个 .dmclip 动画片段，按时间采样轨道值写入目标组件字段。
     *
     * 用法：
     *   1. 元素需有目标组件（如 Transform），再添加 AnimationPlayer
     *   2. Inspector 中 Clip 字段拖入 Project 面板的 .dmclip 资源（GUID 引用）
     *   3. Play On Start 时自动从头播放；也可运行时调用 play()/stop()/setTime()
     *
     * 轨道目标（.dmclip 轨道 target 字段）：target 非空时按名称递归匹配 owner 的
     * 后代元素（Transform.findChildByName），采样写入该子元素的组件；空串则作用于
     * owner 自身。由此一个片段可同时控制多个子元素。
     *
     * 字段独占：播放期间，片段轨道覆盖的字段（目标元素 id.组件短类名.字段名）会注册到
     * AnimationFieldControl，Inspector 编辑等外部写入被忽略/覆盖，保证动画值稳定。
     * 独占按目标元素作用域化：不同元素上的同名字段互不干扰。停止/销毁时自动释放。
     *
     * 采样后通过组件 applyRuntimeField 写入字段。基类默认实现委托
     * setFieldValue（组件为 Inspector 声明的字段映射），因此任何写了
     * setFieldValue 的组件无需额外代码即可被动画驱动；写入不受 Studio 门控。
     */
    public class AnimationPlayer extends DreamComponent
    {
        /** 播放的动画片段资源 GUID（指向 .dmclip）。 */
        public var clipGuid:String;

        /** 片段加载完成后是否自动从头播放。 */
        public var playOnStart:Boolean = true;

        /** 播放速度倍率（负值倒放，0 暂停）。 */
        public var speed:Number = 1;

        private var _clip:AnimationClip;
        private var _time:Number = 0;
        private var _playing:Boolean = false;
        private var _clipLoaded:Boolean = false;
        private var _requestedPlay:Boolean = false; // clip 未就绪时的播放请求

        public function AnimationPlayer(clipGuid:String = null)
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
                if (_clipLoaded) play();
                else _requestedPlay = true; // 异步加载完成后自动播放
            }
        }

        override protected function onEnterFrame(dt:Number):void
        {
            if (!_playing || _clip == null || speed == 0) return;
            _time += dt * speed;
            sampleAndApply();
        }

        // ── 公开播放控制 ──

        /** 从头开始播放。片段未加载完成时记录请求，加载后自动播放。 */
        public function play():void
        {
            if (_clip == null)
            {
                _requestedPlay = true;
                if (clipGuid != null && clipGuid.length > 0) loadClip(clipGuid);
                return;
            }
            _playing = true;
            _time = 0;
            registerControls();
        }

        /** 停止播放并释放独占字段（保留当前采样结果）。 */
        public function stop():void
        {
            _playing = false;
            _requestedPlay = false;
            releaseControls();
        }

        /** 是否正在播放。 */
        public function get isPlaying():Boolean { return _playing; }

        /** 设置播放时间（秒）并立即采样应用。 */
        public function setTime(t:Number):void
        {
            _time = t;
            if (_clip != null) sampleAndApply();
        }

        // ── 采样与字段写入 ──

        private function sampleAndApply():void
        {
            if (_clip == null || owner == null) return;
            for each (var track:Object in _clip.tracks)
            {
                if (track == null || track.componentType == null || track.field == null) continue;
                var te:Element = resolveTarget(track);
                if (te == null) continue;
                var comp:DreamComponent = findComponentByShortName(te, track.componentType);
                if (comp == null) continue;
                var v:* = _clip.sampleTrack(track, _time);
                if (v != null) comp.applyRuntimeField(track.field, v);
            }
        }

        /**
         * 解析轨道目标元素：track.target 非空时按路径匹配 owner 的后代
         * （Transform.findChildByPath，"A/B/C" 逐段或单名递归），否则为 owner 自身。
         */
        private function resolveTarget(track:Object):Element
        {
            if (owner == null) return null;
            var targetName:String = track.target != null ? String(track.target) : "";
            if (targetName.length == 0) return owner;
            var t:Transform = owner.getComponent(Transform) as Transform;
            if (t == null) return null;
            var ct:Transform = t.findChildByPath(targetName);
            return (ct != null && ct.owner != null) ? ct.owner : null;
        }

        private function findComponentByShortName(e:Element, shortName:String):DreamComponent
        {
            for each (var c:DreamComponent in e.getAllComponents())
            {
                if (shortClassName(c) == shortName) return c;
            }
            return null;
        }

        private static function shortClassName(obj:Object):String
        {
            var full:String = getQualifiedClassName(obj);
            var idx:int = full.lastIndexOf("::");
            if (idx >= 0) return full.substr(idx + 2);
            idx = full.lastIndexOf(".");
            if (idx >= 0) return full.substr(idx + 1);
            return full;
        }

        // ── 片段加载 ──

        private function loadClip(guid:String):void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null) return;
            rm.requestClipByGuid(guid, onClipLoaded);
        }

        private function onClipLoaded(clip:AnimationClip):void
        {
            _clip = clip;
            _clipLoaded = clip != null;
            if (_clip == null) return;
            if (_requestedPlay || (playOnStart && !_playing))
            {
                _requestedPlay = false;
                play();
            }
            else if (_playing)
            {
                registerControls();
            }
        }

        // ── 独占字段注册 ──

        private function registerControls():void
        {
            if (_clip == null) return;
            for each (var track:Object in _clip.tracks)
            {
                if (track == null || track.componentType == null || track.field == null) continue;
                AnimationFieldControl.control(elementKey(resolveTarget(track)), track.componentType, track.field);
            }
        }

        private function releaseControls():void
        {
            if (_clip == null) return;
            for each (var track:Object in _clip.tracks)
            {
                if (track == null || track.componentType == null || track.field == null) continue;
                AnimationFieldControl.release(elementKey(resolveTarget(track)), track.componentType, track.field);
            }
        }

        /** 元素独占键：e+元素 id（目标解析失败时回退 e0，与无目标等价且互不干扰）。 */
        private static function elementKey(e:Element):String
        {
            return (e != null) ? ("e" + e.id) : "e0";
        }

        override protected function onDisable():void
        {
            stop();
        }

        override protected function onDestroy():void
        {
            stop();
            _clip = null;
            _clipLoaded = false;
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            return [
                new FieldInfo("clipGuid", "Clip", "resource", clipGuid),
                new FieldInfo("playOnStart", "Play On Start", "boolean", playOnStart),
                new FieldInfo("speed", "Speed", "number", speed),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "clipGuid":
                    // null/空串 → 清空；否则加载新片段（String(null) 返回 "null"，需显式判空）。
                    var effective:String = (value == null) ? null : String(value);
                    clipGuid = (effective == null || effective.length == 0) ? null : effective;
                    stop();          // 释放旧片段独占字段
                    _clip = null;
                    _clipLoaded = false;
                    if (clipGuid != null && clipGuid.length > 0)
                        loadClip(clipGuid);
                    break;
                case "playOnStart":
                    playOnStart = Boolean(value);
                    break;
                case "speed":
                    speed = Number(value);
                    break;
            }
        }
    }
}

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

    import flash.utils.Dictionary;
    import flash.utils.getQualifiedClassName;

    /**
     * 动画状态机组件：按 .dmanimator 控制器驱动元素播放各状态的动画片段，
     * 根据参数与过渡条件自动切换状态。
     *
     * 用法：
     *   1. 元素需有目标组件（如 Transform），再添加 AnimStateMachine
     *   2. Inspector 中 Controller 字段拖入 Project 面板的 .dmanimator 资源（GUID 引用）
     *   3. Play On Start 时自动进入默认状态开始播放；脚本可用 setFloat/setBool/
     *      setTrigger 修改参数，驱动状态过渡。
     *
     * 过渡语义（与 AnimatorController.evaluateTransition 一致）：
     *   - 每帧按声明顺序评估当前状态的过渡；hasExitTime 时需先播放满 exitTime 秒
     *   - 所有条件满足（AND）即切换，trigger 参数在过渡被采纳时消费（复位）
     *   - 状态切换为瞬切：立即进入目标状态，片段从头播放
     *
     * 片段播放：采样规则与 AnimationPlayer 一致（数值插值、非数值向前追踪），
     * 通过 applyRuntimeField 写入目标组件字段；播放期间轨道字段注册
     * AnimationFieldControl 独占，Inspector 编辑等外部写入被拦截。
     *
     * 参数：
     *   - 运行时参数由控制器声明（boolean/float/int/trigger），默认值取自控制器；
     *     场景序列化保存运行时值，控制器加载时补齐缺失参数。
     *   - Inspector 中参数以 "p:参数名" 字段暴露（float/int 数值、boolean 勾选、
     *     trigger 按钮），编辑即写回运行时值。
     */
    public final class AnimStateMachine extends DreamComponent
    {
        /** 状态机控制器资源 GUID（指向 .dmanimator）。 */
        public var controllerGuid:String;

        /** 控制器加载完成后是否自动进入默认状态开始播放。 */
        public var playOnStart:Boolean = true;

        /** 状态切换回调：function(fromState:String, toState:String):void（初始进入时 from 为空串）。 */
        public var onStateChanged:Function;

        private var _controller:AnimatorController;
        /** 运行时参数表（name → 值；trigger 为一次性 true/false）。 */
        private var _params:Dictionary = new Dictionary();

        private var _stateName:String = "";   // 当前状态名
        private var _timeInState:Number = 0;  // 当前状态已播放时长（秒，用于 Exit Time）
        private var _clipGuid:String = "";    // 当前状态绑定的片段 GUID
        private var _requestedClipGuid:String = ""; // 最近一次请求的片段 GUID（防过期回调）
        private var _clip:AnimationClip;
        private var _clipLoaded:Boolean = false;
        private var _clipTime:Number = 0;
        private var _active:Boolean = false;
        private var _requestedPlay:Boolean = false; // 控制器未就绪时的播放请求

        public function AnimStateMachine(controllerGuid:String = null)
        {
            this.controllerGuid = controllerGuid;
        }

        override protected function onLoad():void
        {
            if (controllerGuid != null && controllerGuid.length > 0)
                loadController(controllerGuid);
        }

        override protected function start():void
        {
            if (playOnStart)
            {
                if (_controller != null) enterDefaultState();
                else _requestedPlay = true; // 控制器加载完成后自动进入默认状态
            }
        }

        override protected function onEnterFrame(dt:Number):void
        {
            if (!_active || _controller == null) return;

            // 过渡评估：满足条件即瞬切到目标状态。
            updateTransitions();

            _timeInState += dt;
            if (_clipLoaded && _clip != null)
            {
                _clipTime += dt;
                sampleAndApply();
            }
        }

        // ── 公开播放控制 ──

        /** 进入默认状态开始播放。控制器未加载完成时记录请求，加载后自动进入。 */
        public function play():void
        {
            if (_controller == null)
            {
                _requestedPlay = true;
                if (controllerGuid != null && controllerGuid.length > 0) loadController(controllerGuid);
                return;
            }
            if (_stateName == "")
            {
                enterDefaultState();
            }
            else
            {
                _clipTime = 0;
                _timeInState = 0;
                _active = true;
                registerControls();
            }
        }

        /** 停止播放并释放独占字段（保留当前采样结果与参数）。 */
        public function stop():void
        {
            _active = false;
            _requestedPlay = false;
            releaseControls();
        }

        /** 是否正在播放。 */
        public function get isPlaying():Boolean { return _active; }

        /** 当前状态名；未进入任何状态时返回空串。 */
        public function get currentStateName():String { return _stateName; }

        /** 当前状态内已播放时长（秒，片段时间）。 */
        public function get stateTime():Number { return _clipTime; }

        /** 已加载的控制器；未加载或加载失败返回 null。 */
        public function get controller():AnimatorController { return _controller; }

        // ── 参数脚本 API ──

        /** 设置 float 参数。 */
        public function setFloat(name:String, value:Number):void { _params[name] = value; }

        /** 设置 int 参数。 */
        public function setInt(name:String, value:int):void { _params[name] = value; }

        /** 设置 bool 参数。 */
        public function setBool(name:String, value:Boolean):void { _params[name] = value; }

        /** 触发 trigger 参数（一次性，被过渡消费后自动复位）。 */
        public function setTrigger(name:String):void { _params[name] = true; }

        /** 手动复位 trigger 参数。 */
        public function resetTrigger(name:String):void { _params[name] = false; }

        /** 读取参数当前值；未设置过时返回 undefined。 */
        public function getParam(name:String):* { return _params[name]; }

        // ── 内部：过渡与状态切换 ──

        private function updateTransitions():void
        {
            if (_controller == null || _stateName == "") return;
            var res:Object = _controller.evaluateTransition(_stateName, _params, _timeInState);
            if (res == null) return;

            // 消费本次过渡用到的 trigger 参数。
            var consumed:Array = res.consumed as Array;
            if (consumed != null)
            {
                for each (var name:String in consumed)
                {
                    if (_params[name] !== undefined) _params[name] = false;
                }
            }

            var to:String = String(res.to);
            if (to == "") return;
            if (_controller.findState(to) == null) return;
            enterState(to);
        }

        /** 瞬切到指定状态：绑定片段并从头播放（同片段复用已加载 clip）。 */
        private function enterState(name:String):void
        {
            if (_controller == null) return;
            var prev:String = _stateName;
            _stateName = name;
            _timeInState = 0;

            var state:Object = _controller.findState(name);
            var guid:String = state != null ? String(state.clipGuid) : "";
            if (guid.length > 0 && guid == _clipGuid)
            {
                // 同一片段：复用已加载 clip，从头播放。
                _clipTime = 0;
                if (_clipLoaded) registerControls();
            }
            else
            {
                releaseControls();
                _clip = null;
                _clipLoaded = false;
                _clipTime = 0;
                _clipGuid = guid;
                if (guid.length > 0) loadClip(guid);
            }

            _active = true;
            notifyStateChanged(prev, name);
        }

        private function enterDefaultState():void
        {
            if (_controller == null) return;
            var sname:String = _controller.defaultState;
            if (sname == "" && _controller.states.length > 0)
                sname = String(_controller.states[0].name);
            if (sname == "") return;
            _requestedPlay = false;
            enterState(sname);
        }

        private function notifyStateChanged(from:String, to:String):void
        {
            if (onStateChanged != null)
            {
                try { onStateChanged(from, to); } catch (e:Error) { }
            }
        }

        // ── 采样与字段写入（规则与 AnimationPlayer 一致） ──

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
                var v:* = _clip.sampleTrack(track, _clipTime);
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

        // ── 控制器 / 片段加载 ──

        private function loadController(guid:String):void
        {
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null) return;
            rm.requestControllerByGuid(guid, onControllerLoaded);
        }

        private function onControllerLoaded(controller:AnimatorController):void
        {
            _controller = controller;
            if (_controller == null) return;
            ensureParams();
            if (_requestedPlay || (playOnStart && !_active)) enterDefaultState();
        }

        /** 补齐缺失参数默认值；已存在（含场景序列化恢复）的参数保持不变。 */
        private function ensureParams():void
        {
            if (_controller == null) return;
            for each (var p:Object in _controller.parameters)
            {
                if (p == null || p.name == null) continue;
                var pname:String = String(p.name);
                if (_params[pname] === undefined || _params[pname] === null)
                    _params[pname] = p.value;
            }
        }

        private function loadClip(guid:String):void
        {
            _requestedClipGuid = guid;
            var rm:ResourceManager = ResourceManager.current;
            if (rm == null) return;
            rm.requestClipByGuid(guid, onClipLoaded);
        }

        private function onClipLoaded(clip:AnimationClip):void
        {
            // 状态已切换：丢弃过期回调（当前 _clipGuid 已指向新状态片段）。
            if (_requestedClipGuid != _clipGuid) return;
            _clip = clip;
            _clipLoaded = clip != null;
            if (_clip == null) return;
            registerControls();
            if (_active) sampleAndApply();
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

        /** 元素独占键：e+元素 id（目标解析失败时回退 e0）。 */
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
            _controller = null;
            _clip = null;
            _clipLoaded = false;
            onStateChanged = null;
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var fields:Array = [
                new FieldInfo("controllerGuid", "Controller", "resource", controllerGuid),
                new FieldInfo("playOnStart", "Play On Start", "boolean", playOnStart)
            ];
            // 控制器加载后追加参数字段：float/int 数值、boolean 勾选、trigger 按钮。
            if (_controller != null)
            {
                for each (var p:Object in _controller.parameters)
                {
                    if (p == null || p.name == null) continue;
                    var pname:String = String(p.name);
                    var ptype:String = String(p.type);
                    if (ptype == "trigger")
                    {
                        fields.push(new FieldInfo("p:" + pname, pname + " ▸", "action", null));
                    }
                    else
                    {
                        var v:* = _params[pname];
                        if (v === undefined || v === null) v = p.value;
                        fields.push(new FieldInfo("p:" + pname, pname,
                            (ptype == "boolean") ? "boolean" : "number", v));
                    }
                }
            }
            return fields;
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "controllerGuid":
                    // null/空串 → 清空；否则加载新控制器（String(null) 返回 "null"，需显式判空）。
                    var effective:String = (value == null) ? null : String(value);
                    controllerGuid = (effective == null || effective.length == 0) ? null : effective;
                    stop();          // 释放旧片段独占字段
                    _controller = null;
                    _params = new Dictionary();
                    if (controllerGuid != null && controllerGuid.length > 0)
                        loadController(controllerGuid);
                    break;
                case "playOnStart":
                    playOnStart = Boolean(value);
                    break;
                default:
                    // 参数字段（"p:参数名"）：trigger 收到按钮的 true 才触发；其余直接写值。
                    if (fieldName != null && fieldName.indexOf("p:") == 0 && value != null)
                    {
                        var pname:String = fieldName.substr(2);
                        var ptype:String = _controller != null ? _controller.parameterType(pname) : "";
                        if (ptype == "trigger")
                        {
                            if (value === true) setTrigger(pname);
                        }
                        else
                        {
                            _params[pname] = value;
                        }
                    }
                    break;
            }
        }
    }
}

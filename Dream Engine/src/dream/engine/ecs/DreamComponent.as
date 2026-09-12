package dream.engine.ecs
{
    import dream.engine.physics2d.Collision2D;

    /**
     * 组件基类：ECS 中的数据与行为载体。每个组件拥有完整的生命周期，
     * 由 Element/World 在合适时机驱动，子类按需重写钩子。
     *
     * 生命周期顺序：
     *   addComponent → onLoad()           组件加入元素时（仅一次）
     *                → onEnable()         enabled 置 true 时（可多次）
     *   首帧前       → start()            元素首次进入更新前（仅一次）
     *   每帧         → onEnterFrame()     对标 update，系统驱动主逻辑
     *                → onExitFrame()      对标 lateUpdate，后处理
     *   enabled=false → onDisable()       enabled 置 false 时（可多次）
     *   enabled=true  → onEnable()        重新启用
     *   destroyElement → onDestroy()      元素销毁时（仅一次）
     *
     * onEnable/onDisable 可反复触发；onLoad/start/onDestroy 仅一次。
     * enabled 默认 true，addComponent 后若元素已在世界中则立即触发 onLoad + onEnable。
     *
     * 物理碰撞回调（onCollision*2D / onTrigger*2D）：由 PhysicsSystem2D 分发，
     * 子类按需覆写。参数 Collision2D 描述"对方"的碰撞信息。
     */
    public class DreamComponent
    {
        /** 宿主元素，addComponent 时由 Element 设置。 */
        public var owner:Element = null;

        /**
         * 自身启用开关（activeSelf）：独立于元素/祖先状态。
         * 实际运行与否取决于元素链的 activeInHierarchy（见 _running）。
         * 赋值时由宿主 Element 重新评估生命周期（onEnable/onDisable 幂等）。
         */
        private var _enabled:Boolean = true;

        public function get enabled():Boolean { return _enabled; }
        public function set enabled(v:Boolean):void
        {
            if (_enabled == v) return;
            _enabled = v;
            if (owner != null) owner.evaluateComponent(this);
        }

        /** 是否实际在运行（onEnable 已调用、onDisable 未调用）。由 Element 维护。 */
        internal var _running:Boolean = false;

        /** 子类查询自身运行状态（如挂载显示对象时按此初始化可见性）。 */
        protected function get isRunning():Boolean { return _running; }

        // 内部状态标记，由 Element/World 维护，子类不应直接修改。
        internal var _loaded:Boolean = false;
        internal var _started:Boolean = false;

        public function DreamComponent()
        {
        }

        // ── 生命周期钩子（子类按需重写） ──────────────────────────

        /** 组件加入元素时调用，仅一次。用于初始化不依赖世界状态的数据。 */
        protected function onLoad():void {}

        /** enabled 由 false→true 时调用。用于注册事件、启用渲染等。 */
        protected function onEnable():void {}

        /** 元素首次进入更新前调用，仅一次。此时已在世界中，可安全查询其他组件。 */
        protected function start():void {}

        /** 每帧更新，对标 Unity Update。dt 为距上一帧的秒数。 */
        protected function onEnterFrame(dt:Number):void {}

        /** 每帧后处理，对标 Unity LateUpdate。在所有 onEnterFrame 之后调用。 */
        protected function onExitFrame(dt:Number):void {}

        /** enabled 由 true→false 时调用。用于注销事件、暂停渲染等。 */
        protected function onDisable():void {}

        /** 元素销毁时调用，仅一次。用于释放资源、移除显示对象等。 */
        protected function onDestroy():void {}

        // ── 物理碰撞回调（PhysicsSystem2D 分发，子类按需覆写） ──────────
        // 参数 Collision2D 描述"对方"信息（collider/element/rigidBody/contacts/relativeSpeed）。
        // 非触发器（无 isSensor）→ onCollision*2D；任一方 isSensor → onTrigger*2D。

        /** 碰撞开始接触（同对每帧仅一次）。 */
        public function onCollisionEnter2D(collision:Collision2D):void {}

        /** 碰撞保持接触中（持续触碰期间每帧调用）。 */
        public function onCollisionStay2D(collision:Collision2D):void {}

        /** 碰撞结束分离。 */
        public function onCollisionExit2D(collision:Collision2D):void {}

        /** 触发器被进入。 */
        public function onTriggerEnter2D(collision:Collision2D):void {}

        /** 触发器保持被进入。 */
        public function onTriggerStay2D(collision:Collision2D):void {}

        /** 触发器被退出。 */
        public function onTriggerExit2D(collision:Collision2D):void {}

        // ── Inspector 反射（子类按需重写） ────────────────────────

        /**
         * 返回可编辑字段描述列表，供 Inspector 面板显示与编辑。
         * 子类重写时应返回 FieldInfo 数组，每个 FieldInfo 描述一个字段的元数据。
         * 默认返回空数组（无可编辑字段）。
         */
        CONFIG::STUDIO
        public function getInspectableFields():Array
        {
            return [];
        }

        /**
         * 按 fieldName 设置字段值。Inspector 编辑与动画采样均通过此方法回写。
         * 子类按需重写，默认空实现。
         */
        public function setFieldValue(fieldName:String, value:*):void {}

        // ── 运行时字段写入（动画系统等运行时系统使用） ──

        /**
         * 按字段名写入运行时值（动画采样结果等）。
         *
         * 基类默认实现直接委托 setFieldValue —— 组件为 Inspector 声明的
         * 字段映射（逻辑名 → 实际赋值）即为动画可驱动的字段集，新建组件
         * 无需为动画额外编写任何代码：写了 setFieldValue 即可被动画写入。
         *
         * setFieldValue 不设 CONFIG 门控（所有构建均存在），因此发布版
         * 动画照常生效。特殊组件可在子类覆写以获得定制写入行为（可选）。
         */
        public function applyRuntimeField(fieldName:String, value:*):void
        {
            setFieldValue(fieldName, value);
        }

        // ── 内部驱动入口（由 Element 调用，避免子类重写绕过状态标记） ──

        internal function internalLoad():void
        {
            if (_loaded) return;
            _loaded = true;
            onLoad();
        }

        internal function internalStart():void
        {
            if (_started) return;
            _started = true;
            start();
        }

        internal function internalEnterFrame(dt:Number):void
        {
            if (_running) onEnterFrame(dt);
        }

        internal function internalExitFrame(dt:Number):void
        {
            if (_running) onExitFrame(dt);
        }

        internal function internalEnable():void
        {
            if (_running) return;
            _running = true;
            onEnable();
        }

        internal function internalDisable():void
        {
            if (!_running) return;
            _running = false;
            onDisable();
        }

        internal function internalDestroy():void
        {
            onDestroy();
        }
    }
}

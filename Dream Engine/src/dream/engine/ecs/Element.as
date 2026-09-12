package dream.engine.ecs
{
    import dream.engine.transform.Transform;
    import flash.utils.Dictionary;

    /**
     * 实体：ECS 中的核心对象，本身只是一个唯一 ID + 组件容器。
     * 不包含逻辑，所有行为由 System 基于组件组合驱动。
     *
     * 组件以 Class 为 key 存储在 Element 中，getComponent(SomeType) 直接按类型取回。
     * 链式调用：element.addComponent(new Transform()).addComponent(new Sprite());
     *
     * 组件生命周期由 Element 驱动：
     *   addComponent → onLoad +（enabled 则 onEnable）
     *   removeComponent → onDisable + onDestroy
     *   setEnabled(false) → 所有组件 onDisable
     *   首帧前（World 驱动）→ start
     *   每帧（World 驱动）→ onEnterFrame + onExitFrame
     *   destroyElement → 所有组件 onDisable + onDestroy
     */
    public final class Element
    {
        private static var s_nextId:int = 0;

        /** 全局唯一 ID，单调递增。用于序列化、网络同步、调试定位。 */
        public var id:int;

        /** 可读名称，用于 Hierarchy 面板显示。默认 "Element"（新建节点不再带递增数字后缀）。 */
        public var name:String;

        /** 标记是否存活。World.destroyElement 时置 false，系统更新时跳过。 */
        public var alive:Boolean = true;

        /**
         * 自身启用开关（activeSelf）：Inspector 勾选框控制的值。
         * 实际"在层级中是否有效"见 activeInHierarchy（自身 × 祖先链）。
         * 变化时重新评估自身组件并级联通知后代（不覆盖后代 activeSelf）。
         */
        private var _enabled:Boolean = true;

        public function get enabled():Boolean { return _enabled; }
        public function set enabled(v:Boolean):void
        {
            if (_enabled == v) return;
            _enabled = v;
            refreshEnabled();
        }

        /**
         * 在层级中是否有效（activeInHierarchy）：自身启用且所有祖先元素启用。
         * 组件实际运行 = 组件.enabled && 本值。
         */
        public function get activeInHierarchy():Boolean
        {
            if (!_enabled) return false;
            var t:Transform = getComponent(Transform) as Transform;
            if (t != null && t.parent != null && t.parent.owner != null)
                return t.parent.owner.activeInHierarchy;
            return true;
        }

        /**
         * 重新评估可用性并级联通知后代（祖先 activeInHierarchy 变化、层级变更后调用）。
         * 仅触发生命周期变化（onEnable/onDisable），不修改后代 activeSelf。
         */
        public function refreshEnabled():void
        {
            for each (var c:DreamComponent in components)
                evaluateComponent(c);
            var t:Transform = getComponent(Transform) as Transform;
            if (t != null)
            {
                for (var i:int = 0; i < t.childCount; i++)
                {
                    var child:Transform = t.getChildAt(i);
                    if (child != null && child.owner != null)
                        child.owner.refreshEnabled();
                }
            }
        }

        /** 评估单个组件是否应运行：组件.enabled && 元素在层级中有效。幂等。 */
        internal function evaluateComponent(c:DreamComponent):void
        {
            var shouldRun:Boolean = c.enabled && activeInHierarchy;
            if (shouldRun && !c._running) c.internalEnable();
            else if (!shouldRun && c._running) c.internalDisable();
        }

        private var _components:Dictionary = new Dictionary(); // Class -> DreamComponent
        private var _order:Array = []; // 插入序组件列表（Inspector/序列化按此顺序，Dictionary 迭代无序）

        public function Element()
        {
            id = s_nextId++;
            name = "Element";
        }

        /**
         * 把全局 ID 计数器抬到 id 之上。反序列化恢复历史 ID（撤销/重做就是整体重建世界）后
         * 必须调用，否则后续新建元素会与恢复出来的 ID 撞号。
         */
        public static function reserveId(id:int):void
        {
            if (id >= s_nextId) s_nextId = id + 1;
        }

        /**
         * 添加组件。以组件的实际 Class 为 key，覆盖同类型旧组件。
         * 触发旧组件 onDisable+onDestroy，新组件 onLoad+（enabled 则 onEnable）。
         * 返回 this 以支持链式调用。
         */
        public function addComponent(c:DreamComponent):Element
        {
            var type:Class = Object(c).constructor;
            var old:DreamComponent = _components[type];
            if (old != null)
            {
                if (old._running) old.internalDisable();
                old.internalDestroy();
                // 同类型覆盖：保留原位，显示顺序稳定。
                var idx:int = _order.indexOf(old);
                if (idx >= 0) _order[idx] = c;
            }
            else
            {
                _order.push(c);
            }

            c.owner = this;
            _components[type] = c;
            c.internalLoad();
            // 依据元素链可用性评估（activeInHierarchy=false 时组件不启动，activeSelf 保持）。
            evaluateComponent(c);
            return this;
        }

        /** 移除并返回指定类型的组件（含子类）；不存在则返回 null。触发 onDisable+onDestroy。 */
        public function removeComponent(type:Class):DreamComponent
        {
            for (var key:* in _components)
            {
                var c:DreamComponent = _components[key];
                if (c is type)
                {
                    if (c._running) c.internalDisable();
                    c.internalDestroy();
                    c.owner = null;
                    delete _components[key];
                    var i:int = _order.indexOf(c);
                    if (i >= 0) _order.splice(i, 1);
                    return c;
                }
            }
            return null;
        }

        /**
         * 获取指定类型的组件；不存在则返回 null。
         * 使用 is 多态匹配：SpriteRenderer（继承 DisplayComponent）可被
         * getComponent(DisplayComponent) 查询命中。
         */
        public function getComponent(type:Class):DreamComponent
        {
            for each (var c:DreamComponent in _order)
            {
                if (c is type) return c;
            }
            return null;
        }

        /** 是否拥有指定类型的组件（含子类实例）。 */
        public function hasComponent(type:Class):Boolean
        {
            for each (var c:DreamComponent in _order)
            {
                if (c is type) return true;
            }
            return false;
        }

        /** 是否拥有 types 中列出的全部组件类型（系统查询用，含子类匹配）。 */
        public function hasAll(types:Array):Boolean
        {
            for each (var t:Class in types)
            {
                if (!hasComponent(t)) return false;
            }
            return true;
        }

        /** 遍历所有组件（World 帧驱动用）。返回的 Vector 是快照，迭代中修改安全。 */
        internal function get components():Vector.<DreamComponent>
        {
            var v:Vector.<DreamComponent> = new Vector.<DreamComponent>();
            for each (var c:DreamComponent in _order) v.push(c);
            return v;
        }

        /**
         * 返回所有组件的快照（运行时组件枚举：物理碰撞回调分发、脚本遍历用）。
         * 与 components 相同，但公开给跨包调用方（如 dream.engine.physics2d）。
         */
        public function getAllComponents():Vector.<DreamComponent>
        {
            return components;
        }

        /** 返回所有组件的快照（供 Inspector 反射用，跨包访问）。 */
        CONFIG::STUDIO
        public function getComponents():Vector.<DreamComponent>
        {
            return components;
        }

        /** 每帧 start 阶段：为所有运行中且未 start 的组件触发 start（Unity：Start 在首个 Update 前）。
         *  组件在元素加入后启用（addComponent）或运行中才变为启用时，start 均在此阶段补触发（仅一次）。 */
        internal function internalStart():void
        {
            for each (var c:DreamComponent in components)
            {
                if (c._running) c.internalStart();
            }
        }

        /** 每帧 onEnterFrame，由 World 驱动。层级无效（自身或祖先禁用）时跳过。 */
        internal function internalEnterFrame(dt:Number):void
        {
            if (!activeInHierarchy) return;
            for each (var c:DreamComponent in components)
            {
                c.internalEnterFrame(dt);
            }
        }

        /** 每帧 onExitFrame，由 World 驱动。在所有元素的 onEnterFrame 之后调用。 */
        internal function internalExitFrame(dt:Number):void
        {
            if (!activeInHierarchy) return;
            for each (var c:DreamComponent in components)
            {
                c.internalExitFrame(dt);
            }
        }

        /** 清除所有组件引用并触发 onDestroy，供 World 销毁元素时调用。 */
        internal function dispose():void
        {
            for each (var c:DreamComponent in _order)
            {
                if (c._running) c.internalDisable();
                c.internalDestroy();
                c.owner = null;
                delete _components[Object(c).constructor];
            }
            _order.length = 0;
        }
    }
}

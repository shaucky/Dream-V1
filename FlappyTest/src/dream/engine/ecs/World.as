package dream.engine.ecs
{
    import dream.engine.transform.Transform;

    import flash.utils.Dictionary;

    /**
     * 世界：ECS 的顶层管理器。持有所有 Element 和 DreamSystem，
     * 提供创建/销毁/查询/更新接口。
     *
     * 典型用法：
     *   var w:World = new World();
     *   w.addSystem(new RenderSystem());
     *   var e:Element = w.createElement();
     *   e.addComponent(new Transform()).addComponent(new Sprite());
     *   // 每帧：
     *   w.update(deltaTime);
     */
    public final class World
    {
        private var _elements:Vector.<Element> = new Vector.<Element>();
        private var _systems:Vector.<DreamSystem> = new Vector.<DreamSystem>();

        /** 层级显示序号缓存（computeHierarchyOrder 惰性构建，World.update 起始清空）。 */
        private var _orderCache:Dictionary = null;

        /** Hierarchy 脏标记：元素增删或重命名时置脏，由 HierarchyBridge 检查并清脏。 */
        public var hierarchyDirty:Boolean = false;

        /**
         * 场景根元素 ID。场景有且仅有一个根元素，不可被销毁。
         * -1 表示尚未创建（独立运行模式不强制创建根）。由 DreamEngine 在 STUDIO 模式创建。
         */
        public var rootElementId:int = -1;

        /**
         * 是否处于运行模式。true 时驱动元素运行时生命周期（start/onEnterFrame/onExitFrame）；
         * false（编辑器模式）时只更新系统（如 RenderSystem 同步变换）与销毁清理，
         * 不调用组件的游戏逻辑帧。默认 true，Studio 接管时置 false 切到编辑器模式。
         */
        public var running:Boolean = true;

        /** 所有存活元素（只读视图）。 */
        public function get elements():Vector.<Element>
        {
            return _elements;
        }

        /** 创建并注册一个新元素。 */
        public function createElement():Element
        {
            var e:Element = new Element();
            _elements.push(e);
            hierarchyDirty = true;
            return e;
        }

        /**
         * 销毁元素：标记为非存活，触发所有组件 onDisable+onDestroy。
         * 实际从列表移除在当前帧更新完成后生效（避免迭代中修改集合）。
         * 销毁前将后代（Transform 子级）重新挂到根元素下（保持世界坐标），
         * 避免删除父元素后后代变成无父的根级元素。
         */
        public function destroyElement(e:Element):void
        {
            if (!e.alive) return;

            if (rootElementId >= 0 && e.id != rootElementId)
            {
                var t:Transform = e.getComponent(Transform) as Transform;
                if (t != null && t.childCount > 0)
                {
                    var rootT:Transform = findRootTransform();
                    if (rootT != null)
                    {
                        // 逆序快照迭代：setParent 会从 _children 移除子级。
                        for (var i:int = t.childCount - 1; i >= 0; i--)
                        {
                            var child:Transform = t.getChildAt(i);
                            if (child != null) child.setParent(rootT, true); // 保持世界坐标
                        }
                    }
                }
            }

            e.alive = false;
            e.dispose();
            hierarchyDirty = true;
        }

        /** 根元素的 Transform；无根/根无 Transform 时返回 null。 */
        private function findRootTransform():Transform
        {
            if (rootElementId < 0) return null;
            for each (var e:Element in _elements)
            {
                if (e.alive && e.id == rootElementId)
                    return e.getComponent(Transform) as Transform;
            }
            return null;
        }

        /** 标记 Hierarchy 需要重新同步（如重命名后）。 */
        public function markHierarchyDirty():void
        {
            hierarchyDirty = true;
        }

        /**
         * 将元素移动到 _elements 中 reference 之前/之后，用于 Hierarchy 同级插入重排。
         * 仅改变 _elements 顺序（决定 HierarchyBridge 快照中兄弟显示顺序），
         * 不改变 Transform 父子关系。before=true 插到 reference 前，false 插到其后。
         */
        public function moveElementTo(e:Element, reference:Element, before:Boolean):void
        {
            var ei:int = _elements.indexOf(e);
            var ri:int = _elements.indexOf(reference);
            if (ei < 0 || ri < 0 || e == reference) return;
            _elements.splice(ei, 1);
            // 移除后重新定位 reference 索引，避免错位。
            ri = _elements.indexOf(reference);
            if (!before) ri++;
            if (ri < 0) ri = 0;
            if (ri > _elements.length) ri = _elements.length;
            _elements.splice(ri, 0, e);
            hierarchyDirty = true;
        }

        /**
         * 计算元素的「层级显示序号」：按 Transform 父子构成森林，从根级元素起深度优先编号，
         * 同父兄弟按 _elements 顺序（与 Hierarchy 面板显示顺序一致）。
         * 序号越大 = 在层级中越靠后 = 叠放时越靠上层。渲染系统与 UI 系统共用。
         * 同一帧内缓存；World.update 起始清空。
         */
        public function computeHierarchyOrder():Dictionary
        {
            if (_orderCache != null) return _orderCache;

            var ordinals:Dictionary = new Dictionary();
            var childrenByParent:Dictionary = new Dictionary();
            var roots:Vector.<Element> = new Vector.<Element>();

            for each (var e:Element in _elements)
            {
                if (!e.alive) continue;
                var t:Transform = e.getComponent(Transform) as Transform;
                var pe:Element = (t != null && t.parent != null) ? t.parent.owner : null;
                if (pe != null && pe.alive && pe != e)
                {
                    var arr:Vector.<Element> = childrenByParent[pe] as Vector.<Element>;
                    if (arr == null)
                    {
                        arr = new Vector.<Element>();
                        childrenByParent[pe] = arr;
                    }
                    arr.push(e);
                }
                else
                {
                    roots.push(e);
                }
            }

            var counter:int = 0;
            for each (var r:Element in roots)
                counter = assignOrder(r, childrenByParent, ordinals, counter);

            // 防御：父链异常导致不可达的元素，按 _elements 顺序补号，保证序号唯一。
            for each (var e2:Element in _elements)
            {
                if (!e2.alive || ordinals[e2] !== undefined) continue;
                ordinals[e2] = counter++;
            }

            _orderCache = ordinals;
            return ordinals;
        }

        /** 深度优先递归编号：先自身，再按 _elements 顺序编号各子级。 */
        private function assignOrder(e:Element, childrenByParent:Dictionary,
                                     ordinals:Dictionary, counter:int):int
        {
            ordinals[e] = counter++;
            var arr:Vector.<Element> = childrenByParent[e] as Vector.<Element>;
            if (arr == null) return counter;
            for each (var c:Element in arr)
                counter = assignOrder(c, childrenByParent, ordinals, counter);
            return counter;
        }

        /** 注册系统。系统按注册顺序更新。 */
        public function addSystem(s:DreamSystem):World
        {
            _systems.push(s);
            return this;
        }

        /** 移除系统。 */
        public function removeSystem(s:DreamSystem):void
        {
            var i:int = _systems.indexOf(s);
            if (i >= 0) _systems.splice(i, 1);
        }

        /**
         * 查询拥有全部指定组件类型的存活元素。
         * 用法：world.query(Transform, Sprite) 返回同时带这两个组件的元素。
         * 每次调用构造新 Vector，不在帧间缓存——调用方按需使用。
         */
        public function query(...types):Vector.<Element>
        {
            var result:Vector.<Element> = new Vector.<Element>();
            for each (var e:Element in _elements)
            {
                if (!e.alive) continue;
                if (e.hasAll(types)) result.push(e);
            }
            return result;
        }

        /**
         * 每帧驱动。
         *
         * running=true（运行模式）顺序：
         *   1. 首次出现的元素触发 start（仅一次）
         *   2. 所有元素 onEnterFrame（对标 update）
         *   3. 各启用系统 update
         *   4. 所有元素 onExitFrame（对标 lateUpdate）
         *   5. 清理本帧标记为销毁的元素
         *
         * running=false（编辑器模式）：
         *   仅执行步骤 3（系统更新，RenderSystem 同步变换必需）与步骤 5（清理），
         *   跳过组件运行时生命周期，避免编辑器下游戏逻辑（如 Rotator 旋转）
         *   与 Inspector 编辑互相干扰。
         */
        public function update(dt:Number):void
        {
            // 层级顺序可能在本帧改变（增删/重排），系统更新前失效缓存。
            _orderCache = null;

            if (running)
            {
                // 1+2. start + onEnterFrame
                for each (var e:Element in _elements)
                {
                    if (!e.alive) continue;
                    e.internalStart();
                    e.internalEnterFrame(dt);
                }
            }

            // 3. 系统更新（始终执行：编辑器下 RenderSystem 同步变换必需）
            for each (var s:DreamSystem in _systems)
            {
                if (s.enabled) s.update(this, dt);
            }

            if (running)
            {
                // 4. onExitFrame
                for each (var e2:Element in _elements)
                {
                    if (!e2.alive) continue;
                    e2.internalExitFrame(dt);
                }
            }

            // 5. 清理销毁元素（始终）
            sweepDestroyed();
        }

        /** 移除已标记为非存活的元素。组件已在 destroyElement 时 dispose。 */
        private function sweepDestroyed():void
        {
            var i:int = 0;
            while (i < _elements.length)
            {
                if (!_elements[i].alive)
                {
                    _elements.splice(i, 1);
                }
                else
                {
                    i++;
                }
            }
        }
    }
}

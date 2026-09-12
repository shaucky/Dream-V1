package dream.engine.ecs.systems
{
    import dream.engine.ecs.DreamSystem;
    import dream.engine.ecs.Element;
    import dream.engine.ecs.World;
    import dream.engine.render.CameraComponent;
    import dream.engine.render.DisplayComponent;
    import dream.engine.render.Drawable;
    import dream.engine.transform.Transform;
    import dream.engine.render.DrawableContainer;
    import dream.engine.render.RenderEngine;

    import flash.geom.Matrix;
    import flash.utils.Dictionary;

    /**
     * 渲染系统：桥接 ECS 变换数据与渲染引擎可绘制对象。
     *
     * 职责：
     *   1. 每帧按主相机元素（CameraComponent）应用相机视图到渲染根容器；
     *      场景无相机时回退到原点相机（位置 0,0、zoom=1），世界原点居中于窗口
     *   2. 首次遇到未挂载的 DisplayComponent 时，调用 attachTo(root) 挂到渲染根容器
     *   3. 每帧把 Transform.worldMatrix 同步到 Drawable.transformationMatrix
     *   4. 把 CanvasSystem 登记的世界/相机空间画布层并入世界排序；其中相机空间层
     *      在此适配为「铺满相机视口」（须在相机视图应用之后，故放在本系统）
     *
     * 不直接接触 Starling：通过 RenderEngine.root（DrawableContainer）
     * 与 DisplayComponent.displayObject（Drawable）操作。
     *
     * RenderEngine 通过构造函数注入；若渲染未就绪（root==null）则跳过当帧。
     */
    public final class RenderSystem extends DreamSystem
    {
        /**
         * 当前实例（服务定位器）。编辑器侧（SceneSelection 视口拾取）通过它读取本帧
         * 渲染顺序，从而按「绘制顺序」而不是元素数组下标判定命中，与 CanvasSystem.current 同例。
         */
        public static var current:RenderSystem;

        private var _renderEngine:RenderEngine;

        /** 上一次应用的根子级顺序（用于判断是否需要重排，避免每帧 O(n²) 操作）。 */
        private var _lastOrder:Vector.<Drawable> = new Vector.<Drawable>();

        /** 本帧渲染顺序（从底层到顶层）的元素快照；编辑器拾取用。 */
        private var _renderOrder:Vector.<Element> = new Vector.<Element>();

        public function RenderSystem(renderEngine:RenderEngine)
        {
            _renderEngine = renderEngine;
            current = this;
        }

        /**
         * 本帧渲染顺序（从底层到顶层）：渲染根下的世界元素与世界/相机空间画布元素。
         * 编辑器拾取按此自上层往下判定，铺满视口的背景层因排序最底而最后被拾取。
         */
        public function get renderOrder():Vector.<Element>
        {
            return _renderOrder;
        }

        override public function get requiredComponents():Array
        {
            return [Transform, DisplayComponent];
        }

        override public function update(world:World, dt:Number):void
        {
            // 渲染根未就绪（Starling root 尚未创建）则跳过，待就绪后自动开始同步。
            var root:DrawableContainer = _renderEngine.root;
            if (root == null) return;

            CONFIG::STUDIO
            {
                // 编辑器模式：视图由编辑器导航相机驱动（查看用，非场景对象）。
                // 场景中的 CameraComponent 元素此时仅是可编辑的普通对象，不参与视图。
                if (!world.running)
                {
                    _renderEngine.applyEditorCamera();
                    applyElementTransforms(world, root);
                    return;
                }
            }

            // 运行模式：主相机元素（CameraComponent，根的子节点）驱动视图；
            // 无相机时回退原点相机（orthoSize=0 → zoom=1）→ 世界原点居中于窗口。
            var ortho:Number = 0;
            var rotation:Number = 0;
            var camX:Number = 0;
            var camY:Number = 0;
            var cams:Vector.<Element> = world.query(Transform, CameraComponent);
            for each (var ce:Element in cams)
            {
                var camC:CameraComponent = ce.getComponent(CameraComponent) as CameraComponent;
                var camT:Transform = ce.getComponent(Transform) as Transform;
                if (camC == null || camT == null || !camC.enabled || !ce.activeInHierarchy) continue;
                var wm:Matrix = camT.worldMatrix;
                camX = wm.tx;
                camY = wm.ty;
                rotation = Math.atan2(wm.b, wm.a);
                ortho = camC.orthographicSize;
                break;
            }
            _renderEngine.applyCameraView(ortho, rotation, camX, camY);

            applyElementTransforms(world, root);
        }

        /** 把世界变换同步到全部可绘制元素（编辑器/运行模式共用），并按排序重排根子级。 */
        private function applyElementTransforms(world:World, root:DrawableContainer):void
        {
            var elements:Vector.<Element> = world.query(Transform, DisplayComponent);
            var ordinals:Dictionary = world.computeHierarchyOrder();
            var entries:Array = [];

            for each (var e:Element in elements)
            {
                var t:Transform = e.getComponent(Transform) as Transform;
                var dc:DisplayComponent = e.getComponent(DisplayComponent) as DisplayComponent;
                if (t == null || dc == null || dc.displayObject == null) continue;

                // 首次处理：挂载到根容器。重复调用内部幂等。
                dc.attachTo(root);

                // 同步世界变换：显示矩阵 = 世界矩阵 × 本地平移(-pivot)。
                // worldMatrix getter 返回缓存引用，必须 clone 再修改（否则原地修改污染缓存）。
                // 注意：flash.geom.Matrix 的点变换约定是 x'=a·x+c·y、y'=b·x+d·y
                // （即矩阵列布局 [a b; c d] 作用于列向量，b 配 x、c 配 y）。
                // 因此 post-multiply W·T(-pivot) 的平移分量为
                //   tx' = tx - (a·px + c·py)
                //   ty' = ty - (b·px + d·py)
                // 此前误按行向量思维写成 a·px+b·py / c·px+d·py，旋转时 pivot 偏移方向
                // 错误，表现为中心沿斜率 ±1 的斜线来回移动。
                var m:Matrix = t.worldMatrix.clone();
                var px:Number = dc.effectivePivotX;
                var py:Number = dc.effectivePivotY;
                if (px != 0 || py != 0)
                {
                    m.tx -= m.a * px + m.c * py;
                    m.ty -= m.b * px + m.d * py;
                }
                dc.displayObject.transformationMatrix = m;

                entries.push({ d: dc.displayObject, order: dc.sortingOrder, dfs: elementOrder(ordinals, e), el: e });
            }

            // 世界空间画布层：与场景元素同处渲染根，按同一尺度 (sortingOrder, 层级序号) 排序，
            // 因此画布层可以落在世界内容之间（被前景遮挡/遮挡背景）。
            var layers:Array = _renderEngine.worldCanvasLayers;
            for each (var wl:Object in layers)
            {
                var layer:Drawable = wl.layer as Drawable;
                if (layer == null) continue;
                // 相机空间画布（fill）：层在根容器下与世界元素一起排序，但要铺满相机视口，
                // 故在此刻（相机视图刚应用完）反算层矩阵，保证与本次相机变换同帧一致。
                if (wl.fill) fitLayerToViewport(layer as DrawableContainer, Number(wl.scale));
                entries.push({ d: layer, order: int(wl.order), dfs: int(wl.dfs), el: wl.element as Element });
            }

            // 叠放顺序 = (sortingOrder 升序, 层级序号升序)；层级序号唯一 → 顺序确定。
            entries.sort(compareRenderEntry);

            // 渲染顺序快照（与即将写入根容器的子级顺序一致）：编辑器拾取据此判定，不再靠
            // 元素数组下标近似。
            _renderOrder.length = 0;
            for each (var en:Object in entries)
            {
                var oe:Element = en.el as Element;
                if (oe != null) _renderOrder.push(oe);
            }

            applyRenderOrder(root, entries);
        }

        /**
         * 把「铺满视口」的层容器（Canvas 相机空间）适配到当前相机视口。
         * 层矩阵取相机视图矩阵（世界 → 舞台）的逆，再按画布缩放 s 收缩：于是
         *   舞台 = 相机矩阵 · 层矩阵 = S(s)
         * 即层局部 (0,0)~(stageW/s, stageH/s) 恰好映射到整个视口 —— 相机平移/缩放/旋转
         * 时始终铺满，且画布内容与屏幕对齐。
         * 必须在 applyCameraView 之后调用（本类两处调用点均在相机视图应用之后）。
         */
        private function fitLayerToViewport(layer:DrawableContainer, s:Number):void
        {
            var inv:Matrix = _renderEngine.root.transformationMatrix.clone();
            inv.invert();
            inv.a *= s; inv.b *= s; inv.c *= s; inv.d *= s;
            layer.transformationMatrix = inv;
        }

        /**
         * 按排序结果重排根容器子级（层级顺序即叠放顺序）。顺序未变时跳过。
         * 编辑器覆盖层（gizmo 等）不是场景元素，会自然留在末尾（最上层）。
         */
        private function applyRenderOrder(root:DrawableContainer, entries:Array):void
        {
            var n:int = entries.length;
            var changed:Boolean = (n != _lastOrder.length);
            if (!changed)
            {
                for (var i:int = 0; i < n; i++)
                {
                    if (_lastOrder[i] !== (entries[i].d as Drawable))
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (!changed) return;

            _lastOrder.length = 0;
            for (var j:int = 0; j < n; j++)
            {
                var d:Drawable = entries[j].d as Drawable;
                _lastOrder.push(d);
                root.setChildIndex(d, j);
            }
        }

        /** 元素层级序号；缺失时取最大（叠放最上层）。 */
        private static function elementOrder(ordinals:Dictionary, e:Element):int
        {
            if (e == null || ordinals == null) return int.MAX_VALUE;
            var v:* = ordinals[e];
            return (v === undefined) ? int.MAX_VALUE : int(v);
        }

        /** 排序比较：先 sortingOrder，再层级序号。 */
        private static function compareRenderEntry(a:Object, b:Object):Number
        {
            var o:int = int(a.order) - int(b.order);
            if (o != 0) return o;
            return int(a.dfs) - int(b.dfs);
        }
    }
}

package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.DreamSystem;
    import dream.engine.ecs.Element;
    import dream.engine.ecs.World;
    import dream.engine.input.InputManager;
    import dream.engine.render.Drawable;
    import dream.engine.render.DrawableContainer;
    import dream.engine.render.RenderEngine;
    import dream.engine.transform.Transform;

    import flash.geom.Matrix;
    import flash.geom.Point;
    import flash.utils.Dictionary;

    /**
     * 画布系统：驱动屏幕空间 UI 的布局、叠放、裁剪（Mask）、滚动（ScrollView/Scrollbar）
     * 与指针交互。
     *
     * 渲染结构：
     *   - 每个 Canvas 一个「画布层」容器，CanvasScaler 缩放挂在其上；
     *   - 元素的绘制对象默认挂在其「所在容器」上；
     *   - 带 Mask / ScrollView 的元素额外拥有一个「分组容器」，其子树绘制对象改挂
     *     入该容器，从而可整体裁剪（stencil mask）与整体平移（滚动）。
     *
     * 叠放顺序 = (sortingOrder, 层级序号)。分组容器以宿主元素层级序号 + 0.5 参与排序，
     * 保证「元素自身绘制对象 → 其子树」的先后与层级一致；每个容器各自重排子级，
     * 命中列表按容器树 + 排序结果重建，故命中顺序与绘制顺序始终一致。
     *
     * 坐标约定：画布逻辑单位、y-down（原点左上）；物理像素 = 逻辑单位 × scaleFactor。
     */
    public final class CanvasSystem extends DreamSystem
    {
        /**
         * 当前实例（服务定位器）。编辑器侧（SceneSelection 拾取/高亮、TransformGizmo 手柄）
         * 通过它查询 UI 元素的屏幕矩形与命中，无需反向依赖 UI 系统。
         */
        public static var current:CanvasSystem;

        // ── 画布级 ──
        private var _layers:Dictionary = new Dictionary();   // 画布 Element → 层容器
        private var _items:Dictionary = new Dictionary();    // 画布 Element → Array<UIDrawable>（绘制序）
        private var _canvasOrder:Array = [];                 // 画布附加序（画布间叠放排序用）

        // ── 帧内结构（每帧重建）──
        private var _entries:Dictionary = null;      // 容器 DrawableContainer → Array<entry>
        private var _frameGroups:Dictionary = null;  // 分组元素 Element → { rect, scroll }
        private var _sortOrdinals:Dictionary = null; // 层级序号表（排序比较用）
        private var _uiRects:Dictionary = null;      // UI 元素 Element → 编辑器矩形信息
        private var _frameScale:Number = 1;          // 当前画布的 CanvasScaler 缩放比
        private var _frameWorld:Boolean = false;     // 当前画布是否世界/相机空间（世界坐标系）
        private var _seq:int = 0;                    // 同容器插入序号（稳定排序用）

        public function CanvasSystem()
        {
            current = this;
        }

        // ── 画布层 / 分组持久结构 ──
        private var _layerParent:Dictionary = new Dictionary();  // 画布 Element → 层当前父容器
        private var _groups:Dictionary = new Dictionary();      // 分组元素 Element → 分组容器
        private var _groupParent:Dictionary = new Dictionary(); // 分组元素 Element → 当前父容器
        private var _groupMask:Dictionary = new Dictionary();   // 分组元素 Element → 遮罩 Drawable
        private var _groupMaskParent:Dictionary = new Dictionary(); // 分组元素 Element → 遮罩当前父容器

        // ── 指针/拖拽 ──
        private var _pressedElement:Element = null;
        // 按下时命中元素所属的画布层：指针事件坐标与拖拽增量都在该层局部坐标系表达
        // （屏幕空间画布 = 画布逻辑单位；世界空间画布 = 世界单位，由层矩阵自动换算）。
        private var _pressedLayer:DrawableContainer = null;
        private var _dragScroll:ScrollView = null;
        private var _dragBar:Scrollbar = null;
        private var _lastPointerX:Number = 0;
        private var _lastPointerY:Number = 0;
        private var _hasPointer:Boolean = false;

        override public function get requiredComponents():Array
        {
            return [Transform, Canvas];
        }

        override public function update(world:World, dt:Number):void
        {
            var re:RenderEngine = RenderEngine.current;
            if (re == null) return;

            var ordinals:Dictionary = world.computeHierarchyOrder();
            _sortOrdinals = ordinals;
            _entries = new Dictionary();
            _frameGroups = new Dictionary();
            // 屏幕矩形表仅供编辑器侧查询（拾取/高亮/Gizmo），独立运行构建不记录。
            CONFIG::STUDIO
            _uiRects = new Dictionary();

            var list:Vector.<Element> = world.query(Transform, Canvas);
            for each (var canvas:Element in list)
            {
                if (!canvas.alive) continue;

                var cc:Canvas = canvas.getComponent(Canvas) as Canvas;
                var mode:String = (cc != null) ? cc.renderMode : Canvas.SCREEN_SPACE;
                var worldMode:Boolean = (mode == Canvas.WORLD_SPACE);
                var cameraMode:Boolean = (mode == Canvas.CAMERA_SPACE);

                // 层容器挂载：屏幕空间 → 屏幕容器（不随相机）；世界空间/相机空间 → 渲染根
                // （随相机，且与世界元素在根容器内一起排序）。
                // 父级未就绪（Starling root 尚未创建）时本帧跳过该画布。
                var parent:DrawableContainer = (worldMode || cameraMode) ? re.root : re.screenContainer;
                if (parent == null) continue;

                var layer:DrawableContainer = _layers[canvas] as DrawableContainer;
                if (layer == null)
                {
                    layer = re.createDrawableContainer();
                    _layers[canvas] = layer;
                    _canvasOrder.push(canvas);
                    _layerParent[canvas] = null;
                }
                // 渲染模式切换（或首次挂载）时改挂到目标父容器；层内子树随之整体搬移。
                if (_layerParent[canvas] !== parent)
                {
                    layer.removeFromParent();
                    parent.addChild(layer);
                    _layerParent[canvas] = parent;
                }

                var s:Number;
                var vp:Object;
                if (worldMode)
                {
                    // 世界空间：画布逻辑矩形仍以「画布单位」（像素语义，与屏幕空间一致）表达，
                    // 层缩放 = 1 / referencePixelsPerUnit，即 100 画布单位 = 1 世界单位。
                    // 这样把屏幕空间画布切成世界空间时，同一份按像素写好的布局只是等比映射到
                    // 世界尺度（不会因为偏移量是几十~上千像素而飞出视野）；层的位置/旋转/缩放
                    // 仍由 Canvas 元素的 Transform 决定。
                    s = 1 / canvasPixelsPerUnit(canvas);
                    vp = worldCanvasRect(canvas);
                    var ct:Transform = canvas.getComponent(Transform) as Transform;
                    if (ct != null)
                    {
                        // 画布局部 → 世界：先在画布单位下按 1/PPU 收缩，再套用 Canvas 的世界矩阵
                        // （即对世界矩阵左乘 S(k)，等价于把 a/b/c/d 同乘 k）。
                        var wm:Matrix = ct.worldMatrix.clone();
                        wm.a *= s; wm.b *= s; wm.c *= s; wm.d *= s;
                        layer.transformationMatrix = wm;
                    }
                }
                else
                {
                    // 画布缩放：CanvasScaler 决定「逻辑单位 → 物理像素」比例；无组件则不缩放。
                    s = canvasScale(canvas);
                    // 画布矩形 = 逻辑分辨率（物理尺寸 ÷ scaleFactor）；子元素锚点相对逻辑矩形解析。
                    vp = { x: 0, y: 0, w: re.stageWidth / s, h: re.stageHeight / s };
                    if (!cameraMode)
                    {
                        var sm:Matrix = new Matrix();
                        sm.scale(s, s);
                        layer.transformationMatrix = sm;
                    }
                    // 相机空间：布局与屏幕空间相同，但层矩阵要「铺满相机视口」——该矩阵依赖
                    // 本帧的相机视图（RenderSystem 稍后才应用），故交给 RenderSystem 在应用相机
                    // 视图之后按层登记的 fill/scale 反算，避免用上一帧的相机视图导致边缘露空。
                }

                _seq = 0;
                _frameScale = s;
                _frameWorld = worldMode || cameraMode;
                layoutElement(canvas, layer, layer, vp, null, null);

                applyScroll();
                applyMasks();
                applyOrder(layer, canvas);
            }

            cleanupGroups();
            cleanup();
            sortCanvases(re);
            if (world.running) updatePointer();
        }

        // ── 布局 ──

        /**
         * 递归布局：解析 el 矩形（overrideRect 非空时由布局覆盖，画布本身即视口矩形），
         * 放置其可绘制组件，并把子级路由到目标容器。
         *
         * container  — el 自身可绘制组件的目标容器；
         * scroll     — el 所处的内容包围盒统计器（null = 不参与滚动统计）。
         */
        private function layoutElement(el:Element, canvasLayer:DrawableContainer,
                                       container:DrawableContainer, parentRect:Object,
                                       scroll:Object, overrideRect:Object = null):void
        {
            var rt:RectTransform = el.getComponent(RectTransform) as RectTransform;
            var isCanvas:Boolean = el.getComponent(Canvas) != null;
            var rect:Object = (overrideRect != null)
                ? overrideRect
                : (isCanvas
                    ? parentRect
                    : ((rt != null) ? resolveChildRect(rt, parentRect) : parentRect));

            // 记录画布局部矩形与所属层：编辑器拾取/高亮/Gizmo 查询用
            // （屏幕/世界空间由 editorRectOf 按各自坐标系换算）。
            // 画布自身的「矩形」= 视口/参考尺寸，不是可编辑的 RectTransform，故不计入。
            CONFIG::STUDIO
            {
                if (!isCanvas && rt != null && _uiRects != null)
                {
                    _uiRects[el] = {
                        x: rect.x, y: rect.y, w: rect.w, h: rect.h,
                        scale: _frameScale, world: _frameWorld, layer: canvasLayer
                    };
                }
            }

            // 1. 宿主自身可绘制组件 → 当前容器。
            var comps:Vector.<DreamComponent> = el.getAllComponents();
            for each (var c:DreamComponent in comps)
            {
                var ui:UIDrawable = c as UIDrawable;
                if (ui == null || ui.drawable == null) continue;

                ui.attachTo(container);

                // 滚动条：可绘制对象是滑块，矩形由目标 ScrollView 决定（覆盖元素矩形）。
                var dr:Object = rect;
                if (ui is Scrollbar)
                {
                    var bar:Scrollbar = ui as Scrollbar;
                    bar._trackRect = rect;
                    dr = bar.thumbRect(rect);
                }

                var d:Drawable = ui.drawable;
                d.x = dr.x;
                d.y = dr.y;
                d.width = Math.max(0, dr.w);   // 配置异常时避免负尺寸
                d.height = Math.max(0, dr.h);

                // 交互缩放（Button 按下等）：围绕矩形中心均匀缩放。
                var k:Number = ui.interactionScale;
                if (k != 1)
                {
                    var cx:Number = dr.x + dr.w * 0.5;
                    var cy:Number = dr.y + dr.h * 0.5;
                    d.x = cx - dr.w * k * 0.5;
                    d.y = cy - dr.h * k * 0.5;
                    d.scaleX *= k;
                    d.scaleY *= k;
                }

                // 内容包围盒统计（滚动条不计入内容）。
                if (scroll != null && !(ui is Scrollbar))
                    expandScroll(scroll, rect.x, rect.y, rect.w, rect.h);

                addEntry(container, d, ui, null, ui.sortingOrder, elementOrder(el));
            }

            var t:Transform = el.getComponent(Transform) as Transform;
            if (t == null) return;

            // 2. 分组容器：带 Mask / ScrollView 的元素为子树建立独立容器。
            var mask:Mask = el.getComponent(Mask) as Mask;
            var sv:ScrollView = el.getComponent(ScrollView) as ScrollView;

            var childContainer:DrawableContainer = container;
            var arrangeScroll:Object = scroll;   // 子级摆放时归属的内容统计器
            var contentScroll:Object = null;     // ScrollView 的内容统计器

            if (mask != null || sv != null)
            {
                childContainer = ensureGroup(el, container);
                var state:Object = { rect: rect, scroll: null };
                if (sv != null)
                {
                    contentScroll = { minX: 0, minY: 0, maxX: 0, maxY: 0, has: false };
                    state.scroll = contentScroll;
                    arrangeScroll = contentScroll;
                }
                _frameGroups[el] = state;
            }

            // 3. Layout：由布局排列子级（位置覆盖，尺寸取子级配置）。
            var layout:Layout = el.getComponent(Layout) as Layout;
            if (layout != null)
            {
                layoutChildren(layout, t, rect, canvasLayer, container, childContainer,
                               arrangeScroll, scroll, sv);
                return;
            }

            // 4. 普通子级：按 ScrollView.contentName 路由到内容容器或原容器。
            var contentName:String = (sv != null) ? effectiveContentName(sv, t) : "";
            for (var i:int = 0; i < t.childCount; i++)
            {
                var child:Transform = t.getChildAt(i);
                if (child == null || child.owner == null) continue;
                // 无 RectTransform 的子级不参与 UI 布局（跳过其子树）。
                if (child.owner.getComponent(RectTransform) == null) continue;

                var toContent:Boolean = (sv == null) ? true : isContentChild(contentName, child.owner);
                var cCont:DrawableContainer = toContent ? childContainer : container;
                var cScroll:Object = (sv == null) ? scroll : (toContent ? contentScroll : scroll);
                layoutElement(child.owner, canvasLayer, cCont, rect, cScroll);
            }
        }

        /**
         * 子级矩形：锚点相对父矩形（尺寸）解析后，平移到画布坐标。
         * resolveRect 返回的是相对父矩形左上角的坐标，必须加上父矩形原点，
         * 否则多级嵌套（Mask/ScrollView 分组容器、容器元素）的子级会落在画布原点。
         */
        private static function resolveChildRect(rt:RectTransform, parentRect:Object):Object
        {
            var r:Object = rt.resolveRect(parentRect.w, parentRect.h);
            r.x += parentRect.x;
            r.y += parentRect.y;
            return r;
        }

        /**
         * 布局排列：子级按方向顺序堆叠，垂直宽度/水平高度拉伸为容器内尺寸。
         * ScrollView 场景下，仅内容子级参与排列；非内容子级（如滚动条）按自身锚点独立布局。
         */
        private function layoutChildren(layout:Layout, t:Transform, containerRect:Object,
                                        canvasLayer:DrawableContainer, baseContainer:DrawableContainer,
                                        contentContainer:DrawableContainer, arrangeScroll:Object,
                                        outerScroll:Object, sv:ScrollView):void
        {
            var vertical:Boolean = layout.orientation != "horizontal";
            var pad:Number = layout.padding;
            var cursor:Number = vertical ? containerRect.y + pad : containerRect.x + pad;
            var contentName:String = (sv != null) ? effectiveContentName(sv, t) : "";

            for (var i:int = 0; i < t.childCount; i++)
            {
                var child:Transform = t.getChildAt(i);
                if (child == null || child.owner == null) continue;
                var krt:RectTransform = child.owner.getComponent(RectTransform) as RectTransform;
                if (krt == null) continue; // 非 UI 子级跳过

                var toContent:Boolean = (sv == null) ? true : isContentChild(contentName, child.owner);
                if (!toContent)
                {
                    // 非内容子级：按自身锚点独立布局，不参与排列。
                    layoutElement(child.owner, canvasLayer, baseContainer, containerRect, outerScroll);
                    continue;
                }

                // 子级尺寸取自其自身 RectTransform（offsetMax−offsetMin，布局不改动序列化字段）；
                // 堆叠轴取自身配置尺寸，交叉轴拉伸为容器内尺寸。
                var w:Number = krt.offsetMaxX - krt.offsetMinX;
                var h:Number = krt.offsetMaxY - krt.offsetMinY;
                var childRect:Object;
                if (vertical)
                {
                    childRect = {
                        x: containerRect.x + pad,
                        y: cursor,
                        w: Math.max(0, containerRect.w - pad * 2),
                        h: Math.max(0, h)
                    };
                    cursor += h + layout.spacing;
                }
                else
                {
                    childRect = {
                        x: cursor,
                        y: containerRect.y + pad,
                        w: Math.max(0, w),
                        h: Math.max(0, containerRect.h - pad * 2)
                    };
                    cursor += w + layout.spacing;
                }
                layoutElement(child.owner, canvasLayer, contentContainer, containerRect, arrangeScroll, childRect);
            }
        }

        // ── 分组容器（Mask / ScrollView 共用） ──

        /** 取（必要时创建）元素的分组容器，确保其挂在 parent 下并登记排序条目。 */
        private function ensureGroup(el:Element, parent:DrawableContainer):DrawableContainer
        {
            var g:DrawableContainer = _groups[el] as DrawableContainer;
            if (g == null)
            {
                g = RenderEngine.current.createDrawableContainer();
                _groups[el] = g;
                _groupParent[el] = null;
            }
            if (_groupParent[el] !== parent)
            {
                g.removeFromParent();
                parent.addChild(g);
                _groupParent[el] = parent;
            }
            // 分组容器代表「宿主元素的子树」，排在宿主自身绘制对象之后（层级序号 + 0.5）。
            addEntry(parent, g, null, g, 0, elementOrder(el) + 0.5);
            return g;
        }

        /** 追加容器条目（本帧结构，供排序与命中列表构建）。 */
        private function addEntry(container:DrawableContainer, d:Drawable, ui:UIDrawable,
                                  grp:DrawableContainer, order:int, ord:Number):void
        {
            var arr:Array = _entries[container] as Array;
            if (arr == null)
            {
                arr = [];
                _entries[container] = arr;
            }
            arr.push({ d: d, ui: ui, grp: grp, order: order, ord: ord, seq: _seq++ });
        }

        /** 扩展滚动内容包围盒。 */
        private static function expandScroll(scroll:Object, x:Number, y:Number, w:Number, h:Number):void
        {
            if (!scroll.has)
            {
                scroll.has = true;
                scroll.minX = x; scroll.minY = y;
                scroll.maxX = x + w; scroll.maxY = y + h;
                return;
            }
            if (x < scroll.minX) scroll.minX = x;
            if (y < scroll.minY) scroll.minY = y;
            if (x + w > scroll.maxX) scroll.maxX = x + w;
            if (y + h > scroll.maxY) scroll.maxY = y + h;
        }

        /** ScrollView 生效的内容子元素名；为空或未匹配到直接子级时返回空串（= 整个子树）。 */
        private static function effectiveContentName(sv:ScrollView, t:Transform):String
        {
            var name:String = sv.contentName;
            if (name == null || name.length == 0) return "";
            for (var i:int = 0; i < t.childCount; i++)
            {
                var c:Transform = t.getChildAt(i);
                if (c != null && c.owner != null && c.owner.name == name) return name;
            }
            return ""; // 未匹配：回退整个子树
        }

        private static function isContentChild(contentName:String, child:Element):Boolean
        {
            if (contentName == null || contentName.length == 0) return true;
            return child.name == contentName;
        }

        // ── 每帧收尾：滚动夹取 → 遮罩定位 → 叠放排序 ──

        /** 更新各 ScrollView 的内容/视口尺寸与夹取，并平移其内容容器。 */
        private function applyScroll():void
        {
            for (var k:Object in _frameGroups)
            {
                var el:Element = k as Element;
                var state:Object = _frameGroups[el] as Object;
                if (state == null || state.scroll == null) continue;
                var sv:ScrollView = el.getComponent(ScrollView) as ScrollView;
                if (sv == null) continue;

                var rect:Object = state.rect;
                var st:Object = state.scroll;
                var cw:Number = st.has ? (st.maxX - st.minX) : 0;
                var ch:Number = st.has ? (st.maxY - st.minY) : 0;

                sv.viewportWidth = rect.w;
                sv.viewportHeight = rect.h;
                sv.contentWidth = cw;
                sv.contentHeight = ch;
                sv.maxScrollX = sv.horizontal ? Math.max(0, cw - rect.w) : 0;
                sv.maxScrollY = sv.vertical ? Math.max(0, ch - rect.h) : 0;
                if (!sv.horizontal) sv.scrollX = 0;
                if (!sv.vertical) sv.scrollY = 0;
                sv.clampScroll();

                var g:DrawableContainer = _groups[el] as DrawableContainer;
                if (g != null)
                {
                    g.x = -sv.scrollX;
                    g.y = -sv.scrollY;
                }
            }
        }

        /**
         * 定位各分组容器的遮罩矩形。
         *
         * 遮罩作为「分组容器所在父容器」的子级、以画布坐标定位：这样它与被遮罩容器同处
         * 一个坐标空间（Starling 遮罩变换语义下无歧义），且只跟随外层滚动移动，不跟随
         * 自身 ScrollView 滚动 —— 即遮罩固定在视口位置。
         */
        private function applyMasks():void
        {
            var re:RenderEngine = RenderEngine.current;
            for (var k:Object in _frameGroups)
            {
                var el:Element = k as Element;
                var g:DrawableContainer = _groups[el] as DrawableContainer;
                var parentC:DrawableContainer = _groupParent[el] as DrawableContainer;
                if (g == null || parentC == null) continue;

                var mask:Mask = el.getComponent(Mask) as Mask;
                var maskD:Drawable = _groupMask[el] as Drawable;

                if (mask == null)
                {
                    if (maskD != null)
                    {
                        g.mask = null;
                        maskD.removeFromParent();
                        delete _groupMask[el];
                        delete _groupMaskParent[el];
                    }
                    continue;
                }

                if (maskD == null)
                {
                    maskD = re.createQuad(1, 1, 0xFFFFFF);
                    // 保持 touchable=true：Starling 的 DisplayObject.hitTestMask 通过
                    // _mask.hitTest() 判定区域内外，touchable=false 会让判定恒为「外部」，
                    // 导致该子树命中全部失效。遮罩不在命中列表 _items 中，不参与 UI 拾取。
                    g.mask = maskD;
                    _groupMask[el] = maskD;
                    _groupMaskParent[el] = null;
                }
                if (_groupMaskParent[el] !== parentC)
                {
                    maskD.removeFromParent();
                    parentC.addChild(maskD);
                    _groupMaskParent[el] = parentC;
                }

                var rect:Object = (_frameGroups[el] as Object).rect;
                var pad:Number = Math.max(0, mask.padding);
                maskD.x = rect.x + pad;
                maskD.y = rect.y + pad;
                maskD.width = Math.max(0, rect.w - pad * 2);
                maskD.height = Math.max(0, rect.h - pad * 2);
                maskD.alpha = mask.showMaskGraphic ? 1 : 0;
                g.maskInverted = mask.invert;
            }
        }

        /** 逐容器按 (sortingOrder, 层级序号, 插入序) 重排子级，并重建绘制序命中列表。 */
        private function applyOrder(layer:DrawableContainer, canvas:Element):void
        {
            var containers:Array = [];
            collectContainers(layer, containers);
            for each (var c:DrawableContainer in containers)
            {
                var arr:Array = _entries[c] as Array;
                if (arr == null) continue;
                arr.sort(compareEntry);
                for (var i:int = 0; i < arr.length; i++)
                    c.setChildIndex((arr[i] as Object).d as Drawable, i);
            }

            var hit:Array = [];
            collectItems(layer, hit);
            _items[canvas] = hit;
        }

        /** 深度优先收集容器（父先于子）。 */
        private function collectContainers(c:DrawableContainer, out:Array):void
        {
            out.push(c);
            var arr:Array = _entries[c] as Array;
            if (arr == null) return;
            for (var i:int = 0; i < arr.length; i++)
            {
                var g:DrawableContainer = (arr[i] as Object).grp as DrawableContainer;
                if (g != null) collectContainers(g, out);
            }
        }

        /** 按容器树 + 已排序条目收集可绘制组件（结果即绘制顺序，末位在最上层）。 */
        private function collectItems(c:DrawableContainer, out:Array):void
        {
            var arr:Array = _entries[c] as Array;
            if (arr == null) return;
            for (var i:int = 0; i < arr.length; i++)
            {
                var e:Object = arr[i];
                var g:DrawableContainer = e.grp as DrawableContainer;
                if (g != null) collectItems(g, out);
                else if (e.ui != null) out.push(e.ui);
            }
        }

        /** 条目比较：先 sortingOrder，再层级序号，最后插入序（稳定）。 */
        private static function compareEntry(a:Object, b:Object):Number
        {
            var o:int = int(a.order) - int(b.order);
            if (o != 0) return o;
            var d:Number = Number(a.ord) - Number(b.ord);
            if (d != 0) return d;
            return int(a.seq) - int(b.seq);
        }

        /** 移除本帧不再需要的分组容器（元素销毁、Mask/ScrollView 被移除）。 */
        private function cleanupGroups():void
        {
            var dead:Array = [];
            for (var k:Object in _groups)
            {
                var el:Element = k as Element;
                if (el == null || !el.alive || _frameGroups[el] == null) dead.push(el);
            }
            for each (var d:Element in dead)
            {
                var g:DrawableContainer = _groups[d] as DrawableContainer;
                if (g != null)
                {
                    g.mask = null;
                    g.removeFromParent();
                }
                delete _groups[d];
                delete _groupParent[d];
                delete _groupMask[d];
                delete _groupMaskParent[d];
            }
        }

        /** 移除已销毁/失去 Canvas 组件的画布层。 */
        private function cleanup():void
        {
            var i:int = _canvasOrder.length;
            while (i > 0)
            {
                i--;
                var el:Element = _canvasOrder[i] as Element;
                if (el == null || !el.alive || el.getComponent(Canvas) == null)
                {
                    var layer:DrawableContainer = _layers[el] as DrawableContainer;
                    if (layer != null) layer.removeFromParent();
                    delete _layers[el];
                    delete _layerParent[el];
                    delete _items[el];
                    _canvasOrder.splice(i, 1);
                }
            }
        }

        // ── 画布间叠放 ──

        /**
         * 画布之间叠放（按排序结果 (sortingOrder, 层级序号) 处理）：
         *   - 屏幕空间画布：在屏幕容器内重排子级索引（整体位于世界内容之上，画布间相对顺序生效）；
         *   - 世界空间画布：登记到 RenderEngine，由 RenderSystem 与世界元素合并排序，
         *     使画布层可按 sortingOrder 落在世界内容之间（被前景遮挡 / 遮挡背景）；
         *   - 相机空间画布：同样登记（参与世界排序），并带 fill 标记 —— 层矩阵由 RenderSystem
         *     在相机视图应用后反算为「铺满视口」。
         */
        private function sortCanvases(re:RenderEngine):void
        {
            _canvasOrder.sort(compareCanvases);
            var screenIndex:int = 0;
            var worldLayers:Array = [];
            for (var i:int = 0; i < _canvasOrder.length; i++)
            {
                var el:Element = _canvasOrder[i] as Element;
                var layer:DrawableContainer = _layers[el] as DrawableContainer;
                if (layer == null) continue;
                var c:Canvas = el.getComponent(Canvas) as Canvas;
                var mode:String = (c != null) ? c.renderMode : Canvas.SCREEN_SPACE;
                if (mode == Canvas.SCREEN_SPACE)
                {
                    re.setScreenLayerIndex(layer, screenIndex++);
                    continue;
                }
                var entry:Object = { layer: layer, element: el, order: (c != null) ? c.sortingOrder : 0,
                                     dfs: elementOrder(el) };
                if (mode == Canvas.CAMERA_SPACE)
                {
                    entry.fill = true;
                    entry.scale = canvasScale(el);
                }
                worldLayers.push(entry);
            }
            re.setWorldCanvasLayers(worldLayers);
        }

        /** 画布比较：先 sortingOrder，再层级序号。 */
        private function compareCanvases(a:Object, b:Object):Number
        {
            var ea:Element = a as Element;
            var eb:Element = b as Element;
            var ca:Canvas = (ea != null) ? ea.getComponent(Canvas) as Canvas : null;
            var cb:Canvas = (eb != null) ? eb.getComponent(Canvas) as Canvas : null;
            var oa:int = (ca != null) ? ca.sortingOrder : 0;
            var ob:int = (cb != null) ? cb.sortingOrder : 0;
            if (oa != ob) return oa - ob;
            return elementOrder(ea) - elementOrder(eb);
        }

        /** 元素层级序号（读 _sortOrdinals）；缺失取最大（叠放最上层）。 */
        private function elementOrder(e:Element):int
        {
            if (e == null || _sortOrdinals == null) return int.MAX_VALUE;
            var v:* = _sortOrdinals[e];
            return (v === undefined) ? int.MAX_VALUE : int(v);
        }

        // ── 指针交互 ──

        private function updatePointer():void
        {
            var input:InputManager = InputManager.current;
            if (input == null) return;
            var mx:Number = input.mouseX;
            var my:Number = input.mouseY;

            // 滚轮：命中处沿祖先链向上找最近 ScrollView 并滚动。
            var wheel:Number = input.mouseWheelDelta;
            if (wheel != 0)
            {
                var hover:Object = hitTest(mx, my);
                if (hover != null)
                {
                    var wsv:ScrollView = findScrollView(hover.element as Element);
                    if (wsv != null) wsv.scrollBy(0, -wheel * wsv.scrollSensitivity);
                }
            }

            if (input.isMousePressed(InputManager.MouseButton.LEFT))
            {
                var down:Object = hitTest(mx, my);
                if (down != null)
                {
                    _pressedElement = down.element as Element;
                    _pressedLayer = down.layer as DrawableContainer;
                    // 指针为舞台（物理像素）坐标 → 换算到命中元素所属画布的局部坐标系
                    // （屏幕空间画布 = 画布逻辑单位；世界空间画布 = 世界单位）。
                    var dp:Point = localPoint(_pressedLayer, mx, my);
                    dispatch(_pressedElement, "onPointerDown", dp.x, dp.y);
                    // 拖拽目标：优先滚动条滑块，其次 ScrollView 内容。
                    _dragBar = findScrollbar(_pressedElement);
                    _dragScroll = (_dragBar != null) ? null : findScrollView(_pressedElement);
                }
            }
            else if (input.isMouseReleased(InputManager.MouseButton.LEFT))
            {
                if (_pressedElement != null && _pressedElement.alive)
                {
                    var upPt:Point = localPoint(_pressedLayer, mx, my);
                    dispatch(_pressedElement, "onPointerUp", upPt.x, upPt.y);
                    var up:Object = hitTest(mx, my);
                    if (up != null && up.element == _pressedElement)
                        dispatch(_pressedElement, "onPointerClick", upPt.x, upPt.y);
                }
                _pressedElement = null;
                _pressedLayer = null;
                _dragScroll = null;
                _dragBar = null;
            }

            // 拖拽位移：按住期间把鼠标位移交给滚动条/ScrollView（增量换算到画布局部单位）。
            if (_hasPointer && input.isMouseDown(InputManager.MouseButton.LEFT)
                && (_dragBar != null || _dragScroll != null))
            {
                var p0:Point = localPoint(_pressedLayer, _lastPointerX, _lastPointerY);
                var p1:Point = localPoint(_pressedLayer, mx, my);
                var dx:Number = p1.x - p0.x;
                var dy:Number = p1.y - p0.y;
                if (_dragBar != null)
                    _dragBar.dragBy((_dragBar.direction == "horizontal") ? dx : dy);
                else if (_dragScroll != null)
                    _dragScroll.scrollBy(_dragScroll.horizontal ? -dx : 0,
                                         _dragScroll.vertical ? -dy : 0);
            }
            _lastPointerX = mx;
            _lastPointerY = my;
            _hasPointer = true;
        }

        /** 舞台（物理像素）点 → 指定画布层局部坐标；无层时按 1:1 处理。 */
        private static function localPoint(layer:DrawableContainer, stageX:Number, stageY:Number):Point
        {
            if (layer == null) return new Point(stageX, stageY);
            return layer.globalToLocal(stageX, stageY);
        }

        /** 沿 Transform 祖先链（含自身）查找最近的 ScrollView。 */
        private static function findScrollView(el:Element):ScrollView
        {
            var t:Transform = (el != null) ? el.getComponent(Transform) as Transform : null;
            while (t != null && t.owner != null)
            {
                var sv:ScrollView = t.owner.getComponent(ScrollView) as ScrollView;
                if (sv != null) return sv;
                t = t.parent;
            }
            return null;
        }

        /** 沿 Transform 祖先链（含自身）查找最近的 Scrollbar。 */
        private static function findScrollbar(el:Element):Scrollbar
        {
            var t:Transform = (el != null) ? el.getComponent(Transform) as Transform : null;
            while (t != null && t.owner != null)
            {
                var sb:Scrollbar = t.owner.getComponent(Scrollbar) as Scrollbar;
                if (sb != null) return sb;
                t = t.parent;
            }
            return null;
        }

        /** 返回鼠标位置（舞台物理像素）最上层的可命中 UI 元素及其所属画布层；无则 null。 */
        private function hitTest(mx:Number, my:Number):Object
        {
            // _canvasOrder 按排序结果升序，故反向遍历即最上层优先。
            for (var c:int = _canvasOrder.length - 1; c >= 0; c--)
            {
                var hit:Object = hitTestCanvas(_canvasOrder[c] as Element, mx, my);
                if (hit != null) return hit;
            }
            return null;
        }

        /** 单个画布内命中（_items 为绘制序，反向遍历即最上层优先）；无则 null。 */
        private function hitTestCanvas(canvasEl:Element, mx:Number, my:Number):Object
        {
            var items:Array = _items[canvasEl] as Array;
            if (items == null) return null;
            for (var i:int = items.length - 1; i >= 0; i--)
            {
                var ui:UIDrawable = items[i] as UIDrawable;
                if (ui == null || ui.owner == null || !ui.owner.alive) continue;
                // hitTestWorld 解析完整父链变换（含世界/相机空间画布的层变换与相机）与祖先遮罩，
                // 传入舞台（物理像素）坐标。
                if (ui.drawable != null && ui.drawable.hitTestWorld(mx, my))
                    return { element: ui.owner, layer: _layers[canvasEl] as DrawableContainer };
            }
            return null;
        }

        /** 画布当前缩放比（无 CanvasScaler 或视口非法时为 1）。 */
        private function canvasScale(canvasEl:Element):Number
        {
            var re:RenderEngine = RenderEngine.current;
            if (re == null) return 1;
            var scaler:CanvasScaler = canvasEl.getComponent(CanvasScaler) as CanvasScaler;
            if (scaler == null) return 1;
            var s:Number = scaler.computeScaleFactor(re.stageWidth, re.stageHeight);
            return (s > 0) ? s : 1;
        }

        /**
         * 世界空间画布的逻辑矩形（画布单位，原点 = 画布元素原点，y-down）。
         * 尺寸取自身 RectTransform 的宽高（画布单位）；无 RectTransform 或尺寸非正时，
         * 回退 CanvasScaler 的参考分辨率（默认 1920×1080）。
         * 实际世界尺寸 = 本矩形 × 层缩放（1 / referencePixelsPerUnit）。
         */
        private static function worldCanvasRect(canvas:Element):Object
        {
            var rt:RectTransform = canvas.getComponent(RectTransform) as RectTransform;
            if (rt != null)
            {
                var w:Number = rt.offsetMaxX - rt.offsetMinX;
                var h:Number = rt.offsetMaxY - rt.offsetMinY;
                if (w > 0 && h > 0) return { x: 0, y: 0, w: w, h: h };
            }
            var scaler:CanvasScaler = canvas.getComponent(CanvasScaler) as CanvasScaler;
            var rw:Number = (scaler != null && scaler.referenceResolutionX > 0)
                ? scaler.referenceResolutionX : 1920;
            var rh:Number = (scaler != null && scaler.referenceResolutionY > 0)
                ? scaler.referenceResolutionY : 1080;
            return { x: 0, y: 0, w: rw, h: rh };
        }

        /** 画布的「纹理像素 → UI 单位」基准（referencePixelsPerUnit；无组件或缺省时 100）。 */
        private static function canvasPixelsPerUnit(canvas:Element):Number
        {
            var scaler:CanvasScaler = canvas.getComponent(CanvasScaler) as CanvasScaler;
            if (scaler == null) return 100;
            return scaler.referencePixelsPerUnit > 0 ? scaler.referencePixelsPerUnit : 100;
        }

        // ── 编辑器查询（视口拾取 / 选中高亮 / Gizmo）──

        /**
         * 屏幕空间画布内命中的最上层 UI 元素；无则 null。
         * 屏幕容器整体位于渲染根之上（见 RenderEngine.screenContainer），故这部分内容在
         * 编辑器拾取中优先于任何世界内容；世界/相机空间画布内的 UI 不在本方法范围内，
         * 由调用方按渲染顺序逐个画布调 hitTestCanvasElement。
         * 与画布内交互共用同一命中逻辑（含祖先遮罩裁剪），故遮罩外的部分不可拾取。
         */
        public function hitTestScreenElement(stageX:Number, stageY:Number):Element
        {
            for (var c:int = _canvasOrder.length - 1; c >= 0; c--)
            {
                var el:Element = _canvasOrder[c] as Element;
                var canvas:Canvas = el.getComponent(Canvas) as Canvas;
                // 未知模式与屏幕空间同义（与 update 的分支默认一致）。
                if (canvas != null && canvas.renderMode != Canvas.SCREEN_SPACE) continue;
                var hit:Object = hitTestCanvas(el, stageX, stageY);
                if (hit != null) return hit.element as Element;
            }
            return null;
        }

        /**
         * 指定画布（世界/相机空间）内命中的最上层 UI 元素；无则 null。
         * 编辑器拾取按渲染顺序逐个画布调用，使画布内 UI 与世界元素的先后与绘制一致。
         */
        public function hitTestCanvasElement(canvasEl:Element, stageX:Number, stageY:Number):Element
        {
            var hit:Object = hitTestCanvas(canvasEl, stageX, stageY);
            return (hit != null) ? (hit.element as Element) : null;
        }

        /**
         * 元素在当前帧的编辑器矩形（含所属坐标系），供选中高亮与 Gizmo 定位：
         *   { space:"screen"|"world", x, y, w, h, scale, layer }
         *   - screen：stage 像素、y-down（画布逻辑单位 × CanvasScaler 缩放比）；
         *   - world ：渲染根局部（世界）坐标的轴对齐包围盒，由画布层矩阵把画布局部矩形
         *     四角变换得到（画布旋转时取 AABB）。
         * scale = 画布局部单位 → 其坐标系单位的比例（世界空间为 1）。
         * 非 UI 元素（无 RectTransform）或本帧未参与布局时返回 null。
         */
        public function editorRectOf(el:Element):Object
        {
            if (el == null || _uiRects == null) return null;
            var r:Object = _uiRects[el] as Object;
            if (r == null) return null;

            var layer:DrawableContainer = r.layer as DrawableContainer;
            if (!r.world)
            {
                var s:Number = Number(r.scale);
                return {
                    space: "screen",
                    x: Number(r.x) * s, y: Number(r.y) * s,
                    w: Number(r.w) * s, h: Number(r.h) * s,
                    scale: s, layer: layer
                };
            }

            if (layer == null) return null;
            var m:Matrix = layer.transformationMatrix;
            var x0:Number = Number(r.x);
            var y0:Number = Number(r.y);
            var x1:Number = x0 + Number(r.w);
            var y1:Number = y0 + Number(r.h);
            var minX:Number = Number.MAX_VALUE;
            var minY:Number = Number.MAX_VALUE;
            var maxX:Number = -Number.MAX_VALUE;
            var maxY:Number = -Number.MAX_VALUE;
            for (var i:int = 0; i < 4; i++)
            {
                var px:Number = ((i & 1) == 0) ? x0 : x1;
                var py:Number = (i < 2) ? y0 : y1;
                var wx:Number = m.a * px + m.c * py + m.tx;
                var wy:Number = m.b * px + m.d * py + m.ty;
                if (wx < minX) minX = wx;
                if (wy < minY) minY = wy;
                if (wx > maxX) maxX = wx;
                if (wy > maxY) maxY = wy;
            }
            return { space: "world", x: minX, y: minY, w: maxX - minX, h: maxY - minY,
                     scale: 1, layer: layer };
        }

        private static function dispatch(el:Element, method:String, x:Number, y:Number):void
        {
            var comps:Vector.<DreamComponent> = el.getAllComponents();
            for each (var c:DreamComponent in comps)
            {
                // 组件禁用（enabled=false）时不接收指针事件。
                if (!c.enabled) continue;
                var h:IPointerHandler = c as IPointerHandler;
                if (h == null) continue;
                switch (method)
                {
                    case "onPointerDown": h.onPointerDown(x, y); break;
                    case "onPointerUp":   h.onPointerUp(x, y);   break;
                    default:              h.onPointerClick(x, y); break;
                }
            }
        }
    }
}

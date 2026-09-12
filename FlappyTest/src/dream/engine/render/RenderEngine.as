package dream.engine.render
{
    import flash.display.BitmapData;
    import flash.display.Stage;
    import flash.events.Event;
    import flash.events.KeyboardEvent;
    import flash.events.MouseEvent;
    import flash.geom.Matrix;
    import flash.geom.Point;
    import flash.geom.Rectangle;
    import flash.ui.Keyboard;

    import starling.core.Starling;
    import starling.display.DisplayObject;
    import starling.display.Image;
    import starling.display.Quad;
    import starling.display.Sprite;
    import starling.text.TextField;
    import starling.text.TextFormat;

    /**
     * 渲染引擎：封装 Starling 初始化、根容器、juggler 驱动与显示对象工厂。
     * 用户通过此类创建所有渲染资源，不直接接触 Starling。
     *
     * 用法：
     *   var re:RenderEngine = new RenderEngine(stage);
     *   re.start();  // 启动渲染循环
     *   var quad:Drawable = re.createQuad(64, 64, 0xFF0000);
     *   re.root.addChild(quad);
     *
     * Starling 的 juggler 由内部 ENTER_FRAME 驱动，外部无需手动 advance。
     * root 是顶层 DrawableContainer，所有 Renderer 的可绘制对象挂在其下。
     *
     * 注：starling.events.Event 与 flash.events.Event 同名。此处 import flash 的
     *     （ENTER_FRAME 用得多），Starling 的 ROOT_CREATED 用字符串 "rootCreated"
     *     直接传入。回调参数用 Object 类型接收 starling.events.Event 实例。
     */
    public final class RenderEngine
    {
        // Starling Event.ROOT_CREATED 的字符串值。
        // starling.events.Event 与 flash.events.Event 同名无法 import，用字符串直接传入。
        // 此值在 Starling 2.x 长期稳定。
        private static const ROOT_CREATED:String = "rootCreated";

        // 滚轮缩放因子（编辑器视口导航专用）。缩放上下限由 CameraComponent 常量约束。
        CONFIG::STUDIO
        {
            private static const ZoomSpeed:Number = 1.1;
        }

        /**
         * 当前渲染引擎实例（服务定位器）。由构造函数设置。
         * 供 DisplayComponent 等需要延迟创建默认 Drawable 的组件在 onLoad 时
         * 通过 createQuad 获取默认可绘制对象，无需显式注入 RenderEngine。
         */
        public static var current:RenderEngine;

        private var _starling:Starling;
        private var _rootContainer:Sprite;
        private var _root:DrawableContainer;
        // 屏幕空间 UI 层容器：整体位于世界根之上，画布层挂在其下（便于画布间排序）。
        private var _screenContainer:DrawableContainer;
        // 本帧登记的世界空间画布层（Array<{layer, order, dfs}>）：CanvasSystem 写入，
        // RenderSystem 排序渲染根子级时与世界元素合并 → 画布层与世界内容按 sortingOrder 交错。
        private var _worldCanvasLayers:Array = [];
        private var _stage:Stage;
        private var _started:Boolean = false;
        // 当前相机缩放缓存（applyCameraView 每帧更新）。编辑器覆盖层按此反算固定像素厚度。
        private var _cameraZoom:Number = 1.0;

        /**
         * 渲染根就绪回调（ROOT_CREATED 之后触发一次，可为空）。
         *
         * 发布构建的启动加载必须挂在这里，不要在文档类构造里直接读场景：Starling 的纹理上传与
         * 顶点/索引缓冲都要求 Stage3D 上下文已创建（context 为空时抛 MissingContextError），
         * 早于 ROOT_CREATED 发起时 ResourceManager 会把它吞成一条 trace、回调 null，
         * 所有资源静默回退成白色方块（场景元素本身照常出现，表现为"有画面但全是白的"）。
         */
        public var onReady:Function;

        // ── 编辑器视口导航（条件编译：独立运行构建不包含这些）──
        CONFIG::STUDIO
        {
            // 是否启用编辑器视口导航（滚轮缩放、空格+左键/中键平移）。
            // 由 DreamEngine 在 STUDIO 模式启动时置 true，运行模式保持 false。
            private var _viewportNavigationEnabled:Boolean = false;

            // 空格+左键/中键拖拽平移状态。
            private var _isPanning:Boolean = false;
            private var _lastMouseX:Number = 0;
            private var _lastMouseY:Number = 0;
            private var _spacePressed:Boolean = false;

            // 编辑器导航相机（查看用，独立于场景对象）：
            // _editorCamX/_editorCamY 为屏幕中心对应的世界点，_editorOrthoSize 为视野半高。
            // 初始值比场景相机默认 orthographicSize(5) 略大：编辑器下留出余量，
            // 运行后切换到场景相机视野更聚焦。
            private var _editorCamX:Number = 0;
            private var _editorCamY:Number = 0;
            private var _editorOrthoSize:Number = 6;

            /** 应用编辑器导航相机（θ=0）到渲染根容器。编辑器模式每帧由 RenderSystem 调用。 */
            public function applyEditorCamera():void
            {
                applyCameraView(_editorOrthoSize, 0, _editorCamX, _editorCamY);
            }

            /**
             * 编辑器滚轮缩放：以鼠标位置为锚调整视野半高（orthographicSize）。
             * 放大 → orthoSize 减小（同一屏幕显示更小世界范围）。
             * p = camPos + (s - vc)/zoom，缩放后 camPos' = p - (s - vc)/zoom'。
             */
            private function zoomEditorAt(cx:Number, cy:Number, factor:Number):void
            {
                var ortho:Number = _editorOrthoSize;
                var newOrtho:Number = Math.max(CameraComponent.MinOrthoSize,
                    Math.min(CameraComponent.MaxOrthoSize, ortho / factor));
                var vcX:Number = _stage.stageWidth * 0.5;
                var vcY:Number = _stage.stageHeight * 0.5;
                var zoom:Number = _stage.stageHeight / (2 * ortho);
                var newZoom:Number = _stage.stageHeight / (2 * newOrtho);
                var ux:Number = (cx - vcX) / zoom;
                var uy:Number = (cy - vcY) / zoom;
                var k:Number = 1 - zoom / newZoom;
                _editorCamX += ux * k;
                _editorCamY += uy * k;
                _editorOrthoSize = newOrtho;
                applyEditorCamera();
            }

            /**
             * 视口左键点击回调（编辑器模式，未按空格时触发）。
             * 签名：(stageX:Number, stageY:Number, shiftKey:Boolean, ctrlKey:Boolean)。
             * 由 DreamEngine 注入，用于场景拾取选中（单选/Shift+Ctrl 增删/空白框选）。
             */
            public var onClick:Function;

            /** 启用或禁用编辑器视口导航。 */
            public function set viewportNavigationEnabled(value:Boolean):void
            {
                _viewportNavigationEnabled = value;
            }

            public function get viewportNavigationEnabled():Boolean
            {
                return _viewportNavigationEnabled;
            }
        }

        public function RenderEngine(stage:Stage)
        {
            _stage = stage;
            current = this;
            // Starling 初始化：传入 native stage，rootClass 用 Sprite。
            _starling = new Starling(Sprite, stage);
            _starling.antiAliasing = 1;

            // root 就绪后保存容器引用，并在此时启动渲染循环
            // （早于 ROOT_CREATED 调用 start 会导致 Stage3D backBuffer 配置异常，
            //  表现为只渲染出一个三角形区域）。
            _starling.addEventListener(ROOT_CREATED, onRootCreated);
        }

        private function onRootCreated(e:Object):void
        {
            _rootContainer = _starling.root as Sprite;
            _root = new DrawableContainer(_rootContainer);

            // 监听舞台尺寸变化，让 Starling 视口与 backBuffer 跟随更新。
            // 不监听则窗口拉伸后渲染区域不变，显示内容被裁剪或留白。
            _stage.addEventListener(Event.RESIZE, onStageResize);

            // 启动即补一次同步：Starling 在文档类构造时初始化，彼时 native stage 还是
            // 占位尺寸（实测 500×375），真实窗口尺寸要到窗口就绪后才生效，而这次变化
            // 发生在监听注册之前（不派发 RESIZE），不补同步则初始 viewPort/舞台尺寸
            // 与真实 stage 尺寸不一致——渲染区域错位，且相机按 stage 尺寸换算倍率时也会错。
            syncViewport();

            // 注册视口导航输入（编辑器专用）。相机视图由 RenderSystem 每帧按主相机元素应用。
            CONFIG::STUDIO
            {
                registerInputHandlers();
            }

            // root 就绪后再启动渲染循环，确保 backBuffer 配置正确。
            if (_started) return;
            _started = true;
            _starling.start();
            _stage.addEventListener(Event.ENTER_FRAME, onEnterFrame);

            // 就绪通知：发布构建的启动加载挂在这里（见 onReady 的说明）。
            if (onReady != null) onReady();
        }

        // ── 编辑器视口导航输入（条件编译：独立运行构建不注册）──
        CONFIG::STUDIO
        {
        /**
         * 注册 native stage 鼠标与键盘事件，实现 Scene 视口导航。
         * 滚轮：以鼠标位置为中心缩放相机。
         * 中键拖拽：平移相机。
         * 空格+左键拖拽：平移相机（与 Unity 风格一致）。
         * 注意：MouseEvent.button 在当前 AIR/SWF 配置下不可用，
         * 因此通过事件名区分按键，而不是读取 button 属性。
         */
        private function registerInputHandlers():void
        {
            _stage.addEventListener(MouseEvent.MOUSE_WHEEL, onMouseWheel);
            _stage.addEventListener(MouseEvent.MOUSE_DOWN, onMouseDown);
            _stage.addEventListener(MouseEvent.MIDDLE_MOUSE_DOWN, onMiddleMouseDown);
            _stage.addEventListener(MouseEvent.MOUSE_MOVE, onMouseMove);
            _stage.addEventListener(MouseEvent.MOUSE_UP, onMouseUp);
            _stage.addEventListener(MouseEvent.MIDDLE_MOUSE_UP, onMouseUp);
            _stage.addEventListener(KeyboardEvent.KEY_DOWN, onKeyDown);
            _stage.addEventListener(KeyboardEvent.KEY_UP, onKeyUp);
        }

        private function onMouseWheel(e:MouseEvent):void
        {
            if (!_viewportNavigationEnabled) return;
            var factor:Number = e.delta > 0 ? ZoomSpeed : 1.0 / ZoomSpeed;
            zoomEditorAt(e.stageX, e.stageY, factor);
        }

        private function onMouseDown(e:MouseEvent):void
        {
            if (!_viewportNavigationEnabled) return;
            // MOUSE_DOWN 默认对应左键。
            // 按住空格时左键按下进入平移模式；否则视为点击 → 触发拾取回调。
            if (_spacePressed)
            {
                startPanning(e.stageX, e.stageY);
                return;
            }
            if (onClick != null)
                onClick(e.stageX, e.stageY, e.shiftKey, e.ctrlKey);
        }

        private function onMiddleMouseDown(e:MouseEvent):void
        {
            if (!_viewportNavigationEnabled) return;
            // MIDDLE_MOUSE_DOWN 明确对应中键，无需读取 button。
            startPanning(e.stageX, e.stageY);
        }

        private function startPanning(stageX:Number, stageY:Number):void
        {
            _isPanning = true;
            _lastMouseX = stageX;
            _lastMouseY = stageY;
        }

        private function onMouseMove(e:MouseEvent):void
        {
            if (!_viewportNavigationEnabled) return;
            if (!_isPanning) return;
            var dx:Number = e.stageX - _lastMouseX;
            var dy:Number = e.stageY - _lastMouseY;
            _lastMouseX = e.stageX;
            _lastMouseY = e.stageY;
            // 屏幕增量 → 相机位置反向移动（拖右 → 场景右移）。
            var zoom:Number = _stage.stageHeight / (2 * _editorOrthoSize);
            _editorCamX -= dx / zoom;
            _editorCamY -= dy / zoom;
            applyEditorCamera();
        }

        private function onMouseUp(e:MouseEvent):void
        {
            if (!_viewportNavigationEnabled) return;
            _isPanning = false;
        }

        private function onKeyDown(e:KeyboardEvent):void
        {
            if (!_viewportNavigationEnabled) return;
            if (e.keyCode == Keyboard.SPACE)
                _spacePressed = true;
        }

        private function onKeyUp(e:KeyboardEvent):void
        {
            if (!_viewportNavigationEnabled) return;
            if (e.keyCode == Keyboard.SPACE)
                _spacePressed = false;
        }
        } // CONFIG::STUDIO（编辑器视口导航：独立运行构建不包含）

        /**
         * 舞台尺寸变化时同步 Starling 视口与舞台尺寸（运行时必需）。
         * 策略：动态调整设计尺寸——viewport 与 stageSize 都跟随窗口实时变化，
         * 内容不变形。相机视图由 RenderSystem 每帧按主相机元素应用——
         * 视口中心随窗口变化，天然保持"视口中心对应的世界点"与窗口中心对齐
         * （中心锚定），无需额外状态机。
         */
        private function onStageResize(e:Event):void
        {
            syncViewport();
        }

        /**
         * 将 Starling 视口与内部舞台尺寸同步为 native stage 尺寸。
         * 调用时机：root 创建后（启动首帧前）与每次 RESIZE。
         * viewPort 是 Starling 实际渲染的物理区域；stageWidth/Height 是 Starling 内部
         * 坐标系范围（决定内容可见区域）。两者都等于窗口尺寸时，坐标 (0,0)~(w,h)
         * 即整个可见区域，内容不变形。
         */
        private function syncViewport():void
        {
            var w:int = _stage.stageWidth;
            var h:int = _stage.stageHeight;
            if (w <= 0 || h <= 0) return;

            _starling.viewPort = new Rectangle(0, 0, w, h);
            _starling.stage.stageWidth = w;
            _starling.stage.stageHeight = h;
        }

        /** 外部请求启动渲染。若 root 尚未就绪则等待 onRootCreated 自动启动。 */
        public function start():void
        {
            if (_root == null) return; // 等 ROOT_CREATED
            if (_started) return;
            _started = true;
            _starling.start();
            _stage.addEventListener(Event.ENTER_FRAME, onEnterFrame);
        }

        /** 停止渲染。 */
        public function stop():void
        {
            if (!_started) return;
            _started = false;
            _starling.stop();
            _stage.removeEventListener(Event.ENTER_FRAME, onEnterFrame);
        }

        private function onEnterFrame(e:Event):void
        {
            // juggler 驱动动画/tween；传入帧时间秒数。
            _starling.juggler.advanceTime(1.0 / _stage.frameRate);
        }

        /** 顶层容器，所有可绘制对象挂在其下才会渲染。 */
        public function get root():DrawableContainer
        {
            return _root;
        }

        /** 是否已就绪（root 创建完成）。 */
        public function get isReady():Boolean { return _root != null; }

        /**
         * 应用相机视图变换到渲染根容器（每帧由 RenderSystem 按主相机元素调用）。
         * Unity 正交模型：世界可视高度固定为 2×orthographicSize，映射到窗口像素高；
         * 世界→屏幕倍率 zoom = 窗口像素高 / (2 × orthographicSize)，宽度按宽高比自然伸缩。
         * 世界 → 屏幕：screen = R(θ)·zoom·p + t，其中 t = vc - zoom·R(θ)·camPos，
         * vc 为视口中心（窗口尺寸一半）。orthographicSize<=0 时视为 zoom=1（默认视角）。
         */
        public function applyCameraView(orthographicSize:Number, rotation:Number, camX:Number, camY:Number):void
        {
            var zoom:Number = orthographicSize > 0 ? _stage.stageHeight / (2 * orthographicSize) : 1;
            _cameraZoom = zoom;
            if (_rootContainer == null) return;
            var vcX:Number = _stage.stageWidth * 0.5;
            var vcY:Number = _stage.stageHeight * 0.5;
            var cosR:Number = Math.cos(rotation);
            var sinR:Number = Math.sin(rotation);
            var m:Matrix = new Matrix();
            m.a = zoom * cosR;
            m.b = zoom * sinR;
            m.c = -zoom * sinR;
            m.d = zoom * cosR;
            m.tx = vcX - (m.a * camX + m.c * camY);
            m.ty = vcY - (m.b * camX + m.d * camY);
            _rootContainer.transformationMatrix = m;
        }

        /** 当前相机缩放（applyCameraView 更新）。编辑器覆盖层按此反算固定像素厚度。 */
        public function get cameraZoom():Number { return _cameraZoom; }

        /**
         * 将 native stage 坐标转换为世界坐标（渲染根局部空间）。
         * 根容器携带相机变换，globalToLocal 即为其反变换。
         */
        public function screenToWorld(stageX:Number, stageY:Number):Point
        {
            return _rootContainer != null
                ? _rootContainer.globalToLocal(new Point(stageX, stageY))
                : null;
        }

        // ── 编辑器视口查询 API（条件编译：独立运行构建不包含）──
        CONFIG::STUDIO
        {

            /**
             * 世界坐标命中测试：返回点击处最顶层的可拾取 Drawable；无则 null。
             * 基于 Starling DisplayObjectContainer.hitTest：从后往前遍历子对象，
             * 返回第一个命中的（渲染顺序即拾取顺序）；touchable=false 的编辑器
             * 覆盖层会被自动排除。
             */
            public function hitTestWorld(stageX:Number, stageY:Number):Drawable
            {
                if (_rootContainer == null) return null;
                var local:Point = screenToWorld(stageX, stageY);
                if (local == null) return null;
                var hit:DisplayObject = _rootContainer.hitTest(local);
                return hit != null ? new Drawable(hit) : null;
            }

            /**
             * 屏幕空间命中测试（stage 像素）：查屏幕容器（UI 画布层 + 编辑器屏幕覆盖层，
             * 如 UI 元素的选择高亮框与 Gizmo 手柄）。世界内容请用 hitTestWorld。
             * 屏幕容器自身无变换，故其局部坐标即 stage 像素。
             */
            public function hitTestScreen(stageX:Number, stageY:Number):Drawable
            {
                if (_screenContainer == null) return null;
                var c:Sprite = _screenContainer._impl as Sprite;
                if (c == null) return null;
                var hit:DisplayObject = c.hitTest(new Point(stageX, stageY));
                return hit != null ? new Drawable(hit) : null;
            }

            /** 创建空的可绘制容器（编辑器覆盖层等用）。 */
            public function createContainer():DrawableContainer
            {
                return new DrawableContainer(new Sprite());
            }
        }

        /**
         * 创建空的可绘制容器（通用工厂：编辑器覆盖层与运行时 UI 分组容器均用）。
         * 容器自身恒等变换，需由调用方定位/缩放。
         */
        public function createDrawableContainer():DrawableContainer
        {
            return new DrawableContainer(new Sprite());
        }

        // ── 视口查询（通用：UI 画布逻辑分辨率换算等运行时也使用）──

        /** 当前视口宽/高（native 像素，= Starling 舞台坐标）。 */
        public function get stageWidth():Number { return _stage.stageWidth; }
        public function get stageHeight():Number { return _stage.stageHeight; }

        // ── 工厂方法 ──

        /**
         * 屏幕空间容器：所有 UI 画布层的父级，整体位于世界根内容之上。
         * 懒创建并挂到 Starling 舞台根（世界根之后，故在世界上层）。
         */
        public function get screenContainer():DrawableContainer
        {
            if (_screenContainer == null && _starling != null)
            {
                var s:Sprite = new Sprite();
                _starling.stage.addChild(s);
                _screenContainer = new DrawableContainer(s);
            }
            return _screenContainer;
        }

        /** 调整屏幕空间层的叠放索引（越大越靠上层，仅在同一屏幕容器内排序）。 */
        public function setScreenLayerIndex(layer:DrawableContainer, index:int):void
        {
            if (_screenContainer == null || layer == null) return;
            _screenContainer.setChildIndex(layer, index);
        }

        // ── 世界空间画布层（CanvasSystem 登记 → RenderSystem 合并排序）──

        /**
         * 登记本帧的世界空间画布层，元素为 {layer:DrawableContainer, order:int, dfs:int}；
         * 相机空间画布额外带 {fill:true, scale:Number}（层矩阵由 RenderSystem 在应用
         * 相机视图后反算为「铺满视口」，见 RenderSystem.fitLayerToViewport）。
         * 每帧由 CanvasSystem 调用（无世界空间画布时传 null 清空）。
         * 传入顺序不影响结果：RenderSystem 会连同世界元素一起按 (order, dfs) 排序。
         */
        public function setWorldCanvasLayers(list:Array):void
        {
            _worldCanvasLayers = (list != null) ? list : [];
        }

        /** 本帧登记的世界空间画布层（只读，RenderSystem 排序用）。 */
        public function get worldCanvasLayers():Array
        {
            return _worldCanvasLayers;
        }

        /** 创建纯色矩形。color 为 0xRRGGBB。 */
        public function createQuad(width:Number, height:Number, color:uint = 0xFFFFFF):Drawable
        {
            var q:Quad = new Quad(width, height, color);
            return new Drawable(q);
        }

        /** 从 BitmapData 创建纹理。调用方负责 dispose BitmapData。 */
        public function createTextureFromBitmapData(bmd:BitmapData):Texture2D
        {
            return Texture2D.fromBitmapData(bmd);
        }

        /** 创建带纹理的 Image 可绘制对象。
         *  frame 非空（且宽高 > 0）时取纹理子区域（图集/精灵表单帧），
         *  像素 alpha 精确拾取同步映射到该子区域。 */
        public function createImage(texture:Texture2D, frame:Rectangle = null):Drawable
        {
            var src:Texture2D = texture;
            if (frame != null && frame.width > 0 && frame.height > 0)
                src = texture.subTexture(frame);
            var img:Image = new Image(src._impl);
            var d:Drawable = new Drawable(img);
            d._sourceBitmapData = src._sourceBitmapData; // 像素 alpha 精确拾取
            d._sourceRect = src._sourceRect;
            return d;
        }

        /**
         * 创建文本可绘制对象（Starling TextField，自动换行；Text 组件用）。
         * Starling 2.8：外观经 TextFormat 传入（构造 4 参 (宽,高,文本,格式)）。
         */
        public function createText(width:Number, height:Number, text:String,
                                   fontSize:Number, color:uint, bold:Boolean = false,
                                   fontName:String = "Arial"):Drawable
        {
            var format:TextFormat = new TextFormat(fontName, fontSize, color);
            format.bold = bold;
            var tf:TextField = new TextField(width, height, text, format);
            tf.wordWrap = true;
            var d:Drawable = new Drawable(tf);
            d.touchable = true;
            return d;
        }

        /** 释放 Starling 资源。 */
        public function dispose():void
        {
            stop();
            _starling.dispose();
        }
    }
}

package dream.engine.scene
{
    CONFIG::STUDIO
    {
        import dream.engine.render.Drawable;
        import dream.engine.render.DrawableContainer;
        import dream.engine.render.RenderEngine;

        /**
         * 文档返回按钮（编辑器 overlay）：固定在视口左上角，点击回调通知 Studio 切回上一个文档。
         *
         * 为什么放在引擎窗口内：返回入口必须始终可见，而 Studio 端的面板可能被其它窗口遮挡，
         * 且按钮若做成 Studio 控件会与嵌入的 ADL 窗口抢层级。画在引擎自己的视口里，
         * 随窗口一起被遮挡/置顶，行为与场景内容一致。
         *
         * 为什么挂屏幕容器：RenderEngine.screenContainer 是 Starling stage 的直接子级、
         * 恒等变换，其坐标即窗口像素，不受相机缩放/平移影响，因而天然固定在左上角；
         * 每帧 removeChild + addChild 保顶，避免被 UI 画布层盖住。
         *
         * 仅当当前文档是预制体时显示（场景文档没有"上一个文档"可回）——
         * 由 DreamEngine 在 scene.load 之后按扩展名切换（见 setVisible）。
         */
        public final class DocumentOverlay
        {
            private static const Margin:Number = 12;
            private static const BtnW:Number = 84;
            private static const BtnH:Number = 28;
            private static const BgColor:uint = 0x2A2A2E;
            private static const TextColor:uint = 0xF0F0F0;
            private static const Label:String = "Back";

            private var _render:RenderEngine;
            private var _onBack:Function;

            private var _overlay:DrawableContainer;
            /** 命中层：整块按钮矩形（标签 touchable=false，命中只认它）。 */
            private var _button:Drawable;

            private var _attached:Boolean = false;
            private var _visible:Boolean = false;

            public function DocumentOverlay(render:RenderEngine, onBack:Function)
            {
                _render = render;
                _onBack = onBack;

                _overlay = render.createContainer();

                _button = render.createQuad(BtnW, BtnH, BgColor);
                _button.touchable = true;
                _button.x = Margin;
                _button.y = Margin;
                _overlay.addChild(_button);

                var label:Drawable = render.createText(BtnW, BtnH, Label, 13, TextColor, true);
                label.textAlign = "center";
                label.verticalAlign = "center";
                label.touchable = false;
                label.x = Margin;
                label.y = Margin;
                _overlay.addChild(label);

                _overlay.visible = false;
            }

            /** 当前文档是否有可返回的目标（预制体文档为 true）。 */
            public function get visible():Boolean { return _visible; }

            /**
             * 切换返回按钮的显示。由 DreamEngine 在文档加载完成后调用：
             * 预制体文档显示（可返回场景），场景文档隐藏（无上一个文档）。
             */
            public function setVisible(v:Boolean):void
            {
                _visible = v;
                if (!v) _overlay.visible = false;
            }

            /** 每帧调用：可见时挂到屏幕容器并保持置顶。 */
            public function update():void
            {
                if (!_visible) return;
                var parent:DrawableContainer = _render.screenContainer;
                if (parent == null) return;

                ensureAttached(parent);
                // 保持覆盖层在屏幕容器顶端（UI 画布层之上）。
                parent.removeChild(_overlay);
                parent.addChild(_overlay);
                _overlay.visible = true;
            }

            /**
             * 视口点击优先消费：命中按钮则回调 onBack 并返回 true（调用方据此短路后续拾取）。
             * 与 Gizmo 手柄的命中约定一致：先到先得，未命中返回 false。
             */
            public function onMouseDown(stageX:Number, stageY:Number):Boolean
            {
                if (!_visible || !_attached) return false;
                var hit:Drawable = _render.hitTestScreen(stageX, stageY);
                if (hit == null || !_button.sameAs(hit)) return false;
                if (_onBack != null) _onBack();
                return true;
            }

            private function ensureAttached(parent:DrawableContainer):void
            {
                if (_attached) return;
                parent.addChild(_overlay);
                _attached = true;
            }
        }
    }
}

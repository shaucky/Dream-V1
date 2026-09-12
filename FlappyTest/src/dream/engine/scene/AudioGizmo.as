package dream.engine.scene
{
    CONFIG::STUDIO
    {
        import dream.engine.audio.AudioListener;
        import dream.engine.audio.AudioSource;
        import dream.engine.ecs.Element;
        import dream.engine.ecs.World;
        import dream.engine.render.Drawable;
        import dream.engine.render.DrawableContainer;
        import dream.engine.render.RenderEngine;
        import dream.engine.transform.Transform;

        /**
         * 音频范围预览：在视口显示选中元素的音频配置。
         *
         *   - AudioSource：橙色。minDistance 圆（实心粗环，全音量区）+ maxDistance 圆（细淡环，衰减外缘），
         *     圆心跟随元素世界位置；spatialBlend<=0 时仅显示圆心标记（无衰减范围）。
         *   - AudioListener：蓝色标记（圆环 + 中心点），随元素位置。
         *
         * 仅编辑器模式显示（运行模式由 EngineSetRunningHandler 联动隐藏）；
         * 范围圆不参与拾取（touchable=false），不干扰场景点击。
         */
        public final class AudioGizmo
        {
            private static const ColorSource:uint = 0xE58C4A;    // 橙：AudioSource 范围
            private static const ColorListener:uint = 0x5A9BD5;  // 蓝：AudioListener 标记
            private static const SourceSegments:int = 48;
            private static const ListenerSegments:int = 32;

            private var _world:World;
            private var _render:RenderEngine;
            private var _selection:SceneSelection;

            private var _overlay:DrawableContainer;
            private var _overlayAttached:Boolean = false;
            private var _visible:Boolean = true;

            private var _sourceLayer:DrawableContainer;
            private var _listenerLayer:DrawableContainer;
            private var _minRing:Array = [];   // Drawable 段
            private var _maxRing:Array = [];
            private var _sourceCenter:Drawable;
            private var _listenerRing:Array = [];
            private var _listenerCenter:Drawable;

            public function AudioGizmo(world:World, render:RenderEngine, selection:SceneSelection)
            {
                _world = world;
                _render = render;
                _selection = selection;

                _overlay = render.createContainer();
                _sourceLayer = render.createContainer();
                _listenerLayer = render.createContainer();
                _overlay.addChild(_sourceLayer);
                _overlay.addChild(_listenerLayer);
                _overlay.visible = false;

                _minRing = createRing(_sourceLayer, SourceSegments, ColorSource, 0.85);
                _maxRing = createRing(_sourceLayer, SourceSegments, ColorSource, 0.35);
                _sourceCenter = render.createQuad(1, 1, ColorSource);
                _sourceCenter.touchable = false;
                _sourceLayer.addChild(_sourceCenter);

                _listenerRing = createRing(_listenerLayer, ListenerSegments, ColorListener, 0.9);
                _listenerCenter = render.createQuad(1, 1, ColorListener);
                _listenerCenter.touchable = false;
                _listenerLayer.addChild(_listenerCenter);
            }

            /** 运行模式隐藏。由 EngineSetRunningHandler 联动调用。 */
            public function setVisible(v:Boolean):void
            {
                _visible = v;
                if (!v) _overlay.visible = false;
            }

            /** 每帧调用：对齐选中元素的音频配置并保持覆盖层置顶。 */
            public function update():void
            {
                if (!_visible) return;
                ensureOverlay();
                var root:DrawableContainer = _render.root;
                if (root == null || !_overlayAttached) return;

                // 保持覆盖层在渲染顺序顶端。
                root.removeChild(_overlay);
                root.addChild(_overlay);

                var e:Element = _selection.selectedElement;
                if (e == null)
                {
                    _overlay.visible = false;
                    return;
                }

                var zoom:Number = _render.cameraZoom;
                var lineTh:Number = 3 / zoom;
                var hasSource:Boolean = false;
                var hasListener:Boolean = false;

                // AudioSource：min/max 衰减范围圆。
                var src:AudioSource = e.getComponent(AudioSource) as AudioSource;
                if (src != null)
                {
                    var t:Transform = e.getComponent(Transform) as Transform;
                    if (t != null)
                    {
                        var cx:Number = t.worldMatrix.tx;
                        var cy:Number = t.worldMatrix.ty;
                        var active:Boolean = src.spatialBlend > 0;
                        var minR:Number = src.minDistance;
                        var maxR:Number = src.maxDistance;
                        if (maxR < minR) maxR = minR;
                        setRing(_minRing, cx, cy, minR, lineTh, active);
                        setRing(_maxRing, cx, cy, maxR, lineTh * 0.7, active);
                        var cs:Number = Math.max(8 / zoom, lineTh * 2);
                        _sourceCenter.x = cx - cs * 0.5;
                        _sourceCenter.y = cy - cs * 0.5;
                        _sourceCenter.width = cs;
                        _sourceCenter.height = cs;
                        _sourceCenter.visible = true;
                        hasSource = true;
                    }
                }

                // AudioListener：蓝色标记。
                var lis:AudioListener = e.getComponent(AudioListener) as AudioListener;
                if (lis != null)
                {
                    var lt:Transform = e.getComponent(Transform) as Transform;
                    if (lt != null)
                    {
                        var lcx:Number = lt.worldMatrix.tx;
                        var lcy:Number = lt.worldMatrix.ty;
                        var lr:Number = Math.max(26 / zoom, 4);
                        setRing(_listenerRing, lcx, lcy, lr, lineTh * 0.8, true);
                        var ls:Number = Math.max(8 / zoom, lineTh * 2);
                        _listenerCenter.x = lcx - ls * 0.5;
                        _listenerCenter.y = lcy - ls * 0.5;
                        _listenerCenter.width = ls;
                        _listenerCenter.height = ls;
                        _listenerCenter.visible = true;
                        hasListener = true;
                    }
                }

                _sourceLayer.visible = hasSource;
                _listenerLayer.visible = hasListener;
                _overlay.visible = hasSource || hasListener;
            }

            // ── 内部 ──

            /** 创建一段圆环（细长 quad 沿圆周切线排列），返回段数组。 */
            private function createRing(layer:DrawableContainer, segments:int, color:uint, alpha:Number):Array
            {
                var ring:Array = [];
                for (var i:int = 0; i < segments; i++)
                {
                    var seg:Drawable = _render.createQuad(1, 1, color);
                    seg.touchable = false;
                    seg.alpha = alpha;
                    layer.addChild(seg);
                    ring.push(seg);
                }
                return ring;
            }

            /** 把圆环段排布到 (cx,cy) 半径 radius 的圆周上；visible=false 时隐藏整环。 */
            private function setRing(ring:Array, cx:Number, cy:Number, radius:Number, thickness:Number, visible:Boolean):void
            {
                var n:int = ring.length;
                for (var i:int = 0; i < n; i++)
                {
                    var seg:Drawable = ring[i] as Drawable;
                    seg.visible = visible;
                    if (!visible) continue;

                    var a:Number = i * (Math.PI * 2 / n);
                    var len:Number = Math.max(Math.PI * 2 * radius / n, thickness * 1.5); // 弧长，小半径时保证段相连
                    var theta:Number = a + Math.PI / 2; // 切线方向
                    var cosT:Number = Math.cos(theta);
                    var sinT:Number = Math.sin(theta);
                    var px:Number = cx + Math.cos(a) * radius;
                    var py:Number = cy + Math.sin(a) * radius;

                    // 段为 1×1 Quad，用标量 setter：scaleX/scaleY = 段长×线宽，rotation = 切线方向。
                    // 不可用 width/height：Starling 的 width/height setter 按"旋转后包围盒"反算
                    // scale（1/(|cosθ|+|sinθ|)），旋转段得到随角度变化的错误尺寸。
                    // 不可用 transformationMatrix：其 setter 会把矩阵分解成 skew/rotation/scale 再
                    // 重组，非均匀缩放+旋转在 θ≈±π/2 时产生巨大畸变（呈现椭圆/随相机变化）。
                    // rotation 绕左上角旋转，故左上角 = 段中心 - R(θ)·(len/2, th/2)。
                    seg.scaleX = len;
                    seg.scaleY = thickness;
                    seg.rotation = theta;
                    seg.x = px - (cosT * len * 0.5 - sinT * thickness * 0.5);
                    seg.y = py - (sinT * len * 0.5 + cosT * thickness * 0.5);
                }
            }

            private function ensureOverlay():void
            {
                if (_overlayAttached) return;
                var root:DrawableContainer = _render.root;
                if (root == null) return;
                root.addChild(_overlay);
                _overlayAttached = true;
            }
        }
    }
}

package dream.engine.render
{
    import dream.engine.ecs.DreamComponent;
    import dream.engine.ecs.Element;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    import dream.engine.render.Drawable;
    import dream.engine.render.DrawableContainer;
    import dream.engine.render.RenderEngine;

    import flash.geom.Rectangle;

    /**
     * 显示组件：持有引擎 Drawable，使元素可被渲染。
     * 与 Transform 配合使用，RenderSystem 每帧同步 worldMatrix 到 Drawable。
     *
     * 生命周期：
     *   onLoad：若 displayObject 为 null（无参构造）则创建默认 1×1 白色方块
     *   onEnable：若已挂载则显示
     *   onDisable：若已挂载则隐藏（visible=false，不从树移除以保留位置）
     *   onDestroy：从父级移除，释放可绘制对象
     *
     * 挂载时机：RenderSystem 首次处理此组件时调用 attachTo(root)。
     *
     * 可通过 Inspector "Add Component" 无参添加：构造时不传 Drawable，
     * onLoad 时通过 RenderEngine.current 创建默认 1×1 白色方块。
     *
     * 注意：几何尺寸不序列化，显示尺寸完全由 Transform 的 Scale 控制
     *（本地默认 1×1，scaleX/scaleY 即最终宽高）。
     */
    public class DisplayComponent extends DreamComponent
    {
        /** 持有的可绘制对象。 */
        public var displayObject:Drawable;

        /**
         * 视觉锚点偏移（pivot）：渲染时在本地空间先平移 (-pivot) 再做世界变换，
         * 即 pivot 点位于 Transform 位置（x/y）。默认 NaN = 自动居中（取显示对象
         * 本地包围盒中心），使矩形以原点为视觉中心；也可在 Inspector 中显式编辑。
         */
        public var pivotX:Number = Number.NaN;
        public var pivotY:Number = Number.NaN;

        /**
         * 渲染排序序号：数值越大越靠上层。同为 0（默认）时按层级顺序叠放，
         * 即「Hierarchy 中靠后的元素画在上层」。RenderSystem 每帧按
         * (sortingOrder, 层级序号) 重排根容器子级。
         */
        public var sortingOrder:int = 0;

        protected var _attached:Boolean = false;
        protected var _root:DrawableContainer;

        /** 解析后的 pivotX：显式值优先；NaN（自动居中）取显示对象本地包围盒中心。 */
        public function get effectivePivotX():Number
        {
            if (!isNaN(pivotX)) return pivotX;
            var b:Rectangle = displayObject != null ? displayObject.localBounds : null;
            return b != null ? b.width * 0.5 : 0;
        }

        /** 解析后的 pivotY：显式值优先；NaN（自动居中）取显示对象本地包围盒中心。 */
        public function get effectivePivotY():Number
        {
            if (!isNaN(pivotY)) return pivotY;
            var b:Rectangle = displayObject != null ? displayObject.localBounds : null;
            return b != null ? b.height * 0.5 : 0;
        }

        /**
         * @param display 可绘制对象；缺省时 onLoad 创建默认 1×1 白色方块，
         *                使组件可由 Inspector "Add Component" 无参创建。
         */
        public function DisplayComponent(display:Drawable = null)
        {
            displayObject = display;
        }

        override protected function onLoad():void
        {
            if (displayObject == null && RenderEngine.current != null)
            {
                displayObject = RenderEngine.current.createQuad(1, 1, 0xFFFFFF);
                applyAppearance();
            }
        }

        /**
         * 挂载到 root 容器。由 RenderSystem 在首次处理时调用（此时 root 已就绪）。
         * 重复调用安全。
         *
         * 注：声明 public 纯为跨包访问需要，属于引擎内部 API，最终用户不应直接调用。
         */
        public function attachTo(root:DrawableContainer):void
        {
            if (_attached) return;
            _root = root;
            root.addChild(displayObject);
            // 初始可见性跟随运行状态：元素/组件禁用（activeInHierarchy 无效）时
            // onEnable 未触发，必须在此按 _running 初始化，否则默认 Quad 会直接显示。
            if (displayObject) displayObject.visible = isRunning;
            _attached = true;
        }

        override protected function onEnable():void
        {
            if (displayObject) displayObject.visible = true;
        }

        override protected function onDisable():void
        {
            if (displayObject) displayObject.visible = false;
        }

        override protected function onDestroy():void
        {
            if (displayObject)
            {
                displayObject.removeFromParent();
                displayObject = null;
            }
            _attached = false;
            _root = null;
        }

        // ── Inspector 反射 ──

        /** 组件级颜色（tint）。displayObject 为空/切换时保留，创建新显示对象时应用。 */
        private var _color:uint = 0xFFFFFF;

        /** 组件级混合模式。displayObject 为空/切换时保留，创建新显示对象时应用。 */
        private var _blendMode:String = "auto";

        /** 颜色（组件级存储，直通当前显示对象；切换显示对象后仍保留）。 */
        public function get color():uint { return _color; }
        public function set color(v:uint):void
        {
            _color = v;
            if (displayObject) displayObject.color = v;
        }

        /** 混合模式（受控字符串常量，与 Starling BlendMode 对应）。 */
        public function get blendMode():String { return _blendMode; }
        public function set blendMode(v:String):void
        {
            _blendMode = v;
            if (displayObject) displayObject.blendMode = v;
        }

        /** 将组件级外观（颜色/混合模式）应用到当前显示对象。显示对象创建/替换后调用。 */
        protected function applyAppearance():void
        {
            if (displayObject == null) return;
            displayObject.color = _color;
            displayObject.blendMode = _blendMode;
        }

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var blendFi:FieldInfo = new FieldInfo("blendMode", "Blend Mode", "string", blendMode);
            blendFi.options = ["auto", "normal", "add", "multiply", "screen", "erase", "mask", "below"];
            // pivot 为自动居中（NaN）时不序列化具体值：一旦固化，恢复时显示对象尺寸
            // 可能不同（Quad 1×1 ↔ Image 纹理尺寸），pivot 偏移 = scale×旧值，产生巨大偏移。
            // null 值：Inspector 显示 (0,0)，编辑任意值即转为显式 pivot。
            var autoPivot:Boolean = isNaN(pivotX) || isNaN(pivotY);
            // 注意：几何尺寸不序列化，尺寸完全由 Transform 的 Scale 控制（默认本地 1×1）。
            return [
                new FieldInfo("sortingOrder", "Sorting Order", "number", sortingOrder),
                new FieldInfo("pivot", "Pivot", "vector2",
                    autoPivot ? null : {x: pivotX, y: pivotY}),
                new FieldInfo("color", "Color", "color", color),
                blendFi
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "sortingOrder":
                    sortingOrder = (value != null) ? int(value) : 0;
                    break;
                case "pivot":
                    if (value == null)
                    {
                        // 恢复自动居中：pivot 跟随显示对象本地尺寸的一半。
                        pivotX = Number.NaN;
                        pivotY = Number.NaN;
                    }
                    else
                    {
                        pivotX = Number(value.x);
                        pivotY = Number(value.y);
                    }
                    break;
                case "color":
                    if (value != null) color = uint(value);
                    break;
                case "blendMode":
                    if (value != null) blendMode = String(value);
                    break;
            }
        }
    }
}

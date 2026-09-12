package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 滚动视图组件（UI）：以宿主元素矩形为视口，滚动其内容。
     *
     * 内容约定：
     *   - contentName 指定宿主的一个直接子元素名 → 仅该子元素的子树滚动；
     *   - contentName 为空（默认）或未匹配到 → 回退为整个子树（宿主全部子级）。
     *
     * 与 Mask 搭配实现视口裁剪（同一元素上挂 Mask 即可，见 Studio 预制 "Scroll View"）。
     *
     * 由 CanvasSystem 驱动：内容绘制对象挂入宿主的分组容器，滚动即整体平移该容器；
     * 内容/视口尺寸与夹取范围每帧重算。
     *
     * 输入：滚轮与拖拽由 CanvasSystem 分发（从命中元素沿祖先链找最近的 ScrollView）；
     * 滚动条（Scrollbar）读取本组件的运行时字段呈现滑块。
     */
    public final class ScrollView extends DreamComponent
    {
        /** 允许水平滚动。 */
        public var horizontal:Boolean = false;

        /** 允许垂直滚动。 */
        public var vertical:Boolean = true;

        /** 夹取模式：clamped（限制在内容范围内）| unrestricted（自由滚动）。 */
        public var movementType:String = "clamped";

        /** 滚轮灵敏度（像素/单位滚轮增量）。 */
        public var scrollSensitivity:Number = 40;

        /** 内容子元素名；空或未匹配则回退为整个子树。 */
        public var contentName:String = "";

        // ── 运行时状态（每帧由 CanvasSystem 更新；不序列化、不进 Inspector）──

        /** 当前滚动偏移（画布逻辑单位，>=0 表示内容已上移/左移的量）。 */
        public var scrollX:Number = 0;
        public var scrollY:Number = 0;

        public var contentWidth:Number = 0;
        public var contentHeight:Number = 0;
        public var viewportWidth:Number = 0;
        public var viewportHeight:Number = 0;

        /** 可滚动上限（内容尺寸 − 视口尺寸，下限 0）。 */
        public var maxScrollX:Number = 0;
        public var maxScrollY:Number = 0;

        public function ScrollView()
        {
        }

        /** 相对滚动（滚轮/拖拽）。clamped 模式下夹取到 [0, max]。 */
        public function scrollBy(dx:Number, dy:Number):void
        {
            scrollX += dx;
            scrollY += dy;
            clampScroll();
        }

        /** 按比例设置滚动位置（滚动条用，t ∈ [0,1]）。 */
        public function setScrollRatio(tx:Number, ty:Number):void
        {
            scrollX = tx * maxScrollX;
            scrollY = ty * maxScrollY;
            clampScroll();
        }

        /** 按夹取模式约束当前滚动位置（CanvasSystem 每帧更新上限后调用）。 */
        public function clampScroll():void
        {
            if (movementType == "unrestricted") return;
            if (!(scrollX >= 0)) scrollX = 0; else if (scrollX > maxScrollX) scrollX = maxScrollX;
            if (!(scrollY >= 0)) scrollY = 0; else if (scrollY > maxScrollY) scrollY = maxScrollY;
        }

        /** 垂直方向是否存在可滚动内容（滚动条 autoHide 用）。 */
        public function get scrollableY():Boolean { return maxScrollY > 0; }

        /** 水平方向是否存在可滚动内容。 */
        public function get scrollableX():Boolean { return maxScrollX > 0; }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var moveFi:FieldInfo = new FieldInfo("movementType", "Movement Type", "string", movementType);
            moveFi.options = ["clamped", "unrestricted"];
            return [
                new FieldInfo("horizontal", "Horizontal", "boolean", horizontal),
                new FieldInfo("vertical", "Vertical", "boolean", vertical),
                moveFi,
                new FieldInfo("scrollSensitivity", "Sensitivity", "number", scrollSensitivity),
                new FieldInfo("contentName", "Content Name", "string", contentName)
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "horizontal":
                    horizontal = Boolean(value);
                    break;
                case "vertical":
                    vertical = Boolean(value);
                    break;
                case "movementType":
                    if (value != null) movementType = String(value);
                    break;
                case "scrollSensitivity":
                    scrollSensitivity = Number(value);
                    break;
                case "contentName":
                    contentName = (value == null) ? "" : String(value);
                    break;
            }
        }
    }
}

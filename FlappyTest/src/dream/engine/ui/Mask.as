package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 遮罩组件（UI）：把宿主元素的子树裁剪到该元素的矩形范围内（对标 Unity RectMask2D）。
     *
     * 由 CanvasSystem 驱动：为宿主元素建立分组容器，子树绘制对象挂入该容器，
     * 并在容器上设置同尺寸的矩形 stencil 遮罩。遮罩矩形以画布坐标定位，
     * 因此配合 ScrollView 滚动时遮罩保持固定（只裁剪视口，不随内容移动）。
     *
     * 说明：
     *   - 宿主元素自身的可绘制组件不受裁剪（与 RectMask2D 一致）；
     *   - 遮罩图形默认不可见（alpha=0，仅写入模板缓冲）；
     *   - 嵌套遮罩为多个分组的自然嵌套，Starling 会各自压/弹模板。
     */
    public final class Mask extends DreamComponent
    {
        /** 四边等比内缩（正数向内收窄遮罩区域）。 */
        public var padding:Number = 0;

        /** 反转：true 时仅渲染遮罩区域之外的部分（Starling maskInverted）。 */
        public var invert:Boolean = false;

        /** 是否显示遮罩图形（默认 false；true 时呈现为白色矩形，便于调试）。 */
        public var showMaskGraphic:Boolean = false;

        public function Mask()
        {
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            return [
                new FieldInfo("padding", "Padding", "number", padding),
                new FieldInfo("invert", "Invert", "boolean", invert),
                new FieldInfo("showMaskGraphic", "Show Mask Graphic", "boolean", showMaskGraphic)
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "padding":
                    padding = Number(value);
                    break;
                case "invert":
                    invert = Boolean(value);
                    break;
                case "showMaskGraphic":
                    showMaskGraphic = Boolean(value);
                    break;
            }
        }
    }
}

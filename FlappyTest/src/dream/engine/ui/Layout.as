package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 布局组件（UI）：按方向顺序排列宿主元素的直系子级（各子级需带 RectTransform）。
     *
     * 垂直（vertical）：子级自上而下排列，宽度拉伸为容器内宽，高度取子级自身
     *   配置尺寸（RectTransform 的 offsetMax−offsetMin），间隔 spacing。
     * 水平（horizontal）：子级自左向右排列，高度拉伸为容器内高，宽度取子级配置尺寸。
     *
     * 子级尺寸由其自身 RectTransform 的 offset 决定（布局只覆盖位置，不改序列化字段）。
     * 布局每帧覆盖子级位置；子级仍可各自带 Image/Text/子 Layout。
     * 容器自身照常由父级布局/锚点决定矩形。
     */
    public final class Layout extends DreamComponent
    {
        /** 排列方向：vertical | horizontal。 */
        public var orientation:String = "vertical";

        /** 子级间距。 */
        public var spacing:Number = 8;

        /** 容器四边内边距（等宽）。 */
        public var padding:Number = 0;

        public function Layout()
        {
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var orientFi:FieldInfo = new FieldInfo("orientation", "Orientation", "string", orientation);
            orientFi.options = ["vertical", "horizontal"];
            return [
                orientFi,
                new FieldInfo("spacing", "Spacing", "number", spacing),
                new FieldInfo("padding", "Padding", "number", padding),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            switch (fieldName)
            {
                case "orientation":
                    if (value != null) orientation = String(value);
                    break;
                case "spacing":
                    spacing = Number(value);
                    break;
                case "padding":
                    padding = Math.max(0, Number(value));
                    break;
            }
        }
    }
}

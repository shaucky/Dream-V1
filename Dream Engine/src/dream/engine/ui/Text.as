package dream.engine.ui
{
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    import dream.engine.render.RenderEngine;

    /**
     * 文本组件（UI）：在画布屏幕空间渲染文本（Starling TextField，设备/嵌入式字体）。
     *
     * 尺寸由 CanvasSystem 按 RectTransform/Layout 逐帧写入（width/height 决定换行区域）。
     * 彩色 emoji 支持为独立跟踪项（自建 emoji 图集混排），基础版依赖系统字体渲染。
     */
    public final class Text extends UIDrawable
    {
        public var text:String = "";
        public var fontSize:Number = 24;
        public var color:uint = 0xFFFFFF;
        /** 水平对齐：left | center | right。 */
        public var horizontalAlign:String = "left";
        /** 垂直对齐：top | center | bottom。 */
        public var verticalAlign:String = "center";
        public var bold:Boolean = false;
        /** 字体名（设备字体或嵌入式字体族名）。 */
        public var fontName:String = "Arial";

        public function Text()
        {
        }

        override protected function onLoad():void
        {
            var re:RenderEngine = RenderEngine.current;
            if (re == null) return;
            _drawable = re.createText(1, 1, text, fontSize, color, bold, fontName);
            _drawable.touchable = true;
            applyProps();
        }

        /** 把当前文本属性应用到可绘制对象（属性变更/重建后调用）。 */
        private function applyProps():void
        {
            if (_drawable == null) return;
            _drawable.text = text;
            _drawable.fontSize = fontSize;
            _drawable.textColor = color;
            _drawable.textAlign = horizontalAlign;
            _drawable.verticalAlign = verticalAlign;
            _drawable.bold = bold;
            _drawable.fontName = fontName;
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var hFi:FieldInfo = new FieldInfo("horizontalAlign", "Horizontal Align", "string", horizontalAlign);
            hFi.options = ["left", "center", "right"];
            var vFi:FieldInfo = new FieldInfo("verticalAlign", "Vertical Align", "string", verticalAlign);
            vFi.options = ["top", "center", "bottom"];
            return [
                sortingOrderFieldInfo(),
                new FieldInfo("text", "Text", "string", text),
                new FieldInfo("fontSize", "Font Size", "number", fontSize),
                new FieldInfo("color", "Color", "color", color),
                hFi,
                vFi,
                new FieldInfo("bold", "Bold", "boolean", bold),
                new FieldInfo("fontName", "Font", "string", fontName),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (applySortingOrderField(fieldName, value)) return;
            switch (fieldName)
            {
                case "text":
                    text = (value == null) ? "" : String(value);
                    break;
                case "fontSize":
                    fontSize = Math.max(1, Number(value));
                    break;
                case "color":
                    if (value != null) color = uint(value);
                    break;
                case "horizontalAlign":
                    if (value != null) horizontalAlign = String(value);
                    break;
                case "verticalAlign":
                    if (value != null) verticalAlign = String(value);
                    break;
                case "bold":
                    bold = Boolean(value);
                    break;
                case "fontName":
                    if (value != null) fontName = String(value);
                    break;
                default:
                    return;
            }
            applyProps();
        }
    }
}

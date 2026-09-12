package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 矩形变换：UI 布局核心组件（对标 Unity RectTransform，屏幕坐标 y-down）。
     *
     * 锚点模型：
     *   anchorMin/anchorMax — 锚点矩形（0..1，父矩形比例；(0,0)=左上，(1,1)=右下）
     *   offsetMin/offsetMax — 锚点边到自身边界的偏移（父矩形单位/像素）
     *
     * 解析（resolveRect，相对父矩形左上角）：
     *   x = anchorMin.x·parentW + offsetMin.x
     *   y = anchorMin.y·parentH + offsetMin.y
     *   w = (anchorMax.x·parentW + offsetMax.x) − x
     *   h = (anchorMax.y·parentH + offsetMax.y) − y
     *
     * 矩形完全由锚点+偏移决定（视觉位置不受 pivot 影响，本组件不含 pivot——
     * 旋转/缩放等需要 pivot 的变换后续实现时再引入）。
     *
     * 默认：点锚点 (0.5,0.5) + 100×100（Unity 式：新建即有一个真实尺寸，居中于父矩形）。
     * 常用预设：
     *   stretch 全屏：  min=(0,0) max=(1,1) offset=0
     *   左上角：       min=(0,0) max=(0,0)，offset 定位置与尺寸
     *
     * 尺寸与位置由 CanvasSystem 每帧解析并写入可绘制组件。
     */
    public final class RectTransform extends DreamComponent
    {
        public var anchorMinX:Number = 0.5;
        public var anchorMinY:Number = 0.5;
        public var anchorMaxX:Number = 0.5;
        public var anchorMaxY:Number = 0.5;
        public var offsetMinX:Number = -50;
        public var offsetMinY:Number = -50;
        public var offsetMaxX:Number = 50;
        public var offsetMaxY:Number = 50;

        public function RectTransform()
        {
        }

        /** 相对父矩形解析自身矩形（{x,y,w,h}，原点=父矩形左上角）。 */
        public function resolveRect(parentW:Number, parentH:Number):Object
        {
            var x:Number = anchorMinX * parentW + offsetMinX;
            var y:Number = anchorMinY * parentH + offsetMinY;
            var w:Number = (anchorMaxX * parentW + offsetMaxX) - x;
            var h:Number = (anchorMaxY * parentH + offsetMaxY) - y;
            return { x: x, y: y, w: w, h: h };
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            return [
                new FieldInfo("anchorMin", "Anchor Min", "vector2", { x: anchorMinX, y: anchorMinY }),
                new FieldInfo("anchorMax", "Anchor Max", "vector2", { x: anchorMaxX, y: anchorMaxY }),
                new FieldInfo("offsetMin", "Offset Min", "vector2", { x: offsetMinX, y: offsetMinY }),
                new FieldInfo("offsetMax", "Offset Max", "vector2", { x: offsetMaxX, y: offsetMaxY }),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (value == null) return;
            switch (fieldName)
            {
                case "anchorMin":
                    anchorMinX = clamp01(Number(value.x));
                    anchorMinY = clamp01(Number(value.y));
                    break;
                case "anchorMax":
                    anchorMaxX = clamp01(Number(value.x));
                    anchorMaxY = clamp01(Number(value.y));
                    break;
                case "offsetMin":
                    offsetMinX = Number(value.x);
                    offsetMinY = Number(value.y);
                    break;
                case "offsetMax":
                    offsetMaxX = Number(value.x);
                    offsetMaxY = Number(value.y);
                    break;
            }
        }

        private static function clamp01(v:Number):Number
        {
            return v < 0 ? 0 : (v > 1 ? 1 : v);
        }
    }
}

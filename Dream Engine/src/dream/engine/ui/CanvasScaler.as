package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 画布缩放器：按当前视口物理尺寸计算画布的 scaleFactor（对标 Unity CanvasScaler）。
     *
     * 职责单一：只做纯计算返回一个缩放比，不直接改任何渲染对象。
     * 消费点集中在 CanvasSystem：
     *   1. 画布逻辑矩形 = 物理尺寸 ÷ scaleFactor（RectTransform 锚点相对逻辑矩形解析）
     *   2. 画布屏幕层容器 scaleX/scaleY = scaleFactor（逻辑单位 → 物理像素）
     *   3. 指针命中前坐标 ÷ scaleFactor（物理像素 → 逻辑单位）
     * 即「画布逻辑分辨率 = 屏幕分辨率 ÷ scaleFactor」（Unity 语义）。
     *
     * 模式：
     *   constantPixelSize    固定 scaleFactor，UI 尺寸不随窗口变化
     *   scaleWithScreenSize  以 referenceResolution 为基准缩放，screenMatchMode 决定宽高比不一致时的取舍：
     *     matchWidthOrHeight  宽/高缩放比在「对数空间」按 matchWidthOrHeight 加权平均
     *                         （零散插值会把 0.75 与 1.33 的均值算成 1.04，对数空间才回到 1.0）
     *     expand              min(宽比, 高比)：画布不小于基准，宁可有富余
     *     shrink              max(宽比, 高比)：画布不大于基准，宁可裁边缘
     *
     * referencePixelsPerUnit：纹理像素 → UI 单位的换算基准（默认 100，即 100 纹理像素 = 1 UI 单位），
     * 供 Image 按纹理原生尺寸适配（setNativeSize）等消费。
     */
    public final class CanvasScaler extends DreamComponent
    {
        /** 缩放模式：constantPixelSize | scaleWithScreenSize。 */
        public var uiScaleMode:String = "scaleWithScreenSize";

        /** constantPixelSize 模式的固定缩放比。 */
        public var scaleFactor:Number = 1;

        /** scaleWithScreenSize 模式的设计基准分辨率（默认 1920×1080）。 */
        public var referenceResolutionX:Number = 1920;
        public var referenceResolutionY:Number = 1080;

        /** 宽高比不一致时的取舍：matchWidthOrHeight | expand | shrink。 */
        public var screenMatchMode:String = "matchWidthOrHeight";

        /** matchWidthOrHeight 的加权：0 = 跟宽，1 = 跟高（对数空间插值）。 */
        public var matchWidthOrHeight:Number = 0.5;

        /** 纹理像素 → UI 单位换算基准（100 = 每 100 纹理像素 1 UI 单位）。 */
        public var referencePixelsPerUnit:Number = 100;

        public function CanvasScaler()
        {
        }

        /**
         * 按视口物理尺寸计算画布 scaleFactor。
         * 视口非法（<=0）或配置异常时返回 1（等价于不缩放）。
         */
        public function computeScaleFactor(viewW:Number, viewH:Number):Number
        {
            if (viewW <= 0 || viewH <= 0) return 1;

            if (uiScaleMode == "constantPixelSize")
                return scaleFactor > 0 ? scaleFactor : 1;

            var rw:Number = referenceResolutionX > 0 ? referenceResolutionX : 1920;
            var rh:Number = referenceResolutionY > 0 ? referenceResolutionY : 1080;
            var ratioW:Number = viewW / rw;
            var ratioH:Number = viewH / rh;

            if (screenMatchMode == "expand") return Math.min(ratioW, ratioH);
            if (screenMatchMode == "shrink") return Math.max(ratioW, ratioH);

            // matchWidthOrHeight：对数空间加权平均。
            var m:Number = matchWidthOrHeight;
            if (m < 0) m = 0; else if (m > 1) m = 1;
            var logW:Number = Math.log(ratioW) / Math.LN2;
            var logH:Number = Math.log(ratioH) / Math.LN2;
            return Math.pow(2, logW + (logH - logW) * m);
        }

        // ── Inspector 反射 ──

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var mode:FieldInfo = new FieldInfo("uiScaleMode", "UI Scale Mode", "string", uiScaleMode);
            mode.options = ["constantPixelSize", "scaleWithScreenSize"];
            var match:FieldInfo = new FieldInfo("screenMatchMode", "Screen Match Mode", "string", screenMatchMode);
            match.options = ["matchWidthOrHeight", "expand", "shrink"];
            return [
                mode,
                new FieldInfo("scaleFactor", "Scale Factor", "number", scaleFactor),
                new FieldInfo("referenceResolution", "Reference Resolution", "vector2", { x: referenceResolutionX, y: referenceResolutionY }),
                match,
                new FieldInfo("matchWidthOrHeight", "Match Width Or Height", "number", matchWidthOrHeight),
                new FieldInfo("referencePixelsPerUnit", "Reference Pixels Per Unit", "number", referencePixelsPerUnit),
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (value == null) return;
            switch (fieldName)
            {
                case "uiScaleMode":
                    uiScaleMode = String(value);
                    break;
                case "scaleFactor":
                    scaleFactor = Math.max(0.01, Number(value));
                    break;
                case "referenceResolution":
                    referenceResolutionX = Math.max(1, Number(value.x));
                    referenceResolutionY = Math.max(1, Number(value.y));
                    break;
                case "screenMatchMode":
                    screenMatchMode = String(value);
                    break;
                case "matchWidthOrHeight":
                    var m:Number = Number(value);
                    matchWidthOrHeight = m < 0 ? 0 : (m > 1 ? 1 : m);
                    break;
                case "referencePixelsPerUnit":
                    referencePixelsPerUnit = Math.max(1, Number(value));
                    break;
            }
        }
    }
}

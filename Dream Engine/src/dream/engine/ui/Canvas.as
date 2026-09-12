package dream.engine.ui
{
    import dream.engine.ecs.DreamComponent;

    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 画布：UI 子树根。
     *
     * 三种渲染模式（renderMode）：
     *   screenSpace（默认）—— 屏幕空间：层容器挂屏幕容器（位于世界内容之上，不随相机），
     *     画布矩形 = 视口逻辑分辨率（物理尺寸 ÷ CanvasScaler.scaleFactor），子元素以
     *     RectTransform 锚点相对该矩形布局。
     *   worldSpace —— 世界空间：层容器挂渲染根（随世界相机缩放/平移），层的位置/旋转/缩放
     *     由本元素的 Transform 驱动；布局仍用「画布单位」（像素语义，与屏幕空间一致）——
     *     画布矩形取自身 RectTransform 尺寸，无 RectTransform 时回退 CanvasScaler 参考分辨率
     *     （默认 1920×1080），层再乘 1/referencePixelsPerUnit，即 100 画布单位 = 1 世界单位。
     *     画布层与场景元素按 sortingOrder 在同一尺度排序，可被前景对象遮挡。
     *   cameraSpace —— 相机空间：布局与屏幕空间完全一致（画布矩形 = 视口逻辑分辨率，铺满
     *     整个视口、不随相机平移缩放），但层容器挂渲染根，因而与场景元素在根容器内按
     *     sortingOrder 一起排序。适合「始终铺满视口、又需要落在世界内容之间」的整屏画面
     *     （如背景、前景遮罩）。本元素的 Transform 不参与布局（铺满视口已完全决定层变换）。
     *
     * 由 CanvasSystem 驱动：为每个画布创建层容器、递归布局、分发指针事件。
     */
    public final class Canvas extends DreamComponent
    {
        /** 屏幕空间（默认）。 */
        public static const SCREEN_SPACE:String = "screenSpace";
        /** 世界空间。 */
        public static const WORLD_SPACE:String = "worldSpace";
        /** 相机空间：铺满视口 + 参与世界排序。 */
        public static const CAMERA_SPACE:String = "cameraSpace";

        /** 渲染模式：SCREEN_SPACE（默认）| WORLD_SPACE | CAMERA_SPACE。 */
        public var renderMode:String = SCREEN_SPACE;

        /**
         * 画布排序序号：数值越大越靠上层。同为 0（默认）时按层级顺序。
         * 屏幕空间：与其他屏幕画布在屏幕容器内排序；
         * 世界空间 / 相机空间：与世界元素同一尺度参与根容器排序。
         */
        public var sortingOrder:int = 0;

        public function Canvas()
        {
        }

        CONFIG::STUDIO
        override public function getInspectableFields():Array
        {
            var mode:FieldInfo = new FieldInfo("renderMode", "Render Mode", "string", renderMode);
            mode.options = [SCREEN_SPACE, WORLD_SPACE, CAMERA_SPACE];
            return [
                mode,
                new FieldInfo("sortingOrder", "Sorting Order", "number", sortingOrder)
            ];
        }

        override public function setFieldValue(fieldName:String, value:*):void
        {
            if (fieldName == "sortingOrder")
                sortingOrder = (value != null) ? int(value) : 0;
            else if (fieldName == "renderMode")
                renderMode = (value != null) ? String(value) : SCREEN_SPACE;
        }
    }
}

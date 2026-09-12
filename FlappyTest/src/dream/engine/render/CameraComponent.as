package dream.engine.render
{
    import dream.engine.ecs.DreamComponent;
    CONFIG::STUDIO
    {
        import dream.engine.ecs.FieldInfo;
    }

    /**
     * 相机组件：挂在元素上定义场景视图（Unity 正交相机风格）。
     *
     * 位置与旋转来自元素 Transform（世界矩阵），视野大小由 orthographicSize 控制：
     *   世界可视高度 = 2 × orthographicSize（固定，不随窗口变化）
     *   世界可视宽度 = 世界可视高度 × 窗口宽高比
     * 窗口 resize 时只改变宽高比，纵向看到的世界范围保持不变。
     *
     * RenderSystem 每帧取主相机（第一个 enabled 的 CameraComponent）应用视图变换，
     * 世界→屏幕倍率 zoom = 窗口像素高 / (2 × orthographicSize)：
     *   世界坐标 → 屏幕坐标 = R(θ)·zoom·p + vc - zoom·R(θ)·camPos
     * 场景没有任何相机时回退到"原点相机"（位置 0,0、zoom=1），世界原点居中于窗口。
     *
     * 用法（游戏代码）：
     *   element.addComponent(new CameraComponent()).orthographicSize = 5;
     *   跟随：每帧移动相机元素 Transform 即可，无需其他设置。
     */
    public final class CameraComponent extends DreamComponent
    {
        /** 视野半高上下限（编辑器滚轮缩放与 Inspector 滑块共用）。 */
        public static const MinOrthoSize:Number = 0.01;
        public static const MaxOrthoSize:Number = 100000;

        private var _orthographicSize:Number = 5;

        /** 视野半高（世界单位）。世界可视高度 = 2 × 此值。 */
        public function get orthographicSize():Number { return _orthographicSize; }
        public function set orthographicSize(v:Number):void
        {
            _orthographicSize = Math.max(MinOrthoSize, Math.min(MaxOrthoSize, v));
        }

        CONFIG::STUDIO
        public override function getInspectableFields():Array
        {
            var f:FieldInfo = new FieldInfo("orthographicSize", "Ortho Size", "number", _orthographicSize);
            f.min = MinOrthoSize;
            f.max = MaxOrthoSize;
            f.step = 1;
            return [f];
        }

        public override function setFieldValue(fieldName:String, value:*):void
        {
            if (fieldName == "orthographicSize") orthographicSize = Number(value);
        }
    }
}

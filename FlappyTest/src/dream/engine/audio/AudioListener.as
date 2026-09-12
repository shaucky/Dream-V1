package dream.engine.audio
{
    import dream.engine.ecs.DreamComponent;
    import dream.engine.transform.Transform;

    /**
     * 音频听者：提供世界位置供空间音频（AudioSource.spatialBlend>0）计算距离衰减。
     * 通常挂在相机/玩家角色上。
     *
     * 同一时刻只有一个生效（AudioListener.current），后加载的覆盖前者；
     * 无听者时空间音频不衰减（保持全音量），保证场景未配置听者也能正常发声。
     *
     * 位置每帧从元素 Transform 的世界矩阵读取，无需同步。
     */
    public final class AudioListener extends DreamComponent
    {
        /** 当前生效的听者（服务定位器）。 */
        public static var current:AudioListener = null;

        override protected function onLoad():void
        {
            current = this;
        }

        /** 听者世界坐标 X。 */
        public function get positionX():Number
        {
            var t:Transform = owner.getComponent(Transform) as Transform;
            return t != null ? t.worldMatrix.tx : 0;
        }

        /** 听者世界坐标 Y。 */
        public function get positionY():Number
        {
            var t:Transform = owner.getComponent(Transform) as Transform;
            return t != null ? t.worldMatrix.ty : 0;
        }

        override protected function onDestroy():void
        {
            if (current == this) current = null;
        }
    }
}

package dream.engine.physics2d
{
    import dream.engine.ecs.Element;

    /**
     * 射线命中结果（Unity RaycastHit2D 风格）：Physics2D.raycast 的返回值。
     * 未命中时 raycast 返回 null。
     */
    public final class RaycastHit2D
    {
        /** 命中点（世界坐标）。 */
        public var point:Object;       // {x, y}

        /** 命中表面法线（单位向量，世界坐标）。 */
        public var normal:Object;      // {x, y}

        /** 起点到命中点的距离（世界单位）。 */
        public var distance:Number = 0;

        /** 命中的碰撞器组件。 */
        public var collider:Collider2D = null;

        /** 命中元素；collider-only 时为 null（理论上有 collider 必有元素）。 */
        public var element:Element = null;

        /** 命中元素上的刚体组件；仅静态碰撞体时为 null。 */
        public var rigidBody:RigidBody2D = null;
    }
}

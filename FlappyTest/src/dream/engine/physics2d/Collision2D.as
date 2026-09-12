package dream.engine.physics2d
{
    import dream.engine.ecs.Element;

    /**
     * 碰撞/触发回调数据（Unity Collision2D 风格）。
     *
     * 由 PhysicsSystem2D 构建并传入组件回调，描述"对方"的碰撞信息：
     *   - collider   对方的碰撞器组件
     *   - rigidBody  对方的刚体组件（静态碰撞体时为 null）
     *   - element    对方的元素
     *   - contacts   世界空间接触点列表（[{x, y}, ...]）
     *   - relativeSpeed  双方相对速度标量
     */
    public final class Collision2D
    {
        /** 对方碰撞器组件。 */
        public var collider:Collider2D = null;

        /** 对方刚体组件；对方是静态碰撞体时为 null。 */
        public var rigidBody:RigidBody2D = null;

        /** 对方元素。 */
        public var element:Element = null;

        /** 世界空间接触点列表（触发回调时可能为空）。 */
        public var contacts:Array = [];

        /** 双方相对速度标量。 */
        public var relativeSpeed:Number = 0;
    }
}

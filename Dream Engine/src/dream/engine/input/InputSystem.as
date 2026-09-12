package dream.engine.input
{
    import dream.engine.ecs.DreamSystem;
    import dream.engine.ecs.World;

    /**
     * 输入系统：每帧刷新 InputManager 的帧状态（重置本帧 pressed/released/滚轮增量）。
     *
     * 必须注册在**所有读取输入的系统之后**（World 按注册顺序更新）：
     * 帧状态是「上次清理之后累积到本帧」的边沿量，消费者（如 CanvasSystem 的点击/滚轮分发）
     * 需要在本帧读到它，之后再清零。若注册在最前，帧间到达的输入会在下一帧起始先被清空，
     * 边沿检测将永远读不到。
     */
    public final class InputSystem extends DreamSystem
    {
        override public function update(world:World, dt:Number):void
        {
            if (InputManager.current != null)
                InputManager.current.update(dt);
        }
    }
}

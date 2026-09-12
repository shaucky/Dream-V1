package dream.engine.ecs
{
    /**
     * 系统基类：ECS 中的逻辑载体。每个系统关注一组组件类型，
     * 在 World.update 时对拥有这些组件的元素集合执行逻辑。
     *
     * 子类重写 update 处理逻辑，通过 world.query(...requiredComponents) 获取目标元素。
     * 系统本身无状态（或有可重置状态），不持有元素引用——每帧动态查询。
     */
    public class DreamSystem
    {
        /** 该系统关注的组件类型列表。World 可据此预过滤，系统也可自行 query。 */
        public function get requiredComponents():Array
        {
            return null;
        }

        /** 是否启用。World.update 时跳过未启用的系统。 */
        public var enabled:Boolean = true;

        /**
         * 每帧更新。dt 为距上一帧的秒数。
         * 子类重写此方法实现具体逻辑。
         */
        public function update(world:World, dt:Number):void
        {
        }
    }
}

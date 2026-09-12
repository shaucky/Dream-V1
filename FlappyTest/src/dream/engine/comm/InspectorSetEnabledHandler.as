package dream.engine.comm
{
    CONFIG::STUDIO
    {
        import dream.engine.ecs.DreamComponent;
        import dream.engine.ecs.Element;
        import dream.engine.ecs.World;
        import dream.engine.scene.SceneSerializer;

        /**
         * 元素/组件启用开关处理器：响应 Inspector 的 enabled 复选框。
         *
         * payload：
         *   {elementId: 0, component: "AudioSource", enabled: true}
         *   component 缺省/空串时设置元素级 enabled（联动所有组件 onEnable/onDisable）；
         *   否则设置指定组件的 enabled。
         */
        public final class InspectorSetEnabledHandler implements IMessageHandler
        {
            private var _world:World;
            private var _serializer:SceneSerializer;

            public function InspectorSetEnabledHandler(world:World, serializer:SceneSerializer = null)
            {
                _world = world;
                _serializer = serializer;
            }

            public function get messageType():String
            {
                return MessageTypes.InspectorSetEnabled;
            }

            public function handle(message:Message, channel:IChannel):void
            {
                var p:Object = message.payload;
                if (p == null) return;

                var elementId:int = int(p.elementId);
                var enabled:Boolean = Boolean(p.enabled);

                var e:Element = InspectorShared.findElement(_world, elementId);
                if (e == null) return;

                var component:String = p.component != null ? String(p.component) : "";
                if (component.length == 0)
                {
                    e.enabled = enabled; // 元素级：setter 联动组件生命周期。
                    // prefab 实例：元素级启用记为 override（元素 enabled 会写进存档）。
                    if (_serializer != null) _serializer.recordOverride(elementId, "enabled");
                    return;
                }

                var c:DreamComponent = InspectorShared.findComponent(e, component);
                if (c != null) c.enabled = enabled; // 组件级：setter 联动 onEnable/onDisable。
            }
        }
    }
}

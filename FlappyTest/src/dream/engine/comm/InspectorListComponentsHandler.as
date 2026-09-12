package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.ComponentFactory;

		/**
		 * Inspector 组件列表处理器：响应 Studio 端 "Add Component" 请求。
		 *
		 * 收到 inspector.listComponents 时返回 ComponentFactory 注册的所有
		 * 可添加组件短类名，供 Studio 弹出选择列表。
		 *
		 * 返回消息 inspector.componentList：
		 *   { "components": ["Rotator", ...] }
		 */
		public final class InspectorListComponentsHandler implements IMessageHandler
		{
			public function InspectorListComponentsHandler()
			{
			}

			public function get messageType():String
			{
				return MessageTypes.InspectorListComponents;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var names:Array = ComponentFactory.listNames();
				channel.send(new Message(MessageTypes.InspectorComponentList, {components: names}));
			}
		}
	}
}

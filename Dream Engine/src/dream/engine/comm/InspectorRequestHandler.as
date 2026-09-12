package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.World;

		/**
		 * Inspector 请求处理器：响应 Studio 端选中元素后的字段查看请求。
		 *
		 * 收到 inspector.request 时序列化指定元素的所有组件字段发送到 Studio。
		 *
		 * 快照格式（JSON）：
		 *   {
		 *     "elementId": 0,
		 *     "components": [
		 *       {
		 *         "name": "Transform",
		 *         "fields": [
		 *           {"name":"position","label":"Position","type":"vector2","value":{"x":0,"y":0}},
		 *           ...
		 *         ]
		 *       }
		 *     ]
		 *   }
		 */
		public final class InspectorRequestHandler implements IMessageHandler
		{
			private var _world:World;

			public function InspectorRequestHandler(world:World)
			{
				_world = world;
			}

			public function get messageType():String
			{
				return MessageTypes.InspectorRequest;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var p:Object = message.payload;
				if (p == null) return;
				var elementId:int = int(p.elementId);
				InspectorShared.sendSnapshot(_world, elementId, channel);
			}
		}
	}
}

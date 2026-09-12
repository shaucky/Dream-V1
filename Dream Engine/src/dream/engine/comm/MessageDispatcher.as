package dream.engine.comm
{
	CONFIG::STUDIO
	{
		/** 按消息类型分发到已注册处理器；未注册类型静默忽略。 */
		public final class MessageDispatcher
		{
			private var handlers:Object = {};

			public function add(handler:IMessageHandler):MessageDispatcher
			{
				handlers[handler.messageType] = handler;
				return this;
			}

			public function dispatch(message:Message, channel:IChannel):void
			{
				var h:IMessageHandler = handlers[message.type] as IMessageHandler;
				if (h) h.handle(message, channel);
			}
		}
	}
}

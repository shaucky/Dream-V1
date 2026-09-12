package dream.engine.comm
{
	CONFIG::STUDIO
	{
		/** 单类型消息处理器（OCP：新增消息只需新增处理器并在 dispatcher 注册）。 */
		public interface IMessageHandler
		{
			function get messageType():String;
			function handle(message:Message, channel:IChannel):void;
		}
	}
}

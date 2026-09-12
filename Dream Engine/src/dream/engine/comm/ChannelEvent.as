package dream.engine.comm
{
	import flash.events.Event;

	CONFIG::STUDIO
	{
		/**
		 * 通道事件：MESSAGE 收到完整消息，CLOSED 通道关闭。
		 * 携带 message 字段（仅 MESSAGE 有效）。
		 */
		public final class ChannelEvent extends Event
		{
			public static const MESSAGE:String = "message";
			public static const CLOSED:String = "closed";

			public var message:Message;

			public function ChannelEvent(type:String, message:Message = null, bubbles:Boolean = false, cancelable:Boolean = false)
			{
				super(type, bubbles, cancelable);
				this.message = message;
			}

			public override function clone():Event
			{
				return new ChannelEvent(type, message, bubbles, cancelable);
			}
		}
	}
}

package dream.engine.comm
{
	import flash.events.IEventDispatcher;

	CONFIG::STUDIO
	{
		/** 双向消息通道抽象：发送、接收、生命周期。 */
		public interface IChannel extends IEventDispatcher
		{
			function get connected():Boolean;
			function send(message:Message):void;
			function close():void;
		}
	}
}

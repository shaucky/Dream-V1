package dream.engine.comm
{
	CONFIG::STUDIO
	{
		/**
		 * 消息信封：所有跨进程消息的统一外壳。
		 * 与 C# 侧 Dream.Studio.Engine.Comm.Message 字段保持一致。
		 */
		public final class Message
		{
			public var type:String;
			public var payload:Object;

			public function Message(type:String = "", payload:Object = null)
			{
				this.type = type;
				this.payload = payload;
			}
		}
	}
}

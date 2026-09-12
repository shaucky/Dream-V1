package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.scene.SceneSerializer;

		import flash.filesystem.File;
		import flash.filesystem.FileMode;
		import flash.filesystem.FileStream;

		/**
		 * 场景保存处理器：序列化当前 World 为 JSON 写入指定路径。
		 *
		 * payload: {path: "C:/.../scene.space"}
		 * 返回 scene.result: {action:"save", success:true|false}
		 */
	public final class SceneSaveHandler implements IMessageHandler
		{
			private var _serializer:SceneSerializer;

			public function SceneSaveHandler(serializer:SceneSerializer)
			{
				_serializer = serializer;
			}

			public function get messageType():String
			{
				return MessageTypes.SceneSave;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var p:Object = message.payload;
				if (p == null) { sendResult(channel, false); return; }
				var path:String = String(p.path);
				var success:Boolean = save(path);
				sendResult(channel, success);
			}

			private function save(path:String):Boolean
			{
				if (path == null || path.length == 0) return false;
				try
				{
					var data:Object = _serializer.serialize();
					var json:String = JSON.stringify(data);
					var file:File = new File(path);
					var fs:FileStream = new FileStream();
					fs.open(file, FileMode.WRITE);
					fs.writeUTFBytes(json);
					fs.close();
					return true;
				}
				catch (e:Error) { return false; }
			}

			private function sendResult(channel:IChannel, success:Boolean):void
			{
				channel.send(new Message(MessageTypes.SceneResult, {action: "save", success: success}));
			}
		}
	}
}

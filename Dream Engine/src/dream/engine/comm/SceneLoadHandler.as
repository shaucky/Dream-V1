package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.scene.SceneSerializer;
		import dream.engine.scene.UndoRedoManager;

		import flash.filesystem.File;
		import flash.filesystem.FileMode;
		import flash.filesystem.FileStream;

		/**
		 * 场景加载处理器：从指定路径读取 JSON 并重建 World。
		 *
		 * payload: {path: "C:/.../scene.space"}
		 * 返回 scene.result: {action:"load", success:true|false}
		 *
		 * onLoaded：加载成功后的回调（参数为路径）。引擎据此按扩展名更新当前文档类型
		 * （预制体文档要显示返回按钮、且不注入自动相机），见 DreamEngine.onDocumentLoaded。
		 */
	public final class SceneLoadHandler implements IMessageHandler
		{
			private var _serializer:SceneSerializer;
			private var _undoRedo:UndoRedoManager;
			private var _onLoaded:Function;

			public function SceneLoadHandler(serializer:SceneSerializer, undoRedo:UndoRedoManager = null,
			                                onLoaded:Function = null)
			{
				_serializer = serializer;
				_undoRedo = undoRedo;
				_onLoaded = onLoaded;
			}

			public function get messageType():String
			{
				return MessageTypes.SceneLoad;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var p:Object = message.payload;
				if (p == null) { sendResult(channel, false); return; }
				var path:String = String(p.path);
				var success:Boolean = load(path);
				if (success)
				{
					if (_undoRedo != null) _undoRedo.clear();
					if (_onLoaded != null) _onLoaded(path);
				}
				sendResult(channel, success);
			}

			private function load(path:String):Boolean
			{
				if (path == null || path.length == 0) return false;
				try
				{
					var file:File = new File(path);
					if (!file.exists) return false;
					var fs:FileStream = new FileStream();
					fs.open(file, FileMode.READ);
					var json:String = fs.readUTFBytes(fs.bytesAvailable);
					fs.close();
					var data:Object = JSON.parse(json);
					return _serializer.deserialize(data);
				}
				catch (e:Error) { return false; }
			}

			private function sendResult(channel:IChannel, success:Boolean):void
			{
				channel.send(new Message(MessageTypes.SceneResult, {action: "load", success: success}));
			}
		}
	}
}

package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.scene.SceneSerializer;

		/**
		 * Prefab 导出处理器：把指定元素为根的子树序列化为 prefab 数据回传 Studio。
		 * 引擎不落盘——文件写出与 .meta 注册由 Studio 负责（资源归属 Studio）。
		 *
		 * payload: {id: 3}
		 * 返回 prefab.exportResult: {action:"export", success:true|false, data:{elements:[...]}}
		 * data 与 .space 同构（见 SceneSerializer.serializeSubtree），可直接写成 .prefab 文件。
		 */
		public final class PrefabExportHandler implements IMessageHandler
		{
			private var _serializer:SceneSerializer;

			public function PrefabExportHandler(serializer:SceneSerializer)
			{
				_serializer = serializer;
			}

			public function get messageType():String
			{
				return MessageTypes.PrefabExport;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var p:Object = message.payload;
				var data:Object = null;
				if (p != null && _serializer != null)
					data = _serializer.serializeSubtree(int(p.id));
				channel.send(new Message(MessageTypes.PrefabExportResult,
					{ action: "export", success: data != null, data: data }));
			}
		}
	}
}

package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.World;
		import dream.engine.scene.SceneSelection;
		import dream.engine.scene.SceneSerializer;

		/**
		 * 预制体源内容处理器：Studio 推送「prefab 资产 GUID → .prefab 文件内容」，
		 * 引擎据此把源改动同步到当前场景里的实例（跳过实例自己的 override）。
		 *
		 * 收到即应用，不做缓存：Studio 只在场景加载完成之后推（打开场景、引擎连上时——
		 * 引擎启动/重启时是先加载 --scene 再 connect 的），所以同步落在的正是目标场景。
		 *
		 * payload 格式：
		 *   {entries: [{guid: "<GUID>", data: {elements: [...]}}, ...]}
		 *
		 * data 为 .prefab 文件内容（与 .space 同构）。同步语义见
		 * SceneSerializer.syncPrefabSources / syncInstances。
		 */
		public final class PrefabSourcesHandler implements IMessageHandler
		{
			private var _serializer:SceneSerializer;
			private var _world:World;
			private var _selection:SceneSelection;

			public function PrefabSourcesHandler(serializer:SceneSerializer, world:World,
			                                     selection:SceneSelection = null)
			{
				_serializer = serializer;
				_world = world;
				_selection = selection;
			}

			public function get messageType():String
			{
				return MessageTypes.PrefabSources;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var p:Object = message.payload;
				if (p == null || _serializer == null) return;

				var applied:int = _serializer.syncPrefabSources(p.entries as Array);
				if (applied <= 0) return;

				// 实例字段可能被改写：刷新当前选中元素的 Inspector 快照。
				// （Hierarchy 里的元素名变化由 hierarchyDirty 标记驱动，无需在此处理。）
				if (_selection != null && _selection.contains(_selection.selectedElementId))
					InspectorShared.sendSnapshot(_world, _selection.selectedElementId, channel);
			}
		}
	}
}

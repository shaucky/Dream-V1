package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.World;
		import dream.engine.scene.SceneSelection;
		import dream.engine.scene.SceneSerializer;

		/**
		 * 预制体还原处理器：把某个 prefab 实例还原到源（清空该实例的全部 override）。
		 *
		 * 收到 prefab.revert 时调用 SceneSerializer.clearInstanceOverrides：
		 *   - 该元素所属实例下所有元素的 override 集被清空（实例顶层的摆放位置保留）；
		 *   - 引擎不缓存 prefab 源内容（见 PrefabSourcesHandler），所以清空只是"解绑"，
		 *     真正把源字段值写回实例要靠 Studio 紧接着重推 prefab.sources。
		 *     两条消息走同一通道，FIFO 保证先后。
		 *
		 * payload 格式：
		 *   {elementId: 12}   // 实例内任意元素的 ID
		 */
		public final class PrefabRevertHandler implements IMessageHandler
		{
			private var _serializer:SceneSerializer;
			private var _world:World;
			private var _selection:SceneSelection;

			public function PrefabRevertHandler(serializer:SceneSerializer, world:World,
			                                    selection:SceneSelection = null)
			{
				_serializer = serializer;
				_world = world;
				_selection = selection;
			}

			public function get messageType():String
			{
				return MessageTypes.PrefabRevert;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var p:Object = message.payload;
				if (p == null || _serializer == null) return;

				var elementId:int = int(p.elementId);
				if (!_serializer.clearInstanceOverrides(elementId)) return;

				// override 标记消失了：刷新当前选中元素的 Inspector 快照，让面板立刻去掉标记
				// （字段值本身要等 Studio 重推源之后才对，届时会再收到一次快照）。
				if (_selection != null && _selection.contains(_selection.selectedElementId))
					InspectorShared.sendSnapshot(_world, _selection.selectedElementId, channel);
			}
		}
	}
}

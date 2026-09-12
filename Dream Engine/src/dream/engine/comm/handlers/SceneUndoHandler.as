package dream.engine.comm.handlers
{
	CONFIG::STUDIO
	{
		import dream.engine.comm.IChannel;
		import dream.engine.comm.IMessageHandler;
		import dream.engine.comm.InspectorShared;
		import dream.engine.comm.Message;
		import dream.engine.comm.MessageTypes;
		import dream.engine.ecs.World;
		import dream.engine.scene.SceneSelection;
		import dream.engine.scene.UndoRedoManager;

		/**
		 * 处理 scene.undo：调用 UndoRedoManager 撤销一步。
		 *
		 * 撤销是"反序列化旧快照"——整个世界被重建、字段值可能整片回退。
		 * Hierarchy 靠脏标记会自行刷新，Inspector 没有推送源，所以要在这里补发一次
		 * 选中元素的快照；与 PrefabSourcesHandler 应用完源之后刷新 Inspector 的做法一致。
		 */
		public final class SceneUndoHandler implements IMessageHandler
		{
			private var _undoRedo:UndoRedoManager;
			private var _world:World;
			private var _selection:SceneSelection;

			public function SceneUndoHandler(undoRedo:UndoRedoManager, world:World = null, selection:SceneSelection = null)
			{
				_undoRedo = undoRedo;
				_world = world;
				_selection = selection;
			}

			public function get messageType():String
			{
				return MessageTypes.SceneUndo;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				// 无可撤销步骤：世界没动过，不必刷新。
				if (!_undoRedo.undo()) return;
				InspectorShared.sendSelectionSnapshot(_world, _selection, channel);
			}
		}
	}
}

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
		 * 处理 scene.redo：调用 UndoRedoManager 重做一步。
		 *
		 * 重做同样是"反序列化旧快照"，字段值可能整片回退，需要补发一次选中元素的
		 * Inspector 快照（见 SceneUndoHandler 的同名处理）。
		 */
		public final class SceneRedoHandler implements IMessageHandler
		{
			private var _undoRedo:UndoRedoManager;
			private var _world:World;
			private var _selection:SceneSelection;

			public function SceneRedoHandler(undoRedo:UndoRedoManager, world:World = null, selection:SceneSelection = null)
			{
				_undoRedo = undoRedo;
				_world = world;
				_selection = selection;
			}

			public function get messageType():String
			{
				return MessageTypes.SceneRedo;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				// 无可重做步骤：世界没动过，不必刷新。
				if (!_undoRedo.redo()) return;
				InspectorShared.sendSelectionSnapshot(_world, _selection, channel);
			}
		}
	}
}

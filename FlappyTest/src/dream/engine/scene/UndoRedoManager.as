package dream.engine.scene
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.World;

		/**
		 * 快照式撤销/重做管理器。
		 *
		 * 每次用户执行修改场景的操作前，先序列化当前 World 状态并入 undo 栈；
		 * 撤销时恢复栈顶快照，重做时恢复 redo 栈顶快照。
		 *
		 * 当前覆盖：Hierarchy 命令（create/destroy/rename/reparent）、Inspector 字段编辑、
		 * Add/Remove/Paste 组件。场景加载/新建时清空历史。
		 */
		public final class UndoRedoManager
		{
			private var _serializer:SceneSerializer;
			private var _undoStack:Vector.<Object> = new Vector.<Object>();
			private var _redoStack:Vector.<Object> = new Vector.<Object>();
			private var _maxHistory:int = 50;

			public function UndoRedoManager(serializer:SceneSerializer)
			{
				_serializer = serializer;
			}

			/** 当前是否可撤销。 */
			public function get canUndo():Boolean { return _undoStack.length > 0; }

			/** 当前是否可重做。 */
			public function get canRedo():Boolean { return _redoStack.length > 0; }

			/**
			 * 记录当前 World 快照。在任意修改操作前调用；
			 * 调用后清空 redo 栈（新分支）。
			 */
			public function record():void
			{
				var snapshot:Object = _serializer.serialize();
				_undoStack.push(snapshot);
				if (_undoStack.length > _maxHistory)
					_undoStack.shift();
				_redoStack.length = 0;
			}

			/** 撤销一步：恢复 undo 栈顶快照，并将当前状态推入 redo 栈。 */
			public function undo():Boolean
			{
				if (!canUndo) return false;
				var current:Object = _serializer.serialize();
				var snapshot:Object = _undoStack.pop();
				_redoStack.push(current);
				return _serializer.deserialize(snapshot);
			}

			/** 重做一步：恢复 redo 栈顶快照，并将当前状态推入 undo 栈。 */
			public function redo():Boolean
			{
				if (!canRedo) return false;
				var current:Object = _serializer.serialize();
				var snapshot:Object = _redoStack.pop();
				_undoStack.push(current);
				return _serializer.deserialize(snapshot);
			}

			/** 清空 undo/redo 历史。场景加载/新建时调用。 */
			public function clear():void
			{
				_undoStack.length = 0;
				_redoStack.length = 0;
			}
		}
	}
}

package dream.engine.comm.handlers
{
	CONFIG::STUDIO
	{
		import dream.engine.comm.IChannel;
		import dream.engine.comm.IMessageHandler;
		import dream.engine.comm.Message;
		import dream.engine.comm.MessageTypes;
		import dream.engine.scene.SceneSelection;

		/**
		 * 场景选中处理器：接收 Studio 的 scene.select 消息，设置编辑器选中元素。
		 *
		 * 触发场景：用户点击 Hierarchy 面板节点 → Studio 发送 scene.select，
		 * 引擎据此移动视口高亮框。视口内直接点击产生的拾取由引擎本地处理
		 * （RenderEngine.onClick → SceneSelection）。
		 *
		 * payload 格式：{ "elementIds": [0,1], "primaryId": 1 }
		 * 兼容旧格式：{ "elementId": 0 }（单选）。
		 */
		public final class SceneSelectHandler implements IMessageHandler
		{
			private var _selection:SceneSelection;

			public function SceneSelectHandler(selection:SceneSelection)
			{
				_selection = selection;
			}

			public function get messageType():String
			{
				return MessageTypes.SceneSelect;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				var p:Object = message.payload;
				if (p == null || _selection == null) return;

				// 静默应用 Studio 的选择集：这是"picked → select"回环的终点，
				// 若再触发 onSelectionChange（→ pushSelection → scene.picked）
				// 会与 Studio 的 SelectionChanged 回发形成无限消息风暴。
				_selection.setNotifyEnabled(false);
				if (p.elementIds is Array)
				{
					var ids:Array = p.elementIds;
					_selection.setSelection(ids, int(p.primaryId));
				}
				else
				{
					_selection.select(int(p.elementId));
				}
				_selection.setNotifyEnabled(true);
			}
		}
	}
}

package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.animation.AnimationFieldControl;
		import dream.engine.ecs.DreamComponent;
		import dream.engine.ecs.Element;
		import dream.engine.ecs.World;
		import dream.engine.scene.SceneSerializer;
		import dream.engine.scene.UndoRedoManager;

		/**
		 * Inspector 编辑处理器：响应 Studio 端编辑字段值。
		 *
		 * 收到 inspector.edit 时调用组件 setFieldValue 应用变更，
		 * 然后发送更新后的快照到 Studio。
		 *
		 * payload 格式：
		 *   {elementId:0, component:"Transform", field:"position", value:{x:10,y:20}}
		 */
		public final class InspectorEditHandler implements IMessageHandler
		{
			private var _world:World;
			private var _undoRedo:UndoRedoManager;
			private var _serializer:SceneSerializer;

			public function InspectorEditHandler(world:World, undoRedo:UndoRedoManager = null,
			                                    serializer:SceneSerializer = null)
			{
				_world = world;
				_undoRedo = undoRedo;
				_serializer = serializer;
			}

			public function get messageType():String
			{
				return MessageTypes.InspectorEdit;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				// 运行模式下也允许字段编辑：运行中的游戏实例可被编辑。
				var p:Object = message.payload;
				if (p == null) return;

				var elementId:int = int(p.elementId);
				var componentName:String = String(p.component);
				var fieldName:String = String(p.field);
				var value:* = p.value;

				var e:Element = InspectorShared.findElement(_world, elementId);
				if (e == null) return;

				var c:DreamComponent = InspectorShared.findComponent(e, componentName);
			if (c == null) return;

			// 动画独占：播放期间动画轨道覆盖的字段禁止外部写入（Inspector 编辑被拦截），
			// 避免与动画采样值争抢导致抖动。独占按目标元素作用域化：仅拦截被该元素
			// 正在播放的动画所覆盖的字段（键 e+元素id）。
			if (AnimationFieldControl.isControlled("e" + elementId, componentName, fieldName)) return;

			if (_undoRedo != null) _undoRedo.record();
			c.setFieldValue(fieldName, value);
			// prefab 实例：把这次改动记为该字段的 override，源 prefab 改动传播时不覆盖它。
			if (_serializer != null) _serializer.recordOverride(elementId, componentName + "." + fieldName);
			InspectorShared.sendSnapshot(_world, elementId, channel);
			}
		}
	}
}

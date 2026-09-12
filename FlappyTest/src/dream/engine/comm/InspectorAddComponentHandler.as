package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.ComponentFactory;
		import dream.engine.ecs.DreamComponent;
		import dream.engine.ecs.Element;
		import dream.engine.physics2d.Collider2D;
		import dream.engine.scene.SceneSerializer;
		import dream.engine.scene.UndoRedoManager;

		/**
		 * Inspector 添加组件处理器：响应 Studio 端 "Add Component" 操作。
		 *
		 * 收到 inspector.addComponent 时：
		 *   1. 按 elementId 查找元素
		 *   2. 按 component 短类名从 ComponentFactory 创建实例
		 *   3. element.addComponent(c) 挂载并触发生命周期
		 *   4. 标记 hierarchyDirty（Hierarchy 需要刷新）
		 *   5. 发送新的 Inspector 快照到 Studio
		 *
		 * payload 格式：
		 *   {elementId:0, component:"Rotator"}
		 */
		public final class InspectorAddComponentHandler implements IMessageHandler
		{
			private var _world:*;
			private var _undoRedo:UndoRedoManager;
			private var _serializer:SceneSerializer;

			public function InspectorAddComponentHandler(world:*, undoRedo:UndoRedoManager = null,
			                                            serializer:SceneSerializer = null)
			{
				_world = world;
				_undoRedo = undoRedo;
				_serializer = serializer;
			}

			public function get messageType():String
			{
				return MessageTypes.InspectorAddComponent;
			}

			public function handle(message:Message, channel:IChannel):void
		{
			// 运行模式下也允许添加组件：运行中的游戏实例可被编辑。
			var p:Object = message.payload;
			if (p == null) return;

			var elementId:int = int(p.elementId);
			var componentName:String = String(p.component);

			var e:Element = InspectorShared.findElement(_world, elementId);
			if (e == null) return;

			// 已有同类型组件则跳过：避免 addComponent 覆盖现有实例导致字段值丢失。
			var type:Class = ComponentFactory.getType(componentName);
			if (type == null || e.hasComponent(type)) return;

			var c:DreamComponent = ComponentFactory.create(componentName);
			if (c == null) return;

			if (_undoRedo != null) _undoRedo.record();
			e.addComponent(c);
			// 新增碰撞器：自动适配宿主元素显示包围盒（开发体验，随后可手动调整）。
			var col:Collider2D = c as Collider2D;
			if (col != null) col.fitToBounds();
			_world.hierarchyDirty = true;
			// prefab 实例：用户自行添加的组件记为 override（键为组件短名），同步时不删不改。
			if (_serializer != null) _serializer.recordOverride(elementId, componentName);
			InspectorShared.sendSnapshot(_world, elementId, channel);
		}
		}
	}
}

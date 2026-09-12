package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.DreamComponent;
		import dream.engine.ecs.Element;
		import dream.engine.scene.SceneSerializer;
		import dream.engine.scene.UndoRedoManager;

		import flash.utils.getDefinitionByName;
		import flash.utils.getQualifiedClassName;

		/**
		 * Inspector 移除组件处理器：响应 Studio 端组件右键菜单 "Remove Component"。
		 *
		 * 收到 inspector.removeComponent 时：
		 *   1. 按 elementId 查找元素
		 *   2. 按 component 短类名查找组件实例，反查其 Class
		 *   3. element.removeComponent(type) 卸载并销毁
		 *   4. 标记 hierarchyDirty
		 *   5. 发送新的 Inspector 快照到 Studio
		 *
		 * payload 格式：
		 *   {elementId:0, component:"Rotator"}
		 *
		 * 注意：Transform 不应被移除（元素必需），由 Studio 端菜单禁用保证。
		 */
		public final class InspectorRemoveComponentHandler implements IMessageHandler
		{
			private var _world:*;
			private var _undoRedo:UndoRedoManager;
			private var _serializer:SceneSerializer;

			public function InspectorRemoveComponentHandler(world:*, undoRedo:UndoRedoManager = null,
			                                               serializer:SceneSerializer = null)
			{
				_world = world;
				_undoRedo = undoRedo;
				_serializer = serializer;
			}

			public function get messageType():String
			{
				return MessageTypes.InspectorRemoveComponent;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				// 运行模式下也允许移除组件：运行中的游戏实例可被编辑。
				var p:Object = message.payload;
				if (p == null) return;

				var elementId:int = int(p.elementId);
				var componentName:String = String(p.component);

				var e:Element = InspectorShared.findElement(_world, elementId);
				if (e == null) return;

				var c:DreamComponent = InspectorShared.findComponent(e, componentName);
				if (c == null) return;

				var type:Class = getDefinitionByName(getQualifiedClassName(c)) as Class;
			if (type == null) return;

			if (_undoRedo != null) _undoRedo.record();
			e.removeComponent(type);
			_world.hierarchyDirty = true;
			// prefab 实例：移除记为 override（"!组件短名"），源 prefab 里仍存在该组件时也不补回。
			if (_serializer != null) _serializer.recordOverride(elementId, "!" + componentName);
			InspectorShared.sendSnapshot(_world, elementId, channel);
			}
		}
	}
}

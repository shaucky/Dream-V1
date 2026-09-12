package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.ComponentFactory;
		import dream.engine.ecs.DreamComponent;
		import dream.engine.ecs.Element;
		import dream.engine.render.DisplayComponent;
		import dream.engine.transform.Transform;
		import dream.engine.render.Drawable;
		import dream.engine.render.RenderEngine;
		import dream.engine.scene.UndoRedoManager;

		/**
		 * Inspector 粘贴组件处理器：响应 Studio 端组件右键菜单的"粘贴组件 / 粘贴组件值"。
		 *
		 * payload 格式：
		 *   {elementId:0, component:"Rotator", fields:{speed:2.0}, create:true}
		 *
		 * 行为：
		 *   create=true（粘贴组件）：
		 *     1. 按 component 短类名创建实例（Transform 跳过——每个元素已有一个且不可重复；
		 *        DisplayComponent 用 RenderEngine.createQuad 构造；其余走 ComponentFactory）
		 *     2. element.addComponent 挂载
		 *     3. 用 setFieldValue 恢复 fields 中每个字段
		 *   create=false（粘贴组件值）：
		 *     1. 在元素上查找同短类名组件，找不到则静默返回
		 *     2. 用 setFieldValue 覆盖 fields 中每个字段
		 *
		 * 完成后标记 hierarchyDirty 并发送新的 Inspector 快照。
		 */
		public final class InspectorPasteComponentHandler implements IMessageHandler
		{
			private var _world:*;
			private var _renderEngine:RenderEngine;
			private var _undoRedo:UndoRedoManager;

			public function InspectorPasteComponentHandler(world:*, renderEngine:RenderEngine, undoRedo:UndoRedoManager = null)
			{
				_world = world;
				_renderEngine = renderEngine;
				_undoRedo = undoRedo;
			}

			public function get messageType():String
			{
				return MessageTypes.InspectorPasteComponent;
			}

			public function handle(message:Message, channel:IChannel):void
			{
				// 运行模式下也允许粘贴组件：运行中的游戏实例可被编辑。
				var p:Object = message.payload;
				if (p == null) return;

				var elementId:int = int(p.elementId);
				var componentName:String = String(p.component);
				var create:Boolean = Boolean(p.create);

				var e:Element = InspectorShared.findElement(_world, elementId);
				if (e == null) return;

				if (create)
			{
				// Transform 不可重复添加，跳过。
				if (componentName == "Transform") return;
				var c:DreamComponent = createComponent(componentName);
				if (c == null) return;
				if (_undoRedo != null) _undoRedo.record();
				e.addComponent(c);
				applyFields(c, p.fields);
				_world.hierarchyDirty = true;
			}
			else
			{
				var existing:DreamComponent = InspectorShared.findComponent(e, componentName);
				if (existing == null) return;
				if (_undoRedo != null) _undoRedo.record();
				applyFields(existing, p.fields);
			}

			InspectorShared.sendSnapshot(_world, elementId, channel);
			}

			/** 按 fields 对象逐字段调用 setFieldValue。 */
			private function applyFields(c:DreamComponent, fields:Object):void
			{
				if (fields == null) return;
				for (var fname:String in fields)
					c.setFieldValue(fname, fields[fname]);
			}

			/** 按类型名创建组件实例。DisplayComponent 需 Drawable，其余走 ComponentFactory。 */
			private function createComponent(typeName:String):DreamComponent
			{
				switch (typeName)
				{
					case "DisplayComponent":
					var quad:Drawable = _renderEngine.createQuad(1, 1, 0xFFFFFF);
					return new DisplayComponent(quad);
					default:
						return ComponentFactory.create(typeName);
				}
			}
		}
	}
}

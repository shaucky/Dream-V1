package dream.engine.comm.handlers
{
	CONFIG::STUDIO
	{
		import dream.engine.comm.IChannel;
		import dream.engine.comm.IMessageHandler;
		import dream.engine.comm.Message;
		import dream.engine.comm.MessageTypes;
		import dream.engine.ecs.ComponentFactory;
		import dream.engine.ecs.DreamComponent;
		import dream.engine.ecs.World;
		import dream.engine.ecs.Element;
		import dream.engine.render.SpriteRenderer;
		import dream.engine.transform.Transform;
		import dream.engine.render.RenderEngine;
		import dream.engine.scene.SceneSelection;
		import dream.engine.scene.SceneSerializer;
		import dream.engine.scene.UndoRedoManager;

		/**
		 * Hierarchy 命令处理器：接收 Studio 发来的操作命令并执行。
		 *
		 * 命令格式（payload）：
		 *   create:  {action:"create",  parentId:-1}          创建新元素，可选挂为 parentId 的子级
		 *   destroy: {action:"destroy", id:0}                 销毁指定元素
		 *   rename:  {action:"rename",  id:0, name:"NewName"} 重命名指定元素
		 *   reparent:{action:"reparent", id, parentId, siblingId?, insertBefore?}
		 *            调整元素父级。parentId=-1 提为根。siblingId 给定时插入到该兄弟
		 *            前/后（insertBefore=true 前，false 后）；不给则追加到末尾。
		 *   duplicate:{action:"duplicate", id, parentId?, offsetX?, offsetY?}
		 *            克隆 id 元素及其子树（全新 ID），挂到 parentId 下（缺省 = 源父级，
		 *            保持同父复制），顶层副本本地坐标偏移 (offsetX, offsetY)
		 *            （缺省 +20/+20）。成功后选中副本并回传 scene.picked。
		 *   instantiate:{action:"instantiate", data, parentId?, guid?}
		 *            按 prefab 数据（与 .space 同构的 {elements:[...]}，通常由 Studio 读取
		 *            .prefab 文件后下发）实例化一棵子树，挂到 parentId 下（缺省挂根节点）。
		 *            成功后选中实例顶层并回传 scene.picked。
		 *
		 * parentId=-1 表示创建为根元素。创建时仅附加 Transform（层级与变换），
		 * 不再默认附加 DisplayComponent——用户按需通过 Inspector "Add Component" 添加。
		 * 若指定 parentId 则挂为该元素的子级（worldPositionStays=false）。
		 */
		public final class HierarchyCommandHandler implements IMessageHandler
		{
			private var _world:World;
			private var _undoRedo:UndoRedoManager;
			private var _serializer:SceneSerializer;
			private var _selection:SceneSelection;

			public function HierarchyCommandHandler(world:World, renderEngine:RenderEngine = null,
				undoRedo:UndoRedoManager = null, serializer:SceneSerializer = null,
				selection:SceneSelection = null)
			{
				_world = world;
				_undoRedo = undoRedo;
				_serializer = serializer;
				_selection = selection;
			}

			public function get messageType():String
			{
				return MessageTypes.HierarchyCommand;
			}

			public function handle(message:Message, channel:IChannel):void
		{
			// 运行模式下也允许场景编辑命令：运行中的游戏实例可被编辑（Studio 与引擎两侧已放开门控）。
			var p:Object = message.payload;
			if (p == null) return;
			var action:String = p.action;
			var data:Object = p.data;
			// 修改类命令前先记录快照，供 Undo/Redo 恢复。
			switch (action)
			{
				case "create":
				case "destroy":
				case "rename":
				case "reparent":
				case "duplicate":
				case "instantiate":
					if (_undoRedo != null) _undoRedo.record();
					break;
			}
			switch (action)
			{
				case "create":
					handleCreate(data);
					break;
				case "destroy":
					handleDestroy(data);
					break;
				case "rename":
					handleRename(data);
					break;
				case "reparent":
					handleReparent(data);
					break;
				case "duplicate":
					handleDuplicate(data);
					break;
				case "instantiate":
					handleInstantiate(data);
					break;
			}
		}

			private function handleReparent(p:Object):void
			{
				var id:int = int(p.id);
				var parentId:int = p.parentId != null ? int(p.parentId) : -1;
				var hasSibling:Boolean = p.siblingId != null;
				var siblingId:int = hasSibling ? int(p.siblingId) : -1;
				var insertBefore:Boolean = p.insertBefore == true;

				var t:Transform = findTransform(id);
				if (t == null) return;
				var movedEl:* = t.owner;

				var newParent:Transform = null;
				if (parentId >= 0)
				{
					newParent = findTransform(parentId);
					if (newParent == null) return;
				}

				// 同父且无同级重排请求：无操作。
				if (newParent == t.parent && !hasSibling) return;

				// setParent 内部已做防环检查（isAncestorOf），worldPositionStays=false
				// 保留 local 变换，仅调整层级关系。setParent 会追加到 _children 末尾。
				t.setParent(newParent, false);

				// 同级插入：以 siblingId 元素为参照，重排 _elements 顺序，
				// 使 HierarchyBridge 快照中该元素出现在兄弟前/后。
				if (hasSibling && movedEl != null)
				{
					var sibEl:* = findElement(siblingId);
					if (sibEl != null && sibEl != movedEl)
					{
						_world.moveElementTo(movedEl, sibEl, insertBefore);
						return;
					}
				}

				_world.markHierarchyDirty();
			}

			private function handleCreate(p:Object):void
	{
		var name:String = p.name != null ? String(p.name) : null;
		var parentId:int = p.parentId != null ? int(p.parentId) : -1;
		var textureGuid:String = p.textureGuid != null ? String(p.textureGuid) : null;
		var components:Array = (p.components is Array) ? (p.components as Array) : null;
		executeCreate(name, parentId, textureGuid, components);
	}

	/**
	 * 创建一个新元素（Transform），可选挂为 parentId 的子级。
	 * parentId=-1 时：若根节点已存在（rootElementId >= 0），则挂为根节点的子级；
	 * 否则创建为根级元素（供 DreamEngine.ensureRoot 创建唯一根节点）。
	 * textureGuid 非空时附加 SpriteRenderer（带贴图）。
	 * components 非空时按短类名附加各组件（ComponentFactory.create；
	 * Transform 已附加跳过；未知/抽象组件返回 null 忽略）。
	 */
	public function executeCreate(name:String, parentId:int, textureGuid:String = null, components:Array = null):void
	{
		var e:* = _world.createElement();
		if (name != null && name.length > 0) e.name = name;
		var t:Transform = new Transform();
		e.addComponent(t);

		if (textureGuid != null && textureGuid.length > 0)
		{
			var sr:SpriteRenderer = new SpriteRenderer(textureGuid);
			e.addComponent(sr);
		}

		if (components != null)
		{
			for each (var compName:String in components)
			{
				if (compName == null || compName.length == 0) continue;
				if (compName == "Transform") continue;
				var c:DreamComponent = ComponentFactory.create(compName);
				if (c != null) e.addComponent(c);
			}
		}

		// 未指定父级且根节点已存在：统一挂到根节点下，保证单根场景结构。
		if (parentId < 0 && _world.rootElementId >= 0)
			parentId = _world.rootElementId;

		if (parentId >= 0)
		{
			var parent:Transform = findTransform(parentId);
			if (parent != null) t.setParent(parent, false);
		}
	}

		/**
		 * 复制元素：克隆 id 元素及其子树为全新元素，挂到 parentId 下
		 * （缺省 = 源元素当前父级，即同父复制）。顶层副本本地坐标偏移
		 * (offsetX, offsetY)（缺省 +20/+20）。成功后选中副本。
		 * 根元素不可复制（会脱离唯一根约束）。
		 */
		private function handleDuplicate(p:Object):void
		{
			var id:int = int(p.id);
			var parentId:int = p.parentId != null ? int(p.parentId) : -1;
			var offsetX:Number = p.offsetX != null ? Number(p.offsetX) : 20;
			var offsetY:Number = p.offsetY != null ? Number(p.offsetY) : 20;
			duplicateElement(id, parentId, offsetX, offsetY);
		}

		/**
		 * 复制指定元素为 parentId 的子级副本（缺省 = 源元素当前父级，即同父复制）。
		 * 顶层副本本地坐标偏移 (offsetX, offsetY)（缺省 +20/+20）。成功后选中副本。
		 * 根元素不可复制（会脱离唯一根约束）。返回新副本元素，失败返回 null。
		 * 命令消息与 Engine 端快捷键（Ctrl+C/V/D）共用。
		 */
		public function duplicateElement(id:int, parentId:int = -1,
			offsetX:Number = 20, offsetY:Number = 20):*
		{
			if (_serializer == null) return null;
			if (id == _world.rootElementId) return null;
			var src:* = findElement(id);
			if (src == null) return null;

			// 缺省父级 = 源元素当前父级（保持同父复制）。
			var srcT:Transform = src.getComponent(Transform) as Transform;
			if (parentId < 0 && srcT != null && srcT.parent != null && srcT.parent.owner != null)
				parentId = srcT.parent.owner.id;

			var targetParent:* = null;
			if (parentId >= 0)
			{
				targetParent = findElement(parentId);
				if (targetParent == null) return null;
			}

			var clone:* = _serializer.cloneSubtree(src, targetParent, offsetX, offsetY);
			if (clone != null && _selection != null)
				_selection.setSelection([clone.id], clone.id); // 触发 onSelectionChange → scene.picked
			return clone;
		}

		/**
		 * 按 prefab 数据实例化子树：data 为 {elements:[...]}（与 .space 同构），
		 * 挂到 parentId 下（缺省挂根节点）。成功后选中实例顶层。
		 * guid 记录在实例溯源里（见 SceneSerializer.instantiate），供后续 override 同步。
		 */
		private function handleInstantiate(p:Object):void
		{
			if (_serializer == null) return;
			var parentId:int = p.parentId != null ? int(p.parentId) : -1;
			var guid:String = p.guid != null ? String(p.guid) : null;
			var newId:int = _serializer.instantiate(p.data, parentId, guid);
			if (newId >= 0 && _selection != null)
				_selection.setSelection([newId], newId); // 触发 onSelectionChange → scene.picked
		}

			private function handleDestroy(p:Object):void
		{
			var id:int = int(p.id);
			// 根元素不可销毁：场景有且仅有一个根，保证 Hierarchy 永远非空。
			if (id == _world.rootElementId) return;
			var e:* = findElement(id);
			if (e != null) _world.destroyElement(e);
		}

			private function handleRename(p:Object):void
			{
				var id:int = int(p.id);
				var e:* = findElement(id);
				if (e != null)
				{
					e.name = String(p.name);
					// prefab 实例：改名记为 override，源 prefab 改名不会覆盖实例上的自定义名字。
					if (_serializer != null) _serializer.recordOverride(id, "name");
					_world.markHierarchyDirty();
				}
			}

			/** 按 ID 查找存活元素。 */
			private function findElement(id:int):*
			{
				for each (var e:* in _world.elements)
				{
					if (e.alive && e.id == id) return e;
				}
				return null;
			}

			/** 按 ID 查找元素的 Transform 组件。 */
			private function findTransform(id:int):Transform
			{
				var e:* = findElement(id);
				if (e == null) return null;
				return e.getComponent(Transform) as Transform;
			}
		}
	}
}

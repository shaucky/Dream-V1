package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.Element;
		import dream.engine.ecs.World;
		import dream.engine.scene.SceneSerializer;
		import dream.engine.transform.Transform;

		/**
		 * Hierarchy 桥接器：检测 World.hierarchyDirty，脏时发送一次快照到 Studio。
		 *
		 * 由 DreamEngine 在 STUDIO 模式下创建，每帧调用 update(dt)。
		 * 元素增删/重命名时 World.hierarchyDirty 置脏，本桥接器检测到脏标记后
		 * 立即序列化元素树发送到 Studio 并清脏。
		 *
		 * 快照格式（JSON）：
		 *   { "nodes": [
		 *     { "id": 0, "name": "Element_0", "parentId": -1, "enabled": true },
		 *     { "id": 1, "name": "Coin", "parentId": 0, "enabled": true,
		 *       "prefabGuid": "<GUID>", "prefabRoot": true },
		 *     ...
		 *   ]}
		 *
		 * parentId：通过 Transform.parent?.owner?.id 获取。无 Transform 或无父级时为 -1。
		 * prefabGuid / prefabRoot：仅 prefab 实例元素才写（见 SceneSerializer.describePrefab），
		 * Studio 面板据此显示实例图标与"Revert to Prefab"入口。
		 */
		public final class HierarchyBridge
		{
			private var _world:World;
			private var _channel:IChannel;

			public function HierarchyBridge(world:World)
			{
				_world = world;
			}

			/** 绑定通信通道。channel 为 null 时停止发送。 */
			public function attach(channel:IChannel):void
			{
				_channel = channel;
			}

			/** 每帧驱动，由 DreamEngine 调用。脏时发送快照。 */
			public function update(dt:Number):void
			{
				if (_channel == null || !_channel.connected) return;
				if (!_world.hierarchyDirty) return;
				_world.hierarchyDirty = false;
				sendSnapshot();
			}

			/** 通道就绪后立即发送一次初始快照。 */
			public function sendInitial():void
			{
				if (_channel == null || !_channel.connected) return;
				sendSnapshot();
			}

			private function sendSnapshot():void
			{
				var nodes:Array = [];
				for each (var e:Element in _world.elements)
				{
					if (!e.alive) continue;
					nodes.push(serializeElement(e));
				}
				_channel.send(new Message(MessageTypes.HierarchySnapshot, {nodes: nodes}));
			}

			private static function serializeElement(e:Element):Object
			{
				var parentId:int = -1;
				var t:Transform = e.getComponent(Transform) as Transform;
				if (t != null && t.parent != null && t.parent.owner != null)
					parentId = t.parent.owner.id;

				var node:Object = {
					id: e.id,
					name: e.name,
					parentId: parentId,
					enabled: e.enabled
				};

				// prefab 实例元素才带实例标识：非实例元素不写这两个键，Studio 侧取默认值。
				var prefab:Object = SceneSerializer.current != null
					? SceneSerializer.current.describePrefab(e) : null;
				if (prefab != null)
				{
					node.prefabGuid = prefab.guid;
					node.prefabRoot = prefab.isRoot;
				}
				return node;
			}
		}
	}
}

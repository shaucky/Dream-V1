package dream.engine.comm
{
	CONFIG::STUDIO
	{
		import dream.engine.ecs.DreamComponent;
		import dream.engine.ecs.Element;
		import dream.engine.ecs.FieldInfo;
		import dream.engine.ecs.World;
		import dream.engine.scene.SceneSelection;
		import dream.engine.scene.SceneSerializer;
		import dream.engine.transform.Transform;
		import dream.engine.ui.Canvas;

		import flash.utils.Dictionary;
		import flash.utils.getQualifiedClassName;

		/**
		 * Inspector 共享工具：序列化元素字段快照、按短类名查找组件。
		 * 供 InspectorRequestHandler / InspectorEditHandler 以及引擎侧
		 * 主动推送（如 Gizmo 拖拽实时刷新）共用。
		 *
		 * prefab 实例元素另带两组信息（见 SceneSerializer.describePrefab）：
		 *   - 元素级 prefabGuid / prefabRoot：面板据此显示 "Prefab: 文件名" 与还原入口；
		 *   - 字段级 overridden：该字段是否被实例 override（面板加粗标记）；
		 *     元素级的 name / enabled 也各有 nameOverridden / enabledOverridden。
		 * 非实例元素不写这些键，Studio 侧取默认值（无标记）。
		 */
		public final class InspectorShared
		{
			/** 序列化指定元素的所有组件字段为快照并发送到 Studio。 */
			public static function sendSnapshot(world:World, elementId:int, channel:IChannel):void
			{
				var e:Element = findElement(world, elementId);
				if (e == null)
				{
					channel.send(new Message(MessageTypes.InspectorSnapshot, {elementId: elementId, elementName: "", enabled: true, ancestors: [], components: []}));
					return;
				}

				// prefab 溯源：仅实例元素非 null。overrides 里的键与编辑器各处记录的一致
				// （"Component.field" / "Component" / "!Component" / "name" / "enabled"）。
				var prefab:Object = SceneSerializer.current != null
					? SceneSerializer.current.describePrefab(e) : null;
				var overrides:Dictionary = prefab != null ? prefab.overrides as Dictionary : null;

				var comps:Array = [];
				// 祖先链（根 → 直接父级，不含自身）：Clip 录制按子树归属判断继续/终止，
				// 并据此计算录制目标子树的路径（目标元素名链）。
				var ancestors:Array = [];
				var pt:Transform = e.getComponent(Transform) as Transform;
				if (pt != null) pt = pt.parent;
				while (pt != null && pt.owner != null)
				{
					ancestors.unshift({ id: pt.owner.id, name: pt.owner.name });
					pt = pt.parent;
				}
				// Canvas 后代的元素：UI 布局由 RectTransform 驱动，Transform 无意义 → 快照隐藏。
				var hideTransform:Boolean = isCanvasDescendant(e);
				for each (var c:DreamComponent in e.getComponents())
				{
					var compName:String = shortClassName(c);
					if (hideTransform && compName == "Transform") continue;
					var fields:Array = c.getInspectableFields();
					var fieldObjs:Array = [];
					for each (var f:FieldInfo in fields)
					{
						fieldObjs.push({
							name: f.name,
							label: f.label,
							type: f.type,
							value: f.value,
							min: f.min,
							max: f.max,
							step: f.step,
							readonly: f.readonly,
							options: f.options,
							overridden: overrides != null && overrides[compName + "." + f.name] == true
						});
					}
					comps.push({name: compName, enabled: c.enabled, fields: fieldObjs});
				}

				var payload:Object = {
					elementId: elementId,
					elementName: e.name,
					enabled: e.enabled,
					ancestors: ancestors,
					components: comps
				};
				if (prefab != null)
				{
					payload.prefabInstanceId = prefab.instanceId;
					payload.prefabGuid = prefab.guid;
					payload.prefabRoot = prefab.isRoot;
					payload.nameOverridden = overrides["name"] == true;
					payload.enabledOverridden = overrides["enabled"] == true;
				}
				channel.send(new Message(MessageTypes.InspectorSnapshot, payload));
			}

			/**
		 * 当前选中元素仍在时补发一次 Inspector 快照。
		 * 撤销/重做是"反序列化旧快照"——整个世界被重建、字段值整片回退，而 Inspector
		 * 没有自己的刷新时机，不补发就会一直停留在变更前的值（Hierarchy 靠脏标记自行刷新）。
		 */
		public static function sendSelectionSnapshot(world:World, selection:SceneSelection, channel:IChannel):void
		{
			if (world == null || selection == null) return;
			var id:int = selection.selectedElementId;
			if (id >= 0 && selection.contains(id)) sendSnapshot(world, id, channel);
		}

		/** 按 ID 查找存活元素。 */
		public static function findElement(world:World, id:int):Element
			{
				for each (var e:Element in world.elements)
				{
					if (e.alive && e.id == id) return e;
				}
				return null;
			}

			/** 按短类名查找组件。 */
			public static function findComponent(e:Element, shortName:String):DreamComponent
			{
				for each (var c:DreamComponent in e.getComponents())
				{
					if (shortClassName(c) == shortName) return c;
				}
				return null;
			}

			/** 元素是否为某 Canvas 元素的后代（沿 Transform 父链查找 Canvas 组件，不含自身）。 */
			private static function isCanvasDescendant(e:Element):Boolean
			{
				var t:Transform = e.getComponent(Transform) as Transform;
				if (t != null) t = t.parent; // 从父级起查：自身带 Canvas 不算"后代"
				while (t != null)
				{
					if (t.owner != null && t.owner.getComponent(Canvas) != null) return true;
					t = t.parent;
				}
				return false;
			}

			/** 获取类的短名（去包名）。
		 *  AS3 getQualifiedClassName 返回形如 "pkg.sub::Class"（包与类用 "::" 分隔），
		 *  取最后一个 "::" 或 "." 之后的部分。 */
		public static function shortClassName(obj:Object):String
		{
			var full:String = getQualifiedClassName(obj);
			var colonIdx:int = full.lastIndexOf("::");
			var dotIdx:int = full.lastIndexOf(".");
			if (colonIdx >= 0)
				return full.substr(colonIdx + 2); // 跳过 "::" 两字符
			if (dotIdx >= 0)
				return full.substr(dotIdx + 1);
			return full;
		}
		}
	}
}

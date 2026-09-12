package dream.engine.comm
{
	CONFIG::STUDIO
	{
		/** 约定消息类型常量，AS3 与 C# 两侧必须保持一致。 */
		public final class MessageTypes
		{
			public static const Ping:String = "ping";   // Studio → Engine：存活探测
			public static const Pong:String = "pong";   // Engine → Studio：存活响应
			public static const Ready:String = "ready"; // Engine → Studio：通信就绪
			public static const Log:String = "log";     // Engine → Studio：日志上报

			// Hierarchy：元素树同步与操作命令
			public static const HierarchySnapshot:String = "hierarchy.snapshot"; // Engine → Studio：元素树快照
			public static const HierarchyCommand:String  = "hierarchy.command";  // Studio → Engine：操作命令（create/destroy/rename）

			// Inspector：选中元素字段查看与编辑
			public static const InspectorRequest:String  = "inspector.request";  // Studio → Engine：请求元素字段快照
			public static const InspectorSnapshot:String = "inspector.snapshot"; // Engine → Studio：返回元素字段快照
			public static const InspectorEdit:String     = "inspector.edit";     // Studio → Engine：编辑字段值

			// Inspector：Add Component
		public static const InspectorListComponents:String = "inspector.listComponents"; // Studio → Engine：请求可添加组件列表
		public static const InspectorComponentList:String  = "inspector.componentList";  // Engine → Studio：返回可添加组件列表
		public static const InspectorAddComponent:String   = "inspector.addComponent";   // Studio → Engine：添加组件到元素
		public static const InspectorRemoveComponent:String = "inspector.removeComponent"; // Studio → Engine：从元素移除组件
		public static const InspectorPasteComponent:String = "inspector.pasteComponent"; // Studio → Engine：粘贴组件（create=true 新建并赋值 / create=false 仅赋值同类型已有组件）
			public static const InspectorSetEnabled:String     = "inspector.setEnabled";     // Studio → Engine：设置元素/组件启用（payload: elementId, component?, enabled）

			// 引擎运行控制
			public static const EngineSetRunning:String = "engine.setRunning"; // Studio → Engine：设置 world.running（暂停/继续逻辑刷新）

			// 场景存档
		public static const SceneSave:String   = "scene.save";   // Studio → Engine：保存场景到指定路径
		public static const SceneLoad:String   = "scene.load";   // Studio → Engine：从指定路径加载场景
		public static const SceneResult:String = "scene.result"; // Engine → Studio：保存/加载结果

		// 场景撤销/重做
		public static const SceneUndo:String = "scene.undo"; // Studio → Engine：撤销一步
		public static const SceneRedo:String = "scene.redo"; // Studio → Engine：重做一步

		// Prefab：把选中子树导出为 prefab 文件内容（格式与 .space 同构，见 SceneSerializer）
		public static const PrefabExport:String       = "prefab.export";       // Studio → Engine：导出子树（payload: {id}）
		public static const PrefabExportResult:String = "prefab.exportResult"; // Engine → Studio：导出结果（payload: {success, data}）
		// Prefab：源内容索引（guid → .prefab 文件内容），供引擎把源改动同步到场景实例
		public static const PrefabSources:String      = "prefab.sources";      // Studio → Engine：源内容（payload: {entries:[{guid, data}]}）
		// Prefab：把实例还原到源（清空该实例的 override，随后由 Studio 重推源同步）
		public static const PrefabRevert:String        = "prefab.revert";       // Studio → Engine：还原实例（payload: {elementId}）

		// 文档导航：引擎左上角返回按钮点击 → 请求 Studio 切换回上一个文档
		public static const DocumentBack:String        = "document.back";       // Engine → Studio：返回上一个文档（无 payload）

		// 资源索引：Studio 推送 GUID → 路径映射，供 ResourceManager 解析 GUID。
			public static const ResourceIndex:String = "resource.index"; // Studio → Engine：GUID→路径索引（含精灵子资源条目）

		// 图集映射：Studio 推送「精灵 GUID → 图集纹理 + 图集内矩形」覆盖表。
		// 图集是可选优化层，未命中覆盖的精灵回落到自身源纹理。
			public static const SpriteAtlasMap:String = "sprite.atlasMap"; // Studio → Engine：精灵的图集覆盖映射

		// 场景选中：视口拾取与高亮同步
			public static const SceneSelect:String = "scene.select"; // Studio → Engine：设置编辑器选中元素（-1 清除）
			public static const ScenePicked:String = "scene.picked"; // Engine → Studio：视口点击拾取结果（elementId，-1 表示空白）

		// 场景编辑通知
			public static const SceneModified:String = "scene.modified"; // Engine → Studio：场景已被修改（Gizmo 拖拽等，Studio 据此置脏）

			// 编辑器快捷键：Engine 窗口聚焦时请求 Studio 执行（保存依赖 Studio 文件对话框）。
			public static const EngineShortcut:String = "engine.shortcut"; // Engine → Studio：请求执行编辑器命令（payload.action: "save"/"saveAs"）

		// Clip 动画预览：Studio → Engine 下发采样值驱动场景元素（不撤销、不快照）。
			public static const ClipPreviewApply:String = "clip.previewApply";

			// 音频：Studio → Engine 音量设置。
			public static const AudioSetVolume:String    = "audio.setVolume";    // Studio → Engine：设置主/组音量（payload: master 或 group+volume）
		}
	}
}

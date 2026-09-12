using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dream.Studio.Engine.Comm
{
    /// <summary>
    /// 消息信封：所有跨进程消息的统一外壳。
    /// 帧格式（传输层）：4 字节大端长度前缀 + UTF-8 JSON 内容（与本文件无关，由通道实现）。
    /// </summary>
    internal sealed record Message
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }

        public static Message Create<T>(string type, T payload)
            => new() { Type = type, Payload = JsonSerializer.SerializeToElement(payload) };
    }

    /// <summary>约定消息类型常量，C# 与 AS3 两侧必须保持一致。</summary>
    internal static class MessageTypes
    {
        public const string Ping = "ping";   // Studio → Engine：存活探测
        public const string Pong = "pong";   // Engine → Studio：存活响应
        public const string Ready = "ready"; // Engine → Studio：通信就绪
        public const string Log = "log";     // Engine → Studio：日志上报

        // Hierarchy：元素树同步与操作命令
        public const string HierarchySnapshot = "hierarchy.snapshot"; // Engine → Studio：元素树快照
        public const string HierarchyCommand  = "hierarchy.command";  // Studio → Engine：操作命令（create/destroy/rename）

        // Inspector：选中元素字段查看与编辑
        public const string InspectorRequest  = "inspector.request";  // Studio → Engine：请求元素字段快照
        public const string InspectorSnapshot = "inspector.snapshot"; // Engine → Studio：返回元素字段快照
        public const string InspectorEdit     = "inspector.edit";     // Studio → Engine：编辑字段值

        // Inspector：Add Component
        public const string InspectorListComponents = "inspector.listComponents"; // Studio → Engine：请求可添加组件列表
        public const string InspectorComponentList  = "inspector.componentList";  // Engine → Studio：返回可添加组件列表
        public const string InspectorAddComponent   = "inspector.addComponent";   // Studio → Engine：添加组件到元素
        public const string InspectorRemoveComponent = "inspector.removeComponent"; // Studio → Engine：从元素移除组件
        public const string InspectorPasteComponent = "inspector.pasteComponent"; // Studio → Engine：粘贴组件（create=true 新建并赋值 / create=false 仅赋值同类型已有组件）
        public const string InspectorSetEnabled     = "inspector.setEnabled";     // Studio → Engine：设置元素/组件启用（payload: elementId, component?, enabled）

        // 引擎运行控制
        public const string EngineSetRunning = "engine.setRunning"; // Studio → Engine：设置 world.running（暂停/继续逻辑刷新）

        // 场景存档
        public const string SceneSave   = "scene.save";   // Studio → Engine：保存场景到指定路径
        public const string SceneLoad   = "scene.load";   // Studio → Engine：从指定路径加载场景
        public const string SceneResult = "scene.result"; // Engine → Studio：保存/加载结果

        // 场景撤销/重做
        public const string SceneUndo = "scene.undo"; // Studio → Engine：撤销一步
        public const string SceneRedo = "scene.redo"; // Studio → Engine：重做一步

        // Prefab：把选中子树导出为 prefab 文件内容（格式与 .space 同构，见 SceneSerializer）
        public const string PrefabExport       = "prefab.export";       // Studio → Engine：导出子树（payload: {id}）
        public const string PrefabExportResult = "prefab.exportResult"; // Engine → Studio：导出结果（payload: {success, data}）
        // Prefab：源内容索引（guid → .prefab 文件内容），供引擎把源改动同步到场景实例
        public const string PrefabSources      = "prefab.sources";      // Studio → Engine：源内容（payload: {entries:[{guid, data}]}）
        // Prefab：把实例还原到源（清空该实例的 override，随后由 Studio 重推源同步）
        public const string PrefabRevert       = "prefab.revert";       // Studio → Engine：还原实例（payload: {elementId}）

        // 文档导航：引擎左上角返回按钮点击 → 请求 Studio 切换回上一个文档
        public const string DocumentBack       = "document.back";       // Engine → Studio：返回上一个文档（无 payload）

        // 资源索引：Studio 推送 GUID → 路径映射，供 ResourceManager 解析 GUID。
        public const string ResourceIndex = "resource.index"; // Studio → Engine：GUID→路径索引

        // 图集覆盖映射：Studio 推送「精灵 GUID → 图集纹理 + 矩形」，供 ResourceManager
        // 在解析精灵时透明改用图集（可选优化层，不推则回落精灵自身的源纹理）。
        public const string SpriteAtlasMap = "sprite.atlasMap"; // Studio → Engine：精灵→图集矩形映射

        // 场景选中：视口拾取与高亮同步
        public const string SceneSelect = "scene.select";   // Studio → Engine：设置编辑器选中元素（-1 清除）
        public const string ScenePicked = "scene.picked";   // Engine → Studio：视口点击拾取结果（elementId，-1 表示空白）

        // 场景编辑通知
        public const string SceneModified = "scene.modified"; // Engine → Studio：场景已被修改（Gizmo 拖拽等，Studio 据此置脏）

        // 编辑器快捷键：Engine 窗口聚焦时请求 Studio 执行（保存依赖 Studio 文件对话框）。
        public const string EngineShortcut = "engine.shortcut"; // Engine → Studio：请求执行编辑器命令（payload.action: "save"/"saveAs"）

        // Clip 动画预览：Studio → Engine 下发采样值驱动场景元素（不撤销、不快照）。
        public const string ClipPreviewApply = "clip.previewApply";

        // 音频：Studio → Engine 音量设置。
        public const string AudioSetVolume   = "audio.setVolume";     // Studio → Engine：设置主/组音量（payload: master 或 group+volume）
    }
}

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Dream.Studio.Panels.Hierarchy
{
    /// <summary>
    /// 从引擎 JSON 快照反序列化的元素数据。
    /// 格式与引擎端 HierarchyBridge.serializeElement 对应（camelCase）。
    /// </summary>
    internal sealed class HierarchyNodeData
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("parentId")] public int ParentId { get; set; } = -1;
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("components")] public List<string> Components { get; set; } = new();

        /// <summary>预制体实例的源资产 GUID；非实例元素为空（引擎不写该键）。
        /// 面板据此显示实例图标与 "Revert to Prefab" 入口。</summary>
        [JsonPropertyName("prefabGuid")] public string? PrefabGuid { get; set; }

        /// <summary>是否为该实例的根元素（实例的摆放节点）。配合 PrefabGuid 使用。</summary>
        [JsonPropertyName("prefabRoot")] public bool PrefabRoot { get; set; }
    }

    /// <summary>
    /// Hierarchy 树节点：包装 <see cref="HierarchyNodeData"/>，附加 UI 状态
    /// （子节点列表、缩进层级、展开状态）。
    /// </summary>
    internal sealed class HierarchyNode
    {
        public HierarchyNodeData Data { get; }
        public List<HierarchyNode> Children { get; } = new();
        public int IndentLevel { get; set; }
        public bool IsExpanded { get; set; }

        public HierarchyNode(HierarchyNodeData data)
        {
            Data = data;
        }

        public bool HasChildren => Children.Count > 0;
    }
}

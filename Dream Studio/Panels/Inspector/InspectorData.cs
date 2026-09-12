using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dream.Studio.Panels.Inspector
{
    /// <summary>
    /// 从引擎 JSON 反序列化的 Inspector 字段元数据。
    /// 格式与引擎端 FieldInfo 对应（camelCase）。
    /// </summary>
    internal sealed class InspectorFieldData
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("value")] public JsonElement? Value { get; set; }
        [JsonPropertyName("min")] public double? Min { get; set; }
        [JsonPropertyName("max")] public double? Max { get; set; }
        [JsonPropertyName("step")] public double? Step { get; set; }
        [JsonPropertyName("readonly")] public bool ReadOnly { get; set; }
        /// <summary>受控取值列表（string 字段下拉用）。</summary>
        [JsonPropertyName("options")] public List<string>? Options { get; set; }
        /// <summary>该字段是否被覆盖（prefab 实例上用户改过）：true 时字段行显示覆盖标记，
        /// 且源 prefab 的改动不会传播到这里。非实例元素恒为 false。</summary>
        [JsonPropertyName("overridden")] public bool Overridden { get; set; }
    }

    /// <summary>组件分组：组件名 + 是否启用 + 字段列表。</summary>
    internal sealed class InspectorComponentData
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("fields")] public List<InspectorFieldData> Fields { get; set; } = new();
    }

    /// <summary>元素 ID + 名称（祖先链 / 目标解析用）。</summary>
    internal sealed class ElementRefData
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    /// <summary>Inspector 快照：元素 ID + 元素名 + 是否启用 + 祖先链 + 组件列表。</summary>
    internal sealed class InspectorSnapshotData
    {
        [JsonPropertyName("elementId")] public int ElementId { get; set; }
        [JsonPropertyName("elementName")] public string ElementName { get; set; } = "";
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        /// <summary>祖先链（根 → 直接父级，不含自身）：Clip 录制判断子树归属并计算目标路径。</summary>
        [JsonPropertyName("ancestors")] public List<ElementRefData> Ancestors { get; set; } = new();
        [JsonPropertyName("components")] public List<InspectorComponentData> Components { get; set; } = new();

        // ── 预制体溯源：仅实例元素才有（引擎不写这些键时全部取默认值）──

        /// <summary>实例 ID（同一实例的所有元素共享）。-1 = 非实例元素。</summary>
        [JsonPropertyName("prefabInstanceId")] public int PrefabInstanceId { get; set; } = -1;

        /// <summary>源资产 GUID。空 = 非实例元素。</summary>
        [JsonPropertyName("prefabGuid")] public string? PrefabGuid { get; set; }

        /// <summary>是否为该实例的根元素（摆放节点）。</summary>
        [JsonPropertyName("prefabRoot")] public bool PrefabRoot { get; set; }

        /// <summary>元素名是否被覆盖（用户重命名过实例中的元素）。</summary>
        [JsonPropertyName("nameOverridden")] public bool NameOverridden { get; set; }

        /// <summary>元素启用状态是否被覆盖。</summary>
        [JsonPropertyName("enabledOverridden")] public bool EnabledOverridden { get; set; }

        /// <summary>是否为预制体实例元素（面板据此显示 Prefab 区块）。</summary>
        public bool IsPrefabInstance => !string.IsNullOrEmpty(PrefabGuid);
    }
}

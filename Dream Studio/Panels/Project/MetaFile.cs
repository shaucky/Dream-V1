using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 资源元数据文件模型。每个 source-path 下的文件都伴随一个同名 .meta 文件，
    /// 用于存储稳定 GUID、资源类型等信息，使资源引用与文件路径解耦。
    /// </summary>
    internal sealed class MetaFile
    {
        /// <summary>全局唯一标识符，资源引用的目标。</summary>
        [JsonPropertyName("guid")]
        public string Guid { get; set; } = "";

        /// <summary>资源类型，如 "texture", "script", "scene" 等。</summary>
        [JsonPropertyName("assetType")]
        public string AssetType { get; set; } = "unknown";

        /// <summary>音频时长（秒）。非音频资源为 0。</summary>
        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }

        /// <summary>采样率（Hz）。非音频资源为 0。</summary>
        [JsonPropertyName("sampleRate")]
        public int SampleRate { get; set; }

        /// <summary>声道数。非音频资源为 0。</summary>
        [JsonPropertyName("channels")]
        public int Channels { get; set; }

        /// <summary>时长是否为估算值（mp3 按 CBR 帧头估算；wav 为精确值）。</summary>
        [JsonPropertyName("estimatedDuration")]
        public bool EstimatedDuration { get; set; }

        /// <summary>序列化为 JSON。</summary>
        public string ToJson() => JsonSerializer.Serialize(this, MetaJsonContext.Default.MetaFile);

        /// <summary>从 JSON 反序列化。</summary>
        public static MetaFile FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new MetaFile();
            return JsonSerializer.Deserialize(json, MetaJsonContext.Default.MetaFile) ?? new MetaFile();
        }

        /// <summary>获取文件对应的 .meta 路径。</summary>
        public static string GetMetaPath(string filePath) => filePath + ".meta";

        /// <summary>生成新的 GUID。</summary>
        public static string GenerateGuid() => System.Guid.NewGuid().ToString("N");
    }

    [JsonSerializable(typeof(MetaFile))]
    internal sealed partial class MetaJsonContext : JsonSerializerContext { }
}

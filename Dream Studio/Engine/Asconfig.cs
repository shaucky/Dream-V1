using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dream.Studio.Engine
{
    /// <summary>asconfig.json 的最小子集模型，仅取引擎集成所需字段。</summary>
    internal sealed class Asconfig
    {
        [JsonPropertyName("config")] public string? Config { get; set; }

        [JsonPropertyName("application")] public string? Application { get; set; }

        [JsonPropertyName("mainClass")] public string? MainClass { get; set; }

        [JsonPropertyName("compilerOptions")]
        public AsconfigCompilerOptions? CompilerOptions { get; set; }

        public static Asconfig Load(string file)
        {
            using var stream = File.OpenRead(file);
            return JsonSerializer.Deserialize<Asconfig>(stream) ?? new Asconfig();
        }
    }

    internal sealed class AsconfigCompilerOptions
    {
        [JsonPropertyName("source-path")] public List<string>? SourcePath { get; set; }

        [JsonPropertyName("library-path")] public List<string>? LibraryPath { get; set; }

        [JsonPropertyName("output")] public string? Output { get; set; }

        /// <summary>条件编译常量定义：{ "name": "CONFIG::STUDIO", "value": true }。</summary>
        [JsonPropertyName("define")] public List<AsconfigDefine>? Define { get; set; }
    }

    internal sealed class AsconfigDefine
    {
        [JsonPropertyName("name")] public string? Name { get; set; }

        /// <summary>原始 JSON 值，由编译器按 ValueKind 转 mxmlc 命令行字面量。</summary>
        [JsonPropertyName("value")] public JsonElement Value { get; set; }
    }
}

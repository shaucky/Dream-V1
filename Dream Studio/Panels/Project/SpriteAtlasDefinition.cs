using System;
using System.IO;
using System.Text.Json;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 图集定义（.dmatlas）：声明「哪些精灵合到一张图集」以及合图参数。
    ///
    /// 对标 Unity 的 SpriteAtlas 资源：**项目里只有这份定义**，不含图集图像、
    /// 不含纹理 GUID、不含矩形映射——因此它本身无法被当成资源引用。
    /// 合图结果落在项目外的缓存目录（见 <see cref="SpriteAtlasCache"/>），
    /// 资源目录里永远看不到图集 PNG。
    /// </summary>
    internal sealed class SpriteAtlasDefinition
    {
        public const string Extension = ".dmatlas";

        /// <summary>图集名（展示与缓存命名用）。</summary>
        public string Name = "";

        /// <summary>打包范围：相对本定义文件所在目录的文件夹路径（"." 即同目录）。</summary>
        public string Folder = ".";

        /// <summary>精灵间间隔（像素），避免相邻精灵在双线性采样时相互渗色。</summary>
        public int Padding = 2;

        /// <summary>图集边长上限（2 的幂）。</summary>
        public int MaxSize = 4096;

        /// <summary>读取定义；文件缺失或解析失败返回 null。</summary>
        public static SpriteAtlasDefinition? Read(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var def = new SpriteAtlasDefinition
                {
                    Name = ReadString(root, "name"),
                    Folder = ReadString(root, "folder"),
                    Padding = ReadInt(root, "padding", 2),
                    MaxSize = ReadInt(root, "maxSize", 4096),
                };
                if (def.Name.Length == 0) def.Name = Path.GetFileNameWithoutExtension(path);
                if (def.Folder.Length == 0) def.Folder = ".";
                return def;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写出定义（字段名与引擎/缓存读取约定一致）。</summary>
        public static void Write(string path, SpriteAtlasDefinition def)
        {
            var payload = new
            {
                name = def.Name,
                folder = def.Folder,
                padding = def.Padding,
                maxSize = def.MaxSize,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>解析打包范围的绝对路径；目录不存在返回 null。</summary>
        public string? ResolveFolder(string definitionPath)
        {
            var baseDir = Path.GetDirectoryName(definitionPath);
            if (string.IsNullOrEmpty(baseDir)) return null;
            try
            {
                var abs = Path.GetFullPath(Path.Combine(baseDir, Folder));
                return Directory.Exists(abs) ? abs : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static int ReadInt(JsonElement obj, string name, int fallback)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var i) ? i : fallback;
    }
}

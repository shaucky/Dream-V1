using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 精灵表（.dmsheet）中的一个精灵：GUID 是组件引用的主键，矩形以源纹理像素坐标表达。
    /// </summary>
    internal sealed class SpriteEntry
    {
        public string Guid = "";
        public string Name = "";
        public int X, Y, W, H;

        public SliceRect Rect => new(X, Y, W, H);
    }

    /// <summary>
    /// 精灵表文件（.dmsheet）：一张源纹理 + 从它切出的一组命名精灵。
    ///
    /// 精灵是组件引用的**主单位**，与图集打包无关——切分完即可用。
    /// "slice" 字段记录切分参数（模式 + 该模式的参数），使重新切分可还原设置，
    /// 新增切分方式时也无需改动文件结构（引擎侧忽略该字段）。
    /// </summary>
    internal sealed class SpriteSheetFile
    {
        public const string Extension = ".dmsheet";

        public string Name = "";
        public string TextureGuid = "";
        public SliceSettings Slice = new();
        public List<SpriteEntry> Sprites = new();

        /// <summary>读取 .dmsheet；文件缺失或解析失败返回 null。</summary>
        public static SpriteSheetFile? Read(string path)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var sheet = new SpriteSheetFile
                {
                    Name = ReadString(root, "name"),
                    TextureGuid = ReadString(root, "textureGuid"),
                };

                if (root.TryGetProperty("slice", out var slice) && slice.ValueKind == JsonValueKind.Object)
                {
                    sheet.Slice = new SliceSettings
                    {
                        Mode = ReadString(slice, "mode") is { Length: > 0 } m ? m : SliceSettings.GridMode,
                        Rows = ReadInt(slice, "rows", 1),
                        Columns = ReadInt(slice, "columns", 1),
                    };
                }

                if (root.TryGetProperty("sprites", out var sprites) && sprites.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sprites.EnumerateArray())
                    {
                        if (s.ValueKind != JsonValueKind.Object) continue;
                        var guid = ReadString(s, "guid");
                        if (guid.Length == 0) continue;
                        sheet.Sprites.Add(new SpriteEntry
                        {
                            Guid = guid,
                            Name = ReadString(s, "name"),
                            X = ReadInt(s, "x", 0),
                            Y = ReadInt(s, "y", 0),
                            W = ReadInt(s, "w", 0),
                            H = ReadInt(s, "h", 0),
                        });
                    }
                }
                return sheet;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写出 .dmsheet。字段名与引擎 SpriteSheet.fromJson 的约定一致。</summary>
        public static void Write(string path, string name, string textureGuid,
                                 SliceSettings slice, IReadOnlyList<SpriteEntry> sprites)
        {
            var payload = new
            {
                name,
                textureGuid,
                slice = new { mode = slice.Mode, rows = slice.Rows, columns = slice.Columns },
                sprites = BuildSpriteArray(sprites),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        private static object[] BuildSpriteArray(IReadOnlyList<SpriteEntry> sprites)
        {
            var array = new object[sprites.Count];
            for (int i = 0; i < sprites.Count; i++)
            {
                var s = sprites[i];
                array[i] = new { guid = s.Guid, name = s.Name, x = s.X, y = s.Y, w = s.W, h = s.H };
            }
            return array;
        }

        private static string ReadString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static int ReadInt(JsonElement obj, string name, int fallback)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var i) ? i : fallback;
    }

    /// <summary>图集中一条覆盖映射：某精灵在缓存图集纹理内的矩形。</summary>
    internal sealed class AtlasEntryRecord
    {
        public string SpriteGuid = "";
        public int X, Y, W, H;
    }

    /// <summary>推送给引擎的图集覆盖映射项；每条自带图集纹理 GUID，便于聚合多个图集。</summary>
    internal readonly record struct AtlasMapEntry(string SpriteGuid, string TextureGuid, int X, int Y, int W, int H);

    /// <summary>
    /// 推送给引擎的资源索引条目。Sprite 非空表示这是精灵子资源：GUID 指向精灵本身，
    /// Path 指向承载它的 .dmsheet（而非可直接加载的图片）。
    /// </summary>
    internal readonly record struct ResourceIndexEntry(string Guid, string Path, string? Sprite);
}

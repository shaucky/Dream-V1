using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Dream.Studio.Engine;

namespace Dream.Studio.Panels.Project
{
    /// <summary>缓存中一份可用的图集：图集纹理 + 「精灵 → 图集矩形」映射。</summary>
    internal sealed class CachedAtlas
    {
        public string Name = "";
        public string TextureGuid = "";
        public string TexturePath = "";
        public List<AtlasMapEntry> Entries = new();
    }

    /// <summary>
    /// 图集合图缓存：把 .dmatlas 定义的精灵合成图集，产物写在项目的 .dream/atlas 目录下。
    ///
    /// 对标 Godot 的 .godot/——派生数据不是资源：资源目录里没有图集图像，但编辑期与运行
    /// 都能用它，从而拿到合批渲染的收益。图集只是透明覆盖层：缓存缺失或合图失败时，
    /// 引擎回落精灵自身的源纹理，场景引用不受影响。
    ///
    /// 失效策略：对「定义参数 + 参与精灵（GUID/矩形/源纹理路径/大小/修改时间）」算签名，
    /// 签名一致时直接复用已有产物，避免每次推送资源状态都重新合图。
    /// 产物文件名带上签名（&lt;图集名&gt;-&lt;定义摘要&gt;-&lt;签名&gt;.png）：内容一变就是新路径，
    /// 引擎按路径缓存的纹理不会命中旧图集，热切换才能真的换图。
    /// </summary>
    internal sealed class SpriteAtlasCache
    {
        /// <summary>
        /// 同步全部图集定义：按需重新合图，返回本次可用的图集。
        /// 失败原因追加到 <paramref name="errors"/>（不抛异常，单个定义失败不影响其它定义）。
        /// </summary>
        public IReadOnlyList<CachedAtlas> Sync(string projectRoot,
                                               IReadOnlyList<string> definitionPaths,
                                               MetaDatabase metaDb,
                                               ICollection<string> errors)
        {
            var result = new List<CachedAtlas>();
            if (definitionPaths.Count == 0) return result;

            var cacheDir = Path.Combine(projectRoot, EnginePaths.ProjectDataDirectoryName, "atlas");
            try
            {
                Directory.CreateDirectory(cacheDir);
            }
            catch (Exception ex)
            {
                errors.Add($"[atlas] cache directory unavailable: {ex.Message}");
                return result;
            }

            // 本次应保留的产物文件名；不在集合里的（定义被删、同一图集的旧签名文件）随后清理。
            var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errorCountAtStart = errors.Count;

            foreach (var defPath in definitionPaths)
            {
                try
                {
                    var atlas = SyncOne(defPath, cacheDir, metaDb, errors, produced);
                    if (atlas != null) result.Add(atlas);
                }
                catch (Exception ex)
                {
                    errors.Add($"[atlas] {Path.GetFileName(defPath)}: {ex.Message}");
                }
            }

            // 有失败时跳过清理：宁可留下多余文件，也不要因为临时错误删掉可用产物。
            if (errors.Count == errorCountAtStart) Prune(cacheDir, produced);
            return result;
        }

        /// <summary>收集某个图集定义将参与合图的精灵（开发期缓存与构建期打包共用）。</summary>
        internal static List<AtlasPacker.PackItem> CollectSprites(SpriteAtlasDefinition def, string definitionPath,
                                                                MetaDatabase metaDb)
        {
            var items = new List<AtlasPacker.PackItem>();
            var folder = def.ResolveFolder(definitionPath);
            if (folder == null) return items;

            List<string> files;
            try { files = Directory.EnumerateFiles(folder).ToList(); }
            catch { return items; }

            // 已切分的纹理：按精灵入集（同目录下散图与精灵表互不重复计算）。
            var slicedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (!string.Equals(Path.GetExtension(file), SpriteSheetFile.Extension,
                        StringComparison.OrdinalIgnoreCase)) continue;
                var sheet = SpriteSheetFile.Read(file);
                if (sheet == null) continue;
                var texturePath = metaDb.ResolvePath(sheet.TextureGuid);
                if (string.IsNullOrEmpty(texturePath) || !File.Exists(texturePath)) continue;

                slicedTextures.Add(texturePath);
                foreach (var sprite in sheet.Sprites)
                    items.Add(new AtlasPacker.PackItem(sprite.Guid, sprite.Name, texturePath, sprite.Rect));
            }

            // 未切分的散图：整张纹理即一个退化精灵。
            foreach (var file in files)
            {
                if (!ProjectPanel.IsImageFile(file)) continue;
                if (slicedTextures.Contains(file)) continue;
                var guid = metaDb.ResolveGuid(file);
                if (string.IsNullOrEmpty(guid)) continue;
                var size = ImagePixelSize(file);
                if (size == null) continue;

                items.Add(new AtlasPacker.PackItem(guid, Path.GetFileNameWithoutExtension(file), file,
                    new SliceRect(0, 0, size.Value.Width, size.Value.Height)));
            }

            // 顺序稳定，同样输入产生同样的图集。
            items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return items;
        }

        private static CachedAtlas? SyncOne(string definitionPath, string cacheDir, MetaDatabase metaDb,
                                            ICollection<string> errors, ICollection<string> produced)
        {
            var def = SpriteAtlasDefinition.Read(definitionPath);
            if (def == null)
            {
                errors.Add($"[atlas] {Path.GetFileName(definitionPath)}: definition unreadable");
                return null;
            }

            var items = CollectSprites(def, definitionPath, metaDb);
            if (items.Count == 0)
            {
                errors.Add($"[atlas] {def.Name}: no sprites to pack");
                return null;
            }

            var key = CacheKey(definitionPath);
            var indexPath = Path.Combine(cacheDir, key + ".json");
            var signature = Signature(def, items);
            produced.Add(Path.GetFileName(indexPath));

            // 命中缓存：签名一致且产物仍在。
            var cached = ReadIndex(indexPath);
            if (cached != null && cached.Signature == signature && cached.TextureFile.Length > 0)
            {
                var cachedPng = Path.Combine(cacheDir, cached.TextureFile);
                if (File.Exists(cachedPng))
                {
                    produced.Add(cached.TextureFile);
                    return ToCached(cached, cachedPng);
                }
            }

            // 产物名带签名：内容变化即换路径，避免引擎按路径缓存命中旧图集。
            var textureFile = $"{key}-{signature.Substring(0, 8)}.png";
            var pngPath = Path.Combine(cacheDir, textureFile);
            var packed = AtlasPacker.Pack(items, pngPath, def.Padding, def.MaxSize, out var error);
            if (packed == null)
            {
                errors.Add($"[atlas] {def.Name}: pack failed - {error}");
                return null;
            }

            // 纹理 GUID 跨会话保持稳定；缓存被清后重新分配（映射与索引同时推送，无副作用）。
            var textureGuid = string.IsNullOrEmpty(cached?.TextureGuid) ? MetaFile.GenerateGuid() : cached!.TextureGuid;
            var index = new CacheIndex
            {
                Name = def.Name,
                TextureGuid = textureGuid,
                TextureFile = textureFile,
                Signature = signature,
                Width = packed.AtlasWidth,
                Height = packed.AtlasHeight,
                Entries = packed.Frames
                    .Select(f => new AtlasEntryRecord { SpriteGuid = f.SpriteGuid, X = f.X, Y = f.Y, W = f.W, H = f.H })
                    .ToList(),
            };
            WriteIndex(indexPath, index);
            produced.Add(textureFile);
            return ToCached(index, pngPath);
        }

        /// <summary>删除本次未产出的缓存文件（定义被删、或同一图集上一版签名的产物）。</summary>
        private static void Prune(string cacheDir, ICollection<string> produced)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(cacheDir))
                {
                    if (produced.Contains(Path.GetFileName(file))) continue;
                    try { File.Delete(file); } catch { /* 被占用则留待下次同步 */ }
                }
            }
            catch { /* 枚举失败忽略：缓存多留几个文件不影响正确性 */ }
        }

        private static CachedAtlas ToCached(CacheIndex index, string pngPath) => new()
        {
            Name = index.Name,
            TextureGuid = index.TextureGuid,
            TexturePath = pngPath,
            Entries = index.Entries
                .Select(e => new AtlasMapEntry(e.SpriteGuid, index.TextureGuid, e.X, e.Y, e.W, e.H))
                .ToList(),
        };

        /// <summary>内容签名：定义参数 + 参与精灵的身份/矩形 + 源纹理的大小与修改时间。</summary>
        private static string Signature(SpriteAtlasDefinition def, IReadOnlyList<AtlasPacker.PackItem> items)
        {
            var sb = new StringBuilder();
            sb.Append(def.Folder).Append('|').Append(def.Padding).Append('|').Append(def.MaxSize);
            foreach (var item in items.OrderBy(i => i.SpriteGuid, StringComparer.Ordinal))
            {
                sb.Append('|').Append(item.SpriteGuid)
                  .Append(':').Append(item.Region.X).Append(',').Append(item.Region.Y)
                  .Append(',').Append(item.Region.W).Append(',').Append(item.Region.H)
                  .Append('@').Append(item.SourcePath.ToLowerInvariant());
                var info = new FileInfo(item.SourcePath);
                if (info.Exists) sb.Append('#').Append(info.Length).Append('#').Append(info.LastWriteTimeUtc.Ticks);
            }
            return ShortHash(sb.ToString(), 16);
        }

        /// <summary>缓存文件名：图集名（kebab-case）+ 定义文件路径摘要，避免同名定义互相覆盖。</summary>
        private static string CacheKey(string definitionPath)
            => $"{Kebab(Path.GetFileNameWithoutExtension(definitionPath))}-{ShortHash(definitionPath.ToLowerInvariant())}";

        /// <summary>转 kebab-case：小写，非字母数字合并为单个 '-'（缓存与构建产物文件名统一该风格）。</summary>
        internal static string Kebab(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
            var kebab = sb.ToString().Trim('-');
            return kebab.Length > 0 ? kebab : "atlas";
        }

        private static string ShortHash(string text, int length = 8)
            => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..length];

        private static PixelSize? ImagePixelSize(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var bmp = new Bitmap(stream);
                return bmp.PixelSize;
            }
            catch
            {
                return null;
            }
        }

        // ── 缓存索引文件（记录产物位置、纹理 GUID 与映射，使缓存可跨会话复用）──

        private sealed class CacheIndex
        {
            public string Name = "";
            public string TextureGuid = "";
            public string TextureFile = "";
            public string Signature = "";
            public int Width;
            public int Height;
            public List<AtlasEntryRecord> Entries = new();
        }

        private static CacheIndex? ReadIndex(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var index = new CacheIndex
                {
                    Name = Str(root, "name"),
                    TextureGuid = Str(root, "textureGuid"),
                    TextureFile = Str(root, "textureFile"),
                    Signature = Str(root, "signature"),
                };
                if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in entries.EnumerateArray())
                    {
                        var guid = Str(e, "spriteGuid");
                        if (guid.Length == 0) continue;
                        index.Entries.Add(new AtlasEntryRecord
                        {
                            SpriteGuid = guid,
                            X = Int(e, "x"), Y = Int(e, "y"), W = Int(e, "w"), H = Int(e, "h"),
                        });
                    }
                }
                return index;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteIndex(string path, CacheIndex index)
        {
            var payload = new
            {
                name = index.Name,
                textureGuid = index.TextureGuid,
                textureFile = index.TextureFile,
                signature = index.Signature,
                width = index.Width,
                height = index.Height,
                entries = index.Entries
                    .Select(e => new { spriteGuid = e.SpriteGuid, x = e.X, y = e.Y, w = e.W, h = e.H })
                    .ToArray(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string Str(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static int Int(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var i) ? i : 0;
    }
}

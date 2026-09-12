using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dream.Studio.Panels.Project;

namespace Dream.Studio.Engine
{
    /// <summary>产物清单中的一条资源：GUID → 包内相对路径（Sprite 非空表示精灵子资源）。</summary>
    internal sealed class BuildResourceEntry
    {
        public string Guid = "";
        public string Path = "";
        public string? Sprite;
    }

    /// <summary>
    /// 构建产物清单（写进产物根的 resource.manifest.json，由引擎发布启动路径读取）。
    /// 路径一律相对应用根（app:/），引擎用 File.applicationDirectory 解析——
    /// 这样同一份清单在 ADL 与打包后的应用里都能正确落地。
    /// </summary>
    internal sealed class BuildManifest
    {
        public const string FileName = "resource.manifest.json";

        public string StartupScene = "";
        public List<BuildResourceEntry> Resources = new();
        public List<AtlasMapEntry> Atlas = new();

        /// <summary>写出清单（字段名与引擎 initRuntime 的读取约定一致）。</summary>
        public void Write(string path)
        {
            var payload = new
            {
                version = 1,
                startupScene = StartupScene,
                resources = Resources
                    .Select(r => new { guid = r.Guid, path = r.Path, sprite = r.Sprite })
                    .ToArray(),
                atlas = Atlas
                    .Select(a => new
                    {
                        spriteGuid = a.SpriteGuid,
                        textureGuid = a.TextureGuid,
                        x = a.X, y = a.Y, w = a.W, h = a.H,
                    })
                    .ToArray(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>
    /// 只收集被引用资源的构建收集器。
    ///
    /// 做法：从启动场景出发，递归扫描 JSON 中出现的 GUID 字面量（不依赖字段名，
    /// 因此新增组件/字段也无需改动这里），得到被引用集合；再逐个落地为包内文件：
    ///   - 精灵 GUID → 承载它的 .dmsheet + 该表的源纹理（图集未覆盖时的回落路径）
    ///   - 其它 GUID → 文件本身
    ///   - .dmclip / .dmanimator 内部还会引用资源 → 继续展开（传递闭包）
    ///
    /// 图集：只打包「引用了其中精灵」的定义，且**只合被引用的那些精灵**（图集只是可选覆盖层，
    /// 未覆盖的精灵仍走源纹理）。这样包体既拿到合批收益，也不为无关精灵付尺寸。
    /// </summary>
    internal static class BuildAssetCollector
    {
        /// <summary>包内资源目录名（清单路径均以它开头）。</summary>
        public const string AssetsDirName = "assets";

        /// <summary>
        /// 内部还会引用其它资源的"引用型"资产扩展名：这些文件里出现的 GUID 要继续展开，
        /// 否则运行时拿不到它们引用的精灵/纹理。
        ///   .dmclip / .dmanimator  动画片段与状态机 → 帧精灵
        ///   .prefab                预制体 → 内部元素的精灵/纹理（运行期 spawn 出的实例要靠它们渲染）
        /// </summary>
        private static readonly HashSet<string> ReferencingExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dmclip", ".dmanimator", ".prefab",
        };

        private static readonly Regex GuidPattern = new(@"\b[0-9a-fA-F]{32}\b", RegexOptions.Compiled);

        public static BuildManifest Collect(string projectRoot, string startupScenePath, MetaDatabase metaDb,
                                            string stagingRoot, ICollection<string> errors, Action<string> log)
        {
            var manifest = new BuildManifest();
            var assetsDir = Path.Combine(stagingRoot, AssetsDirName);
            Directory.CreateDirectory(assetsDir);

            // 1. 启动场景自身进包（清单记录其包内路径）。
            manifest.StartupScene = CopyAsset(projectRoot, startupScenePath, assetsDir, errors) ?? "";

            // 2. 传递闭包收集被引用的 GUID。
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (var guid in ScanGuids(SafeReadText(startupScenePath)))
                if (referenced.Add(guid)) queue.Enqueue(guid);

            while (queue.Count > 0)
            {
                var guid = queue.Dequeue();
                var path = metaDb.ResolvePath(guid);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                var ext = Path.GetExtension(path);
                // 引用型资产内部还引用精灵与纹理：继续展开，否则运行时动画会缺帧、
                // spawn 出的预制体实例会没有图。
                if (ReferencingExtensions.Contains(ext))
                {
                    foreach (var nested in ScanGuids(SafeReadText(path)))
                        if (referenced.Add(nested)) queue.Enqueue(nested);
                }
            }

            // 3. 逐个落地（同一 GUID 只出一条清单项）。
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var guid in referenced)
            {
                var sheetPath = metaDb.ResolveSpriteSheetPath(guid);
                if (!string.IsNullOrEmpty(sheetPath))
                {
                    AddResource(manifest, emitted, guid, projectRoot, sheetPath, assetsDir,
                        metaDb.ResolveSpriteName(guid), errors);

                    // 精灵表的源纹理（图像文件本身不是 GUID 资源，但精灵回落时需要能加载）。
                    var sheet = SpriteSheetFile.Read(sheetPath);
                    if (sheet == null)
                    {
                        errors.Add($"[build] {Path.GetFileName(sheetPath)}: sprite sheet unreadable");
                        continue;
                    }
                    if (string.IsNullOrEmpty(sheet.TextureGuid)) continue;
                    var texturePath = metaDb.ResolvePath(sheet.TextureGuid);
                    if (string.IsNullOrEmpty(texturePath) || !File.Exists(texturePath))
                    {
                        errors.Add($"[build] {Path.GetFileName(sheetPath)}: source texture missing");
                        continue;
                    }
                    AddResource(manifest, emitted, sheet.TextureGuid, projectRoot, texturePath, assetsDir, null, errors);
                    continue;
                }

                var path = metaDb.ResolvePath(guid);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    errors.Add($"[build] no file for referenced guid {guid}");
                    continue;
                }
                AddResource(manifest, emitted, guid, projectRoot, path, assetsDir, null, errors);
            }

            // 4. 图集：只打包覆盖到被引用精灵的定义，且只合这些精灵。
            foreach (var atlasPath in metaDb.PathsWithExtension(SpriteAtlasDefinition.Extension))
            {
                var def = SpriteAtlasDefinition.Read(atlasPath);
                if (def == null) continue;

                var covered = SpriteAtlasCache.CollectSprites(def, atlasPath, metaDb)
                    .Where(i => referenced.Contains(i.SpriteGuid))
                    .ToList();
                if (covered.Count == 0) continue;

                var atlasFile = SpriteAtlasCache.Kebab(def.Name) + "-atlas.png";
                var packed = AtlasPacker.Pack(covered, Path.Combine(assetsDir, atlasFile),
                    def.Padding, def.MaxSize, out var packError);
                if (packed == null)
                {
                    errors.Add($"[build] atlas {def.Name}: {packError}");
                    continue;
                }

                // 图集纹理 GUID 由定义路径确定性派生：重复构建产物一致，便于覆盖更新。
                var textureGuid = DeterministicGuid("dream-atlas:" + atlasPath.ToLowerInvariant());
                manifest.Resources.Add(new BuildResourceEntry
                {
                    Guid = textureGuid,
                    Path = AssetsDirName + "/" + atlasFile,
                });
                foreach (var f in packed.Frames)
                    manifest.Atlas.Add(new AtlasMapEntry(f.SpriteGuid, textureGuid, f.X, f.Y, f.W, f.H));
                log($"[build] atlas {def.Name}: {covered.Count} sprite(s) → {packed.AtlasWidth}x{packed.AtlasHeight}");
            }

            return manifest;
        }

        private static void AddResource(BuildManifest manifest, HashSet<string> emitted, string guid,
                                        string projectRoot, string absPath, string assetsDir,
                                        string? sprite, ICollection<string> errors)
        {
            if (!emitted.Add(guid)) return;
            var rel = CopyAsset(projectRoot, absPath, assetsDir, errors);
            if (rel == null) return;
            manifest.Resources.Add(new BuildResourceEntry
            {
                Guid = guid,
                Path = rel,
                Sprite = string.IsNullOrEmpty(sprite) ? null : sprite,
            });
        }

        /// <summary>复制单个资源进包，返回其相对应用根的路径（正斜杠）；失败返回 null。</summary>
        private static string? CopyAsset(string projectRoot, string absPath, string assetsDir,
                                         ICollection<string> errors)
        {
            try
            {
                var rel = Path.GetRelativePath(projectRoot, absPath);
                if (rel.StartsWith("..", StringComparison.Ordinal))
                {
                    errors.Add("[build] asset outside project root: " + absPath);
                    return null;
                }
                var target = Path.Combine(assetsDir, rel);
                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                File.Copy(absPath, target, overwrite: true);
                return AssetsDirName + "/" + rel.Replace('\\', '/');
            }
            catch (Exception ex)
            {
                errors.Add($"[build] copy failed {absPath}: {ex.Message}");
                return null;
            }
        }

        private static IEnumerable<string> ScanGuids(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            foreach (Match m in GuidPattern.Matches(text)) yield return m.Value;
        }

        private static string SafeReadText(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return ""; }
        }

        private static string DeterministicGuid(string seed)
            => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(seed)))[..32].ToLowerInvariant();
    }
}

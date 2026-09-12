using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dream.Studio.Engine;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 项目元数据数据库：维护 GUID → 文件绝对路径的映射。
    /// 
    /// 职责：
    ///   1. 在 ProjectPanel 扫描目录时，为没有 .meta 的文件自动生成 .meta。
    ///   2. 加载已有 .meta 文件，建立 Guid → Path 索引。
    ///   3. 文件移动/重命名时同步更新索引（.meta 文件随文件一起移动）。
    ///   4. 为 AS3 端 ResourceManager / SpriteRenderer 提供 GUID 解析服务。
    /// </summary>
    internal sealed class MetaDatabase
    {
        // GUID → 文件绝对路径
        private readonly Dictionary<string, string> _guidToPath = new(StringComparer.OrdinalIgnoreCase);
        // 路径 → GUID（反向索引，用于移动/删除时快速定位）
        private readonly Dictionary<string, string> _pathToGuid = new(StringComparer.OrdinalIgnoreCase);
        // 精灵 GUID → 所在精灵表（GUID 指向 .dmsheet 内的一个精灵，而非文件本身）
        private readonly Dictionary<string, SpriteRef> _spriteIndex = new(StringComparer.OrdinalIgnoreCase);
        // 构建产物目录名（由项目设置得出）：扫描时整体跳过，其内容不是资源。
        private string _buildOutputDirName = "";

        /// <summary>精灵子资源：所在 .dmsheet 文件路径 + 精灵名。</summary>
        internal readonly record struct SpriteRef(string SheetPath, string Name);

        /// <summary>已知 GUID 数量。</summary>
        public int Count => _guidToPath.Count;

        /// <summary>根据 GUID 查找文件绝对路径。未找到返回 null。</summary>
        public string? ResolvePath(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            _guidToPath.TryGetValue(guid, out var path);
            return path;
        }

        /// <summary>根据 GUID 查询资源类型（读 .meta 文件）。未找到返回 null。</summary>
        public string? GetAssetType(string guid)
        {
            var path = ResolvePath(guid);
            if (path == null) return null;
            var metaPath = MetaFile.GetMetaPath(path);
            if (!File.Exists(metaPath)) return null;
            try { return MetaFile.FromJson(File.ReadAllText(metaPath)).AssetType; }
            catch { return null; }
        }

        /// <summary>根据文件绝对路径查找 GUID。未找到返回 null。</summary>
        public string? ResolveGuid(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            _pathToGuid.TryGetValue(path, out var guid);
            return guid;
        }

        /// <summary>返回 GUID → 路径映射的快照。</summary>
        public IReadOnlyDictionary<string, string> GetIndex()
        {
            return new Dictionary<string, string>(_guidToPath, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 扫描目录并为所有文件加载/生成 .meta。
        /// 首次调用时清空旧索引；后续可增量刷新（但当前实现简单全量）。
        /// </summary>
        public void ScanDirectory(string directory)
        {
            _guidToPath.Clear();
            _pathToGuid.Clear();
            _spriteIndex.Clear();
            // 构建产物目录同样不是资源：其内容（swf / 复制的资源 / 清单）不应进索引、不生成 .meta。
            _buildOutputDirName = DreamProjectSettings.ResolveOutputTopDirectoryName(directory);
            if (!Directory.Exists(directory)) return;
            ScanRecursive(directory);
        }

        private void ScanRecursive(string directory)
        {
            foreach (var dir in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(dir);
                // 派生数据目录（.dream）不是资源：既不生成 .meta，也不进索引。
                if (string.Equals(name, EnginePaths.ProjectDataDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (_buildOutputDirName.Length > 0
                    && string.Equals(name, _buildOutputDirName, StringComparison.OrdinalIgnoreCase))
                    continue;
                ScanRecursive(dir);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                // 跳过 .meta 文件本身
                if (string.Equals(Path.GetExtension(file), ".meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                var metaPath = MetaFile.GetMetaPath(file);
                MetaFile meta;
                if (File.Exists(metaPath))
                {
                    meta = MetaFile.FromJson(File.ReadAllText(metaPath));
                    if (string.IsNullOrWhiteSpace(meta.Guid))
                        meta.Guid = MetaFile.GenerateGuid();
                }
                else
                {
                    meta = new MetaFile
                    {
                        Guid = MetaFile.GenerateGuid(),
                        AssetType = InferAssetType(file),
                    };
                }

                // 音频资源：补齐时长/采样率/声道元数据（缺失时读取并写回）。
                if (string.Equals(meta.AssetType, "audio", StringComparison.OrdinalIgnoreCase)
                    && meta.DurationSeconds <= 0)
                {
                    var audio = AudioMetadataReader.Read(file);
                    if (audio.DurationSeconds > 0)
                    {
                        meta.DurationSeconds = audio.DurationSeconds;
                        meta.SampleRate = audio.SampleRate;
                        meta.Channels = audio.Channels;
                        meta.EstimatedDuration = audio.Estimated;
                    }
                }

                try { File.WriteAllText(metaPath, meta.ToJson()); }
                catch { continue; }

                Register(meta.Guid, file);

                // 精灵表：把表内各精灵的 GUID 一并登记为子资源，供组件引用与面板展开。
                if (string.Equals(Path.GetExtension(file), SpriteSheetFile.Extension,
                        StringComparison.OrdinalIgnoreCase))
                    RegisterSheetSprites(file);
            }
        }

        /// <summary>解析 .dmsheet，登记其中每个精灵的 GUID → (表路径, 精灵名)。</summary>
        private void RegisterSheetSprites(string sheetPath)
        {
            var sheet = SpriteSheetFile.Read(sheetPath);
            if (sheet == null) return;
            foreach (var sprite in sheet.Sprites)
                _spriteIndex[sprite.Guid] = new SpriteRef(sheetPath, sprite.Name);
        }

        /// <summary>GUID 是否为精灵子资源（指向 .dmsheet 内的某个精灵，而非文件本身）。</summary>
        public bool IsSprite(string guid)
            => guid != null && _spriteIndex.ContainsKey(guid);

        /// <summary>精灵名（找不到返回 null）。用于 Inspector 显示。</summary>
        public string? ResolveSpriteName(string guid)
            => guid != null && _spriteIndex.TryGetValue(guid, out var r) ? r.Name : null;

        /// <summary>精灵 GUID → 所在精灵表路径。</summary>
        public string? ResolveSpriteSheetPath(string guid)
            => guid != null && _spriteIndex.TryGetValue(guid, out var r) ? r.SheetPath : null;

        /// <summary>返回精灵 GUID → 所在精灵表的快照，供推送到 AS3 端。</summary>
        public IReadOnlyDictionary<string, SpriteRef> GetSpriteIndex()
            => new Dictionary<string, SpriteRef>(_spriteIndex, StringComparer.OrdinalIgnoreCase);

        /// <summary>注册或更新 GUID → Path 映射。</summary>
        public void Register(string guid, string path)
        {
            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(path)) return;
            _guidToPath[guid] = path;
            _pathToGuid[path] = guid;
        }

        /// <summary>文件或目录移动后更新索引（.meta 文件随原件一起移动）。</summary>
        public void Move(string oldPath, string newPath)
        {
            // 序数比较：仅大小写不同的改名（Art → art）也要落到下面更新索引，
            // 否则索引里会留着文件系统上已不存在的旧写法。
            if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return;

            // 目录搬家/改名：索引里不登记目录本身，但它下面的每个文件都以旧路径为前缀，
            // 需要整体换前缀，否则这些文件的 GUID 会指向已不存在的路径（拖走一个文件夹即丢图）。
            if (Directory.Exists(newPath))
            {
                RebaseDirectory(oldPath, newPath);
                return;
            }

            if (!_pathToGuid.TryGetValue(oldPath, out var guid)) return;

            _pathToGuid.Remove(oldPath);
            _pathToGuid[newPath] = guid;
            _guidToPath[guid] = newPath;

            // 精灵表改名：表内精灵登记的是"所在表路径"，不跟着换会让图集解析去旧位置找表。
            if (string.Equals(Path.GetExtension(newPath), SpriteSheetFile.Extension, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var spriteGuid in _spriteIndex.Keys.ToList())
                {
                    var r = _spriteIndex[spriteGuid];
                    if (string.Equals(r.SheetPath, oldPath, StringComparison.OrdinalIgnoreCase))
                        _spriteIndex[spriteGuid] = new SpriteRef(newPath, r.Name);
                }
            }

            // 移动 .meta 文件
            var oldMeta = MetaFile.GetMetaPath(oldPath);
            var newMeta = MetaFile.GetMetaPath(newPath);
            try
            {
                if (File.Exists(oldMeta))
                    File.Move(oldMeta, newMeta);
            }
            catch { }
        }

        /// <summary>目录整体移动/改名：把索引里位于该目录下的所有路径换成新前缀。</summary>
        private void RebaseDirectory(string oldDir, string newDir)
        {
            var prefixOld = oldDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
            var prefixNew = newDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

            // 先取键快照再改写：不能边遍历字典边增删。
            var keys = _pathToGuid.Keys
                .Where(k => k.StartsWith(prefixOld, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keys)
            {
                var guid = _pathToGuid[key];
                var target = prefixNew + key.Substring(prefixOld.Length);
                // 必须先删再加：字典比较器忽略大小写，纯大小写改名时新旧键"相等"，
                // 直接赋值会保留旧的键写法。
                _pathToGuid.Remove(key);
                _pathToGuid[target] = guid;
                _guidToPath[guid] = target;
            }

            // 精灵子资源登记的是"所在精灵表路径"，同样是绝对路径，一并换前缀，
            // 否则目录一移，图集解析还会去旧位置找表。
            foreach (var spriteGuid in _spriteIndex.Keys.ToList())
            {
                var r = _spriteIndex[spriteGuid];
                if (r.SheetPath.StartsWith(prefixOld, StringComparison.OrdinalIgnoreCase))
                    _spriteIndex[spriteGuid] = new SpriteRef(
                        prefixNew + r.SheetPath.Substring(prefixOld.Length), r.Name);
            }
        }

        /// <summary>文件删除时移除索引并尝试删除 .meta。</summary>
        public void Remove(string path)
        {
            if (!_pathToGuid.TryGetValue(path, out var guid)) return;
            _pathToGuid.Remove(path);
            _guidToPath.Remove(guid);
            try
            {
                var metaPath = MetaFile.GetMetaPath(path);
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            catch { }
        }

        /// <summary>返回索引中所有指定扩展名的文件路径（用于收集全部图集）。</summary>
        public IReadOnlyList<string> PathsWithExtension(string extension)
        {
            var result = new List<string>();
            foreach (var path in _pathToGuid.Keys)
            {
                if (string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                    result.Add(path);
            }
            return result;
        }

        /// <summary>根据文件扩展名推断资源类型。</summary>
        private static string InferAssetType(string path)
        {
            return InferAssetTypeStatic(path);
        }

        /// <summary>静态版本：供 ProjectPanel 导入外部文件时调用。</summary>
        public static string InferAssetTypeStatic(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => "texture",
                ".as" => "script",
                ".space" => "scene",
                ".prefab" => "prefab",
                ".dmclip" => "clip",
                ".dmanimator" => "animator",
                ".dmsheet" => "spritesheet",
                ".dmatlas" => "atlas",
                ".mp3" or ".wav" => "audio",
                ".json" => "json",
                _ => "unknown",
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 描述符 schema 视图：某个命名空间版本允许哪些元素、以及元素之间的父子关系。
    ///
    /// 与 <see cref="IconSizeCatalog"/> 同一思路——AIR SDK 自带
    /// <c>templates/air/Descriptor.&lt;命名空间&gt;.xsd</c>，就是 ADT 校验描述符用的那份定义。
    /// 构建面板据此判断某个字段在项目当前的命名空间下是否可用：不支持的字段写进描述符，
    /// ADT 会报 <c>error 103: xxx is an unexpected element/attribute</c>。
    ///
    /// 版本间差异很实在：<c>depthAndStencil</c> 自 3.7、<c>allowMultipleInstances</c> 自 50.0、
    /// <c>useAngle</c> 自 51.2、<c>windows/localAppData</c> 自 51.3。
    /// </summary>
    internal sealed class DescriptorSchema
    {
        private static readonly Dictionary<string, DescriptorSchema?> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string?> MinVersionCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string?> LatestCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        /// <summary>所有出现过的元素名。</summary>
        private readonly HashSet<string> _elements = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>父元素名 → 其内部出现过的元素名（用于判断 "父/子" 路径是否成立）。</summary>
        private readonly Dictionary<string, HashSet<string>> _children = new(StringComparer.OrdinalIgnoreCase);

        private DescriptorSchema()
        {
        }

        /// <summary>
        /// 读某命名空间版本的 schema；该版本没有 schema 文件时返回 null
        /// （调用方按"无法判断"处理，不要据此禁用字段）。
        /// </summary>
        public static DescriptorSchema? For(string sdkPath, string version)
        {
            var path = IconSizeCatalog.SchemaPath(sdkPath, version);
            if (path == null) return null;

            lock (Gate)
            {
                if (Cache.TryGetValue(path, out var cached)) return cached;
            }

            var schema = Read(path);
            lock (Gate)
            {
                Cache[path] = schema;
            }
            return schema;
        }

        /// <summary>
        /// 该 SDK 支持的最新描述符命名空间版本（"51.3" 形式）；目录里没有 schema 时返回 null。
        ///
        /// 构建面板的判定基准用它而不是描述符里写的 xmlns：SDK 只自带它自己及更早版本的 schema，
        /// 拿一个比 SDK 还新的命名空间去查必然查不到（表现为"换了 SDK 面板毫无变化"）。
        /// 按 SDK 自身的最新命名空间查，恒有 schema 可用，且换 SDK 后结果随之改变。
        /// </summary>
        public static string? LatestNamespaceVersion(string sdkPath)
        {
            if (string.IsNullOrWhiteSpace(sdkPath)) return null;
            lock (Gate)
            {
                if (LatestCache.TryGetValue(sdkPath, out var cached)) return cached;
            }

            var latest = FindLatestNamespaceVersion(sdkPath);
            lock (Gate)
            {
                LatestCache[sdkPath] = latest;
            }
            return latest;
        }

        private static string? FindLatestNamespaceVersion(string sdkPath)
        {
            var dir = Path.Combine(sdkPath, "templates", "air");
            if (!Directory.Exists(dir)) return null;

            string? latest = null;
            var latestKey = "";
            foreach (var file in Directory.EnumerateFiles(dir, "Descriptor.*.xsd"))
            {
                var version = Path.GetFileNameWithoutExtension(file).Substring("Descriptor.".Length);
                var key = VersionKey(version);
                if (latest != null && string.CompareOrdinal(key, latestKey) <= 0) continue;
                latest = version;
                latestKey = key;
            }
            return latest;
        }

        /// <summary>清空缓存（SDK 路径变更、或同一路径下换了 SDK 版本时必须调用）。</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                Cache.Clear();
                MinVersionCache.Clear();
                LatestCache.Clear();
            }
        }

        /// <summary>该版本是否允许某个描述符路径（<c>元素</c> 或 <c>父/子</c>）。</summary>
        public bool Supports(string path)
        {
            var parts = path.Split('/');
            if (parts.Length == 1) return _elements.Contains(parts[0]);
            return _children.TryGetValue(parts[^2], out var children) && children.Contains(parts[^1]);
        }

        /// <summary>
        /// 在所有版本的 schema 里找最早支持该路径的命名空间版本（"51.2" 形式）；找不到返回 null。
        /// 只用于给禁用字段标注"需要哪个版本"，按 (SDK, 路径) 缓存。
        /// </summary>
        public static string? FirstSupportingVersion(string sdkPath, string path)
        {
            if (string.IsNullOrWhiteSpace(sdkPath)) return null;
            var key = sdkPath + "|" + path;
            lock (Gate)
            {
                if (MinVersionCache.TryGetValue(key, out var cached)) return cached;
            }

            var result = FindFirstVersion(sdkPath, path);
            lock (Gate)
            {
                MinVersionCache[key] = result;
            }
            return result;
        }

        private static string? FindFirstVersion(string sdkPath, string path)
        {
            var dir = Path.Combine(sdkPath, "templates", "air");
            if (!Directory.Exists(dir)) return null;

            var candidates = Directory.EnumerateFiles(dir, "Descriptor.*.xsd")
                .Select(file => Path.GetFileNameWithoutExtension(file).Substring("Descriptor.".Length))
                .OrderBy(VersionKey, StringComparer.Ordinal);

            foreach (var version in candidates)
            {
                var schema = For(sdkPath, version);
                if (schema != null && schema.Supports(path)) return version;
            }
            return null;
        }

        /// <summary>版本号排序键：把 "51.3" 补零成 "0051.0003" 之类，避免按字符串排序出错。</summary>
        private static string VersionKey(string version)
        {
            var parts = version.Split('.');
            var padded = new string[Math.Max(parts.Length, 2)];
            for (var i = 0; i < padded.Length; i++)
            {
                var value = i < parts.Length && int.TryParse(parts[i], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                padded[i] = value.ToString("D4", CultureInfo.InvariantCulture);
            }
            return string.Join('.', padded);
        }

        private static DescriptorSchema? Read(string path)
        {
            try
            {
                var doc = XDocument.Load(path);
                var schema = new DescriptorSchema();
                foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "element"))
                {
                    var name = (string?)element.Attribute("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    schema._elements.Add(name!);

                    var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var child in element.Descendants().Where(e => e.Name.LocalName == "element"))
                    {
                        var childName = (string?)child.Attribute("name");
                        if (!string.IsNullOrEmpty(childName)) children.Add(childName!);
                    }
                    if (children.Count == 0) continue;
                    if (schema._children.TryGetValue(name!, out var existing)) existing.UnionWith(children);
                    else schema._children[name!] = children;
                }
                return schema;
            }
            catch
            {
                return null;
            }
        }
    }
}

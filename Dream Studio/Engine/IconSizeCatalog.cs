using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 应用图标的尺寸白名单。
    ///
    /// AIR 只接受自己认识的那些 <c>imageNxN</c> 元素：写了不认识的尺寸，ADT 直接报
    /// <c>error 103: application.icon.imageNxN is an unexpected element/attribute</c>；
    /// 而认识哪些尺寸是**随版本变化**的（1.1 只有 16/32/48/128，51.1 起有 34 个）。
    ///
    /// 因此不在这里硬编码版本表——AIR SDK 自带每个命名空间的描述符 schema
    /// （<c>&lt;SDK&gt;/templates/air/Descriptor.&lt;命名空间&gt;.xsd</c>），其中
    /// <c>IconType</c> 逐条列出该版本允许的尺寸。按项目描述符实际使用的命名空间去读它，
    /// 用户换 SDK、换命名空间都自动适配（与 ADT 校验用的是同一份数据）。
    /// </summary>
    internal static class IconSizeCatalog
    {
        /// <summary>
        /// 读不到 schema 时的兜底尺寸：AIR 1.0 起就存在、任何版本都接受的桌面基础集。
        /// </summary>
        public static readonly IReadOnlyList<string> FallbackSizes = new[] { "16x16", "32x32", "48x48", "128x128" };

        private static readonly Regex IconElementName = new(@"^image(\d+)x(\d+)$", RegexOptions.Compiled);
        private static readonly Dictionary<string, List<string>> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        /// <summary>
        /// 某命名空间版本支持的尺寸（保持 schema 中的原始顺序）。
        /// <paramref name="version"/> 形如 "51.3"；schema 缺失时返回 <see cref="FallbackSizes"/>。
        /// </summary>
        public static IReadOnlyList<string> SizesFor(string sdkPath, string version)
        {
            if (string.IsNullOrWhiteSpace(sdkPath) || string.IsNullOrWhiteSpace(version))
                return FallbackSizes;

            var key = sdkPath + "|" + version;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
            }

            var sizes = ReadSchema(SchemaPath(sdkPath, version));
            lock (Gate)
            {
                Cache[key] = sizes;
            }
            return sizes;
        }

        /// <summary>清空缓存（SDK 路径变更、或同一路径下换了 SDK 版本时必须调用）。</summary>
        public static void Invalidate()
        {
            lock (Gate) Cache.Clear();
        }

        /// <summary>描述符 schema 的绝对路径；该 SDK 没有对应版本的 schema 时返回 null。</summary>
        public static string? SchemaPath(string sdkPath, string version)
        {
            if (string.IsNullOrWhiteSpace(sdkPath) || string.IsNullOrWhiteSpace(version)) return null;
            // air = 应用描述符 schema；sdk = 引擎编译期用的描述符 schema，作为备选。
            foreach (var sub in new[] { "air", "sdk" })
            {
                var path = Path.Combine(sdkPath, "templates", sub, "Descriptor." + version + ".xsd");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static List<string> ReadSchema(string? schemaPath)
        {
            if (schemaPath == null) return new List<string>(FallbackSizes);
            try
            {
                var doc = XDocument.Load(schemaPath);
                var iconType = doc.Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "complexType" && (string?)e.Attribute("name") == "IconType");
                if (iconType == null) return new List<string>(FallbackSizes);

                var sizes = new List<string>();
                foreach (var element in iconType.Descendants().Where(e => e.Name.LocalName == "element"))
                {
                    var name = (string?)element.Attribute("name");
                    var match = name == null ? null : IconElementName.Match(name);
                    if (match == null || !match.Success) continue;
                    var size = match.Groups[1].Value + "x" + match.Groups[2].Value;
                    if (!sizes.Contains(size)) sizes.Add(size);
                }
                return sizes.Count > 0 ? sizes : new List<string>(FallbackSizes);
            }
            catch
            {
                return new List<string>(FallbackSizes);
            }
        }

        /// <summary>把 <c>imageNxN</c> 尺寸串解析为像素宽高；非法返回 false。</summary>
        public static bool TryParseSize(string size, out int width, out int height)
        {
            width = height = 0;
            var match = size == null ? null : IconElementName.Match("image" + size);
            if (match == null || !match.Success) return false;
            return int.TryParse(match.Groups[1].Value, out width)
                && int.TryParse(match.Groups[2].Value, out height)
                && width > 0 && height > 0;
        }

        /// <summary>元素名 <c>imageNxN</c> → 尺寸串 <c>NxN</c>；不是图标元素返回 null。</summary>
        public static string? SizeOfElementName(string elementName)
        {
            var match = IconElementName.Match(elementName ?? "");
            return match.Success ? match.Groups[1].Value + "x" + match.Groups[2].Value : null;
        }
    }
}

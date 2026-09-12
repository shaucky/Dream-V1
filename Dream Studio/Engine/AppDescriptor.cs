using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 应用程序描述符（项目里的 &lt;name&gt;-app.xml）的读写视图。
    ///
    /// 应用身份与平台配置本来就写在描述符里，构建窗口直接读它、改它，不在
    /// dream.project.json 里另存一份——同一件事两处定义迟早会不一致。
    ///
    /// 写回只碰构建窗口提供的那些值：其余元素、注释与顺序原样保留。模板型描述符会把
    /// 暂时用不到的标签整段注释掉（如 &lt;!-- &lt;name&gt;&lt;/name&gt; --&gt;，
    /// 甚至 &lt;!-- &lt;windows&gt; --&gt; … &lt;!-- &lt;/windows&gt; --&gt;），
    /// 写回时会优先「就地还原」对应的注释，而不是把新元素堆到文件末尾。
    /// </summary>
    internal sealed class AppDescriptor
    {
        // 注释内容恰好是一个成对标签（可带示例值）：<name></name>、<name>armv7,armv8</name>。
        // 不允许值里再出现 '<'，否则整段注释掉的容器（如 <icon>…<image16x16/>…</icon>）会被误判。
        private static readonly Regex PairedTag = new(
            @"^\s*<(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*>\s*[^<]*?</\s*\k<name>\s*>\s*$",
            RegexOptions.Singleline);

        // 注释内容恰好是一个开始标签：<windows> 或 <windows/>
        private static readonly Regex OpenTag = new(
            @"^\s*<(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*/?>\s*$",
            RegexOptions.Singleline);

        // 注释内容恰好是一个结束标签：</windows>
        private static readonly Regex CloseTag = new(
            @"^\s*</\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*>\s*$",
            RegexOptions.Singleline);

        private readonly XDocument _doc;
        private XNamespace _ns;

        /// <summary>描述符原本没有 xmlns 时，用它拼目标命名空间 URI。</summary>
        private const string DefaultNamespacePrefix = "http://ns.adobe.com/air/application/";

        /// <summary>待写回的值：路径 → 值。写入时按此表逐个落地，空值不写入（沿用原值）。</summary>
        private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

        /// <summary>
        /// 待写回的图标条目（&lt;icon&gt; 的子元素）。图标不是一个"值"而是一个容器，
        /// 无法走上面的路径表，单独记一份，由 <see cref="Save"/> 统一落地。
        /// </summary>
        private List<AppIconEntry>? _pendingIcons;

        /// <summary>写回时使用的描述符命名空间版本；空值表示保持文件原有的命名空间。</summary>
        private string _pendingNamespaceVersion = "";

        /// <summary>源描述符的绝对路径。</summary>
        public string Path { get; }

        /// <summary>描述符当前声明的命名空间版本（如 "51.3"）——即它要求的 AIR 版本。</summary>
        public string NamespaceVersion => VersionOf(_ns.NamespaceName);

        private static string VersionOf(string? namespaceUri)
        {
            if (string.IsNullOrWhiteSpace(namespaceUri)) return "";
            var slash = namespaceUri!.LastIndexOf('/');
            return slash >= 0 && slash < namespaceUri.Length - 1
                ? namespaceUri.Substring(slash + 1)
                : "";
        }

        private AppDescriptor(string path, XDocument doc, XNamespace ns)
        {
            Path = path;
            _doc = doc;
            _ns = ns;
        }

        /// <summary>
        /// 指定写回时使用的描述符命名空间版本（如 "51.3"）——构建面板按当前 SDK 判定的那个版本。
        /// 元素名在解析时就绑定了文件里原有的命名空间，只改根上的 xmlns 会让每个元素各自补一份
        /// 旧 xmlns，所以写回时要把命名空间与元素名一起换（见 <see cref="ApplyNamespace"/>）。
        /// </summary>
        public void SetNamespaceVersion(string version) => _pendingNamespaceVersion = version?.Trim() ?? "";

        /// <summary>读取某个路径的值：已由界面写入的待写回值优先，否则读文件里的当前值。</summary>
        public string this[string path]
        {
            get => _pending.TryGetValue(path, out var pending) ? pending : Read(path);
            set => _pending[path] = value ?? "";
        }

        /// <summary>读取描述符；文件缺失或 XML 非法时返回 null（原因由调用方提示）。</summary>
        public static AppDescriptor? Load(string path)
        {
            try
            {
                var doc = XDocument.Load(path);
                var root = doc.Root;
                return root == null ? null : new AppDescriptor(path, doc, root.GetDefaultNamespace());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 写回源描述符；空值不写入（元素不存在就保持不存在，存在则保留原值）。
        /// 返回是否真的产生了改动——没改就不落盘，避免白白改动项目文件的时间戳。
        /// </summary>
        public bool Save()
        {
            var root = _doc.Root;
            if (root == null) return false;

            var before = _doc.ToString();
            ApplyNamespace(root);
            ApplyIcons(root);
            foreach (var entry in _pending)
            {
                if (string.IsNullOrWhiteSpace(entry.Value)) continue;
                var parts = entry.Key.Split('/');
                var parent = root;
                for (var i = 0; i < parts.Length - 1; i++) parent = EnsureElement(parent, parts[i]);
                EnsureElement(parent, parts[^1]).Value = entry.Value.Trim();
            }

            if (_doc.ToString() == before) return false;

            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _doc.Save(Path);
            return true;
        }

        // ── 命名空间 ──────────────────────────────────────────

        /// <summary>
        /// 把描述符的默认命名空间换成 <see cref="SetNamespaceVersion"/> 指定的版本。
        ///
        /// 命名空间 URI 即 AIR 版本，决定了 ADT 接受哪些元素与图标尺寸；面板按当前 SDK 的
        /// 最新命名空间判定字段可用性，写回时也把描述符换到同一版本，两者才不会各说各话。
        /// </summary>
        private void ApplyNamespace(XElement root)
        {
            if (_pendingNamespaceVersion.Length == 0) return;

            var uri = NamespaceUri(_pendingNamespaceVersion);
            var ns = XNamespace.Get(uri);
            if (ns == _ns) return;

            foreach (var element in root.DescendantsAndSelf())
                element.Name = ns + element.Name.LocalName;

            var declaration = root.Attributes()
                .FirstOrDefault(a => a.IsNamespaceDeclaration && a.Name.LocalName == "xmlns");
            declaration?.Remove();
            root.SetAttributeValue("xmlns", uri);

            _ns = ns;
        }

        /// <summary>目标命名空间 URI：沿用描述符原本的前缀，只换掉末尾的版本号。</summary>
        private string NamespaceUri(string version)
        {
            var current = _ns.NamespaceName;
            var slash = current.LastIndexOf('/');
            return slash >= 0 && slash < current.Length - 1
                ? current.Substring(0, slash + 1) + version
                : DefaultNamespacePrefix + version;
        }

        // ── 应用图标（<icon> 容器） ──────────────────────────────

        /// <summary>
        /// 读当前图标条目（&lt;imageNxN&gt; 的子元素，按文档顺序；已排队的修改优先）。
        /// 尺寸取 <c>NxN</c> 形式，不含 image 前缀。
        /// </summary>
        public List<AppIconEntry> GetIcons()
        {
            if (_pendingIcons != null) return new List<AppIconEntry>(_pendingIcons);

            var result = new List<AppIconEntry>();
            var icon = _doc.Root?.Element(_ns + "icon");
            if (icon == null) return result;
            foreach (var child in icon.Elements())
            {
                var size = IconSizeCatalog.SizeOfElementName(child.Name.LocalName);
                if (size != null) result.Add(new AppIconEntry(size, child.Value.Trim()));
            }
            return result;
        }

        /// <summary>
        /// 排队写回图标条目：整块替换 &lt;icon&gt; 的内容，只保留传入的尺寸。
        /// （不要的尺寸必须真删掉——空的 &lt;imageNxN&gt; 会让 ADT 报 error 200 找不到图标文件。）
        /// </summary>
        public void SetIcons(IReadOnlyList<AppIconEntry> entries)
        {
            var kept = new List<AppIconEntry>();
            foreach (var entry in entries)
            {
                if (IconSizeCatalog.TryParseSize(entry.Size, out _, out _)
                    && !string.IsNullOrWhiteSpace(entry.Path))
                    kept.Add(new AppIconEntry(entry.Size, entry.Path.Trim()));
            }
            _pendingIcons = kept;
        }

        private void ApplyIcons(XElement root)
        {
            if (_pendingIcons == null) return;
            var icon = FindOrCreateIconElement(root);
            icon.RemoveNodes();
            foreach (var entry in _pendingIcons)
                icon.Add(new XElement(_ns + "image" + entry.Size, entry.Path));
            _pendingIcons = null;
        }

        /// <summary>
        /// 取 &lt;icon&gt; 元素：已有就用；没有就还原模板里被整段注释掉的那个
        /// （&lt;!-- &lt;icon&gt; … &lt;/icon&gt; --&gt;，注释值本身是一段合法 XML，解析后就地替换，
        /// 位置与顺序都不变）；再没有才追加到末尾。
        /// </summary>
        private XElement FindOrCreateIconElement(XElement root)
        {
            var existing = root.Element(_ns + "icon");
            if (existing != null) return existing;

            foreach (var node in root.Nodes())
            {
                if (node is not XComment comment) continue;
                var restored = TryParseFragment(comment.Value);
                if (restored == null || restored.Name.LocalName != "icon") continue;
                var element = new XElement(_ns + "icon");
                comment.ReplaceWith(element);
                return element;
            }

            var appended = new XElement(_ns + "icon");
            root.Add(appended);
            return appended;
        }

        /// <summary>把注释内容当 XML 片段解析（包一层根），失败返回 null。</summary>
        private static XElement? TryParseFragment(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.IndexOf('<') < 0) return null;
            try
            {
                var doc = XDocument.Parse("<root>" + text + "</root>");
                return doc.Root?.Elements().FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private string Read(string path)
        {
            var current = _doc.Root;
            foreach (var part in path.Split('/'))
            {
                if (current == null) return "";
                current = current.Element(_ns + part);
            }
            return current?.Value.Trim() ?? "";
        }

        /// <summary>
        /// 取子元素：已有的直接用；没有就尝试「还原」被注释掉的同名标签（位置不变）；
        /// 都没有才追加到父元素末尾。
        /// </summary>
        private XElement EnsureElement(XElement parent, string name)
        {
            var existing = parent.Element(_ns + name);
            if (existing != null) return existing;

            // 情形一：成对注释（<!-- <name></name> -->）——注释就地换成空元素。
            foreach (var node in parent.Nodes())
            {
                if (node is not XComment comment || !Matches(PairedTag, comment.Value, name)) continue;
                var element = new XElement(_ns + name);
                comment.ReplaceWith(element);
                return element;
            }

            // 情形二：容器被整段注释掉（<!-- <windows> --> … <!-- </windows> -->）——
            // 还原开始标签，把两者之间的兄弟节点（含那些被注释掉的子标签）收进新元素，
            // 再删掉结束注释。这样内部子标签仍能被后续就地还原。
            foreach (var node in parent.Nodes())
            {
                if (node is not XComment comment || !Matches(OpenTag, comment.Value, name)) continue;

                var element = new XElement(_ns + name);
                var moved = new List<XNode>();
                XComment? closing = null;
                for (var next = comment.NextNode; next != null; next = next.NextNode)
                {
                    if (next is XComment candidate && Matches(CloseTag, candidate.Value, name))
                    {
                        closing = candidate;
                        break;
                    }
                    moved.Add(next);
                }

                // 没有配对的结束注释（例如 <!-- <foo/> -->）：只就地换成元素，不搬动兄弟节点。
                if (closing == null)
                {
                    comment.ReplaceWith(element);
                    return element;
                }

                comment.ReplaceWith(element);
                foreach (var sibling in moved) sibling.Remove();
                element.Add(moved);
                closing.Remove();
                return element;
            }

            var appended = new XElement(_ns + name);
            parent.Add(appended);
            return appended;
        }

        private static bool Matches(Regex pattern, string comment, string name)
        {
            var match = pattern.Match(comment);
            return match.Success
                && string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal);
        }
    }

    /// <summary>一条应用图标声明：尺寸（<c>NxN</c>，对应描述符里的 <c>imageNxN</c>）与图标文件路径。</summary>
    internal readonly record struct AppIconEntry(string Size, string Path);

    /// <summary>
    /// 描述符内各元素的路径（'/' 分层），集中一处供构建窗口与引擎侧共用，
    /// 避免把元素名字符串散落在各处。大小写按 AIR 描述符原样保留。
    /// </summary>
    internal static class AppDescriptorPath
    {
        // 顶层
        public const string Id = "id";
        public const string Filename = "filename";
        public const string DisplayName = "name";
        public const string Version = "versionNumber";
        public const string VersionLabel = "versionLabel";
        public const string Description = "description";
        public const string Copyright = "copyright";
        public const string PublisherId = "publisherID";
        public const string AllowMultipleInstances = "allowMultipleInstances";

        // initialWindow
        public const string WindowTitle = "initialWindow/title";
        public const string WindowWidth = "initialWindow/width";
        public const string WindowHeight = "initialWindow/height";
        public const string WindowMinSize = "initialWindow/minSize";
        public const string WindowMaxSize = "initialWindow/maxSize";
        public const string SystemChrome = "initialWindow/systemChrome";
        public const string Transparent = "initialWindow/transparent";
        public const string Visible = "initialWindow/visible";
        public const string Resizable = "initialWindow/resizable";
        public const string Minimizable = "initialWindow/minimizable";
        public const string Maximizable = "initialWindow/maximizable";
        public const string RenderMode = "initialWindow/renderMode";
        public const string DepthAndStencil = "initialWindow/depthAndStencil";
        public const string RequestedDisplayResolution = "initialWindow/requestedDisplayResolution";
        public const string UseAngle = "initialWindow/useAngle";

        // windows（localAppData 自 AIR 51.3.1 起可用）
        public const string WindowsLocalAppData = "windows/localAppData";
        public const string WindowsMaxD3D = "windows/maxD3D";
        public const string WindowsUseWebView2 = "windows/UseWebView2";
        public const string WindowsUseDirectDrawFonts = "windows/useDirectDrawFonts";

        // android
        public const string AndroidColorDepth = "android/colorDepth";
        public const string AndroidBuildArchitectures = "android/buildArchitectures";
        public const string AndroidContainsVideo = "android/containsVideo";
        public const string AndroidSupportsAndroidTV = "android/supportsAndroidTV";
        public const string AndroidWebContentsDebugging = "android/webContentsDebuggingEnabled";
        public const string AndroidCreateAppBundle = "android/createAppBundle";
        public const string AndroidPreventDeviceModelAccess = "android/preventDeviceModelAccess";
        public const string AndroidAsyncStartup = "android/asyncStartup";
        public const string AndroidUseCamera2 = "android/useCamera2";

        // iPhone
        public const string IosRequestedDisplayResolution = "iPhone/requestedDisplayResolution";
        public const string IosForceCpuRenderDevices = "iPhone/forceCPURenderModeForDevices";
        public const string IosDisableCustomKeyboard = "iPhone/disableCustomKeyboard";
        public const string IosExternalSwfs = "iPhone/externalSwfs";
    }
}

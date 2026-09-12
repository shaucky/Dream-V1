using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Dream.Studio.Engine;
using Dream.Studio.Panels;

#pragma warning disable CS0618 // 旧版拖拽 API：DataObject/FileNames/DoDragDrop 在 11.3 已过时但仍可用。

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 项目浏览器面板：以列表形式浏览当前项目目录，支持文件夹展开/收起、
    /// 文件图标（自定义优先、系统回退）、文件/目录拖拽（拖出 Studio 时保留原件）。
    ///
    /// 根节点只显示 asconfig.json 中 source-path 与 library-path 声明的目录，
    /// 其余根级文件（asconfig.json、app 描述符等）不在列表中显示。
    /// 配置缺失或解析失败时回退为显示整个项目根目录。
    ///
    /// 设计要点：
    /// - 用扁平 <see cref="ObservableCollection{T}"/> + 缩进模拟树形列表，
    ///   避免 TreeView 的虚拟化/选择机制干扰自定义行交互。
    /// - 子目录懒加载：首次展开时枚举一级子项，避免启动时全量遍历。
    /// - 拖拽仅允许 <see cref="DragDropEffects.Copy"/>：外部目标（Explorer 等）
    ///   收到的是复制语义，不会移动/删除源文件。
    ///   
    /// Meta 系统：
    /// - source-path 目录下的每个文件伴随同名 .meta 文件，内含 GUID 与 assetType。
    /// - 资源引用（如 SpriteRenderer 的 textureGuid）指向 GUID 而非文件路径，
    ///   使重命名/移动文件不会破坏场景引用。
    /// - 文件移动/重命名时，.meta 文件随原件一起移动。
    /// </summary>
    public sealed class ProjectPanel : UserControl
    {
        private string _projectRoot;
        private readonly FileIconService _icons;
        private readonly ObservableCollection<ProjectNode> _flat = new();

        // 滚动容器：全量重建行会把滚动位置重置到顶部，需在重建后恢复。
        private readonly ScrollViewer _scroll;
        private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", EnginePaths.ProjectDataDirectoryName };

        // 由项目设置得出的构建产物目录名（动态加入 _excluded）：记下来便于切换项目时替换。
        private string _buildOutputDirName = "";

        // 图片文件扩展名集合，用于缩略图预览与拖拽到 Hierarchy 创建 SpriteRenderer。
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        // 缩略图缓存：路径 → Bitmap，避免重复解码。
        private readonly Dictionary<string, Bitmap?> _thumbnailCache = new();

        // 元数据数据库：维护 GUID → 路径映射，自动生成/加载 .meta 文件。
        // 切换项目根目录时整体重建（索引与项目目录绑定）。
        private MetaDatabase _metaDb = new();

        // 图集合图缓存：把 .dmatlas 定义的精灵合到项目的 .dream/atlas，供编辑期与运行使用。
        private readonly SpriteAtlasCache _atlasCache = new();

        // 最近一次同步得到的可用图集（供资源索引与图集映射推送）。
        private IReadOnlyList<CachedAtlas> _cachedAtlases = Array.Empty<CachedAtlas>();

        // asconfig.json 中 source-path 与 library-path 解析出的绝对路径集合。
        // 根节点仅加载这些目录；为空（配置缺失）时回退为枚举整个项目根。
        private List<string> _configuredRoots = new();

        // 仅 source-path 解析出的绝对路径（用于推导 AS3 包名：文件目录相对 source 根，
        // 路径分隔符替换为点）。与 _configuredRoots 区分：后者还含 library-path。
        private List<string> _sourceRoots = new();

        private ProjectNode? _root;

        // 拖拽状态：按下后未超过阈值时为"待定"，移动超阈值才真正发起 DoDragDrop。
        private ProjectNode? _pressNode;
        private Point _pressPos;
        private bool _dragStarted;

        // 选中状态：当前选中节点及其对应行 Border，用于切换高亮。
        private ProjectNode? _selectedNode;
        private Border? _selectedRow;

        // 新建文件后待重命名的节点路径：BuildRow 渲染到该节点时自动进入重命名。
        // 用于"新建文件后自动重命名"流程，避免在行未渲染时调用 BeginRename。
        private string? _pendingRenamePath;

        // 重命名态变化事件：true=进入重命名（TextBox 焦点），false=结束。
        // EngineSession 订阅以在重命名期间挂起 .as 文件热重载，避免打断用户输入。
        public event Action<bool>? RenamingStateChanged;

        /// <summary>双击 .space 场景文件时触发，参数为场景文件绝对路径。</summary>
        public event Action<string>? SceneLoadRequested;

        /// <summary>双击 .prefab 预制体文件时触发（进视口编辑），参数为预制体文件绝对路径。</summary>
        public event Action<string>? PrefabOpenRequested;

        /// <summary>双击 .dmclip 动画片段时触发，参数为动画片段文件绝对路径。</summary>
        public event Action<string>? ClipOpenRequested;

        /// <summary>双击 .dmanimator 动画状态机控制器时触发，参数为控制器文件绝对路径。</summary>
        public event Action<string>? AnimatorOpenRequested;

        /// <summary>资源集合变化（切分精灵 / 打包图集）后触发：参数为结果摘要（成功或失败原因）。
        /// 订阅方应把摘要写入 Console，并重新推送资源索引与图集映射——两者都新增了资源。</summary>
        public event Action<string>? AssetsChanged;

        private const double DragThreshold = 4;
        private static readonly IBrush SelectedBrush =
            new SolidColorBrush(Color.FromRgb(0x2A, 0x4D, 0x7A));
        private static readonly IBrush HoverBrush =
            new SolidColorBrush(Color.FromArgb(30, 0xFF, 0xFF, 0xFF));

        public ProjectPanel(string projectRoot)
        {
            _projectRoot = projectRoot;
            _icons = CreateIconService();

            var list = new ItemsControl
            {
                ItemsSource = _flat,
                ItemTemplate = new FuncDataTemplate<ProjectNode>((node, _) => BuildRow(node!)),
            };

            var scroll = new ScrollViewer
            {
                Content = list,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26)),
            };
            DragDrop.SetAllowDrop(scroll, true);
            _scroll = scroll;
            Content = scroll;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // 注册 Project 面板自身的拖放：接受外部文件导入 + 内部移动。
            AddHandler(DragDrop.DropEvent, OnProjectDrop);
            AddHandler(DragDrop.DragOverEvent, OnProjectDragOver);
            // 不在此处自动加载：首次启动时模板克隆尚未完成（StartAsync 晚于 attach），
            // 此时加载会读到上一次遗留的工作目录结构。由 EngineSession.ProjectReady
            // 事件在克隆完成后显式调用 Refresh。
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            RemoveHandler(DragDrop.DropEvent, OnProjectDrop);
            RemoveHandler(DragDrop.DragOverEvent, OnProjectDragOver);
        }

        /// <summary>当前项目根目录（绝对路径）。</summary>
        public string ProjectRoot => _projectRoot;

        /// <summary>
        /// 切换项目根目录：重建元数据索引并清空文件树，随后调用 Refresh 加载新项目。
        /// 用于打开任意项目目录（就地访问）。
        /// </summary>
        public void SetProjectRoot(string projectRoot)
        {
            if (string.Equals(_projectRoot, projectRoot, StringComparison.OrdinalIgnoreCase)) return;
            _projectRoot = projectRoot;
            _metaDb = new MetaDatabase();
            _thumbnailCache.Clear();
            _configuredRoots = new List<string>();
            _sourceRoots = new List<string>();
            _root = null;
            _flat.Clear();
            _selectedNode = null;
            _selectedRow = null;
        }

        /// <summary>重新加载项目树。保留已展开的目录/精灵表与选中项。</summary>
        public void Refresh()
        {
            // 全量重建会创建全新的节点对象：先按路径记录展开状态与选中项，重建后恢复。
            // 否则任何一次重建（新建文件、文件热重载、资源变更）都会把整棵树收起。
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectExpandedPaths(_root, expanded);
            // 精灵节点与所在精灵表共用路径，按路径无法区分，故不参与选中恢复。
            var selectedPath = _selectedNode?.SpriteGuid == null ? _selectedNode?.FullPath : null;
            var scrollOffset = _scroll.Offset;

            _flat.Clear();

            if (!Directory.Exists(_projectRoot)) return;

            // 构建产物目录不进资源树（与 MetaDatabase 的扫描排除一致）。
            ApplyBuildOutputExclusion();
            _configuredRoots = LoadConfiguredRoots();

            var rootName = Path.GetFileName(_projectRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(rootName)) rootName = _projectRoot;

            // 刷新 Meta 索引：扫描整个项目根目录，为缺失文件生成 .meta。
            _metaDb.ScanDirectory(_projectRoot);

            _root = new ProjectNode(rootName, _projectRoot, true);
            LoadChildren(_root);
            _root.IsExpanded = true;
            RestoreExpanded(_root, expanded);
            Flatten(_root, 0);

            // 行已全部重建：由 BuildRow 按 _selectedNode 重新套用高亮，旧行引用作废。
            _selectedRow = null;
            _selectedNode = selectedPath == null ? null : FindNodeByPath(_root, selectedPath);

            // 新行的内容要等本次布局完成才有滚动范围，此刻设置会被夹到 0，故延迟到 Loaded 再恢复。
            if (scrollOffset.Y > 0)
                Dispatcher.UIThread.Post(() => _scroll.Offset = scrollOffset, DispatcherPriority.Loaded);
        }

        /// <summary>把项目设置里的构建产物目录加入排除集（切换项目时替换上一次的项）。</summary>
        private void ApplyBuildOutputExclusion()
        {
            if (_buildOutputDirName.Length > 0) _excluded.Remove(_buildOutputDirName);
            _buildOutputDirName = DreamProjectSettings.ResolveOutputTopDirectoryName(_projectRoot);
            if (_buildOutputDirName.Length > 0) _excluded.Add(_buildOutputDirName);
        }

        /// <summary>收集当前树中已展开的可展开节点路径（重建前调用）。</summary>
        private static void CollectExpandedPaths(ProjectNode? node, HashSet<string> into)
        {
            if (node == null) return;
            foreach (var child in node.Children)
            {
                if (!child.IsExpandable || !child.IsExpanded) continue;
                into.Add(child.FullPath);
                CollectExpandedPaths(child, into);
            }
        }

        /// <summary>按路径集合恢复展开状态：只递归进入已展开的节点（子级仍是懒加载）。</summary>
        private void RestoreExpanded(ProjectNode node, HashSet<string> expanded)
        {
            if (!node.IsExpandable) return;
            foreach (var child in node.Children)
            {
                if (!expanded.Contains(child.FullPath)) continue;
                if (!child.ChildrenLoaded) LoadChildren(child);
                child.IsExpanded = true;
                RestoreExpanded(child, expanded);
            }
        }

        /// <summary>
        /// 读取 asconfig.json，合并 source-path 与 library-path 解析为绝对路径。
        /// 配置缺失或解析失败时返回空列表（调用方回退为枚举整个项目根）。
        /// </summary>
        private List<string> LoadConfiguredRoots()
        {
            var roots = new List<string>();
            _sourceRoots = new List<string>();
            var asconfigPath = Path.Combine(_projectRoot, "asconfig.json");
            if (!File.Exists(asconfigPath)) return roots;

            try
            {
                var cfg = Asconfig.Load(asconfigPath);
                var opts = cfg.CompilerOptions;
                if (opts?.SourcePath != null)
                    foreach (var sp in opts.SourcePath)
                        AddConfiguredRoot(roots, sp, _sourceRoots);
                if (opts?.LibraryPath != null)
                    foreach (var lp in opts.LibraryPath)
                        AddConfiguredRoot(roots, lp);
            }
            catch { /* 解析失败静默：回退为整个项目根 */ }
            return roots;
        }

        /// <summary>将相对/绝对路径规范化为绝对路径，去重并仅保留存在的目录。
        /// sourceRoots 非空时同步登记（用于推导 AS3 包名）。</summary>
        private void AddConfiguredRoot(List<string> roots, string relativeOrAbsolute, List<string>? sourceRoots = null)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolute)) return;
            var abs = Path.GetFullPath(Path.Combine(_projectRoot, relativeOrAbsolute));
            if (!Directory.Exists(abs)) return;
            foreach (var r in roots)
                if (string.Equals(r, abs, StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(abs);
            sourceRoots?.Add(abs);
        }

        // ── 树操作 ──────────────────────────────────────────────

        private void LoadChildren(ProjectNode node)
        {
            if (node.ChildrenLoaded || node.IsSprite) return;
            node.ChildrenLoaded = true;

            // 精灵表：展开列出表内的精灵（虚拟节点，各自带 GUID，可单独拖拽到资源字段）。
            if (!node.IsDirectory)
            {
                if (string.Equals(Path.GetExtension(node.FullPath), SpriteSheetFile.Extension,
                        StringComparison.OrdinalIgnoreCase))
                    LoadSheetSprites(node);
                return;
            }

            // 根节点：仅显示 asconfig.json 中声明的 source-path / library-path 目录。
            // 配置缺失（_configuredRoots 为空）时回退为枚举整个项目根。
            if (ReferenceEquals(node, _root) && _configuredRoots.Count > 0)
            {
                foreach (var abs in _configuredRoots)
                {
                    var name = Path.GetFileName(abs.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    node.Children.Add(new ProjectNode(name, abs, true));
                }
                // 根级 .space 场景文件直接显示在根节点下：保存对话框起始位置是项目根，
                // 场景若直接存到根目录（不在 source-path 下）也能在 Project 面板发现。
                foreach (var file in Directory.EnumerateFiles(node.FullPath))
                {
                    if (string.Equals(Path.GetExtension(file), ".space", StringComparison.OrdinalIgnoreCase))
                        node.Children.Add(CreateNode(file, false));
                }
                return;
            }

            try
            {
                var entries = new List<(string Path, bool IsDir)>();
                foreach (var dir in Directory.EnumerateDirectories(node.FullPath))
                {
                    var name = Path.GetFileName(dir);
                    if (_excluded.Contains(name)) continue;
                    entries.Add((dir, true));
                }
                foreach (var file in Directory.EnumerateFiles(node.FullPath))
                {
                    // .meta 文件不单独显示，作为原文件的附属数据。
                    if (string.Equals(Path.GetExtension(file), ".meta", StringComparison.OrdinalIgnoreCase))
                        continue;
                    entries.Add((file, false));
                }

                // 目录在前、文件在后，各组按名称排序。
                entries.Sort((a, b) =>
                {
                    int c = b.IsDir.CompareTo(a.IsDir);
                    return c != 0 ? c
                        : string.CompareOrdinal(Path.GetFileName(a.Path), Path.GetFileName(b.Path));
                });

                foreach (var (path, isDir) in entries)
                    node.Children.Add(CreateNode(path, isDir));
            }
            catch { /* 枚举失败（权限等）静默：已加载标记避免重试 */ }
        }

        /// <summary>创建节点。精灵表标记为可展开：展开后列出表内的精灵。</summary>
        private static ProjectNode CreateNode(string path, bool isDirectory)
        {
            var node = new ProjectNode(Path.GetFileName(path), path, isDirectory);
            if (!isDirectory && string.Equals(Path.GetExtension(path), SpriteSheetFile.Extension,
                    StringComparison.OrdinalIgnoreCase))
                node.IsExpandable = true;
            return node;
        }

        /// <summary>把精灵表内的精灵加载为子节点；每个精灵节点携带自己的 GUID。</summary>
        private void LoadSheetSprites(ProjectNode sheetNode)
        {
            var sheet = SpriteSheetFile.Read(sheetNode.FullPath);
            if (sheet == null) return;
            AddSpriteNodes(sheetNode, sheet.Sprites.Select(s => (s.Name, s.Guid)));
        }

        private static void AddSpriteNodes(ProjectNode parent, IEnumerable<(string Name, string Guid)> sprites)
        {
            foreach (var (name, guid) in sprites)
            {
                parent.Children.Add(new ProjectNode(name, parent.FullPath, false)
                {
                    SpriteGuid = guid,
                });
            }
        }

        /// <summary>
        /// 展开/收起节点：增量更新扁平列表，只插入/移除该节点的子树行，
        /// 不再清空重建整棵树（避免展开时所有行闪烁、行被销毁导致双击手势中断）。
        /// <paramref name="syncRowVisuals"/> 由发起切换的行提供：该行自身不会被重建，
        /// 其三角形与图标须就地同步，否则视觉会停留在切换前的状态。
        /// </summary>
        private void ToggleExpand(ProjectNode node, Action? syncRowVisuals = null)
        {
            if (!node.IsExpandable) return;

            if (node.IsExpanded)
            {
                node.IsExpanded = false;
                RemoveSubtreeAfter(node);
            }
            else
            {
                node.IsExpanded = true;
                if (!node.ChildrenLoaded) LoadChildren(node);
                var index = _flat.IndexOf(node);
                InsertChildren(node, index + 1, node.IndentLevel + 1);
            }
            syncRowVisuals?.Invoke();
        }

        /// <summary>展开指示三角：可展开节点为 ▼/▶，其余留空占位以保持各行列对齐。</summary>
        private static string ChevronGlyph(ProjectNode node)
            => node.IsExpandable ? (node.IsExpanded ? "\u25BC" : "\u25B6") : "";

        /// <summary>把 parent 的子级（含已展开子级的子树）插入 _flat 的 startIndex 起，返回下一个可插入位置。</summary>
        private int InsertChildren(ProjectNode parent, int startIndex, int level)
        {
            var i = startIndex;
            foreach (var child in parent.Children)
            {
                child.IndentLevel = level;
                _flat.Insert(i++, child);
                if (child.IsExpandable && child.IsExpanded)
                    i = InsertChildren(child, i, level + 1);
            }
            return i;
        }

        /// <summary>移除 _flat 中 node 之后的整棵子树（缩进大于 node 的连续项，即其全部后代）。</summary>
        private void RemoveSubtreeAfter(ProjectNode node)
        {
            var i = _flat.IndexOf(node) + 1;
            while (i < _flat.Count && _flat[i].IndentLevel > node.IndentLevel)
                _flat.RemoveAt(i);
        }

        private void RebuildFlatList()
        {
            _flat.Clear();
            if (_root != null) Flatten(_root, 0);
        }

        private void Flatten(ProjectNode node, int level)
        {
            node.IndentLevel = level;
            _flat.Add(node);
            if (node.IsExpandable && node.IsExpanded)
                foreach (var child in node.Children)
                    Flatten(child, level + 1);
        }

        // ── 行构建 ──────────────────────────────────────────────

        private Control BuildRow(ProjectNode node)
        {
            // 缩进：每级 14px。
            var indent = new Thickness(node.IndentLevel * 14 + 2, 0, 0, 0);

            var chevron = new TextBlock
            {
                Width = 14,
                FontSize = 8,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99)),
                Text = ChevronGlyph(node),
            };

            var icon = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = GetIconForNode(node),
            };

            // 展开/收起是增量更新（行本身不重建），故就地同步本行的三角形与文件夹开合图标。
            void SyncExpandVisuals()
            {
                chevron.Text = ChevronGlyph(node);
                icon.Source = GetIconForNode(node);
            }

            // 仅点击三角形展开/收起；单击行其他区域只选中。
            // 同步执行：增量更新不会销毁当前行（旧实现全量重建需延迟 Post 以免在事件中
            // 销毁控件，但延迟的 toggle 会与行 DoubleTapped 竞态导致"展开又被收起"）。
            if (node.IsExpandable)
            {
                chevron.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    ToggleExpand(node, SyncExpandVisuals);
                };
            }

            var name = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6)),
                Text = node.Name,
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = indent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(chevron);
            panel.Children.Add(icon);
            panel.Children.Add(name);

            var row = new Border
            {
                Height = 22,
                Child = panel,
                Background = Brushes.Transparent,
            };

            // 初次构建时若已是选中项，立即高亮（Rebuild 后保持选中视觉）。
            if (ReferenceEquals(_selectedNode, node))
            {
                row.Background = SelectedBrush;
                _selectedRow = row;

                // 新建文件后待重命名：行渲染完成即进入重命名。
                if (_pendingRenamePath != null
                    && string.Equals(node.FullPath, _pendingRenamePath, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingRenamePath = null;
                    // 延迟到当前布局周期之后，确保 row 已加入视觉树再聚焦 TextBox。
                    // preserveExtension=true：仅选中主名，保留扩展名。
                    Dispatcher.UIThread.Post(() => BeginRename(node, preserveExtension: true));
                }
            }

            // hover 高亮：仅当非选中行才显示 hover 色。
            row.PointerEntered += (_, _) =>
            {
                if (!ReferenceEquals(_selectedRow, row))
                    row.Background = HoverBrush;
            };
            row.PointerExited += (_, _) =>
            {
                if (!ReferenceEquals(_selectedRow, row))
                    row.Background = Brushes.Transparent;
            };

            // 交互：按下选中并记录拖拽起点；移动超阈值发起拖拽；释放无拖拽则文件夹切换展开。
            row.PointerPressed += (_, e) => OnRowPressed(node, row, e);
            row.PointerMoved += (_, e) => OnRowMoved(e);
            row.PointerReleased += (_, e) => OnRowReleased(node, e);
            // 双击文件：以默认程序打开；展开型节点切换展开。
            row.DoubleTapped += (_, _) => OnRowDoubleTapped(node, SyncExpandVisuals);
            // 右键菜单：常规文件操作。
            row.ContextMenu = BuildContextMenu(node);

            return row;
        }

        // ── 图标获取 ──────────────────────────────────────────

        /// <summary>
        /// 获取节点图标。图片文件使用缩略图预览，其余使用 FileIconService。
        /// </summary>
        private IImage? GetIconForNode(ProjectNode node)
        {
            if (!node.IsDirectory && IsImageFile(node.FullPath))
            {
                var thumb = GetThumbnail(node.FullPath);
                if (thumb != null) return thumb;
            }
            return _icons.GetIcon(node.FullPath, node.IsDirectory, node.IsExpanded);
        }

        /// <summary>判断文件是否为支持的图片格式。</summary>
        internal static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path);
            return ImageExtensions.Contains(ext);
        }

        /// <summary>判断路径是否为 .meta 文件。</summary>
        internal static bool IsMetaFile(string path)
            => string.Equals(Path.GetExtension(path), ".meta", StringComparison.OrdinalIgnoreCase);

        /// <summary>Project 面板内部拖拽时使用的自定义数据格式：携带资源 GUID。</summary>
        internal const string AssetGuidFormat = "application/x-dream-asset-guid";

        /// <summary>根据文件绝对路径返回 GUID。未注册返回 null。</summary>
        public string? GetGuid(string path) => _metaDb.ResolveGuid(path);

        /// <summary>根据 GUID 返回文件绝对路径。未找到返回 null。</summary>
        public string? ResolvePath(string guid) => _metaDb.ResolvePath(guid);

        /// <summary>根据 GUID 查询资源类型（如 "texture"/"audio"）。未找到返回 null。</summary>
        public string? GetAssetType(string guid) => _metaDb.GetAssetType(guid);

        /// <summary>返回索引中所有指定扩展名的文件路径（如 ".prefab"）。</summary>
        public IReadOnlyList<string> PathsWithExtension(string extension) => _metaDb.PathsWithExtension(extension);

        /// <summary>
        /// 资源索引条目：文件资源 + 精灵子资源（精灵 GUID 指向所属 .dmsheet，附精灵名）
        /// + 缓存图集纹理（GUID 指向项目 .dream/atlas 里的合图产物，无精灵名）。
        /// 供 EngineSession 推送 resource.index，使组件能按精灵 GUID 引用。
        /// </summary>
        internal IReadOnlyList<ResourceIndexEntry> GetResourceEntries()
        {
            var entries = new List<ResourceIndexEntry>(_metaDb.Count);
            foreach (var kv in _metaDb.GetIndex())
                entries.Add(new ResourceIndexEntry(kv.Key, kv.Value, null));
            foreach (var kv in _metaDb.GetSpriteIndex())
                entries.Add(new ResourceIndexEntry(kv.Key, kv.Value.SheetPath, kv.Value.Name));
            foreach (var atlas in _cachedAtlases)
                entries.Add(new ResourceIndexEntry(atlas.TextureGuid, atlas.TexturePath, null));
            return entries;
        }

        /// <summary>汇总当前可用图集的覆盖映射，供推送 sprite.atlasMap。</summary>
        internal IReadOnlyList<AtlasMapEntry> GetAtlasMap()
        {
            var entries = new List<AtlasMapEntry>();
            foreach (var atlas in _cachedAtlases)
                entries.AddRange(atlas.Entries);
            return entries;
        }

        /// <summary>根据 GUID 返回显示名：精灵显示精灵名，图集纹理显示图集名，文件显示文件名。</summary>
        public string? GetDisplayName(string guid)
        {
            var spriteName = _metaDb.ResolveSpriteName(guid);
            if (spriteName != null) return spriteName;
            foreach (var atlas in _cachedAtlases)
            {
                if (string.Equals(atlas.TextureGuid, guid, StringComparison.OrdinalIgnoreCase))
                    return atlas.Name + "_atlas";
            }
            var path = ResolvePath(guid);
            return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
        }

        /// <summary>加载图片缩略图（32px 宽，2x 供高清显示）。结果缓存在 _thumbnailCache。</summary>
        private Bitmap? GetThumbnail(string path)
        {
            if (_thumbnailCache.TryGetValue(path, out var cached))
                return cached;
            try
            {
                using var stream = File.OpenRead(path);
                _thumbnailCache[path] = Bitmap.DecodeToWidth(stream, 32);
            }
            catch
            {
                _thumbnailCache[path] = null;
            }
            return _thumbnailCache[path];
        }

        /// <summary>构建右键菜单。按节点类型（文件夹/文件）附加不同菜单项。</summary>
        private ContextMenu BuildContextMenu(ProjectNode node)
        {
            var menu = new ContextMenu { FontSize = 11 };
            var items = new List<MenuItem>();

            // 精灵节点是精灵表内的虚拟子资源：没有独立文件，只提供只读操作。
            if (node.IsSprite)
            {
                var copyNameItem = new MenuItem { Header = "Copy name" };
                copyNameItem.Click += (_, _) => CopyText(node.Name);
                items.Add(copyNameItem);

                var revealSheetItem = new MenuItem { Header = "Reveal Sheet in Explorer" };
                revealSheetItem.Click += (_, _) => OpenInExplorer(node);
                items.Add(revealSheetItem);

                foreach (var spriteItem in items) menu.Items.Add(spriteItem);
                return menu;
            }

            // 文件夹：附加 New 子菜单（新建空场景 / 新建 ActionScript 组件 / 动画片段）。
            // 根节点（项目目录）除外：其子节点经过类型筛选，新建的文件通常不可见。
            if (node.IsDirectory && !ReferenceEquals(node, _root))
            {
                var newMenu = new MenuItem { Header = "New" };

                var newFolderItem = new MenuItem { Header = "Folder" };
                newFolderItem.Click += (_, _) => CreateNewFolder(node);
                newMenu.Items.Add(newFolderItem);

                var newSceneItem = new MenuItem { Header = "Scene" };
                newSceneItem.Click += (_, _) => CreateNewScene(node);
                newMenu.Items.Add(newSceneItem);

                var newPrefabItem = new MenuItem { Header = "Prefab" };
                newPrefabItem.Click += (_, _) => CreateNewPrefab(node);
                newMenu.Items.Add(newPrefabItem);

                var newScriptItem = new MenuItem { Header = "ActionScript Component" };
                newScriptItem.Click += (_, _) => CreateNewActionScriptComponent(node);
                newMenu.Items.Add(newScriptItem);

                var newClipItem = new MenuItem { Header = "Animation Clip" };
                newClipItem.Click += (_, _) => CreateNewAnimationClip(node);
                newMenu.Items.Add(newClipItem);

                var newAnimatorItem = new MenuItem { Header = "Animator Controller" };
                newAnimatorItem.Click += (_, _) => CreateNewAnimatorController(node);
                newMenu.Items.Add(newAnimatorItem);

                // 图集定义：声明该目录下的精灵合到一张图集；合图产物落在项目 .dream/atlas 里。
                var newAtlasItem = new MenuItem { Header = "Sprite Atlas" };
                newAtlasItem.Click += (_, _) => CreateNewSpriteAtlas(node);
                newMenu.Items.Add(newAtlasItem);

                items.Add(newMenu);

                items.Add(new MenuItem { Header = "-" });
            }

            // 纹理：切成多个命名精灵。精灵是组件的引用单位，切分完即可用（与打包无关）。
            if (!node.IsDirectory && IsImageFile(node.FullPath))
            {
                var sliceItem = new MenuItem { Header = "Slice into Sprites..." };
                sliceItem.Click += async (_, _) => await SliceTextureAsync(node);
                items.Add(sliceItem);
                items.Add(new MenuItem { Header = "-" });
            }

            // 打开：文件用默认程序，文件夹用资源管理器。
            var openItem = new MenuItem { Header = "Open" };
            openItem.Click += (_, _) => OnRowDoubleTapped(node);
            items.Add(openItem);

            var explorerItem = new MenuItem { Header = "Reveal in Explorer" };
            explorerItem.Click += (_, _) => OpenInExplorer(node);
            items.Add(explorerItem);
            items.Add(new MenuItem { Header = "-" }); // 分隔线

            var copyPathItem = new MenuItem { Header = "Copy path" };
            copyPathItem.Click += (_, _) => CopyText(node.FullPath);
            items.Add(copyPathItem);
            items.Add(new MenuItem { Header = "-" });

            // 剪切/复制/粘贴：剪贴板操作，跨进程兼容（用系统剪贴板）。
            var cutItem = new MenuItem { Header = "Cut" };
            cutItem.Click += (_, _) => SetClipboard(node, cut: true);
            items.Add(cutItem);

            var copyItem = new MenuItem { Header = "Copy" };
            copyItem.Click += (_, _) => SetClipboard(node, cut: false);
            items.Add(copyItem);

            // 粘贴：仅目录可用，且需剪贴板有内容。
            var pasteItem = new MenuItem
            {
                Header = "Paste",
                IsEnabled = node.IsDirectory && _clipboard != null,
            };
            pasteItem.Click += (_, _) => PasteFromClipboard(node);
            items.Add(pasteItem);

            items.Add(new MenuItem { Header = "-" });

            var renameItem = new MenuItem { Header = "Rename" };
            renameItem.Click += (_, _) => BeginRename(node);
            items.Add(renameItem);

            var deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += (_, _) => DeleteNode(node);
            items.Add(deleteItem);

            foreach (var item in items) menu.Items.Add(item);
            return menu;
        }

        // ── 新建文件 ──────────────────────────────────────────

        /// <summary>在指定目录下新建文件夹（重名自动加序号），并自动进入重命名。</summary>
        private void CreateNewFolder(ProjectNode dir)
        {
            if (!dir.IsDirectory) return;
            var path = ResolveUniquePath(Path.Combine(dir.FullPath, "NewFolder"));
            try
            {
                Directory.CreateDirectory(path);
                SelectCreatedNode(dir, path);
            }
            catch { }
        }

        /// <summary>
        /// 在指定目录下新建场景文件（.space，含唯一根节点与主相机），并自动进入重命名。
        /// </summary>
        private void CreateNewScene(ProjectNode dir)
        {
            if (!dir.IsDirectory) return;
            var path = ResolveUniquePath(Path.Combine(dir.FullPath, "NewScene.space"));
            try
            {
                File.WriteAllText(path, EmptySceneTemplate);
                EnsureMeta(path);
                SelectCreatedNode(dir, path);
            }
            catch { }
        }

        /// <summary>
        /// 新建场景文件模板：Root（Transform） + Camera（Transform + CameraComponent，挂 Root 下）。
        /// 结构须与 SceneSerializer 的反序列化约定一致（parentId 为 elements 数组下标），
        /// 与 DreamEngine.ensureCameraOn 自动创建的形态相同，加载时不会重复建相机。
        /// </summary>
        private const string EmptySceneTemplate =
            "{\n" +
            "  \"elements\": [\n" +
            "    {\n" +
            "      \"name\": \"Root\",\n" +
            "      \"parentId\": -1,\n" +
            "      \"enabled\": true,\n" +
            "      \"components\": [\n" +
            "        { \"type\": \"Transform\", \"fields\": { \"position\": {\"x\":0,\"y\":0}, \"rotation\":0, \"scale\": {\"x\":1,\"y\":1} } }\n" +
            "      ]\n" +
            "    },\n" +
            "    {\n" +
            "      \"name\": \"Camera\",\n" +
            "      \"parentId\": 0,\n" +
            "      \"enabled\": true,\n" +
            "      \"components\": [\n" +
            "        { \"type\": \"Transform\", \"fields\": { \"position\": {\"x\":0,\"y\":0}, \"rotation\":0, \"scale\": {\"x\":1,\"y\":1} } },\n" +
            "        { \"type\": \"CameraComponent\", \"fields\": { \"orthographicSize\": 250 } }\n" +
            "      ]\n" +
            "    }\n" +
            "  ]\n" +
            "}";

        /// <summary>在指定目录下新建预制体文件（.prefab，单个空根节点），并自动进入重命名。</summary>
        private void CreateNewPrefab(ProjectNode dir)
        {
            if (!dir.IsDirectory) return;
            var path = ResolveUniquePath(Path.Combine(dir.FullPath, "NewPrefab.prefab"));
            try
            {
                File.WriteAllText(path, EmptyPrefabTemplate);
                EnsureMeta(path);
                SelectCreatedNode(dir, path);
            }
            catch { }
        }

        /// <summary>
        /// 新建预制体模板：单个根元素（仅 Transform）。格式与 .space 同构（见 SceneSerializer），
        /// 由 SceneSerializer.instantiate 直接读回，因此可从 Project 面板拖进场景实例化。
        /// </summary>
        private const string EmptyPrefabTemplate =
            "{\n" +
            "  \"elements\": [\n" +
            "    {\n" +
            "      \"name\": \"PrefabRoot\",\n" +
            "      \"parentId\": -1,\n" +
            "      \"enabled\": true,\n" +
            "      \"components\": [\n" +
            "        { \"type\": \"Transform\", \"fields\": { \"position\": {\"x\":0,\"y\":0}, \"rotation\":0, \"scale\": {\"x\":1,\"y\":1} } }\n" +
            "      ]\n" +
            "    }\n" +
            "  ]\n" +
            "}";

        /// <summary>
        /// 外部（如 Hierarchy 的 "Create Prefab..."）写出新资产文件后调用：
        /// 补 .meta、刷新文件树，使新资产立刻可被拖拽引用。
        /// </summary>
        public void RegisterNewAsset(string path)
        {
            EnsureMeta(path);
            Refresh();
        }

        /// <summary>在指定目录下新建 ActionScript 组件文件（基于模板），并自动进入重命名。</summary>
        private void CreateNewActionScriptComponent(ProjectNode dir)
        {
            if (!dir.IsDirectory) return;
            var path = ResolveUniquePath(Path.Combine(dir.FullPath, "NewComponent.as"));
            try
            {
                File.WriteAllText(path, ActionScriptComponentTemplate);
                EnsureMeta(path);
                SelectCreatedNode(dir, path);
            }
            catch { }
        }

        /// <summary>在指定目录下新建动画片段文件（.dmclip，空轨道模板），并自动进入重命名。</summary>
        private void CreateNewAnimationClip(ProjectNode dir)
        {
            if (!dir.IsDirectory) return;
            var path = ResolveUniquePath(Path.Combine(dir.FullPath, "NewClip.dmclip"));
            try
            {
                File.WriteAllText(path, EmptyClipTemplate);
                EnsureMeta(path);
                SelectCreatedNode(dir, path);
            }
            catch { }
        }

        /// <summary>在指定目录下新建动画状态机控制器文件（.dmanimator，默认单状态模板），并自动进入重命名。</summary>
        private void CreateNewAnimatorController(ProjectNode dir)
        {
            if (!dir.IsDirectory) return;
            var path = ResolveUniquePath(Path.Combine(dir.FullPath, "NewController.dmanimator"));
            try
            {
                File.WriteAllText(path, EmptyAnimatorTemplate);
                EnsureMeta(path);
                SelectCreatedNode(dir, path);
            }
            catch { }
        }

        /// <summary>
        /// 在指定目录下新建图集定义（.dmatlas）。默认打包范围即该目录（folder = "."），
        /// 于是"在某目录建图集 = 该目录下的精灵进图集"，与 Unity 的操作一致。
        /// 文件本身不含图像，合图结果落在项目 .dream/atlas 里。
        /// </summary>
        private void CreateNewSpriteAtlas(ProjectNode dir)
        {
            if (!dir.IsDirectory) return;
            var path = ResolveUniquePath(Path.Combine(dir.FullPath, "NewAtlas" + SpriteAtlasDefinition.Extension));
            try
            {
                SpriteAtlasDefinition.Write(path, new SpriteAtlasDefinition
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Folder = ".",
                });
                EnsureMeta(path);
                SelectCreatedNode(dir, path);
                // 新定义可能立刻就能合图（目录下已有精灵）：通知订阅方同步缓存并推送。
                AssetsChanged?.Invoke($"[atlas] {Path.GetFileNameWithoutExtension(path)}: definition created");
            }
            catch { }
        }

        /// <summary>
        /// 为新文件生成 .meta（GUID + 资源类型）并注册到 Meta 索引。
        /// 全量 Refresh 的目录扫描只发生在启动/保存后；New 菜单创建的文件在拖拽等
        /// GUID 引用场景中需要立即有 GUID，故创建时同步生成。
        /// </summary>
        private void EnsureMeta(string path)
        {
            var metaPath = MetaFile.GetMetaPath(path);
            if (File.Exists(metaPath)) return;
            var meta = new MetaFile
            {
                Guid = MetaFile.GenerateGuid(),
                AssetType = MetaDatabase.InferAssetTypeStatic(path),
            };
            try
            {
                File.WriteAllText(metaPath, meta.ToJson());
                _metaDb.Register(meta.Guid, path);
            }
            catch { }
        }

        /// <summary>
        /// 把纹理切成多个命名精灵，写出/更新 &lt;纹理名&gt;.dmsheet。
        ///
        /// 精灵是组件引用的主单位：本操作**不涉及打包**，切完即可拖到 SpriteRenderer 的
        /// Sprite 字段使用。重新切分时按精灵名复用已有 GUID，场景引用不被打断。
        /// 结果（含失败原因）通过 <see cref="AssetsChanged"/> 上报。
        /// </summary>
        private async Task SliceTextureAsync(ProjectNode node)
        {
            var texturePath = node.FullPath;
            if (!IsImageFile(texturePath)) return;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            var baseName = Path.GetFileNameWithoutExtension(texturePath);
            var sheetPath = Path.Combine(Path.GetDirectoryName(texturePath)!, baseName + SpriteSheetFile.Extension);

            // 重新切分：沿用上次的行列设置作为默认值。
            var existing = SpriteSheetFile.Read(sheetPath);
            var settings = await SliceDialog.PickAsync(owner, Path.GetFileName(texturePath),
                existing?.Slice ?? new SliceSettings());
            if (settings == null) return;

            try
            {
                var size = GetImagePixelSize(texturePath);
                if (size == null)
                {
                    AssetsChanged?.Invoke($"[slice] {baseName}: failed to decode texture");
                    return;
                }

                var rects = SpriteSlicer.Slice(settings, size.Value.Width, size.Value.Height);
                if (rects.Count == 0)
                {
                    AssetsChanged?.Invoke($"[slice] {baseName}: no sprites produced");
                    return;
                }

                // 精灵表要引用源纹理 GUID，先确保纹理已登记。
                EnsureMeta(texturePath);

                var sprites = BuildSprites(baseName, rects, settings, existing);
                SpriteSheetFile.Write(sheetPath, baseName, GetGuid(texturePath) ?? "", settings, sprites);
                EnsureMeta(sheetPath);

                Refresh();
                AssetsChanged?.Invoke(
                    $"[slice] {baseName}: {sprites.Count} sprite(s) -> {Path.GetFileName(sheetPath)}");
            }
            catch (Exception ex)
            {
                AssetsChanged?.Invoke($"[slice] {baseName}: {ex.Message}");
            }
        }

        /// <summary>按切分结果生成精灵定义；与上次同名的精灵沿用原 GUID，使已有引用保持有效。</summary>
        private static List<SpriteEntry> BuildSprites(string baseName, IReadOnlyList<SliceRect> rects,
                                                      SliceSettings settings, SpriteSheetFile? existing)
        {
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null)
                foreach (var s in existing.Sprites) previous[s.Name] = s.Guid;

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var single = rects.Count == 1;
            var result = new List<SpriteEntry>(rects.Count);

            for (int i = 0; i < rects.Count; i++)
            {
                var name = SpriteName(baseName, i, settings, single);
                if (!used.Add(name)) name = $"{name}_{i}";
                var rect = rects[i];
                result.Add(new SpriteEntry
                {
                    // 同名沿用旧 GUID；否则新分配。
                    Guid = previous.TryGetValue(name, out var guid) ? guid : MetaFile.GenerateGuid(),
                    Name = name,
                    X = rect.X,
                    Y = rect.Y,
                    W = rect.W,
                    H = rect.H,
                });
            }
            return result;
        }

        /// <summary>精灵命名：切出多个时用"表名_行_列"（便于阅读与跨表区分），只有单个时直接用表名。</summary>
        private static string SpriteName(string baseName, int index, SliceSettings settings, bool single)
        {
            if (single) return baseName;
            var columns = Math.Max(1, settings.Columns);
            return $"{baseName}_{index / columns}_{index % columns}";
        }

        /// <summary>读取图片像素尺寸。失败返回 null。</summary>
        private static PixelSize? GetImagePixelSize(string path)
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

        /// <summary>
        /// 同步全部图集定义：按内容签名决定是否重新合图，产物落在项目的 .dream/atlas 里，
        /// 资源目录中不出现图集图像。图集只是透明覆盖层——缓存缺失或合图失败时，
        /// 引擎回落精灵自身的源纹理，场景引用不受影响。
        /// 返回失败摘要（调用方写入 Console），不抛异常。
        /// </summary>
        internal IReadOnlyList<string> SyncAtlases()
        {
            var errors = new List<string>();
            _cachedAtlases = _atlasCache.Sync(_projectRoot,
                _metaDb.PathsWithExtension(SpriteAtlasDefinition.Extension), _metaDb, errors);
            return errors;
        }

        /// <summary>
        /// 动画片段模板：空轨道。字段与引擎 AnimationClip.fromJson 解析约定一致
        /// （name/duration/loop/tracks[{componentType,field,fieldType,keys[{time,value}]}]）。
        /// fps 为编辑期帧率（关键帧时间对齐），引擎按时间采样会忽略该字段。
        /// </summary>
        private const string EmptyClipTemplate =
            "{\n" +
            "  \"name\": \"NewClip\",\n" +
            "  \"duration\": 1.0,\n" +
            "  \"fps\": 24,\n" +
            "  \"loop\": true,\n" +
            "  \"tracks\": []\n" +
            "}\n";

        /// <summary>
        /// 动画状态机控制器模板：单默认状态、空参数/空过渡。
        /// 字段与引擎 AnimatorController.fromJson 解析约定一致
        /// （name/defaultState/parameters[{name,type,value}]/
        ///  states[{name,clipGuid}]/transitions[{from,to,hasExitTime,exitTime,
        ///  conditions[{param,op,value}]}]）。
        /// </summary>
        private const string EmptyAnimatorTemplate =
            "{\n" +
            "  \"name\": \"NewController\",\n" +
            "  \"defaultState\": \"Idle\",\n" +
            "  \"parameters\": [],\n" +
            "  \"states\": [\n" +
            "    { \"name\": \"Idle\", \"clipGuid\": \"\" }\n" +
            "  ],\n" +
            "  \"transitions\": []\n" +
            "}\n";

        /// <summary>
        /// 确保目录展开、刷新子级，选中新建文件节点并安排自动重命名。
        /// RebuildFlatList 后行异步渲染，BuildRow 检测 _pendingRenamePath 触发 BeginRename。
        /// </summary>
        private void SelectCreatedNode(ProjectNode dir, string createdPath)
        {
            if (!dir.IsExpanded)
            {
                LoadChildren(dir);
                dir.IsExpanded = true;
            }
            RefreshParent(dir);
            // 在刷新后的子级中按路径定位新建节点并选中。
            var created = FindNodeByPath(_root, createdPath);
            if (created != null)
            {
                _selectedNode = created;
                _selectedRow = null; // 行尚未渲染，BuildRow 会重新赋值
                _pendingRenamePath = createdPath;
                RebuildFlatList(); // 触发行重建，BuildRow 中命中 _pendingRenamePath 启动重命名
            }
        }

        /// <summary>在树中按 FullPath 查找节点（大小写不敏感，Windows 文件系统语义）。</summary>
        private static ProjectNode? FindNodeByPath(ProjectNode? root, string path)
        {
            if (root == null) return null;
            if (string.Equals(root.FullPath, path, StringComparison.OrdinalIgnoreCase))
                return root;
            foreach (var child in root.Children)
            {
                var found = FindNodeByPath(child, path);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// ActionScript 组件代码模板：继承 DreamComponent，含无参构造、
        /// Inspector 反射钩子（getInspectableFields/setFieldValue）骨架。
        /// 占位符 {ClassName} 与 {path.to.package} 在创建后由 ApplyClassNameToTemplate 替换：
        /// 类名由文件名推导；包名由文件相对 source-path 的目录推导（src 根 → 空=默认包）。
        /// </summary>
        private const string ActionScriptComponentTemplate =
            "package {path.to.package}\n" +
            "{\n" +
            "\timport dream.engine.ecs.DreamComponent;\n" +
            "\tCONFIG::STUDIO\n" +
            "\t{\n" +
            "\t\timport dream.engine.ecs.FieldInfo;\n" +
            "\t}\n" +
            "\n" +
            "\t/**\n" +
            "\t * 自定义组件：继承 DreamComponent，由 Inspector \"Add Component\" 反射创建。\n" +
            "\t */\n" +
            "\tpublic final class {ClassName} extends DreamComponent\n" +
            "\t{\n" +
            "\t\t//private var _content:String = \"\";\n" +
            "\n" +
            "\t\t// ── Inspector 反射 ──\n" +
            "\n" +
            "\t\tCONFIG::STUDIO\n" +
            "\t\toverride public function getInspectableFields():Array\n" +
            "\t\t{\n" +
            "\t\t\treturn [/*new FieldInfo(\"content\", \"Content\", \"string\", _content)*/];\n" +
            "\t\t}\n" +
            "\n" +
            "\t\toverride public function setFieldValue(fieldName:String, value:*):void\n" +
            "\t\t{\n" +
            "\t\t\t/*\n" +
            "\t\t\tswitch (fieldName)\n" +
            "\t\t\t{\n" +
            "\t\t\t\tcase \"content\": _content = value; break;\n" +
            "\t\t\t}\n" +
            "\t\t\t*/\n" +
            "\t\t}\n" +
            "\n" +
            "\t\tpublic function {ClassName}()\n" +
            "\t\t{\n" +
            "\t\t}\n" +
            "\n" +
            "\t\toverride protected function onEnterFrame(dt:Number):void\n" +
            "\t\t{\n" +
            "\t\t\t//trace(_content);\n" +
            "\t\t}\n" +
            "\t}\n" +
            "}\n";

        // ── 右键菜单命令实现 ────────────────────────────────────

        private void OpenInExplorer(ProjectNode node)
        {
            try
            {
#if WINDOWS
                // Windows：explorer.exe，目录直接打开，文件用 /select 定位选中。
                var args = node.IsDirectory
                    ? $"\"{node.FullPath}\""
                    : $"/select,\"{node.FullPath}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = args,
                    UseShellExecute = true,
                });
#else
                // 非 Windows：以系统默认方式打开所在目录。
                Process.Start(new ProcessStartInfo
                {
                    FileName = node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath) ?? "",
                    UseShellExecute = true,
                });
#endif
            }
            catch { }
        }

        private void CopyText(string text)
        {
            try
            {
                var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                cb?.SetTextAsync(text);
            }
            catch { }
        }

        // 简易剪贴板：仅记录节点路径与剪切标记，不依赖系统剪贴板跨进程语义。
        private record struct ClipboardEntry(string Path, bool Cut);
        private ClipboardEntry? _clipboard;

        private void SetClipboard(ProjectNode node, bool cut)
        {
            _clipboard = new ClipboardEntry(node.FullPath, cut);
        }

        private void PasteFromClipboard(ProjectNode target)
        {
            if (_clipboard is not { } entry || !target.IsDirectory) return;
            if (!File.Exists(entry.Path) && !Directory.Exists(entry.Path)) return;

            var srcName = Path.GetFileName(entry.Path);
            var dst = Path.Combine(target.FullPath, srcName);
            if (string.Equals(entry.Path, dst, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                // 同名时跳过或加后缀：(2)、(3)…
                dst = ResolveUniquePath(dst);

                if (Directory.Exists(entry.Path))
                    CopyDirectory(entry.Path, dst);
                else
                    File.Copy(entry.Path, dst);

                // 剪切语义：粘贴后删除源。
                if (entry.Cut && !string.Equals(entry.Path, dst, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(entry.Path)) Directory.Delete(entry.Path, true);
                    else File.Delete(entry.Path);
                }
                _clipboard = null;
                RefreshParent(target);
            }
            catch { }
        }

        private static string ResolveUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.EnumerateDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        private void DeleteNode(ProjectNode node)
        {
            try
            {
                if (node.IsDirectory) Directory.Delete(node.FullPath, true);
                else File.Delete(node.FullPath);
                // 删除根节点时整体刷新，否则刷新其父目录。
                var parent = FindParentNode(_root, node);
                RefreshParent(parent ?? _root!);
            }
            catch { }
        }

        /// <summary>行内重命名：把文本块切换为可编辑 TextBox，回车提交、Esc 取消。</summary>
        /// <param name="preserveExtension">true 时仅选中文件名部分（不含扩展名），
        /// 提交时若用户未输入扩展名则自动补回原扩展名。用于新建文件后自动重命名。</param>
        private void BeginRename(ProjectNode node, bool preserveExtension = false)
        {
            if (_selectedRow?.Child is not StackPanel panel) return;
            if (panel.Children[2] is not TextBlock nameBlock) return;

            var textBox = new TextBox
            {
                Text = node.Name,
                FontSize = 12,
                Padding = new Thickness(2),
                MinWidth = 80,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children[2] = textBox;
            textBox.Focus();
            RenamingStateChanged?.Invoke(true);

            // preserveExtension：仅选中主名（不含扩展名），让用户只编辑名称部分。
            var origExt = preserveExtension ? Path.GetExtension(node.Name) : "";
            if (preserveExtension && origExt.Length > 0)
            {
                var stem = Path.GetFileNameWithoutExtension(node.Name);
                textBox.Text = stem;
                textBox.SelectionStart = 0;
                textBox.SelectionEnd = stem.Length;
            }
            else
            {
                textBox.SelectAll();
            }

            string? newName = null;
            void Commit()
            {
                if (newName != null) return; // 防重入
                newName = textBox.Text;
                try
                {
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        // preserveExtension：若用户未输入扩展名则补回原扩展名。
                        if (preserveExtension && origExt.Length > 0
                            && !string.Equals(Path.GetExtension(newName), origExt, StringComparison.OrdinalIgnoreCase))
                            newName += origExt;

                        if (newName != node.Name)
                        {
                            var invalid = Path.GetInvalidFileNameChars();
                            if (newName.IndexOfAny(invalid) >= 0) goto Restore;
                            var dir = Path.GetDirectoryName(node.FullPath) ?? "";
                            var dst = Path.Combine(dir, newName);
                            // 仅大小写不同的改名（Art → art）：大小写不敏感的文件系统上"目标已存在"
                            // 命中的其实是源自身，且 .NET 的 Directory.Move 会以"源与目标必须不同"
                            // 直接抛 IOException —— 这两种情况都要放行，否则改名被静默丢弃、行名回弹。
                            var caseOnly = string.Equals(dst, node.FullPath, StringComparison.OrdinalIgnoreCase);
                            if (!caseOnly && (File.Exists(dst) || Directory.Exists(dst))) goto Restore;

                            var oldPath = node.FullPath;
                            if (node.IsDirectory)
                            {
                                if (caseOnly)
                                {
                                    // 目录无法一步改大小写：先改到临时名，再改到目标名（两步都是真实改名）。
                                    var temp = Path.Combine(dir, node.Name + ".renaming");
                                    while (File.Exists(temp) || Directory.Exists(temp)) temp += "_";
                                    Directory.Move(node.FullPath, temp);
                                    Directory.Move(temp, dst);
                                }
                                else Directory.Move(node.FullPath, dst);
                            }
                            else File.Move(node.FullPath, dst);

                            // Meta 索引与 .meta 文件随原件一起移动。
                            _metaDb.Move(oldPath, dst);

                            node.Name = newName;
                            node.FullPath = dst;

                            // ActionScript 组件：将输入名转为合法类名，替换模板占位符 {ClassName}。
                            if (string.Equals(Path.GetExtension(newName), ".as", StringComparison.OrdinalIgnoreCase))
                                ApplyClassNameToTemplate(dst);
                        }
                    }
                }
                catch { }
            Restore:
                // 通知订阅方重命名已结束（EngineSession 恢复热重载监听）。
                RenamingStateChanged?.Invoke(false);
                // 异步重建：避免在焦点/键事件回调中同步修改集合，
                // Clear() 触发 TextBox 失焦 → LostFocus → Commit 重入导致 NPE。
                Dispatcher.UIThread.Post(RebuildFlatList);
            }

            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape)
                {
                    // 标记已处理：阻止后续 LostFocus 触发的 Commit 应用更改或重入 RebuildFlatList。
                    newName = node.Name;
                    RenamingStateChanged?.Invoke(false);
                    Dispatcher.UIThread.Post(RebuildFlatList);
                    e.Handled = true;
                }
            };
            textBox.LostFocus += (_, _) => Commit();
        }

        /// <summary>
        /// 替换模板占位符：{ClassName} → 由文件名推导的合法标识符；
        /// {path.to.package} → 由文件目录相对 source-path 根推导的包名（路径分隔符转点）。
        /// 文件不在任何 source-path 下时包名为空（AS3 默认包）。
        /// </summary>
        private void ApplyClassNameToTemplate(string filePath)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var className = ToValidIdentifier(fileName);
                var pkg = ResolvePackageName(Path.GetDirectoryName(filePath) ?? "");
                var content = File.ReadAllText(filePath);
                var updated = content
                    .Replace("{path.to.package}", pkg)
                    .Replace("{ClassName}", className);
                if (updated != content) File.WriteAllText(filePath, updated);
            }
            catch { }
        }

        /// <summary>根据文件所在目录相对 source-path 根推导 AS3 包名。
        /// 如 src/dream/engine/ecs/components 相对 src → dream.engine.ecs.components。
        /// 不在任何 source 根下返回空串（默认包）。</summary>
        private string ResolvePackageName(string fileDir)
        {
            foreach (var root in _sourceRoots)
            {
                var rel = Path.GetRelativePath(root, fileDir);
                // GetRelativePath 在同目录时返回 "."，视为默认包。
                if (rel == ".") return "";
                // 跨盘符或不可达时返回绝对路径，跳过。
                if (Path.IsPathRooted(rel)) continue;
                // 路径分隔符替换为点，组装包名。
                var pkg = rel.Replace(Path.DirectorySeparatorChar, '.')
                             .Replace(Path.AltDirectorySeparatorChar, '.');
                return pkg;
            }
            return "";
        }

        /// <summary>将任意文件名转为能过 AS3 编译的标识符（最小修正）。</summary>
        private static string ToValidIdentifier(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "Component";

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < fileName.Length; i++)
            {
                var c = fileName[i];
                // 字母、数字、下划线、$ 均为 AS3 标识符合法字符。
                if (char.IsLetterOrDigit(c) || c == '_' || c == '$')
                    sb.Append(c);
                else
                    sb.Append('_'); // 非法字符替换为下划线，保留用户原名的近似形态
            }

            if (sb.Length == 0) return "Component";

            // 首字符非字母/下划线/$（如数字开头）则前缀下划线。
            var first = sb[0];
            if (!(char.IsLetter(first) || first == '_' || first == '$'))
                sb.Insert(0, '_');

            return sb.ToString();
        }

        /// <summary>查找 directChild 在树中的父节点（基于引用相等）。</summary>
        private static ProjectNode? FindParentNode(ProjectNode? root, ProjectNode target)
        {
            if (root == null) return null;
            foreach (var child in root.Children)
            {
                if (ReferenceEquals(child, target)) return root;
                var found = FindParentNode(child, target);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>刷新指定节点的子级并重建扁平列表。保持其展开状态。</summary>
        private void RefreshParent(ProjectNode node)
        {
            if (!node.IsDirectory) return;
            // 根节点的子级由 asconfig 配置决定，刷新时重新读取配置，
            // 以反映配置目录被删除/重命名或 asconfig 本身被编辑后的状态。
            if (ReferenceEquals(node, _root))
                _configuredRoots = LoadConfiguredRoots();
            node.ChildrenLoaded = false;
            node.Children.Clear();
            LoadChildren(node);
            RebuildFlatList();
        }

        /// <summary>切换选中到指定行，清除旧选中高亮。</summary>
        private void Select(ProjectNode node, Border row)
        {
            if (_selectedRow is { } old && !ReferenceEquals(old, row))
                old.Background = Brushes.Transparent;
            _selectedNode = node;
            _selectedRow = row;
            row.Background = SelectedBrush;
        }

        /// <summary>双击文件：.space 场景 / .prefab 预制体触发对应的打开请求，其它用系统默认程序打开。
        /// 文件夹/精灵表则切换展开。</summary>
        private void OnRowDoubleTapped(ProjectNode node, Action? syncRowVisuals = null)
        {
            // 展开型节点（目录、精灵表）：双击切换展开，露出子项（精灵）。
            if (node.IsExpandable)
            {
                ToggleExpand(node, syncRowVisuals);
                return;
            }

            // 精灵是表内的虚拟子资源，没有独立文件可打开。
            if (node.IsSprite) return;

            if (string.Equals(Path.GetExtension(node.FullPath), ".space", StringComparison.OrdinalIgnoreCase))
            {
                SceneLoadRequested?.Invoke(node.FullPath);
                return;
            }

            if (string.Equals(Path.GetExtension(node.FullPath), ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                PrefabOpenRequested?.Invoke(node.FullPath);
                return;
            }

            if (string.Equals(Path.GetExtension(node.FullPath), ".dmclip", StringComparison.OrdinalIgnoreCase))
            {
                ClipOpenRequested?.Invoke(node.FullPath);
                return;
            }

            if (string.Equals(Path.GetExtension(node.FullPath), ".dmanimator", StringComparison.OrdinalIgnoreCase))
            {
                AnimatorOpenRequested?.Invoke(node.FullPath);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = node.FullPath,
                    UseShellExecute = true,  // 用系统关联程序打开
                });
            }
            catch { /* 无关联程序或失败时静默 */ }
        }

        // ── 拖拽 & 点击 ─────────────────────────────────────────

        private void OnRowPressed(ProjectNode node, Border row, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
            // 按下即选中：单击高亮单项。
            Select(node, row);
            _pressNode = node;
            _pressPos = e.GetPosition(null);
            _dragStarted = false;
            e.Pointer.Capture(e.Source as IInputElement);
        }

        private async void OnRowMoved(PointerEventArgs e)
        {
            if (_pressNode == null || _dragStarted) return;

            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _pressPos.X) < DragThreshold &&
                Math.Abs(pos.Y - _pressPos.Y) < DragThreshold)
                return;

            _dragStarted = true;
            var node = _pressNode;
            // 精灵节点是表内的虚拟子资源：GUID 指向精灵本身，路径仍指向所在精灵表。
            var guid = node.SpriteGuid ?? GetGuid(node.FullPath);

            var data = new DataObject();
            data.Set(DataFormats.FileNames, new[] { node.FullPath });
            // 内部拖拽用自定义格式携带 GUID，避免路径解析失败；外部拖拽仍用 FileNames。
            if (!string.IsNullOrEmpty(guid))
                data.Set(AssetGuidFormat, guid);
            // 保留 Text 供外部目标读取路径；精灵虚拟节点没有独立文件，不设置以免被面板
            // 的内部移动逻辑当作可移动文件。
            if (!node.IsSprite)
                data.Set(DataFormats.Text, node.FullPath);

            // 跨窗口资源拖拽：Avalonia 内置拖拽仅在同一窗口内路由，浮动面板（如 Animator 编辑器）
            // 是独立窗口，从主窗口拖入无法命中内置拖放。此处并行跟踪全局鼠标，光标位于其它
            // Studio 窗口并命中资源字段时，由 CrossWindowDragService 代为完成拖放。
            if (!string.IsNullOrEmpty(guid) && TopLevel.GetTopLevel(this) is Window sourceWindow)
                CrossWindowDragService.TrackDrag(sourceWindow, new DragPayload
                {
                    Guid = guid,
                    Path = node.FullPath,
                    FileName = node.Name,
                });

            // 仅 Move：面板内部移动文件/目录；外部目标收到 FileNames 后复制由对方决定。
            try
            {
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            }
            finally
            {
                CrossWindowDragService.EndTrack();
            }
        }

        private void OnRowReleased(ProjectNode node, PointerReleasedEventArgs e)
        {
            if (_pressNode != null)
                e.Pointer.Capture(null);

            // 单击行只选中；文件夹展开/收起由行首三角形处理（此前的"点击切换展开"
            // 会与选中、拖拽、双击打开等交互冲突）。
            _pressNode = null;
            _dragStarted = false;
        }

        // ── 面板自身拖放：外部导入 + 内部移动 ───────────────────

        /// <summary>拖拽悬停在 Project 面板上方：判断目标目录并允许 Drop。</summary>
        private void OnProjectDragOver(object? sender, DragEventArgs e)
        {
            var targetDir = GetDropTargetDirectory(e);
            if (targetDir != null)
                e.DragEffects = DragDropEffects.Move;
            else
                e.DragEffects = DragDropEffects.None;
        }

        /// <summary>在 Project 面板释放拖拽：处理内部移动或外部导入。</summary>
        private void OnProjectDrop(object? sender, DragEventArgs e)
        {
            var targetDir = GetDropTargetDirectory(e);
            if (targetDir == null) return;

            // 优先处理内部移动（自定义 Text 格式携带源路径）。
            var sourcePath = e.Data.Get(DataFormats.Text) as string;
            if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath) || Directory.Exists(sourcePath))
            {
                MoveProjectNode(sourcePath, targetDir);
                Refresh();
                return;
            }

            // 外部导入：从 FileNames 读取文件并复制到目标目录。
            var fileNames = e.Data.Get(DataFormats.FileNames) as string[];
            if (fileNames != null)
            {
                foreach (var file in fileNames)
                {
                    if (Directory.Exists(file)) continue; // 暂不支持导入整个目录
                    ImportExternalFile(file, targetDir);
                }
                Refresh();
            }
        }

        /// <summary>命中测试：返回鼠标下方的目录节点路径。若未命中任何目录则返回项目根。</summary>
        private string? GetDropTargetDirectory(DragEventArgs e)
        {
            if (Content is not ScrollViewer scroll) return null;

            var hit = scroll.InputHitTest(e.GetPosition(scroll));
            for (var el = hit as StyledElement; el != null; el = el.Parent as StyledElement)
            {
                if (el is Border b && b.DataContext is ProjectNode node)
                {
                    // 目标是文件 → 取其所在目录；目标是目录 → 取目录本身。
                    return node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath);
                }
            }

            // 未命中任何行：落到根目录。
            return _projectRoot;
        }

        /// <summary>移动文件或目录到目标目录，.meta 文件随行。</summary>
        private void MoveProjectNode(string sourcePath, string targetDir)
        {
            try
            {
                var name = Path.GetFileName(sourcePath);
                var dst = Path.Combine(targetDir, name);
                if (string.Equals(sourcePath, dst, StringComparison.OrdinalIgnoreCase)) return;
                if (File.Exists(dst) || Directory.Exists(dst)) return;

                if (Directory.Exists(sourcePath))
                    Directory.Move(sourcePath, dst);
                else if (File.Exists(sourcePath))
                    File.Move(sourcePath, dst);
                else
                    return;

                _metaDb.Move(sourcePath, dst);
            }
            catch { }
        }

        /// <summary>导入外部文件到项目目录，并生成 .meta。</summary>
        private void ImportExternalFile(string sourcePath, string targetDir)
        {
            try
            {
                var name = Path.GetFileName(sourcePath);
                var dst = Path.Combine(targetDir, name);
                if (File.Exists(dst)) return;
                File.Copy(sourcePath, dst);

                var meta = new MetaFile
                {
                    Guid = MetaFile.GenerateGuid(),
                    AssetType = MetaDatabase.InferAssetTypeStatic(dst),
                };
                File.WriteAllText(MetaFile.GetMetaPath(dst), meta.ToJson());
                _metaDb.Register(meta.Guid, dst);
            }
            catch { }
        }

        // ── 图标服务初始化 ──────────────────────────────────────

        private static FileIconService CreateIconService()
        {
            var service = new FileIconService(new SystemFileIconProvider());

            // 自定义图标注册（矢量 DrawingImage，可无限缩放）。
            // 文件夹：浅色文件夹形状，收起/展开略有差异。
            service.RegisterFolder(
                CreateFolderIcon(false),
                CreateFolderIcon(true));

            // .as：ActionScript 源文件，橙色调。
            service.RegisterExtension(".as", CreateDocumentIcon(Color.FromRgb(0xD9, 0x7A, 0x14)));

            // .json：绿色调。
            service.RegisterExtension(".json", CreateDocumentIcon(Color.FromRgb(0x4E, 0x9A, 0x06)));

            // .dmclip：动画片段，紫罗兰色调。
            service.RegisterExtension(".dmclip", CreateDocumentIcon(Color.FromRgb(0x9A, 0x5A, 0xC8)));

            // .prefab：预制体（场景子树的资产化形式），天蓝色调。
            service.RegisterExtension(".prefab", CreateDocumentIcon(Color.FromRgb(0x3A, 0x8F, 0xD9)));

            // .dmanimator：动画状态机控制器，青色调。
            service.RegisterExtension(".dmanimator", CreateDocumentIcon(Color.FromRgb(0x2A, 0xA6, 0xB8)));

            // .dmsheet：精灵表（纹理切分出的命名精灵集合），青绿色调。
            service.RegisterExtension(".dmsheet", CreateDocumentIcon(Color.FromRgb(0x3E, 0xB8, 0x86)));

            // .dmatlas：图集定义（声明哪些精灵合到一张图集；图像在 .dream/atlas 里），金黄色调。
            service.RegisterExtension(".dmatlas", CreateDocumentIcon(Color.FromRgb(0xC8, 0x9A, 0x2A)));

            // .mp3/.wav：音频资源，红色调。
            var audioIcon = CreateDocumentIcon(Color.FromRgb(0xD9, 0x4A, 0x4A));
            service.RegisterExtension(".mp3", audioIcon);
            service.RegisterExtension(".wav", audioIcon);

            return service;
        }

        private static IImage CreateFolderIcon(bool open)
        {
            // 简洁文件夹形状：底部矩形 + 顶部凸起 tab。
            var body = StreamGeometry.Parse("M1,5 L1,14 L15,14 L15,5 Z");
            var tab = StreamGeometry.Parse("M1,5 L6,5 L7,3 L12,3 L12,5");

            var group = new DrawingGroup();
            var fill = new SolidColorBrush(Color.FromRgb((byte)(open ? 0x8C : 0x6E), 0x8C, 0x5A));
            var stroke = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x3E));

            group.Children.Add(new GeometryDrawing { Geometry = tab, Brush = fill });
            group.Children.Add(new GeometryDrawing
            {
                Geometry = body,
                Brush = fill,
                Pen = new Pen(stroke, 0.8),
            });

            return new DrawingImage(group);
        }

        private static IImage CreateDocumentIcon(Color color)
        {
            // 文档形状：矩形 + 右上角折叠角。
            var body = StreamGeometry.Parse("M3,1 L11,1 L15,5 L15,15 L3,15 Z");
            var corner = StreamGeometry.Parse("M11,1 L11,5 L15,5");

            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing
            {
                Geometry = body,
                Brush = new SolidColorBrush(color),
                Pen = new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)), 0.8),
            });
            group.Children.Add(new GeometryDrawing
            {
                Geometry = corner,
                Brush = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            });

            return new DrawingImage(group);
        }
    }
}

#pragma warning restore CS0618

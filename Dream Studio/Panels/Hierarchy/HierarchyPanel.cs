using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dream.Studio.Panels.Project;

#pragma warning disable CS0618 // 旧版拖拽 API：DragEventArgs.Data/DoDragDrop 在 11.3 已过时但仍可用。

namespace Dream.Studio.Panels.Hierarchy
{
    /// <summary>
    /// Hierarchy 面板：以扁平列表 + 缩进模拟树形，展示引擎元素层级。
    ///
    /// 数据由引擎端 HierarchyBridge 每 0.5 秒推送快照，通过
    /// HierarchySnapshotHandler 反序列化后调用 <see cref="UpdateSnapshot"/> 更新。
    ///
    /// 右键菜单操作（创建/删除/重命名）通过 <see cref="CommandRequested"/> 事件
    /// 冒泡到 MainWindow，转发到 EngineSession 发送到引擎。
    ///
    /// 设计要点（与 ProjectPanel 一致）：
    /// - 扁平 ObservableCollection + 缩进，避免 TreeView 虚拟化干扰。
    /// - 选中高亮 + hover 反馈。
    /// - 行内重命名：TextBlock ↔ TextBox 切换。
    /// </summary>
    public sealed class HierarchyPanel : UserControl
    {
        private readonly ObservableCollection<HierarchyNode> _flat = new();
        private HierarchyNode? _root;
        private ScrollViewer? _scrollViewer;

        private HierarchyNode? _selectedNode;
        private Border? _selectedRow;

        // 多选支持：选中 ID 集合（无序），_primaryId 为主选中（最近操作，用于批量变换基准）。
        private readonly HashSet<int> _selectedIds = new();
        private int _primaryId = -1;

        // 元素 ID → 已渲染行。行内点击时仅刷新这些行背景，不重建列表（避免
        // 销毁正在交互/拖拽的行控件）；重建时整表更新。
        private readonly Dictionary<int, Border> _rowById = new();

        // 两个独立的暂停标志，互不干扰：
        // _isContextMenuOpen：右键菜单弹出期间，防止 row 被重建导致菜单闪退。
        // _isRenaming：行内重命名期间，防止 row 被重建导致 TextBox 销毁。
        private bool _isContextMenuOpen;
        private bool _isRenaming;
        // 缓存菜单/重命名期间到达的快照，交互结束后统一应用，避免丢失更新。
        private List<HierarchyNodeData>? _pendingSnapshot;

        // 拖拽 reparent 状态：左键按下记录起点，移动超过阈值进入拖拽，
        // 释放时按命中目标行 + 区域（前/后/内）发送 reparent 命令到引擎。
        private HierarchyNode? _dragSource;
        private Point _dragOrigin;
        private bool _isDragging;
        private Border? _dropTargetRow;
        private DropZone _dropZone = DropZone.None;

        private enum DropZone { None, Before, After, Child }

        // 元素剪贴板（进程内静态，跨面板实例保持）：Hierarchy 复制/粘贴元素用。
        internal static List<int> ClipboardIds = new();

        private static readonly IBrush SelectedBrush =
            new SolidColorBrush(Color.FromRgb(0x2A, 0x4D, 0x7A));
        private static readonly IBrush HoverBrush =
            new SolidColorBrush(Color.FromArgb(30, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush DimBrush =
            new SolidColorBrush(Color.FromRgb(0x6A, 0x70, 0x79));
        private static readonly IBrush DropTargetBrush =
            new SolidColorBrush(Color.FromArgb(70, 0x4A, 0x9A, 0x8C));
        private static readonly IBrush InsertLineBrush =
            new SolidColorBrush(Color.FromRgb(0x6E, 0xC8, 0xB4));
        // 预制体实例图标：实例根用亮蓝（它是摆放节点），实例内其余元素用暗蓝。
        private static readonly Color PrefabRootColor = Color.FromRgb(0x6E, 0x9B, 0xD1);
        private static readonly Color PrefabChildColor = Color.FromRgb(0x4A, 0x6C, 0x8F);

        /// <summary>
        /// 命令请求事件。参数：(action, payload)。
        /// action 为 "create"/"destroy"/"rename"，payload 为命令数据。
        /// </summary>
        public event Action<string, object?>? CommandRequested;

        /// <summary>选中节点变化时触发，参数为主选中元素 ID（-1 表示取消选中）。</summary>
        public event Action<int>? SelectionChanged;

        /// <summary>请求把元素（含子树）导出为 prefab 资产时触发，参数：(元素 ID, 元素名)。
        /// 实际导出（引擎序列化 + 落盘 + 注册 .meta）由 MainWindow 编排。</summary>
        public event Action<int, string>? CreatePrefabRequested;

        /// <summary>请求把预制体实例还原到源时触发，参数为实例内任意元素 ID。
        /// 还原（清空整实例 override + 重推源）由 EngineSession 执行。</summary>
        public event Action<int>? RevertPrefabRequested;

        /// <summary>拖入 .prefab 资产时触发，参数：(资源 GUID, prefab 文件绝对路径)。
        /// 文件读取与实例化命令由 MainWindow 执行。</summary>
        public event Action<string, string>? PrefabDropRequested;

        /// <summary>当前选中元素 ID 的有序数组（选择顺序，末尾为主选中）。</summary>
        public IReadOnlyList<int> SelectedIds
        {
            get
            {
                var list = new List<int>();
                // 保持选择顺序：按 primary 到其余的顺序组织不必要，直接以集合迭代
                // 后附加 primary 即可（引擎侧以"最后 = primary"为约定）。
                list.AddRange(_selectedIds.Where(id => id != _primaryId));
                if (_primaryId >= 0 && _selectedIds.Contains(_primaryId)) list.Add(_primaryId);
                return list;
            }
        }

        /// <summary>Project 面板引用，用于将拖拽文件路径解析为 GUID。</summary>
        public ProjectPanel? ProjectPanel { get; set; }

        public HierarchyPanel()
        {
            var list = new ItemsControl
            {
                ItemsSource = _flat,
                ItemTemplate = new FuncDataTemplate<HierarchyNode>((node, _) => BuildRow(node!)),
            };

            _scrollViewer = new ScrollViewer
            {
                Content = list,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26)),
            };

            // 不再提供空白处右键菜单：场景始终有且仅有一个不可移除的根元素，
            // Hierarchy 永远非空，无需"Create Root"入口。

            // 接受外部拖放（如从 Project 面板拖拽图片文件创建带贴图节点）。
            DragDrop.SetAllowDrop(_scrollViewer, true);

            // 面板级快捷键（Ctrl+C/V/D 复制粘贴）：点击行时聚焦面板后生效。
            Focusable = true;
            KeyDown += OnPanelKeyDown;

            Content = _scrollViewer;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // 注册拖放事件：在视觉树就绪后绑定，确保 _scrollViewer 可用。
            AddHandler(DragDrop.DropEvent, OnExternalDrop);
            AddHandler(DragDrop.DragOverEvent, OnExternalDragOver);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            RemoveHandler(DragDrop.DropEvent, OnExternalDrop);
            RemoveHandler(DragDrop.DragOverEvent, OnExternalDragOver);
        }

        /// <summary>
        /// 外部拖放悬停：Project 面板内部拖拽（携带 GUID）允许 Move；
        /// 外部图片文件允许 Copy；其余拒绝。
        /// </summary>
        private void OnExternalDragOver(object? sender, DragEventArgs e)
        {
            var data = e.Data!;
            if (data.Contains(ProjectPanel.AssetGuidFormat))
            {
                // Project 面板内部拖拽源只允许 Move。
                e.DragEffects = DragDropEffects.Move;
            }
            else if (HasImageFile(data))
            {
                e.DragEffects = DragDropEffects.Copy;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        /// <summary>
        /// 外部拖放释放：Project 面板拖拽的图片 → 发送 create 命令（含 textureGuid），
        /// 新节点统一挂到根节点下。外部文件（无 GUID）不创建。
        /// </summary>
        private void OnExternalDrop(object? sender, DragEventArgs e)
        {
            var data = e.Data!;

            // 优先处理 Project 面板内部拖拽：自定义格式携带 GUID，避免路径解析问题。
            var internalGuid = data.Get(ProjectPanel.AssetGuidFormat) as string;
            if (!string.IsNullOrEmpty(internalGuid))
            {
                // prefab 资产：按数据实例化子树，交给 MainWindow 读文件后命令引擎。
                if (ProjectPanel?.GetAssetType(internalGuid) == "prefab")
                {
                    var prefabPath = ProjectPanel.ResolvePath(internalGuid);
                    if (!string.IsNullOrEmpty(prefabPath))
                        PrefabDropRequested?.Invoke(internalGuid, prefabPath!);
                    return;
                }
                CreateTextureNode(internalGuid);
                return;
            }

            // 外部拖入：从文件路径解析 GUID（当前项目外的文件无 GUID，会被跳过）。
            var paths = GetDroppedFilePaths(data);
            if (paths == null) return;

            var project = ProjectPanel;
            foreach (var path in paths)
            {
                if (!ProjectPanel.IsImageFile(path)) continue;
                var guid = project?.GetGuid(path);
                if (string.IsNullOrEmpty(guid)) continue;
                CreateTextureNode(guid, Path.GetFileNameWithoutExtension(path));
            }
        }

        /// <summary>发送 create 命令创建带 SpriteRenderer 的节点，parentId=-1 由引擎挂到根节点下。</summary>
        private void CreateTextureNode(string textureGuid, string? name = null)
        {
            if (string.IsNullOrEmpty(textureGuid)) return;
            CommandRequested?.Invoke("create",
                new { parentId = -1, name, textureGuid });
        }

        /// <summary>判断拖拽数据是否包含至少一个图片文件路径。</summary>
        private static bool HasImageFile(IDataObject data)
        {
            var paths = GetDroppedFilePaths(data);
            if (paths == null) return false;
            foreach (var path in paths)
                if (ProjectPanel.IsImageFile(path)) return true;
            return false;
        }

        /// <summary>
        /// 从拖拽数据读取文件路径：优先尝试新 IDataObject.GetFiles()，
        /// 失败则回退到旧 DataFormats.FileNames 字符串数组（ProjectPanel 发起）。
        /// </summary>
        private static IEnumerable<string>? GetDroppedFilePaths(IDataObject data)
        {
            // 新 API：拖入来自系统 Explorer 等来源时可用。
            try
            {
                var files = data.GetFiles();
                if (files != null)
                {
                    var list = new List<string>();
                    foreach (var f in files)
                    {
                        var p = f.Path?.LocalPath;
                        if (p != null) list.Add(p);
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch { /* 忽略 */ }

            // 旧 API：ProjectPanel 用 DataFormats.FileNames 发起拖拽。
            if (data.Contains(DataFormats.FileNames))
            {
                var names = data.Get(DataFormats.FileNames) as string[];
                if (names != null && names.Length > 0)
                    return names;
            }

            return null;
        }

        /// <summary>
        /// 用引擎推送的快照数据更新树。保留展开状态与选中状态。
        /// 在 UI 线程调用。
        /// </summary>
        internal void UpdateSnapshot(List<HierarchyNodeData> dataList)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // 菜单打开、重命名或拖拽期间缓存快照，交互结束后统一应用。
                if (_isContextMenuOpen || _isRenaming || _isDragging)
                {
                    _pendingSnapshot = dataList;
                    return;
                }
                ApplySnapshot(dataList ?? new List<HierarchyNodeData>());
            });
        }

        /** 菜单关闭或重命名/拖拽结束后，检查是否有缓存的快照需要应用。 */
        private void TryApplyPendingSnapshot()
        {
            if (_isContextMenuOpen || _isRenaming || _isDragging) return;
            if (_pendingSnapshot == null) return;
            var pending = _pendingSnapshot;
            _pendingSnapshot = null;
            ApplySnapshot(pending);
        }

        private void ApplySnapshot(List<HierarchyNodeData> dataList)
        {
            // 记录旧的展开状态与选中状态（选择集 + primary）
            var oldExpanded = new HashSet<int>();
            if (_root != null) CollectExpanded(_root, oldExpanded);
            var oldSelectedIds = new HashSet<int>(_selectedIds);
            var oldPrimary = _primaryId;

            _root = null;

            if (dataList == null || dataList.Count == 0)
            {
                _flat.Clear();
                return;
            }

            // 按 id 索引
            var byId = new Dictionary<int, HierarchyNode>();
            foreach (var d in dataList)
                byId[d.Id] = new HierarchyNode(d);

            // 构建父子关系
            List<HierarchyNode> roots = new();
            foreach (var node in byId.Values)
            {
                if (node.Data.ParentId >= 0 && byId.TryGetValue(node.Data.ParentId, out var parent))
                    parent.Children.Add(node);
                else
                    roots.Add(node);
            }

            // 虚拟根：把多个根元素挂到一个虚拟根下统一扁平化
            _root = new HierarchyNode(new HierarchyNodeData { Id = -1, Name = "" });
            foreach (var r in roots) _root.Children.Add(r);

            // 恢复展开状态：默认全部展开
            RestoreExpanded(_root, oldExpanded);

            RebuildFlatList();

            // 恢复选中：仅保留仍在树中的 id（元素可能被删除）。
            _selectedIds.Clear();
            foreach (var id in oldSelectedIds)
            {
                if (FindNode(_root!, id) != null) _selectedIds.Add(id);
            }
            _primaryId = _selectedIds.Contains(oldPrimary)
                ? oldPrimary
                : (_selectedIds.Count > 0 ? _selectedIds.First() : -1);
            _selectedNode = _primaryId >= 0 ? FindNode(_root!, _primaryId) : null;
            // RebuildFlatList 时 FinishRow 用的是旧选择集，恢复后需按新集刷新行背景。
            RefreshRowHighlights();
        }

        // ── 树操作 ──────────────────────────────────────────────

        private static void CollectExpanded(HierarchyNode node, HashSet<int> set)
        {
            if (node.IsExpanded && node.Data.Id >= 0) set.Add(node.Data.Id);
            foreach (var c in node.Children) CollectExpanded(c, set);
        }

        private static void RestoreExpanded(HierarchyNode node, HashSet<int> oldExpanded)
        {
            if (node.Data.Id >= 0)
                node.IsExpanded = oldExpanded.Count == 0 || oldExpanded.Contains(node.Data.Id);
            foreach (var c in node.Children) RestoreExpanded(c, oldExpanded);
        }

        private void RebuildFlatList()
        {
            _rowById.Clear();
            _flat.Clear();
            if (_root != null)
                foreach (var child in _root.Children)
                    Flatten(child, 0);
        }

        private void Flatten(HierarchyNode node, int level)
        {
            node.IndentLevel = level;
            _flat.Add(node);
            if (node.IsExpanded)
                foreach (var child in node.Children)
                    Flatten(child, level + 1);
        }

        private void ToggleExpand(HierarchyNode node)
        {
            node.IsExpanded = !node.IsExpanded;
            RebuildFlatList();
        }

        private static HierarchyNode? FindNode(HierarchyNode root, int id)
        {
            if (root.Data.Id == id) return root;
            foreach (var c in root.Children)
            {
                var found = FindNode(c, id);
                if (found != null) return found;
            }
            return null;
        }

        // ── 行构建 ──────────────────────────────────────────────

        private Control BuildRow(HierarchyNode node)
        {
            var indent = new Thickness(node.IndentLevel * 14 + 2, 0, 0, 0);

            var chevron = new TextBlock
            {
                Width = 14,
                FontSize = 8,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99)),
                Text = node.HasChildren ? (node.IsExpanded ? "\u25BC" : "\u25B6") : "",
            };
            // 仅点击三角形展开/收起；单击行其他区域只选中。延迟执行避免在事件中销毁控件。
            if (node.HasChildren)
            {
                chevron.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    Dispatcher.UIThread.Post(() => ToggleExpand(node));
                };
            }

            // 图标：预制体实例元素用菱形（实例根为实色、其余为暗色），普通元素为方块。
            var isPrefabInstance = !string.IsNullOrEmpty(node.Data.PrefabGuid);
            var icon = new TextBlock
            {
                Width = 16,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(isPrefabInstance
                    ? (node.Data.PrefabRoot ? PrefabRootColor : PrefabChildColor)
                    : Color.FromRgb(0xC8, 0xB4, 0x6E)),
                Text = isPrefabInstance ? "\u25C6" : "\u25A0",
            };

            var name = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = node.Data.Enabled
                    ? new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6))
                    : DimBrush,
                Text = node.Data.Name,
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

            return FinishRow(node, panel);
        }

        private Control FinishRow(HierarchyNode node, StackPanel panel)
        {
            var row = new Border
            {
                Height = 22,
                Child = panel,
                Background = Brushes.Transparent,
                DataContext = node, // 供拖拽命中测试反查节点
            };

            _rowById[node.Data.Id] = row;

            if (_selectedIds.Contains(node.Data.Id))
            {
                row.Background = SelectedBrush;
                if (node.Data.Id == _primaryId) _selectedRow = row;
            }

            row.PointerEntered += (_, _) =>
            {
                if (_isDragging) return;
                if (_selectedIds.Contains(node.Data.Id)) return;
                row.Background = HoverBrush;
            };
            row.PointerExited += (_, _) =>
            {
                if (_isDragging) return;
                // 选中行悬停退出时保持高亮（多选中其它行也归此列）。
                if (_selectedIds.Contains(node.Data.Id)) return;
                row.Background = Brushes.Transparent;
            };

            row.PointerPressed += (_, e) => OnRowPressed(node, row, e);
            row.PointerMoved += (_, e) => OnRowMoved(e);
            row.PointerReleased += (_, e) => OnRowReleased(node, e);
            row.ContextMenu = BuildContextMenu(node);

            return row;
        }

        // ── 选中与交互 ─────────────────────────────────────────

        private void Select(HierarchyNode node, Border row)
        {
            // 单选：替换整个选择集。
            _selectedIds.Clear();
            _selectedIds.Add(node.Data.Id);
            _primaryId = node.Data.Id;
            CommitRowSelection(node, row);
        }

        /// <summary>Ctrl/Shift 点击：在选中集中切换该节点并设为主选中。</summary>
        private void ToggleSelect(HierarchyNode node, Border row)
        {
            if (!_selectedIds.Remove(node.Data.Id))
            {
                _selectedIds.Add(node.Data.Id);
                _primaryId = node.Data.Id;
            }
            else if (_selectedIds.Count == 0)
            {
                _primaryId = -1;
            }
            else if (_primaryId == node.Data.Id)
            {
                // 移除的恰是主选中：primary 回退为集合中的任意一个。
                _primaryId = _selectedIds.First();
            }
            CommitRowSelection(node, row);
        }

        /// <summary>行内点击（单选/Ctrl 多选）后的提交：只刷新所有已渲染行的背景，
        /// 绝不重建列表——重建会销毁正在交互/拖拽的行控件（右键菜单、拖拽 reparent 都会失效）。</summary>
        private void CommitRowSelection(HierarchyNode node, Border row)
        {
            if (_isContextMenuOpen || _isRenaming || _isDragging)
            {
                _selectedNode = node;
                _selectedRow = null;
            }
            else
            {
                _selectedNode = node;
                _selectedRow = row;
            }
            RefreshRowHighlights();
            SelectionChanged?.Invoke(_primaryId);
        }

        /// <summary>按 _rowById 统一刷新所有已渲染行的选中背景（不重建列表）。</summary>
        private void RefreshRowHighlights()
        {
            foreach (var kv in _rowById)
                kv.Value.Background = _selectedIds.Contains(kv.Key) ? SelectedBrush : Brushes.Transparent;
        }

        /// <summary>选择集变化后的统一收尾：交互中仅记录状态，否则展开 primary 路径并重建行刷新高亮。</summary>
        private void ApplySelectionVisual(HierarchyNode? node)
        {
            if (_isContextMenuOpen || _isRenaming || _isDragging)
            {
                _selectedNode = node;
                _selectedRow = null;
                SelectionChanged?.Invoke(_primaryId);
                return;
            }

            _selectedNode = node;
            _selectedRow = null;
            if (node != null && _root != null) ExpandPathTo(_root!, node);
            RebuildFlatList(); // 重建行时 FinishRow 按 _selectedIds 重新应用高亮
            SelectionChanged?.Invoke(_primaryId);
        }

        /// <summary>
        /// 外部（引擎视口拾取 scene.picked）设置选中：选中树中对应节点并触发 SelectionChanged。
        /// 消息处理在后台线程，这里封送到 UI 线程执行（与 UpdateSnapshot 一致）。
        /// id=-1 取消选中；节点不存在时仅清空选中。会展开祖先使节点可见。
        /// </summary>
        internal void SelectElementById(int id)
            => Dispatcher.UIThread.Post(() => SelectElementByIdCore(id));

        private void SelectElementByIdCore(int id)
        {
            var root = _root;
            var node = id >= 0 && root != null ? FindNode(root, id) : null;
            _selectedIds.Clear();
            if (id >= 0) _selectedIds.Add(id);
            _primaryId = id;
            ApplySelectionVisual(node);
        }

        /// <summary>外部批量设置选中（引擎多选拾取/框选回传 scene.picked）。ids 有序，末尾为主选中。</summary>
        internal void SetSelection(IReadOnlyList<int> ids, int primaryId)
            => Dispatcher.UIThread.Post(() => SetSelectionCore(ids, primaryId));

        private void SetSelectionCore(IReadOnlyList<int> ids, int primaryId)
        {
            var root = _root;
            _selectedIds.Clear();
            foreach (var id in ids)
            {
                if (id < 0) continue;
                _selectedIds.Add(id);
            }
            _primaryId = _selectedIds.Contains(primaryId) ? primaryId
                : (_selectedIds.Count > 0 ? _selectedIds.First() : -1);

            HierarchyNode? primaryNode = null;
            if (_primaryId >= 0 && root != null) primaryNode = FindNode(root, _primaryId);
            ApplySelectionVisual(primaryNode);
        }

        /// <summary>展开 target 的所有祖先节点，使其在扁平列表中可见。</summary>
        private static void ExpandPathTo(HierarchyNode root, HierarchyNode target)
        {
            foreach (var c in root.Children)
            {
                if (ReferenceEquals(c, target))
                {
                    root.IsExpanded = true;
                    return;
                }
                if (ContainsNode(c, target))
                {
                    c.IsExpanded = true;
                    ExpandPathTo(c, target);
                    return;
                }
            }
        }

        private static bool ContainsNode(HierarchyNode root, HierarchyNode target)
        {
            if (ReferenceEquals(root, target)) return true;
            foreach (var c in root.Children)
                if (ContainsNode(c, target)) return true;
            return false;
        }

        private void OnRowPressed(HierarchyNode node, Border row, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
            {
                // 右键按下：仅更新选中状态并刷新行背景，不重建列表——否则会在
                // 菜单弹出前销毁 ContextMenu 依附的行控件，导致菜单无法弹出。
                // 菜单 Opened 后 _isContextMenuOpen 会阻止快照重建。
                _selectedIds.Clear();
                _selectedIds.Add(node.Data.Id);
                _primaryId = node.Data.Id;
                _selectedNode = node;
                _selectedRow = row;
                RefreshRowHighlights();
                SelectionChanged?.Invoke(_primaryId);
                return;
            }
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

            // Ctrl/Shift 点击：切换选中（多选）；否则单选。
            var multi = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
            if (multi) ToggleSelect(node, row);
            else Select(node, row);
            Focus(); // 面板获得键盘焦点，启用 Ctrl+C/V/D 复制粘贴快捷键
            e.Pointer.Capture(e.Source as IInputElement);

            // 记录拖拽起点：移动超过阈值才进入拖拽，否则视为普通点击（选中/展开）。
            _dragSource = node;
            _dragOrigin = e.GetPosition(null);
            _isDragging = false;
            _dropTargetRow = null;
        }

        private void OnRowMoved(PointerEventArgs e)
        {
            if (_dragSource == null) return;
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

            if (!_isDragging)
            {
                var pos = e.GetPosition(null);
                // 曼哈顿距离超过阈值才进入拖拽，避免点击误触。
                if (Math.Abs(pos.X - _dragOrigin.X) + Math.Abs(pos.Y - _dragOrigin.Y) <= 4)
                    return;
                _isDragging = true;
            }

            // 命中测试：找出当前鼠标下的目标行，按 Y 位置决定区域（前/后/内）。
            var target = HitTestRow(e.GetPosition(_scrollViewer!));
            var zone = ComputeDropZone(target, e);
            SetDropTarget(target, zone);
        }

        private void OnRowReleased(HierarchyNode node, PointerReleasedEventArgs e)
        {
            // 仅左键释放才处理；右键释放交给 ContextMenu 处理，
            // 否则 ToggleExpand → RebuildFlatList 会销毁 row 导致菜单闪退。
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Pointer.Capture(null);

            // 拖拽结束：按命中目标 + 区域发送 reparent；否则仅收尾（单击行只选中，
            // 展开/收起由行首三角形处理）。
            if (_isDragging)
            {
                var src = _dragSource;
                var targetNode = _dropTargetRow?.DataContext as HierarchyNode;
                var zone = _dropZone;
                ClearDropHighlight();
                FinishDrag();
                DispatchReparent(src, targetNode, zone);
                return;
            }

            FinishDrag();
        }

        /// <summary>根据区域计算新父级与同级参照，发送 reparent 命令。</summary>
        private void DispatchReparent(HierarchyNode? src, HierarchyNode? target, DropZone zone)
        {
            if (src == null) return;
            // 根元素不可被 reparent（场景始终只有一个根）。
            if (src.Data.ParentId < 0) return;
            if (target == null || zone == DropZone.None)
            {
                // 拖到空白处或无有效区域：提为根元素被禁止（唯一根），忽略。
                return;
            }
            if (ReferenceEquals(src, target) || IsDescendant(src, target)) return;

            switch (zone)
            {
                case DropZone.Child:
                    // 作为目标子级（追加到末尾）。
                    CommandRequested?.Invoke("reparent",
                        new { id = src.Data.Id, parentId = target.Data.Id });
                    break;
                case DropZone.Before:
                    // 插到目标前：新父级 = 目标的父级，参照 = 目标，insertBefore=true。
                    CommandRequested?.Invoke("reparent",
                        new { id = src.Data.Id, parentId = target.Data.ParentId,
                              siblingId = target.Data.Id, insertBefore = true });
                    break;
                case DropZone.After:
                    // 插到目标后：新父级 = 目标的父级，参照 = 目标，insertBefore=false。
                    CommandRequested?.Invoke("reparent",
                        new { id = src.Data.Id, parentId = target.Data.ParentId,
                              siblingId = target.Data.Id, insertBefore = false });
                    break;
            }
        }

        private void FinishDrag()
        {
            _dragSource = null;
            _isDragging = false;
            _dropTargetRow = null;
            _dropZone = DropZone.None;
            // 拖拽期间缓存的快照统一应用。
            TryApplyPendingSnapshot();
        }

        // ── 拖拽命中测试与高亮 ─────────────────────────────────

        /// <summary>在 ScrollViewer 坐标系下命中测试，返回鼠标下的行 Border。</summary>
        private Border? HitTestRow(Point point)
        {
            if (_scrollViewer == null) return null;
            var hit = _scrollViewer.InputHitTest(point);
            // 从命中元素沿逻辑父链向上查找 DataContext 为 HierarchyNode 的 Border（即行容器）。
            for (var el = hit as StyledElement; el != null; el = el.Parent as StyledElement)
            {
                if (el is Border b && b.DataContext is HierarchyNode)
                    return b;
            }
            return null;
        }

        /// <summary>
        /// 根据鼠标在目标行内的 Y 位置决定放置区域：
        /// 顶部 1/4 → Before，底部 1/4 → After，中间 1/2 → Child。
        /// 非法目标（自身/后代、根元素的 Before/After）返回 None。
        /// 根元素无父级，Before/After 会产生第二个根，故禁止。
        /// </summary>
        private DropZone ComputeDropZone(Border? target, PointerEventArgs e)
        {
            if (target == null || target.DataContext is not HierarchyNode tn) return DropZone.None;
            if (ReferenceEquals(tn, _dragSource) || IsDescendant(_dragSource, tn))
                return DropZone.None;

            var y = e.GetPosition(target).Y;
            var h = target.Bounds.Height;
            if (h <= 0) return DropZone.Child;
            // 根元素只允许 Child（无父级，Before/After 会产生第二个根）。
            if (tn.Data.ParentId < 0) return DropZone.Child;
            if (y < h * 0.25) return DropZone.Before;
            if (y > h * 0.75) return DropZone.After;
            return DropZone.Child;
        }

        /// <summary>设置当前放置目标行 + 区域高亮。</summary>
        private void SetDropTarget(Border? target, DropZone zone)
        {
            // 同行同区域：无需更新。
            if (ReferenceEquals(target, _dropTargetRow) && zone == _dropZone) return;

            // 非法目标：清除已有高亮但不设置新目标。
            if (target != null && zone == DropZone.None)
            {
                ClearDropHighlight();
                return;
            }

            ClearDropHighlight();
            _dropTargetRow = target;
            _dropZone = zone;
            if (target != null)
                ApplyDropVisual(target, zone);
        }

        /// <summary>按区域应用视觉反馈：Child=整行高亮，Before/After=上下边插入线。</summary>
        private void ApplyDropVisual(Border row, DropZone zone)
        {
            switch (zone)
            {
                case DropZone.Child:
                    row.Background = DropTargetBrush;
                    row.BorderBrush = null;
                    row.BorderThickness = new Thickness(0);
                    break;
                case DropZone.Before:
                    row.Background = Brushes.Transparent;
                    row.BorderBrush = InsertLineBrush;
                    row.BorderThickness = new Thickness(0, 2, 0, 0);
                    break;
                case DropZone.After:
                    row.Background = Brushes.Transparent;
                    row.BorderBrush = InsertLineBrush;
                    row.BorderThickness = new Thickness(0, 0, 0, 2);
                    break;
            }
        }

        private void ClearDropHighlight()
        {
            if (_dropTargetRow == null) return;
            var row = _dropTargetRow;
            // 恢复：选中行恢复选中色，否则透明；清除插入线边框。
            row.Background =
                ReferenceEquals(row, _selectedRow) ? SelectedBrush : Brushes.Transparent;
            row.BorderBrush = null;
            row.BorderThickness = new Thickness(0);
            _dropTargetRow = null;
            _dropZone = DropZone.None;
        }

        /// <summary>candidate 是否是 root 的后代（不含 root 自身）。</summary>
        private static bool IsDescendant(HierarchyNode? root, HierarchyNode candidate)
        {
            if (root == null) return false;
            foreach (var c in root.Children)
            {
                if (ReferenceEquals(c, candidate)) return true;
                if (IsDescendant(c, candidate)) return true;
            }
            return false;
        }

        // ── 右键菜单 ───────────────────────────────────────────

        /// <summary>
        /// 面板级快捷键：Ctrl+C 复制选中、Ctrl+V 粘贴到选中项下、Ctrl+D 复制副本。
        /// 注意：焦点在嵌入的 Engine 窗口时本面板收不到键盘事件，这些快捷键已在引擎侧
        /// DreamEngine.initEditorShortcuts（DreamEngine.as）兜底实现，两处须保持一致，
        /// 修改任一须同步另一处。
        /// </summary>
        private void OnPanelKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
            switch (e.Key)
            {
                case Key.C:
                    CopySelectionToClipboard();
                    e.Handled = true;
                    break;
                case Key.V:
                    PasteClipboard();
                    e.Handled = true;
                    break;
                case Key.D:
                    DuplicatePrimary();
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>复制当前选中集到剪贴板（根元素不可复制，过滤掉）。</summary>
        private void CopySelectionToClipboard()
        {
            ClipboardIds.Clear();
            foreach (var id in SelectedIds)
            {
                var n = _root != null ? FindNode(_root, id) : null;
                if (n != null && n.Data.ParentId >= 0) ClipboardIds.Add(id);
            }
        }

        /// <summary>将剪贴板元素粘贴为当前选中 primary 的子级；未选中则粘贴到根元素下。</summary>
        private void PasteClipboard()
        {
            if (ClipboardIds.Count == 0) return;
            var targetId = (_primaryId >= 0 && _selectedIds.Contains(_primaryId))
                ? _primaryId
                : FindRootId();
            if (targetId < 0) return;
            foreach (var clipId in ClipboardIds)
                CommandRequested?.Invoke("duplicate",
                    new { id = clipId, parentId = targetId, offsetX = 0, offsetY = 0 });
        }

        /// <summary>复制当前主选中元素为同父副本（位置偏移，引擎默认 +20/+20）。</summary>
        private void DuplicatePrimary()
        {
            if (_primaryId < 0 || !_selectedIds.Contains(_primaryId)) return;
            var n = _root != null ? FindNode(_root, _primaryId) : null;
            if (n == null || n.Data.ParentId < 0) return; // 根元素不可复制
            CommandRequested?.Invoke("duplicate", new { id = _primaryId });
        }

        /// <summary>查找根元素 ID（parentId&lt;0 的节点）。</summary>
        private int FindRootId()
        {
            if (_root == null) return -1;
            foreach (var c in _root.Children)
                if (c.Data.ParentId < 0) return c.Data.Id;
            return -1;
        }

        private ContextMenu BuildContextMenu(HierarchyNode node)
        {
            var menu = new ContextMenu { FontSize = 11 };
            var items = new List<MenuItem>();
            // 根元素（parentId < 0）：不可创建同级、不可删除。引擎保证唯一根。
            var isRoot = node.Data.ParentId < 0;

            // Create Child：在选中元素下创建子元素
            var createChildItem = new MenuItem { Header = "Create Child" };
            createChildItem.Click += (_, _) =>
                CommandRequested?.Invoke("create", new { parentId = node.Data.Id });
            items.Add(createChildItem);

            // Create Sibling：创建同级元素（用父级的 parentId）。根元素无同级。
            if (!isRoot)
            {
                var createSiblingItem = new MenuItem { Header = "Create Sibling" };
                createSiblingItem.Click += (_, _) =>
                    CommandRequested?.Invoke("create", new { parentId = node.Data.ParentId });
                items.Add(createSiblingItem);
            }

            // Create：创建带相应组件的元素（UI/物理/音频等预制）。预制定义见下方 AddCreatePreset
            // 调用，新增组件时在此追加即可；引擎按 components 短类名逐一附加。
            var createWithMenu = new MenuItem { Header = "Create" };
            // UI（带 RectTransform 锚点布局）
            AddCreatePreset(createWithMenu, node.Data.Id, "Canvas", new[] { "RectTransform", "Canvas", "CanvasScaler" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Image", new[] { "RectTransform", "Image" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Text", new[] { "RectTransform", "Text" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Button", new[] { "RectTransform", "Image", "Button" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Layout", new[] { "RectTransform", "Layout" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Mask", new[] { "RectTransform", "Mask" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Scroll View", new[] { "RectTransform", "Mask", "ScrollView" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Scrollbar", new[] { "RectTransform", "Image", "Scrollbar" });
            createWithMenu.Items.Add(new MenuItem { Header = "-" });
            // 渲染
            AddCreatePreset(createWithMenu, node.Data.Id, "Sprite", new[] { "SpriteRenderer" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Camera", new[] { "CameraComponent" });
            createWithMenu.Items.Add(new MenuItem { Header = "-" });
            // 物理
            AddCreatePreset(createWithMenu, node.Data.Id, "Box Collider", new[] { "BoxCollider2D" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Circle Collider", new[] { "CircleCollider2D" });
            createWithMenu.Items.Add(new MenuItem { Header = "-" });
            // 音频
            AddCreatePreset(createWithMenu, node.Data.Id, "Audio Source", new[] { "AudioSource" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Audio Listener", new[] { "AudioListener" });
            createWithMenu.Items.Add(new MenuItem { Header = "-" });
            // 动画
            AddCreatePreset(createWithMenu, node.Data.Id, "Animation State Machine", new[] { "AnimStateMachine" });
            AddCreatePreset(createWithMenu, node.Data.Id, "Animation Player", new[] { "AnimationPlayer" });
            items.Add(createWithMenu);

            items.Add(new MenuItem { Header = "-" });

            // Duplicate：复制点中元素（含子树）为同父副本，位置偏移。根元素不可复制。
            if (!isRoot)
            {
                var duplicateItem = new MenuItem { Header = "Duplicate" };
                duplicateItem.Click += (_, _) =>
                    CommandRequested?.Invoke("duplicate", new { id = node.Data.Id });
                items.Add(duplicateItem);
            }

            // Copy：复制点中元素到剪贴板（根元素不可复制，粘贴时引擎也会拒绝）。
            if (!isRoot)
            {
                var copyItem = new MenuItem { Header = "Copy" };
                copyItem.Click += (_, _) =>
                {
                    ClipboardIds.Clear();
                    ClipboardIds.Add(node.Data.Id);
                };
                items.Add(copyItem);
            }

            // Paste：将剪贴板中的元素（含子树）粘贴为点中节点的子级。
            var pasteItem = new MenuItem { Header = "Paste" };
            pasteItem.IsEnabled = ClipboardIds.Count > 0;
            pasteItem.Click += (_, _) =>
            {
                foreach (var clipId in ClipboardIds)
                    CommandRequested?.Invoke("duplicate",
                        new { id = clipId, parentId = node.Data.Id, offsetX = 0, offsetY = 0 });
            };
            items.Add(pasteItem);

            // Create Prefab：把点中元素（含子树）导出为 .prefab 资产文件。
            var createPrefabItem = new MenuItem { Header = "Create Prefab..." };
            createPrefabItem.Click += (_, _) =>
                CreatePrefabRequested?.Invoke(node.Data.Id, node.Data.Name);
            items.Add(createPrefabItem);

            // Revert to Prefab：丢弃该实例的全部 override（字段级与元素级），还原为源的当前内容。
            // 作用于整个实例 —— 点实例内任意元素都在同一个实例上生效。
            if (!string.IsNullOrEmpty(node.Data.PrefabGuid))
            {
                var sourceName = ProjectPanel?.GetDisplayName(node.Data.PrefabGuid!);
                var revertItem = new MenuItem
                {
                    Header = string.IsNullOrEmpty(sourceName)
                        ? "Revert to Prefab"
                        : $"Revert to Prefab ({sourceName})",
                };
                revertItem.Click += (_, _) => RevertPrefabRequested?.Invoke(node.Data.Id);
                items.Add(revertItem);
            }

            items.Add(new MenuItem { Header = "-" });

            // Rename：行内编辑
            var renameItem = new MenuItem { Header = "Rename" };
            renameItem.Click += (_, _) => BeginRename(node);
            items.Add(renameItem);

            // Delete：销毁元素。根元素不可删除。
            if (!isRoot)
            {
                var deleteItem = new MenuItem { Header = "Delete" };
                deleteItem.Click += (_, _) =>
                    CommandRequested?.Invoke("destroy", new { id = node.Data.Id });
                items.Add(deleteItem);
            }

            foreach (var item in items) menu.Items.Add(item);
            menu.Opened += (_, _) => _isContextMenuOpen = true;
            menu.Closed += (_, _) => { _isContextMenuOpen = false; TryApplyPendingSnapshot(); };
            return menu;
        }

        /// <summary>向 Create 子菜单追加一个预制项：在 parentId 下创建带指定组件的新元素。</summary>
        private void AddCreatePreset(MenuItem parent, int parentId, string name, string[] components)
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) =>
                CommandRequested?.Invoke("create", new { parentId, name, components });
            parent.Items.Add(item);
        }

        /// <summary>行内重命名：TextBlock → TextBox，回车提交、Esc 取消。</summary>
        private void BeginRename(HierarchyNode node)
        {
            if (_selectedRow?.Child is not StackPanel panel) return;
            // name 是第 3 个子元素（chevron, icon, name）
            if (panel.Children[2] is not TextBlock nameBlock) return;

            // 重命名期间暂停快照更新，避免 row 被重建销毁 TextBox。
            _isRenaming = true;

            var textBox = new TextBox
            {
                Text = node.Data.Name,
                FontSize = 12,
                Padding = new Thickness(2),
                MinWidth = 80,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children[2] = textBox;
            textBox.Focus();
            textBox.SelectAll();

            string? committed = null;
            void FinishRename()
            {
                _isRenaming = false;
                TryApplyPendingSnapshot();
            }
            void Commit()
            {
                if (committed != null) return;
                committed = textBox.Text;
                if (!string.IsNullOrWhiteSpace(committed) && committed != node.Data.Name)
                {
                    node.Data.Name = committed;
                    CommandRequested?.Invoke("rename", new { id = node.Data.Id, name = committed });
                }
                FinishRename();
                Dispatcher.UIThread.Post(RebuildFlatList);
            }

            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape)
                {
                    committed = node.Data.Name; // 标记已处理，阻止 LostFocus 重新提交
                    FinishRename();
                    Dispatcher.UIThread.Post(RebuildFlatList);
                    e.Handled = true;
                }
            };
            textBox.LostFocus += (_, _) => Commit();
        }
    }
}

#pragma warning restore CS0618

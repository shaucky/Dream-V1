using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dream.Studio.Engine;
using Dream.Studio.Panels;
using Dream.Studio.Panels.Project;

#pragma warning disable CS0618 // 旧版拖拽 API：IDataObject/FileNames/Data 在 11.3 已过时但仍可用（与 ProjectPanel 拖拽源一致）。

namespace Dream.Studio.Panels.Inspector
{
    /// <summary>
    /// Inspector 面板：显示选中元素的组件字段，支持行内编辑。
    ///
    /// 功能：
    /// - 选中 Hierarchy 节点 → 请求引擎字段快照 → 渲染字段编辑器
    /// - 组件标题栏可点击折叠/展开字段（状态按组件名缓存，跨选中保持）
    /// - 底部 Add Component 按钮弹出引擎注册的可添加组件列表
    /// - 字段编辑（LostFocus/Checked）实时下发到引擎
    /// </summary>
    internal sealed class InspectorPanel : UserControl
    {
        private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x28, 0x30));
        private static readonly IBrush HeaderHoverBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x31, 0x38));
        private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
        private static readonly IBrush FieldBorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x31, 0x38));
        private static readonly IBrush ValueBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD6));
        private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x6E, 0xA0));
        // 覆盖标记（prefab 实例上被改过的字段/元素名）：与 Hierarchy 的实例图标同色系。
        private static readonly IBrush OverriddenBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x9B, 0xD1));
        private static readonly IBrush PrefabBarBrush = new SolidColorBrush(Color.FromRgb(0x24, 0x2C, 0x38));
        /// <summary>覆盖标记字形：字段标签与元素名前缀。</summary>
        private const string OverriddenMark = "\u25AA ";

        private readonly ScrollViewer _scroll;
        private readonly StackPanel _content;
        // 根容器：叠加层（Add Component 输入匹配弹窗）挂在滚动区之上，避免随内容重建被 Clear。
        private readonly Grid _overlayRoot;
        private int _currentElementId = -1;

        // Add Component 输入匹配：引擎返回的可添加组件名缓存 + 当前弹窗（内容重建时重建）。
        private readonly List<string> _componentNames = new();
        private Popup? _addComponentPopup;

        // 已折叠的组件名集合（按短类名缓存，跨元素选中保持一致）。
        private readonly HashSet<string> _collapsed = new();

        // 定时刷新：每 150ms 请求当前元素的快照，让 Inspector 实时反映引擎侧值变化。
        // _isEditing 为 true（任意 TextBox 处于焦点）时暂停刷新，避免覆盖用户输入。
        private readonly DispatcherTimer _refreshTimer;
        private bool _isEditing;

        /// <summary>请求引擎发送指定元素的 Inspector 快照。参数为元素 ID。</summary>
        public event Action<int>? RequestInspector;

        /// <summary>当前选中的元素 ID；-1 表示未选中。供外部（如拖拽结束）触发即时刷新。</summary>
        internal int CurrentElementId => _currentElementId;

        /// <summary>字段编辑后通知外部，转发到引擎。component/field/value 三元组定位字段。</summary>
        public event Action<int, string, string, object?>? FieldEdited;

        /// <summary>Project 面板引用，供 resource 类型字段拖放解析 GUID。</summary>
        public ProjectPanel? ProjectPanel { get; set; }

        /// <summary>请求引擎返回可添加组件列表（Add Component 按钮点击时触发）。</summary>
        public event Action? RequestComponentList;

        /// <summary>添加组件到指定元素。参数为元素 ID 与组件短类名。</summary>
        public event Action<int, string>? AddComponentRequested;

        /// <summary>移除组件。参数为元素 ID 与组件短类名。</summary>
        public event Action<int, string>? RemoveComponentRequested;

        /// <summary>粘贴组件。参数为元素 ID、组件短类名、字段值字典、是否新建（true=粘贴组件 / false=粘贴值）。</summary>
        public event Action<int, string, object, bool>? PasteComponentRequested;

        /// <summary>启用/禁用元素或组件。component 为 null 表示元素级启用。</summary>
        public event Action<int, string?, bool>? SetEnabledRequested;

        /// <summary>请求把预制体实例还原到源。参数为实例内任意元素 ID（引擎按实例生效）。</summary>
        public event Action<int>? RevertPrefabRequested;

        // 组件剪贴板：跨元素、跨选中保持。存储被复制组件的类型与字段值（CLR 可序列化形式）。
        private static (string Type, Dictionary<string, object?> Fields)? _componentClipboard;

        public InspectorPanel()
        {
            _content = new StackPanel { Orientation = Orientation.Vertical };
            _scroll = new ScrollViewer
            {
                Content = _content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26)),
            };
            _overlayRoot = new Grid { Children = { _scroll } };
            Content = _overlayRoot;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _refreshTimer.Tick += OnRefreshTick;
            _refreshTimer.Start();

            ShowEmpty("Select an element to inspect");
        }

        /// <summary>定时请求当前元素的快照。编辑中（TextBox 聚焦）也不停发——应用侧会跳过聚焦控件。</summary>
        private void OnRefreshTick(object? sender, EventArgs e)
        {
            if (_currentElementId < 0) return;
            RequestInspector?.Invoke(_currentElementId);
        }

        /// <summary>选中变化时调用（单选或批量）。多选时显示占位，不请求字段快照。</summary>
        public void SelectElements(IReadOnlyList<int> ids, int primaryId)
        {
            if (ids.Count <= 1)
            {
                SelectElement(primaryId);
                return;
            }

            _currentElementId = -1;  // 多选不关联单元素快照
            _isEditing = false;
            _renderedElementId = -1;  // 强制下次 ApplySnapshot 重建结构
            _renderedSignature = "";
            _content.Children.Clear();
            ShowEmpty(ids.Count + " elements selected");
        }

        /// <summary>选中元素变化时调用，向引擎请求该元素的 Inspector 快照。
        /// 切换选中视为退出编辑态（旧编辑器即将销毁）。</summary>
        public void SelectElement(int elementId)
        {
            _currentElementId = elementId;
            _isEditing = false;
            _renderedElementId = -1;  // 强制下次 ApplySnapshot 重建结构
            _renderedSignature = "";
            _content.Children.Clear();
            if (elementId < 0)
            {
                ShowEmpty("Select an element to inspect");
                return;
            }
            ShowEmpty("Loading...");
            RequestInspector?.Invoke(elementId);
        }

        /// <summary>应用引擎返回的 Inspector 快照，重建字段编辑器。</summary>
        internal void UpdateSnapshot(InspectorSnapshotData snapshot)
        {
            Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
        }

        // 当前已渲染的元素 ID 与组件签名（组件名有序列表），用于判断是否需要重建结构。
        // 同元素的定时刷新只更新字段值时跳过重建，避免打断编辑/右键菜单/滚动等交互。
        private int _renderedElementId = -1;
        private string _renderedSignature = "";
        private string _renderedElementName = "";
        // 元素标题的 enabled 复选框（结构未变时同步勾选状态）。
        private CheckBox? _elementEnabledBox;
        // 元素标题的名称文本（结构未变时同步重命名/覆盖标记，避免走可视树索引）。
        private TextBlock? _headerNameText;
        // 已应用到标题上的"启用状态被覆盖"标记（结构未变时只在翻转时更新提示）。
        private bool _renderedEnabledOverridden;

        private void ApplySnapshot(InspectorSnapshotData snapshot)
        {
            // 快照与当前选中不一致（用户已切换选中），丢弃旧快照。
            if (snapshot.ElementId != _currentElementId) return;

            // 计算组件签名：组件名有序拼接。签名变化才重建结构（增删组件、切换元素）。
            // 覆盖标记不参与签名：它由增量路径改标签/提示，避免"改一个字段就重建面板"——
            // 重建会关掉正开着的取色弹层、打断刚拖完的 Gizmo 交互。
            var sig = string.Join('|', snapshot.Components.Select(c => c.Name));
            var structureChanged = snapshot.ElementId != _renderedElementId || sig != _renderedSignature;

            // 编辑中（任意字段 TextBox 聚焦）：不重建结构，避免销毁正在输入的控件；
            // 结构未变时照常增量更新（UpdateEditorValue 会跳过聚焦控件，不覆盖输入）。
            if (structureChanged && _isEditing) return;

            if (structureChanged)
            {
                _content.Children.Clear();
                _renderedElementId = snapshot.ElementId;
                _renderedSignature = sig;
                _renderedElementName = snapshot.ElementName ?? "";
                _renderedEnabledOverridden = snapshot.EnabledOverridden;

                // 顶部元素标题
                _content.Children.Add(BuildElementHeader(snapshot));

                if (snapshot.Components.Count == 0)
                {
                    ShowEmpty("No components");
                }
                else
                {
                    foreach (var comp in snapshot.Components)
                        _content.Children.Add(BuildComponentSection(comp));
                }
                _content.Children.Add(BuildAddComponentButton());

                // 结构重建后旧编辑器已销毁，编辑态复位。
                _isEditing = false;
            }
            else
            {
                // 结构未变：增量更新已有字段编辑器的值（不重建控件，不丢焦点）。
                // 同步刷新元素名与覆盖标记（重命名 / 引擎刚记上 override 的场景）与 enabled 勾选。
                var name = snapshot.ElementName ?? "";
                if (name != _renderedElementName) _renderedElementName = name;
                var display = snapshot.NameOverridden ? OverriddenMark + name : name;
                if (_headerNameText != null && _headerNameText.Text != display)
                    _headerNameText.Text = display;
                if (_elementEnabledBox != null)
                {
                    if (_elementEnabledBox.IsChecked != snapshot.Enabled)
                        _elementEnabledBox.IsChecked = snapshot.Enabled;
                    if (_renderedEnabledOverridden != snapshot.EnabledOverridden)
                    {
                        _renderedEnabledOverridden = snapshot.EnabledOverridden;
                        ToolTip.SetTip(_elementEnabledBox,
                            snapshot.EnabledOverridden ? "Enabled state overridden from prefab" : null);
                    }
                }
                UpdateFieldValues(snapshot);
            }
        }

        /// <summary>
        /// 元素标题栏：显示 enabled 复选框 + 元素名，置顶于 Inspector 内容区。
        /// prefab 实例元素额外附一条来源栏（源名 + Revert 按钮）；
        /// 元素名/启用状态被覆盖过时打上覆盖标记。
        /// </summary>
        private Control BuildElementHeader(InspectorSnapshotData snapshot)
        {
            var check = new CheckBox
            {
                IsChecked = snapshot.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            check.Click += (_, _) => SetEnabledRequested?.Invoke(_currentElementId, null, check.IsChecked == true);
            if (snapshot.EnabledOverridden)
                ToolTip.SetTip(check, "Enabled state overridden from prefab");
            _elementEnabledBox = check;

            var name = snapshot.ElementName ?? "";
            var text = new TextBlock
            {
                Text = snapshot.NameOverridden ? OverriddenMark + name : name,
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xD0, 0xA0)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (snapshot.NameOverridden)
                ToolTip.SetTip(text, "Name overridden from prefab");
            _headerNameText = text;

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(check, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(check);
            row.Children.Add(text);
            row.Margin = new Thickness(8, 6, 8, 6);

            // 非实例元素：标题行即全部（保持既有视觉）。
            if (!snapshot.IsPrefabInstance) return row;

            var header = new StackPanel { Orientation = Orientation.Vertical };
            header.Children.Add(row);
            header.Children.Add(BuildPrefabBar(snapshot));
            return header;
        }

        /// <summary>
        /// 预制体来源栏：显示实例的来源资产名与 Revert 按钮。
        /// Revert 只作用于整个实例（清空全部 override），文案里点明以免误以为是"还原该字段"。
        /// </summary>
        private Control BuildPrefabBar(InspectorSnapshotData snapshot)
        {
            var sourceName = ProjectPanel?.GetDisplayName(snapshot.PrefabGuid!)
                             ?? snapshot.PrefabGuid!;

            var bar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(8, 0, 8, 6),
            };

            var label = new TextBlock
            {
                Text = "Prefab: " + sourceName,
                FontSize = 11,
                Foreground = OverriddenBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(label, snapshot.PrefabRoot
                ? "Root element of this prefab instance"
                : "Part of a prefab instance (root: " + sourceName + ")");
            Grid.SetColumn(label, 0);
            bar.Children.Add(label);

            var revert = new Button
            {
                Content = "Revert",
                FontSize = 11,
                Padding = new Thickness(8, 1, 8, 1),
                Background = PrefabBarBrush,
                BorderBrush = FieldBorderBrush,
                Foreground = ValueBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(revert, "Revert the whole instance to the prefab (discards all overrides)");
            revert.Click += (_, _) => RevertPrefabRequested?.Invoke(_currentElementId);
            Grid.SetColumn(revert, 1);
            bar.Children.Add(revert);

            return bar;
        }

        /// <summary>结构不变时，按组件名查找已渲染的 section 并更新字段值。
        /// 只更新非焦点控件的值，避免覆盖正在编辑的输入。</summary>
        private void UpdateFieldValues(InspectorSnapshotData snapshot)
        {
            // section 顺序与 snapshot.Components 一致，但 _content 布局为：
            // [0]=元素标题, [1..N]=组件 section, [N+1]=Add Component 按钮。
            // 因此 section 索引 = 组件索引 + 1（此前漏加偏移导致增量更新全部错位，
            // Transform 字段永远收不到正确值——这正是"拖拽 Gizmo 时 Inspector 不更新"的根因）。
            var compCount = snapshot.Components.Count;
            for (int i = 0; i < compCount; i++)
            {
                var section = _content.Children[i + 1] as StackPanel;
                if (section == null) continue;
                UpdateSectionFieldValues(section, snapshot.Components[i]);
            }
        }

        /// <summary>更新单个组件 section 内的字段值。跳过处于焦点的 TextBox。</summary>
        private void UpdateSectionFieldValues(StackPanel section, InspectorComponentData comp)
        {
            // 组件 enabled 状态同步（结构未变时）。
            if (section.Tag is CheckBox enabledBox && enabledBox.IsChecked != comp.Enabled)
                enabledBox.IsChecked = comp.Enabled;

            // section 结构：[header, fieldContainer, divider]，字段编辑器在 fieldContainer 内。
            if (section.Children.Count < 2) return;
            var fieldContainer = section.Children[1] as StackPanel;
            if (fieldContainer == null) return;

            for (int i = 0; i < fieldContainer.Children.Count && i < comp.Fields.Count; i++)
            {
                var row = fieldContainer.Children[i] as Grid;
                if (row == null) continue;
                UpdateFieldLabel(row, comp.Fields[i]);
                var editor = row.Children.Count > 1 ? row.Children[1] : null;
                if (editor == null) continue;
                UpdateEditorValue(editor, comp.Fields[i]);
            }
        }

        /// <summary>
        /// 同步字段行的覆盖标记（字段级）。不起作用时保持原样，不重建控件——
        /// 引擎在用户改过字段后立刻把它记为 override，标记要能当场出现。
        /// </summary>
        private static void UpdateFieldLabel(Grid row, InspectorFieldData field)
        {
            // action（按钮）行的标签留空，无标记可言。
            if (field.Type == "action" || row.Children.Count == 0) return;
            if (row.Children[0] is not TextBlock label) return;

            var text = field.Overridden ? OverriddenMark + field.Label : field.Label;
            if (label.Text != text) label.Text = text;
            var brush = field.Overridden ? OverriddenBrush : LabelBrush;
            if (!ReferenceEquals(label.Foreground, brush)) label.Foreground = brush;
        }

        /// <summary>按编辑器类型更新值。TextBox 处于焦点时跳过（正在编辑）。</summary>
        private void UpdateEditorValue(Control editor, InspectorFieldData field)
        {
            switch (field.Type)
            {
                case "number":
                    if (editor is TextBox tb1 && !tb1.IsFocused)
                        tb1.Text = FormatNumber(ExtractDouble(field.Value));
                    break;
                case "string":
                    if (editor is TextBox tb2 && !tb2.IsFocused)
                        tb2.Text = field.Value?.ValueKind == JsonValueKind.String
                            ? field.Value.Value.GetString() ?? "" : "";
                    break;
                case "boolean":
                    if (editor is CheckBox cb && !cb.IsFocused)
                        cb.IsChecked = field.Value?.ValueKind == JsonValueKind.True;
                    break;
                case "vector2":
                    // vector2 编辑器是 StackPanel(TextBox, TextBlock, TextBox)
                    if (editor is StackPanel vp && vp.Children.Count >= 3)
                    {
                        // null 表示"自动"（如 pivot 自动居中）：与 BuildVector2Editor 一致显示空框。
                        // 缺这条判断时增量刷新会把它刷成 "0,0"，与初次渲染的空白对不上。
                        var isNull = !field.Value.HasValue || field.Value.Value.ValueKind == JsonValueKind.Null;
                        double x = 0, y = 0;
                        if (!isNull)
                        {
                            var v = field.Value!.Value;
                            if (v.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number) x = xe.GetDouble();
                            if (v.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number) y = ye.GetDouble();
                        }
                        var xText = isNull ? "" : FormatNumber(x);
                        var yText = isNull ? "" : FormatNumber(y);
                        if (vp.Children[0] is TextBox xtb && !xtb.IsFocused) xtb.Text = xText;
                        if (vp.Children[2] is TextBox ytb && !ytb.IsFocused) ytb.Text = yText;
                    }
                    break;
                case "color":
                    // color 编辑器是 StackPanel(ColorPicker, TextBox)。
                    // 选色中（Tag==true，尚未提交）时跳过快照同步，避免拖动中的颜色被引擎旧值覆盖。
                    if (editor is StackPanel cp && cp.Children.Count >= 2)
                    {
                        var c = (uint)ExtractDouble(field.Value);
                        var hex = "#" + c.ToString("X6");
                        if (cp.Children[0] is ColorPicker picker && picker.Tag is not true)
                        {
                            picker.Tag = true; // 标记"快照同步中"，ColorChanged 不启动提交计时器
                            picker.Color = Color.FromArgb(255, (byte)(c >> 16), (byte)(c >> 8), (byte)c);
                            picker.Tag = false;
                        }
                        if (cp.Children[1] is TextBox ctb && !ctb.IsFocused)
                            ctb.Text = hex;
                    }
                    break;
                case "resource":
                    if (editor is TextBox rtb && !rtb.IsFocused)
                        rtb.Text = ResolveGuidToDisplayName(field.Value);
                    break;
            }
        }

        /// <summary>将资源 GUID 解析为显示名：精灵显示精灵名，文件显示文件名；无法解析时显示空串。</summary>
        private string ResolveGuidToDisplayName(JsonElement? value)
        {
            var guid = value?.ValueKind == JsonValueKind.String
                ? value.Value.GetString() ?? ""
                : "";
            if (string.IsNullOrEmpty(guid)) return "";
            return ProjectPanel?.GetDisplayName(guid) ?? "";
        }

        private Control BuildComponentSection(InspectorComponentData comp)
        {
            var section = new StackPanel { Orientation = Orientation.Vertical };
            var isCollapsed = _collapsed.Contains(comp.Name);

            // 折叠箭头：▶ 收起 / ▼ 展开
            var arrow = new TextBlock
            {
                Width = 12,
                FontSize = 8,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = LabelBrush,
                Text = isCollapsed ? "\u25B6" : "\u25BC",
            };

            var title = new TextBlock
            {
                Text = comp.Name,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xB4, 0x6E)),
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 组件 enabled 复选框（禁用即停组件生命周期）。点击消费事件避免触发标题折叠。
            var enabledBox = new CheckBox
            {
                IsChecked = comp.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0),
            };
            enabledBox.Click += (_, _) => SetEnabledRequested?.Invoke(_currentElementId, comp.Name, enabledBox.IsChecked == true);
            enabledBox.PointerPressed += (_, e) => e.Handled = true;
            // section.Tag 存复选框引用：结构未变时增量同步组件 enabled 状态。
            section.Tag = enabledBox;

            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            headerPanel.Children.Add(arrow);
            headerPanel.Children.Add(enabledBox);
            headerPanel.Children.Add(title);

            // 字段容器：折叠时隐藏（先声明，供标题栏事件引用）。
            // 展开时给最小高度：无字段组件（如 MoveController）也能显示可见内容区，
            // 避免整行 0 高度看起来像组件缺失/渲染异常。
            var fieldContainer = new StackPanel
            {
                Orientation = Orientation.Vertical,
                IsVisible = !isCollapsed,
                MinHeight = 24,
            };

            // 标题栏：可点击切换折叠，hover 高亮
            var header = new Border
            {
                Background = HeaderBrush,
                Padding = new Thickness(6, 4),
                Child = headerPanel,
            };
            header.PointerEntered += (_, _) =>
            {
                if (header.Background != AccentBrush)
                    header.Background = HeaderHoverBrush;
            };
            header.PointerExited += (_, _) =>
            {
                if (header.Background != AccentBrush)
                    header.Background = HeaderBrush;
            };
            header.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                ToggleCollapse(comp.Name, arrow, fieldContainer);
                e.Handled = true;
            };
            // 右键菜单：复制/粘贴组件/粘贴组件值/移除组件。
            header.ContextMenu = BuildComponentContextMenu(comp.Name, comp.Fields);
            section.Children.Add(header);

            foreach (var field in comp.Fields)
                fieldContainer.Children.Add(BuildFieldRow(_currentElementId, comp.Name, field));
            section.Children.Add(fieldContainer);

            // 分隔线
            section.Children.Add(new Border
            {
                Height = 1,
                Background = FieldBorderBrush,
            });

            return section;
        }

        /// <summary>切换组件折叠状态，更新箭头方向与字段容器可见性。</summary>
        private void ToggleCollapse(string componentName, TextBlock arrow, Control fieldContainer)
        {
            if (_collapsed.Contains(componentName))
            {
                _collapsed.Remove(componentName);
                arrow.Text = "\u25BC";
                fieldContainer.IsVisible = true;
            }
            else
            {
                _collapsed.Add(componentName);
                arrow.Text = "\u25B6";
                fieldContainer.IsVisible = false;
            }
        }

        /// <summary>构建底部 Add Component 按钮。点击后弹出"输入过滤 + 列表选择"的弹窗。</summary>
        private Control BuildAddComponentButton()
        {
            var button = new Button
            {
                Content = "+ Add Component",
                FontSize = 11,
                Margin = new Thickness(8, 8, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = HeaderBrush,
                BorderBrush = FieldBorderBrush,
                Foreground = LabelBrush,
            };

            // 内容重建会替换按钮：先移除旧弹窗，避免叠加层残留孤儿 Popup。
            if (_addComponentPopup != null)
            {
                _overlayRoot.Children.Remove(_addComponentPopup);
                _addComponentPopup = null;
            }

            var searchBox = new TextBox
            {
                Watermark = "Filter components…",
                FontSize = 11,
                MinHeight = 0,
                Padding = new Thickness(4, 2, 4, 2),
            };
            var listBox = new ListBox
            {
                FontSize = 11,
                MaxHeight = 240,
                MinWidth = 180,
            };
            var popupBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x28, 0x2C, 0x34)),
                BorderBrush = FieldBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children = { searchBox, listBox },
                },
            };
            var popup = new Popup
            {
                Placement = PlacementMode.Bottom,
                PlacementTarget = button,
                Child = popupBorder,
            };
            _addComponentPopup = popup;
            _overlayRoot.Children.Add(popup);

            // 输入过滤：按字符从前往后匹配（子序列匹配，忽略大小写）；过滤后默认选中首项。
            void ApplyFilter()
            {
                var query = searchBox.Text?.Trim() ?? "";
                listBox.ItemsSource = query.Length == 0
                    ? _componentNames
                    : _componentNames.Where(n => MatchSubsequence(n, query)).ToList();
                listBox.SelectedIndex = listBox.ItemCount > 0 ? 0 : -1;
            }

            void Submit(string name)
            {
                popup.IsOpen = false;
                if (_currentElementId >= 0)
                    AddComponentRequested?.Invoke(_currentElementId, name);
            }

            searchBox.TextChanged += (_, _) => ApplyFilter();
            searchBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    if (listBox.SelectedItem is string name) Submit(name);
                    else if (listBox.ItemCount > 0) Submit(_componentNames.First());
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    popup.IsOpen = false;
                    e.Handled = true;
                }
                else if (e.Key == Key.Down && listBox.ItemCount > 0)
                {
                    listBox.SelectedIndex = 0;
                    listBox.Focus();
                    e.Handled = true;
                }
            };
            listBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && listBox.SelectedItem is string name)
                {
                    Submit(name);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    popup.IsOpen = false;
                    e.Handled = true;
                }
            };
            // 点击列表项：提交点击的项。不能用 SelectionChanged——弹窗打开时默认选中
            // 第一项，再次点击已选中项不会触发选中变化，导致"点击无法添加"。
            // 也不能用普通 PointerReleased（+=）：SelectingItemsControl 内部处理选中时
            // 会把 PointerReleased 标记为 Handled，冒泡侧默认不接收已处理事件，handler 收不到。
            // 因此用 AddHandler(handledEventsToo: true) 强制接收；再通过
            // FindItemContainer 沿父链找 ListBoxItem，排除点击滚动条/空白导致的误提交。
            listBox.AddHandler(
                InputElement.PointerReleasedEvent,
                (object? _, PointerReleasedEventArgs e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Left) return;
                    if (FindItemContainer(e.Source) is { } item &&
                        item.DataContext is string name)
                        Submit(name);
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);
            popup.Opened += (_, _) =>
            {
                // 弹窗宽度与 Add Component 按钮一致（按钮为 Stretch，即面板内容宽度）。
                if (button.Bounds.Width > 0)
                    popupBorder.Width = button.Bounds.Width;
                searchBox.Text = "";
                ApplyFilter();
                searchBox.Focus();
            };

            button.Click += (_, _) =>
            {
                if (_currentElementId < 0) return;
                // 请求引擎返回可添加组件列表，收到后由 UpdateComponentList 打开弹窗。
                RequestComponentList?.Invoke();
            };
            return button;
        }

        /// <summary>
        /// 接收引擎返回的可添加组件列表：缓存并弹出输入匹配弹窗。
        /// 在 UI 线程调用（由 InspectorComponentListHandler 触发）。
        /// </summary>
        internal void UpdateComponentList(List<string> componentNames)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _componentNames.Clear();
                _componentNames.AddRange(componentNames);
                if (_addComponentPopup != null && componentNames.Count > 0)
                    _addComponentPopup.IsOpen = true;
            });
        }

        /// <summary>构建组件标题栏右键菜单：复制/粘贴组件/粘贴组件值/移除组件。</summary>
        private ContextMenu BuildComponentContextMenu(string compName, List<InspectorFieldData> fields)
        {
            var menu = new ContextMenu { FontSize = 11 };
            var hasClipboard = _componentClipboard != null;
            var sameType = hasClipboard && _componentClipboard!.Value.Type == compName;
            var canRemove = compName != "Transform"; // Transform 为元素必需组件，不可移除

            var copyItem = new MenuItem { Header = "Copy Component", IsEnabled = true };
            copyItem.Click += (_, _) => CopyComponent(compName, fields);
            menu.Items.Add(copyItem);

            var pasteCompItem = new MenuItem { Header = "Paste Component", IsEnabled = hasClipboard };
            pasteCompItem.Click += (_, _) =>
            {
                if (_componentClipboard is { } cb)
                    PasteComponentRequested?.Invoke(_currentElementId, cb.Type, cb.Fields, true);
            };
            menu.Items.Add(pasteCompItem);

            var pasteValuesItem = new MenuItem { Header = "Paste Component Values", IsEnabled = sameType };
            pasteValuesItem.Click += (_, _) =>
            {
                if (_componentClipboard is { } cb)
                    PasteComponentRequested?.Invoke(_currentElementId, compName, cb.Fields, false);
            };
            menu.Items.Add(pasteValuesItem);

            var removeItem = new MenuItem { Header = "Remove Component", IsEnabled = canRemove };
            removeItem.Click += (_, _) =>
                RemoveComponentRequested?.Invoke(_currentElementId, compName);
            menu.Items.Add(removeItem);

            return menu;
        }

        /// <summary>复制组件到剪贴板：提取组件类型与所有字段值（CLR 可序列化形式）。</summary>
        private void CopyComponent(string compName, List<InspectorFieldData> fields)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var f in fields)
                dict[f.Name] = ExtractFieldValue(f);
            _componentClipboard = (compName, dict);
        }

        /// <summary>将 InspectorFieldData.Value（JsonElement）转为可被 JSON 序列化的 CLR 对象。</summary>
        private static object? ExtractFieldValue(InspectorFieldData field)
        {
            if (field.Value is not { } v) return null;
            switch (field.Type)
            {
                case "number":
                    return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;
                case "boolean":
                    return v.ValueKind == JsonValueKind.True;
                case "string":
                case "resource":
                    return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                case "vector2":
                    // null 表示"自动"（如 pivot 自动居中）：原样传递，粘贴时引擎恢复为自动。
                    if (v.ValueKind == JsonValueKind.Null) return null;
                    double x = 0, y = 0;
                    if (v.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number) x = xe.GetDouble();
                    if (v.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number) y = ye.GetDouble();
                    return new { x, y };
                default:
                    return null;
            }
        }

        private Control BuildFieldRow(int elementId, string componentName, InspectorFieldData field)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("80,*"),
                Margin = new Thickness(0, 2, 0, 2),
            };

            var label = new TextBlock
            {
                // action（按钮）行的标签留空：按钮文字已表达含义，避免重复。
                Text = field.Type == "action"
                    ? ""
                    : (field.Overridden ? OverriddenMark + field.Label : field.Label),
                FontSize = 11,
                // 被覆盖的字段用覆盖色 + 前缀标记：源 prefab 的后续改动不会传播到此字段。
                Foreground = field.Overridden ? OverriddenBrush : LabelBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0),
            };
            if (field.Overridden)
                ToolTip.SetTip(label, "Overridden from prefab");
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var editor = BuildEditor(elementId, componentName, field);
            Grid.SetColumn(editor, 1);
            row.Children.Add(editor);

            return row;
        }

        private Control BuildEditor(int elementId, string componentName, InspectorFieldData field)
        {
            switch (field.Type)
            {
                case "number":
                    return BuildNumberEditor(elementId, componentName, field);
                case "boolean":
                    return BuildBooleanEditor(elementId, componentName, field);
                case "string":
                    // 受控取值列表（options）存在时用下拉框，否则自由文本框。
                    if (field.Options is { Count: > 0 })
                        return BuildChoiceEditor(elementId, componentName, field);
                    return BuildStringEditor(elementId, componentName, field);
                case "vector2":
                    return BuildVector2Editor(elementId, componentName, field);
                case "color":
                    return BuildColorEditor(elementId, componentName, field);
                case "resource":
                    return BuildResourceEditor(elementId, componentName, field);
                case "action":
                    // 按钮（如 Fit to Bounds）：点击向引擎发送字段编辑请求。
                    return BuildActionButton(elementId, componentName, field);
                default:
                    return new TextBlock
                    {
                        Text = "<unsupported: " + field.Type + ">",
                        FontSize = 11,
                        Foreground = LabelBrush,
                    };
            }
        }

        /// <summary>动作按钮编辑器：点击触发引擎侧操作（通过 inspector.edit 下发字段请求）。</summary>
        private Control BuildActionButton(int elementId, string componentName, InspectorFieldData field)
        {
            var button = new Button
            {
                Content = field.Label,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = HeaderBrush,
                BorderBrush = FieldBorderBrush,
                Foreground = LabelBrush,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(8, 2, 8, 2),
                IsEnabled = !field.ReadOnly,
            };
            if (!field.ReadOnly)
                button.Click += (_, _) => RaiseEdit(elementId, componentName, field.Name, true);
            return button;
        }

        /// <summary>颜色编辑器：ColorPicker（紧凑取色入口）+ 十六进制文本框。
        /// 输入 #RRGGBB 或 RRGGBB，解析为 uint 下发到引擎。</summary>
        private Control BuildColorEditor(int elementId, string componentName, InspectorFieldData field)
        {
            var raw = ExtractDouble(field.Value);
            var colorUint = (uint)raw;
            var picked = colorUint;

            var textBox = CreateFieldTextBox("#" + colorUint.ToString("X6"), field.ReadOnly);
            textBox.Width = 80;
            textBox.FontFamily = new FontFamily("Consolas,Courier New,monospace");
            // ColorPicker 按钮比普通字段高，会撑高整行；文本框被拉伸后文本垂直居中显示。
            textBox.VerticalContentAlignment = VerticalAlignment.Center;

            // ColorPicker 本身即"预览色块 + 下拉取色器"的紧凑控件，直接作为行内编辑器
            // （即选择器的入口），无需自绘色块 + 自建 Popup。弹层尺寸由控件自带主题决定。
            var colorPicker = new ColorPicker
            {
                Color = Color.FromArgb(255,
                    (byte)(colorUint >> 16), (byte)(colorUint >> 8), (byte)colorUint),
                IsEnabled = !field.ReadOnly,
            };

            if (!field.ReadOnly)
            {
                // 延迟提交：选色停止 350ms 后提交一次，避免拖动过程每次变动都写 undo。
                // 提交前把 Tag 置 true 标记"选色中"，快照同步会跳过该编辑器，
                // 防止拖动中的颜色被引擎旧值覆盖。
                var commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                commitTimer.Tick += (_, _) =>
                {
                    commitTimer.Stop();
                    colorPicker.Tag = false;
                    if (picked != colorUint)
                        RaiseEdit(elementId, componentName, field.Name, (double)picked);
                };
                colorPicker.ColorChanged += (_, e) =>
                {
                    var c = e.NewColor;
                    picked = (uint)((c.R << 16) | (c.G << 8) | c.B);
                    textBox.Text = "#" + picked.ToString("X6");
                    // 快照同步（Tag 先置 true）引起的变更不启动提交计时器，避免回声提交。
                    if (colorPicker.Tag is true) return;
                    colorPicker.Tag = true;
                    commitTimer.Stop();
                    commitTimer.Start();
                };

                void CommitText()
                {
                    var text = (textBox.Text ?? "").Trim();
                    if (TryParseHexColor(text, out var c))
                    {
                        picked = c;
                        colorPicker.Color = Color.FromArgb(255,
                            (byte)(c >> 16), (byte)(c >> 8), (byte)c);
                        RaiseEdit(elementId, componentName, field.Name, (double)c);
                    }
                }
                BindTextBoxCommit(textBox, CommitText);
            }

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            panel.Children.Add(colorPicker);
            panel.Children.Add(textBox);
            return panel;
        }

        /// <summary>下拉选项编辑器：受控字符串取值列表（AS3 无枚举，用 options 约束）。</summary>
        private Control BuildChoiceEditor(int elementId, string componentName, InspectorFieldData field)
        {
            var options = field.Options!;
            var current = field.Value?.ValueKind == JsonValueKind.String
                ? field.Value.Value.GetString() ?? ""
                : "";

            var combo = new ComboBox
            {
                ItemsSource = options,
                SelectedItem = options.Contains(current) ? current : null,
                IsEnabled = !field.ReadOnly,
                FontSize = 11,
                MinHeight = 0,
            };

            // 初始化阶段设置 SelectedItem 会触发 SelectionChanged，用标志屏蔽，避免误发编辑。
            var initializing = true;
            if (!field.ReadOnly)
            {
                combo.SelectionChanged += (_, _) =>
                {
                    if (initializing) return;
                    if (combo.SelectedItem is string s)
                        RaiseEdit(elementId, componentName, field.Name, s);
                };
            }
            initializing = false;
            return combo;
        }

        /// <summary>从事件源沿父链向上查找所属的 ListBoxItem；不在任何项内（滚动条/空白）时返回 null。</summary>
        private static ListBoxItem? FindItemContainer(object? source)
        {
            for (var c = source as StyledElement; c != null; c = c.Parent)
            {
                if (c is ListBoxItem item) return item;
            }
            return null;
        }

        /// <summary>
        /// 子序列匹配：query 的每个字符按顺序出现在 name 中（贪婪、从前向后扫描，不必连续）。
        /// 如 "dc" 可匹配 "DisplayComponent"，"ro" 可匹配 "Rotator"。忽略大小写。
        /// </summary>
        private static bool MatchSubsequence(string name, string query)
        {
            if (query.Length == 0) return true;
            int ni = 0;
            for (int qi = 0; qi < query.Length; qi++)
            {
                var qc = char.ToLowerInvariant(query[qi]);
                bool found = false;
                while (ni < name.Length)
                {
                    if (char.ToLowerInvariant(name[ni]) == qc) { found = true; ni++; break; }
                    ni++;
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>解析十六进制颜色字符串（#RRGGBB / RRGGBB / #RGB / RGB），输出 uint（0xRRGGBB）。</summary>
        private static bool TryParseHexColor(string text, out uint color)
        {
            color = 0;
            if (string.IsNullOrEmpty(text)) return false;
            if (text[0] == '#') text = text.Substring(1);
            // 3 位简写 #RGB → RRGGBB
            if (text.Length == 3)
                text = "" + text[0] + text[0] + text[1] + text[1] + text[2] + text[2];
            if (text.Length != 6) return false;
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color);
        }

        /// <summary>
        /// 资源字段编辑器：只读文本框，显示资源文件名；
        /// 支持从 Project 面板拖拽文件到此解析并写入 GUID。
        /// </summary>
        private Control BuildResourceEditor(int elementId, string componentName, InspectorFieldData field)
        {
            // 只读：常规用户不直接查看/输入 GUID。
            var textBox = CreateFieldTextBox(ResolveGuidToDisplayName(field.Value), readOnly: true);
            textBox.Watermark = "drag asset here";

            // 跨窗口拖放目标标记：Inspector 可能位于独立浮动窗口（面板可浮动），
            // 内置 DragDrop 无法跨窗口路由，由 CrossWindowDragService 命中此标记。
            textBox.Tag = new DropTargetMarker
            {
                Drop = p =>
                {
                    textBox.Text = ResolveGuidToDisplayName(JsonSerializer.SerializeToElement(p.Guid));
                    RaiseEdit(elementId, componentName, field.Name, p.Guid);
                },
            };

            DragDrop.SetAllowDrop(textBox, true);
            textBox.AddHandler(DragDrop.DropEvent, (object? sender, DragEventArgs e) =>
            {
                var guid = ResolveDroppedGuid(e.Data);
                if (string.IsNullOrEmpty(guid)) return;
                textBox.Text = ResolveGuidToDisplayName(JsonSerializer.SerializeToElement(guid));
                RaiseEdit(elementId, componentName, field.Name, guid);
                e.Handled = true;
            });

            // 右键菜单：清空资源字段（置空 GUID，引擎端回退为默认显示对象）。
            var clearItem = new MenuItem { Header = "Clear" };
            clearItem.Click += (_, _) =>
            {
                textBox.Text = "";
                RaiseEdit(elementId, componentName, field.Name, "");
            };
            var contextMenu = new ContextMenu { FontSize = 11 };
            contextMenu.Items.Add(clearItem);
            textBox.ContextMenu = contextMenu;
            textBox.AddHandler(DragDrop.DragOverEvent, (object? sender, DragEventArgs e) =>
            {
                var guid = ResolveDroppedGuid(e.Data);
                if (string.IsNullOrEmpty(guid))
                {
                    e.DragEffects = DragDropEffects.None;
                }
                else if (e.Data?.Contains(ProjectPanel.AssetGuidFormat) == true)
                {
                    // Project 面板内部拖拽源只允许 Move。
                    e.DragEffects = DragDropEffects.Move;
                }
                else
                {
                    e.DragEffects = DragDropEffects.Copy;
                }
                e.Handled = true;
            });

            return textBox;
        }

        /// <summary>从拖拽数据解析资源 GUID：优先 Project 面板内部格式，其次按路径查询。</summary>
        private string? ResolveDroppedGuid(IDataObject? data)
        {
            if (data == null) return null;
            var internalGuid = data.Get(ProjectPanel.AssetGuidFormat) as string;
            if (!string.IsNullOrEmpty(internalGuid)) return internalGuid;

            var paths = GetDroppedFilePaths(data);
            if (paths == null) return null;
            foreach (var path in paths)
            {
                if (!ProjectPanel.IsImageFile(path)) continue;
                var guid = ProjectPanel?.GetGuid(path);
                if (!string.IsNullOrEmpty(guid)) return guid;
            }
            return null;
        }

        /// <summary>
        /// 从拖拽数据读取文件路径：优先尝试新 IDataObject.GetFiles()，
        /// 失败则回退到旧 DataFormats.FileNames 字符串数组。
        /// </summary>
        private static IEnumerable<string>? GetDroppedFilePaths(IDataObject data)
        {
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

            if (data.Contains(DataFormats.FileNames))
            {
                var names = data.Get(DataFormats.FileNames) as string[];
                if (names != null && names.Length > 0)
                    return names;
            }

            return null;
        }

        private Control BuildNumberEditor(int elementId, string componentName, InspectorFieldData field)
        {
            var value = ExtractDouble(field.Value);

            // min/max 齐备时用滑块（步进可调），拖动结束一次性提交，避免每次变动都写 undo。
            if (field.Min.HasValue && field.Max.HasValue && field.Max.Value > field.Min.Value)
                return BuildSliderEditor(elementId, componentName, field, value);

            var textBox = CreateFieldTextBox(FormatNumber(value), field.ReadOnly);

            if (!field.ReadOnly)
            {
                void Commit() =>
                    RaiseEditIfNumber(elementId, textBox.Text ?? "", componentName, field.Name);

                BindTextBoxCommit(textBox, Commit);
            }

            return textBox;
        }

        /// <summary>数字滑块编辑器：自绘轻量滑块（4px 轨道 + 12px 圆手柄）+ TextBox 精确输入。
        /// 不使用 Avalonia Slider/Track——其 Track.ArrangeOverride 会把 Thumb 拉伸到轨道全高并
        /// 忽略自身高度，紧凑行内易把圆手柄裁成半圆。此处完全自绘布局与拖动，尺寸可控。</summary>
        private Control BuildSliderEditor(int elementId, string componentName, InspectorFieldData field, double value)
        {
            var min = field.Min!.Value;
            var max = field.Max!.Value;
            var range = max - min;
            var readOnly = field.ReadOnly;
            var current = Math.Clamp(value, min, max);

            var textBox = CreateFieldTextBox(FormatNumber(current), readOnly);
            textBox.VerticalAlignment = VerticalAlignment.Center;
            textBox.Width = 64;

            // 自绘轨道：灰色底轨 + 蓝色激活段 + 圆形手柄（12px），无主题模板干扰。
            var track = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x31, 0x38)),
                Height = 4,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var fill = new Border
            {
                Background = AccentBrush,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false,
            };
            var thumb = new Border
            {
                Background = ValueBrush,
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var slider = new Grid
            {
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = false,
                IsEnabled = !readOnly,
            };
            slider.Children.Add(track);
            slider.Children.Add(fill);
            slider.Children.Add(thumb);

            // 手柄可拖动区间 = 轨道宽 - 手柄直径；手柄中心对应值比例。
            void Layout(double v)
            {
                var w = Math.Max(0, slider.Bounds.Width - 12);
                var ratio = range <= 0 ? 0 : Math.Clamp((v - min) / range, 0, 1);
                thumb.Margin = new Thickness(ratio * w, 0, 0, 0);
                fill.Width = ratio * w + 6; // 激活段延伸到手柄中心
            }

            void ApplyValue(double v)
            {
                current = Math.Clamp(v, min, max);
                Layout(current);
                if (!textBox.IsKeyboardFocusWithin) textBox.Text = FormatNumber(current);
            }

            // 尺寸变化（含首次布局）后重绘手柄位置。
            slider.SizeChanged += (_, _) => Layout(current);
            slider.Loaded += (_, _) => Layout(current);

            if (!readOnly)
            {
                double ValueFromPoint(double x)
                {
                    var w = Math.Max(1, slider.Bounds.Width - 12);
                    return min + Math.Clamp((x - 6) / w, 0, 1) * range;
                }

                // 拖动：按下定位 + 捕获指针，移动更新，释放提交。
                slider.PointerPressed += (_, e) =>
                {
                    ApplyValue(ValueFromPoint(e.GetPosition(slider).X));
                    e.Pointer.Capture(slider); // Avalonia 11：捕获指针以接收后续移动/释放
                };
                slider.PointerMoved += (_, e) =>
                {
                    if (e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed)
                        ApplyValue(ValueFromPoint(e.GetPosition(slider).X));
                };
                slider.PointerReleased += (_, e) =>
                {
                    e.Pointer.Capture(null);
                    ApplyValue(ValueFromPoint(e.GetPosition(slider).X));
                    RaiseEdit(elementId, componentName, field.Name, current);
                };

                void CommitText()
                {
                    if (double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    {
                        ApplyValue(v);
                        RaiseEdit(elementId, componentName, field.Name, current);
                    }
                    else
                    {
                        textBox.Text = FormatNumber(current); // 非法输入还原为当前值
                    }
                }
                BindTextBoxCommit(textBox, CommitText);
            }

            // Grid 两列：滑块列 * 拉伸占满剩余宽度，输入框列 Auto 靠右。
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 2, 0, 2),
            };
            grid.Children.Add(slider);
            Grid.SetColumn(slider, 0);
            grid.Children.Add(textBox);
            Grid.SetColumn(textBox, 1);
            return grid;
        }

        private Control BuildStringEditor(int elementId, string componentName, InspectorFieldData field)
        {
            var value = field.Value?.ValueKind == JsonValueKind.String
                ? field.Value.Value.GetString() ?? ""
                : "";

            var textBox = CreateFieldTextBox(value, field.ReadOnly);

            if (!field.ReadOnly)
            {
                void Commit() =>
                    RaiseEdit(elementId, componentName, field.Name, textBox.Text ?? "");
                BindTextBoxCommit(textBox, Commit);
            }

            return textBox;
        }

        private Control BuildBooleanEditor(int elementId, string componentName, InspectorFieldData field)
        {
            var value = field.Value?.ValueKind == JsonValueKind.True;

            var checkBox = new CheckBox
            {
                IsChecked = value,
                IsEnabled = !field.ReadOnly,
            };

            if (!field.ReadOnly)
            {
                checkBox.IsCheckedChanged += (_, _) =>
                    RaiseEdit(elementId, componentName, field.Name, checkBox.IsChecked == true);
            }

            return checkBox;
        }

        private Control BuildVector2Editor(int elementId, string componentName, InspectorFieldData field)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
            };

            double x = 0, y = 0;
            // 值为 null（如 pivot 自动居中）时显示空，避免误导为 (0,0)；编辑输入才转为显式值。
            var isNull = !field.Value.HasValue || field.Value.Value.ValueKind == JsonValueKind.Null;
            if (!isNull)
            {
                var v = field.Value!.Value;
                if (v.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number)
                    x = xe.GetDouble();
                if (v.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number)
                    y = ye.GetDouble();
            }

            var xBox = CreateVectorTextBox();
            var yBox = CreateVectorTextBox();
            xBox.Text = isNull ? "" : FormatNumber(x);
            yBox.Text = isNull ? "" : FormatNumber(y);

            panel.Children.Add(xBox);
            panel.Children.Add(new TextBlock
            {
                Text = ",",
                FontSize = 11,
                Foreground = LabelBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(yBox);

            if (!field.ReadOnly)
            {
                void Commit() => TryRaiseVectorEdit(elementId, componentName, field, xBox, yBox);
                BindTextBoxCommit(xBox, Commit);
                BindTextBoxCommit(yBox, Commit);
            }

            return panel;
        }

        /// <summary>创建字段 TextBox，统一样式。</summary>
        private TextBox CreateFieldTextBox(string text, bool readOnly)
        {
            return new TextBox
            {
                Text = text,
                FontSize = 11,
                Foreground = ValueBrush,
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C)),
                BorderBrush = FieldBorderBrush,
                Padding = new Thickness(4, 2, 4, 2),
                MinHeight = 0,
                IsReadOnly = readOnly,
            };
        }

        /// <summary>
        /// 绑定 TextBox 的提交逻辑：
        /// - GotFocus：标记编辑中，暂停定时刷新避免覆盖输入
        /// - LostFocus：清除标记，执行提交
        /// - KeyDown Enter：立即提交并失焦（失焦会再次触发 LostFocus，已用 _committed 防重入）
        /// </summary>
        private void BindTextBoxCommit(TextBox textBox, Action commit)
        {
            textBox.GotFocus += (_, _) => _isEditing = true;
            textBox.LostFocus += (_, _) =>
            {
                _isEditing = false;
                commit();
            };
            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    commit();
                    // 移走焦点触发 LostFocus 完成编辑态复位（FocusManager 可能为 null，兜底）
                    (textBox.Parent as Control)?.Focus();
                    e.Handled = true;
                }
            };
        }

        /// <summary>解析文本为 double，成功则下发编辑命令。</summary>
        private void RaiseEditIfNumber(int elementId, string text, string componentName, string fieldName)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                RaiseEdit(elementId, componentName, fieldName, v);
        }

        private TextBox CreateVectorTextBox()
        {
            return new TextBox
            {
                FontSize = 11,
                Foreground = ValueBrush,
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C)),
                BorderBrush = FieldBorderBrush,
                Padding = new Thickness(4, 2, 4, 2),
                MinHeight = 0,
                Width = 60,
            };
        }

        private void TryRaiseVectorEdit(int elementId, string componentName, InspectorFieldData field, TextBox xBox, TextBox yBox)
        {
            // 两个输入框均清空：表示"自动"（如 pivot 自动居中），发送 null 由引擎恢复。
            if (string.IsNullOrWhiteSpace(xBox.Text) && string.IsNullOrWhiteSpace(yBox.Text))
            {
                RaiseEdit(elementId, componentName, field.Name, null);
                return;
            }
            if (double.TryParse(xBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(yBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                RaiseEdit(elementId, componentName, field.Name, new { x, y });
            }
        }

        /// <summary>提交字段编辑到引擎。elementId 是编辑器创建时绑定的元素 ID，
        /// 避免失焦提交时 _currentElementId 已被切换选中改写，导致值赋给错误元素。</summary>
        private void RaiseEdit(int elementId, string componentName, string fieldName, object? value)
        {
            if (elementId < 0) return;
            FieldEdited?.Invoke(elementId, componentName, fieldName, value);
        }

        private void ShowEmpty(string message)
        {
            _content.Children.Add(new Border
            {
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = message,
                    FontSize = 11,
                    Foreground = LabelBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            });
        }

        private static double ExtractDouble(JsonElement? v)
        {
            if (v is { } e && e.ValueKind == JsonValueKind.Number)
                return e.GetDouble();
            return 0;
        }

        private static string FormatNumber(double v)
        {
            // 整数去掉小数点，浮点保留 3 位有效数字且去尾零。
            if (Math.Abs(v - Math.Round(v)) < 1e-9) return Math.Round(v).ToString(CultureInfo.InvariantCulture);
            return Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);
        }
    }
}

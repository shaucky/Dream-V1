using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dream.Studio.Panels;
using Dream.Studio.Panels.Project;
using Path = System.IO.Path;

#pragma warning disable CS0618 // 旧版拖拽 API：DataObject/FileNames/DoDragDrop 在 11.3 已过时但仍可用（与 ProjectPanel/InspectorPanel 拖拽源一致）。

namespace Dream.Studio.Panels.Animator
{
    /// <summary>
    /// 动画状态机控制器编辑器面板：打开 .dmanimator 文件，编辑
    /// 状态 / 参数 / 过渡（含 Exit Time 与条件）并保存回文件。
    ///
    /// 数据格式与引擎 AnimatorController.fromJson 解析约定一致：
    ///   { name, defaultState,
    ///     parameters: [{ name, type, value }],
    ///     states: [{ name, clipGuid }],
    ///     transitions: [{ from, to, hasExitTime, exitTime,
    ///       conditions: [{ param, op, value }] }] }
    /// 参数类型：boolean | float | int | trigger；条件 op：== | != | > | < | >= | <=。
    ///
    /// 交互：
    /// - 工具栏：控制器名 / 默认状态 / 保存
    /// - 左列：参数列表（增删、行内改默认值）+ 状态列表（增删、双击改名、
    ///   右键设默认、clip 从 Project 面板拖入）
    /// - 右列：选中状态的过渡列表（增删）+ 过渡编辑（目标状态 / Exit Time / 条件）
    /// </summary>
    internal sealed class AnimatorPanel : UserControl
    {
        private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26));
        private static readonly IBrush TrackBgBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));
        private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
        private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4D, 0x7A));
        private static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(30, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x8E, 0xC8));

        /// <summary>可选的参数类型（与引擎 AnimatorController 一致）。</summary>
        private static readonly string[] ParamTypeOptions = { "float", "int", "boolean", "trigger" };

        /// <summary>可选的比较运算符（与引擎 AnimatorController 一致）。</summary>
        private static readonly string[] OpOptions = { "==", "!=", ">", "<", ">=", "<=" };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private string? _filePath;
        private AnimatorModel _model = new();
        private int _selectedState = -1;
        private int _selectedTransition = -1;
        private readonly List<ConditionModel> _draftConditions = new();

        // ── UI 控件 ──
        private readonly TextBox _nameBox;
        private readonly ComboBox _defaultCombo;
        private readonly Button _saveButton;
        private readonly TextBlock _status;

        private readonly StackPanel _paramList;
        private readonly TextBox _paramNameBox;
        private readonly ComboBox _paramTypeCombo;
        private readonly Button _addParamButton;

        private readonly StackPanel _stateList;
        private readonly TextBox _stateNameBox;
        private readonly Button _addStateButton;

        private readonly TextBlock _transitionHeader;
        private readonly StackPanel _transitionList;
        private readonly Button _removeTransitionButton;
        private readonly ComboBox _toCombo;
        private readonly CheckBox _exitTimeCheck;
        private readonly TextBox _exitTimeBox;
        private readonly StackPanel _draftCondList;
        private readonly ComboBox _condParamCombo;
        private readonly ComboBox _condOpCombo;
        private readonly TextBox _condValueBox;
        private readonly ComboBox _condBoolCombo;
        private readonly TextBlock _condTriggerLabel;
        private readonly Button _addCondButton;
        private readonly Button _addTransitionButton;

        /// <summary>Project 面板引用，供状态 clip 字段拖放解析 GUID 与显示文件名。</summary>
        public ProjectPanel? ProjectPanel { get; set; }

        /// <summary>保存成功事件（供外部刷新等）。</summary>
        public event Action? Saved;

        public AnimatorPanel()
        {
            // ── 工具栏 ──
            _nameBox = CreateTextBox(140);
            _nameBox.TextChanged += (_, _) => _model.Name = _nameBox.Text ?? "Controller";
            _defaultCombo = CreateCombo(120);
            _defaultCombo.SelectionChanged += (_, _) =>
            {
                if (_defaultCombo.SelectedItem is string s)
                {
                    _model.DefaultState = s;
                    RefreshStates(); // 同步 States 列表中的 ⭐ 默认状态图标
                }
            };
            _saveButton = CreateToolButton("Save");
            _saveButton.Click += (_, _) => SaveAnimator();
            _status = new TextBlock
            {
                FontSize = 11,
                Foreground = DimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };

            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(6),
                Background = BgBrush,
            };
            toolbar.Children.Add(CreateLabel("Name"));
            toolbar.Children.Add(_nameBox);
            toolbar.Children.Add(CreateLabel("Default"));
            toolbar.Children.Add(_defaultCombo);
            toolbar.Children.Add(_saveButton);
            toolbar.Children.Add(_status);

            // ── 左列：参数 + 状态 ──
            _paramNameBox = CreateTextBox(90);
            _paramTypeCombo = CreateCombo(80);
            foreach (var t in ParamTypeOptions) _paramTypeCombo.Items.Add(t);
            _paramTypeCombo.SelectedIndex = 0;
            _addParamButton = CreateToolButton("+ Param");
            _addParamButton.Click += (_, _) => AddParameter();

            _paramList = new StackPanel { Orientation = Orientation.Vertical };

            var paramSection = new StackPanel { Orientation = Orientation.Vertical };
            paramSection.Children.Add(new TextBlock
            {
                Text = "Parameters",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = AccentBrush,
                Margin = new Thickness(6, 6, 0, 2),
            });
            var paramAddRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(4, 0, 0, 2),
            };
            paramAddRow.Children.Add(_paramNameBox);
            paramAddRow.Children.Add(_paramTypeCombo);
            paramAddRow.Children.Add(_addParamButton);
            paramSection.Children.Add(paramAddRow);
            paramSection.Children.Add(_paramList);

            _stateNameBox = CreateTextBox(90);
            _addStateButton = CreateToolButton("+ State");
            _addStateButton.Click += (_, _) => AddState();

            _stateList = new StackPanel { Orientation = Orientation.Vertical };

            var stateSection = new StackPanel { Orientation = Orientation.Vertical };
            stateSection.Children.Add(new TextBlock
            {
                Text = "States",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = AccentBrush,
                Margin = new Thickness(6, 10, 0, 2),
            });
            var stateAddRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(4, 0, 0, 2),
            };
            stateAddRow.Children.Add(_stateNameBox);
            stateAddRow.Children.Add(_addStateButton);
            stateSection.Children.Add(stateAddRow);
            stateSection.Children.Add(_stateList);

            var leftColumn = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children = { paramSection, stateSection },
                },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = TrackBgBrush,
            };

            // ── 右列：过渡 ──
            _transitionHeader = new TextBlock
            {
                FontSize = 12,
                Foreground = TextBrush,
                Margin = new Thickness(6, 2, 0, 2),
            };
            _removeTransitionButton = CreateToolButton("Remove Transition");
            _removeTransitionButton.IsEnabled = false;
            _removeTransitionButton.Click += (_, _) => RemoveSelectedTransition();

            var transitionHeaderRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
            };
            transitionHeaderRow.Children.Add(_transitionHeader);
            transitionHeaderRow.Children.Add(_removeTransitionButton);

            _transitionList = new StackPanel { Orientation = Orientation.Vertical };

            // Add Transition 编辑区
            _toCombo = CreateCombo(110);
            _exitTimeCheck = new CheckBox
            {
                Content = "Exit Time",
                FontSize = 11,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _exitTimeBox = CreateTextBox(56);
            _condParamCombo = CreateCombo(100);
            _condOpCombo = CreateCombo(52);
            foreach (var op in OpOptions) _condOpCombo.Items.Add(op);
            _condOpCombo.SelectedIndex = 2; // ">"
            _condValueBox = CreateTextBox(56);
            _condBoolCombo = CreateCombo(76);
            _condBoolCombo.Items.Add("is true");
            _condBoolCombo.Items.Add("is false");
            _condBoolCombo.SelectedIndex = 0;
            _condBoolCombo.IsVisible = false; // 默认数值参数，隐藏布尔编辑
            _condTriggerLabel = new TextBlock
            {
                Text = "fired",
                FontSize = 12,
                Foreground = DimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            _condTriggerLabel.IsVisible = false;
            _condParamCombo.SelectionChanged += (_, _) => UpdateCondEditorForParam();
            _addCondButton = CreateToolButton("+ Cond");
            _addCondButton.Click += (_, _) => AddDraftCondition();
            _addTransitionButton = CreateToolButton("+ Transition");
            _addTransitionButton.Click += (_, _) => AddTransition();
            _draftCondList = new StackPanel { Orientation = Orientation.Vertical };

            var condRow = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 2, 0, 0),
            };
            condRow.Children.Add(_condParamCombo);
            condRow.Children.Add(_condOpCombo);
            condRow.Children.Add(_condValueBox);
            condRow.Children.Add(_condBoolCombo);
            condRow.Children.Add(_condTriggerLabel);
            condRow.Children.Add(_addCondButton);

            var addPanel = new Border
            {
                Background = TrackBgBrush,
                Padding = new Thickness(4),
                Child = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            Margin = new Thickness(0, 0, 0, 2),
                            Children = { CreateLabel("To"), _toCombo, _exitTimeCheck, CreateLabel("s"), _exitTimeBox },
                        },
                        new TextBlock
                        {
                            Text = "Conditions (all must match)",
                            FontSize = 11,
                            Foreground = DimBrush,
                            Margin = new Thickness(4, 2, 0, 0),
                        },
                        _draftCondList,
                        condRow,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Margin = new Thickness(4, 4, 0, 0),
                            Children = { _addTransitionButton },
                        },
                    },
                },
            };

            var rightColumn = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Background = BgBrush,
                Children = { transitionHeaderRow, _transitionList, addPanel },
            };

            var body = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
            Grid.SetColumn(leftColumn, 0);
            Grid.SetColumn(rightColumn, 1);
            body.Children.Add(leftColumn);
            body.Children.Add(rightColumn);

            var layout = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                Background = BgBrush,
            };
            Grid.SetRow(toolbar, 0);
            Grid.SetRow(body, 1);
            layout.Children.Add(toolbar);
            layout.Children.Add(body);

            Content = layout;
            ShowEmptyState();
        }

        // ── 打开 / 保存 ──

        /// <summary>打开并加载 .dmanimator 文件。失败时清空并显示错误状态。</summary>
        public void OpenAnimator(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var model = JsonSerializer.Deserialize<AnimatorModel>(json, JsonOptions);
                _model = model ?? new AnimatorModel();
            }
            catch (Exception ex)
            {
                _model = new AnimatorModel();
                _status.Text = "Load failed: " + ex.Message;
            }

            _filePath = path;
            _selectedState = -1;
            _selectedTransition = -1;
            _draftConditions.Clear();
            Normalize();
            RefreshAll();
        }

        /// <summary>将当前模型写回 .dmanimator 文件。返回是否成功。</summary>
        public bool SaveAnimator()
        {
            if (string.IsNullOrEmpty(_filePath)) return false;
            Normalize();
            try
            {
                var json = JsonSerializer.Serialize(_model, JsonOptions);
                File.WriteAllText(_filePath, json);
                _status.Text = "Saved " + DateTime.Now.ToString("HH:mm:ss");
                Saved?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                _status.Text = "Save failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>当前编辑的文件路径；null 表示尚未打开文件。</summary>
        public string? CurrentFilePath => _filePath;

        /// <summary>Project 面板刷新（新增 .dmclip）后更新各状态 clip 的显示名。</summary>
        public void OnProjectRefreshed() => RefreshStates();

        // ── 数据整理 ──

        private void Normalize()
        {
            if (string.IsNullOrEmpty(_model.Name))
                _model.Name = Path.GetFileNameWithoutExtension(_filePath ?? "Controller") ?? "Controller";
            foreach (var p in _model.Parameters)
                if (string.IsNullOrEmpty(p.Name)) p.Name = "Param";
            // 状态名去重；过渡引用不存在的状态时保留（引擎按名称匹配，仅在面板隐藏）。
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in _model.States)
            {
                if (string.IsNullOrEmpty(s.Name)) s.Name = "State";
                var baseName = s.Name;
                for (int i = 2; !used.Add(s.Name); i++)
                    s.Name = baseName + i;
            }
            foreach (var t in _model.Transitions)
            {
                if (string.IsNullOrEmpty(t.From)) t.From = _model.States.FirstOrDefault()?.Name ?? "";
                if (string.IsNullOrEmpty(t.To)) t.To = _model.States.FirstOrDefault()?.Name ?? "";
                if (t.ExitTime < 0) t.ExitTime = 0;
            }
            // 参数名去重。
            var pused = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in _model.Parameters)
            {
                if (string.IsNullOrEmpty(p.Name)) continue;
                var baseName = p.Name;
                for (int i = 2; !pused.Add(p.Name); i++)
                    p.Name = baseName + i;
            }
            if (_model.DefaultState == "" && _model.States.Count > 0)
                _model.DefaultState = _model.States[0].Name;
        }

        private void RefreshAll()
        {
            _nameBox.Text = _model.Name;
            RefreshParams();
            RefreshStates();
            RefreshDefaultCombo();
            RefreshTransitions();
        }

        // ── 参数列表 ──

        private void RefreshParams()
        {
            _paramList.Children.Clear();
            for (int i = 0; i < _model.Parameters.Count; i++)
            {
                var p = _model.Parameters[i];
                int index = i;

                var nameBlock = new TextBlock
                {
                    Text = p.Name,
                    FontSize = 12,
                    Foreground = TextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 70,
                };
                var typeBlock = new TextBlock
                {
                    Text = p.Type,
                    FontSize = 11,
                    Foreground = DimBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 44,
                };

                Control valueEditor;
                if (p.Type == "trigger")
                {
                    valueEditor = new TextBlock
                    {
                        Text = "—",
                        FontSize = 11,
                        Foreground = DimBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                else if (p.Type == "boolean")
                {
                    var cb = new CheckBox
                    {
                        IsChecked = p.Value != 0,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    cb.IsCheckedChanged += (_, _) => { p.Value = cb.IsChecked == true ? 1 : 0; };
                    valueEditor = cb;
                }
                else
                {
                    var tb = CreateTextBox(52);
                    tb.Text = p.Value.ToString("0.##", CultureInfo.InvariantCulture);
                    tb.TextChanged += (_, _) =>
                    {
                        if (TryParseDouble(tb.Text, out var v)) p.Value = v;
                    };
                    valueEditor = tb;
                }

                var deleteBtn = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Height = 18,
                    Width = 22,
                    Padding = new Thickness(0),
                    Background = TrackBgBrush,
                    Foreground = DimBrush,
                };
                deleteBtn.Click += (_, _) => RemoveParameter(index);

                var row = new Border
                {
                    Height = 24,
                    Background = Brushes.Transparent,
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        Margin = new Thickness(6, 0, 0, 0),
                        Children = { nameBlock, typeBlock, valueEditor, deleteBtn },
                    },
                };
                _paramList.Children.Add(row);
            }
        }

        private void AddParameter()
        {
            var name = (_paramNameBox.Text ?? "").Trim();
            if (name.Length == 0) return;
            var type = _paramTypeCombo.SelectedItem as string ?? "float";
            if (_model.Parameters.Any(p => p.Name == name)) return;
            _model.Parameters.Add(new ParameterModel
            {
                Name = name,
                Type = type,
                Value = type == "boolean" ? 0 : 0,
            });
            _paramNameBox.Text = "";
            RefreshParams();
            RefreshTransitions(); // 条件参数候选变化
        }

        private void RemoveParameter(int index)
        {
            if (index < 0 || index >= _model.Parameters.Count) return;
            var name = _model.Parameters[index].Name;
            _model.Parameters.RemoveAt(index);
            // 清理引用该参数的过渡条件。
            foreach (var t in _model.Transitions)
                t.Conditions.RemoveAll(c => c.Param == name);
            RefreshParams();
            RefreshTransitions();
        }

        // ── 状态列表 ──

        private void RefreshStates()
        {
            _stateList.Children.Clear();
            for (int i = 0; i < _model.States.Count; i++)
            {
                var s = _model.States[i];
                int index = i;
                var isDefault = s.Name == _model.DefaultState;

                var dot = new TextBlock
                {
                    Text = isDefault ? "★" : "·",
                    FontSize = 11,
                    Foreground = isDefault ? AccentBrush : DimBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 14,
                };
                var nameBlock = new TextBlock
                {
                    Text = s.Name,
                    FontSize = 12,
                    Foreground = TextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 50,
                };

                // clip 资源字段：拖入 Project 面板的 .dmclip → GUID。
                var clipBox = CreateTextBox(110);
                clipBox.IsReadOnly = true;
                clipBox.Watermark = "drag clip";
                clipBox.Text = ResolveGuidToDisplayName(s.ClipGuid);

                // 内置拖放与跨窗口拖放共用同一赋值逻辑。
                void AssignClip(string? guid)
                {
                    if (string.IsNullOrEmpty(guid)) return;
                    s.ClipGuid = guid;
                    clipBox.Text = ResolveGuidToDisplayName(guid);
                }

                // 跨窗口拖放目标标记：Animator 面板可能位于独立浮动窗口，
                // 内置 DragDrop 无法跨窗口路由，由 CrossWindowDragService 命中此标记。
                clipBox.Tag = new DropTargetMarker { Drop = p => AssignClip(p.Guid) };

                DragDrop.SetAllowDrop(clipBox, true);
                clipBox.AddHandler(DragDrop.DropEvent, (object? sender, DragEventArgs e) =>
                {
                    var guid = ResolveDroppedGuid(e.Data);
                    if (string.IsNullOrEmpty(guid)) return;
                    AssignClip(guid);
                    e.Handled = true;
                });
                clipBox.AddHandler(DragDrop.DragOverEvent, (object? sender, DragEventArgs e) =>
                {
                    e.DragEffects = string.IsNullOrEmpty(ResolveDroppedGuid(e.Data))
                        ? DragDropEffects.None : DragDropEffects.Move;
                    e.Handled = true;
                });
                var clearItem = new MenuItem { Header = "Clear Clip", FontSize = 11 };
                clearItem.Click += (_, _) => { s.ClipGuid = ""; clipBox.Text = ""; };
                clipBox.ContextMenu = new ContextMenu { FontSize = 11, Items = { clearItem } };

                var deleteBtn = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Height = 18,
                    Width = 22,
                    Padding = new Thickness(0),
                    Background = TrackBgBrush,
                    Foreground = DimBrush,
                };
                deleteBtn.Click += (_, _) => RemoveState(index);

                var row = new Border
                {
                    Height = 24,
                    Background = ReferenceEqualsSelectedState(index) ? SelectedBrush : Brushes.Transparent,
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        Margin = new Thickness(4, 0, 0, 0),
                        Children = { dot, nameBlock, clipBox, deleteBtn },
                    },
                };
                row.PointerEntered += (_, _) =>
                {
                    if (!ReferenceEqualsSelectedState(index)) row.Background = HoverBrush;
                };
                row.PointerExited += (_, _) =>
                {
                    if (!ReferenceEqualsSelectedState(index)) row.Background = Brushes.Transparent;
                };
                row.PointerPressed += (_, e) =>
                {
                    if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                    _selectedState = index;
                    _selectedTransition = -1;
                    RefreshStates();
                    RefreshTransitions();
                };
                // 右键：设为默认 / 重命名 / 删除。
                var setDefaultItem = new MenuItem { Header = "Set as Default", FontSize = 11 };
                setDefaultItem.Click += (_, _) =>
                {
                    _model.DefaultState = s.Name;
                    RefreshStates();
                    RefreshDefaultCombo();
                };
                var renameItem = new MenuItem { Header = "Rename", FontSize = 11 };
                renameItem.Click += (_, _) => BeginRenameState(index, row, nameBlock);
                var removeItem = new MenuItem { Header = "Remove State", FontSize = 11 };
                removeItem.Click += (_, _) => RemoveState(index);
                row.ContextMenu = new ContextMenu { FontSize = 11, Items = { setDefaultItem, renameItem, removeItem } };
                _stateList.Children.Add(row);
            }
            _status.Text = _model.States.Count == 0 ? "No states — add one to begin" : "";
        }

        private bool ReferenceEqualsSelectedState(int index) => index == _selectedState;

        private void AddState()
        {
            var name = (_stateNameBox.Text ?? "").Trim();
            if (name.Length == 0) return;
            if (_model.States.Any(s => s.Name == name)) return;
            _model.States.Add(new StateModel { Name = name, ClipGuid = "" });
            _stateNameBox.Text = "";
            if (_model.DefaultState == "") _model.DefaultState = name;
            RefreshStates();
            RefreshDefaultCombo();
            RefreshTransitions();
        }

        private void RemoveState(int index)
        {
            if (index < 0 || index >= _model.States.Count) return;
            var name = _model.States[index].Name;
            _model.States.RemoveAt(index);
            // 清理引用该状态的过渡与默认状态。
            _model.Transitions.RemoveAll(t => t.From == name || t.To == name);
            if (_model.DefaultState == name)
                _model.DefaultState = _model.States.Count > 0 ? _model.States[0].Name : "";
            if (_selectedState >= _model.States.Count) _selectedState = -1;
            RefreshStates();
            RefreshDefaultCombo();
            RefreshTransitions();
        }

        /// <summary>行内改名：提交后同步更新过渡 from/to 与默认状态引用。</summary>
        private void BeginRenameState(int index, Border row, TextBlock nameBlock)
        {
            if (row.Child is not StackPanel panel) return;
            var state = _model.States[index];
            var oldName = state.Name;

            var textBox = new TextBox
            {
                Text = oldName,
                FontSize = 12,
                Height = 24, // 与其余输入框一致
                MinHeight = 0, // 覆盖 Fluent 主题默认 MinHeight，避免替换 nameBlock 后溢出
                Padding = new Thickness(2),
                MinWidth = 70,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children[1] = textBox; // 替换 nameBlock（dot=0, name=1, clip=2, delete=3）
            textBox.Focus();
            textBox.SelectAll();

            string? committed = null;
            void Commit()
            {
                if (committed != null) return;
                committed = textBox.Text ?? "";
                var newName = committed.Trim();
                if (newName.Length > 0 && newName != oldName && !_model.States.Any(s => s.Name == newName))
                {
                    state.Name = newName;
                    foreach (var t in _model.Transitions)
                    {
                        if (t.From == oldName) t.From = newName;
                        if (t.To == oldName) t.To = newName;
                    }
                    if (_model.DefaultState == oldName) _model.DefaultState = newName;
                    RefreshStates();
                    RefreshDefaultCombo();
                    RefreshTransitions();
                }
                else
                {
                    Dispatcher.UIThread.Post(RefreshStates);
                }
            }
            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape)
                {
                    committed = oldName;
                    Dispatcher.UIThread.Post(RefreshStates);
                    e.Handled = true;
                }
            };
            textBox.LostFocus += (_, _) => Commit();
        }

        private void RefreshDefaultCombo()
        {
            var selected = _defaultCombo.SelectedItem as string;
            _defaultCombo.Items.Clear();
            foreach (var s in _model.States) _defaultCombo.Items.Add(s.Name);
            _defaultCombo.SelectedItem = _model.States.Any(s => s.Name == _model.DefaultState)
                ? _model.DefaultState : null;
        }

        // ── 过渡列表（选中状态） ──

        private void RefreshTransitions()
        {
            var fromName = SelectedStateName();
            _transitionHeader.Text = fromName == null ? "Transitions (select a state)" : $"Transitions from \"{fromName}\"";
            _transitionList.Children.Clear();

            if (fromName != null)
            {
                var list = _model.Transitions
                    .Where(t => t.From == fromName)
                    .Select((t, i) => (t, i))
                    .ToList();
                for (int k = 0; k < list.Count; k++)
                {
                    var (t, _) = list[k];
                    int local = k;
                    var toState = _model.States.FirstOrDefault(s => s.Name == t.To);
                    var summary = new TextBlock
                    {
                        Text = BuildTransitionSummary(t, toState),
                        FontSize = 11,
                        Foreground = TextBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    var row = new Border
                    {
                        MinHeight = 24,
                        Background = ReferenceEqualsSelectedTransition(local) ? SelectedBrush : Brushes.Transparent,
                        Padding = new Thickness(6, 2),
                        Child = summary,
                    };
                    row.PointerEntered += (_, _) =>
                    {
                        if (!ReferenceEqualsSelectedTransition(local)) row.Background = HoverBrush;
                    };
                    row.PointerExited += (_, _) =>
                    {
                        if (!ReferenceEqualsSelectedTransition(local)) row.Background = Brushes.Transparent;
                    };
                    row.PointerPressed += (_, e) =>
                    {
                        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
                        _selectedTransition = local;
                        RefreshTransitions();
                    };
                    var deleteItem = new MenuItem { Header = "Delete Transition", FontSize = 11 };
                    deleteItem.Click += (_, _) =>
                    {
                        var realIndex = _model.Transitions.IndexOf(t);
                        if (realIndex >= 0) _model.Transitions.RemoveAt(realIndex);
                        _selectedTransition = -1;
                        RefreshTransitions();
                    };
                    row.ContextMenu = new ContextMenu { FontSize = 11, Items = { deleteItem } };
                    _transitionList.Children.Add(row);
                }
            }

            _removeTransitionButton.IsEnabled = fromName != null && _selectedTransition >= 0;

            // 刷新 Add Transition 区：目标候选与条件参数候选。
            var prevTo = _toCombo.SelectedItem as string;
            _toCombo.Items.Clear();
            if (fromName != null)
            {
                foreach (var s in _model.States)
                    if (s.Name != fromName) _toCombo.Items.Add(s.Name);
            }
            _toCombo.SelectedItem = prevTo != null && _toCombo.Items.Contains(prevTo) ? prevTo : null;
            RefreshDraftCondParams();
            RefreshDraftCondList();
        }

        private string BuildTransitionSummary(TransitionModel t, StateModel? toState)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("→ ").Append(string.IsNullOrEmpty(t.To) ? "(none)" : t.To);
            if (t.HasExitTime) sb.Append("  [exit ").Append(t.ExitTime.ToString("0.##", CultureInfo.InvariantCulture)).Append("s]");
            if (t.Conditions.Count > 0)
            {
                sb.Append("  (");
                sb.Append(string.Join(", ", t.Conditions.Select(DescribeCondition)));
                sb.Append(")");
            }
            return sb.ToString();
        }

        private bool ReferenceEqualsSelectedTransition(int index) => index == _selectedTransition;

        private string? SelectedStateName()
        {
            if (_selectedState < 0 || _selectedState >= _model.States.Count) return null;
            return _model.States[_selectedState].Name;
        }

        private void RemoveSelectedTransition()
        {
            var fromName = SelectedStateName();
            if (fromName == null || _selectedTransition < 0) return;
            var list = _model.Transitions.Where(t => t.From == fromName).ToList();
            if (_selectedTransition >= list.Count) return;
            _model.Transitions.Remove(list[_selectedTransition]);
            _selectedTransition = -1;
            RefreshTransitions();
        }

        // ── 过渡编辑（草稿） ──

        private void RefreshDraftCondParams()
        {
            var prev = _condParamCombo.SelectedItem as string;
            _condParamCombo.Items.Clear();
            foreach (var p in _model.Parameters) _condParamCombo.Items.Add(p.Name);
            _condParamCombo.SelectedItem = prev != null && _condParamCombo.Items.Contains(prev) ? prev : null;
        }

        private void RefreshDraftCondList()
        {
            _draftCondList.Children.Clear();
            for (int i = 0; i < _draftConditions.Count; i++)
            {
                var c = _draftConditions[i];
                int index = i;
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(4, 2, 0, 0),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = DescribeCondition(c),
                            FontSize = 11,
                            Foreground = TextBrush,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                };
                var removeBtn = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Height = 18,
                    Width = 22,
                    Padding = new Thickness(0),
                    Background = TrackBgBrush,
                    Foreground = DimBrush,
                };
                removeBtn.Click += (_, _) =>
                {
                    _draftConditions.RemoveAt(index);
                    RefreshDraftCondList();
                };
                row.Children.Add(removeBtn);
                _draftCondList.Children.Add(row);
            }
        }

        private void AddDraftCondition()
        {
            var param = _condParamCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(param)) return;

            switch (ParameterType(param))
            {
                case "boolean":
                    // boolean 仅支持 is true / is false（保存为 == 1 / == 0，引擎语义一致）。
                    _draftConditions.Add(new ConditionModel
                    {
                        Param = param,
                        Op = "==",
                        Value = _condBoolCombo.SelectedIndex == 1 ? 0 : 1,
                    });
                    break;
                case "trigger":
                    // trigger 触发即满足，无需 op / value（引擎忽略这两项）。
                    _draftConditions.Add(new ConditionModel { Param = param, Op = "==", Value = 0 });
                    break;
                default: // float / int
                    var op = _condOpCombo.SelectedItem as string ?? ">";
                    TryParseDouble(_condValueBox.Text, out var v);
                    _draftConditions.Add(new ConditionModel { Param = param, Op = op, Value = v });
                    break;
            }
            RefreshDraftCondList();
        }

        /// <summary>按参数名查类型；未找到返回 ""。</summary>
        private string ParameterType(string name)
            => _model.Parameters.FirstOrDefault(p => p.Name == name)?.Type ?? "";

        /// <summary>条件编辑器随所选参数类型切换：float/int 用 op+值，boolean 用 is true/is false，trigger 仅提示 fired。</summary>
        private void UpdateCondEditorForParam()
        {
            var param = _condParamCombo.SelectedItem as string;
            var type = param == null ? "" : ParameterType(param);
            var isBool = type == "boolean";
            var isTrigger = type == "trigger";
            _condOpCombo.IsVisible = !isBool && !isTrigger;
            _condValueBox.IsVisible = !isBool && !isTrigger;
            _condBoolCombo.IsVisible = isBool;
            _condTriggerLabel.IsVisible = isTrigger;
        }

        /// <summary>条件的人类可读描述（含 boolean/trigger 语义）。</summary>
        private string DescribeCondition(ConditionModel c)
        {
            var type = ParameterType(c.Param);
            if (type == "trigger") return c.Param + " fired";
            if (type == "boolean")
            {
                var isTrue = c.Op == "==" ? (c.Value != 0) : (c.Value == 0);
                return c.Param + " is " + (isTrue ? "true" : "false");
            }
            return c.Param + " " + c.Op + " " + c.Value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void AddTransition()
        {
            var fromName = SelectedStateName();
            var to = _toCombo.SelectedItem as string;
            if (fromName == null || string.IsNullOrEmpty(to) || to == fromName) return;

            TryParseDouble(_exitTimeBox.Text, out var exitTime);
            _model.Transitions.Add(new TransitionModel
            {
                From = fromName,
                To = to,
                HasExitTime = _exitTimeCheck.IsChecked == true,
                ExitTime = exitTime,
                Conditions = _draftConditions.ToList(),
            });
            _draftConditions.Clear();
            _exitTimeCheck.IsChecked = false;
            _exitTimeBox.Text = "0";
            _selectedTransition = -1;
            RefreshTransitions();
        }

        // ── 资源 GUID 辅助（与 Inspector 资源字段一致） ──

        private string ResolveGuidToDisplayName(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "";
            var path = ProjectPanel?.ResolvePath(guid);
            return string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path);
        }

        private string? ResolveDroppedGuid(IDataObject? data)
        {
            if (data == null) return null;
            var internalGuid = data.Get(ProjectPanel.AssetGuidFormat) as string;
            if (!string.IsNullOrEmpty(internalGuid)) return internalGuid;

            var paths = GetDroppedFilePaths(data);
            if (paths == null) return null;
            foreach (var path in paths)
            {
                if (!string.Equals(Path.GetExtension(path), ".dmclip", StringComparison.OrdinalIgnoreCase)) continue;
                var guid = ProjectPanel?.GetGuid(path);
                if (!string.IsNullOrEmpty(guid)) return guid;
            }
            return null;
        }

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
            catch { }

            if (data.Contains(DataFormats.FileNames))
            {
                var names = data.Get(DataFormats.FileNames) as string[];
                if (names != null && names.Length > 0) return names;
            }
            return null;
        }

        // ── 控件工厂 / 辅助 ──

        private void ShowEmptyState()
        {
            _paramList.Children.Clear();
            _stateList.Children.Clear();
            _transitionList.Children.Clear();
            _stateList.Children.Add(new TextBlock
            {
                Text = "Double-click a .dmanimator in the Project panel to edit",
                FontSize = 12,
                Foreground = DimBrush,
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        private static TextBlock CreateLabel(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static TextBox CreateTextBox(double width) => new()
        {
            Width = width,
            Height = 24, // 与下拉框同高；MinHeight=0 覆盖 Fluent 主题默认(~32)使 Height 生效
            MinHeight = 0,
            FontSize = 12,
            Padding = new Thickness(4, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static Button CreateToolButton(string text) => new()
        {
            Content = text,
            FontSize = 11,
            Height = 22,
            Padding = new Thickness(8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46)),
            Foreground = TextBrush,
        };

        private static ComboBox CreateCombo(double width) => new()
        {
            FontSize = 12,
            Height = 24, // 与输入框同高
            MinHeight = 0, // 覆盖 Fluent 主题默认(~32)
            // 覆盖模板 Padding(12,5,0,7)：上下共 12px 在 24px 高内放不下文本，
            // 会导致文本溢出遮住下边框；收窄后文本完整显示。
            Padding = new Thickness(8, 2, 0, 2),
            Width = width,
        };

        private static bool TryParseDouble(string? text, out double value)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // ── 模型 ──

        private sealed class AnimatorModel
        {
            public string Name { get; set; } = "NewController";
            public string DefaultState { get; set; } = "";
            public List<ParameterModel> Parameters { get; set; } = new();
            public List<StateModel> States { get; set; } = new();
            public List<TransitionModel> Transitions { get; set; } = new();
        }

        private sealed class ParameterModel
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "float";
            public double Value { get; set; }
        }

        private sealed class StateModel
        {
            public string Name { get; set; } = "State";
            public string ClipGuid { get; set; } = "";
        }

        private sealed class TransitionModel
        {
            public string From { get; set; } = "";
            public string To { get; set; } = "";
            public bool HasExitTime { get; set; }
            public double ExitTime { get; set; }
            public List<ConditionModel> Conditions { get; set; } = new();
        }

        private sealed class ConditionModel
        {
            public string Param { get; set; } = "";
            public string Op { get; set; } = ">";
            public double Value { get; set; }
        }
    }
}

#pragma warning restore CS0618

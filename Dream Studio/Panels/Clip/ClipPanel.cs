using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dream.Studio.Panels.Inspector;
using Path = System.IO.Path;

namespace Dream.Studio.Panels.Clip
{
    /// <summary>
    /// 动画片段编辑器面板（Clip 曲线编辑器）：打开 .dmclip 文件，
    /// 以"轨道 × 时间轴"形式查看/编辑动画关键帧并保存回文件。
    ///
    /// 数据格式与引擎 AnimationClip.fromJson 解析约定一致：
    ///   { name, duration, loop, tracks: [{ target, componentType, field, fieldType,
    ///     keys: [{ time, value }] }] }
    /// target：目标子元素名（空串 = 动画所在元素自身；引擎沿 Transform 后代按名递归匹配），
    /// 用于一个片段同时控制多个子元素。fieldType：number | boolean | string | vector2 | color。
    ///
    /// 交互：
    /// - 工具栏：片段名 / 时长 / 循环 / 保存 / 当前时间
    /// - 轨道列表：+ Add Track 内联添加（目标子元素/组件类型/字段/字段类型），右键删除轨道
    /// - 时间轴：单击轨道行空白添加关键帧，拖动关键帧改时间，右键删除，
    ///   顶部标尺点击设置当前时间（播放头）
    /// - 底部值编辑器：选中轨道可编辑目标子元素名，选中关键帧后按字段类型编辑值
    /// </summary>
    internal sealed class ClipPanel : UserControl
    {
        private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26));
        private static readonly IBrush TrackBgBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));
        private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
        private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x2F));
        private static readonly IBrush TrackLineBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46));
        private static readonly IBrush KeyBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x8E, 0xC8));
        private static readonly IBrush KeySelectedBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x8C, 0x4A));
        private static readonly IBrush PlayheadBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x57, 0x4A));
        private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4D, 0x7A));
        private static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(30, 0xFF, 0xFF, 0xFF));

        private const double PxPerSecond = 100;
        private const double RowHeight = 26;
        private const double HeaderHeight = 20;
        private const double KeyRadius = 4;
        private const double HitRadius = 6;

        /// <summary>未选中元素时的兜底候选（与引擎 Transform 可动画字段一致）。</summary>
        private static readonly List<(string Name, string Type)> TransformFallbackFields = new()
        {
            ("position", "vector2"),
            ("rotation", "number"),
            ("scale", "vector2"),
        };

        /// <summary>可选的字段类型（Clip 数据层，与引擎 AnimationClip.fieldType 一致）。</summary>
        private static readonly string[] FieldTypeOptions =
            { "number", "boolean", "string", "vector2", "color" };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private string? _filePath;
        private ClipModel _model = new();
        private int _selectedTrack = -1;
        private (int Track, int Key)? _selectedKey;
        private (int Track, int Key)? _dragging;
        private bool _headDragging;      // 标尺拖动播放头中
        private double _currentTime;

        // 当前选中元素上下文（Unity 式 Add Track 动态候选）：元素 ID + 组件/字段元数据。
        private int _elementId = -1;
        private bool _recording;         // 录制中：Inspector 编辑自动打帧
        /// <summary>录制锚点元素 ID：录制开始时选中的元素；其子树为录制目标范围。</summary>
        private int _recordAnchorId = -1;
        /// <summary>当前选中元素相对锚点的子树路径（"A/B/C"；空串 = 锚点自身；null = 快照未到达未知）。</summary>
        private string? _recordTargetPath;
        private readonly Dictionary<string, List<(string Name, string Type)>> _elementFields = new();

        /// <summary>元素字段当前值缓存（component.field → 值），供预览初值记录。</summary>
        private readonly Dictionary<string, object?> _elementValues = new();

        /// <summary>预览开始时的字段初值（component.field → 值）；停止预览时写回还原。</summary>
        private Dictionary<string, object?>? _previewStartValues;

        // ── UI 控件 ──
        private readonly TextBox _nameBox;
        private readonly TextBox _durationBox;
        private readonly TextBox _fpsBox;
        private readonly CheckBox _loopBox;
        private readonly TextBox _timeBox;
        private readonly Button _recordButton;
        private readonly Button _previewButton;
        private readonly DispatcherTimer _previewTimer;
        private readonly TextBlock _previewList;
        private bool _previewPlaying;
        private readonly TextBlock _status;
        private readonly StackPanel _trackList;
        private readonly ScrollViewer _trackListScroll; // 轨道列表滚动器（与时间轴同步垂直滚动）
        private readonly Canvas _timeline;              // 轨道区内容（不含标尺）
        private readonly Canvas _rulerCanvas;           // 标尺刻度内容（固定顶部，随水平滚动平移）
        private readonly TranslateTransform _rulerOffset = new TranslateTransform(); // 标尺水平位移（复用实例，避免每次重建）
        private readonly Panel _rulerHost;              // 标尺固定容器（不随垂直滚动）
        private readonly ScrollViewer _timelineScroll;
        private readonly StackPanel _valueEditor;
        private readonly TextBlock _valueTitle;
        private readonly Button _addTrackButton;
        private readonly Border _addTrackRow;
        private readonly ComboBox _newComponentType;
        private readonly ComboBox _newField;
        private readonly ComboBox _newFieldType;
        private readonly TextBox _newTarget;
        private readonly Button _confirmAddTrack;
        private readonly Button _cancelAddTrack;
        private readonly Button _removeTrackButton;
        private readonly Button _deleteKeyButton;

        /// <summary>保存成功事件（供外部刷新等）。</summary>
        public event Action? Saved;

        /// <summary>预览采样事件（elementId, target, component, field, value）：播放时逐帧下发引擎驱动场景元素。</summary>
        public event Action<int, string, string, string, object?>? PreviewSample;

        public ClipPanel()
        {
            _nameBox = CreateTextBox(140);
            _durationBox = CreateTextBox(56);
            _durationBox.TextChanged += (_, _) =>
            {
                if (TryParseDouble(_durationBox.Text, out var d) && d > 0)
                {
                    _model.Duration = d;
                    RefreshTimeline();
                }
            };
            _fpsBox = CreateTextBox(44);
            _fpsBox.TextChanged += (_, _) =>
            {
                if (TryParseDouble(_fpsBox.Text, out var f) && f >= 1 && f != _model.Fps)
                {
                    _model.Fps = f;
                    // 帧率变更：已有关键帧重新吸附到新帧率（Unity 式）。
                    int removed = ResnapKeys();
                    _status.Text = removed > 0
                        ? $"FPS {f:0.##}: keys re-snapped ({removed} removed)"
                        : $"FPS {f:0.##}: keys re-snapped";
                    RefreshTrackList();
                    RefreshTimeline();
                    RefreshValueEditor();
                }
            };
            _loopBox = new CheckBox
            {
                Content = "Loop",
                FontSize = 12,
                Foreground = TextBrush,
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _loopBox.IsCheckedChanged += (_, _) => _model.Loop = _loopBox.IsChecked == true;

            var saveButton = new Button
            {
                Content = "Save",
                FontSize = 12,
                Height = 24,
                Padding = new Thickness(10, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46)),
                Foreground = TextBrush,
            };
            saveButton.Click += (_, _) => SaveClip();

            // 录制：开启后 Inspector 中的字段编辑自动在当前时间打关键帧（Unity 式）。
            _recordButton = new Button
            {
                Content = "Record",
                FontSize = 12,
                Height = 24,
                Padding = new Thickness(10, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46)),
                Foreground = TextBrush,
            };
            _recordButton.Click += (_, _) =>
            {
                if (_recording)
                {
                    StopRecording("");
                    return;
                }
                // 开始录制：必须已选中元素；记录该元素为录制锚点（其子树为录制范围）。
                if (_elementId < 0)
                {
                    _status.Text = "Select an element in the Hierarchy to start recording";
                    return;
                }
                _recordAnchorId = _elementId;
                _recordTargetPath = ""; // 锚点自身
                _recording = true;
                _recordButton.Content = "Stop Recording";
                _recordButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x57, 0x4A));
                _recordButton.BorderThickness = new Thickness(1.5);
                _status.Text = "Recording: Inspector edits will be keyed at current time";
            };

            // 预览：播放时按引擎采样规则推进时间并显示各轨道当前采样值。
            _previewButton = new Button
            {
                Content = "Play",
                FontSize = 12,
                Height = 24,
                Padding = new Thickness(10, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46)),
                Foreground = TextBrush,
            };
            _previewButton.Click += (_, _) => TogglePreview();
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _previewTimer.Tick += (_, _) => OnPreviewTick();

            _timeBox = CreateTextBox(64);
            _timeBox.TextChanged += (_, _) =>
            {
                if (TryParseDouble(_timeBox.Text, out var t) && t >= 0)
                {
                    _currentTime = t;
                    RefreshTimeline();
                }
            };

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
            toolbar.Children.Add(CreateLabel("Duration"));
            toolbar.Children.Add(_durationBox);
            toolbar.Children.Add(CreateLabel("FPS"));
            toolbar.Children.Add(_fpsBox);
            toolbar.Children.Add(_loopBox);
            toolbar.Children.Add(saveButton);
            toolbar.Children.Add(_recordButton);
            toolbar.Children.Add(_previewButton);
            toolbar.Children.Add(CreateLabel("Time"));
            toolbar.Children.Add(_timeBox);
            toolbar.Children.Add(_status);

            // ── 轨道列表列 ──
            _removeTrackButton = CreateToolButton("Remove Track");
            _removeTrackButton.IsEnabled = false;
            _removeTrackButton.Click += (_, _) => RemoveSelectedTrack();

            _addTrackButton = CreateToolButton("+ Add Track");
            // 总是展开输入行（不 toggle），并刷新候选组件/字段（Unity 式：跟随选中元素）。
            _addTrackButton.Click += (_, _) =>
            {
                RefreshAddTrackCandidates();
                _addTrackRow.IsVisible = true;
            };

            _newComponentType = CreateCombo();
            _newField = CreateCombo();
            // 字段类型由所选字段决定（引擎 FieldInfo.type 映射），只读展示，不可手改。
            _newFieldType = CreateCombo();
            _newFieldType.IsEnabled = false;
            // 目标子元素名：记录要控制的子元素（空串 = 当前选中元素自身）。
            _newTarget = CreateTextBox(110);
            // 组件变化 → 刷新字段候选与类型；字段变化 → 仅重推类型（类型始终跟随字段）。
            _newComponentType.SelectionChanged += (_, _) => RefreshFieldCandidates();
            _newField.SelectionChanged += (_, _) => RefreshFieldType();

            RefreshAddTrackCandidates();

            _confirmAddTrack = CreateToolButton("Add");
            _confirmAddTrack.Click += (_, _) => AddTrack();

            _cancelAddTrack = CreateToolButton("Cancel");
            _cancelAddTrack.Click += (_, _) => _addTrackRow.IsVisible = false;

            // 输入行用 WrapPanel：控件过多时自动换行，避免在窄列中被截断。
            var addRowInner = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(2),
            };
            addRowInner.Children.Add(CreateLabel("Target"));
            addRowInner.Children.Add(_newTarget);
            addRowInner.Children.Add(_newComponentType);
            addRowInner.Children.Add(_newField);
            addRowInner.Children.Add(_newFieldType);
            addRowInner.Children.Add(_confirmAddTrack);
            addRowInner.Children.Add(_cancelAddTrack);
            _addTrackRow = new Border
            {
                Child = addRowInner,
                IsVisible = false,
                Background = TrackBgBrush,
                Padding = new Thickness(2),
            };

            _trackList = new StackPanel { Orientation = Orientation.Vertical };

            // 顶部行左列：工具按钮 + Add Track 输入（与右侧标尺同一行，保证下方轨道列表/时间轴顶部对齐）。
            var trackTools = new StackPanel { Orientation = Orientation.Vertical };
            var trackButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(4),
            };
            trackButtons.Children.Add(_addTrackButton);
            trackButtons.Children.Add(_removeTrackButton);
            trackTools.Children.Add(trackButtons);
            trackTools.Children.Add(_addTrackRow);

            // 轨道列表：行高与时间轴 RowHeight 一致，滚动同步后行与行对齐。
            _trackListScroll = new ScrollViewer
            {
                Content = _trackList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                // 与时间轴一致：轨道少时内容靠顶显示。
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            // 轨道列表滚动 → 同步时间轴垂直偏移（双向同步）。
            _trackListScroll.ScrollChanged += (_, _) =>
            {
                _timelineScroll.Offset = new Vector(_timelineScroll.Offset.X, _trackListScroll.Offset.Y);
            };
            // ── 时间轴 ──
            _timeline = new Canvas
            {
                Background = TrackBgBrush,
                ClipToBounds = true,
                // 显式左锚定（与标尺同款处理）：面板比内容宽时内容不会被居中/拉伸，
                // 与标尺始终左对齐，保证刻度与网格严格对齐。
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _timeline.PointerPressed += OnTimelinePressed;
            _timeline.PointerMoved += OnTimelineMoved;
            _timeline.PointerReleased += OnTimelineReleased;
            _timelineScroll = new ScrollViewer
            {
                Content = _timeline,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = TrackBgBrush,
                // 内容顶部对齐：轨道少（内容低于视口）时时间轴靠顶显示，不垂直居中。
                VerticalContentAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            // 视口尺寸变化（窗口缩放/停靠）后重算时间轴高度，保证低轨道数时仍贴顶；
            // 同时重同步标尺位移（水平滚动量可能在视口变化后被钳制）。
            _timelineScroll.SizeChanged += (_, _) =>
            {
                ApplyRulerOffset();
                if (_timeline.Height < _timelineScroll.Viewport.Height)
                    RefreshTimeline();
            };
            // 时间轴滚动 → 同步标尺水平平移 + 轨道列表垂直偏移。
            _timelineScroll.ScrollChanged += (_, _) =>
            {
                ApplyRulerOffset();
                _trackListScroll.Offset = new Vector(0, _timelineScroll.Offset.Y);
            };
            // 每次布局后重同步标尺：水平偏移在布局/内容尺寸变化时可能被 ScrollViewer
            // 重新钳制，仅靠 ScrollChanged 存在事件间隙；布局后读取最终值保证严格对齐。
            _timelineScroll.LayoutUpdated += (_, _) => ApplyRulerOffset();

            // 标尺层：固定在 ScrollViewer 外部顶部（不随垂直滚动），内容随水平滚动平移。
            // 显式左锚定：避免在比内容宽的容器中被居中对齐（与时间轴内容左对齐一致）。
            _rulerCanvas = new Canvas
            {
                ClipToBounds = true,
                Background = TrackBgBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _rulerCanvas.PointerPressed += OnRulerPressed;
            _rulerCanvas.PointerMoved += OnRulerMoved;
            _rulerCanvas.PointerReleased += OnRulerReleased;
            _rulerHost = new Panel
            {
                Height = HeaderHeight,
                ClipToBounds = true,
            };
            _rulerHost.Children.Add(_rulerCanvas);

            // 顶部行：左=工具/Add 行，右=标尺（同一行 → 顶部对齐）。
            var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
            Grid.SetColumn(trackTools, 0);
            Grid.SetColumn(_rulerHost, 1);
            topRow.Children.Add(trackTools);
            topRow.Children.Add(_rulerHost);

            // 中部：左=轨道列表，右=时间轴（同一行 → 顶部对齐；行高一致 + 滚动同步 → 行级对齐）。
            var midArea = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
            Grid.SetColumn(_trackListScroll, 0);
            Grid.SetColumn(_timelineScroll, 1);
            midArea.Children.Add(_trackListScroll);
            midArea.Children.Add(_timelineScroll);

            var clipBody = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            Grid.SetRow(topRow, 0);
            Grid.SetRow(midArea, 1);
            clipBody.Children.Add(topRow);
            clipBody.Children.Add(midArea);

            // ── 值编辑器 ──
            _valueTitle = new TextBlock
            {
                FontSize = 12,
                Foreground = DimBrush,
                Margin = new Thickness(6, 2, 0, 2),
            };
            _valueEditor = new StackPanel { Orientation = Orientation.Vertical };
            _deleteKeyButton = CreateToolButton("Delete Key");
            _deleteKeyButton.IsEnabled = false;
            _deleteKeyButton.Click += (_, _) => DeleteSelectedKey();
            var valueHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };
            valueHeader.Children.Add(_valueTitle);
            valueHeader.Children.Add(_deleteKeyButton);

            _previewList = new TextBlock
            {
                FontSize = 11,
                Foreground = DimBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 0, 0, 4),
            };

            var bottom = new StackPanel { Orientation = Orientation.Vertical };
            bottom.Children.Add(valueHeader);
            bottom.Children.Add(_valueEditor);
            bottom.Children.Add(new TextBlock
            {
                Text = "Preview (current time samples)",
                FontSize = 11,
                Foreground = DimBrush,
                Margin = new Thickness(6, 4, 0, 2),
            });
            bottom.Children.Add(_previewList);

            var layout = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Background = BgBrush,
            };
            Grid.SetRow(toolbar, 0);
            Grid.SetRow(clipBody, 1);
            Grid.SetRow(bottom, 2);
            layout.Children.Add(toolbar);
            layout.Children.Add(clipBody);
            layout.Children.Add(bottom);

            Content = layout;
            ShowEmpty("Double-click a .dmclip in the Project panel to edit");
        }

        // ── 打开 / 保存 ──

        /// <summary>打开并加载 .dmclip 文件。失败时清空并显示错误状态。</summary>
        public void OpenClip(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var model = JsonSerializer.Deserialize<ClipModel>(json, JsonOptions);
                _model = model ?? new ClipModel();
            }
            catch (Exception ex)
            {
                _model = new ClipModel();
                _status.Text = "Load failed: " + ex.Message;
            }

            _filePath = path;
            _selectedTrack = -1;
            _selectedKey = null;
            _currentTime = 0;
            StopPreview();
            Normalize();
            // 打开时按当前帧率重新吸附一次（处理外部编辑/旧数据未对齐）。
            ResnapKeys();
            RefreshAll();
        }

        /// <summary>将当前模型写回 .dmclip 文件。返回是否成功。</summary>
        public bool SaveClip()
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

        // ── 元素上下文与候选（Unity 式 Add Track） ──

        /// <summary>选中元素变化时由 MainWindow 通知：退出预览（还原初值）并清空候选（等快照到达）。
        /// 录制中切换到新元素：目标路径未知，等新元素快照到达后判定子树归属。</summary>
        public void SetSelectedElement(int elementId)
        {
            if (elementId == _elementId) return;
            StopPreview(); // 切换元素时退出预览并还原旧元素初值
            _elementId = elementId;
            _elementFields.Clear();
            _elementValues.Clear();
            if (_recording) _recordTargetPath = null; // 等待新元素快照确定子树路径
        }

        /// <summary>收到 Inspector 快照（EngineSession 转发）：缓存选中元素的组件/字段元数据与当前值。
        /// 录制中：新选中元素须在录制锚点子树内，否则终止录制；在子树内则记录其相对锚点的路径。</summary>
        public void OnElementSnapshot(InspectorSnapshotData snapshot)
        {
            if (snapshot.ElementId != _elementId) return;
            _elementFields.Clear();
            _elementValues.Clear();
            foreach (var comp in snapshot.Components)
            {
                var fields = new List<(string Name, string Type)>();
                foreach (var f in comp.Fields)
                {
                    // action 类型（按钮，如 Fit to Bounds）不可动画，排除。
                    if (f.Type == "action") continue;
                    fields.Add((f.Name, f.Type));
                    _elementValues[comp.Name + "." + f.Name] = ExtractValue(f);
                }
                _elementFields[comp.Name] = fields;
            }

            // 录制中的子树归属判定（基于快照祖先链，根 → 直接父级）。
            if (_recording && _recordAnchorId >= 0)
            {
                if (snapshot.ElementId == _recordAnchorId)
                {
                    _recordTargetPath = ""; // 回到锚点自身
                }
                else if (TryComputeSubtreePath(snapshot, _recordAnchorId, out var path))
                {
                    _recordTargetPath = path;
                }
                else
                {
                    // 新选中元素不在录制锚点子树内：终止录制。
                    StopRecording("Selection outside recording subtree — recording stopped");
                }
            }
        }

        /// <summary>在快照祖先链（根 → 直接父级）中定位锚点；找到则输出锚点之后到当前元素的
        /// 名称路径（"A/B/C"，不含锚点自身），否则返回 false（不在锚点子树内）。</summary>
        private static bool TryComputeSubtreePath(InspectorSnapshotData snapshot, int anchorId, out string path)
        {
            path = "";
            var anc = snapshot.Ancestors;
            var idx = anc.FindIndex(a => a.Id == anchorId);
            if (idx < 0) return false;
            var names = new List<string>(anc.Count - idx);
            for (int i = idx + 1; i < anc.Count; i++) names.Add(anc[i].Name);
            names.Add(snapshot.ElementName);
            path = string.Join("/", names);
            return true;
        }

        /// <summary>终止录制并复位录制状态（按钮/边框/状态栏）。</summary>
        private void StopRecording(string reason)
        {
            _recording = false;
            _recordAnchorId = -1;
            _recordTargetPath = null;
            _recordButton.Content = "Record";
            _recordButton.BorderBrush = Brushes.Transparent;
            _recordButton.BorderThickness = new Thickness(0);
            _status.Text = reason ?? "";
        }

        /// <summary>快照字段值（JsonElement）→ 引擎可接收的 CLR 值（vector2 为 {x,y}，color 为 double）。</summary>
        private static object? ExtractValue(InspectorFieldData f)
        {
            if (f.Value is not { } v) return null;
            switch (f.Type)
            {
                case "number":
                    return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;
                case "boolean":
                    return v.ValueKind == JsonValueKind.True;
                case "string":
                case "resource":
                    return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                case "vector2":
                {
                    double x = 0, y = 0;
                    if (v.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number) x = xe.GetDouble();
                    if (v.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number) y = ye.GetDouble();
                    return new { x, y };
                }
                case "color":
                    return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;
                default:
                    return null;
            }
        }

        /// <summary>刷新 Add Track 输入行候选：选中元素组件列表；未选中/未知时仅 Transform。</summary>
        private void RefreshAddTrackCandidates()
        {
            _newComponentType.Items.Clear();
            var names = _elementFields.Count > 0
                ? _elementFields.Keys.ToList()
                : new List<string> { "Transform" };
            foreach (var n in names) _newComponentType.Items.Add(n);
            _newComponentType.SelectedIndex = names.Count > 0 ? 0 : -1;
            RefreshFieldCandidates();
        }

        /// <summary>按所选组件刷新字段候选与字段类型（类型由字段自动推断，只读展示）。</summary>
        private void RefreshFieldCandidates()
        {
            _newField.Items.Clear();
            _newFieldType.Items.Clear();
            foreach (var t in FieldTypeOptions) _newFieldType.Items.Add(t);

            var comp = _newComponentType.SelectedItem as string;
            var fields = comp != null && _elementFields.TryGetValue(comp, out var f)
                ? f
                : TransformFallbackFields;

            foreach (var (name, _) in fields) _newField.Items.Add(name);
            _newField.SelectedIndex = fields.Count > 0 ? 0 : -1;

            RefreshFieldType();
        }

        /// <summary>按当前所选字段重推字段类型（引擎 FieldInfo.type → Clip fieldType）。</summary>
        private void RefreshFieldType()
        {
            var comp = _newComponentType.SelectedItem as string;
            var fields = comp != null && _elementFields.TryGetValue(comp, out var f)
                ? f
                : TransformFallbackFields;
            if (_newField.SelectedItem is string fn && fields.Count > 0)
            {
                var t = fields.Find(x => x.Name == fn).Type;
                _newFieldType.SelectedItem = MapFieldType(t);
            }
        }

        /// <summary>引擎 FieldInfo.type → Clip fieldType。resource 归为 string（GUID 步进赋值）。</summary>
        private static string MapFieldType(string engineType)
        {
            switch (engineType)
            {
                case "number": return "number";
                case "boolean": return "boolean";
                case "vector2": return "vector2";
                case "color": return "color";
                case "string":
                case "resource":
                default:
                    return "string";
            }
        }

        // ── 帧率重吸附 ──

        /// <summary>
        /// 把所有关键帧时间吸附到当前帧率（round(t × fps) / fps）。
        /// 吸附后同轨道时间冲突时顺延 +1 帧，超出时长则移除。
        /// 返回被移除的关键帧数量。帧率变更与打开片段时调用。
        /// </summary>
        private int ResnapKeys()
        {
            double fps = _model.Fps > 0 ? _model.Fps : 24;
            double frame = 1.0 / fps;
            int removed = 0;
            foreach (var track in _model.Tracks)
            {
                foreach (var key in track.Keys)
                    key.Time = Math.Round(key.Time * fps) / fps;

                track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                var kept = new List<KeyModel>();
                double prev = double.NegativeInfinity;
                foreach (var key in track.Keys)
                {
                    var t = key.Time;
                    if (t <= prev)
                    {
                        t = prev + frame;
                        if (t > _model.Duration + 1e-9)
                        {
                            removed++;
                            continue; // 无法放置，移除
                        }
                        key.Time = t;
                    }
                    prev = key.Time;
                    kept.Add(key);
                }
                track.Keys = kept;
            }
            return removed;
        }

        // ── 录制（Inspector 编辑自动打帧） ──

        /// <summary>录制中由 MainWindow 转发 Inspector 字段编辑：创建/复用轨道并在当前时间打帧。
        /// 目标路径 = 当前选中元素相对录制锚点的子树路径（锚点自身为空串）。</summary>
        public void OnInspectorFieldEdited(int elementId, string component, string field, object? value)
        {
            if (!_recording || string.IsNullOrEmpty(_filePath)) return;
            if (elementId != _elementId) return;          // 只录制当前选中元素
            if (string.IsNullOrEmpty(component) || string.IsNullOrEmpty(field)) return;
            if (value == null) return;                    // null（如 pivot 自动）不录制
            if (_recordTargetPath == null) return;        // 新元素快照未到达，目标路径未知

            var fieldType = InferFieldTypeFor(component, field, value);
            var jsonValue = ValueToJsonNode(fieldType, value);
            if (jsonValue == null) return;

            // 复用已存在轨道（同目标路径/组件/字段），否则新建。
            var target = _recordTargetPath;
            var track = _model.Tracks.Find(t =>
                t.Target == target && t.ComponentType == component && t.Field == field);
            if (track == null)
            {
                track = new TrackModel
                {
                    Target = target,
                    ComponentType = component,
                    Field = field,
                    FieldType = fieldType,
                    Keys = new List<KeyModel>(),
                };
                _model.Tracks.Add(track);
            }

            // 当前时间吸附到帧。
            double fps = _model.Fps > 0 ? _model.Fps : 24;
            double t = Math.Round(_currentTime * fps) / fps;
            t = Math.Clamp(t, 0, _model.Duration);

            // 已有该时刻关键帧 → 改值；否则新增。
            var key = track.Keys.Find(k => Math.Abs(k.Time - t) < 1e-6);
            if (key == null)
            {
                key = new KeyModel { Time = t, Value = jsonValue };
                track.Keys.Add(key);
                track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            else
            {
                key.Value = jsonValue;
            }

            var shown = string.IsNullOrEmpty(target) ? component + "." + field : $"{target} ▸ {component}.{field}";
            _status.Text = $"Recorded {shown} @ {t:0.###}s";
            RefreshTrackList();
            RefreshTimeline();
            RefreshValueEditor();
        }

        /// <summary>录制时推断字段类型：优先快照元数据，其次从值形态推断。</summary>
        private string InferFieldTypeFor(string component, string field, object? value)
        {
            if (_elementFields.TryGetValue(component, out var fields))
            {
                var match = fields.Find(x => x.Name == field);
                if (!string.IsNullOrEmpty(match.Name))
                    return MapFieldType(match.Type);
            }
            return InferFieldTypeFromValue(value);
        }

        /// <summary>从值形态推断字段类型：{x,y}→vector2，bool→boolean，string→string，否则 number。</summary>
        private static string InferFieldTypeFromValue(object? value)
        {
            switch (value)
            {
                case bool:
                    return "boolean";
                case string:
                    return "string";
                case double:
                    return "number";
                default:
                    // 匿名对象 {x,y} 等
                    return "vector2";
            }
        }

        /// <summary>把 Inspector 编辑值转为 Clip 的 JsonNode 存储值。</summary>
        private static JsonNode? ValueToJsonNode(string fieldType, object? value)
        {
            switch (fieldType)
            {
                case "boolean":
                    return JsonValue.Create(value is bool b && b);
                case "string":
                    return JsonValue.Create(value?.ToString() ?? "");
                case "vector2":
                    if (value is JsonElement je && je.ValueKind == JsonValueKind.Object)
                    {
                        double x = je.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number ? xe.GetDouble() : 0;
                        double y = je.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number ? ye.GetDouble() : 0;
                        return new JsonObject { ["x"] = x, ["y"] = y };
                    }
                    return ObjectToVector2(value);
                case "color":
                    // Inspector 颜色以 uint（0xRRGGBB）double 传递。
                    uint c = (Convert.ToUInt32(value ?? 0, CultureInfo.InvariantCulture)) & 0xFFFFFF;
                    return JsonValue.Create((double)c);
                case "number":
                default:
                    return value is double d
                        ? JsonValue.Create(d)
                        : JsonValue.Create(Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>从匿名对象 {x,y} / 带 x/y 属性的对象取 vector2 JsonNode。</summary>
        private static JsonNode? ObjectToVector2(object? value)
        {
            if (value == null) return null;
            var t = value.GetType();
            var xp = t.GetProperty("x");
            var yp = t.GetProperty("y");
            if (xp == null || yp == null) return null;
            double x = Convert.ToDouble(xp.GetValue(value), CultureInfo.InvariantCulture);
            double y = Convert.ToDouble(yp.GetValue(value), CultureInfo.InvariantCulture);
            return new JsonObject { ["x"] = x, ["y"] = y };
        }

        // ── 数据规范化 ──

        /// <summary>整理模型：duration/fps 取合法值、轨道与关键帧按时间排序、时间非负。</summary>
        private void Normalize()
        {
            if (_model.Duration <= 0) _model.Duration = 1;
            if (_model.Fps < 1) _model.Fps = 24;
            _model.Name = string.IsNullOrEmpty(_model.Name)
                ? Path.GetFileNameWithoutExtension(_filePath ?? "Clip") ?? "Clip"
                : _model.Name;
            foreach (var t in _model.Tracks)
            {
                t.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                for (int i = 0; i < t.Keys.Count; i++)
                    if (t.Keys[i].Time < 0) t.Keys[i].Time = 0;
            }
        }

        private void RefreshAll()
        {
            _nameBox.Text = _model.Name;
            _durationBox.Text = _model.Duration.ToString("0.##", CultureInfo.InvariantCulture);
            _fpsBox.Text = _model.Fps.ToString("0.##", CultureInfo.InvariantCulture);
            _loopBox.IsChecked = _model.Loop;
            _timeBox.Text = "0";
            RefreshTrackList();
            RefreshValueEditor();
            RefreshTimeline();
        }

        // ── 轨道列表 ──

        private void RefreshTrackList()
        {
            _trackList.Children.Clear();
            for (int i = 0; i < _model.Tracks.Count; i++)
            {
                var t = _model.Tracks[i];
                int index = i;
                // 目标子元素名：target 非空时以 "名称 ▸" 前缀展示（空串 = 自身）。
                var label = string.IsNullOrEmpty(t.Target)
                    ? $"{t.ComponentType}.{t.Field}  ({t.FieldType})  [{t.Keys.Count}]"
                    : $"{t.Target} ▸ {t.ComponentType}.{t.Field}  ({t.FieldType})  [{t.Keys.Count}]";
                var text = new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    Foreground = TextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                };
                var row = new Border
                {
                    // 行高与时间轴 RowHeight 一致，保证左右行级对齐。
                    Height = RowHeight,
                    Child = text,
                    Background = ReferenceEqualsSelected(i) ? SelectedBrush : Brushes.Transparent,
                };
                row.PointerPressed += (_, _) =>
                {
                    SelectTrack(index);
                };
                row.ContextMenu = BuildTrackMenu(index);
                _trackList.Children.Add(row);
            }
            _removeTrackButton.IsEnabled = _selectedTrack >= 0;
        }

        private bool ReferenceEqualsSelected(int index) => index == _selectedTrack;

        private ContextMenu BuildTrackMenu(int index)
        {
            var delete = new MenuItem { Header = "Delete Track", FontSize = 11 };
            delete.Click += (_, _) =>
            {
                if (index < 0 || index >= _model.Tracks.Count) return;
                _model.Tracks.RemoveAt(index);
                if (_selectedTrack >= _model.Tracks.Count) _selectedTrack = -1;
                _selectedKey = null;
                RefreshTrackList();
                RefreshValueEditor();
                RefreshTimeline();
            };
            return new ContextMenu { Items = { delete } };
        }

        private void SelectTrack(int index)
        {
            _selectedTrack = index;
            _selectedKey = null;
            RefreshTrackList();
            RefreshValueEditor();
            RefreshTimeline();
        }

        private void AddTrack()
        {
            var componentType = _newComponentType.SelectedItem as string;
            var field = _newField.SelectedItem as string;
            if (string.IsNullOrEmpty(componentType) || string.IsNullOrEmpty(field)) return;
            // 字段类型由字段决定（Add Track 行只读展示推断值，无法手改）。
            var fieldType = _newFieldType.SelectedItem as string
                            ?? InferFieldType(field);

            _model.Tracks.Add(new TrackModel
            {
                Target = _newTarget.Text?.Trim() ?? "",
                ComponentType = componentType,
                Field = field,
                FieldType = fieldType,
                Keys = new List<KeyModel>(),
            });
            _selectedTrack = _model.Tracks.Count - 1;
            _selectedKey = null;
            _addTrackRow.IsVisible = false;
            RefreshTrackList();
            RefreshValueEditor();
            RefreshTimeline();
        }

        private void RemoveSelectedTrack()
        {
            if (_selectedTrack < 0 || _selectedTrack >= _model.Tracks.Count) return;
            _model.Tracks.RemoveAt(_selectedTrack);
            _selectedTrack = -1;
            _selectedKey = null;
            RefreshTrackList();
            RefreshValueEditor();
            RefreshTimeline();
        }

        /// <summary>根据字段名推断常用字段类型（可被用户覆盖）。</summary>
        private static string InferFieldType(string field)
        {
            switch (field)
            {
                case "position":
                case "scale":
                    return "vector2";
                case "rotation":
                case "x":
                case "y":
                    return "number";
                default:
                    return "number";
            }
        }

        // ── 时间轴 ──

        private void RefreshTimeline()
        {
            _timeline.Children.Clear();
            _rulerCanvas.Children.Clear();

            double width = Math.Max(600, _model.Duration * PxPerSecond + 40);
            double contentHeight = _model.Tracks.Count * RowHeight;
            // 内容低于视口时填满视口高度：ScrollView 无剩余空间可居中，轨道行贴顶显示。
            double height = Math.Max(contentHeight, _timelineScroll.Viewport.Height);
            _timeline.Width = width;
            _timeline.Height = height;
            _rulerCanvas.Width = width;
            _rulerCanvas.Height = HeaderHeight;

            // 背景轨道行（交替底色）。
            for (int i = 0; i < _model.Tracks.Count; i++)
            {
                var rowBg = new Rectangle
                {
                    Fill = i % 2 == 0 ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(12, 0xFF, 0xFF, 0xFF)),
                    Width = width,
                    Height = RowHeight,
                };
                Canvas.SetTop(rowBg, i * RowHeight);
                Canvas.SetLeft(rowBg, 0);
                _timeline.Children.Add(rowBg);
            }

            // 时间网格：每 0.5s 一条竖线（轨道区整高）；刻度标签画在固定标尺层。
            double step = 0.5;
            for (double t = 0; t <= _model.Duration + 0.001; t += step)
            {
                var line = new Line
                {
                    StartPoint = new Point(t * PxPerSecond, 0),
                    EndPoint = new Point(t * PxPerSecond, height),
                    Stroke = GridBrush,
                    StrokeThickness = 1,
                };
                _timeline.Children.Add(line);

                var label = new TextBlock
                {
                    Text = t.ToString("0.##", CultureInfo.InvariantCulture),
                    FontSize = 10,
                    Foreground = DimBrush,
                    Margin = new Thickness(2, 2, 0, 0),
                };
                Canvas.SetLeft(label, t * PxPerSecond);
                Canvas.SetTop(label, 0);
                _rulerCanvas.Children.Add(label);
            }

            // 每条轨道的基线与关键帧。
            for (int i = 0; i < _model.Tracks.Count; i++)
            {
                var track = _model.Tracks[i];
                double y = i * RowHeight;

                var baseline = new Line
                {
                    StartPoint = new Point(0, y + RowHeight / 2),
                    EndPoint = new Point(width, y + RowHeight / 2),
                    Stroke = TrackLineBrush,
                    StrokeThickness = 1,
                };
                _timeline.Children.Add(baseline);

                for (int k = 0; k < track.Keys.Count; k++)
                {
                    var key = track.Keys[k];
                    var rect = new Rectangle
                    {
                        Width = KeyRadius * 2,
                        Height = KeyRadius * 2,
                        Fill = (_selectedKey is (int ti, int ki) && ti == i && ki == k)
                            ? KeySelectedBrush
                            : KeyBrush,
                        // 命中区域稍大便于点击。
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(rect, key.Time * PxPerSecond - KeyRadius);
                    Canvas.SetTop(rect, y + RowHeight / 2 - KeyRadius);
                    _timeline.Children.Add(rect);
                }
            }

            // 播放头：轨道区整高 + 标尺层短线（两者水平对齐，随水平滚动同步平移）。
            if (_currentTime >= 0)
            {
                var head = new Line
                {
                    StartPoint = new Point(_currentTime * PxPerSecond, 0),
                    EndPoint = new Point(_currentTime * PxPerSecond, height),
                    Stroke = PlayheadBrush,
                    StrokeThickness = 1,
                };
                _timeline.Children.Add(head);

                var rulerHead = new Line
                {
                    StartPoint = new Point(_currentTime * PxPerSecond, 0),
                    EndPoint = new Point(_currentTime * PxPerSecond, HeaderHeight),
                    Stroke = PlayheadBrush,
                    StrokeThickness = 1,
                };
                _rulerCanvas.Children.Add(rulerHead);
            }

            ApplyRulerOffset();
        }

        /// <summary>标尺内容随时间轴水平滚动平移（垂直固定，不随垂直滚动移动）。
        /// 复用单一 TranslateTransform 实例，避免每次布局重建对象。</summary>
        private void ApplyRulerOffset()
        {
            _rulerOffset.X = -_timelineScroll.Offset.X;
            _rulerCanvas.RenderTransform = _rulerOffset;
        }

        /// <summary>时间轴按压：命中关键帧开始拖动；轨道行空白添加关键帧。（标尺拖动播放头在标尺层处理）</summary>
        private void OnTimelinePressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_timeline).Properties.IsLeftButtonPressed) return;
            var pos = e.GetPosition(_timeline);

            int trackIndex = (int)(pos.Y / RowHeight);
            if (trackIndex < 0 || trackIndex >= _model.Tracks.Count) return;
            _selectedTrack = trackIndex;

            // 命中该轨道已有关键帧 → 开始拖动。
            var track = _model.Tracks[trackIndex];
            for (int k = 0; k < track.Keys.Count; k++)
            {
                double kx = track.Keys[k].Time * PxPerSecond;
                if (Math.Abs(kx - pos.X) <= HitRadius)
                {
                    _selectedKey = (trackIndex, k);
                    _dragging = (trackIndex, k);
                    e.Pointer.Capture(_timeline);
                    RefreshTrackList();
                    RefreshValueEditor();
                    RefreshTimeline();
                    return;
                }
            }

            // 空白：添加关键帧并选中。
            double t = SnapToTime(pos.X);
            if (TrackHasKeyAt(track, t))
            {
                // 该时刻已有值：仅选中。
                for (int k = 0; k < track.Keys.Count; k++)
                    if (Math.Abs(track.Keys[k].Time - t) < 0.001) _selectedKey = (trackIndex, k);
            }
            else
            {
                track.Keys.Add(new KeyModel { Time = t, Value = DefaultValue(track.FieldType) });
                track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                int idx = track.Keys.FindIndex(k => Math.Abs(k.Time - t) < 0.001);
                _selectedKey = (trackIndex, idx);
            }
            RefreshTrackList();
            RefreshValueEditor();
            RefreshTimeline();
        }

        private void OnTimelineMoved(object? sender, PointerEventArgs e)
        {
            if (_dragging is not (int trackIndex, int keyIndex)) return;
            var pos = e.GetPosition(_timeline);
            if (trackIndex >= _model.Tracks.Count) return;
            var track = _model.Tracks[trackIndex];
            if (keyIndex >= track.Keys.Count) return;

            var t = SnapToTime(pos.X);
            // 与其他关键帧保持时间不重叠。
            for (int k = 0; k < track.Keys.Count; k++)
            {
                if (k == keyIndex) continue;
                if (Math.Abs(track.Keys[k].Time - t) < 0.001)
                {
                    t = Math.Max(0, Math.Min(t, _model.Duration));
                    return; // 与已有关键帧重叠时不移动
                }
            }
            track.Keys[keyIndex].Time = t;
            track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
            // 排序后重新定位选中索引。
            int newIndex = track.Keys.FindIndex(k => Math.Abs(k.Time - t) < 0.001);
            if (newIndex >= 0)
            {
                _selectedKey = (trackIndex, newIndex);
                _dragging = (trackIndex, newIndex);
            }
            RefreshTimeline();
            RefreshValueEditor();
        }

        private void OnTimelineReleased(object? sender, PointerReleasedEventArgs e)
        {
            _dragging = null;
            _headDragging = false;
            e.Pointer.Capture(null);
        }

        // ── 标尺（固定顶部）：点击/拖动播放头 ──

        private void OnRulerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_rulerCanvas).Properties.IsLeftButtonPressed) return;
            // GetPosition(_rulerCanvas) 已含水平滚动偏移（RenderTransform 逆变换），即内容坐标。
            var pos = e.GetPosition(_rulerCanvas);
            _headDragging = true;
            SetCurrentTime(pos.X);
            e.Pointer.Capture(_rulerCanvas);
        }

        private void OnRulerMoved(object? sender, PointerEventArgs e)
        {
            if (!_headDragging) return;
            var pos = e.GetPosition(_rulerCanvas);
            SetCurrentTime(pos.X);
        }

        private void OnRulerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _headDragging = false;
            e.Pointer.Capture(null);
        }

        /// <summary>将 x 像素坐标转为对齐到最近一帧（1/fps）的时间，并夹在 [0, duration]。</summary>
        private double SnapToTime(double x)
        {
            double fps = _model.Fps > 0 ? _model.Fps : 24;
            double t = x / PxPerSecond;
            t = Math.Round(t * fps) / fps;
            return Math.Clamp(t, 0, _model.Duration);
        }

        private static bool TrackHasKeyAt(TrackModel track, double time)
        {
            foreach (var k in track.Keys)
                if (Math.Abs(k.Time - time) < 0.001) return true;
            return false;
        }

        private void SetCurrentTime(double x)
        {
            double fps = _model.Fps > 0 ? _model.Fps : 24;
            double t = Math.Clamp(x / PxPerSecond, 0, _model.Duration);
            // 播放头同样对齐到帧，保证选中的时刻都是可落盘的帧时间。
            _currentTime = Math.Round(t * fps) / fps;
            _timeBox.Text = _currentTime.ToString("0.###", CultureInfo.InvariantCulture);
            RefreshTimeline();
        }

        // ── 值编辑器 ──

        private void RefreshValueEditor()
        {
            _valueEditor.Children.Clear();
            _valueTitle.Text = "";
            _deleteKeyButton.IsEnabled = false;

            // 选中轨道的目标子元素名编辑（轨道级，与关键帧选择无关）。
            if (_selectedTrack >= 0 && _selectedTrack < _model.Tracks.Count)
            {
                var selTrack = _model.Tracks[_selectedTrack];
                var targetBox = CreateTextBox(140);
                targetBox.Text = selTrack.Target ?? "";
                targetBox.TextChanged += (_, _) =>
                {
                    selTrack.Target = targetBox.Text?.Trim() ?? "";
                    RefreshTrackList();
                };
                var targetRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(6, 0, 0, 4),
                };
                targetRow.Children.Add(CreateLabel("Target"));
                targetRow.Children.Add(targetBox);
                targetRow.Children.Add(new TextBlock
                {
                    Text = "target subtree path, e.g. A/B/C; empty = this element",
                    FontSize = 10,
                    Foreground = DimBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                _valueEditor.Children.Add(targetRow);
            }

            if (_selectedKey is not (int trackIndex, int keyIndex)) return;
            if (trackIndex >= _model.Tracks.Count) return;
            var track = _model.Tracks[trackIndex];
            if (keyIndex >= track.Keys.Count) return;
            var key = track.Keys[keyIndex];

            _deleteKeyButton.IsEnabled = true;
            _valueTitle.Text =
                $"Key: {track.ComponentType}.{track.Field} @ {key.Time.ToString("0.##", CultureInfo.InvariantCulture)}s  ({track.FieldType})";

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 0, 0, 4) };

            switch (track.FieldType)
            {
                case "boolean":
                    var cb = new CheckBox
                    {
                        FontSize = 12,
                        Foreground = TextBrush,
                        Content = "Value",
                        IsChecked = GetBool(key.Value),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    cb.IsCheckedChanged += (_, _) =>
                    {
                        key.Value = JsonValue.Create(cb.IsChecked == true);
                        RefreshTimeline();
                    };
                    row.Children.Add(cb);
                    break;

                case "vector2":
                    var xb = CreateTextBox(70);
                    xb.Text = TryGetDouble(key.Value?["x"], out var xv) ? xv.ToString("0.###", CultureInfo.InvariantCulture) : "0";
                    var yb = CreateTextBox(70);
                    yb.Text = TryGetDouble(key.Value?["y"], out var yv) ? yv.ToString("0.###", CultureInfo.InvariantCulture) : "0";
                    row.Children.Add(CreateLabel("X"));
                    row.Children.Add(xb);
                    row.Children.Add(CreateLabel("Y"));
                    row.Children.Add(yb);
                    xb.TextChanged += (_, _) => UpdateVector2(key, xb.Text, yb.Text);
                    yb.TextChanged += (_, _) => UpdateVector2(key, xb.Text, yb.Text);
                    break;

                case "color":
                    var cbox = CreateTextBox(90);
                    cbox.Text = ColorToHex(GetColor(key.Value));
                    row.Children.Add(CreateLabel("#RRGGBB"));
                    row.Children.Add(cbox);
                    cbox.TextChanged += (_, _) =>
                    {
                        if (TryParseColor(cbox.Text, out var c)) { key.Value = JsonValue.Create((double)c); RefreshTimeline(); }
                    };
                    break;

                case "string":
                    var sb = CreateTextBox(160);
                    sb.Text = GetString(key.Value);
                    row.Children.Add(sb);
                    sb.TextChanged += (_, _) => { key.Value = JsonValue.Create(sb.Text ?? ""); };
                    break;

                default: // number
                    var nb = CreateTextBox(90);
                    nb.Text = TryGetDouble(key.Value, out var dv) ? dv.ToString("0.###", CultureInfo.InvariantCulture) : "0";
                    row.Children.Add(nb);
                    nb.TextChanged += (_, _) =>
                    {
                        if (TryParseDouble(nb.Text, out var v)) { key.Value = JsonValue.Create(v); RefreshTimeline(); }
                    };
                    break;
            }

            _valueEditor.Children.Add(row);
        }

        private void UpdateVector2(KeyModel key, string xt, string yt)
        {
            if (!TryParseDouble(xt, out var x) || !TryParseDouble(yt, out var y)) return;
            key.Value = new JsonObject { ["x"] = x, ["y"] = y };
            RefreshTimeline();
        }

        private void DeleteSelectedKey()
        {
            if (_selectedKey is not (int trackIndex, int keyIndex)) return;
            if (trackIndex >= _model.Tracks.Count) return;
            var track = _model.Tracks[trackIndex];
            if (keyIndex >= track.Keys.Count) return;
            track.Keys.RemoveAt(keyIndex);
            _selectedKey = null;
            RefreshTrackList();
            RefreshValueEditor();
            RefreshTimeline();
        }

        // ── 预览播放（复刻引擎 AnimationClip 采样规则：数值插值，非数值向前追踪） ──

        private void TogglePreview()
        {
            if (_model.Tracks.Count == 0) return;
            if (_previewPlaying)
            {
                StopPreview(); // 退出预览：还原初值
                return;
            }
            _previewPlaying = true;
            _previewButton.Content = "Pause";
            CaptureStartValues();
            if (_currentTime >= _model.Duration) _currentTime = 0;
            _previewTimer.Start();
        }

        private void StopPreview()
        {
            RestoreStartValues();
            _previewPlaying = false;
            _previewTimer.Stop();
            _previewButton.Content = "Play";
        }

        /// <summary>记录预览开始时的字段初值（从最近快照缓存取，逐轨道匹配）。
        /// 仅记录自身目标（target 空串）的轨道：子元素目标的初值不在选中元素快照中，
        /// 无法可靠还原，预览停止后保持最后采样值。</summary>
        private void CaptureStartValues()
        {
            _previewStartValues = new Dictionary<string, object?>();
            if (_elementId < 0) return;
            foreach (var t in _model.Tracks)
            {
                if (!string.IsNullOrEmpty(t.Target)) continue; // 子元素目标：跳过还原
                var key = t.ComponentType + "." + t.Field;
                if (_elementValues.TryGetValue(key, out var v))
                    _previewStartValues[key] = v;
            }
        }

        /// <summary>把预览初值逐字段写回引擎（复用预览下发通道），恢复预览前状态。</summary>
        private void RestoreStartValues()
        {
            if (_previewStartValues == null) return;
            if (_elementId >= 0)
            {
                foreach (var kv in _previewStartValues)
                {
                    var parts = kv.Key.Split('.', 2);
                    if (parts.Length != 2) continue;
                    PreviewSample?.Invoke(_elementId, "", parts[0], parts[1], kv.Value);
                }
            }
            _previewStartValues = null;
        }

        private void OnPreviewTick()
        {
            double dt = _previewTimer.Interval.TotalSeconds;
            double duration = _model.Duration > 0 ? _model.Duration : 1;
            double next = _currentTime + dt;
            if (_model.Loop)
            {
                next = Mod(next, duration);
            }
            else if (next >= duration)
            {
                next = duration;
                StopPreview(); // 非循环：播放到末尾自动暂停
            }
            _currentTime = next;
            RefreshPreviewUI();
            // 驱动场景：把各轨道采样值下发给引擎（轻量通道，无撤销/快照）。
            // target 用于引擎按子元素名定位目标元素；空串 = 当前选中元素自身。
            if (_elementId >= 0)
            {
                foreach (var t in _model.Tracks)
                {
                    var v = SampleTrackValue(t, _currentTime);
                    if (v != null) PreviewSample?.Invoke(_elementId, t.Target ?? "", t.ComponentType, t.Field, v);
                }
            }
        }

        private void RefreshPreviewUI()
        {
            _timeBox.Text = _currentTime.ToString("0.###", CultureInfo.InvariantCulture);
            RefreshTimeline();
            if (_model.Tracks.Count == 0) { _previewList.Text = ""; return; }
            var sb = new System.Text.StringBuilder();
            foreach (var t in _model.Tracks)
                sb.AppendLine($"{t.ComponentType}.{t.Field}  =  {FormatValue(SampleTrackValue(t, _currentTime), t.FieldType)}");
            _previewList.Text = sb.ToString();
        }

        private static double Mod(double x, double m)
        {
            if (m <= 0) return 0;
            double r = x % m;
            return r < 0 ? r + m : r;
        }

        /// <summary>按引擎 AnimationClip.sampleTrack 规则采样轨道当前时间值（返回原始值供下发引擎）。</summary>
        private object? SampleTrackValue(TrackModel track, double t)
        {
            if (track.Keys.Count == 0) return null;
            double duration = _model.Duration > 0 ? _model.Duration : 1;
            double ft = _model.Loop ? Mod(t, duration) : Math.Clamp(t, 0, duration);
            string type = track.FieldType;
            if (track.Keys.Count == 1) return KeyValueToObject(track.Keys[0].Value, type);

            // 非插值类型（boolean/string 等）：向前追踪最后一个 time <= ft 的关键帧。
            if (type is not ("number" or "vector2" or "color"))
            {
                JsonNode? last = null;
                foreach (var k in track.Keys)
                {
                    if (k.Time <= ft) last = k.Value;
                    else break;
                }
                return KeyValueToObject(last ?? track.Keys[0].Value, type);
            }

            // 插值类型：定位区间，线性插值。
            int lo = 0, hi = track.Keys.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (track.Keys[mid].Time <= ft) lo = mid + 1; else hi = mid;
            }
            var b = track.Keys[lo];
            var a = track.Keys[lo - 1];
            if (lo == 0 || b.Time <= a.Time) return KeyValueToObject(b.Value, type);
            double u = (ft - a.Time) / (b.Time - a.Time);
            return InterpolateValue(a.Value, b.Value, u, type);
        }

        /// <summary>关键帧 JsonNode 值 → 引擎可接收的 CLR 值（vector2 为 {x,y}，color 为 0xRRGGBB double）。</summary>
        private static object KeyValueToObject(JsonNode? v, string type)
        {
            switch (type)
            {
                case "boolean": return GetBool(v);
                case "string": return GetString(v);
                case "vector2": return new { x = GetNum(v?["x"]), y = GetNum(v?["y"]) };
                case "color": return (double)GetColor(v);
                default: return GetNum(v);
            }
        }

        /// <summary>两个关键帧值之间插值（返回引擎可接收的 CLR 值）。</summary>
        private static object InterpolateValue(JsonNode? av, JsonNode? bv, double u, string type)
        {
            switch (type)
            {
                case "vector2":
                {
                    double ax = GetNum(av?["x"]), ay = GetNum(av?["y"]);
                    double bx = GetNum(bv?["x"]), by = GetNum(bv?["y"]);
                    return new { x = ax + (bx - ax) * u, y = ay + (by - ay) * u };
                }
                case "color":
                {
                    uint ca = GetColor(av), cb = GetColor(bv);
                    double r = ((ca >> 16) & 0xFF) * (1 - u) + ((cb >> 16) & 0xFF) * u;
                    double g = ((ca >> 8) & 0xFF) * (1 - u) + ((cb >> 8) & 0xFF) * u;
                    double bl = (ca & 0xFF) * (1 - u) + (cb & 0xFF) * u;
                    return (double)(((uint)Math.Round(r) << 16) | ((uint)Math.Round(g) << 8) | (uint)Math.Round(bl));
                }
                default:
                {
                    double a = GetNum(av), b = GetNum(bv);
                    return a + (b - a) * u;
                }
            }
        }

        /// <summary>采样值格式化为展示文本。</summary>
        private static string FormatValue(object? v, string type)
        {
            switch (type)
            {
                case "boolean": return v is bool b ? (b ? "true" : "false") : "false";
                case "string": return "\"" + (v as string ?? "") + "\"";
                case "vector2":
                {
                    double x = 0, y = 0;
                    if (v != null)
                    {
                        var t = v.GetType();
                        var xp = t.GetProperty("x");
                        var yp = t.GetProperty("y");
                        if (xp != null) x = Convert.ToDouble(xp.GetValue(v), CultureInfo.InvariantCulture);
                        if (yp != null) y = Convert.ToDouble(yp.GetValue(v), CultureInfo.InvariantCulture);
                    }
                    return $"({x:0.###}, {y:0.###})";
                }
                case "color":
                    return v is double dc
                        ? "#" + ((uint)Math.Clamp(dc, 0, 0xFFFFFF)).ToString("X6")
                        : "#FFFFFF";
                default:
                    return v is double d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "0";
            }
        }

        /// <summary>取 JsonNode 数值（无/非法 → 0）。</summary>
        private static double GetNum(JsonNode? node)
            => TryGetDouble(node, out var d) ? d : 0;

        // ── 值解析辅助 ──

        private static JsonNode? DefaultValue(string fieldType)
        {
            switch (fieldType)
            {
                case "vector2": return new JsonObject { ["x"] = 0.0, ["y"] = 0.0 };
                case "boolean": return JsonValue.Create(false);
                case "string": return JsonValue.Create("");
                case "color": return JsonValue.Create(0xFFFFFF);
                default: return JsonValue.Create(0.0);
            }
        }

        private static bool TryGetDouble(JsonNode? node, out double value)
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<double>(out value)) return true;
                if (v.TryGetValue<int>(out var i)) { value = i; return true; }
                if (v.TryGetValue<long>(out var l)) { value = l; return true; }
            }
            value = 0;
            return false;
        }

        private static bool GetBool(JsonNode? node)
            => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        private static string GetString(JsonNode? node)
            => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

        private static uint GetColor(JsonNode? node)
            => TryGetDouble(node, out var d) ? (uint)Math.Clamp(d, 0, 0xFFFFFF) : 0xFFFFFFu;

        private static string ColorToHex(uint color)
            => "#" + color.ToString("X6");

        private static bool TryParseColor(string text, out uint color)
        {
            color = 0;
            var s = text?.Trim();
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith('#')) s = s.Substring(1);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out color)
                   && color <= 0xFFFFFF;
        }

        private static bool TryParseDouble(string? text, out double value)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // ── 控件工厂 ──

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

        /// <summary>候选下拉框（组件/字段/类型）：非可编辑，只能选候选，禁止手输不匹配的名称。</summary>
        private static ComboBox CreateCombo() => new()
        {
            FontSize = 12,
            Height = 24, // 与输入框同高
            MinHeight = 0, // 覆盖 Fluent 主题默认(~32)
            // 覆盖模板 Padding(12,5,0,7)：上下共 12px 在 24px 高内放不下文本，
            // 会导致文本溢出遮住下边框；收窄后文本完整显示。
            Padding = new Thickness(8, 2, 0, 2),
            Width = 100,
        };

        private void ShowEmpty(string text)
        {
            _trackList.Children.Clear();
            _trackList.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = DimBrush,
                Margin = new Thickness(8),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // ── 模型 ──

        private sealed class ClipModel
        {
            public string Name { get; set; } = "NewClip";
            public double Duration { get; set; } = 1.0;
            /// <summary>帧率：关键帧时间对齐到 1/Fps 秒（编辑期吸附，引擎按时间采样不受影响）。</summary>
            public double Fps { get; set; } = 24;
            public bool Loop { get; set; } = true;
            public List<TrackModel> Tracks { get; set; } = new();
        }

        private sealed class TrackModel
        {
            /// <summary>目标子元素名（元素名，引擎沿 Transform 后代递归匹配）；空串 = 动画所在元素自身。</summary>
            public string Target { get; set; } = "";
            public string ComponentType { get; set; } = "Transform";
            public string Field { get; set; } = "position";
            public string FieldType { get; set; } = "number";
            public List<KeyModel> Keys { get; set; } = new();
        }

        private sealed class KeyModel
        {
            public double Time { get; set; }
            public JsonNode? Value { get; set; }
        }
    }
}

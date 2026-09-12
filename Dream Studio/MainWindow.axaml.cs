using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
#if WINDOWS
using Avalonia.Win32;
#endif
using Dream.Studio.Docking;
using Dream.Studio.Engine;
using Dream.Studio.Panels;
using Dream.Studio.Panels.Animator;
using Dream.Studio.Panels.Audio;
using Dream.Studio.Panels.Clip;
using Dream.Studio.Panels.Hierarchy;
using Dream.Studio.Panels.Project;
using Dream.Studio.Settings;
using DockPanel = Dream.Studio.Docking.DockPanel;

namespace Dream.Studio
{
    public partial class MainWindow : Window
    {
        private readonly PanelRegistry _registry = new();
        private readonly DockWorkspace _workspace = new();
        private readonly SceneSurface _sceneSurface = new();
        private readonly ConsolePanel _consolePanel = new();
        private readonly ProjectPanel _projectPanel;
        private readonly HierarchyPanel _hierarchyPanel = new();
        private readonly Panels.Inspector.InspectorPanel _inspectorPanel = new();
        private readonly ClipPanel _clipPanel = new();
        private readonly AnimatorPanel _animatorPanel = new();
        private readonly AudioPanel _audioPanel = new();

        /// <summary>进入预制体之前编辑的场景路径：引擎左上角返回按钮据此切回。
        /// 只记一层——预制体不支持嵌套，也没有更深的文档栈。</summary>
        private string? _returnDocumentPath;
        private EngineSession? _session;

        public MainWindow()
        {
            InitializeComponent();

            // 启动项目：用户上次打开的项目就地访问（不从模板克隆）；未设置或已失效则回退到
            // 默认开发工作目录（.workspace/Project，启动时按模板重建，仅用于开发过程测试）。
            var savedProject = StudioSettings.Current.ProjectPath;
            var useSavedProject = !string.IsNullOrWhiteSpace(savedProject)
                                  && new ActionScriptProject(savedProject).IsValid();
            var startupRoot = useSavedProject ? savedProject : EnginePaths.WorkingProjectPath;
            _projectPanel = new ProjectPanel(startupRoot);

            // 上次记录的项目已失效（被移动/删除/结构被破坏）：本次回退默认开发工作目录，
            // 不清除设置（用户仍可通过 File > Open Project 重新选择）。
            if (!useSavedProject && !string.IsNullOrWhiteSpace(savedProject))
                ((IEngineConsoleSink)_consolePanel).WriteLine(
                    $"Saved project is not a valid ActionScript project, falling back to the default workspace: {savedProject}",
                    EngineConsoleLevel.Warning);

            Host.Manager = _workspace.MainManager;
            Host.Workspace = _workspace;

            // 监听窗口状态变化以更新最大化/还原按钮图标。
            PropertyChanged += MainWindow_PropertyChanged;

#if WINDOWS
            // Windows 11 Snap Layouts：钩住 WM_NCHITTEST，当鼠标位于自定义最大化按钮上时
            // 返回 HTMAXBUTTON，让系统识别该区域为最大化按钮以弹出贴靠布局。
            Win32Properties.AddWndProcHookCallback(this, WndProc);
#endif

            RegisterPanels();
            // 先构建布局再绑定菜单：避免构建过程中 LayoutChanged 的中间状态
            // 触发 Sync 错误移除尚未 Dock 的面板实例。
            BuildInitialLayout(_workspace.MainManager);
            PanelMenuItem.Bind(_registry, _workspace);

            // 引擎子项目会话：启动时确保模板 → debug 编译 → 运行 ADL → 嵌入 Scene 面板。
            // ConsolePanel 作为 IEngineConsoleSink 注入：ADL 输出与 Engine 日志都汇入 Console。
            // HierarchyPanel 注入：接收引擎元素树快照，命令通过事件回调发送。
            _session = new EngineSession(
                _sceneSurface,
                new ActionScriptProject(startupRoot),
                new TemplateCloner(EnginePaths.TemplateSourcePath),
                new ComponentIndexGenerator(),
                new MxmlcCompiler(StudioSettings.Current.AirSdkPath, _consolePanel),
                new AdlRunner(StudioSettings.Current.AirSdkPath, _consolePanel),
#if WINDOWS
                new Win32TrackingWindowEmbedder(),
#else
                null,
#endif
                _consolePanel,
                _hierarchyPanel,
                _inspectorPanel,
                _projectPanel,
                snap => _clipPanel.OnElementSnapshot(snap),
                // 仅默认开发工作目录从模板克隆；用户打开的项目就地编译。
                cloneFromTemplate: !useSavedProject || IsDefaultWorkspace(startupRoot));

            // Hierarchy 命令转发：面板 → EngineSession → 引擎。
            _hierarchyPanel.CommandRequested += OnHierarchyCommand;
            // Hierarchy 选中 → 通知 Inspector 请求字段。
            _hierarchyPanel.SelectionChanged += OnHierarchySelectionChanged;
            // Hierarchy 右键 "Create Prefab..." → 引擎序列化子树 + 落盘为 .prefab 资产。
            _hierarchyPanel.CreatePrefabRequested += OnCreatePrefabRequested;
            // Hierarchy 拖入 .prefab 资产 → 按数据实例化子树。
            _hierarchyPanel.PrefabDropRequested += OnPrefabDropRequested;
            // Hierarchy 右键 "Revert to Prefab" → 清空该实例 override 并重推源。
            _hierarchyPanel.RevertPrefabRequested += elementId => _ = _session?.RevertPrefabAsync(elementId);
            // Inspector 请求与编辑转发到 EngineSession。
            _inspectorPanel.RequestInspector += OnInspectorRequest;
            _inspectorPanel.FieldEdited += OnInspectorFieldEdited;
            _inspectorPanel.RequestComponentList += OnInspectorRequestComponentList;
            _inspectorPanel.AddComponentRequested += OnInspectorAddComponent;
            _inspectorPanel.RemoveComponentRequested += OnInspectorRemoveComponent;
            _inspectorPanel.PasteComponentRequested += OnInspectorPasteComponent;
            _inspectorPanel.SetEnabledRequested += (id, comp, enabled) =>
                _ = _session?.SetEnabledAsync(id, comp, enabled);
            // Inspector 顶部 "Revert" → 清空该实例 override 并重推源（与 Hierarchy 右键同一入口）。
            _inspectorPanel.RevertPrefabRequested += elementId => _ = _session?.RevertPrefabAsync(elementId);
            // Clip 预览采样值 → 引擎（驱动场景元素；target 为子元素名，空串 = 元素自身），
            // 与录制走不同通道（无撤销/快照）。
            _clipPanel.PreviewSample += (id, target, comp, field, val) =>
                _ = _session?.ApplyClipPreviewAsync(id, target, comp, field, val);
            // 全局音频面板：主/组音量 → 引擎 AudioManager。
            _audioPanel.MasterVolumeChanged += v =>
                _ = _session?.SetAudioVolumeAsync(v, null, null);
            _audioPanel.GroupVolumeChanged += (group, v) =>
                _ = _session?.SetAudioVolumeAsync(null, group, v);

            // ProjectPanel 重命名态变化 → 挂起/恢复 .as 热重载，避免重命名期间被打断。
            _projectPanel.RenamingStateChanged += paused => _session.SetFileWatcherPaused(paused);

            // Hierarchy / Inspector 资源字段需要 ProjectPanel 解析 GUID。
            _hierarchyPanel.ProjectPanel = _projectPanel;
            _inspectorPanel.ProjectPanel = _projectPanel;
            _animatorPanel.ProjectPanel = _projectPanel;

            // Project 面板双击 .space 场景文件 → 加载场景（脏场景会先提示保存）。
            _projectPanel.SceneLoadRequested += OnProjectSceneLoadRequested;

            // Project 面板双击 .prefab 预制体 → 进视口编辑该预制体（脏文档会先提示保存）。
            _projectPanel.PrefabOpenRequested += OnProjectPrefabOpenRequested;

            // Project 面板双击 .dmclip 动画片段 → 打开 Clip 曲线编辑器面板并加载。
            _projectPanel.ClipOpenRequested += OnProjectClipOpenRequested;

            // Project 面板双击 .dmanimator 动画状态机 → 打开 Animator 编辑器面板并加载。
            _projectPanel.AnimatorOpenRequested += OnProjectAnimatorOpenRequested;

            // Project 面板切分精灵 / 打包图集 → Console 摘要 + 重新推送资源状态。
            // 两者都新增了资源（精灵子资源、图集纹理与 .dmatlas 映射），
            // 引擎需要新的索引与图集映射才能引用。
            _projectPanel.AssetsChanged += msg =>
            {
                ((IEngineConsoleSink)_consolePanel).WriteLine(msg, EngineConsoleLevel.Info);
                _ = _session?.PushResourceStateAsync();
            };

            // 工具栏运行/暂停状态同步：EngineSession 状态变化时更新按钮标签与可用性。
            // StateChanged 可能在后台线程触发（重启流程含线程池等待），需封送到 UI 线程。
            _session.StateChanged += () => Dispatcher.UIThread.Post(UpdateToolbarState);
            UpdateToolbarState();

            // 文档状态变化（打开 / 保存 / 置脏）→ 刷新窗口标题与工具栏（预制体文档禁用运行）；
            // 可能后台线程触发。
            _session.DocumentChanged += () => Dispatcher.UIThread.Post(() =>
            {
                UpdateWindowTitle();
                UpdateToolbarState();
            });
            UpdateWindowTitle();

            // 引擎启动/重载阶段状态 → Scene 面板占位层；OnChannelConnected 可能后台线程触发。
            _session.EngineStatusChanged += text => Dispatcher.UIThread.Post(() => _sceneSurface.SetStatus(text));

            // 初始化标题栏 AIR SDK 状态按钮。
            UpdateSdkStatusButton();

            // 模板克隆完成后加载 ProjectPanel 文件树：首次启动时 attach 早于 StartAsync，
            // 不等待克隆即加载会读到上一次遗留的结构。ProjectReady 可能在后台线程触发。
            _session.ProjectReady += () => Dispatcher.UIThread.Post(() =>
            {
                _projectPanel.Refresh();
                _animatorPanel.OnProjectRefreshed();
            });

            // 场景保存成功后重建 Project 文件树：新保存的 .space 文件在懒加载树中不可见，
            // 需重新枚举目录后才出现在 Project 面板。SceneSaved 可能在后台线程触发。
            _session.SceneSaved += () => Dispatcher.UIThread.Post(() =>
            {
                _projectPanel.Refresh();
                _animatorPanel.OnProjectRefreshed();
            });

            // Engine 窗口快捷键请求保存（Ctrl+S/Ctrl+Shift+S）：消息可能在后台线程触发，
            // 封送 UI 线程执行（弹对话框/直接保存）。与引擎侧 DreamEngine.initEditorShortcuts 对应。
            _session.SaveRequested += saveAs =>
                Dispatcher.UIThread.Post(() => _ = HandleEngineSaveRequestAsync(saveAs));

            // 引擎左上角返回按钮（document.back）：切回进入预制体之前编辑的场景。
            _session.DocumentBackRequested += () =>
                Dispatcher.UIThread.Post(() => _ = ReturnToPreviousDocumentAsync());

            // 新场景首次保存时弹保存对话框获取路径，由 EngineSession 在运行前调用。
            _session.PromptForSavePath = PromptForSaveScenePathAsync;

            Opened += OnOpened;
            Closed += OnClosed;
            // 拖动主窗口时 Avalonia 不失效布局，LayoutUpdated 不触发；
            // 但 SceneSurface 的屏幕坐标已变，需主动重算以更新 Engine 窗口位置。
            PositionChanged += OnPositionChanged;
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            // 注册为主窗口：供跨窗口资源拖拽（CrossWindowDragService）作为拖放窗口。
            CrossWindowDragService.RegisterWindow(this);
            _ = _session?.StartAsync();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            CrossWindowDragService.UnregisterWindow(this);
            _session?.Stop();
        }

        private void OnPositionChanged(object? sender, PixelPointEventArgs e) => _session?.NotifyHostMoved();

        /// <summary>是否处于编辑器状态。运行模式（IsRunning）下仍允许场景编辑命令
        /// （Inspector / Hierarchy / 撤销重做，引擎侧同步放开）；仅场景切换/加载类
        /// 操作保持门控，避免用编辑器场景替换运行中的世界。</summary>
        private bool IsEditing => _session != null && !_session.IsRunning;

        /// <summary>Hierarchy 面板命令转发到引擎会话（含运行模式，允许运行中编辑场景）。</summary>
        private void OnHierarchyCommand(string action, object? payload)
        {
            _ = _session?.SendCommandAsync(action, payload);
        }

        /// <summary>Project 面板双击 .space 文件：若当前场景未保存则弹窗询问，然后加载目标场景。
        /// 运行模式下禁用，避免用编辑器场景替换运行中的世界。</summary>
        private async void OnProjectSceneLoadRequested(string path)
        {
            if (_session == null || !IsEditing) return;
            if (!await EnsureSceneSavedAsync("switch scene")) return;
            // 显式导航到场景：之前记录的"返回目标"已作废（用户自己选了新文档）。
            _returnDocumentPath = null;
            await _session.LoadSceneAsync(path);
        }

        /// <summary>Project 面板双击 .prefab：进视口编辑该预制体。预制体与 .space 格式同构，
        /// 引擎按同一路径加载，Studio 只记住"当前文档是预制体"（保存写回 .prefab、禁用运行）。
        /// 运行模式下禁用，避免用编辑器文档替换运行中的世界。</summary>
        private async void OnProjectPrefabOpenRequested(string path)
        {
            if (_session == null || !IsEditing) return;
            // 记录返回目标：只记"从场景进预制体"的那一次；已在预制体里再开另一个时保留原目标。
            if (!_session.IsEditingPrefab) _returnDocumentPath = _session.CurrentDocumentPath;
            if (!await EnsureSceneSavedAsync("open this prefab")) return;
            await _session.LoadSceneAsync(path);
        }

        /// <summary>
        /// 引擎左上角返回按钮：切回进入预制体之前编辑的场景。
        /// 进入前若场景从未保存过（Engine 启动时的空场景），没有可加载的路径，只提示不动作——
        /// 这种情况下用户从 Project 面板双击任意场景即可离开预制体。
        /// </summary>
        private async Task ReturnToPreviousDocumentAsync()
        {
            if (_session == null || !IsEditing) return;
            if (string.IsNullOrEmpty(_returnDocumentPath))
            {
                WriteConsole("[prefab] no previous scene to return to. Open a scene from the Project panel.");
                return;
            }
            if (!await EnsureSceneSavedAsync("return to the previous scene")) return;
            var path = _returnDocumentPath;
            _returnDocumentPath = null;
            await _session.LoadSceneAsync(path);
        }

        /// <summary>
        /// Project 面板双击 .dmclip：加载到 Clip 编辑器面板并打开（未打开时以浮动窗口显示）。
        /// </summary>
        private void OnProjectClipOpenRequested(string path)
        {
            _clipPanel.OpenClip(path);
            if (!_registry.IsOpen("Clip", _workspace.EnumerateAllPanels()))
                _workspace.FloatPanel(_registry.GetOrCreate("Clip"), null);
        }

        /// <summary>Project 面板双击 .dmanimator：加载到 Animator 编辑器面板并打开。
        /// Animator 面板始终以独立浮动窗口打开；其状态 clip 的跨窗口拖入由
        /// CrossWindowDragService 接管（内置 DragDrop 无法跨窗口路由）。</summary>
        private void OnProjectAnimatorOpenRequested(string path)
        {
            _animatorPanel.OpenAnimator(path);
            if (!_registry.IsOpen("Animator", _workspace.EnumerateAllPanels()))
                _workspace.FloatPanel(_registry.GetOrCreate("Animator"), null);
        }

        /// <summary>
        /// Hierarchy 右键 "Create Prefab..."：把选中元素（含子树）导出为 .prefab 资产。
        /// 引擎负责序列化子树并回传 JSON（与 .space 同构），Studio 负责落盘与注册 .meta。
        /// 原元素保持原样（不替换为实例），实例化由拖拽入口按需进行。
        /// </summary>
        private async void OnCreatePrefabRequested(int elementId, string elementName)
        {
            if (_session == null) return;
            if (_session.IsEditingPrefab)
            {
                WriteConsole("[prefab] nested prefabs are not supported: a prefab cannot define another prefab.");
                return;
            }

            var suggested = string.IsNullOrWhiteSpace(elementName) ? "NewPrefab" : elementName;
            var path = await PromptForSavePrefabPathAsync(suggested);
            if (path == null) return; // 用户取消

            var json = await _session.ExportPrefabAsync(elementId);
            if (json == null)
            {
                WriteConsole("[prefab] export failed: no response from engine.");
                return;
            }

            try
            {
                System.IO.File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                WriteConsole("[prefab] write failed: " + ex.Message);
                return;
            }

            _projectPanel.RegisterNewAsset(path);
            WriteConsole($"[prefab] created: {System.IO.Path.GetFileName(path)}");
        }

        /// <summary>
        /// Hierarchy 拖入 .prefab 资产：读取文件内容，命令引擎按数据实例化一棵子树。
        /// 引擎记录实例溯源（instanceId + GUID），随场景保存写回。
        /// </summary>
        private async void OnPrefabDropRequested(string guid, string path)
        {
            if (_session == null) return;
            if (_session.IsEditingPrefab)
            {
                WriteConsole("[prefab] nested prefabs are not supported: cannot drop a prefab into a prefab document.");
                return;
            }

            JsonElement data;
            try
            {
                data = JsonSerializer.Deserialize<JsonElement>(System.IO.File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                WriteConsole("[prefab] read failed: " + ex.Message);
                return;
            }
            if (data.ValueKind != JsonValueKind.Object) return;

            await _session.SendCommandAsync("instantiate", new { parentId = -1, guid, data });
        }

        /// <summary>弹保存文件对话框获取 prefab 资产路径。用户取消返回 null。</summary>
        private async Task<string?> PromptForSavePrefabPathAsync(string suggestedName)
        {
            IStorageFolder? startLocation = null;
            try
            {
                var projectRoot = _session?.ProjectRoot ?? EnginePaths.WorkingProjectPath;
                var startDir = ActionScriptProject.GetDefaultSaveDirectory(projectRoot);
                startLocation = await StorageProvider.TryGetFolderFromPathAsync(startDir);
            }
            catch { }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Prefab",
                DefaultExtension = "prefab",
                SuggestedFileName = suggestedName,
                SuggestedStartLocation = startLocation,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Prefab File")
                    {
                        Patterns = new[] { "*.prefab" }
                    }
                }
            });
            return file?.Path.LocalPath;
        }

        /// <summary>向 Console 面板写一行信息（prefab 等 Studio 侧操作的反馈）。</summary>
        private void WriteConsole(string message)
            => ((IEngineConsoleSink)_consolePanel).WriteLine(message, EngineConsoleLevel.Info);

        /// <summary>
        /// 若当前文档（场景或预制体）有未保存修改，弹窗询问保存/不保存/取消。
        /// 返回 true 表示可继续后续操作（已保存或选择不保存），false 表示取消。
        /// </summary>
        private async Task<bool> EnsureSceneSavedAsync(string context)
        {
            if (_session == null || !_session.IsSceneDirty) return true;

            var docName = string.IsNullOrEmpty(_session.CurrentDocumentPath)
                ? "Untitled"
                : System.IO.Path.GetFileName(_session.CurrentDocumentPath);
            var result = await ShowSaveDirtyDialogAsync(docName, context);

            switch (result)
            {
                case MessageBoxResult.Save:
                    var savePath = _session.CurrentDocumentPath;
                    if (string.IsNullOrEmpty(savePath))
                    {
                        savePath = await PromptForSaveScenePathAsync();
                        if (savePath == null) return false; // 用户取消保存对话框 → 取消后续操作
                    }
                    return await _session.SaveSceneAsync(savePath);
                case MessageBoxResult.DontSave:
                    return true;
                case MessageBoxResult.Cancel:
                default:
                    return false;
            }
        }

        private enum MessageBoxResult { Save, DontSave, Cancel }

        /// <summary>简易保存确认对话框：Save / Don't Save / Cancel。</summary>
        private Task<MessageBoxResult> ShowSaveDirtyDialogAsync(string sceneName, string context)
        {
            var tcs = new TaskCompletionSource<MessageBoxResult>();
            var window = new Window
            {
                Title = "Dream Studio",
                Width = 420,
                Height = 170,
                CanResize = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26)),
            };

            var message = new TextBlock
            {
                Text = $"Save changes to \"{sceneName}\" before {context}?",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD6)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 16, 16, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };

            Button CreateButton(string text, MessageBoxResult result, bool isDefault)
            {
                var btn = new Button
                {
                    Content = text,
                    Width = 90,
                    Height = 26,
                    FontSize = 11,
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46)),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD6)),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                if (isDefault)
                {
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x8E, 0xC8));
                    btn.BorderThickness = new Thickness(1);
                }
                btn.Click += (_, _) =>
                {
                    tcs.TrySetResult(result);
                    window.Close();
                };
                return btn;
            }

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(16),
            };
            buttons.Children.Add(CreateButton("Save", MessageBoxResult.Save, true));
            buttons.Children.Add(CreateButton("Don't Save", MessageBoxResult.DontSave, false));
            buttons.Children.Add(CreateButton("Cancel", MessageBoxResult.Cancel, false));

            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
            Grid.SetRow(message, 0);
            Grid.SetRow(buttons, 1);
            grid.Children.Add(message);
            grid.Children.Add(buttons);

            window.Content = grid;
            window.Closed += (_, _) => tcs.TrySetResult(MessageBoxResult.Cancel);
            _ = window.ShowDialog(this);
            return tcs.Task;
        }

        /// <summary>Hierarchy 选中变化 → 通知 Inspector 请求字段，并同步引擎视口高亮（多选整体下发）。
        /// 运行模式下也同步：编辑器高亮在运行中隐藏，仅用于让 Inspector 跟随选中元素。</summary>
        private void OnHierarchySelectionChanged(int elementId)
        {
            var ids = _hierarchyPanel.SelectedIds;
            _inspectorPanel.SelectElements(ids, elementId);
            // Clip 曲线编辑器：Add Track 的动态组件/字段候选跟随选中元素（Unity 式）。
            _clipPanel.SetSelectedElement(elementId);
            _ = _session?.SelectSceneElementsAsync(ids, elementId);
        }

        /// <summary>Inspector 请求字段快照 → 转发到引擎（含运行模式）。</summary>
        private void OnInspectorRequest(int elementId)
        {
            _ = _session?.RequestInspectorAsync(elementId);
        }

        /// <summary>Inspector 字段编辑 → 转发到引擎应用（含运行模式）；录制中同时转发给 Clip 面板打帧。</summary>
        private void OnInspectorFieldEdited(int elementId, string component, string field, object? value)
        {
            // Clip 面板录制：在引擎应用前记录，保证轨道打的是编辑后的值。
            _clipPanel.OnInspectorFieldEdited(elementId, component, field, value);
            _ = _session?.EditInspectorFieldAsync(elementId, component, field, value);
        }

        /// <summary>Inspector 请求可添加组件列表 → 转发到引擎（含运行模式）。</summary>
        private void OnInspectorRequestComponentList()
        {
            _ = _session?.ListComponentsAsync();
        }

        /// <summary>Inspector 添加组件 → 转发到引擎（含运行模式）。</summary>
        private void OnInspectorAddComponent(int elementId, string component)
        {
            _ = _session?.AddComponentAsync(elementId, component);
        }

        /// <summary>Inspector 移除组件 → 转发到引擎（含运行模式）。</summary>
        private void OnInspectorRemoveComponent(int elementId, string component)
        {
            _ = _session?.RemoveComponentAsync(elementId, component);
        }

        /// <summary>Inspector 粘贴组件 → 转发到引擎（含运行模式）。</summary>
        private void OnInspectorPasteComponent(int elementId, string component, object fields, bool create)
        {
            _ = _session?.PasteComponentAsync(elementId, component, fields, create);
        }

        // ── 工具栏：运行/终止、暂停 ──

        private bool _toolbarBusy;

        /// <summary>运行/终止按钮：根据当前运行状态切换。运行时点击=终止，编辑时点击=运行。
        /// 编译中（IsBusy）禁用，避免与编译流程冲突。</summary>
        private async void RunButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null || _toolbarBusy || _session.IsBusy) return;
            _toolbarBusy = true;
            UpdateToolbarState();
            try
            {
                if (_session.IsRunning)
                    await _session.TerminateAsync();
                else
                    await _session.StartRunAsync();
            }
            catch (Exception ex)
            {
                // async void 未捕获异常会终止应用：作为最后兜底，记录到 Console 与日志。
                ((IEngineConsoleSink)_consolePanel).WriteLine("运行/终止失败：" + ex.Message, EngineConsoleLevel.Error);
            }
            finally
            {
                _toolbarBusy = false;
                UpdateToolbarState();
            }
        }

        /// <summary>暂停按钮：切换引擎逻辑刷新（不重启 ADL）。仅在运行模式下有效。</summary>
        private async void PauseButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null || _toolbarBusy) return;
            try
            {
                await _session.TogglePauseAsync();
            }
            catch (Exception ex)
            {
                ((IEngineConsoleSink)_consolePanel).WriteLine("暂停切换失败：" + ex.Message, EngineConsoleLevel.Error);
            }
        }

        /// <summary>按 EngineSession 状态更新工具栏按钮标签与可用性。
        /// 编译中（IsBusy）或工具栏繁忙时禁用运行/终止按钮。
        /// 编辑预制体文档时禁用运行：运行会注入场景级内容（自动相机），存回会污染 .prefab。</summary>
        private void UpdateToolbarState()
        {
            if (RunButton is null || PauseButton is null || _session == null) return;
            var running = _session.IsRunning;
            var paused = _session.IsPaused;
            var busy = _session.IsBusy || _toolbarBusy;
            var prefab = !running && _session.IsEditingPrefab;

            RunButton.Content = running ? "Terminate" : "Run";
            RunButton.IsEnabled = !busy && !prefab;
            ToolTip.SetTip(RunButton, prefab
                ? "A prefab document cannot be run. Drag it into a scene and run that scene."
                : null);

            // 暂停按钮仅在运行模式且非编译中时启用；运行中显示"Pause"，暂停中显示"Resume"。
            PauseButton.Content = paused ? "Resume" : "Pause";
            PauseButton.IsEnabled = running && !busy;
        }

        /// <summary>刷新窗口标题：类型标记（场景/预制体）+ 文档名 + 脏标记，编辑预制体时一眼可辨。</summary>
        private void UpdateWindowTitle()
        {
            var path = _session?.CurrentDocumentPath;
            if (_session == null || string.IsNullOrEmpty(path))
            {
                Title = "[Scene] Untitled* — Dream Studio";
                return;
            }
            var name = System.IO.Path.GetFileName(path);
            var kind = _session.IsEditingPrefab ? "Prefab" : "Scene";
            var dirty = _session.IsSceneDirty ? "*" : "";
            Title = $"[{kind}] {name}{dirty} — Dream Studio";
        }

        /// <summary>弹保存文件对话框获取场景存档路径。用户取消返回 null。
        /// 由 EngineSession.StartRunAsync 在 CurrentDocumentPath 为 null 时调用。
        /// 起始位置：当前项目 asconfig source-path 中首个可匹配（存在）的目录，否则回退项目根。</summary>
        private async Task<string?> PromptForSaveScenePathAsync()
        {
            // 解析首选目录 → IStorageFolder 作为弹窗起始位置。
            // 失败（路径不存在等）则不指定起始位置，由系统决定。
            IStorageFolder? startLocation = null;
            try
            {
                var projectRoot = _session?.ProjectRoot ?? EnginePaths.WorkingProjectPath;
                var startDir = ActionScriptProject.GetDefaultSaveDirectory(projectRoot);
                startLocation = await StorageProvider.TryGetFolderFromPathAsync(startDir);
            }
            catch { }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Scene",
                DefaultExtension = "space",
                SuggestedStartLocation = startLocation,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Scene File")
                    {
                        Patterns = new[] { "*.space" }
                    }
                }
            });
            return file?.Path.LocalPath;
        }

        // ── File 菜单：场景保存/打开/退出 ──

        private async void FileSaveScene_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var path = _session.CurrentDocumentPath;
            if (string.IsNullOrEmpty(path))
            {
                path = await PromptForSaveScenePathAsync();
                if (path == null) return;
            }
            await _session.SaveSceneAsync(path);
        }

        private async void FileSaveSceneAs_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var path = await PromptForSaveScenePathAsync();
            if (path == null) return;
            await _session.SaveSceneAsync(path);
        }

        /// <summary>Engine 窗口快捷键触发的保存请求（Ctrl+S/另存为）：
        /// 另存为或当前无路径时弹保存对话框，否则直接保存到当前路径。</summary>
        private async Task HandleEngineSaveRequestAsync(bool saveAs)
        {
            if (_session == null) return;
            var path = _session.CurrentDocumentPath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                path = await PromptForSaveScenePathAsync();
                if (path == null) return; // 用户取消
            }
            await _session.SaveSceneAsync(path);
        }

        /// <summary>打开任意项目目录：就地访问（不从模板克隆），成功后记入设置供下次启动直接打开。
        /// 未保存的场景会先询问保存；运行/编译中禁止切换。</summary>
        private async void FileOpenProject_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            if (_session.IsBusy || _session.IsRunning)
            {
                ((IEngineConsoleSink)_consolePanel).WriteLine(
                    "Cannot open project while the engine is running or compiling.", EngineConsoleLevel.Error);
                return;
            }
            if (!await EnsureSceneSavedAsync("opening another project")) return;

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open Project",
                AllowMultiple = false,
            });
            if (folders.Count == 0) return;

            var root = folders[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(root)) return;
            if (string.Equals(root, _session.ProjectRoot, StringComparison.OrdinalIgnoreCase)) return;

            if (await _session.OpenProjectAsync(root))
            {
                // 换项目后旧文档路径全部作废（含预制体的返回目标）。
                _returnDocumentPath = null;
                // 记住选择：下次启动直接打开该项目（就地访问，不克隆）。
                // 默认开发工作目录例外：不记录，使其始终保持「按模板重建」的开发测试流程。
                StudioSettings.Current.ProjectPath = IsDefaultWorkspace(root) ? "" : root;
                StudioSettings.Current.Save();
            }
        }

        /// <summary>给定目录是否等同默认开发工作目录（EnginePaths.WorkingProjectPath）。</summary>
        private static bool IsDefaultWorkspace(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            try
            {
                return string.Equals(
                    NormalizeDir(root),
                    NormalizeDir(EnginePaths.WorkingProjectPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string NormalizeDir(string path)
            => System.IO.Path.GetFullPath(path).TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        private async void FileOpenScene_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null || !IsEditing) return;
            if (!await EnsureSceneSavedAsync("opening another document")) return;
            var path = await PromptForOpenScenePathAsync();
            if (path == null) return;
            // 显式导航到场景：之前记录的"返回目标"已作废。
            _returnDocumentPath = null;
            await _session.LoadSceneAsync(path);
        }

        private async void FileExit_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null || await EnsureSceneSavedAsync("exiting"))
                Close();
        }

        // ── Edit 菜单：撤销 / 重做（运行模式下允许，用于回退运行中的编辑）──

        private async void EditUndo_Click(object? sender, RoutedEventArgs e)
        {
            await (_session?.SendUndoAsync() ?? Task.CompletedTask);
        }

        private async void EditRedo_Click(object? sender, RoutedEventArgs e)
        {
            await (_session?.SendRedoAsync() ?? Task.CompletedTask);
        }

        // ── Tools 菜单：设置 ──

        private async void ToolsSettings_Click(object? sender, RoutedEventArgs e)
            => await ShowSettingsAsync();

        /// <summary>打开构建窗口。构建产物与编辑器运行互不干扰，运行中也允许构建。</summary>
        private async void Build_Click(object? sender, RoutedEventArgs e)
        {
            if (_session == null) return;
            var window = new Dream.Studio.Panels.Build.BuildWindow(_session.ProjectRoot, _consolePanel);
            await window.ShowDialog(this);
            // 产物目录被排除在扫描外，但资产可能因构建生成了新的 .meta：刷新一次以免显示滞后。
            _projectPanel.Refresh();
        }

        private async void SdkStatusButton_Click(object? sender, RoutedEventArgs e)
            => await ShowSettingsAsync();

        private async Task ShowSettingsAsync()
        {
            var changed = await SettingsDialog.ShowAsync(this);
            UpdateSdkStatusButton();
            if (changed && _session != null && !_session.IsRunning)
                await _session.RecompileEditorAsync();
        }

        /// <summary>根据当前 AIR SDK 路径更新标题栏状态按钮的颜色与文本。</summary>
        private void UpdateSdkStatusButton()
        {
            if (SdkStatusButton == null || SdkStatusText == null) return;

            var path = StudioSettings.Current.AirSdkPath;
            var found = AirSdkDetector.TryDetect(path, out var version);

            if (found)
            {
                var label = string.IsNullOrEmpty(version) ? "AIR SDK" : $"AIR SDK {version}";
                SdkStatusText.Text = label;
                SdkStatusButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x7A));
                SdkStatusButton.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x7A));
                ToolTip.SetTip(SdkStatusButton, $"SDK: {path}");
            }
            else
            {
                SdkStatusText.Text = "AIR SDK NOT FOUND";
                SdkStatusButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x7C, 0x4A));
                SdkStatusButton.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x7C, 0x4A));
                ToolTip.SetTip(SdkStatusButton, "AIR SDK path is invalid. Click to open Settings.");
            }
        }

        /// <summary>弹打开文件对话框选择当前编辑文档（.space 场景 或 .prefab 预制体）。取消返回 null。</summary>
        private async Task<string?> PromptForOpenScenePathAsync()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Document",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Scene / Prefab File")
                    {
                        Patterns = new[] { "*.space", "*.prefab" }
                    }
                }
            });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }

        /// <summary>注册所有可由菜单创建的面板定义（开放封闭：新增面板只需在此注册）。</summary>
        private void RegisterPanels()
        {
            _registry.Register(new PanelDescriptor("Scene", "Scene", () => new DockPanel("Scene", _sceneSurface)));
            _registry.Register(new PanelDescriptor("Hierarchy", "Hierarchy", () => new DockPanel("Hierarchy", _hierarchyPanel)));
            _registry.Register(new PanelDescriptor("Inspector", "Inspector", () => new DockPanel("Inspector", _inspectorPanel)));
            _registry.Register(new PanelDescriptor("Console", "Console", () => new DockPanel("Console", _consolePanel)));
            _registry.Register(new PanelDescriptor("Project", "Project", () => new DockPanel("Project", _projectPanel)));
            _registry.Register(new PanelDescriptor("Clip", "Clip", () => new DockPanel("Clip", _clipPanel)));
            _registry.Register(new PanelDescriptor("Animator", "Animator", () => new DockPanel("Animator", _animatorPanel)));
            _registry.Register(new PanelDescriptor("Audio", "Audio", () => new DockPanel("Audio", _audioPanel)));
        }

#if WINDOWS
        // Snap Layout 区域被返回为 HTMAXBUTTON 后，系统接管该 NC 区，
        // Avalonia 不再路由鼠标消息到按钮。这里手动驱动伪类与点击。
        private bool _maxHovering;
        private bool _maxPressing;

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const uint WM_NCHITTEST = 0x0084;
            const uint WM_NCMOUSEMOVE = 0x00A0;
            const uint WM_NCLBUTTONDOWN = 0x00A1;
            const uint WM_NCLBUTTONUP = 0x00A2;
            const uint WM_NCMOUSELEAVE = 0x02A2;
            const int HTMAXBUTTON = 9;

            switch (msg)
            {
                case WM_NCHITTEST:
                    if (IsPointInMaxButton(lParam))
                    {
                        // handled=true 让系统使用 HTMAXBUTTON，从而弹出 Snap Layout 浮层。
                        handled = true;
                        return new IntPtr(HTMAXBUTTON);
                    }
                    break;

                case WM_NCMOUSEMOVE:
                    // wParam 为当前命中测试结果。在按钮上时驱动 :pointerover。
                    if (wParam == new IntPtr(HTMAXBUTTON))
                    {
                        SetMaxHover(true);
                    }
                    else
                    {
                        SetMaxHover(false);
                        SetMaxPress(false);
                    }
                    break;

                case WM_NCLBUTTONDOWN:
                    if (wParam == new IntPtr(HTMAXBUTTON))
                    {
                        // 拦截系统的默认最大化行为，自行处理按下/弹起。
                        SetMaxPress(true);
                        handled = true;
                    }
                    break;

                case WM_NCLBUTTONUP:
                    if (_maxPressing && wParam == new IntPtr(HTMAXBUTTON))
                    {
                        ToggleMaximized();
                    }
                    // 点击完成后恢复常态：清除 pressed 与 hover。
                    SetMaxPress(false);
                    SetMaxHover(false);
                    break;

                case WM_NCMOUSELEAVE:
                    // 鼠标彻底离开 NC 区，确保按钮恢复常态。
                    SetMaxHover(false);
                    SetMaxPress(false);
                    break;
            }

            return IntPtr.Zero;
        }

        private bool IsPointInMaxButton(IntPtr lParam)
        {
            if (MaxButton is null)
            {
                return false;
            }

            var packed = lParam.ToInt32();
            var screenX = (short)(packed & 0xFFFF);
            var screenY = (short)((packed >> 16) & 0xFFFF);
            var localPoint = this.PointToClient(new PixelPoint(screenX, screenY));

            if (MaxButton.TranslatePoint(new Point(0, 0), this) is { } origin)
            {
                var buttonRect = new Rect(origin, MaxButton.Bounds.Size);
                return buttonRect.Contains(localPoint);
            }

            return false;
        }

        private void SetMaxHover(bool value)
        {
            if (_maxHovering == value || MaxButton is null)
            {
                return;
            }
            _maxHovering = value;
            SetPseudoClass(MaxButton, ":pointerover", value);
        }

        private void SetMaxPress(bool value)
        {
            if (_maxPressing == value || MaxButton is null)
            {
                return;
            }
            _maxPressing = value;
            SetPseudoClass(MaxButton, ":pressed", value);
        }

        // PseudoClasses 为 protected internal，跨程序集需通过反射访问。
        private static void SetPseudoClass(Control control, string name, bool value)
        {
            var prop = typeof(StyledElement).GetProperty(
                "PseudoClasses",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (prop?.GetValue(control) is IPseudoClasses pseudo)
            {
                pseudo.Set(name, value);
            }
        }
#endif

        private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateMaxIcon();
                // Studio 最小化时隐藏引擎窗口，恢复时显示，避免独立窗口悬浮在最小化的 Studio 之上。
                _session?.SetEngineVisible(WindowState != WindowState.Minimized);
            }
        }

        // ExtendClientAreaToDecorationsHint 下，系统标题栏区域已并入客户区，
        // 拖拽与双击最大化需由自定义标题栏自行接管。
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
        {
            ToggleMaximized();
        }

        private void MinButton_Click(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaxButton_Click(object? sender, RoutedEventArgs e)
        {
            ToggleMaximized();
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleMaximized()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        // 窗口状态变化时切换最大化/还原按钮的图标。
        private void UpdateMaxIcon()
        {
            if (MaxIcon is null)
            {
                return;
            }

            // 最大化时显示“还原”图标：上方矩形被下方矩形遮挡，其左侧和下侧边
            // 在与下方矩形相交的部分不绘制，只保留可见的顶边、右边及左上、右下两段。
            MaxIcon.Data = WindowState == WindowState.Maximized
                ? StreamGeometry.Parse("M2,0 H10 V8 H8 M2,2 V0 M0,2 H8 V10 H0 Z")
                : StreamGeometry.Parse("M0,0 H10 V10 H0 Z");
        }

        private void BuildInitialLayout(DockManager manager)
        {
            // 通过注册表创建面板实例，使菜单的勾选状态与初始布局一致。
            var scene = _registry.GetOrCreate("Scene");
            var hierarchy = _registry.GetOrCreate("Hierarchy");
            var inspector = _registry.GetOrCreate("Inspector");
            var console = _registry.GetOrCreate("Console");
            var project = _registry.GetOrCreate("Project");

            // 显式构建初始布局，避免 Dock 顺序隐式决定占比。
            // 目标结构：
            //   HSplit((Hierarchy 0.5 | Project 0.5) 0.2 | 右侧 0.8)
            //        右侧 = HSplit(Scene 0.75 | Inspector 0.25)
            //               Scene 一侧 = VSplit(Scene 0.7 | Console 0.3)
            //
            // 注意 Dock 的 ratio 参数表示“新面板所在一侧”的占比。

            manager.Dock(scene, null, DockSide.Center);
            var sceneGroup = manager.Root!.Groups().First();

            // Inspector 在 Scene 右侧，占 25% 宽 → splitRatio = 1 - 0.25 = 0.75（Scene 在左）。
            manager.Dock(inspector, sceneGroup, DockSide.Right, 0.25);

            // 此时 Root = HSplit(Scene | Inspector)。取 Inspector 的 Parent split 的另一侧 = Scene group。
            var inspectorGroup = manager.Root!.Groups().First(g => g.Panels.Contains(inspector));
            var topSplit = inspectorGroup.Parent!;
            var sceneSideGroup = ReferenceEquals(topSplit.First, inspectorGroup)
                ? (LayoutGroup)topSplit.Second
                : (LayoutGroup)topSplit.First;

            // Console 在 Scene 下方，占 30% 高 → splitRatio = 1 - 0.3 = 0.7（Scene 在上）。
            manager.Dock(console, sceneSideGroup, DockSide.Bottom, 0.3);

            // 最外层：Hierarchy 在左占 20%，wrap 当前整个 Root（右侧整体）。
            manager.Dock(hierarchy, null, DockSide.Left, 0.2);
            var hierarchyGroup = manager.Root!.Groups().First(g => g.Panels.Contains(hierarchy));
            manager.Dock(project, hierarchyGroup, DockSide.Bottom, 0.5);

            // 菜单勾选状态在 Bind 时直接查询 workspace 实时面板，无需手动同步。
        }

        private static Control MakeContent(string label)
        {
            return new Border
            {
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                }
            };
        }
    }
}

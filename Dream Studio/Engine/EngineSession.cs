using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dream.Studio.Engine.Comm;
using Dream.Studio.Engine.Comm.Handlers;
using Dream.Studio.Panels.Project;
using Dream.Studio.Settings;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 引擎子项目会话编排器：启动时确保模板 → debug 编译 → 启动通信 → 运行 ADL → 嵌入 Scene 面板。
    /// 依赖注入各环节抽象（DIP），自身只负责流程编排（SRP）。
    /// </summary>
    internal sealed class EngineSession
    {
        private readonly IEmbedSurface _surface;
        private ActionScriptProject _project;
        private readonly ITemplateCloner _cloner;
        // 是否在启动时从模板克隆项目：仅默认开发工作目录（.workspace/Project）为 true；
        // 打开任意项目目录时就地访问，不克隆、不覆盖。
        private bool _cloneFromTemplate;
        private readonly IComponentIndexer _indexer;
        private IAsCompiler _compiler;
        private IAirAppRunner _runner;
        private readonly IWindowEmbedder? _embedder;
        private readonly IEngineConsoleSink? _console;
        private readonly MessageDispatcher _dispatcher;

        private TcpMessageServer? _server;
        private IMessageChannel? _channel;
        private ProjectFileWatcher? _watcher;
        // 项目资源（图片 / 精灵表 / 图集定义）监控：变更后重新同步图集缓存并推送，实现热切换。
        private ProjectFileWatcher? _assetWatcher;
        // app 描述符（<name>-app.xml）监控：窗口尺寸/标题等改动要重编译并重启 ADL 才生效。
        private ProjectFileWatcher? _descriptorWatcher;
        private readonly ProjectPanel? _projectPanel;

        // 场景保存/加载的等待句柄：发送 scene.save/scene.load 后由 SceneResultHandler 回填。
        private TaskCompletionSource<bool>? _scenePending;

        // prefab 子树导出的等待句柄：发送 prefab.export 后由 PrefabExportResultHandler 回填。
        private TaskCompletionSource<string?>? _prefabPending;

        // 热重载守卫：true 表示正在重编译+重启，避免文件改动事件重入。
        private bool _reloading;

        // 运行状态：IsRunning=true 表示引擎处于运行模式（ADL 以 --mode=run 启动）；
        // IsPaused=true 表示运行模式下逻辑刷新被暂停（world.running=false，未重启 ADL）。
        public bool IsRunning { get; private set; }
        public bool IsPaused { get; private set; }

        // 编译/重载中：true 时禁用运行/终止按钮，避免与编译流程冲突。
        public bool IsBusy { get; private set; }

        /// <summary>运行/暂停状态变化时触发，MainWindow 据此更新工具栏按钮标签与可用性。</summary>
        public event Action? StateChanged;

        /// <summary>引擎启动/重载阶段状态变化。参数为状态文本；null 表示引擎已就绪（隐藏占位层）。</summary>
        public event Action<string?>? EngineStatusChanged;

        /// <summary>模板克隆完成后触发：此时工作目录已就绪，ProjectPanel 可安全加载文件树。
        /// 首次 StartAsync 在 OnOpened 触发，晚于 ProjectPanel 构造与 attach，
        /// 若不等待克隆即加载会看到上一次遗留的结构。</summary>
        public event Action? ProjectReady;

        /// <summary>场景保存成功后触发：可能产生新的 .space 文件，ProjectPanel 需重建文件树。</summary>
        public event Action? SceneSaved;

        /// <summary>引擎窗口请求保存场景（Ctrl+S/Ctrl+Shift+S）。参数 true=另存为。
        /// 由 MainWindow 订阅执行：已有路径直接保存，否则弹保存对话框。</summary>
        public event Action<bool>? SaveRequested;

        /// <summary>引擎左上角返回按钮被点击。由 MainWindow 订阅执行：切回进入预制体之前的场景。</summary>
        public event Action? DocumentBackRequested;

        /// <summary>当前编辑文档路径：.space 场景或 .prefab 预制体（两者格式同构）。null 表示新场景尚未保存过。
        /// 首次保存（运行前）由 PromptForSavePath 回调获取路径，之后复用此路径。</summary>
        public string? CurrentDocumentPath { get; private set; }

        /// <summary>当前编辑的是否为预制体文档（按扩展名判定）。
        /// 预制体是"场景子树资产"：没有场景级内容（自动相机），也不支持运行。</summary>
        public bool IsEditingPrefab =>
            CurrentDocumentPath != null &&
            string.Equals(Path.GetExtension(CurrentDocumentPath), ".prefab", StringComparison.OrdinalIgnoreCase);

        /// <summary>当前文档是否包含未保存的修改。由各类修改命令置脏，保存/加载成功后清零。</summary>
        public bool IsSceneDirty { get; private set; }

        /// <summary>文档状态变化（打开 / 保存 / 脏标记翻转）时触发，MainWindow 据此刷新窗口标题。</summary>
        public event Action? DocumentChanged;

        /// <summary>由 MainWindow 注入：当 CurrentDocumentPath 为 null（新场景）时弹保存对话框获取路径。
        /// 返回用户选择的绝对路径；用户取消返回 null。引擎开发阶段子项目无价值，
        /// 路径可任意选择（包括工作项目内，克隆覆盖问题不在此考虑）。</summary>
        public Func<Task<string?>>? PromptForSavePath { get; set; }

        public EngineSession(
            IEmbedSurface surface,
            ActionScriptProject project,
            ITemplateCloner cloner,
            IComponentIndexer indexer,
            IAsCompiler compiler,
            IAirAppRunner runner,
            IWindowEmbedder? embedder,
            IEngineConsoleSink? console = null,
            Panels.Hierarchy.HierarchyPanel? hierarchy = null,
            Panels.Inspector.InspectorPanel? inspector = null,
            ProjectPanel? projectPanel = null,
            Action<Panels.Inspector.InspectorSnapshotData>? inspectorSnapshotForward = null,
            bool cloneFromTemplate = true)
        {
            _surface = surface;
            _project = project;
            _cloner = cloner;
            _cloneFromTemplate = cloneFromTemplate;
            _indexer = indexer;
            _compiler = compiler;
            _runner = runner;
            _embedder = embedder;
            _console = console;
            _projectPanel = projectPanel;
            // 注入 console 到日志处理器（DIP：EngineLogHandler 依赖 sink 抽象而非具体控件）。
            _dispatcher = new MessageDispatcher()
                .Add(new PongHandler())
                .Add(new EngineLogHandler(console));

            if (hierarchy != null)
            {
                _dispatcher.Add(new HierarchySnapshotHandler(hierarchy));
                _dispatcher.Add(new ScenePickedHandler(hierarchy));
            }

            if (inspector != null)
            {
                _dispatcher.Add(new InspectorSnapshotHandler(inspector, inspectorSnapshotForward));
                _dispatcher.Add(new InspectorComponentListHandler(inspector));
            }

            // 场景存档结果回调：回填 SaveSceneAsync/LoadSceneAsync 的等待句柄。
            _dispatcher.Add(new SceneResultHandler(OnSceneResult));

            // prefab 子树导出结果：回填 ExportPrefabAsync 的等待句柄。
            _dispatcher.Add(new PrefabExportResultHandler(OnPrefabExportResult));

            // 引擎本地编辑（Gizmo 拖拽等）通知：标记场景为未保存，并立即刷新 Inspector
            //（定时刷新之外的兜底，确保拖拽结束后的最终变换值显示）。
            _dispatcher.Add(new SceneModifiedHandler(() =>
            {
                MarkSceneDirty();
                if (inspector != null && inspector.CurrentElementId >= 0)
                    _ = RequestInspectorAsync(inspector.CurrentElementId);
            }));

            // 引擎窗口快捷键请求（Ctrl+S/Ctrl+Shift+S）：保存/另存为依赖 Studio 文件对话框，
            // 由 MainWindow 订阅 SaveRequested 执行。与引擎侧 DreamEngine.initEditorShortcuts 对应。
            _dispatcher.Add(new EngineShortcutHandler(action =>
                SaveRequested?.Invoke(action == "saveAs")));

            // 引擎左上角返回按钮（document.back）：文档栈在 MainWindow，这里只转发。
            _dispatcher.Add(new DocumentBackHandler(() => DocumentBackRequested?.Invoke()));
        }

        /// <summary>
        /// 根据当前 StudioSettings 重新创建编译器与运行器，避免使用启动时缓存的旧 SDK 路径。
        /// 旧运行器会被释放，以终止其可能持有的 ADL 进程。
        /// </summary>
        private void RecreateSdkTools()
        {
            _compiler = new MxmlcCompiler(StudioSettings.Current.AirSdkPath, _console);
            var oldRunner = _runner;
            _runner = new AdlRunner(StudioSettings.Current.AirSdkPath, _console);
            oldRunner?.Dispose();
        }

        /// <summary>更新引擎启动/重载阶段状态（Scene 面板占位层）。</summary>
        private void SetStatus(string? text) => EngineStatusChanged?.Invoke(text);

        /// <summary>当前项目根目录（绝对路径）。</summary>
        public string ProjectRoot => _project.RootDir;

        public async Task StartAsync()
        {
            try
            {
                RecreateSdkTools();

                // 1. 启动通信服务器，获得端口供 Engine 回连（先于 ADL 启动）。
                _server = new TcpMessageServer();
                _server.ChannelConnected += OnChannelConnected;
                _server.Start();
                Log($"[comm] listen port {_server.Port}");

                // 2. 编译 → 运行 → 嵌入（默认开发工作目录会先按模板克隆）。
                await CompileAndLaunchAsync(_cloneFromTemplate);
            }
            catch (Exception ex)
            {
                Log("[studio] Engine session start failed: " + ex);
            }
        }

        /// <summary>
        /// 编译前把项目描述符对齐到当前 SDK 的命名空间。
        ///
        /// mxmlc 按主类名自动读取同名描述符、ADL 也按它启动，而描述符里的 xmlns 末段就是要求
        /// 的 AIR 版本：它比当前 SDK 新时，ADL 会以 <c>Unknown namespace</c> 直接退出（预览起不来），
        /// mxmlc 也可能读不下去。规则与构建面板一致——取当前 SDK 自带的最新命名空间；
        /// 已经一致时 <see cref="AppDescriptor.Save"/> 不会落盘，不动文件时间戳。
        /// </summary>
        private void AlignAppDescriptorWithSdk()
        {
            // 顺带加载 asconfig：GetAppXmlPath 依赖它解析出的描述符相对路径，未加载时是 null
            // （首次启动刚克隆完项目就属于这种状态）。结构不完整则不动。
            if (!_project.IsValid()) return;

            var sdk = StudioSettings.Current.AirSdkPath;
            var descriptor = AppDescriptor.Load(_project.GetAppXmlPath());
            if (descriptor == null) return;

            // 描述符声明的版本当前 SDK 认识 → 不动它：老项目可能有意要求更低的 AIR 版本。
            var current = descriptor.NamespaceVersion;
            if (current.Length > 0 && IconSizeCatalog.SchemaPath(sdk, current) != null) return;

            var version = DescriptorSchema.LatestNamespaceVersion(sdk);
            if (string.IsNullOrEmpty(version)) return;

            descriptor.SetNamespaceVersion(version!);
            if (descriptor.Save())
                Log($"[studio] app descriptor namespace {current} → {version} (not known to the current AIR SDK)");
        }

        /// <summary>
        /// 编译当前项目 → 以编辑器模式启动 ADL → 嵌入 Scene 面板 → 启动 .as 文件监控。
        /// cloneFromTemplate=true 时先按模板重建项目目录（仅默认开发工作目录用）。
        /// 编译/启动失败时输出到 Console 并返回 false（不抛异常，避免上层 async void 崩溃）。
        /// </summary>
        private async Task<bool> CompileAndLaunchAsync(bool cloneFromTemplate)
        {
            IsBusy = true;
            StateChanged?.Invoke();
            try
            {
                SetStatus("Compiling engine…");
                var compiled = await Task.Run(() =>
                {
                    // 仅默认开发工作目录从模板克隆（每次清空重建，保证与模板同步）；
                    // 打开的项目就地编译，不克隆、不覆盖用户文件。
                    if (cloneFromTemplate) _cloner.Ensure(_project);
                    // 克隆/就地项目都定下来之后再对齐：mxmlc 与 ADL 都按描述符里的 AIR 版本行事。
                    AlignAppDescriptorWithSdk();
                    _indexer.Generate(_project);
                    return _compiler.Compile(_project);
                });
                // 项目目录已就绪（克隆完成或就地项目）：通知 ProjectPanel 加载文件树。
                ProjectReady?.Invoke();
                if (!compiled)
                {
                    EmitConsole("[studio] Engine compilation failed, startup aborted", EngineConsoleLevel.Error);
                    Log("[studio] Compilation failed. See Console panel for details");
                    SetStatus(null);
                    return false;
                }

                // 运行 ADL，传入通信端口与运行模式（编辑器模式）。
                SetStatus("Starting engine…");
                var extraArgs = new List<string> { $"--comm-port={_server!.Port}" };
                AppendDocumentArgs(extraArgs);
                extraArgs.Add("--mode=edit");
                var hwnd = await Task.Run(() => _runner.Run(_project, extraArgs));
                if (hwnd == IntPtr.Zero)
                {
                    Log("[studio] ADL main window is not ready, skip embedding");
                    SetStatus(null);
                    return false;
                }

                // 嵌入需在 UI 线程读取布局几何。
                if (_embedder != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => _embedder.Embed(hwnd, _surface));
                }

                // 启动 .as 文件监控：仅在编辑器模式下生效，运行模式不打断用户。
                StartWatcher();
                SetStatus(null);
                return true;
            }
            finally
            {
                // 流程结束（成功嵌入或失败）：解锁工具栏，避免启动窗口期内误点运行按钮。
                IsBusy = false;
                StateChanged?.Invoke();
            }
        }

        /// <summary>把当前编辑文档追加到 ADL 启动参数：--scene=&lt;路径&gt;。
        /// 引擎按扩展名判定文档类型（.prefab → 预制体：不注入自动相机，见 DreamEngine.onDocumentLoaded），
        /// 与 Studio 的 IsEditingPrefab 是同一规则。</summary>
        private void AppendDocumentArgs(List<string> extraArgs)
        {
            if (CurrentDocumentPath == null) return;
            extraArgs.Add($"--scene={CurrentDocumentPath}");
        }

        /// <summary>
        /// 打开任意项目目录（就地访问，不从模板克隆）：校验项目结构 → 停止当前引擎与监控 →
        /// 切换项目根 → 重新编译并以编辑器模式启动。成功返回 true。
        /// 结构非法（缺 asconfig.json / application 描述符 / 主类源文件）时输出错误并返回 false。
        /// </summary>
        public async Task<bool> OpenProjectAsync(string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir)) return false;
            if (_server == null)
            {
                Log("[studio] Cannot open project: the message server is not started");
                return false;
            }
            if (IsBusy || _reloading)
            {
                EmitConsole("[studio] Cannot open project while the engine is busy", EngineConsoleLevel.Error);
                return false;
            }

            var project = new ActionScriptProject(rootDir);
            if (!project.IsValid())
            {
                EmitConsole(
                    $"[studio] Not a valid ActionScript project (asconfig.json / application descriptor / main class required): {rootDir}",
                    EngineConsoleLevel.Error);
                return false;
            }

            try
            {
                // 1. 停止文件监控与窗口嵌入，终止旧 ADL（通道随之断开，新 ADL 会重新回连）。
                StopWatchers();
                _embedder?.Dispose();
                await Task.Run(() => _runner.Stop());

                // 2. 切换项目根（就地访问）：重置场景状态与 Project 面板。
                _project = project;
                _cloneFromTemplate = false;
                CurrentDocumentPath = null;
                IsSceneDirty = false;
                IsRunning = false;
                IsPaused = false;
                StateChanged?.Invoke();
                DocumentChanged?.Invoke();
                _projectPanel?.SetProjectRoot(rootDir);

                // 3. 重新编译并以编辑器模式启动；ProjectReady 事件会刷新 Project 面板文件树。
                EmitConsole($"[studio] Opening project: {rootDir}", EngineConsoleLevel.Info);
                return await CompileAndLaunchAsync(false);
            }
            catch (Exception ex)
            {
                EmitConsole("[studio] Failed to open project: " + ex.Message, EngineConsoleLevel.Error);
                Log("[studio] Open project failed: " + ex);
                return false;
            }
        }

        /// <summary>触发图集缓存重新同步的资源文件扩展名（改图 / 改切分 / 改图集定义都要重新合图）。</summary>
        private static readonly string[] AssetWatchExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", SpriteSheetFile.Extension, SpriteAtlasDefinition.Extension };

        /// <summary>创建并启动文件监控：监听所有 source-path 目录。
        /// 变更事件在后台线程触发，经防抖后分别调用 OnAsFileChanged / OnProjectAssetChanged。</summary>
        private void StartWatcher()
        {
            StopWatchers();
            var dirs = new List<string>();
            foreach (var sp in _project.GetSourcePaths())
            {
                var abs = Path.GetFullPath(Path.Combine(_project.RootDir, sp));
                if (Directory.Exists(abs)) dirs.Add(abs);
            }

            _watcher = new ProjectFileWatcher(new[] { ".as" });
            _watcher.Changed += OnAsFileChanged;
            _watcher.Start(dirs);

            _assetWatcher = new ProjectFileWatcher(AssetWatchExtensions);
            _assetWatcher.Changed += OnProjectAssetChanged;
            _assetWatcher.Start(dirs);

            // app 描述符单独监听它所在的目录（可能不在 source-path 里，放项目根目录也合法），
            // 只认这一个文件名——描述符里的窗口尺寸/标题/图标等都写在启动时读取，改完必须重编译重启。
            var descriptorPath = _project.GetAppXmlPath();
            var descriptorDir = Path.GetDirectoryName(descriptorPath);
            if (!string.IsNullOrEmpty(descriptorDir) && Directory.Exists(descriptorDir))
            {
                _descriptorWatcher = new ProjectFileWatcher(Array.Empty<string>(),
                                                            new[] { Path.GetFileName(descriptorPath) });
                _descriptorWatcher.Changed += OnDescriptorChanged;
                _descriptorWatcher.Start(new[] { descriptorDir });
            }

            Log("[watcher] ActionScript (`.as`) files monitoring started");
            Log("[watcher] Project assets (textures / sprite sheets / sprite atlases) monitoring started");
            if (_descriptorWatcher != null) Log("[watcher] App descriptor monitoring started");
        }

        /// <summary>停止全部文件监控（切换项目、停止会话时调用）。</summary>
        private void StopWatchers()
        {
            _watcher?.Dispose();
            _watcher = null;
            _assetWatcher?.Dispose();
            _assetWatcher = null;
            _descriptorWatcher?.Dispose();
            _descriptorWatcher = null;
        }

        /// <summary>项目资源文件变更回调（防抖后，后台线程）：重扫资源索引（可能新增/删除文件）
        /// → 按内容签名重新合图 → 推送索引与图集映射，使编辑期无需重启即可看到图集变化。
        /// 运行模式下同样生效：合图不会重启 ADL，正好用来观察合批结果。</summary>
        private void OnProjectAssetChanged()
        {
            // 事件在后台线程触发；Project 面板与推送都在 UI 线程上操作。
            Dispatcher.UIThread.Post(() =>
            {
                if (IsBusy || _projectPanel == null) return;
                _projectPanel.Refresh();
                _ = PushResourceStateAsync();
            });
        }

        /// <summary>.as 文件变更回调（防抖后，后台线程）。</summary>
        private async void OnAsFileChanged() => await RecompileForChangeAsync("ActionScript file(s)");

        /// <summary>app 描述符变更回调（防抖后，后台线程）。</summary>
        private async void OnDescriptorChanged() => await RecompileForChangeAsync("the app descriptor");

        /// <summary>
        /// 项目源文件变更后的重编译（防抖后，后台线程调用）。
        /// 仅在编辑器模式且非重载中触发：重编译 → 成功则重启 ADL 编辑器模式。
        /// 运行模式下忽略，避免打断用户游戏运行。</summary>
        private async Task RecompileForChangeAsync(string what)
        {
            // 运行模式下忽略，避免打断用户游戏运行；IsBusy 期间（启动/重载中）也忽略，
            // 防止文件变更与启动流程并发操作同一 ADL 进程/嵌入器。
            if (_reloading || IsRunning || IsBusy) return;
            _reloading = true;
            IsBusy = true;
            StateChanged?.Invoke();
            try
            {
                EmitConsole($"[studio] Detected {what} change, recompiling…", EngineConsoleLevel.Info);
                // 重编译后要重启 ADL、从磁盘重新加载文档：先把未保存的编辑静默写回，
                // 否则它们会随旧进程一起消失（场景与预制体都是文档，一并处理）。
                await SaveDocumentBeforeCompileAsync();
                var compiled = await Task.Run(() =>
                {
                    AlignAppDescriptorWithSdk();
                    _indexer.Generate(_project);
                    return _compiler.Compile(_project);
                });
                if (!compiled)
                {
                    EmitConsole("[studio] Failed to recompile, now keep the current engine state", EngineConsoleLevel.Error);
                    return;
                }
                EmitConsole("[studio] Compilation succeeded, restarting ADL…", EngineConsoleLevel.Info);
                await RestartAsync("edit");
            }
            catch (Exception ex)
            {
                EmitConsole("Hot reload failure: " + ex.Message, EngineConsoleLevel.Error);
                Log("Hot reload failure: " + ex);
            }
            finally
            {
                _reloading = false;
                IsBusy = false;
                StateChanged?.Invoke();
            }
        }

        private async void OnChannelConnected(object? sender, TcpMessageChannel channel)
        {
            _channel = channel;
            channel.MessageReceived += OnMessageReceived;
            channel.Closed += OnChannelClosed;
            Log("[comm] Engine connected");
            // 引擎已连接（ADL 窗口已嵌入）：隐藏启动占位层。
            SetStatus(null);
            // 验证双向：发送 ping，期望 pong（由 PongHandler 落盘）。
            try { await channel.SendAsync(new Message { Type = MessageTypes.Ping }); }
            catch (Exception ex) { Log("[comm] ping failed in send：" + ex); }
            // 推送资源索引（含精灵子资源）与图集覆盖映射，供 AS3 ResourceManager 解析引用。
            _ = PushResourceStateAsync();
        }

        /// <summary>
        /// 同步图集缓存后依次推送资源索引与图集映射（索引先到，映射后到）。
        /// 资源集合变化、以及引擎连上时都应调用：合图按内容签名判定，未变化时直接复用缓存产物。
        /// </summary>
        public async Task PushResourceStateAsync()
        {
            if (_projectPanel != null)
            {
                foreach (var error in _projectPanel.SyncAtlases()) Log(error);
            }
            await SendResourceIndexAsync();
            await SendSpriteAtlasMapAsync();
            // prefab 源内容：引擎据此把源改动同步到场景实例（场景加载后与每次连接都会重放）。
            await PushPrefabSourcesAsync();
        }

        private async void OnMessageReceived(object? sender, Message msg)
        {
            try { await _dispatcher.DispatchAsync(msg, _channel!); }
            catch (Exception ex) { Log("[comm] processing failed：" + ex); }
        }

        private void OnChannelClosed(object? sender, EventArgs e) => Log("[comm] Engine closed");

        public void Stop()
        {
            StopWatchers();
            try { _channel?.DisposeAsync().AsTask().Wait(); } catch { }
            _server?.Dispose();
            _embedder?.Dispose();
            _runner.Dispose();
        }

        /// <summary>同步子窗口可见性（宿主最小化/恢复时调用）。</summary>
        public void SetEngineVisible(bool visible) => _embedder?.SetVisible(visible);

        /// <summary>挂起/恢复文件监控。重命名期间挂起避免打断用户输入（资源刷新会重建行控件），
        /// 结束后恢复并补触发一次（捕获重命名产生的文件改动）。</summary>
        public void SetFileWatcherPaused(bool paused)
        {
            Apply(_watcher);
            Apply(_assetWatcher);
            Apply(_descriptorWatcher);

            void Apply(ProjectFileWatcher? watcher)
            {
                if (watcher == null) return;
                if (paused) watcher.Pause();
                else watcher.Resume();
            }
        }

        /// <summary>
        /// 发送 Hierarchy 操作命令到引擎。action 为 "create"/"destroy"/"rename"，
        /// payload 为命令数据对象（会被 JSON 序列化）。
        /// </summary>
        public async Task SendCommandAsync(string action, object? payload)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.HierarchyCommand, new { action, data = payload });
            try
            {
                await _channel.SendAsync(msg);
                MarkSceneDirty();
            }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>标记当前文档已修改。仅在干净 → 脏这一跳通知：拖拽 Gizmo 会持续置脏，逐次通知会白刷标题。</summary>
        public void MarkSceneDirty()
        {
            if (IsSceneDirty) return;
            IsSceneDirty = true;
            DocumentChanged?.Invoke();
        }

        /// <summary>将 ProjectPanel 的资源索引（文件 + 精灵子资源）推送到引擎，供 ResourceManager 解析 GUID。</summary>
        public async Task SendResourceIndexAsync()
        {
            if (_channel == null || !_channel.IsConnected || _projectPanel == null) return;
            var entries = _projectPanel.GetResourceEntries()
                .Select(e => new { guid = e.Guid, path = e.Path, sprite = e.Sprite })
                .ToArray();
            if (entries.Length == 0) return;
            var msg = Message.Create(MessageTypes.ResourceIndex, new { entries });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>
        /// 把项目内所有图集的「精灵 → 图集纹理 + 矩形」映射推送到引擎。
        /// 图集是可选覆盖层：不推送（或推送空表）时引擎回落精灵自身的源纹理，
        /// 因此删除图集不会破坏任何场景引用。
        /// </summary>
        public async Task SendSpriteAtlasMapAsync()
        {
            if (_channel == null || !_channel.IsConnected || _projectPanel == null) return;
            var entries = _projectPanel.GetAtlasMap()
                .Select(e => new
                {
                    spriteGuid = e.SpriteGuid,
                    textureGuid = e.TextureGuid,
                    x = e.X,
                    y = e.Y,
                    w = e.W,
                    h = e.H,
                })
                .ToArray();
            var msg = Message.Create(MessageTypes.SpriteAtlasMap, new { entries });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>请求指定元素的 Inspector 字段快照。</summary>
        public async Task RequestInspectorAsync(int elementId)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.InspectorRequest, new { elementId });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>设置引擎编辑器选中元素集（驱动 Scene 视口高亮与多选框）。ids 有序，末尾为主选中。</summary>
        public async Task SelectSceneElementsAsync(IReadOnlyList<int> ids, int primaryId)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.SceneSelect, new { elementIds = ids.ToArray(), primaryId });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>编辑字段值并下发到引擎。</summary>
        public async Task EditInspectorFieldAsync(int elementId, string component, string field, object? value)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.InspectorEdit, new { elementId, component, field, value });
            try
            {
                await _channel.SendAsync(msg);
                MarkSceneDirty();
            }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>设置元素/组件启用。component 为 null/空串表示元素级。</summary>
        public async Task SetEnabledAsync(int elementId, string? component, bool enabled)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.InspectorSetEnabled, new { elementId, component, enabled });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>Clip 动画预览：把采样值下发到引擎驱动场景元素。不记录撤销、不发快照，可高频调用。
        /// target 为目标子元素名（空串 = elementId 元素自身），引擎按 Transform 后代递归匹配。</summary>
        public async Task ApplyClipPreviewAsync(int elementId, string target, string component, string field, object? value)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.ClipPreviewApply, new { elementId, target, component, field, value });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>设置主音量/组音量（由全局音频面板调用）。master 或 group+volume 至少一项。</summary>
        public async Task SetAudioVolumeAsync(double? master, string? group, double? volume)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.AudioSetVolume, new { master, group, volume });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>请求引擎返回可添加组件列表（供 Add Component 弹出选择）。</summary>
        public async Task ListComponentsAsync()
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.InspectorListComponents, new { });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>添加组件到指定元素，引擎创建后返回新快照。</summary>
        public async Task AddComponentAsync(int elementId, string component)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.InspectorAddComponent, new { elementId, component });
            try
            {
                await _channel.SendAsync(msg);
                MarkSceneDirty();
            }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>从指定元素移除组件，引擎销毁后返回新快照。</summary>
        public async Task RemoveComponentAsync(int elementId, string component)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.InspectorRemoveComponent, new { elementId, component });
            try
            {
                await _channel.SendAsync(msg);
                MarkSceneDirty();
            }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>设置引擎逻辑刷新开关（暂停/继续），不重启 ADL。</summary>
        public async Task SetRunningAsync(bool running)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.EngineSetRunning, new { running });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        // ── 场景存档 ──

        /// <summary>SceneResultHandler 回调：回填当前保存/加载等待句柄。</summary>
        private void OnSceneResult(bool success, string action)
        {
            _scenePending?.TrySetResult(success);
        }

        /// <summary>保存当前文档（场景或预制体）到指定路径，等待引擎返回结果（5s 超时兜底）。
        /// 成功则清零脏标记并切换当前文档路径。</summary>
        public async Task<bool> SaveSceneAsync(string path)
        {
            if (_channel == null || !_channel.IsConnected) return false;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _scenePending = tcs;
            var msg = Message.Create(MessageTypes.SceneSave, new { path });
            try { await _channel.SendAsync(msg); }
            catch { _scenePending = null; return false; }
            using var cts = new CancellationTokenSource(5000);
            cts.Token.Register(() => tcs.TrySetResult(false));
            var success = await tcs.Task;
            if (success)
            {
                CurrentDocumentPath = path;
                IsSceneDirty = false;
                DocumentChanged?.Invoke();
                // 保存可能产生新的场景文件，通知 ProjectPanel 重建文件树。
                SceneSaved?.Invoke();
            }
            return success;
        }

        /// <summary>从指定路径加载文档（.space 场景或 .prefab 预制体，格式同构）到视口，
        /// 等待引擎返回结果（5s 超时兜底）。成功则切换当前文档路径并清零脏标记。</summary>
        public async Task<bool> LoadSceneAsync(string path)
        {
            if (_channel == null || !_channel.IsConnected) return false;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _scenePending = tcs;
            var msg = Message.Create(MessageTypes.SceneLoad, new { path });
            try { await _channel.SendAsync(msg); }
            catch { _scenePending = null; return false; }
            using var cts = new CancellationTokenSource(5000);
            cts.Token.Register(() => tcs.TrySetResult(false));
            var success = await tcs.Task;
            if (success)
            {
                CurrentDocumentPath = path;
                IsSceneDirty = false;
                DocumentChanged?.Invoke();
                // 场景刚从磁盘重建：存档里的实例带的是"上次保存时"的源内容，
                // 而 prefab 源之后可能已改过。此刻场景已在引擎里，推一次源让实例对齐。
                await PushPrefabSourcesAsync();
            }
            return success;
        }

        /// <summary>
        /// 编译/重编译前静默保存当前文档（场景或预制体）。
        /// 这些流程成功后都会重启 ADL，而新进程是从磁盘加载文档的——不先落盘，
        /// 编辑器里未保存的改动会随旧进程一起消失。
        /// 未保存过的新文档没有路径，无法静默落盘：给出 Console 提示（重启后会回到空场景）。
        /// </summary>
        private async Task SaveDocumentBeforeCompileAsync()
        {
            if (!IsSceneDirty) return;
            var path = CurrentDocumentPath;
            if (string.IsNullOrEmpty(path))
            {
                EmitConsole(
                    "[studio] The current document has never been saved — its contents will be lost on engine restart",
                    EngineConsoleLevel.Error);
                return;
            }
            if (await SaveSceneAsync(path))
                EmitConsole($"[studio] Saved {Path.GetFileName(path)} before recompiling", EngineConsoleLevel.Info);
        }

        // ── prefab ──

        /// <summary>PrefabExportResultHandler 回调：回填 prefab 导出等待句柄。</summary>
        private void OnPrefabExportResult(string? data)
        {
            _prefabPending?.TrySetResult(data);
        }

        /// <summary>
        /// 请求引擎把 elementId 为根的子树序列化为 prefab 数据（JSON 文本，与 .space 同构）。
        /// 引擎不落盘：调用方负责写出 .prefab 文件。失败/超时返回 null。
        /// </summary>
        public async Task<string?> ExportPrefabAsync(int elementId)
        {
            if (_channel == null || !_channel.IsConnected) return null;
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _prefabPending = tcs;
            var msg = Message.Create(MessageTypes.PrefabExport, new { id = elementId });
            try { await _channel.SendAsync(msg); }
            catch { _prefabPending = null; return null; }
            using var cts = new CancellationTokenSource(5000);
            cts.Token.Register(() => tcs.TrySetResult(null));
            var data = await tcs.Task;
            _prefabPending = null;
            return data;
        }

        /// <summary>
        /// 推送项目内全部 .prefab 源内容（guid → 文件数据），引擎据此把源改动同步到场景实例。
        /// 全量推送（非增量）：引擎不做缓存，收到即应用，全量能顺带清掉已删除的 prefab。
        ///
        /// 只在"场景已经加载完成"的时刻调用，否则同步会落在错误的文档上：
        ///   - 引擎连上时（OnChannelConnected）：引擎启动/重启是先加载 --scene 再 connect 的；
        ///   - 打开场景成功后（LoadSceneAsync）。
        /// </summary>
        public async Task PushPrefabSourcesAsync()
        {
            if (_channel == null || !_channel.IsConnected || _projectPanel == null) return;

            var entries = new List<object>();
            foreach (var path in _projectPanel.PathsWithExtension(".prefab"))
            {
                var guid = _projectPanel.GetGuid(path);
                if (string.IsNullOrEmpty(guid)) continue;
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    using var doc = JsonDocument.Parse(json);
                    entries.Add(new { guid, data = doc.RootElement.Clone() });
                }
                catch (Exception ex)
                {
                    Log($"[prefab] skip unreadable source {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            var msg = Message.Create(MessageTypes.PrefabSources, new { entries });
            try { await _channel.SendAsync(msg); }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>
        /// 把 elementId 所属的整个预制体实例还原到源（清空该实例的全部 override）。
        ///
        /// 引擎不缓存 prefab 源内容，所以清空 override 只是"解绑"：真正把源字段值写回实例，
        /// 要靠紧接其后的 prefab.sources 重推。两条消息走同一通道，FIFO 保证先后，
        /// 因此无需回执。字段值变化由引擎在应用源时按需回发 Inspector 快照。
        /// </summary>
        public async Task RevertPrefabAsync(int elementId)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.PrefabRevert, new { elementId });
            try
            {
                await _channel.SendAsync(msg);
                MarkSceneDirty();
            }
            catch { /* 通道断开时静默 */ }
            await PushPrefabSourcesAsync();
        }

        // ── 运行/终止/暂停 ──

        /// <summary>以指定模式重启 ADL：保存当前编辑器场景（仅 run 模式前）→ 停止旧 ADL →
        /// 以 --scene + --mode 重新启动 → 重新嵌入。通信服务器保持监听，新 ADL 自动回连。
        /// mode: "run" 启动即运行；"edit" 编辑器模式。返回 false 表示重启失败（已输出错误到 Console）。</summary>
        public async Task<bool> RestartAsync(string mode)
        {
            if (_server == null)
            {
                Log("[studio] Restart failed: the message server is not started");
                return false;
            }

            try
            {
                // 1. 卸载嵌入（解除对旧窗口的跟踪与事件订阅），为重新嵌入腾出状态。
                _embedder?.Dispose();
                // 2. 终止旧 ADL 进程（通道随之断开，旧 _channel 失效，新 ADL 回连时重建）。
                await Task.Run(() => _runner.Stop());
                // 3. 重新启动 ADL，透传通信端口、场景存档路径与运行模式。
                //    CurrentDocumentPath 在运行前已由 StartRunAsync 确保非 null。
                SetStatus(mode == "run" ? "Starting engine…" : "Restarting engine…");
                var extraArgs = new List<string> { $"--comm-port={_server.Port}" };
                AppendDocumentArgs(extraArgs);
                extraArgs.Add($"--mode={mode}");
                var hwnd = await Task.Run(() => _runner.Run(_project, extraArgs));
                if (hwnd == IntPtr.Zero)
                {
                    Log("[studio] ADL main window is not ready, skip embedding");
                    return false;
                }
                // 4. 重新嵌入（需 UI 线程读取 SceneSurface 布局几何）。
                if (_embedder != null)
                    await Dispatcher.UIThread.InvokeAsync(() => _embedder.Embed(hwnd, _surface));
                // ADL 重启后会从 CurrentDocumentPath 加载场景（若存在），编辑器状态恢复为已保存。
                IsSceneDirty = false;
                return true;
            }
            catch (Exception ex)
            {
                // ADL 启动/嵌入失败（如子项目编译产物损坏、ADL 进程异常等）：
                // 输出到 Console 面板让用户看到原因，不抛异常避免上层 async void 崩溃。
                EmitConsole("[studio] Engine compilation failed, startup aborted", EngineConsoleLevel.Error);
                EmitConsole(ex.Message, EngineConsoleLevel.Error);
                Log("[studio] Startup failed: " + ex);
                return false;
            }
        }

        /// <summary>运行：确保有存档路径（新场景弹窗获取）→ 保存当前编辑器场景 →
        /// 以运行模式重启 ADL（加载刚保存的存档）。
        /// 预制体文档不能直接运行：运行会为其注入场景级内容（自动相机），
        /// 存回时会把它们写进 .prefab。要在运行中看效果，把预制体拖进场景再运行场景。</summary>
        public async Task StartRunAsync()
        {
            if (IsEditingPrefab)
            {
                EmitConsole(
                    "[studio] Cannot run a prefab document. Drag the prefab into a scene and run that scene.",
                    EngineConsoleLevel.Error);
                return;
            }
            // 新场景未保存：通过回调弹窗获取路径；用户取消则中止运行。
            if (CurrentDocumentPath == null)
            {
                if (PromptForSavePath == null)
                {
                    Log("[studio] Cannot run: the new scene is not saved and the path selection callback is not registered");
                    return;
                }
                var path = await PromptForSavePath();
                if (path == null) return; // 用户取消
                CurrentDocumentPath = path;
            }
            await SaveSceneAsync(CurrentDocumentPath);
            IsPaused = false;
            // 重启失败时不进入运行态，避免工具栏显示与实际不符。
            if (!await RestartAsync("run")) return;
            IsRunning = true;
            StateChanged?.Invoke();
        }

        /// <summary>终止：以编辑器模式重启 ADL，重新加载上次保存的存档（丢弃运行时改动）。</summary>
        public async Task TerminateAsync()
        {
            IsPaused = false;
            // 无论重启是否成功，运行态都结束（终止是用户主动退出运行）。
            await RestartAsync("edit");
            IsRunning = false;
            StateChanged?.Invoke();
        }

        /// <summary>暂停/继续切换：仅切换 world.running，不重启 ADL。仅在运行模式下有效。</summary>
        public async Task TogglePauseAsync()
        {
            if (!IsRunning) return;
            IsPaused = !IsPaused;
            await SetRunningAsync(!IsPaused);
            StateChanged?.Invoke();
        }

        /// <summary>
        /// 编辑器模式下强制重新编译并重启 ADL。用于设置面板修改 AIR SDK 路径后立即使新 SDK 生效。
        /// 运行模式下不执行，避免打断用户。
        /// </summary>
        public async Task RecompileEditorAsync()
        {
            if (IsRunning || _reloading) return;
            _reloading = true;
            IsBusy = true;
            StateChanged?.Invoke();
            try
            {
                RecreateSdkTools();

                EmitConsole("[studio] AIR SDK path changed, recompiling…", EngineConsoleLevel.Info);
                // SDK 切换同样会重启 ADL：先静默保存未保存的编辑（见 SaveDocumentBeforeCompileAsync）。
                await SaveDocumentBeforeCompileAsync();
                var compiled = await Task.Run(() =>
                {
                    AlignAppDescriptorWithSdk();
                    _indexer.Generate(_project);
                    return _compiler.Compile(_project);
                });
                if (!compiled)
                {
                    EmitConsole("[studio] Failed to recompile, now keep the current engine state", EngineConsoleLevel.Error);
                    return;
                }
                EmitConsole("[studio] Compilation succeeded, restarting ADL…", EngineConsoleLevel.Info);
                await RestartAsync("edit");
            }
            catch (Exception ex)
            {
                EmitConsole("[studio] Recompile failed: " + ex.Message, EngineConsoleLevel.Error);
                Log("[studio] Recompile failed: " + ex);
            }
            finally
            {
                _reloading = false;
                IsBusy = false;
                StateChanged?.Invoke();
            }
        }

        // ── Inspector 粘贴组件 ──

        /// <summary>粘贴组件到指定元素。create=true 新建同类型组件并赋值；
        /// create=false 仅对元素上已有的同类型组件赋值（粘贴组件值）。</summary>
        public async Task PasteComponentAsync(int elementId, string component, object fields, bool create)
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = Message.Create(MessageTypes.InspectorPasteComponent,
                new { elementId, component, fields, create });
            try
            {
                await _channel.SendAsync(msg);
                MarkSceneDirty();
            }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>向引擎发送撤销命令。撤销成功后场景被视为已修改。</summary>
        public async Task SendUndoAsync()
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = new Message { Type = MessageTypes.SceneUndo };
            try
            {
                await _channel.SendAsync(msg);
                MarkSceneDirty();
            }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>向引擎发送重做命令。重做成功后场景被视为已修改。</summary>
        public async Task SendRedoAsync()
        {
            if (_channel == null || !_channel.IsConnected) return;
            var msg = new Message { Type = MessageTypes.SceneRedo };
            try
            {
                await _channel.SendAsync(msg);
                MarkSceneDirty();
            }
            catch { /* 通道断开时静默 */ }
        }

        /// <summary>宿主窗口位置变化（拖动）时调用，触发 SceneSurface 重算屏幕坐标。
        /// Avalonia 拖动不触发 LayoutUpdated，需外部驱动以更新嵌入窗口位置。</summary>
        public void NotifyHostMoved() => _surface.RefreshScreenBounds();

        /// <summary>输出一行到 Console 面板（若注入了 sink）并落盘日志。
        /// 实现方负责线程安全，此处可直接从任意线程调用。</summary>
        private void EmitConsole(string text, EngineConsoleLevel level)
        {
            try { _console?.WriteLine(text, level); } catch { }
            Log("[" + level + "] " + text);
        }

        private static void Log(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(EnginePaths.DreamRoot, "engine.log"),
                    message + Environment.NewLine);
            }
            catch { }
        }
    }
}

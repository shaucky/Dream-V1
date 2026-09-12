using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dream.Studio.Engine;
using Dream.Studio.Settings;

namespace Dream.Studio.Panels.Build
{
    /// <summary>
    /// 构建窗口。
    ///
    /// 布局分工：窗口上半部是各平台通用的配置（项目入口 + 应用身份 + 主窗口），
    /// 标签页里只放平台特有的东西（架构、签名、该平台的描述符段）。所有平台最终都从
    /// 同一条命令行落地——未来 ADT 新增 target / 参数时用户直接改命令即可，不必等 Studio 支持。
    ///
    /// 数据去向：
    ///   - 应用身份与平台配置写在项目的 app 描述符里（&lt;name&gt;-app.xml），由
    ///     <see cref="AppDescriptor"/> 直接读写，不在别处另存一份；
    ///   - Dream 专有的构建语义（启动场景、产物目录、按平台的架构与签名）放在
    ///     dream.project.json。
    ///
    /// 目前接通的打包目标：桌面 bundle、Android apk-captive-runtime 与 aab（见
    /// <see cref="AdtPackageCommand.ForPlan"/>）；iOS 标签页的取值会写进描述符，
    /// 等对应 adt target 接上后即可生效。
    /// </summary>
    internal sealed partial class BuildWindow : Window
    {
        /// <summary>未配置平台时的默认目标平台（也是构建窗口默认打开的标签页）。</summary>
        private const string DefaultPlatform = "windows";

        // 下拉候选；首项空串表示「沿用描述符原值」。这里是常用取值，不是白名单：
        // 描述符里已有的取值会被补进候选，不会被 Studio 的列表抹掉。
        private static readonly string[] BooleanValues = { "", "true", "false" };
        private static readonly string[] RenderModes = { "", "auto", "cpu", "gpu", "direct" };
        private static readonly string[] DisplayResolutions = { "", "standard", "high" };
        private static readonly string[] SystemChromes = { "", "standard", "none" };
        private static readonly string[] WebView2Modes = { "", "true", "false", "exclusive" };
        private static readonly string[] ColorDepths = { "", "32bit", "16bit" };

        /// <summary>Android 的 ADT 打包目标候选（没有空项：平台总有默认目标）。</summary>
        private static readonly string[] AndroidTargets = { "apk-captive-runtime", "aab" };

        private static readonly IBrush HintBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD6));
        private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x6B, 0x5E));
        private static readonly IBrush OkBrush = new SolidColorBrush(Color.FromRgb(0x5E, 0xC9, 0x8A));

        /// <summary>
        /// 描述符字段 → 界面控件；<see cref="Values"/> 非空表示下拉候选。
        /// <see cref="Supported"/> = 该字段在面板采用的描述符命名空间下是否存在（见
        /// <see cref="ApplyDescriptorVersionGate"/>）：不支持时控件禁用，且回读时整体跳过，
        /// 以免把描述符里已有的值清掉。
        /// </summary>
        private readonly record struct DescriptorField(string Path, Control Editor, string[]? Values,
                                                      bool Supported = true);

        /// <summary>单平台的架构、打包目标与签名控件（每个平台标签页各一份）。</summary>
        private sealed class PlatformEditors
        {
            public string Name = "";
            public ComboBox Architecture = null!;

            /// <summary>该平台的 ADT 打包目标；只有一个合理目标时为 null（桌面固定 bundle）。</summary>
            public ComboBox? Target;

            public TextBox Keystore = null!;
            public TextBox StorePass = null!;

            /// <summary>时间戳（-tsa）；Android 目标不支持该参数，故 Android 页没有此控件。</summary>
            public TextBox? Timestamp;

            public TextBox? Alias;
            public TextBox? KeyPass;
        }

        /// <summary>
        /// 一行图标尺寸：勾选 = 写入 app 描述符；<see cref="Override"/> 为空表示该尺寸由
        /// "标准图标"缩放而来，否则用这里指定的专用源图。
        /// </summary>
        private sealed class IconSizeRow
        {
            public string Size = "";
            public CheckBox Check = null!;
            public TextBlock Source = null!;
            public string Override = "";
        }

        private readonly string _projectRoot;
        private readonly DreamProjectSettings _settings;
        private readonly IEngineConsoleSink? _console;
        private readonly EngineBuilder _builder;

        private readonly List<DescriptorField> _descriptorFields = new();
        private readonly Dictionary<string, PlatformEditors> _platforms = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>图标尺寸行（顺序 = 当前描述符命名空间下 SDK 允许的尺寸顺序）。</summary>
        private readonly List<IconSizeRow> _iconRows = new();

        /// <summary>项目的 app 描述符（读写应用身份与平台配置）；项目不合法时为 null。</summary>
        private readonly AppDescriptor? _descriptor;
        private readonly string _descriptorPath = "";

        /// <summary>
        /// 面板打开时采用的描述符命名空间版本（当前 SDK 支持的最新版本，形如 "51.3"）。
        /// 字段可用性、图标尺寸清单与写回描述符的命名空间都以它为准，不再看描述符里原有的 xmlns。
        /// </summary>
        private readonly string _namespaceVersion;

        private BuildPlan? _plan;
        private StagedBuild? _staged;
        private bool _busy;

        public BuildWindow(string projectRoot, IEngineConsoleSink? console)
        {
            InitializeComponent();
            _projectRoot = projectRoot;
            _console = console;
            _settings = DreamProjectSettings.Load(projectRoot);
            _builder = new EngineBuilder(new ComponentIndexGenerator(), console);

            var project = new ActionScriptProject(projectRoot);
            if (project.IsValid())
            {
                _descriptorPath = project.GetAppXmlPath();
                _descriptor = AppDescriptor.Load(_descriptorPath);
            }

            // 打开面板时定下命名空间：取当前 SDK 自带的最新描述符命名空间。SDK 只自带自己及更早
            // 版本的 schema，若照着描述符里可能更新的 xmlns 去查，会查不到 schema 而导致门控失效。
            _namespaceVersion = DescriptorSchema.LatestNamespaceVersion(StudioSettings.Current.AirSdkPath) ?? "";

            RegisterPlatforms();
            RegisterDescriptorFields();
            ApplyDescriptorVersionGate();

            // Dream 专有的构建输入（dream.project.json）。
            StartupSceneBox.Text = _settings.StartupScene;
            OutputDirBox.Text = _settings.Build.OutputDirectory;

            // 应用身份与平台配置一律来自项目描述符。
            if (_descriptor != null)
            {
                AppSectionLabel.Text = "APPLICATION — " + Path.GetFileName(_descriptorPath);
                foreach (var field in _descriptorFields) SetFieldValue(field, _descriptor[field.Path]);
            }

            LoadPlatformSettings();
            BuildIconSection();
            LoadArchitecturesAsync();
            SelectStartupTab();

            PackageButton.IsEnabled = false;
            if (_descriptor == null)
                SetStatus("Not a valid ActionScript project (asconfig.json / app descriptor / main class): "
                          + projectRoot, ErrorBrush);
        }

        /// <summary>
        /// 关闭时把界面当前值写回项目文件（含描述符命名空间）。
        ///
        /// 本面板的取值只在显式落盘时才生效（Stage / 打开描述符），关闭即丢弃——但面板在打开时就已经
        /// 按当前 SDK 定下了命名空间与字段可用性，光是关掉面板就会让这些判断悬空（典型情形：用低级
        /// SDK 打开高级项目，描述符里仍是新命名空间，ADL 会以 Unknown namespace 拒绝启动）。
        /// 因此关闭时也写回一次；无变化时两侧的写盘各自有"内容相同就跳过"的自检，不会白改时间戳。
        /// </summary>
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            SaveDescriptorAndSettings();
        }

        // ── 界面 ↔ 数据 ───────────────────────────────────────

        private void RegisterPlatforms()
        {
            AddPlatform(DefaultPlatform, WinArchitectureBox, null, WinKeystoreBox, WinStorePassBox,
                        WinTsaBox, WinAliasBox, WinKeyPassBox);
            AddPlatform("android", AndroidArchitectureBox, AndroidTargetBox, AndroidKeystoreBox,
                        AndroidStorePassBox, null, AndroidAliasBox, AndroidKeyPassBox);
            AddPlatform("ios", IosArchitectureBox, null, IosKeystoreBox, IosStorePassBox,
                        IosTsaBox, IosAliasBox, null);
        }

        private void AddPlatform(string name, ComboBox architecture, ComboBox? target, TextBox keystore,
                                 TextBox storePass, TextBox? timestamp, TextBox? alias, TextBox? keyPass)
        {
            _platforms[name] = new PlatformEditors
            {
                Name = name,
                Architecture = architecture,
                Target = target,
                Keystore = keystore,
                StorePass = storePass,
                Timestamp = timestamp,
                Alias = alias,
                KeyPass = keyPass,
            };
        }

        private void RegisterDescriptorFields()
        {
            // 通用：应用身份
            Add(AppDescriptorPath.Filename, AppNameBox);
            Add(AppDescriptorPath.Id, AppIdBox);
            Add(AppDescriptorPath.DisplayName, AppDisplayNameBox);
            Add(AppDescriptorPath.Version, AppVersionBox);
            Add(AppDescriptorPath.VersionLabel, AppVersionLabelBox);
            Add(AppDescriptorPath.Copyright, AppCopyrightBox);
            Add(AppDescriptorPath.PublisherId, AppPublisherBox);
            Add(AppDescriptorPath.AllowMultipleInstances, AppMultiInstanceBox, BooleanValues);
            Add(AppDescriptorPath.Description, AppDescriptionBox);

            // 通用：主窗口
            Add(AppDescriptorPath.WindowTitle, WindowTitleBox);
            Add(AppDescriptorPath.RenderMode, WindowRenderModeBox, RenderModes);
            Add(AppDescriptorPath.WindowWidth, WindowWidthBox);
            Add(AppDescriptorPath.WindowHeight, WindowHeightBox);
            Add(AppDescriptorPath.WindowMinSize, WindowMinSizeBox);
            Add(AppDescriptorPath.WindowMaxSize, WindowMaxSizeBox);
            Add(AppDescriptorPath.RequestedDisplayResolution, WindowResolutionBox, DisplayResolutions);
            Add(AppDescriptorPath.DepthAndStencil, WindowDepthStencilBox, BooleanValues);
            Add(AppDescriptorPath.UseAngle, WindowUseAngleBox, BooleanValues);
            Add(AppDescriptorPath.SystemChrome, WindowSystemChromeBox, SystemChromes);
            Add(AppDescriptorPath.Visible, WindowVisibleBox, BooleanValues);
            Add(AppDescriptorPath.Transparent, WindowTransparentBox, BooleanValues);
            Add(AppDescriptorPath.Resizable, WindowResizableBox, BooleanValues);
            Add(AppDescriptorPath.Minimizable, WindowMinimizableBox, BooleanValues);
            Add(AppDescriptorPath.Maximizable, WindowMaximizableBox, BooleanValues);

            // 平台特有的描述符段
            Add(AppDescriptorPath.WindowsLocalAppData, WinLocalAppDataBox, BooleanValues);
            Add(AppDescriptorPath.WindowsMaxD3D, WinMaxD3DBox);
            Add(AppDescriptorPath.WindowsUseWebView2, WinUseWebView2Box, WebView2Modes);
            Add(AppDescriptorPath.WindowsUseDirectDrawFonts, WinDirectDrawFontsBox, BooleanValues);

            Add(AppDescriptorPath.AndroidColorDepth, AndroidColorDepthBox, ColorDepths);
            Add(AppDescriptorPath.AndroidBuildArchitectures, AndroidBuildArchitecturesBox);
            Add(AppDescriptorPath.AndroidContainsVideo, AndroidContainsVideoBox, BooleanValues);
            Add(AppDescriptorPath.AndroidSupportsAndroidTV, AndroidSupportsTvBox, BooleanValues);
            Add(AppDescriptorPath.AndroidWebContentsDebugging, AndroidWebDebugBox, BooleanValues);
            Add(AppDescriptorPath.AndroidCreateAppBundle, AndroidCreateBundleBox, BooleanValues);
            Add(AppDescriptorPath.AndroidPreventDeviceModelAccess, AndroidPreventDeviceModelBox, BooleanValues);
            Add(AppDescriptorPath.AndroidAsyncStartup, AndroidAsyncStartupBox, BooleanValues);
            Add(AppDescriptorPath.AndroidUseCamera2, AndroidCamera2Box, BooleanValues);

            Add(AppDescriptorPath.IosRequestedDisplayResolution, IosResolutionBox, DisplayResolutions);
            Add(AppDescriptorPath.IosForceCpuRenderDevices, IosForceCpuBox);
            Add(AppDescriptorPath.IosDisableCustomKeyboard, IosCustomKeyboardBox, BooleanValues);
            Add(AppDescriptorPath.IosExternalSwfs, IosExternalSwfsBox);
        }

        private void Add(string path, Control editor, string[]? values = null)
            => _descriptorFields.Add(new DescriptorField(path, editor, values));

        private static void SetFieldValue(DescriptorField field, string value)
        {
            switch (field.Editor)
            {
                case ComboBox combo:
                    SelectValue(combo, field.Values ?? Array.Empty<string>(), value);
                    break;
                case TextBox box:
                    box.Text = value;
                    break;
            }
        }

        private static string GetFieldValue(DescriptorField field)
            => field.Editor switch
            {
                ComboBox combo => combo.SelectedItem as string ?? "",
                TextBox box => box.Text?.Trim() ?? "",
                _ => "",
            };

        /// <summary>
        /// 选中候选中的某个值；取值不在候选里（例如新版本 AIR 引入的取值）时补进候选项，
        /// 避免 Studio 的候选列表把描述符里已有的取值挡掉。
        /// </summary>
        private static void SelectValue(ComboBox combo, IReadOnlyList<string> candidates, string value)
        {
            var items = new List<string>(candidates);
            if (!items.Contains(value)) items.Add(value);
            combo.ItemsSource = items;
            combo.SelectedItem = value;
        }

        /// <summary>
        /// 按 SDK 的最新描述符命名空间给字段做门控：该版本不认识的字段禁用编辑。
        ///
        /// 判定依据是 AIR SDK 自带的描述符 schema（见 <see cref="DescriptorSchema"/>），与 ADT
        /// 校验用的是同一份定义——填了不支持的字段，ADT 会直接以
        /// <c>error 103: xxx is an unexpected element/attribute</c> 终止打包。
        ///
        /// 基准取 SDK 自带的最新命名空间，而不是描述符里的 xmlns：SDK 只带自己及更早版本的
        /// schema，描述符命名空间比 SDK 新时按它查必然查不到，门控会整体失效（表现为"换了 SDK
        /// 面板毫无变化"）。禁用只是不让改：控件仍显示描述符里已有的值，回读时也整体跳过，
        /// 不会把值清掉。
        /// </summary>
        private void ApplyDescriptorVersionGate()
        {
            var sdk = StudioSettings.Current.AirSdkPath;
            var version = _namespaceVersion;
            var schema = version.Length > 0 ? DescriptorSchema.For(sdk, version) : null;

            if (schema == null)
            {
                DescriptorSupportHint.Text = "";
                return;
            }

            var disabled = new List<string>();
            for (var i = 0; i < _descriptorFields.Count; i++)
            {
                var field = _descriptorFields[i];
                if (schema.Supports(field.Path)) continue;

                var since = DescriptorSchema.FirstSupportingVersion(sdk, field.Path);
                field.Editor.IsEnabled = false;
                field.Editor.Opacity = 0.45;
                ToolTip.SetTip(field.Editor,
                    (since != null
                        ? $"Not available in this AIR version (added in {since}). "
                        : "Not available in this AIR version. ")
                    + "The value already in the app descriptor is kept as is.");
                _descriptorFields[i] = field with { Supported = false };
                disabled.Add(field.Path.Split('/')[^1] + (since != null ? $" (needs {since})" : ""));
            }

            DescriptorSupportHint.Text = disabled.Count == 0
                ? ""
                : $"{disabled.Count} field(s) are not available in this AIR version, "
                  + $"so they are disabled and left untouched: {string.Join(", ", disabled)}";
        }

        private void LoadPlatformSettings()
        {
            foreach (var editors in _platforms.Values)
            {
                var platform = _settings.Build.GetPlatform(editors.Name);
                editors.Architecture.ItemsSource = new List<string> { platform.Architecture };
                editors.Architecture.SelectedIndex = 0;
                if (editors.Target != null)
                {
                    var target = TargetOrDefault(platform.Target, editors.Name);
                    var items = new List<string>(AndroidTargets);
                    if (!items.Contains(target)) items.Add(target);
                    editors.Target.ItemsSource = items;
                    editors.Target.SelectedItem = target;
                }
                editors.Keystore.Text = platform.Certificate.Keystore;
                editors.StorePass.Text = platform.Certificate.StorePass;
                if (editors.Timestamp != null) editors.Timestamp.Text = platform.Certificate.TimestampUrl;
                if (editors.Alias != null) editors.Alias.Text = platform.Certificate.Alias;
                if (editors.KeyPass != null) editors.KeyPass.Text = platform.Certificate.KeyPass;
            }
        }

        /// <summary>把界面上各平台的架构、打包目标与签名写回项目设置。</summary>
        private void SavePlatformSettings()
        {
            _settings.Build.Platform = SelectedPlatform;
            foreach (var editors in _platforms.Values)
            {
                var platform = _settings.Build.GetPlatform(editors.Name);
                platform.Architecture = editors.Architecture.SelectedItem as string ?? "";
                if (editors.Target != null) platform.Target = editors.Target.SelectedItem as string ?? "";
                platform.Certificate.Keystore = editors.Keystore.Text?.Trim() ?? "";
                platform.Certificate.StorePass = editors.StorePass.Text ?? "";
                // 该平台没有时间戳控件（Android 目标不支持 -tsa）时保留已存的值，不清空。
                if (editors.Timestamp != null)
                    platform.Certificate.TimestampUrl = editors.Timestamp.Text?.Trim() ?? "";
                platform.Certificate.Alias = editors.Alias?.Text?.Trim() ?? "";
                platform.Certificate.KeyPass = editors.KeyPass?.Text ?? "";
            }
        }

        /// <summary>项目设置里的打包目标；未配置时取平台默认（iOS 等未接通的平台为空串）。</summary>
        private static string TargetOrDefault(string configured, string platform)
            => configured.Length > 0 ? configured : AdtPackageCommand.DefaultTarget(platform);

        /// <summary>当前选中的平台标签页；停在命令行标签页时沿用上次的选择。</summary>
        private string SelectedPlatform
        {
            get
            {
                if ((Tabs.SelectedItem as TabItem)?.Tag is string name && _platforms.ContainsKey(name))
                    return name;
                return _settings.Build.Platform.Length > 0 ? _settings.Build.Platform : DefaultPlatform;
            }
        }

        private void SelectStartupTab()
        {
            foreach (var item in Tabs.Items)
            {
                if (item is not TabItem tab || tab.Tag is not string name) continue;
                if (!string.Equals(name, _settings.Build.Platform, StringComparison.OrdinalIgnoreCase)) continue;
                Tabs.SelectedItem = tab;
                return;
            }
        }

        /// <summary>探测当前 SDK 支持的架构取值，避免把某版本的值写死。</summary>
        private void LoadArchitecturesAsync()
        {
            var sdk = StudioSettings.Current.AirSdkPath;
            _ = Task.Run(() => AdtCapabilitiesProbe.Probe(sdk)).ContinueWith(task =>
            {
                var caps = task.Result;
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var editors in _platforms.Values)
                    {
                        var current = _settings.Build.GetPlatform(editors.Name).Architecture;
                        var items = new List<string> { "" };
                        items.AddRange(caps.Architectures);
                        if (current.Length > 0 && !items.Contains(current)) items.Add(current);
                        editors.Architecture.ItemsSource = items;
                        editors.Architecture.SelectedItem = current;
                    }
                    if (caps.Error.Length > 0) Log("[build] ADT probe: " + caps.Error);
                });
            }, TaskScheduler.Default);
        }

        // ── 应用图标 ──────────────────────────────────────────

        /// <summary>
        /// 按面板打开时定下的命名空间（当前 SDK 的最新版本）列出 SDK 允许的图标尺寸。
        ///
        /// 尺寸清单不硬编码：AIR 只接受自己不认识的尺寸会报 error 103，而各版本认识哪些尺寸是变化的
        /// （1.1 只有 16/32/48/128，51.1 起有 34 个）。这些数据在 SDK 自带的
        /// templates/air/Descriptor.&lt;命名空间&gt;.xsd 里，按 SDK 的最新命名空间读取即可自动
        /// 向后兼容——描述符原来的 xmlns 可能比 SDK 还新，照它查会查不到 schema。
        /// </summary>
        private void BuildIconSection()
        {
            var sdk = StudioSettings.Current.AirSdkPath;
            var version = _namespaceVersion;
            var sizes = IconSizeCatalog.SizesFor(sdk, version);

            IconSourceBox.Text = _settings.Build.Icon.Source;

            var configured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _descriptor?.GetIcons() ?? new List<AppIconEntry>())
                configured[entry.Size] = entry.Path;

            var fallback = new HashSet<string>(IconSizeCatalog.FallbackSizes, StringComparer.OrdinalIgnoreCase);

            foreach (var size in sizes)
            {
                var row = new IconSizeRow { Size = size };
                row.Check = new CheckBox
                {
                    Content = size,
                    Width = 78,
                    FontSize = 12,
                    Foreground = TextBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Source = new TextBlock
                {
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                if (_settings.Build.Icon.Overrides.TryGetValue(size, out var overridden))
                    row.Override = overridden;
                if (row.Override.Length == 0 && configured.TryGetValue(size, out var path)
                    && !AppIconWriter.IsGeneratedPath(size, path))
                    row.Override = path; // 描述符里指向的不是生成文件 → 视为该尺寸的专用图

                row.Check.IsChecked = configured.ContainsKey(size)
                    || (configured.Count == 0 && fallback.Contains(size));

                var browse = new Button { Content = "…", MinWidth = 26, Margin = new Thickness(8, 0, 0, 0) };
                browse.Click += (_, _) => PickIconOverride(row);
                var clear = new Button { Content = "×", MinWidth = 24, Margin = new Thickness(6, 0, 0, 0) };
                clear.Click += (_, _) =>
                {
                    row.Override = "";
                    RefreshIconRow(row);
                };

                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
                Grid.SetColumn(row.Check, 0);
                line.Children.Add(row.Check);
                Grid.SetColumn(row.Source, 1);
                line.Children.Add(row.Source);
                Grid.SetColumn(browse, 2);
                line.Children.Add(browse);
                Grid.SetColumn(clear, 3);
                line.Children.Add(clear);

                IconSizeList.Children.Add(line);
                _iconRows.Add(row);
                RefreshIconRow(row);
            }

            IconHintText.Text = (version.Length == 0
                    ? "No SDK schema for icon sizes was found — the desktop default set is offered."
                    : $"Only these {sizes.Count} icon size(s) are offered.")
                + " ADT rejects any other size with error 103."
                + " Checked sizes are written to the descriptor; unchecked ones are removed.";
        }

        private void RefreshIconRow(IconSizeRow row)
        {
            var generated = row.Override.Length == 0;
            row.Source.Text = generated ? "standard icon (scaled)" : row.Override;
            row.Source.Foreground = generated ? HintBrush : TextBrush;
        }

        private async void BrowseIconSource_Click(object? sender, RoutedEventArgs e)
        {
            var path = await PickFileAsync("Standard application icon", "PNG image", new[] { "*.png" });
            if (path != null) IconSourceBox.Text = ToProjectRelative(path);
        }

        /// <summary>把标准图标用到所有尺寸：清掉各尺寸的专用图（勾选状态不变）。</summary>
        private void UseStandardIconForAll_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var row in _iconRows)
            {
                row.Override = "";
                RefreshIconRow(row);
            }
        }

        private async void PickIconOverride(IconSizeRow row)
        {
            var path = await PickFileAsync("Icon " + row.Size, "PNG image", new[] { "*.png" });
            if (path == null) return;
            row.Override = ToProjectRelative(path);
            row.Check.IsChecked = true;
            RefreshIconRow(row);
        }

        /// <summary>项目相对路径 → 存在的绝对路径；空或不存在返回 null。</summary>
        private string? ResolveIconSource(string projectRelative)
        {
            if (string.IsNullOrWhiteSpace(projectRelative)) return null;
            try
            {
                var abs = Path.GetFullPath(Path.Combine(_projectRoot, projectRelative));
                return File.Exists(abs) ? abs : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 生成各勾选尺寸的精确尺寸图标文件，并把 &lt;icon&gt; 写进项目描述符。
        ///
        /// 每个尺寸的源图 = 该尺寸的专用图（若有）否则标准图标；生成结果统一落在描述符同级的
        /// icons/ 目录，描述符里写同名相对路径——ADT 对图标既要尺寸名合法（error 103），
        /// 又要图片像素与元素名完全一致（error 201），所以必须落盘成精确尺寸的文件。
        /// </summary>
        private bool SyncIcons(ICollection<string> errors)
        {
            if (_descriptor == null) return true;

            var appRoot = Path.GetDirectoryName(_descriptorPath) ?? _projectRoot;
            var standard = ResolveIconSource(IconSourceBox.Text?.Trim() ?? "");
            var entries = new List<AppIconEntry>();
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 完全没配图标（既无标准图标也无专用图）：不写图标，并清掉描述符里可能残留的 <icon>。
            if (standard == null && !_iconRows.Exists(r => r.Override.Length > 0))
            {
                _descriptor.SetIcons(entries);
                _settings.Build.Icon.Source = IconSourceBox.Text?.Trim() ?? "";
                _settings.Build.Icon.Overrides = overrides;
                return true;
            }

            foreach (var row in _iconRows)
            {
                if (row.Check.IsChecked != true) continue;

                var source = row.Override.Length > 0 ? ResolveIconSource(row.Override) : standard;
                if (source == null)
                {
                    errors.Add($"[icon] {row.Size}: no source image"
                        + (row.Override.Length > 0
                            ? $" (override not found: {row.Override})"
                            : " — set the standard icon first"));
                    continue;
                }

                var relative = AppIconWriter.Write(appRoot, row.Size, source, _console);
                if (relative == null)
                {
                    errors.Add($"[icon] {row.Size}: could not write the icon (see Console)");
                    continue;
                }

                entries.Add(new AppIconEntry(row.Size, relative));
                if (row.Override.Length > 0) overrides[row.Size] = row.Override;
            }

            if (entries.Count == 0 && _iconRows.Count > 0 && IconSourceBox.Text is { Length: > 0 })
                errors.Add("[icon] no size produced a file; check the standard icon path");

            _descriptor.SetIcons(entries);
            _settings.Build.Icon.Source = IconSourceBox.Text?.Trim() ?? "";
            _settings.Build.Icon.Overrides = overrides;
            if (entries.Count > 0) Log($"[icon] {entries.Count} icon size(s) written to {AppIconWriter.DirectoryFor(appRoot)}");
            return errors.Count == 0;
        }

        // ── 文件选择 ──────────────────────────────────────────

        private async void BrowseStartupScene_Click(object? sender, RoutedEventArgs e)
        {
            var path = await PickFileAsync("Startup scene", "Scene", new[] { "*.space" });
            if (path != null) StartupSceneBox.Text = ToProjectRelative(path);
        }

        private async void BrowseOutputDir_Click(object? sender, RoutedEventArgs e)
        {
            var path = await PickFolderAsync("Select output directory");
            if (path != null) OutputDirBox.Text = ToProjectRelative(path);
        }

        private async void BrowseWinCertificate_Click(object? sender, RoutedEventArgs e)
            => WinKeystoreBox.Text = await PickCertificateAsync() ?? WinKeystoreBox.Text;

        private async void BrowseAndroidCertificate_Click(object? sender, RoutedEventArgs e)
            => AndroidKeystoreBox.Text = await PickCertificateAsync() ?? AndroidKeystoreBox.Text;

        private async void BrowseIosCertificate_Click(object? sender, RoutedEventArgs e)
            => IosKeystoreBox.Text = await PickCertificateAsync() ?? IosKeystoreBox.Text;

        private Task<string?> PickCertificateAsync()
            => PickFileAsync("Certificate", "Certificate", new[] { "*.p12", "*.pfx" });

        private async Task<string?> PickFileAsync(string title, string filterName, string[] patterns)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns } },
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        }

        private async Task<string?> PickFolderAsync(string title)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return null;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        }

        /// <summary>绝对路径 → 项目相对路径（设置文件里存相对路径，换机器/换盘符仍有效）。</summary>
        private string ToProjectRelative(string path)
        {
            try
            {
                var rel = Path.GetRelativePath(_projectRoot, path);
                return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel;
            }
            catch
            {
                return path;
            }
        }

        // ── 动作 ──────────────────────────────────────────────

        private async void Stage_Click(object? sender, RoutedEventArgs e) => await RunStageAsync();

        private async void Package_Click(object? sender, RoutedEventArgs e) => await RunPackageAsync();

        private async void Build_Click(object? sender, RoutedEventArgs e)
        {
            if (await RunStageAsync()) await RunPackageAsync();
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 用系统默认程序打开项目描述符。
        ///
        /// 打开前先把界面上的当前值写回描述符与项目设置（与 Stage 同一套回读逻辑），再关掉本窗口：
        /// 否则打开的是磁盘上的旧内容，而且外部编辑器与本面板会同时改同一份文件。
        /// </summary>
        private void OpenDescriptor_Click(object? sender, RoutedEventArgs e)
        {
            if (_descriptor == null || _descriptorPath.Length == 0)
            {
                SetStatus("No app descriptor was loaded — nothing to open.", ErrorBrush);
                return;
            }

            SaveDescriptorAndSettings();
            Close();

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _descriptorPath,
                    UseShellExecute = true,   // 用系统关联程序打开
                });
            }
            catch (Exception ex)
            {
                Log("[build] open app descriptor failed: " + ex.Message);
            }
        }

        /// <summary>
        /// 回读界面 → 项目设置 + 描述符字段 → 落盘。
        /// 项目不合法（无描述符）时什么都不做。
        /// </summary>
        private void SaveDescriptorAndSettings()
        {
            if (_descriptor == null) return;
            CollectPlan();
            DreamProjectSettings.Write(DreamProjectSettings.GetPath(_projectRoot), _settings);
            if (_descriptor.Save()) Log("[build] app descriptor updated: " + _descriptorPath);
        }

        /// <summary>暂存：编译 release + 收集被引用资源 + 生成描述符与清单，并据此刷新命令。</summary>
        private async Task<bool> RunStageAsync()
        {
            if (_busy) return false;
            if (_descriptor == null)
            {
                SetStatus("No app descriptor was loaded — cannot build.", ErrorBrush);
                return false;
            }

            var plan = CollectPlan();

            // 图标先生成精确尺寸的文件、再连同 <icon> 一起写进描述符（顺序不能反：描述符里记的是生成后的路径）。
            var iconErrors = new List<string>();
            if (!SyncIcons(iconErrors))
            {
                foreach (var message in iconErrors) Log(message);
                SetStatus("Icon configuration is incomplete — see Console.", ErrorBrush);
                return false;
            }

            DreamProjectSettings.Write(DreamProjectSettings.GetPath(_projectRoot), _settings);
            if (_descriptor.Save()) Log("[build] app descriptor updated: " + _descriptorPath);

            SetBusy(true, "Staging…");
            StagedBuild? staged;
            try
            {
                var errors = new List<string>();
                staged = await Task.Run(() => _builder.Stage(plan, errors, Log));
                foreach (var error in errors) Log(error);
            }
            catch (Exception ex)
            {
                Log("[build] staging failed: " + ex.Message);
                staged = null;
            }
            finally
            {
                SetBusy(false);
            }

            if (staged == null)
            {
                _staged = null;
                PackageButton.IsEnabled = false;
                SetStatus("Stage failed — see Console.", ErrorBrush);
                return false;
            }

            _plan = plan;
            _staged = staged;

            // bundle / apk / aab 目标都强制要求签名：用户没配证书时自动准备一张自签名开发证书，
            // 保证零配置可打包（Android 与桌面共用同一张 PKCS12）。
            var certificate = _settings.Build.GetPlatform(plan.Platform).Certificate;
            if (certificate.IsEmpty)
            {
                var sdk = StudioSettings.Current.AirSdkPath;
                var dev = await Task.Run(() => DevCertificate.Ensure(_projectRoot, sdk, Log));
                if (dev != null)
                {
                    certificate = dev;
                    Log("[build] using a generated self-signed certificate: " + dev.Keystore);
                }
            }

            PackageButton.IsEnabled = RegenerateCommand(certificate);
            if (!PackageButton.IsEnabled)
            {
                SetStatus($"Staged {staged.ResourceCount} resource(s), {staged.AtlasEntryCount} atlas entr(ies), "
                        + $"{staged.IconCount} icon(s), but no packaging target is wired for {plan.Platform} yet.",
                    ErrorBrush);
                return false;
            }

            // aab 必须带 -platformsdk：没配 Android SDK 时生成的命令会缺这一项，先说明白。
            if (IsAndroidBundleTarget(plan.Target) && StudioSettings.Current.AndroidSdkPath.Length == 0)
                Log("[build] the aab target needs an Android SDK — set it in Settings; "
                    + "the generated command currently has no -platformsdk.");

            SetStatus($"Staged {staged.ResourceCount} resource(s), {staged.AtlasEntryCount} atlas entr(ies), "
                    + $"{staged.IconCount} icon(s). Now Package → {plan.PackageOutputPath}", OkBrush);
            return true;
        }

        /// <summary>打包：执行命令行标签页里的命令（用户可改）。</summary>
        private async Task RunPackageAsync()
        {
            if (_busy) return;
            var args = AdtCommandLine.Parse(CommandLineBox.Text ?? "");
            if (args.Count == 0)
            {
                SetStatus("Command line is empty — run Stage first.", ErrorBrush);
                return;
            }

            // 旧产物先清掉：保证输出只含本次构建结果（bundle 是逐文件落盘的目录，apk / aab 是单文件）。
            if (_plan != null) TryDeleteOutput(_plan.PackageOutputPath);

            SetBusy(true, "Packaging…");
            string error = "";
            bool ok;
            try
            {
                var sdk = StudioSettings.Current.AirSdkPath;
                var workingDirectory = _projectRoot;
                ok = await Task.Run(() =>
                    new AdtPackager(sdk, _console).Run(args, workingDirectory, out error));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ok = false;
            }
            finally
            {
                SetBusy(false);
            }

            if (ok)
            {
                var target = _plan?.PackageOutputPath ?? "";
                Log("[build] package succeeded: " + target);
                SetStatus("Package succeeded: " + target, OkBrush);
            }
            else
            {
                SetStatus("Package failed: " + error, ErrorBrush);
            }
        }

        /// <summary>
        /// 把命令行标签页的内容重置为当前平台/目标生成的命令。
        /// 返回 false 表示该平台还没接通打包目标（如 iOS）——命令行已清空，不该继续 Package。
        /// </summary>
        private bool RegenerateCommand(CertificateSettings certificate)
        {
            if (_plan == null || _staged == null) return false;
            var args = AdtPackageCommand.ForPlan(_plan, _staged, certificate,
                                                StudioSettings.Current.AndroidSdkPath);
            CommandLineBox.Text = AdtCommandLine.Format(args);
            return args.Count > 0;
        }

        /// <summary>该目标是不是 Android App Bundle（需要 -platformsdk）。</summary>
        private static bool IsAndroidBundleTarget(string target) => target is "aab" or "aab-debug";

        /// <summary>回读界面 → 项目设置 + 描述符字段 + 构建意图。</summary>
        private BuildPlan CollectPlan()
        {
            _settings.StartupScene = StartupSceneBox.Text?.Trim() ?? "";
            _settings.Build.OutputDirectory = string.IsNullOrWhiteSpace(OutputDirBox.Text)
                ? "build" : OutputDirBox.Text!.Trim();
            SavePlatformSettings();

            // 应用身份与平台配置写回项目描述符（Stage 时统一落盘；空值不写入，沿用原值）。
            // 当前命名空间不支持的字段整体跳过：既不该写进去（ADT 会报 error 103），
            // 也不能因为控件被禁用它就把描述符里已有的值抹掉。
            _descriptor!.SetNamespaceVersion(_namespaceVersion);
            foreach (var field in _descriptorFields)
            {
                if (!field.Supported) continue;
                _descriptor![field.Path] = GetFieldValue(field);
            }

            // 产物名是 ADT 的必填项：空值回退到默认名并回填界面，避免界面与产物不一致。
            var appName = _descriptor![AppDescriptorPath.Filename];
            if (appName.Length == 0)
            {
                appName = "DreamApp";
                AppNameBox.Text = appName;
                _descriptor[AppDescriptorPath.Filename] = appName;
            }

            // 当前标签页决定平台：Stage 出来的目标与命令都按它生成。
            var platform = SelectedPlatform;
            var platformSettings = _settings.Build.GetPlatform(platform);
            return new BuildPlan
            {
                ProjectRoot = _projectRoot,
                OutputDirectory = _settings.Build.ResolveOutputDirectory(_projectRoot),
                Platform = platform,
                Target = TargetOrDefault(platformSettings.Target, platform),
                Architecture = platformSettings.Architecture,
                AppName = appName,
                StartupScenePath = _settings.ResolveStartupScene(_projectRoot),
            };
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            StageButton.IsEnabled = !busy;
            BuildButton.IsEnabled = !busy;
            PackageButton.IsEnabled = !busy && _staged != null;
            if (status != null) SetStatus(status, HintBrush);
        }

        private void SetStatus(string text, IBrush brush)
        {
            StatusText.Text = text;
            StatusText.Foreground = brush;
        }

        private void Log(string text)
        {
            try { _console?.WriteLine(text, EngineConsoleLevel.Info); } catch { }
        }

        /// <summary>删除上一次的产物：可能是目录（bundle），也可能是文件（apk / aab）。</summary>
        private static void TryDeleteOutput(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { /* 被占用：留给 ADT 报错，用户能看到具体原因 */ }
        }
    }
}

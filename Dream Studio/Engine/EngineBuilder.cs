using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Dream.Studio.Panels.Project;
using Dream.Studio.Settings;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 一次构建的意图（由项目设置 + 构建窗口当前平台标签页合成）。
    /// 与平台无关：平台差异体现在 <see cref="Platform"/> / <see cref="Architecture"/>
    /// 以及调用方据此选择的 ADT target。
    /// </summary>
    internal sealed class BuildPlan
    {
        public string ProjectRoot = "";
        /// <summary>产物根目录（绝对）。</summary>
        public string OutputDirectory = "";
        /// <summary>平台标识（windows / macos / linux / android / ios ...）。</summary>
        public string Platform = "windows";
        /// <summary>
        /// ADT 打包目标（-target 取值）：bundle / apk-captive-runtime / aab ...
        /// 由平台设置给出，空时取平台默认（见 <see cref="AdtPackageCommand.DefaultTarget"/>）。
        /// </summary>
        public string Target = "bundle";
        /// <summary>目标架构（ADT -arch 取值）；空 = 交给 ADT 默认。</summary>
        public string Architecture = "";
        /// <summary>产物名：SWF、描述符与可执行文件都用它（取自描述符的 &lt;filename&gt;）。</summary>
        public string AppName = "DreamApp";
        /// <summary>启动场景绝对路径；null = 未配置。</summary>
        public string? StartupScenePath;

        /// <summary>暂存目录：SWF 与被引用资源都先落在这里，再由 ADT 原样收进包。</summary>
        public string StagingDirectory => Path.Combine(OutputDirectory, "staging");

        /// <summary>生成的 app 描述符路径（放在产物根，不进暂存目录）。</summary>
        public string AppDescriptorPath => Path.Combine(OutputDirectory, AppName + "-app.xml");

        /// <summary>
        /// 打包输出路径：目录型目标（bundle）产出目录，文件型目标（apk / aab）产出同名文件，
        /// 后缀由 <see cref="Target"/> 决定——同一次构建换目标不会互相覆盖。
        /// </summary>
        public string PackageOutputPath
            => Path.Combine(OutputDirectory,
                (Architecture.Length > 0 ? $"{Platform}-{Architecture}" : Platform) + PackageExtension);

        /// <summary>产物后缀：bundle 无（目录），apk / aab 按格式补上。</summary>
        private string PackageExtension => Target switch
        {
            "apk" or "apk-debug" or "apk-emulator" or "apk-captive-runtime" => ".apk",
            "aab" or "aab-debug" => ".aab",
            _ => "",
        };
    }

    /// <summary>暂存结果：描述符与包内文件都已就位，可直接交给 ADT 打包。</summary>
    internal sealed class StagedBuild
    {
        public string StagingDirectory = "";
        public string AppDescriptorPath = "";
        public string SwfPath = "";
        public string ManifestPath = "";
        /// <summary>包内资源条数（不含图集纹理）。</summary>
        public int ResourceCount;
        /// <summary>图集覆盖映射条数。</summary>
        public int AtlasEntryCount;
        /// <summary>包内应用图标文件数。</summary>
        public int IconCount;
    }

    /// <summary>
    /// 构建流水线：release 编译 → 收集被引用资源 → 生成 app 描述符与产物清单 → 暂存。
    /// 只做到"暂存"为止；真正的 ADT 打包由 <see cref="AdtPackager"/> 按命令行执行，
    /// 以便构建窗口的命令行标签页能直接改写那条命令。
    /// </summary>
    internal sealed class EngineBuilder
    {
        private readonly IComponentIndexer _indexer;
        private readonly IEngineConsoleSink? _console;

        public EngineBuilder(IComponentIndexer indexer, IEngineConsoleSink? console = null)
        {
            _indexer = indexer;
            _console = console;
        }

        /// <summary>暂存一次构建。失败返回 null，原因追加到 errors。</summary>
        public StagedBuild? Stage(BuildPlan plan, ICollection<string> errors, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(plan.StartupScenePath) || !File.Exists(plan.StartupScenePath))
            {
                errors.Add("[build] startup scene is not configured or missing"
                    + " (set \"startupScene\" in dream.project.json)");
                return null;
            }

            var project = new ActionScriptProject(plan.ProjectRoot);
            if (!project.IsValid())
            {
                errors.Add("[build] not a valid ActionScript project (asconfig.json / app descriptor / main class)");
                return null;
            }

            // 每次构建清空暂存目录：否则上一版删掉的资源会残留进包。
            var staging = plan.StagingDirectory;
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
            }
            catch (Exception ex)
            {
                errors.Add("[build] cannot reset staging directory: " + ex.Message);
                return null;
            }

            log("[build] generating component index…");
            _indexer.Generate(project);

            log("[build] compiling release swf…");
            var swfPath = Path.Combine(staging, plan.AppName + ".swf");
            var compiler = new MxmlcCompiler(StudioSettings.Current.AirSdkPath, _console);
            var options = new MxmlcOptions { OutputPath = swfPath, Debug = false };
            // 发布构建：编辑器通信/工具代码整体不编译进来（CONFIG::STUDIO=false）。
            options.DefineOverrides["CONFIG::STUDIO"] = "false";
            if (!compiler.Compile(project, options))
            {
                errors.Add("[build] release compilation failed (see Console)");
                return null;
            }
            if (!File.Exists(swfPath))
            {
                errors.Add("[build] swf was not produced: " + swfPath);
                return null;
            }

            log("[build] scanning resources…");
            var metaDb = new MetaDatabase();
            metaDb.ScanDirectory(plan.ProjectRoot);

            var manifest = BuildAssetCollector.Collect(
                plan.ProjectRoot, plan.StartupScenePath!, metaDb, staging, errors, log);
            var manifestPath = Path.Combine(staging, BuildManifest.FileName);
            manifest.Write(manifestPath);

            if (!WriteAppDescriptor(project, plan, errors)) return null;
            if (!StageIcons(project, plan, errors, log, out var iconCount)) return null;

            log($"[build] staged: {manifest.Resources.Count} resource(s), {manifest.Atlas.Count} atlas entr(ies)");

            return new StagedBuild
            {
                StagingDirectory = staging,
                AppDescriptorPath = plan.AppDescriptorPath,
                SwfPath = swfPath,
                ManifestPath = manifestPath,
                ResourceCount = manifest.Resources.Count,
                AtlasEntryCount = manifest.Atlas.Count,
                IconCount = iconCount,
            };
        }

        /// <summary>
        /// 把描述符 &lt;icon&gt; 引用的图标文件复制进暂存目录。
        ///
        /// 图标不是场景引用的资源（BuildAssetCollector 按 GUID 扫描，扫不到它们），必须单独处理：
        /// ADT 对描述符里的相对路径按**应用根目录**（即 <c>-C</c> 目标目录）解析，而源项目里
        /// 这些路径是相对描述符所在目录的，所以按同一相对路径放进暂存目录，两边都能解析。
        /// </summary>
        private static bool StageIcons(ActionScriptProject project, BuildPlan plan,
                                       ICollection<string> errors, Action<string> log, out int iconCount)
        {
            iconCount = 0;
            var sourceDescriptor = project.GetAppXmlPath();
            XDocument doc;
            try
            {
                doc = XDocument.Load(sourceDescriptor);
            }
            catch (Exception ex)
            {
                errors.Add("[build] app descriptor: " + ex.Message);
                return false;
            }

            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var icon = doc.Root?.Element(ns + "icon");
            if (icon == null) return true; // 未配置图标

            var appRoot = Path.GetDirectoryName(sourceDescriptor) ?? plan.ProjectRoot;
            foreach (var element in icon.Elements())
            {
                var relative = element.Value.Trim();
                if (relative.Length == 0)
                {
                    // 空值会让 ADT 报 error 200（找不到图标文件），在此直接给出可定位的提示。
                    errors.Add($"[build] icon {element.Name.LocalName} has no file configured");
                    return false;
                }

                var source = Path.GetFullPath(Path.Combine(appRoot, relative));
                if (!File.Exists(source))
                {
                    errors.Add($"[build] icon file not found: {relative} (expected at {source})");
                    return false;
                }

                var target = Path.Combine(plan.StagingDirectory, relative);
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(source, target, true);
                iconCount++;
            }

            log($"[build] staged {iconCount} icon(s)");
            return true;
        }

        /// <summary>
        /// 生成 ADT 需要的描述符副本。
        ///
        /// 以项目现有描述符为模板（应用身份已由构建窗口写回源文件），只改「必须与产物一致」
        /// 的两处：&lt;filename&gt; 与 &lt;initialWindow&gt;&lt;content&gt;——描述符与 SWF 同名同处，
        /// content 必须指向打进包的那个文件。renderMode、depthAndStencil、useAngle 等
        /// 引擎必需项原样保留，Studio 不在此重复声明。
        /// </summary>
        private static bool WriteAppDescriptor(ActionScriptProject project, BuildPlan plan,
                                              ICollection<string> errors)
        {
            try
            {
                var doc = XDocument.Load(project.GetAppXmlPath());
                var root = doc.Root;
                if (root == null)
                {
                    errors.Add("[build] app descriptor has no root element");
                    return false;
                }

                var ns = root.GetDefaultNamespace();
                SetValue(root, ns, "filename", plan.AppName);

                var window = root.Element(ns + "initialWindow");
                if (window == null)
                {
                    window = new XElement(ns + "initialWindow");
                    root.Add(window);
                }
                SetValue(window, ns, "content", plan.AppName + ".swf");

                var dir = Path.GetDirectoryName(plan.AppDescriptorPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                doc.Save(plan.AppDescriptorPath);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add("[build] app descriptor: " + ex.Message);
                return false;
            }
        }

        /// <summary>空值表示"沿用描述符原值"，不覆盖。</summary>
        private static void SetValue(XElement parent, XNamespace ns, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var element = parent.Element(ns + name);
            if (element == null) parent.Add(new XElement(ns + name, value));
            else element.Value = value;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 命令行参数 ↔ 可编辑文本的互转。
    ///
    /// 构建窗口的平台标签页只负责生成参数列表，命令行标签页把参数列表渲染成一条
    /// 可直接粘贴到终端的命令；用户改完文本后由 <see cref="Parse"/> 还原成参数列表执行。
    /// 这样未来 ADT 新增参数/平台时，用户不必等 Studio 支持——直接改命令行即可。
    /// </summary>
    internal static class AdtCommandLine
    {
        /// <summary>把参数列表渲染成可粘贴的命令行（含空格/引号者加双引号）。</summary>
        public static string Format(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(arg));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 解析命令行为参数列表：支持双引号包裹、反斜杠转义引号；
        /// 单引号不参与转义——ADT 的 -define 取值本身可能含单引号，不能当引号吃掉。
        /// </summary>
        public static List<string> Parse(string commandLine)
        {
            var args = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine)) return args;

            var current = new StringBuilder();
            bool inQuotes = false;
            bool hasToken = false;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (inQuotes)
                {
                    if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                    hasToken = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (hasToken || current.Length > 0)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (hasToken || current.Length > 0) args.Add(current.ToString());
            return args;
        }

        private static string Quote(string arg)
        {
            if (arg.Length == 0) return "\"\"";
            if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }
    }

    /// <summary>
    /// ADT 打包参数构造：把一次暂存结果翻译成 ADT 参数列表。
    /// 每个目标形态一个方法，参数顺序与 adt -help 给出的语法一致。
    /// 平台差异全部收敛在这里（签名是否带 -tsa、是否需要 -platformsdk、是否接受 -arch）。
    /// </summary>
    internal static class AdtPackageCommand
    {
        /// <summary>
        /// 平台默认的 ADT 打包目标：桌面用 bundle（文件夹 + captive runtime，解压即用）；
        /// Android 用 apk-captive-runtime（自带运行时，设备无需预装 AIR）。
        /// iOS 尚未接通，返回空串——调用方据此提示该平台还不能打包。
        /// </summary>
        public static string DefaultTarget(string platform) => platform.ToLowerInvariant() switch
        {
            "windows" or "macos" or "linux" => "bundle",
            "android" => "apk-captive-runtime",
            _ => "",
        };

        /// <summary>按构建目标生成 ADT 参数；目标为空（该平台尚未接通）时返回空列表。</summary>
        public static List<string> ForPlan(BuildPlan plan, StagedBuild staged,
                                           CertificateSettings? certificate, string androidSdkPath)
        {
            var target = plan.Target.Length > 0 ? plan.Target : DefaultTarget(plan.Platform);
            switch (target)
            {
                case "aab":
                case "aab-debug":
                    return ForAndroidBundle(plan, staged, certificate, androidSdkPath);
                case "apk":
                case "apk-debug":
                case "apk-emulator":
                case "apk-captive-runtime":
                    return ForAndroidApk(plan, staged, certificate);
                case "bundle":
                case "cmdline":
                    return ForDesktopBundle(plan, staged, certificate);
                default:
                    return new List<string>();
            }
        }

        /// <summary>
        /// 桌面自包含产物（Windows .exe / macOS .app / Linux 可执行目录）：
        /// -target bundle 产出「文件夹 + captive runtime」，解压即用，不要求机器上装过 AIR 运行时。
        /// （-target native 产出的是共享运行时安装器，且需要 .air 作为输入，故不采用。）
        /// </summary>
        public static List<string> ForDesktopBundle(BuildPlan plan, StagedBuild staged,
                                                    CertificateSettings? certificate)
        {
            var args = new List<string> { "-package" };
            AddSigning(args, certificate, includeTsa: true);
            args.Add("-target");
            args.Add(plan.Target.Length > 0 ? plan.Target : "bundle");
            if (plan.Architecture.Length > 0)
            {
                args.Add("-arch");
                args.Add(plan.Architecture);
            }
            AppendInputs(args, plan, staged);
            return args;
        }

        /// <summary>
        /// Android APK（默认 apk-captive-runtime：内置 AIR 运行时，设备无需预装）。
        /// 不需要 -platformsdk —— AIR SDK 自带 lib/android 下的 aapt2 / d8 / apksigner。
        /// </summary>
        public static List<string> ForAndroidApk(BuildPlan plan, StagedBuild staged,
                                                 CertificateSettings? certificate)
        {
            // 注意参数顺序：Android 的 apk 语法是 -package -target … SIGNING_OPTIONS <output>，
            // 签名必须在 -target 之后（桌面 bundle 的语法反过来允许签名在前，两者不能共用一条模板）。
            var args = new List<string> { "-package" };
            args.Add("-target");
            args.Add(plan.Target.Length > 0 ? plan.Target : "apk-captive-runtime");
            if (plan.Architecture.Length > 0)
            {
                args.Add("-arch");
                args.Add(plan.Architecture);
            }
            AddSigning(args, certificate, includeTsa: false);
            AppendInputs(args, plan, staged);
            return args;
        }

        /// <summary>
        /// Android App Bundle：ADT 强制要求 -platformsdk 指向 Android SDK 根目录（该目标要用
        /// SDK 里的构建工具，与 apk 目标不同），且不接受 -arch —— 包含哪些架构由描述符的
        /// &lt;android&gt;&lt;buildArchitectures&gt; 决定。
        /// </summary>
        public static List<string> ForAndroidBundle(BuildPlan plan, StagedBuild staged,
                                                    CertificateSettings? certificate, string androidSdkPath)
        {
            // 与 apk 同为「-target 在签名之前」的顺序（见 ForAndroidApk 的说明）。
            var args = new List<string> { "-package" };
            args.Add("-target");
            args.Add(plan.Target.Length > 0 ? plan.Target : "aab");
            AddSigning(args, certificate, includeTsa: false);
            AppendInputs(args, plan, staged);
            if (androidSdkPath.Length > 0)
            {
                args.Add("-platformsdk");
                args.Add(androidSdkPath);
            }
            return args;
        }

        /// <summary>
        /// 输出包 + 描述符 + 暂存目录内容。-C &lt;dir&gt; . 把暂存目录内容原样收进包根，
        /// 使描述符里的 content 相对路径成立。
        /// </summary>
        private static void AppendInputs(List<string> args, BuildPlan plan, StagedBuild staged)
        {
            args.Add(plan.PackageOutputPath);
            args.Add(staged.AppDescriptorPath);
            args.Add("-C");
            args.Add(staged.StagingDirectory);
            args.Add(".");
        }

        /// <summary>
        /// 签名参数；证书未配置时不下发（由调用方用 <see cref="DevCertificate"/> 兜底）。
        ///
        /// <paramref name="includeTsa"/> 为 false 时不下发 -tsa：Android 目标（apk / aab）
        /// 用的是 apksigner，ADT 对它们直接拒绝该参数（报 "-tsa option not supported"），
        /// 时间戳是桌面签名的概念。
        /// </summary>
        private static void AddSigning(List<string> args, CertificateSettings? certificate, bool includeTsa)
        {
            if (certificate == null || certificate.IsEmpty) return;
            args.Add("-storetype");
            args.Add("PKCS12");
            args.Add("-keystore");
            args.Add(certificate.Keystore);
            if (certificate.StorePass.Length > 0)
            {
                args.Add("-storepass");
                args.Add(certificate.StorePass);
            }
            if (certificate.Alias.Length > 0)
            {
                args.Add("-alias");
                args.Add(certificate.Alias);
            }
            if (certificate.KeyPass.Length > 0)
            {
                args.Add("-keypass");
                args.Add(certificate.KeyPass);
            }
            // 留空表示交给 ADT 的默认 TSA；"none" 跳过时间戳（离线/内网构建）。
            if (includeTsa && certificate.TimestampUrl.Length > 0)
            {
                args.Add("-tsa");
                args.Add(certificate.TimestampUrl);
            }
        }
    }

    /// <summary>
    /// ADT 打包器：直接执行 java -jar &lt;sdk&gt;/lib/adt.jar，与 MxmlcCompiler 一致，
    /// 不经 bin/adt.bat（避免 cmd.exe 引号解析与控制台窗口闪现）。
    /// 输出实时回显到 Console 面板，失败不抛异常（避免上层 async void 崩溃）。
    /// </summary>
    internal sealed class AdtPackager
    {
        private readonly string _sdkPath;
        private readonly IEngineConsoleSink? _console;

        public AdtPackager(string sdkPath, IEngineConsoleSink? console = null)
        {
            _sdkPath = sdkPath;
            _console = console;
        }

        /// <summary>执行一次 ADT 调用。返回 true 表示退出码为 0。</summary>
        public bool Run(IReadOnlyList<string> args, string workingDirectory, out string error)
        {
            error = "";
            var jar = Path.Combine(_sdkPath, "lib", "adt.jar");
            if (string.IsNullOrWhiteSpace(_sdkPath) || !File.Exists(jar))
            {
                error = "Invalid AIR SDK path or missing adt.jar: " + _sdkPath;
                Emit(error, EngineConsoleLevel.Error);
                return false;
            }
            if (!Directory.Exists(workingDirectory))
            {
                error = "Working directory does not exist: " + workingDirectory;
                Emit(error, EngineConsoleLevel.Error);
                return false;
            }

            Emit("adt " + AdtCommandLine.Format(args), EngineConsoleLevel.Info);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "java",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-jar");
                psi.ArgumentList.Add(jar);
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) Emit(e.Data, EngineConsoleLevel.Info);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) Emit(e.Data, EngineConsoleLevel.Error);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    error = $"adt exited with code {proc.ExitCode}";
                    Emit(error, EngineConsoleLevel.Error);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Emit("Unable to run adt: " + ex.Message, EngineConsoleLevel.Error);
                return false;
            }
        }

        private void Emit(string text, EngineConsoleLevel level)
        {
            try { _console?.WriteLine(text, level); } catch { }
        }
    }
}

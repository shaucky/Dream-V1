using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Dream.Studio.Engine
{
    /// <summary>ADT 能力探测结果。</summary>
    internal sealed class AdtCapabilities
    {
        /// <summary>SDK 版本横幅（adt -help 首行，仅用于展示）。</summary>
        public string Version = "";

        /// <summary>该 SDK 支持的 -target 取值（去重升序）。</summary>
        public List<string> Targets = new();

        /// <summary>该 SDK 支持的 -arch 取值（去重，保持 help 中的顺序）。</summary>
        public List<string> Architectures = new();

        /// <summary>探测失败原因；为空表示探测成功。</summary>
        public string Error = "";
    }

    /// <summary>
    /// ADT 能力探测：向实际安装的 AIR SDK 问出可用 target 与 -arch 取值。
    ///
    /// 不在 Studio 里硬编码版本相关清单——ADT 各版本会增减 target（例如 air / native 的
    /// 存废、Android 的 aab 与 android-studio），硬编码必然随 SDK 升级失效。探测结果按
    /// SDK 路径缓存：每次探测要起一次 java，不值得反复付这个代价。
    ///
    /// 直接调用 lib/adt.jar 而非 bin/adt.bat，与 MxmlcCompiler 一致——避免经 cmd.exe
    /// 引发的引号解析问题与控制台窗口闪现。
    /// </summary>
    internal static class AdtCapabilitiesProbe
    {
        private static readonly Dictionary<string, AdtCapabilities> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        // -target ( a | b | c ) 形式；裸 -target foo 形式单独再扫一次。
        private static readonly Regex TargetGroup = new(@"-target\s*\(([^)]+)\)", RegexOptions.Compiled);
        private static readonly Regex TargetBare = new(@"-target\s+([A-Za-z][A-Za-z0-9_-]*)", RegexOptions.Compiled);
        private static readonly Regex ArchGroup = new(@"ARCH_OPTIONS\s*:\s*-arch\s*\(([^)]+)\)", RegexOptions.Compiled);

        /// <summary>探测并缓存指定 SDK 的 ADT 能力。</summary>
        public static AdtCapabilities Probe(string sdkPath)
        {
            if (string.IsNullOrWhiteSpace(sdkPath))
                return new AdtCapabilities { Error = "AIR SDK path is empty" };

            lock (Gate)
            {
                if (Cache.TryGetValue(sdkPath, out var cached)) return cached;
            }

            var result = Run(sdkPath);
            lock (Gate)
            {
                Cache[sdkPath] = result;
            }
            return result;
        }

        /// <summary>清除缓存（SDK 路径变更或升级后强制重新探测）。</summary>
        public static void Invalidate()
        {
            lock (Gate) Cache.Clear();
        }

        private static AdtCapabilities Run(string sdkPath)
        {
            var result = new AdtCapabilities();
            var jar = Path.Combine(sdkPath, "lib", "adt.jar");
            if (!File.Exists(jar))
            {
                result.Error = "missing " + jar;
                return result;
            }

            string output;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "java",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                psi.ArgumentList.Add("-jar");
                psi.ArgumentList.Add(jar);
                psi.ArgumentList.Add("-help");

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    result.Error = "unable to start java";
                    return result;
                }
                output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit();
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            result.Version = FirstNonEmptyLine(output);

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            foreach (Match m in TargetGroup.Matches(output))
            {
                foreach (var part in m.Groups[1].Value.Split('|'))
                {
                    var name = part.Trim();
                    // 分组里可能整体带 "-target"（如 "( -target air )?"），去掉后才是名字。
                    if (name.StartsWith("-target", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring("-target".Length).Trim();
                    if (name.Length == 0 || name.Contains(' ') || name.Contains('<')) continue;
                    if (targets.Add(name)) ordered.Add(name);
                }
            }
            foreach (Match m in TargetBare.Matches(output))
            {
                var name = m.Groups[1].Value;
                if (targets.Add(name)) ordered.Add(name);
            }
            ordered.Sort(StringComparer.OrdinalIgnoreCase);
            result.Targets = ordered;

            var archMatch = ArchGroup.Match(output);
            if (archMatch.Success)
            {
                foreach (var part in archMatch.Groups[1].Value.Split('|'))
                {
                    var name = part.Trim();
                    if (name.Length > 0 && !name.Contains('<')) result.Architectures.Add(name);
                }
            }

            if (result.Targets.Count == 0)
                result.Error = "no -target found in adt -help output";
            return result;
        }

        private static string FirstNonEmptyLine(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return "";
        }
    }
}

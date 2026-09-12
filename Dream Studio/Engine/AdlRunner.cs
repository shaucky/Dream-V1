using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
#if WINDOWS
using Dream.Studio.Engine.Native;
#endif

namespace Dream.Studio.Engine
{
    /// <summary>AIR 应用运行器抽象。</summary>
    internal interface IAirAppRunner : IDisposable
    {
        /// <summary>启动应用并以阻塞方式等待其主窗口句柄就绪，返回句柄（失败为 IntPtr.Zero）。
        /// extraArgs 作为 ADL 启动参数透传给应用（如通信端口）。</summary>
        IntPtr Run(ActionScriptProject project, IEnumerable<string>? extraArgs = null);

        void Stop();
    }

    /// <summary>
    /// 使用 AIR SDK 的 ADL（AIR Debug Launcher）从源码运行 debug 版应用。
    /// ADL 的 rootDirectory 设为 SWF 输出目录，使应用描述符中的 &lt;content&gt;
    /// 正确解析到编译产物。
    /// </summary>
    internal sealed class AdlRunner : IAirAppRunner
    {
        private readonly string _sdkPath;
        private readonly IEngineConsoleSink? _console;
        private Process? _process;
        private readonly JobObjectGuard _jobGuard = new();
        // 区分"我们主动 Kill"与"ADL 自身/外部退出"；每次 Run() 复位（只描述当前这次运行）。
        private volatile bool _stoppingByUs;

        public AdlRunner(string sdkPath, IEngineConsoleSink? console = null)
        {
            _sdkPath = sdkPath;
            _console = console;
        }

        public IntPtr Run(ActionScriptProject project, IEnumerable<string>? extraArgs = null)
        {
            var appXml = project.GetAppXmlPath();
            // ADL 的 rootDirectory 即 app:/ 根；content 路径相对它解析。
            // 必须是应用描述符所在目录，且 SWF 也编译到该目录（见 asconfig output）。
            var rootDir = Path.GetDirectoryName(appXml) ?? project.RootDir;

            var adl = Path.Combine(_sdkPath, "bin", "adl.exe");
            if (string.IsNullOrWhiteSpace(_sdkPath) || !File.Exists(adl))
            {
                Log($"[adl] AIR SDK 路径无效或缺少 adl.exe：{_sdkPath}");
                return IntPtr.Zero;
            }

            Log($"[adl] 启动：{adl}  appXml={appXml}  rootDir={rootDir} extraArgs={string.Join(",", extraArgs ?? Array.Empty<string>())}");
            // ADL 是 GUI 应用：UseShellExecute=false + 重定向 stdio 用于错误捕获，
            // CreateNoWindow=true 抑制控制台窗口（详见下方 psi 注释）。
            var psi = new ProcessStartInfo
            {
                FileName = adl,
                WorkingDirectory = project.RootDir,
                UseShellExecute = false,
                // UseShellExecute=false 下 CreateNoWindow=true 抑制子进程控制台窗口。
                // ADL 本身是 GUI 应用，但重定向 stdio 时 .NET 默认可能关联控制台，
                // 这里显式关闭，避免黑色 cmd 窗口闪现/残留遮挡 Scene 面板。
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add(appXml);
            psi.ArgumentList.Add(rootDir);
            // ADL 文档语法：adl app.xml rootDirectory [-- arg1 arg2 ...]
            // 必须以独立 "--" 分隔应用参数；否则形如 --comm-port=xxx 的 token 会被
            // ADL 当作其自身选项解析，导致 "initial content not found"（ExitCode=8）。
            if (extraArgs != null)
            {
                using var en = extraArgs.GetEnumerator();
                if (en.MoveNext())
                {
                    psi.ArgumentList.Add("--");
                    do { psi.ArgumentList.Add(en.Current); } while (en.MoveNext());
                }
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process = process;
            // 每次运行各自记一份：上一次是我们 Kill 的，不代表这一次也是；不复位的话
            // 第一次 Stop() 之后所有退出都会被记成 byUs=True，日志里再也看不出异常退出。
            _stoppingByUs = false;
            var stdoutBuf = new System.Text.StringBuilder();
            var stderrBuf = new System.Text.StringBuilder();
            // 实时推送 ADL 输出到 Console（如有 sink），同时累积到 buffer 供 Exited 落盘。
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stdoutBuf.AppendLine(e.Data);
                _console?.WriteLine(e.Data, EngineConsoleLevel.Info);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stderrBuf.AppendLine(e.Data);
                _console?.WriteLine(e.Data, EngineConsoleLevel.Error);
            };
            process.Exited += (_, _) =>
            {
                // 取捕获到的 process 而不是 _process 字段：重启时字段已指向新进程，
                // 上一次运行迟到的事件会读到新进程的退出码与标志。
                // 上一次运行被 Stop() 淘汰时不再重复记，那次 Kill 已由 Stop() 记录。
                if (!ReferenceEquals(_process, process)) return;
                // _stoppingByUs=true 表示是我们 Stop() 主动 Kill 的（正常关闭）；
                // =false 表示 ADL 自身崩溃或被外部终止——这才是需要排查的异常退出。
                Log($"[adl] 进程退出 ExitCode={process.ExitCode} byUs={_stoppingByUs}");
                if (stdoutBuf.Length > 0) Log("[adl stdout] " + stdoutBuf.ToString().TrimEnd());
                if (stderrBuf.Length > 0) Log("[adl stderr] " + stderrBuf.ToString().TrimEnd());
            };
            process.Start();
            // 尽早把 ADL 进程加入 Job Object：Studio 崩溃/被 VS 停止时内核自动终止 ADL，
            // 避免 ADL 残留占用 .workspace/Project 导致下次启动无法删除。
            _jobGuard.Assign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return WaitForMainWindow(process);
        }

        private static IntPtr WaitForMainWindow(Process process)
        {
#if WINDOWS
            // HARMAN ADL 启动时先弹 splash（标题固定 'Adobe AIR'），真正的应用窗口随后出现，
            // 且可能在子进程。不以尺寸判 splash（splash 尺寸可能随版本/主题变化），
            // 仅按标题排除 'Adobe AIR'，从剩余候选选面积最大：
            //   - 不依赖应用标题（游戏引擎实际项目名未必等于 app.xml <filename>）；
            //   - 要求同一句柄连续多轮稳定存在才接受，避免抓到过渡窗口；
            //   - 超时仍未找到真窗口时，回退到曾见的最大候选（含 splash），便于诊断。
            const int pollMs = 300;
            const int stableNeed = 4;          // 连续 4 次（约 1.2s）同一句柄才视为稳定
            const int totalIters = 30_000 / pollMs;

            IntPtr chosen = IntPtr.Zero;
            int stableCount = 0;
            Win32.CandidateWindow fallback = default;   // 曾见的最大候选（含 splash）
            bool haveFallback = false;
            string? lastLoggedTitles = null;

            for (int i = 0; i < totalIters; i++)
            {
                if (process.HasExited) break;

                var pids = Win32.GetProcessTreePids(process.Id);
                var candidates = Win32.EnumerateCandidateWindows(pids);

                // 候选标题集合变化时记录一次，捕捉 splash→应用窗口的切换。
                var titles = candidates.Count == 0 ? "" :
                    string.Join("|", candidates.ConvertAll(c => $"'{c.Title}'"));
                if (titles != lastLoggedTitles)
                {
                    LogCandidates(i, candidates);
                    lastLoggedTitles = titles;
                }

                // 维护 fallback：曾见的最大候选（不论是否 splash）。
                foreach (var c in candidates)
                {
                    if (!haveFallback || c.Rect.Area > fallback.Rect.Area)
                    { fallback = c; haveFallback = true; }
                }

                var best = PickBest(candidates);
                if (best.Handle == IntPtr.Zero)
                {
                    stableCount = 0;
                    chosen = IntPtr.Zero;
                }
                else if (best.Handle == chosen && Win32.IsStillWindow(chosen))
                {
                    stableCount++;
                }
                else
                {
                    chosen = best.Handle;
                    stableCount = 1;
                }

                if (stableCount >= stableNeed && chosen != IntPtr.Zero)
                {
                    Log($"ADL 主窗口就绪：hwnd=0x{chosen.ToInt64():X} title='{best.Title}' {best.Rect.Width}x{best.Rect.Height}");
                    return chosen;
                }

                Thread.Sleep(pollMs);
            }

            // 超时回退：若有候选但无匹配预期标题者，返回最大候选以便嵌入器记录诊断。
            if (chosen == IntPtr.Zero && haveFallback)
            {
                Log($"ADL 未找到非 splash 窗口，回退到最大候选：hwnd=0x{fallback.Handle.ToInt64():X} title='{fallback.Title}' {fallback.Rect.Width}x{fallback.Rect.Height}");
                return fallback.Handle;
            }

            Log($"ADL 主窗口未稳定就绪（chosen=0x{chosen.ToInt64():X}）。");
            return IntPtr.Zero;
#else
            return IntPtr.Zero;
#endif
        }

#if WINDOWS
        /// <summary>选最佳候选：排除 'Adobe AIR' splash 后取面积最大；过小窗口视为噪声忽略。
        /// 不依赖应用标题：实际游戏引擎项目名未必等于 app.xml &lt;filename&gt;，仅靠排除 splash 识别。</summary>
        private static Win32.CandidateWindow PickBest(List<Win32.CandidateWindow> candidates)
        {
            Win32.CandidateWindow best = default;
            bool any = false;
            foreach (var c in candidates)
            {
                if (c.Title == "Adobe AIR") continue;        // 不以尺寸判 splash，按标题排除
                if (c.Rect.Area < 32 * 32) continue;         // 仅过滤明显噪声，不用作 splash 判据
                if (!any || c.Rect.Area > best.Rect.Area) { best = c; any = true; }
            }
            return best;
        }

        private static void LogCandidates(int iter, List<Win32.CandidateWindow> candidates)
        {
            if (candidates.Count == 0)
            {
                Log($"[poll {iter}] 无候选");
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append($"[poll {iter}] 候选 {candidates.Count}:");
            foreach (var c in candidates)
                sb.Append($" 0x{c.Handle.ToInt64():X}(pid={c.ProcessId},{c.Rect.Width}x{c.Rect.Height},'{c.Title}')");
            Log(sb.ToString());
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
#endif

        public void Stop()
        {
            // 标记为主动停止，供 Exited 事件区分正常关闭 vs 异常退出。
            // 记录调用栈帮助定位是否有意外调用路径（非 OnClosed）触发 Stop。
            _stoppingByUs = true;
            if (_process != null && !_process.HasExited)
            {
                Log($"[adl] Stop() 调用，Kill 进程树。调用栈：{Environment.StackTrace}");
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            _process?.Dispose();
            _process = null;
        }

        public void Dispose()
        {
            Stop();
            _jobGuard.Dispose();
        }
    }
}

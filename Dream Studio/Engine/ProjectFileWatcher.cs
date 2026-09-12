using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 监控项目文件变更，防抖后触发事件。按扩展名（或具体文件名）过滤，并忽略派生数据目录
    /// （.dream）——否则合图写缓存会反过来再次触发自己。
    /// 基于 FileSystemWatcher（Windows ReadDirectoryChangesW 异步 IO，空闲零 CPU），
    /// 内置 500ms 防抖（用户连续保存只触发一次）与错误重连（FSW 内部 buffer 溢出后自动重建）。
    /// 事件在后台线程触发，订阅方自行封送到需要的线程。
    /// </summary>
    internal sealed class ProjectFileWatcher : IDisposable
    {
        /// <summary>防抖窗口：最后一次变更后等待此毫秒数再触发，合并连续改动。</summary>
        private const int DebounceMs = 500;

        /// <summary>FSW 出错后重建前的等待，避免循环抛错。</summary>
        private const int ReconnectDelayMs = 2000;

        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly HashSet<string> _extensions;
        private readonly HashSet<string> _fileNames;
        private readonly string _filter;
        private CancellationTokenSource? _debounceCts;
        private bool _disposed;
        // 暂停期间变更不触发防抖，但记录是否发生变更，Resume 时若为 true 则补触发一次。
        private bool _paused;
        private bool _changedDuringPause;

        /// <param name="extensions">关注的扩展名（含点，如 ".as"）；空集合表示关注全部文件。</param>
        /// <param name="fileNames">限定到具体文件名（如 app 描述符）；非空时只按文件名匹配，不再看扩展名。</param>
        public ProjectFileWatcher(IReadOnlyCollection<string> extensions,
                                  IReadOnlyCollection<string>? fileNames = null)
        {
            _extensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            _fileNames = new HashSet<string>(fileNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            // 过滤能交给 FSW 就交给它（事件更少）：单一文件名 / 单一扩展名都能直接当过滤器，
            // 多值时用通配后自行筛选。
            _filter = _fileNames.Count > 0
                ? (_fileNames.Count == 1 ? _fileNames.First() : "*.*")
                : _extensions.Count == 1 ? "*" + _extensions.First() : "*.*";
        }

        /// <summary>防抖后触发（参数为空，订阅方按需自行读取当前状态）。</summary>
        public event Action? Changed;

        /// <summary>为每个 source-path 绝对目录创建 FSW 监听（含子目录）。</summary>
        public void Start(IEnumerable<string> sourceRootAbsDirs)
        {
            foreach (var dir in sourceRootAbsDirs)
                StartOne(dir);
        }

        private void StartOne(string dir)
        {
            if (!Directory.Exists(dir)) return;
            var fsw = new FileSystemWatcher(dir, _filter)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                             | NotifyFilters.Size | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024, // 64KB，减少大量改动时的 buffer 溢出
            };
            fsw.Changed += OnTick;
            fsw.Created += OnTick;
            fsw.Renamed += OnTick;
            fsw.Deleted += OnTick;
            fsw.Error += OnError;
            fsw.EnableRaisingEvents = true;
            _watchers.Add(fsw);
        }

        private void OnTick(object sender, FileSystemEventArgs e)
        {
            if (!IsTracked(e.FullPath)) return;
            if (_paused) { _changedDuringPause = true; return; }
            ScheduleDebounce();
        }

        /// <summary>是否关注该文件：命中扩展名（或文件名），且不在派生数据目录（.dream）内。</summary>
        private bool IsTracked(string path)
        {
            var matched = _fileNames.Count > 0
                ? _fileNames.Contains(Path.GetFileName(path))
                : _extensions.Count == 0 || _extensions.Contains(Path.GetExtension(path));
            if (!matched) return false;

            var marker = Path.DirectorySeparatorChar + EnginePaths.ProjectDataDirectoryName
                       + Path.DirectorySeparatorChar;
            return path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>暂停触发：丢弃待触发的防抖。暂停前后发生的变更都会被记录，Resume 时补触发一次。</summary>
        public void Pause()
        {
            _paused = true;
            // 已排队但尚未触发的防抖也要记为"有变更"，否则暂停期间会丢掉这次改动。
            // 只在确实有排队变更时置位：直接赋值会把暂停期间已经记下的变更标记抹掉，
            // 于是 Resume 不补触发，重命名流程里就会吞掉一次热重载。
            var pending = Interlocked.Exchange(ref _debounceCts, null);
            if (pending != null) _changedDuringPause = true;
            try { pending?.Cancel(); } catch { }
        }

        /// <summary>恢复触发：若暂停期间有变更则立即触发一次防抖。</summary>
        public void Resume()
        {
            _paused = false;
            if (_changedDuringPause)
            {
                _changedDuringPause = false;
                ScheduleDebounce();
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            // FSW buffer 溢出或底层句柄异常：当前 FSW 已失效，移除并延迟重建。
            if (_disposed) return;
            if (sender is FileSystemWatcher dead)
            {
                Detach(dead);
                _ = Task.Delay(ReconnectDelayMs).ContinueWith(_ =>
                {
                    if (_disposed) return;
                    StartOne(dead.Path);
                });
            }
        }

        /// <summary>防抖：取消上次待触发，重新计时。并发安全通过 Interlocked.Exchange 丢弃旧 CTS。</summary>
        private void ScheduleDebounce()
        {
            var prev = Interlocked.Exchange(ref _debounceCts, new CancellationTokenSource());
            try { prev?.Cancel(); } catch { }
            var cts = _debounceCts;
            if (cts == null) return;
            _ = Task.Delay(DebounceMs, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                try { Changed?.Invoke(); } catch { }
            }, TaskScheduler.Default);
        }

        private void Detach(FileSystemWatcher fsw)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Changed -= OnTick;
            fsw.Created -= OnTick;
            fsw.Renamed -= OnTick;
            fsw.Deleted -= OnTick;
            fsw.Error -= OnError;
            _watchers.Remove(fsw);
            try { fsw.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var fsw in _watchers.ToArray())
                Detach(fsw);
            try { _debounceCts?.Cancel(); } catch { }
            _debounceCts?.Dispose();
        }
    }
}

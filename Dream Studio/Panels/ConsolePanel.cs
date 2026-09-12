using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Dream.Studio.Engine;

namespace Dream.Studio.Panels
{
    /// <summary>
    /// Console 面板：展示 ADL 进程输出（stdout/stderr）与 Engine 上报的日志。
    /// 显式实现 <see cref="IEngineConsoleSink"/>（DIP：作为 Engine 层的输出接收者，
    /// 接口为 internal，public 类通过显式实现绕过可见性约束）。
    /// 为降低高频输出对 UI 的阻塞，采用行列表 + 批量刷新：任意线程调用 WriteLine 只入队，
    /// UI 线程按 100ms 或单次上限批量处理，避免每次输出都触发整段文本重排。
    /// 限制最大行数避免无限增长。
    /// </summary>
    public sealed class ConsolePanel : UserControl, IEngineConsoleSink
    {
        private readonly SelectableTextBlock _text;
        private readonly ScrollViewer _scroll;

        // 待刷新队列：WriteLine 从任意线程调用时入队，UI 线程批量消费。
        private readonly Queue<string> _pending = new();
        private readonly Queue<EngineConsoleLevel> _pendingLevels = new();

        // 已渲染行（按行存储，截断时头部 O(1) 移除）。
        private readonly List<string> _lines = new();

        private const int MaxLines = 500;
        private const int BatchFlushMs = 100;
        private const int MaxBatchPerFlush = 500;

        // 防抖计时器：控制批量刷新频率。
        private DispatcherTimer? _flushTimer;

        public ConsolePanel()
        {
            _text = new SelectableTextBlock
            {
                FontFamily = FontFamily.Parse("Consolas,Menlo,monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Content = _scroll = new ScrollViewer
            {
                Content = _text,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }

        // 显式接口实现：internal 接口由 public 类承载，仅通过接口引用访问。
        void IEngineConsoleSink.WriteLine(string text, EngineConsoleLevel level)
            => WriteLine(text, level);

        /// <summary>追加一行（可从任意线程调用，内部切到 UI 线程批量刷新）。</summary>
        private void WriteLine(string text, EngineConsoleLevel level)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_pending)
            {
                _pending.Enqueue(text);
                _pendingLevels.Enqueue(level);
                if (_flushTimer == null)
                {
                    _flushTimer = new DispatcherTimer(
                        TimeSpan.FromMilliseconds(BatchFlushMs),
                        DispatcherPriority.Background,
                        (_, _) => Flush())
                    {
                        IsEnabled = true
                    };
                }
            }
            // 高优先级触发一次刷新调度：降低输入延迟。
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }

        /// <summary>
        /// UI 线程批量刷新：将队列中的行追加到 TextBlock，控制最大行数并决定滚动。
        /// </summary>
        private void Flush()
        {
            List<string> batch;
            List<EngineConsoleLevel> batchLevels;
            lock (_pending)
            {
                if (_pending.Count == 0) return;
                var count = Math.Min(_pending.Count, MaxBatchPerFlush);
                batch = new List<string>(count);
                batchLevels = new List<EngineConsoleLevel>(count);
                while (count-- > 0)
                {
                    batch.Add(_pending.Dequeue());
                    batchLevels.Add(_pendingLevels.Dequeue());
                }
                // 队列仍有数据时继续定时刷新；否则停掉计时器减少资源占用。
                if (_pending.Count == 0)
                {
                    _flushTimer?.Stop();
                    _flushTimer = null;
                }
            }

            // 追加前判断用户是否接近底部：是则追加后跟随滚动，否则保持原位。
            var wasNearBottom = IsNearBottom();

            var sb = new StringBuilder();
            for (int i = 0; i < batch.Count; i++)
            {
                var text = batch[i];
                var level = batchLevels[i];
                var prefix = level switch
                {
                    EngineConsoleLevel.Error => "[error] ",
                    EngineConsoleLevel.Warning => "[warning] ",
                    _ => "",
                };
                var line = prefix + text;
                _lines.Add(line);
                sb.AppendLine(line);
            }

            // 超过上限时直接移除头部行，避免字符串 IndexOf/Remove 的 O(n²) 开销。
            while (_lines.Count > MaxLines)
                _lines.RemoveAt(0);

            // 当前实现仍重新赋值整段文本；
            // 由于已按 100ms/MaxBatchPerFlush 限流，高频输出不再阻塞交互。
            if (_lines.Count == batch.Count)
            {
                // 首次有内容：直接重建全部文本。
                _text.Text = BuildText();
            }
            else
            {
                // 追加增量文本，避免 Avalonia 重新解析全量文本。
                _text.Text += sb.ToString();
                // 头部行被截断后需要重建，否则文本与 _lines 不一致。
                var expectedText = BuildText();
                if (_text.Text.Length > expectedText.Length * 2 + 1024)
                    _text.Text = expectedText;
            }

            if (wasNearBottom)
                _scroll.ScrollToEnd();
        }

        private string BuildText()
        {
            var sb = new StringBuilder();
            foreach (var line in _lines)
                sb.AppendLine(line);
            return sb.ToString();
        }

        /// <summary>视口距底部小于阈值时视为"接近底部"。内容不足一屏时返回 true。</summary>
        private bool IsNearBottom()
        {
            const double Threshold = 40;
            var extent = _scroll.Extent;
            var viewport = _scroll.Viewport;
            if (viewport.Height <= 0 || extent.Height <= viewport.Height) return true;
            return _scroll.Offset.Y + viewport.Height >= extent.Height - Threshold;
        }
    }
}

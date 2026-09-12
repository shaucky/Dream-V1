using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Dream.Studio.Engine
{
    /// <summary>
    /// Scene 面板内的嵌入宿主表面：Avalonia 布局驱动的原生覆盖区域。
    /// 计算自身相对所在顶层窗口（主窗口或浮动面板窗口）客户区的像素矩形，
    /// 并在尺寸/位置/所在窗口变化时通知嵌入器。
    /// 嵌入前显示占位背景，嵌入后由子窗口覆盖。
    /// </summary>
    internal sealed class SceneSurface : Border, IEmbedSurface
    {
        private IntPtr _host = IntPtr.Zero;
        private PixelRect _clientBounds;
        private PixelRect _screenBounds;
        private TopLevel? _trackedTopLevel;

        // 启动占位层：编译/启动引擎期间显示状态提示与进度动画；
        // ADL 窗口嵌入后由子窗口覆盖，SetStatus(null) 隐藏。
        private readonly StackPanel _overlay;
        private readonly TextBlock _statusText;

        public IntPtr HostHandle => _host;
        public PixelRect ClientBounds => _clientBounds;
        public PixelRect ScreenBounds => _screenBounds;
        public event EventHandler? BoundsChanged;

        /// <summary>SceneSurface 是否在可视化树中（面板未关闭）。脱离树时应隐藏 Engine 窗口。</summary>
        public bool IsInTree { get; private set; }

        public SceneSurface()
        {
            Background = Brushes.Black;

            _statusText = new TextBlock
            {
                Text = "Starting engine…",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8)),
                FontSize = 12,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            };
            _overlay = new StackPanel
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Spacing = 12,
                Children =
                {
                    new ProgressBar
                    {
                        IsIndeterminate = true,
                        Width = 160,
                        Height = 4,
                    },
                    _statusText,
                },
            };
            Child = _overlay;

            LayoutUpdated += OnLayoutUpdated;
            AttachedToVisualTree += OnAttached;
            DetachedFromVisualTree += OnDetached;
        }

        /// <summary>更新启动状态提示。null 表示引擎已就绪（ADL 已嵌入/连接），隐藏占位层。</summary>
        public void SetStatus(string? text)
        {
            if (text == null)
            {
                _overlay.IsVisible = false;
            }
            else
            {
                _statusText.Text = text;
                _overlay.IsVisible = true;
            }
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            IsInTree = true;
            Recompute();
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            IsInTree = false;
            // 脱离可视化树：清零边界并通知嵌入器隐藏 Engine 窗口。
            if (_screenBounds != default)
            {
                _clientBounds = default;
                _screenBounds = default;
                _host = IntPtr.Zero;
                BoundsChanged?.Invoke(this, EventArgs.Empty);
            }
            // 解除对旧 TopLevel 的 PositionChanged 订阅。
            UntrackTopLevel();
        }

        private void OnLayoutUpdated(object? sender, EventArgs e) => Recompute();

        /// <summary>外部（如宿主窗口 PositionChanged）主动触发重算。
        /// 拖动窗口时 Avalonia 不一定失效布局，LayoutUpdated 不触发，
        /// 但 ClientToScreen 结果已变，需外部驱动重算以更新 ScreenBounds。</summary>
        public void RefreshScreenBounds() => Recompute();

        private void Recompute()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;

            // TopLevel 变化（主窗口 ↔ 浮动窗口）：重新订阅 PositionChanged，
            // 使浮动面板拖动时也能驱动重算。
            if (top != _trackedTopLevel)
            {
                UntrackTopLevel();
                _trackedTopLevel = top;
                if (top is Window w)
                {
                    w.PositionChanged += OnTopLevelPositionChanged;
                }
            }

            _host = top.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (_host == IntPtr.Zero) return;

            var transform = this.TransformToVisual(top);
            if (!transform.HasValue) return;

            var origin = transform.Value.Transform(new Point(0, 0));
            var size = Bounds.Size;
            var scale = top.RenderScaling;
            var client = new PixelRect(
                (int)Math.Round(origin.X * scale),
                (int)Math.Round(origin.Y * scale),
                (int)Math.Round(size.Width * scale),
                (int)Math.Round(size.Height * scale));

            // 屏幕坐标 = 宿主客户区原点的屏幕坐标 + 客户区相对坐标。
            var screen = client;
#if WINDOWS
            if (_host != IntPtr.Zero)
            {
                var pt = new Native.Win32.POINT { X = 0, Y = 0 };
                if (Native.Win32.ClientToScreen(_host, ref pt))
                {
                    screen = new PixelRect(pt.X + client.X, pt.Y + client.Y, client.Width, client.Height);
                }
            }
#endif

            if (client != _clientBounds || screen != _screenBounds)
            {
                _clientBounds = client;
                _screenBounds = screen;
                BoundsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnTopLevelPositionChanged(object? sender, PixelPointEventArgs e) => Recompute();

        private void UntrackTopLevel()
        {
            if (_trackedTopLevel is Window w)
            {
                w.PositionChanged -= OnTopLevelPositionChanged;
            }
            _trackedTopLevel = null;
        }
    }
}

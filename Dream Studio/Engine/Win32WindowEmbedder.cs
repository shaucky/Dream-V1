using System;
using System.IO;
using System.Threading;
using Avalonia;
#if WINDOWS
using Dream.Studio.Engine.Native;
#endif

namespace Dream.Studio.Engine
{
    /// <summary>
    /// 嵌入宿主表面：暴露父窗口句柄与目标矩形，并在几何变化时通知。
    /// ClientBounds 为相对父客户区的像素矩形（用于真嵌入 SetParent）；
    /// ScreenBounds 为屏幕坐标像素矩形（用于跟踪几何 SetWindowPos 顶层窗口）。
    /// </summary>
    internal interface IEmbedSurface
    {
        IntPtr HostHandle { get; }
        PixelRect ClientBounds { get; }
        PixelRect ScreenBounds { get; }
        event EventHandler? BoundsChanged;

        /// <summary>宿主窗口位置变化（拖动）时主动触发重算屏幕坐标。
        /// Avalonia 拖动不失效布局、不触发 LayoutUpdated，需外部驱动。</summary>
        void RefreshScreenBounds();
    }

    /// <summary>外部窗口嵌入器抽象：把给定子窗口嵌入到 <see cref="IEmbedSurface"/> 并随其拉伸。</summary>
    internal interface IWindowEmbedder : IDisposable
    {
        void Embed(IntPtr childWindow, IEmbedSurface surface);

        /// <summary>同步子窗口可见性（用于宿主最小化/恢复）。默认实现可空操作。</summary>
        void SetVisible(bool visible) { }
    }

#if WINDOWS
    /// <summary>
    /// Win32 实现：通过 SetParent 把子窗口重定为 Scene 面板所在主窗口的子窗口，
    /// 剥离标题/边框并按面板客户区拉伸；面板几何变化时同步 SetWindowPos。
    /// </summary>
    internal sealed class Win32WindowEmbedder : IWindowEmbedder
    {
        private IntPtr _child;
        private IEmbedSurface? _surface;

        public void Embed(IntPtr childWindow, IEmbedSurface surface)
        {
            if (childWindow == IntPtr.Zero) { Log("Embed 跳过：childWindow=0"); return; }
            _child = childWindow;
            _surface = surface;

            // Opened 触发时 SceneSurface 可能尚未进入可视化树 → HostHandle 为零。
            // 短暂重试，等 Avalonia 完成首帧布局。
            IntPtr host = IntPtr.Zero;
            for (int i = 0; i < 50; i++)
            {
                host = surface.HostHandle;
                if (host != IntPtr.Zero) break;
                Thread.Sleep(50);
            }
            if (host == IntPtr.Zero) { Log("Embed 失败：HostHandle 持续为 0（SceneSurface 未进入可视化树）"); return; }

            Log($"Embed 开始：child=0x{childWindow.ToInt64():X} host=0x{host.ToInt64():X}");

            var prevParent = Win32.SetParent(childWindow, host);
            var newParent = Win32.GetParent(childWindow);
            Log($"SetParent: prev=0x{prevParent.ToInt64():X} now=0x{newParent.ToInt64():X} expected=0x{host.ToInt64():X}");

            // 剥离顶层窗口装饰，转为子窗口风格。
            var style = Win32.GetWindowLongPtr(childWindow, Win32.GWL_STYLE).ToInt64();
            style &= ~(Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_THICKFRAME |
                       Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | Win32.WS_SYSMENU);
            style |= Win32.WS_CHILD | Win32.WS_VISIBLE;
            Win32.SetWindowLongPtr(childWindow, Win32.GWL_STYLE, new IntPtr(style));

            var ex = Win32.GetWindowLongPtr(childWindow, Win32.GWL_EXSTYLE).ToInt64();
            ex &= ~(Win32.WS_EX_WINDOWEDGE | Win32.WS_EX_CLIENTEDGE | Win32.WS_EX_APPWINDOW);
            Win32.SetWindowLongPtr(childWindow, Win32.GWL_EXSTYLE, new IntPtr(ex));

            ApplyBounds();

            surface.BoundsChanged += OnBoundsChanged;
            Log($"Embed 完成：bounds={surface.ClientBounds}");
        }

        private void OnBoundsChanged(object? sender, EventArgs e) => ApplyBounds();

        private void ApplyBounds()
        {
            if (_child == IntPtr.Zero || _surface == null) return;
            var b = _surface.ClientBounds;
            if (b.Width <= 0 || b.Height <= 0) return;
            Win32.SetWindowPos(_child, IntPtr.Zero, b.X, b.Y, b.Width, b.Height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
        }

        public void Dispose()
        {
            if (_surface != null)
                _surface.BoundsChanged -= OnBoundsChanged;
            _surface = null;
            _child = IntPtr.Zero;
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
#endif
}

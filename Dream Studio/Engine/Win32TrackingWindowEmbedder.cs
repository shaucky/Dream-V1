using System;
using System.IO;
using System.Threading;
using Avalonia;
#if WINDOWS
using Dream.Studio.Engine.Native;
#endif

namespace Dream.Studio.Engine
{
#if WINDOWS
    /// <summary>
    /// 跟踪几何嵌入器：ADL 窗口保持独立顶层窗口，但用 SetWindowPos 把它的屏幕坐标
    /// 与尺寸持续对齐到 Scene 面板在屏幕上的矩形，视觉上覆盖在面板区域。
    ///
    /// 用于 SetParent 真嵌入被运行时拒绝（HARMAN AIR）时的回退方案：
    /// - 剥离边框/标题，避免独立窗口的装饰出现在面板上方；
    /// - WS_EX_TOOLWINDOW 隐藏任务栏项；WS_EX_NOACTIVATE 不抢焦点；
    /// - z-order 通过 owner 关系维持（GWLP_HWNDPARENT，不重定为 WS_CHILD）：
    ///   owned 始终在 owner 之上，但不挡其他应用；
    /// - owner/样式仅在首次设置一次，不与 runtime 反复争夺（之前每轮清 Topmost 造成拉锯，
    ///   实测无必要——owner 关系已足够维持 z-order）；
    /// - 位置/尺寸由 BoundsChanged + 高频定时器持续同步（拖动、布局变化）；
    /// - Studio 最小化时同步隐藏，恢复时同步显示。
    /// </summary>
    internal sealed class Win32TrackingWindowEmbedder : IWindowEmbedder
    {
        private IntPtr _child;
        private IEmbedSurface? _surface;
        private Timer? _assertTimer;
        private int _assertCount;       // 重申次数，用于控制诊断日志频率
        private IntPtr _lastHost;       // 上次设置的 owner 宿主句柄，用于检测面板跨窗口迁移
        private bool _hiddenByDetach;   // 因面板脱离可视化树而隐藏（区别于最小化隐藏）
        private bool _zOrderDirty = true; // 需主动校正 z-order（首次/owner 变化后）

        public void Embed(IntPtr childWindow, IEmbedSurface surface)
        {
            if (childWindow == IntPtr.Zero) { Log("Tracking Embed 跳过：childWindow=0"); return; }
            _child = childWindow;
            _surface = surface;

            // 等待 SceneSurface 进入可视化树并获得屏幕坐标。
            for (int i = 0; i < 50; i++)
            {
                if (surface.HostHandle != IntPtr.Zero && surface.ScreenBounds.Width > 0) break;
                Thread.Sleep(50);
            }

            // 入口立即设 owner：splash 期间用户与 Studio 交互会让 Studio 成为前景窗口，
            // 若此时 Engine 尚未设 owner，Engine 会落到 Studio 下方。设 owner 后即便
            // Studio 被激活，Engine 也始终在 Studio 之上（owned 窗口的 z-order 约束）。
            ApplyOwner();
            ApplyStyle();

            // 首次同步位置（AssertWindow 会处理 owner 变化与可见性）。
            AssertWindow();

            surface.BoundsChanged += OnBoundsChanged;
            // 高频同步位置：dueTime=0 立即首次执行（尽快贴位置），
            // 之后 50ms 保持拖动/缩放时视觉紧跟。样式只设一次，不拉锯。
            _assertTimer = new Timer(_ => AssertWindow(), null, 0, 50);

            Log($"Tracking Embed 完成：screen={surface.ScreenBounds} host=0x{surface.HostHandle.ToInt64():X}");
        }

        private void OnBoundsChanged(object? sender, EventArgs e) => AssertWindow();

        /// <summary>首次设置样式；之后仅同步位置与 z-order。</summary>
        private void AssertWindow()
        {
            if (_child == IntPtr.Zero || _surface == null) return;
            if (!Win32.IsStillWindow(_child))
            {
                if (_assertCount < 5) Log("[embed] 目标窗口已失效（IsWindow=false）");
                return;
            }

            // Scene 面板脱离可视化树（被关闭）：ScreenBounds 清零 → 隐藏 Engine 窗口。
            // 重新进入树（面板重新创建/拖回）会再次触发 BoundsChanged 带非零尺寸 → 自动恢复。
            var b = _surface.ScreenBounds;
            if (b.Width <= 0 || b.Height <= 0)
            {
                if (!_hiddenByDetach)
                {
                    Win32.ShowWindow(_child, Win32.SW_HIDE);
                    _hiddenByDetach = true;
                    Log("[embed] Scene 面板不可见，隐藏 Engine 窗口");
                }
                return;
            }
            if (_hiddenByDetach)
            {
                _hiddenByDetach = false;
                Log("[embed] Scene 面板恢复可见，显示 Engine 窗口");
            }

            // 面板从主窗口浮动到 FloatingDockWindow（或反向）：HostHandle 变化 → 重设 owner，
            // 使 Engine 跟随新的宿主窗口 z-order。
            if (_surface.HostHandle != _lastHost)
            {
                ApplyOwner();
            }

            ApplyBounds();
            _assertCount++;
            // 前 3 次与之后每 ~200 次（约每 10s@50ms）记录一次回读，验证实际生效情况。
            if (_assertCount <= 3 || _assertCount % 200 == 0)
                LogReadback();
        }

        /// <summary>把 Engine 的 owner 设为 Studio 宿主窗口（主窗口或浮动面板窗口）。
        /// owner-owned 关系使 Engine 始终在宿主之上，但不挡其他应用。
        /// 用 GWLP_HWNDPARENT 而非 SetParent，避免重定为 WS_CHILD（runtime 会拒绝）。</summary>
        private void ApplyOwner()
        {
            if (_child == IntPtr.Zero || _surface == null) return;
            var host = _surface.HostHandle;
            if (host == IntPtr.Zero) return;
            var prev = Win32.GetWindowLongPtr(_child, Win32.GWLP_HWNDPARENT);
            Win32.SetWindowLongPtr(_child, Win32.GWLP_HWNDPARENT, host);
            _lastHost = host;
            // owner 变化后需主动校正一次 z-order，使 Engine 落到新宿主之上；
            // 之后由 owner 关系自动维持，不再主动提升，避免压过其它激活的浮动窗口。
            _zOrderDirty = true;
            Log($"[embed] owner 设置：child=0x{_child.ToInt64():X} owner=0x{host.ToInt64():X} prev=0x{prev.ToInt64():X}");
        }

        private void ApplyStyle()
        {
            if (_child == IntPtr.Zero) return;

            // 剥离装饰，转为无边框 popup 风格，仍为顶层窗口。
            var style = Win32.GetWindowLongPtr(_child, Win32.GWL_STYLE).ToInt64();
            style &= ~(Win32.WS_CAPTION | Win32.WS_THICKFRAME |
                       Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | Win32.WS_SYSMENU);
            Win32.SetWindowLongPtr(_child, Win32.GWL_STYLE, new IntPtr(style));

            // 隐藏任务栏项 + 不抢焦点。owner 关系已维持 z-order，无需 Topmost；
            // 不主动清 Topmost 以免与 runtime 拉锯（runtime 若加回也无害，owner 已保证不挡其他应用）。
            var ex = Win32.GetWindowLongPtr(_child, Win32.GWL_EXSTYLE).ToInt64();
            ex &= ~Win32.WS_EX_APPWINDOW;
            ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            Win32.SetWindowLongPtr(_child, Win32.GWL_EXSTYLE, new IntPtr(ex));
        }

        private void ApplyBounds()
        {
            if (_child == IntPtr.Zero || _surface == null) return;
            var b = _surface.ScreenBounds;
            // 零尺寸由 AssertWindow 统一处理为隐藏，此处不再 early return。
            if (b.Width <= 0 || b.Height <= 0) return;

            // z-order 策略：
            // - 日常同步用 SWP_NOZORDER：完全不碰 z-order，由 owner 关系自动维持。
            //   系统保证 owned 窗口始终在 owner 之上；其它窗口激活时会整体压在
            //   Engine + 宿主之上——这正是不让 Engine 遮挡其它浮动面板的关键。
            // - 仅在 owner 变化（面板跨窗口迁移）或首次嵌入后主动用 HWND_TOP 校正一次，
            //   使 Engine 落到新宿主正上方，之后交还 owner 关系自动维护。
            if (_zOrderDirty)
            {
                _zOrderDirty = false;
                // HWND_TOP 把 Engine 提到非 topmost 栈顶；SWP_NOOWNERZORDER 保留 owner 关系。
                // 不能用 owner 作 insertAfter——SetWindowPos(child, owner) 会把 child 放到
                // owner 下方（Win32 语义），反而把 Engine 压到宿主之下。
                Win32.SetWindowPos(_child, Win32.HWND_TOP, b.X, b.Y, b.Width, b.Height,
                    Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW |
                    Win32.SWP_NOOWNERZORDER);
            }
            else
            {
                Win32.SetWindowPos(_child, IntPtr.Zero, b.X, b.Y, b.Width, b.Height,
                    Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_SHOWWINDOW |
                    Win32.SWP_NOOWNERZORDER);
            }
        }

        /// <summary>回读实际窗口样式、owner 与矩形，与目标对比，用于诊断“设置是否生效”。</summary>
        private void LogReadback()
        {
            try
            {
                var style = Win32.GetWindowLongPtr(_child, Win32.GWL_STYLE).ToInt64();
                var ex = Win32.GetWindowLongPtr(_child, Win32.GWL_EXSTYLE).ToInt64();
                var owner = Win32.GetWindowLongPtr(_child, Win32.GWLP_HWNDPARENT);
                Win32.GetWindowRect(_child, out var rect);
                var target = _surface!.ScreenBounds;
                bool topmost = (ex & Win32.WS_EX_TOPMOST) != 0;
                bool tool = (ex & Win32.WS_EX_TOOLWINDOW) != 0;
                bool caption = (style & Win32.WS_CAPTION) != 0;
                bool thick = (style & Win32.WS_THICKFRAME) != 0;
                Log($"[embed#{_assertCount}] actual=({rect.Left},{rect.Top},{rect.Width}x{rect.Height}) target=({target.X},{target.Y},{target.Width}x{target.Height}) owner=0x{owner.ToInt64():X} topmost={topmost} tool={tool} caption={caption} thick={thick}");
            }
            catch { }
        }

        /// <summary>Studio 最小化/恢复时调用，同步 ADL 窗口可见性。
        /// 若 Scene 面板当前已关闭（_hiddenByDetach），恢复最小化时不强行显示——
        /// 面板恢复可见时由 AssertWindow 自动重新显示。</summary>
        public void SetVisible(bool visible)
        {
            if (_child == IntPtr.Zero) return;
            if (visible && _hiddenByDetach) return;
            Win32.ShowWindow(_child, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
        }

        public void Dispose()
        {
            if (_surface != null)
                _surface.BoundsChanged -= OnBoundsChanged;
            _assertTimer?.Dispose();
            _assertTimer = null;
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

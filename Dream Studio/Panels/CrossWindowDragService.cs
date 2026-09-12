using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Dream.Studio.Engine.Native;

namespace Dream.Studio.Panels
{
    /// <summary>
    /// 跨窗口资源拖拽载荷：在拖拽期间传递给拖放目标的资源信息。
    /// </summary>
    public sealed class DragPayload
    {
        /// <summary>资源 GUID（.meta 中分配）。</summary>
        public string Guid { get; init; } = "";

        /// <summary>资源文件绝对路径。</summary>
        public string Path { get; init; } = "";

        /// <summary>资源文件名（显示用）。</summary>
        public string FileName { get; init; } = "";
    }

    /// <summary>
    /// 资源字段拖放目标标记：挂在可接收资源拖放的控件 Tag 上。
    /// Drop 为释放时执行的回调（闭包捕获数据，目标控件被面板重建后仍有效）。
    /// </summary>
    public sealed class DropTargetMarker
    {
        public required Action<DragPayload> Drop { get; init; }
    }

    /// <summary>
    /// 跨窗口资源拖拽服务：弥补 Avalonia 内置 DragDrop 仅在同一 TopLevel 内生效的限制。
    ///
    /// 背景：Animator 等面板以独立浮动窗口（FloatingDockWindow，主窗口的属主窗口）打开，
    /// 主窗口 Project 面板的拖拽（Avalonia 内部实现为进程内、单窗口路由）无法命中浮动
    /// 窗口中的拖放目标。
    ///
    /// 方案：拖拽源（ProjectPanel）发起内置拖拽的同时调用 <see cref="TrackDrag"/> 启动
    /// 一个后台线程，用 Win32 轮询全局鼠标（GetCursorPos/WindowFromPoint/GetAsyncKeyState），
    /// 把光标所在窗口与坐标 Post 到 UI 线程做命中检测（InputHitTest），实时维护悬停目标。
    /// 左键释放时提交拖放（<see cref="TryCompleteDrop"/>），内置拖拽结束后
    /// <see cref="EndTrack"/> 兜底：均仅在"左键已释放且未按 Esc"且悬停在带
    /// <see cref="DropTargetMarker"/> 的目标上时执行 Drop 回调，幂等互斥。
    ///
    /// 同窗口拖放仍由内置 DragDrop 处理：命中检测显式跳过源窗口，避免双重处理。
    /// 后台线程只做纯 Win32 调用与 Post，不依赖 UI 线程调度，因此不受原生拖拽循环阻塞影响。
    /// </summary>
    internal static class CrossWindowDragService
    {
#if WINDOWS
        // 已注册窗口：句柄 → 窗口。仅 UI 线程访问。
        private static readonly Dictionary<IntPtr, Window> _windows = new();

        private static DragPayload? _payload;   // UI 线程
        private static Window? _sourceWindow;   // UI 线程
        private static Control? _hover;         // UI 线程（UpdateTarget 写，TryCompleteDrop/EndTrack 读）
        private static bool _dropHandled;       // UI 线程：拖放是否已消费（防双完成）

        // 后台线程读，UI 线程写：
        private static volatile bool _active;   // 跟踪进行中
        private static volatile bool _escaped;  // 后台线程检测到 Esc
        private static volatile DragPayload? _activePayload; // 后台线程循环退出条件用

        /// <summary>注册一个可作为跨窗口拖放目标的窗口（主窗口 / 浮动窗口）。</summary>
        public static void RegisterWindow(Window window)
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero) _windows[hwnd] = window;
        }

        /// <summary>注销窗口（窗口关闭时调用）。</summary>
        public static void UnregisterWindow(Window window)
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero) _windows.Remove(hwnd);
        }

        /// <summary>
        /// 开始跨窗口拖拽跟踪。由拖拽源在发起内置 DragDrop 前调用；
        /// 内置拖拽结束后必须调用 <see cref="EndTrack"/>。
        /// </summary>
        public static void TrackDrag(Window source, DragPayload payload)
        {
            EndTrack(); // 清理上一次遗留状态
            _sourceWindow = source;
            _payload = payload;
            _hover = null;
            _dropHandled = false;
            _escaped = false;
            _active = true;
            _activePayload = payload;
            var thread = new Thread(TrackLoop) { IsBackground = true };
            thread.Start();
        }

        /// <summary>结束拖拽跟踪并完成/取消拖放。幂等。必须在 UI 线程调用。</summary>
        public static void EndTrack()
        {
            // 停止后台跟踪线程（线程最多在下一轮循环退出）。
            _active = false;
            _activePayload = null;

            // 后台线程可能已提交拖放（或正在排队）：未消费时在此同步完成/取消。
            if (!_dropHandled) TryCompleteDrop();
            _dropHandled = false; // 为下一次拖拽复位
        }

        /// <summary>
        /// 完成/取消拖放。仅在"左键已释放且未按 Esc"时执行 Drop 回调（Esc 取消）。
        /// 幂等：状态消费后置 _dropHandled，重复调用直接返回。
        /// </summary>
        private static void TryCompleteDrop()
        {
            if (_dropHandled) return;
            var payload = _payload;
            var hover = _hover;
            var escaped = _escaped;
            // 立即消费状态，防止 EndTrack 与排队的 Post 重复处理。
            _payload = null;
            _sourceWindow = null;
            _hover = null;
            _escaped = false;
            _dropHandled = true;

            if (!escaped
                && payload != null
                && hover is { } c
                && c.Tag is DropTargetMarker m
                && (Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) == 0
                && (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) == 0)
            {
                // 目标控件可能已因面板重建而脱离视觉树，但回调闭包捕获的数据仍有效。
                try { m.Drop(payload); } catch { /* 目标处理失败不影响拖拽流程 */ }
            }
        }

        /// <summary>后台线程主循环：纯 Win32 轮询 + Post 到 UI 线程，不触碰 Avalonia UI 对象。</summary>
        private static void TrackLoop()
        {
            while (_active && _activePayload != null)
            {
                if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) != 0)
                {
                    _escaped = true; // Esc 取消
                    break;
                }
                if ((Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) == 0)
                {
                    // 左键已释放：贴近释放点刷新一次命中，随后提交拖放（即使原生拖拽
                    // 未随跨窗口释放结束，也能独立完成）。
                    PollCursor();
                    PostToUi(TryCompleteDrop);
                    return;
                }
                PollCursor();
                Thread.Sleep(15);
            }
        }

        /// <summary>读取光标位置与所在窗口，Post 到 UI 线程做命中检测。</summary>
        private static void PollCursor()
        {
            if (!Win32.GetCursorPos(out var pt)) return;
            var hwnd = Win32.WindowFromPoint(pt);
            var sx = pt.X;
            var sy = pt.Y;
            PostToUi(() => UpdateTarget(hwnd, sx, sy));
        }

        /// <summary>跨线程 Post 到 UI 线程（应用退出等极端场景下 Post 可能失败，静默忽略）。</summary>
        private static void PostToUi(Action action)
        {
            try { Dispatcher.UIThread.Post(action); }
            catch { /* 线程已停止或应用退出 */ }
        }

        /// <summary>UI 线程：把光标屏幕坐标映射到窗口并更新悬停目标。</summary>
        private static void UpdateTarget(IntPtr hwnd, int sx, int sy)
        {
            if (_payload == null) return; // 拖拽已结束

            // 先直接按句柄查（浮动窗口自身；WindowFromPoint 返回其自身 hwnd）；
            // 未命中再沿父链/属主链解析（覆盖主窗口内嵌引擎子窗口、弹层等）。
            Window? win = null;
            if (_windows.TryGetValue(hwnd, out var direct)) win = direct;
            else
            {
                var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
                var owner = Win32.GetAncestor(hwnd, Win32.GA_ROOTOWNER);
                if (root != IntPtr.Zero && _windows.TryGetValue(root, out var w1)) win = w1;
                else if (owner != IntPtr.Zero && _windows.TryGetValue(owner, out var w2)) win = w2;
            }

            // 源窗口由内置 DragDrop 处理；只接管其它窗口（浮动窗口等）。
            if (win == null || ReferenceEquals(win, _sourceWindow))
            {
                _hover = null;
                return;
            }

            try
            {
                var local = win.PointToClient(new PixelPoint(sx, sy));
                // 窗口边界外（含跨屏负坐标）不处理。
                if (local.X < 0 || local.Y < 0 || local.X >= win.Bounds.Width || local.Y >= win.Bounds.Height)
                {
                    _hover = null;
                    return;
                }
                var hit = win.InputHitTest(local);
                Control? target = null;
                for (var el = hit as StyledElement; el != null; el = el.Parent as StyledElement)
                {
                    if (el is Control c && c.Tag is DropTargetMarker)
                    {
                        target = c;
                        break;
                    }
                }
                _hover = target;
            }
            catch
            {
                _hover = null;
            }
        }
#else
        public static void RegisterWindow(Window window) { }
        public static void UnregisterWindow(Window window) { }
        public static void TrackDrag(Window source, DragPayload payload) { }
        public static void EndTrack() { }
#endif
    }
}

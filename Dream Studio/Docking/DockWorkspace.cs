using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// Top-level coordinator for a docking system. Owns the main window's <see cref="DockManager"/>,
    /// the <see cref="DragService"/>, and the set of live hosts (main + floating). Cross-cutting
    /// operations that span hosts — hit-testing, floating a panel, returning a panel to the main
    /// host — live here so individual hosts stay decoupled from one another.
    /// </summary>
    public sealed class DockWorkspace
    {
        private readonly List<DockHost> _hosts = new();
        private readonly List<FloatingDockWindow> _floating = new();
        private Window? _owner;

        public DockManager MainManager { get; } = new();

        internal DragService DragService { get; }

        public IReadOnlyList<DockHost> Hosts => _hosts;

        /// <summary>
        /// 聚合所有管理器（主 + 浮动）的布局变更事件。任意管理器结构变化时触发，
        /// 使订阅方（如面板菜单）无需分别订阅各浮动窗口。
        /// </summary>
        public event EventHandler? LayoutChanged;

        /// <summary>聚合所有管理器（主 + 浮动）当前的面板。</summary>
        public IEnumerable<DockPanel> EnumerateAllPanels()
        {
            foreach (var panel in MainManager.EnumeratePanels())
            {
                yield return panel;
            }

            foreach (var window in _floating)
            {
                foreach (var panel in window.Manager.EnumeratePanels())
                {
                    yield return panel;
                }
            }
        }

        public DockWorkspace()
        {
            DragService = new DragService(this);
            MainManager.LayoutChanged += OnAnyLayoutChanged;
        }

        private void OnAnyLayoutChanged(object? sender, EventArgs e) => LayoutChanged?.Invoke(this, e);

        internal void EnsureOwner(Window window) => _owner ??= window;

        internal void RegisterHost(DockHost host)
        {
            if (!_hosts.Contains(host))
            {
                _hosts.Add(host);
            }
        }

        internal void UnregisterHost(DockHost host) => _hosts.Remove(host);

        /// <summary>
        /// Returns the smallest host whose on-screen bounds contain <paramref name="screen"/>, or
        /// null when the cursor is outside every host (e.g. over the desktop).
        /// </summary>
        internal DockHost? HitTestHost(PixelPoint screen)
        {
            DockHost? best = null;
            var bestArea = double.MaxValue;

            foreach (var host in _hosts)
            {
                var topLevel = TopLevel.GetTopLevel(host);
                if (topLevel is null)
                {
                    continue;
                }

                var local = topLevel.PointToClient(screen);
                var origin = host.TranslatePoint(new Point(0, 0), topLevel);
                if (origin is null)
                {
                    continue;
                }

                var rect = new Rect(origin.Value, host.Bounds.Size);
                if (!rect.Contains(local))
                {
                    continue;
                }

                var area = rect.Width * rect.Height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = host;
                }
            }

            return best;
        }

        /// <summary>Floats <paramref name="panel"/> into a new floating window at <paramref name="screen"/>.</summary>
        internal void FloatPanel(DockPanel panel, PixelPoint? screen)
        {
            var owner = FindOwnerManager(panel);
            owner?.Detach(panel);
            panel.State = DockState.Floating;

            var position = screen
                ?? (_owner is not null
                    ? new PixelPoint(_owner.Position.X + 120, _owner.Position.Y + 120)
                    : new PixelPoint(200, 200));

            var window = new FloatingDockWindow(this, panel, position);
            window.Manager.LayoutChanged += OnAnyLayoutChanged;
            _floating.Add(window);
            window.Closed += (_, _) =>
            {
                // 先取消订阅，避免逐个 Detach 时反复触发聚合事件。
                window.Manager.LayoutChanged -= OnAnyLayoutChanged;
                _floating.Remove(window);

                // 卸下窗口内所有面板，清空其 Group 引用。否则缓存的实例被重新停靠时，
                // DockManager.Detach 会误操作已失效的浮动管理器树，导致状态错乱。
                foreach (var p in window.Manager.EnumeratePanels().ToList())
                {
                    window.Manager.Detach(p);
                }

                // 窗口关闭不会自动触发布局事件，主动通知订阅方（如面板菜单）刷新状态。
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            };

            if (_owner is not null)
            {
                window.Show(_owner);
            }
            else
            {
                window.Show();
            }

            // 在订阅 LayoutChanged 之后将面板停靠进浮动窗口的管理器，
            // 触发的首次 LayoutChanged 会被聚合到 DockWorkspace，使菜单状态正确更新。
            window.Manager.Dock(panel, null, DockSide.Center);
        }

        /// <summary>
        /// Toggles a panel between the main host and its own floating window. Bound to a tab's
        /// double-click gesture.
        /// </summary>
        internal void ToggleFloat(DockPanel panel)
        {
            if (MainManager.EnumeratePanels().Contains(panel))
            {
                FloatPanel(panel, null);
            }
            else
            {
                DockToMain(panel);
            }
        }

        /// <summary>Returns a floating panel to the main host as a centred tab.</summary>
        internal void DockToMain(DockPanel panel)
        {
            var owner = FindOwnerManager(panel);
            if (owner is null)
            {
                return;
            }

            owner.Detach(panel);
            panel.State = DockState.Docked;
            MainManager.Dock(panel, null, DockSide.Center);
            CloseEmptyFloating();
        }

        /// <summary>
        /// 跨主机关闭面板：查找面板所在的管理器（主或浮动）并移除。
        /// 浮动窗口变空后会自动关闭。
        /// </summary>
        public void ClosePanel(DockPanel panel)
        {
            var owner = FindOwnerManager(panel);
            owner?.Close(panel);
            CloseEmptyFloating();
        }

        private void CloseEmptyFloating()
        {
            foreach (var window in _floating.ToList())
            {
                if (window.Manager.Root is null)
                {
                    window.Close();
                }
            }
        }

        private DockManager? FindOwnerManager(DockPanel panel)
        {
            if (MainManager.EnumeratePanels().Contains(panel))
            {
                return MainManager;
            }

            foreach (var window in _floating)
            {
                if (window.Manager.EnumeratePanels().Contains(panel))
                {
                    return window.Manager;
                }
            }

            return null;
        }
    }
}

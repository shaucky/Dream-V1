using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Dream.Studio.Panels;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// A detached OS window that hosts its own <see cref="DockManager"/> and <see cref="DockHost"/>,
    /// letting a floated panel keep full docking behaviour (split/tab/resize). When its last panel
    /// leaves (e.g. via double-click → dock-to-main) the window closes itself automatically.
    /// </summary>
    public sealed class FloatingDockWindow : Window
    {
        public DockManager Manager { get; } = new();

        public FloatingDockWindow(DockWorkspace workspace, DockPanel panel, PixelPoint position)
        {
            Title = panel.Title;
            Width = 480;
            Height = 340;
            Position = new PixelPoint(position.X - 40, position.Y - 16);
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26));

            var host = new DockHost
            {
                Manager = Manager,
                Workspace = workspace,
            };

            Content = host;

            // Defer the close so it never runs synchronously inside a LayoutChanged handler.
            Manager.LayoutChanged += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                {
                    if (Manager.Root is null)
                    {
                        Close();
                    }
                });

            // 注意：面板的 Dock 由 DockWorkspace.FloatPanel 在订阅 LayoutChanged 后调用，
            // 否则构造函数内触发的首次 LayoutChanged 会丢失（workspace 尚未订阅）。
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            // 注册为浮动拖放窗口：供跨窗口资源拖拽（CrossWindowDragService）命中内部目标。
            CrossWindowDragService.RegisterWindow(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            CrossWindowDragService.UnregisterWindow(this);
        }
    }
}

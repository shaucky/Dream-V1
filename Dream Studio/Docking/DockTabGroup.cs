using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// Visual representation of a <see cref="LayoutGroup"/>: a horizontal tab strip plus a content
    /// area showing the active panel. The control is rebuilt from its group on every layout change
    /// by <see cref="DockHost"/>, so it only renders current state and delegates all interaction
    /// back to the host/manager.
    /// </summary>
    public sealed class DockTabGroup : TemplatedControl
    {
        private readonly DockHost _host;
        private readonly DockManager _manager;
        private readonly LayoutGroup _group;

        private StackPanel? _tabs;
        private Border? _content;

        public DockTabGroup(DockHost host, DockManager manager, LayoutGroup group)
        {
            _host = host;
            _manager = manager;
            _group = group;
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);
            _tabs = e.NameScope?.Find("PART_Tabs") as StackPanel;
            _content = e.NameScope?.Find("PART_Content") as Border;
            Render();
        }

        private void Render()
        {
            if (_tabs is null || _content is null)
            {
                return;
            }

            _tabs.Children.Clear();

            for (var i = 0; i < _group.Panels.Count; i++)
            {
                var panel = _group.Panels[i];
                var header = new DockTabHeader
                {
                    Title = panel.Title,
                    IsActive = i == _group.ActiveIndex,
                    CanClose = panel.CanClose,
                };

                var index = i;
                header.PointerPressed += (_, e) => OnHeaderPressed(panel, index, e);
                header.RequestClose += (_, _) => _manager.Close(panel);
                // 延迟执行 ToggleFloat：避免在输入事件回调中同步执行 Detach + Dock + Close 窗口，
                // 这会在指针处理栈内销毁当前可视树导致崩溃。Post 到下一帧让输入事件先完成。
                header.DoubleTapped += (_, _) =>
                    Dispatcher.UIThread.Post(() => _host.Workspace?.ToggleFloat(panel));

                _tabs.Children.Add(header);
            }

            // The same Content control is reused across rebuilds; detach it from whatever
            // Border previously held it before re-parenting, otherwise Avalonia throws
            // "The Control already has a parent."
            var content = _group.ActivePanel?.Content;
            if (content is not null && content.Parent is Border previous)
            {
                previous.Child = null;
            }
            _content.Child = content;
        }

        private void OnHeaderPressed(DockPanel panel, int index, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _manager.SetActive(_group, index);
            _host.BeginDrag(_manager, panel, e);
            e.Handled = true;
        }
    }
}

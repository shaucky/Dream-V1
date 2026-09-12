using System;
using System.Linq;
using Avalonia.Controls;
using Dream.Studio.Docking;

namespace Dream.Studio.Panels
{
    /// <summary>
    /// 面板菜单控件。依赖 <see cref="PanelRegistry"/>（查询面板定义）与
    /// <see cref="DockWorkspace"/>（跨主机执行停靠/关闭），自身只负责菜单的构建与交互
    /// （单一职责）。菜单项勾选状态随布局变化自动同步，包含浮动窗口中的面板。
    /// </summary>
    public sealed class PanelMenu : MenuItem
    {
        private PanelRegistry? _registry;
        private DockWorkspace? _workspace;

        /// <summary>绑定注册表与工作区，并订阅布局变更以刷新菜单。</summary>
        public void Bind(PanelRegistry registry, DockWorkspace workspace)
        {
            if (_workspace is not null)
            {
                _workspace.LayoutChanged -= OnLayoutChanged;
            }

            _registry = registry;
            _workspace = workspace;
            // 订阅 workspace 的聚合事件，覆盖主管理器与所有浮动窗口的布局变更。
            workspace.LayoutChanged += OnLayoutChanged;

            Rebuild();
        }

        private void OnLayoutChanged(object? sender, EventArgs e)
        {
            Rebuild();
        }

        /// <summary>根据注册表重建子菜单项。已打开的面板显示勾选状态。</summary>
        private void Rebuild()
        {
            Items.Clear();

            if (_registry is null || _workspace is null)
            {
                return;
            }

            // 直接查询 workspace 的实时面板集合，避免维护跟踪表与时序不同步。
            var livePanels = _workspace.EnumerateAllPanels().ToList();

            foreach (var desc in _registry.Descriptors)
            {
                var item = new MenuItem
                {
                    Header = desc.Title,
                    IsChecked = _registry.IsOpen(desc.Id, livePanels),
                    ToggleType = MenuItemToggleType.CheckBox,
                };
                item.Click += (_, _) => OnToggle(desc.Id);
                Items.Add(item);
            }
        }

        private void OnToggle(string id)
        {
            if (_registry is null || _workspace is null)
            {
                return;
            }

            var livePanels = _workspace.EnumerateAllPanels();

            if (_registry.IsOpen(id, livePanels))
            {
                // 已打开 → 关闭（通过工作区查找其所在管理器并移除）。
                var panel = _registry.Instances[id];
                _workspace.ClosePanel(panel);
            }
            else
            {
                // 未打开 → 创建并默认为浮动面板（独立窗口，可再手动停靠/合并）。
                var panel = _registry.GetOrCreate(id);
                _workspace.FloatPanel(panel, null);
            }

            // 点击后收起整个子菜单。
            IsSubMenuOpen = false;
        }
    }
}


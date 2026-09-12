using System;
using System.Collections.Generic;
using System.Linq;
using Dream.Studio.Docking;

namespace Dream.Studio.Panels
{
    /// <summary>
    /// 简单的 <see cref="IPanelDescriptor"/> 实现，用委托创建面板内容。
    /// </summary>
    public sealed class PanelDescriptor : IPanelDescriptor
    {
        private readonly Func<DockPanel> _factory;

        public string Id { get; }
        public string Title { get; }

        public PanelDescriptor(string id, string title, Func<DockPanel> factory)
        {
            Id = id;
            Title = title;
            _factory = factory;
        }

        public DockPanel Create() => _factory();
    }
}

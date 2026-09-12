using System;
using System.Collections.Generic;
using System.Linq;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// A tabbed stack of panels. One <see cref="ActivePanel"/> is visible at a time; the others
    /// are reachable through their tab headers. A group is a leaf of the layout tree.
    /// </summary>
    public sealed class LayoutGroup : LayoutNode
    {
        private readonly List<DockPanel> _panels = new();
        private int _activeIndex;

        public IReadOnlyList<DockPanel> Panels => _panels;

        public int ActiveIndex
        {
            get => _activeIndex;
            set => _activeIndex = _panels.Count == 0 ? 0 : Math.Clamp(value, 0, _panels.Count - 1);
        }

        public DockPanel? ActivePanel => _panels.Count == 0 ? null : _panels[ActiveIndex];

        public override IEnumerable<LayoutNode> Children => Enumerable.Empty<LayoutNode>();

        internal void Add(DockPanel panel, int index = -1)
        {
            if (index < 0 || index > _panels.Count)
            {
                index = _panels.Count;
            }
            _panels.Insert(index, panel);
            panel.Group = this;
            _activeIndex = index;
        }

        internal bool Remove(DockPanel panel)
        {
            var removed = _panels.Remove(panel);
            if (removed)
            {
                panel.Group = null;
                if (_activeIndex >= _panels.Count)
                {
                    _activeIndex = Math.Max(0, _panels.Count - 1);
                }
            }
            return removed;
        }

        internal bool Contains(DockPanel panel) => _panels.Contains(panel);
    }
}

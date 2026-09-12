using System;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// The controller contract for a single dock host's layout tree. A manager owns one
    /// <see cref="LayoutNode"/> root and is the only component allowed to mutate it, keeping
    /// mutation logic in one place (Single Responsibility).
    /// </summary>
    public interface IDockManager
    {
        /// <summary>The root of the layout tree, or null when the host is empty.</summary>
        LayoutNode? Root { get; }

        /// <summary>Raised whenever the layout tree structurally changes.</summary>
        event EventHandler? LayoutChanged;

        /// <summary>Docks <paramref name="panel"/> relative to <paramref name="target"/>.</summary>
        /// <param name="ratio">新面板所在一侧的占比（0.05..0.95），默认 0.5。</param>
        void Dock(DockPanel panel, LayoutGroup? target, DockSide side, double ratio = 0.5);

        /// <summary>Adds <paramref name="panel"/> as a tab in <paramref name="target"/>.</summary>
        void DockAsTab(DockPanel panel, LayoutGroup target, int index);

        /// <summary>Selects the visible tab of a group.</summary>
        void SetActive(LayoutGroup group, int index);

        /// <summary>Removes a panel from the tree and rebalances empty splits.</summary>
        bool Detach(DockPanel panel);

        /// <summary>Removes a panel and marks it closed.</summary>
        void Close(DockPanel panel);

        /// <summary>Persists a new split ratio (clamped).</summary>
        void SetSplitRatio(LayoutSplit split, double ratio);

        /// <summary>Enumerates every panel hosted in this tree.</summary>
        System.Collections.Generic.IEnumerable<DockPanel> EnumeratePanels();
    }
}

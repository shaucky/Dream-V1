using Avalonia.Controls;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// Default <see cref="IDockPanel"/> implementation: a titled piece of content that can be
    /// docked, tabbed, split, floated and closed. The panel is a pure model; it carries no UI
    /// behaviour of its own.
    /// </summary>
    public sealed class DockPanel : IDockPanel
    {
        /// <summary>The group currently hosting this panel, set by the layout tree.</summary>
        internal LayoutGroup? Group { get; set; }

        public string Title { get; set; } = string.Empty;
        public Control? Content { get; set; }
        public bool CanClose { get; set; } = true;
        public object? Icon { get; set; }
        public DockState State { get; set; } = DockState.Docked;

        public DockPanel() { }

        public DockPanel(string title, Control? content = null)
        {
            Title = title;
            Content = content;
        }
    }
}

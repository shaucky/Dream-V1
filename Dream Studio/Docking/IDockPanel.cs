using Avalonia.Controls;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// The content contract for a single dockable panel. Implemented by <see cref="DockPanel"/>;
    /// consumers may provide their own implementation to plug arbitrary content into the system.
    /// </summary>
    public interface IDockPanel
    {
        /// <summary>Title shown in the panel's tab header.</summary>
        string Title { get; }

        /// <summary>The visual content rendered inside the panel.</summary>
        Control? Content { get; }

        /// <summary>Whether the user is allowed to close the panel.</summary>
        bool CanClose { get; }

        /// <summary>Optional icon geometry / object rendered by the header template.</summary>
        object? Icon { get; }

        /// <summary>The current placement state of the panel.</summary>
        DockState State { get; }
    }
}

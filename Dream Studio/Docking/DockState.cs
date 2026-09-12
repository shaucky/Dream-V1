namespace Dream.Studio.Docking
{
    /// <summary>
    /// The visual state of a dockable panel.
    /// </summary>
    public enum DockState
    {
        /// <summary>Participates in a host's layout tree.</summary>
        Docked,

        /// <summary>Lives inside its own floating window.</summary>
        Floating,

        /// <summary>Collapsed to a side strip; slides out on hover. (Reserved for extension.)</summary>
        AutoHidden
    }
}

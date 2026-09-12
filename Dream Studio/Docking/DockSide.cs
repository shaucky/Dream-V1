namespace Dream.Studio.Docking
{
    /// <summary>
    /// The side of a layout group onto which a panel can be docked.
    /// Side splits the target area; <see cref="Center"/> adds the panel as a new tab.
    /// </summary>
    public enum DockSide
    {
        Left,
        Top,
        Right,
        Bottom,
        Center
    }
}

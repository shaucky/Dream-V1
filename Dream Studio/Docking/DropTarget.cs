namespace Dream.Studio.Docking
{
    /// <summary>
    /// Resolved destination for an in-flight drag: which host receives the panel, which group is
    /// the drop anchor (null when filling an empty host) and on which side the panel will land.
    /// A default value (<see cref="IsEmpty"/>) means "no host under cursor" → float.
    /// </summary>
    internal readonly record struct DropTarget(DockHost? Host, LayoutGroup? Group, DockSide Side)
    {
        public static DropTarget Empty => default;

        public bool IsEmpty => Host is null;
    }
}

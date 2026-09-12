using System.Collections.Generic;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// Base type for nodes in the immutable-ish layout tree. A tree is either a
    /// <see cref="LayoutSplit"/> (two children + a draggable divider) or a
    /// <see cref="LayoutGroup"/> (a stack of tabbed panels). Mutation is centralised in
    /// <see cref="DockManager"/>; nodes only expose structural links.
    /// </summary>
    public abstract class LayoutNode
    {
        /// <summary>The owning split, or null when this node is the root.</summary>
        public LayoutSplit? Parent { get; internal set; }

        /// <summary>Child layout nodes (panels themselves are not layout nodes).</summary>
        public abstract IEnumerable<LayoutNode> Children { get; }

        /// <summary>Walks from the immediate parent up to the root.</summary>
        public IEnumerable<LayoutNode> Ancestors()
        {
            var node = Parent;
            while (node is not null)
            {
                yield return node;
                node = node.Parent;
            }
        }
    }
}

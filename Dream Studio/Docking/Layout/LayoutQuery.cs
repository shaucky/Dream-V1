using System.Collections.Generic;

namespace Dream.Studio.Docking
{
    /// <summary>Read-only traversal helpers over the layout tree.</summary>
    public static class LayoutQuery
    {
        /// <summary>Depth-first walk including the node itself.</summary>
        public static IEnumerable<LayoutNode> DescendantsAndSelf(this LayoutNode node)
        {
            yield return node;
            foreach (var child in node.Children)
            {
                foreach (var d in DescendantsAndSelf(child))
                {
                    yield return d;
                }
            }
        }

        /// <summary>Every <see cref="LayoutGroup"/> contained in the tree (empty if root is null).</summary>
        public static IEnumerable<LayoutGroup> Groups(this LayoutNode? root)
        {
            if (root is null)
            {
                yield break;
            }

            foreach (var n in root.DescendantsAndSelf())
            {
                if (n is LayoutGroup g)
                {
                    yield return g;
                }
            }
        }
    }
}

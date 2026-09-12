using System.Collections.Generic;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// A binary split in the layout tree. The two children are separated by a draggable
    /// divider; <see cref="Ratio"/> (clamped to 0.05..0.95) controls how the available space is
    /// divided between <see cref="First"/> and <see cref="Second"/>.
    /// </summary>
    public sealed class LayoutSplit : LayoutNode
    {
        private LayoutNode _first = null!;
        private LayoutNode _second = null!;

        public SplitOrientation Orientation { get; set; } = SplitOrientation.Horizontal;

        public LayoutNode First
        {
            get => _first;
            private set => _first = value;
        }

        public LayoutNode Second
        {
            get => _second;
            private set => _second = value;
        }

        public double Ratio { get; set; } = 0.5;

        public override IEnumerable<LayoutNode> Children
        {
            get { yield return First; yield return Second; }
        }

        internal void SetChildren(LayoutNode first, LayoutNode second)
        {
            _first = first;
            _second = second;
            first.Parent = this;
            second.Parent = this;
        }

        internal void Replace(LayoutNode oldChild, LayoutNode newChild)
        {
            if (ReferenceEquals(_first, oldChild))
            {
                _first = newChild;
            }
            else if (ReferenceEquals(_second, oldChild))
            {
                _second = newChild;
            }
            newChild.Parent = this;
        }
    }

    /// <summary>Axis along which a <see cref="LayoutSplit"/> divides its children.</summary>
    public enum SplitOrientation
    {
        Horizontal,
        Vertical
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// Sole mutator of a layout tree. Encapsulates docking, tabbing, detaching, closing and split
    /// ratio persistence, plus the rebalancing that keeps the tree free of empty/degree-1 splits.
    /// All structural changes raise <see cref="LayoutChanged"/> so views can rebuild.
    /// </summary>
    public sealed class DockManager : IDockManager
    {
        public LayoutNode? Root { get; private set; }

        public event EventHandler? LayoutChanged;

        public void Dock(DockPanel panel, LayoutGroup? target, DockSide side, double ratio = 0.5)
        {
            // Ensure the panel is detached from any previous location before re-placing it.
            if (panel.Group is not null)
            {
                Detach(panel);
            }

            if (side == DockSide.Center)
            {
                DockCenter(panel, target);
                OnChanged();
                return;
            }

            var newGroup = new LayoutGroup();
            newGroup.Add(panel);
            panel.State = DockState.Docked;

            var orientation = (side == DockSide.Left || side == DockSide.Right)
                ? SplitOrientation.Horizontal
                : SplitOrientation.Vertical;

            var newFirst = side == DockSide.Left || side == DockSide.Top;
            // ratio 始终表示“新面板所在一侧”的占比，与 newFirst 配合决定 split 的实际 Ratio。
            var splitRatio = newFirst ? ratio : 1 - ratio;
            splitRatio = Math.Clamp(splitRatio, 0.05, 0.95);

            if (target is null)
            {
                if (Root is null)
                {
                    SetRoot(newGroup);
                }
                else
                {
                    // Wrap the current root in a new split. SetChildren reparents Root to the
                    // split, so install the split as root directly — SetRoot would wrongly null
                    // out the old root's Parent even though it is now a child of the split.
                    var split = new LayoutSplit { Orientation = orientation, Ratio = splitRatio };
                    split.SetChildren(newFirst ? newGroup : Root, newFirst ? Root : newGroup);
                    Root = split;
                    split.Parent = null;
                }
            }
            else
            {
                // SetChildren reparents target to the new split, so capture target's original
                // parent first and use it to install the split in target's previous position.
                var originalParent = target.Parent;
                var split = new LayoutSplit { Orientation = orientation, Ratio = splitRatio };
                split.SetChildren(newFirst ? newGroup : target, newFirst ? target : newGroup);
                if (originalParent is LayoutSplit parent)
                {
                    parent.Replace(target, split);
                }
                else
                {
                    // target was the root.
                    Root = split;
                    split.Parent = null;
                }
            }

            OnChanged();
        }

        public void DockAsTab(DockPanel panel, LayoutGroup target, int index)
        {
            if (panel.Group is not null)
            {
                Detach(panel);
            }

            target.Add(panel, index);
            panel.State = DockState.Docked;
            OnChanged();
        }

        public void SetActive(LayoutGroup group, int index)
        {
            if (group.ActiveIndex == index)
            {
                return;
            }

            group.ActiveIndex = index;
            OnChanged();
        }

        public bool Detach(DockPanel panel)
        {
            var group = panel.Group ?? Root?.Groups().FirstOrDefault(g => g.Contains(panel));
            if (group is null)
            {
                return false;
            }

            group.Remove(panel);
            panel.Group = null;

            if (group.Panels.Count == 0)
            {
                RemoveNode(group);
            }

            OnChanged();
            return true;
        }

        public void Close(DockPanel panel)
        {
            Detach(panel);
        }

        public void SetSplitRatio(LayoutSplit split, double ratio)
        {
            split.Ratio = Math.Clamp(ratio, 0.05, 0.95);
            OnChanged();
        }

        public IEnumerable<DockPanel> EnumeratePanels()
        {
            if (Root is null)
            {
                yield break;
            }

            foreach (var group in Root.Groups())
            {
                foreach (var panel in group.Panels)
                {
                    yield return panel;
                }
            }
        }

        private void DockCenter(DockPanel panel, LayoutGroup? target)
        {
            if (target is null)
            {
                // No explicit target: tab into the root group if there is one, otherwise into the
                // first group of an existing split, otherwise create a fresh root group.
                var group = Root as LayoutGroup ?? Root?.Groups().FirstOrDefault();
                if (group is null)
                {
                    group = new LayoutGroup();
                    group.Add(panel);
                    SetRoot(group);
                }
                else
                {
                    group.Add(panel);
                }
            }
            else
            {
                target.Add(panel);
            }

            panel.State = DockState.Docked;
        }

        private void RemoveNode(LayoutNode node)
        {
            if (node.Parent is LayoutSplit split)
            {
                var survivor = ReferenceEquals(split.First, node) ? split.Second : split.First;
                survivor.Parent = null;
                ReplaceNode(split, survivor);
            }
            else
            {
                SetRoot(null);
            }
        }

        private void ReplaceNode(LayoutNode oldNode, LayoutNode newNode)
        {
            if (oldNode.Parent is LayoutSplit parent)
            {
                parent.Replace(oldNode, newNode);
            }
            else
            {
                SetRoot(newNode);
            }
        }

        private void SetRoot(LayoutNode? node)
        {
            if (Root is not null)
            {
                Root.Parent = null;
            }

            Root = node;

            if (node is not null)
            {
                node.Parent = null;
            }
        }

        private void OnChanged()
        {
            // 后处理简化：扁平化同方向链式嵌套，清理单子/空 split。
            // 在触发事件前完成，使视图重建看到的是简化后的树。
            Root = SimplifyNode(Root);
            if (Root is not null) Root.Parent = null;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 后处理简化布局子树，消除拖拽产生的冗余嵌套：
        /// - 收集 node 子树中与 node 同方向的连续 split 的叶子（保留顺序与累积权重），
        ///   重建为平衡二叉。视觉上各区域大小与顺序不变，仅层级扁平化。
        ///   例：H[H[A, H[B, H[C, D]]]] → H[H[A, B], H[C, D]]（4 叶子 3 层 → 2 层）。
        /// - 单子/空 split 用唯一有效子替换自身（对应“Grid 子只有一个 Grid 时提升子并删除自身”）。
        /// - 反方向子 split 作为整体叶子保留，并递归简化其内部。
        /// </summary>
        private static LayoutNode? SimplifyNode(LayoutNode? node)
        {
            if (node is null) return null;
            if (node is LayoutGroup) return node;
            if (node is LayoutSplit s)
            {
                var leaves = new List<(LayoutNode Node, double Weight)>();
                CollectSameOrientation(s, s.Orientation, 1.0, leaves);

                if (leaves.Count == 0) return null;
                if (leaves.Count == 1) return leaves[0].Node; // 单子：提升，删除自身
                return BuildBalanced(leaves, s.Orientation);
            }
            return node;
        }

        /// <summary>
        /// 收集 node 子树中方向与 <paramref name="orient"/> 相同的连续 split 的叶子。
        /// weight 沿 Ratio 链累积，代表该叶子在原结构中占据的归一化比例（用于重建时还原视觉）。
        /// 反方向 split 作为整体叶子，并先递归 SimplifyNode 其内部。
        /// </summary>
        private static void CollectSameOrientation(LayoutNode node, SplitOrientation orient,
            double weight, List<(LayoutNode Node, double Weight)> result)
        {
            if (node is LayoutGroup g)
            {
                result.Add((g, weight));
                return;
            }
            if (node is LayoutSplit sp && sp.Orientation == orient)
            {
                CollectSameOrientation(sp.First, orient, weight * sp.Ratio, result);
                CollectSameOrientation(sp.Second, orient, weight * (1 - sp.Ratio), result);
            }
            else
            {
                // 反方向 split：作为整体叶子，内部递归简化（处理其自身的同方向链）。
                result.Add((SimplifyNode(node)!, weight));
            }
        }

        /// <summary>按原顺序将叶子重建为平衡二叉，权重还原为 Ratio，保持视觉比例。</summary>
        private static LayoutNode BuildBalanced(List<(LayoutNode Node, double Weight)> leaves, SplitOrientation orient)
        {
            if (leaves.Count == 1) return leaves[0].Node;
            int mid = leaves.Count / 2;
            var left = leaves.GetRange(0, mid);
            var right = leaves.GetRange(mid, leaves.Count - mid);
            double leftSum = 0, rightSum = 0;
            foreach (var l in left) leftSum += l.Weight;
            foreach (var r in right) rightSum += r.Weight;
            double ratio = leftSum + rightSum > 0 ? leftSum / (leftSum + rightSum) : 0.5;
            var split = new LayoutSplit { Orientation = orient, Ratio = Math.Clamp(ratio, 0.05, 0.95) };
            split.SetChildren(BuildBalanced(left, orient), BuildBalanced(right, orient));
            return split;
        }
    }
}

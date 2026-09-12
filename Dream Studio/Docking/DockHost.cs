using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// Renders one <see cref="DockManager"/>'s layout tree and hosts all pointer-driven drag
    /// interaction. The host is a dumb view: it rebuilds its visual tree from the model on every
    /// <see cref="DockManager.LayoutChanged"/> and forwards input to the <see cref="DragService"/>.
    /// It also participates in cross-host hit-testing coordinated by <see cref="DockWorkspace"/>.
    /// </summary>
    public class DockHost : TemplatedControl
    {
        public static readonly StyledProperty<DockManager?> ManagerProperty =
            AvaloniaProperty.Register<DockHost, DockManager?>(nameof(Manager));

        public static readonly StyledProperty<DockWorkspace?> WorkspaceProperty =
            AvaloniaProperty.Register<DockHost, DockWorkspace?>(nameof(Workspace));

        private readonly Grid _surface = new();
        private readonly DockOverlay _overlay = new();
        private readonly Dictionary<LayoutGroup, DockTabGroup> _groupControls = new();

        private DockManager? _subscribedManager;
        private bool _templateApplied;

        /// <summary>停靠预览虚影在目标面板对应方向上占据的比例（边缘起算）。</summary>
        private const double DropHintRatio = 0.2;

        static DockHost()
        {
            // Rebuild whenever the bound manager changes.
            ManagerProperty.Changed.AddClassHandler<DockHost>((h, _) => h.OnManagerChanged());
        }

        public DockManager? Manager
        {
            get => GetValue(ManagerProperty);
            set => SetValue(ManagerProperty, value);
        }

        public DockWorkspace? Workspace
        {
            get => GetValue(WorkspaceProperty);
            set => SetValue(WorkspaceProperty, value);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (!_templateApplied && e.NameScope?.Find("PART_Root") is Grid root)
            {
                root.Children.Add(_surface);
                root.Children.Add(_overlay);
                _templateApplied = true;
            }

            Rebuild();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (Workspace is not null)
            {
                Workspace.RegisterHost(this);
                if (TopLevel.GetTopLevel(this) is Window window)
                {
                    Workspace.EnsureOwner(window);
                }
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Workspace?.UnregisterHost(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var drag = Workspace?.DragService;
            if (drag is null || !drag.IsDragging)
            {
                return;
            }

            drag.Move(this.PointToScreen(e.GetPosition(this)));
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var drag = Workspace?.DragService;
            if (drag is null || !drag.IsDragging)
            {
                return;
            }

            drag.End(this.PointToScreen(e.GetPosition(this)));
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        /// <summary>Called by a tab header to start dragging <paramref name="panel"/>.</summary>
        internal void BeginDrag(DockManager manager, DockPanel panel, PointerPressedEventArgs e)
        {
            if (Workspace is null)
            {
                return;
            }

            var screen = this.PointToScreen(e.GetPosition(this));
            Workspace.DragService.Begin(manager, panel, this, screen);
            e.Pointer.Capture(this);
        }

        /// <summary>Converts a screen-space point into this host's local coordinates.</summary>
        internal Point? FromScreen(PixelPoint screen)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null)
            {
                return null;
            }

            var local = topLevel.PointToClient(screen);
            return topLevel.TranslatePoint(local, this);
        }

        /// <summary>Computes the drop target currently under the cursor, if any.</summary>
        internal DropTarget ResolveTarget(PixelPoint screen)
        {
            var inHost = FromScreen(screen);
            if (inHost is null || !new Rect(Bounds.Size).Contains(inHost.Value))
            {
                return DropTarget.Empty;
            }

            LayoutGroup? best = null;
            var bestRect = default(Rect);

            foreach (var (group, control) in _groupControls)
            {
                var origin = control.TranslatePoint(new Point(0, 0), this);
                if (origin is null)
                {
                    continue;
                }

                var rect = new Rect(origin.Value, control.Bounds.Size);
                if (!rect.Contains(inHost.Value))
                {
                    continue;
                }

                if (best is null || rect.Width * rect.Height < bestRect.Width * bestRect.Height)
                {
                    best = group;
                    bestRect = rect;
                }
            }

            if (best is null)
            {
                // No group under the cursor: empty host accepts a fill, otherwise this is "float".
                return Manager?.Root is null
                    ? new DropTarget(this, null, DockSide.Center)
                    : DropTarget.Empty;
            }

            var side = ComputeSide(inHost.Value - bestRect.Position, bestRect.Size);
            return new DropTarget(this, best, side);
        }

        internal void ShowDropHint(DropTarget target, PixelPoint screen)
        {
            if (target.Host is null)
            {
                _overlay.HideHint();
                return;
            }

            var inHost = FromScreen(screen) ?? default(Point);
            Rect rect;

            if (target.Group is null || !_groupControls.TryGetValue(target.Group, out var control))
            {
                rect = new Rect(Bounds.Size);
            }
            else
            {
                var origin = control.TranslatePoint(new Point(0, 0), this) ?? default(Point);
                var groupRect = new Rect(origin, control.Bounds.Size);
                rect = target.Side switch
                {
                    DockSide.Left => new Rect(groupRect.X, groupRect.Y, groupRect.Width * DropHintRatio, groupRect.Height),
                    DockSide.Right => new Rect(groupRect.X + groupRect.Width * (1 - DropHintRatio), groupRect.Y, groupRect.Width * DropHintRatio, groupRect.Height),
                    DockSide.Top => new Rect(groupRect.X, groupRect.Y, groupRect.Width, groupRect.Height * DropHintRatio),
                    DockSide.Bottom => new Rect(groupRect.X, groupRect.Y + groupRect.Height * (1 - DropHintRatio), groupRect.Width, groupRect.Height * DropHintRatio),
                    _ => groupRect
                };
            }

            _overlay.ShowHint(rect);
        }

        internal void HideHint() => _overlay.HideHint();

        internal void ShowGhost(DockPanel panel, PixelPoint screen)
        {
            var local = FromScreen(screen);
            if (local is null)
            {
                return;
            }

            _overlay.ShowGhost(panel.Title, local.Value - new Vector(20, 8));
        }

        internal void MoveGhost(PixelPoint screen)
        {
            var local = FromScreen(screen);
            if (local is null)
            {
                return;
            }

            _overlay.MoveGhost(local.Value - new Vector(20, 8));
        }

        internal void HideGhost() => _overlay.HideGhost();

        private void OnManagerChanged()
        {
            if (_subscribedManager is not null)
            {
                _subscribedManager.LayoutChanged -= OnLayoutChanged;
            }

            _subscribedManager = Manager;

            if (_subscribedManager is not null)
            {
                _subscribedManager.LayoutChanged += OnLayoutChanged;
            }

            Rebuild();
        }

        private void OnLayoutChanged(object? sender, EventArgs e) => Rebuild();

        private void Rebuild()
        {
            if (!_templateApplied)
            {
                return;
            }

            _surface.Children.Clear();
            _groupControls.Clear();

            if (Manager is null || Manager.Root is null)
            {
                _surface.Children.Add(CreateEmptyPlaceholder());
                return;
            }

            _surface.Children.Add(Build(Manager.Root));
        }

        private static Control CreateEmptyPlaceholder()
        {
            return new Border
            {
                Classes = { "dock-empty" },
                Child = new TextBlock
                {
                    Text = "No panels",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            };
        }

        private Control Build(LayoutNode node)
        {
            return node switch
            {
                LayoutSplit split => BuildSplit(split),
                LayoutGroup group => BuildGroup(group),
                _ => throw new InvalidOperationException("Unknown layout node: " + node.GetType())
            };
        }

        private Control BuildSplit(LayoutSplit split)
        {
            var grid = new Grid();
            var horizontal = split.Orientation == SplitOrientation.Horizontal;
            var ratio = Math.Clamp(split.Ratio, 0.05, 0.95);

            if (horizontal)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
            }
            else
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star) });
            }

            var first = Build(split.First);
            var second = Build(split.Second);

            var splitter = new DockSplitter
            {
                Orientation = split.Orientation,
                Cursor = horizontal
                    ? new Cursor(StandardCursorType.SizeWestEast)
                    : new Cursor(StandardCursorType.SizeNorthSouth),
            };

            if (horizontal)
            {
                splitter.Width = 6;
                Grid.SetColumn(first, 0);
                Grid.SetColumn(splitter, 1);
                Grid.SetColumn(second, 2);
            }
            else
            {
                splitter.Height = 6;
                Grid.SetRow(first, 0);
                Grid.SetRow(splitter, 1);
                Grid.SetRow(second, 2);
            }

            splitter.Attach(grid, split, Manager!);

            grid.Children.Add(first);
            grid.Children.Add(splitter);
            grid.Children.Add(second);
            return grid;
        }

        private Control BuildGroup(LayoutGroup group)
        {
            var tabGroup = new DockTabGroup(this, Manager!, group);
            _groupControls[group] = tabGroup;
            return tabGroup;
        }

        private static DockSide ComputeSide(Vector relative, Size size)
        {
            var nx = size.Width > 0 ? relative.X / size.Width : 0.5;
            var ny = size.Height > 0 ? relative.Y / size.Height : 0.5;
            var dx = Math.Abs(nx - 0.5);
            var dy = Math.Abs(ny - 0.5);

            if (dx < 0.25 && dy < 0.25)
            {
                return DockSide.Center;
            }

            if (dx > dy)
            {
                return nx < 0.5 ? DockSide.Left : DockSide.Right;
            }

            return ny < 0.5 ? DockSide.Top : DockSide.Bottom;
        }
    }
}

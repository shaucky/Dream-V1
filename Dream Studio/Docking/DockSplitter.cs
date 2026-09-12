using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// The draggable divider between the two children of a <see cref="LayoutSplit"/>. During a
    /// drag it updates the parent <see cref="Grid"/>'s star definitions live for smooth feedback;
    /// on release it commits the ratio through <see cref="DockManager"/> (the sole model mutator).
    /// </summary>
    public sealed class DockSplitter : TemplatedControl
    {
        public static readonly StyledProperty<SplitOrientation> OrientationProperty =
            StyledProperty<SplitOrientation>.Register<DockSplitter, SplitOrientation>(
                nameof(Orientation), SplitOrientation.Horizontal);

        public SplitOrientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        private Grid? _grid;
        private LayoutSplit? _split;
        private DockManager? _manager;
        private double _lastRatio = 0.5;
        private bool _dragging;

        internal void Attach(Grid grid, LayoutSplit split, DockManager manager)
        {
            _grid = grid;
            _split = split;
            _manager = manager;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            if (_grid is null || _split is null)
            {
                return;
            }

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _dragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            if (!_dragging || _grid is null)
            {
                return;
            }

            var p = e.GetPosition(_grid);
            var length = Orientation == SplitOrientation.Horizontal
                ? Math.Max(1, _grid.Bounds.Width)
                : Math.Max(1, _grid.Bounds.Height);

            var ratio = Math.Clamp((Orientation == SplitOrientation.Horizontal ? p.X : p.Y) / length, 0.05, 0.95);
            _lastRatio = ratio;
            ApplyRatio(ratio);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            e.Pointer.Capture(null);

            if (_split is not null && _manager is not null)
            {
                _manager.SetSplitRatio(_split, _lastRatio);
            }

            e.Handled = true;
        }

        private void ApplyRatio(double ratio)
        {
            if (_grid is null)
            {
                return;
            }

            if (Orientation == SplitOrientation.Horizontal && _grid.ColumnDefinitions.Count >= 3)
            {
                _grid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
                _grid.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
            }
            else if (Orientation == SplitOrientation.Vertical && _grid.RowDefinitions.Count >= 3)
            {
                _grid.RowDefinitions[0].Height = new GridLength(ratio, GridUnitType.Star);
                _grid.RowDefinitions[2].Height = new GridLength(1 - ratio, GridUnitType.Star);
            }
        }
    }
}

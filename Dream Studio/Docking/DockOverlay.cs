using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// A non-interactive canvas that draws the drop hint rectangle and the drag ghost over a
    /// <see cref="DockHost"/>. Children are positioned with attached Canvas properties.
    /// </summary>
    internal sealed class DockOverlay : Canvas
    {
        private static readonly IBrush HintFill = new SolidColorBrush(Color.FromArgb(0x33, 0x66, 0xCC, 0xFF));
        private static readonly IBrush HintBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0xCC, 0xFF));

        private readonly Border _hint;
        private readonly Border _ghost;

        public DockOverlay()
        {
            IsHitTestVisible = false;

            _hint = new Border
            {
                BorderBrush = HintBorder,
                Background = HintFill,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                IsVisible = false,
            };

            var ghostTitle = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(8, 4),
            };

            _ghost = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x2A, 0x2D, 0x34)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0xCC, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = ghostTitle,
                IsVisible = false,
            };

            Children.Add(_hint);
            Children.Add(_ghost);
        }

        public void ShowHint(Rect rect)
        {
            Position(_hint, rect);
            _hint.IsVisible = true;
        }

        public void HideHint() => _hint.IsVisible = false;

        public void ShowGhost(string title, Point position)
        {
            ((TextBlock)_ghost.Child!).Text = title;
            Canvas.SetLeft(_ghost, position.X);
            Canvas.SetTop(_ghost, position.Y);
            _ghost.IsVisible = true;
        }

        public void MoveGhost(Point position)
        {
            Canvas.SetLeft(_ghost, position.X);
            Canvas.SetTop(_ghost, position.Y);
        }

        public void HideGhost() => _ghost.IsVisible = false;

        private static void Position(Control c, Rect rect)
        {
            c.Width = rect.Width;
            c.Height = rect.Height;
            Canvas.SetLeft(c, rect.X);
            Canvas.SetTop(c, rect.Y);
        }
    }
}

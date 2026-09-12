using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dream.Studio.Panels.Project
{
    /// <summary>
    /// 切分对话框：询问网格的行/列数（对应 <see cref="SliceSettings"/> 的 "grid" 模式）。
    /// 将来新增切分方式时，在此增加模式选择即可，调用方无需改动。
    /// </summary>
    internal sealed class SliceDialog : Window
    {
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD6));
        private static readonly IBrush ButtonBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46));

        private readonly NumericUpDown _rows;
        private readonly NumericUpDown _columns;
        private SliceSettings? _result;

        private SliceDialog(string textureName, SliceSettings current)
        {
            Title = "Slice into Sprites";
            Width = 360;
            Height = 190;
            CanResize = false;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26));

            _rows = CreateSpinner(current.Rows);
            _columns = CreateSpinner(current.Columns);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                Margin = new Thickness(16, 12, 16, 0),
            };

            var label = new TextBlock
            {
                Text = textureName,
                FontSize = 12,
                Foreground = TextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 10),
            };
            Grid.SetRow(label, 0);
            Grid.SetColumnSpan(label, 4);
            grid.Children.Add(label);

            AddField(grid, 1, 0, "Rows:", _rows);
            AddField(grid, 1, 2, "Columns:", _columns);

            var ok = new Button
            {
                Content = "Slice",
                Width = 80,
                Height = 26,
                FontSize = 11,
                Background = ButtonBrush,
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            ok.Click += (_, _) =>
            {
                _result = new SliceSettings
                {
                    Mode = SliceSettings.GridMode,
                    Rows = (int)(_rows.Value ?? 1),
                    Columns = (int)(_columns.Value ?? 1),
                };
                Close();
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 26,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                Background = ButtonBrush,
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            cancel.Click += (_, _) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 16, 16, 16),
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(grid);
            Content = root;
        }

        /// <summary>显示对话框；用户确认返回切分设置，取消返回 null。</summary>
        public static async Task<SliceSettings?> PickAsync(Window owner, string textureName, SliceSettings current)
        {
            var dialog = new SliceDialog(textureName, current);
            await dialog.ShowDialog(owner);
            return dialog._result;
        }

        private static NumericUpDown CreateSpinner(int value) => new()
        {
            Minimum = 1,
            Maximum = 64,
            Increment = 1,
            Value = value,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4),
        };

        private static void AddField(Grid grid, int row, int column, string text, Control editor)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, column);
            grid.Children.Add(label);

            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, column + 1);
            grid.Children.Add(editor);
        }
    }
}

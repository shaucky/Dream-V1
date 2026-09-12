using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dream.Studio.Panels.Audio
{
    /// <summary>
    /// 全局音频设置面板：主音量 + 内置混音组（Music/SFX）音量滑条。
    /// 变化通过事件转发到引擎（AudioManager），实时生效于所有正在播放的声音。
    /// </summary>
    internal sealed class AudioPanel : UserControl
    {
        /// <summary>主音量变化（0..1）。</summary>
        public event Action<double>? MasterVolumeChanged;

        /// <summary>组音量变化。参数为组名与音量（0..1）。</summary>
        public event Action<string, double>? GroupVolumeChanged;

        // 初始化完成后置 true：避免滑条初值设置触发误报。
        private bool _ready;

        private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x26));
        private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC6));
        private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x99));

        public AudioPanel()
        {
            var master = CreateRow("Master", 1.0, v => MasterVolumeChanged?.Invoke(v));
            var music = CreateRow("Music", 1.0, v => GroupVolumeChanged?.Invoke("music", v));
            var sfx = CreateRow("SFX", 1.0, v => GroupVolumeChanged?.Invoke("sfx", v));

            var root = new StackPanel
            {
                Margin = new Thickness(10),
                Spacing = 14,
                Background = BgBrush,
                Children = { master, music, sfx },
            };
            Content = root;
            _ready = true;
        }

        private Control CreateRow(string label, double initial, Action<double> onChanged)
        {
            var caption = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 64,
            };

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = initial,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x6E, 0xA0)),
            };

            var valueText = new TextBlock
            {
                Text = initial.ToString("0%"),
                FontSize = 11,
                Foreground = DimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 40,
                TextAlignment = TextAlignment.Right,
            };

            slider.ValueChanged += (_, e) =>
            {
                if (!_ready) return;
                var v = Math.Max(0, Math.Min(1, e.NewValue));
                valueText.Text = v.ToString("0%");
                onChanged(v);
            };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            };
            Grid.SetColumn(caption, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(valueText, 2);
            row.Children.Add(caption);
            row.Children.Add(slider);
            row.Children.Add(valueText);
            return row;
        }
    }
}

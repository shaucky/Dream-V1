using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dream.Studio.Engine;

namespace Dream.Studio.Settings
{
    /// <summary>
    /// 设置对话框：AIR SDK 路径（编译/运行/打包都用它）与 Android SDK 路径
    /// （只有 Android App Bundle 目标需要）。
    /// 由 Help 菜单与标题栏上的 SDK 状态按钮调用。
    /// </summary>
    internal sealed partial class SettingsDialog : Window
    {
        private bool _accepted;

        public SettingsDialog()
        {
            InitializeComponent();
            SdkPathBox.Text = StudioSettings.Current.AirSdkPath;
            AndroidSdkBox.Text = StudioSettings.Current.AndroidSdkPath;
        }

        private async void Browse_Click(object? sender, RoutedEventArgs e)
            => await PickFolderAsync(SdkPathBox, "Select AIR SDK Directory");

        private async void BrowseAndroid_Click(object? sender, RoutedEventArgs e)
            => await PickFolderAsync(AndroidSdkBox, "Select Android SDK Directory");

        private async Task PickFolderAsync(TextBox target, string title)
        {
            var start = string.IsNullOrWhiteSpace(target.Text)
                ? null
                : await StorageProvider.TryGetFolderFromPathAsync(target.Text!);

            var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                SuggestedStartLocation = start,
            });

            if (folder.Count > 0 && folder[0].Path.LocalPath is { } path)
                target.Text = path;
        }

        private void Ok_Click(object? sender, RoutedEventArgs e)
        {
            _accepted = true;
            Close();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(System.EventArgs e)
        {
            // 先落盘再调 base：base 会触发 Closed 事件（Avalonia 据此完成 ShowDialog），
            // 调用方的后续动作——刷新 SDK 状态按钮、按新 SDK 重编译——就在那时执行，
            // 顺序反了它们读到的还是旧路径，表现为状态按钮慢一拍、重编译仍在用上一个 SDK。
            if (_accepted)
            {
                var settings = StudioSettings.Current;
                settings.AirSdkPath = SdkPathBox.Text?.Trim() ?? "";
                settings.AndroidSdkPath = AndroidSdkBox.Text?.Trim() ?? "";
                settings.Save();

                // SDK 派生数据的缓存按路径作键：换了路径要清；同一路径下换了 SDK 版本（就地升级/
                // 覆盖）路径没变，也必须清，否则构建面板会一直按旧 SDK 的数据渲染。
                AdtCapabilitiesProbe.Invalidate();
                IconSizeCatalog.Invalidate();
                DescriptorSchema.Invalidate();
            }

            base.OnClosed(e);
        }

        /// <summary>模态显示设置对话框；返回是否点击了 OK。</summary>
        public static async Task<bool> ShowAsync(Window owner)
        {
            var dialog = new SettingsDialog();
            await dialog.ShowDialog(owner);
            return dialog._accepted;
        }
    }
}

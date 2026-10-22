using System.Windows;

namespace AntoCopy
{
    /// <summary>设置 WiFi 断开后键鼠空闲多少秒才息屏。</summary>
    public partial class ScreenOffIdleWindow : Window
    {
        internal ScreenOffIdleWindow()
        {
            InitializeComponent();
            ThemeManager.Attach(this);

            MainWindow.AttachNumericInput(SecondsInput, WifiScreenOff.MaxIdleSeconds);
            SecondsInput.Text = WifiScreenOff.IdleSeconds.ToString();
            Loaded += (_, _) =>
            {
                SecondsInput.Focus();
                SecondsInput.SelectAll();
            };

            OkButton.Click += (_, _) => Confirm();
        }

        private void Confirm()
        {
            if (!int.TryParse(SecondsInput.Text.Trim(), out int seconds) || seconds < 0 || seconds > WifiScreenOff.MaxIdleSeconds)
            {
                MessageBox.Show(this, $"请输入 0 到 {WifiScreenOff.MaxIdleSeconds} 的整数秒数。", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SecondsInput.Focus();
                SecondsInput.SelectAll();
                return;
            }

            WifiScreenOff.IdleSeconds = seconds;
            DialogResult = true;
        }
    }
}

using System;
using System.Windows;
using System.Windows.Threading;

namespace AntoCopy
{
    /// <summary>
    /// 自动息屏前在屏幕右下角显示的倒计时提示：点击提示取消，倒计时走完触发 Finished。
    /// 不抢焦点，免得打断正在输入的程序。
    /// </summary>
    public partial class ScreenOffToast : Window
    {
        private const int ScreenMargin = 12;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private int _remaining;
        private bool _done;

        /// <summary>倒计时走完（提示随之关闭）。</summary>
        public event Action? Finished;

        /// <summary>用户点击了提示（提示随之关闭）。</summary>
        public event Action? Cancelled;

        internal ScreenOffToast(int seconds)
        {
            InitializeComponent();

            _remaining = seconds;
            UpdateText();

            _timer.Tick += (_, _) =>
            {
                if (--_remaining > 0)
                {
                    UpdateText();
                    return;
                }

                End();
                Finished?.Invoke();
            };

            MouseLeftButtonUp += (_, _) =>
            {
                if (_done)
                    return;

                End();
                Cancelled?.Invoke();
            };

            // 高度由内容决定，布局完成后再贴到工作区（不含任务栏）右下角。
            SizeChanged += (_, _) =>
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth - ScreenMargin;
                Top = area.Bottom - ActualHeight - ScreenMargin;
            };

            Loaded += (_, _) => _timer.Start();
        }

        /// <summary>不触发任何事件地关闭（WiFi 恢复、关掉功能时）。</summary>
        public void Dismiss() => End();

        private void UpdateText() => CountdownText.Text = $"{_remaining} 秒后关闭显示器";

        private void End()
        {
            if (_done)
                return;

            _done = true;
            _timer.Stop();
            Close();
        }
    }
}

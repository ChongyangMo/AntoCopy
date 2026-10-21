using System;
using System.Collections.Generic;
using System.Windows;

namespace AntoCopy
{
    /// <summary>为单个源文件夹设置定时/倒计时频率：跟随全局、自定义或不参与。</summary>
    public partial class SourceRuleWindow : Window
    {
        private const int MaxEveryDays = 365;

        /// <summary>点“确定”后的设置；取消时为 null。</summary>
        internal SourceRule? Result { get; private set; }

        internal SourceRuleWindow(string sourcePath, SourceRule rule)
        {
            InitializeComponent();
            ThemeManager.Attach(this);

            Title = $"备份频率 - {SourceGroups.DisplayName(sourcePath)}";
            PathText.Text = sourcePath;
            PathText.ToolTip = sourcePath;

            ScheduledGlobalRadio.Content = TimeSpan.TryParse(AppSettings.RunTime, out _)
                ? $"跟随全局（每天 {AppSettings.RunTime}）"
                : "跟随全局（当前未启用每日定时）";
            CountdownGlobalRadio.Content = AppSettings.CountdownSeconds > 0
                ? $"跟随全局（每 {SourceRule.FormatDuration(AppSettings.CountdownSeconds)}）"
                : "跟随全局（当前未设置倒计时间隔）";

            MainWindow.AttachNumericInput(EveryDaysInput, MaxEveryDays);
            MainWindow.AttachNumericInput(CountdownHoursInput, 99_999);
            MainWindow.AttachNumericInput(CountdownMinutesInput, 59);

            Load(rule);

            OkButton.Click += (_, _) => Confirm();
            ResetButton.Click += (_, _) => Load(new SourceRule());
        }

        private void Load(SourceRule rule)
        {
            (rule.ScheduledMode switch
            {
                RuleMode.Custom => ScheduledCustomRadio,
                RuleMode.Off => ScheduledOffRadio,
                _ => ScheduledGlobalRadio
            }).IsChecked = true;
            (rule.CountdownMode switch
            {
                RuleMode.Custom => CountdownCustomRadio,
                RuleMode.Off => CountdownOffRadio,
                _ => CountdownGlobalRadio
            }).IsChecked = true;

            // 未自定义过时预填全局值，切到“自定义”后在此基础上改。
            EveryDaysInput.Text = Math.Max(1, rule.ScheduledEveryDays).ToString();
            TimesInput.Text = rule.ScheduledTimes.Count > 0
                ? string.Join(", ", rule.ScheduledTimes)
                : TimeSpan.TryParse(AppSettings.RunTime, out _) ? AppSettings.RunTime : "02:00";

            int seconds = rule.CountdownSeconds > 0
                ? rule.CountdownSeconds
                : AppSettings.CountdownSeconds > 0 ? AppSettings.CountdownSeconds : 2 * 3600;
            CountdownHoursInput.Text = (seconds / 3600).ToString();
            CountdownMinutesInput.Text = (seconds % 3600 / 60).ToString();
        }

        private void Confirm()
        {
            SourceRule rule = new();

            if (ScheduledCustomRadio.IsChecked == true)
            {
                if (!int.TryParse(EveryDaysInput.Text.Trim(), out int everyDays) || everyDays < 1 || everyDays > MaxEveryDays)
                {
                    Warn($"“每几天”请输入 1 到 {MaxEveryDays} 的整数。", EveryDaysInput);
                    return;
                }

                if (!SourceRule.TryParseTimes(TimesInput.Text, out List<string> times))
                {
                    Warn("执行时间格式错误，请输入 HH:mm，多个用逗号分隔，例如 02:00, 14:00。", TimesInput);
                    return;
                }

                rule.ScheduledMode = RuleMode.Custom;
                rule.ScheduledEveryDays = everyDays;
                rule.ScheduledTimes = times;
            }
            else if (ScheduledOffRadio.IsChecked == true)
            {
                rule.ScheduledMode = RuleMode.Off;
            }

            if (CountdownCustomRadio.IsChecked == true)
            {
                int.TryParse(CountdownHoursInput.Text.Trim(), out int hours);
                int.TryParse(CountdownMinutesInput.Text.Trim(), out int minutes);
                long seconds = hours * 3600L + minutes * 60L;
                if (seconds <= 0 || seconds > int.MaxValue)
                {
                    Warn("倒计时间隔须大于 0。", CountdownHoursInput);
                    return;
                }

                rule.CountdownMode = RuleMode.Custom;
                rule.CountdownSeconds = (int)seconds;
            }
            else if (CountdownOffRadio.IsChecked == true)
            {
                rule.CountdownMode = RuleMode.Off;
            }

            Result = rule;
            DialogResult = true;
        }

        private void Warn(string message, System.Windows.Controls.TextBox focus)
        {
            MessageBox.Show(this, message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            focus.Focus();
            focus.SelectAll();
        }
    }
}

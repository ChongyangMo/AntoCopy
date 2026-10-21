using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AntoCopy
{
    public partial class MainWindow : Window
    {
        private const string Subtitle = "文件夹定时 7z 打包备份";
        private const int MaxLogLines = 800;
        private const int MaxKeepCopies = 10000;

        // 布局尺寸（WPF 与设备无关的像素）。
        private const double Pad = 12;
        private const double Gap = 10;
        private const double TileHeight = 58;
        private const double ResourceTileHeight = 64;
        private const double ScheduledHeight = 156;
        private const double CountdownHeight = 212;
        private const double TargetHeight = 108;
        private const double WideMinWidth = 760;
        private const double WideLogMinHeight = 120;
        private const double NarrowMinWidth = 400;
        private const double NarrowSourceHeight = 270;
        private const double NarrowLogHeight = 220;

        /// <summary>与 MainWindow.xaml 中 ScrollBar 样式的宽/高一致。</summary>
        private const double ScrollBarSize = 10;

        private const double WideContentMinHeight =
            Pad * 2 + TileHeight + Gap + ResourceTileHeight + Gap + ScheduledHeight + Gap + CountdownHeight + Gap + WideLogMinHeight;

        private const double NarrowContentHeight =
            Pad * 2 + TileHeight * 2 + Gap + Gap + ResourceTileHeight * 2 + Gap + Gap + NarrowSourceHeight + Gap + TargetHeight + Gap +
            ScheduledHeight + Gap + CountdownHeight + Gap + NarrowLogHeight;

        private readonly ObservableCollection<SourceItem> _sources = new();
        private readonly ObservableCollection<LogLine> _logLines = new();
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _saveHintTimer;
        private readonly DispatcherTimer _progressTimer;
        private volatile bool _progressDirty = true;

        private Dictionary<string, bool> _sourceExists = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, SourceBackupStats> _sourceStats = new(StringComparer.OrdinalIgnoreCase);
        private int _dashboardRefreshRunning;
        private bool _dashboardRefreshPending;
        private bool _working;
        private long _lastLogSeq;
        private bool _scrollToEndQueued;
        private bool? _narrowLayout;

        private sealed class DashboardSnapshot
        {
            public long TotalBytes { get; set; }
            public int ScheduledCopies { get; set; }
            public int CountdownCopies { get; set; }

            /// <summary>键为压缩包名（即源文件夹名，不区分大小写）。</summary>
            public Dictionary<string, SourceBackupStats> SourceStats { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class SourceBackupStats
        {
            public DateTime LastBackup { get; set; }
            public int ScheduledCopies { get; set; }
            public int CountdownCopies { get; set; }
            public int TotalCopies => ScheduledCopies + CountdownCopies;
        }

        public MainWindow()
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            Icon = AppIcon.GetImageSource();
            SubtitleText.Text = Subtitle;
            SourceList.ItemsSource = _sources;
            LogList.ItemsSource = _logLines;

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statusTimer.Tick += (_, _) =>
            {
                if (IsVisible)
                    UpdateCountdownStatus();
            };
            _statusTimer.Start();

            _saveHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _saveHintTimer.Tick += (_, _) =>
            {
                _saveHintTimer.Stop();
                SaveHintText.Text = string.Empty;
            };

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _progressTimer.Tick += (_, _) =>
            {
                if (!_progressDirty || !IsVisible)
                    return;
                _progressDirty = false;
                RenderBackupProgress();
            };
            _progressTimer.Start();

            WireEvents();

            Loaded += (_, _) =>
            {
                LoadSettingsToForm();
                RefreshDashboard();
            };
            IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true)
                {
                    ReloadLog();
                    RefreshDashboard();
                    if (ResourceMonitor.Latest is ResourceSnapshot latest)
                        ApplyResourceSnapshot(latest);
                    _progressDirty = true;
                }
            };
            Scroller.SizeChanged += (_, _) => UpdateContentLayout();

            AppLogger.MessageLogged += AppendLog;
            CountdownService.StateChanged += HandleCountdownStateChanged;
            AppEvents.CopyCompleted += HandleCopyCompleted;
            WifiScreenOff.EnabledChanged += HandleWifiScreenOffChanged;
            ThemeManager.Changed += HandleThemeChanged;
            ResourceMonitor.Updated += HandleResourceUpdated;
            BackupProgress.Changed += HandleBackupProgressChanged;
        }

        #region 自适应布局

        /// <summary>
        /// 宽窗口：统计 4 列 + 左右两栏，日志占满剩余高度；窄窗口：统计 2×2 + 全部卡片单列。
        /// 内容区按窗口大小设定尺寸，比最小尺寸小时由 ScrollViewer 出滚动条。
        /// </summary>
        private void UpdateContentLayout()
        {
            double width = Scroller.ActualWidth;
            double height = Scroller.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            bool narrow = width < WideMinWidth;
            if (_narrowLayout != narrow)
                ApplyLayout(narrow);

            double minWidth = narrow ? NarrowMinWidth : WideMinWidth;
            double minHeight = narrow ? NarrowContentHeight : WideContentMinHeight;
            bool needVertical = minHeight > height;
            bool needHorizontal = minWidth > width - (needVertical ? ScrollBarSize : 0);
            if (needHorizontal && !needVertical)
                needVertical = minHeight > height - ScrollBarSize;

            double availableWidth = width - (needVertical ? ScrollBarSize : 0);
            double availableHeight = height - (needHorizontal ? ScrollBarSize : 0);

            ContentRoot.Width = Math.Max(availableWidth, minWidth) - Pad * 2;
            ContentRoot.Height = (narrow ? minHeight : Math.Max(availableHeight, minHeight)) - Pad * 2;
        }

        private void ApplyLayout(bool narrow)
        {
            _narrowLayout = narrow;
            SubtitleText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            TilesGrid.Columns = narrow ? 2 : 4;
            ResourceTilesGrid.Columns = narrow ? 2 : 4;

            BodyGrid.ColumnDefinitions.Clear();
            BodyGrid.RowDefinitions.Clear();
            if (narrow)
            {
                BodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                BodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Gap) });
                BodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                PlaceInBody(LeftColumn, row: 0, column: 0);
                PlaceInBody(RightColumn, row: 2, column: 0);
                BodyGrid.Height = double.NaN;
                SourceRow.Height = new GridLength(NarrowSourceHeight);
                CountdownRow.Height = new GridLength(CountdownHeight);
                LogRow.Height = new GridLength(NarrowLogHeight);
            }
            else
            {
                BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56, GridUnitType.Star) });
                BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
                BodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44, GridUnitType.Star) });
                PlaceInBody(LeftColumn, row: 0, column: 0);
                PlaceInBody(RightColumn, row: 0, column: 2);
                BodyGrid.Height = ScheduledHeight + Gap + CountdownHeight;
                SourceRow.Height = new GridLength(1, GridUnitType.Star);
                CountdownRow.Height = new GridLength(1, GridUnitType.Star);
                LogRow.Height = new GridLength(1, GridUnitType.Star);
            }
        }

        private static void PlaceInBody(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
        }

        #endregion

        private void WireEvents()
        {
            SourceBrowseButton.Click += (_, _) => BrowseSourceInput();
            AddSourceButton.Click += (_, _) => AddSourcePathFromInput();
            RemoveSourceButton.Click += (_, _) => RemoveSelectedSourcePath();
            ClearSourcesButton.Click += (_, _) => ClearSourcePaths();
            OpenSourceButton.Click += (_, _) => OpenSourcePath();
            SourceRuleButton.Click += (_, _) => EditSourceRule();
            SourceRuleMenu.Click += (_, _) => EditSourceRule();
            OpenSourceMenu.Click += (_, _) => OpenSourcePath();
            RemoveSourceMenu.Click += (_, _) => RemoveSelectedSourcePath();
            SourceList.MouseDoubleClick += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject origin &&
                    ItemsControl.ContainerFromElement(SourceList, origin) is ListBoxItem)
                    EditSourceRule();
            };
            DestBrowseButton.Click += (_, _) => BrowseFolder(DestInput, "选择目标文件夹");
            OpenDestButton.Click += (_, _) => OpenDirectoryPath(DestInput.Text.Trim());
            SaveButton.Click += (_, _) => SaveClicked();
            CopyNowButton.Click += async (_, _) => await CopyNowClickedAsync();
            StartCountdownButton.Click += (_, _) => StartCountdownClicked();
            StopCountdownButton.Click += (_, _) => CountdownService.Stop();
            RefreshButton.Click += (_, _) => RefreshDashboard();
            ClearLogButton.Click += (_, _) => ClearLog();

            SourceInput.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;
                e.Handled = true;
                AddSourcePathFromInput();
            };
            SourceList.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Delete)
                    RemoveSelectedSourcePath();
            };
            SourceList.SelectionChanged += (_, _) =>
            {
                if (SourceList.SelectedItem is SourceItem item)
                    SourceInput.Text = item.Path;
            };
            SourceList.DragOver += (_, e) =>
            {
                e.Effects = GetDroppedDirectories(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            SourceList.Drop += (_, e) =>
            {
                foreach (string directory in GetDroppedDirectories(e))
                    AddSourcePath(directory);
            };

            CountdownSecondsInput.TextChanged += (_, _) => UpdateCountdownDurationHint();
            AttachNumericInput(CountdownSecondsInput, int.MaxValue);
            AttachNumericInput(ScheduledKeepInput, MaxKeepCopies);
            AttachNumericInput(CountdownKeepInput, MaxKeepCopies);

            LogList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopyLogLines(selectedOnly: true)));
            CopySelectedLogMenu.Click += (_, _) => CopyLogLines(selectedOnly: true);
            CopyAllLogMenu.Click += (_, _) => CopyLogLines(selectedOnly: false);

            WireMenuBar();
        }

        private void WireMenuBar()
        {
            // Ctrl+S 由 ApplicationCommands.Save 自带的快捷键触发。
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Save,
                (_, _) => SaveClicked(), (_, e) => e.CanExecute = !_working));

            SaveMenu.Click += (_, _) => SaveClicked();
            OpenDestMenu.Click += (_, _) => OpenDirectoryPath(DestInput.Text.Trim());
            OpenLogDirMenu.Click += (_, _) => Program.OpenLogDirectory();
            HideToTrayMenu.Click += (_, _) => HideToTray();
            ExitMenu.Click += (_, _) => Program.ExitApplication();

            CopyNowMenu.Click += async (_, _) => await CopyNowClickedAsync();
            StartCountdownMenu.Click += (_, _) => StartCountdownClicked();
            StopCountdownMenu.Click += (_, _) => CountdownService.Stop();
            ScreenOffNowMenu.Click += async (_, _) => await WifiScreenOff.TurnOffNowAsync();

            WifiScreenOffMenu.IsChecked = WifiScreenOff.Enabled;
            WifiScreenOffMenu.Click += (_, _) => WifiScreenOff.Enabled = WifiScreenOffMenu.IsChecked;

            ThemeSystemMenu.Click += (_, _) => ThemeManager.Select(AppTheme.System);
            ThemeLightMenu.Click += (_, _) => ThemeManager.Select(AppTheme.Light);
            ThemeDarkMenu.Click += (_, _) => ThemeManager.Select(AppTheme.Dark);
            UpdateThemeMenuChecks();

            // 打开菜单时与界面上对应按钮的可用状态保持一致。
            FileMenu.SubmenuOpened += (_, _) => SaveMenu.IsEnabled = !_working;
            ActionMenu.SubmenuOpened += (_, _) =>
            {
                CopyNowMenu.IsEnabled = CopyNowButton.IsEnabled;
                StartCountdownMenu.IsEnabled = StartCountdownButton.IsEnabled;
                StopCountdownMenu.IsEnabled = StopCountdownButton.IsEnabled;
            };
        }

        /// <summary>托盘菜单切换开关时同步主窗口菜单的勾选。</summary>
        private void HandleWifiScreenOffChanged() => WifiScreenOffMenu.IsChecked = WifiScreenOff.Enabled;

        private void HandleThemeChanged() => UpdateThemeMenuChecks();

        private void UpdateThemeMenuChecks()
        {
            ThemeSystemMenu.IsChecked = ThemeManager.Selected == AppTheme.System;
            ThemeLightMenu.IsChecked = ThemeManager.Selected == AppTheme.Light;
            ThemeDarkMenu.IsChecked = ThemeManager.Selected == AppTheme.Dark;
        }

        /// <summary>只允许输入数字；↑/↓ 键在 [0, max] 内加减 1。</summary>
        internal static void AttachNumericInput(TextBox box, int max)
        {
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            DataObject.AddPastingHandler(box, (_, e) =>
            {
                if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.Trim().All(char.IsDigit))
                    e.CancelCommand();
            });
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Up && e.Key != Key.Down)
                    return;
                int.TryParse(box.Text.Trim(), out int value);
                value = Math.Clamp(value + (e.Key == Key.Up ? 1 : -1), 0, max);
                box.Text = value.ToString();
                box.CaretIndex = box.Text.Length;
                e.Handled = true;
            };
        }

        private void LoadSettingsToForm()
        {
            SetSourcePaths(AppSettings.SourcePaths);
            DestInput.Text = AppSettings.DestPath;
            RunTimeInput.Text = AppSettings.RunTime;
            ScheduledFolderInput.Text = AppSettings.ScheduledFolderName;
            CountdownFolderInput.Text = AppSettings.CountdownFolderName;
            ScheduledKeepInput.Text = Math.Min(MaxKeepCopies, AppSettings.ScheduledKeepCopies).ToString();
            CountdownSecondsInput.Text = AppSettings.CountdownSeconds.ToString();
            CountdownKeepInput.Text = Math.Min(MaxKeepCopies, AppSettings.CountdownKeepCopies).ToString();
            SkipWindowCheck.IsChecked = AppSettings.CountdownSkipEnabled;
            SkipStartInput.Text = AppSettings.CountdownSkipStart;
            SkipEndInput.Text = AppSettings.CountdownSkipEnd;
            AutoStartCheck.IsChecked = AppSettings.AutoStart;
        }

        #region 源路径列表

        private void SetSourcePaths(string[] sourcePaths)
        {
            _sources.Clear();
            foreach (string sourcePath in sourcePaths)
                _sources.Add(new SourceItem(sourcePath));
            OnSourcesChanged();
        }

        private string[] GetSourcePaths()
        {
            return _sources
                .Select(item => item.Path.Trim())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void BrowseSourceInput() => BrowseFolder(SourceInput, "选择源文件夹");

        private void AddSourcePathFromInput()
        {
            string sourcePath = SourceInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(sourcePath))
                return;

            if (!AddSourcePath(sourcePath))
            {
                SourceInput.SelectAll();
                SourceInput.Focus();
                return;
            }

            SourceInput.Clear();
            SourceInput.Focus();
        }

        private bool AddSourcePath(string sourcePath)
        {
            if (_sources.Any(item => string.Equals(item.Path, sourcePath, StringComparison.OrdinalIgnoreCase)))
                return false;

            var item = new SourceItem(sourcePath);
            _sources.Add(item);
            SourceList.SelectedItem = item;
            SourceList.ScrollIntoView(item);
            OnSourcesChanged();
            return true;
        }

        private void RemoveSelectedSourcePath()
        {
            int index = SourceList.SelectedIndex;
            if (index < 0)
                return;

            _sources.RemoveAt(index);
            if (_sources.Count > 0)
                SourceList.SelectedIndex = Math.Min(Math.Max(0, index - 1), _sources.Count - 1);
            else
                SourceInput.Clear();

            OnSourcesChanged();
        }

        private void ClearSourcePaths()
        {
            _sources.Clear();
            OnSourcesChanged();
        }

        private void OnSourcesChanged()
        {
            SourceTitleText.Text = $"源文件夹（{_sources.Count}）";
            RefreshSourceItems();
            _progressDirty = true;
            RefreshSourceExistence();
        }

        /// <summary>后台检查各源路径是否存在（网络路径可能较慢），不存在的在列表里标红。</summary>
        private void RefreshSourceExistence()
        {
            string[] paths = _sources.Select(item => item.Path).ToArray();
            Task.Run(() => paths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(path => path, Directory.Exists, StringComparer.OrdinalIgnoreCase))
                .ContinueWith(task =>
                {
                    if (task.Status != TaskStatus.RanToCompletion)
                        return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        _sourceExists = task.Result;
                        RefreshSourceItems();
                    });
                }, TaskScheduler.Default);
        }

        /// <summary>按最新的“路径是否存在”和“备份统计”更新每一行的显示。</summary>
        private void RefreshSourceItems()
        {
            foreach (SourceItem item in _sources)
            {
                bool missing = _sourceExists.TryGetValue(item.Path, out bool exists) && !exists;
                SourceBackupStats? stats = GetSourceStats(item.Path);
                bool neverBackedUp = stats == null || stats.TotalCopies == 0;
                string summary = neverBackedUp
                    ? "尚未备份"
                    : $"上次 {FormatRelativeTime(stats!.LastBackup)} · {stats.TotalCopies} 份";
                string detail = neverBackedUp
                    ? "目标目录中还没有它的备份"
                    : $"最近备份：{stats!.LastBackup:yyyy-MM-dd HH:mm:ss}\n" +
                      $"现存备份：共 {stats.TotalCopies} 份（定时 {stats.ScheduledCopies} 份，倒计时 {stats.CountdownCopies} 份）";
                SourceRule rule = AppSettings.GetSourceRule(item.Path);
                if (!rule.IsDefault)
                    detail += $"\n备份频率：{rule.LongText}";
                item.Update(missing, summary, neverBackedUp, rule.ShortText, $"{item.Path}\n{detail}");
            }
        }

        private SourceBackupStats? GetSourceStats(string sourcePath)
        {
            try
            {
                return _sourceStats.TryGetValue(CopyHelper.GetSourceFolderName(sourcePath), out SourceBackupStats? stats)
                    ? stats
                    : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        private static string FormatRelativeTime(DateTime time)
        {
            DateTime today = DateTime.Today;
            if (time.Date == today)
                return $"今天 {time:HH:mm}";
            if (time.Date == today.AddDays(-1))
                return $"昨天 {time:HH:mm}";
            return time.Year == today.Year ? $"{time:MM-dd HH:mm}" : $"{time:yyyy-MM-dd}";
        }

        private static string[] GetDroppedDirectories(DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] dropped)
                return Array.Empty<string>();

            return dropped.Where(Directory.Exists).ToArray();
        }

        private void OpenSourcePath()
        {
            string path = (SourceList.SelectedItem as SourceItem)?.Path ?? SourceInput.Text.Trim();
            OpenDirectoryPath(path);
        }

        /// <summary>为选中的文件夹单独设置定时/倒计时频率；确定后立即生效（不需要再点保存设置）。</summary>
        private void EditSourceRule()
        {
            if (SourceList.SelectedItem is not SourceItem item)
            {
                MessageBox.Show(this, "请先在列表中选中一个文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SourceRuleWindow dialog = new(item.Path, AppSettings.GetSourceRule(item.Path)) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result == null)
                return;

            AppSettings.SetSourceRule(item.Path, dialog.Result);
            string name = SourceGroups.DisplayName(item.Path);
            AppLogger.Log(dialog.Result.IsDefault
                ? $"「{name}」的备份频率已恢复为跟随全局。"
                : $"「{name}」的备份频率已更新：{dialog.Result.LongText}。");

            if (!AppSettings.SourcePaths.Contains(item.Path.Trim(), StringComparer.OrdinalIgnoreCase))
                ShowSaveHint("该文件夹尚未保存，点“保存设置”后频率才会生效");

            AppEvents.RaiseSettingsChanged();
            RefreshSourceItems();
        }

        #endregion

        private void BrowseFolder(TextBox target, string title)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (dialog.ShowDialog(this) == true)
                target.Text = dialog.FolderName;
        }

        private void SaveClicked()
        {
            if (!TrySaveSettings(showSuccessMessage: true))
                return;

            RefreshDashboard();
        }

        private async Task CopyNowClickedAsync()
        {
            if (!TrySaveSettings(showSuccessMessage: false))
                return;

            SetWorkingState(true);
            AppLogger.Log("手动触发定时模式复制。");

            try
            {
                await Task.Run(() => CopyHelper.RunScheduledCopy());
            }
            finally
            {
                SetWorkingState(false);
                RefreshDashboard();
            }
        }

        private void StartCountdownClicked()
        {
            if (!TrySaveSettings(showSuccessMessage: false))
                return;

            if (!CountdownService.HasRunnableJobs())
            {
                ShowWarning("请先填写大于 0 的倒计时秒数，或为文件夹设置自定义倒计时频率。");
                return;
            }

            CountdownService.Start();
            RefreshDashboard();
        }

        private bool TrySaveSettings(bool showSuccessMessage)
        {
            AddSourcePathFromInput();

            string[] sourcePaths = GetSourcePaths();
            string runTimeText = RunTimeInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(runTimeText) && !TimeSpan.TryParse(runTimeText, out _))
            {
                ShowWarning("每日执行时间格式错误，请输入 HH:mm，例如 02:00。留空则不启用每日定时。");
                return false;
            }

            if (!TryParseNumber(CountdownSecondsInput.Text, int.MaxValue, out int countdownSeconds))
            {
                ShowWarning("倒计时秒数格式错误，请输入大于等于 0 的整数。");
                return false;
            }

            if (!TryParseNumber(ScheduledKeepInput.Text, MaxKeepCopies, out int scheduledKeep) ||
                !TryParseNumber(CountdownKeepInput.Text, MaxKeepCopies, out int countdownKeep))
            {
                ShowWarning($"保留份数格式错误，请输入 0 到 {MaxKeepCopies} 的整数。");
                return false;
            }

            if (!SourceRule.TryParseTime(SkipStartInput.Text, out _) || !SourceRule.TryParseTime(SkipEndInput.Text, out _))
            {
                ShowWarning("跳过时段格式错误，请输入 HH:mm，例如 00:00 – 07:59；终点早于起点表示跨越午夜。");
                return false;
            }

            AppSettings.SourcePaths = sourcePaths;
            AppSettings.PruneSourceRules(sourcePaths);
            AppSettings.DestPath = DestInput.Text.Trim();
            AppSettings.RunTime = runTimeText;
            AppSettings.ScheduledFolderName = ScheduledFolderInput.Text.Trim();
            AppSettings.CountdownFolderName = CountdownFolderInput.Text.Trim();
            AppSettings.ScheduledKeepCopies = scheduledKeep;
            AppSettings.CountdownSeconds = countdownSeconds;
            AppSettings.CountdownKeepCopies = countdownKeep;
            AppSettings.CountdownSkipEnabled = SkipWindowCheck.IsChecked == true;
            AppSettings.CountdownSkipStart = SkipStartInput.Text;
            AppSettings.CountdownSkipEnd = SkipEndInput.Text;
            AppSettings.AutoStart = AutoStartCheck.IsChecked == true;

            ScheduledFolderInput.Text = AppSettings.ScheduledFolderName;
            CountdownFolderInput.Text = AppSettings.CountdownFolderName;
            ScheduledKeepInput.Text = AppSettings.ScheduledKeepCopies.ToString();
            CountdownSecondsInput.Text = AppSettings.CountdownSeconds.ToString();
            CountdownKeepInput.Text = AppSettings.CountdownKeepCopies.ToString();
            SkipWindowCheck.IsChecked = AppSettings.CountdownSkipEnabled;
            SkipStartInput.Text = AppSettings.CountdownSkipStart;
            SkipEndInput.Text = AppSettings.CountdownSkipEnd;

            AppLogger.Log("配置已保存。");
            AppEvents.RaiseSettingsChanged();
            RefreshSourceItems();

            if (showSuccessMessage)
                ShowSaveHint("✓ 设置已保存");

            return true;
        }

        /// <summary>空白视为 0；必须是 [0, max] 内的整数。</summary>
        private static bool TryParseNumber(string text, int max, out int value)
        {
            text = text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                value = 0;
                return true;
            }

            return int.TryParse(text, out value) && value >= 0 && value <= max;
        }

        private void ShowWarning(string message)
        {
            MessageBox.Show(this, message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ShowSaveHint(string text)
        {
            SaveHintText.Text = text;
            _saveHintTimer.Stop();
            _saveHintTimer.Start();
        }

        #region 统计

        private void RefreshDashboard()
        {
            UpdateCountdownStatus();
            RefreshSourceExistence();
            QueueDashboardRefresh(forceWhenHidden: false);
        }

        private void QueueDashboardRefresh(bool forceWhenHidden)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => QueueDashboardRefresh(forceWhenHidden));
                return;
            }

            UpdateCountdownStatus();

            if (!forceWhenHidden && !IsVisible)
            {
                _dashboardRefreshPending = true;
                return;
            }

            _dashboardRefreshPending = false;

            if (Interlocked.Exchange(ref _dashboardRefreshRunning, 1) == 1)
            {
                _dashboardRefreshPending = true;
                return;
            }

            string targetRoot = DestInput.Text.Trim();
            string scheduledFolder = NormalizeFolderName(ScheduledFolderInput.Text, "定时模式");
            string countdownFolder = NormalizeFolderName(CountdownFolderInput.Text, "倒计时模式");

            Task.Run(() => CollectDashboardSnapshot(targetRoot, scheduledFolder, countdownFolder))
                .ContinueWith(task => Dispatcher.BeginInvoke(() => ApplyDashboardRefreshResult(task)),
                    TaskScheduler.Default);
        }

        private static DashboardSnapshot CollectDashboardSnapshot(
            string targetRoot,
            string scheduledFolder,
            string countdownFolder)
        {
            DashboardSnapshot snapshot = new();

            try
            {
                if (Directory.Exists(targetRoot))
                {
                    // 只统计本程序管理的两个模式子目录：目标可能是整盘根目录（如 D:\），
                    // 遍历整盘既慢又会因 System Volume Information 等无权限目录抛异常导致全部归零。
                    string scheduledRoot = Path.Combine(targetRoot, scheduledFolder);
                    if (Directory.Exists(scheduledRoot))
                        snapshot.TotalBytes += CalculateDirectoryBytes(scheduledRoot);

                    string countdownRoot = Path.Combine(targetRoot, countdownFolder);
                    if (Directory.Exists(countdownRoot))
                        snapshot.TotalBytes += CalculateDirectoryBytes(countdownRoot);

                    CollectSourceStats(scheduledRoot, isScheduled: true, snapshot.SourceStats);
                    CollectSourceStats(countdownRoot, isScheduled: false, snapshot.SourceStats);

                    // 保留份数按文件夹各自计算（各文件夹频率可能不同），这里显示份数最多的那个文件夹。
                    snapshot.ScheduledCopies = snapshot.SourceStats.Values.Select(stats => stats.ScheduledCopies).DefaultIfEmpty(0).Max();
                    snapshot.CountdownCopies = snapshot.SourceStats.Values.Select(stats => stats.CountdownCopies).DefaultIfEmpty(0).Max();
                }
            }
            catch
            {
                // 统计失败时保留默认值。
            }

            return snapshot;
        }

        /// <summary>按 模式目录\时间戳\名称.7z 统计每个源的备份份数与最近备份时间（以压缩包写入完成时间为准）。</summary>
        private static void CollectSourceStats(
            string modeRoot,
            bool isScheduled,
            Dictionary<string, SourceBackupStats> stats)
        {
            if (!Directory.Exists(modeRoot))
                return;

            EnumerationOptions options = new() { IgnoreInaccessible = true };
            foreach (string copyDirectory in Directory.EnumerateDirectories(modeRoot, "*", options))
            {
                foreach (string archive in Directory.EnumerateFiles(copyDirectory, "*.7z", options))
                {
                    string name = Path.GetFileNameWithoutExtension(archive);
                    if (!stats.TryGetValue(name, out SourceBackupStats? entry))
                    {
                        entry = new SourceBackupStats();
                        stats[name] = entry;
                    }

                    if (isScheduled)
                        entry.ScheduledCopies++;
                    else
                        entry.CountdownCopies++;

                    DateTime written = File.GetLastWriteTime(archive);
                    if (written > entry.LastBackup)
                        entry.LastBackup = written;
                }
            }
        }

        private void ApplyDashboardRefreshResult(Task<DashboardSnapshot> task)
        {
            try
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    ApplyDashboardSnapshot(task.Result);
            }
            catch
            {
                // 刷新失败不影响界面交互。
            }
            finally
            {
                Interlocked.Exchange(ref _dashboardRefreshRunning, 0);

                if (_dashboardRefreshPending && IsVisible)
                    QueueDashboardRefresh(forceWhenHidden: true);
            }
        }

        private void ApplyDashboardSnapshot(DashboardSnapshot snapshot)
        {
            StorageText.Text = FormatBytes(snapshot.TotalBytes);
            ScheduledCopiesText.Text = FormatCopies(snapshot.ScheduledCopies, ReadKeepCopies(ScheduledKeepInput));
            CountdownCopiesText.Text = FormatCopies(snapshot.CountdownCopies, ReadKeepCopies(CountdownKeepInput));
            _sourceStats = snapshot.SourceStats;
            RefreshSourceItems();
        }

        private static int ReadKeepCopies(TextBox box) =>
            TryParseNumber(box.Text, MaxKeepCopies, out int value) ? value : 0;

        private static string FormatCopies(int copies, int keepCopies)
        {
            return keepCopies > 0 ? $"{copies} / {keepCopies} 份" : $"{copies} 份 · 不限";
        }

        private void UpdateCountdownStatus()
        {
            string text;
            string brushKey;

            if (CountdownService.IsExecuting)
            {
                text = "正在复制…";
                brushKey = "WarningBrush";
            }
            else if (CountdownService.IsRunning && !CountdownService.HasJobs)
            {
                text = "运行中 · 无可执行项";
                brushKey = "WarningBrush";
            }
            else if (CountdownService.IsRunning)
            {
                // 多个文件夹各自计时时，显示最近一次执行。
                text = $"{FormatClock(CountdownService.RemainingSeconds)} 后执行";
                brushKey = "SuccessBrush";
            }
            else
            {
                text = "未启动";
                brushKey = "MutedBrush";
            }

            CountdownStatusText.Text = text;
            // 引用资源键而非具体画刷，切换主题时颜色跟着变。
            CountdownStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

            StartCountdownButton.IsEnabled = !_working && !CountdownService.IsRunning;
            StopCountdownButton.IsEnabled = CountdownService.IsRunning || CountdownService.IsExecuting;
        }

        private void UpdateCountdownDurationHint()
        {
            CountdownDurationText.Text =
                int.TryParse(CountdownSecondsInput.Text.Trim(), out int seconds) && seconds > 0
                    ? $"= {FormatDuration(seconds)}"
                    : string.Empty;
        }

        private void SetWorkingState(bool working)
        {
            _working = working;
            SaveButton.IsEnabled = !working;
            CopyNowButton.IsEnabled = !working;
            CopyNowButton.Content = working ? "执行中…" : "立即执行";
            Cursor = working ? Cursors.Wait : null;
            UpdateCountdownStatus();
        }

        #endregion

        #region 日志

        private void AppendLog(long seq, string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => AppendLog(seq, message));
                return;
            }

            // 隐藏期间不更新列表；重新显示时从 AppLogger 的缓存整体重载。
            if (!IsVisible || seq <= _lastLogSeq)
                return;

            _lastLogSeq = seq;
            _logLines.Add(LogLine.Parse(message));
            while (_logLines.Count > MaxLogLines)
                _logLines.RemoveAt(0);
            QueueScrollLogToEnd();
        }

        private void ReloadLog()
        {
            _logLines.Clear();
            foreach ((long seq, string line) in AppLogger.GetRecent())
            {
                _logLines.Add(LogLine.Parse(line));
                _lastLogSeq = seq;
            }
            QueueScrollLogToEnd();
        }

        private void ClearLog()
        {
            AppLogger.ClearRecent();
            _logLines.Clear();
        }

        /// <summary>同一批日志只在空闲时滚动一次，避免每行都触发布局。</summary>
        private void QueueScrollLogToEnd()
        {
            if (_scrollToEndQueued)
                return;

            _scrollToEndQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _scrollToEndQueued = false;
                if (_logLines.Count > 0)
                    LogList.ScrollIntoView(_logLines[^1]);
            });
        }

        private void CopyLogLines(bool selectedOnly)
        {
            IEnumerable<LogLine> lines = selectedOnly
                ? LogList.SelectedItems.Cast<LogLine>().OrderBy(line => _logLines.IndexOf(line))
                : _logLines;
            string text = string.Join(Environment.NewLine, lines.Select(line => line.Time + line.Message));
            if (!string.IsNullOrEmpty(text))
                Clipboard.SetText(text);
        }

        #endregion

        private void HandleCountdownStateChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(HandleCountdownStateChanged);
                return;
            }

            if (IsVisible)
                UpdateCountdownStatus();
        }

        private void HandleCopyCompleted() => QueueDashboardRefresh(forceWhenHidden: false);

        #region 备份进度

        /// <summary>进度回调可能每秒好几次且来自后台线程，只置脏标记，由界面计时器合并刷新。</summary>
        private void HandleBackupProgressChanged() => _progressDirty = true;

        private void RenderBackupProgress()
        {
            IReadOnlyList<BackupSessionSnapshot> sessions = BackupProgress.GetSnapshot();
            ProgressStrip.Visibility = sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ProgressSessionList.ItemsSource = sessions.Select(BuildSessionView).ToList();

            // 同一文件夹可能同时在定时、倒计时两次备份里：正在备份 > 等待中 > 已结束（后开始的优先）。
            Dictionary<string, (int Rank, BackupItemSnapshot Item)> byPath = new(StringComparer.OrdinalIgnoreCase);
            foreach (BackupItemSnapshot item in sessions.SelectMany(session => session.Items))
            {
                int rank = item.State switch
                {
                    BackupItemState.Compressing or BackupItemState.Copying => 3,
                    BackupItemState.Pending => 2,
                    _ => 1
                };
                if (!byPath.TryGetValue(item.Path, out var existing) || rank >= existing.Rank)
                    byPath[item.Path] = (rank, item);
            }

            foreach (SourceItem source in _sources)
            {
                if (byPath.TryGetValue(source.Path.Trim(), out var entry))
                {
                    (string text, string status) = DescribeRowProgress(entry.Item);
                    source.SetProgress(text, status);
                }
                else
                {
                    source.SetProgress(null, string.Empty);
                }
            }
        }

        private static ProgressSessionView BuildSessionView(BackupSessionSnapshot session)
        {
            int total = session.Items.Count;
            string detail;
            if (session.Failed)
            {
                detail = $"已完成 {session.DoneCount}/{total} · 失败：{session.Message}";
            }
            else if (session.Finished)
            {
                detail = $"已完成 {session.DoneCount}/{total}";
            }
            else
            {
                detail = $"已完成 {session.DoneCount}/{total}";
                if (session.Current is BackupItemSnapshot current)
                    detail += $" · 正在备份 {current.Name}（{(current.State == BackupItemState.Copying ? "复制" : "压缩")} {current.Percent:0}%）";
            }

            return new ProgressSessionView
            {
                Title = session.Label,
                Detail = detail,
                Percent = session.Finished && !session.Failed ? 100 : session.OverallPercent,
                PercentText = session.Failed ? "失败" : session.Finished ? "完成" : $"{session.OverallPercent:0}%",
                Status = session.Failed ? "Failed" : session.Finished ? "Done" : "Running",
                Items = session.Items.Select(item => new ProgressItemView
                {
                    Text = item.State switch
                    {
                        BackupItemState.Compressing => $"⟳ {item.Name} 压缩 {item.Percent:0}%",
                        BackupItemState.Copying => $"⟳ {item.Name} 复制 {item.Percent:0}%",
                        BackupItemState.Done => $"✓ {item.Name}",
                        BackupItemState.Failed => $"✗ {item.Name}",
                        BackupItemState.Skipped => $"– {item.Name} 未执行",
                        _ => $"○ {item.Name}"
                    },
                    Status = StatusOf(item.State),
                    ToolTipText = item.Path + (item.Error != null ? $"\n失败原因：{item.Error}" : string.Empty)
                }).ToList()
            };
        }

        private static (string Text, string Status) DescribeRowProgress(BackupItemSnapshot item) => (item.State switch
        {
            BackupItemState.Compressing => $"压缩中 {item.Percent:0}%",
            BackupItemState.Copying => $"复制中 {item.Percent:0}%",
            BackupItemState.Done => "✓ 本次已完成",
            BackupItemState.Failed => "✗ 本次失败",
            BackupItemState.Skipped => "本次未执行",
            _ => "等待备份"
        }, StatusOf(item.State));

        private static string StatusOf(BackupItemState state) => state switch
        {
            BackupItemState.Compressing or BackupItemState.Copying => "Active",
            BackupItemState.Done => "Done",
            BackupItemState.Failed => "Failed",
            BackupItemState.Skipped => "Skipped",
            _ => "Pending"
        };

        #endregion

        #region 资源占用

        /// <summary>采样在后台线程触发；窗口隐藏时不刷新，重新显示时用最近一次结果补上。</summary>
        private void HandleResourceUpdated(ResourceSnapshot snapshot)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible)
                    ApplyResourceSnapshot(snapshot);
            });
        }

        private void ApplyResourceSnapshot(ResourceSnapshot s)
        {
            const double GB = 1024d * 1024 * 1024;
            int cores = Environment.ProcessorCount;

            SetUsage(CpuText, CpuBar, s.SystemCpu);
            CpuDetailText.Text = $"{cores} 线程";
            CpuTile.ToolTip = $"系统总 CPU 占用 {s.SystemCpu:0.#}%（{cores} 个逻辑处理器）\n其中本程序（含 7z）{s.AppCpu:0.#}%";

            SetUsage(MemoryText, MemoryBar, s.MemoryPercent);
            MemoryDetailText.Text = $"{s.MemoryUsed / GB:0.#}/{s.MemoryTotal / GB:0} GB";
            MemoryTile.ToolTip =
                $"系统内存已用 {FormatBytes((long)s.MemoryUsed)} / 共 {FormatBytes((long)s.MemoryTotal)}\n其中本程序（含 7z）{FormatBytes(s.AppMemory)}";

            DiskTitleText.Text = s.DiskRoot == null ? "存储" : $"存储 · {s.DiskRoot.TrimEnd('\\')}";
            if (s.DiskPercent is double diskPercent)
            {
                SetUsage(DiskText, DiskBar, diskPercent);
                DiskDetailText.Text = $"剩 {FormatBytes((long)s.DiskFree)}";
                DiskTile.ToolTip =
                    $"备份目标所在磁盘 {s.DiskRoot}\n已用 {FormatBytes((long)(s.DiskTotal - s.DiskFree))} / 共 {FormatBytes((long)s.DiskTotal)}，剩余 {FormatBytes((long)s.DiskFree)}";
            }
            else
            {
                DiskText.Text = "—";
                DiskBar.Value = 0;
                DiskDetailText.Text = s.DiskRoot == null ? "未设置目标" : "读取失败";
                DiskTile.ToolTip = s.DiskRoot == null ? "请先设置备份目标" : $"无法读取 {s.DiskRoot} 的容量（磁盘未连接或网络路径不可达）";
            }

            double ioRate = s.AppReadPerSecond + s.AppWritePerSecond;
            AppTitleText.Text = s.BackupRunning ? $"本程序 · 备份中 {FormatBytes((long)ioRate)}/s" : "本程序";
            AppTitleText.SetResourceReference(TextBlock.ForegroundProperty, s.BackupRunning ? "WarningBrush" : "MutedBrush");
            SetUsage(AppText, AppBar, s.AppCpu);
            AppDetailText.Text = FormatBytes(s.AppMemory);
            AppTile.ToolTip =
                $"本程序（含 7z 压缩进程）\n" +
                $"CPU {s.AppCpu:0.#}%（占全部 {cores} 个逻辑处理器）\n" +
                $"内存 {FormatBytes(s.AppMemory)}\n" +
                $"磁盘读取 {FormatBytes((long)s.AppReadPerSecond)}/s · 写入 {FormatBytes((long)s.AppWritePerSecond)}/s" +
                (ResourceMonitor.LastBackupSummary is string last ? $"\n\n上次备份：{last}" : "\n\n每次备份完成后，执行日志里会记录这次的资源占用。");
        }

        /// <summary>显示百分比，并按高低给进度条换色：≥90% 危险色，≥75% 警示色。</summary>
        private static void SetUsage(TextBlock valueText, ProgressBar bar, double percent)
        {
            valueText.Text = $"{percent:0}%";
            bar.Value = percent;
            bar.SetResourceReference(ForegroundProperty, percent >= 90 ? "DangerBrush" : percent >= 75 ? "WarningBrush" : "AccentBrush");
        }

        #endregion

        #region 窗口：关闭/最小化收到托盘

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!Program.IsExiting)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _statusTimer.Stop();
            _saveHintTimer.Stop();
            AppLogger.MessageLogged -= AppendLog;
            CountdownService.StateChanged -= HandleCountdownStateChanged;
            AppEvents.CopyCompleted -= HandleCopyCompleted;
            WifiScreenOff.EnabledChanged -= HandleWifiScreenOffChanged;
            ThemeManager.Changed -= HandleThemeChanged;
            ResourceMonitor.Updated -= HandleResourceUpdated;
            BackupProgress.Changed -= HandleBackupProgressChanged;
            _progressTimer.Stop();
            base.OnClosed(e);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
                HideToTray();
        }

        private void HideToTray()
        {
            Hide();
            Program.NotifyHiddenToTray();
        }

        #endregion

        private void OpenDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, "请先填写路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, $"路径不存在：{path}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }

        private static string NormalizeFolderName(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static long CalculateDirectoryBytes(string rootPath)
        {
            long totalBytes = 0;

            EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (string filePath in Directory.EnumerateFiles(rootPath, "*", options))
            {
                try
                {
                    totalBytes += new FileInfo(filePath).Length;
                }
                catch
                {
                    // 文件可能在统计过程中被占用或删除，忽略即可。
                }
            }

            return totalBytes;
        }

        private static string FormatBytes(long bytes) => ResourceMonitor.FormatBytes(bytes);

        private static string FormatClock(int seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes:00}:{time.Seconds:00}";
        }

        private static string FormatDuration(int seconds) => SourceRule.FormatDuration(seconds);
    }

    /// <summary>源文件夹列表的一行。</summary>
    public sealed class SourceItem : INotifyPropertyChanged
    {
        private bool _missing;
        private bool _neverBackedUp = true;
        private string _summary = string.Empty;
        private string _ruleText = string.Empty;
        private string _toolTip = string.Empty;

        public SourceItem(string path)
        {
            Path = path;
            _toolTip = path;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Path { get; }

        public string DisplayPath => _missing ? $"{Path}    （路径不存在）" : Path;

        /// <summary>路径不存在时标红（颜色由 MainWindow.xaml 按主题决定）。</summary>
        public bool IsMissing => _missing;

        public string Summary => _summary;

        /// <summary>从未备份过用警示色提醒。</summary>
        public bool NeverBackedUp => _neverBackedUp;

        /// <summary>单独设置的备份频率简述；跟随全局时为空。</summary>
        public string RuleText => _ruleText;

        /// <summary>备份进行中该文件夹的进度说明；不在本次备份中时为 null。</summary>
        public string? ProgressText { get; private set; }

        /// <summary>Pending / Active / Done / Failed / Skipped；不在备份中时为空串。</summary>
        public string ProgressStatus { get; private set; } = string.Empty;

        public string DisplaySummary => ProgressText ?? _summary;

        public void SetProgress(string? text, string status)
        {
            if (ProgressText == text && ProgressStatus == status)
                return;

            ProgressText = text;
            ProgressStatus = status;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        public string ToolTipText => _toolTip;

        public void Update(bool missing, string summary, bool neverBackedUp, string ruleText, string toolTip)
        {
            if (_missing == missing && _summary == summary && _neverBackedUp == neverBackedUp &&
                _ruleText == ruleText && _toolTip == toolTip)
                return;

            _missing = missing;
            _summary = summary;
            _neverBackedUp = neverBackedUp;
            _ruleText = ruleText;
            _toolTip = toolTip;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    /// <summary>进度条上的一次备份（一行）。</summary>
    public sealed class ProgressSessionView
    {
        public string Title { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public double Percent { get; init; }
        public string PercentText { get; init; } = string.Empty;

        /// <summary>Running / Done / Failed，决定进度条颜色。</summary>
        public string Status { get; init; } = string.Empty;
        public List<ProgressItemView> Items { get; init; } = new();
    }

    /// <summary>进度条下的一个文件夹标签。</summary>
    public sealed class ProgressItemView
    {
        public string Text { get; init; } = string.Empty;

        /// <summary>Pending / Active / Done / Failed / Skipped，决定标签颜色。</summary>
        public string Status { get; init; } = string.Empty;
        public string ToolTipText { get; init; } = string.Empty;
    }

    public enum LogKind
    {
        Normal,
        Error,
        Warning,
        Success
    }

    /// <summary>一行日志：时间戳灰色，正文按类别着色（失败红、跳过黄、完成绿；颜色由 MainWindow.xaml 按主题决定）。</summary>
    public sealed class LogLine
    {
        private LogLine(string time, string message, LogKind kind)
        {
            Time = time;
            Message = message;
            Kind = kind;
        }

        public string Time { get; }
        public string Message { get; }
        public LogKind Kind { get; }

        public static LogLine Parse(string line)
        {
            // 行格式：[HH:mm:ss] 内容
            int split = line.StartsWith("[", StringComparison.Ordinal) ? line.IndexOf("] ", StringComparison.Ordinal) : -1;
            string time = split > 0 ? line[..(split + 2)] : string.Empty;
            string message = split > 0 ? line[(split + 2)..] : line;
            return new LogLine(time, message, Classify(line));
        }

        private static LogKind Classify(string line)
        {
            if (ContainsAny(line, "失败", "出错", "取消", "异常", "不存在", "错误"))
                return LogKind.Error;
            if (ContainsAny(line, "跳过", "警告"))
                return LogKind.Warning;
            if (ContainsAny(line, "完成", "成功", "已保存"))
                return LogKind.Success;
            return LogKind.Normal;
        }

        private static bool ContainsAny(string text, params string[] keywords) =>
            keywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
    }
}

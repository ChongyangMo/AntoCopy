using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AntoCopy
{
    internal static class AppSettings
    {
        private const string RegKey = @"SOFTWARE\AutoCopy";
        private const string AutoStartArg = "--background";

        public static string[] SourcePaths
        {
            get => GetSourcePaths();
            set
            {
                string[] normalized = NormalizePaths(value);
                Set("SourcePaths", string.Join(Environment.NewLine, normalized));
                Set("SourcePath", normalized.FirstOrDefault() ?? string.Empty);
            }
        }

        public static string DestPath
        {
            get => Get("DestPath");
            set => Set("DestPath", value?.Trim() ?? string.Empty);
        }

        public static string RunTime
        {
            get => Get("RunTime", "02:00");
            set => Set("RunTime", value?.Trim() ?? string.Empty);
        }

        public static int CountdownSeconds
        {
            get
            {
                return int.TryParse(Get("CountdownSeconds", "0"), out int seconds) && seconds >= 0
                    ? seconds
                    : 0;
            }
            set => Set("CountdownSeconds", Math.Max(0, value).ToString());
        }

        public static int CountdownKeepCopies
        {
            get
            {
                return int.TryParse(Get("CountdownKeepCopies", "0"), out int copies) && copies >= 0
                    ? copies
                    : 0;
            }
            set => Set("CountdownKeepCopies", Math.Max(0, value).ToString());
        }

        public static int ScheduledKeepCopies
        {
            get
            {
                return int.TryParse(Get("ScheduledKeepCopies", "1"), out int copies) && copies >= 0
                    ? copies
                    : 1;
            }
            set => Set("ScheduledKeepCopies", Math.Max(0, value).ToString());
        }

        public static string ScheduledFolderName
        {
            get => NormalizeFolderName(Get("ScheduledFolderName", "定时模式"), "定时模式");
            set => Set("ScheduledFolderName", NormalizeFolderName(value, "定时模式"));
        }

        public static string CountdownFolderName
        {
            get => NormalizeFolderName(Get("CountdownFolderName", "倒计时模式"), "倒计时模式");
            set => Set("CountdownFolderName", NormalizeFolderName(value, "倒计时模式"));
        }

        public static bool AutoStart
        {
            get => Get("AutoStart") == "1";
            set
            {
                Set("AutoStart", value ? "1" : "0");
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true)!;

                if (value)
                    key.SetValue("AutoCopy", $"\"{Application.ExecutablePath}\" {AutoStartArg}");
                else
                    key.DeleteValue("AutoCopy", throwOnMissingValue: false);
            }
        }

        /// <summary>
        /// 已勾选开机自启、但启动项指向的程序已不存在（程序被移动/重新生成到别处）时，改指向当前程序。
        /// 启动项指向的程序仍存在时不改动，避免多份副本之间互相抢占。
        /// </summary>
        public static void RepairAutoStartEntry()
        {
            if (!AutoStart)
                return;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null)
                    return;

                string? registeredExe = ParseExecutablePath(key.GetValue("AutoCopy") as string);
                if (registeredExe != null && File.Exists(registeredExe))
                    return;

                key.SetValue("AutoCopy", $"\"{Application.ExecutablePath}\" {AutoStartArg}");
                AppLogger.Log($"开机自启项指向的程序不存在（{registeredExe ?? "未设置"}），已修复为：{Application.ExecutablePath}");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"检查开机自启项失败：{ex.Message}");
            }
        }

        private static string? ParseExecutablePath(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return null;

            command = command.Trim();
            if (command.StartsWith('"'))
            {
                int end = command.IndexOf('"', 1);
                return end > 1 ? command[1..end] : null;
            }

            int space = command.IndexOf(' ');
            return space > 0 ? command[..space] : command;
        }

        /// <summary>最近一次定时模式自动触发的日期；用于保证每天最多自动执行一次（重启不会重复执行）。</summary>
        public static DateTime? LastScheduledRunDate
        {
            get => DateTime.TryParseExact(Get("LastScheduledRunDate"), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime date)
                ? date
                : null;
            set => Set("LastScheduledRunDate",
                value?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        /// <summary>倒计时到达时若处于跳过时段则不执行（注册表键沿用旧名以兼容已有设置）。</summary>
        public static bool CountdownSkipEnabled
        {
            get => Get("CountdownSkipEarlyMorning") == "1";
            set => Set("CountdownSkipEarlyMorning", value ? "1" : "0");
        }

        /// <summary>跳过时段起点 HH:mm（含）。</summary>
        public static string CountdownSkipStart
        {
            get => NormalizeTime(Get("CountdownSkipStart"), "00:00");
            set => Set("CountdownSkipStart", NormalizeTime(value, "00:00"));
        }

        /// <summary>跳过时段终点 HH:mm（含该分钟）；早于起点时表示跨越午夜。</summary>
        public static string CountdownSkipEnd
        {
            get => NormalizeTime(Get("CountdownSkipEnd"), "07:59");
            set => Set("CountdownSkipEnd", NormalizeTime(value, "07:59"));
        }

        private static string NormalizeTime(string? text, string def) =>
            SourceRule.TryParseTime(text ?? string.Empty, out TimeSpan time) ? time.ToString(@"hh\:mm") : def;

        /// <summary>WiFi 断开时自动关闭显示器（系统保持运行），见 WifiScreenOff。</summary>
        public static bool ScreenOffOnWifiLost
        {
            get => Get("ScreenOffOnWifiLost") == "1";
            set => Set("ScreenOffOnWifiLost", value ? "1" : "0");
        }

        /// <summary>WiFi 断开后键鼠空闲满多少秒才息屏，默认 30 秒；0 表示立即息屏。</summary>
        public static int ScreenOffIdleSeconds
        {
            get => int.TryParse(Get("ScreenOffIdleSeconds"), out int seconds) && seconds >= 0 ? seconds : 30;
            set => Set("ScreenOffIdleSeconds", value.ToString());
        }

        #region 按文件夹的备份频率

        private const string LastRunFormat = "yyyy-MM-dd HH:mm:ss";
        private static readonly object _rulesLock = new();
        private static readonly JsonSerializerOptions RulesJsonOptions = new() { Converters = { new JsonStringEnumConverter() } };
        private static Dictionary<string, SourceRule>? _rules;
        private static Dictionary<string, string>? _lastRuns;

        /// <summary>某个源的频率设置（返回副本）；没有单独设置时两种模式都跟随全局。</summary>
        public static SourceRule GetSourceRule(string sourcePath)
        {
            lock (_rulesLock)
                return LoadRules().TryGetValue(sourcePath.Trim(), out SourceRule? rule) ? rule.Clone() : new SourceRule();
        }

        public static void SetSourceRule(string sourcePath, SourceRule rule)
        {
            sourcePath = sourcePath.Trim();
            lock (_rulesLock)
            {
                Dictionary<string, SourceRule> rules = LoadRules();
                SourceRule old = rules.TryGetValue(sourcePath, out SourceRule? existing) ? existing : new SourceRule();

                // 新设或改了自定义定时：从下一个时刻开始执行，不因“错过了今天更早的时刻”立刻补跑。
                if (rule.ScheduledMode == RuleMode.Custom && !rule.SameScheduleAs(old))
                    SetLastScheduledRunLocked(sourcePath, DateTime.Now);

                if (rule.IsDefault)
                    rules.Remove(sourcePath);
                else
                    rules[sourcePath] = rule.Clone();

                Set("SourceRules", JsonSerializer.Serialize(rules, RulesJsonOptions));
            }
        }

        /// <summary>源列表保存后删掉已移除源的频率设置与执行记录。</summary>
        public static void PruneSourceRules(IEnumerable<string> keepPaths)
        {
            HashSet<string> keep = new(keepPaths.Select(path => path.Trim()), StringComparer.OrdinalIgnoreCase);
            lock (_rulesLock)
            {
                Dictionary<string, SourceRule> rules = LoadRules();
                if (rules.Keys.Where(path => !keep.Contains(path)).ToList() is { Count: > 0 } staleRules)
                {
                    staleRules.ForEach(path => rules.Remove(path));
                    Set("SourceRules", JsonSerializer.Serialize(rules, RulesJsonOptions));
                }

                Dictionary<string, string> lastRuns = LoadLastRuns();
                if (lastRuns.Keys.Where(path => !keep.Contains(path)).ToList() is { Count: > 0 } staleRuns)
                {
                    staleRuns.ForEach(path => lastRuns.Remove(path));
                    Set("SourceLastScheduledRun", JsonSerializer.Serialize(lastRuns));
                }
            }
        }

        /// <summary>自定义定时最近一次触发的时间（触发即记账，与全局定时一致）。</summary>
        public static DateTime? GetSourceLastScheduledRun(string sourcePath)
        {
            lock (_rulesLock)
            {
                return LoadLastRuns().TryGetValue(sourcePath.Trim(), out string? text) &&
                       DateTime.TryParseExact(text, LastRunFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)
                    ? time
                    : null;
            }
        }

        public static void SetSourceLastScheduledRun(string sourcePath, DateTime time)
        {
            lock (_rulesLock)
                SetLastScheduledRunLocked(sourcePath.Trim(), time);
        }

        private static void SetLastScheduledRunLocked(string sourcePath, DateTime time)
        {
            Dictionary<string, string> lastRuns = LoadLastRuns();
            lastRuns[sourcePath] = time.ToString(LastRunFormat, CultureInfo.InvariantCulture);
            Set("SourceLastScheduledRun", JsonSerializer.Serialize(lastRuns));
        }

        private static Dictionary<string, SourceRule> LoadRules()
        {
            if (_rules != null)
                return _rules;

            _rules = new Dictionary<string, SourceRule>(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, SourceRule rule) in ReadJson<Dictionary<string, SourceRule>>("SourceRules", RulesJsonOptions) ?? new())
                _rules[path] = rule;
            return _rules;
        }

        private static Dictionary<string, string> LoadLastRuns()
        {
            if (_lastRuns != null)
                return _lastRuns;

            _lastRuns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, string time) in ReadJson<Dictionary<string, string>>("SourceLastScheduledRun", null) ?? new())
                _lastRuns[path] = time;
            return _lastRuns;
        }

        private static T? ReadJson<T>(string name, JsonSerializerOptions? options) where T : class
        {
            string json = Get(name);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JsonSerializer.Deserialize<T>(json, options);
            }
            catch (JsonException ex)
            {
                AppLogger.Log($"读取设置 {name} 失败，已按空处理：{ex.Message}");
                return null;
            }
        }

        #endregion

        /// <summary>界面主题：跟随系统 / 白天 / 黑夜，见 ThemeManager。</summary>
        public static AppTheme Theme
        {
            get => Enum.TryParse(Get("Theme"), out AppTheme theme) ? theme : AppTheme.System;
            set => Set("Theme", value.ToString());
        }

        private static string Get(string name, string def = "")
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey);
            return key?.GetValue(name)?.ToString() ?? def;
        }

        private static string[] GetSourcePaths()
        {
            string raw = Get("SourcePaths");
            if (string.IsNullOrWhiteSpace(raw))
                raw = Get("SourcePath");

            return NormalizePaths(raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string[] NormalizePaths(string[]? paths)
        {
            return (paths ?? Array.Empty<string>())
                .Select(path => path.Trim())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeFolderName(string? value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static void Set(string name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegKey);
            key.SetValue(name, value);
        }
    }

    internal static class AppEvents
    {
        public static event Action? CopyCompleted;

        /// <summary>源列表或频率设置保存后触发（界面线程），倒计时据此调整各组。</summary>
        public static event Action? SettingsChanged;

        public static void RaiseCopyCompleted()
        {
            CopyCompleted?.Invoke();
        }

        public static void RaiseSettingsChanged()
        {
            SettingsChanged?.Invoke();
        }
    }

    internal static class AppLogger
    {
        private const int MaxRecent = 500;
        private const int LogRetentionDays = 14;
        private static readonly Queue<(long Seq, string Line)> _recent = new();
        private static readonly object _lock = new();
        private static long _seq;

        /// <summary>日志文件目录（按天一个文件），用于事后排查程序为何退出/备份为何失败。</summary>
        public static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCopy", "logs");

        /// <summary>参数：序号、日志行。序号用于窗口重载历史后去重。</summary>
        public static event Action<long, string>? MessageLogged;

        public static void Log(string message)
        {
            DateTime now = DateTime.Now;
            string line = $"[{now:HH:mm:ss}] {message}";
            long seq;
            lock (_lock)
            {
                seq = ++_seq;
                _recent.Enqueue((seq, line));
                while (_recent.Count > MaxRecent)
                    _recent.Dequeue();

                WriteToFile(now, line);
            }

            MessageLogged?.Invoke(seq, line);
            Console.WriteLine(line);
        }

        private static void WriteToFile(DateTime now, string line)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    Path.Combine(LogDirectory, $"autocopy-{now:yyyyMMdd}.log"),
                    line + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
                // 写日志文件失败不能影响程序运行。
            }
        }

        public static void PruneOldLogFiles()
        {
            try
            {
                if (!Directory.Exists(LogDirectory))
                    return;

                DateTime cutoff = DateTime.Now.AddDays(-LogRetentionDays);
                foreach (string file in Directory.EnumerateFiles(LogDirectory, "autocopy-*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
            }
            catch
            {
                // 清理旧日志失败忽略。
            }
        }

        /// <summary>最近的日志（窗口隐藏期间的日志也在内，例如凌晨定时执行）。</summary>
        public static (long Seq, string Line)[] GetRecent()
        {
            lock (_lock)
                return _recent.ToArray();
        }

        public static void ClearRecent()
        {
            lock (_lock)
                _recent.Clear();
        }
    }

    internal enum CopyMode
    {
        Scheduled,
        Countdown
    }

    internal static class CopyHelper
    {
        private const string BackupWorkflowVersion = "7z-workflow-20260519-02";
        private static int _scheduledRunning;
        private static int _countdownRunning;

        /// <summary>是否有备份正在执行（定时或倒计时）。</summary>
        public static bool IsBusy =>
            Volatile.Read(ref _scheduledRunning) == 1 || Volatile.Read(ref _countdownRunning) == 1;

        /// <summary>定时模式是否有备份正在执行（全局与各文件夹的自定义定时依次执行，不并发）。</summary>
        public static bool IsScheduledBusy => Volatile.Read(ref _scheduledRunning) == 1;

        /// <summary>手动“立即执行定时模式”：参与定时的全部源（跟随全局 + 自定义）打包进同一个副本目录。</summary>
        public static void RunScheduledCopy() =>
            RunScheduledCopy(SourceGroups.Participating(CopyMode.Scheduled), "定时模式");

        /// <param name="label">日志/托盘提示里的名称，例如“定时模式（项目A）”。</param>
        public static void RunScheduledCopy(string[] sources, string label)
        {
            if (Interlocked.Exchange(ref _scheduledRunning, 1) == 1)
            {
                AppLogger.Log($"{label}跳过：上一次执行尚未完成。");
                return;
            }

            try
            {
                ExecuteCopy(CopyMode.Scheduled, sources, label);
            }
            finally
            {
                Interlocked.Exchange(ref _scheduledRunning, 0);
            }
        }

        public static void RunCountdownCopy(string[] sources, string label)
        {
            if (Interlocked.Exchange(ref _countdownRunning, 1) == 1)
            {
                AppLogger.Log($"{label}跳过：上一次执行尚未完成。");
                return;
            }

            try
            {
                ExecuteCopy(CopyMode.Countdown, sources, label);
            }
            finally
            {
                Interlocked.Exchange(ref _countdownRunning, 0);
            }
        }

        private static void ExecuteCopy(CopyMode mode, string[] sources, string modeName)
        {
            string destRoot = AppSettings.DestPath;
            using KeepAwakeScope keepAwake = KeepAwakeScope.Begin();

            AppLogger.Log(
                $"{modeName}进入备份流程，当前链路版本：{BackupWorkflowVersion}，程序目录：{AppContext.BaseDirectory}，当前目录：{Environment.CurrentDirectory}");

            if (sources.Length == 0)
            {
                AppLogger.Log($"{modeName}跳过：当前没有需要备份的源路径，程序保持运行但不复制。");
                return;
            }

            if (string.IsNullOrWhiteSpace(destRoot))
            {
                AppLogger.Log($"{modeName}跳过：未配置目标路径。");
                Program.ShowTrayMessage("AutoCopy", $"{modeName}未执行：请先配置目标路径。", ToolTipIcon.Warning);
                return;
            }

            // 按全部源检查重名：各源的压缩包共用模式目录，按包名统计和清理，重名会互相覆盖。
            string[] duplicateNames = AppSettings.SourcePaths
                .GroupBy(GetSourceFolderName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();

            if (duplicateNames.Length > 0)
            {
                AppLogger.Log($"{modeName}取消：源目录名称重复：{string.Join("、", duplicateNames)}");
                Program.ShowTrayMessage("AutoCopy", $"源目录名称重复：{string.Join("、", duplicateNames)}", ToolTipIcon.Error);
                return;
            }

            BackupUsageTracker? usage = null;
            int progressId = BackupProgress.Begin(modeName, sources);
            string? failure = null;
            try
            {
                foreach (string src in sources)
                {
                    if (!Directory.Exists(src))
                    {
                        failure = $"源路径不存在：{src}";
                        BackupProgress.Update(progressId, src, BackupItemState.Failed, error: "路径不存在");
                        AppLogger.Log($"{modeName}取消：源路径不存在：{src}");
                        Program.ShowTrayMessage("AutoCopy", $"源路径不存在：{src}", ToolTipIcon.Error);
                        return;
                    }
                }

                string sevenZipExecutable = ResolveSevenZipExecutable();
                AppLogger.Log($"{modeName}已确认 7z 可执行文件：{sevenZipExecutable}");
                string modeRoot = Path.Combine(
                    destRoot,
                    mode == CopyMode.Scheduled ? AppSettings.ScheduledFolderName : AppSettings.CountdownFolderName);

                int copied = 0;
                int skipped = 0;
                string effectiveRoot = CreateCopyOutputRoot(modeRoot, modeName);

                AppLogger.Log($"{modeName}开始执行，输出目录：{effectiveRoot}");
                usage = ResourceMonitor.BeginBackup();

                foreach (string src in sources)
                {
                    string sourceName = GetSourceFolderName(src);
                    string targetPath = Path.Combine(effectiveRoot, $"{sourceName}.7z");
                    string tempArchiveDirectory = ResolveArchiveTempDirectory(src, destRoot);
                    string tempArchivePath = Path.Combine(
                        tempArchiveDirectory,
                        $"{sourceName}__{mode.ToString().ToLowerInvariant()}__{Guid.NewGuid():N}.7z");

                    try
                    {
                        AppLogger.Log($"{modeName}已选择临时压缩包目录：{tempArchiveDirectory}");
                        AppLogger.Log($"{modeName}开始打包：{src} -> {tempArchivePath}");
                        BackupProgress.Update(progressId, src, BackupItemState.Compressing);
                        CreateSevenZipArchive(sevenZipExecutable, src, tempArchivePath,
                            percent => BackupProgress.Update(progressId, src, BackupItemState.Compressing, percent));

                        AppLogger.Log($"{modeName}正在复制压缩包：{tempArchivePath} -> {targetPath}");
                        BackupProgress.Update(progressId, src, BackupItemState.Copying);
                        CopyArchiveFile(tempArchivePath, targetPath,
                            percent => BackupProgress.Update(progressId, src, BackupItemState.Copying, percent));
                        BackupProgress.Update(progressId, src, BackupItemState.Done, 100);
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        BackupProgress.Update(progressId, src, BackupItemState.Failed, error: ex.Message);
                        throw;
                    }
                    finally
                    {
                        TryDeleteTempArchive(tempArchivePath, modeName);
                    }
                }

                int trimmedCopies = TrimArchives(
                    modeRoot,
                    mode == CopyMode.Scheduled ? AppSettings.ScheduledKeepCopies : AppSettings.CountdownKeepCopies,
                    modeName);

                AppLogger.Log(
                    $"{modeName}完成：生成并复制 {copied} 个压缩包，跳过 {skipped} 个异常项" +
                    (trimmedCopies > 0 ? $"，清理 {trimmedCopies} 份旧副本" : string.Empty) +
                    "。");
                Program.ShowTrayMessage("AutoCopy", $"{modeName}完成：生成并复制 {copied} 个压缩包。", ToolTipIcon.Info);
                AppEvents.RaiseCopyCompleted();
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                AppLogger.Log($"{modeName}失败：{ex.Message}");
                Program.ShowTrayMessage("AutoCopy 出错", ex.Message, ToolTipIcon.Error, 4000);
            }
            finally
            {
                BackupProgress.End(progressId, failure);

                // 本程序 + 7z 在这次备份期间的占用（成功或失败都记录）。
                if (usage != null)
                    AppLogger.Log($"{modeName}资源占用：{ResourceMonitor.EndBackup(usage)}。");
            }
        }

        private static string ResolveSevenZipExecutable()
        {
            List<string> searchedPaths = new();

            foreach (string directory in EnumerateSevenZipSearchDirectories())
            {
                AppLogger.Log($"开始查找 7z，可选目录：{directory}");
                foreach (string fileName in new[] { "7z.exe", "7za.exe", "7zr.exe" })
                {
                    string candidate = Path.Combine(directory, fileName);
                    searchedPaths.Add(candidate);
                    if (File.Exists(candidate))
                    {
                        AppLogger.Log($"已找到 7z 可执行文件：{candidate}");
                        return candidate;
                    }
                }
            }

            foreach (string fileName in new[] { "7z.exe", "7za.exe", "7zr.exe" })
            {
                if (TryResolveExecutableFromPath(fileName, out string resolved))
                {
                    AppLogger.Log($"已从 PATH 找到 7z 可执行文件：{resolved}");
                    return resolved;
                }
            }

            throw new FileNotFoundException(
                "未找到 7z 压缩程序。请将 7z.exe、7za.exe 或 7zr.exe 放到程序目录，或安装 7-Zip 并加入 PATH。"
                + (searchedPaths.Count > 0 ? $" 已检查：{string.Join("；", searchedPaths)}" : string.Empty));
        }

        private static IEnumerable<string> EnumerateSevenZipSearchDirectories()
        {
            HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);

            void AddDirectory(string? path)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    directories.Add(Path.GetFullPath(path));
            }

            AddDirectory(AppContext.BaseDirectory);
            AddDirectory(Environment.CurrentDirectory);

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            AddDirectory(Path.Combine(programFiles, "7-Zip"));
            AddDirectory(Path.Combine(programFilesX86, "7-Zip"));

            return directories;
        }

        private static bool TryResolveExecutableFromPath(string executableName, out string resolvedPath)
        {
            resolvedPath = string.Empty;

            string? rawPath = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            foreach (string entry in rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(entry.Trim(), executableName);
                    if (File.Exists(candidate))
                    {
                        resolvedPath = candidate;
                        return true;
                    }
                }
                catch
                {
                    // 忽略非法 PATH 项，继续查找。
                }
            }

            return false;
        }

        private static string ResolveArchiveTempDirectory(string sourcePath, string destRoot)
        {
            string fullSourcePath = Path.GetFullPath(sourcePath);
            string[] candidates =
            {
                Path.GetFullPath(destRoot),
                Path.GetFullPath(AppContext.BaseDirectory)
            };

            foreach (string candidate in candidates)
            {
                if (!IsPathInsideDirectory(candidate, fullSourcePath))
                {
                    AppLogger.Log($"临时压缩包目录可用：{candidate}");
                    return candidate;
                }
            }

            AppLogger.Log($"目标目录和程序目录都位于源目录内，临时压缩包改用系统临时目录：{Path.GetTempPath()}");
            return Path.GetTempPath();
        }

        private static bool IsPathInsideDirectory(string candidatePath, string directoryPath)
        {
            string candidate = EnsureTrailingSeparator(Path.GetFullPath(candidatePath));
            string directory = EnsureTrailingSeparator(Path.GetFullPath(directoryPath));
            return candidate.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        /// <summary>7z 的进度片段，形如“  45% 3 + 文件名”。</summary>
        private static readonly System.Text.RegularExpressions.Regex SevenZipProgressPattern =
            new(@"^\s*(\d{1,3})%", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <param name="onProgress">压缩百分比（0-100），在读取线程上回调。</param>
        private static void CreateSevenZipArchive(
            string sevenZipExecutable, string sourcePath, string archivePath, Action<double>? onProgress = null)
        {
            string fullSourcePath = Path.GetFullPath(sourcePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? sourceParent = Path.GetDirectoryName(fullSourcePath);
            string sourceName = Path.GetFileName(fullSourcePath);
            string workingDirectory = sourceParent ?? fullSourcePath;
            string sourceArgument = string.IsNullOrWhiteSpace(sourceName) ? "*" : sourceName;

            string? archiveDirectory = Path.GetDirectoryName(archivePath);
            if (!string.IsNullOrWhiteSpace(archiveDirectory))
                Directory.CreateDirectory(archiveDirectory);

            if (File.Exists(archivePath))
                File.Delete(archivePath);

            ProcessStartInfo startInfo = new()
            {
                FileName = sevenZipExecutable,
                // -sccUTF-8：7z 默认按控制台代码页(GBK)输出，重定向后按 UTF-8 读会乱码。
                // -bsp2：进度输出到 stderr，用于界面上的压缩百分比。
                Arguments = $"a -t7z -y -sccUTF-8 -bsp2 {QuoteArgument(archivePath)} {QuoteArgument(sourceArgument)}",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using Process process = new() { StartInfo = startInfo };
            StringBuilder outputBuilder = new();
            StringBuilder errorBuilder = new();

            AppLogger.Log(
                $"准备调用 7z，工作目录：{workingDirectory}，命令：{Path.GetFileName(sevenZipExecutable)} {startInfo.Arguments}");

            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    outputBuilder.AppendLine(args.Data);
            };

            try
            {
                if (!process.Start())
                    throw new IOException("7z 进程启动失败。");
            }
            catch (Win32Exception ex)
            {
                throw new IOException($"无法启动 7z 压缩程序：{ex.Message}", ex);
            }

            // AutoCopy 退出/崩溃/被结束时，7z 随之结束，不留孤儿进程。
            ChildProcessJob.Attach(process);

            process.BeginOutputReadLine();
            Task errorReader = Task.Run(() => ReadSevenZipErrorStream(process.StandardError, errorBuilder, onProgress));
            process.WaitForExit();
            errorReader.Wait();

            string processMessage = BuildProcessMessage(outputBuilder, errorBuilder);
            if (process.ExitCode > 1 || !File.Exists(archivePath))
            {
                throw new IOException(
                    $"7z 打包失败，退出码 {process.ExitCode}。"
                    + (string.IsNullOrWhiteSpace(processMessage) ? string.Empty : $" 输出：{processMessage}"));
            }

            long archiveBytes = new FileInfo(archivePath).Length;
            AppLogger.Log($"7z 打包成功：{archivePath}，大小 {archiveBytes} 字节，退出码 {process.ExitCode}");

            if (process.ExitCode == 1)
                AppLogger.Log($"7z 打包完成，但存在警告：{processMessage}");
        }

        /// <summary>
        /// 7z 的进度用回车（不换行）原地刷新，按行读取要等进程结束才拿得到，所以按字符读：
        /// 回车分隔的“NN%”片段是进度；以换行结尾的其他内容是错误/警告，照旧收集进 errorBuilder。
        /// </summary>
        private static void ReadSevenZipErrorStream(StreamReader reader, StringBuilder errorBuilder, Action<double>? onProgress)
        {
            StringBuilder segment = new();
            // 以回车结尾的非进度片段：若紧跟换行（警告/错误行是 CRLF 结尾），它其实是完整的一行。
            string? crSegment = null;
            char[] buffer = new char[1024];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    switch (c)
                    {
                        case '\r' or '\b':
                            string text = segment.ToString();
                            segment.Clear();
                            crSegment = TryReportProgress(text) ? null : text;
                            break;
                        case '\n':
                            HandleLine(segment.Length > 0 ? segment.ToString() : crSegment ?? string.Empty);
                            segment.Clear();
                            crSegment = null;
                            break;
                        default:
                            segment.Append(c);
                            crSegment = null;
                            break;
                    }
                }
            }

            HandleLine(segment.ToString());

            bool TryReportProgress(string text)
            {
                var match = SevenZipProgressPattern.Match(text);
                if (match.Success)
                    onProgress?.Invoke(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                return match.Success;
            }

            void HandleLine(string text)
            {
                if (!TryReportProgress(text) && !string.IsNullOrWhiteSpace(text))
                    errorBuilder.AppendLine(text.Trim());
            }
        }

        private static string BuildProcessMessage(StringBuilder outputBuilder, StringBuilder errorBuilder)
        {
            string combined = (outputBuilder.ToString() + Environment.NewLine + errorBuilder.ToString()).Trim();
            if (string.IsNullOrWhiteSpace(combined))
                return string.Empty;

            string singleLine = combined.Replace("\r", " ").Replace("\n", " ");
            return singleLine.Length <= 240 ? singleLine : singleLine[..240] + "...";
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        /// <param name="onProgress">复制百分比（0-100）。用系统 CopyFileEx（与 File.Copy 相同的复制方式）获取进度。</param>
        private static void CopyArchiveFile(string sourceArchivePath, string targetArchivePath, Action<double>? onProgress = null)
        {
            string? targetDirectory = Path.GetDirectoryName(targetArchivePath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            NativeMethods.CopyProgressRoutine progress = (total, transferred, _, _, _, _, _, _, _) =>
            {
                onProgress?.Invoke(total > 0 ? transferred * 100.0 / total : 100);
                return NativeMethods.PROGRESS_CONTINUE;
            };
            if (!NativeMethods.CopyFileEx(sourceArchivePath, targetArchivePath, progress, IntPtr.Zero, IntPtr.Zero, 0))
                throw new IOException($"复制压缩包失败：{new Win32Exception().Message}");
            GC.KeepAlive(progress);

            long targetBytes = new FileInfo(targetArchivePath).Length;
            AppLogger.Log($"压缩包复制完成：{targetArchivePath}，大小 {targetBytes} 字节");
        }

        private static void TryDeleteTempArchive(string tempArchivePath, string modeName)
        {
            if (!File.Exists(tempArchivePath))
                return;

            try
            {
                File.Delete(tempArchivePath);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                AppLogger.Log($"{modeName}清理临时压缩包失败：{tempArchivePath}，原因：{ex.Message}");
            }
        }

        private static string CreateCopyOutputRoot(string modeRoot, string modeName)
        {
            Directory.CreateDirectory(modeRoot);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string copyRoot = Path.Combine(modeRoot, timestamp);
            int suffix = 1;

            while (Directory.Exists(copyRoot))
            {
                copyRoot = Path.Combine(modeRoot, $"{timestamp}_{suffix:00}");
                suffix++;
            }

            Directory.CreateDirectory(copyRoot);
            AppLogger.Log($"{modeName}已创建副本目录：{copyRoot}");
            return copyRoot;
        }

        /// <summary>
        /// 按源（压缩包名）分别只保留最近 keepCopies 份，再删掉因此清空的副本目录。
        /// 各文件夹可能按不同频率单独备份（一个副本目录里只有它自己的包），所以不能再按副本目录个数清理。
        /// </summary>
        private static int TrimArchives(string modeRoot, int keepCopies, string modeName)
        {
            if (keepCopies <= 0)
            {
                AppLogger.Log($"{modeName}保留份数为 0（不限），跳过清理。");
                return 0;
            }

            if (!Directory.Exists(modeRoot))
                return 0;

            List<IGrouping<string, FileInfo>> archivesBySource;
            try
            {
                archivesBySource = new DirectoryInfo(modeRoot).EnumerateDirectories()
                    .SelectMany(directory => directory.EnumerateFiles("*.7z"))
                    .GroupBy(file => Path.GetFileNameWithoutExtension(file.Name), StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                AppLogger.Log($"{modeName}检查旧副本失败：{modeRoot}，原因：{ex.Message}");
                return 0;
            }

            int removed = 0;
            HashSet<string> touchedDirectories = new(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, FileInfo> archives in archivesBySource)
            {
                // 副本目录名是时间戳（yyyyMMdd_HHmmss[_NN]），按名称排序即按时间先后。
                FileInfo[] expired = archives
                    .OrderBy(file => file.Directory!.Name, StringComparer.OrdinalIgnoreCase)
                    .SkipLast(keepCopies)
                    .ToArray();

                foreach (FileInfo archive in expired)
                {
                    try
                    {
                        archive.Attributes = FileAttributes.Normal;
                        archive.Delete();
                        removed++;
                        touchedDirectories.Add(archive.DirectoryName!);
                        AppLogger.Log($"{modeName}已清理旧副本：{archive.FullName}");
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        AppLogger.Log($"{modeName}旧副本清理失败：{archive.FullName}，原因：{ex.Message}");
                    }
                }
            }

            foreach (string directory in touchedDirectories)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    AppLogger.Log($"{modeName}删除空副本目录失败：{directory}，原因：{ex.Message}");
                }
            }

            if (removed > 0)
                AppLogger.Log($"{modeName}已按保留份数清理旧副本，每个文件夹保留最近 {keepCopies} 份。");
            return removed;
        }

        /// <summary>源目录对应的压缩包名（不含 .7z）；界面按它在备份目录里查找该源的备份。</summary>
        internal static string GetSourceFolderName(string sourcePath)
        {
            string fullPath = Path.GetFullPath(sourcePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(fullPath);

            if (!string.IsNullOrWhiteSpace(name))
                return name;

            return fullPath
                .Replace(':', '_')
                .Replace(Path.DirectorySeparatorChar, '_')
                .Replace(Path.AltDirectorySeparatorChar, '_');
        }
    }

    /// <summary>
    /// 定时模式：全局每日定时打包所有跟随全局的源；自定义了定时频率的源各自按时刻单独打包。
    /// 同一时刻只执行一份，上一份没跑完时不记账，等下一轮（30 秒后）再触发。
    /// </summary>
    internal static class BackgroundTimer
    {
        private static System.Windows.Forms.Timer _timer = null!;

        public static void Start()
        {
            _timer = new System.Windows.Forms.Timer { Interval = 30_000 };
            _timer.Tick += (_, _) =>
            {
                if (!CheckGlobalSchedule())
                    CheckCustomSchedules();
            };
            _timer.Start();
        }

        /// <returns>本轮是否触发了执行。</returns>
        private static bool CheckGlobalSchedule()
        {
            if (!TimeSpan.TryParse(AppSettings.RunTime, out TimeSpan target))
                return false;

            // 上次触发日期存注册表而非内存：否则每次重启（含开机自启）只要过了执行时间就会再跑一次。
            DateTime now = DateTime.Now;
            if (AppSettings.LastScheduledRunDate == now.Date || now < DateTime.Today.Add(target))
                return false;

            if (CopyHelper.IsScheduledBusy)
                return false;

            // 触发即记账：执行中途被关闭也不在当天重跑，保证每天最多自动执行一次。
            AppSettings.LastScheduledRunDate = now.Date;
            string[] sources = SourceGroups.Global(CopyMode.Scheduled);
            if (sources.Length == 0)
            {
                AppLogger.Log($"已到每日执行时间 {AppSettings.RunTime}，但没有跟随全局定时的文件夹，今日跳过。");
                return false;
            }

            AppLogger.Log($"已到每日执行时间 {AppSettings.RunTime}，自动触发定时模式（今日不再自动触发）。");
            _ = Task.Run(() => CopyHelper.RunScheduledCopy(sources, "定时模式"));
            return true;
        }

        private static void CheckCustomSchedules()
        {
            if (CopyHelper.IsScheduledBusy)
                return;

            DateTime now = DateTime.Now;
            foreach ((string path, SourceRule rule) in SourceGroups.Custom(CopyMode.Scheduled))
            {
                DateTime? slot = BackupSchedule.MostRecentSlot(rule.ParsedTimes, rule.ScheduledEveryDays, now);
                if (slot == null || AppSettings.GetSourceLastScheduledRun(path) >= slot)
                    continue;

                // 同全局定时：触发即记账；错过多个时刻（如关机期间）只补跑一次。
                AppSettings.SetSourceLastScheduledRun(path, now);
                string name = SourceGroups.DisplayName(path);
                AppLogger.Log($"已到「{name}」的自定义定时 {slot:HH:mm}，自动触发备份。");
                _ = Task.Run(() => CopyHelper.RunScheduledCopy(new[] { path }, $"定时模式（{name}）"));
                return;
            }
        }
    }

    /// <summary>
    /// 倒计时模式：跟随全局的源按全局间隔一起打包；自定义了倒计时频率的源各自计时、单独打包。
    /// 各组同时计时，到点的组依次执行；执行期间其他组照常计时，到点后排队等候。
    /// </summary>
    internal static class CountdownService
    {
        private const string GlobalJobKey = "*";

        private static System.Windows.Forms.Timer _timer = null!;
        private static bool _timerCreated;
        private static readonly List<CountdownJob> _jobs = new();

        private sealed class CountdownJob
        {
            public CountdownJob(string key, int interval)
            {
                Key = key;
                Interval = interval;
                Remaining = interval;
            }

            /// <summary>全局组为 GlobalJobKey，否则为源路径。</summary>
            public string Key { get; }
            public int Interval { get; }
            public int Remaining { get; set; }
            public bool IsGlobal => Key == GlobalJobKey;
            public string Name => IsGlobal ? "全局" : SourceGroups.DisplayName(Key);
            public string Label => IsGlobal ? "倒计时模式" : $"倒计时模式（{Name}）";
        }

        public static event Action? StateChanged;

        public static bool IsRunning { get; private set; }
        public static bool IsExecuting { get; private set; }
        public static bool HasJobs => _jobs.Count > 0;

        /// <summary>距离最近一组执行的秒数。</summary>
        public static int RemainingSeconds => _jobs.Count == 0 ? 0 : _jobs.Min(job => job.Remaining);

        /// <summary>按当前设置是否有可执行的倒计时（全局间隔 &gt; 0 且有跟随全局的源，或有源自定义了倒计时）。</summary>
        public static bool HasRunnableJobs() => BuildDesiredJobs().Count > 0;

        public static bool Start()
        {
            EnsureTimer();

            _jobs.Clear();
            _jobs.AddRange(BuildDesiredJobs().Select(job => new CountdownJob(job.Key, job.Interval)));
            if (_jobs.Count == 0)
            {
                AppLogger.Log("倒计时模式未启动：全局间隔为 0 或没有跟随全局的文件夹，且没有文件夹自定义倒计时频率。");
                RaiseStateChanged();
                return false;
            }

            IsRunning = true;
            IsExecuting = false;
            _timer.Start();

            AppLogger.Log($"倒计时模式已启动：{DescribeJobs()}。");
            RaiseStateChanged();
            return true;
        }

        public static void Stop(bool writeLog = true)
        {
            if (_timerCreated)
                _timer.Stop();

            bool hadState = IsRunning || IsExecuting;
            IsRunning = false;
            IsExecuting = false;
            _jobs.Clear();

            if (writeLog && hadState)
                AppLogger.Log("倒计时模式已停止。");

            RaiseStateChanged();
        }

        private static void EnsureTimer()
        {
            if (_timerCreated)
                return;

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (_, _) => HandleTick();
            AppEvents.SettingsChanged += Reconcile;
            _timerCreated = true;
        }

        private static List<(string Key, int Interval)> BuildDesiredJobs()
        {
            List<(string Key, int Interval)> jobs = new();
            int globalSeconds = AppSettings.CountdownSeconds;
            if (globalSeconds > 0 && SourceGroups.Global(CopyMode.Countdown).Length > 0)
                jobs.Add((GlobalJobKey, globalSeconds));

            foreach ((string path, SourceRule rule) in SourceGroups.Custom(CopyMode.Countdown))
            {
                if (rule.CountdownSeconds > 0)
                    jobs.Add((path, rule.CountdownSeconds));
            }

            return jobs;
        }

        /// <summary>设置保存后按最新的源和频率调整：间隔没变的组保留剩余时间，新增或改了间隔的组从头计时。</summary>
        private static void Reconcile()
        {
            if (!IsRunning)
                return;

            List<(string Key, int Interval)> desired = BuildDesiredJobs();
            bool changed = desired.Count != _jobs.Count;
            List<CountdownJob> next = new();
            foreach ((string key, int interval) in desired)
            {
                CountdownJob? existing = _jobs.FirstOrDefault(job =>
                    string.Equals(job.Key, key, StringComparison.OrdinalIgnoreCase) && job.Interval == interval);
                changed |= existing == null;
                next.Add(existing ?? new CountdownJob(key, interval));
            }

            if (!changed)
                return;

            _jobs.Clear();
            _jobs.AddRange(next);
            AppLogger.Log(_jobs.Count > 0
                ? $"倒计时频率已更新：{DescribeJobs()}。"
                : "倒计时频率已更新：当前没有可执行的倒计时，保存设置后会继续按新配置计时。");
            RaiseStateChanged();
        }

        private static string DescribeJobs() => string.Join("；", _jobs.Select(job =>
            $"{job.Name}每 {SourceRule.FormatDuration(job.Interval)} 创建 1 份新副本"));

        private static void HandleTick()
        {
            if (!IsRunning)
            {
                RaiseStateChanged();
                return;
            }

            foreach (CountdownJob job in _jobs)
            {
                if (job.Remaining > 0)
                    job.Remaining--;
            }

            CountdownJob? due = IsExecuting ? null : _jobs.FirstOrDefault(job => job.Remaining == 0);
            if (due == null)
            {
                RaiseStateChanged();
                return;
            }

            if (ShouldSkipCurrentExecution())
            {
                foreach (CountdownJob job in _jobs.Where(job => job.Remaining == 0))
                {
                    job.Remaining = job.Interval;
                    AppLogger.Log($"{job.Label}倒计时到达，但当前处于 {AppSettings.CountdownSkipStart}-{AppSettings.CountdownSkipEnd} 禁止执行时段，本次已跳过。");
                }

                RaiseStateChanged();
                return;
            }

            string[] sources = due.IsGlobal ? SourceGroups.Global(CopyMode.Countdown) : new[] { due.Key };
            string label = due.Label;
            IsExecuting = true;
            AppLogger.Log($"{label}倒计时到达，开始创建新副本。");
            RaiseStateChanged();

            _ = Task.Run(() =>
            {
                CopyHelper.RunCountdownCopy(sources, label);
                Program.UiContext.Post(_ =>
                {
                    IsExecuting = false;
                    if (IsRunning)
                        due.Remaining = due.Interval;

                    RaiseStateChanged();
                }, null);
            });
        }

        private static bool ShouldSkipCurrentExecution()
        {
            if (!AppSettings.CountdownSkipEnabled)
                return false;

            SourceRule.TryParseTime(AppSettings.CountdownSkipStart, out TimeSpan start);
            SourceRule.TryParseTime(AppSettings.CountdownSkipEnd, out TimeSpan end);
            DateTime now = DateTime.Now;
            TimeSpan current = new(now.Hour, now.Minute, 0);

            // 终点包含整分钟；终点早于起点时跨越午夜（例如 22:00 – 06:59）。
            return start <= end
                ? current >= start && current <= end
                : current >= start || current <= end;
        }

        private static void RaiseStateChanged()
        {
            StateChanged?.Invoke();
        }
    }

    internal static class Program
    {
        public static NotifyIcon TrayIcon = null!;
        public static SynchronizationContext UiContext = null!;

        /// <summary>为 true 时窗口允许真正关闭（托盘“退出”）；否则关闭只是收到托盘。</summary>
        public static bool IsExiting { get; private set; }

        private static bool _closeHintShown;
        private static System.Windows.Application _wpfApp = null!;
        private static MainWindow? _mainWindow;

        [STAThread]
        private static void Main()
        {
            bool startHidden = ShouldStartHidden();

            // 单实例：已在运行时，手动启动只唤起原窗口；开机自启/崩溃重启（--background）直接静默退出。
            using SingleInstance? instance = SingleInstance.TryAcquire(notifyExisting: !startHidden);
            if (instance == null)
                return;

            // 界面用 WPF（单窗口句柄、GPU 合成，拖动缩放流畅）；托盘图标、计时器、对话框仍沿用 WinForms 组件，
            // 它们只依赖 Win32 消息泵，WPF 的 Dispatcher 同样会泵送。
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();
            _wpfApp = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
            };
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(_wpfApp.Dispatcher));
            UiContext = SynchronizationContext.Current!;

            InstallExceptionHandlers();
            AppLogger.PruneOldLogFiles();
            AppLogger.Log(
                $"程序启动，EXE：{Application.ExecutablePath}，程序目录：{AppContext.BaseDirectory}，当前目录：{Environment.CurrentDirectory}");

            CrashRecovery.RegisterAutoRestart();
            AppSettings.RepairAutoStartEntry();
            ThemeManager.Initialize(_wpfApp);
            instance.ListenForShowRequests(() => UiContext.Post(_ => ShowMainWindow(), null));

            DateTime startedAt = Process.GetCurrentProcess().StartTime;
            string destPath = AppSettings.DestPath;
            _ = Task.Run(() => TempArchiveCleaner.CleanLeftovers(
                startedAt, destPath, AppContext.BaseDirectory, Path.GetTempPath()));

            TrayIcon = new NotifyIcon
            {
                Icon = AppIcon.Get(),
                Visible = true,
                Text = "AutoCopy - 自动复制"
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开设置", null, (_, _) => ShowMainWindow());
            menu.Items.Add("立即执行定时模式", null, (_, _) => _ = Task.Run(() => CopyHelper.RunScheduledCopy()));
            menu.Items.Add("打开日志目录", null, (_, _) => OpenLogDirectory());
            menu.Items.Add(new ToolStripSeparator());
            WifiScreenOff.Start();
            var wifiScreenOffItem = new ToolStripMenuItem("WiFi 断开时自动息屏")
            {
                CheckOnClick = true,
                Checked = WifiScreenOff.Enabled,
                ToolTipText = WifiScreenOff.Description + "。"
            };
            menu.Opening += (_, _) => wifiScreenOffItem.ToolTipText = WifiScreenOff.Description + "。";
            wifiScreenOffItem.CheckedChanged += (_, _) => WifiScreenOff.Enabled = wifiScreenOffItem.Checked;
            WifiScreenOff.EnabledChanged += () => wifiScreenOffItem.Checked = WifiScreenOff.Enabled;
            menu.Items.Add(wifiScreenOffItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApplication());

            TrayIcon.ContextMenuStrip = menu;
            BackupProgress.Changed += () => UiContext.Post(_ => UpdateTrayText(), null);
            TrayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ShowMainWindow();
            };

            BackgroundTimer.Start();
            ResourceMonitor.Start();

            if (!startHidden)
                _wpfApp.Startup += (_, _) => ShowMainWindow();

            _wpfApp.Run();
            AppLogger.Log("程序已退出。");
        }

        private static void InstallExceptionHandlers()
        {
            // 界面线程异常：记录后继续运行，不让一个异常把常驻程序带走。
            _wpfApp.DispatcherUnhandledException += (_, e) =>
            {
                AppLogger.Log($"界面线程出现未处理异常，已拦截，程序继续运行：{e.Exception}");
                e.Handled = true;
            };
            // WinForms 组件（托盘菜单、计时器）回调里的异常走这里。
            Application.ThreadException += (_, e) =>
                AppLogger.Log($"界面线程出现未处理异常，已拦截，程序继续运行：{e.Exception}");

            // 后台线程异常无法拦截，进程会退出；记录原因，之后由 Windows 崩溃自动重启拉起。
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppLogger.Log($"后台线程出现未处理异常，程序即将退出（运行满 60 秒时系统会自动重启本程序）：{e.ExceptionObject}");

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.Log($"后台任务出现未观察的异常，已忽略：{e.Exception}");
                e.SetObserved();
            };
        }

        /// <summary>只有托盘菜单“退出”会真正结束程序；关闭/最小化窗口都只是收到托盘。</summary>
        public static void ExitApplication()
        {
            string? warning = CopyHelper.IsBusy
                ? "正在执行备份，退出会中断本次备份（未完成的压缩包会在下次启动时清理）。"
                : CountdownService.IsRunning
                    ? "倒计时模式正在运行，退出后将停止，下次启动不会自动恢复。"
                    : null;

            if (warning != null &&
                MessageBox.Show($"{warning}\n\n确定退出 AutoCopy？", "AutoCopy",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            AppLogger.Log("用户从托盘菜单退出程序。");
            IsExiting = true;
            CountdownService.Stop(writeLog: false);
            TrayIcon.Visible = false;
            TrayIcon.Dispose();
            _mainWindow?.Close();
            _wpfApp.Shutdown();
        }

        /// <summary>窗口被关闭/最小化收到托盘时调用：每次运行只提示一次程序仍在后台。</summary>
        public static void NotifyHiddenToTray()
        {
            if (_closeHintShown)
                return;

            _closeHintShown = true;
            ShowTrayMessage("AutoCopy 仍在后台运行",
                "备份计划照常执行。点击托盘图标打开窗口，右键选择「退出」才会完全关闭。",
                ToolTipIcon.Info, 4000);
        }

        public static void OpenLogDirectory()
        {
            try
            {
                Directory.CreateDirectory(AppLogger.LogDirectory);
                Process.Start(new ProcessStartInfo { FileName = AppLogger.LogDirectory, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"打开日志目录失败：{ex.Message}");
            }
        }

        /// <summary>托盘悬停提示显示备份进度，窗口收在托盘时也能看到。</summary>
        private static void UpdateTrayText()
        {
            if (!TrayIcon.Visible)
                return;

            var running = BackupProgress.GetSnapshot().Where(session => !session.Finished).ToArray();
            TrayIcon.Text = running.Length == 0
                ? "AutoCopy - 自动复制"
                : $"AutoCopy - 备份中 {running.Average(session => session.OverallPercent):0}%";
        }

        public static void ShowTrayMessage(string title, string message, ToolTipIcon icon, int timeout = 3000)
        {
            UiContext.Post(_ =>
            {
                if (TrayIcon.Visible)
                    TrayIcon.ShowBalloonTip(timeout, title, message, icon);
            }, null);
        }

        private static bool ShouldStartHidden()
        {
            return Environment.GetCommandLineArgs()
                .Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>主窗口整个运行期只创建一次，关闭/最小化只是隐藏到托盘。</summary>
        private static void ShowMainWindow()
        {
            _mainWindow ??= new MainWindow();

            if (!_mainWindow.IsVisible)
                _mainWindow.Show();

            if (_mainWindow.WindowState == System.Windows.WindowState.Minimized)
                _mainWindow.WindowState = System.Windows.WindowState.Normal;

            _mainWindow.Activate();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace AntoCopy
{
    internal enum RuleMode
    {
        /// <summary>跟随全局频率，和其他跟随全局的文件夹一起打包。</summary>
        Global,

        /// <summary>按本文件夹自己的频率单独打包。</summary>
        Custom,

        /// <summary>不参与该模式。</summary>
        Off
    }

    /// <summary>某个源文件夹单独设置的备份频率；两种模式都跟随全局时不保存。</summary>
    internal sealed class SourceRule
    {
        public RuleMode ScheduledMode { get; set; }

        /// <summary>自定义定时：每天的执行时刻（HH:mm，升序、去重）。</summary>
        public List<string> ScheduledTimes { get; set; } = new();

        /// <summary>自定义定时：每隔几天执行一天（1 = 每天）。</summary>
        public int ScheduledEveryDays { get; set; } = 1;

        public RuleMode CountdownMode { get; set; }

        /// <summary>自定义倒计时：间隔秒数。</summary>
        public int CountdownSeconds { get; set; }

        [JsonIgnore]
        public bool IsDefault => ScheduledMode == RuleMode.Global && CountdownMode == RuleMode.Global;

        public RuleMode ModeOf(CopyMode mode) => mode == CopyMode.Scheduled ? ScheduledMode : CountdownMode;

        [JsonIgnore]
        public IReadOnlyList<TimeSpan> ParsedTimes => ScheduledTimes
            .Select(text => TryParseTime(text, out TimeSpan time) ? time : (TimeSpan?)null)
            .Where(time => time.HasValue)
            .Select(time => time!.Value)
            .OrderBy(time => time)
            .ToArray();

        public SourceRule Clone() => new()
        {
            ScheduledMode = ScheduledMode,
            ScheduledTimes = new List<string>(ScheduledTimes),
            ScheduledEveryDays = ScheduledEveryDays,
            CountdownMode = CountdownMode,
            CountdownSeconds = CountdownSeconds
        };

        public bool SameScheduleAs(SourceRule other) =>
            ScheduledMode == other.ScheduledMode &&
            ScheduledEveryDays == other.ScheduledEveryDays &&
            ScheduledTimes.SequenceEqual(other.ScheduledTimes);

        /// <summary>列表里显示的简短说明；全部跟随全局时为空。</summary>
        public string ShortText => string.Join(" · ", new[]
        {
            ScheduledMode switch
            {
                RuleMode.Custom => "定时" + DescribeDays() + (ScheduledTimes.Count == 1 ? " " + ScheduledTimes[0] : $"{ScheduledTimes.Count}次"),
                RuleMode.Off => "不定时",
                _ => string.Empty
            },
            CountdownMode switch
            {
                RuleMode.Custom => "倒计时每" + FormatDuration(CountdownSeconds),
                RuleMode.Off => "不倒计时",
                _ => string.Empty
            }
        }.Where(part => part.Length > 0));

        /// <summary>悬停提示里的完整说明。</summary>
        public string LongText =>
            "定时：" + ScheduledMode switch
            {
                RuleMode.Custom => $"{DescribeDays()} {string.Join("、", ScheduledTimes)}",
                RuleMode.Off => "不参与",
                _ => "跟随全局"
            } +
            "；倒计时：" + CountdownMode switch
            {
                RuleMode.Custom => $"每 {FormatDuration(CountdownSeconds)}",
                RuleMode.Off => "不参与",
                _ => "跟随全局"
            };

        private string DescribeDays() => ScheduledEveryDays <= 1 ? "每天" : $"每{ScheduledEveryDays}天";

        public static bool TryParseTime(string text, out TimeSpan time)
        {
            string[] formats = { @"h\:mm", @"hh\:mm" };
            return TimeSpan.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, out time) &&
                   time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        }

        /// <summary>解析“02:00, 14:00”这类时刻列表（逗号、顿号、分号或空格分隔），输出升序去重的 HH:mm。</summary>
        public static bool TryParseTimes(string text, out List<string> times)
        {
            times = new List<string>();
            List<TimeSpan> parsed = new();
            foreach (string part in text.Split(new[] { ',', '，', '、', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseTime(part, out TimeSpan time))
                    return false;
                parsed.Add(time);
            }

            times = parsed.Distinct().OrderBy(time => time).Select(time => time.ToString(@"hh\:mm")).ToList();
            return times.Count > 0;
        }

        public static string FormatDuration(int seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            List<string> parts = new();
            if (time.Days > 0) parts.Add($"{time.Days} 天");
            if (time.Hours > 0) parts.Add($"{time.Hours} 小时");
            if (time.Minutes > 0) parts.Add($"{time.Minutes} 分");
            if (time.Seconds > 0) parts.Add($"{time.Seconds} 秒");
            return string.Join(" ", parts);
        }
    }

    internal static class BackupSchedule
    {
        /// <summary>“每 N 天”的起算日：与此相差 N 的整数倍的日子才执行。</summary>
        private static readonly DateTime EveryDaysAnchor = new(2000, 1, 1);

        /// <summary>不晚于 now 的最近一个执行时刻；没有时刻可用时返回 null。</summary>
        public static DateTime? MostRecentSlot(IReadOnlyList<TimeSpan> times, int everyDays, DateTime now)
        {
            if (times.Count == 0)
                return null;

            everyDays = Math.Max(1, everyDays);
            // 往前最多看 everyDays 天，必然覆盖上一个执行日。
            for (int back = 0; back <= everyDays; back++)
            {
                DateTime day = now.Date.AddDays(-back);
                if ((day - EveryDaysAnchor).Days % everyDays != 0)
                    continue;

                for (int i = times.Count - 1; i >= 0; i--)
                {
                    DateTime slot = day + times[i];
                    if (slot <= now)
                        return slot;
                }
            }

            return null;
        }
    }

    /// <summary>按频率设置把源文件夹分组。</summary>
    internal static class SourceGroups
    {
        /// <summary>跟随全局频率的源：全局定时 / 全局倒计时一次把它们打包进同一个副本目录。</summary>
        public static string[] Global(CopyMode mode) =>
            AppSettings.SourcePaths.Where(path => AppSettings.GetSourceRule(path).ModeOf(mode) == RuleMode.Global).ToArray();

        /// <summary>参与该模式的全部源（手动“立即执行”用）。</summary>
        public static string[] Participating(CopyMode mode) =>
            AppSettings.SourcePaths.Where(path => AppSettings.GetSourceRule(path).ModeOf(mode) != RuleMode.Off).ToArray();

        /// <summary>该模式下自定义了频率的源。</summary>
        public static List<(string Path, SourceRule Rule)> Custom(CopyMode mode) =>
            AppSettings.SourcePaths
                .Select(path => (Path: path, Rule: AppSettings.GetSourceRule(path)))
                .Where(item => item.Rule.ModeOf(mode) == RuleMode.Custom)
                .ToList();

        public static string DisplayName(string sourcePath)
        {
            try
            {
                return CopyHelper.GetSourceFolderName(sourcePath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return sourcePath;
            }
        }
    }
}

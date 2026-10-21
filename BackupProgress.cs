using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AntoCopy
{
    internal enum BackupItemState
    {
        /// <summary>排队中，还没轮到。</summary>
        Pending,
        Compressing,
        Copying,
        Done,
        Failed,

        /// <summary>前面的文件夹失败导致本次未执行。</summary>
        Skipped
    }

    internal sealed record BackupItemSnapshot(string Path, string Name, BackupItemState State, double Percent, string? Error);

    internal sealed record BackupSessionSnapshot(
        int Id, string Label, bool Finished, bool Failed, string? Message, IReadOnlyList<BackupItemSnapshot> Items)
    {
        public int DoneCount => Items.Count(item => item.State == BackupItemState.Done);

        public BackupItemSnapshot? Current =>
            Items.FirstOrDefault(item => item.State is BackupItemState.Compressing or BackupItemState.Copying);

        /// <summary>整体进度：每个文件夹权重相同，压缩占 85%，复制压缩包占 15%。</summary>
        public double OverallPercent => Items.Count == 0 ? 0 : Items.Sum(item => item.State switch
        {
            BackupItemState.Done or BackupItemState.Failed or BackupItemState.Skipped => 1.0,
            BackupItemState.Compressing => 0.85 * item.Percent / 100,
            BackupItemState.Copying => 0.85 + 0.15 * item.Percent / 100,
            _ => 0.0
        }) / Items.Count * 100;
    }

    /// <summary>
    /// 正在进行（以及刚结束）的备份进度，供界面显示哪些文件夹已备份、正在备份、还没轮到。
    /// 定时与倒计时可能同时在跑，各自一个会话；结束后保留几秒再移除，让人看清结果。
    /// </summary>
    internal static class BackupProgress
    {
        private const int KeepFinishedMilliseconds = 8000;

        private sealed class Session
        {
            public required int Id { get; init; }
            public required string Label { get; init; }
            public required List<BackupItemSnapshot> Items { get; init; }
            public bool Finished { get; set; }
            public bool Failed { get; set; }
            public string? Message { get; set; }
        }

        private static readonly object _lock = new();
        private static readonly List<Session> _sessions = new();
        private static int _nextId;

        /// <summary>任何进度变化时触发（可能在后台线程、且很频繁，界面需自行合并刷新）。</summary>
        public static event Action? Changed;

        public static int Begin(string label, IEnumerable<string> sources)
        {
            Session session = new()
            {
                Id = ++_nextId,
                Label = label,
                Items = sources
                    .Select(path => new BackupItemSnapshot(path, SourceGroups.DisplayName(path), BackupItemState.Pending, 0, null))
                    .ToList()
            };

            lock (_lock)
                _sessions.Add(session);
            Changed?.Invoke();
            return session.Id;
        }

        public static void Update(int sessionId, string path, BackupItemState state, double percent = 0, string? error = null)
        {
            lock (_lock)
            {
                Session? session = _sessions.FirstOrDefault(s => s.Id == sessionId);
                int index = session?.Items.FindIndex(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) ?? -1;
                if (session == null || index < 0)
                    return;

                BackupItemSnapshot old = session.Items[index];
                double clamped = Math.Clamp(percent, 0, 100);
                // 7z 每几百毫秒报一次进度，百分比没变就不通知界面。
                if (old.State == state && Math.Abs(old.Percent - clamped) < 1 && error == null)
                    return;

                session.Items[index] = old with { State = state, Percent = clamped, Error = error };
            }

            Changed?.Invoke();
        }

        /// <param name="message">失败原因；成功为 null。</param>
        public static void End(int sessionId, string? message)
        {
            lock (_lock)
            {
                Session? session = _sessions.FirstOrDefault(s => s.Id == sessionId);
                if (session == null)
                    return;

                session.Finished = true;
                session.Failed = message != null;
                session.Message = message;
                for (int i = 0; i < session.Items.Count; i++)
                {
                    if (session.Items[i].State is BackupItemState.Pending or BackupItemState.Compressing or BackupItemState.Copying)
                        session.Items[i] = session.Items[i] with { State = BackupItemState.Skipped, Percent = 0 };
                }
            }

            Changed?.Invoke();

            _ = Task.Delay(KeepFinishedMilliseconds).ContinueWith(_ =>
            {
                lock (_lock)
                    _sessions.RemoveAll(s => s.Id == sessionId);
                Changed?.Invoke();
            }, TaskScheduler.Default);
        }

        public static IReadOnlyList<BackupSessionSnapshot> GetSnapshot()
        {
            lock (_lock)
            {
                return _sessions
                    .Select(s => new BackupSessionSnapshot(s.Id, s.Label, s.Finished, s.Failed, s.Message, s.Items.ToArray()))
                    .ToArray();
            }
        }
    }
}

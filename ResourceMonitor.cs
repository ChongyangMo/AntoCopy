using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AntoCopy
{
    internal sealed class ResourceSnapshot
    {
        public double SystemCpu { get; init; }
        public double MemoryPercent { get; init; }
        public ulong MemoryUsed { get; init; }
        public ulong MemoryTotal { get; init; }

        /// <summary>备份目标所在磁盘根目录；未设置目标时为 null。</summary>
        public string? DiskRoot { get; init; }
        /// <summary>已用百分比；读不到（如网络路径断开）时为 null。</summary>
        public double? DiskPercent { get; init; }
        public ulong DiskFree { get; init; }
        public ulong DiskTotal { get; init; }

        /// <summary>本程序 + 7z 子进程占全部逻辑处理器的 CPU 百分比。</summary>
        public double AppCpu { get; init; }
        public long AppMemory { get; init; }
        public double AppReadPerSecond { get; init; }
        public double AppWritePerSecond { get; init; }
        public bool BackupRunning { get; init; }
    }

    /// <summary>本程序（含 7z 子进程）的累计 CPU 时间与磁盘读写量；7z 已退出的部分也由作业对象记账。</summary>
    internal readonly record struct AppTotals(long CpuTicks, ulong ReadBytes, ulong WriteBytes);

    /// <summary>一次备份期间的资源占用：平均值由累计量精确计算，峰值取自每 2 秒的采样。</summary>
    internal sealed class BackupUsageTracker
    {
        private readonly object _lock = new();
        private double _peakCpu;
        private long _peakMemory;

        internal BackupUsageTracker(AppTotals start, long memory)
        {
            Start = start;
            StartTick = Environment.TickCount64;
            _peakMemory = memory;
        }

        internal AppTotals Start { get; }
        internal long StartTick { get; }

        internal void AddSample(double cpu, long memory)
        {
            lock (_lock)
            {
                _peakCpu = Math.Max(_peakCpu, cpu);
                _peakMemory = Math.Max(_peakMemory, memory);
            }
        }

        internal (double PeakCpu, long PeakMemory) Peaks
        {
            get
            {
                lock (_lock)
                    return (_peakCpu, _peakMemory);
            }
        }
    }

    /// <summary>
    /// 每 2 秒在后台采样一次：系统 CPU/内存、备份目标磁盘容量，以及本程序（含 7z 压缩进程）的 CPU、内存、磁盘读写。
    /// 无论窗口是否显示都在运行，备份期间的平均/峰值据此汇总写入日志。
    /// </summary>
    internal static class ResourceMonitor
    {
        private const int SampleMilliseconds = 2000;

        /// <summary>磁盘容量每 5 次（约 10 秒）查一次：目标可能是网络路径，查询较慢。</summary>
        private const int DiskSampleEvery = 5;

        private static readonly object _trackersLock = new();
        private static readonly List<BackupUsageTracker> _trackers = new();
        private static Timer? _timer;
        private static long _lastTick;
        private static long _lastIdle, _lastKernel, _lastUser;
        private static AppTotals _lastApp;
        private static int _sampleCount;
        private static (string? Root, double? Percent, ulong Free, ulong Total) _disk;
        private static bool _errorLogged;

        /// <summary>每次采样后在后台线程触发。</summary>
        public static event Action<ResourceSnapshot>? Updated;

        public static ResourceSnapshot? Latest { get; private set; }

        /// <summary>最近一次备份的资源占用说明（带完成时刻）。</summary>
        public static string? LastBackupSummary { get; private set; }

        public static void Start()
        {
            _timer = new Timer(_ => Sample(), null, 0, Timeout.Infinite);
        }

        public static BackupUsageTracker BeginBackup()
        {
            BackupUsageTracker tracker = new(ReadAppTotals(), ReadAppMemory());
            lock (_trackersLock)
                _trackers.Add(tracker);
            return tracker;
        }

        /// <summary>结束统计，返回“耗时、CPU 平均/峰值、内存峰值、读写量”说明。同时有两份备份在跑时为程序整体的占用。</summary>
        public static string EndBackup(BackupUsageTracker tracker)
        {
            lock (_trackersLock)
                _trackers.Remove(tracker);

            AppTotals end = ReadAppTotals();
            double seconds = Math.Max(0.001, (Environment.TickCount64 - tracker.StartTick) / 1000.0);
            double averageCpu = CpuPercent(end.CpuTicks - tracker.Start.CpuTicks, seconds);
            (double peakCpu, long peakMemory) = tracker.Peaks;
            peakCpu = Math.Max(peakCpu, averageCpu);

            string duration = seconds < 1 ? "不到 1 秒" : SourceRule.FormatDuration((int)Math.Round(seconds));
            string summary =
                $"耗时 {duration}，CPU 平均 {averageCpu:0}%（峰值 {peakCpu:0}%），内存峰值 {FormatBytes(peakMemory)}，" +
                $"读取 {FormatBytes((long)(end.ReadBytes - tracker.Start.ReadBytes))}，写入 {FormatBytes((long)(end.WriteBytes - tracker.Start.WriteBytes))}";
            LastBackupSummary = $"{DateTime.Now:MM-dd HH:mm} {summary}";
            return summary;
        }

        private static void Sample()
        {
            try
            {
                long tick = Environment.TickCount64;
                double elapsed = _lastTick == 0 ? 0 : (tick - _lastTick) / 1000.0;

                NativeMethods.GetSystemTimes(out long idle, out long kernel, out long user);
                double systemCpu = 0;
                if (elapsed > 0)
                {
                    // 内核时间包含空闲时间。
                    long total = (kernel - _lastKernel) + (user - _lastUser);
                    systemCpu = total > 0 ? (total - (idle - _lastIdle)) * 100.0 / total : 0;
                }
                (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);

                NativeMethods.MEMORYSTATUSEX memory = new() { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
                NativeMethods.GlobalMemoryStatusEx(ref memory);
                ulong memoryUsed = memory.ullTotalPhys - memory.ullAvailPhys;

                if (_sampleCount++ % DiskSampleEvery == 0)
                    _disk = ReadDisk(AppSettings.DestPath);

                AppTotals app = ReadAppTotals();
                long appMemory = ReadAppMemory();
                double appCpu = 0, readRate = 0, writeRate = 0;
                if (elapsed > 0)
                {
                    appCpu = CpuPercent(app.CpuTicks - _lastApp.CpuTicks, elapsed);
                    readRate = (app.ReadBytes - _lastApp.ReadBytes) / elapsed;
                    writeRate = (app.WriteBytes - _lastApp.WriteBytes) / elapsed;
                }
                _lastApp = app;
                _lastTick = tick;

                lock (_trackersLock)
                {
                    foreach (BackupUsageTracker tracker in _trackers)
                        tracker.AddSample(appCpu, appMemory);
                }

                ResourceSnapshot snapshot = new()
                {
                    SystemCpu = Math.Clamp(systemCpu, 0, 100),
                    MemoryPercent = memory.ullTotalPhys > 0 ? memoryUsed * 100.0 / memory.ullTotalPhys : 0,
                    MemoryUsed = memoryUsed,
                    MemoryTotal = memory.ullTotalPhys,
                    DiskRoot = _disk.Root,
                    DiskPercent = _disk.Percent,
                    DiskFree = _disk.Free,
                    DiskTotal = _disk.Total,
                    AppCpu = appCpu,
                    AppMemory = appMemory,
                    AppReadPerSecond = readRate,
                    AppWritePerSecond = writeRate,
                    BackupRunning = CopyHelper.IsBusy
                };
                Latest = snapshot;
                Updated?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                // 计时器回调在后台线程，异常不拦截会让整个程序退出；只记一次，避免每 2 秒刷屏。
                if (!_errorLogged)
                {
                    _errorLogged = true;
                    AppLogger.Log($"资源占用采样出错（不影响备份）：{ex.Message}");
                }
            }
            finally
            {
                _timer?.Change(SampleMilliseconds, Timeout.Infinite);
            }
        }

        private static double CpuPercent(long cpuTicks, double seconds) =>
            Math.Clamp(cpuTicks / (seconds * TimeSpan.TicksPerSecond * Environment.ProcessorCount) * 100, 0, 100);

        private static AppTotals ReadAppTotals()
        {
            using Process self = Process.GetCurrentProcess();
            long cpu = self.TotalProcessorTime.Ticks;
            NativeMethods.GetProcessIoCounters(self.Handle, out NativeMethods.IO_COUNTERS io);
            ulong read = io.ReadTransferCount, write = io.WriteTransferCount;

            IntPtr job = ChildProcessJob.HandleIfCreated;
            if (job != IntPtr.Zero && NativeMethods.QueryInformationJobObject(
                    job, NativeMethods.JobObjectBasicAndIoAccountingInformation, out NativeMethods.JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION info,
                    (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
            {
                cpu += info.TotalUserTime + info.TotalKernelTime;
                read += info.IoInfo.ReadTransferCount;
                write += info.IoInfo.WriteTransferCount;
            }

            return new AppTotals(cpu, read, write);
        }

        /// <summary>本程序 + 正在运行的 7z 进程的工作集。</summary>
        private static long ReadAppMemory()
        {
            using Process self = Process.GetCurrentProcess();
            long total = self.WorkingSet64;
            foreach (int pid in ChildProcessJob.GetProcessIds())
            {
                try
                {
                    using Process child = Process.GetProcessById(pid);
                    total += child.WorkingSet64;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                {
                    // 进程刚好退出。
                }
            }

            return total;
        }

        private static (string? Root, double? Percent, ulong Free, ulong Total) ReadDisk(string destPath)
        {
            if (string.IsNullOrWhiteSpace(destPath))
                return (null, null, 0, 0);

            string? root = null;
            try
            {
                root = Path.GetPathRoot(Path.GetFullPath(destPath));
                if (string.IsNullOrEmpty(root))
                    return (null, null, 0, 0);

                // 网络共享根目录（\\server\share）须带结尾反斜杠。
                string query = root.EndsWith('\\') ? root : root + "\\";
                if (NativeMethods.GetDiskFreeSpaceEx(query, out ulong free, out ulong total, out _) && total > 0)
                    return (root, (total - free) * 100.0 / total, free, total);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                // 路径非法，按读不到处理。
            }

            return (root, null, 0, 0);
        }

        public static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            return $"{value:0.##} {units[unitIndex]}";
        }
    }
}

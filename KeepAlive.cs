using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace AntoCopy
{
    /// <summary>
    /// 单实例：同一用户会话只允许一个 AutoCopy，避免两个定时器重复备份。
    /// 再次启动时通知已运行的实例显示窗口，然后自身退出。
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private const string MutexName = @"Local\AutoCopy.SingleInstance";
        private const string ShowEventName = @"Local\AutoCopy.ShowWindow";

        private readonly Mutex _mutex;
        private EventWaitHandle? _showEvent;

        private SingleInstance(Mutex mutex)
        {
            _mutex = mutex;
        }

        /// <summary>成为首个实例则返回守卫对象；否则（按需）唤起已有实例窗口并返回 null。</summary>
        public static SingleInstance? TryAcquire(bool notifyExisting)
        {
            Mutex mutex = new(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew)
                return new SingleInstance(mutex);

            mutex.Dispose();
            if (notifyExisting && EventWaitHandle.TryOpenExisting(ShowEventName, out EventWaitHandle? showEvent))
            {
                using (showEvent)
                    showEvent.Set();
            }

            return null;
        }

        /// <summary>监听“再次启动”信号，在后台线程收到后回调（回调需自行切回界面线程）。</summary>
        public void ListenForShowRequests(Action onShowRequested)
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            EventWaitHandle showEvent = _showEvent;
            Thread listener = new(() =>
            {
                try
                {
                    while (showEvent.WaitOne())
                        onShowRequested();
                }
                catch (ObjectDisposedException)
                {
                    // 退出时句柄已释放。
                }
            })
            {
                IsBackground = true,
                Name = "AutoCopy.ShowWindowListener"
            };
            listener.Start();
        }

        public void Dispose()
        {
            _showEvent?.Dispose();
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 非持有线程释放，忽略；进程退出时系统会回收。
            }
            _mutex.Dispose();
        }
    }

    /// <summary>
    /// 把 7z 子进程放进“随句柄关闭而终止”的作业对象：AutoCopy 无论正常退出、崩溃还是被结束进程，
    /// 正在跑的 7z 都会一起结束，不会留下孤儿进程继续往磁盘写临时包。
    /// </summary>
    internal static class ChildProcessJob
    {
        private static readonly Lazy<IntPtr> _job = new(CreateKillOnCloseJob);

        /// <summary>作业对象句柄；还没启动过 7z 时为 IntPtr.Zero（资源统计不必为此创建作业对象）。</summary>
        public static IntPtr HandleIfCreated => _job.IsValueCreated ? _job.Value : IntPtr.Zero;

        /// <summary>作业对象中正在运行的 7z 进程 ID。</summary>
        public static int[] GetProcessIds()
        {
            IntPtr job = HandleIfCreated;
            if (job == IntPtr.Zero)
                return Array.Empty<int>();

            // JOBOBJECT_BASIC_PROCESS_ID_LIST：两个 DWORD 后接 ULONG_PTR 数组；7z 同时最多一两个，留 32 个位置足够。
            const int capacity = 32;
            int length = 8 + capacity * IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!NativeMethods.QueryInformationJobObject(job, NativeMethods.JobObjectBasicProcessIdList, buffer, (uint)length, IntPtr.Zero))
                    return Array.Empty<int>();

                int count = Math.Min(Marshal.ReadInt32(buffer, 4), capacity);
                int[] ids = new int[count];
                for (int i = 0; i < count; i++)
                    ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                return ids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static void Attach(Process process)
        {
            IntPtr job = _job.Value;
            if (job == IntPtr.Zero)
                return;

            if (!NativeMethods.AssignProcessToJobObject(job, process.Handle))
                AppLogger.Log($"7z 进程加入作业对象失败（不影响备份，但 AutoCopy 退出时它不会被自动结束）：{new Win32Exception().Message}");
        }

        private static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                AppLogger.Log($"创建作业对象失败：{new Win32Exception().Message}");
                return IntPtr.Zero;
            }

            NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new();
            info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!NativeMethods.SetInformationJobObject(
                        job, NativeMethods.JobObjectExtendedLimitInformation, buffer, (uint)length))
                {
                    AppLogger.Log($"设置作业对象失败：{new Win32Exception().Message}");
                    NativeMethods.CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // 句柄故意不关闭：进程结束时由系统关闭，从而触发 KILL_ON_JOB_CLOSE。
            return job;
        }
    }

    /// <summary>备份期间阻止系统自动睡眠（不影响熄屏），用 using 包住备份过程。</summary>
    internal sealed class KeepAwakeScope : IDisposable
    {
        private KeepAwakeScope()
        {
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
        }

        public static KeepAwakeScope Begin() => new();

        public void Dispose()
        {
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        }
    }

    internal static class CrashRecovery
    {
        private const int RestartNoReboot = 8;

        /// <summary>
        /// 注册 Windows 崩溃/无响应后自动重启（进程需已运行满 60 秒才生效），以后台模式启动。
        /// 不包括系统重启后的恢复——那由“开机自动启动”负责。
        /// </summary>
        public static void RegisterAutoRestart()
        {
            int result = NativeMethods.RegisterApplicationRestart("--background", RestartNoReboot);
            if (result != 0)
                AppLogger.Log($"注册崩溃自动重启失败，HRESULT=0x{result:X8}");
        }
    }

    /// <summary>清理上次异常退出遗留的临时压缩包（只匹配本程序生成的 名称__模式__32位GUID.7z）。</summary>
    internal static class TempArchiveCleaner
    {
        private static readonly Regex TempArchivePattern =
            new(@"^.+__(scheduled|countdown)__[0-9a-f]{32}\.7z$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <param name="writtenBefore">只删最后写入早于此时刻的文件（传本进程启动时间），绝不碰本次运行正在写的包。</param>
        public static void CleanLeftovers(DateTime writtenBefore, params string?[] directories)
        {
            foreach (string directory in directories
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Select(path => Path.GetFullPath(path!))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(directory))
                        continue;

                    foreach (string file in Directory.EnumerateFiles(directory, "*__*__*.7z"))
                    {
                        if (!TempArchivePattern.IsMatch(Path.GetFileName(file)) ||
                            File.GetLastWriteTime(file) >= writtenBefore)
                            continue;

                        try
                        {
                            File.Delete(file);
                            AppLogger.Log($"已清理上次中断遗留的临时压缩包：{file}");
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            AppLogger.Log($"清理遗留临时压缩包失败：{file}，原因：{ex.Message}");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    AppLogger.Log($"检查遗留临时压缩包失败：{directory}，原因：{ex.Message}");
                }
            }
        }
    }

    internal static class NativeMethods
    {
        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterApplicationRestart(string? pwzCommandline, int dwFlags);

        public const uint WM_SYSCOMMAND = 0x0112;
        public static readonly IntPtr SC_MONITORPOWER = new(0xF170);
        public static readonly IntPtr MONITOR_OFF = new(2);
        public const int WlanInterfaceStateConnected = 1;

        public const uint PROGRESS_CONTINUE = 0;

        public delegate uint CopyProgressRoutine(
            long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
            uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

        /// <summary>flags 为 0 时目标已存在会被覆盖（与 File.Copy overwrite: true 一致）。</summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CopyFileEx(
            string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine,
            IntPtr data, IntPtr cancel, uint copyFlags);

        public const int JobObjectBasicProcessIdList = 3;
        public const int JobObjectBasicAndIoAccountingInformation = 8;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
            public IO_COUNTERS IoInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool QueryInformationJobObject(
            IntPtr hJob, int infoClass, out JOBOBJECT_BASIC_AND_IO_ACCOUNTING_INFORMATION info, uint length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, IntPtr info, uint length, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_CAPTION_COLOR = 35; // Win11 22000+，值为 COLORREF（0x00BBGGRR）

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

        [DllImport("wlanapi.dll")]
        public static extern void WlanFreeMemory(IntPtr memory);

        [DllImport("wlanapi.dll")]
        public static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    }
}

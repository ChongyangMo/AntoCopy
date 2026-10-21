using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AntoCopy
{
    /// <summary>
    /// 息屏但不睡眠：
    /// 1. WiFi 从已连接变为断开（例如手机热点随人离开）时自动关闭显示器，WiFi 恢复前阻止系统自动睡眠；
    /// 2. 手动“立即息屏”，有人操作键鼠前阻止系统自动睡眠。
    /// 只关屏不锁屏，动鼠标/键盘即可点亮。
    /// </summary>
    internal static class WifiScreenOff
    {
        private const int PollMilliseconds = 2000;

        /// <summary>连续这么多次（约 6 秒）都是断开才动作，避免漫游、信号抖动误触发。</summary>
        private const int ConfirmPolls = 3;

        /// <summary>两次轮询间隔远超预期说明刚从睡眠/休眠唤醒；WiFi 重连需要时间，宽限期内的断开不算。</summary>
        private const long ResumeGapMilliseconds = 15_000;
        private const long ResumeGraceMilliseconds = 60_000;

        /// <summary>手动息屏前稍等，免得点击菜单时手还在动鼠标，屏幕立刻被唤醒。</summary>
        private const int ManualOffDelayMilliseconds = 800;

        private static System.Windows.Forms.Timer? _timer;
        private static bool _enabled;
        private static bool _wasConnected;
        private static int _disconnectedPolls;
        private static long _lastPollTick;
        private static long _graceUntilTick;

        private static bool _wifiHold;
        private static bool _manualHold;
        private static uint _inputTickAtManualOff;
        private static bool _keepingAwake;

        /// <summary>开关变化（托盘菜单和主窗口菜单据此同步勾选状态），在界面线程触发。</summary>
        public static event Action? EnabledChanged;

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;

                _enabled = value;
                AppSettings.ScreenOffOnWifiLost = value;
                ResetWifiState();
                AppLogger.Log(value ? "已开启：WiFi 断开时自动息屏。" : "已关闭：WiFi 断开时自动息屏。");
                EnabledChanged?.Invoke();
            }
        }

        /// <summary>须在界面线程调用（计时器回调和防睡眠状态都绑定在界面线程上）。</summary>
        public static void Start()
        {
            _enabled = AppSettings.ScreenOffOnWifiLost;
            _timer = new System.Windows.Forms.Timer { Interval = PollMilliseconds };
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
        }

        /// <summary>手动立即息屏，须在界面线程调用。</summary>
        public static async Task TurnOffNowAsync()
        {
            AppLogger.Log("手动息屏：关闭显示器（有人操作键鼠前系统不会自动睡眠）。");
            await Task.Delay(ManualOffDelayMilliseconds);

            _inputTickAtManualOff = GetLastInputTick();
            _manualHold = true;
            UpdateKeepAwake();
            TurnOffDisplay();
        }

        private static void Poll()
        {
            long now = Environment.TickCount64;
            if (_lastPollTick != 0 && now - _lastPollTick > ResumeGapMilliseconds)
                _graceUntilTick = now + ResumeGraceMilliseconds;
            _lastPollTick = now;

            if (_manualHold && GetLastInputTick() != _inputTickAtManualOff)
            {
                _manualHold = false;
                UpdateKeepAwake();
                AppLogger.Log("检测到键鼠操作，手动息屏结束，撤销防睡眠。");
            }

            if (!_enabled)
                return;

            bool? connected = IsWifiConnected();
            if (connected == null)
                return;

            if (connected.Value)
            {
                if (_wifiHold)
                {
                    _wifiHold = false;
                    UpdateKeepAwake();
                    AppLogger.Log("WiFi 已恢复连接，撤销防睡眠。");
                }

                _wasConnected = true;
                _disconnectedPolls = 0;
                return;
            }

            // 只对“已连接 → 断开”动作；本来就没连 WiFi（或刚唤醒还没重连上）不处理。
            if (!_wasConnected || now < _graceUntilTick)
            {
                _wasConnected = false;
                _disconnectedPolls = 0;
                return;
            }

            if (++_disconnectedPolls < ConfirmPolls)
                return;

            _wasConnected = false;
            _disconnectedPolls = 0;
            AppLogger.Log("检测到 WiFi 已断开，关闭显示器（系统保持运行，WiFi 恢复前不会自动睡眠）。");
            _wifiHold = true;
            UpdateKeepAwake();
            TurnOffDisplay();
        }

        private static void ResetWifiState()
        {
            _wifiHold = false;
            UpdateKeepAwake();
            _wasConnected = false;
            _disconnectedPolls = 0;
        }

        /// <summary>WiFi 断开或手动息屏任一仍在生效就阻止系统自动睡眠（不影响熄屏）。</summary>
        private static void UpdateKeepAwake()
        {
            bool keep = _wifiHold || _manualHold;
            if (keep == _keepingAwake)
                return;

            _keepingAwake = keep;
            NativeMethods.SetThreadExecutionState(keep
                ? NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED
                : NativeMethods.ES_CONTINUOUS);
        }

        private static void TurnOffDisplay()
        {
            // 广播给所有顶层窗口由 DefWindowProc 处理；放后台线程，免得被无响应的窗口拖住界面。
            _ = Task.Run(() => NativeMethods.SendMessageTimeout(
                NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND,
                NativeMethods.SC_MONITORPOWER, NativeMethods.MONITOR_OFF,
                NativeMethods.SMTO_ABORTIFHUNG, 1000, out _));
        }

        private static uint GetLastInputTick()
        {
            NativeMethods.LASTINPUTINFO info = new() { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
            return NativeMethods.GetLastInputInfo(ref info) ? info.dwTime : 0;
        }

        /// <summary>任一无线网卡处于已连接状态返回 true；WLAN 服务不可用返回 null（状态未知，不动作）。</summary>
        private static bool? IsWifiConnected()
        {
            if (NativeMethods.WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr client) != 0)
                return null;

            try
            {
                if (NativeMethods.WlanEnumInterfaces(client, IntPtr.Zero, out IntPtr list) != 0)
                    return null;

                try
                {
                    // WLAN_INTERFACE_INFO_LIST：两个 DWORD 后接数组；每项 GUID(16) + WCHAR[256](512) + 状态(4)。
                    // 网卡被拔掉时列表为空，按断开处理。
                    const int headerSize = 8, itemSize = 532, stateOffset = 528;
                    int count = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < count; i++)
                    {
                        if (Marshal.ReadInt32(list, headerSize + i * itemSize + stateOffset) == NativeMethods.WlanInterfaceStateConnected)
                            return true;
                    }

                    return false;
                }
                finally
                {
                    NativeMethods.WlanFreeMemory(list);
                }
            }
            finally
            {
                NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
            }
        }
    }
}

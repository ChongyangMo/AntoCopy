using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AntoCopy
{
    /// <summary>
    /// 息屏但不睡眠：
    /// 1. WiFi 从已连接变为断开（例如手机热点随人离开）且键鼠空闲一段时间后自动关闭显示器，WiFi 恢复前阻止系统自动睡眠；
    /// 2. 手动“立即息屏”，有人操作键鼠前阻止系统自动睡眠。
    /// 只关屏不锁屏，动鼠标/键盘即可点亮。
    /// </summary>
    internal static class WifiScreenOff
    {
        private const int PollMilliseconds = 2000;

        /// <summary>连续这么多次（约 6 秒）都是断开才动作，避免漫游、信号抖动误触发。</summary>
        private const int ConfirmPolls = 3;

        /// <summary>“息屏前空闲时间”的上限（1 天）。</summary>
        public const int MaxIdleSeconds = 86_400;

        /// <summary>两次轮询间隔远超预期说明刚从睡眠/休眠唤醒；WiFi 重连需要时间，宽限期内的断开不算。</summary>
        private const long ResumeGapMilliseconds = 15_000;
        private const long ResumeGraceMilliseconds = 60_000;

        /// <summary>手动息屏前稍等，免得点击菜单时手还在动鼠标，屏幕立刻被唤醒。</summary>
        private const int ManualOffDelayMilliseconds = 800;

        private static System.Windows.Forms.Timer? _timer;

        /// <summary>
        /// 只用来接收息屏消息的隐藏顶层窗口，由 DefWindowProc 处理 SC_MONITORPOWER。
        /// 不能广播给所有窗口：每个窗口都会各自关一次屏，几百个窗口逐个处理要拖很久，
        /// 期间动鼠标点亮后又被下一个窗口关掉，各程序的界面线程也会被拖住（曾把 explorer 拖到无响应）。
        /// </summary>
        private static System.Windows.Forms.NativeWindow? _powerWindow;

        private static bool _enabled;
        private static bool _wasConnected;
        private static int _disconnectedPolls;
        private static long _lastPollTick;
        private static long _graceUntilTick;

        private static bool _wifiHold;

        /// <summary>已确认 WiFi 断开、还在等键鼠空闲；息屏或用户点提示取消后清除，每次断开最多息屏一次。</summary>
        private static bool _wifiOffPending;

        /// <summary>自动息屏前右下角倒计时的秒数。</summary>
        private const int ToastSeconds = 10;

        /// <summary>正在显示的息屏倒计时提示，及其弹出时的最后输入时刻（用来判断倒计时期间有没有人动过键鼠）。</summary>
        private static ScreenOffToast? _toast;
        private static uint _inputTickAtToast;

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

        /// <summary>
        /// WiFi 断开后还要键鼠空闲满这么多秒才息屏：人还在电脑前时热点掉线不应关屏。
        /// 0 表示确认断开后立即息屏。
        /// </summary>
        public static int IdleSeconds
        {
            get => AppSettings.ScreenOffIdleSeconds;
            set
            {
                value = Math.Clamp(value, 0, MaxIdleSeconds);
                if (AppSettings.ScreenOffIdleSeconds == value)
                    return;

                AppSettings.ScreenOffIdleSeconds = value;
                AppLogger.Log($"息屏前空闲时间已设为 {value} 秒。");
            }
        }

        /// <summary>菜单提示文字，随当前空闲时间设置变化。</summary>
        public static string Description =>
            $"已连接的 WiFi 断开、且键鼠空闲满 {IdleSeconds} 秒后，右下角倒计时 {ToastSeconds} 秒关闭显示器（点击提示可取消）；" +
            "系统不睡眠，备份照常执行，WiFi 恢复后撤销";

        /// <summary>须在界面线程调用（计时器回调和防睡眠状态都绑定在界面线程上）。</summary>
        public static void Start()
        {
            _enabled = AppSettings.ScreenOffOnWifiLost;
            _powerWindow = new System.Windows.Forms.NativeWindow();
            _powerWindow.CreateHandle(new System.Windows.Forms.CreateParams());
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
                    _wifiOffPending = false;
                    DismissToast();
                    UpdateKeepAwake();
                    AppLogger.Log("WiFi 已恢复连接，撤销防睡眠。");
                }

                _wasConnected = true;
                _disconnectedPolls = 0;
                return;
            }

            if (_wifiOffPending)
            {
                TurnOffWhenIdle();
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
            AppLogger.Log($"检测到 WiFi 已断开，键鼠空闲满 {IdleSeconds} 秒后倒计时息屏（WiFi 恢复前系统不会自动睡眠）。");
            _wifiHold = true;
            _wifiOffPending = true;
            UpdateKeepAwake();
            TurnOffWhenIdle();
        }

        /// <summary>
        /// 键鼠空闲够久后先在右下角倒计时，走完才息屏；每次断开只息屏一次：之后有人动键鼠点亮屏幕就不再关。
        /// </summary>
        private static void TurnOffWhenIdle()
        {
            if (_toast != null)
                return;

            uint idleMilliseconds = unchecked((uint)Environment.TickCount - GetLastInputTick());
            if (idleMilliseconds < (uint)IdleSeconds * 1000)
                return;

            AppLogger.Log($"WiFi 仍断开且键鼠已空闲 {idleMilliseconds / 1000} 秒，{ToastSeconds} 秒后关闭显示器（点击右下角提示可取消）。");
            _inputTickAtToast = GetLastInputTick();
            _toast = new ScreenOffToast(ToastSeconds);
            _toast.Finished += HandleToastFinished;
            _toast.Cancelled += HandleToastCancelled;
            _toast.Show();
        }

        private static void HandleToastFinished()
        {
            _toast = null;

            // 倒计时期间动过键鼠（例如正要去点提示、或在别处打字）说明有人在用电脑：这次不关，重新等空闲。
            if (GetLastInputTick() != _inputTickAtToast)
            {
                AppLogger.Log("息屏倒计时期间检测到键鼠操作，暂不息屏，继续等待键鼠空闲。");
                return;
            }

            _wifiOffPending = false;
            AppLogger.Log("息屏倒计时结束，关闭显示器。");
            TurnOffDisplay();
        }

        private static void HandleToastCancelled()
        {
            _toast = null;
            _wifiOffPending = false;
            AppLogger.Log("已点击提示，取消本次息屏（WiFi 恢复前不再自动息屏）。");
        }

        private static void DismissToast()
        {
            _toast?.Dismiss();
            _toast = null;
        }

        private static void ResetWifiState()
        {
            _wifiHold = false;
            _wifiOffPending = false;
            DismissToast();
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
            // 只投递给自己的隐藏窗口一次，由它的 DefWindowProc 关屏；不广播（原因见 _powerWindow）。
            if (_powerWindow != null)
                NativeMethods.PostMessage(_powerWindow.Handle, NativeMethods.WM_SYSCOMMAND,
                    NativeMethods.SC_MONITORPOWER, NativeMethods.MONITOR_OFF);
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

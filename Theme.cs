using System;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace AntoCopy
{
    internal enum AppTheme
    {
        System,
        Light,
        Dark
    }

    /// <summary>
    /// 白天/黑夜主题。配色在 Themes\Light.xaml、Themes\Dark.xaml，控件样式在 Themes\Controls.xaml，都挂在应用级资源上，
    /// 所有窗口共用；切换时替换配色字典，界面颜色都用 DynamicResource 引用，随之刷新。
    /// “跟随系统”时随 Windows 的应用深浅色设置自动切换。
    /// </summary>
    internal static class ThemeManager
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        /// <summary>所选主题或实际深浅色变化时触发，在界面线程。</summary>
        public static event Action? Changed;

        public static AppTheme Selected { get; private set; }
        public static bool IsDark { get; private set; }

        /// <summary>须在界面线程、创建任何窗口之前调用：装载配色和控件样式到应用级资源。</summary>
        public static void Initialize(Application app)
        {
            Selected = AppSettings.Theme;
            IsDark = ResolveIsDark();
            app.Resources.MergedDictionaries.Add(LoadColors());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/AntoCopy;component/Themes/Controls.xaml", UriKind.Relative)
            });

            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General)
                    Program.UiContext.Post(_ => Refresh(notifyAlways: false), null);
            };
        }

        /// <summary>窗口句柄创建后按当前主题设置标题栏深浅色。</summary>
        public static void Attach(Window window)
        {
            window.SourceInitialized += (_, _) => ApplyTitleBar(window);
        }

        public static void Select(AppTheme theme)
        {
            if (Selected == theme)
                return;

            Selected = theme;
            AppSettings.Theme = theme;
            Refresh(notifyAlways: true);
        }

        private static ResourceDictionary LoadColors() => new()
        {
            Source = new Uri($"/AntoCopy;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };

        /// <summary>替换应用级配色字典（第一个），并刷新所有已打开窗口的标题栏。</summary>
        private static void ApplyToApplication()
        {
            Application app = Application.Current;
            app.Resources.MergedDictionaries[0] = LoadColors();
            foreach (Window window in app.Windows)
                ApplyTitleBar(window);
        }

        /// <summary>Windows 11 / 10 20H1+ 的深色标题栏；窗口句柄创建前调用无效。</summary>
        private static void ApplyTitleBar(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int dark = IsDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }

        private static void Refresh(bool notifyAlways)
        {
            bool dark = ResolveIsDark();
            if (!notifyAlways && dark == IsDark)
                return;

            IsDark = dark;
            ApplyToApplication();
            Changed?.Invoke();
        }

        private static bool ResolveIsDark() => Selected switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => IsSystemDark()
        };

        private static bool IsSystemDark()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}

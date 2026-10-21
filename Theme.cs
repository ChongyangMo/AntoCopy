using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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

        /// <summary>每个窗口的标题栏涂成哪个配色。</summary>
        private static readonly ConditionalWeakTable<Window, string> CaptionBrushKeys = new();

        /// <summary>窗口句柄创建后按当前主题设置标题栏深浅色和底色。</summary>
        /// <param name="captionBrushKey">标题栏底色取哪个配色：和窗口最上面一条内容的底色一致，标题栏与内容连成一体、看不出分界</param>
        public static void Attach(Window window, string captionBrushKey = "PageBrush")
        {
            CaptionBrushKeys.AddOrUpdate(window, captionBrushKey);
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

        /// <summary>
        /// Windows 11 / 10 20H1+ 的深色标题栏；Windows 11 上再把标题栏涂成内容的底色（Win10 不认这个属性，保持系统色）。
        /// 窗口句柄创建前调用无效。
        /// </summary>
        private static void ApplyTitleBar(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int dark = IsDark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            if (CaptionBrushKeys.TryGetValue(window, out string? key) && window.TryFindResource(key) is SolidColorBrush caption)
            {
                Color c = caption.Color;
                int colorRef = c.R | c.G << 8 | c.B << 16;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
            }
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

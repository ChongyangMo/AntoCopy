using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AntoCopy
{
    /// <summary>程序图标（托盘与窗口共用）：蓝色圆角方块 + 白色“下载到托盘”箭头。</summary>
    internal static class AppIcon
    {
        private static readonly Color Accent = Color.FromArgb(37, 99, 235);
        private static Icon? _icon;
        private static System.Windows.Media.ImageSource? _imageSource;

        public static Icon Get() => _icon ??= Create();

        /// <summary>供 WPF 窗口使用的同一图标。</summary>
        public static System.Windows.Media.ImageSource GetImageSource()
        {
            if (_imageSource != null)
                return _imageSource;

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                Get().Handle,
                System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return _imageSource = source;
        }

        private static Icon Create()
        {
            using Bitmap bitmap = new(32, 32);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using GraphicsPath path = RoundedRect(new Rectangle(1, 1, 29, 29), 7);
                using SolidBrush fill = new(Accent);
                g.FillPath(fill, path);

                using Pen pen = new(Color.White, 3F)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                g.DrawLine(pen, 16, 7, 16, 18);
                g.DrawLines(pen, new[] { new Point(11, 14), new Point(16, 19), new Point(21, 14) });
                g.DrawLines(pen, new[] { new Point(8, 19), new Point(8, 24), new Point(24, 24), new Point(24, 19) });
            }

            return Icon.FromHandle(bitmap.GetHicon());
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}

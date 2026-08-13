using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LoudEQ
{
    // 运行时绘制应用图标（均衡条样式），无需附带 .ico 文件
    internal static class AppIcon
    {
        public static Icon Create()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundedRect(2, 2, 28, 28, 7))
                using (var brush = new SolidBrush(Color.FromArgb(0x2B, 0x5A, 0x9E)))
                    g.FillPath(brush, path);
                using (var bar = new SolidBrush(Color.White))
                {
                    g.FillRectangle(bar, 8, 14, 4, 10);
                    g.FillRectangle(bar, 14, 7, 4, 17);
                    g.FillRectangle(bar, 20, 12, 4, 12);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally { DestroyIcon(h); }
            }
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
        {
            var p = new GraphicsPath();
            int d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}

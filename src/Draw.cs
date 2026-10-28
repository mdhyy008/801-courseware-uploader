using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal static class Draw
    {
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0) d = 1;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color color, Rectangle r, int radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(color))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Color color, Rectangle r, int radius, float width)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            Rectangle rr = new Rectangle(r.X, r.Y, Math.Max(1, r.Width - 1), Math.Max(1, r.Height - 1));
            using (GraphicsPath p = Round(rr, radius))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, p);
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        public static int Dpi(Control c)
        {
            if (c != null && c.IsHandleCreated)
            {
                try
                {
                    uint dpi = GetDpiForWindow(c.Handle);
                    if (dpi >= 96) return (int)dpi;
                }
                catch
                {
                }
            }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    return (int)Math.Round(g.DpiX);
            }
            catch
            {
                return 96;
            }
        }

        public static int Scale(Control c, int px)
        {
            return (int)Math.Round(px * Dpi(c) / 96.0);
        }

        public static Color Mix(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * amount),
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
        }

        public static void CheckMark(Graphics g, Rectangle box, Color color)
        {
            float w = box.Width;
            using (Pen pen = new Pen(color, Math.Max(1.6f, w * 0.12f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                PointF[] pts = new PointF[]
                {
                    new PointF(box.Left + w * 0.24f, box.Top + w * 0.52f),
                    new PointF(box.Left + w * 0.43f, box.Top + w * 0.71f),
                    new PointF(box.Left + w * 0.77f, box.Top + w * 0.32f)
                };
                g.DrawLines(pen, pts);
            }
        }

        public static void Cross(Graphics g, Rectangle box, Color color, float thickness)
        {
            using (Pen pen = new Pen(color, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int inset = box.Width / 4;
                Rectangle r = new Rectangle(box.X + inset, box.Y + inset,
                    box.Width - inset * 2, box.Height - inset * 2);
                g.DrawLine(pen, r.Left, r.Top, r.Right, r.Bottom);
                g.DrawLine(pen, r.Right, r.Top, r.Left, r.Bottom);
            }
        }

        public static void Chevron(Graphics g, Rectangle box, Color color, bool right)
        {
            using (Pen pen = new Pen(color, 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int w = box.Width / 2;
                int cy = box.Top + box.Height / 2;
                int cx = box.Left + box.Width / 2;
                if (right)
                {
                    g.DrawLine(pen, cx - w / 3, cy - w / 2, cx + w / 4, cy);
                    g.DrawLine(pen, cx + w / 4, cy, cx - w / 3, cy + w / 2);
                }
                else
                {
                    g.DrawLine(pen, cx + w / 3, cy - w / 2, cx - w / 4, cy);
                    g.DrawLine(pen, cx - w / 4, cy, cx - w / 3, cy + w / 2);
                }
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double value = bytes / 1024.0;
            if (value < 1024) return value.ToString("0.0") + " KB";
            value = value / 1024.0;
            if (value < 1024) return value.ToString("0.0") + " MB";
            value = value / 1024.0;
            return value.ToString("0.00") + " GB";
        }
    }
}

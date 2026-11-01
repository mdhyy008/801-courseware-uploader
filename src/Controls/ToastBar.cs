using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class ToastBar : Control
    {
        private string message = "";

        public ToastBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
            Visible = false;
            Font = Theme.FontBody;
        }

        public void SetMessage(string text)
        {
            message = text ?? "";
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084)
            {
                m.Result = (IntPtr)(-1);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = ClientRectangle;
            int radius = Draw.Scale(this, 8);
            Draw.FillRound(g, Color.FromArgb(26, 0, 0, 0),
                new Rectangle(r.X + 1, r.Y + 2, r.Width - 2, r.Height - 2), radius);
            Draw.FillRound(g, Theme.Surface, r, radius);
            Draw.StrokeRound(g, Theme.Border, r, radius, 1f);

            TextRenderer.DrawText(g, message, Font, r, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}

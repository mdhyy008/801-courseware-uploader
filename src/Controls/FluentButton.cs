using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class FluentButton : Control
    {
        public enum Variant
        {
            Primary,
            Secondary,
            Ghost,
            Danger
        }

        public enum Glyph
        {
            None,
            Qr,
            Check,
            Copy,
            Refresh
        }

        private Variant variant = Variant.Secondary;
        private Glyph icon = Glyph.None;
        private bool hover;
        private bool pressed;

        public FluentButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            Font = Theme.FontBody;
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Theme.Window;
        }

        public Variant Style
        {
            get { return variant; }
            set { variant = value; Invalidate(); }
        }

        public Glyph Icon
        {
            get { return icon; }
            set { icon = value; Invalidate(); }
        }

        public void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                pressed = true;
                Focus();
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (pressed)
            {
                pressed = false;
                Invalidate();
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Enter || keyData == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                PerformClick();
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = ClientRectangle;
            if (r.Width <= 2 || r.Height <= 2) return;
            int radius = Draw.Scale(this, 7);

            Color back = Color.Empty;
            Color fore = Theme.Text;
            Color border = Color.Empty;

            if (!Enabled)
            {
                back = Theme.Surface;
                fore = Theme.TextMuted;
                border = Theme.Border;
            }
            else if (variant == Variant.Primary)
            {
                back = pressed ? Draw.Mix(Theme.Accent, Color.Black, 0.14f)
                    : (hover ? Theme.AccentHover : Theme.Accent);
                fore = Theme.AccentText;
            }
            else if (variant == Variant.Danger)
            {
                back = pressed ? Draw.Mix(Theme.Danger, Color.Black, 0.14f)
                    : (hover ? Draw.Mix(Theme.Danger, Color.White, 0.1f) : Theme.Danger);
                fore = Color.White;
            }
            else if (variant == Variant.Ghost)
            {
                back = pressed ? Theme.Pressed : (hover ? Theme.Hover : Color.Transparent);
                fore = Theme.Text;
            }
            else
            {
                back = pressed ? Draw.Mix(Theme.SurfaceHover, Theme.Text, 0.1f)
                    : (hover ? Theme.SurfaceHover : Theme.Surface);
                fore = Theme.Text;
                border = Theme.Border;
            }

            if (back != Color.Empty && back.A > 0)
                Draw.FillRound(g, back, r, radius);
            if (border != Color.Empty)
                Draw.StrokeRound(g, border, r, radius, 1f);
            if (Focused)
                Draw.StrokeRound(g, Theme.Accent, Rectangle.Inflate(r, -1, -1), radius, 1.5f);

            if (icon != Glyph.None)
            {
                int size = Draw.Scale(this, 14);
                Rectangle box = new Rectangle(r.X + (r.Width - size) / 2,
                    r.Y + (r.Height - size) / 2, size, size);
                if (icon == Glyph.Qr) DrawQr(g, box, fore);
                else if (icon == Glyph.Copy) DrawCopy(g, box, fore);
                else if (icon == Glyph.Refresh) DrawRefresh(g, box, fore);
                else Draw.CheckMark(g, box, fore);
                return;
            }

            TextRenderer.DrawText(g, Text, Font, r, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static void DrawRefresh(Graphics g, Rectangle box, Color color)
        {
            float size = box.Width;
            float thickness = Math.Max(1.4f, size * 0.09f);
            float cx = box.Left + size / 2f;
            float cy = box.Top + size / 2f;
            float r = size / 2f - thickness;
            using (Pen pen = new Pen(color, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 30f, 300f);

                double end = 330d * Math.PI / 180d;
                float px = cx + (float)(r * Math.Cos(end));
                float py = cy + (float)(r * Math.Sin(end));
                float dirX = (float)-Math.Sin(end);
                float dirY = (float)Math.Cos(end);
                float len = size * 0.26f;
                float perpX = -dirY * len * 0.62f;
                float perpY = dirX * len * 0.62f;
                g.DrawLine(pen, px, py, px - dirX * len + perpX, py - dirY * len + perpY);
                g.DrawLine(pen, px, py, px - dirX * len - perpX, py - dirY * len - perpY);
            }
        }

        private static void DrawCopy(Graphics g, Rectangle box, Color color)
        {
            int size = box.Width;
            int off = Math.Max(2, size / 4);
            int side = size - off;
            int radius = Math.Max(2, size / 5);
            float thickness = Math.Max(1.2f, size * 0.08f);
            Draw.StrokeRound(g, color, new Rectangle(box.Left, box.Top + off, side, side),
                radius, thickness);
            using (Pen pen = new Pen(color, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(box.Left + off, box.Top),
                    new PointF(box.Right, box.Top),
                    new PointF(box.Right, box.Bottom - off)
                });
            }
        }

        private static void DrawQr(Graphics g, Rectangle box, Color color)
        {
            int cell = Math.Max(1, box.Width / 7);
            int size = cell * 7;
            int left = box.X + (box.Width - size) / 2;
            int top = box.Y + (box.Height - size) / 2;
            using (SolidBrush brush = new SolidBrush(color))
            {
                DrawQrCorner(g, brush, left, top, cell);
                DrawQrCorner(g, brush, left + cell * 4, top, cell);
                DrawQrCorner(g, brush, left, top + cell * 4, cell);
                g.FillRectangle(brush, left + cell * 4, top + cell * 4, cell, cell);
                g.FillRectangle(brush, left + cell * 6, top + cell * 4, cell, cell);
                g.FillRectangle(brush, left + cell * 4, top + cell * 6, cell, cell);
                g.FillRectangle(brush, left + cell * 5, top + cell * 5, cell, cell);
                g.FillRectangle(brush, left + cell * 6, top + cell * 6, cell, cell);
            }
        }

        private static void DrawQrCorner(Graphics g, Brush brush, int x, int y, int cell)
        {
            g.FillRectangle(brush, x, y, cell * 3, cell);
            g.FillRectangle(brush, x, y + cell * 2, cell * 3, cell);
            g.FillRectangle(brush, x, y, cell, cell * 3);
            g.FillRectangle(brush, x + cell * 2, y, cell, cell * 3);
            g.FillRectangle(brush, x + cell, y + cell, cell, cell);
        }
    }
}

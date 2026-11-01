using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class FluentToggle : Control
    {
        private bool checkedState;
        private bool hover;

        public event EventHandler CheckedChanged;

        public FluentToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Theme.Window;
            Size = new Size(44, 24);
        }

        public bool Checked
        {
            get { return checkedState; }
            set
            {
                if (checkedState == value) return;
                checkedState = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
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
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Focus();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Toggle();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                Toggle();
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

        private void Toggle()
        {
            if (Enabled) Checked = !Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = ClientRectangle;
            if (r.Width <= 4 || r.Height <= 4) return;
            int radius = r.Height / 2;

            Color track;
            if (!Enabled) track = Theme.Border;
            else if (checkedState) track = hover ? Theme.AccentHover : Theme.Accent;
            else track = hover ? Draw.Mix(Theme.Border, Theme.Text, 0.2f) : Theme.Border;

            Draw.FillRound(g, track, r, radius);

            int pad = Math.Max(2, radius / 5);
            int dot = r.Height - pad * 2;
            int x = checkedState ? r.Right - dot - pad : r.Left + pad;
            using (SolidBrush brush = new SolidBrush(Enabled ? Color.White : Theme.Surface))
                g.FillEllipse(brush, x, r.Top + pad, dot, dot);

            if (Focused)
                Draw.StrokeRound(g, Theme.Accent, Rectangle.Inflate(r, -1, -1), radius, 1.5f);
        }
    }
}

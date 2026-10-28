using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class FluentTextBox : Control
    {
        private sealed class InnerTextBox : TextBox
        {
            public string Hint = "";

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F || Hint.Length == 0 || TextLength != 0) return;
                using (Graphics g = Graphics.FromHwnd(Handle))
                using (SolidBrush brush = new SolidBrush(Theme.TextMuted))
                {
                    int y = (ClientSize.Height - Font.Height) / 2;
                    g.DrawString(Hint, Font, brush, 0, y);
                }
            }
        }

        private readonly InnerTextBox box = new InnerTextBox();
        private bool focused;

        public FluentTextBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            box.BorderStyle = BorderStyle.None;
            box.Font = Theme.FontBody;
            box.BackColor = Theme.Surface;
            box.ForeColor = Theme.Text;
            box.ShortcutsEnabled = true;
            box.GotFocus += delegate { focused = true; Invalidate(); };
            box.LostFocus += delegate { focused = false; Invalidate(); };
            box.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
            Controls.Add(box);

            BackColor = Theme.Window;
            Height = 32;
        }

        public TextBox Inner
        {
            get { return box; }
        }

        public bool Multiline
        {
            get { return box.Multiline; }
            set
            {
                box.Multiline = value;
                box.ScrollBars = value ? ScrollBars.Vertical : ScrollBars.None;
                box.AcceptsReturn = value;
                LayoutBox();
                Invalidate();
            }
        }

        public bool Password
        {
            get { return box.UseSystemPasswordChar; }
            set { box.UseSystemPasswordChar = value; }
        }

        public string Hint
        {
            get { return box.Hint; }
            set
            {
                box.Hint = value == null ? "" : value;
                box.Invalidate();
            }
        }

        public override string Text
        {
            get { return box.Text; }
            set { box.Text = value; }
        }

        public void FocusInput()
        {
            box.Focus();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            box.Focus();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            LayoutBox();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            box.Font = Font;
            LayoutBox();
        }

        private void LayoutBox()
        {
            int pad = Draw.Scale(this, 10);
            int padV = Draw.Scale(this, 6);
            int width = Math.Max(0, Width - pad * 2);
            if (box.Multiline)
            {
                box.SetBounds(pad, padV, width, Math.Max(0, Height - padV * 2));
            }
            else
            {
                int height = box.PreferredHeight;
                int top = (Height - height) / 2;
                if (top < padV) top = padV;
                box.SetBounds(pad, top, width, height);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            if (r.Width <= 2 || r.Height <= 2) return;
            int radius = Draw.Scale(this, 7);
            Draw.FillRound(g, Theme.Surface, r, radius);
            Draw.StrokeRound(g, focused ? Theme.Accent : Theme.Border, r, radius, focused ? 1.6f : 1f);
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal class FlatDialog : Form
    {
        public FlatDialog(string title)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Native.ApplyWindowChrome(Handle, Theme.IsDark);
            Native.ApplyDarkScrollbars(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.CenterOnScreen(this);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(Theme.Window))
                e.Graphics.FillRectangle(brush, ClientRectangle);
            base.OnPaint(e);
        }
    }
}

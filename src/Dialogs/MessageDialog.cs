using System;
using System.Drawing;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class MessageDialog : FlatDialog
    {
        private readonly string message;
        private readonly FluentButton okButton;
        private readonly FluentButton cancelButton;
        private Rectangle messageRect;

        private MessageDialog(string title, string message, string okText, bool hasCancel, bool danger)
            : base(title)
        {
            this.message = message == null ? "" : message;
            okButton = new FluentButton();
            okButton.Text = okText;
            okButton.Style = danger ? FluentButton.Variant.Danger : FluentButton.Variant.Primary;
            okButton.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(okButton);

            if (hasCancel)
            {
                cancelButton = new FluentButton();
                cancelButton.Text = "取消";
                cancelButton.Style = FluentButton.Variant.Ghost;
                cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(cancelButton);
            }
        }

        public static void Show(IWin32Window owner, string title, string message)
        {
            using (MessageDialog dialog = new MessageDialog(title, message, "确定", false, false))
                dialog.ShowDialog(owner);
        }

        public static bool Confirm(IWin32Window owner, string title, string message,
            string okText, bool danger)
        {
            using (MessageDialog dialog = new MessageDialog(title, message, okText, true, danger))
                return dialog.ShowDialog(owner) == DialogResult.OK;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            int pad = Draw.Scale(this, 20);
            int maxWidth = Draw.Scale(this, 340);
            Size textSize = TextRenderer.MeasureText(message, Theme.FontBody,
                new Size(maxWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int buttonHeight = Draw.Scale(this, 32);

            int width = Math.Max(maxWidth, textSize.Width) + pad * 2;
            int height = pad + textSize.Height + Draw.Scale(this, 18) + buttonHeight + pad;
            ClientSize = new Size(width, height);
            messageRect = new Rectangle(pad, pad, width - pad * 2, textSize.Height);

            Rectangle okBounds = ButtonBounds(okButton, buttonHeight);
            okBounds.X = width - pad - okBounds.Width;
            okBounds.Y = height - pad - buttonHeight;
            okButton.Bounds = okBounds;

            if (cancelButton != null)
            {
                Rectangle cancelBounds = ButtonBounds(cancelButton, buttonHeight);
                cancelBounds.X = okBounds.X - Draw.Scale(this, 6) - cancelBounds.Width;
                cancelBounds.Y = okBounds.Y;
                cancelButton.Bounds = cancelBounds;
            }

            Native.CenterOnScreen(this);
        }

        private Rectangle ButtonBounds(FluentButton button, int height)
        {
            int textWidth = TextRenderer.MeasureText(button.Text, button.Font).Width;
            int width = Math.Max(Draw.Scale(this, 76), textWidth + Draw.Scale(this, 28));
            return new Rectangle(0, 0, width, height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, message, Theme.FontBody, messageRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak |
                TextFormatFlags.NoPrefix);
        }
    }
}

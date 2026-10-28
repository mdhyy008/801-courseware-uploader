using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class SettingsDialog : FlatDialog
    {
        private readonly FileCatalog catalog;
        private readonly WebAuth auth;
        private readonly UploadManager uploads;

        private readonly FluentTextBox concurrencyBox;
        private readonly FluentTextBox whitelistBox;
        private readonly FluentTextBox passwordBox;
        private readonly FluentButton defaultsButton;
        private readonly FluentButton cancelButton;
        private readonly FluentButton saveButton;
        private readonly FluentButton clearCacheButton;

        private Rectangle concurrencyLabel;
        private Rectangle whitelistLabel;
        private Rectangle passwordLabel;
        private Rectangle cacheLabel;
        private string cacheText;

        public SettingsDialog(FileCatalog catalog, WebAuth auth, UploadManager uploads)
            : base("设置")
        {
            this.catalog = catalog;
            this.auth = auth;
            this.uploads = uploads;

            AppConfig snapshot = ConfigStore.Instance.Read(delegate(AppConfig c)
            {
                AppConfig copy = new AppConfig();
                copy.MaxConcurrent = c.MaxConcurrent;
                copy.Whitelist = new List<string>(c.Whitelist);
                return copy;
            });

            concurrencyBox = new FluentTextBox();
            concurrencyBox.Text = snapshot.MaxConcurrent.ToString();
            Controls.Add(concurrencyBox);

            whitelistBox = new FluentTextBox();
            whitelistBox.Multiline = true;
            List<string> list = snapshot.Whitelist;
            whitelistBox.Text = string.Join(", ", list.ToArray());
            Controls.Add(whitelistBox);

            passwordBox = new FluentTextBox();
            passwordBox.Password = true;
            passwordBox.Hint = "无密码";
            Controls.Add(passwordBox);

            cacheText = "缓存 " + Draw.FormatSize(uploads.CacheBytes());
            clearCacheButton = new FluentButton();
            clearCacheButton.Text = "清理";
            clearCacheButton.Style = FluentButton.Variant.Secondary;
            clearCacheButton.Click += delegate { ClearCache(); };
            Controls.Add(clearCacheButton);

            defaultsButton = new FluentButton();
            defaultsButton.Text = "恢复默认";
            defaultsButton.Style = FluentButton.Variant.Secondary;
            defaultsButton.Click += delegate { RestoreDefaults(); };
            Controls.Add(defaultsButton);

            cancelButton = new FluentButton();
            cancelButton.Text = "取消";
            cancelButton.Style = FluentButton.Variant.Secondary;
            cancelButton.Click += delegate { Close(); };
            Controls.Add(cancelButton);

            saveButton = new FluentButton();
            saveButton.Text = "保存";
            saveButton.Style = FluentButton.Variant.Primary;
            saveButton.Click += delegate { Save(); };
            Controls.Add(saveButton);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            int pad = Draw.Scale(this, 20);
            int width = Draw.Scale(this, 480);
            int height = Draw.Scale(this, 404);
            ClientSize = new Size(width, height);

            int fieldWidth = width - pad * 2;
            int labelHeight = Draw.Scale(this, 18);
            int inputHeight = Draw.Scale(this, 32);

            concurrencyLabel = new Rectangle(pad, Draw.Scale(this, 16), fieldWidth, labelHeight);
            concurrencyBox.Bounds = new Rectangle(pad, Draw.Scale(this, 38), Draw.Scale(this, 110), inputHeight);

            whitelistLabel = new Rectangle(pad, Draw.Scale(this, 82), fieldWidth, Draw.Scale(this, 22));
            whitelistBox.Bounds = new Rectangle(pad, Draw.Scale(this, 108), fieldWidth, Draw.Scale(this, 120));

            passwordLabel = new Rectangle(pad, Draw.Scale(this, 240), fieldWidth, labelHeight);
            passwordBox.Bounds = new Rectangle(pad, Draw.Scale(this, 262), Draw.Scale(this, 280), inputHeight);

            int cacheHeight = Draw.Scale(this, 24);
            int cacheButtonWidth = Math.Max(Draw.Scale(this, 64),
                TextRenderer.MeasureText(clearCacheButton.Text, clearCacheButton.Font).Width + Draw.Scale(this, 24));
            clearCacheButton.Bounds = new Rectangle(width - pad - cacheButtonWidth, Draw.Scale(this, 306),
                cacheButtonWidth, cacheHeight);
            cacheLabel = new Rectangle(pad, Draw.Scale(this, 306),
                fieldWidth - cacheButtonWidth - Draw.Scale(this, 8), cacheHeight);

            int buttonHeight = Draw.Scale(this, 32);
            int buttonY = height - pad - buttonHeight;
            int saveWidth = Math.Max(Draw.Scale(this, 76),
                TextRenderer.MeasureText(saveButton.Text, saveButton.Font).Width + Draw.Scale(this, 32));
            int cancelWidth = Math.Max(Draw.Scale(this, 76),
                TextRenderer.MeasureText(cancelButton.Text, cancelButton.Font).Width + Draw.Scale(this, 32));
            saveButton.Bounds = new Rectangle(width - pad - saveWidth, buttonY, saveWidth, buttonHeight);
            cancelButton.Bounds = new Rectangle(width - pad - saveWidth - Draw.Scale(this, 6) - cancelWidth,
                buttonY, cancelWidth, buttonHeight);

            int defaultsWidth = Math.Max(Draw.Scale(this, 72),
                TextRenderer.MeasureText(defaultsButton.Text, defaultsButton.Font).Width + Draw.Scale(this, 24));
            defaultsButton.Bounds = new Rectangle(width - pad - defaultsWidth, Draw.Scale(this, 81),
                defaultsWidth, Draw.Scale(this, 24));

            Native.CenterOnScreen(this);
        }

        private void RestoreDefaults()
        {
            whitelistBox.Text = string.Join(", ", AppConfig.DefaultWhitelist().ToArray());
        }

        private void ClearCache()
        {
            long cleared;
            if (!uploads.ClearCache(out cleared))
            {
                MessageDialog.Show(this, "清理缓存", "仍有上传任务");
                return;
            }
            cacheText = "缓存 " + Draw.FormatSize(uploads.CacheBytes());
            Invalidate();
            MessageDialog.Show(this, "清理缓存", "已清理");
        }

        private static List<string> Split(string text)
        {
            if (text == null) return new List<string>();
            string normalized = text.Replace('，', ',').Replace('；', ',').Replace(';', ',')
                .Replace('\r', ',').Replace('\n', ',').Replace('\t', ',');
            return new List<string>(normalized.Split(','));
        }

        private void Save()
        {
            int concurrency;
            if (!int.TryParse(concurrencyBox.Text.Trim(), out concurrency) ||
                concurrency < 1 || concurrency > 50)
            {
                MessageDialog.Show(this, "设置", "同时上传数量无效");
                concurrencyBox.FocusInput();
                return;
            }

            List<string> list = ConfigStore.NormalizeWhitelist(Split(whitelistBox.Text));
            if (list.Count == 0)
            {
                MessageDialog.Show(this, "设置", "文件类型不能为空");
                whitelistBox.FocusInput();
                return;
            }

            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                c.MaxConcurrent = concurrency;
                c.Whitelist = list;
            });
            catalog.SetWhitelist(list);

            auth.SetPassword(passwordBox.Text);

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, "同时上传", Theme.FontBody, concurrencyLabel,
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, "可上传文件类型白名单", Theme.FontBody, whitelistLabel,
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, "访问密码", Theme.FontBody, passwordLabel,
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, cacheText, Theme.FontBody, cacheLabel,
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix);
        }
    }
}

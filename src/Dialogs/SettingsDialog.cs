using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class SettingsDialog : FlatDialog
    {
        private sealed class TextLine
        {
            public Rectangle Bounds;
            public string Text;
            public Font Font;
            public Color Color;
            public TextFormatFlags Flags;
        }

        private readonly FileCatalog catalog;
        private readonly WebAuth auth;
        private readonly UploadManager uploads;

        private readonly FluentTextBox concurrencyBox;
        private readonly FluentTextBox whitelistBox;
        private readonly FluentTextBox passwordBox;
        private readonly FluentToggle hiddenToggle;
        private readonly FluentToggle screenToggle;
        private readonly FluentButton defaultsButton;
        private readonly FluentButton cancelButton;
        private readonly FluentButton saveButton;
        private readonly FluentButton clearCacheButton;
        private readonly LinkLabel sourceLink;
        private readonly LinkLabel releaseLink;

        private readonly List<TextLine> textLines = new List<TextLine>();
        private readonly List<Rectangle> cards = new List<Rectangle>();

        private Rectangle cacheLabel;
        private string cacheText;

        private const string SourceUrl =
            "https://github.com/mdhyy008/801-courseware-uploader";
        private const string ReleaseUrl =
            "https://github.com/mdhyy008/801-courseware-uploader/releases";

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
                copy.ShowHiddenFiles = c.ShowHiddenFiles;
                copy.ScreenControlEnabled = c.ScreenControlEnabled;
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

            hiddenToggle = new FluentToggle();
            hiddenToggle.Checked = snapshot.ShowHiddenFiles;
            Controls.Add(hiddenToggle);

            screenToggle = new FluentToggle();
            screenToggle.Checked = snapshot.ScreenControlEnabled;
            Controls.Add(screenToggle);

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

            sourceLink = CreateLink(SourceUrl, "github.com/mdhyy008/801-courseware-uploader");
            releaseLink = CreateLink(ReleaseUrl, "github.com/mdhyy008/801-courseware-uploader/releases");

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

        private LinkLabel CreateLink(string url, string text)
        {
            LinkLabel link = new LinkLabel();
            link.Text = text;
            link.AutoSize = false;
            link.Font = Theme.FontSmall;
            link.BackColor = Theme.Surface;
            link.LinkColor = Theme.Accent;
            link.ActiveLinkColor = Theme.AccentHover;
            link.VisitedLinkColor = Theme.Accent;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.TextAlign = ContentAlignment.MiddleLeft;
            link.Cursor = Cursors.Hand;
            link.LinkClicked += delegate { OpenUrl(url); };
            Controls.Add(link);
            return link;
        }

        private void OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = url;
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch
            {
                MessageDialog.Show(this, "打开链接", "无法打开浏览器");
            }
        }

        private void AddText(string text, Font font, Color color, int x, int y, int width, int height)
        {
            TextLine line = new TextLine();
            line.Text = text;
            line.Font = font;
            line.Color = color;
            line.Bounds = new Rectangle(x, y, width, height);
            line.Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix;
            textLines.Add(line);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            int width = Draw.Scale(this, 680);
            int pad = Draw.Scale(this, 20);
            int cardPad = Draw.Scale(this, 16);
            int gap = Draw.Scale(this, 16);
            int labelH = Draw.Scale(this, 18);
            int descH = Draw.Scale(this, 17);
            int gapDesc = Draw.Scale(this, 6);
            int gapRow = Draw.Scale(this, 16);
            int inputH = Draw.Scale(this, 32);
            int titleH = Draw.Scale(this, 24);
            int titleGap = Draw.Scale(this, 6);
            int cardBottom = Draw.Scale(this, 14);

            int colWidth = (width - pad * 2 - gap) / 2;
            int innerWidth = colWidth - cardPad * 2;
            int leftX = pad + cardPad;
            int rightX = pad + colWidth + gap + cardPad;

            int toggleWidth = Draw.Scale(this, 44);
            int toggleHeight = Draw.Scale(this, 24);

            // —— 左栏：上传 ——
            int y = Draw.Scale(this, 14);
            AddText("上传", Theme.FontTitle, Theme.Text, pad, y, colWidth, titleH);
            y += titleH + titleGap;

            int cardTop = y;
            int cy = cardTop + cardPad;

            AddText("同时上传数量", Theme.FontBody, Theme.Text, leftX, cy, innerWidth, labelH);
            AddText("同时进行的上传任务数（1 - 50）", Theme.FontSmall, Theme.TextMuted,
                leftX, cy + labelH, innerWidth, descH);
            concurrencyBox.Bounds = new Rectangle(leftX, cy + labelH + descH + gapDesc,
                Draw.Scale(this, 110), inputH);
            cy += labelH + descH + gapDesc + inputH + gapRow;

            int defaultsWidth = Math.Max(Draw.Scale(this, 80),
                TextRenderer.MeasureText(defaultsButton.Text, defaultsButton.Font).Width +
                Draw.Scale(this, 24));
            AddText("可上传文件类型", Theme.FontBody, Theme.Text, leftX, cy,
                innerWidth - defaultsWidth - Draw.Scale(this, 8), labelH);
            AddText("逗号分隔扩展名，如 ppt、pptx、pdf", Theme.FontSmall, Theme.TextMuted,
                leftX, cy + labelH, innerWidth, descH);
            defaultsButton.Bounds = new Rectangle(leftX + innerWidth - defaultsWidth, cy,
                defaultsWidth, Draw.Scale(this, 24));
            whitelistBox.Bounds = new Rectangle(leftX, cy + labelH + descH + gapDesc,
                innerWidth, Draw.Scale(this, 92));
            cy += labelH + descH + gapDesc + Draw.Scale(this, 92) + gapRow;

            int cacheButtonWidth = Math.Max(Draw.Scale(this, 64),
                TextRenderer.MeasureText(clearCacheButton.Text, clearCacheButton.Font).Width +
                Draw.Scale(this, 24));
            int cacheButtonY = cy + labelH + Draw.Scale(this, 3);
            AddText("上传缓存", Theme.FontBody, Theme.Text, leftX, cy,
                innerWidth - cacheButtonWidth - Draw.Scale(this, 8), labelH);
            AddText("可随时清理已接收的文件", Theme.FontSmall, Theme.TextMuted, leftX,
                cy + labelH, innerWidth - cacheButtonWidth - Draw.Scale(this, 8), descH);
            cacheLabel = new Rectangle(leftX, cy, innerWidth - cacheButtonWidth - Draw.Scale(this, 8),
                labelH);
            clearCacheButton.Bounds = new Rectangle(leftX + innerWidth - cacheButtonWidth,
                cacheButtonY, cacheButtonWidth, Draw.Scale(this, 24));
            cy += labelH + Draw.Scale(this, 3) + Draw.Scale(this, 24);

            cards.Add(new Rectangle(pad, cardTop, colWidth, cy + cardBottom - cardTop));
            int leftBottom = cy + cardBottom;

            // —— 右栏：安全与远程 ——
            y = Draw.Scale(this, 14);
            AddText("安全与远程", Theme.FontTitle, Theme.Text, pad + colWidth + gap, y,
                colWidth, titleH);
            y += titleH + titleGap;

            cardTop = y;
            cy = cardTop + cardPad;

            AddText("访问密码", Theme.FontBody, Theme.Text, rightX, cy, innerWidth, labelH);
            AddText("打开网页时需输入的密码，留空即不设", Theme.FontSmall, Theme.TextMuted,
                rightX, cy + labelH, innerWidth, descH);
            passwordBox.Bounds = new Rectangle(rightX, cy + labelH + descH + gapDesc,
                innerWidth, inputH);
            cy += labelH + descH + gapDesc + inputH + gapRow;

            AddText("显示隐藏文件", Theme.FontBody, Theme.Text, rightX, cy,
                innerWidth - toggleWidth - Draw.Scale(this, 8), labelH);
            AddText("在网页文件列表中显示以点开头", Theme.FontSmall, Theme.TextMuted,
                rightX, cy + labelH, innerWidth - toggleWidth - Draw.Scale(this, 8), descH);
            hiddenToggle.Bounds = new Rectangle(rightX + innerWidth - toggleWidth,
                cy + (labelH + descH - toggleHeight) / 2, toggleWidth, toggleHeight);
            cy += labelH + descH + gapRow;

            AddText("屏幕控制", Theme.FontBody, Theme.Text, rightX, cy,
                innerWidth - toggleWidth - Draw.Scale(this, 8), labelH);
            AddText("允许网页远程查看并操作这台电脑", Theme.FontSmall, Theme.TextMuted,
                rightX, cy + labelH, innerWidth - toggleWidth - Draw.Scale(this, 8), descH);
            screenToggle.Bounds = new Rectangle(rightX + innerWidth - toggleWidth,
                cy + (labelH + descH - toggleHeight) / 2, toggleWidth, toggleHeight);
            cy += labelH + descH;

            cards.Add(new Rectangle(pad + colWidth + gap, cardTop, colWidth,
                cy + cardBottom - cardTop));
            int rightBottom = cy + cardBottom;

            // —— 关于（整行） ——
            int fullWidth = width - pad * 2;
            y = Math.Max(leftBottom, rightBottom) + gap;
            AddText("关于", Theme.FontTitle, Theme.Text, pad, y, fullWidth, titleH);
            y += titleH + titleGap;

            cardTop = y;
            cy = cardTop + cardPad;
            int linkX = pad + cardPad;
            int linkInner = fullWidth - cardPad * 2;

            int linkHeight = Draw.Scale(this, 20);
            int linkLabelWidth = TextRenderer.MeasureText("发布地址", Theme.FontSmall).Width +
                Draw.Scale(this, 12);
            AddText("开源地址", Theme.FontSmall, Theme.TextMuted, linkX, cy,
                linkLabelWidth, linkHeight);
            sourceLink.Bounds = new Rectangle(linkX + linkLabelWidth, cy,
                linkInner - linkLabelWidth, linkHeight);

            int releaseY = cy + linkHeight + Draw.Scale(this, 6);
            AddText("发布地址", Theme.FontSmall, Theme.TextMuted, linkX, releaseY,
                linkLabelWidth, linkHeight);
            releaseLink.Bounds = new Rectangle(linkX + linkLabelWidth, releaseY,
                linkInner - linkLabelWidth, linkHeight);
            cy = releaseY + linkHeight;

            cards.Add(new Rectangle(pad, cardTop, fullWidth, cy + cardBottom - cardTop));
            int aboutBottom = cy + cardBottom;

            // —— 底部按钮 ——
            int buttonHeight = Draw.Scale(this, 32);
            int buttonY = aboutBottom + gap;
            int saveWidth = Math.Max(Draw.Scale(this, 76),
                TextRenderer.MeasureText(saveButton.Text, saveButton.Font).Width +
                Draw.Scale(this, 32));
            int cancelWidth = Math.Max(Draw.Scale(this, 76),
                TextRenderer.MeasureText(cancelButton.Text, cancelButton.Font).Width +
                Draw.Scale(this, 32));
            saveButton.Bounds = new Rectangle(width - pad - saveWidth, buttonY,
                saveWidth, buttonHeight);
            cancelButton.Bounds = new Rectangle(
                width - pad - saveWidth - Draw.Scale(this, 6) - cancelWidth,
                buttonY, cancelWidth, buttonHeight);

            int height = buttonY + buttonHeight + pad;
            ClientSize = new Size(width, height);
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

            bool showHidden = hiddenToggle.Checked;
            bool screenEnabled = screenToggle.Checked;
            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                c.MaxConcurrent = concurrency;
                c.Whitelist = list;
                c.ShowHiddenFiles = showHidden;
                c.ScreenControlEnabled = screenEnabled;
            });
            catalog.SetWhitelist(list);
            catalog.SetShowHiddenFiles(showHidden);

            auth.SetPassword(passwordBox.Text);

            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            int radius = Draw.Scale(this, 10);
            foreach (Rectangle card in cards)
            {
                Draw.FillRound(e.Graphics, Theme.Surface, card, radius);
                Draw.StrokeRound(e.Graphics, Theme.Border, card, radius, 1f);
            }

            foreach (TextLine line in textLines)
                TextRenderer.DrawText(e.Graphics, line.Text, line.Font, line.Bounds,
                    line.Color, line.Flags);

            if (cacheLabel.Width > 0)
                TextRenderer.DrawText(e.Graphics, cacheText, Theme.FontBody, cacheLabel,
                    Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class MainForm : Form
    {
        private readonly FileCatalog catalog;
        private readonly UploadManager uploads;
        private readonly WebAuth auth;
        private readonly WebServer server;

        private readonly FluentButton addButton;
        private readonly FluentButton settingsButton;
        private readonly FluentButton qrButton;
        private readonly CardListPanel listPanel;
        private readonly Dictionary<string, DirectoryCard> cards =
            new Dictionary<string, DirectoryCard>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer timer;
        private readonly NotifyIcon tray;
        private readonly Icon trayIcon;

        private Rectangle percentRect;
        private Rectangle addressRect;
        private string addressText = "";
        private bool serverOk;
        private bool addressHover;
        private long totalDone;
        private long totalSize;
        private bool closingConfirmed;
        private bool exiting;
        private bool centered;

        public MainForm()
        {
            Text = "801课件上传工具";
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.FontBody;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            catalog = new FileCatalog();
            uploads = new UploadManager(catalog);
            auth = new WebAuth();
            server = new WebServer(catalog, uploads, auth);
            StartServer();

            addButton = new FluentButton();
            addButton.Text = "添加目录";
            addButton.Style = FluentButton.Variant.Primary;
            addButton.Click += delegate { AddDirectory(); };
            Controls.Add(addButton);

            settingsButton = new FluentButton();
            settingsButton.Text = "设置";
            settingsButton.Style = FluentButton.Variant.Secondary;
            settingsButton.Click += delegate { OpenSettings(); };
            Controls.Add(settingsButton);

            qrButton = new FluentButton();
            qrButton.Icon = FluentButton.Glyph.Qr;
            qrButton.Style = FluentButton.Variant.Secondary;
            qrButton.Enabled = serverOk;
            qrButton.Click += delegate { ShowQrCode(); };
            Controls.Add(qrButton);

            listPanel = new CardListPanel();
            listPanel.Resize += delegate { LayoutCards(); };
            Controls.Add(listPanel);

            timer = new Timer();
            timer.Interval = 400;
            timer.Tick += OnTick;
            timer.Start();

            trayIcon = Native.LoadAppIcon();
            Icon = trayIcon;
            tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = Text;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowMainWindow(); };

            ContextMenu trayMenu = new ContextMenu();
            MenuItem openItem = new MenuItem("打开主界面");
            openItem.Click += delegate { ShowMainWindow(); };
            MenuItem exitItem = new MenuItem("退出程序");
            exitItem.Click += delegate { ExitApplication(); };
            trayMenu.MenuItems.Add(openItem);
            trayMenu.MenuItems.Add(new MenuItem("-"));
            trayMenu.MenuItems.Add(exitItem);
            tray.ContextMenu = trayMenu;
        }

        private void ShowMainWindow()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Native.BringToForeground(Handle);
        }

        private void ExitApplication()
        {
            exiting = true;
            Close();
        }

        private void StartServer()
        {
            int port = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.Port; });
            if (server.Start(port))
            {
                serverOk = true;
                addressText = server.LanMode
                    ? LanSetup.GetLanAddress(port)
                    : "http://localhost:" + port.ToString() + "/";
            }
            else
            {
                serverOk = false;
                addressText = "服务未启动 · " + server.LastError;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ClientSize = new Size(960, 640);
            MinimumSize = new Size(800, 540);
            Native.ApplyWindowChrome(Handle, Theme.IsDark);
            Native.ApplyDarkScrollbars(listPanel.Handle);
            RebuildCards();
            Native.CenterOnScreen(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (centered) return;
            centered = true;
            Native.CenterOnScreen(this);
        }

        private static int MeasureButton(FluentButton button, int minWidth)
        {
            int textWidth = TextRenderer.MeasureText(button.Text, button.Font).Width;
            int width = textWidth + Draw.Scale(button, 32);
            return width < minWidth ? minWidth : width;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);

            if (addButton == null || settingsButton == null || qrButton == null || listPanel == null) return;

            int padX = Draw.Scale(this, 20);
            int top = Draw.Scale(this, 12);
            int buttonHeight = Draw.Scale(this, 32);

            int settingsWidth = MeasureButton(settingsButton, Draw.Scale(this, 68));
            int addWidth = MeasureButton(addButton, Draw.Scale(this, 90));
            settingsButton.Bounds = new Rectangle(ClientSize.Width - padX - settingsWidth,
                top, settingsWidth, buttonHeight);
            addButton.Bounds = new Rectangle(settingsButton.Left - Draw.Scale(this, 6) - addWidth,
                top, addWidth, buttonHeight);

            int qrSize = Draw.Scale(this, 24);
            int qrGap = Draw.Scale(this, 6);
            int addressWidth = TextRenderer.MeasureText(addressText, Theme.FontSmall).Width +
                Draw.Scale(this, 4);
            int addressLimit = ClientSize.Width - padX * 2 - qrSize - qrGap;
            if (addressWidth > addressLimit) addressWidth = addressLimit;
            if (addressWidth < 0) addressWidth = 0;
            addressRect = new Rectangle(padX, Draw.Scale(this, 42),
                addressWidth, Draw.Scale(this, 22));
            qrButton.Bounds = new Rectangle(addressRect.Right + qrGap,
                Draw.Scale(this, 41), qrSize, qrSize);

            int titleWidth = TextRenderer.MeasureText(Text, Theme.FontPageTitle).Width;
            percentRect = new Rectangle(padX + titleWidth + Draw.Scale(this, 10),
                Draw.Scale(this, 10), Draw.Scale(this, 160), Draw.Scale(this, 30));

            int listTop = Draw.Scale(this, 106);
            int listBottom = ClientSize.Height - Draw.Scale(this, 20);
            listPanel.Bounds = new Rectangle(padX, listTop,
                Math.Max(0, ClientSize.Width - padX * 2),
                Math.Max(0, listBottom - listTop));
            LayoutCards();
        }

        private void LayoutCards()
        {
            if (listPanel == null) return;
            int width = listPanel.ClientSize.Width - Draw.Scale(this, 2);
            int minimum = Draw.Scale(this, 320);
            if (width < minimum) width = minimum;
            int height = Draw.Scale(this, 72);
            foreach (DirectoryCard card in cards.Values)
            {
                card.Width = width;
                card.Height = height;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = serverOk && addressRect.Contains(e.Location);
            if (over == addressHover) return;
            addressHover = over;
            Cursor = over ? Cursors.Hand : Cursors.Default;
            Invalidate(addressRect);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!addressHover) return;
            addressHover = false;
            Cursor = Cursors.Default;
            Invalidate(addressRect);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (serverOk && addressRect.Contains(e.Location)) OpenAddress();
        }

        private void OpenAddress()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = addressText;
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch
            {
            }
        }

        private void ShowQrCode()
        {
            if (!serverOk) return;
            QrDialog.Show(this, QrCode.Encode(addressText));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (SolidBrush background = new SolidBrush(Theme.Window))
                g.FillRectangle(background, ClientRectangle);

            int padX = Draw.Scale(this, 20);
            TextRenderer.DrawText(g, Text, Theme.FontPageTitle,
                new Rectangle(padX, Draw.Scale(this, 10), ClientSize.Width - padX * 2,
                    Draw.Scale(this, 30)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (totalSize > 0)
            {
                int percent = (int)(totalDone * 100L / totalSize);
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                TextRenderer.DrawText(g, percent.ToString() + "%", Theme.FontTitle, percentRect,
                    Theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix);
            }

            TextRenderer.DrawText(g, addressText, Theme.FontSmall, addressRect,
                serverOk ? (addressHover ? Theme.Accent : Theme.TextMuted) : Theme.Danger,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            using (Pen pen = new Pen(Theme.Divider, 1f))
                g.DrawLine(pen, padX, Draw.Scale(this, 72), ClientSize.Width - padX,
                    Draw.Scale(this, 72));

            TextRenderer.DrawText(g, "目录", Theme.FontTitle,
                new Rectangle(padX, Draw.Scale(this, 80), Draw.Scale(this, 200),
                    Draw.Scale(this, 22)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (cards.Count == 0)
            {
                TextRenderer.DrawText(g, "尚未添加目录", Theme.FontBody, listPanel.Bounds,
                    Theme.TextMuted, TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            List<FileCatalog.DirView> dirs = catalog.GetSnapshot();
            if (NeedsRebuild(dirs)) RebuildCards();

            List<UploadManager.DirProgress> progress = uploads.GetDirProgress();
            Dictionary<string, UploadManager.DirProgress> active =
                new Dictionary<string, UploadManager.DirProgress>(StringComparer.OrdinalIgnoreCase);
            totalDone = 0;
            totalSize = 0;
            for (int i = 0; i < progress.Count; i++)
            {
                UploadManager.DirProgress entry = progress[i];
                active[entry.DirId] = entry;
                totalDone += entry.DoneBytes;
                totalSize += entry.TotalBytes;
            }

            for (int i = 0; i < dirs.Count; i++)
            {
                FileCatalog.DirView view = dirs[i];
                DirectoryCard card;
                if (!cards.TryGetValue(view.Id, out card)) continue;
                UploadManager.DirProgress entry;
                if (!active.TryGetValue(view.Id, out entry))
                {
                    card.SetData(view.Id, view.Name, view.Path, view.Count, view.Indexing,
                        view.IndexDone, view.IndexTotal, view.Error,
                        view.IndexBytes, view.IndexTotalBytes, 0, 0, 0L, 0L);
                    continue;
                }
                card.SetData(view.Id, view.Name, view.Path, view.Count, view.Indexing,
                    view.IndexDone, view.IndexTotal, view.Error,
                    view.IndexBytes, view.IndexTotalBytes,
                    entry.Active, entry.Saving, entry.DoneBytes, entry.TotalBytes);
            }

            Invalidate(percentRect);
        }

        private bool NeedsRebuild(List<FileCatalog.DirView> dirs)
        {
            if (dirs.Count != cards.Count) return true;
            for (int i = 0; i < dirs.Count; i++)
            {
                if (!cards.ContainsKey(dirs[i].Id)) return true;
            }
            return false;
        }

        private void RebuildCards()
        {
            List<FileCatalog.DirView> dirs = catalog.GetSnapshot();
            HashSet<string> alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < dirs.Count; i++) alive.Add(dirs[i].Id);

            List<string> stale = new List<string>();
            foreach (string id in cards.Keys)
            {
                if (!alive.Contains(id)) stale.Add(id);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                DirectoryCard card = cards[stale[i]];
                cards.Remove(stale[i]);
                listPanel.Controls.Remove(card);
                card.Dispose();
            }

            listPanel.SuspendLayout();
            for (int i = 0; i < dirs.Count; i++)
            {
                FileCatalog.DirView view = dirs[i];
                DirectoryCard card;
                if (!cards.TryGetValue(view.Id, out card))
                {
                    card = new DirectoryCard();
                    card.OpenClicked += CardOpenClicked;
                    card.CopyPathClicked += CardCopyPathClicked;
                    card.ReindexClicked += CardReindexClicked;
                    card.RemoveClicked += CardRemoveClicked;
                    cards[view.Id] = card;
                    listPanel.Controls.Add(card);
                }
                card.Margin = new Padding(0, 0, 0, Draw.Scale(this, 8));
                listPanel.Controls.SetChildIndex(card, i);
            }
            listPanel.Visible = dirs.Count > 0;
            listPanel.ResumeLayout();
            LayoutCards();
            OnTick(null, EventArgs.Empty);
        }

        private void CardOpenClicked(object sender, EventArgs e)
        {
            DirectoryCard card = sender as DirectoryCard;
            if (card == null) return;
            if (!Directory.Exists(card.DirectoryPath))
            {
                MessageDialog.Show(this, "打开目录", "目录不存在");
                return;
            }
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = card.DirectoryPath;
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch
            {
            }
        }

        private void CardCopyPathClicked(object sender, EventArgs e)
        {
            DirectoryCard card = sender as DirectoryCard;
            if (card == null) return;
            try
            {
                Clipboard.SetText(card.DirectoryPath);
            }
            catch
            {
            }
        }

        private void CardReindexClicked(object sender, EventArgs e)
        {
            DirectoryCard card = sender as DirectoryCard;
            if (card == null) return;
            catalog.Reindex(card.DirectoryId);
        }

        private void CardRemoveClicked(object sender, EventArgs e)
        {
            DirectoryCard card = sender as DirectoryCard;
            if (card == null) return;
            if (!MessageDialog.Confirm(this, "移除目录", card.DirectoryPath, "移除", true)) return;
            catalog.RemoveDirectory(card.DirectoryId, false);
            RebuildCards();
        }

        private void AddDirectory()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "添加目录";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string error;
                string id;
                if (!catalog.AddDirectory(dialog.SelectedPath, out error, out id))
                {
                    MessageDialog.Show(this, "添加目录", error);
                    return;
                }
                RebuildCards();
            }
        }

        private void OpenSettings()
        {
            using (SettingsDialog dialog = new SettingsDialog(catalog, auth, uploads))
                dialog.ShowDialog(this);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            if (!closingConfirmed && uploads.HasActive)
            {
                if (!MessageDialog.Confirm(this, "退出", "仍有上传任务", "退出", true))
                {
                    e.Cancel = true;
                    exiting = false;
                    return;
                }
                closingConfirmed = true;
            }
            timer.Stop();
            server.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (trayIcon != null && !ReferenceEquals(trayIcon, SystemIcons.Application))
                trayIcon.Dispose();
            base.OnFormClosing(e);
        }
    }
}

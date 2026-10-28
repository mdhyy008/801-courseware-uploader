using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class DirectoryCard : Control
    {
        private string directoryId = "";
        private string directoryName = "";
        private string directoryPath = "";
        private int fileCount;
        private bool indexing;
        private int indexDone;
        private int indexTotal;
        private long indexDoneBytes;
        private long indexTotalBytes;
        private string indexError;
        private int activeCount;
        private int savingCount;
        private long activeDone;
        private long activeTotal;
        private bool removeHover;
        private bool removePressed;

        public event EventHandler RemoveClicked;
        public event EventHandler OpenClicked;
        public event EventHandler CopyPathClicked;
        public event EventHandler ReindexClicked;

        public DirectoryCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
            Font = Theme.FontBody;
            BuildMenu();
        }

        private void BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem open = new MenuItem("打开目录");
            open.Click += delegate { if (OpenClicked != null) OpenClicked(this, EventArgs.Empty); };
            MenuItem copy = new MenuItem("复制路径");
            copy.Click += delegate { if (CopyPathClicked != null) CopyPathClicked(this, EventArgs.Empty); };
            MenuItem reindex = new MenuItem("重新索引");
            reindex.Click += delegate { if (ReindexClicked != null) ReindexClicked(this, EventArgs.Empty); };
            MenuItem remove = new MenuItem("移除目录");
            remove.Click += delegate { if (RemoveClicked != null) RemoveClicked(this, EventArgs.Empty); };
            menu.MenuItems.Add(open);
            menu.MenuItems.Add(copy);
            menu.MenuItems.Add(reindex);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(remove);
            ContextMenu = menu;
        }

        public string DirectoryId
        {
            get { return directoryId; }
        }

        public string DirectoryPath
        {
            get { return directoryPath; }
        }

        public string DirectoryName
        {
            get { return directoryName; }
        }

        public void SetData(string id, string name, string path, int count,
            bool isIndexing, int done, int total, string error,
            long doneBytes, long totalBytes,
            int active, int saving, long activeReceived, long activeSize)
        {
            bool changed = id != directoryId || name != directoryName || path != directoryPath ||
                count != fileCount || isIndexing != indexing || done != indexDone ||
                total != indexTotal || error != indexError || doneBytes != indexDoneBytes ||
                totalBytes != indexTotalBytes || active != activeCount ||
                saving != savingCount || activeReceived != activeDone ||
                activeSize != activeTotal;
            if (!changed) return;

            directoryId = id == null ? "" : id;
            directoryName = name == null ? "" : name;
            directoryPath = path == null ? "" : path;
            fileCount = count;
            indexing = isIndexing;
            indexDone = done;
            indexTotal = total;
            indexError = error;
            indexDoneBytes = doneBytes;
            indexTotalBytes = totalBytes;
            activeCount = active;
            savingCount = saving;
            activeDone = activeReceived;
            activeTotal = activeSize;
            Invalidate();
        }

        private Rectangle RemoveRect()
        {
            int pad = Draw.Scale(this, 14);
            int size = Draw.Scale(this, 22);
            return new Rectangle(Width - pad - size, pad - Draw.Scale(this, 2), size, size);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = RemoveRect().Contains(e.Location);
            if (over != removeHover)
            {
                removeHover = over;
                Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (removeHover || removePressed)
            {
                removeHover = false;
                removePressed = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && RemoveRect().Contains(e.Location))
            {
                removePressed = true;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool fire = removePressed && RemoveRect().Contains(e.Location);
            removePressed = false;
            Invalidate();
            if (fire && RemoveClicked != null) RemoveClicked(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = ClientRectangle;
            if (r.Width <= 4 || r.Height <= 4) return;

            int pad = Draw.Scale(this, 14);
            int radius = Draw.Scale(this, 9);
            Draw.FillRound(g, Theme.Surface, r, radius);
            Draw.StrokeRound(g, Theme.Border, r, radius, 1f);

            int iconSize = Draw.Scale(this, 21);
            Rectangle iconRect = new Rectangle(pad, pad, iconSize, iconSize);
            Icon icon = FolderIcon;
            if (icon != null) g.DrawIcon(icon, iconRect);

            Rectangle removeRect = RemoveRect();
            if (removeHover)
            {
                Color hover = removePressed ? Theme.Pressed : Theme.Hover;
                Draw.FillRound(g, hover, removeRect, removeRect.Width / 2);
            }
            Rectangle crossBox = Rectangle.Inflate(removeRect, -Draw.Scale(this, 6), -Draw.Scale(this, 6));
            Draw.Cross(g, crossBox, removeHover ? Theme.Text : Theme.TextMuted, 1.4f);

            int textLeft = iconRect.Right + Draw.Scale(this, 10);
            int textRight = removeRect.Left - Draw.Scale(this, 6);

            string rightText = fileCount.ToString() + " 个文件";
            Rectangle countRect = new Rectangle(textLeft, pad,
                Math.Max(0, textRight - textLeft), Draw.Scale(this, 20));
            TextRenderer.DrawText(g, rightText, Theme.FontSmall, countRect, Theme.TextMuted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string statusText = "";
            Color statusColor = Theme.Accent;
            if (indexing)
            {
                if (indexTotalBytes > 0 || indexTotal > 0)
                {
                    long indexPercent = indexTotalBytes > 0
                        ? indexDoneBytes * 100L / indexTotalBytes
                        : (long)indexDone * 100L / indexTotal;
                    if (indexPercent < 0) indexPercent = 0;
                    if (indexPercent > 100) indexPercent = 100;
                    statusText = "正在索引 " + indexPercent.ToString() + "%";
                }
                else
                {
                    statusText = "正在索引";
                }
            }
            else if (!string.IsNullOrEmpty(indexError))
            {
                statusText = "索引失败";
                statusColor = Theme.Danger;
            }
            else if (activeTotal > 0)
            {
                int percent = (int)(activeDone * 100L / activeTotal);
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                if (percent >= 100 && savingCount > 0)
                    statusText = "正在保存 " + savingCount.ToString() + " 个文件";
                else
                    statusText = "上传 " + activeCount.ToString() + " 个文件 · " +
                        percent.ToString() + "%";
            }

            int countWidth = TextRenderer.MeasureText(rightText, Theme.FontSmall).Width;
            int nameMax = Math.Max(0, textRight - textLeft - countWidth - Draw.Scale(this, 10));
            int gap = Draw.Scale(this, 8);
            int statusWidth = statusText.Length > 0
                ? TextRenderer.MeasureText(statusText, Theme.FontSmall).Width
                : 0;
            int nameLimit = nameMax;
            if (statusWidth > 0) nameLimit = Math.Max(0, nameMax - statusWidth - gap);

            int nameFull = directoryName.Length > 0
                ? TextRenderer.MeasureText(directoryName, Theme.FontTitle).Width
                : 0;
            int nameDraw = Math.Min(nameFull, nameLimit);
            Rectangle nameRect = new Rectangle(textLeft, pad, nameDraw, Draw.Scale(this, 20));
            TextRenderer.DrawText(g, directoryName, Theme.FontTitle, nameRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (statusWidth > 0)
            {
                int statusLeft = textLeft + nameDraw + gap;
                Rectangle statusRect = new Rectangle(statusLeft, pad,
                    Math.Max(0, textRight - statusLeft), Draw.Scale(this, 20));
                TextRenderer.DrawText(g, statusText, Theme.FontSmall, statusRect, statusColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            Rectangle pathRect = new Rectangle(textLeft, pad + Draw.Scale(this, 22),
                Math.Max(0, Width - pad - textLeft), Draw.Scale(this, 18));
            TextRenderer.DrawText(g, directoryPath, Theme.FontSmall, pathRect, Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
        }

        private static Icon folderIcon;

        private static Icon FolderIcon
        {
            get
            {
                if (folderIcon == null) folderIcon = Native.GetFolderIcon();
                return folderIcon;
            }
        }
    }
}

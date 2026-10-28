using System;
using System.Drawing;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class QrDialog : FlatDialog
    {
        private readonly bool[,] matrix;

        private QrDialog(bool[,] matrix)
            : base("二维码")
        {
            this.matrix = matrix;
        }

        public static void Show(IWin32Window owner, bool[,] matrix)
        {
            if (matrix == null) return;
            using (QrDialog dialog = new QrDialog(matrix))
                dialog.ShowDialog(owner);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            int side = Draw.Scale(this, 240);
            int pad = Draw.Scale(this, 16);
            ClientSize = new Size(side + pad * 2, side + pad * 2);
            Native.CenterOnScreen(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

            int pad = Draw.Scale(this, 16);
            int side = ClientSize.Width - pad * 2;
            int quiet = 4;
            int modules = matrix.GetLength(0) + quiet * 2;
            int cell = Math.Max(1, side / modules);
            int actual = cell * modules;
            int left = (ClientSize.Width - actual) / 2;
            int top = (ClientSize.Height - actual) / 2;

            using (SolidBrush white = new SolidBrush(Color.White))
            using (SolidBrush black = new SolidBrush(Color.Black))
            {
                g.FillRectangle(white, new Rectangle(left, top, actual, actual));
                for (int y = 0; y < matrix.GetLength(0); y++)
                {
                    for (int x = 0; x < matrix.GetLength(1); x++)
                    {
                        if (!matrix[y, x]) continue;
                        g.FillRectangle(black, new Rectangle(left + (x + quiet) * cell,
                            top + (y + quiet) * cell, cell, cell));
                    }
                }
            }
        }
    }
}

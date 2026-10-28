using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal sealed class CardListPanel : FlowLayoutPanel
    {
        public CardListPanel()
        {
            DoubleBuffered = true;
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoScroll = true;
            BackColor = Theme.Window;
            Padding = new Padding(0);
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal static class ScreenControl
    {
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr destDc, int xDest, int yDest, int widthDest,
            int heightDest, IntPtr srcDc, int xSrc, int ySrc, int widthSrc, int heightSrc,
            uint rop);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint SRCCOPY = 0x00CC0020;
        private const uint CAPTUREBLT = 0x40000000;
        private const int COLORONCOLOR = 3;

        private const int MaxWidth = 1920;
        private const int CacheMs = 10;

        private static readonly object gate = new object();
        private static byte[] cached;
        private static int cachedAt;
        private static int cachedIndex = -1;
        private static Bitmap frameBitmap;
        private static Graphics frameGraphics;
        private static readonly MemoryStream frameStream = new MemoryStream(256 * 1024);

        public static Screen[] Screens
        {
            get { return Screen.AllScreens; }
        }

        public static int PrimaryIndex()
        {
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                if (screens[i].Primary) return i;
            }
            return 0;
        }

        public static int NormalizeIndex(int index)
        {
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) return -1;
            if (index < 0 || index >= screens.Length) return PrimaryIndex();
            return index;
        }

        public static Rectangle BoundsOf(int index)
        {
            index = NormalizeIndex(index);
            return index < 0 ? Rectangle.Empty : Screen.AllScreens[index].Bounds;
        }

        public static byte[] Frame(int index)
        {
            index = NormalizeIndex(index);
            if (index < 0) return null;
            lock (gate)
            {
                int now = Environment.TickCount;
                if (cached != null && cachedIndex == index &&
                    unchecked(now - cachedAt) < CacheMs)
                {
                    return cached;
                }
                byte[] jpeg = Capture(index);
                if (jpeg != null)
                {
                    cached = jpeg;
                    cachedAt = now;
                    cachedIndex = index;
                }
                return jpeg;
            }
        }

        public static Size FrameSizeOf(int index)
        {
            return FrameSizeOf(index, MaxWidth);
        }

        public static Size FrameSizeOf(int index, int maxWidth)
        {
            Rectangle bounds = BoundsOf(index);
            if (bounds.Width <= 0 || bounds.Height <= 0) return Size.Empty;
            int width = bounds.Width;
            int height = bounds.Height;
            if (width > maxWidth)
            {
                height = Math.Max(1, (int)Math.Round(height * (double)maxWidth / width));
                width = maxWidth;
            }
            return new Size(width, height);
        }

        public static bool CaptureInto(int index, byte[] dest, out int width, out int height)
        {
            return CaptureInto(index, dest, MaxWidth, out width, out height);
        }

        public static bool CaptureInto(int index, byte[] dest, int maxWidth, out int width, out int height)
        {
            width = 0;
            height = 0;
            index = NormalizeIndex(index);
            if (index < 0 || dest == null) return false;
            lock (gate)
            {
                Rectangle bounds = BoundsOf(index);
                if (bounds.Width <= 0 || bounds.Height <= 0) return false;
                Size size = FrameSizeOf(index, maxWidth);
                int w = size.Width;
                int h = size.Height;
                if (dest.Length < w * h * 4) return false;
                try
                {
                    EnsureFrame(w, h);
                    Blit(bounds, w, h);
                    BitmapData data = frameBitmap.LockBits(new Rectangle(0, 0, w, h),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        int rowBytes = w * 4;
                        if (data.Stride == rowBytes)
                        {
                            Marshal.Copy(data.Scan0, dest, 0, rowBytes * h);
                        }
                        else
                        {
                            for (int y = 0; y < h; y++)
                            {
                                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), dest, y * rowBytes, rowBytes);
                            }
                        }
                    }
                    finally
                    {
                        frameBitmap.UnlockBits(data);
                    }
                    width = w;
                    height = h;
                    return true;
                }
                catch
                {
                    width = 0;
                    height = 0;
                    return false;
                }
            }
        }

        private static byte[] Capture(int index)
        {
            Rectangle bounds = BoundsOf(index);
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            int width = bounds.Width;
            int height = bounds.Height;
            if (width > MaxWidth)
            {
                height = Math.Max(1, (int)Math.Round(height * (double)MaxWidth / width));
                width = MaxWidth;
            }
            try
            {
                EnsureFrame(width, height);
                Blit(bounds, width, height);
                frameStream.SetLength(0);
                frameStream.Position = 0;
                SaveJpeg(frameBitmap, frameStream);
                return frameStream.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static void Blit(Rectangle bounds, int width, int height)
        {
            IntPtr destDc = frameGraphics.GetHdc();
            IntPtr screenDc = GetDC(IntPtr.Zero);
            try
            {
                SetStretchBltMode(destDc, COLORONCOLOR);
                StretchBlt(destDc, 0, 0, width, height,
                    screenDc, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    SRCCOPY | CAPTUREBLT);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                frameGraphics.ReleaseHdc(destDc);
            }
        }

        private static void EnsureFrame(int width, int height)
        {
            if (frameBitmap != null && frameBitmap.Width == width && frameBitmap.Height == height) return;
            if (frameGraphics != null) frameGraphics.Dispose();
            if (frameBitmap != null) frameBitmap.Dispose();
            frameBitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            frameGraphics = Graphics.FromImage(frameBitmap);
        }

        private static ImageCodecInfo jpegCodec;

        private static ImageCodecInfo JpegCodec()
        {
            if (jpegCodec != null) return jpegCodec;
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].FormatID == ImageFormat.Jpeg.Guid)
                {
                    jpegCodec = codecs[i];
                    break;
                }
            }
            return jpegCodec;
        }

        private static void SaveJpeg(Image image, Stream stream)
        {
            ImageCodecInfo codec = JpegCodec();
            if (codec == null)
            {
                image.Save(stream, ImageFormat.Jpeg);
                return;
            }
            using (EncoderParameters parameters = new EncoderParameters(1))
            using (EncoderParameter quality = new EncoderParameter(Encoder.Quality, 80L))
            {
                parameters.Param[0] = quality;
                image.Save(stream, codec, parameters);
            }
        }

        public static void Click(int x, int y)
        {
            Point point = ClampPoint(x, y);
            SetCursorPos(point.X, point.Y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        public static void RightClick(int x, int y)
        {
            Point point = ClampPoint(x, y);
            SetCursorPos(point.X, point.Y);
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }

        public static void PointerDown(int x, int y)
        {
            Point point = ClampPoint(x, y);
            SetCursorPos(point.X, point.Y);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        }

        public static void PointerMove(int x, int y)
        {
            Point point = ClampPoint(x, y);
            SetCursorPos(point.X, point.Y);
        }

        public static void PointerUp(int x, int y)
        {
            Point point = ClampPoint(x, y);
            SetCursorPos(point.X, point.Y);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        public static void KeyDown(int vk)
        {
            if (vk <= 0 || vk > 255) return;
            keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        }

        public static void KeyUp(int vk)
        {
            if (vk <= 0 || vk > 255) return;
            keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static Point ClampPoint(int x, int y)
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            return new Point(
                Math.Max(bounds.Left, Math.Min(bounds.Right - 1, x)),
                Math.Max(bounds.Top, Math.Min(bounds.Bottom - 1, y)));
        }
    }
}

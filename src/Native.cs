using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal static class Native
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        private const uint SHGFI_ICON = 0x00000100;
        private const uint SHGFI_LARGEICON = 0x00000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes,
            ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        public static Icon GetFolderIcon()
        {
            SHFILEINFO info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref info,
                (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                using (Icon handle = Icon.FromHandle(info.hIcon))
                    return (Icon)handle.Clone();
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        public static Icon LoadAppIcon()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("app.ico"))
                {
                    if (stream != null) return new Icon(stream);
                }
            }
            catch
            {
            }
            try
            {
                Icon extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (extracted != null) return extracted;
            }
            catch
            {
            }
            return SystemIcons.Application;
        }

        public static void CenterOnScreen(Form form)
        {
            Screen screen = Screen.PrimaryScreen;
            if (screen == null) return;
            Rectangle area = screen.WorkingArea;
            form.Location = new Point(area.X + (area.Width - form.Width) / 2,
                area.Y + (area.Height - form.Height) / 2);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string subApp, string subIdList);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,
            int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool enable);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private const uint GW_CHILD = 5;
        private const uint GW_HWNDNEXT = 2;

        public static void ApplyWindowChrome(IntPtr handle, bool dark)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                int preference = 2;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                int flag = dark ? 1 : 0;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref flag, sizeof(int));
                int border = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch
            {
            }
        }

        public static void ApplyDarkScrollbars(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                string mode = Theme.IsDark ? "DarkMode_Explorer" : "Explorer";
                SetWindowTheme(handle, mode, null);
                IntPtr child = GetWindow(handle, GW_CHILD);
                while (child != IntPtr.Zero)
                {
                    SetWindowTheme(child, mode, null);
                    child = GetWindow(child, GW_HWNDNEXT);
                }
            }
            catch
            {
            }
        }

        public static void BringToForeground(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                ShowWindow(handle, 9);
                IntPtr topmost = new IntPtr(-1);
                IntPtr notTopmost = new IntPtr(-2);
                const uint flags = 0x0001 | 0x0002 | 0x0040;
                SetWindowPos(handle, topmost, 0, 0, 0, 0, flags);
                SetWindowPos(handle, notTopmost, 0, 0, 0, 0, flags & ~0x0040u);

                IntPtr foreground = GetForegroundWindow();
                uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
                uint currentThread = GetCurrentThreadId();
                bool attached = false;
                if (foregroundThread != 0 && foregroundThread != currentThread)
                    attached = AttachThreadInput(currentThread, foregroundThread, true);
                try
                {
                    BringWindowToTop(handle);
                    SetForegroundWindow(handle);
                }
                finally
                {
                    if (attached) AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
            catch
            {
            }
        }

        public static IntPtr FindVisibleWindow(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hwnd)) return true;
                StringBuilder sb = new StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (title.Length == 0) return true;
                if (title.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool ActivateExistingWindow(string title)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (!IsWindowVisible(hwnd)) return true;
                StringBuilder sb = new StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                if (sb.ToString() == title)
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero) return false;
            ShowWindow(found, 9);
            SetForegroundWindow(found);
            return true;
        }
    }
}

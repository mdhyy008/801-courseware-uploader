using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace _801CoursewareUploader
{
    internal static class Theme
    {
        public static bool IsDark;
        public static Color Window;
        public static Color Surface;
        public static Color SurfaceHover;
        public static Color Text;
        public static Color TextMuted;
        public static Color Border;
        public static Color Divider;
        public static Color Accent;
        public static Color AccentText;
        public static Color AccentHover;
        public static Color Hover;
        public static Color Pressed;
        public static Color ImageBack;
        public static Color Danger;

        public static Font FontBody;
        public static Font FontSmall;
        public static Font FontTitle;
        public static Font FontPageTitle;

        static Theme()
        {
            FontBody = new Font("Microsoft YaHei UI", 9F);
            FontSmall = new Font("Microsoft YaHei UI", 8.5F);
            FontTitle = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            FontPageTitle = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold);
            Reload();
        }

        public static void Reload()
        {
            bool dark = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int && (int)v == 0) dark = true;
                    }
                }
            }
            catch
            {
            }

            IsDark = dark;

            if (dark)
            {
                Window = Color.FromArgb(32, 32, 32);
                Surface = Color.FromArgb(44, 44, 44);
                SurfaceHover = Color.FromArgb(54, 54, 54);
                Text = Color.FromArgb(255, 255, 255);
                TextMuted = Color.FromArgb(158, 158, 158);
                Border = Color.FromArgb(62, 62, 62);
                Divider = Color.FromArgb(48, 48, 48);
                Accent = Color.FromArgb(76, 194, 255);
                AccentText = Color.FromArgb(12, 20, 26);
                AccentHover = Color.FromArgb(120, 208, 255);
                Hover = Color.FromArgb(20, 255, 255, 255);
                Pressed = Color.FromArgb(32, 255, 255, 255);
                ImageBack = Color.FromArgb(24, 24, 24);
                Danger = Color.FromArgb(255, 110, 110);
            }
            else
            {
                Window = Color.FromArgb(243, 243, 243);
                Surface = Color.FromArgb(255, 255, 255);
                SurfaceHover = Color.FromArgb(250, 250, 250);
                Text = Color.FromArgb(26, 26, 26);
                TextMuted = Color.FromArgb(96, 96, 96);
                Border = Color.FromArgb(226, 226, 226);
                Divider = Color.FromArgb(234, 234, 234);
                Accent = Color.FromArgb(0, 103, 192);
                AccentText = Color.White;
                AccentHover = Color.FromArgb(16, 110, 190);
                Hover = Color.FromArgb(10, 0, 0, 0);
                Pressed = Color.FromArgb(22, 0, 0, 0);
                ImageBack = Color.FromArgb(238, 238, 238);
                Danger = Color.FromArgb(196, 43, 28);
            }
        }
    }
}

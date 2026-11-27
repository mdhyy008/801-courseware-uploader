using System;
using System.Threading;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length >= 2 &&
                string.Equals(args[0], "--register", StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(LanSetup.RunRegistration(args[1]));
                return;
            }

            bool restarted = args != null && Array.IndexOf(args, "--restarted") >= 0;
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "801CoursewareUploader.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    if (!restarted)
                    {
                        Native.ActivateExistingWindow("801课件上传工具");
                        return;
                    }
                    try
                    {
                        if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                        {
                            MessageBox.Show("未能接管正在运行的程序，请手动退出旧实例后重试。",
                                "管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                    }
                }

                AppPaths.Ensure();
                Theme.Reload();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                EnsureLanAccess();

                Application.Run(new MainForm(restarted));
            }
        }

        private static void EnsureLanAccess()
        {
            int port = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.Port; });
            bool rtcReady = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.RtcFirewall; });
            if (!rtcReady)
            {
                string rtcError;
                LanSetup.EnsureRegistered(port, true, out rtcError);
                ConfigStore.Instance.Mutate(delegate(AppConfig c) { c.RtcFirewall = true; });
                return;
            }
            if (LanSetup.IsPortRegistered(port)) return;
            bool prompted = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.LanPrompted; });
            if (prompted) return;

            ConfigStore.Instance.Mutate(delegate(AppConfig c) { c.LanPrompted = true; });
            string error;
            LanSetup.EnsureRegistered(port, false, out error);
        }
    }
}

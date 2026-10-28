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

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "801CoursewareUploader.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    Native.ActivateExistingWindow("801课件上传工具");
                    return;
                }

                AppPaths.Ensure();
                Theme.Reload();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                EnsureLanAccess();

                Application.Run(new MainForm());
            }
        }

        private static void EnsureLanAccess()
        {
            int port = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.Port; });
            if (LanSetup.IsPortRegistered(port)) return;
            bool prompted = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.LanPrompted; });
            if (prompted) return;

            ConfigStore.Instance.Mutate(delegate(AppConfig c) { c.LanPrompted = true; });
            string error;
            LanSetup.EnsureRegistered(port, false, out error);
        }
    }
}

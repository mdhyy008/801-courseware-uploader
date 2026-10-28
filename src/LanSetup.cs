using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace _801CoursewareUploader
{
    internal static class LanSetup
    {
        public static bool IsPortRegistered(int port)
        {
            return ConfigStore.Instance.Read(delegate(AppConfig c)
            {
                return c.RegisteredPorts.Contains(port);
            });
        }

        public static bool EnsureRegistered(int port, bool force, out string error)
        {
            error = null;
            if (!force && IsPortRegistered(port)) return true;

            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = Application.ExecutablePath;
                info.Arguments = "--register " + port.ToString();
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process process = Process.Start(info))
                {
                    if (process == null)
                    {
                        error = "无法启动提权进程";
                        return false;
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        error = "局域网配置未完成";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                if (!c.RegisteredPorts.Contains(port)) c.RegisteredPorts.Add(port);
            });
            return true;
        }

        public static int RunRegistration(string portText)
        {
            int port;
            if (!int.TryParse(portText, out port) || port < 1024 || port > 65535) return 2;

            string url = "http://+:" + port.ToString() + "/";
            RunNetsh("http delete urlacl url=" + url);
            string result = RunNetsh("http add urlacl url=" + url + " sddl=D:(A;;GX;;;WD)");
            if (result == null)
            {
                result = RunNetsh("http add urlacl url=" + url + " user=Everyone");
                if (result == null) return 3;
            }

            string ruleName = "CoursewareUploader " + port.ToString();
            RunNetsh("advfirewall firewall delete rule name=\"" + ruleName + "\"");
            string firewall = RunNetsh("advfirewall firewall add rule name=\"" + ruleName +
                "\" dir=in action=allow protocol=TCP localport=" + port.ToString() + " profile=any");
            if (firewall == null) return 4;
            return 0;
        }

        private static string RunNetsh(string arguments)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = "netsh.exe";
                info.Arguments = arguments;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process process = Process.Start(info))
                {
                    if (process == null) return null;
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit(20000);
                    if (process.ExitCode == 0) return output;
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        public static string GetLanAddress(int port)
        {
            try
            {
                System.Net.NetworkInformation.NetworkInterface[] interfaces =
                    System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    System.Net.NetworkInformation.NetworkInterface ni = interfaces[i];
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    System.Net.NetworkInformation.UnicastIPAddressInformationCollection addresses =
                        ni.GetIPProperties().UnicastAddresses;
                    for (int j = 0; j < addresses.Count; j++)
                    {
                        System.Net.IPAddress address = addresses[j].Address;
                        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        string text = address.ToString();
                        if (text.StartsWith("169.254.")) continue;
                        return "http://" + text + ":" + port.ToString() + "/";
                    }
                }
            }
            catch
            {
            }
            return "http://localhost:" + port.ToString() + "/";
        }
    }
}

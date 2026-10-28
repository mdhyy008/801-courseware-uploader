using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace _801CoursewareUploader
{
    internal sealed class ConfigStore
    {
        private static readonly ConfigStore instance = new ConfigStore();
        public static ConfigStore Instance { get { return instance; } }

        private readonly object gate = new object();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private AppConfig config;

        private ConfigStore()
        {
            Load();
        }

        public void Load()
        {
            lock (gate)
            {
                AppConfig loaded = null;
                try
                {
                    if (File.Exists(AppPaths.ConfigFile))
                    {
                        string text = File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8);
                        loaded = json.Deserialize<AppConfig>(text);
                    }
                }
                catch
                {
                    loaded = null;
                }
                if (loaded == null) loaded = new AppConfig();

                if (loaded.Port < 1024 || loaded.Port > 65535) loaded.Port = 41004;
                if (loaded.MaxConcurrent < 1) loaded.MaxConcurrent = 1;
                if (loaded.MaxConcurrent > 50) loaded.MaxConcurrent = 50;
                if (loaded.Whitelist == null) loaded.Whitelist = AppConfig.DefaultWhitelist();
                loaded.Whitelist = NormalizeWhitelist(loaded.Whitelist);
                if (loaded.Whitelist.Count == 0) loaded.Whitelist = AppConfig.DefaultWhitelist();
                if (loaded.Directories == null) loaded.Directories = new List<DirectoryEntry>();
                if (loaded.Rosters == null) loaded.Rosters = new List<RosterEntry>();
                if (loaded.RegisteredPorts == null) loaded.RegisteredPorts = new List<int>();
                if (loaded.PasswordSalt == null) loaded.PasswordSalt = "";
                if (loaded.PasswordHash == null) loaded.PasswordHash = "";

                config = loaded;
            }
        }

        public T Read<T>(Func<AppConfig, T> reader)
        {
            lock (gate) return reader(config);
        }

        public void Mutate(Action<AppConfig> mutator)
        {
            lock (gate)
            {
                mutator(config);
                SaveLocked();
            }
        }

        private void SaveLocked()
        {
            try
            {
                AppPaths.Ensure();
                string text = json.Serialize(config);
                string temp = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(AppPaths.ConfigFile)) File.Delete(AppPaths.ConfigFile);
                File.Move(temp, AppPaths.ConfigFile);
            }
            catch
            {
            }
        }

        public static List<string> NormalizeWhitelist(IEnumerable<string> raw)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (raw != null)
            {
                foreach (string item in raw)
                {
                    if (item == null) continue;
                    string s = item.Trim().TrimStart('.').ToLowerInvariant();
                    if (s.Length == 0 || s.Length > 16) continue;
                    bool valid = true;
                    for (int i = 0; i < s.Length; i++)
                    {
                        char c = s[i];
                        if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) { valid = false; break; }
                    }
                    if (!valid) continue;
                    if (seen.Add(s)) result.Add(s);
                }
            }
            return result;
        }
    }
}

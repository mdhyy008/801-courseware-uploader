using System;
using System.Collections.Generic;

namespace _801CoursewareUploader
{
    internal sealed class WebAuth
    {
        private readonly object gate = new object();
        private readonly HashSet<string> tokens = new HashSet<string>(StringComparer.Ordinal);

        public bool Required
        {
            get
            {
                return ConfigStore.Instance.Read(delegate(AppConfig c)
                {
                    return !string.IsNullOrEmpty(c.PasswordHash) && !string.IsNullOrEmpty(c.PasswordSalt);
                });
            }
        }

        public static string ComposeHash(string salt, string password)
        {
            return Hashing.Sha256String(salt + "|" + password);
        }

        public void SetPassword(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                ConfigStore.Instance.Mutate(delegate(AppConfig c)
                {
                    c.PasswordSalt = "";
                    c.PasswordHash = "";
                });
                RevokeAll();
                return;
            }
            string salt = Hashing.RandomHex(16);
            string hash = ComposeHash(salt, plain);
            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                c.PasswordSalt = salt;
                c.PasswordHash = hash;
            });
            RevokeAll();
        }

        public bool Verify(string password)
        {
            if (!Required) return true;
            if (password == null) return false;
            string salt = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.PasswordSalt; });
            string expected = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.PasswordHash; });
            string actual = ComposeHash(salt, password);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        public string IssueToken()
        {
            string token = Hashing.RandomHex(24);
            lock (gate) tokens.Add(token);
            return token;
        }

        public bool IsValid(string token)
        {
            if (!Required) return true;
            if (string.IsNullOrEmpty(token)) return false;
            lock (gate) return tokens.Contains(token);
        }

        public void RevokeAll()
        {
            lock (gate) tokens.Clear();
        }
    }
}

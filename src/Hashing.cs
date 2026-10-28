using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace _801CoursewareUploader
{
    internal static class Hashing
    {
        public static string Sha256File(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                return ToHex(hash);
            }
        }

        public static string Sha256File(string path, Action<long> progress)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[1024 * 1024];
                long loaded = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    loaded += read;
                    if (progress != null) progress(loaded);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return ToHex(sha.Hash);
            }
        }

        public static string Sha256Stream(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(stream);
                return ToHex(hash);
            }
        }

        public static string Sha256Bytes(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        public static string Sha256String(string text)
        {
            return Sha256Bytes(Encoding.UTF8.GetBytes(text));
        }

        public static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        public static string RandomHex(int byteCount)
        {
            byte[] buffer = new byte[byteCount];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(buffer);
            }
            return ToHex(buffer);
        }

        public static bool LooksLikeHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}

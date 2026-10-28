using System;
using System.IO;
using System.Reflection;

namespace _801CoursewareUploader
{
    internal static class AppPaths
    {
        public static readonly string BaseDir =
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public static readonly string DataDir = Path.Combine(BaseDir, "data");
        public static readonly string ConfigFile = Path.Combine(DataDir, "config.json");
        public static readonly string IndexDir = Path.Combine(DataDir, "index");
        public static readonly string UploadsDir = Path.Combine(DataDir, "uploads");
        public static readonly string TmpDir = Path.Combine(DataDir, "tmp");
        public static readonly string LanStateFile = Path.Combine(DataDir, "lan.json");

        public static void Ensure()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(IndexDir);
            Directory.CreateDirectory(UploadsDir);
            Directory.CreateDirectory(TmpDir);
        }
    }
}

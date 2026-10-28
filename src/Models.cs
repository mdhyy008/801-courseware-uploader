using System;
using System.Collections.Generic;

namespace _801CoursewareUploader
{
    public sealed class DirectoryEntry
    {
        public string Id { get; set; }
        public string Path { get; set; }
    }

    public sealed class RosterEntry
    {
        public string DirId { get; set; }
        public string Folder { get; set; }
        public List<string> Names { get; set; }
        public bool Enabled { get; set; }

        public RosterEntry()
        {
            Names = new List<string>();
        }
    }

    public sealed class RosterSaveRequest
    {
        public string DirId { get; set; }
        public string Folder { get; set; }
        public List<string> Names { get; set; }
        public bool Enabled { get; set; }
    }

    public sealed class AppConfig
    {
        public int Port { get; set; }
        public string PasswordSalt { get; set; }
        public string PasswordHash { get; set; }
        public int MaxConcurrent { get; set; }
        public List<string> Whitelist { get; set; }
        public List<DirectoryEntry> Directories { get; set; }
        public List<RosterEntry> Rosters { get; set; }
        public bool LanPrompted { get; set; }
        public List<int> RegisteredPorts { get; set; }

        public AppConfig()
        {
            Port = 41004;
            PasswordSalt = "";
            PasswordHash = "";
            MaxConcurrent = 3;
            Whitelist = DefaultWhitelist();
            Directories = new List<DirectoryEntry>();
            Rosters = new List<RosterEntry>();
            LanPrompted = false;
            RegisteredPorts = new List<int>();
        }

        public static List<string> DefaultWhitelist()
        {
            string[] raw = new string[]
            {
                "ppt", "pptx", "pps", "ppsx", "pot", "potx",
                "doc", "docx", "xls", "xlsx",
                "pdf", "txt",
                "zip", "rar", "7z",
                "mp4", "avi", "mkv", "mov", "wmv",
                "mp3", "wav",
                "png", "jpg", "jpeg", "gif", "webp",
                "html", "htm"
            };
            List<string> list = new List<string>();
            for (int i = 0; i < raw.Length; i++) list.Add(raw[i]);
            return list;
        }
    }

    public sealed class IndexEntry
    {
        public long Size { get; set; }
        public long Mtime { get; set; }
        public string Hash { get; set; }
    }

    public sealed class DirectoryIndexFile
    {
        public Dictionary<string, IndexEntry> Files { get; set; }

        public DirectoryIndexFile()
        {
            Files = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class LanState
    {
        public List<int> Ports { get; set; }

        public LanState()
        {
            Ports = new List<int>();
        }
    }

    public sealed class LoginRequest
    {
        public string Password { get; set; }
    }

    public sealed class IdRequest
    {
        public string Id { get; set; }
    }

    public sealed class FileOpRequest
    {
        public string DirId { get; set; }
        public string Path { get; set; }
        public string NewName { get; set; }
    }

    public sealed class UploadInitRequest
    {
        public string DirId { get; set; }
        public string Folder { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public string Hash { get; set; }
        public string Action { get; set; }
    }

    public sealed class CreateFolderRequest
    {
        public string DirId { get; set; }
        public string Folder { get; set; }
        public string Name { get; set; }
    }

    public sealed class UploadPrecheckRequest
    {
        public string DirId { get; set; }
        public string Folder { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
    }

    public sealed class DirProgressEntry
    {
        public string DirId { get; set; }
        public int Active { get; set; }
        public int Saving { get; set; }
        public long DoneBytes { get; set; }
        public long TotalBytes { get; set; }
    }

    public sealed class UploadProgressRequest
    {
        public List<DirProgressEntry> Dirs { get; set; }
    }
}

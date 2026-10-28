using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.VisualBasic.FileIO;

namespace _801CoursewareUploader
{
    internal sealed class FileCatalog
    {
        public sealed class DirectoryState
        {
            public DirectoryEntry Entry;
            public Dictionary<string, IndexEntry> Files;
            public volatile bool Indexing;
            public volatile bool SilentIndexing;
            public int IndexTotal;
            public int IndexDone;
            public long IndexBytes;
            public long IndexTotalBytes;
            public string IndexError;
            public DateTime LastTreeBuild;
            public TreeNodeSnapshot Tree;
            public readonly object Gate = new object();
        }

        public sealed class DirView
        {
            public string Id;
            public string Name;
            public string Path;
            public int Count;
            public bool Indexing;
            public int IndexTotal;
            public int IndexDone;
            public long IndexBytes;
            public long IndexTotalBytes;
            public string Error;
        }

        public sealed class FileView
        {
            public string Name;
            public string RelPath;
            public long Size;
            public DateTime Modified;
        }

        public sealed class RosterStatusView
        {
            public string Name;
            public int Count;
            public long Bytes;
            public DateTime Modified;
        }

        public sealed class TreeNodeSnapshot
        {
            public string Name;
            public string Path;
            public List<TreeNodeSnapshot> Children;

            public TreeNodeSnapshot()
            {
                Children = new List<TreeNodeSnapshot>();
            }
        }

        private sealed class FolderPair
        {
            public string Full;
            public TreeNodeSnapshot Node;
        }

        private readonly object gate = new object();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly Dictionary<string, DirectoryState> dirs =
            new Dictionary<string, DirectoryState>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> whitelist =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly BlockingCollection<string> indexQueue = new BlockingCollection<string>();
        private readonly Thread indexThread;

        public FileCatalog()
        {
            ReloadWhitelist();
            LoadDirectories();
            indexThread = new Thread(IndexWorker);
            indexThread.IsBackground = true;
            indexThread.Name = "IndexWorker";
            indexThread.Start();
        }

        private void ReloadWhitelist()
        {
            List<string> list = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.Whitelist; });
            lock (gate)
            {
                whitelist.Clear();
                for (int i = 0; i < list.Count; i++) whitelist.Add(list[i]);
            }
        }

        public void SetWhitelist(List<string> list)
        {
            lock (gate)
            {
                whitelist.Clear();
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++) whitelist.Add(list[i]);
                }
            }
            ReindexAll();
        }

        public bool IsWhitelisted(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.TrimStart('.').ToLowerInvariant();
            lock (gate) return whitelist.Contains(ext);
        }

        private void LoadDirectories()
        {
            List<DirectoryEntry> entries = ConfigStore.Instance.Read(delegate(AppConfig c)
            {
                return new List<DirectoryEntry>(c.Directories);
            });

            for (int i = 0; i < entries.Count; i++)
            {
                DirectoryEntry entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.Id) || string.IsNullOrEmpty(entry.Path)) continue;
                DirectoryState state = new DirectoryState();
                state.Entry = entry;
                state.Files = LoadIndexFile(entry.Id);
                state.SilentIndexing = true;
                lock (gate) dirs[entry.Id] = state;
                if (Directory.Exists(entry.Path)) indexQueue.Add(entry.Id);
            }
        }

        private Dictionary<string, IndexEntry> LoadIndexFile(string id)
        {
            try
            {
                string path = Path.Combine(AppPaths.IndexDir, id + ".json");
                if (!File.Exists(path)) return new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
                string text = File.ReadAllText(path, Encoding.UTF8);
                DirectoryIndexFile file = json.Deserialize<DirectoryIndexFile>(text);
                if (file == null || file.Files == null)
                    return new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, IndexEntry> result =
                    new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, IndexEntry> pair in file.Files)
                {
                    if (pair.Value != null) result[pair.Key] = pair.Value;
                }
                return result;
            }
            catch
            {
                return new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveIndexFile(string id, Dictionary<string, IndexEntry> files)
        {
            try
            {
                AppPaths.Ensure();
                DirectoryIndexFile file = new DirectoryIndexFile();
                file.Files = files;
                string text = json.Serialize(file);
                string path = Path.Combine(AppPaths.IndexDir, id + ".json");
                string temp = path + ".tmp";
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
            }
        }

        public List<DirView> GetSnapshot()
        {
            List<DirView> result = new List<DirView>();
            lock (gate)
            {
                foreach (DirectoryState state in dirs.Values)
                {
                    DirView view = new DirView();
                    view.Id = state.Entry.Id;
                    view.Path = state.Entry.Path;
                    view.Name = LeafName(state.Entry.Path);
                    view.Indexing = state.Indexing && !state.SilentIndexing;
                    view.IndexTotal = state.IndexTotal;
                    view.IndexDone = state.IndexDone;
                    view.IndexBytes = state.IndexBytes;
                    view.IndexTotalBytes = state.IndexTotalBytes;
                    view.Error = state.IndexError;
                    lock (state.Gate) view.Count = state.Files.Count;
                    result.Add(view);
                }
            }
            return result;
        }

        public List<DirectoryEntry> GetDirectories()
        {
            List<DirectoryEntry> result = new List<DirectoryEntry>();
            lock (gate)
            {
                foreach (DirectoryState state in dirs.Values)
                {
                    DirectoryEntry entry = new DirectoryEntry();
                    entry.Id = state.Entry.Id;
                    entry.Path = state.Entry.Path;
                    result.Add(entry);
                }
            }
            return result;
        }

        public DirectoryState GetState(string id)
        {
            if (id == null) return null;
            lock (gate)
            {
                DirectoryState state;
                return dirs.TryGetValue(id, out state) ? state : null;
            }
        }

        public static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length == 0) return path;
            string name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) return trimmed;
            return name;
        }

        public bool AddDirectory(string path, out string error, out string id)
        {
            error = null;
            id = null;
            string full;
            try
            {
                full = Path.GetFullPath(path).TrimEnd('\\', '/');
                if (full.Length == 2 && full[1] == ':') full = full + "\\";
            }
            catch
            {
                error = "路径无效";
                return false;
            }
            if (!Directory.Exists(full))
            {
                error = "目录不存在";
                return false;
            }

            DirectoryEntry entry = new DirectoryEntry();
            entry.Id = Guid.NewGuid().ToString("N");
            entry.Path = full;

            DirectoryState state = new DirectoryState();
            state.Entry = entry;
            state.Files = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);

            lock (gate)
            {
                foreach (DirectoryState existing in dirs.Values)
                {
                    if (string.Equals(NormalizeRoot(existing.Entry.Path), NormalizeRoot(full),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        error = "已在列表中";
                        return false;
                    }
                }
                dirs[entry.Id] = state;
            }

            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                c.Directories.Add(entry);
            });

            id = entry.Id;
            indexQueue.Add(entry.Id);
            return true;
        }

        public void RemoveDirectory(string id, bool recycleFiles)
        {
            DirectoryState state = null;
            lock (gate)
            {
                if (dirs.TryGetValue(id, out state)) dirs.Remove(id);
            }
            if (state == null) return;

            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                for (int i = c.Directories.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(c.Directories[i].Id, id, StringComparison.OrdinalIgnoreCase))
                        c.Directories.RemoveAt(i);
                }
            });

            if (recycleFiles)
            {
                List<string> files = new List<string>();
                lock (gate)
                {
                    foreach (string rel in state.Files.Keys)
                    {
                        string full;
                        if (TryCombine(state.Entry.Path, rel, out full)) files.Add(full);
                    }
                }
                for (int i = 0; i < files.Count; i++) Recycle(files[i]);
            }

            try
            {
                string index = Path.Combine(AppPaths.IndexDir, id + ".json");
                if (File.Exists(index)) File.Delete(index);
            }
            catch
            {
            }
        }

        public void Reindex(string id)
        {
            DirectoryState state = GetState(id);
            if (state == null) return;
            state.SilentIndexing = false;
            if (state.Indexing) return;
            state.Indexing = true;
            indexQueue.Add(id);
        }

        public void ReindexAll()
        {
            List<string> ids = new List<string>();
            lock (gate)
            {
                foreach (string id in dirs.Keys) ids.Add(id);
            }
            for (int i = 0; i < ids.Count; i++)
            {
                DirectoryState state = GetState(ids[i]);
                if (state != null) state.SilentIndexing = false;
                indexQueue.Add(ids[i]);
            }
        }

        private void IndexWorker()
        {
            while (true)
            {
                string id = indexQueue.Take();
                try
                {
                    IndexDirectory(id);
                }
                catch
                {
                    DirectoryState state = GetState(id);
                    if (state != null)
                    {
                        state.Indexing = false;
                        state.SilentIndexing = false;
                    }
                }
            }
        }

        private void IndexDirectory(string id)
        {
            DirectoryState state = GetState(id);
            if (state == null) return;

            DateTime startedUtc = DateTime.UtcNow;
            state.Indexing = true;
            state.IndexError = null;
            state.IndexDone = 0;
            state.IndexTotal = 0;
            state.IndexBytes = 0;
            state.IndexTotalBytes = 0;

            List<string> relPaths = new List<string>();
            List<string> fullPaths = new List<string>();
            long totalBytes = 0;
            Dictionary<string, IndexEntry> old;
            lock (state.Gate) old = new Dictionary<string, IndexEntry>(state.Files, StringComparer.OrdinalIgnoreCase);

            try
            {
                List<string> files = EnumerateWhitelistedFiles(state.Entry.Path);
                for (int i = 0; i < files.Count; i++)
                {
                    string full = files[i];
                    string rel = RelativePath(state.Entry.Path, full);
                    if (rel == null) continue;
                    relPaths.Add(rel);
                    fullPaths.Add(full);
                    try { totalBytes += new FileInfo(full).Length; }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                state.IndexError = ex.Message;
            }

            state.IndexTotal = relPaths.Count;
            state.IndexTotalBytes = totalBytes;

            Dictionary<string, IndexEntry> next =
                new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);

            long doneBytes = 0;
            for (int i = 0; i < relPaths.Count; i++)
            {
                if (GetState(id) == null)
                {
                    state.Indexing = false;
                    state.SilentIndexing = false;
                    return;
                }
                string rel = relPaths[i];
                string full = fullPaths[i];
                IndexEntry entry = null;
                long size = 0;
                try
                {
                    FileInfo info = new FileInfo(full);
                    size = info.Length;
                    long mtime = info.LastWriteTimeUtc.Ticks;
                    IndexEntry existing;
                    if (old.TryGetValue(rel, out existing) && existing != null &&
                        existing.Size == size && existing.Mtime == mtime &&
                        !string.IsNullOrEmpty(existing.Hash))
                    {
                        entry = existing;
                    }
                    else
                    {
                        entry = new IndexEntry();
                        entry.Size = size;
                        entry.Mtime = mtime;
                        long baseBytes = doneBytes;
                        entry.Hash = Hashing.Sha256File(full, delegate(long loaded)
                        {
                            state.IndexBytes = baseBytes + loaded;
                        });
                    }
                }
                catch
                {
                    entry = null;
                }
                if (entry != null) next[rel] = entry;
                doneBytes += size;
                state.IndexDone = i + 1;
                state.IndexBytes = doneBytes;
            }

            if (GetState(id) == null)
            {
                state.Indexing = false;
                state.SilentIndexing = false;
                return;
            }
            lock (state.Gate) state.Files = next;
            SaveIndexFile(id, next);
            int elapsed = (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds;
            if (elapsed < 700)
            {
                try { Thread.Sleep(700 - elapsed); }
                catch { }
            }
            state.Indexing = false;
        }

        private List<string> EnumerateWhitelistedFiles(string root)
        {
            List<string> result = new List<string>();
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = null;
                string[] subs = null;
                try { files = Directory.GetFiles(dir); } catch { }
                try { subs = Directory.GetDirectories(dir); } catch { }

                if (files != null)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        if (IsWhitelisted(files[i])) result.Add(files[i]);
                    }
                }
                if (subs != null)
                {
                    for (int i = 0; i < subs.Length; i++)
                    {
                        try
                        {
                            FileAttributes attrs = File.GetAttributes(subs[i]);
                            if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch
                        {
                            continue;
                        }
                        stack.Push(subs[i]);
                    }
                }
            }
            return result;
        }

        public static string NormalizeRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string full;
            try { full = Path.GetFullPath(path); }
            catch { full = path; }
            full = full.TrimEnd('\\', '/');
            if (full.Length == 2 && full[1] == ':') full = full + "\\";
            if (full.Length > 0 && full[full.Length - 1] != '\\') full = full + "\\";
            return full;
        }

        private static string RelativePath(string root, string full)
        {
            string rootFull = NormalizeRoot(root);
            try
            {
                string fullPath = Path.GetFullPath(full);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
                return NormalizeRel(fullPath.Substring(rootFull.Length));
            }
            catch
            {
                return null;
            }
        }

        public static string NormalizeRel(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            string s = rel.Replace('/', '\\').Trim();
            while (s.StartsWith("\\")) s = s.Substring(1);
            s = s.TrimEnd('\\');
            if (s == ".") return "";
            return s;
        }

        private static bool TryCombine(string root, string rel, out string full)
        {
            full = null;
            string rootFull = NormalizeRoot(root);
            string normalized = NormalizeRel(rel);
            if (normalized.Length > 0)
            {
                string[] parts = normalized.Split('\\');
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (part.Length == 0 || part == "." || part == "..") return false;
                    if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
                }
            }
            string combined = normalized.Length == 0 ? rootFull : Path.Combine(rootFull, normalized);
            try { combined = Path.GetFullPath(combined); }
            catch { return false; }
            if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return false;
            full = combined;
            return true;
        }

        public bool TryResolve(string dirId, string rel, out string full)
        {
            full = null;
            DirectoryState state = GetState(dirId);
            if (state == null) return false;
            return TryCombine(state.Entry.Path, rel, out full);
        }

        public bool IsSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name == "." || name == "..") return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0) return false;
            return true;
        }

        public bool EnsureFolder(string dirId, string folder, string name, out string error)
        {
            error = null;
            if (!IsSafeFileName(name))
            {
                error = "文件夹名无效";
                return false;
            }
            DirectoryState state = GetState(dirId);
            if (state == null)
            {
                error = "目录不存在";
                return false;
            }
            string fullFolder;
            if (!TryCombine(state.Entry.Path, folder, out fullFolder) || !Directory.Exists(fullFolder))
            {
                error = "文件夹不存在";
                return false;
            }
            string target = Path.Combine(fullFolder, name);
            try
            {
                if (Directory.Exists(target)) return true;
                if (File.Exists(target))
                {
                    error = "已存在同名文件";
                    return false;
                }
                Directory.CreateDirectory(target);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public List<RosterStatusView> RosterStatuses(string dirId, string folder, List<string> names)
        {
            List<RosterStatusView> result = new List<RosterStatusView>();
            if (names == null) return result;
            DirectoryState state = GetState(dirId);
            string fullFolder = null;
            if (state != null) TryCombine(state.Entry.Path, folder, out fullFolder);
            for (int i = 0; i < names.Count; i++)
            {
                RosterStatusView view = new RosterStatusView();
                view.Name = names[i];
                view.Modified = DateTime.MinValue;
                result.Add(view);
                if (fullFolder == null || !IsSafeFileName(view.Name)) continue;
                string[] files;
                try { files = Directory.GetFiles(Path.Combine(fullFolder, view.Name)); }
                catch { continue; }
                for (int j = 0; j < files.Length; j++)
                {
                    if (!IsWhitelisted(Path.GetFileName(files[j]))) continue;
                    try
                    {
                        FileInfo info = new FileInfo(files[j]);
                        view.Count++;
                        view.Bytes += info.Length;
                        if (info.LastWriteTime > view.Modified) view.Modified = info.LastWriteTime;
                    }
                    catch
                    {
                    }
                }
            }
            return result;
        }

        public FileView ToFileView(string dirId, string rel)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return null;
            string full;
            if (!TryCombine(state.Entry.Path, rel, out full)) return null;
            try
            {
                FileInfo info = new FileInfo(full);
                if (!info.Exists) return null;
                FileView view = new FileView();
                view.Name = info.Name;
                view.RelPath = NormalizeRel(rel);
                view.Size = info.Length;
                view.Modified = info.LastWriteTime;
                return view;
            }
            catch
            {
                return null;
            }
        }

        public List<FileView> ListFolder(string dirId, string folder, int page, int pageSize,
            string sort, bool desc, out int total)
        {
            total = 0;
            List<FileView> result = new List<FileView>();
            DirectoryState state = GetState(dirId);
            if (state == null) return result;
            string fullFolder;
            if (!TryCombine(state.Entry.Path, folder, out fullFolder)) return result;
            if (!Directory.Exists(fullFolder)) return result;

            List<FileView> all = new List<FileView>();
            string[] files = null;
            try { files = Directory.GetFiles(fullFolder); } catch { }
            if (files != null)
            {
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileName(files[i]);
                    if (!IsWhitelisted(name)) continue;
                    try
                    {
                        FileInfo info = new FileInfo(files[i]);
                        FileView view = new FileView();
                        view.Name = name;
                        string rel = NormalizeRel(folder);
                        view.RelPath = rel.Length == 0 ? name : rel + "\\" + name;
                        view.Size = info.Length;
                        view.Modified = info.LastWriteTime;
                        all.Add(view);
                    }
                    catch
                    {
                    }
                }
            }

            if (string.Equals(sort, "size", StringComparison.OrdinalIgnoreCase))
            {
                all.Sort(delegate(FileView a, FileView b) { return a.Size.CompareTo(b.Size); });
            }
            else if (string.Equals(sort, "name", StringComparison.OrdinalIgnoreCase))
            {
                all.Sort(delegate(FileView a, FileView b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            else
            {
                all.Sort(delegate(FileView a, FileView b) { return a.Modified.CompareTo(b.Modified); });
            }
            if (desc) all.Reverse();

            total = all.Count;
            int start = (page - 1) * pageSize;
            if (start < 0) start = 0;
            for (int i = start; i < all.Count && i < start + pageSize; i++) result.Add(all[i]);
            return result;
        }

        public List<string> ListFolders(string dirId, string folder, out int totalFiles)
        {
            totalFiles = 0;
            List<string> result = new List<string>();
            DirectoryState state = GetState(dirId);
            if (state == null) return result;
            string fullFolder;
            if (!TryCombine(state.Entry.Path, folder, out fullFolder)) return result;
            string[] subs = null;
            try { subs = Directory.GetDirectories(fullFolder); } catch { }
            if (subs == null) return result;
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < subs.Length; i++)
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(subs[i]);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }
                string name = Path.GetFileName(subs[i]);
                string rel = NormalizeRel(folder);
                result.Add(rel.Length == 0 ? name : rel + "\\" + name);
            }
            return result;
        }

        public TreeNodeSnapshot GetTree(string dirId)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return null;

            lock (state.Gate)
            {
                if (state.Tree != null && (DateTime.UtcNow - state.LastTreeBuild).TotalSeconds < 5)
                    return state.Tree;
            }

            TreeNodeSnapshot root = new TreeNodeSnapshot();
            root.Name = LeafName(state.Entry.Path);
            root.Path = "";

            Stack<FolderPair> stack = new Stack<FolderPair>();
            FolderPair first = new FolderPair();
            first.Full = NormalizeRoot(state.Entry.Path);
            first.Node = root;
            stack.Push(first);

            while (stack.Count > 0)
            {
                FolderPair pair = stack.Pop();
                string[] subs = null;
                try { subs = Directory.GetDirectories(pair.Full); } catch { }
                if (subs == null) continue;
                for (int i = 0; i < subs.Length; i++)
                {
                    try
                    {
                        FileAttributes attrs = File.GetAttributes(subs[i]);
                        if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch
                    {
                        continue;
                    }
                    TreeNodeSnapshot node = new TreeNodeSnapshot();
                    node.Name = Path.GetFileName(subs[i]);
                    string rel = RelativePath(state.Entry.Path, subs[i]);
                    node.Path = rel == null ? "" : rel;
                    pair.Node.Children.Add(node);
                    FolderPair child = new FolderPair();
                    child.Full = subs[i];
                    child.Node = node;
                    stack.Push(child);
                }
            }

            SortTree(root);
            lock (state.Gate)
            {
                state.Tree = root;
                state.LastTreeBuild = DateTime.UtcNow;
            }
            return root;
        }

        private static void SortTree(TreeNodeSnapshot node)
        {
            node.Children.Sort(delegate(TreeNodeSnapshot a, TreeNodeSnapshot b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < node.Children.Count; i++) SortTree(node.Children[i]);
        }

        public void InvalidateTree(string dirId)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return;
            lock (state.Gate)
            {
                state.Tree = null;
                state.LastTreeBuild = DateTime.MinValue;
            }
        }

        public bool HasSameSizeFile(string dirId, string folder, long size, string excludeName)
        {
            if (size <= 0) return false;
            DirectoryState state = GetState(dirId);
            if (state == null) return false;
            string fullFolder;
            if (!TryCombine(state.Entry.Path, folder, out fullFolder)) return false;
            string[] files;
            try { files = Directory.GetFiles(fullFolder); } catch { return false; }
            for (int i = 0; i < files.Length; i++)
            {
                if (!IsWhitelisted(files[i])) continue;
                if (excludeName != null &&
                    string.Equals(Path.GetFileName(files[i]), excludeName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (new FileInfo(files[i]).Length == size) return true;
                }
                catch
                {
                }
            }
            return false;
        }

        public bool TryGetSameName(string dirId, string folder, string name,
            out long size, out DateTime modified)
        {
            size = 0;
            modified = DateTime.MinValue;
            if (!IsSafeFileName(name)) return false;
            DirectoryState state = GetState(dirId);
            if (state == null) return false;
            string fullFolder;
            if (!TryCombine(state.Entry.Path, folder, out fullFolder)) return false;
            try
            {
                FileInfo info = new FileInfo(Path.Combine(fullFolder, name));
                if (!info.Exists) return false;
                size = info.Length;
                modified = info.LastWriteTime;
                return true;
            }
            catch { return false; }
        }

        public string FindByHash(string hash, long size, string excludeFullPath)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            List<DirectoryState> states;
            lock (gate)
            {
                states = new List<DirectoryState>(dirs.Values);
            }
            for (int s = 0; s < states.Count; s++)
            {
                DirectoryState state = states[s];
                lock (state.Gate)
                {
                    foreach (KeyValuePair<string, IndexEntry> pair in state.Files)
                    {
                        IndexEntry entry = pair.Value;
                        if (entry == null) continue;
                        if (size > 0 && entry.Size != size) continue;
                        if (!string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string full;
                        if (!TryCombine(state.Entry.Path, pair.Key, out full)) continue;
                        if (excludeFullPath != null && string.Equals(full, excludeFullPath,
                            StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            if (File.Exists(full)) return full;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            return null;
        }

        public string HashOf(string dirId, string rel)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return null;
            string key = NormalizeRel(rel);
            lock (state.Gate)
            {
                IndexEntry entry;
                if (state.Files.TryGetValue(key, out entry) && entry != null) return entry.Hash;
            }
            return null;
        }

        public void AddUploadedEntry(string dirId, string rel, long size, long mtimeUtcTicks, string hash)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return;
            IndexEntry entry = new IndexEntry();
            entry.Size = size;
            entry.Mtime = mtimeUtcTicks;
            entry.Hash = hash;
            lock (state.Gate)
            {
                state.Files[NormalizeRel(rel)] = entry;
                state.Tree = null;
            }
            SaveIndexFile(dirId, state.Files);
        }

        public void RemoveEntry(string dirId, string rel)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return;
            lock (state.Gate)
            {
                state.Files.Remove(NormalizeRel(rel));
                state.Tree = null;
            }
            SaveIndexFile(dirId, state.Files);
        }

        public void RenameEntry(string dirId, string oldRel, string newRel)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return;
            lock (state.Gate)
            {
                string oldKey = NormalizeRel(oldRel);
                IndexEntry entry;
                if (state.Files.TryGetValue(oldKey, out entry))
                {
                    state.Files.Remove(oldKey);
                    state.Files[NormalizeRel(newRel)] = entry;
                }
                state.Tree = null;
            }
            SaveIndexFile(dirId, state.Files);
        }

        public bool DeleteFile(string dirId, string rel, out string error)
        {
            error = null;
            string full;
            if (!TryResolve(dirId, rel, out full))
            {
                error = "路径无效";
                return false;
            }
            try
            {
                FileInfo info = new FileInfo(full);
                if (!info.Exists)
                {
                    error = "文件不存在";
                    return false;
                }
                if (!Recycle(full))
                {
                    error = "移入回收站失败";
                    return false;
                }
                RemoveEntry(dirId, rel);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool RenameFile(string dirId, string rel, string newName, out string error, out string newRel)
        {
            error = null;
            newRel = null;
            if (!IsSafeFileName(newName))
            {
                error = "文件名无效";
                return false;
            }
            DirectoryState state = GetState(dirId);
            if (state == null)
            {
                error = "目录不存在";
                return false;
            }
            string full;
            if (!TryResolve(dirId, rel, out full))
            {
                error = "路径无效";
                return false;
            }
            string oldExt = Path.GetExtension(full);
            string newExt = Path.GetExtension(newName);
            if (!string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase))
            {
                error = "扩展名不能修改";
                return false;
            }
            string folder = Path.GetDirectoryName(full);
            string target = Path.Combine(folder, newName);
            if (File.Exists(target) && !string.Equals(target, full, StringComparison.OrdinalIgnoreCase))
            {
                error = "同名文件已存在";
                return false;
            }
            try
            {
                File.Move(full, target);
                string relFolder = Path.GetDirectoryName(NormalizeRel(rel));
                newRel = string.IsNullOrEmpty(relFolder) ? newName : relFolder + "\\" + newName;
                RenameEntry(dirId, rel, newRel);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool CreateFolder(string dirId, string folder, string name, out string error, out string newRel)
        {
            error = null;
            newRel = null;
            if (!IsSafeFileName(name) || name == "." || name == "..")
            {
                error = "文件夹名无效";
                return false;
            }
            DirectoryState state = GetState(dirId);
            if (state == null)
            {
                error = "目录不存在";
                return false;
            }
            string fullFolder;
            if (!TryCombine(state.Entry.Path, folder, out fullFolder) || !Directory.Exists(fullFolder))
            {
                error = "目标文件夹不存在";
                return false;
            }
            string target = Path.Combine(fullFolder, name);
            if (Directory.Exists(target) || File.Exists(target))
            {
                error = "已存在同名文件或文件夹";
                return false;
            }
            try
            {
                Directory.CreateDirectory(target);
                string relFolder = NormalizeRel(folder);
                newRel = relFolder.Length == 0 ? name : relFolder + "\\" + name;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool RenameFolder(string dirId, string rel, string newName, out string error, out string newRel)
        {
            error = null;
            newRel = null;
            if (!IsSafeFileName(newName) || newName == "." || newName == "..")
            {
                error = "文件夹名无效";
                return false;
            }
            DirectoryState state = GetState(dirId);
            if (state == null)
            {
                error = "目录不存在";
                return false;
            }
            string folder = NormalizeRel(rel);
            if (folder.Length == 0)
            {
                error = "根目录不能重命名";
                return false;
            }
            string full;
            if (!TryCombine(state.Entry.Path, folder, out full) || !Directory.Exists(full))
            {
                error = "文件夹不存在";
                return false;
            }
            string parent = Path.GetDirectoryName(full);
            string target = Path.Combine(parent, newName);
            if (string.Equals(target, full, StringComparison.OrdinalIgnoreCase))
            {
                error = "文件夹名未变化";
                return false;
            }
            if (Directory.Exists(target) || File.Exists(target))
            {
                error = "已存在同名文件或文件夹";
                return false;
            }
            try
            {
                Directory.Move(full, target);
                string relParent = Path.GetDirectoryName(folder);
                newRel = string.IsNullOrEmpty(relParent) ? newName : relParent + "\\" + newName;
                RenameFolderEntries(dirId, folder, newRel);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void RenameFolderEntries(string dirId, string oldRel, string newRel)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return;
            string prefix = oldRel + "\\";
            lock (state.Gate)
            {
                List<string> keys = new List<string>(state.Files.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        IndexEntry entry = state.Files[key];
                        state.Files.Remove(key);
                        state.Files[newRel + key.Substring(oldRel.Length)] = entry;
                    }
                }
                state.Tree = null;
                state.LastTreeBuild = DateTime.MinValue;
            }
            SaveIndexFile(dirId, state.Files);
        }

        public bool DeleteFolder(string dirId, string rel, out string error)
        {
            error = null;
            DirectoryState state = GetState(dirId);
            if (state == null)
            {
                error = "目录不存在";
                return false;
            }
            string folder = NormalizeRel(rel);
            if (folder.Length == 0)
            {
                error = "根目录不能删除";
                return false;
            }
            string full;
            if (!TryCombine(state.Entry.Path, folder, out full) || !Directory.Exists(full))
            {
                error = "文件夹不存在";
                return false;
            }
            try
            {
                FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            if (Directory.Exists(full))
            {
                error = "移入回收站失败";
                return false;
            }
            RemoveFolderEntries(dirId, folder);
            return true;
        }

        private void RemoveFolderEntries(string dirId, string rel)
        {
            DirectoryState state = GetState(dirId);
            if (state == null) return;
            string prefix = rel + "\\";
            lock (state.Gate)
            {
                List<string> keys = new List<string>(state.Files.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        state.Files.Remove(key);
                }
                state.Tree = null;
                state.LastTreeBuild = DateTime.MinValue;
            }
            SaveIndexFile(dirId, state.Files);
        }

        public static bool Recycle(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string UniqueName(string folderFull, string fileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            string candidate = fileName;
            int index = 1;
            while (File.Exists(Path.Combine(folderFull, candidate)))
            {
                candidate = baseName + "(" + index.ToString() + ")" + ext;
                index++;
                if (index > 9999) break;
            }
            return candidate;
        }
    }
}

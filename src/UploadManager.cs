using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace _801CoursewareUploader
{
    internal sealed class UploadManager
    {
        public const int ChunkSize = 4 * 1024 * 1024;

        public sealed class Session
        {
            public string Id;
            public string DirId;
            public string Folder;
            public string OriginalName;
            public string TargetName;
            public long Size;
            public string Hash;
            public int TotalChunks;
            public long Received;
            public bool Overwrite;
            public bool Completing;
            public DateTime LastActiveUtc;
            public string SessionDir;
            public readonly object Gate = new object();
        }

        public sealed class Meta
        {
            public string Id { get; set; }
            public string DirId { get; set; }
            public string Folder { get; set; }
            public string OriginalName { get; set; }
            public string TargetName { get; set; }
            public long Size { get; set; }
            public string Hash { get; set; }
            public int TotalChunks { get; set; }
            public bool Overwrite { get; set; }
        }

        public sealed class InitRequest
        {
            public string DirId { get; set; }
            public string Folder { get; set; }
            public string Name { get; set; }
            public long Size { get; set; }
            public string Hash { get; set; }
            public string Action { get; set; }
        }

        public sealed class InitResult
        {
            public string Status { get; set; }
            public string UploadId { get; set; }
            public int ChunkSize { get; set; }
            public List<int> Uploaded { get; set; }
            public string TargetName { get; set; }
            public string ExistingPath { get; set; }
            public long ExistingSize { get; set; }
            public string ExistingTime { get; set; }
            public string Error { get; set; }
        }

        public sealed class DirProgress
        {
            public string DirId;
            public int Active;
            public int Saving;
            public long DoneBytes;
            public long TotalBytes;
        }

        private readonly FileCatalog catalog;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly object gate = new object();
        private readonly Dictionary<string, Session> sessions =
            new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private readonly object activeGate = new object();
        private readonly Dictionary<string, DateTime> active =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly object alertGate = new object();
        private readonly Dictionary<string, string> alerts =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object progressGate = new object();
        private readonly Dictionary<string, DirProgress> dirProgress =
            new Dictionary<string, DirProgress>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> progressUpdated =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Thread sweeper;

        public UploadManager(FileCatalog catalog)
        {
            this.catalog = catalog;
            CleanupOldSessions();
            sweeper = new Thread(SweepLoop);
            sweeper.IsBackground = true;
            sweeper.Name = "UploadSweeper";
            sweeper.Start();
        }

        private static void CleanupOldSessions()
        {
            try
            {
                if (!Directory.Exists(AppPaths.UploadsDir)) return;
                string[] dirs = Directory.GetDirectories(AppPaths.UploadsDir);
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < dirs.Length; i++)
                {
                    try
                    {
                        if ((now - Directory.GetLastWriteTimeUtc(dirs[i])).TotalHours >= 24)
                            Directory.Delete(dirs[i], true);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private void SweepLoop()
        {
            while (true)
            {
                Thread.Sleep(10 * 60 * 1000);
                try { Sweep(); }
                catch { }
            }
        }

        private void Sweep()
        {
            List<Session> expired = new List<Session>();
            lock (gate)
            {
                List<string> ids = new List<string>(sessions.Keys);
                for (int i = 0; i < ids.Count; i++)
                {
                    Session s = sessions[ids[i]];
                    if ((DateTime.UtcNow - s.LastActiveUtc).TotalHours >= 24) expired.Add(s);
                }
                for (int i = 0; i < expired.Count; i++) sessions.Remove(expired[i].Id);
            }
            for (int i = 0; i < expired.Count; i++) DeleteSessionDir(expired[i].Id);

            lock (activeGate)
            {
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, DateTime> pair in active)
                {
                    if ((DateTime.UtcNow - pair.Value).TotalMinutes >= 5) stale.Add(pair.Key);
                }
                for (int i = 0; i < stale.Count; i++) active.Remove(stale[i]);
            }
        }

        private static string SessionDirOf(string id)
        {
            return Path.Combine(AppPaths.UploadsDir, id);
        }

        private static void DeleteSessionDir(string id)
        {
            try
            {
                string dir = SessionDirOf(id);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
            }
        }

        private int MaxConcurrent
        {
            get { return ConfigStore.Instance.Read(delegate(AppConfig c) { return c.MaxConcurrent; }); }
        }

        public InitResult Init(InitRequest request)
        {
            InitResult result = new InitResult();
            result.Status = "error";
            if (request == null)
            {
                result.Error = "请求无效";
                return result;
            }
            if (string.IsNullOrEmpty(request.DirId) || string.IsNullOrEmpty(request.Name))
            {
                result.Error = "参数无效";
                return result;
            }
            FileCatalog.DirectoryState state = catalog.GetState(request.DirId);
            if (state == null)
            {
                result.Error = "目录不在列表中";
                return result;
            }
            if (!catalog.IsSafeFileName(request.Name))
            {
                result.Error = "文件名无效";
                return result;
            }
            if (!catalog.IsWhitelisted(request.Name))
            {
                result.Error = "文件类型不允许";
                return result;
            }
            if (request.Size <= 0)
            {
                result.Error = "文件为空";
                return result;
            }
            string hash = string.IsNullOrEmpty(request.Hash) ? "" : request.Hash.Trim().ToLowerInvariant();
            if (hash.Length > 0 && !Hashing.LooksLikeHash(hash))
            {
                result.Error = "校验值无效";
                return result;
            }

            string folder = FileCatalog.NormalizeRel(request.Folder);
            string fullFolder;
            if (!catalog.TryResolve(request.DirId, folder, out fullFolder) || !Directory.Exists(fullFolder))
            {
                result.Error = "目标文件夹不存在";
                return result;
            }

            string action = (request.Action ?? "").Trim().ToLowerInvariant();
            string targetName = request.Name;
            bool overwrite = false;

            string targetPath = Path.Combine(fullFolder, targetName);
            bool targetExists = File.Exists(targetPath);

            if (hash.Length > 0)
            {
                string duplicate = catalog.FindByHash(hash, request.Size,
                    action == "overwrite" && targetExists ? targetPath : null);
                if (duplicate != null)
                {
                    if (action == "skip")
                    {
                        result.Status = "skipped";
                        return result;
                    }
                    if (action != "rename")
                    {
                        result.Status = "duplicate";
                        result.ExistingPath = duplicate;
                        return result;
                    }
                    targetName = FileCatalog.UniqueName(fullFolder, targetName);
                }
            }

            if (targetExists)
            {
                if (action == "overwrite")
                {
                    overwrite = true;
                }
                else if (action == "rename")
                {
                    targetName = FileCatalog.UniqueName(fullFolder, targetName);
                }
                else
                {
                    result.Status = "nameConflict";
                    result.TargetName = targetName;
                    try
                    {
                        FileInfo info = new FileInfo(targetPath);
                        if (info.Exists)
                        {
                            result.ExistingSize = info.Length;
                            result.ExistingTime = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                        }
                    }
                    catch
                    {
                    }
                    return result;
                }
            }

            int totalChunks = (int)((request.Size + ChunkSize - 1) / ChunkSize);
            if (totalChunks <= 0) totalChunks = 1;

            string id = Hashing.Sha256String(string.Join("|", new string[]
            {
                request.DirId, folder, request.Name, request.Size.ToString(),
                hash, targetName, overwrite ? "1" : "0"
            }));

            Session session;
            lock (gate)
            {
                if (!sessions.TryGetValue(id, out session))
                {
                    session = LoadSession(id);
                    if (session == null)
                    {
                        session = new Session();
                        session.Id = id;
                        session.DirId = request.DirId;
                        session.Folder = folder;
                        session.OriginalName = request.Name;
                        session.TargetName = targetName;
                        session.Size = request.Size;
                        session.Hash = hash;
                        session.TotalChunks = totalChunks;
                        session.Overwrite = overwrite;
                        session.SessionDir = SessionDirOf(id);
                        session.LastActiveUtc = DateTime.UtcNow;
                        Directory.CreateDirectory(session.SessionDir);
                        Directory.CreateDirectory(Path.Combine(session.SessionDir, "chunks"));
                        SaveMeta(session);
                    }
                    sessions[id] = session;
                }
            }

            List<int> uploaded = new List<int>();
            long received = 0;
            lock (session.Gate)
            {
                for (int i = 0; i < session.TotalChunks; i++)
                {
                    string chunk = Path.Combine(session.SessionDir, "chunks", i.ToString() + ".bin");
                    try
                    {
                        if (File.Exists(chunk))
                        {
                            uploaded.Add(i);
                            received += new FileInfo(chunk).Length;
                        }
                    }
                    catch
                    {
                    }
                }
                session.Received = received;
                session.LastActiveUtc = DateTime.UtcNow;
            }

            result.Status = "ok";
            result.UploadId = session.Id;
            result.ChunkSize = ChunkSize;
            result.Uploaded = uploaded;
            result.TargetName = session.TargetName;
            return result;
        }

        private Session LoadSession(string id)
        {
            try
            {
                string dir = SessionDirOf(id);
                string metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath)) return null;
                string text = File.ReadAllText(metaPath, Encoding.UTF8);
                Meta meta = json.Deserialize<Meta>(text);
                if (meta == null || meta.Id != id) return null;
                Session session = new Session();
                session.Id = meta.Id;
                session.DirId = meta.DirId;
                session.Folder = meta.Folder;
                session.OriginalName = meta.OriginalName;
                session.TargetName = meta.TargetName;
                session.Size = meta.Size;
                session.Hash = meta.Hash;
                session.TotalChunks = meta.TotalChunks;
                session.Overwrite = meta.Overwrite;
                session.SessionDir = dir;
                session.LastActiveUtc = Directory.GetLastWriteTimeUtc(dir);
                if (session.TotalChunks <= 0)
                    session.TotalChunks = (int)((session.Size + ChunkSize - 1) / ChunkSize);
                return session;
            }
            catch
            {
                return null;
            }
        }

        private void SaveMeta(Session session)
        {
            try
            {
                Meta meta = new Meta();
                meta.Id = session.Id;
                meta.DirId = session.DirId;
                meta.Folder = session.Folder;
                meta.OriginalName = session.OriginalName;
                meta.TargetName = session.TargetName;
                meta.Size = session.Size;
                meta.Hash = session.Hash;
                meta.TotalChunks = session.TotalChunks;
                meta.Overwrite = session.Overwrite;
                string text = json.Serialize(meta);
                File.WriteAllText(Path.Combine(session.SessionDir, "meta.json"), text, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private Session Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (gate)
            {
                Session session;
                if (sessions.TryGetValue(id, out session)) return session;
                session = LoadSession(id);
                if (session != null) sessions[id] = session;
                return session;
            }
        }

        private bool TryEnter(string id)
        {
            lock (activeGate)
            {
                if (active.ContainsKey(id))
                {
                    active[id] = DateTime.UtcNow;
                    return true;
                }
                if (active.Count >= MaxConcurrent) return false;
                active[id] = DateTime.UtcNow;
                return true;
            }
        }

        private void Leave(string id)
        {
            lock (activeGate)
            {
                active.Remove(id);
            }
        }

        public string WriteChunk(string id, int index, Stream body, long length)
        {
            Session session = Find(id);
            if (session == null) return "上传任务不存在";
            if (index < 0 || index >= session.TotalChunks) return "分块序号无效";

            long expected = ChunkSize;
            if (index == session.TotalChunks - 1)
                expected = session.Size - (long)index * ChunkSize;
            if (length != expected) return "分块大小不符";

            if (!TryEnter(id)) return "busy";

            try
            {
                lock (session.Gate)
                {
                    string chunksDir = Path.Combine(session.SessionDir, "chunks");
                    Directory.CreateDirectory(chunksDir);
                    string target = Path.Combine(chunksDir, index.ToString() + ".bin");
                    string temp = target + ".tmp";
                    long oldLength = 0;
                    try { if (File.Exists(target)) oldLength = new FileInfo(target).Length; }
                    catch { }

                    using (FileStream output = new FileStream(temp, FileMode.Create, FileAccess.Write,
                        FileShare.None, 128 * 1024, FileOptions.SequentialScan))
                    {
                        byte[] buffer = new byte[128 * 1024];
                        long remaining = length;
                        while (remaining > 0)
                        {
                            int want = buffer.Length;
                            if (want > remaining) want = (int)remaining;
                            int read = body.Read(buffer, 0, want);
                            if (read <= 0) break;
                            output.Write(buffer, 0, read);
                            remaining -= read;
                        }
                        if (remaining != 0)
                        {
                            output.Close();
                            try { File.Delete(temp); } catch { }
                            return "分块不完整";
                        }
                    }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(temp, target);
                    session.Received += expected - oldLength;
                    if (session.Received < 0) session.Received = 0;
                    session.LastActiveUtc = DateTime.UtcNow;
                    try { File.SetLastWriteTimeUtc(session.SessionDir, DateTime.UtcNow); }
                    catch { }
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public sealed class CompleteResult
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public string SavedName { get; set; }
            public string RelPath { get; set; }
            public string DirId { get; set; }
        }

        public CompleteResult Complete(string id)
        {
            CompleteResult result = new CompleteResult();
            Session session = Find(id);
            if (session == null)
            {
                result.Error = "上传任务不存在";
                return result;
            }

            string dirId;
            string folder;
            long size;
            int totalChunks;
            string hash;
            string targetName;
            bool overwrite;
            string sessionDir;
            lock (session.Gate)
            {
                if (session.Completing)
                {
                    result.Error = "正在处理";
                    return result;
                }
                if (session.Received < session.Size)
                {
                    result.Error = "分块不完整";
                    return result;
                }
                session.Completing = true;
                dirId = session.DirId;
                folder = session.Folder;
                size = session.Size;
                totalChunks = session.TotalChunks;
                hash = session.Hash;
                targetName = session.TargetName;
                overwrite = session.Overwrite;
                sessionDir = session.SessionDir;
            }

            string assembled = Path.Combine(sessionDir, "assembled.bin");
            try
            {
                using (FileStream output = new FileStream(assembled, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                {
                    for (int i = 0; i < totalChunks; i++)
                    {
                        string chunk = Path.Combine(sessionDir, "chunks", i.ToString() + ".bin");
                        if (!File.Exists(chunk))
                        {
                            result.Error = "分块缺失";
                            lock (session.Gate) session.Completing = false;
                            return result;
                        }
                        using (FileStream input = new FileStream(chunk, FileMode.Open, FileAccess.Read,
                            FileShare.Read, 1024 * 1024))
                        {
                            byte[] buffer = new byte[1024 * 1024];
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                output.Write(buffer, 0, read);
                        }
                    }
                }

                FileCatalog.DirectoryState state = catalog.GetState(dirId);
                if (state == null)
                {
                    result.Error = "目录不在列表中";
                    lock (session.Gate) session.Completing = false;
                    return result;
                }
                string fullFolder;
                if (!catalog.TryResolve(dirId, folder, out fullFolder) ||
                    !Directory.Exists(fullFolder))
                {
                    result.Error = "目标文件夹不存在";
                    lock (session.Gate) session.Completing = false;
                    return result;
                }

                string targetPath = Path.Combine(fullFolder, targetName);
                if (File.Exists(targetPath))
                {
                    if (overwrite)
                    {
                        if (!FileCatalog.Recycle(targetPath))
                        {
                            result.Error = "无法替换同名文件";
                            lock (session.Gate) session.Completing = false;
                            return result;
                        }
                    }
                    else
                    {
                        targetName = FileCatalog.UniqueName(fullFolder, targetName);
                        targetPath = Path.Combine(fullFolder, targetName);
                    }
                }

                try
                {
                    File.Move(assembled, targetPath);
                }
                catch (IOException)
                {
                    File.Copy(assembled, targetPath, true);
                    try { File.Delete(assembled); } catch { }
                }

                string rel = folder.Length == 0
                    ? targetName
                    : folder + "\\" + targetName;
                long mtime = 0;
                try { mtime = File.GetLastWriteTimeUtc(targetPath).Ticks; }
                catch { mtime = DateTime.UtcNow.Ticks; }
                catalog.AddUploadedEntry(dirId, rel, size, mtime, hash);
                catalog.InvalidateTree(dirId);

                result.Ok = true;
                result.SavedName = targetName;
                result.RelPath = rel;
                result.DirId = dirId;

                if (!string.IsNullOrEmpty(hash))
                    VerifyInBackground(dirId, rel, targetPath, hash);

                DeleteSessionDir(id);
                lock (gate) sessions.Remove(id);
                Leave(id);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                lock (session.Gate) session.Completing = false;
                return result;
            }
        }

        public bool Cancel(string id)
        {
            Session session = Find(id);
            if (session == null) return false;
            lock (session.Gate)
            {
                if (session.Completing) return false;
            }
            lock (gate) sessions.Remove(id);
            Leave(id);
            DeleteSessionDir(id);
            return true;
        }

        private void VerifyInBackground(string dirId, string rel, string fullPath, string hash)
        {
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try
                {
                    string actual;
                    using (FileStream input = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1024 * 1024))
                    {
                        actual = Hashing.Sha256Stream(input);
                    }
                    if (string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                        ClearAlert(dirId, rel);
                    else
                        SetAlert(dirId, rel, "校验失败");
                }
                catch
                {
                }
            });
        }

        private static string AlertKey(string dirId, string rel)
        {
            return (dirId ?? "") + "|" + FileCatalog.NormalizeRel(rel);
        }

        public string GetAlert(string dirId, string rel)
        {
            lock (alertGate)
            {
                string value;
                return alerts.TryGetValue(AlertKey(dirId, rel), out value) ? value : null;
            }
        }

        public void SetAlert(string dirId, string rel, string message)
        {
            lock (alertGate)
            {
                alerts[AlertKey(dirId, rel)] = message;
            }
        }

        public void ClearAlert(string dirId, string rel)
        {
            lock (alertGate)
            {
                alerts.Remove(AlertKey(dirId, rel));
            }
        }

        public void RenameAlert(string dirId, string oldRel, string newRel)
        {
            lock (alertGate)
            {
                string oldKey = AlertKey(dirId, oldRel);
                string value;
                if (!alerts.TryGetValue(oldKey, out value)) return;
                alerts.Remove(oldKey);
                alerts[AlertKey(dirId, newRel)] = value;
            }
        }

        public void RenameAlertFolder(string dirId, string oldRel, string newRel)
        {
            lock (alertGate)
            {
                string prefix = (dirId ?? "") + "|" + FileCatalog.NormalizeRel(oldRel) + "\\";
                string replacement = (dirId ?? "") + "|" + FileCatalog.NormalizeRel(newRel) + "\\";
                List<string> keys = new List<string>(alerts.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string value = alerts[key];
                    alerts.Remove(key);
                    alerts[replacement + key.Substring(prefix.Length)] = value;
                }
            }
        }

        public void RemoveAlertFolder(string dirId, string rel)
        {
            lock (alertGate)
            {
                string prefix = (dirId ?? "") + "|" + FileCatalog.NormalizeRel(rel) + "\\";
                List<string> keys = new List<string>(alerts.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    if (keys[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        alerts.Remove(keys[i]);
                }
            }
        }

        public void UpdateDirProgress(List<DirProgressEntry> entries)
        {
            lock (progressGate)
            {
                dirProgress.Clear();
                progressUpdated.Clear();
                if (entries == null) return;
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < entries.Count; i++)
                {
                    DirProgressEntry entry = entries[i];
                    if (entry == null || string.IsNullOrEmpty(entry.DirId)) continue;
                    if (entry.Active <= 0 || entry.TotalBytes <= 0) continue;
                    DirProgress state = new DirProgress();
                    state.DirId = entry.DirId;
                    state.Active = entry.Active;
                    state.Saving = entry.Saving < 0 ? 0 : entry.Saving;
                    state.TotalBytes = entry.TotalBytes;
                    long done = entry.DoneBytes;
                    if (done < 0) done = 0;
                    if (done > entry.TotalBytes) done = entry.TotalBytes;
                    state.DoneBytes = done;
                    dirProgress[entry.DirId] = state;
                    progressUpdated[entry.DirId] = now;
                }
            }
        }

        public List<DirProgress> GetDirProgress()
        {
            List<DirProgress> result = new List<DirProgress>();
            lock (progressGate)
            {
                DateTime now = DateTime.UtcNow;
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, DirProgress> pair in dirProgress)
                {
                    DateTime updated;
                    if (progressUpdated.TryGetValue(pair.Key, out updated) &&
                        (now - updated).TotalSeconds >= 15)
                    {
                        stale.Add(pair.Key);
                        continue;
                    }
                    DirProgress copy = new DirProgress();
                    copy.DirId = pair.Value.DirId;
                    copy.Active = pair.Value.Active;
                    copy.Saving = pair.Value.Saving;
                    copy.DoneBytes = pair.Value.DoneBytes;
                    copy.TotalBytes = pair.Value.TotalBytes;
                    result.Add(copy);
                }
                for (int i = 0; i < stale.Count; i++)
                {
                    dirProgress.Remove(stale[i]);
                    progressUpdated.Remove(stale[i]);
                }
            }
            return result;
        }

        public long CacheBytes()
        {
            return DirBytes(AppPaths.UploadsDir) + DirBytes(AppPaths.TmpDir);
        }

        private static long DirBytes(string dir)
        {
            long total = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    try { total += new FileInfo(files[i]).Length; }
                    catch { }
                }
            }
            catch { }
            return total;
        }

        private static void DeleteContents(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                string[] subdirs = Directory.GetDirectories(dir);
                for (int i = 0; i < subdirs.Length; i++)
                {
                    try { Directory.Delete(subdirs[i], true); }
                    catch { }
                }
                string[] files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); }
                    catch { }
                }
            }
            catch { }
        }

        public bool ClearCache(out long cleared)
        {
            cleared = 0;
            lock (activeGate)
            {
                if (active.Count > 0) return false;
            }
            cleared = CacheBytes();
            lock (gate) sessions.Clear();
            DeleteContents(AppPaths.UploadsDir);
            DeleteContents(AppPaths.TmpDir);
            return true;
        }

        public bool HasActive
        {
            get
            {
                List<Session> list = new List<Session>();
                lock (gate)
                {
                    foreach (Session session in sessions.Values) list.Add(session);
                }
                for (int i = 0; i < list.Count; i++)
                {
                    Session session = list[i];
                    lock (session.Gate)
                    {
                        if (session.Received > 0) return true;
                    }
                }
                return false;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.Encoders.Codecs;
using vpxmd;

namespace _801CoursewareUploader
{
    internal sealed class WebServer
    {
        private readonly FileCatalog catalog;
        private readonly UploadManager uploads;
        private readonly WebAuth auth;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly Dictionary<string, byte[]> assetCache =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        private sealed class FolderPackage
        {
            public string Token;
            public string Source;
            public string Name;
            public string TempPath;
            public string State;
            public long Size;
            public long Total;
            public long Done;
            public int Skipped;
            public string Error;
            public DateTime Created;
            public volatile bool Cancelled;
        }

        private readonly Dictionary<string, FolderPackage> packages =
            new Dictionary<string, FolderPackage>(StringComparer.Ordinal);
        private readonly object packagesLock = new object();

        private sealed class RtcSession
        {
            public string Id;
            public int Index;
            public RTCPeerConnection Peer;
            public Vp8Codec Encoder;
            public VideoFormat Codec;
            public Thread Capture;
            public volatile bool Running;
            public volatile bool ForceKey;
            public int Started;
            public Size EncodeSize;
        }

        private readonly Dictionary<string, RtcSession> rtcSessions =
            new Dictionary<string, RtcSession>(StringComparer.Ordinal);
        private readonly object rtcLock = new object();

        private sealed class ScreenTraffic
        {
            public long Bytes;
            public int Frames;
            public int Last;
            public long LastBps;
            public int LastFps;
        }

        private readonly Dictionary<string, ScreenTraffic> screenTraffic =
            new Dictionary<string, ScreenTraffic>(StringComparer.Ordinal);
        private readonly object screenTrafficLock = new object();

        private HttpListener listener;
        private Thread listenThread;
        private volatile bool running;

        public bool LanMode { get; private set; }
        public int Port { get; private set; }
        public string LastError { get; private set; }

        public WebServer(FileCatalog catalog, UploadManager uploads, WebAuth auth)
        {
            this.catalog = catalog;
            this.uploads = uploads;
            this.auth = auth;
            json.MaxJsonLength = int.MaxValue;
            json.RecursionLimit = 512;
        }

        public bool Start(int port)
        {
            Stop();
            CleanTempZips();
            Port = port;
            LastError = null;

            bool registered = LanSetup.IsPortRegistered(port);
            if (registered && TryStart("http://+:" + port.ToString() + "/", true)) return true;
            if (TryStart("http://localhost:" + port.ToString() + "/", false)) return true;

            LastError = "端口 " + port.ToString() + " 无法使用";
            return false;
        }

        private bool TryStart(string prefix, bool lan)
        {
            HttpListener candidate = new HttpListener();
            try
            {
                candidate.Prefixes.Add(prefix);
                candidate.Start();
                listener = candidate;
                LanMode = lan;
                running = true;
                listenThread = new Thread(ListenLoop);
                listenThread.IsBackground = true;
                listenThread.Name = "WebServer";
                listenThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                try { candidate.Close(); } catch { }
                LastError = ex.Message;
                return false;
            }
        }

        public void Stop()
        {
            running = false;
            HttpListener current = listener;
            listener = null;
            if (current != null)
            {
                try { current.Stop(); } catch { }
                try { current.Close(); } catch { }
            }
            listenThread = null;
            ClearPackages();
            StopAllRtc();
        }

        private void ListenLoop()
        {
            HttpListener current = listener;
            while (running && current != null)
            {
                HttpListenerContext context;
                try
                {
                    context = current.GetContext();
                }
                catch
                {
                    break;
                }
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    Handle((HttpListenerContext)state);
                }, context);
            }
        }

        private void Handle(HttpListenerContext context)
        {
            try
            {
                Route(context);
            }
            catch (Exception ex)
            {
                try { WriteJson(context, 500, new { error = ex.Message }); }
                catch { }
            }
            finally
            {
                try { context.Response.OutputStream.Close(); }
                catch { }
            }
        }

        private void Route(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            string path = request.Url.AbsolutePath;
            string method = request.HttpMethod.ToUpperInvariant();

            if (string.Equals(path, "/manual.html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/manual", StringComparison.OrdinalIgnoreCase))
            {
                ServeAsset(context, "web/manual.html");
                return;
            }

            if (string.Equals(path, "/api/login", StringComparison.OrdinalIgnoreCase) &&
                method == "POST")
            {
                HandleLogin(context);
                return;
            }

            bool authed = auth.IsValid(GetCookie(request, "courseware_session"));
            if (!authed)
            {
                if (string.Equals(path, "/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    ServeAsset(context, "web/login.html");
                    return;
                }
                if (string.Equals(path, "/login.html", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/vendor/", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryServeStatic(context, path)) return;
                }
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(context, 401, new { error = "auth" });
                    return;
                }
                ServeAsset(context, "web/login.html");
                return;
            }

            if (string.Equals(path, "/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase))
            {
                ServeAsset(context, "web/index.html");
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                RouteApi(context, path, method);
                return;
            }

            if (TryServeStatic(context, path)) return;
            WriteJson(context, 404, new { error = "未找到" });
        }

        private void RouteApi(HttpListenerContext context, string path, string method)
        {
            if (string.Equals(path, "/api/status", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleStatus(context);
                return;
            }
            if (string.Equals(path, "/api/qrcode", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleQrCode(context);
                return;
            }
            if (string.Equals(path, "/api/tree", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleTree(context);
                return;
            }
            if (string.Equals(path, "/api/list", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleList(context);
                return;
            }
            if (string.Equals(path, "/api/roster", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleRosterGet(context);
                return;
            }
            if (string.Equals(path, "/api/roster", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleRosterSave(context);
                return;
            }
            if (string.Equals(path, "/api/upload/precheck", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleUploadPrecheck(context);
                return;
            }
            if (string.Equals(path, "/api/upload/init", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleUploadInit(context);
                return;
            }
            if (string.Equals(path, "/api/upload/chunk", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleUploadChunk(context);
                return;
            }
            if (string.Equals(path, "/api/upload/complete", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleUploadComplete(context);
                return;
            }
            if (string.Equals(path, "/api/upload/cancel", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleUploadCancel(context);
                return;
            }
            if (string.Equals(path, "/api/upload/progress", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleUploadProgress(context);
                return;
            }
            if (string.Equals(path, "/api/screen/info", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenInfo(context);
                return;
            }
            if (string.Equals(path, "/api/screen/elevate", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenElevate(context);
                return;
            }
            if (string.Equals(path, "/api/screen/quality", StringComparison.OrdinalIgnoreCase) &&
                (method == "GET" || method == "POST"))
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenQuality(context);
                return;
            }
            if (string.Equals(path, "/api/screen/options", StringComparison.OrdinalIgnoreCase) &&
                (method == "GET" || method == "POST"))
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenOptions(context);
                return;
            }
            if (string.Equals(path, "/api/screen/stats", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenStats(context);
                return;
            }
            if (string.Equals(path, "/api/screen/stream", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenStream(context);
                return;
            }
            if (string.Equals(path, "/api/screen/live", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenLive(context);
                return;
            }
            if (string.Equals(path, "/api/rtc/offer", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleRtcOffer(context);
                return;
            }
            if (string.Equals(path, "/api/rtc/close", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleRtcClose(context);
                return;
            }
            if (string.Equals(path, "/api/screen/click", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenClick(context);
                return;
            }
            if (string.Equals(path, "/api/screen/input", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenInput(context);
                return;
            }
            if (string.Equals(path, "/api/screen/pointer", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                if (!ScreenEnabled)
                {
                    WriteJson(context, 403, new { error = "屏幕控制已关闭" });
                    return;
                }
                HandleScreenPointer(context);
                return;
            }
            if (string.Equals(path, "/api/file", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleFile(context);
                return;
            }
            if (string.Equals(path, "/api/file/delete", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleDelete(context);
                return;
            }
            if (string.Equals(path, "/api/file/rename", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleRename(context);
                return;
            }
            if (string.Equals(path, "/api/file/open", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleOpen(context);
                return;
            }
            if (string.Equals(path, "/api/folder/prepare", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleFolderPrepare(context);
                return;
            }
            if (string.Equals(path, "/api/folder/status", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleFolderStatus(context);
                return;
            }
            if (string.Equals(path, "/api/folder/cancel", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleFolderCancel(context);
                return;
            }
            if (string.Equals(path, "/api/folder/download", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleFolderDownload(context);
                return;
            }
            if (string.Equals(path, "/api/folder/create", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleCreateFolder(context);
                return;
            }
            if (string.Equals(path, "/api/folder/rename", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleRenameFolder(context);
                return;
            }
            if (string.Equals(path, "/api/folder/delete", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                HandleDeleteFolder(context);
                return;
            }
            WriteJson(context, 404, new { error = "未找到" });
        }

        private void HandleLogin(HttpListenerContext context)
        {
            LoginRequest request = ParseJson<LoginRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            if (!auth.Verify(request.Password))
            {
                WriteJson(context, 401, new { error = "密码错误" });
                return;
            }
            if (auth.Required)
            {
                string token = auth.IssueToken();
                context.Response.AppendHeader("Set-Cookie",
                    "courseware_session=" + token + "; Path=/; Max-Age=315360000; HttpOnly; SameSite=Lax");
            }
            WriteJson(context, 200, new { ok = true });
        }

        private static string Query(HttpListenerContext context, string key)
        {
            string raw = context.Request.Url.Query;
            if (string.IsNullOrEmpty(raw)) return null;
            string text = raw[0] == '?' ? raw.Substring(1) : raw;
            string[] parts = text.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string name = eq < 0 ? part : part.Substring(0, eq);
                if (!string.Equals(Uri.UnescapeDataString(name.Replace('+', ' ')), key,
                    StringComparison.OrdinalIgnoreCase)) continue;
                if (eq < 0) return "";
                return Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' '));
            }
            return null;
        }

        private void HandleStatus(HttpListenerContext context)
        {
            List<object> dirs = new List<object>();
            List<FileCatalog.DirView> entries = catalog.GetSnapshot();
            for (int i = 0; i < entries.Count; i++)
            {
                FileCatalog.DirView entry = entries[i];
                dirs.Add(new
                {
                    id = entry.Id,
                    name = entry.Name,
                    path = entry.Path,
                    count = entry.Count,
                    indexing = entry.Indexing,
                    indexDone = entry.IndexDone,
                    indexTotal = entry.IndexTotal
                });
            }
            WriteJson(context, 200, new
            {
                ok = true,
                authRequired = auth.Required,
                dirs = dirs,
                concurrent = ConfigStore.Instance.Read(delegate(AppConfig c) { return c.MaxConcurrent; }),
                lan = LanMode,
                port = Port,
                address = SiteAddress(),
                version = VersionText(),
                device = DeviceName(),
                ip = LanSetup.GetLanIp(),
                screen = ScreenEnabled,
                disks = DiskSpace(entries)
            });
        }

        private bool ScreenEnabled
        {
            get { return ConfigStore.Instance.Read(delegate(AppConfig c) { return c.ScreenControlEnabled; }); }
        }

        private string SiteAddress()
        {
            return LanMode ? LanSetup.GetLanAddress(Port)
                : "http://localhost:" + Port.ToString() + "/";
        }

        private static string DeviceName()
        {
            try { return Environment.MachineName; }
            catch { return "本机"; }
        }

        private static string VersionText()
        {
            try
            {
                Version version = Assembly.GetExecutingAssembly().GetName().Version;
                if (version == null) return "";
                return version.Major + "." + version.Minor + "." + version.Build;
            }
            catch
            {
                return "";
            }
        }

        private static List<object> DiskSpace(List<FileCatalog.DirView> entries)
        {
            List<object> result = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                try
                {
                    string root = Path.GetPathRoot(Path.GetFullPath(entries[i].Path));
                    if (string.IsNullOrEmpty(root) || seen.Contains(root)) continue;
                    long free;
                    long total;
                    try
                    {
                        DriveInfo drive = new DriveInfo(root);
                        free = drive.AvailableFreeSpace;
                        total = drive.TotalSize;
                    }
                    catch { continue; }
                    seen.Add(root);
                    string trimmed = root.TrimEnd('\\', '/');
                    string name = trimmed.Length == 2 && trimmed[1] == ':'
                        ? trimmed.Substring(0, 1).ToUpperInvariant() + "盘"
                        : trimmed;
                    result.Add(new { name = name, free = free, total = total });
                }
                catch { }
            }
            return result;
        }

        private static long FreeDiskSpace(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return 0;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return 0;
            }
        }

        private void HandleQrCode(HttpListenerContext context)
        {
            bool[,] matrix = QrCode.Encode(SiteAddress());
            if (matrix == null)
            {
                WriteJson(context, 500, new { error = "地址过长" });
                return;
            }

            const int quiet = 4;
            const int cell = 8;
            int modules = matrix.GetLength(0) + quiet * 2;
            int size = modules * cell;

            byte[] data;
            using (Bitmap bitmap = new Bitmap(size, size))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                using (SolidBrush brush = new SolidBrush(Color.Black))
                {
                    for (int y = 0; y < matrix.GetLength(0); y++)
                    {
                        for (int x = 0; x < matrix.GetLength(1); x++)
                        {
                            if (!matrix[y, x]) continue;
                            graphics.FillRectangle(brush, (x + quiet) * cell,
                                (y + quiet) * cell, cell, cell);
                        }
                    }
                }
                using (MemoryStream memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Png);
                    data = memory.ToArray();
                }
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "image/png";
            context.Response.AddHeader("Cache-Control", "no-store");
            context.Response.ContentLength64 = data.Length;
            context.Response.OutputStream.Write(data, 0, data.Length);
        }

        private void HandleTree(HttpListenerContext context)
        {
            string dirId = Query(context, "dirId");
            FileCatalog.TreeNodeSnapshot tree = catalog.GetTree(dirId);
            if (tree == null)
            {
                WriteJson(context, 404, new { error = "目录不存在" });
                return;
            }
            WriteJson(context, 200, new { ok = true, tree = MapTree(tree) });
        }

        private static object MapTree(FileCatalog.TreeNodeSnapshot node)
        {
            List<object> children = new List<object>();
            for (int i = 0; i < node.Children.Count; i++) children.Add(MapTree(node.Children[i]));
            return new { name = node.Name, path = node.Path, children = children };
        }

        private void HandleList(HttpListenerContext context)
        {
            string dirId = Query(context, "dirId");
            string folder = Query(context, "folder");
            int page = ParseInt(Query(context, "page"), 1);
            int size = ParseInt(Query(context, "size"), 100);
            if (size < 1) size = 1;
            if (size > 200) size = 200;
            if (page < 1) page = 1;
            string sort = Query(context, "sort");
            string order = Query(context, "order");
            bool desc = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

            int total;
            string normalizedFolder = FileCatalog.NormalizeRel(folder);
            List<FileCatalog.FolderView> folders = catalog.ListChildFolders(dirId, normalizedFolder);
            List<object> folderItems = new List<object>();
            for (int i = 0; i < folders.Count; i++)
            {
                FileCatalog.FolderView sub = folders[i];
                folderItems.Add(new
                {
                    name = sub.Name,
                    path = sub.RelPath,
                    mtime = sub.Modified == DateTime.MinValue
                        ? "" : sub.Modified.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }
            List<FileCatalog.FileView> files =
                catalog.ListFolder(dirId, folder, page, size, sort, desc, out total);
            List<object> items = new List<object>();
            for (int i = 0; i < files.Count; i++)
            {
                FileCatalog.FileView file = files[i];
                items.Add(new
                {
                    name = file.Name,
                    path = file.RelPath,
                    size = file.Size,
                    mtime = file.Modified.ToString("yyyy-MM-dd HH:mm:ss"),
                    ext = Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant(),
                    alert = uploads.GetAlert(dirId, file.RelPath)
                });
            }
            WriteJson(context, 200, new
            {
                ok = true,
                total = total,
                page = page,
                size = size,
                folders = folderItems,
                files = items
            });
        }

        private static RosterEntry FindRoster(AppConfig config, string dirId, string folder)
        {
            if (config.Rosters == null || string.IsNullOrEmpty(dirId)) return null;
            for (int i = 0; i < config.Rosters.Count; i++)
            {
                RosterEntry entry = config.Rosters[i];
                if (entry == null) continue;
                if (!string.Equals(entry.DirId, dirId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(FileCatalog.NormalizeRel(entry.Folder), folder,
                    StringComparison.OrdinalIgnoreCase)) continue;
                return entry;
            }
            return null;
        }

        private void HandleRosterGet(HttpListenerContext context)
        {
            string dirId = Query(context, "dirId");
            string folder = FileCatalog.NormalizeRel(Query(context, "folder"));
            RosterEntry entry = ConfigStore.Instance.Read(delegate(AppConfig c)
            {
                RosterEntry found = FindRoster(c, dirId, folder);
                if (found == null) return null;
                RosterEntry copy = new RosterEntry();
                copy.Enabled = found.Enabled;
                if (found.Names != null) copy.Names = new List<string>(found.Names);
                return copy;
            });

            bool enabled = entry != null && entry.Enabled && entry.Names != null && entry.Names.Count > 0;
            List<string> names = entry != null && entry.Names != null
                ? new List<string>(entry.Names) : new List<string>();
            List<object> stats = new List<object>();
            if (enabled)
            {
                bool rebuilt = false;
                for (int i = 0; i < names.Count; i++)
                {
                    string rel = folder.Length == 0 ? names[i] : folder + "\\" + names[i];
                    string fullPerson;
                    if (!catalog.TryResolve(dirId, rel, out fullPerson) || Directory.Exists(fullPerson))
                        continue;
                    string error;
                    if (catalog.EnsureFolder(dirId, folder, names[i], out error)) rebuilt = true;
                }
                if (rebuilt) catalog.InvalidateTree(dirId);

                List<FileCatalog.RosterStatusView> views = catalog.RosterStatuses(dirId, folder, names);
                for (int i = 0; i < views.Count; i++)
                {
                    FileCatalog.RosterStatusView view = views[i];
                    stats.Add(new
                    {
                        name = view.Name,
                        first = view.FirstFile == null ? "" : view.FirstFile,
                        count = view.Count,
                        folders = view.FolderCount,
                        bytes = view.Bytes,
                        mtime = view.Modified == DateTime.MinValue
                            ? "" : view.Modified.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                }
            }

            WriteJson(context, 200, new
            {
                ok = true,
                enabled = enabled,
                names = names,
                stats = stats
            });
        }

        private void HandleRosterSave(HttpListenerContext context)
        {
            RosterSaveRequest request = ParseJson<RosterSaveRequest>(context.Request);
            if (request == null || string.IsNullOrEmpty(request.DirId))
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            string folder = FileCatalog.NormalizeRel(request.Folder);
            string fullFolder;
            if (!catalog.TryResolve(request.DirId, folder, out fullFolder) ||
                !Directory.Exists(fullFolder))
            {
                WriteJson(context, 200, new { ok = false, error = "目标文件夹不存在" });
                return;
            }

            List<string> names = new List<string>();
            List<object> failed = new List<object>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> raw = request.Names != null ? request.Names : new List<string>();
            for (int i = 0; i < raw.Count; i++)
            {
                string name = raw[i] == null ? "" : raw[i].Replace('　', ' ').Trim();
                if (name.Length == 0) continue;
                if (!catalog.IsSafeFileName(name))
                {
                    failed.Add(new { name = name, error = "文件夹名无效" });
                    continue;
                }
                if (!seen.Add(name)) continue;
                string error;
                if (catalog.EnsureFolder(request.DirId, folder, name, out error))
                {
                    names.Add(name);
                }
                else
                {
                    seen.Remove(name);
                    failed.Add(new { name = name, error = error });
                }
            }

            if (request.Enabled && names.Count == 0)
            {
                WriteJson(context, 200, new { ok = false, error = "名字不能为空", failed = failed });
                return;
            }

            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                if (c.Rosters == null) c.Rosters = new List<RosterEntry>();
                for (int i = c.Rosters.Count - 1; i >= 0; i--)
                {
                    RosterEntry entry = c.Rosters[i];
                    if (entry == null)
                    {
                        c.Rosters.RemoveAt(i);
                        continue;
                    }
                    if (!string.Equals(entry.DirId, request.DirId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.Equals(FileCatalog.NormalizeRel(entry.Folder), folder,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    c.Rosters.RemoveAt(i);
                }
                if (names.Count > 0)
                {
                    RosterEntry entry = new RosterEntry();
                    entry.DirId = request.DirId;
                    entry.Folder = folder;
                    entry.Names = new List<string>(names);
                    entry.Enabled = request.Enabled;
                    c.Rosters.Add(entry);
                }
            });
            catalog.InvalidateTree(request.DirId);

            WriteJson(context, 200, new
            {
                ok = true,
                enabled = request.Enabled && names.Count > 0,
                names = names,
                failed = failed
            });
        }

        private void HandleUploadPrecheck(HttpListenerContext context)
        {
            UploadPrecheckRequest request = ParseJson<UploadPrecheckRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            long nameSize = 0;
            DateTime nameTime = DateTime.MinValue;
            bool nameConflict = !string.IsNullOrEmpty(request.Name) &&
                catalog.TryGetSameName(request.DirId, request.Folder, request.Name,
                    out nameSize, out nameTime);
            bool required = catalog.HasSameSizeFile(request.DirId, request.Folder, request.Size,
                nameConflict ? request.Name : null);
            WriteJson(context, 200, new
            {
                ok = true,
                nameConflict = nameConflict,
                nameSize = nameSize,
                nameTime = nameConflict ? nameTime.ToString("yyyy-MM-dd HH:mm:ss") : "",
                hashRequired = required
            });
        }

        private void HandleUploadInit(HttpListenerContext context)
        {
            UploadInitRequest request = ParseJson<UploadInitRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            UploadManager.InitRequest input = new UploadManager.InitRequest();
            input.DirId = request.DirId;
            input.Folder = request.Folder;
            input.Name = request.Name;
            input.Size = request.Size;
            input.Hash = request.Hash;
            input.Action = request.Action;
            UploadManager.InitResult result = uploads.Init(input);
            WriteJson(context, 200, new
            {
                status = result.Status,
                uploadId = result.UploadId,
                chunkSize = result.ChunkSize,
                uploaded = result.Uploaded,
                targetName = result.TargetName,
                existingPath = result.ExistingPath,
                existingSize = result.ExistingSize,
                existingTime = result.ExistingTime,
                error = result.Error
            });
        }

        private void HandleUploadChunk(HttpListenerContext context)
        {
            string id = Query(context, "id");
            int index = ParseInt(Query(context, "index"), -1);
            if (string.IsNullOrEmpty(id) || index < 0)
            {
                WriteJson(context, 400, new { error = "参数无效" });
                return;
            }

            long length = context.Request.ContentLength64;
            string error;
            if (length > 0)
            {
                error = uploads.WriteChunk(id, index, context.Request.InputStream, length);
            }
            else
            {
                byte[] body;
                try
                {
                    body = ReadBody(context.Request, UploadManager.ChunkSize + 1024);
                }
                catch (Exception ex)
                {
                    WriteJson(context, 400, new { error = ex.Message });
                    return;
                }
                using (MemoryStream stream = new MemoryStream(body))
                {
                    error = uploads.WriteChunk(id, index, stream, body.Length);
                }
            }

            if (error == null)
            {
                WriteJson(context, 200, new { ok = true });
                return;
            }
            if (error == "busy")
            {
                WriteJson(context, 429, new { ok = false, error = "busy" });
                return;
            }
            WriteJson(context, 400, new { ok = false, error = error });
        }

        private void HandleUploadComplete(HttpListenerContext context)
        {
            IdRequest request = ParseJson<IdRequest>(context.Request);
            if (request == null || string.IsNullOrEmpty(request.Id))
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            UploadManager.CompleteResult result = uploads.Complete(request.Id);
            WriteJson(context, 200, new
            {
                ok = result.Ok,
                error = result.Error,
                savedName = result.SavedName,
                relPath = result.RelPath,
                dirId = result.DirId
            });
        }

        private void HandleUploadCancel(HttpListenerContext context)
        {
            IdRequest request = ParseJson<IdRequest>(context.Request);
            if (request == null || string.IsNullOrEmpty(request.Id))
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            bool ok = uploads.Cancel(request.Id);
            WriteJson(context, 200, new { ok = ok });
        }

        private void HandleUploadProgress(HttpListenerContext context)
        {
            UploadProgressRequest request = ParseJson<UploadProgressRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            uploads.UpdateDirProgress(request.Dirs);
            WriteJson(context, 200, new { ok = true });
        }

        private void HandleFolderPrepare(HttpListenerContext context)
        {
            string dirId = Query(context, "dirId");
            string rel = Query(context, "path");
            string full;
            if (!catalog.TryResolve(dirId, rel, out full) || !Directory.Exists(full))
            {
                WriteJson(context, 404, new { error = "文件夹不存在" });
                return;
            }

            string name = Path.GetFileName(full.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = "root";
            string source = Path.GetFullPath(full.TrimEnd('\\', '/'));

            ClearExpiredPackages();
            string existing = null;
            string existingName = null;
            lock (packagesLock)
            {
                foreach (KeyValuePair<string, FolderPackage> pair in packages)
                {
                    if (string.Equals(pair.Value.Source, source, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(pair.Value.State, "error", StringComparison.Ordinal) &&
                        !string.Equals(pair.Value.State, "canceled", StringComparison.Ordinal))
                    {
                        existing = pair.Key;
                        existingName = pair.Value.Name;
                        break;
                    }
                }
            }
            if (existing != null)
            {
                WriteJson(context, 200, new { token = existing, name = existingName });
                return;
            }

            string token = Guid.NewGuid().ToString("N");
            FolderPackage item = new FolderPackage
            {
                Token = token,
                Source = source,
                Name = name,
                State = "preparing",
                Created = DateTime.UtcNow
            };
            lock (packagesLock) packages[token] = item;

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                string temp = Path.Combine(AppPaths.TmpDir, token + ".zip");
                try
                {
                    Directory.CreateDirectory(AppPaths.TmpDir);
                    List<string> files = new List<string>();
                    long total = 0;
                    CollectFiles(source, files, ref total);
                    lock (packagesLock) item.Total = total;
                    long free = FreeDiskSpace(AppPaths.TmpDir); // 打包临时文件写在程序目录所在磁盘
                    if (free > 0 && total > free)
                    {
                        lock (packagesLock)
                        {
                            item.State = "error";
                            item.Error = "磁盘空间不足，无法打包下载";
                        }
                        return;
                    }
                    int skipped = BuildZip(source, files, temp, item);
                    FileInfo info = new FileInfo(temp);
                    bool discard = false;
                    lock (packagesLock)
                    {
                        if (!running || item.Cancelled)
                        {
                            item.State = "canceled";
                            packages.Remove(token);
                            discard = true;
                        }
                        else
                        {
                            item.TempPath = temp;
                            item.Size = info.Length;
                            item.Skipped = skipped;
                            item.State = "ready";
                        }
                    }
                    if (discard)
                    {
                        try { File.Delete(temp); } catch { }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    try { File.Delete(temp); } catch { }
                    lock (packagesLock)
                    {
                        if (!item.Cancelled)
                        {
                            item.State = "error";
                            item.Error = "打包失败：" + ex.Message;
                        }
                    }
                }
            }, null);

            WriteJson(context, 200, new { token = token, name = name });
        }

        private void HandleFolderStatus(HttpListenerContext context)
        {
            string token = Query(context, "token");
            FolderPackage item = null;
            lock (packagesLock)
            {
                if (!string.IsNullOrEmpty(token)) packages.TryGetValue(token, out item);
            }
            if (item == null)
            {
                WriteJson(context, 404, new { error = "打包任务不存在" });
                return;
            }
            string state;
            long size;
            long total;
            long done;
            int skipped;
            string name;
            string error;
            lock (packagesLock)
            {
                state = item.State;
                size = item.Size;
                total = item.Total;
                done = item.Done;
                skipped = item.Skipped;
                name = item.Name;
                error = item.Error;
            }
            WriteJson(context, 200, new
            {
                state = state,
                size = size,
                total = total,
                done = done,
                skipped = skipped,
                name = name,
                error = error
            });
        }

        private void HandleFolderCancel(HttpListenerContext context)
        {
            IdRequest request = ParseJson<IdRequest>(context.Request);
            if (request == null || string.IsNullOrEmpty(request.Id))
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }

            bool found = false;
            string temp = null;
            lock (packagesLock)
            {
                FolderPackage item;
                if (packages.TryGetValue(request.Id, out item))
                {
                    found = true;
                    item.Cancelled = true;
                    if (string.Equals(item.State, "ready", StringComparison.Ordinal))
                    {
                        temp = item.TempPath;
                        item.State = "canceled";
                        packages.Remove(request.Id);
                    }
                }
            }
            if (!string.IsNullOrEmpty(temp))
            {
                try { File.Delete(temp); } catch { }
            }
            WriteJson(context, 200, new { ok = found });
        }

        private void HandleFolderDownload(HttpListenerContext context)
        {
            string token = Query(context, "token");
            FolderPackage item = null;
            lock (packagesLock)
            {
                if (!string.IsNullOrEmpty(token) && packages.TryGetValue(token, out item) &&
                    item.State == "ready")
                {
                    packages.Remove(token);
                }
                else
                {
                    item = null;
                }
            }
            if (item == null)
            {
                WriteJson(context, 404, new { error = "打包任务不存在" });
                return;
            }

            string temp = item.TempPath;
            try
            {
                string fileName = item.Name + ".zip";
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/zip";
                context.Response.AddHeader("Content-Disposition",
                    "attachment; filename=\"" + SanitizeAscii(fileName) + "\"; filename*=UTF-8''" +
                    Uri.EscapeDataString(fileName));
                context.Response.ContentLength64 = item.Size;
                using (FileStream stream = new FileStream(temp, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[128 * 1024];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        context.Response.OutputStream.Write(buffer, 0, read);
                }
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        private static void CollectFiles(string dir, List<string> files, ref long total)
        {
            try
            {
                string[] subs = Directory.GetDirectories(dir);
                for (int i = 0; i < subs.Length; i++) CollectFiles(subs[i], files, ref total);
            }
            catch { }

            string[] names;
            try { names = Directory.GetFiles(dir); }
            catch { return; }

            for (int i = 0; i < names.Length; i++)
            {
                files.Add(names[i]);
                try { total += new FileInfo(names[i]).Length; } catch { }
            }
        }

        private int BuildZip(string source, List<string> files, string temp, FolderPackage item)
        {
            int skipped = 0;
            using (FileStream file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024))
            using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                byte[] buffer = new byte[128 * 1024];
                for (int i = 0; i < files.Count; i++)
                {
                    if (item.Cancelled) break;
                    string path = files[i];
                    string entryName = path.Substring(source.Length).TrimStart('\\', '/')
                        .Replace('\\', '/');
                    try
                    {
                        using (FileStream input = new FileStream(path, FileMode.Open,
                            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                            128 * 1024, FileOptions.SequentialScan))
                        {
                            ZipArchiveEntry entry = archive.CreateEntry(entryName,
                                CompressionLevel.Fastest);
                            using (Stream output = entry.Open())
                            {
                                int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    if (item.Cancelled) break;
                                    output.Write(buffer, 0, read);
                                    lock (packagesLock) item.Done += read;
                                }
                            }
                        }
                    }
                    catch { skipped++; }
                }
            }
            return skipped;
        }

        private void ClearExpiredPackages()
        {
            List<FolderPackage> stale = new List<FolderPackage>();
            lock (packagesLock)
            {
                List<string> keys = new List<string>();
                foreach (KeyValuePair<string, FolderPackage> pair in packages)
                {
                    if (DateTime.UtcNow - pair.Value.Created > TimeSpan.FromMinutes(30))
                        keys.Add(pair.Key);
                }
                for (int i = 0; i < keys.Count; i++)
                {
                    stale.Add(packages[keys[i]]);
                    packages.Remove(keys[i]);
                }
            }
            for (int i = 0; i < stale.Count; i++)
            {
                string path = stale[i].TempPath;
                if (!string.IsNullOrEmpty(path))
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }

        private static void CleanTempZips()
        {
            try
            {
                if (!Directory.Exists(AppPaths.TmpDir)) return;
                string[] files = Directory.GetFiles(AppPaths.TmpDir, "*.zip");
                for (int i = 0; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        private void ClearPackages()
        {
            List<FolderPackage> all = new List<FolderPackage>();
            lock (packagesLock)
            {
                foreach (FolderPackage item in packages.Values) all.Add(item);
                packages.Clear();
            }
            for (int i = 0; i < all.Count; i++)
            {
                string path = all[i].TempPath;
                if (!string.IsNullOrEmpty(path))
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }

        private void HandleScreenInfo(HttpListenerContext context)
        {
            System.Windows.Forms.Screen[] screens = ScreenControl.Screens;
            int primary = ScreenControl.PrimaryIndex();
            List<object> list = new List<object>(screens.Length);
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle bounds = screens[i].Bounds;
                list.Add(new
                {
                    id = i,
                    name = "显示器 " + (i + 1).ToString(),
                    left = bounds.Left,
                    top = bounds.Top,
                    width = bounds.Width,
                    height = bounds.Height,
                    primary = screens[i].Primary
                });
            }
            WriteJson(context, 200, new
            {
                primary = primary,
                elevated = Native.IsElevated,
                quality = ScreenControl.JpegQuality,
                diffWidth = ScreenControl.DiffWidth,
                rtcWidth = ScreenControl.RtcWidth,
                rtcBitrate = ScreenControl.RtcBitrate,
                screens = list
            });
        }

        private void HandleScreenQuality(HttpListenerContext context)
        {
            if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                ScreenQualityRequest request = ParseJson<ScreenQualityRequest>(context.Request);
                if (request != null && request.Quality > 0)
                {
                    ScreenControl.JpegQuality = request.Quality;
                }
            }
            WriteJson(context, 200, new { quality = ScreenControl.JpegQuality });
        }

        private void HandleScreenOptions(HttpListenerContext context)
        {
            if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                ScreenOptionsRequest request = ParseJson<ScreenOptionsRequest>(context.Request);
                if (request != null)
                {
                    if (request.DiffWidth >= 0) ScreenControl.DiffWidth = request.DiffWidth;
                    if (request.RtcWidth >= 0) ScreenControl.RtcWidth = request.RtcWidth;
                    if (request.RtcBitrate > 0) ScreenControl.RtcBitrate = request.RtcBitrate;
                }
            }
            WriteJson(context, 200, new
            {
                diffWidth = ScreenControl.DiffWidth,
                rtcWidth = ScreenControl.RtcWidth,
                rtcBitrate = ScreenControl.RtcBitrate
            });
        }

        private void HandleScreenElevate(HttpListenerContext context)
        {
            if (Native.IsElevated)
            {
                WriteJson(context, 200, new { ok = true, elevated = true });
                return;
            }
            bool started;
            System.Windows.Forms.Form form =
                System.Windows.Forms.Application.OpenForms.Count > 0
                    ? System.Windows.Forms.Application.OpenForms[0]
                    : null;
            if (form == null)
            {
                started = Native.RestartElevated();
            }
            else
            {
                try
                {
                    started = (bool)form.Invoke(new Func<bool>(Native.RestartElevated));
                }
                catch
                {
                    started = false;
                }
            }
            if (!started)
            {
                WriteJson(context, 500, new { ok = false, error = "未能以管理员身份启动" });
                return;
            }

            WriteJson(context, 200, new { ok = true, restarting = true });
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(800);
                System.Windows.Forms.Form target =
                    System.Windows.Forms.Application.OpenForms.Count > 0
                        ? System.Windows.Forms.Application.OpenForms[0]
                        : null;
                if (target == null)
                {
                    System.Windows.Forms.Application.Exit();
                    return;
                }
                try
                {
                    target.BeginInvoke(new System.Windows.Forms.MethodInvoker(delegate
                    {
                        System.Windows.Forms.Application.Exit();
                    }));
                }
                catch
                {
                    System.Windows.Forms.Application.Exit();
                }
            });
        }

        private void RecordScreenBytes(string kind, int index, int bytes, int frames)
        {
            if (bytes <= 0 && frames <= 0) return;
            string key = kind + ":" + index.ToString();
            lock (screenTrafficLock)
            {
                ScreenTraffic stat;
                if (!screenTraffic.TryGetValue(key, out stat))
                {
                    stat = new ScreenTraffic();
                    stat.Last = Environment.TickCount;
                    screenTraffic[key] = stat;
                }
                stat.Bytes += bytes;
                stat.Frames += frames;
            }
        }

        private void HandleScreenStats(HttpListenerContext context)
        {
            string kind = Query(context, "kind");
            if (string.IsNullOrEmpty(kind)) kind = "diff";
            int index;
            if (!int.TryParse(Query(context, "screen"), out index)) index = 0;

            long bps = 0;
            int fps = 0;
            lock (screenTrafficLock)
            {
                ScreenTraffic stat;
                if (screenTraffic.TryGetValue(kind + ":" + index.ToString(), out stat))
                {
                    int now = Environment.TickCount;
                    int elapsed = unchecked(now - stat.Last);
                    if (elapsed < 300)
                    {
                        bps = stat.LastBps;
                        fps = stat.LastFps;
                    }
                    else
                    {
                        if (elapsed <= 2500)
                        {
                            bps = (long)(stat.Bytes * 1000.0 / elapsed);
                            fps = (int)Math.Round(stat.Frames * 1000.0 / elapsed);
                        }
                        stat.Bytes = 0;
                        stat.Frames = 0;
                        stat.Last = now;
                        stat.LastBps = bps;
                        stat.LastFps = fps;
                    }
                }
            }
            WriteJson(context, 200, new { bps = bps, fps = fps });
        }

        private void HandleScreenStream(HttpListenerContext context)
        {
            int index;
            if (!int.TryParse(Query(context, "screen"), out index)) index = -1;
            index = ScreenControl.NormalizeIndex(index);
            if (index < 0)
            {
                WriteJson(context, 404, new { error = "没有可用的显示器" });
                return;
            }
            HttpListenerResponse response = context.Response;
            response.StatusCode = 200;
            response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            response.SendChunked = true;
            response.Headers["Cache-Control"] = "no-store";
            Stream output = response.OutputStream;
            byte[] tail = Encoding.ASCII.GetBytes("\r\n");
            try
            {
                while (running)
                {
                    long start = Environment.TickCount;
                    byte[] jpeg = ScreenControl.Frame(index);
                    if (jpeg == null) break;
                    byte[] header = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n" +
                        "Content-Length: " + jpeg.Length.ToString() + "\r\n\r\n");
                    output.Write(header, 0, header.Length);
                    output.Write(jpeg, 0, jpeg.Length);
                    output.Write(tail, 0, tail.Length);
                    output.Flush();
                    RecordScreenBytes("mjpeg", index, header.Length + jpeg.Length + tail.Length, 1);
                    int spent = unchecked((int)(Environment.TickCount - start));
                    int wait = 20 - spent;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            catch
            {
            }
        }

        [DllImport("ntdll.dll", EntryPoint = "RtlCompareMemory")]
        private static extern UIntPtr RtlCompareMemory(IntPtr source1, IntPtr source2, UIntPtr length);

        private void HandleScreenLive(HttpListenerContext context)
        {
            int index;
            if (!int.TryParse(Query(context, "screen"), out index)) index = -1;
            index = ScreenControl.NormalizeIndex(index);
            if (index < 0)
            {
                WriteJson(context, 404, new { error = "没有可用的显示器" });
                return;
            }
            bool compress = string.Equals(Query(context, "z"), "1", StringComparison.Ordinal);
            WebSocket socket;
            try
            {
                socket = context.AcceptWebSocketAsync(null).GetAwaiter().GetResult().WebSocket;
            }
            catch
            {
                return;
            }
            try
            {
                StreamScreenTiles(socket, index, compress);
            }
            catch
            {
            }
            finally
            {
                try { socket.Dispose(); } catch { }
            }
        }

        private static Task<bool> WatchSocketClose(WebSocket socket)
        {
            return Task.Factory.StartNew(delegate
            {
                byte[] buffer = new byte[64];
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        WebSocketReceiveResult result = socket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), CancellationToken.None).GetAwaiter().GetResult();
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }
                }
                catch
                {
                }
                return true;
            }, TaskCreationOptions.LongRunning);
        }

        private static void WriteU16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static int CompressPayload(byte[] source, int offset, int count, MemoryStream target)
        {
            target.SetLength(0);
            using (DeflateStream deflate = new DeflateStream(target, CompressionLevel.Fastest, true))
            {
                deflate.Write(source, offset, count);
            }
            return (int)target.Length;
        }

        private void StreamScreenTiles(WebSocket socket, int index, bool compress)
        {
            const int TileSize = 32;
            int preset = ScreenControl.DiffWidth;
            Size size = ScreenControl.FrameSizeOf(index, preset);
            if (size.Width <= 0 || size.Height <= 0) return;
            int width = size.Width;
            int height = size.Height;
            int cols = (width + TileSize - 1) / TileSize;
            int rows = (height + TileSize - 1) / TileSize;
            int tileCount = cols * rows;
            byte[] current = new byte[width * height * 4];
            byte[] previous = null;
            byte[] output = new byte[width * height * 4 + 8 + tileCount * 4];
            GCHandle currentHandle = GCHandle.Alloc(current, GCHandleType.Pinned);
            GCHandle previousHandle = default(GCHandle);
            MemoryStream compressedStream = compress ? new MemoryStream(64 * 1024) : null;
            Task<bool> closed = WatchSocketClose(socket);
            try
            {
                while (running && !closed.IsCompleted)
                {
                    int frameWidth;
                    int frameHeight;
                    if (!ScreenControl.CaptureInto(index, current, preset, out frameWidth, out frameHeight)) break;
                    if (frameWidth != width || frameHeight != height) break;

                    int start = Environment.TickCount;
                    IntPtr pCurrent = currentHandle.AddrOfPinnedObject();
                    IntPtr pPrevious = previous == null ? IntPtr.Zero : previousHandle.AddrOfPinnedObject();
                    int used = 8;
                    int dirty = 0;
                    for (int ty = 0; ty < rows; ty++)
                    {
                        for (int tx = 0; tx < cols; tx++)
                        {
                            int x = tx * TileSize;
                            int y = ty * TileSize;
                            int tw = Math.Min(TileSize, width - x);
                            int th = Math.Min(TileSize, height - y);
                            bool same = previous != null;
                            if (same)
                            {
                                int rowBytes = tw * 4;
                                for (int r = 0; r < th; r++)
                                {
                                    int offset = (y + r) * width * 4 + x * 4;
                                    if ((ulong)RtlCompareMemory(IntPtr.Add(pCurrent, offset),
                                        IntPtr.Add(pPrevious, offset), (UIntPtr)(uint)rowBytes) != (ulong)(uint)rowBytes)
                                    {
                                        same = false;
                                        break;
                                    }
                                }
                            }
                            if (same) continue;

                            output[used] = (byte)((ty * cols + tx) & 0xFF);
                            output[used + 1] = (byte)(((ty * cols + tx) >> 8) & 0xFF);
                            output[used + 2] = (byte)tw;
                            output[used + 3] = (byte)th;
                            used += 4;
                            int tileBytes = tw * th * 4;
                            int dst = used;
                            for (int r = 0; r < th; r++)
                            {
                                int src = (y + r) * width * 4 + x * 4;
                                int rowDst = dst + r * tw * 4;
                                for (int c = 0; c < tw * 4; c += 4)
                                {
                                    output[rowDst + c] = current[src + c + 2];
                                    output[rowDst + c + 1] = current[src + c + 1];
                                    output[rowDst + c + 2] = current[src + c];
                                    output[rowDst + c + 3] = 255;
                                }
                            }
                            used += tileBytes;
                            dirty++;
                        }
                    }

                    bool heavy = previous == null || dirty * 2 > tileCount;
                    if (dirty > 0)
                    {
                        output[0] = 1;
                        output[1] = TileSize;
                        WriteU16(output, 2, width);
                        WriteU16(output, 4, height);
                        WriteU16(output, 6, dirty);
                        int length = used;
                        if (compress && used - 8 > 128)
                        {
                            int packed = CompressPayload(output, 8, used - 8, compressedStream);
                            if (packed > 0 && packed + 8 < used - 32)
                            {
                                output[0] = 2;
                                Buffer.BlockCopy(compressedStream.GetBuffer(), 0, output, 8, packed);
                                length = 8 + packed;
                            }
                        }
                        socket.SendAsync(new ArraySegment<byte>(output, 0, length),
                            WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();
                        RecordScreenBytes("diff", index, length, 1);

                        if (previous == null)
                        {
                            previous = new byte[current.Length];
                            previousHandle = GCHandle.Alloc(previous, GCHandleType.Pinned);
                        }
                        Buffer.BlockCopy(current, 0, previous, 0, current.Length);
                    }

                    int spent = unchecked(Environment.TickCount - start);
                    int wait = (heavy ? 50 : 20) - spent;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            finally
            {
                if (previousHandle.IsAllocated) previousHandle.Free();
                currentHandle.Free();
                if (compressedStream != null) compressedStream.Dispose();
            }
        }

        private void HandleScreenClick(HttpListenerContext context)
        {
            ScreenClickRequest request = ParseJson<ScreenClickRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            ScreenControl.Click(request.X, request.Y);
            WriteJson(context, 200, new { ok = true });
        }

        private void HandleScreenPointer(HttpListenerContext context)
        {
            ScreenPointerRequest request = ParseJson<ScreenPointerRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            ApplyPointerCommand(request.Action, request.X, request.Y, request.Delta);
            WriteJson(context, 200, new { ok = true });
        }

        private static void ApplyPointerCommand(string action, int x, int y, int delta)
        {
            switch (action)
            {
                case "d": ScreenControl.PointerDown(x, y); break;
                case "m": ScreenControl.PointerMove(x, y); break;
                case "u": ScreenControl.PointerUp(x, y); break;
                case "r": ScreenControl.RightClick(x, y); break;
                case "c": ScreenControl.Click(x, y); break;
                case "w": ScreenControl.Wheel(x, y, delta); break;
                case "b": ScreenControl.XButton(x, y, 1); break;
                case "f": ScreenControl.XButton(x, y, 2); break;
                case "s": ScreenControl.WinCombo(x); break;
                case "k":
                    if (y == 1) ScreenControl.KeyDown(x);
                    else ScreenControl.KeyUp(x);
                    break;
            }
        }

        private void HandleScreenInput(HttpListenerContext context)
        {
            WebSocket socket;
            try
            {
                socket = context.AcceptWebSocketAsync(null).GetAwaiter().GetResult().WebSocket;
            }
            catch
            {
                return;
            }
            byte[] buffer = new byte[256];
            StringBuilder message = new StringBuilder();
            bool down = false;
            int lastX = 0;
            int lastY = 0;
            HashSet<int> pressedKeys = new HashSet<int>();
            try
            {
                while (running && socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result;
                    try
                    {
                        result = socket.ReceiveAsync(new ArraySegment<byte>(buffer),
                            CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;
                    if (result.Count > 0) message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;
                    string text = message.ToString();
                    message.Length = 0;
                    string[] parts = text.Split(' ');
                    int x;
                    int y;
                    if (parts.Length < 3 || !int.TryParse(parts[1], out x) || !int.TryParse(parts[2], out y))
                    {
                        continue;
                    }
                    string action = parts[0];
                    if (action == "d")
                    {
                        if (down) continue;
                        ScreenControl.PointerDown(x, y);
                        down = true;
                        lastX = x;
                        lastY = y;
                    }
                    else if (action == "m")
                    {
                        if (!down) continue;
                        ScreenControl.PointerMove(x, y);
                        lastX = x;
                        lastY = y;
                    }
                    else if (action == "u")
                    {
                        if (!down) continue;
                        ScreenControl.PointerUp(x, y);
                        down = false;
                    }
                    else if (action == "r")
                    {
                        ScreenControl.RightClick(x, y);
                    }
                    else if (action == "c")
                    {
                        ScreenControl.Click(x, y);
                    }
                    else if (action == "w")
                    {
                        int delta;
                        if (parts.Length >= 4 && int.TryParse(parts[3], out delta))
                            ScreenControl.Wheel(x, y, delta);
                    }
                    else if (action == "b")
                    {
                        ScreenControl.XButton(x, y, 1);
                    }
                    else if (action == "f")
                    {
                        ScreenControl.XButton(x, y, 2);
                    }
                    else if (action == "s")
                    {
                        ScreenControl.WinCombo(x);
                    }
                    else if (action == "k")
                    {
                        if (y == 1)
                        {
                            if (pressedKeys.Add(x)) ScreenControl.KeyDown(x);
                        }
                        else if (pressedKeys.Remove(x))
                        {
                            ScreenControl.KeyUp(x);
                        }
                    }
                }
            }
            finally
            {
                if (down)
                {
                    try { ScreenControl.PointerUp(lastX, lastY); } catch { }
                }
                foreach (int vk in pressedKeys)
                {
                    try { ScreenControl.KeyUp(vk); } catch { }
                }
                try { socket.Dispose(); } catch { }
            }
        }

        private void HandleRtcOffer(HttpListenerContext context)
        {
            RtcOfferRequest request = ParseJson<RtcOfferRequest>(context.Request);
            if (request == null || string.IsNullOrEmpty(request.Sdp))
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            int index = ScreenControl.NormalizeIndex(request.Screen);
            if (index < 0)
            {
                WriteJson(context, 404, new { error = "没有可用的显示器" });
                return;
            }

            RTCPeerConnection peer = null;
            try
            {
                peer = new RTCPeerConnection();
                VpxVideoEncoder formatsSource = new VpxVideoEncoder();
                List<VideoFormat> formats = formatsSource.SupportedFormats;
                ((IDisposable)formatsSource).Dispose();
                MediaStreamTrack track = new MediaStreamTrack(formats, MediaStreamStatusEnum.SendOnly);
                peer.addTrack(track);

                RtcSession session = new RtcSession();
                session.Id = Guid.NewGuid().ToString("N");
                session.Index = index;
                session.Peer = peer;
                session.Codec = PickVideoCodec(formats);
                session.Running = true;
                session.ForceKey = true;
                RtcSession sessionRef = session;

                peer.OnVideoFormatsNegotiated += delegate(List<VideoFormat> negotiated)
                {
                    if (negotiated != null && negotiated.Count > 0) sessionRef.Codec = negotiated[0];
                };

                RTCSessionDescriptionInit offer = new RTCSessionDescriptionInit();
                offer.type = RTCSdpType.offer;
                offer.sdp = request.Sdp;
                if (peer.setRemoteDescription(offer) != SetDescriptionResultEnum.OK)
                {
                    try { peer.close(); } catch { }
                    WriteJson(context, 400, new { error = "SDP 无效" });
                    return;
                }

                RTCSessionDescriptionInit answer = peer.createAnswer(new RTCAnswerOptions());
                peer.setLocalDescription(answer);
                for (int i = 0; i < 30 && peer.iceGatheringState != RTCIceGatheringState.complete; i++)
                {
                    Thread.Sleep(50);
                }
                string sdp = peer.localDescription.sdp.ToString();

                peer.onconnectionstatechange += delegate(RTCPeerConnectionState state)
                {
                    if (state == RTCPeerConnectionState.connected)
                    {
                        sessionRef.ForceKey = true;
                        StartRtcCapture(sessionRef);
                    }
                    else if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
                    {
                        StopRtc(sessionRef.Id);
                    }
                };

                lock (rtcLock) { rtcSessions[session.Id] = session; }
                WriteJson(context, 200, new { id = session.Id, sdp = sdp });
                if (peer.connectionState == RTCPeerConnectionState.connected)
                {
                    StartRtcCapture(session);
                }

                Thread watchdog = new Thread(delegate()
                {
                    for (int i = 0; i < 80; i++)
                    {
                        Thread.Sleep(500);
                        if (!sessionRef.Running || sessionRef.Started != 0) return;
                    }
                    StopRtc(sessionRef.Id);
                });
                watchdog.IsBackground = true;
                watchdog.Start();
            }
            catch (Exception ex)
            {
                if (peer != null) { try { peer.close(); } catch { } }
                WriteJson(context, 500, new { error = ex.Message });
            }
        }

        private void StartRtcCapture(RtcSession session)
        {
            if (session == null || Interlocked.CompareExchange(ref session.Started, 1, 0) != 0) return;
            session.Capture = new Thread(delegate() { RtcCaptureLoop(session); });
            session.Capture.IsBackground = true;
            session.Capture.Start();
        }

        private static VideoFormat PickVideoCodec(List<VideoFormat> formats)
        {
            if (formats != null && formats.Count > 0)
            {
                for (int i = 0; i < formats.Count; i++)
                {
                    if (formats[i].Codec == VideoCodecsEnum.VP8) return formats[i];
                }
                return formats[0];
            }
            return new VideoFormat(VideoCodecsEnum.VP8, 96, 90000, "VP8");
        }

        [DllImport("vpxmd.dll", EntryPoint = "vpx_codec_control_", CallingConvention = CallingConvention.Cdecl)]
        private static extern int vpx_codec_control(IntPtr ctx, int ctrlId, int value);

        private static void TuneVp8(Vp8Codec codec)
        {
            try
            {
                FieldInfo field = typeof(Vp8Codec).GetField("_vpxEncodeCtx",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                VpxCodecCtx ctx = field == null ? null : field.GetValue(codec) as VpxCodecCtx;
                if (ctx == null) return;
                vpx_codec_control(ctx.__Instance, 13, 12);
            }
            catch
            {
            }
        }

        private sealed class I420Mapper
        {
            public int SourceWidth;
            public int SourceHeight;
            public int DestWidth;
            public int DestHeight;
            public int[] LumaX;
            public int[] ChromaX;
            public int[] LumaRow;
            public int[] ChromaRow;

            public static I420Mapper Create(int sourceWidth, int sourceHeight, int destWidth, int destHeight)
            {
                I420Mapper mapper = new I420Mapper();
                mapper.SourceWidth = sourceWidth;
                mapper.SourceHeight = sourceHeight;
                mapper.DestWidth = destWidth;
                mapper.DestHeight = destHeight;
                int stride = sourceWidth * 4;
                mapper.LumaX = new int[destWidth];
                for (int x = 0; x < destWidth; x++)
                {
                    mapper.LumaX[x] = (x * sourceWidth / destWidth) * 4;
                }
                int chromaWidth = destWidth / 2;
                mapper.ChromaX = new int[chromaWidth];
                for (int x = 0; x < chromaWidth; x++)
                {
                    mapper.ChromaX[x] = ((2 * x + 1) * sourceWidth / destWidth) * 4;
                }
                mapper.LumaRow = new int[destHeight];
                for (int y = 0; y < destHeight; y++)
                {
                    mapper.LumaRow[y] = (y * sourceHeight / destHeight) * stride;
                }
                int chromaHeight = destHeight / 2;
                mapper.ChromaRow = new int[chromaHeight];
                for (int y = 0; y < chromaHeight; y++)
                {
                    mapper.ChromaRow[y] = ((2 * y + 1) * sourceHeight / destHeight) * stride;
                }
                return mapper;
            }
        }

        private static void ConvertBgraToI420(byte[] bgra, byte[] i420, I420Mapper mapper)
        {
            int width = mapper.DestWidth;
            int height = mapper.DestHeight;
            int chromaW = width / 2;
            int chromaH = height / 2;
            int uOffset = width * height;
            int vOffset = uOffset + chromaW * chromaH;
            int[] lumaX = mapper.LumaX;
            int[] lumaRow = mapper.LumaRow;
            for (int y = 0; y < height; y++)
            {
                int row = lumaRow[y];
                int dst = y * width;
                for (int x = 0; x < width; x++)
                {
                    int p = row + lumaX[x];
                    int b = bgra[p];
                    int g = bgra[p + 1];
                    int r = bgra[p + 2];
                    int luma = (306 * r + 601 * g + 117 * b) >> 10;
                    i420[dst + x] = (byte)(luma < 0 ? 0 : (luma > 255 ? 255 : luma));
                }
            }
            int[] chromaX = mapper.ChromaX;
            int[] chromaRow = mapper.ChromaRow;
            for (int y = 0; y < chromaH; y++)
            {
                int row = chromaRow[y];
                int du = uOffset + y * chromaW;
                int dv = vOffset + y * chromaW;
                for (int x = 0; x < chromaW; x++)
                {
                    int p = row + chromaX[x];
                    int b = bgra[p];
                    int g = bgra[p + 1];
                    int r = bgra[p + 2];
                    int u = ((112 * b - 74 * g - 38 * r) >> 8) + 128;
                    int v = ((157 * r - 132 * g - 26 * b) >> 8) + 128;
                    i420[du + x] = (byte)(u < 0 ? 0 : (u > 255 ? 255 : u));
                    i420[dv + x] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
                }
            }
        }

        private void RtcCaptureLoop(RtcSession session)
        {
            while (session.Running && running)
            {
                Size size = ScreenControl.FrameSizeOf(session.Index, ScreenControl.RtcWidth);
                int width = size.Width & ~1;
                int height = size.Height & ~1;
                if (width <= 1 || height <= 1)
                {
                    Thread.Sleep(100);
                    continue;
                }
                if (!RunRtcPipeline(session, new Size(width, height))) return;
            }
        }

        private bool RunRtcPipeline(RtcSession session, Size size)
        {
            int width = size.Width;
            int height = size.Height;
            if (session.EncodeSize.Width != width || session.EncodeSize.Height != height)
            {
                if (session.Encoder != null)
                {
                    try { session.Encoder.Dispose(); } catch { }
                }
                Vp8Codec fresh = new Vp8Codec();
                long computed = (long)width * height * 4 / 1000;
                long cap = ScreenControl.RtcBitrate * 1000L;
                uint bitrate = (uint)Math.Max(8000L, Math.Min(cap, computed));
                fresh.InitialiseEncoder((uint)width, (uint)height, bitrate);
                TuneVp8(fresh);
                session.Encoder = fresh;
                session.ForceKey = true;
                session.EncodeSize = size;
            }
            I420Mapper mapper = I420Mapper.Create(width, height, width, height);

            Queue<byte[]> free = new Queue<byte[]>(2);
            free.Enqueue(new byte[width * height * 4]);
            free.Enqueue(new byte[width * height * 4]);
            byte[] i420 = new byte[width * height + 2 * (width / 2) * (height / 2)];
            object sync = new object();
            byte[] slot = null;
            bool abort = false;

            Thread captureThread = new Thread(delegate()
            {
                int lastCapture = 0;
                while (session.Running && running)
                {
                    int now = Environment.TickCount;
                    if (lastCapture != 0)
                    {
                        int delta = unchecked(now - lastCapture);
                        if (delta < 33) Thread.Sleep(33 - delta);
                    }
                    lastCapture = Environment.TickCount;
                    Size current = ScreenControl.FrameSizeOf(session.Index, width);
                    if (current.Width != width || current.Height != height) break;
                    byte[] target;
                    lock (sync)
                    {
                        while (session.Running && running && !abort && free.Count == 0)
                        {
                            Monitor.Wait(sync, 100);
                        }
                        if (!session.Running || !running || abort) break;
                        target = free.Dequeue();
                    }
                    int frameWidth;
                    int frameHeight;
                    bool captured = ScreenControl.CaptureInto(session.Index, target, width, out frameWidth, out frameHeight);
                    if (!captured)
                    {
                        lock (sync) { free.Enqueue(target); Monitor.PulseAll(sync); }
                        Thread.Sleep(50);
                        continue;
                    }
                    if (frameWidth != width || frameHeight != height)
                    {
                        lock (sync) { free.Enqueue(target); Monitor.PulseAll(sync); }
                        break;
                    }
                    lock (sync)
                    {
                        slot = target;
                        Monitor.PulseAll(sync);
                    }
                }
                lock (sync)
                {
                    abort = true;
                    Monitor.PulseAll(sync);
                }
            });

            Thread encodeThread = new Thread(delegate()
            {
                Stopwatch clock = Stopwatch.StartNew();
                long lastSend = -1;
                int frames = 0;
                while (session.Running && running)
                {
                    byte[] frame;
                    lock (sync)
                    {
                        while (session.Running && running && !abort && slot == null)
                        {
                            Monitor.Wait(sync, 100);
                        }
                        if (!session.Running || !running || abort) break;
                        frame = slot;
                        slot = null;
                    }
                    if (frame == null) continue;
                    try
                    {
                        bool key = session.ForceKey || (frames > 0 && frames % 300 == 0);
                        if (session.ForceKey) session.ForceKey = false;
                        ConvertBgraToI420(frame, i420, mapper);
                        byte[] encoded = session.Encoder.Encode(i420, VpxImgFmt.VPX_IMG_FMT_I420, key);
                        if (encoded != null && encoded.Length > 0)
                        {
                            long now = clock.ElapsedTicks;
                            int ms = lastSend < 0 ? 40 : (int)Math.Round((now - lastSend) * 1000.0 / Stopwatch.Frequency);
                            lastSend = now;
                            if (ms < 10) ms = 10;
                            if (ms > 200) ms = 200;
                            session.Peer.SendVideo((uint)(ms * 90), encoded);
                            RecordScreenBytes("rtc", session.Index, encoded.Length, 1);
                            frames++;
                        }
                    }
                    catch
                    {
                    }
                    lock (sync)
                    {
                        free.Enqueue(frame);
                        Monitor.PulseAll(sync);
                    }
                }
                lock (sync) { Monitor.PulseAll(sync); }
            });

            captureThread.IsBackground = true;
            encodeThread.IsBackground = true;
            captureThread.Start();
            encodeThread.Start();
            captureThread.Join();
            encodeThread.Join();
            return session.Running && running;
        }

        private void HandleRtcClose(HttpListenerContext context)
        {
            IdRequest request = ParseJson<IdRequest>(context.Request);
            if (request != null) StopRtc(request.Id);
            WriteJson(context, 200, new { ok = true });
        }

        private void StopRtc(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            RtcSession session = null;
            lock (rtcLock)
            {
                if (rtcSessions.TryGetValue(id, out session)) rtcSessions.Remove(id);
            }
            if (session == null) return;
            session.Running = false;
            if (session.Capture != null)
            {
                try { session.Capture.Join(2000); } catch { }
            }
            try { ((IDisposable)session.Encoder).Dispose(); } catch { }
            try { session.Peer.close(); } catch { }
        }

        private void StopAllRtc()
        {
            List<RtcSession> all = new List<RtcSession>();
            lock (rtcLock)
            {
                foreach (RtcSession item in rtcSessions.Values) all.Add(item);
                rtcSessions.Clear();
            }
            for (int i = 0; i < all.Count; i++)
            {
                all[i].Running = false;
                if (all[i].Capture != null)
                {
                    try { all[i].Capture.Join(2000); } catch { }
                }
                try { ((IDisposable)all[i].Encoder).Dispose(); } catch { }
                try { all[i].Peer.close(); } catch { }
            }
        }

        private void HandleFile(HttpListenerContext context)
        {
            string dirId = Query(context, "dirId");
            string rel = Query(context, "path");
            string mode = Query(context, "mode");
            string full;
            if (!catalog.TryResolve(dirId, rel, out full) || !File.Exists(full))
            {
                WriteJson(context, 404, new { error = "文件不存在" });
                return;
            }
            ServeFile(context, full, !string.Equals(mode, "attachment", StringComparison.OrdinalIgnoreCase));
        }

        private void ServeFile(HttpListenerContext context, string full, bool inline)
        {
            FileInfo info = new FileInfo(full);
            long size = info.Length;
            string contentType = ContentTypeOf(full);
            string name = info.Name;

            context.Response.ContentType = contentType;
            context.Response.AddHeader("Accept-Ranges", "bytes");
            string disposition = inline ? "inline" : "attachment";
            context.Response.AddHeader("Content-Disposition",
                disposition + "; filename=\"" + SanitizeAscii(name) + "\"; filename*=UTF-8''" +
                Uri.EscapeDataString(name));

            long start = 0;
            long end = size - 1;
            string range = context.Request.Headers["Range"];
            bool partial = false;
            if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string spec = range.Substring(6).Trim();
                int dash = spec.IndexOf('-');
                if (dash > 0)
                {
                    string startText = spec.Substring(0, dash).Trim();
                    string endText = spec.Substring(dash + 1).Trim();
                    long parsed;
                    if (long.TryParse(startText, out parsed)) start = parsed;
                    if (endText.Length > 0 && long.TryParse(endText, out parsed)) end = parsed;
                    if (end >= size) end = size - 1;
                    if (start < 0) start = 0;
                    if (start > end || start >= size)
                    {
                        context.Response.StatusCode = 416;
                        context.Response.AddHeader("Content-Range", "bytes */" + size.ToString());
                        context.Response.ContentLength64 = 0;
                        return;
                    }
                    partial = true;
                }
            }

            long length = end - start + 1;
            context.Response.StatusCode = partial ? 206 : 200;
            if (partial)
            {
                context.Response.AddHeader("Content-Range",
                    "bytes " + start.ToString() + "-" + end.ToString() + "/" + size.ToString());
            }
            context.Response.ContentLength64 = length;

            using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan))
            {
                stream.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[128 * 1024];
                long remaining = length;
                while (remaining > 0)
                {
                    int want = buffer.Length;
                    if (want > remaining) want = (int)remaining;
                    int read = stream.Read(buffer, 0, want);
                    if (read <= 0) break;
                    context.Response.OutputStream.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
        }

        private void HandleDelete(HttpListenerContext context)
        {
            FileOpRequest request = ParseJson<FileOpRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            string error;
            bool ok = catalog.DeleteFile(request.DirId, request.Path, out error);
            if (ok)
            {
                catalog.InvalidateTree(request.DirId);
                uploads.ClearAlert(request.DirId, request.Path);
            }
            WriteJson(context, 200, new { ok = ok, error = error });
        }

        private void HandleRename(HttpListenerContext context)
        {
            FileOpRequest request = ParseJson<FileOpRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            string error;
            string newRel;
            bool ok = catalog.RenameFile(request.DirId, request.Path, request.NewName, out error, out newRel);
            if (ok)
            {
                catalog.InvalidateTree(request.DirId);
                uploads.RenameAlert(request.DirId, request.Path, newRel);
            }
            WriteJson(context, 200, new { ok = ok, error = error, path = newRel });
        }

        private void HandleCreateFolder(HttpListenerContext context)
        {
            CreateFolderRequest request = ParseJson<CreateFolderRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            string error;
            string newRel;
            bool ok = catalog.CreateFolder(request.DirId, request.Folder, request.Name, out error, out newRel);
            if (ok) catalog.InvalidateTree(request.DirId);
            WriteJson(context, 200, new { ok = ok, error = error, path = newRel });
        }

        private void HandleRenameFolder(HttpListenerContext context)
        {
            FileOpRequest request = ParseJson<FileOpRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            string error;
            string newRel;
            bool ok = catalog.RenameFolder(request.DirId, request.Path, request.NewName,
                out error, out newRel);
            if (ok)
            {
                catalog.InvalidateTree(request.DirId);
                uploads.RenameAlertFolder(request.DirId, request.Path, newRel);
                RenameRosterPath(request.DirId, FileCatalog.NormalizeRel(request.Path), newRel);
            }
            WriteJson(context, 200, new { ok = ok, error = error, path = newRel });
        }

        private static void RenameRosterPath(string dirId, string oldRel, string newRel)
        {
            if (string.IsNullOrEmpty(oldRel) || string.IsNullOrEmpty(newRel)) return;
            if (string.Equals(oldRel, newRel, StringComparison.OrdinalIgnoreCase)) return;
            string prefix = oldRel + "\\";
            int cut = oldRel.LastIndexOf('\\');
            string oldParent = cut >= 0 ? oldRel.Substring(0, cut) : "";
            string oldLeaf = cut >= 0 ? oldRel.Substring(cut + 1) : oldRel;
            int nextCut = newRel.LastIndexOf('\\');
            string newLeaf = nextCut >= 0 ? newRel.Substring(nextCut + 1) : newRel;

            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                if (c.Rosters == null) return;
                for (int i = 0; i < c.Rosters.Count; i++)
                {
                    RosterEntry entry = c.Rosters[i];
                    if (entry == null) continue;
                    if (!string.Equals(entry.DirId, dirId, StringComparison.OrdinalIgnoreCase)) continue;
                    string folder = FileCatalog.NormalizeRel(entry.Folder);
                    if (string.Equals(folder, oldRel, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Folder = newRel;
                    }
                    else if (folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Folder = newRel + folder.Substring(oldRel.Length);
                    }
                    else if (string.Equals(folder, oldParent, StringComparison.OrdinalIgnoreCase))
                    {
                        RenameRosterName(entry, oldLeaf, newLeaf);
                    }
                }
            });
        }

        private static void RenameRosterName(RosterEntry entry, string oldName, string newName)
        {
            if (entry.Names == null) return;
            for (int i = 0; i < entry.Names.Count; i++)
            {
                if (!string.Equals(entry.Names[i], oldName, StringComparison.OrdinalIgnoreCase)) continue;
                entry.Names.RemoveAt(i);
                break;
            }
            for (int i = 0; i < entry.Names.Count; i++)
            {
                if (!string.Equals(entry.Names[i], newName, StringComparison.OrdinalIgnoreCase)) continue;
                return;
            }
            entry.Names.Add(newName);
        }

        private static void RemoveRosterPath(string dirId, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return;
            string prefix = rel + "\\";
            int cut = rel.LastIndexOf('\\');
            string parent = cut >= 0 ? rel.Substring(0, cut) : "";
            string leaf = cut >= 0 ? rel.Substring(cut + 1) : rel;

            ConfigStore.Instance.Mutate(delegate(AppConfig c)
            {
                if (c.Rosters == null) return;
                for (int i = c.Rosters.Count - 1; i >= 0; i--)
                {
                    RosterEntry entry = c.Rosters[i];
                    if (entry == null)
                    {
                        c.Rosters.RemoveAt(i);
                        continue;
                    }
                    if (!string.Equals(entry.DirId, dirId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string folder = FileCatalog.NormalizeRel(entry.Folder);
                    if (string.Equals(folder, rel, StringComparison.OrdinalIgnoreCase) ||
                        folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        c.Rosters.RemoveAt(i);
                        continue;
                    }
                    if (!string.Equals(folder, parent, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Names == null) continue;
                    for (int j = entry.Names.Count - 1; j >= 0; j--)
                    {
                        if (string.Equals(entry.Names[j], leaf, StringComparison.OrdinalIgnoreCase))
                            entry.Names.RemoveAt(j);
                    }
                    if (entry.Names.Count == 0) c.Rosters.RemoveAt(i);
                }
            });
        }

        private void HandleDeleteFolder(HttpListenerContext context)
        {
            FileOpRequest request = ParseJson<FileOpRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            string error;
            bool ok = catalog.DeleteFolder(request.DirId, request.Path, out error);
            if (ok)
            {
                catalog.InvalidateTree(request.DirId);
                uploads.RemoveAlertFolder(request.DirId, request.Path);
                RemoveRosterPath(request.DirId, FileCatalog.NormalizeRel(request.Path));
            }
            WriteJson(context, 200, new { ok = ok, error = error });
        }

        private void HandleOpen(HttpListenerContext context)
        {
            FileOpRequest request = ParseJson<FileOpRequest>(context.Request);
            if (request == null)
            {
                WriteJson(context, 400, new { error = "请求无效" });
                return;
            }
            string full;
            if (!catalog.TryResolve(request.DirId, request.Path, out full) || !File.Exists(full))
            {
                WriteJson(context, 404, new { ok = false, error = "文件不存在" });
                return;
            }
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = full;
                info.UseShellExecute = true;
                Process process = Process.Start(info);
                BringOpenedFileToFront(process, full);
                WriteJson(context, 200, new { ok = true });
            }
            catch (Exception ex)
            {
                WriteJson(context, 200, new { ok = false, error = ex.Message });
            }
        }

        private static void BringOpenedFileToFront(Process process, string fullPath)
        {
            string fileName = Path.GetFileName(fullPath);
            string baseName = Path.GetFileNameWithoutExtension(fullPath);
            Thread thread = new Thread(delegate()
            {
                try
                {
                    IntPtr handle = IntPtr.Zero;
                    for (int i = 0; i < 40; i++)
                    {
                        if (process != null)
                        {
                            try
                            {
                                process.Refresh();
                                if (process.HasExited)
                                {
                                    process = null;
                                }
                                else if (process.MainWindowHandle != IntPtr.Zero)
                                {
                                    handle = process.MainWindowHandle;
                                    break;
                                }
                            }
                            catch
                            {
                                process = null;
                            }
                        }
                        handle = Native.FindVisibleWindow(fileName);
                        if (handle == IntPtr.Zero && baseName.Length > 0)
                            handle = Native.FindVisibleWindow(baseName);
                        if (handle != IntPtr.Zero) break;
                        Thread.Sleep(200);
                    }
                    if (handle == IntPtr.Zero) return;
                    Thread.Sleep(200);
                    Native.BringToForeground(handle);
                }
                catch
                {
                }
            });
            thread.IsBackground = true;
            thread.Name = "OpenForeground";
            thread.Start();
        }

        private bool TryServeStatic(HttpListenerContext context, string path)
        {
            string resource = null;
            if (string.Equals(path, "/vendor/bootstrap.min.css", StringComparison.OrdinalIgnoreCase))
                resource = "web/vendor/bootstrap.min.css";
            else if (string.Equals(path, "/vendor/bootstrap.bundle.min.js", StringComparison.OrdinalIgnoreCase))
                resource = "web/vendor/bootstrap.bundle.min.js";
            else if (string.Equals(path, "/vendor/bootstrap-icons.min.css", StringComparison.OrdinalIgnoreCase))
                resource = "web/vendor/bootstrap-icons.min.css";
            else if (string.Equals(path, "/vendor/html2canvas.min.js", StringComparison.OrdinalIgnoreCase))
                resource = "web/vendor/html2canvas.min.js";
            else if (string.Equals(path, "/vendor/fonts/bootstrap-icons.woff2", StringComparison.OrdinalIgnoreCase))
                resource = "web/vendor/fonts/bootstrap-icons.woff2";
            else if (string.Equals(path, "/vendor/fonts/bootstrap-icons.woff", StringComparison.OrdinalIgnoreCase))
                resource = "web/vendor/fonts/bootstrap-icons.woff";
            else if (string.Equals(path, "/app.js", StringComparison.OrdinalIgnoreCase))
                resource = "web/app.js";
            else if (string.Equals(path, "/style.css", StringComparison.OrdinalIgnoreCase))
                resource = "web/style.css";
            else if (string.Equals(path, "/sha256-worker.js", StringComparison.OrdinalIgnoreCase))
                resource = "web/sha256-worker.js";
            else if (string.Equals(path, "/login.html", StringComparison.OrdinalIgnoreCase))
                resource = "web/login.html";
            else if (string.Equals(path, "/manual.html", StringComparison.OrdinalIgnoreCase))
                resource = "web/manual.html";

            if (resource == null) return false;
            ServeAsset(context, resource);
            return true;
        }

        private void ServeAsset(HttpListenerContext context, string resource)
        {
            byte[] data = GetAsset(resource);
            if (data == null)
            {
                WriteJson(context, 404, new { error = "未找到" });
                return;
            }
            string ext = Path.GetExtension(resource);
            context.Response.ContentType = ContentTypeOf(resource);
            context.Response.ContentLength64 = data.Length;
            if (ext == ".html" || ext == ".js" || ext == ".css")
                context.Response.AddHeader("Cache-Control", "no-store");
            else
                context.Response.AddHeader("Cache-Control", "public, max-age=86400");
            context.Response.OutputStream.Write(data, 0, data.Length);
        }

        private byte[] GetAsset(string resource)
        {
            lock (assetCache)
            {
                byte[] cached;
                if (assetCache.TryGetValue(resource, out cached)) return cached;
            }
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(resource))
                {
                    if (stream == null) return null;
                    using (MemoryStream memory = new MemoryStream())
                    {
                        byte[] buffer = new byte[64 * 1024];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            memory.Write(buffer, 0, read);
                        byte[] data = memory.ToArray();
                        lock (assetCache) assetCache[resource] = data;
                        return data;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private void WriteJson(HttpListenerContext context, int status, object payload)
        {
            byte[] data = Encoding.UTF8.GetBytes(json.Serialize(payload));
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = data.Length;
            context.Response.OutputStream.Write(data, 0, data.Length);
        }

        private T ParseJson<T>(HttpListenerRequest request) where T : class
        {
            try
            {
                byte[] body = ReadBody(request, 8 * 1024 * 1024);
                if (body.Length == 0) return null;
                string text = Encoding.UTF8.GetString(body);
                return json.Deserialize<T>(text);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] ReadBody(HttpListenerRequest request, int limit)
        {
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > limit) throw new InvalidOperationException("请求内容过大");
                    memory.Write(buffer, 0, read);
                }
                return memory.ToArray();
            }
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            if (int.TryParse(text, out value)) return value;
            return fallback;
        }

        private static string GetCookie(HttpListenerRequest request, string name)
        {
            string header = request.Headers["Cookie"];
            if (string.IsNullOrEmpty(header)) return null;
            string[] parts = header.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(part.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase))
                    return part.Substring(eq + 1);
            }
            return null;
        }

        private static string SanitizeAscii(string name)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c >= 32 && c < 127 && c != '"' && c != '%' && c != '\\') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        private static string ContentTypeOf(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".pdf": return "application/pdf";
                case ".txt": return "text/plain; charset=utf-8";
                case ".mp4": return "video/mp4";
                case ".avi": return "video/x-msvideo";
                case ".mkv": return "video/x-matroska";
                case ".mov": return "video/quicktime";
                case ".wmv": return "video/x-ms-wmv";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".zip": return "application/zip";
                case ".rar": return "application/vnd.rar";
                case ".7z": return "application/x-7z-compressed";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                case ".ppsx": return "application/vnd.openxmlformats-officedocument.presentationml.slideshow";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".ppt": return "application/vnd.ms-powerpoint";
                case ".pps": return "application/vnd.ms-powerpoint";
                case ".doc": return "application/msword";
                case ".xls": return "application/vnd.ms-excel";
                case ".woff2": return "font/woff2";
                case ".woff": return "font/woff";
                default: return "application/octet-stream";
            }
        }
    }
}

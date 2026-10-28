using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

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
                address = SiteAddress()
            });
        }

        private string SiteAddress()
        {
            return LanMode ? LanSetup.GetLanAddress(Port)
                : "http://localhost:" + Port.ToString() + "/";
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
                        count = view.Count,
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

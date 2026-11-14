// Trạm phát của Second Brain: một bộ não, vô số thiết bị cắm vào.
// - Ổ cắm HTTP chỉ trên máy này (127.0.0.1): MCP qua HTTP, REST API + mô tả OpenAPI, cùng dùng chung bộ não với MCP stdio
// - Mỗi yêu cầu phải kèm mã khoá riêng (lưu ở o-cam.json) để trang web lạ không gọi trộm được
// - Đồng hồ điện: mọi lần cắm / dùng đều ghi vào hoat-dong.jsonl
// Chạy "app.exe /serve" để mở trạm phát ngầm (tự chạy khi bật máy sau khi cài).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// ---------- Đồng hồ điện: nhật ký mọi lần thiết bị cắm vào và dùng bộ não ----------
public class ActivityEntry { public DateTime T; public string Client, Channel, Action, Detail; }
public class Device { public string Name; public DateTime LastSeen; public int Uses; public string Channels; }

static class Activity
{
    public static readonly string LogFile = Path.Combine(Cleaner.DataDir, "hoat-dong.jsonl");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static readonly object gate = new object();

    public static void Log(string client, string channel, string action, string detail)
    {
        var line = Json.Serialize(new Dictionary<string, object> {
            { "t", DateTime.Now.ToString("o") }, { "c", client ?? "?" }, { "ch", channel }, { "a", action }, { "d", detail ?? "" } }) + "\n";
        var bytes = new UTF8Encoding(false).GetBytes(line);
        lock (gate)
            for (int i = 0; i < 5; i++)
                try
                {
                    Directory.CreateDirectory(Cleaner.DataDir);
                    var fi = new FileInfo(LogFile);
                    if (fi.Exists && fi.Length > 4 * 1024 * 1024) { File.Copy(LogFile, LogFile + ".1", true); File.WriteAllText(LogFile, ""); }
                    using (var fs = new FileStream(LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) { Thread.Sleep(30); }
                catch { return; }
    }

    // Tóm tắt tham số để nhật ký dễ đọc (không ghi nội dung dài)
    public static string Summary(string tool, Dictionary<string, object> a)
    {
        foreach (var k in new[] { "task", "name", "query", "path", "title", "text", "id", "source", "filter" })
        {
            object v;
            if (a != null && a.TryGetValue(k, out v) && v != null)
            {
                var s = Convert.ToString(v, CultureInfo.InvariantCulture).Replace("\n", " ");
                return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
            }
        }
        return "";
    }

    // Đọc các dòng cuối (không cần đọc cả file)
    public static List<ActivityEntry> Recent(int max)
    {
        var list = new List<ActivityEntry>();
        try
        {
            if (!File.Exists(LogFile)) return list;
            string text;
            using (var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - 512 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
            }
            foreach (var l in text.Split('\n').Reverse())
            {
                if (l.Trim().Length == 0) continue;
                try
                {
                    var d = Json.Deserialize<Dictionary<string, object>>(l);
                    list.Add(new ActivityEntry { T = DateTime.Parse((string)d["t"], null, DateTimeStyles.RoundtripKind), Client = (string)d["c"], Channel = (string)d["ch"], Action = (string)d["a"], Detail = (string)d["d"] });
                    if (list.Count >= max) break;
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    // Các thiết bị đã cắm vào (bóng đèn), mới dùng gần đây xếp trước
    public static List<Device> Devices(int days)
    {
        var since = DateTime.Now.AddDays(-days);
        return Recent(20000).Where(e => e.T >= since && !string.IsNullOrEmpty(e.Client))
            .GroupBy(e => e.Client, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Device { Name = g.First().Client, LastSeen = g.Max(e => e.T), Uses = g.Count(e => e.Action != "cắm vào"), Channels = string.Join(", ", g.Select(e => e.Channel).Distinct()) })
            .OrderByDescending(d => d.LastSeen).ToList();
    }
}

// ---------- Ổ cắm HTTP ----------
static class PowerStation
{
    public static readonly string ConfigFile = Path.Combine(Cleaner.DataDir, "o-cam.json");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
    static readonly ConcurrentDictionary<string, string> sessions = new ConcurrentDictionary<string, string>();
    static TcpListener listener;
    public static int Port;
    public static string Token;

    // Mã khoá và cổng đang dùng, để thiết bị / giao diện biết cách cắm vào
    public static void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var d = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFile, Encoding.UTF8));
                Token = d.ContainsKey("token") ? (string)d["token"] : null;
                Port = d.ContainsKey("port") ? Convert.ToInt32(d["port"]) : 0;
            }
        }
        catch { }
        if (string.IsNullOrEmpty(Token))
        {
            var b = new byte[24];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            Token = "sb_" + BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
        if (Port == 0) Port = 7801;
    }

    static void SaveConfig()
    {
        Directory.CreateDirectory(Cleaner.DataDir);
        File.WriteAllText(ConfigFile, Json.Serialize(new Dictionary<string, object> { { "port", Port }, { "token", Token }, { "url", "http://127.0.0.1:" + Port } }), new UTF8Encoding(false));
    }

    public static string BaseUrl { get { return "http://127.0.0.1:" + Port; } }

    // Trạm phát có đang chạy không (thử kết nối vào cổng)
    public static bool IsRunning()
    {
        LoadConfig();
        try { using (var c = new TcpClient()) { var t = c.ConnectAsync(IPAddress.Loopback, Port); return t.Wait(300) && c.Connected; } }
        catch { return false; }
    }

    // Chạy trạm phát (chặn luồng hiện tại). Chỉ một trạm trên mỗi máy.
    public static void Serve()
    {
        bool created;
        var single = new Mutex(true, @"Local\BentarSecondBrainServe", out created);
        if (!created) return;
        LoadConfig();
        for (int p = Port; p < Port + 20; p++)
        {
            try { listener = new TcpListener(IPAddress.Loopback, p); listener.Start(); Port = p; break; }
            catch { listener = null; }
        }
        if (listener == null) return;
        SaveConfig();
        Activity.Log("trạm phát", "http", "bật", BaseUrl);
        Task.Run(() => { try { Brain.EnsureLoaded(); } catch { } });
        // Giữ chỉ mục luôn mới khi trạm đang chạy
        var refresh = new Timer(_ => { try { Brain.RefreshIfStale(TimeSpan.FromMinutes(30)); } catch { } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        // Giữ bộ não cả đời: sao lưu khi trí nhớ thay đổi, tự kiểm tra bản sao lưu mỗi tuần
        var backup = new Timer(_ => { try { if (L.On("autobackup", true)) Backup.Tick(); } catch { } }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
        GC.KeepAlive(backup);
        // Tự nối lại các agent người dùng đã chọn nếu cấu hình bị mất (vd. agent cập nhật và xoá mục MCP)
        var heal = new Timer(_ => { try { AgentHooks.Heal(); } catch { } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
        GC.KeepAlive(heal);
        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { break; }
            Task.Run(() => HandleConnection(c));
        }
        GC.KeepAlive(refresh); GC.KeepAlive(single);
    }

    // Mở trạm phát ngầm nếu chưa chạy (giao diện gọi khi mở)
    public static void EnsureRunning(string exe)
    {
        try
        {
            if (IsRunning()) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "/serve") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { }
    }

    // Trạm phát đang chạy bản cũ (vd vừa cập nhật app) hoặc không nhận mã khoá hiện tại: tắt nó rồi mở bản mới.
    // Trả về true nếu đã khởi động lại.
    public static bool RestartIfOutdated(string exe)
    {
        if (!IsRunning()) return false;
        string running = null;
        try
        {
            using (var wc = new WebClient())
            {
                wc.Headers["Authorization"] = "Bearer " + Token;
                wc.Headers["X-Client-Name"] = "Bentar Second Brain";
                var d = Json.DeserializeObject(wc.DownloadString(BaseUrl + "/api/status")) as Dictionary<string, object>;
                object v; if (d != null && d.TryGetValue("version", out v)) running = v as string;
            }
        }
        catch { }   // 401 (mã khoá lệch) hoặc trạm treo: coi như cần khởi động lại
        if (running == Updater.Current.ToString(3)) return false;
        Stop();
        if (IsRunning()) KillServe();
        for (int i = 0; i < 20 && IsRunning(); i++) System.Threading.Thread.Sleep(150);
        Activity.Log("trạm phát", "http", L.T("restarted", "khởi động lại"), (running ?? "?") + " → " + Updater.Current.ToString(3));
        EnsureRunning(exe);
        return true;
    }

    // Dừng mọi tiến trình "… /serve" của app (không đụng tới các tiến trình /mcp của agent)
    static void KillServe()
    {
        try
        {
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'Bentar Second Brain.exe'"))
                foreach (System.Management.ManagementObject o in q.Get())
                {
                    var cmd = Convert.ToString(o["CommandLine"]) ?? "";
                    int pid = Convert.ToInt32(o["ProcessId"]);
                    if (pid == self || cmd.IndexOf("/serve", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
                }
        }
        catch { }
    }

    // Tắt trạm phát đang chạy (gọi vào chính nó, kèm mã khoá)
    public static void Stop()
    {
        if (!IsRunning()) return;
        try
        {
            using (var wc = new WebClient())
            {
                wc.Headers["Authorization"] = "Bearer " + Token;
                wc.Headers["X-Client-Name"] = "Bentar Second Brain";
                wc.UploadString(BaseUrl + "/api/shutdown", "POST", "");
            }
        }
        catch { }
        for (int i = 0; i < 20 && IsRunning(); i++) System.Threading.Thread.Sleep(150);
    }

    // Tạo mã khoá mới: mọi công cụ dùng mã cũ sẽ bị từ chối. Khởi động lại trạm để nhận mã mới.
    public static void NewToken(string exe)
    {
        bool was = IsRunning();
        if (was) Stop();
        Token = null; LoadConfig();
        var b = new byte[24];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
        Token = "sb_" + BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        SaveConfig();
        if (was) EnsureRunning(exe);
    }

    // ---------- HTTP tối giản ----------
    class Req { public string Method, Path, Query; public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); public byte[] Body; }

    static Req ReadRequest(NetworkStream ns)
    {
        var buf = new MemoryStream();
        var one = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = ns.Read(one, 0, one.Length);
            if (n <= 0) return null;
            buf.Write(one, 0, n);
            if (buf.Length > 64 * 1024) return null;
            var arr = buf.GetBuffer();
            for (int i = 3; i < buf.Length; i++)
                if (arr[i - 3] == '\r' && arr[i - 2] == '\n' && arr[i - 1] == '\r' && arr[i] == '\n') { headerEnd = i + 1; break; }
        }
        var all = buf.ToArray();
        var head = Encoding.UTF8.GetString(all, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var first = head[0].Split(' ');
        if (first.Length < 2) return null;
        var r = new Req { Method = first[0].ToUpperInvariant() };
        int q = first[1].IndexOf('?');
        r.Path = q >= 0 ? first[1].Substring(0, q) : first[1];
        r.Query = q >= 0 ? first[1].Substring(q + 1) : "";
        foreach (var h in head.Skip(1))
        {
            int c = h.IndexOf(':');
            if (c > 0) r.Headers[h.Substring(0, c).Trim()] = h.Substring(c + 1).Trim();
        }
        int len = 0;
        string cl;
        if (r.Headers.TryGetValue("Content-Length", out cl)) int.TryParse(cl, out len);
        if (len > 8 * 1024 * 1024) return null;
        var body = new MemoryStream();
        body.Write(all, headerEnd, all.Length - headerEnd);
        while (body.Length < len)
        {
            int n = ns.Read(one, 0, Math.Min(one.Length, len - (int)body.Length));
            if (n <= 0) break;
            body.Write(one, 0, n);
        }
        r.Body = body.ToArray();
        return r;
    }

    static void Write(NetworkStream ns, int status, string contentType, string body, Dictionary<string, string> extra = null)
    {
        var bytes = new UTF8Encoding(false).GetBytes(body ?? "");
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(status == 200 ? "OK" : status == 202 ? "Accepted" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : "Error").Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
        sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\nConnection: close\r\n");
        if (extra != null) foreach (var kv in extra) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        ns.Write(head, 0, head.Length);
        ns.Write(bytes, 0, bytes.Length);
    }

    static Dictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (q ?? "").Split('&'))
        {
            if (part.Length == 0) continue;
            int i = part.IndexOf('=');
            string k = Uri.UnescapeDataString((i >= 0 ? part.Substring(0, i) : part).Replace('+', ' '));
            string v = i >= 0 ? Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' ')) : "";
            d[k] = v;
        }
        return d;
    }

    static void HandleConnection(TcpClient c)
    {
        using (c)
        using (var ns = c.GetStream())
        {
            try
            {
                c.ReceiveTimeout = 15000;
                var r = ReadRequest(ns);
                if (r == null) return;
                var q = ParseQuery(r.Query);

                // Chặn trang web dùng tên miền lạ trỏ về máy này (DNS rebinding)
                string host;
                r.Headers.TryGetValue("Host", out host);
                var h = (host ?? "").ToLowerInvariant();
                if (!(h.StartsWith("127.0.0.1") || h.StartsWith("localhost") || h.StartsWith("[::1]"))) { Write(ns, 403, "text/plain", L.T("Only local connections are accepted.", "Chỉ nhận kết nối trên máy này.")); return; }

                if (r.Path == "/" && r.Method == "GET") { Write(ns, 200, "text/plain", Help()); return; }
                if (r.Path == "/openapi.json") { Write(ns, 200, "application/json", OpenApi()); return; }

                string auth, token = null, tok;
                if (r.Headers.TryGetValue("Authorization", out auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = auth.Substring(7).Trim();
                else if (r.Headers.TryGetValue("X-Brain-Token", out tok)) token = tok;
                else if (q.ContainsKey("token")) token = q["token"];
                if (token != Token) { Write(ns, 401, "application/json", L.T("{\"error\":\"Missing or wrong key. Get it from the Sockets page in Bentar Second Brain.\"}", "{\"error\":\"Thiếu hoặc sai mã khoá. Lấy mã ở trang Ổ cắm của Bentar Second Brain.\"}")); return; }

                string ua, named;
                r.Headers.TryGetValue("User-Agent", out ua);
                string client = r.Headers.ContainsKey("X-Client-Name") ? r.Headers["X-Client-Name"] : q.ContainsKey("client") ? q["client"] : ua != null ? ua.Split(' ', '/')[0] : "http";

                if (r.Path == "/mcp")
                {
                    if (r.Method != "POST") { Write(ns, 405, "application/json", L.T("{\"error\":\"Use POST\"}", "{\"error\":\"Dùng POST\"}"), new Dictionary<string, string> { { "Allow", "POST" } }); return; }
                    string sid; r.Headers.TryGetValue("Mcp-Session-Id", out sid);
                    string known;
                    if (sid != null && sessions.TryGetValue(sid, out known)) client = known;
                    var parsed = Json.DeserializeObject(Encoding.UTF8.GetString(r.Body));
                    var extra = new Dictionary<string, string>();
                    var list = parsed is object[] ? ((object[])parsed).OfType<Dictionary<string, object>>().ToList() : new List<Dictionary<string, object>> { parsed as Dictionary<string, object> };
                    var replies = new List<object>();
                    foreach (var m in list.Where(x => x != null))
                    {
                        var resp = McpServer.Process(m, client, "mcp-http", out named);
                        if (named != null)
                        {
                            client = named;
                            sid = Guid.NewGuid().ToString("N");
                            sessions[sid] = named;
                            extra["Mcp-Session-Id"] = sid;
                        }
                        if (resp != null) replies.Add(resp);
                    }
                    if (replies.Count == 0) { Write(ns, 202, "application/json", "", extra); return; }
                    Write(ns, 200, "application/json", Json.Serialize(parsed is object[] ? (object)replies : replies[0]), extra);
                    return;
                }

                if (r.Path == "/api/shutdown" && r.Method == "POST")
                {
                    Write(ns, 200, "application/json", "{\"ok\":true}");
                    Activity.Log(client, "http", "tắt", BaseUrl);
                    Task.Run(() => { System.Threading.Thread.Sleep(200); Environment.Exit(0); });
                    return;
                }
                if (r.Path == "/api/status")
                {
                    var mc = Memory.Counts();
                    Write(ns, 200, "application/json", Json.Serialize(new Dictionary<string, object> {
                        { "version", Updater.Current.ToString(3) }, { "files", Brain.FileCount }, { "projects", Brain.ProjectCount },
                        { "memories", mc.Item1 }, { "links", mc.Item2 },
                        { "devices", Activity.Devices(30).Select(d => new Dictionary<string, object> { { "name", d.Name }, { "lastSeen", d.LastSeen.ToString("o") }, { "uses", d.Uses }, { "channels", d.Channels } }).ToList() } }));
                    return;
                }
                // Ngữ cảnh cho hook của agent (SessionStart / UserPromptSubmit / BeforeAgent)
                if (r.Path == "/api/context")
                {
                    var body = r.Body.Length > 0 ? Json.DeserializeObject(Encoding.UTF8.GetString(r.Body)) as Dictionary<string, object> : null;
                    Func<string, string> g = k => { object v; return body != null && body.TryGetValue(k, out v) ? v as string : (q.ContainsKey(k) ? q[k] : null); };
                    string ev = g("event") ?? "UserPromptSubmit";
                    var ctx = AgentHooks.BuildContext(ev, g("cwd"), g("prompt"));
                    Activity.Log(client, "hook", ev, ctx.Length > 0 ? ctx.Length + L.T(" chars of context", " ký tự ngữ cảnh") : L.T("nothing relevant", "không có gì liên quan"));
                    Write(ns, 200, "application/json", Json.Serialize(new Dictionary<string, object> { { "context", ctx } }));
                    return;
                }
                if (r.Path == "/api/tools") { Write(ns, 200, "application/json", Json.Serialize(McpServer.Tools())); return; }

                if (r.Path.StartsWith("/api/"))
                {
                    string tool = r.Path.Substring(5);
                    if (!McpServer.Tools().OfType<Dictionary<string, object>>().Any(t => (string)t["name"] == tool)) { Write(ns, 404, "application/json", L.T("{\"error\":\"No such tool: ", "{\"error\":\"Không có công cụ ") + tool + "\"}"); return; }
                    var args = new Dictionary<string, object>();
                    foreach (var kv in q) if (kv.Key != "token" && kv.Key != "client") args[kv.Key] = kv.Value == "true" ? (object)true : kv.Value == "false" ? (object)false : kv.Value;
                    if (r.Body.Length > 0)
                    {
                        var body = Json.DeserializeObject(Encoding.UTF8.GetString(r.Body)) as Dictionary<string, object>;
                        if (body != null) foreach (var kv in body) args[kv.Key] = kv.Value;
                    }
                    bool isError;
                    var text = McpServer.Invoke(client, "rest", tool, args, out isError);
                    Write(ns, isError ? 400 : 200, "application/json", Json.Serialize(new Dictionary<string, object> { { "ok", !isError }, { "text", text } }));
                    return;
                }
                Write(ns, 404, "application/json", L.T("{\"error\":\"Unknown path. See GET / for usage.\"}", "{\"error\":\"Không có đường dẫn này. Xem GET / để biết cách dùng.\"}"));
            }
            catch (Exception ex)
            {
                try { Write(ns, 500, "application/json", Json.Serialize(new Dictionary<string, object> { { "error", ex.Message } })); } catch { }
            }
        }
    }

    static string Help()
    {
        return L.T("Bentar Second Brain – station\n\n", "Bentar Second Brain – trạm phát\n\n") +
               L.T("One brain that every agent and tool plugs into. Every request (except this page and /openapi.json) needs the key:\n", "Một bộ não, mọi agent và công cụ cắm vào. Mọi yêu cầu (trừ trang này và /openapi.json) cần mã khoá:\n") +
               L.T("  Authorization: Bearer <key>   or   X-Brain-Token: <key>   or   ?token=<key>\n", "  Authorization: Bearer <mã>   hoặc   X-Brain-Token: <mã>   hoặc   ?token=<mã>\n") +
               L.T("Name your device: header X-Client-Name: <name>  or  ?client=<name>\n\n", "Đặt tên thiết bị: header X-Client-Name: <tên>  hoặc  ?client=<tên>\n\n") +
               "  POST /mcp                MCP qua HTTP (JSON-RPC)\n" +
               L.T("  GET|POST /api/<tool>     call a tool, e.g. /api/search?query=..., /api/recall, /api/remember\n", "  GET|POST /api/<công cụ>  gọi công cụ, ví dụ /api/search?query=..., /api/recall, /api/remember\n") +
               L.T("  GET /api/tools           list tools\n", "  GET /api/tools           danh sách công cụ\n") +
               L.T("  GET /api/status          brain status and plugged-in devices\n", "  GET /api/status          trạng thái bộ não và các thiết bị đã cắm\n") +
               L.T("  GET /openapi.json        OpenAPI spec for all tools\n", "  GET /openapi.json        mô tả OpenAPI cho mọi công cụ\n");
    }

    // Mô tả OpenAPI sinh từ chính danh sách công cụ (thêm công cụ là tự có trong mô tả)
    static string OpenApi()
    {
        var paths = new Dictionary<string, object>();
        foreach (var t in McpServer.Tools().OfType<Dictionary<string, object>>())
        {
            var schema = (Dictionary<string, object>)t["inputSchema"];
            paths["/api/" + t["name"]] = new Dictionary<string, object> {
                { "post", new Dictionary<string, object> {
                    { "operationId", t["name"] }, { "summary", t["description"] },
                    { "requestBody", new Dictionary<string, object> { { "required", true }, { "content", new Dictionary<string, object> { { "application/json", new Dictionary<string, object> { { "schema", schema } } } } } } },
                    { "responses", new Dictionary<string, object> { { "200", new Dictionary<string, object> { { "description", L.T("Text result in the text field", "Kết quả dạng văn bản trong trường text") } } } } } } } };
        }
        return Json.Serialize(new Dictionary<string, object> {
            { "openapi", "3.0.3" },
            { "info", new Dictionary<string, object> { { "title", "Bentar Second Brain" }, { "version", Updater.Current.ToString(3) }, { "description", L.T("A second brain shared by every agent and tool on this PC.", "Bộ não thứ hai dùng chung cho mọi agent và công cụ trên máy này.") } } },
            { "servers", new object[] { new Dictionary<string, object> { { "url", BaseUrl } } } },
            { "components", new Dictionary<string, object> { { "securitySchemes", new Dictionary<string, object> { { "bearer", new Dictionary<string, object> { { "type", "http" }, { "scheme", "bearer" } } } } } } },
            { "security", new object[] { new Dictionary<string, object> { { "bearer", new object[0] } } } },
            { "paths", paths } });
    }
}

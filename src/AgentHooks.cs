// Làm cho MỌI agent thật sự dùng Second Brain, không chỉ "cắm vào":
// 1) Hook: agent tự chạy "app.exe /hook" khi mở phiên và mỗi lần người dùng hỏi; app đưa sẵn ngữ cảnh đã xác minh vào (agent không cần tự nhớ ra mà gọi)
// 2) Dòng lệnh "sb": agent nào chạy được lệnh shell cũng dùng được (kể cả agent không có MCP)
// 3) Skill (chuẩn Agent Skills) + đoạn hướng dẫn trong CLAUDE.md / AGENTS.md / GEMINI.md của người dùng
// 4) Tự nối lại: trạm phát định kỳ kiểm tra, cấu hình bị mất thì cài lại
// Hook và dòng lệnh gọi qua trạm phát (chỉ mục đã nằm sẵn trong bộ nhớ) nên chạy nhanh.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

static class AgentHooks
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string BinDir { get { return Path.Combine(Cleaner.DataDir, "bin"); } }
    static string Exe { get { return Installer.Exe; } }
    static string Fwd(string p) { return p.Replace('\\', '/'); }
    const int MaxContext = 8000;   // agent giới hạn ~10.000 ký tự cho ngữ cảnh từ hook

    // ================= Ngữ cảnh đưa vào agent (chạy trong trạm phát) =================
    public static string BuildContext(string ev, string cwd, string prompt)
    {
        var sb = new StringBuilder();
        string proj = null;
        try { if (!string.IsNullOrEmpty(cwd)) proj = Brain.ProjectOf(cwd) ?? (Directory.Exists(cwd) ? Path.GetFullPath(cwd).TrimEnd('\\') : null); } catch { }
        bool start = ev == "SessionStart";
        if (start)
        {
            sb.AppendLine(L.T("[Bentar Second Brain] This PC has a shared Second Brain: an index of every project and document on all drives, plus a long-term memory shared by all agents (only verified knowledge is shared).",
                              "[Bentar Second Brain] Máy này có Second Brain dùng chung: chỉ mục mọi dự án và tài liệu trên mọi ổ đĩa, cùng trí nhớ dài hạn chung cho mọi agent (chỉ chia sẻ kiến thức đã xác minh)."));
            sb.AppendLine(L.T("- Need docs/code/a project outside this folder? Find it yourself: MCP tool search, or shell: sb search \"…\"; read: sb read <path>; bring it in: sb copy <src> <dst> (never overwrites). Don't ask the user for paths.",
                              "- Cần tài liệu/code/dự án ngoài thư mục này? Tự tìm: công cụ MCP search, hoặc shell: sb search \"…\"; đọc: sb read <đường dẫn>; chép vào: sb copy <nguồn> <đích> (không ghi đè). Đừng hỏi người dùng đường dẫn."));
            sb.AppendLine(L.T("- Results are labelled ✓ trusted / ⚠ unverified. Only follow ✓ trusted code as an example.",
                              "- Kết quả được gắn nhãn ✓ tin cậy / ⚠ chưa kiểm chứng. Chỉ học theo code ✓ tin cậy."));
            sb.AppendLine(L.T("- Save knowledge only with evidence the app can check: remember (or sb remember \"…\" --evidence 'file \"<path>\" \"<exact quote>\"'). Unproven notes are not shared.",
                              "- Chỉ lưu kiến thức kèm bằng chứng app kiểm tra được: remember (hoặc sb remember \"…\" --evidence 'file \"<đường dẫn>\" \"<trích nguyên văn>\"'). Điều chưa chứng minh không được chia sẻ."));
            if (proj != null)
            {
                sb.AppendLine().AppendLine(L.T("Current project: ", "Dự án hiện tại: ") + proj + "  —  " + Trust.Tag(Trust.Of(proj)));
                var mem = Memory.ForProject(proj);
                if (mem.Length > 0) sb.AppendLine(mem);
            }
        }
        else
        {
            var q = (prompt ?? "").Trim();
            if (q.Length < 12) return "";
            // Ghi nhớ ĐÃ XÁC MINH khớp với câu hỏi
            var mem = Memory.RecallRaw(q, 6, false).Where(r => r.N.Kind != "file" && r.N.Kind != "du-an" && r.Score >= 4).Take(4).ToList();
            // Tài liệu ở dự án KHÁC khớp mạnh với câu hỏi (dự án hiện tại agent tự đọc được)
            var files = new List<string>();
            try
            {
                foreach (var h in Brain.Search(q, null, null, false, 40))
                {
                    if (files.Count >= 3) break;
                    if (h.Score < 40) continue;
                    if (proj != null && (h.File.Path + "\\").StartsWith(proj + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    var t = Trust.Of(h.File.Path);
                    if (t.Level == Trust.Level.Excluded) continue;
                    files.Add("• " + h.File.Path + "  [" + Trust.Tag(t) + "]" + (h.Snippet != null ? "\n    " + h.Snippet.Replace("\n", "\n    ").TrimEnd() : ""));
                }
            }
            catch { }
            if (mem.Count == 0 && files.Count == 0) return "";
            sb.AppendLine(L.T("[Bentar Second Brain] Possibly relevant to this request (verify before relying on it):", "[Bentar Second Brain] Có thể liên quan tới yêu cầu này (kiểm tra trước khi dựa vào):"));
            if (mem.Count > 0) sb.AppendLine(L.T("Verified memories:", "Ghi nhớ đã xác minh:")).AppendLine(Memory.Format(mem, false));
            if (files.Count > 0) sb.AppendLine(L.T("Documents in other projects (read with read_file / sb read):", "Tài liệu ở dự án khác (đọc bằng read_file / sb read):")).AppendLine(string.Join("\n", files));
        }
        var s = sb.ToString().TrimEnd();
        return s.Length > MaxContext ? s.Substring(0, MaxContext) + "…" : s;
    }

    // ================= Gọi trạm phát =================
    static string CallStation(string path, Dictionary<string, object> body, string client, int timeoutMs)
    {
        PowerStation.LoadConfig();
        var req = (HttpWebRequest)WebRequest.Create(PowerStation.BaseUrl + path);
        req.Method = "POST"; req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.Proxy = null;
        req.ContentType = "application/json; charset=utf-8";
        req.Headers["Authorization"] = "Bearer " + PowerStation.Token;
        req.Headers["X-Client-Name"] = client;
        var bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(body));
        using (var st = req.GetRequestStream()) st.Write(bytes, 0, bytes.Length);
        try
        {
            using (var resp = (HttpWebResponse)req.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }
        catch (WebException ex)
        {
            if (ex.Response == null) throw;
            using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }
    }

    static bool EnsureStation(int waitMs)
    {
        PowerStation.LoadConfig();
        if (PowerStation.IsRunning()) return true;
        PowerStation.EnsureRunning(Installer.IsInstalled ? Exe : System.Windows.Forms.Application.ExecutablePath);
        for (int i = 0; i < waitMs / 200; i++) { System.Threading.Thread.Sleep(200); PowerStation.LoadConfig(); if (PowerStation.IsRunning()) return true; }
        return false;
    }

    static string AgentFrom(Dictionary<string, object> p)
    {
        object t; p.TryGetValue("transcript_path", out t);
        var s = (t as string ?? "").ToLowerInvariant();
        if (s.Contains(".claude")) return "claude-code";
        if (s.Contains(".codex")) return "codex";
        if (s.Contains(".gemini")) return "gemini-cli";
        return "agent";
    }

    // ================= app.exe /hook =================
    // Đọc JSON sự kiện từ stdin, in JSON ngữ cảnh ra stdout. Không bao giờ làm hỏng phiên của agent: lỗi gì cũng thoát êm (mã 0, không in gì).
    public static void RunHook()
    {
        try
        {
            string input;
            using (var sr = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))) input = sr.ReadToEnd();
            var p = Json.DeserializeObject(input) as Dictionary<string, object> ?? new Dictionary<string, object>();
            Func<string, string> g = k => { object v; return p.TryGetValue(k, out v) ? v as string : null; };
            string ev = g("hook_event_name") ?? "UserPromptSubmit";
            string agent = AgentFrom(p);
            if (ev == "PreToolUse")
            {
                object ti; p.TryGetValue("tool_input", out ti);
                var tip = CodeGraph.Nudge(g("tool_name"), ti as Dictionary<string, object>, g("session_id"), g("cwd"));
                if (tip == null) return;
                var pj = Json.Serialize(new Dictionary<string, object> { { "hookSpecificOutput", new Dictionary<string, object> { { "hookEventName", ev }, { "additionalContext", tip } } } });
                using (var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))) o.Write(pj);
                return;
            }
            if (!EnsureStation(4000)) return;
            var raw = CallStation("/api/context", new Dictionary<string, object> { { "event", ev }, { "cwd", g("cwd") ?? Environment.CurrentDirectory }, { "prompt", g("prompt") ?? "" } }, agent, 8000);
            var d = Json.DeserializeObject(raw) as Dictionary<string, object>;
            object ctx; if (d == null || !d.TryGetValue("context", out ctx) || string.IsNullOrEmpty(ctx as string)) return;
            var outJson = Json.Serialize(new Dictionary<string, object> { { "hookSpecificOutput", new Dictionary<string, object> { { "hookEventName", ev }, { "additionalContext", (string)ctx } } } });
            using (var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))) o.Write(outJson);
        }
        catch { }
    }

    // ================= app.exe /cli … (lệnh "sb") =================
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern int GetFileType(IntPtr h);

    static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { "search", "search" }, { "find", "search" }, { "recall", "recall" }, { "remember", "remember" }, { "read", "read_file" }, { "cat", "read_file" },
        { "ls", "list_dir" }, { "dir", "list_dir" }, { "projects", "list_projects" }, { "overview", "project_overview" }, { "copy", "copy" }, { "cp", "copy" },
        { "status", "index_status" }, { "revise", "revise_memory" }, { "link", "connect" },
        { "pack", "context_pack" }, { "context", "context_pack" }, { "map", "code_map" }, { "explain", "explain" }, { "callers", "who_calls" }, { "who-calls", "who_calls" }, { "path", "call_path" } };
    static readonly Dictionary<string, string> OptAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { "from", "start_line" }, { "lines", "max_lines" }, { "all", "include_unverified" }, { "excluded", "include_excluded" }, { "name-only", "name_only" } };
    static readonly HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name_only", "include_unverified", "include_excluded", "refresh", "outdated" };

    public static int RunCli(string[] args)
    {
        // App có giao diện (winexe): khi chạy trong cửa sổ dòng lệnh mà không chuyển hướng output thì gắn vào console của cửa sổ đó
        bool redirected = GetFileType(GetStdHandle(-11)) != 0;
        if (!redirected && AttachConsole(-1)) try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help" || args[0] == "-h") { o.WriteLine(CliHelp()); return 0; }
            string tool;
            if (!Aliases.TryGetValue(args[0], out tool)) { o.WriteLine(L.T("Unknown command: ", "Không có lệnh: ") + args[0] + "\n\n" + CliHelp()); return 2; }
            var a = new Dictionary<string, object>(); var pos = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    var k = args[i].Substring(2); string mk; if (OptAlias.TryGetValue(k, out mk)) k = mk; k = k.Replace('-', '_');
                    if (Flags.Contains(k)) a[k] = true; else if (i + 1 < args.Length) a[k] = args[++i];
                }
                else pos.Add(args[i]);
            }
            Action<string> joinTo = k => { if (pos.Count > 0 && !a.ContainsKey(k)) a[k] = string.Join(" ", pos); };
            switch (tool)
            {
                case "search": case "recall": joinTo("query"); break;
                case "remember": joinTo("text"); break;
                case "list_projects": joinTo("filter"); break;
                case "read_file": if (pos.Count > 0) a["path"] = pos[0]; if (pos.Count > 1) a["start_line"] = pos[1]; if (pos.Count > 2) a["max_lines"] = pos[2]; break;
                case "list_dir": case "project_overview": if (pos.Count > 0) a["path"] = pos[0]; break;
                case "copy": if (pos.Count > 0) a["source"] = pos[0]; if (pos.Count > 1) a["destination"] = pos[1]; break;
                case "revise_memory": if (pos.Count > 0) a["id"] = pos[0]; if (pos.Count > 1) a["text"] = string.Join(" ", pos.Skip(1)); break;
                case "connect": if (pos.Count > 0) a["a"] = pos[0]; if (pos.Count > 1) a["b"] = pos[1]; break;
                case "context_pack": joinTo("task"); if (!a.ContainsKey("path")) a["path"] = Environment.CurrentDirectory; break;
                case "code_map": a["path"] = pos.Count > 0 ? pos[0] : Environment.CurrentDirectory; break;
                case "explain": case "who_calls": if (pos.Count > 0) a["name"] = pos[0]; if (!a.ContainsKey("path")) a["path"] = pos.Count > 1 ? pos[1] : Environment.CurrentDirectory; break;
                case "call_path": if (pos.Count > 0) a["from"] = pos[0]; if (pos.Count > 1) a["to"] = pos[1]; if (!a.ContainsKey("path")) a["path"] = Environment.CurrentDirectory; break;
            }
            foreach (var k in new[] { "path", "source", "destination" })
            {
                object v;
                if (a.TryGetValue(k, out v) && v is string && !Path.IsPathRooted((string)v) && ((string)v).Length > 0) a[k] = Path.GetFullPath((string)v);   // đường dẫn tương đối tính từ thư mục hiện tại của agent
            }
            if (tool == "remember" && !a.ContainsKey("evidence")) a["evidence"] = "";
            string text; bool isError;
            if (EnsureStation(5000))
            {
                var d = Json.DeserializeObject(CallStation("/api/" + tool, a, "cli", 120000)) as Dictionary<string, object> ?? new Dictionary<string, object>();
                object t, ok; d.TryGetValue("text", out t); d.TryGetValue("ok", out ok);
                if (t == null) { object e; d.TryGetValue("error", out e); t = e; }
                text = Convert.ToString(t); isError = !(ok is bool && (bool)ok);
            }
            else text = McpServer.Invoke("cli", "cli", tool, a, out isError);   // trạm phát tắt: tự chạy (chậm hơn vì phải nạp chỉ mục)
            o.WriteLine(text);
            return isError ? 1 : 0;
        }
        catch (Exception ex) { o.WriteLine(L.T("Error: ", "Lỗi: ") + ex.Message); return 1; }
    }

    static string CliHelp()
    {
        return "Bentar Second Brain – sb\n\n" +
            "  sb search <words> [--path D:\\dir] [--ext md] [--limit 20] [--name-only] [--excluded]\n" +
            "  sb read <file> [start_line] [max_lines]\n" +
            "  sb ls [folder]                 (no folder = list drives)\n" +
            "  sb projects [filter]\n" +
            "  sb overview <project folder>\n" +
            "  sb copy <source> <destination> (never overwrites)\n" +
            "  sb pack <task> [--path <project>] [--budget 6000]   (exact code for a task; default path = current folder)\n" +
            "  sb map [file|folder]  ·  sb explain <Name>  ·  sb callers <Name>  ·  sb path <From> <To>\n" +
            "  sb recall [topic] [--all]      (verified memories; --all also shows unverified)\n" +
            "  sb remember <text> --evidence 'file \"<path>\" \"<exact quote>\"' [--kind lesson|decision|fact|todo|note] [--source <path>]\n" +
            "       evidence lines: file \"<path>\" \"<quote>\" | commit \"<repo>\" <hash> | url https://… \"<quote>\" | user \"<what the user said>\"\n" +
            "  sb status\n";
    }

    // ================= Cài đặt cho từng agent =================
    const string BlockStart = "<!-- bentar-second-brain:start -->", BlockEnd = "<!-- bentar-second-brain:end -->";
    static readonly Regex BlockRx = new Regex("\\r?\\n?" + Regex.Escape(BlockStart) + ".*?" + Regex.Escape(BlockEnd) + "\\r?\\n?", RegexOptions.Singleline);

    static string InstructionBlock()
    {
        return BlockStart + "\n" +
            "## Bentar Second Brain (managed by the app – edits inside this block are overwritten)\n" +
            "- This PC has a shared Second Brain: an index of every project and document on all drives, plus a long-term memory shared by all agents.\n" +
            "- Need a document, code or another project outside the current folder? Find it yourself – MCP tool `search` (server `second-brain`) or shell `sb search \"…\"` – read it (`read_file` / `sb read <path>`), and bring it in with `copy` / `sb copy <src> <dst>` (never overwrites). Don't ask the user for paths.\n" +
            "- Results are labelled ✓ trusted (tests/CI + releases, or marked by the user) or ⚠ unverified. Only follow ✓ trusted code as an example; read ⚠ code critically.\n" +
            "- Coding in a project? Call `context_pack` (task + project folder) FIRST – exact relevant code with line numbers within a token budget (shell: `sb pack \"<task>\"`). Then `explain <symbol>`, `who_calls <symbol>`, `code_map <file|dir>`, `call_path A B` instead of grep or reading whole files; read_file only the line ranges they point to.\n" +
            "- At the start of a task, `recall` (or `sb recall \"<topic>\"`) – it returns verified memories only.\n" +
            "- Save knowledge with `remember` / `sb remember` ONLY with evidence the app can check: file \"<path>\" \"<exact quote>\", commit \"<repo>\" <hash>, or url https://… \"<exact quote>\". Unproven notes wait for the user's approval. Never store secrets.\n" +
            "- If `sb` is not on PATH, use: \"" + Fwd(Path.Combine(BinDir, "sb.cmd")) + "\"\n" +
            BlockEnd + "\n";
    }

    static string SkillText()
    {
        return "---\n" +
            "name: second-brain\n" +
            "description: Find, read and copy documents or code from any project or drive on this PC, and recall or save verified long-term memory shared by all coding agents. Use when the user mentions files, docs, research, notes or code outside the current folder, another project, \"second brain\", or when starting a task where earlier decisions and lessons may exist.\n" +
            "---\n\n" +
            "# Bentar Second Brain\n\n" +
            "Bentar Second Brain indexes every drive on this PC and keeps a long-term memory shared by all agents. It never edits or deletes the user's files.\n\n" +
            "## Tools\n\n" +
            "Prefer the MCP tools from the `second-brain` server. If they are not available, use the `sb` command in the shell (full path: `" + Fwd(Path.Combine(BinDir, "sb.cmd")) + "`).\n\n" +
            "| Need | MCP tool | Shell |\n|---|---|---|\n" +
            "| Find docs/code anywhere (accents ignored) | `search` | `sb search \"words\"` |\n" +
            "| List projects / project overview | `list_projects`, `project_overview` | `sb projects`, `sb overview <dir>` |\n" +
            "| Read a file (also .docx) | `read_file` | `sb read <path> [start] [lines]` |\n" +
            "| Bring a file/folder into this project | `copy` (never overwrites) | `sb copy <src> <dst>` |\n" +
            "| What earlier agents learned (verified only) | `recall` | `sb recall \"topic\"` |\n" +
            "| Save verified knowledge | `remember` | `sb remember \"text\" --evidence '…'` |\n\n" +
            "## Trust rules\n\n" +
            "- Results are labelled **✓ trusted** (tests/CI + releases, or marked trusted by the user), **⚠ unverified**, or hidden when excluded (scratch/temp folders).\n" +
            "- Only follow ✓ trusted code as an example. Read ⚠ code critically; never present it as a proven pattern.\n\n" +
            "## Saving memory needs evidence\n\n" +
            "The app checks evidence itself and refuses false evidence. Give one or more lines:\n\n" +
            "```\nfile \"D:\\proj\\README.md\" \"exact sentence copied from the file\"\ncommit \"D:\\proj\" a4c38fc\nurl https://example.com/docs \"exact sentence from the page\"\nuser \"what the user said\"      (waits for the user's approval)\n```\n\n" +
            "- Lessons (how to do things) only count when the file is in a ✓ trusted project.\n" +
            "- Without strong evidence the memory waits for the user's approval and is NOT shared with other agents.\n" +
            "- Never store passwords, API keys or tokens.\n";
    }

    // Ghi file chỉ khi nội dung thật sự đổi (giữ nguyên thời gian sửa file; Codex coi hook bị đổi là chưa tin cậy)
    static bool WriteIfChanged(string file, string text)
    {
        if (File.Exists(file) && File.ReadAllText(file, Encoding.UTF8) == text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        if (File.Exists(file) && !File.Exists(file + ".bak-secondbrain")) File.Copy(file, file + ".bak-secondbrain");
        File.WriteAllText(file, text, new UTF8Encoding(false));
        return true;
    }

    static void PutBlock(string file)
    {
        string text = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";
        string block = InstructionBlock();
        string updated = BlockRx.IsMatch(text) ? BlockRx.Replace(text, "\n" + block, 1) : (text.TrimEnd() + (text.Trim().Length > 0 ? "\n\n" : "") + block);
        WriteIfChanged(file, updated.TrimStart('\n'));
    }
    static void DropBlock(string file)
    {
        if (!File.Exists(file)) return;
        var text = File.ReadAllText(file, Encoding.UTF8);
        if (BlockRx.IsMatch(text)) WriteIfChanged(file, BlockRx.Replace(text, "\n").Trim() + "\n");
    }

    // ---- Sửa file cấu hình JSON của agent: chỉ thêm/bớt mục hook của Second Brain, giữ nguyên mọi thứ khác ----
    static bool IsOurs(object hookGroup)
    {
        var s = Json.Serialize(hookGroup);
        return s.IndexOf("Bentar Second Brain", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("sb-hook", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static void EditHooks(string file, Dictionary<string, object> mine, bool remove)
    {
        string text = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";
        var root = text.Trim().Length == 0 ? new Dictionary<string, object>() : Json.DeserializeObject(text) as Dictionary<string, object>;
        if (root == null) throw new InvalidDataException(L.T("Invalid config file: ", "File cấu hình không hợp lệ: ") + file);
        object ho; root.TryGetValue("hooks", out ho);
        var hooks = ho as Dictionary<string, object> ?? new Dictionary<string, object>();
        foreach (var ev in mine.Keys.Union(hooks.Keys.ToList()).ToList())
        {
            object cur; hooks.TryGetValue(ev, out cur);
            var list = (cur as object[] ?? new object[0]).Where(x => !IsOurs(x)).ToList();
            if (!remove && mine.ContainsKey(ev)) list.Add(mine[ev]);
            if (list.Count > 0) hooks[ev] = list.ToArray(); else if (cur != null) hooks.Remove(ev);
        }
        if (hooks.Count > 0) root["hooks"] = hooks; else root.Remove("hooks");
        if (remove && !File.Exists(file)) return;
        WriteIfChanged(file, Pretty(root, 0) + "\n");
    }

    static Dictionary<string, object> Group(string matcher, Dictionary<string, object> hook)
    {
        var g = new Dictionary<string, object>();
        if (matcher != null) g["matcher"] = matcher;
        g["hooks"] = new object[] { hook };
        return g;
    }

    static string ShimHook { get { return Fwd(Path.Combine(BinDir, "sb-hook.cmd")); } }

    static string ClaudeDir { get { return Path.Combine(Home, ".claude"); } }
    static string CodexDir { get { return Path.Combine(Home, ".codex"); } }
    static string GeminiDir { get { return Path.Combine(Home, ".gemini"); } }

    /// <summary>Cài hook + skill + hướng dẫn cho một agent (gọi lại nhiều lần vẫn an toàn). Trả về mô tả những gì đã làm.</summary>
    public static string Install(string agent)
    {
        EnsureShims();
        var done = new List<string>();
        if (agent == "Claude Code" && Directory.Exists(ClaudeDir))
        {
            // Claude Code chạy thẳng file .exe (không qua shell) nên không lo dấu cách hay dấu \ trong đường dẫn
            Func<int, Dictionary<string, object>> h = t => new Dictionary<string, object> { { "type", "command" }, { "command", Exe }, { "args", new object[] { "--hook" } }, { "timeout", t } };
            EditHooks(Path.Combine(ClaudeDir, "settings.json"), new Dictionary<string, object> {
                { "SessionStart", Group("startup|resume|clear|compact", h(15)) }, { "UserPromptSubmit", Group(null, h(10)) }, { "PreToolUse", Group("Read|Grep", h(5)) } }, false);
            WriteIfChanged(Path.Combine(ClaudeDir, "skills", "second-brain", "SKILL.md"), SkillText());
            PutBlock(Path.Combine(ClaudeDir, "CLAUDE.md"));
            done.Add(L.T("hooks, skill, CLAUDE.md", "hook, skill, CLAUDE.md"));
        }
        else if (agent == "Codex" && Directory.Exists(CodexDir))
        {
            var cfg = Path.Combine(CodexDir, "config.toml");
            bool tomlHooks = File.Exists(cfg) && Regex.IsMatch(File.ReadAllText(cfg), "^\\s*\\[\\[?hooks", RegexOptions.Multiline);
            if (!tomlHooks)
            {
                Func<int, Dictionary<string, object>> h = t => new Dictionary<string, object> { { "type", "command" }, { "command", ShimHook }, { "timeout", t } };
                EditHooks(Path.Combine(CodexDir, "hooks.json"), new Dictionary<string, object> {
                    { "SessionStart", Group("startup|resume", h(15)) }, { "UserPromptSubmit", Group(null, h(10)) } }, false);
                done.Add(L.T("hooks (approve once in Codex with /hooks)", "hook (duyệt một lần trong Codex bằng /hooks)"));
            }
            WriteIfChanged(Path.Combine(Home, ".agents", "skills", "second-brain", "SKILL.md"), SkillText());
            PutBlock(Path.Combine(CodexDir, "AGENTS.md"));
            done.Add(L.T("skill, AGENTS.md", "skill, AGENTS.md"));
        }
        else if (agent == "Gemini CLI" && Directory.Exists(GeminiDir))
        {
            Func<int, Dictionary<string, object>> h = t => new Dictionary<string, object> { { "name", "second-brain" }, { "type", "command" }, { "command", ShimHook }, { "timeout", t } };
            EditHooks(Path.Combine(GeminiDir, "settings.json"), new Dictionary<string, object> {
                { "SessionStart", Group("startup", h(15000)) }, { "BeforeAgent", Group(null, h(10000)) } }, false);
            WriteIfChanged(Path.Combine(Home, ".agents", "skills", "second-brain", "SKILL.md"), SkillText());
            PutBlock(Path.Combine(GeminiDir, "GEMINI.md"));
            done.Add(L.T("hooks, skill, GEMINI.md", "hook, skill, GEMINI.md"));
        }
        return done.Count == 0 ? null : string.Join(", ", done);
    }

    public static void Uninstall(string agent)
    {
        try
        {
            if (agent == "Claude Code")
            {
                if (File.Exists(Path.Combine(ClaudeDir, "settings.json"))) EditHooks(Path.Combine(ClaudeDir, "settings.json"), new Dictionary<string, object>(), true);
                DeleteSkill(Path.Combine(ClaudeDir, "skills", "second-brain"));
                DropBlock(Path.Combine(ClaudeDir, "CLAUDE.md"));
            }
            else if (agent == "Codex")
            {
                if (File.Exists(Path.Combine(CodexDir, "hooks.json"))) EditHooks(Path.Combine(CodexDir, "hooks.json"), new Dictionary<string, object>(), true);
                DropBlock(Path.Combine(CodexDir, "AGENTS.md"));
            }
            else if (agent == "Gemini CLI")
            {
                if (File.Exists(Path.Combine(GeminiDir, "settings.json"))) EditHooks(Path.Combine(GeminiDir, "settings.json"), new Dictionary<string, object>(), true);
                DropBlock(Path.Combine(GeminiDir, "GEMINI.md"));
            }
        }
        catch { }
    }

    static void DeleteSkill(string dir)
    {
        var f = Path.Combine(dir, "SKILL.md");
        if (File.Exists(f) && File.ReadAllText(f).Contains("# Bentar Second Brain")) { File.Delete(f); try { Directory.Delete(dir); } catch { } }
    }

    // ---- Lệnh "sb" trên PATH ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, UIntPtr w, string l, uint flags, uint timeout, out UIntPtr result);

    public static void EnsureShims()
    {
        Directory.CreateDirectory(BinDir);
        WriteIfChanged(Path.Combine(BinDir, "sb.cmd"), "@echo off\r\n\"" + Exe + "\" --cli %*\r\n");
        WriteIfChanged(Path.Combine(BinDir, "sb-hook.cmd"), "@\"" + Exe + "\" --hook\r\n");
        // Git Bash / WSL-style shell: script không đuôi
        var unix = Regex.Replace(Fwd(Exe), "^([A-Za-z]):", m => "/" + m.Groups[1].Value.ToLowerInvariant());
        WriteIfChanged(Path.Combine(BinDir, "sb"), "#!/bin/sh\n# Git Bash tự đổi đối số bắt đầu bằng \"/\" thành đường dẫn; tắt việc đó và dùng --cli\nexport MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'\nexec \"" + unix + "\" --cli \"$@\"\n");
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", true))
            {
                var cur = k.GetValue("Path", "", Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                if (!cur.Split(';').Any(x => string.Equals(x.Trim().TrimEnd('\\'), BinDir, StringComparison.OrdinalIgnoreCase)))
                {
                    k.SetValue("Path", (cur.TrimEnd(';') + (cur.Length > 0 ? ";" : "") + BinDir), Microsoft.Win32.RegistryValueKind.ExpandString);
                    UIntPtr r; SendMessageTimeout((IntPtr)0xffff, 0x001A, UIntPtr.Zero, "Environment", 0x0002, 3000, out r);   // báo cho các app mới mở nhận PATH mới
                }
            }
        }
        catch { }
    }

    // ================= Tự nối lại (trạm phát gọi định kỳ) =================
    public static void Heal()
    {
        try { AgentLinks.RetireOthers(); } catch { }
        foreach (var a in AgentLinks.List())
        {
            var key = "agent." + a.Name;
            var want = L.Get(key, "");
            // Agent đã nối từ bản cũ: người dùng mới đồng ý MCP, nên chỉ giữ MCP ("mcp"); hook/skill/hướng dẫn chỉ cài khi người dùng bấm Kết nối ("1")
            if (want == "" && a.Connected) { L.Set(key, "mcp"); want = "mcp"; }
            if ((want != "1" && want != "mcp") || !a.Installed) continue;
            try
            {
                if (!a.Connected)
                {
                    if (want == "1") AgentLinks.Connect(a.Name); else AgentLinks.ConnectMcpOnly(a.Name);
                    Activity.Log("Second Brain", "heal", L.T("reconnected", "tự nối lại"), a.Name);
                }
                if (want == "1") Install(a.Name);
            }
            catch (Exception ex) { Activity.Log("Second Brain", "heal", L.T("error", "lỗi"), a.Name + ": " + ex.Message); }
        }
    }

    // ================= JSON đẹp (giữ file cấu hình dễ đọc) =================
    static string Pretty(object o, int ind)
    {
        string pad = new string(' ', (ind + 1) * 2), end = new string(' ', ind * 2);
        var d = o as Dictionary<string, object>;
        if (d != null)
            return d.Count == 0 ? "{}" : "{\n" + string.Join(",\n", d.Select(kv => pad + Json.Serialize(kv.Key) + ": " + Pretty(kv.Value, ind + 1))) + "\n" + end + "}";
        var arr = o as System.Collections.IEnumerable;
        if (arr != null && !(o is string))
        {
            var items = arr.Cast<object>().ToList();
            return items.Count == 0 ? "[]" : "[\n" + string.Join(",\n", items.Select(x => pad + Pretty(x, ind + 1))) + "\n" + end + "]";
        }
        return Json.Serialize(o);
    }
}

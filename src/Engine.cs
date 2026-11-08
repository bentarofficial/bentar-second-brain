// Bộ máy suy nghĩ của Second Brain: chạy Claude Code (gói Claude của người dùng) ở chế độ không tương tác "claude -p".
// - Hỏi đáp: chỉ được đọc (công cụ Second Brain + đọc file), không sửa gì.
// - Lập kế hoạch: chế độ plan, không sửa gì.
// - Làm việc: chỉ sau khi người dùng duyệt kế hoạch; sửa trong thư mục dự án; CẤM đẩy code, phát hành, xoá.
// Mã đăng nhập (claude setup-token) được mã hoá bằng tài khoản Windows (DPAPI), không bao giờ vào trí nhớ hay bản sao lưu.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

static class ClaudeEngine
{
    public enum Mode { Ask, Plan, Task }
    public class Event { public string Kind, Text; }                  // Kind: text | tool | info
    public class Result { public bool Error; public string Text, SessionId; public double Cost; public int Turns; }

    static readonly string TokenFile = Path.Combine(Cleaner.DataDir, "claude-token.bin");
    static readonly byte[] Salt = Encoding.UTF8.GetBytes("BentarSecondBrain.ClaudeToken");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };

    // ---------- Mã đăng nhập ----------
    public static bool HasToken { get { return File.Exists(TokenFile); } }
    public static void SetToken(string token)
    {
        token = (token ?? "").Trim();
        if (token.Length < 20) throw new ArgumentException(L.T("That doesn't look like a token from `claude setup-token`.", "Có vẻ không phải mã từ lệnh `claude setup-token`."));
        Directory.CreateDirectory(Cleaner.DataDir);
        File.WriteAllBytes(TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Salt, DataProtectionScope.CurrentUser));
    }
    public static void ClearToken() { try { File.Delete(TokenFile); } catch { } }
    static string Token() { try { return HasToken ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), Salt, DataProtectionScope.CurrentUser)) : null; } catch { return null; } }

    // ---------- Tìm Claude Code ----------
    static string cli;
    public static string Cli
    {
        get
        {
            if (cli != null && File.Exists(cli)) return cli;
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Concat(new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin") });
            foreach (var d in dirs.Where(x => x.Trim().Length > 0))
                foreach (var n in new[] { "claude.exe", "claude.cmd" })
                    try { var p = Path.Combine(d.Trim(), n); if (File.Exists(p)) return cli = p; } catch { }
            return null;
        }
    }
    public static bool Installed { get { return Cli != null; } }

    /// <summary>Sẵn sàng chạy chưa: có Claude Code và (có mã đăng nhập hoặc CLI đang đăng nhập).</summary>
    public static string Readiness()
    {
        if (!Installed) return L.T("Claude Code is not installed (npm i -g @anthropic-ai/claude-code).", "Chưa cài Claude Code (npm i -g @anthropic-ai/claude-code).");
        if (HasToken) return null;
        if (CliLoggedIn()) return null;
        return L.T("Claude is not signed in. Click “Claude sign-in…” and sign in with Google or your Claude account.", "Claude chưa đăng nhập. Bấm “Đăng nhập Claude…” rồi đăng nhập bằng Google hoặc tài khoản Claude.");
    }

    /// <summary>Mở cửa sổ "claude auth login": trình duyệt bật lên, người dùng đăng nhập bằng Google hoặc tài khoản Claude.</summary>
    public static Process StartBrowserLogin()
    {
        var exe = Cli;
        if (exe == null) throw new InvalidOperationException(Readiness());
        var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(exe) + " auth login\"")
        { UseShellExecute = true, WorkingDirectory = Cleaner.DataDir };
        Directory.CreateDirectory(Cleaner.DataDir);
        return Process.Start(psi);
    }

    /// <summary>Claude Code (CLI) đã đăng nhập bằng tài khoản chưa.</summary>
    public static bool CliLoggedIn()
    {
        try
        {
            var p = Start(new[] { "auth", "status" }, Cleaner.DataDir, false);
            var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(15000);
            return o.Contains("\"loggedIn\": true") || o.Contains("\"loggedIn\":true");
        }
        catch { return false; }
    }

    static Process Start(IEnumerable<string> args, string cwd, bool stdin)
    {
        var exe = Cli;
        // claude.cmd (bản npm) phải chạy qua cmd.exe; các đối số đều do app tạo, không chứa nội dung người dùng (nội dung đi qua stdin)
        var argText = string.Join(" ", args.Select(Quote));
        var psi = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Quote(exe) + " " + argText + "\"")
            : new ProcessStartInfo(exe, argText);
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WorkingDirectory = cwd;
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.RedirectStandardInput = stdin;
        psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
        var tok = Token();
        if (tok != null) psi.EnvironmentVariables["CLAUDE_CODE_OAUTH_TOKEN"] = tok;
        psi.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");   // luôn dùng gói Claude của người dùng, không tính phí API ngầm
        return Process.Start(psi);
    }
    static string Quote(string a) { return a.Length > 0 && a.IndexOfAny(new[] { ' ', '(', ')', '&', '^', '|', '<', '>', '*', '"', ',' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\""; }

    // Không bao giờ cho agent tự làm những việc khó đảo ngược hoặc lộ ra ngoài
    static readonly string[] Forbidden = {
        "Bash(git push:*)", "Bash(git reset:*)", "Bash(git clean:*)", "Bash(git checkout --:*)", "Bash(git branch -D:*)", "Bash(git rebase:*)", "Bash(git commit:*)",
        "Bash(gh release:*)", "Bash(gh repo:*)", "Bash(gh pr merge:*)", "Bash(npm publish:*)", "Bash(rm:*)", "Bash(rmdir:*)", "Bash(del:*)", "Bash(rd:*)",
        "Bash(Remove-Item:*)", "Bash(format:*)", "Bash(shutdown:*)", "Bash(curl:*)", "Bash(wget:*)", "Bash(Invoke-WebRequest:*)" };
    static readonly string[] BrainRead = { "mcp__second-brain__search", "mcp__second-brain__read_file", "mcp__second-brain__list_dir", "mcp__second-brain__list_projects",
        "mcp__second-brain__project_overview", "mcp__second-brain__recall", "mcp__second-brain__index_status" };

    static string WriteSupportFiles(string extra)
    {
        Directory.CreateDirectory(Cleaner.DataDir);
        var mcp = Path.Combine(Cleaner.DataDir, "engine-mcp.json");
        var exe = Installer.IsInstalled ? Installer.Exe : System.Windows.Forms.Application.ExecutablePath;
        File.WriteAllText(mcp, Json.Serialize(new Dictionary<string, object> { { "mcpServers", new Dictionary<string, object> {
            { "second-brain", new Dictionary<string, object> { { "command", exe }, { "args", new[] { "/mcp" } } } } } } }), new UTF8Encoding(false));
        var sys = Path.Combine(Cleaner.DataDir, "engine-prompt.md");
        File.WriteAllText(sys,
            "You are working for the user through their Bentar Second Brain app. The user talks to you in the app's Chat page and may not see a terminal.\n" +
            "- Always answer in the user's language (Vietnamese if they write Vietnamese). Be concise and concrete.\n" +
            "- Use the second-brain MCP tools to find documents, code and verified memories anywhere on this PC (search, read_file, list_projects, project_overview, recall). Results marked ✓ trusted are safe examples; treat ⚠ unverified code critically.\n" +
            "- Never push, publish, release, merge, delete files or contact external services. Those actions are blocked; if the task needs one, say exactly what the user should do instead.\n" +
            "- When you change code: keep changes inside the project folder, run the project's tests or build if they exist, and finish with a short report: what changed (files), how you verified it, and what is left.\n" +
            "- If you learned something worth keeping and can prove it, save it with the second-brain remember tool including evidence (file \"<path>\" \"<exact quote>\" or commit \"<repo>\" <hash>).\n" +
            (string.IsNullOrWhiteSpace(extra) ? "" : "\n## Project instructions from the user\n" + extra.Trim() + "\n"),
            new UTF8Encoding(false));
        return mcp + "|" + sys;
    }

    public class Run
    {
        internal Process P; volatile bool stopped;
        public string SessionId;
        public bool Stopped { get { return stopped; } }
        public void Stop()
        {
            stopped = true;
            try { if (P != null && !P.HasExited) Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + P.Id) { CreateNoWindow = true, UseShellExecute = false }).WaitForExit(5000); } catch { }
        }
    }

    /// <summary>Chạy một lượt Claude. onEvent/onDone được gọi trên luồng nền.</summary>
    public static Run Start(string prompt, string cwd, Mode mode, string resume, Action<Event> onEvent, Action<Result> onDone, bool fork = false, string projectInstructions = null)
    {
        var files = WriteSupportFiles(projectInstructions).Split('|');
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--mcp-config", files[0], "--append-system-prompt-file", files[1] };
        if (mode == Mode.Ask)
        {
            args.AddRange(new[] { "--permission-mode", "default", "--allowedTools" }); args.AddRange(BrainRead.Concat(new[] { "Read", "Glob", "Grep" }));
            args.Add("--max-turns"); args.Add("25");
        }
        else if (mode == Mode.Plan)
        {
            args.AddRange(new[] { "--permission-mode", "plan", "--allowedTools" }); args.AddRange(BrainRead.Concat(new[] { "Read", "Glob", "Grep" }));
            args.Add("--max-turns"); args.Add("40");
        }
        else
        {
            args.AddRange(new[] { "--permission-mode", "acceptEdits", "--allowedTools" });
            args.AddRange(BrainRead.Concat(new[] { "mcp__second-brain__copy", "mcp__second-brain__remember", "Read", "Glob", "Grep", "Edit", "Write", "Bash", "TodoWrite" }));
            args.Add("--max-turns"); args.Add("120");
        }
        args.Add("--disallowedTools"); args.AddRange(Forbidden);
        if (!string.IsNullOrEmpty(resume)) { args.Add("--resume"); args.Add(resume); if (fork) args.Add("--fork-session"); }

        var run = new Run();
        var p = Start(args, Directory.Exists(cwd) ? cwd : Cleaner.DataDir, true);
        run.P = p;
        Activity.Log("Second Brain", "engine", mode.ToString().ToLowerInvariant(), cwd);
        p.StandardInput.Write(prompt); p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        Task.Run(() =>
        {
            var res = new Result();
            try
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    Dictionary<string, object> m;
                    try { m = Json.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
                    if (m == null) continue;
                    var type = m.ContainsKey("type") ? m["type"] as string : "";
                    object sid; if (m.TryGetValue("session_id", out sid) && sid is string) run.SessionId = res.SessionId = (string)sid;
                    if (type == "assistant")
                    {
                        var msg = m["message"] as Dictionary<string, object>;
                        foreach (var c in (msg != null && msg.ContainsKey("content") ? msg["content"] as object[] : null) ?? new object[0])
                        {
                            var b = c as Dictionary<string, object>; if (b == null) continue;
                            var bt = b.ContainsKey("type") ? b["type"] as string : "";
                            if (bt == "text") onEvent(new Event { Kind = "text", Text = b["text"] as string });
                            else if (bt == "tool_use") onEvent(new Event { Kind = "tool", Text = ToolLine(b) });
                        }
                    }
                    else if (type == "result")
                    {
                        res.Error = m.ContainsKey("is_error") && m["is_error"] is bool && (bool)m["is_error"];
                        res.Text = m.ContainsKey("result") ? m["result"] as string : null;
                        if (m.ContainsKey("total_cost_usd")) try { res.Cost = Convert.ToDouble(m["total_cost_usd"]); } catch { }
                        if (m.ContainsKey("num_turns")) try { res.Turns = Convert.ToInt32(m["num_turns"]); } catch { }
                        object sub; if (m.TryGetValue("subtype", out sub) && (sub as string ?? "").StartsWith("error") && string.IsNullOrEmpty(res.Text)) { res.Error = true; res.Text = (string)sub; }
                    }
                }
                p.WaitForExit();
                if (res.Text == null && !run.Stopped)
                {
                    var e = (err.Result ?? "").Trim();
                    res.Error = true; res.Text = e.Length > 0 ? e : L.T("Claude stopped without an answer (exit code ", "Claude dừng mà không trả lời (mã ") + p.ExitCode + ").";
                }
            }
            catch (Exception ex) { res.Error = true; res.Text = ex.Message; }
            if (run.Stopped) { res.Error = true; res.Text = L.T("Stopped.", "Đã dừng."); }
            Activity.Log("Second Brain", "engine", res.Error ? L.T("error", "lỗi") : L.T("done", "xong"), (res.Text ?? "").Length > 120 ? res.Text.Substring(0, 120) + "…" : res.Text);
            onDone(res);
        });
        return run;
    }

    // Mô tả ngắn một lần dùng công cụ, để người dùng theo dõi agent đang làm gì
    static string ToolLine(Dictionary<string, object> b)
    {
        var name = (b["name"] as string ?? "").Replace("mcp__second-brain__", "brain.");
        var input = b.ContainsKey("input") ? b["input"] as Dictionary<string, object> : null;
        string detail = null;
        if (input != null)
            foreach (var k in new[] { "query", "file_path", "path", "command", "pattern", "source", "description" })
            {
                object v; if (input.TryGetValue(k, out v) && v is string) { detail = (string)v; break; }
            }
        if (detail != null && detail.Length > 110) detail = detail.Substring(0, 110) + "…";
        return "→ " + name + (detail != null ? "  " + detail.Replace("\n", " ") : "");
    }
}

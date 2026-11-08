// Mỗi việc giao cho agent chạy trên một NHÁNH RIÊNG (git worktree) để:
// - nhiều việc chạy song song không giẫm lên nhau và không đụng vào code đang làm dở của người dùng
// - xong việc người dùng xem thay đổi (diff) rồi tự quyết: Gộp vào nhánh chính, hoặc Bỏ (xoá nhánh)
// Agent không được commit; app tự commit kết quả VÀO NHÁNH RIÊNG của việc đó (không bao giờ vào nhánh chính).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

public class WsTask
{
    public string Id { get; set; } public string ChatId { get; set; } public string ProjectId { get; set; } public string Title { get; set; }
    public string Branch { get; set; } public string Worktree { get; set; } public string BaseCommit { get; set; } public string BaseBranch { get; set; }
    public string Status { get; set; }   // running | done | nochange | failed | stopped | interrupted | merged | discarded
    public string Note { get; set; } public int Files { get; set; }
    public DateTime Created { get; set; } public DateTime Updated { get; set; }
    public bool Open { get { return Status != "merged" && Status != "discarded"; } }
}

static class TaskBranches
{
    static string Root { get { return Path.Combine(Cleaner.DataDir, "worktrees"); } }
    const string Who = "-c user.name=\"Bentar Second Brain\" -c user.email=second-brain@localhost";

    public static Tuple<int, string> Git(string dir, string args, int timeoutMs = 120000)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = dir, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return Tuple.Create(-1, "timeout"); }
            return Tuple.Create(p.ExitCode, (o + err.Result).Trim());
        }
    }

    public static bool IsRepo(string dir) { return dir != null && Directory.Exists(dir) && Git(dir, "rev-parse --is-inside-work-tree").Item2 == "true"; }
    static bool HasCommit(string dir) { return Git(dir, "rev-parse --verify HEAD").Item1 == 0; }

    /// <summary>Repo mới tạo chưa có commit nào: tạo commit đầu tiên để có điểm xuất phát cho nhánh.</summary>
    public static void EnsureFirstCommit(string dir)
    {
        if (HasCommit(dir)) return;
        Git(dir, "add -A");
        Git(dir, Who + " commit -q --allow-empty -m \"Initial commit (Bentar Second Brain)\"");
    }

    public static WsTask Start(WsProject proj, string chatId, string title)
    {
        if (!IsRepo(proj.Path)) throw new InvalidOperationException(L.T("This project is not a git repository.", "Project này chưa phải repo git."));
        EnsureFirstCommit(proj.Path);
        var id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var slug = Regex.Replace(Brain.Fold(title ?? "task").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 32) slug = slug.Substring(0, 32).Trim('-');
        var branch = "sb/" + (slug.Length > 0 ? slug : "task") + "-" + id.Substring(0, 4);
        var wt = Path.Combine(Root, id);
        Directory.CreateDirectory(Root);
        var baseCommit = Git(proj.Path, "rev-parse HEAD").Item2;
        var baseBranch = Git(proj.Path, "rev-parse --abbrev-ref HEAD").Item2;
        var r = Git(proj.Path, "worktree add -q -b \"" + branch + "\" \"" + wt + "\" HEAD");
        if (r.Item1 != 0) throw new InvalidOperationException(L.T("Could not create the task branch: ", "Không tạo được nhánh làm việc: ") + r.Item2);
        var t = new WsTask { Id = id, ChatId = chatId, ProjectId = proj.Id, Title = title, Branch = branch, Worktree = wt, BaseCommit = baseCommit, BaseBranch = baseBranch,
            Status = "running", Created = DateTime.Now, Updated = DateTime.Now };
        lock (Workspace.Data) Workspace.Data.Tasks.Add(t);
        Workspace.Save();
        Activity.Log("Second Brain", "task", L.T("branch created", "tạo nhánh"), branch);
        return t;
    }

    /// <summary>Agent làm xong: app commit mọi thay đổi vào nhánh riêng của việc.</summary>
    public static void Finish(WsTask t, string status)
    {
        if (Directory.Exists(t.Worktree))
        {
            Git(t.Worktree, "add -A");
            if (Git(t.Worktree, "diff --cached --quiet").Item1 != 0)
                Git(t.Worktree, Who + " commit -q -m \"" + ("Second Brain task: " + t.Title).Replace("\"", "'") + "\"");
            t.Files = Git(t.Worktree, "diff --name-only " + t.BaseCommit + " HEAD").Item2.Split('\n').Count(l => l.Trim().Length > 0);
        }
        t.Status = status == "done" && t.Files == 0 ? "nochange" : status;
        t.Updated = DateTime.Now;
        Workspace.Save();
        Activity.Log("Second Brain", "task", t.Status, t.Branch + " · " + t.Files + L.T(" files", " file"));
    }

    public static string Diff(WsTask t, WsProject proj, bool statOnly)
    {
        var dir = Directory.Exists(t.Worktree) ? t.Worktree : proj.Path;
        var r = Git(dir, "diff " + (statOnly ? "--stat " : "") + t.BaseCommit + " " + t.Branch);
        var s = r.Item2;
        return s.Length > 400000 ? s.Substring(0, 400000) + "\n… " + L.T("(truncated)", "(cắt bớt)") : s;
    }

    /// <summary>Gộp nhánh của việc vào nhánh đang dùng của project. Từ chối nếu project còn thay đổi chưa commit; xung đột thì huỷ gộp.</summary>
    public static string Merge(WsTask t, WsProject proj)
    {
        var dirty = Git(proj.Path, "status --porcelain --untracked-files=no").Item2;
        if (dirty.Length > 0) throw new InvalidOperationException(L.T("The project has uncommitted changes. Commit or stash them first, then merge.", "Project đang có thay đổi chưa commit. Hãy commit hoặc cất (stash) trước rồi gộp."));
        var r = Git(proj.Path, Who + " merge --no-ff \"" + t.Branch + "\" -m \"" + ("Merge Second Brain task: " + t.Title).Replace("\"", "'") + "\"");
        if (r.Item1 != 0)
        {
            Git(proj.Path, "merge --abort");
            throw new InvalidOperationException(L.T("Merge conflict – nothing was changed. Details: ", "Xung đột khi gộp – chưa có gì thay đổi. Chi tiết: ") + r.Item2);
        }
        Cleanup(t, proj, false);
        t.Status = "merged"; t.Updated = DateTime.Now; Workspace.Save();
        Activity.Log("Second Brain", "task", L.T("merged", "đã gộp"), t.Branch);
        return L.T("Merged into ", "Đã gộp vào ") + Git(proj.Path, "rev-parse --abbrev-ref HEAD").Item2 + ".";
    }

    public static void Discard(WsTask t, WsProject proj)
    {
        Cleanup(t, proj, true);
        t.Status = "discarded"; t.Updated = DateTime.Now; Workspace.Save();
        Activity.Log("Second Brain", "task", L.T("discarded", "đã bỏ"), t.Branch);
    }

    static void Cleanup(WsTask t, WsProject proj, bool force)
    {
        if (proj != null && Directory.Exists(proj.Path))
        {
            Git(proj.Path, "worktree remove --force \"" + t.Worktree + "\"");
            Git(proj.Path, "branch " + (force ? "-D" : "-d") + " \"" + t.Branch + "\"");
            Git(proj.Path, "worktree prune");
        }
        try { if (Directory.Exists(t.Worktree)) Directory.Delete(t.Worktree, true); } catch { }
    }

    public static string Label(string status)
    {
        switch (status)
        {
            case "running": return L.T("● Running", "● Đang làm");
            case "done": return L.T("✓ Ready to review", "✓ Chờ xem lại");
            case "nochange": return L.T("No changes", "Không có thay đổi");
            case "failed": return L.T("⚠ Failed", "⚠ Lỗi");
            case "stopped": return L.T("■ Stopped", "■ Đã dừng");
            case "interrupted": return L.T("Interrupted", "Bị ngắt");
            case "merged": return L.T("Merged", "Đã gộp");
            case "discarded": return L.T("Discarded", "Đã bỏ");
            default: return status;
        }
    }
}

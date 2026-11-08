// Project và cuộc trò chuyện của trang Chat. Mọi thứ tạo ra đều có thao tác ngược:
// tạo ↔ xoá (vào thùng rác 30 ngày, khôi phục được), lưu trữ ↔ bỏ lưu trữ, ghim ↔ bỏ ghim, thêm ↔ gỡ khỏi Second Brain.
// Thư mục dự án CHỈ bị xoá khi do Second Brain tạo ra, và chỉ chuyển vào Thùng rác của Windows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

public class WsMessage { public string Role { get; set; } public string Text { get; set; } public DateTime T { get; set; } }

public class WsProject
{
    public string Id { get; set; } public string Name { get; set; } public string Path { get; set; } public string Instructions { get; set; }
    public bool Pinned { get; set; } public bool Archived { get; set; } public bool CreatedHere { get; set; }
    public DateTime Created { get; set; } public DateTime? Deleted { get; set; }
}

public class WsChat
{
    public string Id { get; set; } public string ProjectId { get; set; } public string Title { get; set; } public string SessionId { get; set; }
    public bool ForkNext { get; set; }   // nhánh mới: lượt đầu tiên tách phiên Claude (--fork-session) để không ảnh hưởng chat gốc
    public bool Pinned { get; set; } public bool Archived { get; set; }
    public DateTime Created { get; set; } public DateTime Updated { get; set; } public DateTime? Deleted { get; set; }
    public List<WsMessage> Messages { get; set; }
}

public class WsStore { public List<WsProject> Projects { get; set; } public List<WsChat> Chats { get; set; } public List<WsTask> Tasks { get; set; } }

static class Workspace
{
    public const string GeneralId = "general";
    static readonly string File_ = Path.Combine(Cleaner.DataDir, "tro-chuyen.json");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly object gate = new object();
    static WsStore store;
    public static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Second Brain Projects"); } }

    public static WsStore Data
    {
        get
        {
            lock (gate)
            {
                if (store != null) return store;
                try { if (File.Exists(File_)) store = Json.Deserialize<WsStore>(File.ReadAllText(File_, Encoding.UTF8)); } catch { }
                if (store == null) store = new WsStore();
                if (store.Projects == null) store.Projects = new List<WsProject>();
                if (store.Chats == null) store.Chats = new List<WsChat>();
                foreach (var c in store.Chats) if (c.Messages == null) c.Messages = new List<WsMessage>();
                if (store.Tasks == null) store.Tasks = new List<WsTask>();
                foreach (var t in store.Tasks.Where(t => t.Status == "running")) t.Status = "interrupted";   // app bị tắt khi việc đang chạy
                if (!store.Projects.Any(p => p.Id == GeneralId))
                    store.Projects.Insert(0, new WsProject { Id = GeneralId, Name = L.T("General", "Chung"), Created = DateTime.Now });
                // Dọn thùng rác quá 30 ngày
                var cutoff = DateTime.Now.AddDays(-30);
                var goneP = store.Projects.Where(p => p.Deleted != null && p.Deleted < cutoff).Select(p => p.Id).ToList();
                store.Projects.RemoveAll(p => goneP.Contains(p.Id));
                store.Chats.RemoveAll(c => goneP.Contains(c.ProjectId) || (c.Deleted != null && c.Deleted < cutoff));
                return store;
            }
        }
    }

    public static void Save()
    {
        lock (gate)
        {
            Directory.CreateDirectory(Cleaner.DataDir);
            var tmp = File_ + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(Data), new UTF8Encoding(false));
            if (File.Exists(File_)) File.Replace(tmp, File_, null); else File.Move(tmp, File_);
        }
    }

    static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 12); }
    public static WsProject Project(string id) { return Data.Projects.FirstOrDefault(p => p.Id == id); }
    public static WsChat Chat(string id) { return Data.Chats.FirstOrDefault(c => c.Id == id); }

    // ---------- Project ----------
    /// <summary>Tạo project: thư mục mới (tuỳ chọn git init) hoặc dùng thư mục có sẵn.</summary>
    public static WsProject CreateProject(string name, string folder, bool createNew, bool gitInit, string instructions)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException(L.T("Give the project a name.", "Hãy đặt tên cho project."));
        string path;
        if (createNew)
        {
            var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
            path = Path.Combine(string.IsNullOrWhiteSpace(folder) ? DefaultRoot : folder, safe);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                throw new IOException(L.T("That folder already exists and is not empty: ", "Thư mục đã có và không trống: ") + path);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "README.md"), "# " + name + "\n\n" + (string.IsNullOrWhiteSpace(instructions) ? "" : instructions.Trim() + "\n"), new UTF8Encoding(false));
            if (gitInit) { Git(path, "init -q"); TaskBranches.EnsureFirstCommit(path); }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException(L.T("Pick an existing folder.", "Hãy chọn một thư mục có sẵn."));
            path = Path.GetFullPath(folder).TrimEnd('\\');
            var dup = Data.Projects.FirstOrDefault(p => p.Deleted == null && string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            if (dup != null) throw new InvalidOperationException(L.T("This folder is already the project ", "Thư mục này đã là project ") + "\"" + dup.Name + "\".");
        }
        var proj = new WsProject { Id = NewId(), Name = name, Path = path, Instructions = instructions, CreatedHere = createNew, Created = DateTime.Now };
        lock (gate) Data.Projects.Add(proj);
        Save();
        Activity.Log("Second Brain", "workspace", L.T("project created", "tạo project"), name + " — " + path);
        return proj;
    }

    static void Git(string dir, string args)
    {
        try
        {
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args) { WorkingDirectory = dir, CreateNoWindow = true, UseShellExecute = false }))
                p.WaitForExit(15000);
        }
        catch { }
    }

    /// <summary>Xoá project: vào thùng rác của app (khôi phục được 30 ngày). Thư mục giữ nguyên trừ khi deleteFolder (chỉ project do app tạo, vào Thùng rác Windows).</summary>
    public static void DeleteProject(string id, bool deleteFolder)
    {
        var p = Project(id);
        if (p == null || p.Id == GeneralId) return;
        if (deleteFolder)
        {
            if (!p.CreatedHere) throw new InvalidOperationException(L.T("Second Brain only deletes folders it created itself.", "Second Brain chỉ xoá thư mục do chính nó tạo ra."));
            if (Directory.Exists(p.Path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        p.Deleted = DateTime.Now;
        Save();
        Activity.Log("Second Brain", "workspace", deleteFolder ? L.T("project + folder to Recycle Bin", "project + thư mục vào Thùng rác") : L.T("project to trash", "project vào thùng rác"), p.Name);
    }

    public static void Restore(string id)
    {
        var p = Project(id); if (p != null) p.Deleted = null;
        var c = Chat(id); if (c != null) { c.Deleted = null; var pp = Project(c.ProjectId); if (pp == null || pp.Deleted != null) c.ProjectId = GeneralId; }
        Save();
    }

    public static void Purge(string id)
    {
        lock (gate)
        {
            Data.Chats.RemoveAll(c => c.Id == id || c.ProjectId == id && Project(id) != null && Project(id).Deleted != null);
            Data.Projects.RemoveAll(p => p.Id == id && p.Id != GeneralId);
        }
        Save();
    }

    // ---------- Chat ----------
    public static WsChat NewChat(string projectId)
    {
        var c = new WsChat { Id = NewId(), ProjectId = projectId ?? GeneralId, Title = L.T("New chat", "Chat mới"), Created = DateTime.Now, Updated = DateTime.Now, Messages = new List<WsMessage>() };
        lock (gate) Data.Chats.Add(c);
        Save();
        return c;
    }

    /// <summary>Phân nhánh: bản sao lịch sử (tới tin nhắn upTo nếu có); lượt kế tiếp tách phiên Claude để chat gốc không bị ảnh hưởng.</summary>
    public static WsChat Branch(string chatId, int upTo = -1)
    {
        var src = Chat(chatId); if (src == null) return null;
        var msgs = upTo >= 0 ? src.Messages.Take(upTo + 1).ToList() : src.Messages.ToList();
        var c = new WsChat { Id = NewId(), ProjectId = src.ProjectId, Title = src.Title + L.T(" (branch)", " (nhánh)"), SessionId = upTo >= 0 && upTo < src.Messages.Count - 1 ? null : src.SessionId,
            ForkNext = true, Created = DateTime.Now, Updated = DateTime.Now, Messages = msgs.Select(m => new WsMessage { Role = m.Role, Text = m.Text, T = m.T }).ToList() };
        lock (gate) Data.Chats.Insert(Data.Chats.IndexOf(src) + 1, c);
        Save();
        return c;
    }

    public static string ExportMarkdown(WsChat c)
    {
        var sb = new StringBuilder("# " + c.Title + "\n\n");
        var p = Project(c.ProjectId); if (p != null && p.Path != null) sb.Append("_" + p.Name + " — " + p.Path + "_\n\n");
        foreach (var m in c.Messages) sb.Append("**" + (m.Role == "user" ? L.T("You", "Bạn") : m.Role == "tool" ? L.T("Step", "Bước") : "Second Brain") + "** · " + m.T.ToString("dd/MM/yyyy HH:mm") + "\n\n" + m.Text + "\n\n");
        return sb.ToString();
    }

    public static IEnumerable<WsProject> LiveProjects(bool archived)
    {
        return Data.Projects.Where(p => p.Deleted == null && (p.Archived == archived || p.Id == GeneralId && !archived))
            .OrderByDescending(p => p.Id == GeneralId).ThenByDescending(p => p.Pinned).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
    }
    public static IEnumerable<WsChat> LiveChats(string projectId, bool archived)
    {
        return Data.Chats.Where(c => c.Deleted == null && c.ProjectId == projectId && c.Archived == archived).OrderByDescending(c => c.Pinned).ThenByDescending(c => c.Updated);
    }
    public static int TrashCount { get { return Data.Projects.Count(p => p.Deleted != null) + Data.Chats.Count(c => c.Deleted != null); } }
}

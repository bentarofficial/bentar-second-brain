// Second Brain: chỉ mục toàn máy (mọi ổ cứng, USB, thẻ nhớ) + máy chủ MCP cho các coding agent
// Chạy "app.exe /mcp" để agent kết nối qua stdio; "app.exe /index" để cập nhật chỉ mục ngầm.
// Nguyên tắc: KHÔNG BAO GIỜ sửa/xoá file nào. Chỉ đọc, tìm và sao chép ra chỗ mới (không ghi đè).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

class BrainFile
{
    public string Path;
    public long Size, Mtime;   // Mtime = DateTime.ToBinary() của lần sửa cuối
    public bool Content;       // true nếu nội dung đã được đọc vào chỉ mục (không chỉ tên file)
    public int[] Terms;
}

class BrainProject
{
    public string Path, Name, Markers, Summary;
    public DateTime LastWrite;
}

static class Brain
{
    public static readonly string IndexFile = Path.Combine(Cleaner.DataDir, "brain.idx");
    const int FormatVersion = 1;
    const long MaxTextSize = 1024 * 1024, MaxDocxSize = 30 * 1024 * 1024;
    const int MaxTermsPerFile = 6000;

    public static readonly HashSet<string> TextExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        ".md", ".markdown", ".txt", ".rst", ".org", ".adoc", ".csv", ".tsv", ".json", ".jsonl", ".yaml", ".yml", ".toml", ".ini", ".xml",
        ".html", ".htm", ".css", ".scss", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".py", ".ipynb", ".cs", ".java", ".kt",
        ".go", ".rs", ".c", ".h", ".cpp", ".hpp", ".swift", ".rb", ".php", ".sh", ".ps1", ".psm1", ".bat", ".cmd", ".sql", ".tex", ".r", ".lua", ".dart", ".srt", ".vtt" };

    // Thư mục hệ thống / thư viện tải về: không lập chỉ mục (agent vẫn duyệt trực tiếp được bằng list_dir/read_file)
    static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Recovery",
        "AppData", "node_modules", ".git", ".svn", ".hg", ".venv", "venv", "env", "__pycache__", "site-packages", ".cache", ".gradle", ".m2",
        ".nuget", "packages", "bower_components", ".next", ".nuxt", "dist", "build", "out", "obj", "target", ".idea", ".vs", ".tox", "vendor",
        "Pods", ".pnpm-store", ".yarn", "WindowsApps", "MSOCache", "Config.Msi", "PerfLogs" };

    // File bí mật: không đọc nội dung vào chỉ mục (vẫn đọc trực tiếp được nếu agent chỉ đích danh)
    static bool IsSecret(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith(".env") || n.EndsWith(".pem") || n.EndsWith(".key") || n.EndsWith(".pfx") || n.StartsWith("id_rsa") || n.StartsWith("id_ed25519")
            || n.Contains("credential") || n.Contains("secret") || n == ".npmrc" || n == ".pypirc" || n == ".netrc";
    }

    static readonly string[] ProjectMarkers = { ".git", "CLAUDE.md", "AGENTS.md", ".claude", "package.json", "pyproject.toml", "requirements.txt",
        "Cargo.toml", "go.mod", "pom.xml", "build.gradle", "composer.json", "Gemfile", ".cursorrules", ".cursor" };
    static readonly string[] OverviewFiles = { "CLAUDE.md", "AGENTS.md", "README.md", "readme.md", "README.txt", "README" };

    static readonly object gate = new object();
    static List<BrainFile> files = new List<BrainFile>();
    static List<BrainProject> projects = new List<BrainProject>();
    static List<string> terms = new List<string>();
    static Dictionary<string, int> termIds = new Dictionary<string, int>();
    static Dictionary<int, List<int>> postings = new Dictionary<int, List<int>>();
    static volatile bool loaded;
    public static DateTime BuiltAt = DateTime.MinValue;
    public static volatile bool Building;
    public static volatile string Progress = "";

    public static int FileCount { get { lock (gate) return files.Count; } }
    public static int ProjectCount { get { lock (gate) return projects.Count; } }

    // ---------- Chuẩn hoá chữ: bỏ dấu tiếng Việt, chữ thường ----------
    public static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }

    public static IEnumerable<string> Tokens(string s)
    {
        var f = Fold(s);
        int start = -1;
        for (int i = 0; i <= f.Length; i++)
        {
            bool w = i < f.Length && char.IsLetterOrDigit(f[i]);
            if (w && start < 0) start = i;
            else if (!w && start >= 0)
            {
                int len = i - start;
                if (len >= 2 && len <= 40) yield return f.Substring(start, len);
                start = -1;
            }
        }
    }

    // ---------- Đọc nội dung file ----------
    public static bool IsDocx(string p) { return p.EndsWith(".docx", StringComparison.OrdinalIgnoreCase); }
    public static bool IsReadable(string p) { return TextExt.Contains(Path.GetExtension(p)) || IsDocx(p) || IsTextByName(p); }
    static bool IsTextByName(string p)
    {
        var n = Path.GetFileName(p);
        return n == "README" || n == "LICENSE" || n == "Makefile" || n == "Dockerfile" || n.StartsWith(".env") || n.StartsWith(".git");
    }

    public static string ReadText(string path)
    {
        if (IsDocx(path)) return ReadDocx(path);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8, true))
        {
            var text = sr.ReadToEnd();
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException(L.T("Binary file, not text.", "File nhị phân, không phải văn bản."));
            return text;
        }
    }

    static string ReadDocx(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var e = zip.GetEntry("word/document.xml");
            if (e == null) return "";
            string xml;
            using (var sr = new StreamReader(e.Open(), Encoding.UTF8)) xml = sr.ReadToEnd();
            xml = Regex.Replace(xml, "</w:p>", "\n");
            xml = Regex.Replace(xml, "<w:tab/>", "\t");
            xml = Regex.Replace(xml, "<[^>]+>", "");
            return System.Net.WebUtility.HtmlDecode(xml);
        }
    }

    // ---------- Ổ đĩa ----------
    public static IEnumerable<DriveInfo> Drives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable); } catch { ok = false; }
            if (ok) yield return d;
        }
    }

    public static bool IsOnline(string path)
    {
        try { var root = Path.GetPathRoot(path); return !string.IsNullOrEmpty(root) && Directory.Exists(root); } catch { return false; }
    }

    // ---------- Lưu / nạp chỉ mục ----------
    // Nạp chỉ mục dưới khoá riêng: giao diện đọc số liệu (khoá gate) không bị chặn trong lúc nạp file lớn
    static readonly object loadGate = new object();
    public static void EnsureLoaded()
    {
        if (loaded) return;
        lock (loadGate)
        {
            if (loaded) return;
            try { if (File.Exists(IndexFile)) LoadFrom(IndexFile); }
            catch { Install(new List<BrainFile>(), new List<BrainProject>(), new List<string>()); }
            loaded = true;
        }
    }

    static void LoadFrom(string file)
    {
        using (var br = new BinaryReader(new BufferedStream(File.OpenRead(file), 1 << 16), Encoding.UTF8))
        {
            if (br.ReadInt32() != FormatVersion) return;
            BuiltAt = DateTime.FromBinary(br.ReadInt64());
            int nt = br.ReadInt32();
            var t = new List<string>(nt);
            for (int i = 0; i < nt; i++) t.Add(br.ReadString());
            int nf = br.ReadInt32();
            var fl = new List<BrainFile>(nf);
            for (int i = 0; i < nf; i++)
            {
                var f = new BrainFile { Path = br.ReadString(), Size = br.ReadInt64(), Mtime = br.ReadInt64(), Content = br.ReadBoolean() };
                int k = br.ReadInt32();
                f.Terms = new int[k];
                for (int j = 0; j < k; j++) f.Terms[j] = br.ReadInt32();
                fl.Add(f);
            }
            int np = br.ReadInt32();
            var pl = new List<BrainProject>(np);
            for (int i = 0; i < np; i++)
                pl.Add(new BrainProject { Path = br.ReadString(), Name = br.ReadString(), Markers = br.ReadString(), Summary = br.ReadString(), LastWrite = DateTime.FromBinary(br.ReadInt64()) });
            Install(fl, pl, t);
        }
    }

    // Gắn dữ liệu mới vào bộ nhớ và dựng lại danh sách tra ngược (từ -> các file chứa từ đó)
    static void Install(List<BrainFile> fl, List<BrainProject> pl, List<string> t)
    {
        var ids = new Dictionary<string, int>(t.Count);
        for (int i = 0; i < t.Count; i++) ids[t[i]] = i;
        var post = new Dictionary<int, List<int>>(t.Count);
        for (int i = 0; i < fl.Count; i++)
            foreach (var id in fl[i].Terms)
            {
                List<int> l;
                if (!post.TryGetValue(id, out l)) post[id] = l = new List<int>();
                l.Add(i);
            }
        lock (gate) { files = fl; projects = pl; terms = t; termIds = ids; postings = post; }
    }

    static void Save(DateTime builtAt, List<BrainFile> fl, List<BrainProject> pl, List<string> t)
    {
        Directory.CreateDirectory(Cleaner.DataDir);
        string tmp = IndexFile + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
        using (var bw = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 16), Encoding.UTF8))
        {
            bw.Write(FormatVersion);
            bw.Write(builtAt.ToBinary());
            bw.Write(t.Count);
            foreach (var s in t) bw.Write(s);
            bw.Write(fl.Count);
            foreach (var f in fl)
            {
                bw.Write(f.Path); bw.Write(f.Size); bw.Write(f.Mtime); bw.Write(f.Content);
                bw.Write(f.Terms.Length);
                foreach (var id in f.Terms) bw.Write(id);
            }
            bw.Write(pl.Count);
            foreach (var p in pl) { bw.Write(p.Path); bw.Write(p.Name); bw.Write(p.Markers); bw.Write(p.Summary ?? ""); bw.Write(p.LastWrite.ToBinary()); }
        }
        if (File.Exists(IndexFile)) File.Replace(tmp, IndexFile, null);
        else File.Move(tmp, IndexFile);
    }

    // ---------- Lập chỉ mục ----------
    // Chỉ đọc lại file mới/đã thay đổi; ổ USB/thẻ nhớ đang rút ra vẫn giữ dữ liệu cũ để agent biết nó từng có gì
    public static void Build()
    {
        EnsureLoaded();
        lock (gate) { if (Building) return; Building = true; }
        try
        {
            List<BrainFile> oldFiles; List<string> oldTerms; List<BrainProject> oldProjects;
            lock (gate) { oldFiles = files; oldTerms = terms; oldProjects = projects; }
            var old = new Dictionary<string, BrainFile>(oldFiles.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var f in oldFiles) old[f.Path] = f;

            var newTerms = new List<string>();
            var newIds = new Dictionary<string, int>();
            Func<string, int> idOf = s => { int id; if (!newIds.TryGetValue(s, out id)) { id = newTerms.Count; newTerms.Add(s); newIds[s] = id; } return id; };
            var fl = new List<BrainFile>();
            var pl = new List<BrainProject>();
            var online = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in Drives())
            {
                online.Add(d.RootDirectory.FullName);
                Walk(d.RootDirectory, 0, old, oldTerms, idOf, fl, pl);
            }
            // Giữ lại dữ liệu của ổ đang không cắm
            foreach (var f in oldFiles)
                if (!online.Contains(Path.GetPathRoot(f.Path)))
                    fl.Add(new BrainFile { Path = f.Path, Size = f.Size, Mtime = f.Mtime, Content = f.Content, Terms = f.Terms.Select(i => idOf(oldTerms[i])).ToArray() });
            foreach (var p in oldProjects)
                if (!online.Contains(Path.GetPathRoot(p.Path))) pl.Add(p);

            var now = DateTime.Now;
            Progress = L.T("Saving index...", "Đang lưu chỉ mục...");
            Save(now, fl, pl, newTerms);
            Install(fl, pl, newTerms);
            BuiltAt = now;
            Progress = "";
        }
        finally { Building = false; }
    }

    static void Walk(DirectoryInfo dir, int depth, Dictionary<string, BrainFile> old, List<string> oldTerms, Func<string, int> idOf,
                     List<BrainFile> fl, List<BrainProject> pl)
    {
        if (depth > 40) return;
        FileSystemInfo[] entries;
        try
        {
            if (depth > 0 && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return;   // bỏ junction/symlink để không lặp vô hạn
            entries = dir.GetFileSystemInfos();
        }
        catch { return; }

        var markers = entries.Where(e => ProjectMarkers.Contains(e.Name, StringComparer.OrdinalIgnoreCase)
                                         || e.Name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                             .Select(e => e.Name).ToList();
        if (markers.Count > 0 && depth > 0) pl.Add(MakeProject(dir, entries, markers));

        Progress = L.T("Indexing: ", "Đang lập chỉ mục: ") + dir.FullName;
        var dirTokens = Tokens(dir.Name).ToList();
        foreach (var e in entries)
        {
            var fi = e as FileInfo;
            if (fi == null) continue;
            try
            {
                if ((fi.Attributes & (FileAttributes.System | FileAttributes.Offline)) != 0) continue;
                long mt = fi.LastWriteTime.ToBinary();
                BrainFile prev;
                if (old.TryGetValue(fi.FullName, out prev) && prev.Size == fi.Length && prev.Mtime == mt)
                {
                    fl.Add(new BrainFile { Path = prev.Path, Size = prev.Size, Mtime = prev.Mtime, Content = prev.Content, Terms = prev.Terms.Select(i => idOf(oldTerms[i])).ToArray() });
                    continue;
                }
                var set = new HashSet<string>(Tokens(Path.GetFileNameWithoutExtension(fi.Name)));
                foreach (var t in dirTokens) set.Add(t);
                var ext = fi.Extension.TrimStart('.').ToLowerInvariant();
                if (ext.Length >= 2) set.Add(ext);
                bool content = false;
                bool readable = TextExt.Contains(fi.Extension) || IsTextByName(fi.FullName);
                if (!IsSecret(fi.Name) && ((readable && fi.Length <= MaxTextSize) || (IsDocx(fi.Name) && fi.Length <= MaxDocxSize)))
                {
                    try
                    {
                        foreach (var t in Tokens(ReadText(fi.FullName))) { set.Add(t); if (set.Count >= MaxTermsPerFile) break; }
                        content = true;
                    }
                    catch { }
                }
                fl.Add(new BrainFile { Path = fi.FullName, Size = fi.Length, Mtime = mt, Content = content, Terms = set.Select(idOf).ToArray() });
            }
            catch { }
        }
        foreach (var e in entries)
        {
            var di = e as DirectoryInfo;
            if (di == null || SkipDirs.Contains(di.Name)) continue;
            try { if ((di.Attributes & FileAttributes.System) != 0 && depth == 0) continue; } catch { continue; }
            Walk(di, depth + 1, old, oldTerms, idOf, fl, pl);
        }
    }

    static BrainProject MakeProject(DirectoryInfo dir, FileSystemInfo[] entries, List<string> markers)
    {
        string summary = "";
        foreach (var name in OverviewFiles)
        {
            var f = entries.FirstOrDefault(e => e is FileInfo && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (f == null) continue;
            try
            {
                var lines = File.ReadLines(f.FullName).Select(l => l.Trim()).Where(l => l.Length > 0).Take(8);
                summary = string.Join(" ", lines);
                if (summary.Length > 400) summary = summary.Substring(0, 400) + "…";
                break;
            }
            catch { }
        }
        DateTime last = dir.LastWriteTime;
        foreach (var e in entries) { try { if (e.LastWriteTime > last) last = e.LastWriteTime; } catch { } }
        return new BrainProject { Path = dir.FullName, Name = dir.Name, Markers = string.Join(", ", markers), Summary = summary, LastWrite = last };
    }

    // Tự cập nhật ngầm nếu chỉ mục đã cũ (gọi mỗi khi agent tra cứu)
    public static void RefreshIfStale(TimeSpan maxAge)
    {
        EnsureLoaded();
        if (!Building && DateTime.Now - BuiltAt > maxAge) Task.Run(() => { try { Build(); } catch { } });
    }

    // ---------- Tra cứu ----------
    public class Hit { public BrainFile File; public double Score; public string Snippet; public int Line; }

    public static List<Hit> Search(string query, string under, string ext, bool nameOnly, int limit)
    {
        EnsureLoaded();
        var q = Tokens(query).Distinct().ToList();
        if (q.Count == 0) return new List<Hit>();
        List<BrainFile> fl; Dictionary<string, int> ids; Dictionary<int, List<int>> post;
        lock (gate) { fl = files; ids = termIds; post = postings; }

        var count = new Dictionary<int, int>();
        foreach (var t in q)
        {
            int id; List<int> l;
            if (!ids.TryGetValue(t, out id) || !post.TryGetValue(id, out l)) continue;
            foreach (var i in l) { int c; count.TryGetValue(i, out c); count[i] = c + 1; }
        }
        string underF = string.IsNullOrEmpty(under) ? null : under.TrimEnd('\\', '/') + "\\";
        string extF = string.IsNullOrEmpty(ext) ? null : "." + ext.TrimStart('.');
        var qset = new HashSet<string>(q);
        var scored = new List<Hit>();
        foreach (var kv in count)
        {
            var f = fl[kv.Key];
            if (underF != null && !(f.Path + "\\").StartsWith(underF, StringComparison.OrdinalIgnoreCase)) continue;
            if (extF != null && !f.Path.EndsWith(extF, StringComparison.OrdinalIgnoreCase)) continue;
            int nameHits = Tokens(Path.GetFileName(f.Path)).Distinct().Count(qset.Contains);
            if (nameOnly && nameHits == 0) continue;
            double score = kv.Value * 10 + nameHits * 6;
            if (kv.Value == q.Count) score += 30;   // khớp đủ mọi từ
            var e = Path.GetExtension(f.Path).ToLowerInvariant();
            if (e == ".md" || e == ".txt" || e == ".docx") score += 4;
            double ageDays = (DateTime.Now - DateTime.FromBinary(f.Mtime)).TotalDays;
            score += Math.Max(0, 5 - ageDays / 120);
            scored.Add(new Hit { File = f, Score = score });
        }
        var top = scored.OrderByDescending(h => h.Score).Take(limit).ToList();
        foreach (var h in top) if (h.File.Content && IsOnline(h.File.Path)) MakeSnippet(h, q);
        return top;
    }

    static void MakeSnippet(Hit h, List<string> q)
    {
        try
        {
            var lines = ReadText(h.File.Path).Split('\n');
            int best = -1, bestCount = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var toks = new HashSet<string>(Tokens(lines[i]));
                int c = q.Count(toks.Contains);
                if (c > bestCount) { bestCount = c; best = i; if (c == q.Count) break; }
            }
            if (best < 0) return;
            int from = Math.Max(0, best - 1), to = Math.Min(lines.Length - 1, best + 1);
            var sb = new StringBuilder();
            for (int i = from; i <= to; i++)
            {
                var l = lines[i].TrimEnd('\r');
                if (l.Length > 240) l = l.Substring(0, 240) + "…";
                sb.Append(i + 1).Append(": ").AppendLine(l);
            }
            h.Snippet = sb.ToString().TrimEnd();
            h.Line = best + 1;
        }
        catch { }
    }

    // Dự án sâu nhất chứa đường dẫn này
    public static string ProjectOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var best = Projects(null).Where(p => (path.TrimEnd('\\') + "\\").StartsWith(p.Path + "\\", StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(p => p.Path.Length).FirstOrDefault();
        return best == null ? null : best.Path;
    }

    public static List<BrainProject> Projects(string filter)
    {
        EnsureLoaded();
        List<BrainProject> pl;
        lock (gate) pl = projects;
        IEnumerable<BrainProject> r = pl;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var q = Tokens(filter).ToList();
            r = r.Where(p => { var t = new HashSet<string>(Tokens(p.Path + " " + p.Summary)); return q.All(t.Contains); });
        }
        return r.OrderByDescending(p => p.LastWrite).ToList();
    }

    // ---------- Sao chép (không bao giờ ghi đè) ----------
    public static string Copy(string source, string destination)
    {
        source = Path.GetFullPath(source);
        // Đích kết thúc bằng "\" hoặc "/" nghĩa là "chép vào thư mục này" (tạo thư mục nếu chưa có)
        bool intoFolder = destination.EndsWith("\\") || destination.EndsWith("/");
        destination = Path.GetFullPath(destination);
        bool isDir = Directory.Exists(source);
        if (!isDir && !File.Exists(source)) throw new FileNotFoundException(L.T("Not found: ", "Không tìm thấy: ") + source);
        if (intoFolder) Directory.CreateDirectory(destination);
        else if (!Directory.Exists(Path.GetDirectoryName(destination.TrimEnd('\\')))) Directory.CreateDirectory(Path.GetDirectoryName(destination.TrimEnd('\\')));
        if (Directory.Exists(destination)) destination = Path.Combine(destination, Path.GetFileName(source.TrimEnd('\\')));
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException(L.T("Destination exists; Second Brain does not overwrite: ", "Đích đã tồn tại, Second Brain không ghi đè: ") + destination);
        if (isDir && (destination + "\\").StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("Cannot copy a folder into itself.", "Không thể chép một thư mục vào bên trong chính nó."));

        int n = 0; long bytes = 0;
        if (isDir)
        {
            foreach (var d in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, d.Substring(source.TrimEnd('\\').Length + 1)));
            Directory.CreateDirectory(destination);
            foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(destination, f.Substring(source.TrimEnd('\\').Length + 1));
                File.Copy(f, to, false); n++; bytes += new FileInfo(f).Length;
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, false); n = 1; bytes = new FileInfo(source).Length;
        }
        Cleaner.Log("[Second Brain] Agent sao chép " + source + " -> " + destination + " (" + n + " file, " + Cleaner.Fmt(bytes) + ")");
        return L.T("Copied ", "Đã sao chép ") + n + " file (" + Cleaner.Fmt(bytes) + L.T(") to ", ") tới ") + destination;
    }
}

// Cắm Second Brain vào các agent có trên máy (Claude Code, Claude Desktop, Cursor, Windsurf, Codex, Gemini CLI)
static class AgentLinks
{
    public const string ServerName = "second-brain";
    static string Exe { get { return Installer.Exe; } }
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Các file cấu hình MCP dạng JSON (mục "mcpServers") của từng agent
    static IEnumerable<Tuple<string, string>> JsonTargets()
    {
        var pk = Path.Combine(Local, "Packages");
        if (Directory.Exists(pk))
            foreach (var pkg in Directory.GetDirectories(pk, "Claude_*"))
                yield return Tuple.Create("Claude Desktop (bản Store)", Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
        yield return Tuple.Create("Claude Desktop", Path.Combine(Roaming, "Claude", "claude_desktop_config.json"));
        yield return Tuple.Create("Cursor", Path.Combine(Home, ".cursor", "mcp.json"));
        yield return Tuple.Create("Windsurf", Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json"));
        yield return Tuple.Create("Gemini CLI", Path.Combine(Home, ".gemini", "settings.json"));
    }
    static string CodexFile { get { return Path.Combine(Home, ".codex", "config.toml"); } }

    // Kết nối với mọi agent có trên máy. onlyExisting = chỉ cập nhật đường dẫn ở những agent đã kết nối từ trước
    // Nối MCP xong thì cài thêm hook + skill + hướng dẫn, và ghi nhận người dùng muốn nối (để trạm phát tự nối lại nếu bị mất)
    static string WithExtras(string agent, string result)
    {
        if (result == null) return null;
        try { L.Set("agent." + agent, "1"); var x = AgentHooks.Install(agent); if (x != null) result += " + " + x; } catch (Exception ex) { result += L.T(" (extras failed: ", " (cài thêm lỗi: ") + ex.Message + ")"; }
        return result;
    }

    public static List<string> ConnectAll(bool onlyExisting)
    {
        try { AgentHooks.EnsureShims(); } catch { }
        var r = new List<string> { Try("Claude Code", () => WithExtras("Claude Code", ClaudeCode(onlyExisting))) };
        bool desktopOpen = ClaudeDesktopRunning();
        foreach (var t in JsonTargets())
        {
            var f = t.Item2;
            // Claude Desktop đang mở sẽ ghi đè file cấu hình của nó và làm mất mục vừa thêm
            if (desktopOpen && t.Item1.StartsWith("Claude Desktop") && Directory.Exists(Path.GetDirectoryName(f)))
            { if (!onlyExisting) r.Add(t.Item1 + L.T(": running – fully quit Claude Desktop (including the tray icon) and connect again", ": đang mở – thoát hẳn Claude Desktop (cả biểu tượng ở khay hệ thống) rồi bấm kết nối lại")); continue; }
            var name = t.Item1;
            r.Add(Try(name, () => WithExtras(name, JsonConnect(f, onlyExisting))));
        }
        r.Add(Try("Codex", () => WithExtras("Codex", CodexConnect(onlyExisting))));
        return r.Where(x => x != null).ToList();
    }

    // Gỡ đúng mục "second-brain" khỏi cấu hình các agent, giữ nguyên mọi thứ khác
    public static List<string> DisconnectAll()
    {
        foreach (var a in new[] { "Claude Code", "Codex", "Gemini CLI", "Cursor", "Windsurf", "Claude Desktop", "Claude Desktop (bản Store)" }) { L.Set("agent." + a, "0"); AgentHooks.Uninstall(a); }
        var r = new List<string>();
        r.Add(Try("Claude Code", () => Cmd("where claude").Item1 == 0 && Cmd("claude mcp remove --scope user " + ServerName).Item1 == 0 ? L.T("disconnected", "đã ngắt") : null));
        foreach (var t in JsonTargets()) { var f = t.Item2; r.Add(Try(t.Item1, () => JsonDisconnect(f))); }
        r.Add(Try("Codex", () =>
        {
            if (!File.Exists(CodexFile)) return null;
            var text = File.ReadAllText(CodexFile, Encoding.UTF8);
            var updated = CodexBlock.Replace(text, "");
            if (updated == text) return null;
            Write(CodexFile, updated.TrimEnd('\n') + "\n");
            return L.T("disconnected", "đã ngắt");
        }));
        return r.Where(x => x != null).ToList();
    }

    // ---------- Từng agent riêng lẻ (cho trang Cài đặt) ----------
    public class Agent { public string Name; public bool Installed, Connected; }

    public static List<Agent> List()
    {
        var r = new List<Agent>();
        bool cc = Cmd("where claude").Item1 == 0;
        r.Add(new Agent { Name = "Claude Code", Installed = cc, Connected = cc && Cmd("claude mcp get " + ServerName).Item1 == 0 });
        foreach (var t in JsonTargets())
        {
            bool inst = Directory.Exists(Path.GetDirectoryName(t.Item2));
            bool conn = false;
            try { conn = File.Exists(t.Item2) && JsonEntry.IsMatch(File.ReadAllText(t.Item2, Encoding.UTF8)); } catch { }
            if (r.Any(a => a.Name == t.Item1)) continue;
            r.Add(new Agent { Name = t.Item1, Installed = inst, Connected = conn });
        }
        bool cx = Directory.Exists(Path.GetDirectoryName(CodexFile)), cxc = false;
        try { cxc = File.Exists(CodexFile) && CodexBlock.IsMatch(File.ReadAllText(CodexFile, Encoding.UTF8)); } catch { }
        r.Add(new Agent { Name = "Codex", Installed = cx, Connected = cxc });
        return r;
    }

    public static string Connect(string name)
    {
        try { AgentHooks.EnsureShims(); } catch { }
        if (name == "Claude Code") return WithExtras(name, ClaudeCode(false));
        if (name == "Codex") return WithExtras(name, CodexConnect(false));
        if (name.StartsWith("Claude Desktop") && ClaudeDesktopRunning())
            return L.T("Fully quit Claude Desktop (including the tray icon) and try again.", "Hãy thoát hẳn Claude Desktop (cả biểu tượng ở khay hệ thống) rồi thử lại.");
        foreach (var t in JsonTargets()) if (t.Item1 == name) return WithExtras(name, JsonConnect(t.Item2, false));
        return null;
    }

    // Chỉ nối lại MCP (không cài hook/skill/hướng dẫn) – dùng khi tự sửa kết nối cho agent đã nối từ bản cũ
    public static string ConnectMcpOnly(string name)
    {
        if (name == "Claude Code") return ClaudeCode(false);
        if (name == "Codex") return CodexConnect(false);
        if (name.StartsWith("Claude Desktop") && ClaudeDesktopRunning()) return null;
        foreach (var t in JsonTargets()) if (t.Item1 == name) return JsonConnect(t.Item2, false);
        return null;
    }

    public static string Disconnect(string name)
    {
        L.Set("agent." + name, "0"); AgentHooks.Uninstall(name);
        if (name == "Claude Code") return Cmd("claude mcp remove --scope user " + ServerName).Item1 == 0 ? L.T("disconnected", "đã ngắt") : null;
        if (name == "Codex")
        {
            if (!File.Exists(CodexFile)) return null;
            var text = File.ReadAllText(CodexFile, Encoding.UTF8);
            var updated = CodexBlock.Replace(text, "");
            if (updated == text) return null;
            Write(CodexFile, updated.TrimEnd('\n') + "\n");
            return L.T("disconnected", "đã ngắt");
        }
        if (name.StartsWith("Claude Desktop") && ClaudeDesktopRunning())
            return L.T("Fully quit Claude Desktop (including the tray icon) and try again.", "Hãy thoát hẳn Claude Desktop (cả biểu tượng ở khay hệ thống) rồi thử lại.");
        foreach (var t in JsonTargets()) if (t.Item1 == name) return JsonDisconnect(t.Item2);
        return null;
    }

    static bool ClaudeDesktopRunning()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("claude"))
            try
            {
                var path = p.MainModule.FileName;
                if (path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("AnthropicClaude", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
        return false;
    }

    static string Try(string agent, Func<string> f)
    {
        try { var s = f(); return s == null ? null : agent + ": " + s; }
        catch (Exception ex) { return agent + L.T(": error – ", ": lỗi – ") + ex.Message; }
    }

    static Tuple<int, string> Cmd(string c)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + c) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = System.Diagnostics.Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd() + err.Result;
            p.WaitForExit();
            return Tuple.Create(p.ExitCode, o);
        }
    }

    static string ClaudeCode(bool onlyExisting)
    {
        if (Cmd("where claude").Item1 != 0) return null;   // chưa cài Claude Code
        var get = Cmd("claude mcp get " + ServerName);
        if (get.Item1 == 0)
        {
            if (get.Item2.IndexOf(Exe, StringComparison.OrdinalIgnoreCase) >= 0) return L.T("connected", "đã kết nối");
            Cmd("claude mcp remove --scope user " + ServerName);
        }
        else if (onlyExisting) return null;
        var r = Cmd("claude mcp add --scope user " + ServerName + " -- \"" + Exe + "\" /mcp");
        if (r.Item1 != 0) throw new Exception(r.Item2.Trim());
        return L.T("connected (all projects)", "đã kết nối (mọi dự án)");
    }

    static readonly Regex JsonEntry = new Regex("\"" + ServerName + "\"\\s*:\\s*\\{[^{}]*\\}");
    static readonly Regex CodexBlock = new Regex("\\n*\\[mcp_servers\\." + ServerName + "\\](?:\\r?\\n(?!\\[).*)*\\r?\\n?");

    static void Write(string file, string text)
    {
        if (File.Exists(file)) File.Copy(file, file + ".bak-secondbrain", true);
        File.WriteAllText(file, text, new UTF8Encoding(false));
    }

    static void CheckJson(string text) { new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text); }

    // Sửa bằng cách chèn/thay chữ để giữ nguyên định dạng và các cài đặt khác của file
    static string JsonConnect(string file, bool onlyExisting)
    {
        if (!Directory.Exists(Path.GetDirectoryName(file))) return null;   // agent chưa cài
        string entry = "\"" + ServerName + "\": { \"command\": \"" + Exe.Replace("\\", "\\\\") + "\", \"args\": [\"/mcp\"] }";
        string text = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "";
        var m = JsonEntry.Match(text);
        string updated;
        if (m.Success)
        {
            if (m.Value.Contains(Exe.Replace("\\", "\\\\"))) return L.T("connected", "đã kết nối");
            updated = text.Substring(0, m.Index) + entry + text.Substring(m.Index + m.Length);
        }
        else if (onlyExisting) return null;
        else if (text.Trim().Length == 0) updated = "{\n  \"mcpServers\": {\n    " + entry + "\n  }\n}\n";
        else
        {
            var ms = Regex.Match(text, "\"mcpServers\"\\s*:\\s*\\{");
            if (ms.Success)
            {
                int at = ms.Index + ms.Length;
                updated = text.Insert(at, "\n    " + entry + (Regex.IsMatch(text.Substring(at), "^\\s*\\}") ? "\n  " : ","));
            }
            else
            {
                int at = text.IndexOf('{');
                if (at < 0) throw new InvalidDataException(L.T("Invalid config file: ", "File cấu hình không hợp lệ: ") + file);
                updated = text.Insert(at + 1, "\n  \"mcpServers\": {\n    " + entry + "\n  }" + (Regex.IsMatch(text.Substring(at + 1), "^\\s*\\}") ? "\n" : ","));
            }
        }
        CheckJson(updated);   // chỉ ghi khi JSON vẫn hợp lệ
        Write(file, updated);
        return L.T("connected (restart the app to pick it up)", "đã kết nối (khởi động lại app để nhận)");
    }

    static string JsonDisconnect(string file)
    {
        if (!File.Exists(file)) return null;
        string text = File.ReadAllText(file, Encoding.UTF8);
        var m = JsonEntry.Match(text);
        if (!m.Success) return null;
        // Bỏ luôn dấu phẩy đi kèm (phía sau, hoặc phía trước nếu là mục cuối)
        int start = m.Index, end = m.Index + m.Length;
        var after = Regex.Match(text.Substring(end), "^\\s*,");
        if (after.Success) end += after.Length;
        else
        {
            var before = Regex.Match(text.Substring(0, start), ",\\s*$");
            if (before.Success) start = before.Index;
        }
        string updated = text.Substring(0, start) + text.Substring(end);
        CheckJson(updated);
        Write(file, updated);
        return L.T("disconnected", "đã ngắt");
    }

    static string CodexConnect(bool onlyExisting)
    {
        if (!Directory.Exists(Path.GetDirectoryName(CodexFile))) return null;
        string text = File.Exists(CodexFile) ? File.ReadAllText(CodexFile, Encoding.UTF8) : "";
        string block = "\n\n[mcp_servers." + ServerName + "]\ncommand = '" + Exe + "'\nargs = [\"/mcp\"]\n";
        var m = CodexBlock.Match(text);
        if (m.Success)
        {
            if (m.Value.Contains("'" + Exe + "'")) return L.T("connected", "đã kết nối");
            Write(CodexFile, (text.Substring(0, m.Index) + block + text.Substring(m.Index + m.Length)).TrimStart('\n'));
            return L.T("connected", "đã kết nối");
        }
        if (onlyExisting) return null;
        Write(CodexFile, (text.TrimEnd('\n') + block).TrimStart('\n'));
        return L.T("connected", "đã kết nối");
    }
}

// Máy chủ MCP (Model Context Protocol) qua stdio: mỗi dòng là một thông điệp JSON-RPC 2.0
static class McpServer
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
    static StreamWriter output;
    static readonly object writeGate = new object();

    public static string Instructions { get { return L.T(
        "Bentar Second Brain indexes every drive on this PC (documents, notes and source code of ALL projects) and keeps a long-term memory shared by all agents. " +
        "When the user mentions documents, research, notes, code or another project outside the current folder, use search / list_projects to find it yourself instead of asking for a path; use read_file to read and copy to bring it into this project (copy never overwrites). Search ignores Vietnamese accents. " +
        "SOURCE TRUST: every result is labelled ✓ trusted (tests/CI + releases, or marked by the user), ⚠ unverified, or hidden if excluded (scratch/temp). Only follow ✓ trusted code as an example; read ⚠ code critically. " +
        "MEMORY: call recall at the start of a task; it only returns VERIFIED memories. Save with remember ONLY when you can give evidence the app can check (file + exact quote, commit, or url + exact quote) – unproven notes wait for the user's approval and are not shared. Never store secrets. " +
        "Shell alternative when MCP is unavailable: sb search \"…\", sb read <path>, sb copy <src> <dst>, sb recall \"…\", sb remember \"…\" --evidence \"…\".",
        "Bentar Second Brain lập chỉ mục mọi ổ đĩa trên máy (tài liệu, ghi chú, mã nguồn của TẤT CẢ dự án) và giữ một trí nhớ dài hạn dùng chung cho mọi agent. " +
        "Khi người dùng nhắc tới tài liệu, nghiên cứu, ghi chú, code hay dự án khác ngoài thư mục hiện tại, hãy dùng search / list_projects để tự tìm thay vì hỏi đường dẫn; dùng read_file để đọc và copy để chép vào dự án này (copy không bao giờ ghi đè). Tìm kiếm không phân biệt dấu. " +
        "ĐỘ TIN CẬY NGUỒN: mọi kết quả được gắn nhãn ✓ tin cậy (có test/CI và đã phát hành, hoặc người dùng đánh dấu), ⚠ chưa kiểm chứng, hoặc bị ẩn nếu bị loại trừ (nháp/tạm). Chỉ học theo code ✓ tin cậy; đọc code ⚠ với thái độ phê phán. " +
        "TRÍ NHỚ: gọi recall khi bắt đầu việc; nó chỉ trả về ghi nhớ ĐÃ XÁC MINH. Chỉ gọi remember khi đưa được bằng chứng app tự kiểm tra được (file + trích nguyên văn, commit, hoặc url + trích nguyên văn) – điều chưa chứng minh sẽ chờ người dùng duyệt và chưa được chia sẻ. Không bao giờ lưu bí mật. " +
        "Khi không có MCP, dùng dòng lệnh: sb search \"…\", sb read <đường dẫn>, sb copy <nguồn> <đích>, sb recall \"…\", sb remember \"…\" --evidence \"…\"."); } }

    public static void Run()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Brain.EnsureLoaded();
        Brain.RefreshIfStale(TimeSpan.FromMinutes(30));
        string line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            Dictionary<string, object> msg;
            try { msg = Json.Deserialize<Dictionary<string, object>>(line); }
            catch { Send(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", null }, { "error", Err(-32700, "Parse error") } }); continue; }
            var m = msg;
            // Mỗi yêu cầu chạy riêng để một lệnh tìm chậm không chặn các lệnh khác
            Task.Run(() =>
            {
                string named;
                var resp = Process(m, stdioClient, "mcp", out named);
                if (named != null) stdioClient = named;
                if (resp != null) Send(resp);
            });
        }
    }
    static volatile string stdioClient = "agent";

    public static Dictionary<string, object> Err(int code, string message) { return new Dictionary<string, object> { { "code", code }, { "message", message } }; }

    static void Send(object o)
    {
        var s = Json.Serialize(o);
        lock (writeGate) output.WriteLine(s);
    }

    // Xử lý một thông điệp JSON-RPC của MCP, dùng chung cho mọi ổ cắm (stdio, HTTP).
    // client: tên thiết bị đang cắm; named: tên mới nếu thông điệp là "initialize". Trả null với thông báo (không cần trả lời).
    public static Dictionary<string, object> Process(Dictionary<string, object> msg, string client, string channel, out string named)
    {
        named = null;
        object id; msg.TryGetValue("id", out id);
        object mo; msg.TryGetValue("method", out mo);
        string method = mo as string;
        if (method == null) return null;                  // phản hồi từ client: bỏ qua
        bool isRequest = msg.ContainsKey("id");
        var p = (msg.ContainsKey("params") ? msg["params"] : null) as Dictionary<string, object> ?? new Dictionary<string, object>();
        object result = null; Dictionary<string, object> error = null;
        try
        {
            switch (method)
            {
                case "initialize":
                    object pv; p.TryGetValue("protocolVersion", out pv);
                    var ci = (p.ContainsKey("clientInfo") ? p["clientInfo"] : null) as Dictionary<string, object>;
                    if (ci != null && ci.ContainsKey("name")) named = client = Convert.ToString(ci["name"]);
                    Activity.Log(client, channel, "cắm vào", "");
                    result = new Dictionary<string, object> {
                        { "protocolVersion", pv as string ?? "2025-06-18" },
                        { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                        { "serverInfo", new Dictionary<string, object> { { "name", "bentar-second-brain" }, { "version", Updater.Current.ToString(3) } } },
                        { "instructions", Instructions } };
                    break;
                case "ping": result = new Dictionary<string, object>(); break;
                case "tools/list": result = new Dictionary<string, object> { { "tools", ToolList() } }; break;
                case "tools/call":
                    string name = p.ContainsKey("name") ? p["name"] as string : null;
                    var args = (p.ContainsKey("arguments") ? p["arguments"] : null) as Dictionary<string, object> ?? new Dictionary<string, object>();
                    string text; bool isError;
                    text = Invoke(client, channel, name, args, out isError);
                    result = new Dictionary<string, object> {
                        { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } },
                        { "isError", isError } };
                    break;
                default:
                    if (method.StartsWith("notifications/")) return null;
                    error = Err(-32601, "Method not found: " + method);
                    break;
            }
        }
        catch (Exception ex) { error = Err(-32603, ex.Message); }
        if (!isRequest) return null;
        var resp = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id } };
        if (error != null) resp["error"] = error; else resp["result"] = result;
        return resp;
    }

    // Gọi một công cụ cho một thiết bị: ghi tên thiết bị vào trí nhớ và ghi nhật ký hoạt động (đồng hồ điện)
    public static string Invoke(string client, string channel, string name, Dictionary<string, object> args, out bool isError)
    {
        Memory.AgentName = client;
        isError = false;
        string text;
        try { text = CallTool(name, args); }
        catch (Exception ex) { text = L.T("Error: ", "Lỗi: ") + ex.Message; isError = true; }
        Activity.Log(client, channel, name, Activity.Summary(name, args) + (isError ? L.T(" (error)", " (lỗi)") : ""));
        return text;
    }

    public static object[] Tools() { return ToolList(); }

    static Dictionary<string, object> Tool(string name, string desc, Dictionary<string, object> props, params string[] required)
    {
        return new Dictionary<string, object> { { "name", name }, { "description", desc },
            { "inputSchema", new Dictionary<string, object> { { "type", "object" }, { "properties", props }, { "required", required } } } };
    }
    static Dictionary<string, object> Prop(string type, string desc) { return new Dictionary<string, object> { { "type", type }, { "description", desc } }; }

    static object[] ToolList()
    {
        return new object[] {
            Tool("search", L.T("Search files across the whole PC (all drives, USB sticks, memory cards) by content and file name. Vietnamese accents are ignored. Returns the path, the containing project and a snippet with matching lines.", "Tìm file trên toàn máy (mọi ổ, USB, thẻ nhớ) theo nội dung và tên file. Không phân biệt dấu tiếng Việt. Trả về đường dẫn, dự án chứa file và đoạn trích có dòng khớp."),
                new Dictionary<string, object> {
                    { "query", Prop("string", L.T("Keywords, e.g. 'coffee market research' or 'oauth refresh token'", "Từ khoá cần tìm, ví dụ: 'nghiên cứu thị trường cà phê' hoặc 'oauth refresh token'")) },
                    { "path", Prop("string", L.T("Only search inside this folder (optional), e.g. D:\\Projects\\abc", "Chỉ tìm bên trong thư mục này (tuỳ chọn), ví dụ D:\\Projects\\abc")) },
                    { "ext", Prop("string", L.T("Only files with this extension (optional), e.g. md, docx, py", "Chỉ tìm file có đuôi này (tuỳ chọn), ví dụ md, docx, py")) },
                    { "name_only", Prop("boolean", L.T("true = match file names only", "true = chỉ tìm theo tên file")) },
                    { "include_excluded", Prop("boolean", L.T("true = also show scratch/temp/excluded sources (hidden by default)", "true = hiện cả nguồn nháp/tạm/bị loại trừ (mặc định ẩn)")) },
                    { "limit", Prop("integer", L.T("Maximum results (default 20, max 100)", "Số kết quả tối đa (mặc định 20, tối đa 100)")) } }, "query"),
            Tool("list_projects", L.T("List projects on this PC (folders with .git, CLAUDE.md, package.json...) with a summary from README/CLAUDE.md, most recently used first.", "Liệt kê các dự án trên máy (thư mục có .git, CLAUDE.md, package.json...) kèm tóm tắt từ README/CLAUDE.md, mới dùng gần đây xếp trước."),
                new Dictionary<string, object> { { "filter", Prop("string", L.T("Filter by name/path/summary (optional)", "Lọc theo tên/đường dẫn/tóm tắt (tuỳ chọn)")) } }),
            Tool("project_overview", L.T("Overview of a project: top-level files/folders and the contents of CLAUDE.md, AGENTS.md, README.", "Xem tổng quan một dự án: các file/thư mục cấp đầu và nội dung CLAUDE.md, AGENTS.md, README."),
                new Dictionary<string, object> { { "path", Prop("string", L.T("Project folder path", "Đường dẫn thư mục dự án")) } }, "path"),
            Tool("read_file", L.T("Read a text, source code or Word (.docx) file anywhere on the PC. Long files are read in line ranges.", "Đọc nội dung một file văn bản, mã nguồn hoặc Word (.docx) ở bất kỳ đâu trên máy. Đọc theo đoạn dòng với file dài."),
                new Dictionary<string, object> {
                    { "path", Prop("string", L.T("Full file path", "Đường dẫn đầy đủ của file")) },
                    { "start_line", Prop("integer", L.T("Start line, 1-based (default 1)", "Dòng bắt đầu, tính từ 1 (mặc định 1)")) },
                    { "max_lines", Prop("integer", L.T("Maximum lines (default 400)", "Số dòng tối đa (mặc định 400)")) } }, "path"),
            Tool("list_dir", L.T("List a folder's contents. Leave path empty to list drives.", "Liệt kê nội dung một thư mục. Để trống path để xem các ổ đĩa."),
                new Dictionary<string, object> { { "path", Prop("string", L.T("Folder path (empty = list drives)", "Đường dẫn thư mục (bỏ trống = danh sách ổ đĩa)")) } }),
            Tool("copy", L.T("Copy a file or folder elsewhere, e.g. bring documents from another project into the current one. Never overwrites existing files.", "Sao chép một file hoặc thư mục tới chỗ khác, ví dụ đưa tài liệu từ dự án khác vào dự án hiện tại. Không bao giờ ghi đè file đã có."),
                new Dictionary<string, object> {
                    { "source", Prop("string", L.T("Source file or folder", "File hoặc thư mục nguồn")) },
                    { "destination", Prop("string", L.T("Destination path; if it is an existing folder, copy into it", "Đường dẫn đích; nếu là thư mục đã có thì chép vào bên trong")) } }, "source", "destination"),
            Tool("recall", L.T("Recall what agents have remembered (decisions, lessons, facts, notes) and related projects/files, with associative spreading through the neural network. Call at the start of each task. Leave query empty for recent highlights.", "Nhớ lại những gì các agent đã ghi nhớ (quyết định, bài học, sự thật, ghi chú) và các dự án/file liên quan, có liên tưởng theo mạng nơ-ron. Gọi ở đầu mỗi việc. Để trống query để xem các ghi nhớ nổi bật gần đây."),
                new Dictionary<string, object> {
                    { "query", Prop("string", L.T("Topic to recall, e.g. 'stripe payments' or a project name", "Chủ đề cần nhớ lại, ví dụ 'thanh toán momo' hoặc tên dự án")) },
                    { "limit", Prop("integer", L.T("Maximum memories (default 10)", "Số ghi nhớ tối đa (mặc định 10)")) },
                    { "include_unverified", Prop("boolean", L.T("true = also show memories still awaiting verification (do NOT treat them as facts)", "true = hiện cả ghi nhớ chưa xác minh (KHÔNG coi là sự thật)")) } }),
            Tool("remember", L.T("Save something worth keeping into the long-term memory shared by all agents. ONLY memories with evidence the app can check itself are shared: give evidence as one or more lines – file \"<path>\" \"<exact quote from it>\" | commit \"<repo folder>\" <hash> | url https://… \"<exact quote from the page>\" | user \"<what the user said>\". The app opens the file/commit/page to confirm; false evidence is refused. Lessons (how to do things) only count from TRUSTED projects (tests/CI + releases, or marked trusted by the user). Without strong evidence the memory waits for the user's approval and is NOT shared.",
                "Lưu một điều đáng nhớ vào trí nhớ dài hạn dùng chung cho mọi agent. CHỈ ghi nhớ có bằng chứng mà app tự kiểm tra được mới được chia sẻ: ghi bằng chứng thành một hoặc nhiều dòng – file \"<đường dẫn>\" \"<trích nguyên văn>\" | commit \"<thư mục repo>\" <mã> | url https://… \"<trích nguyên văn trên trang>\" | user \"<lời người dùng nói>\". App tự mở file/commit/trang để xác nhận; bằng chứng sai sẽ bị từ chối. Bài học (cách làm) chỉ được tính khi lấy từ dự án TIN CẬY (có test/CI và đã phát hành, hoặc người dùng đánh dấu tin cậy). Không đủ bằng chứng thì ghi nhớ chờ người dùng duyệt và CHƯA được chia sẻ."),
                new Dictionary<string, object> {
                    { "text", Prop("string", L.T("What to remember, written so another agent understands it without context", "Nội dung cần nhớ, viết đủ ý để agent khác hiểu mà không cần ngữ cảnh")) },
                    { "title", Prop("string", L.T("Short title (optional)", "Tiêu đề ngắn (tuỳ chọn)")) },
                    { "kind", Prop("string", L.T("decision | lesson | fact | todo | note (default note)", "decision | lesson | fact | todo | note (mặc định note)")) },
                    { "source", Prop("string", L.T("Related file or project folder paths, separated by ';' (recommended)", "Đường dẫn file hoặc thư mục dự án liên quan, nhiều cái cách nhau bằng ';' (nên có)")) },
                    { "tags", Prop("string", L.T("Extra keywords to help recall (optional)", "Từ khoá bổ sung để dễ nhớ lại (tuỳ chọn)")) },
                    { "evidence", Prop("string", L.T("Evidence lines the app will check, e.g. file \"D:\\proj\\README.md\" \"exact sentence\" or commit \"D:\\proj\" a4c38fc", "Các dòng bằng chứng để app kiểm tra, ví dụ file \"D:\\proj\\README.md\" \"câu nguyên văn\" hoặc commit \"D:\\proj\" a4c38fc")) } }, "text", "evidence"),
            Tool("revise_memory", L.T("Edit a memory or mark it outdated/wrong. The old version stays in history; outdated memories fade instead of being deleted.", "Sửa nội dung một ghi nhớ hoặc đánh dấu nó đã cũ/sai. Bản cũ được giữ trong lịch sử; ghi nhớ đã cũ mờ dần chứ không bị xoá."),
                new Dictionary<string, object> {
                    { "id", Prop("string", L.T("Memory id, e.g. n12", "Mã ghi nhớ, ví dụ n12")) },
                    { "text", Prop("string", L.T("New content (optional)", "Nội dung mới (tuỳ chọn)")) },
                    { "outdated", Prop("boolean", L.T("true = mark outdated/no longer true", "true = đánh dấu đã cũ/không còn đúng")) } }, "id"),
            Tool("connect", L.T("Link two items in memory (memory, file or project) when you notice they are related.", "Nối hai ý trong trí nhớ (ghi nhớ, file hoặc dự án) khi bạn nhận ra chúng liên quan."),
                new Dictionary<string, object> {
                    { "a", Prop("string", L.T("Memory id (n12), file path or project folder", "Mã ghi nhớ (n12), đường dẫn file hoặc thư mục dự án")) },
                    { "b", Prop("string", L.T("Second item, same format", "Ý thứ hai, cùng dạng")) },
                    { "why", Prop("string", L.T("Why they are related (optional)", "Vì sao liên quan (tuỳ chọn)")) } }, "a", "b"),
            Tool("index_status", L.T("Index status: file count, project count, last update, connected drives. Set refresh=true to re-index now (in the background).", "Trạng thái chỉ mục: số file, số dự án, lần cập nhật cuối, các ổ đang cắm. Đặt refresh=true để cập nhật lại ngay (chạy ngầm)."),
                new Dictionary<string, object> { { "refresh", Prop("boolean", L.T("true = re-index now", "true = cập nhật chỉ mục ngay")) } }),
        };
    }

    static string S(Dictionary<string, object> a, string k) { object v; return a.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
    static int I(Dictionary<string, object> a, string k, int def) { int r; var s = S(a, k); return s != null && int.TryParse(s, out r) ? r : def; }
    static bool B(Dictionary<string, object> a, string k) { object v; return a.TryGetValue(k, out v) && v is bool && (bool)v; }

    static string ProjectOf(string path) { return Brain.ProjectOf(path); }

    static string Note()
    {
        if (Brain.FileCount == 0)
            return Brain.Building ? L.T("\n(Second Brain is building its first index; results are incomplete. ", "\n(Second Brain đang lập chỉ mục lần đầu, kết quả chưa đầy đủ. ") + Brain.Progress + ")" : L.T("\n(The index is empty.)", "\n(Chỉ mục còn trống.)");
        return Brain.Building ? L.T("\n(Updating the index in the background.)", "\n(Đang cập nhật chỉ mục ngầm.)") : "";
    }

    static string CallTool(string name, Dictionary<string, object> a)
    {
        if (name != "index_status") Brain.RefreshIfStale(TimeSpan.FromMinutes(30));
        var sb = new StringBuilder();
        switch (name)
        {
            case "search":
            {
                var q = S(a, "query") ?? "";
                int limit = Math.Max(1, Math.Min(100, I(a, "limit", 20)));
                bool all = B(a, "include_excluded") || !string.IsNullOrEmpty(S(a, "path"));   // tìm trong thư mục chỉ định = người dùng muốn xem cả nguồn bị loại trừ
                var hits = Brain.Search(q, S(a, "path"), S(a, "ext"), B(a, "name_only"), Math.Min(300, limit * 3))
                    .Select(h => new { h, t = Trust.Of(h.File.Path) })
                    .Where(x => all || x.t.Level != Trust.Level.Excluded)
                    .OrderByDescending(x => x.h.Score + (x.t.Level == Trust.Level.Trusted ? 15 : 0)).Take(limit).ToList();
                if (hits.Count == 0) return L.T("No results for \"", "Không tìm thấy kết quả cho \"") + q + "\"." + Note();
                sb.AppendLine(L.T("Found ", "Tìm thấy ") + hits.Count + L.T(" results for \"", " kết quả cho \"") + q + "\":");
                foreach (var x in hits)
                {
                    var h = x.h;
                    sb.AppendLine().Append("• ").Append(h.File.Path);
                    sb.Append("  (" + Cleaner.Fmt(h.File.Size) + L.T(", modified ", ", sửa ") + DateTime.FromBinary(h.File.Mtime).ToString("dd/MM/yyyy") + ")");
                    if (!Brain.IsOnline(h.File.Path)) sb.Append(L.T("  [drive not connected]", "  [ổ đang không cắm]"));
                    sb.AppendLine();
                    var proj = ProjectOf(h.File.Path);
                    if (proj != null) sb.AppendLine(L.T("  Project: ", "  Dự án: ") + proj);
                    sb.AppendLine(L.T("  Source trust: ", "  Độ tin cậy nguồn: ") + Trust.Tag(x.t));
                    if (h.Snippet != null) foreach (var l in h.Snippet.Split('\n')) sb.AppendLine("    " + l.TrimEnd());
                }
                var mem = Memory.RecallRaw(q, 3, false).Where(r => r.N.Kind != "file").ToList();
                if (mem.Count > 0) sb.AppendLine().AppendLine(L.T("Related memories (call recall for more):", "Ghi nhớ liên quan (gọi recall để xem thêm):")).AppendLine(Memory.Format(mem, false));
                sb.AppendLine().Append(L.T("Only follow ✓ trusted sources as examples; ⚠ unverified code may be untested – read it critically and never cite it as a lesson.",
                    "Chỉ học theo nguồn ✓ tin cậy; code ⚠ chưa kiểm chứng có thể chưa được test – đọc có phê phán và đừng lấy làm bài học."));
                return sb.ToString().TrimEnd() + Note();
            }
            case "recall":
            {
                var list = Memory.RecallRaw(S(a, "query"), Math.Max(1, Math.Min(50, I(a, "limit", 10))), true, B(a, "include_unverified"));
                if (list.Count == 0) return L.T("No memories about \"", "Chưa có ghi nhớ nào về \"") + S(a, "query") + L.T("\" yet. When you learn something worth keeping, use remember.", "\". Khi học được điều đáng giữ, hãy dùng remember.");
                return L.T("Recalled ", "Nhớ lại được ") + list.Count + L.T(" items:\n", " ý:\n") + Memory.Format(list, true);
            }
            case "remember":
                return Memory.Remember(S(a, "text"), S(a, "title"), S(a, "kind"), S(a, "source"), S(a, "tags"), S(a, "evidence"));
            case "revise_memory":
                return Memory.Revise(S(a, "id"), S(a, "text"), B(a, "outdated"));
            case "connect":
                return Memory.Connect(S(a, "a"), S(a, "b"), S(a, "why"));
            case "list_projects":
            {
                var list = Brain.Projects(S(a, "filter"));
                if (list.Count == 0) return L.T("No projects found.", "Chưa thấy dự án nào.") + Note();
                sb.AppendLine(list.Count + L.T(" projects:", " dự án:"));
                foreach (var p in list.Take(200))
                {
                    sb.AppendLine().AppendLine("• " + p.Name + " — " + p.Path + (Brain.IsOnline(p.Path) ? "" : L.T("  [drive not connected]", "  [ổ đang không cắm]")));
                    sb.AppendLine(L.T("  Last used: ", "  Dùng gần nhất: ") + p.LastWrite.ToString("dd/MM/yyyy") + L.T(" · Markers: ", " · Dấu hiệu: ") + p.Markers);
                    sb.AppendLine(L.T("  Trust: ", "  Độ tin cậy: ") + Trust.Tag(Trust.Of(p.Path)));
                    if (!string.IsNullOrEmpty(p.Summary)) sb.AppendLine("  " + p.Summary);
                }
                return sb.ToString().TrimEnd() + Note();
            }
            case "project_overview":
            {
                var path = S(a, "path");
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException(L.T("Folder not found: ", "Không tìm thấy thư mục: ") + path);
                var di = new DirectoryInfo(path);
                sb.AppendLine(L.T("Project: ", "Dự án: ") + di.FullName).AppendLine(L.T("Trust: ", "Độ tin cậy: ") + Trust.Tag(Trust.Of(di.FullName))).AppendLine().AppendLine(L.T("Top-level contents:", "Nội dung cấp đầu:"));
                foreach (var e in di.GetFileSystemInfos().OrderBy(e => !(e is DirectoryInfo)).ThenBy(e => e.Name).Take(200))
                    sb.AppendLine("  " + (e is DirectoryInfo ? e.Name + "\\" : e.Name + "  (" + Cleaner.Fmt(((FileInfo)e).Length) + ")"));
                foreach (var n in new[] { "CLAUDE.md", "AGENTS.md", "README.md", "README.txt", "README" })
                {
                    var f = Path.Combine(path, n);
                    if (!File.Exists(f)) continue;
                    var lines = Brain.ReadText(f).Split('\n');
                    sb.AppendLine().AppendLine("===== " + n + (lines.Length > 200 ? " (200/" + lines.Length + L.T(" first lines)", " dòng đầu)") : "") + " =====");
                    foreach (var l in lines.Take(200)) sb.AppendLine(l.TrimEnd('\r'));
                }
                var memo = Memory.ForProject(di.FullName);
                if (memo.Length > 0) sb.AppendLine().AppendLine(L.T("===== Second Brain memory =====", "===== Trí nhớ Second Brain =====")).AppendLine(memo);
                Memory.Experience(di.FullName);
                return sb.ToString().TrimEnd();
            }
            case "read_file":
            {
                var path = S(a, "path");
                if (!File.Exists(path)) throw new FileNotFoundException(L.T("File not found: ", "Không tìm thấy file: ") + path);
                var fi = new FileInfo(path);
                if (!Brain.IsReadable(path) && fi.Length > 0)
                {
                    // Thử đọc như văn bản; nếu là file nhị phân thì báo thông tin cơ bản
                    try { if (fi.Length > 5 * 1024 * 1024) throw new InvalidDataException(); Brain.ReadText(path); }
                    catch { return "File " + path + L.T(" is not text (", " không phải văn bản (") + Cleaner.Fmt(fi.Length) + L.T(", modified ", ", sửa ") + fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm") + L.T("). Use copy to bring this file into the project if needed.", "). Dùng copy để chép file này sang dự án nếu cần."); }
                }
                if (fi.Length > 50L * 1024 * 1024) throw new IOException(L.T("File too large to read (", "File quá lớn để đọc (") + Cleaner.Fmt(fi.Length) + ").");
                var all = Brain.ReadText(path).Split('\n');
                int start = Math.Max(1, I(a, "start_line", 1)), max = Math.Max(1, Math.Min(2000, I(a, "max_lines", 400)));
                int end = Math.Min(all.Length, start - 1 + max);
                sb.AppendLine(path + "  (" + Cleaner.Fmt(fi.Length) + ", " + all.Length + L.T(" lines, modified ", " dòng, sửa ") + fi.LastWriteTime.ToString("dd/MM/yyyy HH:mm") + ")");
                sb.AppendLine(L.T("Lines ", "Dòng ") + start + "–" + end + ":");
                int chars = 0;
                for (int i = start - 1; i < end; i++)
                {
                    var l = all[i].TrimEnd('\r');
                    chars += l.Length;
                    if (chars > 200000) { sb.AppendLine(L.T("… (truncated, continue from line ", "… (cắt bớt, đọc tiếp từ dòng ") + (i + 1) + ")"); break; }
                    sb.Append(i + 1).Append('\t').AppendLine(l);
                }
                if (end < all.Length) sb.AppendLine(L.T("… ", "… còn ") + (all.Length - end) + L.T(" more lines, call again with start_line=", " dòng, gọi lại với start_line=") + (end + 1));
                Memory.Experience(path);
                return sb.ToString().TrimEnd();
            }
            case "list_dir":
            {
                var path = S(a, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    sb.AppendLine(L.T("Available drives:", "Các ổ đĩa đang có:"));
                    foreach (var d in Brain.Drives())
                        sb.AppendLine("  " + d.Name + "  " + (d.DriveType == DriveType.Removable ? L.T("USB/memory card", "USB/thẻ nhớ") : L.T("hard drive", "ổ cứng")) + "  \"" + d.VolumeLabel + L.T("\"  free ", "\"  trống ") + Cleaner.Fmt(d.AvailableFreeSpace) + "/" + Cleaner.Fmt(d.TotalSize));
                    return sb.ToString().TrimEnd();
                }
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException(L.T("Folder not found: ", "Không tìm thấy thư mục: ") + path);
                var entries = new DirectoryInfo(path).GetFileSystemInfos().OrderBy(e => !(e is DirectoryInfo)).ThenBy(e => e.Name).ToList();
                sb.AppendLine(path + " (" + entries.Count + L.T(" items):", " mục):"));
                foreach (var e in entries.Take(1000))
                    sb.AppendLine("  " + (e is DirectoryInfo ? e.Name + "\\" : e.Name + "  " + Cleaner.Fmt(((FileInfo)e).Length)) + "  " + e.LastWriteTime.ToString("dd/MM/yyyy"));
                if (entries.Count > 1000) sb.AppendLine(L.T("  … and ", "  … và ") + (entries.Count - 1000) + L.T(" more", " mục khác"));
                return sb.ToString().TrimEnd();
            }
            case "copy":
            {
                var res = Brain.Copy(S(a, "source"), S(a, "destination"));
                Memory.Copied(S(a, "source"), S(a, "destination"));
                return res;
            }
            case "index_status":
            {
                Brain.EnsureLoaded();
                if (B(a, "refresh") && !Brain.Building) Task.Run(() => { try { Brain.Build(); } catch { } });
                Thread.Sleep(100);
                sb.AppendLine(L.T("Files in index: ", "Số file trong chỉ mục: ") + Brain.FileCount.ToString("N0"));
                sb.AppendLine(L.T("Projects: ", "Số dự án: ") + Brain.ProjectCount);
                var mc = Memory.Counts();
                sb.AppendLine(L.T("Memory: ", "Trí nhớ: ") + mc.Item1 + L.T(" memories, ", " ghi nhớ, ") + mc.Item2 + L.T(" links", " liên kết"));
                sb.AppendLine(L.T("Last updated: ", "Cập nhật lần cuối: ") + (Brain.BuiltAt == DateTime.MinValue ? L.T("never", "chưa có") : Brain.BuiltAt.ToString("dd/MM/yyyy HH:mm")));
                sb.AppendLine(L.T("Updating: ", "Đang cập nhật: ") + (Brain.Building ? L.T("yes — ", "có — ") + Brain.Progress : L.T("no", "không")));
                sb.Append(L.T("Connected drives: ", "Ổ đang cắm: ") + string.Join(", ", Brain.Drives().Select(d => d.Name.TrimEnd('\\'))));
                return sb.ToString();
            }
            default:
                throw new ArgumentException(L.T("No such tool: ", "Không có công cụ: ") + name);
        }
    }
}

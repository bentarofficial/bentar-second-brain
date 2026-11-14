// Đồ thị code (ý tưởng từ Graphify, viết lại gọn bằng C#, không cần Python):
// - Bản đồ ký hiệu: hàm/class/method kèm khoảng dòng, chữ ký, chú thích
// - Cạnh quan hệ: lời gọi hàm, kế thừa, import
// - Cập nhật dần: chỉ phân tích lại file đã đổi; lưu ở %LOCALAPPDATA%\BentarSecondBrain\graph
// - context_pack: gói ngữ cảnh có giới hạn token cho một nhiệm vụ (code đúng đoạn + quan hệ + trí nhớ đã xác minh + ví dụ ✓ ở dự án khác)
// Nguyên tắc: chỉ ĐỌC file của người dùng.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

class CgCall { public string Name, Qual; public int Line; public bool Ctor; }

class CgSym
{
    public string Name, Kind, Container, Sig, Doc, Words;
    public int Start, End;
    public List<CgCall> Calls = new List<CgCall>();
    public List<string> Bases = new List<string>();
    [NonSerialized] public CgFile File;
    public string Full { get { return string.IsNullOrEmpty(Container) ? Name : Container + "." + Name; } }
    public int Lines { get { return End - Start + 1; } }
}

class CgFile
{
    public string Path, Lang;
    public long Mtime, Size;
    public int LineCount;
    public List<string> Imports = new List<string>();
    public List<CgSym> Syms = new List<CgSym>();
}

class CgGraph
{
    public string Root;
    public Dictionary<string, CgFile> Files = new Dictionary<string, CgFile>(StringComparer.OrdinalIgnoreCase);
    public DateTime CheckedAt = DateTime.MinValue;
    public bool Dirty;
    // Chỉ mục dẫn xuất (dựng lại khi file đổi)
    public Dictionary<string, List<CgSym>> ByName;
    public Dictionary<string, List<Tuple<CgSym, CgCall>>> Callers;
    public readonly object Gate = new object();

    public void Derive()
    {
        var byName = new Dictionary<string, List<CgSym>>(StringComparer.Ordinal);
        var callers = new Dictionary<string, List<Tuple<CgSym, CgCall>>>(StringComparer.Ordinal);
        foreach (var f in Files.Values)
            foreach (var s in f.Syms)
            {
                s.File = f;
                List<CgSym> l; if (!byName.TryGetValue(s.Name, out l)) byName[s.Name] = l = new List<CgSym>(); l.Add(s);
                foreach (var c in s.Calls)
                {
                    List<Tuple<CgSym, CgCall>> cl; if (!callers.TryGetValue(c.Name, out cl)) callers[c.Name] = cl = new List<Tuple<CgSym, CgCall>>();
                    cl.Add(Tuple.Create(s, c));
                }
            }
        ByName = byName; Callers = callers;
    }

    public List<CgSym> Defs(string name) { List<CgSym> l; return ByName != null && ByName.TryGetValue(name, out l) ? l : new List<CgSym>(); }
    public List<Tuple<CgSym, CgCall>> CallersOf(string name) { List<Tuple<CgSym, CgCall>> l; return Callers != null && Callers.TryGetValue(name, out l) ? l : new List<Tuple<CgSym, CgCall>>(); }
    public int SymCount { get { return Files.Values.Sum(f => f.Syms.Count); } }
}

static class CodeGraph
{
    const int FormatVersion = 6;   // tăng mỗi khi bộ phân tích đổi để đồ thị cũ được dựng lại
    const long MaxFileSize = 1024 * 1024;
    const int MaxFiles = 25000;
    static readonly string Dir = Path.Combine(Cleaner.DataDir, "graph");
    static readonly string SavingsFile = Path.Combine(Cleaner.DataDir, "savings.log");
    static readonly Dictionary<string, CgGraph> cache = new Dictionary<string, CgGraph>(StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<string, string> LangOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { ".cs", "cs" }, { ".java", "java" }, { ".kt", "kt" }, { ".kts", "kt" }, { ".scala", "java" }, { ".dart", "dart" }, { ".swift", "swift" },
        { ".js", "js" }, { ".mjs", "js" }, { ".cjs", "js" }, { ".jsx", "js" }, { ".ts", "js" }, { ".tsx", "js" }, { ".vue", "js" }, { ".svelte", "js" },
        { ".go", "go" }, { ".rs", "rs" }, { ".c", "c" }, { ".h", "c" }, { ".cc", "c" }, { ".cpp", "c" }, { ".cxx", "c" }, { ".hpp", "c" }, { ".hh", "c" },
        { ".php", "php" }, { ".py", "py" }, { ".rb", "rb" }, { ".lua", "lua" }, { ".ps1", "ps" }, { ".psm1", "ps" } };

    static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "node_modules", ".git", ".svn", ".hg", ".venv", "venv", "env", "__pycache__", "site-packages", ".cache", ".gradle", ".m2", ".nuget", "packages",
        "bower_components", ".next", ".nuxt", "dist", "build", "out", "obj", "bin", "target", ".idea", ".vs", ".tox", "vendor", "Pods", ".pnpm-store", ".yarn",
        "coverage", ".turbo", "AppData", "$Recycle.Bin", "System Volume Information", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "wp-includes", "wp-admin", ".svelte-kit", "release", "Release", "Debug", "generated", ".dart_tool", ".expo" };

    static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal) {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "else", "sizeof", "typeof", "nameof", "when", "throw", "await",
        "base", "this", "super", "function", "fixed", "checked", "unchecked", "default", "case", "do", "try", "finally", "yield", "in", "is", "as", "and", "or",
        "not", "elif", "except", "with", "print", "assert", "del", "lambda", "match", "loop", "fn", "func", "def", "defer", "go", "select", "range", "make",
        "len", "cap", "append", "delete", "void", "var", "let", "const", "static", "public", "private", "protected", "internal", "async", "import", "export",
        "from", "require", "include", "define", "elseif", "end", "then", "local", "self", "Self", "struct", "class", "interface", "enum", "true", "false", "null",
        "nil", "None", "out", "ref", "params", "operator", "implicit", "explicit", "get", "set", "where", "select", "unless", "until", "foreach", "echo", "isset",
        "empty", "array", "list", "string", "int", "bool", "char", "float", "double", "long", "object", "decimal", "byte", "Write-Host", "param" };

    public static bool IsCode(string path) { try { return LangOf.ContainsKey(Path.GetExtension(path)); } catch { return false; } }

    // Gợi ý nhẹ cho agent trước khi đọc nguyên file code lớn hoặc grep một tên hàm (hook PreToolUse; không bao giờ chặn).
    // Mỗi phiên chỉ nhắc tối đa một lần cho mỗi loại.
    public static string Nudge(string tool, Dictionary<string, object> input, string session, string cwd)
    {
        Func<string, string> g = k => { object v; return input != null && input.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; };
        string kind = null, msg = null;
        if (tool == "Read")
        {
            var fp = g("file_path");
            if (fp == null || g("offset") != null || g("limit") != null || !IsCode(fp)) return null;
            long len; try { len = new FileInfo(fp).Length; } catch { return null; }
            if (len < 24 * 1024) return null;
            kind = "read";
            msg = L.T("[Bentar Second Brain] This file is large (≈" + (len / 4).ToString("N0") + " tokens). Next time: code_map \"" + fp + "\" (outline with line ranges) or explain <symbol>, then read only the lines you need – or context_pack \"<task>\" for the whole task.",
                      "[Bentar Second Brain] File này lớn (≈" + (len / 4).ToString("N0") + " token). Lần sau: code_map \"" + fp + "\" (dàn ý kèm khoảng dòng) hoặc explain <ký hiệu>, rồi chỉ đọc đúng đoạn cần – hoặc context_pack \"<nhiệm vụ>\" cho cả nhiệm vụ.");
        }
        else if (tool == "Grep")
        {
            var pat = (g("pattern") ?? "").Trim();
            var m = Regex.Match(pat, @"^(?:\\b)?([A-Za-z_][A-Za-z0-9_]{2,})(?:\\b)?(?:\\s\*)?(?:\\\(|\()?$");
            if (!m.Success) return null;
            kind = "grep";
            msg = L.T("[Bentar Second Brain] Looking for a symbol? who_calls " + m.Groups[1].Value + " (every call site with file:line) or explain " + m.Groups[1].Value + " (definition, calls, callers) answer in one step with fewer tokens.",
                      "[Bentar Second Brain] Đang tìm một ký hiệu? who_calls " + m.Groups[1].Value + " (mọi nơi gọi kèm file:dòng) hoặc explain " + m.Groups[1].Value + " (định nghĩa, gọi gì, ai gọi) trả lời trong một bước, ít token hơn.");
        }
        if (msg == null) return null;
        try
        {
            var mark = Path.Combine(Path.GetTempPath(), "bsb-nudge-" + Regex.Replace(session ?? "x", @"[^\w-]", "") + "-" + kind);
            if (File.Exists(mark)) return null;
            File.WriteAllText(mark, "");
        }
        catch { }
        return msg;
    }

    // ================= Lấy đồ thị (luôn mới) =================
    public static string RootOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(L.T("Give path = the project folder (your current working directory) or a file in it.", "Cần path = thư mục dự án (thư mục đang làm việc của bạn) hoặc một file trong đó."));
        string full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\');
        string dir = Directory.Exists(full) ? full : File.Exists(full) ? Path.GetDirectoryName(full) : null;
        if (dir == null) throw new DirectoryNotFoundException(L.T("Not found: ", "Không tìm thấy: ") + full);
        // Gốc dự án = thư mục gần nhất (đi lên) có dấu hiệu dự án; không bao giờ là gốc ổ đĩa hay thư mục người dùng
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        for (var x = new DirectoryInfo(dir); x != null && x.Parent != null; x = x.Parent)
        {
            var p = x.FullName.TrimEnd('\\');
            if (p.Equals(home, StringComparison.OrdinalIgnoreCase)) break;
            if (new[] { ".git", "package.json", "pyproject.toml", "go.mod", "Cargo.toml", "composer.json", "pom.xml", "build.gradle", "setup.py", "requirements.txt", "CLAUDE.md", "AGENTS.md" }.Any(m => File.Exists(Path.Combine(p, m)) || Directory.Exists(Path.Combine(p, m)))
                || SafeAny(x, "*.sln") || SafeAny(x, "*.csproj"))
            {
                // Thư mục con của một dự án đã biết: dùng dự án đó (vd repo chứa nhiều gói)
                Brain.EnsureLoaded();
                var proj = Brain.ProjectOf(p);
                return proj != null && !IsTooBroad(proj) ? proj : p;
            }
        }
        if (IsTooBroad(dir)) throw new ArgumentException(L.T("This folder is too broad for a code graph (drive root / user folder). Give the project folder.", "Thư mục này quá rộng để dựng đồ thị code (gốc ổ đĩa / thư mục người dùng). Hãy đưa thư mục dự án."));
        return dir;
    }

    static bool IsTooBroad(string p)
    {
        p = p.TrimEnd('\\');
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        return p.Length <= 3 || p.Equals(home, StringComparison.OrdinalIgnoreCase) || p.Equals(Path.GetDirectoryName(home), StringComparison.OrdinalIgnoreCase)
            || p.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
    }
    static bool SafeAny(DirectoryInfo d, string pat) { try { return d.EnumerateFiles(pat).Any(); } catch { return false; } }

    public static CgGraph Get(string root)
    {
        CgGraph g;
        lock (cache)
        {
            if (!cache.TryGetValue(root, out g)) { g = Load(root) ?? new CgGraph { Root = root }; cache[root] = g; }
        }
        lock (g.Gate)
        {
            if ((DateTime.Now - g.CheckedAt).TotalSeconds > 3) Refresh(g);
            if (g.ByName == null) g.Derive();
        }
        return g;
    }

    static void Refresh(CgGraph g)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var fi in CodeFiles(new DirectoryInfo(g.Root), 0))
        {
            if (seen.Count >= MaxFiles) break;
            seen.Add(fi.FullName);
            long mt = fi.LastWriteTimeUtc.ToBinary();
            CgFile old;
            if (g.Files.TryGetValue(fi.FullName, out old) && old.Mtime == mt && old.Size == fi.Length) continue;
            try { g.Files[fi.FullName] = Parse(fi.FullName, File.ReadAllText(fi.FullName), mt, fi.Length); }
            catch { g.Files.Remove(fi.FullName); }
            changed = true;
        }
        foreach (var k in g.Files.Keys.Where(k => !seen.Contains(k)).ToList()) { g.Files.Remove(k); changed = true; }
        g.CheckedAt = DateTime.Now;
        if (changed || g.ByName == null) { g.Derive(); g.Dirty = true; }
        if (g.Dirty) { try { Save(g); g.Dirty = false; } catch { } }
    }

    [ThreadStatic] static int dirsVisited;
    static IEnumerable<FileInfo> CodeFiles(DirectoryInfo dir, int depth)
    {
        if (depth == 0) dirsVisited = 0;
        if (depth > 25 || ++dirsVisited > 15000) yield break;
        FileSystemInfo[] entries;
        try
        {
            if (depth > 0 && (dir.Attributes & FileAttributes.ReparsePoint) != 0) yield break;
            entries = dir.GetFileSystemInfos();
        }
        catch { yield break; }
        foreach (var e in entries)
        {
            var fi = e as FileInfo;
            if (fi != null && LangOf.ContainsKey(fi.Extension) && fi.Length <= MaxFileSize && !fi.Name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
                && !fi.Name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) && !fi.Name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) yield return fi;
        }
        foreach (var e in entries)
        {
            var di = e as DirectoryInfo;
            if (di == null || SkipDirs.Contains(di.Name) || di.Name.StartsWith(".")) continue;
            foreach (var f in CodeFiles(di, depth + 1)) yield return f;
        }
    }

    // ================= Lưu / nạp =================
    static string FileFor(string root)
    {
        using (var md5 = MD5.Create())
        {
            var h = md5.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
            return Path.Combine(Dir, string.Concat(h.Take(8).Select(b => b.ToString("x2"))) + ".cg");
        }
    }

    static void Save(CgGraph g)
    {
        Directory.CreateDirectory(Dir);
        var file = FileFor(g.Root);
        var tmp = file + "." + System.Diagnostics.Process.GetCurrentProcess().Id + "." + Thread.CurrentThread.ManagedThreadId + ".tmp";
        using (var bw = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 16), Encoding.UTF8))
        {
            bw.Write(FormatVersion); bw.Write(g.Root); bw.Write(g.Files.Count);
            foreach (var f in g.Files.Values)
            {
                bw.Write(f.Path); bw.Write(f.Lang); bw.Write(f.Mtime); bw.Write(f.Size); bw.Write(f.LineCount);
                bw.Write(f.Imports.Count); foreach (var i in f.Imports) bw.Write(i);
                bw.Write(f.Syms.Count);
                foreach (var s in f.Syms)
                {
                    bw.Write(s.Name); bw.Write(s.Kind); bw.Write(s.Container ?? ""); bw.Write(s.Sig ?? ""); bw.Write(s.Doc ?? ""); bw.Write(s.Words ?? "");
                    bw.Write(s.Start); bw.Write(s.End);
                    bw.Write(s.Calls.Count); foreach (var c in s.Calls) { bw.Write(c.Name); bw.Write(c.Qual ?? ""); bw.Write(c.Line); bw.Write(c.Ctor); }
                    bw.Write(s.Bases.Count); foreach (var b in s.Bases) bw.Write(b);
                }
            }
        }
        try { if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file); }
        catch { try { File.Delete(tmp); } catch { } }
    }

    static CgGraph Load(string root)
    {
        var file = FileFor(root);
        if (!File.Exists(file)) return null;
        try
        {
            using (var br = new BinaryReader(new BufferedStream(File.OpenRead(file), 1 << 16), Encoding.UTF8))
            {
                if (br.ReadInt32() != FormatVersion) return null;
                var g = new CgGraph { Root = br.ReadString() };
                if (!g.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) return null;
                int nf = br.ReadInt32();
                for (int i = 0; i < nf; i++)
                {
                    var f = new CgFile { Path = br.ReadString(), Lang = br.ReadString(), Mtime = br.ReadInt64(), Size = br.ReadInt64(), LineCount = br.ReadInt32() };
                    int ni = br.ReadInt32(); for (int j = 0; j < ni; j++) f.Imports.Add(br.ReadString());
                    int ns = br.ReadInt32();
                    for (int j = 0; j < ns; j++)
                    {
                        var s = new CgSym { Name = br.ReadString(), Kind = br.ReadString(), Container = br.ReadString(), Sig = br.ReadString(), Doc = br.ReadString(), Words = br.ReadString(), Start = br.ReadInt32(), End = br.ReadInt32() };
                        if (s.Container.Length == 0) s.Container = null;
                        int nc = br.ReadInt32(); for (int k = 0; k < nc; k++) { var c = new CgCall { Name = br.ReadString(), Qual = br.ReadString(), Line = br.ReadInt32(), Ctor = br.ReadBoolean() }; if (c.Qual.Length == 0) c.Qual = null; s.Calls.Add(c); }
                        int nb = br.ReadInt32(); for (int k = 0; k < nb; k++) s.Bases.Add(br.ReadString());
                        f.Syms.Add(s);
                    }
                    g.Files[f.Path] = f;
                }
                return g;
            }
        }
        catch { return null; }
    }

    // Dựng sẵn đồ thị cho các dự án dùng gần đây (chạy ngầm sau khi lập chỉ mục)
    public static void Prewarm(int maxProjects)
    {
        try
        {
            foreach (var p in Brain.Projects(null).Where(p => Brain.IsOnline(p.Path) && Trust.Of(p.Path).Level != Trust.Level.Excluded)
                                 .OrderByDescending(p => p.LastWrite).Take(maxProjects))
            {
                try { Get(p.Path); } catch { }
                lock (cache) cache.Remove(p.Path);   // không giữ trong RAM của tiến trình lập chỉ mục
                Thread.Sleep(50);
            }
        }
        catch { }
    }

    // ================= Phân tích một file =================
    static readonly Regex RxPy = new Regex(@"^(\s*)(?:async\s+)?(def|class)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    static readonly Regex RxRb = new Regex(@"^(\s*)(def|class|module)\s+(?:self\.)?([A-Za-z_]\w*[?!]?)", RegexOptions.Compiled);
    static readonly Regex RxLua = new Regex(@"^(\s*)(?:local\s+)?function\s+([\w.:]+)\s*\(", RegexOptions.Compiled);
    static readonly Regex RxPs = new Regex(@"^(\s*)function\s+([\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex RxType = new Regex(@"\b(class|interface|struct|enum|record|trait|protocol|object|namespace|module)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    static readonly Regex RxGoType = new Regex(@"^\s*type\s+([A-Za-z_]\w*)\s+(struct|interface)\b", RegexOptions.Compiled);
    static readonly Regex RxGoFunc = new Regex(@"^\s*func\s+(?:\(\s*\w*\s*\*?\s*([A-Za-z_]\w*)[^)]*\)\s*)?([A-Za-z_]\w*)\s*[\(\[]", RegexOptions.Compiled);
    static readonly Regex RxRsFn = new Regex(@"\bfn\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    static readonly Regex RxRsImpl = new Regex(@"^\s*impl(?:<[^>]*>)?\s+(?:[\w:]+(?:<[^>]*>)?\s+for\s+)?([A-Za-z_]\w*)", RegexOptions.Compiled);
    static readonly Regex RxKwFn = new Regex(@"\b(?:function|func|fun)\b\s*\*?\s*([A-Za-z_$][\w$]*)\s*[<(]", RegexOptions.Compiled);
    static readonly Regex RxJsVarFn = new Regex(@"^\s*(?:export\s+)?(?:default\s+)?(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=]+)?=\s*(?:async\s*)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|[A-Za-z_$][\w$]*\s*=>)", RegexOptions.Compiled);
    static readonly Regex RxJsMethod = new Regex(@"^\s*(?:(?:public|private|protected|static|async|readonly|override|get|set|abstract)\s+)*\*?([A-Za-z_$][\w$]*)\s*(?:<[^>]*>)?\s*\([^;]*$", RegexOptions.Compiled);
    static readonly Regex RxCMethod = new Regex(@"^\s*(?:\[[^\]]*\]\s*)*((?:[\w<>\[\],.?*&:~]+\s+)+)\*?&?([A-Za-z_~]\w*)\s*(?:<[^>()]*>)?\s*\(", RegexOptions.Compiled);
    static readonly Regex RxCtor = new Regex(@"^\s*(?:(?:public|private|protected|internal|static)\s+)*([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
    static readonly Regex RxCall = new Regex(@"(?<![\w$])([A-Za-z_$][\w$]*)\s*(?:<[\w\s,.<>\[\]?]*>)?\s*\(", RegexOptions.Compiled);
    static readonly Regex RxNew = new Regex(@"\bnew\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    static readonly Regex RxWord = new Regex(@"[A-Za-z_][A-Za-z0-9_]{2,}", RegexOptions.Compiled);

    static readonly Regex[] RxImport = {
        new Regex(@"^\s*using\s+(?:static\s+)?([\w.]+)\s*;", RegexOptions.Compiled),
        new Regex(@"^\s*import\s+(?:static\s+)?([\w.*]+)\s*;?\s*$", RegexOptions.Compiled),
        new Regex(@"\bfrom\s+['""]([^'""]+)['""]", RegexOptions.Compiled),
        new Regex(@"^\s*import\s+['""]([^'""]+)['""]", RegexOptions.Compiled),
        new Regex(@"\brequire\s*\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.Compiled),
        new Regex(@"^\s*from\s+([\w.]+)\s+import\b", RegexOptions.Compiled),
        new Regex(@"^\s*import\s+([\w.]+)(?:\s+as\s+\w+)?\s*$", RegexOptions.Compiled),
        new Regex(@"^\s*#\s*include\s+[""<]([^"">]+)["">]", RegexOptions.Compiled),
        new Regex(@"^\s*use\s+([\w:]+)", RegexOptions.Compiled),
        new Regex(@"^\s*(?:require|require_relative)\s+['""]([^'""]+)['""]", RegexOptions.Compiled),
        new Regex(@"^\s*""([\w./-]+)""\s*$", RegexOptions.Compiled) };

    public static CgFile Parse(string path, string text, long mtime, long size)
    {
        var lang = LangOf[Path.GetExtension(path)];
        var f = new CgFile { Path = path, Lang = lang, Mtime = mtime, Size = size };
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = raw.Split('\n');
        f.LineCount = rawLines.Length;
        var clean = Clean(raw, lang);
        var lines = clean.Split('\n');
        // offset đầu mỗi dòng trong clean
        var lineStart = new int[lines.Length + 1];
        for (int i = 0, o = 0; i < lines.Length; i++) { lineStart[i] = o; o += lines[i].Length + 1; }
        lineStart[lines.Length] = clean.Length + 1;

        // Import (trên văn bản gốc vì chuỗi đã bị xoá trong bản sạch)
        bool goImport = false;
        for (int i = 0; i < rawLines.Length && i < 400; i++)
        {
            var l = rawLines[i];
            if (lang == "go") { if (l.StartsWith("import (")) { goImport = true; continue; } if (goImport && l.StartsWith(")")) goImport = false; }
            for (int r = 0; r < RxImport.Length; r++)
            {
                if (r == 10 && !goImport) continue;
                if ((r == 5 || r == 6) && lang != "py") continue;
                if (r == 8 && lang != "rs" && lang != "php") continue;
                var m = RxImport[r].Match(l);
                if (m.Success && f.Imports.Count < 80) { if (!f.Imports.Contains(m.Groups[1].Value)) f.Imports.Add(m.Groups[1].Value); break; }
            }
            if (lang == "go") { var m = Regex.Match(l, @"^\s*import\s+""([^""]+)"""); if (m.Success) f.Imports.Add(m.Groups[1].Value); }
        }

        if (lang == "py" || lang == "rb" || lang == "lua" || lang == "ps") ParseIndented(f, lang, lines, rawLines);
        else ParseBraces(f, lang, clean, lines, lineStart, rawLines);

        // Container = kiểu bao ngoài gần nhất; Calls; Words
        var types = f.Syms.Where(s => IsType(s.Kind)).ToList();
        foreach (var s in f.Syms)
        {
            if (s.Container == null)
            {
                var c = types.Where(t => t != s && t.Start <= s.Start && t.End >= s.End && t.Kind != "namespace" && t.Kind != "module").OrderByDescending(t => t.Start).FirstOrDefault();
                if (c != null) s.Container = c.Name;
            }
            if (s.Doc == null) s.Doc = DocAbove(rawLines, s.Start - 1, lang, s.Start, s.End);
            var words = new HashSet<string>();
            foreach (var w in SplitIdent(s.Name)) words.Add(w);
            if (!IsType(s.Kind) || s.Lines < 400)
            {
                var seen = new HashSet<string>();
                int bodyFrom = s.Start - 1, bodyTo = Math.Min(s.End, lines.Length) - 1;
                for (int i = bodyFrom; i <= bodyTo; i++)
                {
                    // Bỏ phần thân các hàm con (để lời gọi thuộc đúng hàm trong cùng)
                    if (!IsType(s.Kind) || true)
                    {
                        var ln = lines[i];
                        foreach (Match m in RxCall.Matches(ln))
                        {
                            var n = m.Groups[1].Value;
                            if (Keywords.Contains(n) || n == s.Name && i == s.Start - 1) continue;
                            string qual = null; bool ctor = false;
                            int b = m.Index - 1; while (b >= 0 && ln[b] == ' ') b--;
                            if (b >= 0 && ln[b] == '.' || b >= 1 && (ln[b] == '>' && ln[b - 1] == '-' || ln[b] == ':' && ln[b - 1] == ':'))
                            {
                                int e = ln[b] == '.' ? b - 1 : b - 2; while (e >= 0 && ln[e] == ' ') e--;
                                int st = e; while (st >= 0 && (char.IsLetterOrDigit(ln[st]) || ln[st] == '_' || ln[st] == '$')) st--;
                                if (e > st) qual = ln.Substring(st + 1, e - st);
                            }
                            else if (b >= 2 && ln.Substring(0, b + 1).EndsWith("new")) ctor = true;
                            var key = n + "|" + qual + "|" + ctor;
                            if (seen.Add(key) && s.Calls.Count < 300) s.Calls.Add(new CgCall { Name = n, Qual = qual, Ctor = ctor, Line = i + 1 });
                        }
                        foreach (Match m in RxNew.Matches(ln))
                        {
                            var n = m.Groups[1].Value;
                            if (seen.Add(n + "||True") && s.Calls.Count < 300) s.Calls.Add(new CgCall { Name = n, Ctor = true, Line = i + 1 });
                        }
                    }
                    if (words.Count < 300) foreach (Match m in RxWord.Matches(lines[i])) { if (words.Count >= 300) break; foreach (var w in SplitIdent(m.Value)) words.Add(w); }
                }
            }
            if (s.Doc != null) foreach (var w in Brain.Tokens(s.Doc)) if (words.Count < 340) words.Add(w);
            s.Words = string.Join(" ", words);
        }
        // Lời gọi chỉ thuộc hàm trong cùng: bỏ khỏi hàm/kiểu bao ngoài những lời gọi nằm trong hàm con
        foreach (var s in f.Syms)
        {
            var kids = f.Syms.Where(k => k != s && k.Start > s.Start && k.End <= s.End && !IsType(k.Kind)).ToList();
            if (kids.Count == 0) continue;
            s.Calls = s.Calls.Where(c => !kids.Any(k => c.Line >= k.Start && c.Line <= k.End)).ToList();
        }
        f.Syms = f.Syms.OrderBy(s => s.Start).ToList();
        return f;
    }

    public static bool IsType(string kind) { return kind == "class" || kind == "interface" || kind == "struct" || kind == "enum" || kind == "record" || kind == "trait" || kind == "protocol" || kind == "object" || kind == "namespace" || kind == "module" || kind == "impl"; }

    static void ParseBraces(CgFile f, string lang, string clean, string[] lines, int[] lineStart, string[] rawLines)
    {
        var typeStack = new List<Tuple<string, int>>();
        string currentType = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Trim().Length < 3) continue;
            while (typeStack.Count > 0 && i + 1 > typeStack[typeStack.Count - 1].Item2) typeStack.RemoveAt(typeStack.Count - 1);
            currentType = typeStack.Count > 0 ? typeStack[typeStack.Count - 1].Item1 : null;
            string name = null, kind = null, container = null; int col = 0;
            Match m;
            if (lang == "go" && (m = RxGoType.Match(l)).Success) { name = m.Groups[1].Value; kind = m.Groups[2].Value; col = m.Index; }
            else if (lang == "go" && (m = RxGoFunc.Match(l)).Success) { name = m.Groups[2].Value; kind = m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? "method" : "function"; if (kind == "method") container = m.Groups[1].Value; col = m.Index; }
            else if (lang == "rs" && (m = RxRsImpl.Match(l)).Success) { name = m.Groups[1].Value; kind = "impl"; col = m.Index; }
            else if (lang == "rs" && (m = RxRsFn.Match(l)).Success) { name = m.Groups[1].Value; kind = "function"; col = m.Index; }
            else if ((m = RxType.Match(l)).Success && !l.TrimStart().StartsWith("using") && !Regex.IsMatch(l, @"\benum\s+class\b(?!\s*\w+\s*[{:])") && !l.Contains("::class"))
            {
                kind = m.Groups[1].Value; name = m.Groups[2].Value; col = m.Index;
                if (kind == "namespace" && l.TrimEnd().EndsWith(";")) { kind = null; name = null; }   // namespace dạng file: bỏ
                else if ((kind == "object" && lang != "kt") || (kind == "module" && lang != "js") || (kind == "trait" && lang != "rs" && lang != "php") || (kind == "protocol" && lang != "swift"))
                {
                    // "object gate = ..." trong C#: không phải khai báo kiểu – thử lại như hàm
                    kind = null; name = null;
                    if (lang != "js" && (m = RxCMethod.Match(l)).Success && !Keywords.Contains(m.Groups[2].Value) && !IsStatementPrefix(m.Groups[1].Value))
                    { name = m.Groups[2].Value; kind = currentType != null ? "method" : "function"; col = m.Groups[2].Index; }
                }
            }
            else if ((m = RxKwFn.Match(l)).Success && (lang == "js" || lang == "kt" || lang == "swift" || lang == "php")) { name = m.Groups[1].Value; kind = "function"; col = m.Index; }
            else if (lang == "js" && (m = RxJsVarFn.Match(l)).Success) { name = m.Groups[1].Value; kind = "function"; col = m.Index; }
            else if (lang == "js" && currentType != null && (m = RxJsMethod.Match(l)).Success && !Keywords.Contains(m.Groups[1].Value)) { name = m.Groups[1].Value; kind = name == "constructor" ? "constructor" : "method"; col = m.Index; }
            else if (lang != "js" && lang != "go" && lang != "rs" && (m = RxCMethod.Match(l)).Success && !Keywords.Contains(m.Groups[2].Value) && !IsStatementPrefix(m.Groups[1].Value))
            { name = m.Groups[2].Value; kind = currentType != null ? "method" : "function"; col = m.Groups[2].Index; }
            else if (lang != "js" && currentType != null && (m = RxCtor.Match(l)).Success && m.Groups[1].Value == currentType) { name = m.Groups[1].Value; kind = "constructor"; col = m.Groups[1].Index; }
            if (name == null || Keywords.Contains(name)) continue;

            // Tìm thân: '{' trước ';' (hoặc '=>' thân biểu thức)
            int pos = lineStart[i] + col, open = -1, end = -1, depthParen = 0;
            for (int p = pos; p < clean.Length && p < pos + 3000; p++)
            {
                char c = clean[p];
                if (c == '(') depthParen++;
                else if (c == ')') depthParen--;
                else if (depthParen <= 0 && c == '{') { open = p; break; }
                else if (depthParen <= 0 && c == ';') { end = p; break; }
                else if (depthParen <= 0 && c == '=' && p + 1 < clean.Length && clean[p + 1] == '>' && !IsType(kind)) { int semi = clean.IndexOf(';', p); if (semi > 0 && clean.IndexOf('{', p, semi - p) < 0) { end = semi; } break; }
            }
            if (open < 0 && end < 0) continue;
            if (open >= 0)
            {
                // Hàm C/C#: giữa ')' và '{' không được có thứ lạ (tránh nhầm lời gọi trong câu lệnh)
                if (!IsType(kind) && kind != "impl")
                {
                    var between = clean.Substring(pos, open - pos);
                    if (between.Contains("=") && !between.Contains("=>") && lang != "js" && lang != "kt" && lang != "swift" && lang != "rs" && lang != "go" && !between.Contains("=default")) continue;
                }
                int d = 0; end = clean.Length - 1;
                for (int p = open; p < clean.Length; p++) { if (clean[p] == '{') d++; else if (clean[p] == '}') { d--; if (d == 0) { end = p; break; } } }
            }
            else if (!IsType(kind) && lang != "c" && !l.Contains("=>") && kind != "constructor" && !Regex.IsMatch(l, @"\b(abstract|extern|partial)\b") && currentType == null) continue;   // khai báo trơn: bỏ
            int endLine = LineOf(lineStart, end) + 1;
            var sig = rawLines[i].Trim();
            if (sig.Length > 200) sig = sig.Substring(0, 200) + "…";
            var s = new CgSym { Name = name, Kind = kind, Container = container, Sig = sig, Start = i + 1, End = Math.Max(i + 1, endLine) };
            if (IsType(kind))
            {
                var head = clean.Substring(pos, Math.Max(0, (open >= 0 ? open : end) - pos));
                var bm = Regex.Match(head, @"(?::|\bextends\b|\bimplements\b|\bfor\b)\s*([^{]+)$");
                if (bm.Success) foreach (Match w in Regex.Matches(Regex.Replace(bm.Groups[1].Value, @"<[^<>]*>", ""), @"[A-Za-z_]\w*"))
                        if (!Keywords.Contains(w.Value) && w.Value != "implements" && w.Value != "extends" && s.Bases.Count < 10 && w.Value != "where") s.Bases.Add(w.Value);
                if (kind != "namespace" && kind != "module") typeStack.Add(Tuple.Create(name, s.End));
            }
            f.Syms.Add(s);
            if (f.Syms.Count > 3000) break;
        }
    }

    static bool IsStatementPrefix(string prefix)
    {
        var w = prefix.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0) return true;
        foreach (var x in w) if (x == "return" || x == "new" || x == "else" || x == "throw" || x == "await" || x == "yield" || x == "case" || x == "goto" || x == "using" || x == "var" || x == "echo") return true;
        if (w[w.Length - 1].EndsWith(".") || w[w.Length - 1].EndsWith(",")) return true;
        return false;
    }

    static void ParseIndented(CgFile f, string lang, string[] lines, string[] rawLines)
    {
        var open = new List<Tuple<CgSym, int>>();   // ký hiệu + mức thụt
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Trim().Length == 0) continue;
            int indent = l.Length - l.TrimStart().Length;
            // đóng các khối có mức thụt >= dòng hiện tại (Python); Ruby/Lua/PS: đóng khi gặp ký hiệu mới cùng mức
            for (int k = open.Count - 1; k >= 0; k--)
            {
                bool closes = lang == "py" ? indent <= open[k].Item2 : (indent <= open[k].Item2 && (Regex.IsMatch(l, @"^\s*(def|class|module|function|local\s+function)\b") || (lang != "ps" && Regex.IsMatch(l, @"^\s*end\b") && indent == open[k].Item2) || (lang == "ps" && l.TrimStart().StartsWith("}") && indent == open[k].Item2)));
                if (!closes) continue;
                var s = open[k].Item1;
                s.End = Regex.IsMatch(l, @"^\s*(end\b|})") && indent == open[k].Item2 ? i + 1 : LastNonBlank(lines, i - 1);
                open.RemoveAt(k);
            }
            Match m; string name = null, kind = null;
            if (lang == "py" && (m = RxPy.Match(l)).Success) { kind = m.Groups[2].Value == "class" ? "class" : "function"; name = m.Groups[3].Value; }
            else if (lang == "rb" && (m = RxRb.Match(l)).Success) { kind = m.Groups[2].Value == "def" ? "function" : m.Groups[2].Value; name = m.Groups[3].Value; }
            else if (lang == "lua" && (m = RxLua.Match(l)).Success) { kind = "function"; var full = m.Groups[2].Value; var cut = full.LastIndexOfAny(new[] { '.', ':' }); name = cut >= 0 ? full.Substring(cut + 1) : full; }
            else if (lang == "ps" && (m = RxPs.Match(l)).Success) { kind = "function"; name = m.Groups[2].Value; }
            if (name == null) continue;
            if (kind == "function" && open.Count > 0 && IsType(open[open.Count - 1].Item1.Kind)) kind = "method";
            var sig = rawLines[i].Trim(); if (sig.Length > 200) sig = sig.Substring(0, 200) + "…";
            var sym = new CgSym { Name = name, Kind = kind, Sig = sig, Start = i + 1, End = i + 1 };
            if (lang == "py" && kind == "class") { var bm = Regex.Match(l, @"class\s+\w+\s*\(([^)]*)\)"); if (bm.Success) foreach (Match w in Regex.Matches(bm.Groups[1].Value, @"[A-Za-z_]\w*")) if (w.Value != "object" && w.Value != "metaclass") sym.Bases.Add(w.Value); }
            if (lang == "py") sym.Doc = PyDoc(rawLines, i);
            f.Syms.Add(sym);
            open.Add(Tuple.Create(sym, indent));
            if (f.Syms.Count > 3000) break;
        }
        foreach (var o in open) o.Item1.End = LastNonBlank(lines, lines.Length - 1);
    }

    static int LastNonBlank(string[] lines, int i) { while (i > 0 && lines[i].Trim().Length == 0) i--; return i + 1; }
    static int LineOf(int[] lineStart, int offset)
    {
        int lo = 0, hi = lineStart.Length - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (lineStart[mid] <= offset) lo = mid; else hi = mid - 1; }
        return lo;
    }

    static string PyDoc(string[] raw, int defLine)
    {
        for (int i = defLine + 1; i < raw.Length && i < defLine + 4; i++)
        {
            var t = raw[i].Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("\"\"\"") || t.StartsWith("'''")) { var d = t.Trim('"', '\'', ' '); if (d.Length == 0 && i + 1 < raw.Length) d = raw[i + 1].Trim(); return d.Length > 200 ? d.Substring(0, 200) + "…" : d; }
            break;
        }
        return null;
    }

    static string DocAbove(string[] raw, int idx, string lang, int start, int end)
    {
        var parts = new List<string>();
        for (int i = idx - 1; i >= 0 && i >= idx - 8; i--)
        {
            var t = raw[i].Trim();
            if (t.StartsWith("[") && t.EndsWith("]") || t.StartsWith("@")) continue;   // attribute/decorator
            if (t.StartsWith("///") || t.StartsWith("//") || t.StartsWith("#") && lang != "c" || t.StartsWith("*") || t.StartsWith("/*") || t.StartsWith("--") && lang == "lua")
            {
                var c = Regex.Replace(t, @"^(///|//|#|/\*\*|/\*|\*/|\*|--)\s*", "");
                c = Regex.Replace(c, @"</?\w+[^>]*>", "").Trim();
                if (c.Length > 0) parts.Insert(0, c);
            }
            else break;
        }
        if (parts.Count == 0) return null;
        var s = string.Join(" ", parts);
        return s.Length > 240 ? s.Substring(0, 240) + "…" : s;
    }

    // Xoá chú thích và nội dung chuỗi (giữ nguyên số dòng và độ dài) để dò ký hiệu không bị nhầm
    static string Clean(string s, string lang)
    {
        var sb = new StringBuilder(s);
        bool hash = lang == "py" || lang == "rb" || lang == "ps" || lang == "php";
        bool slash = lang != "py" && lang != "rb" && lang != "ps" && lang != "lua";
        int n = s.Length, i = 0;
        Action<int, int> blank = (a, b) => { for (int k = a; k < b && k < n; k++) if (sb[k] != '\n') sb[k] = ' '; };
        while (i < n)
        {
            char c = s[i];
            if (slash && c == '/' && i + 1 < n && s[i + 1] == '/') { int e = s.IndexOf('\n', i); if (e < 0) e = n; blank(i, e); i = e; continue; }
            if (slash && c == '/' && i + 1 < n && s[i + 1] == '*') { int e = s.IndexOf("*/", i + 2); e = e < 0 ? n : e + 2; blank(i, e); i = e; continue; }
            if (hash && c == '#' && !(lang == "php" && i + 1 < n && s[i + 1] == '[')) { int e = s.IndexOf('\n', i); if (e < 0) e = n; blank(i, e); i = e; continue; }
            if (lang == "lua" && c == '-' && i + 1 < n && s[i + 1] == '-') { int e = s.IndexOf('\n', i); if (e < 0) e = n; blank(i, e); i = e; continue; }
            if (lang == "ps" && c == '<' && i + 1 < n && s[i + 1] == '#') { int e = s.IndexOf("#>", i + 2); e = e < 0 ? n : e + 2; blank(i, e); i = e; continue; }
            if (lang == "py" && (c == '"' || c == '\'') && i + 2 < n && s[i + 1] == c && s[i + 2] == c)
            {
                var q = new string(c, 3); int e = s.IndexOf(q, i + 3); e = e < 0 ? n : e + 3; blank(i + 1, e - 1); i = e; continue;
            }
            if (c == '/' && (lang == "js" || lang == "rb") && IsRegexStart(s, i))
            {
                // Biểu thức chính quy /…/ của JS: ngoặc và dấu nháy bên trong không được tính
                int k = i + 1; bool cls = false;
                while (k < n && s[k] != '\n')
                {
                    if (s[k] == '\\') { k += 2; continue; }
                    if (s[k] == '[') cls = true; else if (s[k] == ']') cls = false;
                    else if (s[k] == '/' && !cls) break;
                    k++;
                }
                if (k < n && s[k] == '/') { blank(i + 1, k); i = k + 1; continue; }
            }
            if (c == '"' || c == '\'' || c == '`' && lang == "js")
            {
                // ' trong C/C#/Java/Rust là ký tự; trong Rust 'a là lifetime
                if (c == '\'' && lang == "rs" && !(i + 2 < n && (s[i + 2] == '\'' || s[i + 1] == '\\'))) { i++; continue; }
                bool verbatim = lang == "cs" && i > 0 && (s[i - 1] == '@' || (i > 1 && s[i - 1] == '$' && s[i - 2] == '@') || (i > 1 && s[i - 1] == '@' && s[i - 2] == '$'));
                int k = i + 1;
                while (k < n)
                {
                    if (s[k] == '\\' && !verbatim) { k += 2; continue; }
                    if (s[k] == c) { if (verbatim && k + 1 < n && s[k + 1] == c) { k += 2; continue; } break; }
                    if (s[k] == '\n' && c != '`' && !verbatim) break;
                    k++;
                }
                blank(i + 1, Math.Min(k, n)); i = k + 1; continue;
            }
            i++;
        }
        return sb.ToString();
    }

    static bool IsRegexStart(string s, int i)
    {
        if (i + 1 < s.Length && (s[i + 1] == '/' || s[i + 1] == '*')) return false;
        int b = i - 1; while (b >= 0 && (s[b] == ' ' || s[b] == '\t')) b--;
        if (b < 0 || s[b] == '\n') return true;
        char p = s[b];
        if ("(,=:[!&|?{};+-*%<>~^".IndexOf(p) >= 0) return true;
        int e = b; while (b >= 0 && char.IsLetter(s[b])) b--;
        var word = s.Substring(b + 1, e - b);
        return word == "return" || word == "typeof" || word == "case" || word == "in" || word == "of" || word == "yield";
    }

    // "ReadDocxFile" / "read_docx_file" -> read, docx, file (đã gập chữ thường)
    public static IEnumerable<string> SplitIdent(string id)
    {
        var parts = Regex.Split(id, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[_\-$.:]+");
        foreach (var p in parts) if (p.Length >= 2) yield return p.ToLowerInvariant();
        if (parts.Length > 1 && id.Length >= 3) yield return id.ToLowerInvariant();
    }

    // ================= Truy vấn =================
    static string Rel(CgGraph g, string path) { return path.StartsWith(g.Root, StringComparison.OrdinalIgnoreCase) ? path.Substring(g.Root.Length).TrimStart('\\') : path; }
    static string Loc(CgGraph g, CgSym s) { return Rel(g, s.File.Path) + ":" + s.Start + "-" + s.End; }

    static List<CgSym> Find(CgGraph g, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new List<CgSym>();
        name = name.Trim().TrimEnd('(', ')');
        string container = null;
        int dot = name.LastIndexOf('.');
        if (dot > 0) { container = name.Substring(0, dot); name = name.Substring(dot + 1); if (container.Contains(".")) container = container.Substring(container.LastIndexOf('.') + 1); }
        var list = g.Defs(name);
        if (list.Count == 0) list = g.ByName.Where(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).SelectMany(kv => kv.Value).ToList();
        if (container != null) { var c = list.Where(s => string.Equals(s.Container, container, StringComparison.OrdinalIgnoreCase)).ToList(); if (c.Count > 0) list = c; }
        return list.OrderBy(s => IsType(s.Kind) ? 0 : 1).ThenBy(s => s.File.Path).ToList();
    }

    // Phân giải một lời gọi về các định nghĩa trong dự án: dùng tên kiểu đứng trước dấu chấm (Brain.Search),
    // "new X(" chỉ khớp kiểu/constructor, gọi trơn ưu tiên cùng class rồi cùng file.
    public static List<CgSym> Resolve(CgGraph g, CgSym from, CgCall c)
    {
        var d = g.Defs(c.Name);
        if (d.Count == 0) return d;
        if (c.Ctor) { d = d.Where(x => IsType(x.Kind) || x.Kind == "constructor").ToList(); return d; }
        d = d.Where(x => !IsType(x.Kind) || x.Kind == "class" || x.Kind == "struct" || x.Kind == "record").ToList();
        if (d.Count <= 1) return d;
        if (c.Qual != null && c.Qual != "this" && c.Qual != "self" && c.Qual != "base" && c.Qual != "super")
        {
            var byType = d.Where(x => x.Container == c.Qual).ToList();
            if (byType.Count > 0) return byType;
            // biến/thuộc tính: không biết kiểu → loại các hàm tĩnh của class khác tên? giữ nguyên (INFERRED)
            return d;
        }
        var same = d.Where(x => from != null && x.Container == from.Container && x.Container != null).ToList();
        if (same.Count > 0) return same;
        if (c.Qual == null)
        {
            var sameFile = d.Where(x => from != null && x.File == from.File).ToList();
            if (sameFile.Count > 0) return sameFile;
        }
        return d;
    }

    // Callees của một ký hiệu: (lời gọi, các định nghĩa khớp)
    static List<Tuple<CgCall, List<CgSym>>> Callees(CgGraph g, CgSym s)
    {
        var r = new List<Tuple<CgCall, List<CgSym>>>();
        foreach (var c in s.Calls)
        {
            var d = Resolve(g, s, c).Where(x => x != s).ToList();
            if (d.Count > 0) r.Add(Tuple.Create(c, d));
        }
        return r;
    }

    // Nơi gọi tới đúng các định nghĩa này (lọc theo phân giải), kèm số ứng viên
    static List<Tuple<CgSym, CgCall, int>> CallSites(CgGraph g, List<CgSym> defs, string bare)
    {
        var set = new HashSet<CgSym>(defs);
        var r = new List<Tuple<CgSym, CgCall, int>>();
        foreach (var t in g.CallersOf(bare))
        {
            if (set.Contains(t.Item1)) continue;
            if (defs.Count == 0) { r.Add(Tuple.Create(t.Item1, t.Item2, 0)); continue; }
            var res = Resolve(g, t.Item1, t.Item2);
            if (res.Any(set.Contains)) r.Add(Tuple.Create(t.Item1, t.Item2, res.Count));
        }
        return r;
    }

    static string Certainty(int candidates) { return candidates == 1 ? "EXTRACTED" : "INFERRED·" + candidates; }

    static string[] RawLines(string path)
    {
        try { return File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); } catch { return new string[0]; }
    }

    static void AppendCode(StringBuilder sb, string[] raw, int from, int to)
    {
        for (int i = Math.Max(1, from); i <= to && i <= raw.Length; i++) sb.Append(i).Append('\t').AppendLine(raw[i - 1].TrimEnd());
    }

    static string Header(CgGraph g)
    {
        return L.T("Project: ", "Dự án: ") + g.Root + "  —  " + Trust.Tag(Trust.Of(g.Root)) + "\n" +
               L.T("Code graph: ", "Đồ thị code: ") + g.Files.Count + L.T(" files, ", " file, ") + g.SymCount + L.T(" symbols (fresh; changed files are re-read automatically)", " ký hiệu (luôn mới; file đổi được đọc lại tự động)") + "\n";
    }

    // ---------- code_map ----------
    public static string CodeMap(string path, int budgetTokens)
    {
        var root = RootOf(path);
        var g = Get(root);
        var sb = new StringBuilder();
        int maxChars = Math.Max(1000, budgetTokens) * 4;
        string full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\');
        lock (g.Gate)
        {
            CgFile one;
            if (g.Files.TryGetValue(full, out one))
            {
                sb.AppendLine(Rel(g, one.Path) + "  (" + one.LineCount + L.T(" lines, ", " dòng, ") + one.Lang + ")");
                if (one.Imports.Count > 0) sb.AppendLine(L.T("Imports: ", "Import: ") + string.Join(", ", one.Imports.Take(30)));
                foreach (var s in one.Syms)
                {
                    int depth = one.Syms.Count(t => t != s && IsType(t.Kind) && t.Start <= s.Start && t.End >= s.End && t.Kind != "namespace");
                    sb.Append(new string(' ', 2 + depth * 2)).Append(s.Kind).Append(' ').Append(s.Name).Append("  ").Append(s.Start).Append('-').Append(s.End);
                    if (s.Bases.Count > 0) sb.Append("  : ").Append(string.Join(", ", s.Bases));
                    int callers = CallSites(g, new List<CgSym> { s }, s.Name).Count;
                    if (callers > 0) sb.Append(L.T("  ← ", "  ← ")).Append(callers);
                    sb.AppendLine();
                    if (!string.IsNullOrEmpty(s.Doc)) sb.Append(new string(' ', 4 + depth * 2)).AppendLine("// " + s.Doc);
                    if (sb.Length > maxChars) { sb.AppendLine("…"); break; }
                }
                sb.AppendLine().Append(L.T("Read only the lines you need: read_file path start_line max_lines. ← n = number of callers (who_calls).", "Chỉ đọc đúng đoạn cần: read_file path start_line max_lines. ← n = số nơi gọi (who_calls)."));
                return Track("code_map", sb.ToString(), one.Size);
            }
            sb.Append(Header(g));
            sb.AppendLine().AppendLine(Hubs(g, 15));
            sb.AppendLine(L.T("Files (symbols with line ranges):", "Các file (ký hiệu kèm khoảng dòng):"));
            string under = Directory.Exists(full) ? full + "\\" : g.Root + "\\";
            foreach (var f in g.Files.Values.Where(f => f.Path.StartsWith(under, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path))
            {
                sb.Append("• ").Append(Rel(g, f.Path)).Append(" (").Append(f.LineCount).Append(")");
                var top = f.Syms.Where(s => s.Kind != "namespace").Take(40).Select(s => (IsType(s.Kind) ? s.Kind + " " : "") + s.Name + " " + s.Start + "-" + s.End);
                sb.Append(": ").AppendLine(string.Join(", ", top) + (f.Syms.Count > 40 ? ", …" : ""));
                if (sb.Length > maxChars) { sb.AppendLine(L.T("… (budget reached; call code_map on a sub-folder or a file)", "… (hết ngân sách; gọi code_map cho thư mục con hoặc một file)")); break; }
            }
            return Track("code_map", sb.ToString().TrimEnd(), g.Files.Values.Where(f => f.Path.StartsWith(under, StringComparison.OrdinalIgnoreCase)).Sum(f => f.Size));
        }
    }

    // Ký hiệu trung tâm: được gọi từ nhiều nơi nhất (tên duy nhất trong dự án)
    public static string Hubs(CgGraph g, int n)
    {
        var hubs = g.ByName.Where(kv => kv.Value.Count == 1)
            .Select(kv => new { s = kv.Value[0], n = CallSites(g, kv.Value, kv.Key).Select(t => t.Item1.File.Path).Distinct().Count() })
            .Where(x => x.n >= 2 && !Regex.IsMatch(x.s.File.Path, @"[\/](tests?|__tests__|spec|stubs?|mocks?|fixtures)[\/]", RegexOptions.IgnoreCase)).OrderByDescending(x => x.n).Take(n).ToList();
        if (hubs.Count == 0) return "";
        var sb = new StringBuilder(L.T("Central symbols (called from the most files):", "Ký hiệu trung tâm (được gọi từ nhiều file nhất):") + "\n");
        foreach (var h in hubs) sb.Append("  ").Append(h.s.Full).Append("  ").Append(Loc(g, h.s)).Append(L.T("  ← ", "  ← ")).Append(h.n).AppendLine(L.T(" files", " file"));
        return sb.ToString();
    }

    public static string OverviewSection(string projectPath)
    {
        try
        {
            var g = Get(RootOf(projectPath));
            lock (g.Gate)
            {
                if (g.Files.Count == 0) return "";
                var langs = g.Files.Values.GroupBy(f => f.Lang).OrderByDescending(x => x.Count()).Select(x => x.Key + " " + x.Count());
                return L.T("===== Code graph =====\n", "===== Đồ thị code =====\n") + g.Files.Count + L.T(" code files (", " file code (") + string.Join(", ", langs) + "), " + g.SymCount + L.T(" symbols\n", " ký hiệu\n") + Hubs(g, 10) +
                       L.T("Tip: context_pack \"<task>\" gives the exact code for a task within a token budget; code_map / explain / who_calls avoid reading whole files.", "Mẹo: context_pack \"<nhiệm vụ>\" trả đúng đoạn code cho một nhiệm vụ trong giới hạn token; code_map / explain / who_calls giúp khỏi đọc cả file.");
            }
        }
        catch { return ""; }
    }

    // ---------- who_calls ----------
    public static string WhoCalls(string name, string path, int limit)
    {
        var g = Get(RootOf(path));
        var sb = new StringBuilder();
        lock (g.Gate)
        {
            var defs = Find(g, name);
            string bare = defs.Count > 0 ? defs[0].Name : name.Trim().TrimEnd('(', ')').Split('.').Last();
            var calls = CallSites(g, defs, bare);
            if (defs.Count == 0 && calls.Count == 0) return L.T("No symbol or call named \"", "Không có ký hiệu hay lời gọi nào tên \"") + name + L.T("\" in ", "\" trong ") + g.Root + L.T(". Try context_pack or search.", ". Thử context_pack hoặc search.");
            sb.AppendLine(L.T("Definition", "Định nghĩa") + (defs.Count > 1 ? " (" + defs.Count + ")" : "") + ":");
            foreach (var d in defs.Take(5)) sb.AppendLine("  " + d.Kind + " " + d.Full + "  " + Loc(g, d) + "  " + d.Sig);
            if (defs.Count == 0) sb.AppendLine(L.T("  (not defined in this project — library/framework call)", "  (không định nghĩa trong dự án — gọi thư viện/framework)"));
            sb.AppendLine().AppendLine(calls.Count + L.T(" call sites", " nơi gọi") + ":");
            var byFile = calls.GroupBy(t => t.Item1.File.Path).OrderBy(x => x.Key).ToList();
            int shown = 0; long baseline = 0;
            foreach (var fg in byFile)
            {
                var raw = RawLines(fg.Key);
                baseline += fg.First().Item1.File.Size;
                sb.AppendLine("• " + Rel(g, fg.Key));
                foreach (var t in fg.OrderBy(t => t.Item2.Line))
                {
                    if (shown++ >= limit) break;
                    var line = t.Item2.Line <= raw.Length ? raw[t.Item2.Line - 1].Trim() : "";
                    if (line.Length > 160) line = line.Substring(0, 160) + "…";
                    sb.AppendLine("    " + t.Item2.Line + L.T(" in ", " trong ") + t.Item1.Full + (t.Item3 > 1 ? "  [" + Certainty(t.Item3) + "]" : "") + ":  " + line);
                }
                if (shown >= limit) { sb.AppendLine(L.T("  … more; raise limit", "  … còn nữa; tăng limit")); break; }
            }
            return Track("who_calls", sb.ToString().TrimEnd(), baseline);
        }
    }

    // ---------- explain ----------
    public static string Explain(string name, string path)
    {
        var g = Get(RootOf(path));
        var sb = new StringBuilder();
        long baseline = 0;
        lock (g.Gate)
        {
            var defs = Find(g, name);
            if (defs.Count == 0) return L.T("No symbol named \"", "Không có ký hiệu tên \"") + name + L.T("\" in ", "\" trong ") + g.Root + L.T(". Try context_pack \"<what you want to do>\" or search.", ". Thử context_pack \"<việc cần làm>\" hoặc search.");
            foreach (var s in defs.Take(3))
            {
                var raw = RawLines(s.File.Path);
                baseline += s.File.Size;
                sb.AppendLine("■ " + s.Kind + " " + s.Full + "  —  " + Loc(g, s) + "  (" + s.Lines + L.T(" lines)", " dòng)"));
                if (!string.IsNullOrEmpty(s.Doc)) sb.AppendLine("  " + s.Doc);
                if (s.Bases.Count > 0) sb.AppendLine(L.T("  Inherits/implements: ", "  Kế thừa/hiện thực: ") + string.Join(", ", s.Bases));
                if (IsType(s.Kind))
                {
                    var members = s.File.Syms.Where(m => m != s && m.Start > s.Start && m.End <= s.End && m.Container == s.Name).Take(60).ToList();
                    if (members.Count > 0) sb.AppendLine(L.T("  Members: ", "  Thành viên: ") + string.Join(", ", members.Select(m => m.Name + " " + m.Start + "-" + m.End)));
                    var subs = g.Files.Values.SelectMany(f => f.Syms).Where(x => x.Bases.Contains(s.Name)).Take(15).ToList();
                    if (subs.Count > 0) sb.AppendLine(L.T("  Subtypes: ", "  Lớp con: ") + string.Join(", ", subs.Select(x => x.Name + " (" + Loc(g, x) + ")")));
                }
                sb.AppendLine(L.T("  Code:", "  Code:"));
                if (s.Lines <= 60) AppendCode(sb, raw, s.Start, s.End);
                else { AppendCode(sb, raw, s.Start, s.Start + 24); sb.AppendLine(L.T("  … (", "  … (") + (s.Lines - 25) + L.T(" more lines: read_file start_line=", " dòng nữa: read_file start_line=") + (s.Start + 25) + ")"); }
                var outs = Callees(g, s).Take(25).ToList();
                if (outs.Count > 0)
                {
                    sb.AppendLine(L.T("  Calls:", "  Gọi tới:"));
                    foreach (var o in outs) sb.AppendLine("    " + o.Item1.Name + "  → " + string.Join(" | ", o.Item2.Take(2).Select(d => Loc(g, d))) + "  [" + Certainty(o.Item2.Count) + "]");
                }
                var ins = CallSites(g, new List<CgSym> { s }, s.Name);
                if (ins.Count > 0)
                {
                    sb.AppendLine(L.T("  Called by (", "  Được gọi bởi (") + ins.Count + "):");
                    foreach (var t in ins.Take(20)) sb.AppendLine("    " + t.Item1.Full + "  " + Rel(g, t.Item1.File.Path) + ":" + t.Item2.Line + "  [" + Certainty(t.Item3) + "]");
                    if (ins.Count > 20) sb.AppendLine(L.T("    … who_calls for all", "    … who_calls để xem hết"));
                }
                sb.AppendLine();
            }
            if (defs.Count > 3) sb.AppendLine(L.T("(+", "(+") + (defs.Count - 3) + L.T(" more definitions with this name: ", " định nghĩa trùng tên khác: ") + string.Join(", ", defs.Skip(3).Take(10).Select(d => Loc(g, d))) + ")");
        }
        AppendMemories(sb, name, defs0: null);
        AppendElsewhere(sb, name, g.Root, 3);
        return Track("explain", sb.ToString().TrimEnd(), baseline);
    }

    static void AppendMemories(StringBuilder sb, string query, object defs0)
    {
        try
        {
            var mem = Memory.RecallRaw(query, 4, false).Where(r => r.N.Kind != "file" && r.N.Kind != "du-an" && r.Score >= 3).ToList();
            if (mem.Count > 0) sb.AppendLine(L.T("Verified memories:", "Ghi nhớ đã xác minh:")).AppendLine(Memory.Format(mem, false)).AppendLine();
        }
        catch { }
    }

    // Cùng tên/chủ đề ở dự án KHÁC, chỉ nguồn ✓ tin cậy (để tái dùng thay vì viết lại)
    static void AppendElsewhere(StringBuilder sb, string query, string root, int max)
    {
        try
        {
            var list = new List<string>();
            foreach (var h in Brain.Search(query, null, null, false, 40))
            {
                if (list.Count >= max) break;
                if ((h.File.Path + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (!LangOf.ContainsKey(Path.GetExtension(h.File.Path))) continue;
                var t = Trust.Of(h.File.Path);
                if (t.Level != Trust.Level.Trusted) continue;
                var snip = h.Snippet == null ? "" : string.Join(" | ", h.Snippet.Split('\n').Take(2).Select(x => x.Trim()));
                list.Add("• " + h.File.Path + "  [✓]" + (snip.Length > 0 ? "\n    " + (snip.Length > 200 ? snip.Substring(0, 200) + "…" : snip) : ""));
            }
            if (list.Count > 0) sb.AppendLine(L.T("✓ Trusted examples in other projects (reuse before rewriting):", "Ví dụ ✓ tin cậy ở dự án khác (tái dùng trước khi viết lại):")).AppendLine(string.Join("\n", list)).AppendLine();
        }
        catch { }
    }

    // ---------- path ----------
    public static string CallPath(string from, string to, string path)
    {
        var g = Get(RootOf(path));
        lock (g.Gate)
        {
            var a = Find(g, from); var b = new HashSet<CgSym>(Find(g, to));
            if (a.Count == 0 || b.Count == 0) return L.T("Symbol not found: ", "Không thấy ký hiệu: ") + (a.Count == 0 ? from : to);
            var prev = new Dictionary<CgSym, CgSym>();
            var q = new Queue<CgSym>(a);
            foreach (var s in a) prev[s] = null;
            CgSym hit = null;
            while (q.Count > 0 && hit == null)
            {
                var s = q.Dequeue();
                if (b.Contains(s)) { hit = s; break; }
                foreach (var c in s.Calls)
                    foreach (var d in Resolve(g, s, c))
                        if (!prev.ContainsKey(d)) { prev[d] = s; q.Enqueue(d); if (prev.Count > 20000) break; }
            }
            if (hit == null) return L.T("No call path from ", "Không có đường gọi từ ") + from + L.T(" to ", " tới ") + to + L.T(" (by name-resolved calls; dynamic dispatch/events may not show).", " (theo lời gọi phân giải theo tên; gọi động/sự kiện có thể không thấy).");
            var chain = new List<CgSym>();
            for (var x = hit; x != null; x = prev[x]) chain.Insert(0, x);
            var sb = new StringBuilder(L.T("Call path (", "Đường gọi (") + (chain.Count - 1) + L.T(" steps):\n", " bước):\n"));
            for (int i = 0; i < chain.Count; i++)
            {
                var s = chain[i];
                sb.Append(new string(' ', i * 2)).Append(i == 0 ? "" : "→ ").Append(s.Full).Append("  ").Append(Loc(g, s));
                if (i + 1 < chain.Count) { var c = s.Calls.FirstOrDefault(k => k.Name == chain[i + 1].Name); if (c != null) sb.Append(L.T("  (call at line ", "  (gọi ở dòng ")).Append(c.Line).Append(", ").Append(Certainty(Resolve(g, s, c).Count)).Append(")"); }
                sb.AppendLine();
            }
            return Track("path", sb.ToString().TrimEnd(), chain.Select(s => s.File).Distinct().Sum(f => f.Size));
        }
    }

    // ---------- context_pack ----------
    public static string ContextPack(string task, string path, int budgetTokens)
    {
        if (string.IsNullOrWhiteSpace(task)) throw new ArgumentException(L.T("Describe the task in task.", "Mô tả nhiệm vụ trong task."));
        budgetTokens = Math.Max(800, Math.Min(30000, budgetTokens));
        int maxChars = budgetTokens * 4;
        var g = Get(RootOf(path));
        var q = new HashSet<string>();
        foreach (var t in Brain.Tokens(task)) { q.Add(t); foreach (var w in SplitIdent(t)) q.Add(w); }
        foreach (Match m in Regex.Matches(task, @"[A-Za-z_][A-Za-z0-9_]{2,}")) foreach (var w in SplitIdent(m.Value)) q.Add(w);
        q.RemoveWhere(w => Stop.Contains(w) || w.Length < 2);
        q = new HashSet<string>(q.Select(Stem));
        // Tên định danh nguyên văn trong câu (ví dụ ReadDocx, project_overview)
        var exact = new HashSet<string>(Regex.Matches(task, @"[A-Za-z_][A-Za-z0-9_]{2,}").Cast<Match>().Select(m => m.Value).Where(x => x.Any(char.IsUpper) && x.Any(char.IsLower) || x.Contains("_")));

        // Điểm file từ chỉ mục toàn văn của Second Brain
        var fileScore = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var h in Brain.Search(task, g.Root, null, false, 60)) fileScore[h.File.Path] = h.Score; } catch { }
        double maxFile = fileScore.Count > 0 ? fileScore.Values.Max() : 1;

        var sb = new StringBuilder();
        var used = new HashSet<CgFile>();
        lock (g.Gate)
        {
            // Trọng số hiếm (IDF): từ có ở hầu hết ký hiệu (vd tên dự án) gần như không tính
            var all = g.Files.Values.SelectMany(f => f.Syms).Where(s => s.Kind != "namespace" && s.Kind != "module").ToList();
            var wordSets = new Dictionary<CgSym, HashSet<string>>();
            var df = q.ToDictionary(w => w, w => 0);
            foreach (var s in all)
            {
                var ws = new HashSet<string>((s.Words ?? "").Split(' ').Where(x => x.Length > 0).Select(Stem));
                wordSets[s] = ws;
                foreach (var w in q) if (ws.Contains(w)) df[w]++;
            }
            var idf = q.ToDictionary(w => w, w => Math.Log((all.Count + 1.0) / (df[w] + 1.0)));
            double idfMax = idf.Count > 0 ? Math.Max(0.1, idf.Values.Max()) : 1;
            var scored = new List<Tuple<CgSym, double>>();
            foreach (var f in g.Files.Values)
            {
                double fs; fileScore.TryGetValue(f.Path, out fs);
                var pathWords = new HashSet<string>(Brain.Tokens(Rel(g, f.Path)).SelectMany(w => SplitIdent(w).Concat(new[] { w })).Select(Stem));
                double pathHit = q.Where(pathWords.Contains).Sum(w => idf[w]) / idfMax;
                foreach (var s in f.Syms)
                {
                    if (s.Kind == "namespace" || s.Kind == "module") continue;
                    var nameW = new HashSet<string>(SplitIdent(s.Name).Select(Stem)); nameW.Add(s.Name.ToLowerInvariant());
                    HashSet<string> words; if (!wordSets.TryGetValue(s, out words)) words = new HashSet<string>();
                    double sc = 0;
                    var nameHitW = q.Where(nameW.Contains).ToList(); var bodyHitW = q.Where(words.Contains).ToList();
                    int nameHit = nameHitW.Count(w => idf[w] > 0.7), bodyHit = bodyHitW.Count(w => idf[w] > 0.7);
                    sc += nameHitW.Sum(w => idf[w]) / idfMax * 6 + bodyHitW.Sum(w => idf[w]) / idfMax * 1.6 + pathHit * 1.5 + (fs / maxFile) * 3;
                    if (bodyHit >= 2) sc *= 1 + 0.25 * (bodyHit - 1);   // khớp nhiều từ khác nhau: thưởng
                    if (exact.Contains(s.Name) || exact.Contains(s.Full)) sc += 30;
                    if (!string.IsNullOrEmpty(s.Doc)) sc += q.Where(w => s.Doc.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).Sum(w => idf[w]) / idfMax * 1.5;
                    if (IsType(s.Kind) && s.Lines > 150) sc *= 0.5;   // class lớn: ưu tiên method bên trong
                    if (sc > 0 && (nameHit > 0 || bodyHit >= Math.Min(2, q.Count) || exact.Contains(s.Name))) scored.Add(Tuple.Create(s, sc));
                }
            }
            var ranked = scored.OrderByDescending(t => t.Item2).ToList();
            if (ranked.Count == 0)
            {
                sb.Append(Header(g)).AppendLine().AppendLine(L.T("No code matched the task words. Central symbols instead:", "Không có code khớp từ khoá của nhiệm vụ. Thay vào đó là các ký hiệu trung tâm:")).AppendLine(Hubs(g, 15));
                AppendMemories(sb, task, null);
                return Track("context_pack", sb.ToString().TrimEnd(), 0);
            }
            double top = ranked[0].Item2;
            var seeds = ranked.Where(t => t.Item2 >= top * 0.35).Take(8).Select(t => t.Item1).ToList();
            var scoreOf = new Dictionary<CgSym, double>();
            foreach (var t in ranked) scoreOf[t.Item1] = t.Item2;

            sb.Append(Header(g));
            sb.AppendLine(L.T("Task: ", "Nhiệm vụ: ") + task.Trim());
            sb.AppendLine(L.T("Code below is the exact current source with line numbers — edit from it directly; read_file only for what is marked \"…\".", "Code dưới đây là mã hiện tại kèm số dòng — sửa trực tiếp từ đây; chỉ read_file phần có đánh dấu \"…\"."));
            sb.AppendLine();
            var rawCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            Func<CgSym, string[]> rawOf = s => { string[] r; if (!rawCache.TryGetValue(s.File.Path, out r)) rawCache[s.File.Path] = r = RawLines(s.File.Path); return r; };
            var shownRanges = new List<CgSym>();
            int reserve = Math.Min(maxChars / 4, 2500);   // chừa chỗ cho quan hệ + trí nhớ
            sb.AppendLine(L.T("=== Most relevant code ===", "=== Code liên quan nhất ==="));
            foreach (var s in seeds)
            {
                if (shownRanges.Any(r => r.File == s.File && r.Start <= s.Start && r.End >= s.End)) continue;
                var raw = rawOf(s);
                int room = maxChars - reserve - sb.Length;
                if (room < 300) break;
                sb.AppendLine().AppendLine("## " + s.Kind + " " + s.Full + "  —  " + Loc(g, s) + (string.IsNullOrEmpty(s.Doc) ? "" : "  // " + s.Doc));
                int maxLines = Math.Max(8, room / 70);
                if (IsType(s.Kind) && s.Lines > maxLines)
                {
                    // Kiểu lớn: chữ ký + danh sách thành viên thay vì toàn bộ thân
                    sb.AppendLine(s.Start + "\t" + (s.Start <= raw.Length ? raw[s.Start - 1].TrimEnd() : s.Sig));
                    var members = s.File.Syms.Where(m => m != s && m.Start > s.Start && m.End <= s.End).Take(50);
                    sb.AppendLine(L.T("  members: ", "  thành viên: ") + string.Join(", ", members.Select(m => m.Name + " " + m.Start + "-" + m.End)));
                }
                else if (s.Lines <= maxLines) AppendCode(sb, raw, s.Start, s.End);
                else
                {
                    // Lấy phần đầu và các dòng chứa từ khoá
                    int head = Math.Max(6, maxLines / 2);
                    AppendCode(sb, raw, s.Start, s.Start + head - 1);
                    var hitsL = new List<int>();
                    for (int i = s.Start + head; i <= s.End && i <= raw.Length; i++)
                    {
                        var low = raw[i - 1].ToLowerInvariant();
                        if (q.Any(w => w.Length >= 3 && low.Contains(w)) || exact.Any(x => raw[i - 1].Contains(x))) hitsL.Add(i);
                    }
                    int budgetLines = maxLines - head; int last = -10;
                    foreach (var i in hitsL)
                    {
                        if (budgetLines <= 0) break;
                        if (i - 1 > last + 1) sb.AppendLine("  …");
                        for (int k = Math.Max(i - 1, last + 1); k <= Math.Min(i + 1, s.End) && budgetLines > 0; k++) { sb.Append(k).Append('\t').AppendLine(raw[k - 1].TrimEnd()); budgetLines--; last = k; }
                    }
                    sb.AppendLine(L.T("  … (", "  … (") + s.Lines + L.T(" lines total; read_file start_line=", " dòng tổng; read_file start_line=") + s.Start + " max_lines=" + s.Lines + ")");
                }
                shownRanges.Add(s); used.Add(s.File);
            }
            // Mở rộng 1 bước theo đồ thị, chỉ từ code đã hiện: hàm nó gọi (liên quan tới nhiệm vụ) và nơi gọi nó
            var related = new Dictionary<CgSym, string>();
            var relScore = new Dictionary<CgSym, double>();
            for (int si = 0; si < shownRanges.Count; si++)
            {
                var s = shownRanges[si];
                foreach (var c in Callees(g, s))
                {
                    if (c.Item2.Count > 2) continue;
                    foreach (var d in c.Item2)
                    {
                        double sc; scoreOf.TryGetValue(d, out sc);
                        if (shownRanges.Contains(d) || IsType(d.Kind) || related.ContainsKey(d) || (sc <= 0 && si >= 2)) continue;
                        related[d] = L.T("called by ", "được gọi bởi ") + s.Name + " [" + Certainty(c.Item2.Count) + "]"; relScore[d] = sc + 1;
                    }
                }
                foreach (var t in CallSites(g, new List<CgSym> { s }, s.Name).Take(6))
                {
                    if (shownRanges.Contains(t.Item1) || related.ContainsKey(t.Item1)) continue;
                    double sc; scoreOf.TryGetValue(t.Item1, out sc);
                    related[t.Item1] = L.T("calls ", "gọi ") + s.Name + L.T(" at line ", " ở dòng ") + t.Item2.Line; relScore[t.Item1] = sc + 2;
                }
            }
            // Quan hệ
            if (related.Count > 0)
            {
                sb.AppendLine().AppendLine(L.T("=== Connected code (graph, 1 hop) ===", "=== Code nối với nó (đồ thị, 1 bước) ==="));
                foreach (var kv in related.OrderByDescending(kv => relScore[kv.Key]).Take(20))
                {
                    if (sb.Length > maxChars - 800) { sb.AppendLine("  …"); break; }
                    var s = kv.Key;
                    sb.AppendLine("• " + s.Full + "  " + Loc(g, s) + "  (" + kv.Value + ")");
                    sb.AppendLine("    " + s.Sig);
                    used.Add(s.File);
                }
            }
            var more = ranked.Select(t => t.Item1).Where(s => !shownRanges.Contains(s) && !related.ContainsKey(s) && !IsType(s.Kind)).Take(10).ToList();
            if (more.Count > 0 && sb.Length < maxChars - 600)
                sb.AppendLine().AppendLine(L.T("Also possibly relevant: ", "Có thể liên quan thêm: ") + string.Join(", ", more.Select(s => s.Full + " (" + Loc(g, s) + ")")));
        }
        sb.AppendLine();
        if (sb.Length < maxChars - 400) AppendMemories(sb, task, null);
        if (sb.Length < maxChars - 400) AppendElsewhere(sb, task, g.Root, 2);
        var text = sb.ToString().TrimEnd();
        if (text.Length > maxChars) text = text.Substring(0, maxChars) + "\n…";
        return Track("context_pack", text, used.Sum(f => f.Size));
    }

    // Gọt đuôi tiếng Anh đơn giản: scripts→script, styles→style, enqueued→enqueu, enqueue→enqueu
    public static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies")) return w.Substring(0, w.Length - 3) + "y";
        if (w.Length > 4 && (w.EndsWith("sses") || w.EndsWith("shes") || w.EndsWith("ches") || w.EndsWith("xes"))) return w.Substring(0, w.Length - 2);
        if (w.Length > 3 && w.EndsWith("s") && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) w = w.Substring(0, w.Length - 1);
        if (w.Length > 5 && w.EndsWith("ing")) return w.Substring(0, w.Length - 3);
        if (w.Length > 4 && w.EndsWith("ed")) return w.Substring(0, w.Length - 2);
        if (w.Length > 4 && w.EndsWith("e")) return w.Substring(0, w.Length - 1);
        return w;
    }

    static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "the", "and", "for", "with", "that", "this", "from", "into", "add", "fix", "make", "use", "when", "how", "what", "where", "why", "should", "would", "could",
        "can", "need", "want", "please", "code", "file", "files", "function", "bug", "error", "new", "all", "not", "but", "are", "was", "has", "have", "does",
        "va", "cua", "cho", "voi", "khi", "thi", "la", "co", "khong", "duoc", "trong", "mot", "cac", "nhung", "de", "nay", "do", "sua", "them", "lam", "loi",
        "can", "phai", "nao", "gi", "sao", "tai", "vao", "ra", "len", "xuong", "hay", "nhu", "the", "neu", "da", "dang", "se", "roi" };

    // ================= Thống kê tiết kiệm =================
    // baselineBytes = kích thước các file agent lẽ ra phải đọc toàn bộ để biết cùng điều đó
    static string Track(string tool, string text, long baselineBytes)
    {
        long ret = Encoding.UTF8.GetByteCount(text);
        long baseTokens = baselineBytes / 4, retTokens = ret / 4;
        try
        {
            Directory.CreateDirectory(Cleaner.DataDir);
            lock (cache) File.AppendAllText(SavingsFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + tool + "\t" + retTokens + "\t" + baseTokens + "\n");
        }
        catch { }
        if (baseTokens > retTokens * 2)
            text += "\n\n" + L.T("[≈", "[≈") + retTokens.ToString("N0") + L.T(" tokens instead of ≈", " token thay vì ≈") + baseTokens.ToString("N0") + L.T(" for reading the whole files]", " nếu đọc nguyên các file]");
        return text;
    }

    public static Tuple<int, long, long> Savings(int days)
    {
        int calls = 0; long ret = 0, baseT = 0;
        try
        {
            if (!File.Exists(SavingsFile)) return Tuple.Create(0, 0L, 0L);
            var since = DateTime.Now.AddDays(-days);
            foreach (var l in File.ReadLines(SavingsFile))
            {
                var p = l.Split('\t'); DateTime d; long r, b;
                if (p.Length < 4 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) || d < since) continue;
                if (!long.TryParse(p[2], out r) || !long.TryParse(p[3], out b)) continue;
                calls++; ret += r; baseT += Math.Max(b, r);
            }
        }
        catch { }
        return Tuple.Create(calls, ret, baseT);
    }

    public static string SavingsLine()
    {
        var s = Savings(30);
        if (s.Item1 == 0) return L.T("Code-graph savings (30 days): no calls yet", "Tiết kiệm nhờ đồ thị code (30 ngày): chưa có lượt gọi");
        double pct = s.Item3 > 0 ? 100.0 * (s.Item3 - s.Item2) / s.Item3 : 0;
        return L.T("Code-graph savings (30 days): ", "Tiết kiệm nhờ đồ thị code (30 ngày): ") + s.Item1 + L.T(" calls, ≈", " lượt, ≈") + s.Item2.ToString("N0") + L.T(" tokens returned instead of ≈", " token trả về thay vì ≈") + s.Item3.ToString("N0") + " (" + pct.ToString("0") + L.T("% saved)", "% tiết kiệm)");
    }
}

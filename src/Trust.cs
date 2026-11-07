// Độ tin cậy của nguồn: chỉ học theo code/tài liệu đã được kiểm chứng.
// App tự chấm mỗi dự án theo dấu hiệu kiểm tra được (có test, có CI, đã phát hành, còn bảo trì, có tài liệu);
// người dùng luôn có quyền quyết định cuối cùng (đánh dấu "Tin cậy" hoặc "Loại trừ").
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

static class Trust
{
    public enum Level { Trusted, Unverified, Excluded }
    public class Info { public Level Level; public bool ByUser; public List<string> Signals = new List<string>(); public string Project; }

    static readonly string File_ = Path.Combine(Cleaner.DataDir, "tin-cay.json");
    static readonly object gate = new object();
    static readonly Dictionary<string, Tuple<DateTime, Info>> cache = new Dictionary<string, Tuple<DateTime, Info>>(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string> overrides; static DateTime overridesStamp;

    // Thư mục nháp, thử nghiệm, tạm: không bao giờ dùng làm mẫu (trừ khi người dùng đánh dấu tin cậy)
    static readonly Regex Scratch = new Regex(@"\\(temp|tmp|scratch|sandbox|playground|nhap|nháp|thu-nghiem|experiments?|old|backup|bak|archive|copy of [^\\]*|[^\\]*[-_ ](test|tmp|old|copy|bak))(\\|$)|\\AppData\\|\\Downloads\\|\\\$Recycle\.Bin\\", RegexOptions.IgnoreCase);

    static Dictionary<string, string> Overrides()
    {
        lock (gate)
        {
            var t = File.Exists(File_) ? File.GetLastWriteTimeUtc(File_) : DateTime.MinValue;
            if (overrides == null || t != overridesStamp)
            {
                overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try { var d = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(File.ReadAllText(File_, Encoding.UTF8)); if (d != null) foreach (var kv in d) overrides[kv.Key] = kv.Value; } catch { }
                overridesStamp = t;
            }
            return overrides;
        }
    }

    /// <summary>Người dùng đặt mức tin cậy cho một dự án/thư mục: "trusted", "excluded" hoặc "auto" (bỏ đánh dấu).</summary>
    public static void Set(string path, string level)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        lock (gate)
        {
            var o = new Dictionary<string, string>(Overrides(), StringComparer.OrdinalIgnoreCase);
            if (level == "trusted" || level == "excluded") o[path] = level; else o.Remove(path);
            Directory.CreateDirectory(Cleaner.DataDir);
            File.WriteAllText(File_, new JavaScriptSerializer().Serialize(o), new UTF8Encoding(false));
            overrides = null; cache.Clear();
        }
    }

    public static string Label(Level l) { return l == Level.Trusted ? L.T("trusted", "tin cậy") : l == Level.Excluded ? L.T("excluded", "loại trừ") : L.T("unverified", "chưa kiểm chứng"); }

    /// <summary>Độ tin cậy của một file hoặc thư mục: theo dự án chứa nó, hoặc theo đường dẫn nếu không thuộc dự án nào.</summary>
    public static Info Of(string path)
    {
        if (string.IsNullOrEmpty(path)) return new Info { Level = Level.Unverified };
        string full; try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return new Info { Level = Level.Unverified }; }
        // Đánh dấu của người dùng: thư mục gần nhất chứa đường dẫn này thắng
        var o = Overrides().Where(kv => (full + "\\").StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
        string proj = null;
        try { proj = Brain.ProjectOf(full); } catch { }
        if (proj == null && Directory.Exists(Path.Combine(full, ".git"))) proj = full;
        Info info = proj != null ? OfProject(proj) : new Info { Level = Scratch.IsMatch(full + "\\") ? Level.Excluded : Level.Unverified };
        // Thư mục nháp/lưu trữ nằm BÊN TRONG một dự án (vd. docs\archive, old\, tmp\) cũng bị loại trừ, dù dự án tin cậy
        if (proj != null && info.Level != Level.Excluded && full.Length > proj.Length && Scratch.IsMatch(full.Substring(proj.Length) + "\\"))
            info = new Info { Level = Level.Excluded, Project = proj, Signals = new List<string>(info.Signals) { L.T("inside a scratch/archive folder", "nằm trong thư mục nháp/lưu trữ") } };
        if (o.Key != null)
        {
            info = new Info { Level = o.Value == "trusted" ? Level.Trusted : Level.Excluded, ByUser = true, Signals = info.Signals, Project = info.Project };
            info.Signals = new List<string>(info.Signals) { o.Value == "trusted" ? L.T("marked trusted by you", "bạn đánh dấu tin cậy") : L.T("excluded by you", "bạn đã loại trừ") };
        }
        return info;
    }

    static Info OfProject(string proj)
    {
        lock (gate)
        {
            Tuple<DateTime, Info> c;
            if (cache.TryGetValue(proj, out c) && DateTime.UtcNow - c.Item1 < TimeSpan.FromMinutes(10)) return c.Item2;
        }
        var info = new Info { Project = proj };
        bool tests = false, ci = false, released = false, maintained = false, docs = false;
        try
        {
            var di = new DirectoryInfo(proj);
            var top = di.GetFileSystemInfos();
            var names = new HashSet<string>(top.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            // Có test: thư mục test ở cấp đầu hoặc trong src, file *.test.* / *_test.* / test_*.py, hoặc script test thật trong package.json
            Func<DirectoryInfo, bool> hasTestDir = d => { try { return d.GetDirectories().Any(x => Regex.IsMatch(x.Name, "^(tests?|__tests__|specs?|e2e|testing)$", RegexOptions.IgnoreCase)); } catch { return false; } };
            tests = hasTestDir(di) || (Directory.Exists(Path.Combine(proj, "src")) && hasTestDir(new DirectoryInfo(Path.Combine(proj, "src"))))
                 || top.OfType<FileInfo>().Any(f => Regex.IsMatch(f.Name, @"(\.(test|spec)\.|_test\.|^test_.*\.py$)", RegexOptions.IgnoreCase));
            if (!tests && names.Contains("package.json"))
                try { var pj = File.ReadAllText(Path.Combine(proj, "package.json")); tests = Regex.IsMatch(pj, "\"test\"\\s*:\\s*\"(?!echo \\\\\"Error: no test)[^\"]+\""); } catch { }
            // Có CI: chạy test/build tự động mỗi lần đẩy code
            var wf = Path.Combine(proj, ".github", "workflows");
            ci = (Directory.Exists(wf) && Directory.GetFiles(wf).Any(f => f.EndsWith(".yml") || f.EndsWith(".yaml")))
                 || names.Contains(".gitlab-ci.yml") || names.Contains("azure-pipelines.yml") || names.Contains(".circleci") || names.Contains("Jenkinsfile");
            // Đã phát hành: có tag phiên bản trong git
            var git = Path.Combine(proj, ".git");
            if (Directory.Exists(git))
            {
                var tags = Path.Combine(git, "refs", "tags");
                released = (Directory.Exists(tags) && Directory.EnumerateFileSystemEntries(tags).Any())
                        || (File.Exists(Path.Combine(git, "packed-refs")) && File.ReadAllText(Path.Combine(git, "packed-refs")).Contains("refs/tags/"));
                var head = Path.Combine(git, "logs", "HEAD");
                var last = File.Exists(head) ? File.GetLastWriteTime(head) : Directory.GetLastWriteTime(git);
                maintained = (DateTime.Now - last).TotalDays <= 180;
            }
            docs = names.Contains("README.md") || names.Contains("README") || names.Contains("README.txt") || names.Contains("docs");
        }
        catch { }
        if (tests) info.Signals.Add(L.T("has tests", "có test"));
        if (ci) info.Signals.Add(L.T("has CI", "có CI"));
        if (released) info.Signals.Add(L.T("has releases", "đã phát hành"));
        if (maintained) info.Signals.Add(L.T("maintained", "còn bảo trì"));
        if (docs) info.Signals.Add(L.T("documented", "có tài liệu"));
        if (!tests && !ci) info.Signals.Add(L.T("no tests or CI", "chưa có test hay CI"));
        // Tin cậy khi code được kiểm chứng tự động (test hoặc CI) VÀ đã được dùng thật (phát hành) hoặc còn bảo trì
        if (Scratch.IsMatch(proj + "\\")) { info.Level = Level.Excluded; info.Signals.Add(L.T("scratch/temp folder", "thư mục nháp/tạm")); }
        else info.Level = (tests || ci) && (released || maintained) ? Level.Trusted : Level.Unverified;
        lock (gate) cache[proj] = Tuple.Create(DateTime.UtcNow, info);
        return info;
    }

    /// <summary>Nhãn ngắn để gắn vào kết quả trả cho agent.</summary>
    public static string Tag(Info i)
    {
        string mark = i.Level == Level.Trusted ? "✓ " : i.Level == Level.Excluded ? "✗ " : "⚠ ";
        return mark + Label(i.Level) + (i.Signals.Count > 0 ? " (" + string.Join(", ", i.Signals) + ")" : "");
    }
}

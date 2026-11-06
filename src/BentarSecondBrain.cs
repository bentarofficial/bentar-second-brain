// Bentar Second Brain - dọn các mục an toàn, có chế độ tự động theo lịch
// Chạy "app.exe /auto" để dọn ngầm theo cài đặt đã lưu (dùng cho Task Scheduler)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Bentar Second Brain")]
[assembly: System.Reflection.AssemblyProduct("Bentar Second Brain")]
[assembly: System.Reflection.AssemblyVersion("2.4.0.0")]

// Cập nhật từ GitHub Releases: so sánh tag (vd "v2.1.0") với phiên bản app, tải file .exe mới rồi tự thay thế
static class Updater
{
    const string Repo = "bentarofficial/bentar-second-brain";
    public static readonly Version Current = typeof(Updater).Assembly.GetName().Version;

    public class Release { public Version Version; public string Tag, ExeUrl, Notes; }

    static System.Net.WebClient Client()
    {
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        var wc = new System.Net.WebClient { Encoding = Encoding.UTF8 };
        wc.Headers.Add("User-Agent", "BentarSecondBrain");
        return wc;
    }

    public static Release Latest()
    {
        string json;
        using (var wc = Client()) json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
        var tag = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
        var url = System.Text.RegularExpressions.Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"");
        var body = System.Text.RegularExpressions.Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!tag.Success || !url.Success) return null;
        var v = new Version(tag.Groups[1].Value);
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        string notes = body.Success ? System.Text.RegularExpressions.Regex.Unescape(body.Groups[1].Value) : "";
        return new Release { Version = v, Tag = tag.Groups[1].Value, ExeUrl = url.Groups[1].Value, Notes = notes };
    }

    // Tải bản mới, rồi nhờ một PowerShell chạy ngầm chờ app thoát, chép đè file .exe và mở lại app
    public static void DownloadAndRestart(Release r)
    {
        string target = Application.ExecutablePath;
        string newExe = Path.Combine(Path.GetTempPath(), "BentarSecondBrain-" + r.Tag + ".exe");
        using (var wc = Client()) wc.DownloadFile(r.ExeUrl, newExe);
        if (new FileInfo(newExe).Length < 10 * 1024) throw new Exception("File tải về không hợp lệ.");

        string installed = Path.Combine(Cleaner.DataDir, "BentarSecondBrain.exe");   // bản dùng cho lịch tự động
        Func<string, string> q = s => "'" + s.Replace("'", "''") + "'";
        string script =
            "$p=" + Process.GetCurrentProcess().Id + "; while(Get-Process -Id $p -ErrorAction SilentlyContinue){Start-Sleep -Milliseconds 300};" +
            "Copy-Item -LiteralPath " + q(newExe) + " -Destination " + q(target) + " -Force;" +
            "if((Test-Path -LiteralPath " + q(installed) + ") -and (" + q(installed) + " -ne " + q(target) + ")){Copy-Item -LiteralPath " + q(newExe) + " -Destination " + q(installed) + " -Force};" +
            "Remove-Item -LiteralPath " + q(newExe) + ";" +
            "Start-Process -FilePath " + q(target);
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand " + encoded)
            { CreateNoWindow = true, UseShellExecute = false });
    }
}

class CleanItem
{
    public string Id, Name;
    public Func<IEnumerable<string>> Paths;   // null = Thùng rác
    public bool DefaultOn;
    public long Size;
    public CleanItem(string id, string name, bool defaultOn, Func<IEnumerable<string>> paths)
    { Id = id; Name = name; DefaultOn = defaultOn; Paths = paths; }
}

static class Cleaner
{
    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

    public const string AppName = "Bentar Second Brain";
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string DataDir = Path.Combine(Local, "BentarSecondBrain");
    public static readonly string SettingsFile = Path.Combine(DataDir, "settings.ini");
    public static readonly string LogFile = Path.Combine(DataDir, "nhat-ky.txt");

    // Cache của các trình duyệt nhân Chromium: chỉ xoá cache, KHÔNG đụng mật khẩu, bookmark, lịch sử
    static IEnumerable<string> BrowserCaches(string userData)
    {
        string root = Path.Combine(Local, userData);
        if (!Directory.Exists(root)) yield break;
        string[] profiles;
        try { profiles = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var p in profiles)
            foreach (var c in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" })
                yield return Path.Combine(p, c);
    }

    public static List<CleanItem> AllItems()
    {
        return new List<CleanItem>
        {
            new CleanItem("recycle", "Thùng rác (Recycle Bin)", true, null),
            new CleanItem("temp", "File tạm của người dùng (Temp)", true, () => new[] { Path.GetTempPath() }),
            new CleanItem("wintemp", "File tạm của Windows", true, () => new[] { @"C:\Windows\Temp" }),
            new CleanItem("chrome", "Bộ nhớ đệm Google Chrome", true, () => BrowserCaches(@"Google\Chrome\User Data")),
            new CleanItem("edge", "Bộ nhớ đệm Microsoft Edge", true, () => BrowserCaches(@"Microsoft\Edge\User Data")),
            new CleanItem("coccoc", "Bộ nhớ đệm Cốc Cốc", true, () => BrowserCaches(@"CocCoc\Browser\User Data")),
            new CleanItem("npm", "Bộ nhớ đệm npm (npm-cache)", true, () => new[] { Path.Combine(Local, "npm-cache") }),
            new CleanItem("crash", "Báo cáo lỗi & crash dump", true, () => new[] {
                Path.Combine(Local, "CrashDumps"),
                @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive",
                @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue" }),
            new CleanItem("dx", "Bộ nhớ đệm đồ hoạ DirectX", true, () => new[] { Path.Combine(Local, "D3DSCache") }),
            new CleanItem("wu", "File cập nhật Windows đã tải (cần quyền Admin)", false, () => new[] { @"C:\Windows\SoftwareDistribution\Download" }),
        };
    }

    public static string Fmt(long b)
    {
        if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.00") + " GB";
        if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0.0") + " MB";
        return (b / 1024.0).ToString("0") + " KB";
    }

    public static long FreeC() { try { return new DriveInfo("C").AvailableFreeSpace; } catch { return 0; } }

    static long DirSize(string path)
    {
        long total = 0;
        if (!Directory.Exists(path)) return 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            try { foreach (var f in Directory.GetFiles(dir)) { try { total += new FileInfo(f).Length; } catch { } } } catch { }
            try
            {
                foreach (var d in Directory.GetDirectories(dir))
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) stack.Push(d);
            }
            catch { }
        }
        return total;
    }

    public static long Measure(CleanItem it)
    {
        if (it.Paths == null)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
            return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        return it.Paths().Sum(p => DirSize(p));
    }

    // Trả về số file không xoá được (đang bị phần mềm khác dùng)
    public static int Clean(CleanItem it)
    {
        if (it.Paths == null) { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); return 0; }
        return it.Paths().Sum(p => EmptyDir(p));
    }

    // Xoá nội dung bên trong thư mục, giữ lại chính thư mục
    static int EmptyDir(string path)
    {
        int skipped = 0;
        if (!Directory.Exists(path)) return 0;
        string[] files = new string[0], dirs = new string[0];
        try { files = Directory.GetFiles(path); } catch { }
        try { dirs = Directory.GetDirectories(path); } catch { }
        foreach (var f in files)
        {
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { skipped++; }
        }
        foreach (var d in dirs)
        {
            try
            {
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(d); continue; }
                skipped += EmptyDir(d);
                Directory.Delete(d);
            }
            catch { skipped++; }
        }
        return skipped;
    }

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(LogFile, DateTime.Now.ToString("dd/MM/yyyy HH:mm") + "  " + msg + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    public static string LastLog()
    {
        try { var lines = File.ReadAllLines(LogFile, Encoding.UTF8); return lines.Length > 0 ? lines[lines.Length - 1] : null; }
        catch { return null; }
    }
}

class Settings
{
    public HashSet<string> Selected = new HashSet<string>();
    public bool AutoEnabled;
    public int Frequency = 3;          // 0 hằng ngày, 1 mỗi 3 ngày, 2 hằng tuần, 3 mỗi khi bật máy (mặc định)
    public string Time = "12:00";
    public bool AutoRecycle = false;   // Thùng rác mặc định KHÔNG dọn tự động, tránh mất file vừa xoá nhầm
    public bool Notify = true;
    public bool Loaded;

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            foreach (var line in File.ReadAllLines(Cleaner.SettingsFile, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line.Substring(0, i), v = line.Substring(i + 1);
                if (k == "selected") s.Selected = new HashSet<string>(v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                else if (k == "auto") s.AutoEnabled = v == "1";
                else if (k == "freq") int.TryParse(v, out s.Frequency);
                else if (k == "time") s.Time = v;
                else if (k == "autorecycle") s.AutoRecycle = v == "1";
                else if (k == "notify") s.Notify = v == "1";
            }
            s.Loaded = true;
        }
        catch { }
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Cleaner.DataDir);
        File.WriteAllLines(Cleaner.SettingsFile, new[] {
            "selected=" + string.Join(",", Selected),
            "auto=" + (AutoEnabled ? "1" : "0"),
            "freq=" + Frequency,
            "time=" + Time,
            "autorecycle=" + (AutoRecycle ? "1" : "0"),
            "notify=" + (Notify ? "1" : "0") }, Encoding.UTF8);
    }
}

static class Scheduler
{
    const string TaskName = "Bentar Second Brain - Tu dong don dep";
    static readonly string InstalledExe = Path.Combine(Cleaner.DataDir, "BentarSecondBrain.exe");

    static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false };
        using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
    }

    // Chép app vào AppData để lịch vẫn chạy được dù bạn di chuyển/xoá file .exe gốc
    public static void Enable(Settings s)
    {
        Directory.CreateDirectory(Cleaner.DataDir);
        if (!string.Equals(Application.ExecutablePath, InstalledExe, StringComparison.OrdinalIgnoreCase))
            File.Copy(Application.ExecutablePath, InstalledExe, true);

        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        string trigger;
        if (s.Frequency == 3)
            trigger = "<LogonTrigger><UserId>" + user + "</UserId><Delay>PT30S</Delay></LogonTrigger>";
        else
        {
            int days = s.Frequency == 1 ? 3 : s.Frequency == 2 ? 7 : 1;
            trigger = "<CalendarTrigger><StartBoundary>" + DateTime.Today.ToString("yyyy-MM-dd") + "T" + s.Time + ":00</StartBoundary>" +
                      "<ScheduleByDay><DaysInterval>" + days + "</DaysInterval></ScheduleByDay></CalendarTrigger>";
        }

        string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
"  <RegistrationInfo><Description>Tu dong don dep file rac - Bentar Second Brain</Description></RegistrationInfo>\n" +
"  <Triggers>" + trigger + "</Triggers>\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n" +
"  <Settings>\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\n" +          // máy tắt đúng giờ hẹn thì dọn ngay khi bật lại
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
"    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>\n" +
"  </Settings>\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(InstalledExe) + "</Command><Arguments>/auto</Arguments></Exec></Actions>\n" +
"</Task>";

        string xmlFile = Path.Combine(Path.GetTempPath(), "bentarsecondbrain-task.xml");
        File.WriteAllText(xmlFile, xml, Encoding.Unicode);
        int code = Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + xmlFile + "\" /F");
        try { File.Delete(xmlFile); } catch { }
        if (code != 0) throw new Exception("Không tạo được lịch (mã lỗi " + code + ").");
    }

    public static void Disable() { Schtasks("/Delete /TN \"" + TaskName + "\" /F"); }

    // Chuyển từ tên cũ "PC master cleand by Bentar" (bản 2.1 trở về trước): chép cài đặt, nhật ký và đổi lịch sang tên mới
    public static void MigrateFromOldName()
    {
        const string oldTask = "PC master cleand by Bentar - Tu dong don dep";
        string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCMasterClean");
        try
        {
            if (Directory.Exists(oldDir) && !File.Exists(Cleaner.SettingsFile))
            {
                Directory.CreateDirectory(Cleaner.DataDir);
                foreach (var name in new[] { "settings.ini", "nhat-ky.txt" })
                    if (File.Exists(Path.Combine(oldDir, name))) File.Copy(Path.Combine(oldDir, name), Path.Combine(Cleaner.DataDir, name), true);
            }
            if (Schtasks("/Query /TN \"" + oldTask + "\"") == 0)
            {
                var s = Settings.Load();
                if (s.AutoEnabled) Enable(s);
                Schtasks("/Delete /TN \"" + oldTask + "\" /F");
                Cleaner.Log("Đã chuyển lịch tự động sang tên mới Bentar Second Brain");
            }
        }
        catch { }
    }

    public static bool Exists() { return Schtasks("/Query /TN \"" + TaskName + "\"") == 0; }
}

class Finding
{
    public string Path, Reason;
    public long Size;
    public DateTime Date;
    public bool IsFolder;
}

class FolderRating
{
    public string Path, Rating;
    public long Size;
    public int Files, Days;
    public DateTime LastUsed;
    public bool Ai, OpenedRecently;
}

class KeepItem
{
    public string Name, Why;
    public string[] Paths;
    public long Size;
    public KeepItem(string name, string why, params string[] paths) { Name = name; Why = why; Paths = paths; }
}

// Phân loại file cá nhân: KHÔNG tự xoá gì, chỉ gợi ý để người dùng tự quyết
static class Analyzer
{
    const long MB = 1L << 20;
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string Downloads = Path.Combine(Home, "Downloads");
    static readonly string[] BackupExt = { ".wpress", ".tar", ".gz", ".zip", ".rar", ".7z", ".bak", ".sql" };
    static readonly System.Text.RegularExpressions.Regex DatedName =
        new System.Text.RegularExpressions.Regex(@"^(.+?)[-_ ]?\d{6,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    static IEnumerable<FileInfo> Walk(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            FileInfo[] files = new FileInfo[0];
            try { files = new DirectoryInfo(dir).GetFiles(); } catch { }
            foreach (var f in files) yield return f;
            try
            {
                foreach (var d in Directory.GetDirectories(dir))
                {
                    string n = Path.GetFileName(d);
                    if (n.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) stack.Push(d);
                }
            }
            catch { }
        }
    }

    public static IEnumerable<DriveInfo> FixedDrives()
    {
        return DriveInfo.GetDrives().Where(d => { try { return d.DriveType == DriveType.Fixed && d.IsReady; } catch { return false; } });
    }

    static bool OnC(string p) { return p.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase); }

    // Băm 1 MB đầu + 1 MB cuối: đủ nhanh để nhận ra file trùng lặp mà không phải đọc cả file
    static string QuickHash(string path)
    {
        try
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[1 << 20];
                int n = fs.Read(buf, 0, buf.Length);
                md5.TransformBlock(buf, 0, n, null, 0);
                if (fs.Length > buf.Length) { fs.Seek(-buf.Length, SeekOrigin.End); n = fs.Read(buf, 0, buf.Length); }
                else n = 0;
                md5.TransformFinalBlock(buf, 0, n);
                return BitConverter.ToString(md5.Hash);
            }
        }
        catch { return null; }
    }

    public static List<Finding> FindReview()
    {
        // Ổ C: chỉ quét thư mục cá nhân; các ổ khác (D, E...): quét toàn bộ ổ
        var folders = new[] { "Downloads", "Desktop", "Documents", "Videos", "Pictures", "Music" }.Select(f => Path.Combine(Home, f))
            .Concat(FixedDrives().Where(d => !OnC(d.Name)).Select(d => d.Name));
        var files = folders.SelectMany(Walk)
            .Where(f => !InDevFolder(f.FullName))
            .Where(f => f.Length >= 50 * MB || (f.Length >= 5 * MB && IsInstaller(f)))
            .ToList();
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. File trùng lặp: giữ bản mới nhất, gợi ý xoá các bản còn lại
        foreach (var g in files.GroupBy(f => f.Length).Where(g => g.Count() > 1))
            foreach (var h in g.GroupBy(f => QuickHash(f.FullName)).Where(h => h.Key != null && h.Count() > 1))
            {
                var keep = h.OrderByDescending(f => f.LastWriteTime).First();
                foreach (var f in h.Where(f => f != keep))
                    reasons[f.FullName] = "Trùng lặp với " + keep.Name + " (" + keep.Directory.Name + ")";
            }

        // 2. Backup cũ: cùng tên website/dự án, đã có bản mới hơn
        foreach (var g in files.Where(f => BackupExt.Contains(f.Extension.ToLower()))
                               .GroupBy(f => { var m = DatedName.Match(f.Name); return m.Success ? m.Groups[1].Value.ToLower() + f.Extension.ToLower() : null; })
                               .Where(g => g.Key != null && g.Count() > 1))
        {
            var newest = g.OrderByDescending(f => f.LastWriteTime).First();
            foreach (var f in g.Where(f => f != newest && !reasons.ContainsKey(f.FullName)))
                reasons[f.FullName] = "Bản backup cũ, đã có bản mới hơn (" + newest.LastWriteTime.ToString("dd/MM/yyyy") + ")";
        }

        foreach (var f in files)
        {
            if (reasons.ContainsKey(f.FullName)) continue;
            bool c = OnC(f.FullName);
            if (IsInstaller(f) && (c ? f.FullName.StartsWith(Downloads, StringComparison.OrdinalIgnoreCase) : LooksLikeSetup(f)))
                reasons[f.FullName] = "File cài đặt phần mềm – đã cài xong thì có thể xoá";
            else if (c && f.LastWriteTime < DateTime.Now.AddDays(-180))
                reasons[f.FullName] = "Hơn 6 tháng không thay đổi – xoá nếu không cần, hoặc chuyển sang ổ D";
            else if (c && f.Length >= 200 * MB)
                reasons[f.FullName] = "File lớn – nên chuyển sang ổ D cho ổ C nhẹ";
            else if (!c && f.Length >= 500 * MB && f.LastWriteTime < DateTime.Now.AddDays(-180))
                reasons[f.FullName] = "File lớn, hơn 6 tháng không thay đổi – xoá nếu không còn cần";
        }

        var result = files.Where(f => reasons.ContainsKey(f.FullName))
            .Select(f => new Finding { Path = f.FullName, Size = f.Length, Date = f.LastWriteTime, Reason = reasons[f.FullName] })
            .ToList();

        if (Directory.Exists(@"C:\Windows.old"))
            result.Add(new Finding { Path = @"C:\Windows.old", IsFolder = true, Size = Walk(@"C:\Windows.old").Sum(f => f.Length),
                Date = Directory.GetLastWriteTime(@"C:\Windows.old"),
                Reason = "Bản Windows cũ sau khi nâng cấp – xoá bằng Disk Cleanup (cleanmgr), không xoá tay" });

        return result.OrderByDescending(r => r.Size).ToList();
    }

    static bool IsInstaller(FileInfo f) { var e = f.Extension.ToLower(); return e == ".exe" || e == ".msi" || e == ".iso"; }

    // Ngoài thư mục Downloads, chỉ coi là file cài đặt khi tên có chữ setup/install (tránh gợi ý xoá app đang dùng)
    static bool LooksLikeSetup(FileInfo f)
    {
        var e = f.Extension.ToLower(); var n = f.Name.ToLower();
        return e == ".msi" || e == ".iso" || n.Contains("setup") || n.Contains("install");
    }

    // Thư mục kỹ thuật của dự án lập trình: xoá file lẻ bên trong sẽ làm hỏng dự án, nên không gợi ý
    static readonly string[] DevDirs = { "node_modules", ".venv", "venv", ".pnpm-store", ".git", "site-packages", "win-unpacked", "__pycache__", ".next", ".cache", "AppData" };
    static bool InDevFolder(string path)
    {
        var parts = path.Split('\\');
        return parts.Any(p => DevDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    // Dấu vết công cụ AI / lập trình đang làm việc trong thư mục
    static readonly string[] AiMarkers = { ".claude", "CLAUDE.md", "AGENTS.md", ".cursor", ".cursorrules", ".codex", ".windsurf", ".aider.conf.yml", ".github", ".git" };

    // Windows đã tắt ghi "lần mở cuối" của file, nên dùng danh sách Recent (file bạn đã mở) để bù lại
    static List<KeyValuePair<string, DateTime>> RecentOpens()
    {
        var result = new List<KeyValuePair<string, DateTime>>();
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            foreach (var lnk in new DirectoryInfo(dir).GetFiles("*.lnk"))
            {
                try
                {
                    object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk.FullName });
                    string target = (string)sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
                    if (!string.IsNullOrEmpty(target)) result.Add(new KeyValuePair<string, DateTime>(target, lnk.LastWriteTime));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    // Đánh giá ở cấp thư mục: thư mục con cấp 1 trong Documents/Downloads/Desktop/Videos... và thư mục gốc của ổ D, E...
    public static List<FolderRating> RateFolders()
    {
        var roots = new[] { "Documents", "Downloads", "Desktop", "Videos", "Pictures", "Music" }.Select(f => Path.Combine(Home, f))
            .Concat(FixedDrives().Where(d => !OnC(d.Name)).Select(d => d.Name));
        var recent = RecentOpens();
        var result = new List<FolderRating>();
        foreach (var root in roots)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var sub in subs)
            {
                string n = Path.GetFileName(sub);
                if (n.StartsWith("$") || n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                try { if ((File.GetAttributes(sub) & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue; } catch { continue; }

                var r = new FolderRating { Path = sub, LastUsed = DateTime.MinValue };
                var stack = new Stack<string>();
                stack.Push(sub);
                while (stack.Count > 0)
                {
                    string dir = stack.Pop();
                    try
                    {
                        foreach (var f in new DirectoryInfo(dir).GetFiles())
                        {
                            r.Size += f.Length; r.Files++;
                            if (f.LastWriteTime > r.LastUsed && f.LastWriteTime <= DateTime.Now) r.LastUsed = f.LastWriteTime;
                            if (AiMarkers.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) r.Ai = true;
                        }
                        foreach (var d in Directory.GetDirectories(dir))
                        {
                            if (AiMarkers.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) r.Ai = true;
                            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) stack.Push(d);
                        }
                    }
                    catch { }
                }
                foreach (var o in recent)
                    if (o.Key.StartsWith(sub + "\\", StringComparison.OrdinalIgnoreCase) && o.Value > r.LastUsed) { r.LastUsed = o.Value; r.OpenedRecently = true; }
                if (r.LastUsed == DateTime.MinValue) r.LastUsed = Directory.GetLastWriteTime(sub);

                int days = (int)(DateTime.Now - r.LastUsed).TotalDays;
                if (r.Ai && days < 365) r.Rating = "Có dấu vết AI/lập trình làm việc – nên giữ";
                else if (days >= 365) r.Rating = "Hơn 1 năm không đụng tới – có thể là thư mục chết" + (r.Ai ? " (từng có AI làm việc)" : "");
                else if (days >= 180) r.Rating = "Ít dùng (hơn 6 tháng)";
                else if (days >= 60) r.Rating = "Thỉnh thoảng dùng";
                else r.Rating = r.OpenedRecently ? "Đang dùng (vừa mở gần đây)" : "Đang dùng";
                if (r.Files == 0) r.Rating = "Thư mục rỗng – xoá được";
                r.Days = days;
                result.Add(r);
            }
        }
        return result.OrderByDescending(r => r.Files == 0 ? int.MaxValue : r.Days).ToList();
    }

    public static List<KeepItem> KeepItems()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new List<KeepItem>
        {
            new KeepItem("Windows (hệ điều hành)", "KHÔNG xoá – xoá nhầm máy sẽ không khởi động được", @"C:\Windows"),
            new KeepItem("Phần mềm đã cài", "Muốn bỏ thì gỡ trong Settings > Apps, không xoá thư mục", @"C:\Program Files", @"C:\Program Files (x86)"),
            new KeepItem("Dữ liệu chung của phần mềm (ProgramData)", "Phần mềm cần để chạy – không xoá", @"C:\ProgramData"),
            new KeepItem("Cài đặt & tài khoản phần mềm (AppData\\Roaming)", "Đăng nhập Zalo, Zoom, Office... nằm ở đây – không xoá", roaming),
            new KeepItem("Hồ sơ trình duyệt (Chrome, Cốc Cốc, Edge)", "Mật khẩu, bookmark, lịch sử – chỉ xoá cache (đã có ở tab An toàn)",
                Path.Combine(local, @"Google\Chrome\User Data"), Path.Combine(local, @"CocCoc\Browser\User Data"), Path.Combine(local, @"Microsoft\Edge\User Data")),
            new KeepItem("Tài liệu, Desktop, Ảnh của bạn", "Dữ liệu cá nhân – nên sao lưu; muốn ổ C trống thì chuyển sang ổ D",
                Path.Combine(Home, "Documents"), Path.Combine(Home, "Desktop"), Path.Combine(Home, "Pictures")),
            new KeepItem("pagefile.sys / hiberfil.sys", "File hệ thống Windows tự quản lý – không xoá", @"C:\pagefile.sys", @"C:\hiberfil.sys"),
        };
    }

    public static long MeasurePath(string p)
    {
        try
        {
            if (File.Exists(p)) return new FileInfo(p).Length;
            return Walk(p).Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }
}

class MainForm : Form
{
    readonly ListView review = new ListView(), keep = new ListView();
    readonly Button btnOpen = new Button(), btnRecycle = new Button(), btnMove = new Button();
    List<Finding> findings = new List<Finding>();
    readonly ListView stale = new ListView();
    readonly Button btnStaleOpen = new Button(), btnStaleRecycle = new Button();
    readonly Button btnUpdate = new Button();
    readonly List<CleanItem> items = Cleaner.AllItems();
    readonly Settings settings = Settings.Load();
    readonly ListView list = new ListView();
    readonly Button btnScan = new Button(), btnClean = new Button(), btnSave = new Button(), btnLog = new Button();
    readonly Label lblStatus = new Label(), lblDisk = new Label(), lblLast = new Label();
    readonly CheckBox chkAuto = new CheckBox(), chkRecycle = new CheckBox(), chkNotify = new CheckBox();
    readonly ComboBox cboFreq = new ComboBox();
    readonly DateTimePicker dtTime = new DateTimePicker();

    public MainForm()
    {
        Text = Cleaner.AppName;
        ClientSize = new Size(760, 702);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 10);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var title = new Label { Text = Cleaner.AppName, Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 102, 204), AutoSize = true, Location = new Point(16, 12) };
        lblDisk.SetBounds(18, 50, 600, 22);

        list.View = View.Details;
        list.CheckBoxes = true;
        list.FullRowSelect = true;
        list.SetBounds(8, 8, 700, 270);
        list.Columns.Add("Mục dọn dẹp (an toàn – app tự xoá được)", 500);
        list.Columns.Add("Dung lượng", 160);
        foreach (var it in items)
        {
            bool on = settings.Loaded ? settings.Selected.Contains(it.Id) : it.DefaultOn;
            list.Items.Add(new ListViewItem(new[] { it.Name, "—" }) { Checked = on, Tag = it });
        }

        btnScan.Text = "Quét toàn bộ";
        btnScan.SetBounds(16, 78, 170, 40);
        btnScan.Click += async (s, e) => await Scan();

        btnClean.Text = "Dọn mục an toàn";
        btnClean.SetBounds(8, 286, 170, 36);
        btnClean.BackColor = Color.FromArgb(0, 153, 76);
        btnClean.ForeColor = Color.White;
        btnClean.FlatStyle = FlatStyle.Flat;
        btnClean.Click += async (s, e) => await CleanNow();

        // --- Tab 2: cần người dùng xem xét ---
        review.View = View.Details;
        review.CheckBoxes = true;
        review.FullRowSelect = true;
        review.SetBounds(8, 8, 700, 270);
        review.Columns.Add("File / thư mục", 230);
        review.Columns.Add("Dung lượng", 85);
        review.Columns.Add("Ngày sửa", 85);
        review.Columns.Add("Vì sao nên xem xét", 290);
        review.DoubleClick += (s, e) => OpenLocation();
        btnOpen.Text = "Mở vị trí";
        btnOpen.SetBounds(8, 286, 120, 36);
        btnOpen.Click += (s, e) => OpenLocation();
        btnRecycle.Text = "Chuyển vào Thùng rác";
        btnRecycle.SetBounds(136, 286, 180, 36);
        btnRecycle.Click += async (s, e) => await ReviewAction(false);
        btnMove.Text = "Chuyển sang ổ D";
        btnMove.SetBounds(324, 286, 160, 36);
        btnMove.Click += async (s, e) => await ReviewAction(true);

        // --- Tab 3: cần giữ ---
        keep.View = View.Details;
        keep.FullRowSelect = true;
        keep.SetBounds(8, 8, 700, 314);
        keep.Columns.Add("Mục", 270);
        keep.Columns.Add("Dung lượng", 90);
        keep.Columns.Add("Vì sao cần giữ", 330);
        foreach (var k in Analyzer.KeepItems()) keep.Items.Add(new ListViewItem(new[] { k.Name, "—", k.Why }) { Tag = k });

        // --- Tab 4: đánh giá thư mục lâu không dùng (chỉ gợi ý, người dùng tự tích & xoá) ---
        stale.View = View.Details;
        stale.CheckBoxes = true;
        stale.FullRowSelect = true;
        stale.ShowItemToolTips = true;
        stale.SetBounds(8, 8, 700, 270);
        stale.Columns.Add("Thư mục", 230);
        stale.Columns.Add("Dung lượng", 80);
        stale.Columns.Add("Dùng lần cuối", 95);
        stale.Columns.Add("Đánh giá", 285);
        stale.DoubleClick += (s, e) => OpenStale();
        btnStaleOpen.Text = "Mở thư mục";
        btnStaleOpen.SetBounds(8, 286, 120, 36);
        btnStaleOpen.Click += (s, e) => OpenStale();
        btnStaleRecycle.Text = "Chuyển thư mục đã tích vào Thùng rác";
        btnStaleRecycle.SetBounds(136, 286, 280, 36);
        btnStaleRecycle.Click += async (s, e) => await RecycleStale();
        var staleNote = new Label { Text = "Đánh giá theo lần sửa file gần nhất + lịch sử mở file của Windows + dấu vết AI (.claude, .git...).\nApp KHÔNG tự xoá – bạn tự xem và tích.",
            Location = new Point(424, 284), Size = new Size(290, 44), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8) };

        var tabs = new TabControl();
        tabs.SetBounds(16, 126, 728, 360);
        var t1 = new TabPage("1. Xoá an toàn (tự động)");
        var t2 = new TabPage("2. Cần bạn xem xét");
        var t3 = new TabPage("3. Cần giữ – không nên xoá");
        t1.Controls.AddRange(new Control[] { list, btnClean });
        t2.Controls.AddRange(new Control[] { review, btnOpen, btnRecycle, btnMove });
        t3.Controls.Add(keep);
        var t4 = new TabPage("4. Thư mục lâu không dùng");
        t4.Controls.AddRange(new Control[] { stale, btnStaleOpen, btnStaleRecycle, staleNote });
        tabs.TabPages.AddRange(new[] { t1, t2, t4, t3, BuildBrainTab() });

        lblStatus.SetBounds(196, 76, 548, 46);
        lblStatus.Text = "Bấm \"Quét toàn bộ\" để app phân loại file trên ổ C thành 3 nhóm.\nApp chỉ TỰ XOÁ nhóm 1; nhóm 2 bạn tự quyết; nhóm 3 không bao giờ bị đụng tới.";

        // --- Tự động dọn dẹp ---
        var grp = new GroupBox { Text = "Tự động dọn dẹp (chỉ nhóm 1)", Location = new Point(16, 494), Size = new Size(728, 196) };
        chkAuto.Text = "Bật tự động dọn dẹp (dọn ngầm các mục đang tích ở danh sách trên)";
        chkAuto.SetBounds(14, 26, 580, 24);
        chkAuto.Checked = settings.AutoEnabled && Scheduler.Exists();

        var lblFreq = new Label { Text = "Tần suất:", Location = new Point(14, 62), AutoSize = true };
        cboFreq.DropDownStyle = ComboBoxStyle.DropDownList;
        cboFreq.Items.AddRange(new object[] { "Hằng ngày", "Mỗi 3 ngày", "Hằng tuần", "Mỗi khi bật máy" });
        cboFreq.SelectedIndex = Math.Max(0, Math.Min(3, settings.Frequency));
        cboFreq.SetBounds(90, 58, 170, 28);

        var lblTime = new Label { Text = "Lúc:", Location = new Point(280, 62), AutoSize = true };
        dtTime.Format = DateTimePickerFormat.Custom;
        dtTime.CustomFormat = "HH:mm";
        dtTime.ShowUpDown = true;
        dtTime.SetBounds(320, 58, 90, 28);
        DateTime t;
        dtTime.Value = DateTime.TryParse(settings.Time, out t) ? DateTime.Today.Add(t.TimeOfDay) : DateTime.Today.AddHours(12);
        cboFreq.SelectedIndexChanged += (s, e) => dtTime.Enabled = cboFreq.SelectedIndex != 3;
        dtTime.Enabled = cboFreq.SelectedIndex != 3;

        chkRecycle.Text = "Cho phép tự động dọn cả Thùng rác (không khôi phục được)";
        chkRecycle.SetBounds(14, 94, 580, 24);
        chkRecycle.Checked = settings.AutoRecycle;
        chkNotify.Text = "Hiện thông báo nhỏ sau mỗi lần tự động dọn";
        chkNotify.SetBounds(14, 120, 580, 24);
        chkNotify.Checked = settings.Notify;

        btnSave.Text = "Lưu cài đặt";
        btnSave.SetBounds(14, 150, 140, 34);
        btnSave.Click += (s, e) => SaveSettings();
        btnLog.Text = "Xem nhật ký";
        btnLog.SetBounds(164, 150, 120, 34);
        btnLog.Click += (s, e) => { if (File.Exists(Cleaner.LogFile)) Process.Start("notepad.exe", "\"" + Cleaner.LogFile + "\""); else MessageBox.Show("Chưa có lần dọn nào.", Text); };
        lblLast.SetBounds(294, 150, 420, 40);
        lblLast.ForeColor = Color.DimGray;
        lblLast.Font = new Font("Segoe UI", 8.5f);

        grp.Controls.AddRange(new Control[] { chkAuto, lblFreq, cboFreq, lblTime, dtTime, chkRecycle, chkNotify, btnSave, btnLog, lblLast });
        var lblVer = new Label { Text = "Phiên bản " + Updater.Current.ToString(3), AutoSize = true, ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8.5f), Location = new Point(470, 22) };
        btnUpdate.Text = "Kiểm tra cập nhật";
        btnUpdate.SetBounds(584, 14, 160, 32);
        btnUpdate.Click += async (s, e) => await CheckUpdate(true);
        Controls.AddRange(new Control[] { title, lblVer, btnUpdate, lblDisk, btnScan, lblStatus, tabs, grp });
        Shown += async (s, e) => await CheckUpdate(false);
        UpdateInfo();
    }

    // --- Tab 5: Second Brain – chỉ mục toàn máy cho agent tra cứu ---
    readonly Label lblBrain = new Label();
    readonly TextBox txtBrain = new TextBox();
    readonly ListView brainHits = new ListView();

    TabPage BuildBrainTab()
    {
        var t = new TabPage("5. Second Brain");
        lblBrain.SetBounds(8, 6, 700, 40);
        lblBrain.Font = new Font("Segoe UI", 8.5f);
        txtBrain.SetBounds(8, 50, 560, 26);
        var btnFind = new Button { Text = "Tìm" };
        btnFind.SetBounds(576, 48, 132, 30);
        brainHits.View = View.Details;
        brainHits.FullRowSelect = true;
        brainHits.ShowItemToolTips = true;
        brainHits.SetBounds(8, 84, 700, 194);
        brainHits.Columns.Add("File", 260);
        brainHits.Columns.Add("Sửa", 80);
        brainHits.Columns.Add("Đoạn khớp", 340);
        brainHits.DoubleClick += (s, e) => { if (brainHits.SelectedItems.Count > 0) Process.Start("explorer.exe", "/select,\"" + brainHits.SelectedItems[0].Tag + "\""); };
        Func<Task> find = async () =>
        {
            var q = txtBrain.Text;
            var hits = await Task.Run(() => Brain.Search(q, null, null, false, 50));
            brainHits.Items.Clear();
            foreach (var h in hits)
            {
                var snip = (h.Snippet ?? "").Replace("\r", "").Replace("\n", "  ·  ");
                brainHits.Items.Add(new ListViewItem(new[] { h.File.Path, DateTime.FromBinary(h.File.Mtime).ToString("dd/MM/yyyy"), snip })
                    { Tag = h.File.Path, ToolTipText = h.File.Path + "\n" + h.Snippet });
            }
            if (hits.Count == 0) brainHits.Items.Add(new ListViewItem(new[] { "Không tìm thấy.", "", "" }));
        };
        btnFind.Click += async (s, e) => await find();
        txtBrain.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await find(); } };

        var btnIndex = new Button { Text = "Cập nhật chỉ mục" };
        btnIndex.SetBounds(8, 286, 160, 36);
        btnIndex.Click += (s, e) => { if (!Brain.Building) Task.Run(() => { try { Brain.Build(); } catch (Exception ex) { Brain.Progress = "Lỗi: " + ex.Message; } }); };
        var btnConnect = new Button { Text = "Kết nối Claude Code", BackColor = Color.FromArgb(58, 42, 160), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        btnConnect.SetBounds(176, 286, 180, 36);
        btnConnect.Click += async (s, e) => await ConnectClaude();
        var note = new Label { Text = "Agent ở mọi dự án tra cứu được tài liệu trên mọi ổ, USB, thẻ nhớ.\nChỉ đọc và sao chép – không bao giờ sửa hay xoá file.",
            Location = new Point(364, 284), Size = new Size(344, 40), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8) };
        t.Controls.AddRange(new Control[] { lblBrain, txtBrain, btnFind, brainHits, btnIndex, btnConnect, note });

        var timer = new Timer { Interval = 1000 };
        timer.Tick += (s, e) => ShowBrainStatus();
        timer.Start();
        Task.Run(() => { Brain.EnsureLoaded(); Brain.RefreshIfStale(TimeSpan.FromHours(6)); });
        return t;
    }

    void ShowBrainStatus()
    {
        var p = Brain.Progress;
        if (p.Length > 110) p = "…" + p.Substring(p.Length - 110);
        lblBrain.Text = "Chỉ mục: " + Brain.FileCount.ToString("N0") + " file, " + Brain.ProjectCount + " dự án · cập nhật " +
            (Brain.BuiltAt == DateTime.MinValue ? "chưa có" : Brain.BuiltAt.ToString("dd/MM/yyyy HH:mm")) +
            (Brain.Building ? "\n" + p : "\nGõ từ khoá để tìm trên toàn máy (không cần gõ dấu).");
    }

    // Đăng ký Second Brain làm máy chủ MCP cho Claude Code ở phạm vi người dùng (mọi dự án đều thấy)
    async Task ConnectClaude()
    {
        string exe = Path.Combine(Cleaner.DataDir, "BentarSecondBrain.exe");
        string cmd = "claude mcp add --scope user second-brain -- \"" + exe + "\" /mcp";
        try
        {
            Directory.CreateDirectory(Cleaner.DataDir);
            if (!string.Equals(Application.ExecutablePath, exe, StringComparison.OrdinalIgnoreCase)) File.Copy(Application.ExecutablePath, exe, true);
        }
        catch (Exception ex) { MessageBox.Show("Không chép được app vào " + exe + ":\n" + ex.Message, Text); return; }
        Func<string, Tuple<int, string>> run = c =>
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + c) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var pr = Process.Start(psi)) { var o = pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd(); pr.WaitForExit(); return Tuple.Create(pr.ExitCode, o); }
        };
        var r = await Task.Run(() => { run("claude mcp remove --scope user second-brain"); return run(cmd); });
        if (r.Item1 == 0)
            MessageBox.Show("Đã kết nối Second Brain với Claude Code.\nMở lại phiên Claude Code ở bất kỳ dự án nào là agent dùng được.", Text);
        else
        {
            Clipboard.SetText(cmd);
            MessageBox.Show("Chưa chạy được lệnh claude (" + r.Item2.Trim() + ").\n\nLệnh đã được chép vào clipboard, bạn dán vào terminal để chạy:\n" + cmd, Text);
        }
    }

    void UpdateInfo()
    {
        try { lblDisk.Text = "Còn trống:  " + string.Join("   |   ", Analyzer.FixedDrives().Select(d => "Ổ " + d.Name.Substring(0, 1) + ": " + Cleaner.Fmt(d.AvailableFreeSpace) + " / " + Cleaner.Fmt(d.TotalSize))); }
        catch { lblDisk.Text = ""; }
        string last = Cleaner.LastLog();
        lblLast.Text = last == null ? "" : "Lần dọn gần nhất:\n" + last;
    }

    List<CleanItem> Chosen()
    {
        return list.Items.Cast<ListViewItem>().Where(r => r.Checked).Select(r => (CleanItem)r.Tag).ToList();
    }

    void SetBusy(bool busy, string msg)
    {
        btnScan.Enabled = btnClean.Enabled = btnSave.Enabled = btnRecycle.Enabled = btnMove.Enabled = btnStaleRecycle.Enabled = !busy;
        lblStatus.Text = msg;
        UseWaitCursor = busy;
    }

    async Task Scan()
    {
        SetBusy(true, "Đang quét...");
        long total = 0;
        foreach (ListViewItem row in list.Items)
        {
            var it = (CleanItem)row.Tag;
            row.SubItems[1].Text = "đang quét...";
            it.Size = await Task.Run(() => Cleaner.Measure(it));
            row.SubItems[1].Text = Cleaner.Fmt(it.Size);
            if (row.Checked) total += it.Size;
        }

        lblStatus.Text = "Đang tìm file lớn, trùng lặp, backup cũ...";
        findings = await Task.Run(() => Analyzer.FindReview());
        FillReview();

        lblStatus.Text = "Đang đánh giá thư mục lâu không dùng...";
        var ratings = await Task.Run(() => Analyzer.RateFolders());
        stale.Items.Clear();
        foreach (var r in ratings)
        {
            var row = new ListViewItem(new[] { r.Path, Cleaner.Fmt(r.Size), r.LastUsed.ToString("dd/MM/yyyy"), r.Rating }) { Tag = r, ToolTipText = r.Path };
            if (r.Days >= 365 || r.Files == 0) row.BackColor = Color.FromArgb(255, 236, 230);
            else if (r.Ai || r.Days < 60) row.BackColor = Color.FromArgb(230, 245, 233);
            stale.Items.Add(row);
        }

        lblStatus.Text = "Đang đo các mục cần giữ...";
        foreach (ListViewItem row in keep.Items)
        {
            var k = (KeepItem)row.Tag;
            k.Size = await Task.Run(() => k.Paths.Sum(p => Analyzer.MeasurePath(p)));
            row.SubItems[1].Text = Cleaner.Fmt(k.Size);
        }

        SetBusy(false, "Quét xong.  Nhóm 1 dọn được " + Cleaner.Fmt(total) + ".  Nhóm 2: " + findings.Count + " mục, " +
            Cleaner.Fmt(findings.Sum(f => f.Size)) + " – xem tab 2 và tự quyết.");
        UpdateInfo();
    }

    void FillReview()
    {
        review.Items.Clear();
        foreach (var f in findings)
        {
            string name = f.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
                ? f.Path.Substring(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Length + 1) : f.Path;
            review.Items.Add(new ListViewItem(new[] { name, Cleaner.Fmt(f.Size), f.Date.ToString("dd/MM/yyyy"), f.Reason }) { Tag = f, ToolTipText = f.Path });
        }
        review.ShowItemToolTips = true;
    }

    // manual = người dùng bấm nút; còn khi mở app thì chỉ kiểm tra lặng lẽ và đổi chữ trên nút nếu có bản mới
    async Task CheckUpdate(bool manual)
    {
        Updater.Release r;
        try { btnUpdate.Enabled = false; r = await Task.Run(() => Updater.Latest()); }
        catch (Exception ex)
        {
            if (manual) MessageBox.Show("Không kiểm tra được cập nhật (cần kết nối Internet).\n" + ex.Message, Text);
            return;
        }
        finally { btnUpdate.Enabled = true; }

        if (r == null || r.Version <= Updater.Current)
        {
            if (manual) MessageBox.Show("Bạn đang dùng phiên bản mới nhất (" + Updater.Current.ToString(3) + ").", Text);
            return;
        }
        btnUpdate.Text = "Cập nhật lên " + r.Tag;
        btnUpdate.BackColor = Color.FromArgb(255, 193, 7);
        if (!manual) return;

        string notes = string.IsNullOrWhiteSpace(r.Notes) ? "" : "\n\nCó gì mới:\n" + (r.Notes.Length > 600 ? r.Notes.Substring(0, 600) + "..." : r.Notes);
        if (MessageBox.Show("Đã có phiên bản " + r.Tag + " (bạn đang dùng " + Updater.Current.ToString(3) + ")." + notes +
            "\n\nCập nhật ngay? App sẽ tự đóng và mở lại.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        try
        {
            SetBusy(true, "Đang tải bản " + r.Tag + "...");
            btnUpdate.Enabled = false;
            await Task.Run(() => Updater.DownloadAndRestart(r));
            Application.Exit();
        }
        catch (Exception ex)
        {
            SetBusy(false, "Cập nhật thất bại: " + ex.Message);
            btnUpdate.Enabled = true;
        }
    }

    void OpenStale()
    {
        if (stale.SelectedItems.Count == 0) { MessageBox.Show("Hãy bấm chọn một dòng trước.", Text); return; }
        Process.Start("explorer.exe", "\"" + ((FolderRating)stale.SelectedItems[0].Tag).Path + "\"");
    }

    async Task RecycleStale()
    {
        var chosen = stale.Items.Cast<ListViewItem>().Where(r => r.Checked).Select(r => (FolderRating)r.Tag).ToList();
        if (chosen.Count == 0) { MessageBox.Show("Hãy tích chọn thư mục ở tab 4 trước.", Text); return; }
        string list = string.Join("\n", chosen.Take(10).Select(c => "• " + c.Path)) + (chosen.Count > 10 ? "\n..." : "");
        if (MessageBox.Show("Chuyển " + chosen.Count + " thư mục (" + Cleaner.Fmt(chosen.Sum(c => c.Size)) + ") vào Thùng rác?\n\n" + list +
            "\n\nVẫn khôi phục lại được từ Thùng rác.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        SetBusy(true, "Đang chuyển thư mục vào Thùng rác...");
        int ok = 0, fail = 0;
        foreach (var c in chosen)
        {
            try
            {
                // AllDialogs: nếu thư mục quá lớn so với Thùng rác, Windows sẽ hỏi lại bạn thay vì tự xoá vĩnh viễn
                await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(c.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
                ok++;
            }
            catch { fail++; }
        }
        Cleaner.Log("Chuyển thư mục vào Thùng rác: " + ok + " thư mục");
        foreach (var row in stale.Items.Cast<ListViewItem>().Where(r => r.Checked && !Directory.Exists(((FolderRating)r.Tag).Path)).ToList())
            stale.Items.Remove(row);
        SetBusy(false, "Xong: " + ok + " thư mục" + (fail > 0 ? ", " + fail + " bị huỷ hoặc lỗi" : "") + ".");
        UpdateInfo();
    }

    void OpenLocation()
    {
        if (review.SelectedItems.Count == 0) { MessageBox.Show("Hãy bấm chọn một dòng trước.", Text); return; }
        var f = (Finding)review.SelectedItems[0].Tag;
        Process.Start("explorer.exe", "/select,\"" + f.Path + "\"");
    }

    async Task ReviewAction(bool moveToD)
    {
        var chosen = review.Items.Cast<ListViewItem>().Where(r => r.Checked).Select(r => (Finding)r.Tag).ToList();
        if (chosen.Count == 0) { MessageBox.Show("Hãy tích chọn file ở tab 2 trước.", Text); return; }
        if (chosen.Any(f => f.IsFolder))
        {
            MessageBox.Show("Windows.old cần xoá bằng Disk Cleanup:\nmở cleanmgr > Clean up system files > tích \"Previous Windows installation(s)\".", Text);
            Process.Start("cleanmgr.exe");
            chosen = chosen.Where(f => !f.IsFolder).ToList();
            if (chosen.Count == 0) return;
        }

        string dest = @"D:\Lưu trữ từ ổ C";
        if (moveToD && !Directory.Exists(@"D:\")) { MessageBox.Show("Không tìm thấy ổ D.", Text); return; }
        if (moveToD)
        {
            chosen = chosen.Where(f => f.Path.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count == 0) { MessageBox.Show("Chỉ chuyển được file đang nằm trên ổ C.", Text); return; }
        }
        string q = moveToD
            ? "Chuyển " + chosen.Count + " file (" + Cleaner.Fmt(chosen.Sum(f => f.Size)) + ") sang thư mục\n" + dest + " ?"
            : "Chuyển " + chosen.Count + " file (" + Cleaner.Fmt(chosen.Sum(f => f.Size)) + ") vào Thùng rác?\nVẫn khôi phục lại được. Ổ C chỉ trống thêm khi Thùng rác được dọn.";
        if (MessageBox.Show(q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        SetBusy(true, moveToD ? "Đang chuyển sang ổ D..." : "Đang chuyển vào Thùng rác...");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        int ok = 0, fail = 0;
        await Task.Run(() =>
        {
            foreach (var f in chosen)
            {
                try
                {
                    if (moveToD)
                    {
                        string rel = f.Path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? f.Path.Substring(home.Length + 1) : Path.GetFileName(f.Path);
                        string target = Path.Combine(dest, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        if (File.Exists(target)) target = Path.Combine(Path.GetDirectoryName(target),
                            Path.GetFileNameWithoutExtension(target) + " (" + DateTime.Now.ToString("yyyyMMddHHmmss") + ")" + Path.GetExtension(target));
                        File.Move(f.Path, target);
                    }
                    else
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f.Path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    ok++;
                }
                catch { fail++; }
            }
        });
        Cleaner.Log((moveToD ? "Chuyển sang ổ D: " : "Chuyển vào Thùng rác: ") + ok + " file");
        findings = findings.Where(f => File.Exists(f.Path) || f.IsFolder).ToList();
        FillReview();
        SetBusy(false, "Xong: " + ok + " file" + (fail > 0 ? ", " + fail + " file lỗi (đang mở hoặc không có quyền)" : "") + ".");
        UpdateInfo();
    }

    async Task CleanNow()
    {
        var chosen = Chosen();
        if (chosen.Count == 0) { MessageBox.Show("Bạn chưa chọn mục nào.", Text); return; }
        string warn = "Xoá vĩnh viễn các mục đã chọn?";
        if (chosen.Any(c => c.Id == "recycle")) warn += "\n(Thùng rác sau khi xoá sẽ KHÔNG khôi phục lại được.)";
        if (chosen.Any(c => c.Id == "chrome" || c.Id == "edge" || c.Id == "coccoc")) warn += "\nNên tắt trình duyệt trước để dọn được nhiều hơn.";
        if (MessageBox.Show(warn, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        long before = Cleaner.FreeC();
        SetBusy(true, "Đang dọn dẹp...");
        int skipped = 0;
        foreach (var it in chosen) skipped += await Task.Run(() => Cleaner.Clean(it));
        long freed = Math.Max(0, Cleaner.FreeC() - before);
        Cleaner.Log("Dọn thủ công: giải phóng " + Cleaner.Fmt(freed));
        await Scan();
        lblStatus.Text = "Dọn xong! Đã giải phóng khoảng " + Cleaner.Fmt(freed) + "." +
            (skipped > 0 ? "\n" + skipped + " file đang được phần mềm khác dùng nên được giữ lại (bình thường)." : "");
    }

    void SaveSettings()
    {
        settings.Selected = new HashSet<string>(Chosen().Select(c => c.Id));
        settings.AutoEnabled = chkAuto.Checked;
        settings.Frequency = cboFreq.SelectedIndex;
        settings.Time = dtTime.Value.ToString("HH:mm");
        settings.AutoRecycle = chkRecycle.Checked;
        settings.Notify = chkNotify.Checked;
        try
        {
            settings.Save();
            if (settings.AutoEnabled)
            {
                Scheduler.Enable(settings);
                string when = settings.Frequency == 3 ? "mỗi khi bật máy (khoảng 30 giây sau khi đăng nhập)" : cboFreq.Text.ToLower() + " lúc " + settings.Time;
                MessageBox.Show("Đã bật tự động dọn dẹp " + when + ".\nNếu đúng giờ đó máy đang tắt, app sẽ dọn ngay khi bạn bật máy lại.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Scheduler.Disable();
                MessageBox.Show("Đã lưu. Tự động dọn dẹp đang TẮT.", Text);
            }
        }
        catch (Exception ex) { MessageBox.Show("Lỗi: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // Chế độ chạy ngầm theo lịch: không mở cửa sổ, dọn xong hiện thông báo nhỏ góc màn hình
    static void RunAuto()
    {
        var s = Settings.Load();
        if (!s.Loaded) return;
        long before = Cleaner.FreeC();
        foreach (var it in Cleaner.AllItems())
        {
            if (!s.Selected.Contains(it.Id)) continue;
            if (it.Id == "recycle" && !s.AutoRecycle) continue;
            try { Cleaner.Clean(it); } catch { }
        }
        long freed = Math.Max(0, Cleaner.FreeC() - before);
        Cleaner.Log("Tự động: giải phóng " + Cleaner.Fmt(freed));

        if (!s.Notify) return;
        using (var tray = new NotifyIcon { Icon = SystemIcons.Information, Visible = true, Text = Cleaner.AppName })
        {
            tray.ShowBalloonTip(8000, Cleaner.AppName, "Đã tự động dọn dẹp, giải phóng " + Cleaner.Fmt(freed) + ".\nỔ C còn trống " + Cleaner.Fmt(Cleaner.FreeC()) + ".", ToolTipIcon.Info);
            var end = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(100); }
            tray.Visible = false;
        }
    }

    static void WriteReport(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 1. XOÁ AN TOÀN (app tự dọn)");
        foreach (var it in Cleaner.AllItems()) sb.AppendLine(Cleaner.Fmt(Cleaner.Measure(it)).PadLeft(10) + "  " + it.Name);
        sb.AppendLine().AppendLine("== 2. CẦN BẠN XEM XÉT");
        foreach (var f in Analyzer.FindReview()) sb.AppendLine(Cleaner.Fmt(f.Size).PadLeft(10) + "  " + f.Path + "  ->  " + f.Reason);
        sb.AppendLine().AppendLine("== 4. THƯ MỤC LÂU KHÔNG DÙNG");
        foreach (var r in Analyzer.RateFolders()) sb.AppendLine(Cleaner.Fmt(r.Size).PadLeft(10) + "  " + r.LastUsed.ToString("dd/MM/yyyy") + "  " + r.Path + "  ->  " + r.Rating);
        sb.AppendLine().AppendLine("== 3. CẦN GIỮ");
        foreach (var k in Analyzer.KeepItems()) sb.AppendLine(Cleaner.Fmt(k.Paths.Sum(p => Analyzer.MeasurePath(p))).PadLeft(10) + "  " + k.Name + "  ->  " + k.Why);
        File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Scheduler.MigrateFromOldName();
        if (args.Any(a => a.Equals("/auto", StringComparison.OrdinalIgnoreCase))) { RunAuto(); return; }
        // /report <file>: chỉ quét (không xoá gì) và ghi kết quả phân loại ra file
        if (args.Length == 2 && args[0].Equals("/report", StringComparison.OrdinalIgnoreCase)) { WriteReport(args[1]); return; }
        // /mcp: máy chủ MCP cho coding agent; /index: cập nhật chỉ mục Second Brain rồi thoát
        if (args.Any(a => a.Equals("/mcp", StringComparison.OrdinalIgnoreCase))) { McpServer.Run(); return; }
        if (args.Any(a => a.Equals("/index", StringComparison.OrdinalIgnoreCase))) { Brain.Build(); return; }
        Application.Run(new MainForm());
    }
}

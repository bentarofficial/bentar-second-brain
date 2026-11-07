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
[assembly: System.Reflection.AssemblyVersion("2.9.2.0")]

// Cập nhật từ GitHub Releases: so sánh tag (vd "v2.1.0") với phiên bản app, tải file .exe mới rồi tự thay thế
static class Updater
{
    public const string Repo = "bentarofficial/bentar-second-brain";
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
        // Mỗi bản phát hành là một file .zip trên GitHub Releases (bản cũ chỉ có .exe thì dùng .exe)
        var url = System.Text.RegularExpressions.Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"");
        if (!url.Success) url = System.Text.RegularExpressions.Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"");
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
        if (r.ExeUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string zip = newExe + ".zip";
            using (var wc = Client()) wc.DownloadFile(r.ExeUrl, zip);
            using (var z = new System.IO.Compression.ZipArchive(File.OpenRead(zip)))
            {
                var entry = z.Entries.Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Length).FirstOrDefault();
                if (entry == null) throw new Exception(L.T("The release .zip has no .exe inside.", "File .zip của bản phát hành không có file .exe."));
                using (var src = entry.Open()) using (var dst = File.Create(newExe)) src.CopyTo(dst);
            }
            try { File.Delete(zip); } catch { }
        }
        else using (var wc = Client()) wc.DownloadFile(r.ExeUrl, newExe);
        if (new FileInfo(newExe).Length < 10 * 1024) throw new Exception(L.T("The downloaded file is invalid.", "File tải về không hợp lệ."));

        string installed = Installer.Exe;   // bản đã cài (lịch tự động và agent dùng chung)
        Func<string, string> q = s => "'" + s.Replace("'", "''") + "'";
        string script =
            "$p=" + Process.GetCurrentProcess().Id + "; while(Get-Process -Id $p -ErrorAction SilentlyContinue){Start-Sleep -Milliseconds 300};" +
            // File có thể đang được agent chạy (/mcp): không ghi đè được thì đổi tên bản cũ sang .old rồi chép
            "function Put($d){try{Copy-Item -LiteralPath " + q(newExe) + " -Destination $d -Force -ErrorAction Stop}catch{Move-Item -LiteralPath $d -Destination ($d+'.'+[DateTime]::Now.Ticks+'.old') -Force;Copy-Item -LiteralPath " + q(newExe) + " -Destination $d -Force}};" +
            "Put " + q(target) + ";" +
            "if((Test-Path -LiteralPath " + q(installed) + ") -and (" + q(installed) + " -ne " + q(target) + ")){Put " + q(installed) + "};" +
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
    // BSB_DATA_DIR: dùng thư mục dữ liệu khác (để chạy thử mà không đụng tới bộ não thật)
    public static readonly string DataDir = Environment.GetEnvironmentVariable("BSB_DATA_DIR") ?? Path.Combine(Local, "BentarSecondBrain");
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
            new CleanItem("recycle", L.T("Recycle Bin", "Thùng rác (Recycle Bin)"), true, null),
            new CleanItem("temp", L.T("User temp files (Temp)", "File tạm của người dùng (Temp)"), true, () => new[] { Path.GetTempPath() }),
            new CleanItem("wintemp", L.T("Windows temp files", "File tạm của Windows"), true, () => new[] { @"C:\Windows\Temp" }),
            new CleanItem("chrome", L.T("Google Chrome cache", "Bộ nhớ đệm Google Chrome"), true, () => BrowserCaches(@"Google\Chrome\User Data")),
            new CleanItem("edge", L.T("Microsoft Edge cache", "Bộ nhớ đệm Microsoft Edge"), true, () => BrowserCaches(@"Microsoft\Edge\User Data")),
            new CleanItem("coccoc", L.T("Coc Coc cache", "Bộ nhớ đệm Cốc Cốc"), true, () => BrowserCaches(@"CocCoc\Browser\User Data")),
            new CleanItem("npm", L.T("npm cache (npm-cache)", "Bộ nhớ đệm npm (npm-cache)"), true, () => new[] { Path.Combine(Local, "npm-cache") }),
            new CleanItem("crash", L.T("Error reports & crash dumps", "Báo cáo lỗi & crash dump"), true, () => new[] {
                Path.Combine(Local, "CrashDumps"),
                @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive",
                @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue" }),
            new CleanItem("dx", L.T("DirectX shader cache", "Bộ nhớ đệm đồ hoạ DirectX"), true, () => new[] { Path.Combine(Local, "D3DSCache") }),
            new CleanItem("wu", L.T("Downloaded Windows Update files (needs Admin)", "File cập nhật Windows đã tải (cần quyền Admin)"), false, () => new[] { @"C:\Windows\SoftwareDistribution\Download" }),
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
    static string InstalledExe { get { return Installer.Exe; } }

    static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false };
        using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
    }

    // Lịch luôn chạy bản đã cài, để vẫn chạy được dù bạn di chuyển/xoá file .exe gốc
    public static void Enable(Settings s)
    {
        Directory.CreateDirectory(Cleaner.DataDir);
        if (!Installer.IsInstalled) Installer.Install();

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
        if (code != 0) throw new Exception(L.T("Could not create the schedule (error code ", "Không tạo được lịch (mã lỗi ") + code + ").");
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
                Cleaner.Log(L.T("Moved the automatic schedule to the new name Bentar Second Brain", "Đã chuyển lịch tự động sang tên mới Bentar Second Brain"));
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
                    reasons[f.FullName] = L.T("Duplicate of ", "Trùng lặp với ") + keep.Name + " (" + keep.Directory.Name + ")";
            }

        // 2. Backup cũ: cùng tên website/dự án, đã có bản mới hơn
        foreach (var g in files.Where(f => BackupExt.Contains(f.Extension.ToLower()))
                               .GroupBy(f => { var m = DatedName.Match(f.Name); return m.Success ? m.Groups[1].Value.ToLower() + f.Extension.ToLower() : null; })
                               .Where(g => g.Key != null && g.Count() > 1))
        {
            var newest = g.OrderByDescending(f => f.LastWriteTime).First();
            foreach (var f in g.Where(f => f != newest && !reasons.ContainsKey(f.FullName)))
                reasons[f.FullName] = L.T("Old backup, a newer one exists (", "Bản backup cũ, đã có bản mới hơn (") + newest.LastWriteTime.ToString("dd/MM/yyyy") + ")";
        }

        foreach (var f in files)
        {
            if (reasons.ContainsKey(f.FullName)) continue;
            bool c = OnC(f.FullName);
            if (IsInstaller(f) && (c ? f.FullName.StartsWith(Downloads, StringComparison.OrdinalIgnoreCase) : LooksLikeSetup(f)))
                reasons[f.FullName] = L.T("Software installer – safe to delete once installed", "File cài đặt phần mềm – đã cài xong thì có thể xoá");
            else if (c && f.LastWriteTime < DateTime.Now.AddDays(-180))
                reasons[f.FullName] = L.T("Unchanged for over 6 months – delete if not needed, or move to drive D", "Hơn 6 tháng không thay đổi – xoá nếu không cần, hoặc chuyển sang ổ D");
            else if (c && f.Length >= 200 * MB)
                reasons[f.FullName] = L.T("Large file – consider moving it to drive D to free up C", "File lớn – nên chuyển sang ổ D cho ổ C nhẹ");
            else if (!c && f.Length >= 500 * MB && f.LastWriteTime < DateTime.Now.AddDays(-180))
                reasons[f.FullName] = L.T("Large file unchanged for over 6 months – delete if no longer needed", "File lớn, hơn 6 tháng không thay đổi – xoá nếu không còn cần");
        }

        var result = files.Where(f => reasons.ContainsKey(f.FullName))
            .Select(f => new Finding { Path = f.FullName, Size = f.Length, Date = f.LastWriteTime, Reason = reasons[f.FullName] })
            .ToList();

        if (Directory.Exists(@"C:\Windows.old"))
            result.Add(new Finding { Path = @"C:\Windows.old", IsFolder = true, Size = Walk(@"C:\Windows.old").Sum(f => f.Length),
                Date = Directory.GetLastWriteTime(@"C:\Windows.old"),
                Reason = L.T("Previous Windows after an upgrade – remove with Disk Cleanup (cleanmgr), not by hand", "Bản Windows cũ sau khi nâng cấp – xoá bằng Disk Cleanup (cleanmgr), không xoá tay") });

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
                if (r.Ai && days < 365) r.Rating = L.T("Has traces of AI/coding work – keep it", "Có dấu vết AI/lập trình làm việc – nên giữ");
                else if (days >= 365) r.Rating = L.T("Untouched for over a year – possibly a dead folder", "Hơn 1 năm không đụng tới – có thể là thư mục chết") + (r.Ai ? L.T(" (AI worked here before)", " (từng có AI làm việc)") : "");
                else if (days >= 180) r.Rating = L.T("Rarely used (over 6 months)", "Ít dùng (hơn 6 tháng)");
                else if (days >= 60) r.Rating = L.T("Used occasionally", "Thỉnh thoảng dùng");
                else r.Rating = r.OpenedRecently ? L.T("In use (opened recently)", "Đang dùng (vừa mở gần đây)") : L.T("In use", "Đang dùng");
                if (r.Files == 0) r.Rating = L.T("Empty folder – safe to delete", "Thư mục rỗng – xoá được");
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
            new KeepItem(L.T("Windows (operating system)", "Windows (hệ điều hành)"), L.T("DO NOT delete – the PC will not boot", "KHÔNG xoá – xoá nhầm máy sẽ không khởi động được"), @"C:\Windows"),
            new KeepItem(L.T("Installed software", "Phần mềm đã cài"), L.T("Uninstall in Settings > Apps instead of deleting folders", "Muốn bỏ thì gỡ trong Settings > Apps, không xoá thư mục"), @"C:\Program Files", @"C:\Program Files (x86)"),
            new KeepItem(L.T("Shared program data (ProgramData)", "Dữ liệu chung của phần mềm (ProgramData)"), L.T("Needed by software to run – do not delete", "Phần mềm cần để chạy – không xoá"), @"C:\ProgramData"),
            new KeepItem(L.T("App settings & accounts (AppData\\Roaming)", "Cài đặt & tài khoản phần mềm (AppData\\Roaming)"), L.T("Sign-ins for Zalo, Zoom, Office... live here – do not delete", "Đăng nhập Zalo, Zoom, Office... nằm ở đây – không xoá"), roaming),
            new KeepItem(L.T("Browser profiles (Chrome, Coc Coc, Edge)", "Hồ sơ trình duyệt (Chrome, Cốc Cốc, Edge)"), L.T("Passwords, bookmarks, history – only clear the cache (in the Safe tab)", "Mật khẩu, bookmark, lịch sử – chỉ xoá cache (đã có ở tab An toàn)"),
                Path.Combine(local, @"Google\Chrome\User Data"), Path.Combine(local, @"CocCoc\Browser\User Data"), Path.Combine(local, @"Microsoft\Edge\User Data")),
            new KeepItem(L.T("Your Documents, Desktop, Pictures", "Tài liệu, Desktop, Ảnh của bạn"), L.T("Personal data – back it up; move it to drive D to free up C", "Dữ liệu cá nhân – nên sao lưu; muốn ổ C trống thì chuyển sang ổ D"),
                Path.Combine(Home, "Documents"), Path.Combine(Home, "Desktop"), Path.Combine(Home, "Pictures")),
            new KeepItem("pagefile.sys / hiberfil.sys", L.T("System files managed by Windows – do not delete", "File hệ thống Windows tự quản lý – không xoá"), @"C:\pagefile.sys", @"C:\hiberfil.sys"),
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

partial class MainForm : Form
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
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = SizeFromClientSize(new Size(760, 702));
        Font = new Font("Segoe UI", 10);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildShell();   // khung giao diện: xem MainShell.cs
        UpdateInfo();
    }

    // Cài app vào AppData rồi đăng ký Second Brain làm máy chủ MCP cho mọi agent có trên máy
    async Task ConnectAgents()
    {
        try { if (!Installer.IsInstalled) Installer.Install(); }
        catch (Exception ex) { MessageBox.Show(L.T("Could not install the app to ", "Không cài được app vào ") + Installer.Dir + ":\n" + ex.Message, Text); return; }
        var lines = await Task.Run(() => AgentLinks.ConnectAll(false));
        MessageBox.Show(lines.Count == 0 ? L.T("No agents found on this PC (Claude Code, Claude Desktop, Cursor, Windsurf, Codex, Gemini CLI).", "Không tìm thấy agent nào trên máy (Claude Code, Claude Desktop, Cursor, Windsurf, Codex, Gemini CLI).") :
            L.T("Connecting Second Brain:\n\n", "Kết nối Second Brain:\n\n") + string.Join("\n", lines) + L.T("\n\nOpen a NEW agent session to start using it.", "\n\nMở phiên agent MỚI là dùng được."), Text);
    }

    void UpdateInfo()
    {
        try { lblDisk.Text = L.T("Free space:  ", "Còn trống:  ") + string.Join("   |   ", Analyzer.FixedDrives().Select(d => L.T("Drive ", "Ổ ") + d.Name.Substring(0, 1) + ": " + Cleaner.Fmt(d.AvailableFreeSpace) + " / " + Cleaner.Fmt(d.TotalSize))); }
        catch { lblDisk.Text = ""; }
        string last = Cleaner.LastLog();
        lblLast.Text = last == null ? "" : L.T("Last cleanup:\n", "Lần dọn gần nhất:\n") + last;
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
        SetBusy(true, L.T("Scanning...", "Đang quét..."));
        long total = 0;
        foreach (ListViewItem row in list.Items)
        {
            var it = (CleanItem)row.Tag;
            row.SubItems[1].Text = L.T("scanning...", "đang quét...");
            it.Size = await Task.Run(() => Cleaner.Measure(it));
            row.SubItems[1].Text = Cleaner.Fmt(it.Size);
            if (row.Checked) total += it.Size;
        }

        lblStatus.Text = L.T("Looking for large files, duplicates, old backups...", "Đang tìm file lớn, trùng lặp, backup cũ...");
        findings = await Task.Run(() => Analyzer.FindReview());
        FillReview();

        lblStatus.Text = L.T("Rating long-unused folders...", "Đang đánh giá thư mục lâu không dùng...");
        var ratings = await Task.Run(() => Analyzer.RateFolders());
        stale.Items.Clear();
        foreach (var r in ratings)
        {
            var row = new ListViewItem(new[] { r.Path, Cleaner.Fmt(r.Size), r.LastUsed.ToString("dd/MM/yyyy"), r.Rating }) { Tag = r, ToolTipText = r.Path };
            if (r.Days >= 365 || r.Files == 0) row.BackColor = Theme.RowBad;
            else if (r.Ai || r.Days < 60) row.BackColor = Theme.RowGood;
            stale.Items.Add(row);
        }

        lblStatus.Text = L.T("Measuring items to keep...", "Đang đo các mục cần giữ...");
        foreach (ListViewItem row in keep.Items)
        {
            var k = (KeepItem)row.Tag;
            k.Size = await Task.Run(() => k.Paths.Sum(p => Analyzer.MeasurePath(p)));
            row.SubItems[1].Text = Cleaner.Fmt(k.Size);
        }

        SetBusy(false, L.T("Scan complete.  Group 1 can free ", "Quét xong.  Nhóm 1 dọn được ") + Cleaner.Fmt(total) + L.T(".  Group 2: ", ".  Nhóm 2: ") + findings.Count + L.T(" items, ", " mục, ") +
            Cleaner.Fmt(findings.Sum(f => f.Size)) + L.T(" – see tab 2 and decide.", " – xem tab 2 và tự quyết."));
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
            if (manual) MessageBox.Show(L.T("Could not check for updates (Internet connection required).\n", "Không kiểm tra được cập nhật (cần kết nối Internet).\n") + ex.Message, Text);
            return;
        }
        finally { btnUpdate.Enabled = true; }

        if (r == null || r.Version <= Updater.Current)
        {
            if (manual) MessageBox.Show(L.T("You are on the latest version (", "Bạn đang dùng phiên bản mới nhất (") + Updater.Current.ToString(3) + ").", Text);
            return;
        }
        btnUpdate.Text = L.T("Update to ", "Cập nhật lên ") + r.Tag;
        btnUpdate.Visible = true; btnUpdate.ForeColor = Color.Black;
        btnUpdate.BackColor = Color.FromArgb(255, 193, 7);
        if (!manual) return;

        string notes = string.IsNullOrWhiteSpace(r.Notes) ? "" : L.T("\n\nWhat's new:\n", "\n\nCó gì mới:\n") + (r.Notes.Length > 600 ? r.Notes.Substring(0, 600) + "..." : r.Notes);
        if (MessageBox.Show(L.T("Version ", "Đã có phiên bản ") + r.Tag + L.T(" is available (you have ", " (bạn đang dùng ") + Updater.Current.ToString(3) + ")." + notes +
            L.T("\n\nUpdate now? The app will close and reopen.", "\n\nCập nhật ngay? App sẽ tự đóng và mở lại."), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
        try
        {
            SetBusy(true, L.T("Downloading ", "Đang tải bản ") + r.Tag + "...");
            btnUpdate.Enabled = false;
            await Task.Run(() => Updater.DownloadAndRestart(r));
            Application.Exit();
        }
        catch (Exception ex)
        {
            SetBusy(false, L.T("Update failed: ", "Cập nhật thất bại: ") + ex.Message);
            btnUpdate.Enabled = true;
        }
    }

    void OpenStale()
    {
        if (stale.SelectedItems.Count == 0) { MessageBox.Show(L.T("Select a row first.", "Hãy bấm chọn một dòng trước."), Text); return; }
        Process.Start("explorer.exe", "\"" + ((FolderRating)stale.SelectedItems[0].Tag).Path + "\"");
    }

    async Task RecycleStale()
    {
        var chosen = stale.Items.Cast<ListViewItem>().Where(r => r.Checked).Select(r => (FolderRating)r.Tag).ToList();
        if (chosen.Count == 0) { MessageBox.Show(L.T("Check some folders in the tab first.", "Hãy tích chọn thư mục ở tab 4 trước."), Text); return; }
        string list = string.Join("\n", chosen.Take(10).Select(c => "• " + c.Path)) + (chosen.Count > 10 ? "\n..." : "");
        if (MessageBox.Show(L.T("Move ", "Chuyển ") + chosen.Count + L.T(" folders (", " thư mục (") + Cleaner.Fmt(chosen.Sum(c => c.Size)) + L.T(") to the Recycle Bin?\n\n", ") vào Thùng rác?\n\n") + list +
            L.T("\n\nYou can still restore them from the Recycle Bin.", "\n\nVẫn khôi phục lại được từ Thùng rác."), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        SetBusy(true, L.T("Moving folders to the Recycle Bin...", "Đang chuyển thư mục vào Thùng rác..."));
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
        Cleaner.Log(L.T("Moved folders to Recycle Bin: ", "Chuyển thư mục vào Thùng rác: ") + ok + L.T(" folders", " thư mục"));
        foreach (var row in stale.Items.Cast<ListViewItem>().Where(r => r.Checked && !Directory.Exists(((FolderRating)r.Tag).Path)).ToList())
            stale.Items.Remove(row);
        SetBusy(false, L.T("Done: ", "Xong: ") + ok + L.T(" folders", " thư mục") + (fail > 0 ? ", " + fail + L.T(" cancelled or failed", " bị huỷ hoặc lỗi") : "") + ".");
        UpdateInfo();
    }

    void OpenLocation()
    {
        if (review.SelectedItems.Count == 0) { MessageBox.Show(L.T("Select a row first.", "Hãy bấm chọn một dòng trước."), Text); return; }
        var f = (Finding)review.SelectedItems[0].Tag;
        Process.Start("explorer.exe", "/select,\"" + f.Path + "\"");
    }

    async Task ReviewAction(bool moveToD)
    {
        var chosen = review.Items.Cast<ListViewItem>().Where(r => r.Checked).Select(r => (Finding)r.Tag).ToList();
        if (chosen.Count == 0) { MessageBox.Show(L.T("Check some files in tab 2 first.", "Hãy tích chọn file ở tab 2 trước."), Text); return; }
        if (chosen.Any(f => f.IsFolder))
        {
            MessageBox.Show(L.T("Windows.old must be removed with Disk Cleanup:\nopen cleanmgr > Clean up system files > check \"Previous Windows installation(s)\".", "Windows.old cần xoá bằng Disk Cleanup:\nmở cleanmgr > Clean up system files > tích \"Previous Windows installation(s)\"."), Text);
            Process.Start("cleanmgr.exe");
            chosen = chosen.Where(f => !f.IsFolder).ToList();
            if (chosen.Count == 0) return;
        }

        string dest = @"D:\Lưu trữ từ ổ C";
        if (moveToD && !Directory.Exists(@"D:\")) { MessageBox.Show(L.T("Drive D not found.", "Không tìm thấy ổ D."), Text); return; }
        if (moveToD)
        {
            chosen = chosen.Where(f => f.Path.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count == 0) { MessageBox.Show(L.T("Only files on drive C can be moved.", "Chỉ chuyển được file đang nằm trên ổ C."), Text); return; }
        }
        string q = moveToD
            ? L.T("Move ", "Chuyển ") + chosen.Count + L.T(" files (", " file (") + Cleaner.Fmt(chosen.Sum(f => f.Size)) + L.T(") to folder\n", ") sang thư mục\n") + dest + " ?"
            : L.T("Move ", "Chuyển ") + chosen.Count + L.T(" files (", " file (") + Cleaner.Fmt(chosen.Sum(f => f.Size)) + L.T(") to the Recycle Bin?\nYou can still restore them. Drive C only gains space once the Recycle Bin is emptied.", ") vào Thùng rác?\nVẫn khôi phục lại được. Ổ C chỉ trống thêm khi Thùng rác được dọn.");
        if (MessageBox.Show(q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        SetBusy(true, moveToD ? L.T("Moving to drive D...", "Đang chuyển sang ổ D...") : L.T("Moving to the Recycle Bin...", "Đang chuyển vào Thùng rác..."));
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
        Cleaner.Log((moveToD ? L.T("Moved to drive D: ", "Chuyển sang ổ D: ") : L.T("Moved to Recycle Bin: ", "Chuyển vào Thùng rác: ")) + ok + " file");
        findings = findings.Where(f => File.Exists(f.Path) || f.IsFolder).ToList();
        FillReview();
        SetBusy(false, L.T("Done: ", "Xong: ") + ok + " file" + (fail > 0 ? ", " + fail + L.T(" files failed (in use or no permission)", " file lỗi (đang mở hoặc không có quyền)") : "") + ".");
        UpdateInfo();
    }

    async Task CleanNow()
    {
        var chosen = Chosen();
        if (chosen.Count == 0) { MessageBox.Show(L.T("You haven't selected anything.", "Bạn chưa chọn mục nào."), Text); return; }
        string warn = L.T("Permanently delete the selected items?", "Xoá vĩnh viễn các mục đã chọn?");
        if (chosen.Any(c => c.Id == "recycle")) warn += L.T("\n(The emptied Recycle Bin can NOT be restored.)", "\n(Thùng rác sau khi xoá sẽ KHÔNG khôi phục lại được.)");
        if (chosen.Any(c => c.Id == "chrome" || c.Id == "edge" || c.Id == "coccoc")) warn += L.T("\nClose your browsers first to clean more.", "\nNên tắt trình duyệt trước để dọn được nhiều hơn.");
        if (MessageBox.Show(warn, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        long before = Cleaner.FreeC();
        SetBusy(true, L.T("Cleaning...", "Đang dọn dẹp..."));
        int skipped = 0;
        foreach (var it in chosen) skipped += await Task.Run(() => Cleaner.Clean(it));
        long freed = Math.Max(0, Cleaner.FreeC() - before);
        Cleaner.Log(L.T("Manual cleanup: freed ", "Dọn thủ công: giải phóng ") + Cleaner.Fmt(freed));
        await Scan();
        lblStatus.Text = L.T("Done! Freed about ", "Dọn xong! Đã giải phóng khoảng ") + Cleaner.Fmt(freed) + "." +
            (skipped > 0 ? "\n" + skipped + L.T(" files are in use by other software and were kept (normal).", " file đang được phần mềm khác dùng nên được giữ lại (bình thường).") : "");
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
                string when = settings.Frequency == 3 ? L.T("at every startup (about 30 seconds after sign-in)", "mỗi khi bật máy (khoảng 30 giây sau khi đăng nhập)") : cboFreq.Text.ToLower() + L.T(" at ", " lúc ") + settings.Time;
                MessageBox.Show(L.T("Automatic cleanup enabled ", "Đã bật tự động dọn dẹp ") + when + L.T(".\nIf the PC is off at that time, the app cleans as soon as you turn it back on.", ".\nNếu đúng giờ đó máy đang tắt, app sẽ dọn ngay khi bạn bật máy lại."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Scheduler.Disable();
                MessageBox.Show(L.T("Saved. Automatic cleanup is OFF.", "Đã lưu. Tự động dọn dẹp đang TẮT."), Text);
            }
        }
        catch (Exception ex) { MessageBox.Show(L.T("Error: ", "Lỗi: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
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
        Cleaner.Log(L.T("Automatic: freed ", "Tự động: giải phóng ") + Cleaner.Fmt(freed));

        if (!s.Notify) return;
        using (var tray = new NotifyIcon { Icon = SystemIcons.Information, Visible = true, Text = Cleaner.AppName })
        {
            tray.ShowBalloonTip(8000, Cleaner.AppName, L.T("Cleaned up automatically, freed ", "Đã tự động dọn dẹp, giải phóng ") + Cleaner.Fmt(freed) + L.T(".\nDrive C has free: ", ".\nỔ C còn trống ") + Cleaner.Fmt(Cleaner.FreeC()) + ".", ToolTipIcon.Info);
            var end = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(100); }
            tray.Visible = false;
        }
    }

    static void WriteReport(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("== 1. SAFE TO DELETE (the app cleans these)", "== 1. XOÁ AN TOÀN (app tự dọn)"));
        foreach (var it in Cleaner.AllItems()) sb.AppendLine(Cleaner.Fmt(Cleaner.Measure(it)).PadLeft(10) + "  " + it.Name);
        sb.AppendLine().AppendLine(L.T("== 2. NEEDS YOUR REVIEW", "== 2. CẦN BẠN XEM XÉT"));
        foreach (var f in Analyzer.FindReview()) sb.AppendLine(Cleaner.Fmt(f.Size).PadLeft(10) + "  " + f.Path + "  ->  " + f.Reason);
        sb.AppendLine().AppendLine(L.T("== 4. LONG-UNUSED FOLDERS", "== 4. THƯ MỤC LÂU KHÔNG DÙNG"));
        foreach (var r in Analyzer.RateFolders()) sb.AppendLine(Cleaner.Fmt(r.Size).PadLeft(10) + "  " + r.LastUsed.ToString("dd/MM/yyyy") + "  " + r.Path + "  ->  " + r.Rating);
        sb.AppendLine().AppendLine(L.T("== 3. KEEP", "== 3. CẦN GIỮ"));
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
        // /serve: trạm phát chạy ngầm, mở ổ cắm HTTP cho mọi agent / công cụ
        if (args.Any(a => a.Equals("/serve", StringComparison.OrdinalIgnoreCase))) { PowerStation.Serve(); return; }
        // Sao lưu / khôi phục bộ não bằng dòng lệnh, in kết quả ra stdout:
        //   /backup-setup <repo> <mật khẩu>   /backup   /backup-verify   /restore <repo> <mật khẩu hoặc mã khôi phục>
        if (args.Length > 0 && new[] { "/backup-prepare", "/backup-setup", "/backup", "/backup-verify", "/restore", "/backup-plain" }.Contains(args[0].ToLowerInvariant()))
        {
            using (var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
                try
                {
                    switch (args[0].ToLowerInvariant())
                    {
                        case "/backup-prepare": Backup.PrepareRepo(args.Length > 1 ? args[1] : "bentar-second-brain-backup", o.WriteLine); break;
                        case "/backup-setup": { var c = Backup.Setup(args[1], args.Length > 2 ? args[2] : null); o.WriteLine(c == null ? L.T("Backed up (not encrypted).", "Đã sao lưu (không mã hoá).") : L.T("RECOVERY CODE (write it down, shown only once): ", "MÃ KHÔI PHỤC (chép lại, chỉ hiện một lần): ") + c); break; }
                        case "/backup": o.WriteLine(Backup.Run(true)); break;
                        case "/backup-verify": o.WriteLine(Backup.Verify()); break;
                        case "/restore": o.WriteLine(Backup.Restore(args[1], args.Length > 2 ? args[2] : null)); break;
                        case "/backup-plain": o.WriteLine(Backup.DisableEncryption()); break;
                    }
                }
                catch (Exception ex) { o.WriteLine(L.T("Error: ", "Lỗi: ") + ex.Message); Environment.Exit(1); }
            return;
        }
        // /install: cài vào máy (lối tắt, Settings → Apps); /uninstall [/quiet]: gỡ, giữ nguyên dữ liệu người dùng
        if (args.Any(a => a.Equals("/install", StringComparison.OrdinalIgnoreCase))) { Installer.Install(); return; }
        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))) { Installer.Uninstall(args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase))); return; }
        if (args.Any(a => a.Equals("/index", StringComparison.OrdinalIgnoreCase))) { Brain.Build(); return; }
        // /connect: cài app vào AppData và kết nối Second Brain với mọi agent trên máy, in kết quả ra stdout
        if (args.Any(a => a.Equals("/connect", StringComparison.OrdinalIgnoreCase)))
        {
            using (var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
            {
                try { if (!Installer.IsInstalled) Installer.Install(); o.WriteLine(L.T("App installed: ", "App đã cài: ") + Installer.Exe); }
                catch (Exception ex) { o.WriteLine(L.T("Install error: ", "Lỗi cài app: ") + ex.Message); Environment.Exit(1); }
                foreach (var l in AgentLinks.ConnectAll(false)) o.WriteLine(l);
            }
            return;
        }
        // Bị mở bằng quyền Administrator: mở lại ở chế độ thường (qua Explorer). Chạy Admin thì Windows không cho đọc
        // đăng nhập GitHub / Git của người dùng, nên sao lưu và các công cụ khác hỏng. Thêm /admin nếu thật sự cần quyền Admin.
        if (!args.Any(a => a.Equals("/admin", StringComparison.OrdinalIgnoreCase)) &&
            new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            try { Process.Start("explorer.exe", "\"" + Application.ExecutablePath + "\""); return; } catch { }
        }
        if (Installer.OfferOnStart()) return;   // đã chuyển sang mở bản vừa cài
        Installer.RefreshRegistration();
        Installer.CleanupOld();
        if (L.On("station", true)) PowerStation.EnsureRunning(Installer.IsInstalled ? Installer.Exe : Application.ExecutablePath);
        Application.Run(new MainForm());
    }
}

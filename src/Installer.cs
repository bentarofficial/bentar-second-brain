// Cài đặt / gỡ cài đặt cho riêng người dùng hiện tại (không cần quyền Admin)
// Cài: chép app vào %LOCALAPPDATA%\Programs\Bentar Second Brain, tạo lối tắt Start Menu + Desktop, hiện trong Settings → Apps.
// Gỡ: chỉ xoá những gì app tự cài. KHÔNG BAO GIỜ xoá file của người dùng; cài đặt, nhật ký, chỉ mục cũng được giữ lại.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

static class Installer
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static readonly string Dir = Path.Combine(Local, "Programs", Cleaner.AppName);
    public static readonly string Exe = Path.Combine(Dir, Cleaner.AppName + ".exe");
    static readonly string LegacyExe = Path.Combine(Cleaner.DataDir, "BentarSecondBrain.exe");   // nơi đặt app ở bản 2.4.x trở về trước
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BentarSecondBrain";
    static readonly string StartLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Cleaner.AppName + ".lnk");
    static readonly string DesktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Cleaner.AppName + ".lnk");
    static readonly string StartupLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Cleaner.AppName + " - tram phat.lnk");
    static readonly string NoPromptFile = Path.Combine(Cleaner.DataDir, "khong-hoi-cai-dat");

    public static bool IsInstalled { get { return File.Exists(Exe); } }

    // Trạm phát tự chạy khi bật máy (lối tắt trong thư mục Startup)
    public static bool AutoStart
    {
        get { return File.Exists(StartupLnk); }
        set
        {
            Directory.CreateDirectory(Cleaner.DataDir);
            if (value) { try { File.Delete(NoAutoStartFile); } catch { } if (!IsInstalled) Install(); Shortcut(StartupLnk, "/serve"); }
            else { File.WriteAllText(NoAutoStartFile, ""); if (File.Exists(StartupLnk)) File.Delete(StartupLnk); }
        }
    }
    static readonly string NoAutoStartFile = Path.Combine(Cleaner.DataDir, "khong-tu-chay");
    public static bool RunningInstalled { get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Exe, StringComparison.OrdinalIgnoreCase); } }

    public static Version InstalledVersion
    {
        get { try { return new Version(FileVersionInfo.GetVersionInfo(Exe).FileVersion); } catch { return new Version(0, 0); } }
    }

    // Gọi khi mở giao diện: mời cài nếu chưa cài, hoặc mời nâng bản đã cài nếu file đang chạy mới hơn.
    // Trả về true nếu đã chuyển sang chạy bản đã cài (khi đó bản hiện tại nên thoát).
    public static bool OfferOnStart()
    {
        if (RunningInstalled) return false;
        if (!IsInstalled)
        {
            if (File.Exists(NoPromptFile)) return false;
            var r = MessageBox.Show(L.T("Install Bentar Second Brain on this PC?\n\n• Shortcuts in the Start Menu and on the Desktop\n• Listed in Settings → Apps, uninstalls like any app\n• The cleanup schedule and agents share one copy that updates itself\n\nChoose \"No\" to run without installing (you won't be asked again; you can still install from the app).", "Cài Bentar Second Brain vào máy?\n\n• Có lối tắt ở Start Menu và Desktop\n• Hiện trong Settings → Apps, gỡ được như app bình thường\n• Lịch dọn tự động và các agent dùng chung một bản, tự cập nhật\n\nChọn \"No\" để chạy tạm không cài (sẽ không hỏi lại; vẫn cài được bằng nút trong app)."),
                Cleaner.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.No) { try { Directory.CreateDirectory(Cleaner.DataDir); File.WriteAllText(NoPromptFile, ""); } catch { } return false; }
            if (r != DialogResult.Yes) return false;
        }
        else
        {
            if (Updater.Current <= InstalledVersion) return false;
            if (MessageBox.Show(L.T("Version ", "Máy đang cài bản ") + InstalledVersion.ToString(3) + L.T(" is installed. Update to ", ". Cập nhật lên bản ") + Updater.Current.ToString(3) + L.T(" now?", " này?"),
                    Cleaner.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        }
        try { Install(); }
        catch (Exception ex) { MessageBox.Show(L.T("Installation failed:\n", "Cài đặt không thành công:\n") + ex.Message, Cleaner.AppName); return false; }
        Process.Start(Exe);
        return true;
    }

    public static void Install()
    {
        Directory.CreateDirectory(Dir);
        CleanupOld();
        CopyOver(Application.ExecutablePath, Exe);
        Shortcut(StartLnk, "");
        Shortcut(DesktopLnk, "");
        if (!File.Exists(NoAutoStartFile)) Shortcut(StartupLnk, "/serve");   // trạm phát tự chạy ngầm mỗi khi bật máy (trừ khi người dùng đã tắt)
        Register();
        try { File.Delete(NoPromptFile); } catch { }

        // Chuyển lịch tự động và các agent đang dùng bản cũ trong AppData sang bản đã cài
        try { if (Scheduler.Exists()) Scheduler.Enable(Settings.Load()); } catch { }
        try { AgentLinks.ConnectAll(true); } catch { }
        if (File.Exists(LegacyExe))
        {
            try { File.Delete(LegacyExe); }
            catch { try { File.Move(LegacyExe, LegacyExe + "." + DateTime.Now.Ticks + ".old"); } catch { } }
        }
        Cleaner.Log(L.T("Installed Bentar Second Brain ", "Đã cài Bentar Second Brain ") + Updater.Current.ToString(3) + L.T(" to ", " vào ") + Dir);
        PowerStation.EnsureRunning(Exe);
    }

    // Chép đè; nếu file đích đang chạy (agent đang dùng /mcp) thì Windows chỉ cho đổi tên, nên đổi tên bản cũ sang .old rồi chép
    public static void CopyOver(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(target) && SameFile(source, target)) return;
        try { File.Copy(source, target, true); }
        catch (IOException)
        {
            File.Move(target, target + "." + DateTime.Now.Ticks + ".old");
            File.Copy(source, target, false);
        }
    }

    public static void CleanupOld()
    {
        foreach (var d in new[] { Dir, Cleaner.DataDir })
            try { foreach (var f in Directory.GetFiles(d, "*.old")) try { File.Delete(f); } catch { } } catch { }
    }

    static bool SameFile(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var sa = File.Open(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sb = File.Open(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            return sha.ComputeHash(sa).SequenceEqual(sha.ComputeHash(sb));
    }

    static void Shortcut(string lnk, string arguments)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        var st = sc.GetType();
        Action<string, object> set = (k, v) => st.InvokeMember(k, BindingFlags.SetProperty, null, sc, new[] { v });
        set("TargetPath", Exe);
        set("Arguments", arguments);
        if (arguments.Length > 0) set("WindowStyle", 7);
        set("WorkingDirectory", Dir);
        set("IconLocation", Exe + ",0");
        set("Description", L.T("PC cleanup and shared memory for agents", "Dọn dẹp máy và bộ nhớ chung cho các agent"));
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    static void Register()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", Cleaner.AppName);
            k.SetValue("DisplayVersion", Updater.Current.ToString(3));
            k.SetValue("Publisher", "Bentar");
            k.SetValue("DisplayIcon", Exe + ",0");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", "\"" + Exe + "\" /uninstall");
            k.SetValue("URLInfoAbout", "https://github.com/" + Updater.Repo);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
    }

    // Cập nhật số phiên bản hiện trong Settings → Apps sau khi app tự cập nhật
    public static void RefreshRegistration()
    {
        try
        {
            if (!RunningInstalled) return;
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true))
                if (k != null && (k.GetValue("DisplayVersion") as string) != Updater.Current.ToString(3)) k.SetValue("DisplayVersion", Updater.Current.ToString(3));
        }
        catch { }
    }

    public static void Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show(L.T("Uninstall Bentar Second Brain?\n\nThe app only removes what it installed: program files, shortcuts, the cleanup schedule and agent connections.\n\n", "Gỡ Bentar Second Brain khỏi máy?\n\nApp chỉ xoá những gì nó đã cài: file chương trình, lối tắt, lịch dọn tự động và kết nối với các agent.\n\n") +
                L.T("Your files, documents and projects are NOT touched. Settings, logs and the Second Brain index are kept in:\n", "File, tài liệu, dự án của bạn KHÔNG bị đụng tới. Cài đặt, nhật ký và chỉ mục Second Brain được giữ lại ở:\n") + Cleaner.DataDir,
                Cleaner.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        try { Scheduler.Disable(); } catch { }
        try { AgentLinks.DisconnectAll(); } catch { }
        foreach (var l in new[] { StartLnk, DesktopLnk, StartupLnk }) try { File.Delete(l); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        Cleaner.Log(L.T("Uninstalled Bentar Second Brain (kept settings, logs, index)", "Đã gỡ Bentar Second Brain (giữ lại cài đặt, nhật ký, chỉ mục)"));

        // File .exe có thể đang chạy (chính app này, hoặc agent dùng /mcp): một PowerShell chạy ngầm chờ app thoát,
        // dừng các tiến trình chạy từ đúng các file chương trình, rồi xoá ĐÚNG các file đó (không xoá thư mục theo kiểu đệ quy).
        Func<string, string> q = s => "'" + s.Replace("'", "''") + "'";
        var targets = new[] { Exe, LegacyExe };
        string script =
            "$p=" + Process.GetCurrentProcess().Id + "; while(Get-Process -Id $p -ErrorAction SilentlyContinue){Start-Sleep -Milliseconds 300};" +
            "$t=@(" + string.Join(",", targets.Select(q)) + ");" +
            "Get-Process | Where-Object { $t -contains $_.Path } | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500;" +
            "foreach($f in $t){ Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue };" +
            "foreach($d in @(" + q(Dir) + "," + q(Cleaner.DataDir) + ")){ Get-ChildItem -LiteralPath $d -Filter '*.old' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue };" +
            "if((Test-Path -LiteralPath " + q(Dir) + ") -and -not (Get-ChildItem -LiteralPath " + q(Dir) + " -Force)){ Remove-Item -LiteralPath " + q(Dir) + " -Force }";
        Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            { CreateNoWindow = true, UseShellExecute = false });
        if (!quiet) MessageBox.Show(L.T("Bentar Second Brain was uninstalled.\nYour data is kept intact.", "Đã gỡ Bentar Second Brain.\nDữ liệu của bạn được giữ nguyên."), Cleaner.AppName);
    }
}

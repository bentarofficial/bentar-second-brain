// Giữ bộ não cả đời: sao lưu mã hoá lên một repo Git riêng tư (GitHub), mỗi lần sao lưu là một phiên bản.
// - Mã hoá theo đúng định dạng của OpenSSL để không phụ thuộc app này:
//     openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -in <file> -out <ra> -pass pass:<mã>
// - bo-nao.sbak: gói zip (trí nhớ JSON + bản Markdown đọc được + cài đặt), mã hoá bằng MÃ KHÔI PHỤC
// - khoa.enc: mã khôi phục, mã hoá bằng MẬT KHẨU của người dùng → chỉ cần một trong hai là mở được
// - Trên máy, mã khôi phục được Windows khoá theo tài khoản (DPAPI) để tự sao lưu không cần hỏi mật khẩu
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

static class Backup
{
    public static readonly string RepoDir = Path.Combine(Cleaner.DataDir, "sao-luu");
    static readonly string StatusFile = Path.Combine(Cleaner.DataDir, "sao-luu.json");
    static readonly string KeyFile = Path.Combine(Cleaner.DataDir, "khoa-sao-luu.bin");
    const string DataName = "bo-nao.sbak", KeyName = "khoa.enc";
    const int Iterations = 200000;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly object gate = new object();

    // Hai kiểu sao lưu: thường (mặc định, chỉ cần đăng nhập GitHub để khôi phục) hoặc mã hoá thêm bằng mật khẩu (tuỳ chọn)
    public static bool Configured { get { return Directory.Exists(Path.Combine(RepoDir, ".git")) && (Status().Plain || File.Exists(KeyFile)); } }
    public static bool Encrypted { get { return Configured && !Status().Plain; } }
    const string PlainName = "tri-nho.json";

    // ---------- Trạng thái ----------
    public class State { public string Repo, LastOk, LastError, LastVerified, LastHash; public bool Plain; }

    public static State Status()
    {
        try { if (File.Exists(StatusFile)) return Json.Deserialize<State>(File.ReadAllText(StatusFile, Encoding.UTF8)) ?? new State(); } catch { }
        return new State();
    }
    static void Save(State s) { Directory.CreateDirectory(Cleaner.DataDir); File.WriteAllText(StatusFile, Json.Serialize(s), new UTF8Encoding(false)); }

    // Có cần báo đỏ không: chưa thiết lập, lỗi, hoặc quá 3 ngày chưa sao lưu được
    public static string Warning()
    {
        if (!Configured) return L.T("The brain is not backed up – lose the PC and you lose the memories.", "Bộ não chưa được sao lưu – mất máy là mất trí nhớ.");
        var s = Status();
        if (!string.IsNullOrEmpty(s.LastError)) return L.T("Backup error: ", "Sao lưu lỗi: ") + s.LastError;
        DateTime ok;
        if (s.LastOk == null || !DateTime.TryParse(s.LastOk, out ok) || (DateTime.Now - ok).TotalDays > 3) return L.T("The brain has not been backed up for over 3 days.", "Đã hơn 3 ngày chưa sao lưu được bộ não.");
        return null;
    }

    // ---------- Mã hoá chuẩn OpenSSL (Salted__ + PBKDF2-SHA256 + AES-256-CBC) ----------
    public static byte[] Encrypt(byte[] data, string pass)
    {
        var salt = new byte[8];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
        using (var kdf = new Rfc2898DeriveBytes(pass, salt, Iterations, HashAlgorithmName.SHA256))
        using (var aes = Aes.Create())
        {
            var km = kdf.GetBytes(48);
            aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            aes.Key = km.Take(32).ToArray(); aes.IV = km.Skip(32).Take(16).ToArray();
            using (var ms = new MemoryStream())
            {
                ms.Write(Encoding.ASCII.GetBytes("Salted__"), 0, 8);
                ms.Write(salt, 0, 8);
                using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write)) cs.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }
    }

    public static byte[] Decrypt(byte[] blob, string pass)
    {
        if (blob.Length < 16 || Encoding.ASCII.GetString(blob, 0, 8) != "Salted__") throw new InvalidDataException(L.T("Not a valid backup file.", "Không phải file sao lưu hợp lệ."));
        var salt = blob.Skip(8).Take(8).ToArray();
        using (var kdf = new Rfc2898DeriveBytes(pass, salt, Iterations, HashAlgorithmName.SHA256))
        using (var aes = Aes.Create())
        {
            var km = kdf.GetBytes(48);
            aes.KeySize = 256; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            aes.Key = km.Take(32).ToArray(); aes.IV = km.Skip(32).Take(16).ToArray();
            try
            {
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(new MemoryStream(blob, 16, blob.Length - 16), aes.CreateDecryptor(), CryptoStreamMode.Read)) cs.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (CryptographicException) { throw new UnauthorizedAccessException(L.T("Wrong password or recovery code.", "Sai mật khẩu hoặc mã khôi phục.")); }
        }
    }

    // Mã khôi phục: 30 ký tự dễ chép tay, chia nhóm 5 (khoảng 150 bit ngẫu nhiên)
    static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var b = new byte[30];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        var sb = new StringBuilder();
        for (int i = 0; i < 30; i++) { if (i > 0 && i % 5 == 0) sb.Append('-'); sb.Append(alphabet[b[i] % 32]); }
        return sb.ToString();
    }

    static string LocalCode()
    {
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser));
    }
    static void StoreLocalCode(string code)
    {
        Directory.CreateDirectory(Cleaner.DataDir);
        File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(code), null, DataProtectionScope.CurrentUser));
    }

    // ---------- Git ----------
    static Tuple<int, string> Git(string args, string dir, int timeoutMs = 180000)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir ?? Cleaner.DataDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return Tuple.Create(-1, L.T("timed out", "quá thời gian")); }
            return Tuple.Create(p.ExitCode, (o + err.Result).Trim());
        }
    }

    public static bool GitAvailable() { try { return Git("--version", null, 10000).Item1 == 0; } catch { return false; } }

    static bool GhAvailable()
    {
        try { var psi = new ProcessStartInfo("cmd.exe", "/c gh --version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }; using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); return p.ExitCode == 0; } }
        catch { return false; }
    }

    static Tuple<int, string> Gh(string args, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c gh " + args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return Tuple.Create(-1, L.T("timed out", "quá thời gian")); }
            return Tuple.Create(p.ExitCode, (o + "\n" + err.Result).Trim());
        }
    }

    public static bool HasGh { get { return GhAvailable(); } }
    // Đã đăng nhập chưa: xem cả nội dung trả về (gh có thể báo lỗi ở một tài khoản phụ dù tài khoản chính vẫn đăng nhập)
    public static bool GhLoggedIn()
    {
        try
        {
            var r = Gh("auth status --hostname github.com", 45000);
            return r.Item1 == 0 || r.Item2.IndexOf("Logged in to github.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    // Mở cửa sổ đăng nhập GitHub (người dùng tự đăng nhập trên trình duyệt), rồi cho Git dùng chung đăng nhập đó.
    // Chờ tối đa 10 phút; quá thì bỏ để hộp thoại không treo mãi.
    public static void GhLogin()
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c title Dang nhap GitHub cho Bentar Second Brain & echo Buoc 1: nhan Enter de mo trang github.com/login/device. & echo Buoc 2: dan ma ben duoi (da tu chep, bam Ctrl+V), bam Continue roi Authorize. & echo. & gh auth login --web --git-protocol https --hostname github.com & gh auth setup-git")
            { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
        if (!p.WaitForExit(10 * 60 * 1000)) { try { p.Kill(); } catch { } throw new TimeoutException(L.T("GitHub sign-in was not completed (over 10 minutes).", "Chưa đăng nhập GitHub xong (quá 10 phút).")); }
        if (!GhLoggedIn()) throw new InvalidOperationException(L.T("Could not sign in to GitHub.", "Chưa đăng nhập được GitHub."));
    }

    // Repo đã chứa bộ não chưa: liệt kê file ở thư mục gốc và tìm đúng tên khoa.enc
    // (không dựa vào mã thoát của "gh api", vốn không đáng tin khi chạy qua cmd)
    static bool HasBrain(string fullName)
    {
        var r = Gh("api repos/" + fullName + "/contents --jq \".[].name\"", 30000);
        return r.Item1 == 0 && r.Item2.Split('\n').Any(l => l.Trim() == KeyName || l.Trim() == PlainName);
    }

    public class RepoInfo { public string Name, Url; public bool IsBackup; public override string ToString() { return (IsBackup ? "★ " : "") + Name; } }

    // Các repo riêng tư của người dùng; repo nào có bản sao lưu bộ não thì đánh dấu và xếp lên đầu
    public static List<RepoInfo> ListRepos()
    {
        var r = Gh("repo list --visibility private --limit 300 --json nameWithOwner,url", 60000);
        if (r.Item1 != 0) throw new InvalidOperationException(L.T("Could not read the repo list: ", "Không đọc được danh sách repo: ") + r.Item2);
        var json = r.Item2.Substring(r.Item2.IndexOf('['));
        json = json.Substring(0, json.LastIndexOf(']') + 1);
        var list = Json.Deserialize<List<Dictionary<string, object>>>(json)
            .Select(d => new RepoInfo { Name = (string)d["nameWithOwner"], Url = (string)d["url"] + ".git" }).ToList();
        // Kiểm tra thật (có file khoa.enc) với các repo có tên giống bản sao lưu, để không phải gọi cho cả trăm repo
        foreach (var repo in list.Where(x => x.Name.IndexOf("brain", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("sao-luu", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0).Take(15))
            repo.IsBackup = HasBrain(repo.Name);
        return list.OrderByDescending(x => x.IsBackup).ThenBy(x => x.Name).ToList();
    }

    // Chuẩn bị repo sao lưu trên GitHub, từng bước có báo cáo (dùng cho nút trong app và lệnh /backup-prepare):
    // kiểm tra Git, gh, đăng nhập → tạo repo riêng tư (hoặc dùng lại nếu đã có và còn trống) → cho Git dùng đăng nhập gh
    // → thử clone, commit README và đẩy lên. Trả về địa chỉ repo; lỗi ở bước nào thì ném ra kèm tên bước đó.
    public static string PrepareRepo(string name, Action<string> log)
    {
        Action<string> say = s => { if (log != null) log(s); };
        say("1. Git: " + (GitAvailable() ? L.T("yes", "có") : L.T("NO", "KHÔNG")));
        if (!GitAvailable()) throw new InvalidOperationException(L.T("Step 1: Git is not installed (git-scm.com).", "Bước 1: máy chưa cài Git (git-scm.com)."));
        say("2. GitHub CLI: " + (GhAvailable() ? L.T("yes", "có") : L.T("NO", "KHÔNG")));
        if (!GhAvailable()) throw new InvalidOperationException(L.T("Step 2: GitHub CLI (gh) is not installed. Create a private repo on github.com and choose \"Use an existing repo\".", "Bước 2: máy chưa có GitHub CLI (gh). Hãy tạo repo riêng tư trên github.com rồi chọn \"Dùng repo có sẵn\"."));
        var st = Gh("auth status --hostname github.com", 45000);
        bool logged = st.Item1 == 0 || st.Item2.IndexOf("Logged in to github.com", StringComparison.OrdinalIgnoreCase) >= 0;
        say(L.T("3. GitHub sign-in: ", "3. Đăng nhập GitHub: ") + (logged ? L.T("signed in", "đã đăng nhập") : L.T("NO", "CHƯA")) + L.T(" (exit code ", " (mã thoát ") + st.Item1 + ")");
        if (!logged) throw new InvalidOperationException(L.T("Step 3: not signed in to GitHub: ", "Bước 3: chưa đăng nhập GitHub: ") + st.Item2);

        var who = Gh("api user -q .login", 30000);
        string owner = who.Item2.Split('\n')[0].Trim();
        if (who.Item1 != 0 || owner.Length == 0) throw new InvalidOperationException(L.T("Step 4: could not read the GitHub account: ", "Bước 4: không đọc được tài khoản GitHub: ") + who.Item2);
        string full = name.Contains("/") ? name : owner + "/" + name;
        say(L.T("4. Account: ", "4. Tài khoản: ") + owner + " · repo: " + full);

        var view = Gh("repo view " + full + " --json url,visibility", 30000);
        if (view.Item1 == 0)
        {
            if (view.Item2.IndexOf("PRIVATE", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException(L.T("Step 5: repo ", "Bước 5: repo ") + full + L.T(" exists but is NOT private – it cannot be used for brain backups.", " đã có nhưng KHÔNG riêng tư – không dùng để sao lưu bộ não."));
            if (HasBrain(full)) throw new InvalidOperationException(L.T("Step 5: repo ", "Bước 5: repo ") + full + L.T(" already has a brain. Use \"Restore\".", " đã có bộ não. Hãy dùng \"Khôi phục\"."));
            say(L.T("5. Repo already exists (private, empty) – reusing it.", "5. Repo đã có sẵn (riêng tư, còn trống) – dùng lại."));
        }
        else
        {
            var cr = Gh("repo create " + full + " --private --description \"Sao luu ma hoa cua Bentar Second Brain\"", 60000);
            say(L.T("5. Create private repo: ", "5. Tạo repo riêng tư: ") + (cr.Item1 == 0 ? L.T("done", "xong") : L.T("ERROR", "LỖI")) + " – " + cr.Item2.Replace("\n", " "));
            if (cr.Item1 != 0) throw new InvalidOperationException(L.T("Step 5: could not create the repo: ", "Bước 5: không tạo được repo: ") + cr.Item2);
        }
        string url = "https://github.com/" + full + ".git";

        var sg = Gh("auth setup-git --hostname github.com", 30000);
        say(L.T("6. Let Git use the GitHub sign-in: ", "6. Cho Git dùng đăng nhập GitHub: ") + (sg.Item1 == 0 ? L.T("done", "xong") : L.T("ERROR – ", "LỖI – ") + sg.Item2));

        string tmp = Path.Combine(Path.GetTempPath(), "bsb-thu-repo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cl = Git("clone \"" + url + "\" \"" + tmp + "\"", Path.GetTempPath(), 120000);
            say("7. Clone: " + (cl.Item1 == 0 ? L.T("done", "xong") : L.T("ERROR – ", "LỖI – ") + cl.Item2));
            if (cl.Item1 != 0) throw new InvalidOperationException(L.T("Step 7: could not clone the repo: ", "Bước 7: không clone được repo: ") + cl.Item2);
            if (!File.Exists(Path.Combine(tmp, "README.md")))
            {
                File.WriteAllText(Path.Combine(tmp, "README.md"), RepoReadme, new UTF8Encoding(false));
                Git("add README.md", tmp);
                var cm = Git("-c user.name=\"Bentar Second Brain\" -c user.email=second-brain@localhost commit -m \"Initialize backup repo\"", tmp);
                var ps = Git("push -u origin HEAD", tmp, 120000);
                say(L.T("8. Test push to GitHub: ", "8. Thử đẩy lên GitHub: ") + (ps.Item1 == 0 ? L.T("done", "xong") : L.T("ERROR – ", "LỖI – ") + ps.Item2));
                if (ps.Item1 != 0) throw new InvalidOperationException(L.T("Step 8: could not push to GitHub: ", "Bước 8: không đẩy lên được GitHub: ") + ps.Item2);
            }
            else say(L.T("8. Repo already has a README – skipping the test push.", "8. Repo đã có README – bỏ qua bước thử đẩy."));
        }
        finally { if (Directory.Exists(tmp)) try { DeleteTree(tmp); } catch { } }
        say(L.T("Ready: ", "Sẵn sàng: ") + url);
        return url;
    }

    // Tạo repo GitHub riêng tư bằng GitHub CLI (nếu có); trả về địa chỉ repo
    public static string CreateGithubRepo(string name)
    {
        if (!GhAvailable()) throw new InvalidOperationException(L.T("GitHub CLI (gh) is not installed. Create a private repo on github.com yourself and paste its URL.", "Máy chưa có GitHub CLI (gh). Hãy tự tạo một repo riêng tư trên github.com rồi dán địa chỉ vào."));
        if (!GhLoggedIn()) throw new InvalidOperationException(L.T("Not signed in to GitHub.", "Chưa đăng nhập GitHub."));
        Gh("auth setup-git", 20000);   // để Git đẩy bản sao lưu bằng chính đăng nhập GitHub này
        var psi = new ProcessStartInfo("cmd.exe", "/c gh repo create " + name + " --private --description \"Sao luu ma hoa cua Bentar Second Brain\" && gh repo view " + name + " --json url -q .url")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            var url = o.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("https://"));
            if (url == null) throw new InvalidOperationException(L.T("Could not create the repo: ", "Không tạo được repo: ") + (o + " " + err.Result).Trim());
            return url.EndsWith(".git") ? url : url + ".git";
        }
    }

    // ---------- Gói dữ liệu ----------
    static byte[] Pack(out string hash)
    {
        var mem = File.Exists(Memory.File_) ? File.ReadAllBytes(Memory.File_) : Encoding.UTF8.GetBytes("{}");
        var settings = File.Exists(Cleaner.SettingsFile) ? File.ReadAllBytes(Cleaner.SettingsFile) : new byte[0];
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(mem.Concat(settings).ToArray())).Replace("-", "");
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                Action<string, byte[]> add = (n, b) => { using (var s = zip.CreateEntry(n).Open()) s.Write(b, 0, b.Length); };
                add("tri-nho.json", mem);
                add("tri-nho.md", Encoding.UTF8.GetBytes(Readable()));
                if (settings.Length > 0) add("settings.ini", settings);
                add("thong-tin.txt", Encoding.UTF8.GetBytes("Bentar Second Brain " + Updater.Current.ToString(3) + L.T("\nBacked up at ", "\nSao lưu lúc ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + L.T("\nPC: ", "\nMáy: ") + Environment.MachineName + "\n"));
            }
            return ms.ToArray();
        }
    }

    // Bản Markdown đọc được bằng mắt, không cần app (ghi nhớ theo loại, kèm nguồn và liên kết)
    static string Readable()
    {
        var snap = Memory.Snapshot();
        var byId = snap.Item1.ToDictionary(n => n.Id);
        var sb = new StringBuilder(L.T("# Second brain – readable copy\n\nExported at ", "# Bộ não thứ hai – bản đọc được\n\nXuất lúc ") + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + "\n");
        foreach (var g in snap.Item1.Where(n => n.Kind != "file" && n.Kind != "du-an").GroupBy(n => n.Kind).OrderBy(g => g.Key))
        {
            sb.Append("\n## ").Append(Memory.Label(g.Key)).Append("\n");
            foreach (var n in g.OrderByDescending(n => n.Updated))
            {
                sb.Append("\n### ").Append(n.Title).Append(n.Outdated ? L.T(" (outdated)", " (đã cũ)") : "").Append("\n");
                sb.Append("_").Append(n.Updated.ToLocalTime().ToString("dd/MM/yyyy")).Append(n.Agent != null ? " · " + n.Agent : "").Append(n.Source != null ? L.T(" · source: ", " · nguồn: ") + n.Source : "").Append("_\n\n");
                sb.Append(n.Text).Append("\n");
                var links = snap.Item2.Where(y => y.A == n.Id || y.B == n.Id).Select(y => y.A == n.Id ? y.B : y.A).Where(byId.ContainsKey).Select(id => byId[id].Title).Take(8).ToList();
                if (links.Count > 0) sb.Append(L.T("\nRelated: ", "\nLiên quan: ")).Append(string.Join(" · ", links)).Append("\n");
            }
        }
        return sb.ToString();
    }

    static string PlainReadme { get { return
        L.T("# Bentar Second Brain backup\n\n", "# Sao lưu Bentar Second Brain\n\n") +
        L.T("This PRIVATE repo holds the second brain. The commit history is the brain's versions over time.\n\n", "Repo RIÊNG TƯ này giữ bộ não thứ hai. Lịch sử commit là các phiên bản của bộ não theo thời gian.\n\n") +
        L.T("- `tri-nho.json`: all memories (memories, links) – the source data for restoring.\n", "- `tri-nho.json`: toàn bộ trí nhớ (ghi nhớ, liên kết) – dữ liệu gốc để khôi phục.\n") +
        L.T("- `tri-nho.md`: a human-readable copy.\n", "- `tri-nho.md`: bản đọc được bằng mắt.\n") +
        L.T("- `settings.ini`: app settings.\n\n", "- `settings.ini`: cài đặt của app.\n\n") +
        L.T("Restore: install Bentar Second Brain, choose \"Restore\", sign in to GitHub and pick this repo.\n", "Khôi phục: cài Bentar Second Brain, chọn \"Khôi phục bộ não\", đăng nhập GitHub và chọn repo này.\n") +
        L.T("Note: the data is not encrypted – anyone with access to the GitHub account or this repo can read it. Keep the repo private.\n", "Lưu ý: dữ liệu không mã hoá – ai vào được tài khoản GitHub hoặc repo này đều đọc được. Giữ repo ở chế độ riêng tư.\n"); } }

    static string RepoReadme { get { return
        L.T("# Bentar Second Brain backup\n\n", "# Sao lưu Bentar Second Brain\n\n") +
        L.T("This repo only holds ENCRYPTED data. The commit history is the brain's versions over time.\n\n", "Repo này chỉ chứa dữ liệu ĐÃ MÃ HOÁ. Lịch sử commit là các phiên bản của bộ não theo thời gian.\n\n") +
        L.T("- `bo-nao.sbak`: a zip (tri-nho.json, readable tri-nho.md, settings.ini) encrypted with the RECOVERY CODE.\n", "- `bo-nao.sbak`: gói zip (tri-nho.json, tri-nho.md đọc được, settings.ini), mã hoá bằng MÃ KHÔI PHỤC.\n") +
        L.T("- `khoa.enc`: the recovery code, encrypted with your PASSWORD.\n\n", "- `khoa.enc`: mã khôi phục, mã hoá bằng MẬT KHẨU của bạn.\n\n") +
        L.T("## Restore\n\n", "## Khôi phục\n\n") +
        L.T("Easiest: install Bentar Second Brain, choose \"Restore\", paste this repo's URL and enter the password or recovery code.\n\n", "Cách dễ nhất: cài Bentar Second Brain, chọn \"Khôi phục bộ não\", dán địa chỉ repo này và nhập mật khẩu hoặc mã khôi phục.\n\n") +
        L.T("Without the app you can still open it with OpenSSL (standard format: Salted__ + PBKDF2-SHA256 200000 iterations + AES-256-CBC):\n\n", "Không có app vẫn mở được bằng OpenSSL (định dạng chuẩn: Salted__ + PBKDF2-SHA256 200000 vòng + AES-256-CBC):\n\n") +
        "```\n" +
        L.T("# 1. Get the recovery code from the password (skip if you have the recovery code)\n", "# 1. Lấy mã khôi phục từ mật khẩu (bỏ qua nếu bạn đã có mã khôi phục)\n") +
        L.T("openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -in khoa.enc -pass pass:\"PASSWORD\"\n", "openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -in khoa.enc -pass pass:\"MẬT KHẨU\"\n") +
        L.T("# 2. Decrypt the brain with the recovery code, then unzip it\n", "# 2. Giải mã bộ não bằng mã khôi phục, rồi giải nén file zip\n") +
        L.T("openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -in bo-nao.sbak -out bo-nao.zip -pass pass:\"RECOVERY-CODE\"\n", "openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -in bo-nao.sbak -out bo-nao.zip -pass pass:\"MÃ-KHÔI-PHỤC\"\n") +
        "```\n"; } }

    // ---------- Thiết lập lần đầu ----------
    // Trả về MÃ KHÔI PHỤC (chỉ hiện một lần cho người dùng chép lại)
    /// <summary>Người dùng có thể nhập tên repo ("ten-repo"), "chu/ten-repo", "github.com/chu/ten-repo" hoặc địa chỉ đầy đủ.</summary>
    public static string ResolveRepo(string input)
    {
        var s = (input ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) throw new ArgumentException(L.T("Enter the repo name or address.", "Hãy nhập tên hoặc địa chỉ repo."));
        if (s.Contains("://") || s.StartsWith("git@")) return s;
        if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) return "https://" + s + (s.EndsWith(".git") ? "" : ".git");
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\w.-]+/[\w.-]+$")) return "https://github.com/" + s + (s.EndsWith(".git") ? "" : ".git");
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\w.-]+$"))
        {
            var user = HasGh ? GhUser() : null;
            if (user == null) throw new ArgumentException(L.T("Sign in to GitHub first, or paste the full address (https://github.com/<you>/", "Hãy đăng nhập GitHub trước, hoặc dán địa chỉ đầy đủ (https://github.com/<tên-bạn>/") + s + ").");
            return "https://github.com/" + user + "/" + s + (s.EndsWith(".git") ? "" : ".git");
        }
        return s;   // đường dẫn thư mục / dạng khác: để git tự xử lý
    }

    public static string Setup(string repoUrl, string password)
    {
        repoUrl = ResolveRepo(repoUrl);
        bool plain = string.IsNullOrEmpty(password);
        if (!plain && password.Length < 8) throw new ArgumentException(L.T("The password needs at least 8 characters.", "Mật khẩu cần ít nhất 8 ký tự."));
        if (!GitAvailable()) throw new InvalidOperationException(L.T("Git is not installed (git-scm.com).", "Máy chưa cài Git (git-scm.com)."));
        lock (gate)
        {
            if (Directory.Exists(RepoDir)) DeleteTree(RepoDir);
            var r = Git("clone \"" + repoUrl + "\" \"" + RepoDir + "\"", Cleaner.DataDir);
            if (r.Item1 != 0) throw new InvalidOperationException(L.T("Could not fetch the repo: ", "Không lấy được repo: ") + r.Item2);
            if (File.Exists(Path.Combine(RepoDir, KeyName)) || File.Exists(Path.Combine(RepoDir, DataName)) || File.Exists(Path.Combine(RepoDir, PlainName)))
                throw new InvalidOperationException(L.T("This repo already has a backup. Use \"Restore\" instead of setting up again.", "Repo này đã có bản sao lưu. Hãy dùng \"Khôi phục\" thay vì thiết lập mới."));
            if (plain)
            {
                File.WriteAllText(Path.Combine(RepoDir, "README.md"), PlainReadme, new UTF8Encoding(false));
                if (File.Exists(KeyFile)) File.Delete(KeyFile);
                var sp = Status(); sp.Repo = repoUrl; sp.LastHash = null; sp.LastError = null; sp.Plain = true; Save(sp);
                Run(true);
                var ap = Status();
                if (!string.IsNullOrEmpty(ap.LastError)) throw new InvalidOperationException(ap.LastError);
                return null;   // sao lưu thường: không có mã khôi phục
            }
            var code = NewRecoveryCode();
            File.WriteAllBytes(Path.Combine(RepoDir, KeyName), Encrypt(Encoding.UTF8.GetBytes(code), password));
            File.WriteAllText(Path.Combine(RepoDir, "README.md"), RepoReadme, new UTF8Encoding(false));
            StoreLocalCode(code);
            var st = Status(); st.Repo = repoUrl; st.LastHash = null; st.LastError = null; st.Plain = false; Save(st);
            Run(true);
            var after = Status();
            if (!string.IsNullOrEmpty(after.LastError)) throw new InvalidOperationException(after.LastError);
            return code;
        }
    }

    // ---------- Sao lưu ----------
    // Chỉ commit khi trí nhớ thực sự thay đổi (hoặc force). Trả về mô tả kết quả.
    public static string Run(bool force)
    {
        if (!Configured) return L.T("Backup is not set up.", "Chưa thiết lập sao lưu.");
        lock (gate)
        {
            var st = Status();
            try
            {
                string hash;
                var packed = Pack(out hash);
                if (!force && hash == st.LastHash && string.IsNullOrEmpty(st.LastError)) return L.T("Nothing new.", "Không có gì mới.");
                if (st.Plain)
                {
                    File.WriteAllBytes(Path.Combine(RepoDir, PlainName), File.Exists(Memory.File_) ? File.ReadAllBytes(Memory.File_) : Encoding.UTF8.GetBytes("{}"));
                    File.WriteAllText(Path.Combine(RepoDir, "tri-nho.md"), Readable(), new UTF8Encoding(false));
                    if (File.Exists(Cleaner.SettingsFile)) File.Copy(Cleaner.SettingsFile, Path.Combine(RepoDir, "settings.ini"), true);
                    foreach (var old in new[] { DataName, KeyName }) if (File.Exists(Path.Combine(RepoDir, old))) File.Delete(Path.Combine(RepoDir, old));
                    File.WriteAllText(Path.Combine(RepoDir, "README.md"), PlainReadme, new UTF8Encoding(false));
                }
                else
                {
                    File.WriteAllBytes(Path.Combine(RepoDir, DataName), Encrypt(packed, LocalCode()));
                    if (!File.Exists(Path.Combine(RepoDir, "README.md"))) File.WriteAllText(Path.Combine(RepoDir, "README.md"), RepoReadme, new UTF8Encoding(false));
                }
                Git("add -A", RepoDir);
                var c = Git("-c user.name=\"Bentar Second Brain\" -c user.email=second-brain@localhost commit -m \"Backup " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " (" + Environment.MachineName + ")\"", RepoDir);
                if (c.Item1 != 0 && !c.Item2.Contains("nothing to commit")) throw new InvalidOperationException(c.Item2);
                var p = Git("push -u origin HEAD", RepoDir);
                if (p.Item1 != 0) throw new InvalidOperationException(L.T("Could not push: ", "Không đẩy lên được: ") + p.Item2);
                st.LastOk = DateTime.Now.ToString("o"); st.LastHash = hash; st.LastError = null; Save(st);
                Activity.Log("sao lưu", "git", "sao lưu", st.Repo);
                return L.T("Backed up at ", "Đã sao lưu lúc ") + DateTime.Now.ToString("HH:mm dd/MM/yyyy") + ".";
            }
            catch (Exception ex)
            {
                st.LastError = ex.Message.Length > 300 ? ex.Message.Substring(0, 300) : ex.Message; Save(st);
                return L.T("Backup error: ", "Sao lưu lỗi: ") + st.LastError;
            }
        }
    }

    // Tự kiểm tra: mở thử bản sao lưu mới nhất trên repo xem có giải mã và đọc được không
    public static string Verify()
    {
        if (!Configured) return L.T("Backup is not set up.", "Chưa thiết lập sao lưu.");
        lock (gate)
        {
            var st = Status();
            try
            {
                Git("pull --ff-only", RepoDir);
                int count = st.Plain ? CountJson(File.ReadAllText(Path.Combine(RepoDir, PlainName), Encoding.UTF8))
                                     : CountMemories(Decrypt(File.ReadAllBytes(Path.Combine(RepoDir, DataName)), LocalCode()));
                st.LastVerified = DateTime.Now.ToString("o"); Save(st);
                return L.T("The backup opens fine: ", "Bản sao lưu mở được: ") + count + L.T(" memories.", " ghi nhớ.");
            }
            catch (Exception ex) { st.LastError = L.T("Backup verification failed: ", "Kiểm tra bản sao lưu thất bại: ") + ex.Message; Save(st); return st.LastError; }
        }
    }

    static int CountJson(string json)
    {
        var store = Json.Deserialize<MemoryStore>(json);
        return store == null || store.Neurons == null ? 0 : store.Neurons.Count(n => n.Kind != "file" && n.Kind != "du-an");
    }

    static int CountMemories(byte[] zipBytes)
    {
        using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        using (var sr = new StreamReader(zip.GetEntry("tri-nho.json").Open(), Encoding.UTF8))
        {
            var store = Json.Deserialize<MemoryStore>(sr.ReadToEnd());
            return store == null || store.Neurons == null ? 0 : store.Neurons.Count(n => n.Kind != "file" && n.Kind != "du-an");
        }
    }

    // Bỏ mã hoá: từ lần sao lưu này trở đi lưu dạng thường (chỉ cần đăng nhập GitHub). Các phiên bản cũ vẫn mã hoá trong lịch sử.
    public static string DisableEncryption()
    {
        if (!Configured) return L.T("Backup is not set up.", "Chưa thiết lập sao lưu.");
        lock (gate)
        {
            var st = Status(); st.Plain = true; st.LastHash = null; Save(st);
            try { if (File.Exists(KeyFile)) File.Delete(KeyFile); } catch { }
        }
        return Run(true);
    }

    // Gọi định kỳ từ trạm phát: sao lưu khi có thay đổi, kiểm tra mỗi tuần
    public static void Tick()
    {
        if (!Configured) return;
        Run(false);
        var st = Status();
        DateTime v;
        if (st.LastVerified == null || !DateTime.TryParse(st.LastVerified, out v) || (DateTime.Now - v).TotalDays >= 7) Verify();
    }

    // ---------- Khôi phục (máy mới / mất dữ liệu) ----------
    // secret: mật khẩu HOẶC mã khôi phục
    public static string Restore(string repoUrl, string secret)
    {
        repoUrl = ResolveRepo(repoUrl);
        if (!GitAvailable()) throw new InvalidOperationException(L.T("Git is not installed (git-scm.com).", "Máy chưa cài Git (git-scm.com)."));
        lock (gate)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "bsb-khoi-phuc-" + Guid.NewGuid().ToString("N"));
            var r = Git("clone \"" + repoUrl + "\" \"" + tmp + "\"", Path.GetTempPath());
            if (r.Item1 != 0) throw new InvalidOperationException(L.T("Could not fetch the repo: ", "Không lấy được repo: ") + r.Item2);
            try
            {
                var plainFile = Path.Combine(tmp, PlainName);
                if (File.Exists(plainFile))
                {
                    var json = File.ReadAllText(plainFile, Encoding.UTF8);
                    int n = CountJson(json);
                    Directory.CreateDirectory(Cleaner.DataDir);
                    if (File.Exists(Memory.File_)) File.Copy(Memory.File_, Memory.File_ + ".truoc-khoi-phuc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                    Memory.ReplaceAll(json);
                    var setp = Path.Combine(tmp, "settings.ini");
                    if (File.Exists(setp) && !File.Exists(Cleaner.SettingsFile)) File.Copy(setp, Cleaner.SettingsFile);
                    if (Directory.Exists(RepoDir)) DeleteTree(RepoDir);
                    Directory.Move(tmp, RepoDir);
                    if (File.Exists(KeyFile)) File.Delete(KeyFile);
                    string hp; Pack(out hp);
                    var sp = Status(); sp.Repo = repoUrl; sp.Plain = true; sp.LastError = null; sp.LastOk = DateTime.Now.ToString("o"); sp.LastHash = hp; sp.LastVerified = DateTime.Now.ToString("o"); Save(sp);
                    Activity.Log("sao lưu", "git", "khôi phục", repoUrl + " · " + n + " ghi nhớ");
                    return L.T("Restored ", "Đã khôi phục ") + n + L.T(" memories. This PC will keep backing up to the same repo.", " ghi nhớ. Máy này sẽ tiếp tục sao lưu vào cùng repo.");
                }
                var dataFile = Path.Combine(tmp, DataName);
                if (!File.Exists(dataFile)) throw new FileNotFoundException(L.T("This repo has no backup yet.", "Repo này chưa có bản sao lưu nào."));
                if (string.IsNullOrEmpty(secret)) throw new UnauthorizedAccessException(L.T("This backup is encrypted – a password or recovery code is needed.", "Bản sao lưu này được mã hoá – cần mật khẩu hoặc mã khôi phục."));
                string code = secret.Trim();
                try { code = Encoding.UTF8.GetString(Decrypt(File.ReadAllBytes(Path.Combine(tmp, KeyName)), secret)); }
                catch (UnauthorizedAccessException) { }   // không phải mật khẩu → thử như mã khôi phục
                var zipBytes = Decrypt(File.ReadAllBytes(dataFile), code.ToUpperInvariant());
                int count = CountMemories(zipBytes);

                // Giữ lại trí nhớ hiện có trên máy (nếu có) trước khi thay
                Directory.CreateDirectory(Cleaner.DataDir);
                if (File.Exists(Memory.File_)) File.Copy(Memory.File_, Memory.File_ + ".truoc-khoi-phuc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
                using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
                {
                    using (var sr = new StreamReader(zip.GetEntry("tri-nho.json").Open(), Encoding.UTF8)) Memory.ReplaceAll(sr.ReadToEnd());
                    var set = zip.GetEntry("settings.ini");
                    if (set != null && !File.Exists(Cleaner.SettingsFile)) using (var s = set.Open()) using (var f = File.Create(Cleaner.SettingsFile)) s.CopyTo(f);
                }
                // Máy này tiếp tục sao lưu vào cùng repo
                if (Directory.Exists(RepoDir)) DeleteTree(RepoDir);
                Directory.Move(tmp, RepoDir);
                StoreLocalCode(code.ToUpperInvariant());
                string h; Pack(out h);
                var st = Status(); st.Repo = repoUrl; st.Plain = false; st.LastError = null; st.LastOk = DateTime.Now.ToString("o"); st.LastHash = h; st.LastVerified = DateTime.Now.ToString("o"); Save(st);
                Activity.Log("sao lưu", "git", "khôi phục", repoUrl + " · " + count + " ghi nhớ");
                return L.T("Restored ", "Đã khôi phục ") + count + L.T(" memories. This PC will keep backing up to the same repo.", " ghi nhớ. Máy này sẽ tiếp tục sao lưu vào cùng repo.");
            }
            finally { if (Directory.Exists(tmp)) try { DeleteTree(tmp); } catch { } }
        }
    }

    // Ngắt sao lưu trên máy này: bỏ bản sao repo, khoá và trạng thái. Repo trên GitHub giữ nguyên để khôi phục lại sau.
    public static void Disconnect()
    {
        lock (gate)
        {
            if (Directory.Exists(RepoDir)) DeleteTree(RepoDir);
            foreach (var f in new[] { KeyFile, StatusFile }) if (File.Exists(f)) File.Delete(f);
        }
    }

    // Tên tài khoản GitHub đang đăng nhập (null nếu chưa đăng nhập hoặc chưa có gh)
    public static string GhUser()
    {
        try { var r = Gh("api user --jq .login", 30000); return r.Item1 == 0 && r.Item2.Length > 0 ? r.Item2.Split('\n')[0].Trim() : null; }
        catch { return null; }
    }

    public static string GhLogout()
    {
        var r = Gh("auth logout --hostname github.com", 30000);
        return r.Item1 == 0 ? null : r.Item2;
    }

    // Xoá thư mục do chính app tạo (bản sao repo); bỏ thuộc tính chỉ-đọc của file trong .git trước
    static void DeleteTree(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }
}

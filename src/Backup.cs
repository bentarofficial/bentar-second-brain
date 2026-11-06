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

    public static bool Configured { get { return File.Exists(KeyFile) && Directory.Exists(Path.Combine(RepoDir, ".git")); } }

    // ---------- Trạng thái ----------
    public class State { public string Repo, LastOk, LastError, LastVerified, LastHash; }

    public static State Status()
    {
        try { if (File.Exists(StatusFile)) return Json.Deserialize<State>(File.ReadAllText(StatusFile, Encoding.UTF8)) ?? new State(); } catch { }
        return new State();
    }
    static void Save(State s) { Directory.CreateDirectory(Cleaner.DataDir); File.WriteAllText(StatusFile, Json.Serialize(s), new UTF8Encoding(false)); }

    // Có cần báo đỏ không: chưa thiết lập, lỗi, hoặc quá 3 ngày chưa sao lưu được
    public static string Warning()
    {
        if (!Configured) return "Bộ não chưa được sao lưu – mất máy là mất trí nhớ.";
        var s = Status();
        if (!string.IsNullOrEmpty(s.LastError)) return "Sao lưu lỗi: " + s.LastError;
        DateTime ok;
        if (s.LastOk == null || !DateTime.TryParse(s.LastOk, out ok) || (DateTime.Now - ok).TotalDays > 3) return "Đã hơn 3 ngày chưa sao lưu được bộ não.";
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
        if (blob.Length < 16 || Encoding.ASCII.GetString(blob, 0, 8) != "Salted__") throw new InvalidDataException("Không phải file sao lưu hợp lệ.");
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
            catch (CryptographicException) { throw new UnauthorizedAccessException("Sai mật khẩu hoặc mã khôi phục."); }
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
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return Tuple.Create(-1, "quá thời gian"); }
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
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return Tuple.Create(-1, "quá thời gian"); }
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
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c title Dang nhap GitHub cho Bentar Second Brain & echo Bam Enter de mo trinh duyet va dang nhap GitHub. & gh auth login --web --git-protocol https --hostname github.com & gh auth setup-git")
            { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
        if (!p.WaitForExit(10 * 60 * 1000)) { try { p.Kill(); } catch { } throw new TimeoutException("Chưa đăng nhập GitHub xong (quá 10 phút)."); }
        if (!GhLoggedIn()) throw new InvalidOperationException("Chưa đăng nhập được GitHub.");
    }

    public class RepoInfo { public string Name, Url; public bool IsBackup; public override string ToString() { return (IsBackup ? "★ " : "") + Name; } }

    // Các repo riêng tư của người dùng; repo nào có bản sao lưu bộ não thì đánh dấu và xếp lên đầu
    public static List<RepoInfo> ListRepos()
    {
        var r = Gh("repo list --visibility private --limit 300 --json nameWithOwner,url", 60000);
        if (r.Item1 != 0) throw new InvalidOperationException("Không đọc được danh sách repo: " + r.Item2);
        var json = r.Item2.Substring(r.Item2.IndexOf('['));
        json = json.Substring(0, json.LastIndexOf(']') + 1);
        var list = Json.Deserialize<List<Dictionary<string, object>>>(json)
            .Select(d => new RepoInfo { Name = (string)d["nameWithOwner"], Url = (string)d["url"] + ".git" }).ToList();
        // Kiểm tra thật (có file khoa.enc) với các repo có tên giống bản sao lưu, để không phải gọi cho cả trăm repo
        foreach (var repo in list.Where(x => x.Name.IndexOf("brain", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("sao-luu", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0).Take(15))
            repo.IsBackup = Gh("api repos/" + repo.Name + "/contents/" + KeyName + " --silent", 20000).Item1 == 0;
        return list.OrderByDescending(x => x.IsBackup).ThenBy(x => x.Name).ToList();
    }

    // Tạo repo GitHub riêng tư bằng GitHub CLI (nếu có); trả về địa chỉ repo
    public static string CreateGithubRepo(string name)
    {
        if (!GhAvailable()) throw new InvalidOperationException("Máy chưa có GitHub CLI (gh). Hãy tự tạo một repo riêng tư trên github.com rồi dán địa chỉ vào.");
        if (!GhLoggedIn()) throw new InvalidOperationException("Chưa đăng nhập GitHub.");
        Gh("auth setup-git", 20000);   // để Git đẩy bản sao lưu bằng chính đăng nhập GitHub này
        var psi = new ProcessStartInfo("cmd.exe", "/c gh repo create " + name + " --private --description \"Sao luu ma hoa cua Bentar Second Brain\" && gh repo view " + name + " --json url -q .url")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            var url = o.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("https://"));
            if (url == null) throw new InvalidOperationException("Không tạo được repo: " + (o + " " + err.Result).Trim());
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
                add("thong-tin.txt", Encoding.UTF8.GetBytes("Bentar Second Brain " + Updater.Current.ToString(3) + "\nSao lưu lúc " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\nMáy: " + Environment.MachineName + "\n"));
            }
            return ms.ToArray();
        }
    }

    // Bản Markdown đọc được bằng mắt, không cần app (ghi nhớ theo loại, kèm nguồn và liên kết)
    static string Readable()
    {
        var snap = Memory.Snapshot();
        var byId = snap.Item1.ToDictionary(n => n.Id);
        var sb = new StringBuilder("# Bộ não thứ hai – bản đọc được\n\nXuất lúc " + DateTime.Now.ToString("dd/MM/yyyy HH:mm") + "\n");
        foreach (var g in snap.Item1.Where(n => n.Kind != "file" && n.Kind != "du-an").GroupBy(n => n.Kind).OrderBy(g => g.Key))
        {
            sb.Append("\n## ").Append(Memory.Label(g.Key)).Append("\n");
            foreach (var n in g.OrderByDescending(n => n.Updated))
            {
                sb.Append("\n### ").Append(n.Title).Append(n.Outdated ? " (đã cũ)" : "").Append("\n");
                sb.Append("_").Append(n.Updated.ToLocalTime().ToString("dd/MM/yyyy")).Append(n.Agent != null ? " · " + n.Agent : "").Append(n.Source != null ? " · nguồn: " + n.Source : "").Append("_\n\n");
                sb.Append(n.Text).Append("\n");
                var links = snap.Item2.Where(y => y.A == n.Id || y.B == n.Id).Select(y => y.A == n.Id ? y.B : y.A).Where(byId.ContainsKey).Select(id => byId[id].Title).Take(8).ToList();
                if (links.Count > 0) sb.Append("\nLiên quan: ").Append(string.Join(" · ", links)).Append("\n");
            }
        }
        return sb.ToString();
    }

    const string RepoReadme =
        "# Sao lưu Bentar Second Brain\n\n" +
        "Repo này chỉ chứa dữ liệu ĐÃ MÃ HOÁ. Lịch sử commit là các phiên bản của bộ não theo thời gian.\n\n" +
        "- `bo-nao.sbak`: gói zip (tri-nho.json, tri-nho.md đọc được, settings.ini), mã hoá bằng MÃ KHÔI PHỤC.\n" +
        "- `khoa.enc`: mã khôi phục, mã hoá bằng MẬT KHẨU của bạn.\n\n" +
        "## Khôi phục\n\n" +
        "Cách dễ nhất: cài Bentar Second Brain, chọn \"Khôi phục bộ não\", dán địa chỉ repo này và nhập mật khẩu hoặc mã khôi phục.\n\n" +
        "Không có app vẫn mở được bằng OpenSSL (định dạng chuẩn: Salted__ + PBKDF2-SHA256 200000 vòng + AES-256-CBC):\n\n" +
        "```\n" +
        "# 1. Lấy mã khôi phục từ mật khẩu (bỏ qua nếu bạn đã có mã khôi phục)\n" +
        "openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -in khoa.enc -pass pass:\"MẬT KHẨU\"\n" +
        "# 2. Giải mã bộ não bằng mã khôi phục, rồi giải nén file zip\n" +
        "openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -in bo-nao.sbak -out bo-nao.zip -pass pass:\"MÃ-KHÔI-PHỤC\"\n" +
        "```\n";

    // ---------- Thiết lập lần đầu ----------
    // Trả về MÃ KHÔI PHỤC (chỉ hiện một lần cho người dùng chép lại)
    public static string Setup(string repoUrl, string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8) throw new ArgumentException("Mật khẩu cần ít nhất 8 ký tự.");
        if (!GitAvailable()) throw new InvalidOperationException("Máy chưa cài Git (git-scm.com).");
        lock (gate)
        {
            if (Directory.Exists(RepoDir)) DeleteTree(RepoDir);
            var r = Git("clone \"" + repoUrl + "\" \"" + RepoDir + "\"", Cleaner.DataDir);
            if (r.Item1 != 0) throw new InvalidOperationException("Không lấy được repo: " + r.Item2);
            if (File.Exists(Path.Combine(RepoDir, KeyName)) || File.Exists(Path.Combine(RepoDir, DataName)))
                throw new InvalidOperationException("Repo này đã có bản sao lưu. Hãy dùng \"Khôi phục\" thay vì thiết lập mới.");
            var code = NewRecoveryCode();
            File.WriteAllBytes(Path.Combine(RepoDir, KeyName), Encrypt(Encoding.UTF8.GetBytes(code), password));
            File.WriteAllText(Path.Combine(RepoDir, "README.md"), RepoReadme, new UTF8Encoding(false));
            StoreLocalCode(code);
            var st = Status(); st.Repo = repoUrl; st.LastHash = null; st.LastError = null; Save(st);
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
        if (!Configured) return "Chưa thiết lập sao lưu.";
        lock (gate)
        {
            var st = Status();
            try
            {
                string hash;
                var packed = Pack(out hash);
                if (!force && hash == st.LastHash && string.IsNullOrEmpty(st.LastError)) return "Không có gì mới.";
                File.WriteAllBytes(Path.Combine(RepoDir, DataName), Encrypt(packed, LocalCode()));
                if (!File.Exists(Path.Combine(RepoDir, "README.md"))) File.WriteAllText(Path.Combine(RepoDir, "README.md"), RepoReadme, new UTF8Encoding(false));
                Git("add -A", RepoDir);
                var c = Git("-c user.name=\"Bentar Second Brain\" -c user.email=second-brain@localhost commit -m \"Sao lưu " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " (" + Environment.MachineName + ")\"", RepoDir);
                if (c.Item1 != 0 && !c.Item2.Contains("nothing to commit")) throw new InvalidOperationException(c.Item2);
                var p = Git("push -u origin HEAD", RepoDir);
                if (p.Item1 != 0) throw new InvalidOperationException("Không đẩy lên được: " + p.Item2);
                st.LastOk = DateTime.Now.ToString("o"); st.LastHash = hash; st.LastError = null; Save(st);
                Activity.Log("sao lưu", "git", "sao lưu", st.Repo);
                return "Đã sao lưu lúc " + DateTime.Now.ToString("HH:mm dd/MM/yyyy") + ".";
            }
            catch (Exception ex)
            {
                st.LastError = ex.Message.Length > 300 ? ex.Message.Substring(0, 300) : ex.Message; Save(st);
                return "Sao lưu lỗi: " + st.LastError;
            }
        }
    }

    // Tự kiểm tra: mở thử bản sao lưu mới nhất trên repo xem có giải mã và đọc được không
    public static string Verify()
    {
        if (!Configured) return "Chưa thiết lập sao lưu.";
        lock (gate)
        {
            var st = Status();
            try
            {
                Git("pull --ff-only", RepoDir);
                var zip = Decrypt(File.ReadAllBytes(Path.Combine(RepoDir, DataName)), LocalCode());
                int count = CountMemories(zip);
                st.LastVerified = DateTime.Now.ToString("o"); Save(st);
                return "Bản sao lưu mở được: " + count + " ghi nhớ.";
            }
            catch (Exception ex) { st.LastError = "Kiểm tra bản sao lưu thất bại: " + ex.Message; Save(st); return st.LastError; }
        }
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
        if (!GitAvailable()) throw new InvalidOperationException("Máy chưa cài Git (git-scm.com).");
        lock (gate)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "bsb-khoi-phuc-" + Guid.NewGuid().ToString("N"));
            var r = Git("clone \"" + repoUrl + "\" \"" + tmp + "\"", Path.GetTempPath());
            if (r.Item1 != 0) throw new InvalidOperationException("Không lấy được repo: " + r.Item2);
            try
            {
                var dataFile = Path.Combine(tmp, DataName);
                if (!File.Exists(dataFile)) throw new FileNotFoundException("Repo này chưa có bản sao lưu nào.");
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
                var st = Status(); st.Repo = repoUrl; st.LastError = null; st.LastOk = DateTime.Now.ToString("o"); st.LastHash = h; st.LastVerified = DateTime.Now.ToString("o"); Save(st);
                Activity.Log("sao lưu", "git", "khôi phục", repoUrl + " · " + count + " ghi nhớ");
                return "Đã khôi phục " + count + " ghi nhớ. Máy này sẽ tiếp tục sao lưu vào cùng repo.";
            }
            finally { if (Directory.Exists(tmp)) try { DeleteTree(tmp); } catch { } }
        }
    }

    // Xoá thư mục do chính app tạo (bản sao repo); bỏ thuộc tính chỉ-đọc của file trong .git trước
    static void DeleteTree(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }
}

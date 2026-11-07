// Trí nhớ của Second Brain, mô phỏng cơ chế bộ não:
// - Nơ-ron: mỗi ghi nhớ (ghi chú, quyết định, bài học...), mỗi dự án, mỗi file từng được dùng
// - Khớp thần kinh: liên kết có độ mạnh giữa các nơ-ron
// - Cùng kích hoạt thì nối chặt hơn (Hebb); lâu không dùng thì phai dần (không xoá); nhớ lại thì nhớ sâu hơn
// - Nhớ lại có liên tưởng: kết quả lan theo khớp thần kinh sang các ý liên quan
// Lưu trong dữ liệu riêng của app (tri-nho.json); KHÔNG BAO GIỜ sửa file của người dùng.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

public class Neuron
{
    public string Id { get; set; }
    public string Kind { get; set; }      // ghi-chu, quyet-dinh, bai-hoc, su-that, viec-can-lam, du-an, file
    public string Title { get; set; }
    public string Text { get; set; }
    public string Source { get; set; }    // đường dẫn file/dự án liên quan (nếu có)
    public string Tags { get; set; }
    public string Agent { get; set; }     // agent đã tạo ghi nhớ
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public DateTime LastUsed { get; set; }
    public int Uses { get; set; }
    public bool Outdated { get; set; }
    public List<string> History { get; set; }
    // Chỉ học điều có bằng chứng: "verified" (app đã tự kiểm tra bằng chứng, hoặc người dùng duyệt),
    // "candidate" (chờ duyệt – KHÔNG đưa cho agent dùng), "stale" (bằng chứng đã đổi, cần xem lại). Trống = bản cũ, coi như chờ duyệt.
    public string Status { get; set; }
    public string Evidence { get; set; }   // mỗi dòng một bằng chứng: file "đường dẫn" "trích dẫn" | commit "repo" mã | url https://… "trích dẫn" | user "lời bạn nói"
    public string CheckNote { get; set; }  // kết quả kiểm tra gần nhất
    public DateTime CheckedAt { get; set; }
    public bool IsVerified { get { return Status == "verified"; } }
}

public class Synapse
{
    public string A { get; set; }
    public string B { get; set; }
    public double Weight { get; set; }
    public DateTime LastFired { get; set; }
    public string Why { get; set; }
}

public class MemoryStore
{
    public int NextId { get; set; }
    public List<Neuron> Neurons { get; set; }
    public List<Synapse> Synapses { get; set; }
}

static class Memory
{
    public static readonly string File_ = Path.Combine(Cleaner.DataDir, "tri-nho.json");
    const double NeuronHalfLifeDays = 90, SynapseHalfLifeDays = 120;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static readonly Mutex mutex = new Mutex(false, @"Local\BentarSecondBrainMemory");   // nhiều agent có thể ghi cùng lúc
    static MemoryStore store;
    static DateTime loadedStamp;
    [ThreadStatic] public static string AgentName;   // thiết bị đang gọi (mỗi luồng một thiết bị)

    public static readonly Dictionary<string, string> KindNames = new Dictionary<string, string> {
        { "note", "ghi-chu" }, { "decision", "quyet-dinh" }, { "lesson", "bai-hoc" }, { "fact", "su-that" }, { "todo", "viec-can-lam" } };
    static string KindLabel(string k)
    {
        switch (k)
        {
            case "ghi-chu": return L.T("note", "ghi chú"); case "quyet-dinh": return L.T("decision", "quyết định"); case "bai-hoc": return L.T("lesson", "bài học");
            case "su-that": return L.T("fact", "sự thật"); case "viec-can-lam": return L.T("to-do", "việc cần làm"); case "du-an": return L.T("project", "dự án"); case "file": return "file";
            default: return k;
        }
    }

    // ---------- Đọc / ghi an toàn giữa nhiều tiến trình ----------
    static T With<T>(bool write, Func<MemoryStore, T> f)
    {
        bool owned = false;
        try { owned = mutex.WaitOne(10000); } catch (AbandonedMutexException) { owned = true; }
        try
        {
            var stamp = File.Exists(File_) ? File.GetLastWriteTimeUtc(File_) : DateTime.MinValue;
            if (store == null || stamp != loadedStamp)
            {
                store = null;
                if (File.Exists(File_)) try { store = Json.Deserialize<MemoryStore>(File.ReadAllText(File_, Encoding.UTF8)); } catch { }
                if (store == null) store = new MemoryStore();
                if (store.Neurons == null) store.Neurons = new List<Neuron>();
                if (store.Synapses == null) store.Synapses = new List<Synapse>();
                foreach (var n in store.Neurons) if (n.History == null) n.History = new List<string>();
                loadedStamp = stamp;
            }
            var r = f(store);
            if (write)
            {
                Directory.CreateDirectory(Cleaner.DataDir);
                var tmp = File_ + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(store), new UTF8Encoding(false));
                if (File.Exists(File_)) File.Replace(tmp, File_, null); else File.Move(tmp, File_);
                loadedStamp = File.GetLastWriteTimeUtc(File_);
            }
            return r;
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    // Ảnh chụp toàn bộ trí nhớ cho bản đồ bộ não (bản sao, không giữ khoá)
    public static Tuple<List<Neuron>, List<Synapse>> Snapshot()
    {
        return With(false, s => Tuple.Create(
            s.Neurons.Select(n => new Neuron { Id = n.Id, Kind = n.Kind, Title = n.Title, Text = n.Text, Source = n.Source, Tags = n.Tags, Agent = n.Agent,
                Created = n.Created, Updated = n.Updated, LastUsed = n.LastUsed, Uses = n.Uses, Outdated = n.Outdated,
                Status = n.Status, Evidence = n.Evidence, CheckNote = n.CheckNote, CheckedAt = n.CheckedAt }).ToList(),
            s.Synapses.Select(y => new Synapse { A = y.A, B = y.B, Weight = EffectiveWeight(y), LastFired = y.LastFired, Why = y.Why }).ToList()));
    }
    // Thay toàn bộ trí nhớ bằng bản khôi phục (giữ khoá để agent khác không ghi chen vào giữa)
    public static void ReplaceAll(string json)
    {
        if (Json.Deserialize<MemoryStore>(json) == null) throw new InvalidDataException(L.T("Invalid memory file.", "Bản trí nhớ không hợp lệ."));
        With(false, s =>
        {
            Directory.CreateDirectory(Cleaner.DataDir);
            File.WriteAllText(File_, json, new UTF8Encoding(false));
            store = null;
            return 0;
        });
    }

    // Người dùng xoá hẳn một ghi nhớ (agent không có quyền này; agent chỉ đánh dấu đã cũ)
    public static void Delete(string id)
    {
        With(true, s =>
        {
            s.Neurons.RemoveAll(n => n.Id == id);
            s.Synapses.RemoveAll(y => y.A == id || y.B == id);
            return 0;
        });
    }

    public static double VitalityOf(Neuron n) { return Vitality(n); }
    public static string Label(string kind) { return KindLabel(kind); }

    public static Tuple<int, int> Counts() { return With(false, s => Tuple.Create(s.Neurons.Count(n => n.Kind != "file" && n.Kind != "du-an"), s.Synapses.Count)); }

    static readonly HashSet<string> staleIds = new HashSet<string>();
    public static int PendingCount() { return With(false, s => s.Neurons.Count(n => n.Kind != "file" && n.Kind != "du-an" && !n.IsVerified && !n.Outdated)); }

    static Neuron Find(MemoryStore s, string id) { return s.Neurons.FirstOrDefault(n => n.Id == id); }

    static HashSet<string> TermsOf(Neuron n) { return new HashSet<string>(Brain.Tokens(n.Title + " " + n.Text + " " + n.Tags + " " + (n.Source == null ? "" : Path.GetFileName(n.Source)))); }

    static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    // Độ sáng hiện tại: phai theo thời gian không dùng, mạnh thêm theo số lần nhớ lại
    static double Vitality(Neuron n)
    {
        double days = (DateTime.UtcNow - n.LastUsed).TotalDays;
        double fade = Math.Pow(0.5, days / NeuronHalfLifeDays);
        return (0.35 + 0.65 * fade) * (1 + Math.Log(1 + n.Uses) * 0.3) * (n.Outdated ? 0.3 : 1);
    }

    static double EffectiveWeight(Synapse y) { return y.Weight * Math.Pow(0.5, (DateTime.UtcNow - y.LastFired).TotalDays / SynapseHalfLifeDays); }

    static void Fire(MemoryStore s, string a, string b, double amount, string why)
    {
        if (a == null || b == null || a == b) return;
        if (string.CompareOrdinal(a, b) > 0) { var t = a; a = b; b = t; }
        var y = s.Synapses.FirstOrDefault(x => x.A == a && x.B == b);
        if (y == null) s.Synapses.Add(new Synapse { A = a, B = b, Weight = amount, LastFired = DateTime.UtcNow, Why = why });
        else
        {
            y.Weight = Math.Min(5, EffectiveWeight(y) + amount);
            y.LastFired = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(why) && (y.Why == null || !y.Why.Contains(why))) y.Why = string.IsNullOrEmpty(y.Why) ? why : y.Why + "; " + why;
        }
    }

    // Nơ-ron đại diện cho dự án / file (tạo khi được dùng tới lần đầu)
    static string ProjectNeuron(MemoryStore s, string path)
    {
        var proj = Brain.ProjectOf(path) ?? (Directory.Exists(path) ? path : null);
        if (proj == null) return null;
        string id = "p:" + proj.ToLowerInvariant();
        if (Find(s, id) == null)
        {
            var info = Brain.Projects(null).FirstOrDefault(p => string.Equals(p.Path, proj, StringComparison.OrdinalIgnoreCase));
            var now = DateTime.UtcNow;
            s.Neurons.Add(new Neuron { Id = id, Kind = "du-an", Title = Path.GetFileName(proj.TrimEnd('\\')), Text = info != null ? info.Summary : "", Source = proj,
                Created = now, Updated = now, LastUsed = now, History = new List<string>() });
        }
        return id;
    }

    static string FileNeuron(MemoryStore s, string path)
    {
        string id = "f:" + path.ToLowerInvariant();
        if (Find(s, id) == null)
        {
            var now = DateTime.UtcNow;
            s.Neurons.Add(new Neuron { Id = id, Kind = "file", Title = Path.GetFileName(path), Text = "", Source = path, Created = now, Updated = now, LastUsed = now, History = new List<string>() });
            var p = ProjectNeuron(s, path);
            if (p != null) Fire(s, id, p, 1, "thuộc dự án");
        }
        return id;
    }

    // Giải tên do agent đưa vào: mã ghi nhớ (#n12 / n12), đường dẫn file hoặc thư mục dự án
    static string Resolve(MemoryStore s, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim().TrimStart('#');
        if (Find(s, key) != null) return key;
        if (File.Exists(key)) return FileNeuron(s, Path.GetFullPath(key));
        if (Directory.Exists(key)) return ProjectNeuron(s, Path.GetFullPath(key).TrimEnd('\\'));
        return null;
    }

    // ---------- Ghi nhớ ----------
    public static string Remember(string text, string title, string kind, string source, string tags, string evidence = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException(L.T("Text is required to remember.", "Cần nội dung để ghi nhớ."));
        var secret = Evidence.FindSecret(text + "\n" + title + "\n" + evidence);
        if (secret != null) throw new ArgumentException(L.T("Refused: this looks like a secret (", "Từ chối: nội dung có vẻ chứa bí mật (") + secret + L.T("). Never store passwords, keys or tokens.", "). Không bao giờ lưu mật khẩu, khoá hay token."));
        string k; if (kind == null || !KindNames.TryGetValue(kind, out k)) k = "ghi-chu";
        if (string.IsNullOrWhiteSpace(title)) { title = text.Trim().Split('\n')[0]; if (title.Length > 80) title = title.Substring(0, 80) + "…"; }
        // Kiểm tra bằng chứng NGOÀI khoá (có thể tải URL / gọi git); bằng chứng sai thì từ chối hẳn
        var check = Evidence.Check(evidence, k);
        if (check.Failed.Count > 0)
            throw new ArgumentException(L.T("Refused: evidence did not check out – ", "Từ chối: bằng chứng không đúng – ") + string.Join("; ", check.Failed) +
                L.T(". Fix the evidence (exact quote from the file/page, real commit) and try again.", ". Sửa lại bằng chứng (trích đúng nguyên văn trong file/trang, commit có thật) rồi thử lại."));
        string status = check.Strong ? "verified" : "candidate";
        return With(true, s =>
        {
            var now = DateTime.UtcNow;
            // Ghi nhớ chờ duyệt quá 30 ngày mà không ai duyệt thì bỏ (không để rác tích tụ)
            s.Neurons.RemoveAll(n => n.Kind != "file" && n.Kind != "du-an" && !n.IsVerified && n.Status != "stale" && (now - n.Created).TotalDays > 30);
            var draft = new Neuron { Title = title, Text = text, Tags = tags, Source = source };
            var terms = TermsOf(draft);
            // Gần trùng một ghi nhớ cũ cùng loại: củng cố thay vì tạo bản sao.
            // Nhưng điều CHƯA có bằng chứng không bao giờ được ghi đè lên điều ĐÃ xác minh.
            string ft = Brain.Fold(title.Trim());
            var dup = s.Neurons.Where(n => n.Kind == k && !n.Outdated)
                               .Select(n => new { n, sim = Similarity(terms, TermsOf(n)) + (Brain.Fold(n.Title.Trim()) == ft ? 0.3 : 0) })
                               .OrderByDescending(x => x.sim).FirstOrDefault();
            string conflictWith = null;
            if (dup != null && dup.sim >= 0.65)
            {
                var n = dup.n;
                if (!n.IsVerified || status == "verified")
                {
                    if (n.Text != text) { n.History.Add(n.Updated.ToString("yyyy-MM-dd") + ": " + n.Text); n.Text = text; n.Title = title; }
                    n.Status = status; n.Evidence = evidence; n.CheckNote = check.Note; n.CheckedAt = now;
                    n.Updated = n.LastUsed = now; n.Uses++; n.Outdated = false;
                    return L.T("A similar memory already exists #", "Đã có ghi nhớ gần giống #") + n.Id + L.T(" — reinforced it. ", " — đã củng cố. ") + StatusLine(n);
                }
                conflictWith = n.Id;   // bản mới chưa có bằng chứng: lưu riêng ở hàng chờ, nối với bản đã xác minh
            }
            var neuron = new Neuron { Id = "n" + (++s.NextId), Kind = k, Title = title, Text = text, Source = source, Tags = tags, Agent = AgentName,
                Created = now, Updated = now, LastUsed = now, History = new List<string>(), Status = status, Evidence = evidence, CheckNote = check.Note, CheckedAt = now };
            s.Neurons.Add(neuron);
            if (conflictWith != null) Fire(s, neuron.Id, conflictWith, 1, "đề xuất sửa (chưa có bằng chứng)");
            var links = new List<string>();
            // Nối với dự án / file nguồn (và các file dùng làm bằng chứng)
            Func<string, string> norm = x => { try { return Path.GetFullPath(x.Trim()).TrimEnd('\\'); } catch { return x.Trim(); } };   // D:/a và D:\a là một
            var srcs = (source ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x.Trim().Length > 0).Select(norm).Concat(check.Files).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var src in srcs)
            {
                var id = File.Exists(src) ? FileNeuron(s, Path.GetFullPath(src)) : Directory.Exists(src) ? ProjectNeuron(s, Path.GetFullPath(src).TrimEnd('\\')) : null;
                if (id == null) continue;
                Fire(s, neuron.Id, id, 1.5, "nguồn");
                var p = ProjectNeuron(s, src);
                if (p != null && p != id) Fire(s, neuron.Id, p, 1, "thuộc dự án");
                links.Add(Find(s, id).Title);
            }
            // Tự nối với các ghi nhớ có nội dung gần nhau
            foreach (var x in s.Neurons.Where(n => n != neuron && n.Kind != "file").Select(n => new { n, sim = Similarity(terms, TermsOf(n)) })
                                       .Where(x => x.sim >= 0.15).OrderByDescending(x => x.sim).Take(5))
            {
                Fire(s, neuron.Id, x.n.Id, Math.Round(x.sim * 2, 2), "nội dung gần nhau");
                links.Add("#" + x.n.Id + " " + x.n.Title);
            }
            return L.T("Saved #", "Đã lưu #") + neuron.Id + " (" + KindLabel(k) + "): " + title + "\n" + StatusLine(neuron) +
                   (conflictWith != null ? L.T("\nNote: it differs from verified memory #", "\nLưu ý: khác với ghi nhớ đã xác minh #") + conflictWith + L.T(", which stays in use until this one is verified.", ", bản đó vẫn được dùng cho tới khi bản này được xác minh.") : "") +
                   (links.Count > 0 ? L.T("\nLinked to: ", "\nĐã nối với: ") + string.Join(", ", links.Distinct().Take(8)) : "");
        });
    }

    static string StatusLine(Neuron n)
    {
        if (n.IsVerified) return L.T("✓ Verified – shared with every agent. ", "✓ Đã xác minh – mọi agent sẽ dùng được. ") + n.CheckNote;
        if (n.Status == "stale") return L.T("⚠ Needs review – its evidence changed. ", "⚠ Cần xem lại – bằng chứng đã thay đổi. ") + n.CheckNote;
        return L.T("⏳ Awaiting approval – NOT shared with agents until verified. ", "⏳ Chờ duyệt – CHƯA chia sẻ cho agent cho tới khi được xác minh. ") + n.CheckNote +
               L.T(" To verify it, remember again with evidence: file \"<path>\" \"<exact quote>\" (lessons must come from a trusted project), commit \"<repo>\" <hash>, or url https://… \"<exact quote>\". Or the user approves it in the app.",
                   " Muốn xác minh: gọi remember lại kèm bằng chứng: file \"<đường dẫn>\" \"<trích nguyên văn>\" (bài học phải lấy từ dự án tin cậy), commit \"<repo>\" <mã>, hoặc url https://… \"<trích nguyên văn>\". Hoặc người dùng duyệt trong app.");
    }

    /// <summary>Người dùng duyệt một ghi nhớ (agent không có quyền này).</summary>
    public static void Approve(string id)
    {
        With(true, s =>
        {
            var n = Find(s, id);
            if (n == null) return 0;
            n.Status = "verified"; n.CheckNote = L.T("approved by you", "bạn đã duyệt") + " " + DateTime.Now.ToString("dd/MM/yyyy"); n.CheckedAt = DateTime.UtcNow; n.Outdated = false;
            return 0;
        });
    }

    // ---------- Bằng chứng: app tự kiểm tra, không tin lời agent ----------
    public static class Evidence
    {
        public class Result { public bool Strong; public string Note = ""; public List<string> Failed = new List<string>(); public List<string> Files = new List<string>(); }

        static readonly Regex FileRx = new Regex("^file\\s+(?:\"(?<p>[^\"]+)\"|(?<p>\\S+))\\s+\"(?<q>.+)\"\\s*$", RegexOptions.IgnoreCase);
        static readonly Regex CommitRx = new Regex("^commit\\s+(?:\"(?<r>[^\"]+)\"|(?<r>\\S+))\\s+(?<h>[0-9a-f]{7,40})\\s*$", RegexOptions.IgnoreCase);
        static readonly Regex UrlRx = new Regex("^url\\s+(?<u>https://\\S+)\\s+\"(?<q>.+)\"\\s*$", RegexOptions.IgnoreCase);
        static readonly Regex UserRx = new Regex("^user\\s+\"(?<q>.+)\"\\s*$", RegexOptions.IgnoreCase);
        static readonly Regex SecretRx = new Regex(@"(sk-[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|xox[abpr]-[A-Za-z0-9\-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY|sb_[0-9a-f]{40,}|(?:password|passwd|pwd|mật khẩu|mat khau|api[_-]?key|secret|token)\s*[:=]\s*\S{6,})", RegexOptions.IgnoreCase);

        public static string FindSecret(string s)
        {
            var m = SecretRx.Match(s ?? "");
            if (!m.Success) return null;
            var v = m.Value; return v.Length > 10 ? v.Substring(0, 6) + "…" : "…";
        }

        // So khớp trích dẫn: bỏ dấu, bỏ hoa thường, gộp khoảng trắng
        static string Norm(string s) { return Regex.Replace(Brain.Fold(s ?? "").ToLowerInvariant(), "\\s+", " ").Trim(); }
        // Trích dẫn phải đủ dài (≥ 20 ký tự) để thật sự chứng minh điều được ghi nhớ, không chỉ là một từ khoá có trên trang
        public const int MinQuote = 20;
        public static bool Contains(string haystack, string quote) { var q = Norm(quote); return q.Length >= MinQuote && Norm(haystack).Contains(q); }
        public static bool TooShort(string quote) { return Norm(quote).Length < MinQuote; }

        public static Result Check(string evidence, string kind)
        {
            var r = new Result();
            var ok = new List<string>(); var weak = new List<string>();
            foreach (var raw in (evidence ?? "").Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim(); if (line.Length == 0) continue;
                Match m;
                if ((m = FileRx.Match(line)).Success)
                {
                    var p = m.Groups["p"].Value; var q = m.Groups["q"].Value;
                    if (TooShort(q)) { r.Failed.Add(L.T("quote too short (need at least 20 characters of the exact text that proves it): ", "đoạn trích quá ngắn (cần ít nhất 20 ký tự nguyên văn chứng minh điều đó): ") + q); continue; }
                    if (!File.Exists(p)) { r.Failed.Add(L.T("file not found: ", "không có file: ") + p); continue; }
                    string text; try { text = Brain.ReadText(p); } catch (Exception ex) { r.Failed.Add(p + ": " + ex.Message); continue; }
                    if (!Contains(text, q)) { r.Failed.Add(L.T("quote not found in ", "không thấy đoạn trích trong ") + p); continue; }
                    r.Files.Add(Path.GetFullPath(p));
                    var t = Trust.Of(p);
                    // Bài học (cách làm nên theo) chỉ học từ nguồn tin cậy; sự thật/quyết định về chính dự án đó thì file của dự án là đủ
                    if (t.Level == Trust.Level.Excluded) weak.Add(L.T("file in an excluded source", "file thuộc nguồn bị loại trừ"));
                    else if (kind == "bai-hoc" && t.Level != Trust.Level.Trusted) weak.Add(L.T("lesson from an unverified source (", "bài học từ nguồn chưa kiểm chứng (") + Path.GetFileName(p) + ")");
                    else ok.Add(L.T("file checked", "đã kiểm tra file") + " " + Path.GetFileName(p));
                }
                else if ((m = CommitRx.Match(line)).Success)
                {
                    var repo = m.Groups["r"].Value; var h = m.Groups["h"].Value;
                    if (!Directory.Exists(repo)) { r.Failed.Add(L.T("repo not found: ", "không có repo: ") + repo); continue; }
                    if (Git(repo, "cat-file -t " + h).Trim() != "commit") { r.Failed.Add(L.T("commit not found: ", "không có commit: ") + h); continue; }
                    ok.Add("commit " + h.Substring(0, 7));
                }
                else if ((m = UrlRx.Match(line)).Success)
                {
                    var u = m.Groups["u"].Value; var q = m.Groups["q"].Value;
                    if (TooShort(q)) { r.Failed.Add(L.T("quote too short (need at least 20 characters of the exact text that proves it): ", "đoạn trích quá ngắn (cần ít nhất 20 ký tự nguyên văn chứng minh điều đó): ") + q); continue; }
                    string page;
                    try { page = Fetch(u); } catch (Exception ex) { r.Failed.Add(L.T("could not open ", "không mở được ") + u + " (" + ex.Message + ")"); continue; }
                    if (!Contains(page, q)) { r.Failed.Add(L.T("quote not found on ", "không thấy đoạn trích trên ") + u); continue; }
                    ok.Add(L.T("page checked ", "đã kiểm tra trang ") + new Uri(u).Host);
                }
                else if ((m = UserRx.Match(line)).Success) weak.Add(L.T("user's words (needs the user's approval)", "lời người dùng (cần người dùng duyệt)"));
                else r.Failed.Add(L.T("unknown evidence format: ", "không hiểu dạng bằng chứng: ") + (line.Length > 60 ? line.Substring(0, 60) + "…" : line));
            }
            r.Strong = ok.Count > 0 && weak.Count == 0;
            r.Note = ok.Count == 0 && weak.Count == 0 ? L.T("No evidence given.", "Chưa có bằng chứng.") : string.Join(", ", ok.Concat(weak)) + ".";
            return r;
        }

        static string Git(string repo, string args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", "-C \"" + repo + "\" " + args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = System.Diagnostics.Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000); return p.ExitCode == 0 ? o : ""; }
            }
            catch { return ""; }
        }

        static string Fetch(string url)
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Timeout = 12000; req.UserAgent = "BentarSecondBrain-evidence-check"; req.AllowAutoRedirect = true;
            using (var resp = req.GetResponse()) using (var st = resp.GetResponseStream()) using (var sr = new StreamReader(st, Encoding.UTF8))
            {
                var buf = new char[3 * 1024 * 1024]; int n = sr.ReadBlock(buf, 0, buf.Length);
                var html = new string(buf, 0, n);
                html = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                return System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
            }
        }

        // Bằng chứng file đã đổi kể từ lần kiểm tra trước thì kiểm tra lại; trích dẫn không còn thì chuyển sang "cần xem lại"
        public static bool StillHolds(Neuron n)
        {
            foreach (var raw in (n.Evidence ?? "").Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var m = FileRx.Match(raw.Trim());
                if (!m.Success) continue;
                var p = m.Groups["p"].Value;
                if (!File.Exists(p)) { if (Brain.IsOnline(p)) return false; continue; }   // ổ đang rút: không kết luận
                if (File.GetLastWriteTimeUtc(p) <= n.CheckedAt) continue;
                try { if (!Contains(Brain.ReadText(p), m.Groups["q"].Value)) return false; } catch { }
            }
            return true;
        }
    }

    public static string Revise(string id, string text, bool outdated)
    {
        return With(true, s =>
        {
            var n = Find(s, (id ?? "").Trim().TrimStart('#'));
            if (n == null) throw new ArgumentException(L.T("No memory ", "Không có ghi nhớ ") + id);
            if (!string.IsNullOrWhiteSpace(text) && text != n.Text)
            {
                n.History.Add(n.Updated.ToString("yyyy-MM-dd") + ": " + n.Text); n.Text = text;
                if (AgentName != null && n.IsVerified) { n.Status = "candidate"; n.CheckNote = L.T("edited by ", "được sửa bởi ") + AgentName + L.T(" – needs re-verification", " – cần xác minh lại"); }
            }
            n.Outdated = outdated;
            n.Updated = n.LastUsed = DateTime.UtcNow;
            return L.T("Updated #", "Đã cập nhật #") + n.Id + (outdated ? L.T(" (marked outdated – will fade but is kept)", " (đánh dấu đã cũ – sẽ mờ dần, vẫn giữ lại)") : "") + ".";
        });
    }

    public static string Connect(string a, string b, string why)
    {
        return With(true, s =>
        {
            var ia = Resolve(s, a); var ib = Resolve(s, b);
            if (ia == null || ib == null) throw new ArgumentException(L.T("Not found: ", "Không tìm thấy ") + (ia == null ? a : b) + L.T(" (use a memory id like #n12, or a file/project path).", " (dùng mã ghi nhớ như #n12, hoặc đường dẫn file/dự án)."));
            Fire(s, ia, ib, 1, string.IsNullOrWhiteSpace(why) ? "agent nối" : why);
            return L.T("Linked ", "Đã nối ") + Find(s, ia).Title + " ↔ " + Find(s, ib).Title + ".";
        });
    }

    // ---------- Nhớ lại có liên tưởng ----------
    public class Recalled { public Neuron N; public double Score; public string Via; }

    static bool Usable(Neuron n, bool includeUnverified) { return n.Kind == "file" || n.Kind == "du-an" || n.IsVerified || includeUnverified; }

    public static List<Recalled> RecallRaw(string query, int limit, bool touch, bool includeUnverified = false)
    {
        // Ghi nhớ đã xác minh mà bằng chứng đổi (file bị sửa, trích dẫn không còn): chuyển sang "cần xem lại" trước khi dùng
        With(false, s => { foreach (var n in s.Neurons.Where(n => n.IsVerified && n.Evidence != null).ToList()) if (!Evidence.StillHolds(n)) { staleIds.Add(n.Id); } return 0; });
        if (staleIds.Count > 0) With(true, s => { foreach (var n in s.Neurons.Where(n => staleIds.Contains(n.Id))) { n.Status = "stale"; n.CheckNote = L.T("its evidence changed on ", "bằng chứng đã thay đổi ngày ") + DateTime.Now.ToString("dd/MM/yyyy"); } staleIds.Clear(); return 0; });
        return With(touch, s =>
        {
            var q = Brain.Tokens(query ?? "").Distinct().ToList();
            var scores = new Dictionary<string, Recalled>();
            if (q.Count == 0)
            {
                // Không có từ khoá: trả về các ghi nhớ đang "sáng" nhất
                foreach (var n in s.Neurons.Where(n => n.Kind != "file" && n.Kind != "du-an" && Usable(n, includeUnverified)).OrderByDescending(n => Vitality(n) * (1 + (n.Updated - DateTime.UtcNow.AddDays(-30)).TotalDays / 30)).Take(limit))
                    scores[n.Id] = new Recalled { N = n, Score = Vitality(n) };
            }
            else
            {
                foreach (var n in s.Neurons.Where(n => Usable(n, includeUnverified)))
                {
                    var t = TermsOf(n);
                    int hit = q.Count(t.Contains);
                    if (hit == 0) continue;
                    // Dự án / file chỉ khớp lẻ tẻ một chữ thì bỏ (tránh kéo nhầm), ghi nhớ thì giữ
                    if ((n.Kind == "du-an" || n.Kind == "file") && q.Count >= 2 && hit * 2 < q.Count + 1) continue;
                    if (q.Count >= 3 && hit < 2) continue;
                    double baseScore = 10.0 * hit / q.Count + (hit == q.Count ? 5 : 0);
                    if (n.Kind == "file") baseScore *= 0.5;
                    scores[n.Id] = new Recalled { N = n, Score = baseScore * Vitality(n) };
                }
                // Lan truyền kích hoạt sang nơ-ron lân cận (liên tưởng), 1 bước
                var seeds = scores.Values.OrderByDescending(r => r.Score).Take(10).ToList();
                foreach (var seed in seeds)
                    foreach (var y in s.Synapses.Where(y => y.A == seed.N.Id || y.B == seed.N.Id))
                    {
                        var other = y.A == seed.N.Id ? y.B : y.A;
                        var on = Find(s, other);
                        if (on == null || !Usable(on, includeUnverified)) continue;
                        double add = seed.Score * Math.Min(1, EffectiveWeight(y) / 2) * 0.5 * Vitality(on);
                        Recalled r;
                        if (!scores.TryGetValue(other, out r)) scores[other] = r = new Recalled { N = on, Score = 0, Via = "#" + seed.N.Id + " " + seed.N.Title };
                        r.Score += add;
                    }
            }
            var top = scores.Values.Where(r => r.Score > 0.3).OrderByDescending(r => r.Score).Take(limit).ToList();
            if (touch)
            {
                var now = DateTime.UtcNow;
                foreach (var r in top) { r.N.Uses++; r.N.LastUsed = now; }   // nhớ lại thì nhớ sâu hơn
                // Các ý cùng được gợi lên thì nối chặt thêm một chút (Hebb)
                var direct = top.Where(r => r.Via == null && r.N.Kind != "file").Take(3).ToList();
                for (int i = 0; i < direct.Count; i++) for (int j = i + 1; j < direct.Count; j++) Fire(s, direct[i].N.Id, direct[j].N.Id, 0.05, "cùng được nhớ lại");
            }
            return top;
        });
    }

    public static string Format(List<Recalled> list, bool full)
    {
        var sb = new StringBuilder();
        foreach (var r in list)
        {
            var n = r.N;
            sb.Append("• #").Append(n.Id).Append(" [").Append(KindLabel(n.Kind));
            if (n.Kind != "file" && n.Kind != "du-an") sb.Append(" · ").Append(n.Updated.ToLocalTime().ToString("dd/MM/yyyy"));
            if (n.Uses > 0) sb.Append(L.T(" · recalled ", " · nhớ lại ")).Append(n.Uses).Append(L.T(" times", " lần"));
            if (n.Outdated) sb.Append(L.T(" · OUTDATED", " · ĐÃ CŨ"));
            if (n.Kind != "file" && n.Kind != "du-an") sb.Append(n.IsVerified ? L.T(" · ✓ verified", " · ✓ đã xác minh") : n.Status == "stale" ? L.T(" · ⚠ needs review", " · ⚠ cần xem lại") : L.T(" · ⏳ unverified", " · ⏳ chưa xác minh"));
            sb.Append("] ").AppendLine(n.Title);
            if (r.Via != null) sb.AppendLine(L.T("  (associated via ", "  (liên tưởng từ ") + r.Via + ")");
            if (!string.IsNullOrEmpty(n.Source)) sb.AppendLine(L.T("  Source: ", "  Nguồn: ") + n.Source);
            if (full && !string.IsNullOrEmpty(n.Evidence)) sb.AppendLine(L.T("  Evidence: ", "  Bằng chứng: ") + n.Evidence.Replace("\n", " | ") + (string.IsNullOrEmpty(n.CheckNote) ? "" : "  → " + n.CheckNote));
            if (!string.IsNullOrEmpty(n.Text) && n.Kind != "file")
            {
                var t = n.Text.Trim();
                int max = n.Kind == "du-an" ? 220 : full ? int.MaxValue : 500;
                if (t.Length > max) t = t.Substring(0, max) + "…";
                foreach (var l in t.Split('\n')) sb.AppendLine("  " + l.TrimEnd('\r'));
            }
        }
        return sb.ToString().TrimEnd();
    }

    // Ghi nhớ gắn với một dự án (cho project_overview)
    public static string ForProject(string path)
    {
        return With(false, s =>
        {
            var proj = Brain.ProjectOf(path) ?? path;
            string pid = "p:" + Path.GetFullPath(proj).TrimEnd('\\').ToLowerInvariant();
            var ids = new HashSet<string>(s.Synapses.Where(y => y.A == pid || y.B == pid).Select(y => y.A == pid ? y.B : y.A));
            var list = s.Neurons.Where(n => ids.Contains(n.Id) && n.Kind != "file" && n.Kind != "du-an" && n.IsVerified).OrderByDescending(Vitality).Take(15)
                         .Select(n => new Recalled { N = n }).ToList();
            var related = s.Synapses.Where(y => (y.A == pid || y.B == pid)).Select(y => new { other = Find(s, y.A == pid ? y.B : y.A), w = EffectiveWeight(y), y.Why })
                           .Where(x => x.other != null && x.other.Kind == "du-an").OrderByDescending(x => x.w).Take(8).ToList();
            var sb = new StringBuilder();
            if (list.Count > 0) sb.AppendLine(L.T("Memories about this project:", "Ghi nhớ về dự án này:")).AppendLine(Format(list, false));
            if (related.Count > 0)
            {
                sb.AppendLine().AppendLine(L.T("Projects often used together:", "Dự án hay đi cùng:"));
                foreach (var x in related) sb.AppendLine("  • " + x.other.Title + " — " + x.other.Source + L.T("  (strength ", "  (độ gắn kết ") + x.w.ToString("0.00") + ": " + x.Why + ")");
            }
            return sb.ToString().TrimEnd();
        });
    }

    // ---------- Tự học từ hoạt động của agent ----------
    static string lastProject; static DateTime lastAt;

    // Agent đọc / dùng một file: củng cố file đó, và nối với dự án vừa dùng trước đó trong cùng phiên
    public static void Experience(string path)
    {
        try
        {
            With(true, s =>
            {
                var f = FileNeuron(s, Path.GetFullPath(path));
                var n = Find(s, f); n.Uses++; n.LastUsed = DateTime.UtcNow;
                var p = ProjectNeuron(s, path);
                if (p != null)
                {
                    var pn = Find(s, p); pn.Uses++; pn.LastUsed = DateTime.UtcNow;
                    if (lastProject != null && lastProject != p && (DateTime.UtcNow - lastAt).TotalMinutes < 20) Fire(s, lastProject, p, 0.1, "dùng cùng phiên");
                    lastProject = p; lastAt = DateTime.UtcNow;
                }
                return 0;
            });
        }
        catch { }
    }

    // Agent chép tài liệu từ chỗ này sang chỗ khác: bằng chứng mạnh rằng hai nơi có liên quan
    public static void Copied(string source, string destination)
    {
        try
        {
            With(true, s =>
            {
                var src = Directory.Exists(source) ? ProjectNeuron(s, source) ?? null : FileNeuron(s, Path.GetFullPath(source));
                var dstDir = Directory.Exists(destination) ? destination : Path.GetDirectoryName(destination);
                var dp = ProjectNeuron(s, dstDir);
                var sp = ProjectNeuron(s, source);
                if (src != null && dp != null) Fire(s, src, dp, 0.8, "đã chép sang");
                if (sp != null && dp != null) Fire(s, sp, dp, 0.5, "chép tài liệu qua lại");
                return 0;
            });
        }
        catch { }
    }
}

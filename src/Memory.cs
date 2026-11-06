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
    public static string AgentName = "agent";

    public static readonly Dictionary<string, string> KindNames = new Dictionary<string, string> {
        { "note", "ghi-chu" }, { "decision", "quyet-dinh" }, { "lesson", "bai-hoc" }, { "fact", "su-that" }, { "todo", "viec-can-lam" } };
    static string KindLabel(string k)
    {
        switch (k)
        {
            case "ghi-chu": return "ghi chú"; case "quyet-dinh": return "quyết định"; case "bai-hoc": return "bài học";
            case "su-that": return "sự thật"; case "viec-can-lam": return "việc cần làm"; case "du-an": return "dự án"; case "file": return "file";
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

    public static Tuple<int, int> Counts() { return With(false, s => Tuple.Create(s.Neurons.Count(n => n.Kind != "file" && n.Kind != "du-an"), s.Synapses.Count)); }

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
    public static string Remember(string text, string title, string kind, string source, string tags)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Cần nội dung để ghi nhớ.");
        string k; if (kind == null || !KindNames.TryGetValue(kind, out k)) k = "ghi-chu";
        if (string.IsNullOrWhiteSpace(title)) { title = text.Trim().Split('\n')[0]; if (title.Length > 80) title = title.Substring(0, 80) + "…"; }
        return With(true, s =>
        {
            var now = DateTime.UtcNow;
            var draft = new Neuron { Title = title, Text = text, Tags = tags, Source = source };
            var terms = TermsOf(draft);
            // Gần trùng một ghi nhớ cũ cùng loại: củng cố ghi nhớ đó thay vì tạo bản sao
            string ft = Brain.Fold(title.Trim());
            var dup = s.Neurons.Where(n => n.Kind == k && !n.Outdated)
                               .Select(n => new { n, sim = Similarity(terms, TermsOf(n)) + (Brain.Fold(n.Title.Trim()) == ft ? 0.3 : 0) })
                               .OrderByDescending(x => x.sim).FirstOrDefault();
            if (dup != null && dup.sim >= 0.65)
            {
                var n = dup.n;
                if (n.Text != text) { n.History.Add(n.Updated.ToString("yyyy-MM-dd") + ": " + n.Text); n.Text = text; n.Title = title; }
                n.Updated = n.LastUsed = now; n.Uses++; n.Outdated = false;
                return "Đã có ghi nhớ gần giống #" + n.Id + " — đã củng cố và cập nhật nội dung mới.";
            }
            var neuron = new Neuron { Id = "n" + (++s.NextId), Kind = k, Title = title, Text = text, Source = source, Tags = tags, Agent = AgentName,
                Created = now, Updated = now, LastUsed = now, History = new List<string>() };
            s.Neurons.Add(neuron);
            var links = new List<string>();
            // Nối với dự án / file nguồn
            if (!string.IsNullOrWhiteSpace(source))
                foreach (var src in source.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
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
            return "Đã ghi nhớ #" + neuron.Id + " (" + KindLabel(k) + "): " + title +
                   (links.Count > 0 ? "\nĐã nối với: " + string.Join(", ", links.Distinct().Take(8)) : "");
        });
    }

    public static string Revise(string id, string text, bool outdated)
    {
        return With(true, s =>
        {
            var n = Find(s, (id ?? "").Trim().TrimStart('#'));
            if (n == null) throw new ArgumentException("Không có ghi nhớ " + id);
            if (!string.IsNullOrWhiteSpace(text) && text != n.Text) { n.History.Add(n.Updated.ToString("yyyy-MM-dd") + ": " + n.Text); n.Text = text; }
            n.Outdated = outdated;
            n.Updated = n.LastUsed = DateTime.UtcNow;
            return "Đã cập nhật #" + n.Id + (outdated ? " (đánh dấu đã cũ – sẽ mờ dần, vẫn giữ lại)" : "") + ".";
        });
    }

    public static string Connect(string a, string b, string why)
    {
        return With(true, s =>
        {
            var ia = Resolve(s, a); var ib = Resolve(s, b);
            if (ia == null || ib == null) throw new ArgumentException("Không tìm thấy " + (ia == null ? a : b) + " (dùng mã ghi nhớ như #n12, hoặc đường dẫn file/dự án).");
            Fire(s, ia, ib, 1, string.IsNullOrWhiteSpace(why) ? "agent nối" : why);
            return "Đã nối " + Find(s, ia).Title + " ↔ " + Find(s, ib).Title + ".";
        });
    }

    // ---------- Nhớ lại có liên tưởng ----------
    public class Recalled { public Neuron N; public double Score; public string Via; }

    public static List<Recalled> RecallRaw(string query, int limit, bool touch)
    {
        return With(touch, s =>
        {
            var q = Brain.Tokens(query ?? "").Distinct().ToList();
            var scores = new Dictionary<string, Recalled>();
            if (q.Count == 0)
            {
                // Không có từ khoá: trả về các ghi nhớ đang "sáng" nhất
                foreach (var n in s.Neurons.Where(n => n.Kind != "file" && n.Kind != "du-an").OrderByDescending(n => Vitality(n) * (1 + (n.Updated - DateTime.UtcNow.AddDays(-30)).TotalDays / 30)).Take(limit))
                    scores[n.Id] = new Recalled { N = n, Score = Vitality(n) };
            }
            else
            {
                foreach (var n in s.Neurons)
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
                        if (on == null) continue;
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
            if (n.Uses > 0) sb.Append(" · nhớ lại ").Append(n.Uses).Append(" lần");
            if (n.Outdated) sb.Append(" · ĐÃ CŨ");
            sb.Append("] ").AppendLine(n.Title);
            if (r.Via != null) sb.AppendLine("  (liên tưởng từ " + r.Via + ")");
            if (!string.IsNullOrEmpty(n.Source)) sb.AppendLine("  Nguồn: " + n.Source);
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
            var list = s.Neurons.Where(n => ids.Contains(n.Id) && n.Kind != "file" && n.Kind != "du-an").OrderByDescending(Vitality).Take(15)
                         .Select(n => new Recalled { N = n }).ToList();
            var related = s.Synapses.Where(y => (y.A == pid || y.B == pid)).Select(y => new { other = Find(s, y.A == pid ? y.B : y.A), w = EffectiveWeight(y), y.Why })
                           .Where(x => x.other != null && x.other.Kind == "du-an").OrderByDescending(x => x.w).Take(8).ToList();
            var sb = new StringBuilder();
            if (list.Count > 0) sb.AppendLine("Ghi nhớ về dự án này:").AppendLine(Format(list, false));
            if (related.Count > 0)
            {
                sb.AppendLine().AppendLine("Dự án hay đi cùng:");
                foreach (var x in related) sb.AppendLine("  • " + x.other.Title + " — " + x.other.Source + "  (độ gắn kết " + x.w.ToString("0.00") + ": " + x.Why + ")");
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

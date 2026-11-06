// Bản đồ bộ não 3D: vẽ đúng các nơ-ron và khớp thần kinh trong trí nhớ Second Brain.
// - Bộ não là đám mây hạt sáng 3D (hai bán cầu, tiểu não, thân não) nối thành lưới mảnh, xoay và lắc nhẹ trong không gian
// - Phối cảnh: phần gần to và sáng, phần xa nhỏ và mờ; mạng lưới sao phía sau xoay chậm hơn tạo chiều sâu
// - Thuỳ trán: ghi nhớ của agent; thuỳ đỉnh: dự án; thuỳ thái dương: file đã dùng
// - Nơ-ron to/sáng theo mức được dùng, mờ dần khi lâu không dùng; xung điện chạy dọc khớp thần kinh
// - Khi agent thật sự nhớ lại / đọc / ghi, nơ-ron tương ứng loé sáng và phát xung lan sang các ý liên quan
// - Kéo chuột để xoay, lăn chuột để phóng to/thu nhỏ, nhấp một nơ-ron để xem, nhấp đúp để mở
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

class BrainMap : Control
{
    class Node
    {
        public string Id, Kind, Title, Text, Source;
        public float X, Y, Z, Flash, Birth, Vital = 0.5f, Phase;
        public int Uses, Degree;
        public bool Outdated, Memory;
        public DateTime LastUsed;
        public PointF P; public float F, D;   // vị trí trên màn hình, hệ số phối cảnh, độ sâu (tính mỗi khung hình)
    }
    class Edge { public Node A, B; public float W; public string Why; public bool Implicit; }
    class Pulse { public Edge E; public bool Fwd; public float T, Speed; public Color C; public int Hops; }
    struct V3 { public float X, Y, Z; public V3(float x, float y, float z) { X = x; Y = y; Z = z; } }

    readonly Dictionary<string, Node> nodes = new Dictionary<string, Node>();
    List<Edge> edges = new List<Edge>();
    readonly List<Pulse> pulses = new List<Pulse>();
    readonly Dictionary<string, DateTime> seenUse = new Dictionary<string, DateTime>();
    readonly Random rnd = new Random(7);
    readonly Timer frame = new Timer { Interval = 33 }, poll = new Timer { Interval = 1500 };
    Node hover, selected;
    HashSet<Node> highlight = new HashSet<Node>();
    float time;
    bool loading, firstLoad = true;
    public event Action<string> StatusChanged;

    // Hình bộ não nhìn nghiêng (toạ độ Z = trước/sau, Y = trên/dưới; trán ở Z âm) dùng để dựng khối 3D
    static readonly PointF[] Profile = {
        new PointF(-0.78f, 0.10f), new PointF(-0.88f, -0.12f), new PointF(-0.82f, -0.36f), new PointF(-0.64f, -0.55f), new PointF(-0.38f, -0.68f),
        new PointF(-0.06f, -0.72f), new PointF(0.26f, -0.68f), new PointF(0.52f, -0.56f), new PointF(0.72f, -0.36f), new PointF(0.82f, -0.12f),
        new PointF(0.78f, 0.10f), new PointF(0.62f, 0.17f), new PointF(0.44f, 0.19f), new PointF(0.30f, 0.24f), new PointF(0.18f, 0.32f),
        new PointF(-0.02f, 0.40f), new PointF(-0.24f, 0.42f), new PointF(-0.42f, 0.36f), new PointF(-0.47f, 0.24f), new PointF(-0.60f, 0.20f) };
    static readonly GraphicsPath profilePath = MakeProfile();
    static GraphicsPath MakeProfile()
    {
        var gp = new GraphicsPath();
        gp.AddClosedCurve(Profile.Select(p => new PointF(p.X * 1000, p.Y * 1000)).ToArray(), 0.5f);
        return gp;
    }
    static bool InProfile(float z, float y) { return profilePath.IsVisible(z * 1000, y * 1000); }

    // Nửa bề rộng của một bán cầu tại (z, y): dạng vòm, hẹp dần về trán, gáy và đỉnh
    static float HalfWidth(float z, float y)
    {
        double a = 1 - Math.Pow((z + 0.03) / 0.92, 2), b = 1 - Math.Pow((y + 0.14) / 0.64, 2);
        return a <= 0 || b <= 0 ? 0 : (float)(0.50 * Math.Sqrt(a) * Math.Sqrt(b)) + 0.05f;
    }

    // Đám mây hạt tạo nên bộ não, và lưới nối các hạt gần nhau
    readonly List<V3> shell = new List<V3>();
    readonly List<int[]> shellLinks = new List<int[]>();
    readonly List<V3> stars = new List<V3>();
    readonly List<int[]> starLinks = new List<int[]>();
    PointF[] shellP; float[] shellD, shellF;
    Bitmap bgCache;

    // Góc nhìn: lắc nhẹ quanh góc nhìn nghiêng; người dùng kéo chuột để xoay thêm, lăn chuột để phóng to
    float userYaw, userPitch, zoom = 1f, yaw, pitch;
    Point dragFrom; bool dragging, dragged;

    public BrainMap()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(2, 4, 10);
        Font = new Font("Segoe UI", 8.5f);
        BuildShell();
        BuildStars();
        frame.Tick += (s, e) => { if (Animating) { Step(); Invalidate(); } };
        poll.Tick += (s, e) => { if (Visible) Reload(); };
        frame.Start(); poll.Start();
    }

    bool Animating { get { var f = FindForm(); return Visible && f != null && f.WindowState != FormWindowState.Minimized; } }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { frame.Dispose(); poll.Dispose(); if (bgCache != null) bgCache.Dispose(); foreach (var b in glows.Values) b.Dispose(); }
        base.Dispose(disposing);
    }

    // ---------- Dựng khối não 3D ----------
    void BuildShell()
    {
        var r = new Random(3);
        // Đại não: hạt trên mặt ngoài hai bán cầu, nhấp nhô theo nếp cuộn; bỏ trống khe giữa hai bán cầu
        while (shell.Count < 880)
        {
            float z = (float)(r.NextDouble() * 1.8 - 0.92), y = (float)(r.NextDouble() * 1.16 - 0.74);
            if (!InProfile(z, y)) continue;
            float hw = HalfWidth(z, y);
            if (hw < 0.08f) continue;
            float side = r.Next(2) == 0 ? -1 : 1;
            float fold = 1 - 0.09f * (float)Math.Abs(Math.Sin(13 * z + 9 * y + side * 2) * Math.Cos(11 * y - 7 * z));
            float depth = r.NextDouble() < 0.82 ? 0.93f + (float)r.NextDouble() * 0.07f : 0.15f + (float)r.NextDouble() * 0.75f;   // phần lớn nằm trên bề mặt
            float x = side * Math.Max(0.035f, hw * fold * depth);
            shell.Add(new V3(x, y, z));
        }
        // Tiểu não: khối bầu dục phía sau, dưới thuỳ chẩm
        for (int i = 0; i < 140; i++)
        {
            double u = r.NextDouble() * Math.PI * 2, v = Math.Acos(2 * r.NextDouble() - 1);
            float x = (float)(Math.Sin(v) * Math.Cos(u)) * 0.30f, y = (float)Math.Cos(v) * 0.12f, z = (float)(Math.Sin(v) * Math.Sin(u)) * 0.22f;
            y += 0.012f * (float)Math.Sin(z * 60);   // các nếp ngang của tiểu não
            shell.Add(new V3(x, 0.32f + y, 0.55f + z));
        }
        // Thân não: trụ thuôn chéo xuống phía sau
        for (int i = 0; i < 80; i++)
        {
            float t = (float)r.NextDouble(), ang = (float)(r.NextDouble() * Math.PI * 2), rad = 0.075f - t * 0.025f;
            shell.Add(new V3((float)Math.Cos(ang) * rad, 0.28f + t * 0.50f, 0.24f - t * 0.02f + (float)Math.Sin(ang) * rad));
        }
        shellLinks.AddRange(NearestLinks(shell, 2, 0.11f));
        shellP = new PointF[shell.Count]; shellD = new float[shell.Count]; shellF = new float[shell.Count];
    }

    void BuildStars()
    {
        var r = new Random(5);
        while (stars.Count < 150)
        {
            double u = r.NextDouble() * Math.PI * 2, v = Math.Acos(2 * r.NextDouble() - 1), rad = 1.15 + r.NextDouble() * 0.7;
            stars.Add(new V3((float)(Math.Sin(v) * Math.Cos(u) * rad), (float)(Math.Cos(v) * rad * 0.75), (float)(Math.Sin(v) * Math.Sin(u) * rad)));
        }
        starLinks.AddRange(NearestLinks(stars, 2, 0.75f));
    }

    static IEnumerable<int[]> NearestLinks(List<V3> pts, int k, float maxDist)
    {
        var seen = new HashSet<long>();
        for (int i = 0; i < pts.Count; i++)
        {
            var near = new List<KeyValuePair<float, int>>();
            for (int j = 0; j < pts.Count; j++)
            {
                if (j == i) continue;
                float dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y, dz = pts[i].Z - pts[j].Z, d = dx * dx + dy * dy + dz * dz;
                if (d < maxDist * maxDist) near.Add(new KeyValuePair<float, int>(d, j));
            }
            foreach (var n in near.OrderBy(x => x.Key).Take(k))
                if (seen.Add(Math.Min(i, n.Value) * 100000L + Math.Max(i, n.Value))) yield return new[] { i, n.Value };
        }
    }

    // Vùng não của từng loại nơ-ron: ghi nhớ ở thuỳ trán, dự án ở thuỳ đỉnh, file ở thuỳ thái dương
    static PointF RegionOf(string kind)
    {
        if (IsMemoryKind(kind)) return new PointF(-0.48f, -0.22f);
        return kind == "file" ? new PointF(-0.05f, 0.18f) : new PointF(0.28f, -0.28f);
    }

    // Vị trí 3D cố định theo mã nơ-ron (mở lại app vẫn ở chỗ cũ), nằm trong đúng thuỳ não
    void Place(Node n)
    {
        var r = new Random(n.Id.GetHashCode());
        var c = RegionOf(n.Kind);
        float spread = IsMemoryKind(n.Kind) ? 0.30f : n.Kind == "file" ? 0.28f : 0.42f;
        for (int i = 0; i < 80; i++)
        {
            float z = c.X + (float)((r.NextDouble() * 2 - 1) * spread * 1.3), y = c.Y + (float)((r.NextDouble() * 2 - 1) * spread * 0.8);
            if (!InProfile(z, y)) continue;
            float hw = HalfWidth(z, y) * 0.85f;
            n.Z = z; n.Y = y; n.X = (r.Next(2) == 0 ? -1 : 1) * (0.05f + (float)r.NextDouble() * Math.Max(0.02f, hw - 0.05f));
            n.Phase = (float)(r.NextDouble() * 6.28);
            return;
        }
        n.Z = c.X; n.Y = c.Y; n.X = 0.1f;
    }

    // ---------- Dữ liệu ----------
    public void Reload()
    {
        if (loading) return;
        loading = true;
        Task.Run(() =>
        {
            try
            {
                // Ghi nhớ nạp ngay; danh sách dự án cần chỉ mục (lần đầu nạp vài giây) nên vẽ sau
                var snap = Memory.Snapshot();
                var devs = Activity.Devices(30).Where(d => d.Name != "trạm phát" && d.Name != "sao lưu").Take(14).ToList();
                if (IsHandleCreated) BeginInvoke(new Action(() => ApplyDevices(devs)));
                if (cachedProjects == null && IsHandleCreated) BeginInvoke(new Action(() => Apply(snap.Item1, snap.Item2, new List<BrainProject>())));
                var projects = Brain.Projects(null);
                if (IsHandleCreated) BeginInvoke(new Action(() => { cachedProjects = projects; Apply(snap.Item1, snap.Item2, projects); loading = false; }));
                else loading = false;
            }
            catch { loading = false; }
        });
    }
    List<BrainProject> cachedProjects;
    string lastStatus;

    // Bóng đèn: các agent / công cụ đã cắm vào bộ não (từ nhật ký hoạt động)
    class Bulb { public string Name; public DateTime LastSeen; public int Uses; public float Flash; }
    readonly List<Bulb> bulbs = new List<Bulb>();

    void ApplyDevices(List<Device> devs)
    {
        foreach (var d in devs)
        {
            var b = bulbs.FirstOrDefault(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase));
            if (b == null) bulbs.Add(b = new Bulb { Name = d.Name, LastSeen = d.LastSeen, Uses = d.Uses, Flash = firstLoad ? 0 : 1 });
            else if (d.LastSeen > b.LastSeen) { b.Flash = 1; b.LastSeen = d.LastSeen; b.Uses = d.Uses; }
        }
        bulbs.RemoveAll(b => !devs.Any(d => string.Equals(d.Name, b.Name, StringComparison.OrdinalIgnoreCase)));
    }

    static bool IsMemoryKind(string k) { return k != "du-an" && k != "file"; }

    void Apply(List<Neuron> neurons, List<Synapse> synapses, List<BrainProject> projects)
    {
        var want = new Dictionary<string, Node>();
        foreach (var p in projects)
        {
            string id = "p:" + p.Path.ToLowerInvariant();
            want[id] = new Node { Id = id, Kind = "du-an", Title = p.Name, Text = p.Summary, Source = p.Path, LastUsed = p.LastWrite.ToUniversalTime() };
        }
        foreach (var n in neurons)
        {
            Node d;
            if (!want.TryGetValue(n.Id, out d)) want[n.Id] = d = new Node { Id = n.Id };
            d.Kind = n.Kind; d.Title = n.Title; d.Text = string.IsNullOrEmpty(n.Text) ? d.Text : n.Text; d.Source = n.Source ?? d.Source;
            d.Uses = n.Uses; d.Outdated = n.Outdated; d.LastUsed = n.LastUsed; d.Memory = true;
            d.Vital = (float)Math.Min(1.6, Memory.VitalityOf(n));
        }

        var activated = new List<Node>();
        foreach (var kv in want)
        {
            Node cur;
            var w = kv.Value;
            if (!w.Memory) w.Vital = (float)(0.25 + 0.35 * Math.Pow(0.5, (DateTime.UtcNow - w.LastUsed).TotalDays / 120));
            if (nodes.TryGetValue(kv.Key, out cur))
            {
                bool moved = cur.Kind != w.Kind;
                cur.Kind = w.Kind; cur.Title = w.Title; cur.Text = w.Text; cur.Source = w.Source; cur.Uses = w.Uses;
                cur.Outdated = w.Outdated; cur.Vital = w.Vital; cur.Memory = w.Memory; cur.LastUsed = w.LastUsed;
                if (moved) Place(cur);
            }
            else
            {
                nodes[kv.Key] = cur = w;
                Place(cur);
                cur.Birth = firstLoad ? 0 : 1;
            }
            DateTime prev;
            if (w.Memory && seenUse.TryGetValue(kv.Key, out prev) && w.LastUsed > prev.AddSeconds(1)) activated.Add(cur);
            else if (w.Memory && !firstLoad && !seenUse.ContainsKey(kv.Key)) activated.Add(cur);
            if (w.Memory) seenUse[kv.Key] = w.LastUsed;
        }
        foreach (var gone in nodes.Keys.Where(k => !want.ContainsKey(k)).ToList()) nodes.Remove(gone);

        var es = new List<Edge>();
        foreach (var y in synapses)
        {
            Node a, b;
            if (nodes.TryGetValue(y.A, out a) && nodes.TryGetValue(y.B, out b)) es.Add(new Edge { A = a, B = b, W = (float)y.Weight, Why = y.Why });
        }
        // Liên kết ngầm: các dự án nằm chung một thư mục cha (cùng một "vùng" kiến thức)
        foreach (var g in nodes.Values.Where(n => n.Kind == "du-an" && n.Source != null)
                                      .GroupBy(n => SafeParent(n.Source)).Where(g => g.Key != null && g.Count() > 1))
        {
            var list = g.ToList();
            for (int i = 1; i < list.Count; i++) es.Add(new Edge { A = list[i - 1], B = list[i], W = 0.12f, Why = "cùng thư mục " + g.Key, Implicit = true });
        }
        foreach (var n in nodes.Values) n.Degree = 0;
        foreach (var e in es) { e.A.Degree++; e.B.Degree++; }
        edges = es;
        pulses.RemoveAll(p => !edges.Contains(p.E));
        if (selected != null && !nodes.ContainsValue(selected)) selected = null;

        foreach (var n in activated) Activate(n, 3);
        firstLoad = false;
        var mem = nodes.Values.Count(n => n.Memory && IsMemoryKind(n.Kind));
        // Chỉ báo khi có gì thay đổi, để không đè lên kết quả tìm kiếm người dùng vừa xem
        string status = nodes.Count + "/" + mem + "/" + edges.Count;
        if (status == lastStatus && activated.Count == 0) return;
        lastStatus = status;
        if (StatusChanged != null)
            StatusChanged(nodes.Count + " nơ-ron (" + mem + " ghi nhớ, " + nodes.Values.Count(n => n.Kind == "du-an") + " dự án) · " + edges.Count(e => !e.Implicit) + " khớp thần kinh" +
                          (activated.Count > 0 ? " · vừa hoạt động: " + string.Join(", ", activated.Take(3).Select(n => Short(n.Title, 30))) : ""));
    }

    static string SafeParent(string p)
    {
        try { var d = Path.GetDirectoryName(p.TrimEnd('\\')); return d == null || d.Length <= 3 ? null : d; } catch { return null; }
    }

    // Một nơ-ron loé sáng và phát xung sang các ý nối với nó
    void Activate(Node n, int hops)
    {
        n.Flash = 1;
        foreach (var e in edges.Where(e => e.A == n || e.B == n).OrderByDescending(e => e.W).Take(8))
            Fire(e, e.A == n, Color.FromArgb(255, 240, 170), hops);
    }

    void Fire(Edge e, bool fwd, Color c, int hops)
    {
        if (pulses.Count > 160) return;
        pulses.Add(new Pulse { E = e, Fwd = fwd, T = 0, Speed = 0.016f + (float)rnd.NextDouble() * 0.018f, C = c, Hops = hops });
    }

    // Tìm kiếm trên bản đồ: làm sáng các ý khớp, cho xung lan ra, xoay về phía ý khớp nhất
    public int Highlight(string query)
    {
        highlight = new HashSet<Node>();
        if (string.IsNullOrWhiteSpace(query)) { selected = null; Invalidate(); return 0; }
        var q = Brain.Tokens(query).ToList();
        foreach (var n in nodes.Values)
        {
            var t = new HashSet<string>(Brain.Tokens((n.Title ?? "") + " " + (n.Text ?? "") + " " + (n.Source ?? "")));
            int hit = q.Count(t.Contains);
            if (hit > 0 && hit * 2 >= q.Count) highlight.Add(n);
        }
        foreach (var n in highlight.Take(12)) Activate(n, 2);
        // Chọn luôn ý khớp nhất để mở thẻ thông tin (ưu tiên ghi nhớ còn đúng, rồi tới nơ-ron sáng nhất)
        selected = highlight.OrderByDescending(n => (n.Memory && IsMemoryKind(n.Kind) ? 2 : 0) + (n.Outdated ? -3 : 0) + n.Vital + n.Degree * 0.1).FirstOrDefault();
        Invalidate();
        return highlight.Count;
    }

    // ---------- Chuyển động ----------
    void Step()
    {
        time += 0.033f;
        foreach (var b in bulbs) b.Flash = Math.Max(0, b.Flash - 0.012f);
        foreach (var a in nodes.Values)
        {
            a.Flash = Math.Max(0, a.Flash - 0.02f);
            a.Birth = Math.Max(0, a.Birth - 0.025f);
        }
        // Xung điện tự phát theo độ mạnh khớp (bộ não không bao giờ im lặng hẳn)
        if (edges.Count > 0 && rnd.NextDouble() < Math.Min(0.6, 0.08 + edges.Count * 0.004))
        {
            var e = edges[rnd.Next(edges.Count)];
            if (rnd.NextDouble() < (e.Implicit ? 0.25 : 0.35 + e.W * 0.5)) Fire(e, rnd.Next(2) == 0, Color.FromArgb(140, 220, 255), rnd.NextDouble() < e.W ? 1 : 0);
        }
        for (int i = pulses.Count - 1; i >= 0; i--)
        {
            var p = pulses[i];
            p.T += p.Speed;
            if (p.T < 1) continue;
            pulses.RemoveAt(i);
            var target = p.Fwd ? p.E.B : p.E.A;
            target.Flash = Math.Max(target.Flash, 0.45f);
            if (p.Hops > 0)   // lan tiếp sang ý kế bên (liên tưởng)
                foreach (var e in edges.Where(e => (e.A == target || e.B == target) && e != p.E).OrderBy(x => rnd.Next()).Take(2))
                    if (rnd.NextDouble() < 0.35 + e.W * 0.3) Fire(e, e.A == target, p.C, p.Hops - 1);
        }
        if (!dragging) { userYaw *= 0.995f; userPitch *= 0.99f; }   // thả chuột thì từ từ trôi về góc nhìn nghiêng
    }

    // ---------- Chiếu 3D ra màn hình ----------
    float scale, cx, cy;
    float cosY, sinY, cosP, sinP;
    const float Cam = 2.8f;

    void SetupView(int W, int H)
    {
        scale = Math.Min(W / 2.3f, (H - 30) / 1.75f) * zoom;
        cx = W / 2f; cy = H / 2f + 0.04f * scale + (float)Math.Sin(time * 0.5) * 5;   // trôi lên xuống nhẹ
        yaw = (float)(Math.PI / 2 + 0.55 * Math.Sin(time * 0.11) + userYaw);           // lắc nhẹ quanh góc nhìn nghiêng
        pitch = (float)(-0.10 + 0.07 * Math.Sin(time * 0.083) + userPitch);
        cosY = (float)Math.Cos(yaw); sinY = (float)Math.Sin(yaw); cosP = (float)Math.Cos(pitch); sinP = (float)Math.Sin(pitch);
    }

    // Trả về điểm trên màn hình, hệ số phối cảnh f và độ sâu d (càng lớn càng xa)
    PointF Project(float x, float y, float z, float spin, out float f, out float d)
    {
        float cy0 = spin == 0 ? cosY : (float)Math.Cos(yaw * spin), sy0 = spin == 0 ? sinY : (float)Math.Sin(yaw * spin);
        float z0 = z - 0.02f;                                      // xoay quanh tâm khối não
        float x1 = x * cy0 + z0 * sy0, z1 = -x * sy0 + z0 * cy0;
        float y2 = y * cosP - z1 * sinP, z2 = y * sinP + z1 * cosP;
        f = Cam / (Cam + z2); d = z2;
        return new PointF(cx + x1 * f * scale, cy + y2 * f * scale);
    }

    // 0 (xa) .. 1 (gần)
    static float Near(float d) { return Math.Max(0f, Math.Min(1f, 0.5f - d * 0.65f)); }

    static Color KindColor(string k)
    {
        switch (k)
        {
            case "du-an": return Color.FromArgb(150, 225, 255);
            case "quyet-dinh": return Color.FromArgb(255, 200, 80);
            case "bai-hoc": return Color.FromArgb(255, 145, 85);
            case "su-that": return Color.FromArgb(110, 175, 255);
            case "viec-can-lam": return Color.FromArgb(255, 105, 175);
            case "file": return Color.FromArgb(170, 185, 210);
            default: return Color.FromArgb(215, 205, 255);
        }
    }

    static Color A(Color c, double alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)alpha)), c); }

    readonly Dictionary<int, Bitmap> glows = new Dictionary<int, Bitmap>();
    Bitmap Glow(Color c, double bright)
    {
        int level = Math.Max(0, Math.Min(3, (int)(bright * 4)));
        int key = (c.ToArgb() & 0xFFFFFF) * 4 + level;
        Bitmap bmp;
        if (glows.TryGetValue(key, out bmp)) return bmp;
        bmp = new Bitmap(48, 48);
        using (var g = Graphics.FromImage(bmp))
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(0, 0, 48, 48);
            using (var br = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(40 + level * 40, c), SurroundColors = new[] { Color.FromArgb(0, c) } }) g.FillPath(br, gp);
        }
        glows[key] = bmp;
        return bmp;
    }
    void DrawGlow(Graphics g, Color c, double bright, PointF p, float r) { g.DrawImage(Glow(c, bright), p.X - r, p.Y - r, 2 * r, 2 * r); }

    float Radius(Node n)
    {
        float r = n.Kind == "file" ? 2.0f : n.Kind == "du-an" ? 2.8f + Math.Min(3.5f, n.Degree * 0.5f) : 4.5f + (float)Math.Log(1 + n.Uses) * 1.5f;
        return r * (0.65f + 0.45f * n.Vital) * Math.Max(0.8f, scale / 380f) * n.F * (1 + 0.08f * (float)Math.Sin(time * 2 + n.Phase));
    }

    void RenderBackground(int W, int H)
    {
        if (bgCache != null) bgCache.Dispose();
        bgCache = new Bitmap(Math.Max(1, W), Math.Max(1, H));
        using (var g = Graphics.FromImage(bgCache))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(2, 4, 10));
            // Tinh vân xanh mờ phía sau bộ não
            foreach (var neb in new[] { new[] { 0f, -0.05f, 1.35f, 95f }, new[] { -0.45f, -0.25f, 0.7f, 45f }, new[] { 0.5f, 0.2f, 0.6f, 35f } })
                using (var gp = new GraphicsPath())
                {
                    float r = neb[2] * scale / zoom, x = W / 2f + neb[0] * scale / zoom, y = H / 2f + neb[1] * scale / zoom;
                    gp.AddEllipse(x - r, y - r * 0.8f, 2 * r, 1.6f * r);
                    using (var br = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)neb[3], 25, 60, 130), SurroundColors = new[] { Color.FromArgb(0, 2, 4, 10) } }) g.FillPath(br, gp);
                }
        }
    }

    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics;
        var watch = Stopwatch.StartNew();
        int W = ClientSize.Width, H = ClientSize.Height;
        SetupView(W, H);
        if (bgCache == null || bgCache.Width != W || bgCache.Height != H) RenderBackground(W, H);
        g.DrawImageUnscaled(bgCache, 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.Low;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float f, d;

        // Mạng lưới sao phía sau: xoay chậm hơn bộ não (thị sai tạo chiều sâu)
        var sp = new PointF[stars.Count]; var sn = new float[stars.Count];
        for (int i = 0; i < stars.Count; i++) { sp[i] = Project(stars[i].X, stars[i].Y, stars[i].Z, 0.35f, out f, out d); sn[i] = Near(d * 0.6f); }
        using (var pen = new Pen(Color.FromArgb(55, 100, 160, 235), 0.8f))
            foreach (var l in starLinks) { pen.Color = Color.FromArgb((int)(18 + 45 * (sn[l[0]] + sn[l[1]]) / 2), 100, 160, 235); g.DrawLine(pen, sp[l[0]], sp[l[1]]); }
        var starGlow = Color.FromArgb(150, 205, 255);
        using (var br = new SolidBrush(Color.White))
            for (int i = 0; i < stars.Count; i++)
            {
                float tw = 0.7f + 0.3f * (float)Math.Sin(time * 1.6f + i), r = 0.8f + 1.8f * sn[i];
                if (sn[i] > 0.35f) DrawGlow(g, starGlow, tw * sn[i] * 0.6, sp[i], r * 4);
                br.Color = Color.FromArgb((int)(220 * tw * (0.35f + 0.65f * sn[i])), 225, 240, 255); g.FillEllipse(br, sp[i].X - r, sp[i].Y - r, 2 * r, 2 * r);
            }

        // Bộ não: lưới mảnh nối các hạt, gần sáng xa mờ
        for (int i = 0; i < shell.Count; i++) { shellP[i] = Project(shell[i].X, shell[i].Y, shell[i].Z, 0, out shellF[i], out shellD[i]); }
        var buckets = new List<PointF>[6];
        for (int k = 0; k < 6; k++) buckets[k] = new List<PointF>();
        foreach (var l in shellLinks)
        {
            float nr = (Near(shellD[l[0]]) + Near(shellD[l[1]])) / 2;
            int b = Math.Min(5, (int)(nr * 6));
            buckets[b].Add(shellP[l[0]]); buckets[b].Add(shellP[l[1]]);
        }
        for (int k = 0; k < 6; k++)
            using (var pen = new Pen(Color.FromArgb(14 + k * 16, 110, 185, 255), 0.7f + k * 0.12f))
                for (int i = 0; i + 1 < buckets[k].Count; i += 2) g.DrawLine(pen, buckets[k][i], buckets[k][i + 1]);
        var shellGlow = Color.FromArgb(120, 195, 255);
        float ps = Math.Max(0.8f, scale / 420f);
        using (var br = new SolidBrush(Color.White))
            for (int i = 0; i < shell.Count; i++)
            {
                float nr = Near(shellD[i]), r = (0.6f + 1.3f * nr) * ps;
                if (nr > 0.55f) DrawGlow(g, shellGlow, nr * 0.7, shellP[i], r * 2.6f);
                br.Color = Color.FromArgb((int)(60 + 170 * nr), 200, 232, 255); g.FillRectangle(br, shellP[i].X - r * 0.6f, shellP[i].Y - r * 0.6f, r * 1.2f, r * 1.2f);
            }

        DrawBulbs(g);

        // Nơ-ron thật: chiếu ra màn hình rồi vẽ từ xa tới gần
        foreach (var n in nodes.Values)
            n.P = Project(n.X, n.Y + 0.006f * (float)Math.Sin(time * 0.9f + n.Phase), n.Z, 0, out n.F, out n.D);
        var focus = selected ?? hover;
        foreach (var e in edges)
        {
            bool on = focus != null && (e.A == focus || e.B == focus);
            double nr = (Near(e.A.D) + Near(e.B.D)) / 2;
            double alpha = (e.Implicit ? 14 : 30 + Math.Min(1, e.W) * 80) * (0.4 + 0.6 * nr);
            if (on) alpha = 220;
            using (var pen = new Pen(A(on ? Color.FromArgb(255, 230, 160) : Color.FromArgb(150, 210, 255), alpha), (e.Implicit ? 0.7f : 0.9f + Math.Min(2.5f, e.W * 1.2f)) * (float)(0.6 + 0.6 * nr)))
            {
                if (e.Implicit) pen.DashStyle = DashStyle.Dot;
                g.DrawLine(pen, e.A.P, e.B.P);
            }
        }
        foreach (var p in pulses)
        {
            var a = p.Fwd ? p.E.A : p.E.B; var b = p.Fwd ? p.E.B : p.E.A;
            for (int k = 0; k < 5; k++)
            {
                float t = Math.Max(0, p.T - k * 0.025f);
                var pt = new PointF(a.P.X + (b.P.X - a.P.X) * t, a.P.Y + (b.P.Y - a.P.Y) * t);
                float r = (3.4f - k * 0.55f) * (a.F + (b.F - a.F) * t);
                using (var br = new SolidBrush(A(p.C, 235 - k * 45))) g.FillEllipse(br, pt.X - r, pt.Y - r, 2 * r, 2 * r);
            }
        }
        foreach (var n in nodes.Values.OrderByDescending(x => x.D))
        {
            var c = KindColor(n.Kind);
            float r = Radius(n) * (1 - n.Birth * 0.7f), nr = Near(n.D);
            bool hl = highlight.Contains(n);
            double bright = Math.Min(1, (0.35 + n.Vital * 0.55 + n.Flash * 0.6 + (hl ? 0.5 : 0)) * (0.45 + 0.55 * nr)) * (n.Outdated ? 0.5 : 1);
            float gr = r * (2.8f + n.Flash * 3f + (hl ? 1.5f : 0));
            DrawGlow(g, c, bright, n.P, gr);
            using (var br = new SolidBrush(A(c, 110 + 145 * bright))) g.FillEllipse(br, n.P.X - r, n.P.Y - r, 2 * r, 2 * r);
            using (var br = new SolidBrush(A(Color.White, 110 * bright))) g.FillEllipse(br, n.P.X - r * 0.4f, n.P.Y - r * 0.4f, r * 0.8f, r * 0.8f);
            if (n.Flash > 0.05f)
                using (var pen = new Pen(A(Color.FromArgb(255, 240, 180), 255 * n.Flash), 1.5f))
                { float rr = r + (1 - n.Flash) * 24; g.DrawEllipse(pen, n.P.X - rr, n.P.Y - rr, 2 * rr, 2 * rr); }
            if (n == focus)
                using (var pen = new Pen(Color.White, 1.5f)) g.DrawEllipse(pen, n.P.X - r - 4, n.P.Y - r - 4, 2 * r + 8, 2 * r + 8);
        }

        // Nhãn: ý đang chọn/được tìm thấy + vài ý nổi bật ở phía gần, không đè lên nhau
        var labeled = nodes.Values.Where(n => n == focus || highlight.Contains(n) || n.Flash > 0.6f)
            .Concat(nodes.Values.Where(n => n.Kind != "file" && Near(n.D) > 0.45f)
                                .OrderByDescending(n => (n.Memory && IsMemoryKind(n.Kind) ? 3 : 0) + n.Vital * 2 + n.Degree * 0.3).Take(9))
            .Distinct().Take(28);
        var taken = new List<RectangleF>();
        foreach (var n in labeled)
        {
            var text = Short(n.Title, 34);
            var sz = g.MeasureString(text, Font);
            float rr = Radius(n), lx = n.P.X + rr + 5, ly = n.P.Y - sz.Height / 2;
            if (lx + sz.Width > W - 6) lx = n.P.X - rr - 5 - sz.Width;
            var rect = new RectangleF(lx - 2, ly, sz.Width + 4, sz.Height);
            if (n != focus && taken.Any(t => t.IntersectsWith(rect))) continue;
            taken.Add(rect);
            using (var br = new SolidBrush(Color.FromArgb(150, 4, 8, 20))) g.FillRectangle(br, rect);
            using (var br = new SolidBrush(A(Color.White, n == focus || highlight.Contains(n) ? 255 : 120 + 110 * Near(n.D)))) g.DrawString(text, Font, br, lx, ly);
        }

        DrawLegend(g, H);
        if (selected != null) DrawCard(g, selected, W);
        string hint = cachedProjects == null ? "Đang nạp bộ não…"
            : selected == null && nodes.Values.Count(n => n.Memory && IsMemoryKind(n.Kind)) == 0 ? "Chưa có ghi nhớ nào: khi agent dùng \"remember\", ghi nhớ sẽ mọc thành nơ-ron sáng ở thuỳ trán."
            : "Kéo chuột để xoay · lăn chuột để phóng to · nhấp một nơ-ron để xem";
        using (var br = new SolidBrush(Color.FromArgb(160, 190, 205, 240))) { var sz = g.MeasureString(hint, Font); g.DrawString(hint, Font, br, (W - sz.Width) / 2, H - 44); }
        // Máy chậm thì giảm nhịp khung hình cho đều, thay vì để giật
        frame.Interval = Math.Max(33, Math.Min(80, (int)watch.ElapsedMilliseconds + 12));
    }

    // Bóng đèn bay quanh bộ não, nối vào não bằng dây dẫn; vừa dùng thì sáng vàng và có dòng điện chạy vào não
    void DrawBulbs(Graphics g)
    {
        if (bulbs.Count == 0) return;
        float f, d, fc, dc;
        var core = Project(0, -0.12f, 0.02f, 0, out fc, out dc);
        var warm = Color.FromArgb(255, 214, 120); var cool = Color.FromArgb(140, 170, 220);
        for (int i = 0; i < bulbs.Count; i++)
        {
            var b = bulbs[i];
            double ang = Math.PI * 2 * i / bulbs.Count + time * 0.06;
            float x = (float)Math.Cos(ang) * 1.18f, z = (float)Math.Sin(ang) * 1.18f, y = -0.08f + 0.22f * (float)Math.Sin(ang * 2 + i);
            var p = Project(x, y, z, 0, out f, out d);
            bool live = (DateTime.Now - b.LastSeen).TotalMinutes < 2 || b.Flash > 0.05f;
            float glow = live ? 0.6f + 0.4f * Math.Max(b.Flash, 0.5f + 0.5f * (float)Math.Sin(time * 3 + i)) : 0.25f;
            var c = live ? warm : cool;
            // Dây dẫn vào bộ não
            using (var pen = new Pen(A(c, live ? 110 : 35), live ? 1.4f : 0.8f) { DashStyle = live ? DashStyle.Solid : DashStyle.Dot }) g.DrawLine(pen, p, core);
            if (live)   // dòng điện chạy vào não
                for (int k = 0; k < 3; k++)
                {
                    float t = (float)((time * 0.6 + k / 3.0 + i * 0.17) % 1.0);
                    var q = new PointF(p.X + (core.X - p.X) * t, p.Y + (core.Y - p.Y) * t);
                    using (var br = new SolidBrush(A(warm, 230 * (1 - t * 0.6)))) g.FillEllipse(br, q.X - 2.4f, q.Y - 2.4f, 4.8f, 4.8f);
                }
            float r = (5 + Math.Min(5, (float)Math.Log(1 + b.Uses) * 1.5f)) * f * Math.Max(0.8f, scale / 400f);
            DrawGlow(g, c, glow, p, r * (3.2f + b.Flash * 3));
            using (var br = new SolidBrush(A(c, 120 + 135 * glow))) g.FillEllipse(br, p.X - r, p.Y - r, 2 * r, 2 * r);
            using (var br = new SolidBrush(A(Color.White, 200 * glow))) g.FillEllipse(br, p.X - r * 0.45f, p.Y - r * 0.6f, r * 0.9f, r * 0.9f);
            var name = Short(b.Name, 22);
            var sz = g.MeasureString(name, Font);
            using (var br = new SolidBrush(A(live ? warm : Color.FromArgb(170, 185, 215), live ? 255 : 170))) g.DrawString(name, Font, br, p.X - sz.Width / 2, p.Y + r + 4);
        }
    }

    static string Short(string s, int n) { s = s ?? ""; return s.Length > n ? s.Substring(0, n - 1) + "…" : s; }

    void DrawLegend(Graphics g, int H)
    {
        var items = new[] { "du-an", "quyet-dinh", "bai-hoc", "su-that", "viec-can-lam", "ghi-chu", "file" };
        float x = 12, y = H - 22;
        foreach (var k in items)
        {
            using (var br = new SolidBrush(KindColor(k))) g.FillEllipse(br, x, y + 4, 9, 9);
            var t = global::Memory.Label(k);
            using (var br = new SolidBrush(Color.FromArgb(190, 210, 220, 255))) g.DrawString(t, Font, br, x + 12, y);
            x += 22 + g.MeasureString(t, Font).Width;
        }
    }

    void DrawCard(Graphics g, Node n, int W)
    {
        float cw = Math.Min(360, W * 0.38f), x = W - cw - 12, y = 12;
        var lines = new List<Tuple<string, Font, Color>>();
        var bold = new Font("Segoe UI", 10f, FontStyle.Bold);
        lines.Add(Tuple.Create(global::Memory.Label(n.Kind).ToUpper() + (n.Outdated ? " · ĐÃ CŨ" : ""), Font, KindColor(n.Kind)));
        lines.Add(Tuple.Create(n.Title ?? "", bold, Color.White));
        if (!string.IsNullOrEmpty(n.Text)) lines.Add(Tuple.Create(Short(n.Text.Trim(), 420), Font, Color.FromArgb(215, 220, 240)));
        if (!string.IsNullOrEmpty(n.Source)) lines.Add(Tuple.Create("Nguồn: " + n.Source, Font, Color.FromArgb(160, 190, 230)));
        var links = edges.Where(e => !e.Implicit && (e.A == n || e.B == n)).OrderByDescending(e => e.W).Take(6)
                         .Select(e => "↔ " + Short((e.A == n ? e.B : e.A).Title, 40) + "  (" + (e.Why ?? "") + ")").ToList();
        if (links.Count > 0) lines.Add(Tuple.Create("Nối với:\n" + string.Join("\n", links), Font, Color.FromArgb(200, 220, 190)));
        lines.Add(Tuple.Create((n.Uses > 0 ? "Được nhớ lại " + n.Uses + " lần · " : "") + "Nhấp đúp để mở vị trí", Font, Color.FromArgb(140, 160, 190)));
        var sizes = lines.Select(l => g.MeasureString(l.Item1, l.Item2, (int)cw - 20)).ToList();
        float h = 14 + sizes.Sum(s => s.Height + 4);
        using (var br = new SolidBrush(Color.FromArgb(225, 10, 16, 36))) g.FillRectangle(br, x, y, cw, h);
        using (var pen = new Pen(A(KindColor(n.Kind), 160))) g.DrawRectangle(pen, x, y, cw, h);
        float yy = y + 8;
        for (int i = 0; i < lines.Count; i++)
        {
            using (var br = new SolidBrush(lines[i].Item3)) g.DrawString(lines[i].Item1, lines[i].Item2, br, new RectangleF(x + 10, yy, cw - 20, sizes[i].Height + 2));
            yy += sizes[i].Height + 4;
        }
        bold.Dispose();
    }

    // ---------- Chuột: kéo để xoay, lăn để phóng to, nhấp để xem ----------
    Node NodeAt(Point pt)
    {
        Node best = null;   // trúng nhiều nơ-ron chồng nhau thì lấy cái gần mắt nhất
        foreach (var n in nodes.Values)
        {
            float d = (n.P.X - pt.X) * (n.P.X - pt.X) + (n.P.Y - pt.Y) * (n.P.Y - pt.Y), r = Radius(n) + 6;
            if (d < r * r && (best == null || n.D < best.D)) best = n;
        }
        return best;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left) { dragging = true; dragged = false; dragFrom = e.Location; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Cursor = Cursors.Default; base.OnMouseUp(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging)
        {
            int dx = e.X - dragFrom.X, dy = e.Y - dragFrom.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 3) dragged = true;
            if (dragged)
            {
                userYaw += dx * 0.008f;
                userPitch = Math.Max(-0.9f, Math.Min(0.9f, userPitch + dy * 0.006f));
                dragFrom = e.Location;
                Cursor = Cursors.SizeAll;
                return;
            }
        }
        var h = NodeAt(e.Location);
        if (h != hover) { hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; }
        base.OnMouseMove(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        zoom = Math.Max(0.6f, Math.Min(2.2f, zoom * (e.Delta > 0 ? 1.1f : 1 / 1.1f)));
        base.OnMouseWheel(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (dragged) { dragged = false; return; }
        var n = NodeAt(e.Location);
        selected = n;
        if (n != null) Activate(n, 2);
        base.OnMouseClick(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        var n = NodeAt(e.Location);
        if (n != null && !string.IsNullOrEmpty(n.Source))
            try
            {
                if (File.Exists(n.Source)) Process.Start("explorer.exe", "/select,\"" + n.Source + "\"");
                else if (Directory.Exists(n.Source)) Process.Start("explorer.exe", "\"" + n.Source + "\"");
            }
            catch { }
        base.OnMouseDoubleClick(e);
    }
}

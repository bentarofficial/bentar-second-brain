// Phòng làm việc: văn phòng isometric sống động, mỗi agent là một nhân viên có bàn, ghế, máy tính.
// Người dùng là Chỉ huy (phòng kính), Second Brain là Trưởng phòng/thư ký, Kho tri thức là trạm phát + chỉ mục.
// Chuyển động gắn với HOẠT ĐỘNG THẬT (hoat-dong.jsonl): agent gọi công cụ thì nhân viên gõ phím, màn hình chạy chữ,
// hồ sơ bay từ Kho tri thức tới bàn; Claude làm xong thì Trưởng phòng mang báo cáo tới bàn Chỉ huy.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

class OfficeView : Control
{
    // ---------- Nhân sự ----------
    public class Staff
    {
        public string Key, Name, Role, LinkName; public string[] Clients; public Color Shirt, Hair; public bool Skirt;
        public float DeskX, DeskY;                 // toạ độ bàn (ô sàn)
        public bool Installed = true, Connected, Known;   // Known: đã lấy được trạng thái kết nối
        public DateTime LastSeen = DateTime.MinValue, LastWork = DateTime.MinValue;
        public string LastAction; public int Uses;
        public string Bubble; public DateTime BubbleUntil;
        public RectangleF Hit;
        public bool Working { get { return (DateTime.Now - LastWork).TotalSeconds < 20; } }
        public bool Online { get { return (DateTime.Now - LastSeen).TotalMinutes < 30; } }
        public bool Present { get { return Key == "brain" || Key == "tools" || Connected || Online; } }
    }

    public readonly List<Staff> Team = new List<Staff>();
    public Staff Selected; Staff hover;
    public event Action<Staff> SelectionChanged;
    public event Action TeamChanged;

    readonly Timer frame = new Timer { Interval = 33 }, poll = new Timer { Interval = 1500 }, linkPoll = new Timer { Interval = 60000 };
    readonly DateTime born = DateTime.Now;
    float T { get { return (float)(DateTime.Now - born).TotalSeconds; } }
    DateTime lastSeenEvent = DateTime.Now.AddSeconds(-5);
    int files; bool pollBusy;

    class Flyer { public Staff To; public float Start; public Color C; }
    readonly List<Flyer> flyers = new List<Flyer>();
    // Trưởng phòng mang báo cáo tới Chỉ huy
    float walkStart = -100; string walkReport;

    const float W = 17, H = 13;   // kích thước sàn (ô)
    float tile = 60, ox, oy;

    public OfficeView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Func<string, string, string, string, string[], Color, Color, float, float, bool, Staff> S = (k, n, r, link, cl, shirt, hair, x, y, skirt) =>
            new Staff { Key = k, Name = n, Role = r, LinkName = link, Clients = cl, Shirt = shirt, Hair = hair, DeskX = x, DeskY = y, Skirt = skirt };
        Team.Add(S("brain", "Second Brain", L.T("Manager · thinks with your Claude plan", "Trưởng phòng · suy nghĩ bằng gói Claude của bạn"), null, new[] { "Second Brain" }, Color.FromArgb(99, 102, 241), Color.FromArgb(40, 30, 30), 7.3f, 1.9f, true));
        Team.Add(S("claude", "Claude Code", L.T("Senior engineer", "Kỹ sư chính"), "Claude Code", new[] { "claude-code" }, Color.FromArgb(217, 119, 87), Color.FromArgb(60, 40, 25), 6.2f, 6.0f, false));
        Team.Add(S("codex", "Codex", L.T("Engineer", "Kỹ sư"), "Codex", new[] { "codex", "codex-mcp-client" }, Color.FromArgb(16, 163, 127), Color.FromArgb(25, 25, 30), 9.7f, 6.0f, true));
        Team.Add(S("gemini", "Gemini CLI", L.T("Engineer", "Kỹ sư"), "Gemini CLI", new[] { "gemini-cli", "gemini" }, Color.FromArgb(66, 133, 244), Color.FromArgb(90, 60, 30), 13.2f, 6.0f, false));
        Team.Add(S("cursor", "Cursor", L.T("Editor specialist", "Chuyên viên biên tập code"), "Cursor", new[] { "cursor" }, Color.FromArgb(70, 75, 90), Color.FromArgb(20, 20, 20), 6.2f, 9.3f, true));
        Team.Add(S("windsurf", "Windsurf", L.T("Editor specialist", "Chuyên viên biên tập code"), "Windsurf", new[] { "windsurf" }, Color.FromArgb(20, 160, 150), Color.FromArgb(120, 80, 40), 9.7f, 9.3f, false));
        Team.Add(S("cowork", "Claude Desktop · Cowork", L.T("Office & documents (Cowork)", "Văn phòng & tài liệu (Cowork)"), "Claude Desktop", new[] { "claude-ai", "claude desktop", "claude-desktop" }, Color.FromArgb(204, 120, 92), Color.FromArgb(70, 45, 30), 13.2f, 9.3f, true));
        Team.Add(S("tools", L.T("Tools & scripts", "Công cụ & script"), L.T("Automation (HTTP, sb command)", "Tự động hoá (HTTP, lệnh sb)"), null, new[] { "cli", "http", "rest", "n8n", "make", "agent", "protocol-test" }, Color.FromArgb(139, 92, 246), Color.FromArgb(50, 50, 60), 2.0f, 9.6f, false));

        frame.Tick += (s, e) => { if (Visible) Invalidate(); };
        poll.Tick += (s, e) => Poll();
        linkPoll.Tick += (s, e) => PollLinks();
        VisibleChanged += (s, e) => { frame.Enabled = poll.Enabled = linkPoll.Enabled = Visible; if (Visible) { Poll(); PollLinks(); } };
        // Chuột trái kéo: xoay 360° (ngang) + nghiêng (dọc) · chuột phải/giữa kéo: di chuyển · cuộn: phóng to/thu nhỏ · nhấp đúp: về góc nhìn gốc
        Point last = Point.Empty, downAt = Point.Empty; MouseButtons btn = MouseButtons.None;
        MouseEnter += (s, e) => Focus();
        MouseDown += (s, e) => { btn = e.Button; downAt = last = e.Location; dragging = false; };
        MouseMove += (s, e) =>
        {
            if (btn != MouseButtons.None)
            {
                if (!dragging && Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > 4) dragging = true;
                if (dragging)
                {
                    int dx = e.X - last.X, dy = e.Y - last.Y;
                    if (btn == MouseButtons.Left && ModifierKeys != Keys.Shift)
                    { yaw += dx * 0.01f; elev = Math.Max(0.2f, Math.Min(1.35f, elev + dy * 0.006f)); Cursor = Cursors.SizeAll; }
                    else { panX += dx; panY += dy; Cursor = Cursors.SizeAll; }
                    last = e.Location; Invalidate(); return;
                }
            }
            var h = HitTest(e.Location);
            bool overCtl = controls.Any(c => c.Key.Contains(e.Location));
            if (h != hover) hover = h;
            Cursor = h != null || overCtl ? Cursors.Hand : Cursors.Default;
        };
        MouseUp += (s, e) =>
        {
            bool wasDrag = dragging; dragging = false; btn = MouseButtons.None; Cursor = Cursors.Default;
            if (wasDrag || e.Button != MouseButtons.Left) return;
            var ctl = controls.FirstOrDefault(c => c.Key.Contains(e.Location));
            if (ctl.Value != null) { ctl.Value(); Invalidate(); return; }
            Selected = HitTest(e.Location); if (SelectionChanged != null) SelectionChanged(Selected);
        };
        MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left && HitTest(e.Location) == null && !controls.Any(c => c.Key.Contains(e.Location))) ResetView(); };
        MouseWheel += (s, e) => ZoomAt(e.Location, (float)Math.Pow(1.15, e.Delta / 120.0));
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Left) yaw -= 0.15f; else if (e.KeyCode == Keys.Right) yaw += 0.15f;
            else if (e.KeyCode == Keys.Up) elev = Math.Min(1.35f, elev + 0.08f); else if (e.KeyCode == Keys.Down) elev = Math.Max(0.2f, elev - 0.08f);
            else if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add) ZoomAt(new Point(Width / 2, Height / 2), 1.2f);
            else if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract) ZoomAt(new Point(Width / 2, Height / 2), 1 / 1.2f);
            else if (e.KeyCode == Keys.Home) ResetView();
        };
    }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }

    bool dragging;
    // Phóng to quanh con trỏ: điểm dưới chuột giữ nguyên chỗ
    void ZoomAt(Point m, float f)
    {
        float nz = Math.Max(0.35f, Math.Min(5f, zoom * f)); f = nz / zoom;
        float cx = Width / 2f, cy = Height * 0.56f;
        panX = m.X - cx - (m.X - cx - panX) * f; panY = m.Y - cy - (m.Y - cy - panY) * f;
        zoom = nz; Invalidate();
    }

    // Nút điều khiển nổi góc dưới phải: xoay trái/phải, thu nhỏ/phóng to, tự xoay, về góc gốc
    readonly List<KeyValuePair<Rectangle, Action>> controls = new List<KeyValuePair<Rectangle, Action>>();
    void DrawControls(Graphics g)
    {
        controls.Clear();
        var items = new List<Tuple<string, string, Action>> {
            Tuple.Create("⟲", L.T("Rotate left", "Xoay trái"), (Action)(() => yaw -= (float)(Math.PI / 4))),
            Tuple.Create("⟳", L.T("Rotate right", "Xoay phải"), (Action)(() => yaw += (float)(Math.PI / 4))),
            Tuple.Create("−", L.T("Zoom out", "Thu nhỏ"), (Action)(() => ZoomAt(new Point(Width / 2, Height / 2), 1 / 1.25f))),
            Tuple.Create("+", L.T("Zoom in", "Phóng to"), (Action)(() => ZoomAt(new Point(Width / 2, Height / 2), 1.25f))),
            Tuple.Create(spin ? "❚❚" : "▶", L.T("Auto-rotate", "Tự xoay"), (Action)(() => spin = !spin)),
            Tuple.Create("⌂", L.T("Reset view", "Góc nhìn gốc"), (Action)ResetView) };
        int sz = 34, gap = 6, x = Width - 14 - items.Count * (sz + gap) + gap, y = Height - sz - 14;
        using (var f = new Font("Segoe UI Symbol", 12f, FontStyle.Bold))
        using (var small = new Font("Segoe UI", 8f))
        {
            var bg = new RectangleF(x - 8, y - 8, items.Count * (sz + gap) - gap + 16, sz + 16);
            using (var path = RoundRect(bg, 12)) using (var b = new SolidBrush(Color.FromArgb(200, 15, 21, 41))) g.FillPath(b, path);
            foreach (var it in items)
            {
                var r = new Rectangle(x, y, sz, sz);
                using (var path = RoundRect(r, 9)) using (var b = new SolidBrush(Color.FromArgb(it.Item1 == "❚❚" ? 120 : 60, 34, 211, 238))) g.FillPath(b, path);
                TextRenderer.DrawText(g, it.Item1, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                controls.Add(new KeyValuePair<Rectangle, Action>(r, it.Item3));
                x += sz + gap;
            }
            // Gợi ý thao tác
            var hintText = L.T("Drag to rotate · Right-drag to move · Scroll to zoom · Double-click to reset", "Kéo để xoay · Chuột phải kéo để di chuyển · Cuộn để phóng to · Nhấp đúp về gốc");
            var hs = g.MeasureString(hintText, small);
            using (var b = new SolidBrush(Color.FromArgb(170, 120, 130, 160))) g.DrawString(hintText, small, b, Width - 14 - hs.Width, y - 26);
        }
    }

    Staff HitTest(Point p) { return Team.LastOrDefault(s => s.Hit.Contains(p)); }

    // ---------- Dữ liệu thật ----------
    async void Poll()
    {
        if (pollBusy) return; pollBusy = true;
        try
        {
            var recent = await Task.Run(() => Activity.Recent(400));
            try { files = Brain.FileCount; } catch { }
            foreach (var s in Team) { s.Uses = 0; }
            foreach (var e in recent.OrderBy(x => x.T))
            {
                var st = Who(e.Client, e.Channel);
                if (st == null) continue;
                if (e.T > st.LastSeen) st.LastSeen = e.T;
                bool work = e.Action != "cắm vào" && e.Action != "bật" && e.Channel != "heal";
                if (work) { st.Uses++; if (e.T > st.LastWork) { st.LastWork = e.T; st.LastAction = e.Action + (string.IsNullOrEmpty(e.Detail) ? "" : ": " + e.Detail); } }
                // Sự kiện mới: tạo chuyển động
                if (e.T > lastSeenEvent && work)
                {
                    st.Bubble = Short(e.Action, e.Detail); st.BubbleUntil = DateTime.Now.AddSeconds(5);
                    if (st.Key != "brain" || e.Channel != "engine") flyers.Add(new Flyer { To = st, Start = T, C = st.Shirt });
                    if (st.Key == "brain" && e.Channel == "engine" && (e.Action == "xong" || e.Action == "done" || e.Action == "lỗi" || e.Action == "error"))
                    { walkStart = T; walkReport = e.Action == "xong" || e.Action == "done" ? L.T("Report: done ✓", "Báo cáo: đã xong ✓") : L.T("Report: a problem", "Báo cáo: có lỗi"); }
                }
            }
            if (recent.Count > 0) lastSeenEvent = recent.Max(x => x.T);
            if (flyers.Count > 12) flyers.RemoveRange(0, flyers.Count - 12);
        }
        catch { }
        pollBusy = false;
    }

    public async void PollLinks()
    {
        try
        {
            var list = await Task.Run(() => AgentLinks.List());
            foreach (var s in Team.Where(x => x.LinkName != null))
            {
                var a = list.Where(x => x.Name == s.LinkName || x.Name.StartsWith(s.LinkName)).OrderByDescending(x => x.Connected).FirstOrDefault();
                s.Known = true; s.Installed = a != null && a.Installed; s.Connected = a != null && a.Connected;
            }
            if (TeamChanged != null) TeamChanged();
        }
        catch { }
    }

    Staff Who(string client, string channel)
    {
        var c = (client ?? "").ToLowerInvariant();
        if (c == "second brain" && channel == "engine") return Team[0];
        if (c == "trạm phát" || c == "sao lưu" || c == "second brain") return null;
        foreach (var s in Team.Skip(1)) if (s.Clients.Any(k => c == k || c.StartsWith(k))) return s;
        return Team.First(s => s.Key == "tools");
    }

    static string Short(string action, string detail)
    {
        var s = action + (string.IsNullOrEmpty(detail) ? "" : ": " + detail);
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 38 ? s.Substring(0, 38) + "…" : s;
    }

    // ---------- Hình học isometric ----------
    // Camera: xoay quanh tâm phòng (yaw, 360°), nghiêng (elev), phóng to (zoom), kéo dịch (pan)
    const float CX = W / 2, CY = H / 2;
    float yaw, elev = (float)(Math.PI / 6), zoom = 1, panX, panY; bool spin;
    float cosY = 1, sinY, ySc = 0.5f, zSc = 1;
    void Camera() { cosY = (float)Math.Cos(yaw); sinY = (float)Math.Sin(yaw); ySc = (float)Math.Sin(elev); zSc = (float)(Math.Cos(elev) / Math.Cos(Math.PI / 6)); }
    void Rot(float x, float y, out float rx, out float ry) { float dx = x - CX, dy = y - CY; rx = CX + dx * cosY - dy * sinY; ry = CY + dx * sinY + dy * cosY; }
    PointF P(float x, float y, float z = 0) { float rx, ry; Rot(x, y, out rx, out ry); return new PointF(ox + (rx - ry) * tile / 2, oy + (rx + ry) * tile / 2 * ySc - z * tile / 2 * zSc); }
    float Depth(float x, float y) { float rx, ry; Rot(x, y, out rx, out ry); return rx + ry; }
    /// <summary>Mặt có pháp tuyến (nx, ny) có quay về phía người xem không.</summary>
    bool Faces(float nx, float ny) { return (nx * cosY - ny * sinY) + (nx * sinY + ny * cosY) > 0.02f; }
    float FaceShade(float nx, float ny) { float rx = nx * cosY - ny * sinY, ry = nx * sinY + ny * cosY; return -0.25f - 0.07f * (rx - ry); }
    public void ResetView() { yaw = 0; elev = (float)(Math.PI / 6); zoom = 1; panX = panY = 0; }

    static int Clamp(int v) { return Math.Max(0, Math.Min(255, v)); }
    static Color Shade(Color c, float f)
    {
        Func<int, int> k = v => Math.Max(0, Math.Min(255, f >= 0 ? (int)(v + (255 - v) * f) : (int)(v * (1 + f))));
        return Color.FromArgb(c.A, k(c.R), k(c.G), k(c.B));
    }

    void Poly(Graphics g, Color c, params PointF[] pts) { using (var b = new SolidBrush(c)) g.FillPolygon(b, pts); }

    // Hộp 3D: vẽ mặt trên, mặt +y (trái-trước) và mặt +x (phải-trước)
    void Box(Graphics g, float x, float y, float z, float dx, float dy, float dz, Color c, bool outline = false)
    {
        var top = new[] { P(x, y, z + dz), P(x + dx, y, z + dz), P(x + dx, y + dy, z + dz), P(x, y + dy, z + dz) };
        // 4 mặt bên: chỉ vẽ mặt đang quay về người xem (nhìn từ hướng nào cũng đúng)
        var faces = new[] {
            Tuple.Create(0f, 1f, new[] { P(x, y + dy, z), P(x + dx, y + dy, z), P(x + dx, y + dy, z + dz), P(x, y + dy, z + dz) }),
            Tuple.Create(1f, 0f, new[] { P(x + dx, y, z), P(x + dx, y + dy, z), P(x + dx, y + dy, z + dz), P(x + dx, y, z + dz) }),
            Tuple.Create(0f, -1f, new[] { P(x, y, z), P(x + dx, y, z), P(x + dx, y, z + dz), P(x, y, z + dz) }),
            Tuple.Create(-1f, 0f, new[] { P(x, y, z), P(x, y + dy, z), P(x, y + dy, z + dz), P(x, y, z + dz) }) };
        using (var pen = outline ? new Pen(Color.FromArgb(40, 0, 0, 0)) : null)
        {
            foreach (var f in faces) if (Faces(f.Item1, f.Item2))
            {
                // Mặt bên: sáng ở trên, tối dần xuống chân (ánh sáng trần + tắc nghẽn môi trường)
                var fc = Shade(c, FaceShade(f.Item1, f.Item2)); var pts = f.Item3;
                float y0 = Math.Min(pts[2].Y, pts[3].Y) - 1, y1 = Math.Max(pts[0].Y, pts[1].Y) + 1;
                if (y1 - y0 > 2) using (var b = new LinearGradientBrush(new PointF(0, y0), new PointF(0, y1), Shade(fc, 0.08f), Shade(fc, -0.22f))) g.FillPolygon(b, pts);
                else Poly(g, fc, pts);
                if (pen != null) g.DrawPolygon(pen, pts);
            }
            Poly(g, Shade(c, 0.12f), top); if (pen != null) g.DrawPolygon(pen, top);
            // Viền sáng mép trên: cạnh bắt sáng làm khối nổi hẳn lên
            if (dz > 0.05f && tile > 20) using (var hl = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                foreach (var f in faces) if (Faces(f.Item1, f.Item2)) g.DrawLine(hl, f.Item3[2], f.Item3[3]);
        }
    }

    void Shadow(Graphics g, float x, float y, float rx, float ry, int a = 50)
    {
        // Bóng mềm: vài lớp elip đồng tâm, mờ dần ra mép (giả lập bóng đổ khuếch tán)
        var c = P(x, y); float w = (rx + ry) * tile / 2, h = (rx + ry) * tile / 2 * ySc;
        for (int i = 0; i < 4; i++)
        {
            float k = 1.25f - i * 0.17f;
            using (var b = new SolidBrush(Color.FromArgb(a / 3, 20, 16, 30))) g.FillEllipse(b, c.X - w * k / 2, c.Y - h * k / 2, w * k, h * k);
        }
    }

    // Bộ vẽ theo chiều sâu (thuật toán hoạ sĩ): vật ở xa vẽ trước
    readonly List<KeyValuePair<float, Action<Graphics>>> queue = new List<KeyValuePair<float, Action<Graphics>>>();
    void Q(float x, float y, Action<Graphics> a) { queue.Add(new KeyValuePair<float, Action<Graphics>>(Depth(x, y), a)); }
    void QFloor(Action<Graphics> a) { queue.Add(new KeyValuePair<float, Action<Graphics>>(-1e6f, a)); }

    // ---------- Vẽ ----------
    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        // Vừa khung: sàn W x H ô, tường cao 3
        if (spin && !dragging) yaw += 0.006f;
        Camera();
        float diag = (float)Math.Sqrt(W * W + H * H);
        tile = Math.Max(14, Math.Min((Width - 40) / (diag * 0.707f), (Height - 50) / (diag * 0.707f * ySc + 1.5f * zSc))) * zoom;
        ox = Width / 2f + panX - (CX - CY) * tile / 2; oy = Height * 0.56f + panY - (CX + CY) * tile / 2 * ySc;
        int hour = DateTime.Now.Hour; bool night = hour < 6 || hour >= 18;
        using (var bg = new LinearGradientBrush(ClientRectangle, night ? Color.FromArgb(22, 26, 48) : Color.FromArgb(226, 233, 245), night ? Color.FromArgb(10, 12, 24) : Color.FromArgb(244, 238, 228), 90f)) g.FillRectangle(bg, ClientRectangle);
        // Bóng lớn dưới cả căn phòng: phòng như một mô hình nổi trên mặt bàn
        { var c = P(CX, CY, -0.6f); float w = (float)Math.Sqrt(W * W + H * H) * tile * 0.62f, h = w * ySc * 1.05f;
          using (var path = new GraphicsPath()) { path.AddEllipse(c.X - w / 2, c.Y - h / 2 + tile * 0.4f, w, h);
            using (var b = new PathGradientBrush(path) { CenterColor = Color.FromArgb(night ? 150 : 90, 0, 0, 10), SurroundColors = new[] { Color.FromArgb(0, 0, 0, 10) } }) g.FillPath(b, path); } }

        DrawRoom(g, night);
        queue.Clear();
        Furniture(night);
        foreach (var s in Team) StaffDesk(s);
        Boss();
        Walker();
        foreach (var kv in queue.OrderBy(k => k.Key)) kv.Value(g);
        DrawFlyers(g);
        foreach (var s in Team) DrawBubble(g, s);
        DrawHeader(g);
        DrawControls(g);
    }

    void DrawRoom(Graphics g, bool night)
    {
        // Sàn gỗ + thảm khu kỹ thuật
        // Bệ sàn dày (như sa bàn): các cạnh đang quay về người xem
        Box(g, -0.15f, -0.15f, -0.55f, W + 0.3f, H + 0.3f, 0.55f, Color.FromArgb(92, 70, 56));
        // Sàn gỗ: các tấm ván dài, so le, màu hơi khác nhau
        var rndF = new Random(11);
        for (int j = 0; j < H; j++)
        {
            float off = (j % 3) * 1.3f; float x0 = -off;
            while (x0 < W)
            {
                float len = 2.2f + (float)rndF.NextDouble() * 1.8f, a = Math.Max(0, x0), b2 = Math.Min(W, x0 + len);
                int v = rndF.Next(-12, 12);
                Poly(g, Color.FromArgb(Clamp(210 + v), Clamp(180 + v), Clamp(142 + v)), P(a, j), P(b2, j), P(b2, j + 1), P(a, j + 1));
                using (var pen = new Pen(Color.FromArgb(60, 120, 85, 55), 1f)) g.DrawLine(pen, P(b2, j), P(b2, j + 1));
                x0 += len;
            }
            using (var pen = new Pen(Color.FromArgb(50, 120, 85, 55), 1f)) g.DrawLine(pen, P(0, j), P(W, j));
        }
        // Bóng tối ở chân các tường đang dựng (ambient occlusion)
        foreach (var w in new[] { Tuple.Create(Faces(0, 1), 0f, 0f, W, 0f, 0f, 0.9f), Tuple.Create(Faces(1, 0), 0f, 0f, 0f, H, 0.9f, 0f),
                                  Tuple.Create(Faces(0, -1), 0f, H, W, H, 0f, -0.9f), Tuple.Create(Faces(-1, 0), W, 0f, W, H, -0.9f, 0f) })
        {
            if (!w.Item1) continue;
            var q = new[] { P(w.Item2, w.Item3), P(w.Item4, w.Item5), P(w.Item4 + w.Item6, w.Item5 + w.Item7), P(w.Item2 + w.Item6, w.Item3 + w.Item7) };
            var m0 = P(w.Item2, w.Item3); var m1 = P(w.Item2 + w.Item6, w.Item3 + w.Item7);
            if (Math.Abs(m0.X - m1.X) + Math.Abs(m0.Y - m1.Y) > 1)
                using (var b = new LinearGradientBrush(m0, m1, Color.FromArgb(80, 40, 25, 15), Color.FromArgb(0, 40, 25, 15))) g.FillPolygon(b, q);
        }
        // Nắng qua cửa sổ đổ xuống sàn (ban ngày)
        if (!night && Faces(0, 1))
            foreach (var wx in new[] { 6.4f, 9.2f, 12.0f })
            {
                float sh = (float)Math.Sin(T * 0.05) * 0.2f;
                using (var b = new SolidBrush(Color.FromArgb(46, 255, 240, 190))) g.FillPolygon(b, new[] { P(wx + 0.6f + sh, 0.2f), P(wx + 2.8f + sh, 0.2f), P(wx + 3.6f + sh, 3.0f), P(wx + 1.4f + sh, 3.0f) });
            }
        Poly(g, Color.FromArgb(150, 92, 124, 140), P(5.4f, 5.2f), P(16.4f, 5.2f), P(16.4f, 11.8f), P(5.4f, 11.8f));
        Poly(g, Color.FromArgb(120, 170, 120, 90), P(0.6f, 5.8f), P(4.2f, 5.8f), P(4.2f, 8.6f), P(0.6f, 8.6f));
        // Vũng sáng đèn trần
        foreach (var l in new[] { new PointF(8, 7.5f), new PointF(12.5f, 7.5f), new PointF(3, 3), new PointF(2.5f, 10f), new PointF(10, 2.5f) })
        {
            var c = P(l.X, l.Y); float w = tile * 3.4f, h = tile * 1.7f;
            using (var path = new GraphicsPath()) { path.AddEllipse(c.X - w / 2, c.Y - h / 2, w, h);
                using (var b = new PathGradientBrush(path) { CenterColor = Color.FromArgb(night ? 110 : 60, 255, 236, 180), SurroundColors = new[] { Color.FromArgb(0, 255, 236, 180) } }) g.FillPath(b, path); }
        }
        // Tường: chỉ dựng các tường ở phía xa (mặt trong quay về người xem), tường phía gần được "cắt" đi để nhìn vào phòng
        bool wallN = Faces(0, 1), wallW = Faces(1, 0), wallS = Faces(0, -1), wallE = Faces(-1, 0);
        Action<PointF, PointF, Color, Color> wall = (a, b, c, skirt) =>
        {
            // Mặt tường: sáng phía trên (đèn trần hắt lên), tối dần xuống chân
            var face = new[] { P(a.X, a.Y, 0), P(b.X, b.Y, 0), P(b.X, b.Y, 3), P(a.X, a.Y, 3) };
            float y0 = Math.Min(face[2].Y, face[3].Y), y1 = Math.Max(face[0].Y, face[1].Y);
            using (var br = new LinearGradientBrush(new PointF(0, y0 - 1), new PointF(0, y1 + 1), Shade(c, 0.06f), Shade(c, -0.14f))) g.FillPolygon(br, face);
            Poly(g, skirt, P(a.X, a.Y, 0), P(b.X, b.Y, 0), P(b.X, b.Y, 0.18f), P(a.X, a.Y, 0.18f));
            // Bề dày tường: dải mũ ở đỉnh, lùi ra ngoài phòng
            float nx = a.Y == b.Y ? 0 : (a.X == 0 ? -0.25f : 0.25f), ny = a.Y == b.Y ? (a.Y == 0 ? -0.25f : 0.25f) : 0;
            Poly(g, Shade(c, -0.35f), P(a.X, a.Y, 3), P(b.X, b.Y, 3), P(b.X + nx, b.Y + ny, 3), P(a.X + nx, a.Y + ny, 3));
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f)) g.DrawLine(pen, P(a.X, a.Y, 3), P(b.X, b.Y, 3));
        };
        if (wallS) wall(new PointF(0, H), new PointF(W, H), Color.FromArgb(230, 224, 214), Color.FromArgb(115, 92, 76));
        if (wallE) wall(new PointF(W, 0), new PointF(W, H), Color.FromArgb(218, 210, 198), Color.FromArgb(105, 85, 70));
        if (wallN) wall(new PointF(0, 0), new PointF(W, 0), Color.FromArgb(236, 230, 220), Color.FromArgb(120, 98, 80));
        if (wallW) wall(new PointF(0, 0), new PointF(0, H), Color.FromArgb(222, 214, 202), Color.FromArgb(105, 85, 70));
        // Cửa sổ: bầu trời theo giờ thật
        Color sky1 = night ? Color.FromArgb(18, 28, 70) : Color.FromArgb(140, 200, 245), sky2 = night ? Color.FromArgb(40, 50, 110) : Color.FromArgb(220, 240, 255);
        if (wallN) foreach (var wx in new[] { 6.4f, 9.2f, 12.0f })
        {
            var r = new[] { P(wx, 0, 1.0f), P(wx + 2.2f, 0, 1.0f), P(wx + 2.2f, 0, 2.6f), P(wx, 0, 2.6f) };
            using (var b = new LinearGradientBrush(new PointF(0, r[3].Y), new PointF(0, r[0].Y + 1), sky1, sky2)) g.FillPolygon(b, r);
            using (var pen = new Pen(Color.FromArgb(250, 250, 250), Math.Max(2, tile / 18))) { g.DrawPolygon(pen, r); g.DrawLine(pen, P(wx + 1.1f, 0, 1.0f), P(wx + 1.1f, 0, 2.6f)); }
        }
        // Mặt trời / mặt trăng
        if (wallN) { var c = P(10.4f + (float)Math.Sin(T * 0.05) * 0.2f, 0, 2.25f); float rr = tile * 0.22f;
          using (var b = new SolidBrush(night ? Color.FromArgb(240, 240, 220) : Color.FromArgb(255, 214, 90))) g.FillEllipse(b, c.X - rr, c.Y - rr, rr * 2, rr * 2); }
        if (wallW) foreach (var wy in new[] { 5.5f, 9.0f })
        {
            var r = new[] { P(0, wy, 1.0f), P(0, wy + 2.4f, 1.0f), P(0, wy + 2.4f, 2.6f), P(0, wy, 2.6f) };
            using (var b = new LinearGradientBrush(new PointF(0, r[3].Y - tile), new PointF(0, r[0].Y + 1), sky1, sky2)) g.FillPolygon(b, r);
            using (var pen = new Pen(Color.FromArgb(250, 250, 250), Math.Max(2, tile / 18))) g.DrawPolygon(pen, r);
        }
        // Đồng hồ treo tường chạy giờ thật
        if (wallN) { var c = P(5.4f, 0, 2.35f); float rr = tile * 0.3f;
          using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, c.X - rr, c.Y - rr * 0.9f, rr * 2, rr * 1.8f);
          using (var pen = new Pen(Color.FromArgb(60, 60, 70), 2)) g.DrawEllipse(pen, c.X - rr, c.Y - rr * 0.9f, rr * 2, rr * 1.8f);
          var now = DateTime.Now; double ha = (now.Hour % 12 + now.Minute / 60.0) / 12 * Math.PI * 2, ma = now.Minute / 60.0 * Math.PI * 2, sa = now.Second / 60.0 * Math.PI * 2;
          using (var pen = new Pen(Color.FromArgb(40, 40, 50), 2.5f)) g.DrawLine(pen, c, new PointF(c.X + (float)Math.Sin(ha) * rr * 0.5f, c.Y - (float)Math.Cos(ha) * rr * 0.45f));
          using (var pen = new Pen(Color.FromArgb(40, 40, 50), 1.6f)) g.DrawLine(pen, c, new PointF(c.X + (float)Math.Sin(ma) * rr * 0.8f, c.Y - (float)Math.Cos(ma) * rr * 0.72f));
          using (var pen = new Pen(Color.FromArgb(220, 60, 60), 1f)) g.DrawLine(pen, c, new PointF(c.X + (float)Math.Sin(sa) * rr * 0.85f, c.Y - (float)Math.Cos(sa) * rr * 0.76f)); }
        // Bảng trắng ghi phương châm
        if (wallW) { var r = new[] { P(0, 1.2f, 1.2f), P(0, 4.2f, 1.2f), P(0, 4.2f, 2.5f), P(0, 1.2f, 2.5f) };
          Poly(g, Color.White, r); using (var pen = new Pen(Color.FromArgb(150, 150, 160), 2)) g.DrawPolygon(pen, r); }
    }

    void Plant(float x, float y, float size)
    {
        Q(x + 0.25f * size, y + 0.25f * size, g =>
        {
            Shadow(g, x + 0.25f, y + 0.25f, 0.4f * size, 0.4f * size);
            Box(g, x, y, 0, 0.5f * size, 0.5f * size, 0.45f * size, Color.FromArgb(190, 110, 70));
            var c = P(x + 0.25f * size, y + 0.25f * size, 0.45f * size);
            float sway = (float)Math.Sin(T * 1.3 + x) * tile * 0.03f;
            for (int i = 0; i < 7; i++)
            {
                double a = i / 7.0 * Math.PI * 2; float r = tile * 0.28f * size;
                float lx = c.X + (float)Math.Cos(a) * r * 0.8f + sway, ly = c.Y - tile * 0.45f * size + (float)Math.Sin(a) * r * 0.45f - (i % 2) * tile * 0.12f * size;
                using (var b = new SolidBrush(i % 2 == 0 ? Color.FromArgb(56, 142, 60) : Color.FromArgb(76, 175, 80))) g.FillEllipse(b, lx - r * 0.75f, ly - r * 0.75f, r * 1.5f, r * 1.5f);
            }
            using (var b = new SolidBrush(Color.FromArgb(102, 187, 106))) g.FillEllipse(b, c.X - tile * 0.18f * size + sway, c.Y - tile * 0.78f * size, tile * 0.36f * size, tile * 0.36f * size);
        });
    }

    void Furniture(bool night)
    {
        // Kho tri thức: tủ máy chủ + kệ sách, đèn nhấp nháy
        Q(14.6f, 0.6f, g =>
        {
            for (int i = 0; i < 3; i++)
            {
                float x = 13.0f + i * 1.05f;
                Box(g, x, 0.25f, 0, 0.9f, 0.8f, 2.3f, Color.FromArgb(45, 52, 70), true);
                for (int k = 0; k < 7; k++)
                {
                    var p = P(x + 0.9f, 0.4f, 0.3f + k * 0.28f);
                    bool on = ((int)(T * 3 + i * 7 + k * 3)) % 4 != 0;
                    using (var b = new SolidBrush(on ? (k % 3 == 0 ? Color.FromArgb(52, 211, 153) : Color.FromArgb(34, 211, 238)) : Color.FromArgb(40, 60, 70))) g.FillEllipse(b, p.X - 2, p.Y - 2, 4, 4);
                }
            }
            Label(g, P(14.6f, 1.2f, 2.75f), L.T("Knowledge vault", "Kho tri thức") + (files > 0 ? " · " + files.ToString("N0") + L.T(" files", " file") : ""), Color.FromArgb(8, 145, 178), true);
        });
        // Kệ sách cạnh kho
        Q(11.6f, 0.45f, g => { Box(g, 10.6f, 0.2f, 0, 2.0f, 0.5f, 1.8f, Color.FromArgb(150, 105, 70), true);
            var rnd = new Random(7);
            for (int r = 0; r < 3; r++) for (int i = 0; i < 9; i++)
            { float x = 10.7f + i * 0.2f; Box(g, x, 0.25f, 0.15f + r * 0.6f, 0.15f, 0.4f, 0.42f, Color.FromArgb(rnd.Next(80, 230), rnd.Next(60, 200), rnd.Next(60, 200))); } });
        // Góc thư giãn: sofa, bàn trà, máy cà phê, bình nước
        Q(2.3f, 6.35f, g => { Shadow(g, 2.2f, 6.5f, 1.6f, 0.6f); Box(g, 0.9f, 5.9f, 0, 2.8f, 0.9f, 0.45f, Color.FromArgb(70, 110, 160), true); Box(g, 0.9f, 5.9f, 0.45f, 2.8f, 0.25f, 0.55f, Color.FromArgb(60, 98, 145)); });
        Q(2.35f, 7.8f, g => { Shadow(g, 2.3f, 7.8f, 0.7f, 0.5f); Box(g, 1.7f, 7.4f, 0, 1.3f, 0.8f, 0.35f, Color.FromArgb(160, 120, 85), true);
            var c = P(2.1f, 7.7f, 0.35f); using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, c.X - tile * 0.07f, c.Y - tile * 0.12f, tile * 0.14f, tile * 0.12f); });
        Q(0.55f, 10.55f, g =>
        {
            Box(g, 0.2f, 10.2f, 0, 0.7f, 0.7f, 0.9f, Color.FromArgb(70, 70, 80), true);   // tủ
            Box(g, 0.25f, 10.3f, 0.9f, 0.5f, 0.5f, 0.55f, Color.FromArgb(40, 40, 45), true); // máy pha cà phê
            var s = P(0.5f, 10.55f, 1.5f);
            for (int i = 0; i < 4; i++) { float ph = (T * 0.8f + i * 0.25f) % 1; using (var b = new SolidBrush(Color.FromArgb((int)(120 * (1 - ph)), 255, 255, 255))) g.FillEllipse(b, s.X - 4 + (float)Math.Sin(T * 3 + i) * 3, s.Y - ph * tile * 0.6f, 8, 8); }
            Label(g, P(0.9f, 10.9f, 1.75f), L.T("Coffee", "Cà phê"), Color.FromArgb(150, 110, 80), true);
        });
        Q(0.5f, 11.85f, g => { Box(g, 0.25f, 11.6f, 0, 0.5f, 0.5f, 0.8f, Color.FromArgb(230, 230, 235), true); Box(g, 0.3f, 11.65f, 0.8f, 0.4f, 0.4f, 0.5f, Color.FromArgb(150, 200, 240)); });
        // Cây xanh khắp phòng
        Plant(5.0f, 5.2f, 0.9f); Plant(16.2f, 2.0f, 1.3f); Plant(16.2f, 12.2f, 1.2f); Plant(5.2f, 12.2f, 1.0f); Plant(11.6f, 4.5f, 0.9f); Plant(0.3f, 4.6f, 1.1f); Plant(8.3f, 0.3f, 0.9f);
    }

    // Bàn làm việc + nhân viên
    void StaffDesk(Staff s)
    {
        float x = s.DeskX, y = s.DeskY;
        bool present = s.Present, working = present && s.Working, asleep = present && !working && !s.Online && s.Key != "brain" && s.Key != "tools";
        Q(x + 0.85f, y + 0.5f, g =>
        {
            Shadow(g, x + 1.0f, y + 0.55f, 1.3f, 0.7f, 40);
            Box(g, x, y, 0, 1.7f, 1.0f, 0.72f, Color.FromArgb(240, 236, 228), true);           // mặt bàn trắng
            Box(g, x + 0.05f, y + 0.85f, 0, 1.6f, 0.1f, 0.68f, Color.FromArgb(200, 196, 188)); // tấm chắn trước
            // Màn hình (mặt +x là màn hình, nhìn thấy được)
            Box(g, x + 0.25f, y + 0.3f, 0.72f, 0.08f, 0.1f, 0.25f, Color.FromArgb(60, 60, 70));
            Box(g, x + 0.2f, y + 0.12f, 0.95f, 0.1f, 0.75f, 0.55f, Color.FromArgb(35, 38, 48));
            var scr = new[] { P(x + 0.3f, y + 0.17f, 0.99f), P(x + 0.3f, y + 0.82f, 0.99f), P(x + 0.3f, y + 0.82f, 1.46f), P(x + 0.3f, y + 0.17f, 1.46f) };
            Color screen = !present ? Color.FromArgb(30, 32, 40) : working ? Color.FromArgb(18, 24, 44) : Color.FromArgb(28, 40, 70);
            if (Faces(1, 0)) Poly(g, screen, scr);
            if (present && Faces(1, 0))
            {
                // Dòng code chạy khi đang làm; màn hình chờ dịu khi rảnh
                int lines = 6;
                for (int i = 0; i < lines; i++)
                {
                    float scroll = working ? (T * 2.2f) % 1 : 0;
                    float zz = 1.02f + (i + scroll) * 0.4f / lines; if (zz > 1.42f) continue;
                    float len = working ? 0.25f + (float)((Math.Sin(i * 3.1 + Math.Floor(T * 2.2f + i)) + 1) * 0.2) : 0.2f;
                    using (var pen = new Pen(working ? (i % 3 == 0 ? Color.FromArgb(52, 211, 153) : i % 3 == 1 ? s.Shirt : Color.FromArgb(180, 200, 255)) : Color.FromArgb(60, 90, 140), Math.Max(1.5f, tile / 30)))
                        g.DrawLine(pen, P(x + 0.31f, y + 0.25f, zz), P(x + 0.31f, y + 0.25f + len, zz));
                }
            }
            // Bàn phím, cốc, cây nhỏ
            Box(g, x + 0.75f, y + 0.25f, 0.72f, 0.3f, 0.55f, 0.03f, Color.FromArgb(70, 72, 80));
            Box(g, x + 1.3f, y + 0.12f, 0.72f, 0.14f, 0.14f, 0.16f, s.Shirt);
            // Biển tên trên bàn
            Label(g, P(x + 0.85f, y + 1.0f, 0.35f), s.Name, s.Shirt, true);
        });
        // Ghế + người (ngồi phía +x, quay về màn hình)
        Q(x + 1.95f, y + 0.5f, g =>
        {
            float cx = x + 1.95f, cy = y + 0.5f;
            Shadow(g, cx, cy, 0.45f, 0.45f, 45);
            Box(g, cx - 0.25f, cy - 0.25f, 0.35f, 0.5f, 0.5f, 0.1f, Color.FromArgb(50, 50, 58));          // đệm
            Box(g, cx + 0.2f, cy - 0.25f, 0.45f, 0.08f, 0.5f, 0.55f, Color.FromArgb(45, 45, 52));         // tựa lưng
            Box(g, cx - 0.04f, cy - 0.04f, 0, 0.08f, 0.08f, 0.35f, Color.FromArgb(90, 90, 100));
            if (!present)
            {
                // Bàn trống: chưa kết nối / chưa cài
                Label(g, P(cx, cy, 1.25f), !s.Known ? "…" : s.Installed ? L.T("Not connected", "Chưa kết nối") : L.T("Hiring", "Đang tuyển"), Color.FromArgb(140, 140, 150), true);
                s.Hit = RectAround(P(x + 0.9f, y + 0.5f, 0.6f), tile * 1.3f, tile * 1.1f);
                return;
            }
            Person(g, s, cx, cy, 0.45f, working, asleep, false, 0);
            s.Hit = RectAround(P(cx, cy, 1.1f), tile * 0.9f, tile * 1.2f);
            if (s == hover || s == Selected)
                using (var pen = new Pen(Color.FromArgb(s == Selected ? 230 : 140, 34, 211, 238), 2.2f)) { var h = s.Hit; g.DrawEllipse(pen, h.X, h.Bottom - tile * 0.25f, h.Width, tile * 0.4f); }
        });
    }

    // Nhân vật: thân, đầu, tóc, tay gõ phím
    void Person(Graphics g, Staff s, float cx, float cy, float seatZ, bool working, bool asleep, bool standing, float walkPhase)
    {
        float breathe = (float)Math.Sin(T * 2 + cx) * tile * 0.01f;
        var hip = P(cx, cy, standing ? 0.55f : seatZ + 0.05f);
        float bw = tile * 0.36f, bh = tile * 0.42f;
        if (standing)
        {
            float st = (float)Math.Sin(walkPhase) * tile * 0.08f;
            using (var pen = new Pen(Color.FromArgb(50, 55, 70), tile * 0.09f)) { g.DrawLine(pen, hip.X - bw * 0.2f, hip.Y, hip.X - bw * 0.2f + st, hip.Y + tile * 0.32f); g.DrawLine(pen, hip.X + bw * 0.2f, hip.Y, hip.X + bw * 0.2f - st, hip.Y + tile * 0.32f); }
        }
        // Thân
        var body = new RectangleF(hip.X - bw / 2, hip.Y - bh + breathe, bw, bh);
        using (var path = RoundRect(body, bw * 0.35f)) using (var b = new LinearGradientBrush(body, Shade(s.Shirt, 0.15f), Shade(s.Shirt, -0.2f), 0f)) g.FillPath(b, path);
        if (s.Skirt && standing) Poly(g, Shade(s.Shirt, -0.3f), new PointF(hip.X - bw * 0.55f, hip.Y + tile * 0.12f), new PointF(hip.X + bw * 0.55f, hip.Y + tile * 0.12f), new PointF(hip.X + bw * 0.4f, hip.Y - 2), new PointF(hip.X - bw * 0.4f, hip.Y - 2));
        // Tay: gõ phím khi làm việc (về phía màn hình = trái-lên trên màn ảnh)
        float tap = working ? (float)Math.Sin(T * 22 + cx * 3) * tile * 0.03f : 0;
        var sh = new PointF(hip.X - bw * 0.35f, hip.Y - bh * 0.8f + breathe);
        var hand = standing ? new PointF(sh.X - tile * 0.05f, sh.Y + tile * 0.3f) : new PointF(sh.X - tile * 0.28f, sh.Y + tile * 0.12f + tap);
        using (var pen = new Pen(Shade(s.Shirt, -0.1f), tile * 0.08f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, sh, hand);
        using (var b = new SolidBrush(Color.FromArgb(240, 200, 170))) g.FillEllipse(b, hand.X - tile * 0.04f, hand.Y - tile * 0.04f, tile * 0.08f, tile * 0.08f);
        if (!standing)
        {
            var sh2 = new PointF(hip.X + bw * 0.3f, hip.Y - bh * 0.8f + breathe); var hand2 = new PointF(sh2.X - tile * 0.2f, sh2.Y + tile * 0.16f - tap);
            using (var pen = new Pen(Shade(s.Shirt, -0.25f), tile * 0.08f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, sh2, hand2);
        }
        // Đầu: gục xuống khi ngủ
        float hr = tile * 0.17f;
        var head = new PointF(hip.X + (asleep ? -tile * 0.08f : 0), hip.Y - bh - hr * 0.7f + breathe + (asleep ? tile * 0.1f : 0));
        // Đầu tròn có khối: sáng ở góc trên-trái, tối dần về mép
        using (var hp = new GraphicsPath())
        {
            hp.AddEllipse(head.X - hr, head.Y - hr, hr * 2, hr * 2);
            using (var b = new PathGradientBrush(hp) { CenterPoint = new PointF(head.X - hr * 0.35f, head.Y - hr * 0.35f), CenterColor = Color.FromArgb(255, 226, 200), SurroundColors = new[] { Color.FromArgb(214, 166, 136) } })
                g.FillPath(b, hp);
        }
        using (var b = new SolidBrush(s.Hair)) { g.FillPie(b, head.X - hr * 1.05f, head.Y - hr * 1.1f, hr * 2.1f, hr * 2.0f, 180, 180); g.FillEllipse(b, head.X - hr * 0.2f, head.Y - hr * 0.9f, hr * 1.25f, hr * 1.6f); }
        if (s.Skirt) using (var b = new SolidBrush(s.Hair)) g.FillEllipse(b, head.X + hr * 0.4f, head.Y - hr * 0.1f, hr * 0.9f, hr * 1.4f);
        if (asleep) using (var f = new Font("Segoe UI", Math.Max(7, tile * 0.16f), FontStyle.Bold)) using (var b = new SolidBrush(Color.FromArgb((int)(150 + 100 * Math.Sin(T * 2)), 120, 130, 160)))
            g.DrawString("z z", f, b, head.X + hr, head.Y - hr * 2 - (T * 6 % 8));
        // Chấm trạng thái
        Color dot = working ? Color.FromArgb(52, 211, 153) : asleep ? Color.FromArgb(150, 150, 160) : Color.FromArgb(250, 204, 21);
        using (var b = new SolidBrush(dot)) g.FillEllipse(b, head.X + hr * 0.7f, head.Y - hr * 1.3f, tile * 0.1f, tile * 0.1f);
        using (var pen = new Pen(Color.White, 1.5f)) g.DrawEllipse(pen, head.X + hr * 0.7f, head.Y - hr * 1.3f, tile * 0.1f, tile * 0.1f);
    }

    // Phòng kính của Chỉ huy (người dùng)
    void Boss()
    {
        QFloor(g =>
        {
            // Sàn thảm phòng sếp
            Poly(g, Color.FromArgb(140, 110, 80, 130), P(0.3f, 0.3f), P(5.6f, 0.3f), P(5.6f, 4.6f), P(0.3f, 4.6f));
        });
        Q(0.5f, 2.05f, g => { Box(g, 0.25f, 0.25f, 0, 0.5f, 3.6f, 2.0f, Color.FromArgb(120, 85, 60), true);   // tủ sách sau lưng
            var rnd = new Random(3);
            for (int r = 0; r < 3; r++) for (int i = 0; i < 12; i++) Box(g, 0.75f, 0.35f + i * 0.28f, 0.2f + r * 0.6f, 0.05f, 0.22f, 0.4f, Color.FromArgb(rnd.Next(90, 220), rnd.Next(60, 180), rnd.Next(60, 160))); });
        Q(2.9f, 2.05f, g =>
        {
            Shadow(g, 2.8f, 2.3f, 1.8f, 0.9f, 45);
            Box(g, 1.9f, 1.4f, 0, 2.0f, 1.3f, 0.78f, Color.FromArgb(92, 60, 40), true);   // bàn gỗ lớn
            Box(g, 2.1f, 1.6f, 0.78f, 0.1f, 0.9f, 0.6f, Color.FromArgb(30, 32, 40));        // màn hình lớn
            var scr = new[] { P(2.21f, 1.65f, 0.83f), P(2.21f, 2.45f, 0.83f), P(2.21f, 2.45f, 1.33f), P(2.21f, 1.65f, 1.33f) };
            if (Faces(1, 0)) using (var b = new LinearGradientBrush(new PointF(scr[3].X, scr[3].Y), new PointF(scr[1].X, scr[1].Y + 1.5f), Color.FromArgb(70, 80, 200), Color.FromArgb(34, 211, 238))) g.FillPolygon(b, scr);
            Box(g, 3.4f, 1.55f, 0.78f, 0.2f, 0.2f, 0.25f, Color.FromArgb(255, 200, 60));   // cúp
            Label(g, P(2.9f, 2.7f, 0.4f), L.T("You · Commander", "Bạn · Chỉ huy"), Color.FromArgb(234, 179, 8), true);
        });
        Q(4.35f, 2.05f, g =>
        {
            float cx = 4.35f, cy = 2.05f;
            Shadow(g, cx, cy, 0.5f, 0.5f, 45);
            Box(g, cx - 0.3f, cy - 0.3f, 0.38f, 0.6f, 0.6f, 0.12f, Color.FromArgb(30, 30, 30));
            Box(g, cx + 0.25f, cy - 0.3f, 0.5f, 0.1f, 0.6f, 0.8f, Color.FromArgb(25, 25, 25));
            var boss = new Staff { Name = "boss", Shirt = Color.FromArgb(30, 41, 59), Hair = Color.FromArgb(30, 25, 25) };
            bool anyWork = Team.Any(s => s.Present && s.Working);
            Person(g, boss, cx, cy, 0.5f, false, false, false, 0);
            // cà vạt + ngôi sao chỉ huy
            var neck = P(cx, cy, 1.25f);
            Poly(g, Color.FromArgb(220, 38, 38), new PointF(neck.X - 3, neck.Y), new PointF(neck.X + 3, neck.Y), new PointF(neck.X + 2, neck.Y + tile * 0.25f), new PointF(neck.X, neck.Y + tile * 0.3f), new PointF(neck.X - 2, neck.Y + tile * 0.25f));
            Star(g, P(cx, cy, 2.05f), tile * 0.13f, Color.FromArgb(250, 204, 21));
            if (anyWork) Label(g, P(cx, cy, 2.5f), L.T("Team is working", "Đội đang làm việc"), Color.FromArgb(16, 185, 129), true);
        });
        // Vách kính phòng sếp (vẽ sau cùng trong khu đó để nhìn xuyên qua)
        // mỗi vách một lớp riêng để xoay góc nào cũng đúng thứ tự trước/sau
        Q(5.6f, 1.85f, g =>
        {
            var gx = new[] { P(5.6f, 0.3f, 0), P(5.6f, 3.4f, 0), P(5.6f, 3.4f, 2.3f), P(5.6f, 0.3f, 2.3f) };
            using (var b = new SolidBrush(Color.FromArgb(45, 160, 210, 240))) g.FillPolygon(b, gx);
            using (var pen = new Pen(Color.FromArgb(160, 120, 130, 150), 2)) g.DrawPolygon(pen, gx);
            using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), tile * 0.08f)) g.DrawLine(pen, P(5.6f, 0.8f, 0.4f), P(5.6f, 1.4f, 1.9f));
        });
        Q(2.45f, 4.6f, g =>
        {
            var gy = new[] { P(0.3f, 4.6f, 0), P(4.6f, 4.6f, 0), P(4.6f, 4.6f, 2.3f), P(0.3f, 4.6f, 2.3f) };
            using (var b = new SolidBrush(Color.FromArgb(45, 160, 210, 240))) g.FillPolygon(b, gy);
            using (var pen = new Pen(Color.FromArgb(160, 120, 130, 150), 2)) g.DrawPolygon(pen, gy);
            using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), tile * 0.08f)) g.DrawLine(pen, P(1.0f, 4.6f, 0.4f), P(1.6f, 4.6f, 1.9f));
            Label(g, P(3.0f, 4.6f, 2.45f), L.T("Command office", "Phòng chỉ huy"), Color.FromArgb(100, 116, 139), true);
        });
    }

    // Trưởng phòng mang báo cáo tới Chỉ huy rồi quay về
    void Walker()
    {
        float t = T - walkStart; if (t < 0 || t > 9) return;
        var brain = Team[0];
        PointF[] path = { new PointF(brain.DeskX + 1.95f, brain.DeskY + 0.9f), new PointF(5.9f, 4.0f), new PointF(5.0f, 2.6f) };
        float pos; bool atBoss = false;
        if (t < 3) pos = t / 3; else if (t < 6) { pos = 1; atBoss = true; } else pos = 1 - (t - 6) / 3;
        float seg = pos * (path.Length - 1); int i = Math.Min(path.Length - 2, (int)seg); float f = seg - i;
        float wx = path[i].X + (path[i + 1].X - path[i].X) * f, wy = path[i].Y + (path[i + 1].Y - path[i].Y) * f;
        Q(wx, wy, g =>
        {
            Shadow(g, wx, wy, 0.35f, 0.35f, 55);
            Person(g, brain, wx, wy, 0, false, false, true, atBoss ? 0 : T * 9);
            var hand = P(wx - 0.15f, wy + 0.1f, 0.95f);
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, hand.X - tile * 0.1f, hand.Y - tile * 0.12f, tile * 0.18f, tile * 0.22f);
            if (atBoss && walkReport != null) Bubble(g, P(wx, wy, 1.9f), walkReport, Color.FromArgb(99, 102, 241));
        });
    }

    void DrawFlyers(Graphics g)
    {
        var from = P(14.0f, 0.8f, 2.3f);
        foreach (var f in flyers.ToList())
        {
            float t = (T - f.Start) / 1.4f;
            if (t > 1) { flyers.Remove(f); continue; }
            var s = f.To; var to = P(s.DeskX + 1.0f, s.DeskY + 0.4f, 1.0f);
            float x = from.X + (to.X - from.X) * t, y = from.Y + (to.Y - from.Y) * t - (float)Math.Sin(t * Math.PI) * tile * 1.6f;
            using (var b = new SolidBrush(Color.FromArgb(60, f.C))) g.FillEllipse(b, x - tile * 0.2f, y - tile * 0.2f, tile * 0.4f, tile * 0.4f);
            var st = g.Save(); g.TranslateTransform(x, y); g.RotateTransform(t * 360);
            using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, -tile * 0.08f, -tile * 0.1f, tile * 0.16f, tile * 0.2f);
            using (var pen = new Pen(f.C, 1.2f)) { g.DrawRectangle(pen, -tile * 0.08f, -tile * 0.1f, tile * 0.16f, tile * 0.2f); g.DrawLine(pen, -tile * 0.05f, -tile * 0.03f, tile * 0.05f, -tile * 0.03f); g.DrawLine(pen, -tile * 0.05f, tile * 0.02f, tile * 0.05f, tile * 0.02f); }
            g.Restore(st);
        }
    }

    void DrawBubble(Graphics g, Staff s)
    {
        if (s.Bubble == null || DateTime.Now > s.BubbleUntil || !s.Present) return;
        Bubble(g, P(s.DeskX + 1.95f, s.DeskY + 0.5f, 2.15f), s.Bubble, s.Shirt);
    }

    void Bubble(Graphics g, PointF at, string text, Color c)
    {
        using (var f = new Font("Segoe UI", Math.Max(7.5f, tile * 0.15f)))
        {
            var sz = g.MeasureString(text, f);
            var r = new RectangleF(at.X - sz.Width / 2 - 8, at.Y - sz.Height - 14, sz.Width + 16, sz.Height + 8);
            using (var path = RoundRect(r, 9)) using (var b = new SolidBrush(Color.FromArgb(245, 255, 255, 255))) { g.FillPath(b, path); using (var pen = new Pen(c, 1.5f)) g.DrawPath(pen, path); }
            Poly(g, Color.White, new PointF(at.X - 6, r.Bottom - 1), new PointF(at.X + 6, r.Bottom - 1), new PointF(at.X, r.Bottom + 7));
            using (var b = new SolidBrush(Color.FromArgb(30, 41, 59))) g.DrawString(text, f, b, r.X + 8, r.Y + 4);
        }
    }

    void Label(Graphics g, PointF at, string text, Color c, bool pill = false)
    {
        using (var f = new Font("Segoe UI", Math.Max(7f, tile * (pill ? 0.14f : 0.15f)), FontStyle.Bold))
        {
            var sz = g.MeasureString(text, f);
            if (pill)
            {
                var r = new RectangleF(at.X - sz.Width / 2 - 6, at.Y - sz.Height / 2 - 1, sz.Width + 12, sz.Height + 2);
                using (var path = RoundRect(r, r.Height / 2)) using (var b = new SolidBrush(Color.FromArgb(235, 255, 255, 255))) { g.FillPath(b, path); using (var pen = new Pen(c, 1.3f)) g.DrawPath(pen, path); }
                using (var b = new SolidBrush(Shade(c, -0.25f))) g.DrawString(text, f, b, at.X - sz.Width / 2, at.Y - sz.Height / 2);
            }
            else using (var b = new SolidBrush(c)) g.DrawString(text, f, b, at.X - sz.Width / 2, at.Y - sz.Height / 2);
        }
    }

    void Star(Graphics g, PointF c, float r, Color col)
    {
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++) { double a = -Math.PI / 2 + i * Math.PI / 5; float rr = i % 2 == 0 ? r : r * 0.45f; pts[i] = new PointF(c.X + (float)Math.Cos(a) * rr, c.Y + (float)Math.Sin(a) * rr); }
        Poly(g, col, pts);
    }

    void DrawHeader(Graphics g)
    {
        int present = Team.Count(s => s.Present), working = Team.Count(s => s.Present && s.Working);
        string t = L.T("You command ", "Bạn đang chỉ huy ") + present + L.T(" staff · ", " nhân viên · ") + working + L.T(" working now", " đang làm việc") + L.T("   ·   Click a person to manage", "   ·   Bấm vào một người để quản lý");
        using (var f = new Font("Segoe UI", 9.5f, FontStyle.Bold)) using (var b = new SolidBrush(DateTime.Now.Hour < 6 || DateTime.Now.Hour >= 18 ? Color.FromArgb(200, 210, 240) : Color.FromArgb(51, 65, 85)))
            g.DrawString(t, f, b, 14, 10);
    }

    static GraphicsPath RoundRect(RectangleF r, float rad) { return Round.Path(r, rad); }
    RectangleF RectAround(PointF c, float w, float h) { return new RectangleF(c.X - w / 2, c.Y - h / 2, w, h); }
}

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
        // Nhân viên nội bộ: một chức năng của app (bảo trì, sao lưu, thủ thư, thư ký) – luôn có mặt, bấm vào mở trang của họ
        public bool Internal, Hat; public string Page; public string[] Actions;   // Actions: các công cụ agent gọi mà người này phụ giúp
        public float DeskX, DeskY;                 // toạ độ bàn (ô sàn)
        public bool Installed = true, Connected, Known;   // Known: đã lấy được trạng thái kết nối
        public DateTime LastSeen = DateTime.MinValue, LastWork = DateTime.MinValue;
        public string LastAction; public int Uses;
        public string Bubble; public DateTime BubbleUntil;
        public RectangleF Hit;
        public bool Working { get { return (DateTime.Now - LastWork).TotalSeconds < 20; } }
        public bool Online { get { return (DateTime.Now - LastSeen).TotalMinutes < 30; } }
        public bool Present { get { return Internal || Key == "brain" || Key == "tools" || Connected || Online; } }
        public bool Helps(ActivityEntry e) { return Actions != null && Actions.Contains((e.Action ?? "").ToLowerInvariant()); }
        public bool Owns(ActivityEntry e) { var c = (e.Client ?? "").ToLowerInvariant(); return Clients.Any(k => c.StartsWith(k)) || Helps(e); }
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

    const float W = 17, H = 17;   // kích thước sàn (ô) – dãy hậu cần ở y 12.6..16.6
    float tile = 60, ox, oy;

    public OfficeView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Func<string, string, string, string, string[], Color, Color, float, float, bool, Staff> S = (k, n, r, link, cl, shirt, hair, x, y, skirt) =>
            new Staff { Key = k, Name = n, Role = r, LinkName = link, Clients = cl, Shirt = shirt, Hair = hair, DeskX = x, DeskY = y, Skirt = skirt };
        Team.Add(S("brain", "Second Brain", L.T("Manager · thinks with your Claude plan", "Trưởng phòng · suy nghĩ bằng gói Claude của bạn"), null, new[] { "Second Brain" }, Color.FromArgb(99, 102, 241), Color.FromArgb(40, 30, 30), 7.3f, 1.9f, true));
        Team.Add(S("claude", "Claude Code", L.T("Engineer · your one coding agent", "Kỹ sư · nhân sự code duy nhất"), "Claude Code", new[] { "claude-code" }, Color.FromArgb(217, 119, 87), Color.FromArgb(60, 40, 25), 9.7f, 7.4f, false));
        Team.Add(S("tools", L.T("Tools & scripts", "Công cụ & script"), L.T("Automation (HTTP, sb command)", "Tự động hoá (HTTP, lệnh sb)"), null, new[] { "cli", "http", "rest", "n8n", "make", "agent", "protocol-test" }, Color.FromArgb(139, 92, 246), Color.FromArgb(50, 50, 60), 2.0f, 9.6f, false));
        // Dãy hậu cần: các chức năng của app cũng là nhân viên thật
        Func<string, string, string, string, string[], string[], Color, Color, float, bool, Staff> I = (k, n, r, page, cl, acts, shirt, hair, x, skirt) =>
        { var s = S(k, n, r, null, cl, shirt, hair, x, 13.4f, skirt); s.Internal = true; s.Page = page; s.Actions = acts; return s; };
        Team.Add(I("it", L.T("IT technician", "Kỹ thuật viên IT"), L.T("PC maintenance · cleans junk, keeps the PC healthy", "Bảo trì máy · dọn rác, giữ máy khoẻ"), "maintain", new[] { "bảo trì" }, null, Color.FromArgb(234, 88, 12), Color.FromArgb(35, 30, 28), 2.0f, false));
        Team.Add(I("archivist", L.T("Archivist", "Thủ kho"), L.T("Brain backup · ships memory to your private GitHub", "Sao lưu bộ não · cất trí nhớ lên GitHub riêng"), "backup", new[] { "sao lưu" }, null, Color.FromArgb(100, 116, 139), Color.FromArgb(150, 150, 150), 6.2f, false));
        Team.Add(I("librarian", L.T("Librarian", "Thủ thư"), L.T("Index & search · knows where every file on the PC is", "Lập chỉ mục & tìm kiếm · biết mọi file trên máy nằm đâu"), "search", new[] { "thủ thư" }, new[] { "search", "read_file", "list_dir", "list_projects", "project_overview", "copy" }, Color.FromArgb(22, 163, 74), Color.FromArgb(110, 60, 30), 9.7f, true));
        Team.Add(I("secretary", L.T("Secretary", "Thư ký"), L.T("Memory · checks evidence, keeps long-term memory", "Trí nhớ · kiểm chứng bằng chứng, giữ trí nhớ dài hạn"), "memory", new[] { "thư ký" }, new[] { "remember", "recall", "revise_memory" }, Color.FromArgb(219, 39, 119), Color.FromArgb(45, 25, 20), 13.2f, true));

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
                    else { panX += dx; panY += dy; ClampPan(); Cursor = Cursors.SizeAll; }
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
        // Lấy vị trí chuột trực tiếp theo khung vẽ (toạ độ của sự kiện cuộn không đáng tin khi khung không giữ focus)
        MouseWheel += (s, e) => ZoomAt(PointToClient(Cursor.Position), (float)Math.Pow(1.15, e.Delta / 120.0));
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
    // Không cho tâm phòng trôi ra ngoài khung nhìn (phòng không bao giờ bị khuất hẳn)
    void ClampPan()
    {
        // Cho phép kéo ra xem phố, nhưng không trôi mất khu văn phòng
        float k = Math.Max(1, zoom);
        panX = Math.Max(-Width * 0.6f * k, Math.Min(Width * 0.6f * k, panX));
        panY = Math.Max(-Height * 0.7f * k, Math.Min(Height * 0.6f * k, panY));
    }
    void ZoomAt(Point m, float f)
    {
        float nz = Math.Max(0.3f, Math.Min(6f, zoom * f)); f = nz / zoom;
        float cx = Width / 2f, cy = Height * 0.56f;
        panX = m.X - cx - (m.X - cx - panX) * f; panY = m.Y - cy - (m.Y - cy - panY) * f;
        ClampPan();
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
                    ReactWithWalk(st, e, recent);
                }
                // Thủ thư / thư ký phụ giúp khi agent tìm kiếm hoặc ghi nhớ
                if (work) foreach (var h in Team.Where(x => x.Helps(e)))
                {
                    if (e.T > h.LastWork) { h.LastWork = e.T; h.LastAction = e.Action + (string.IsNullOrEmpty(e.Detail) ? "" : ": " + e.Detail); }
                    if (e.T > lastSeenEvent)
                    {
                        h.Bubble = (h.Key == "librarian" ? "🔎 " : "📝 ") + Short(e.Action, e.Detail); h.BubbleUntil = DateTime.Now.AddSeconds(5);
                        if (h.Key == "librarian") Send(h, Tuple.Create(Vault, 1.6f, L.T("Here it is 📚", "Có ngay 📚"), true));
                    }
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
        if (c == "trạm phát" || c == "second brain") return null;
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
        // Vừa khung: phòng + một dải phố (vỉa hè, đường) xung quanh
        float diag = (float)Math.Sqrt((W + 5) * (W + 5) + (H + 5) * (H + 5));
        tile = Math.Max(10, Math.Min((Width - 40) / (diag * 0.707f), (Height - 50) / (diag * 0.707f * ySc + 1.5f * zSc))) * zoom;
        ox = Width / 2f + panX - (CX - CY) * tile / 2; oy = Height * 0.56f + panY - (CX + CY) * tile / 2 * ySc;
        int hour = DateTime.Now.Hour; bool night = hour < 6 || hour >= 18;
        using (var bg = new LinearGradientBrush(ClientRectangle, night ? Color.FromArgb(22, 26, 48) : Color.FromArgb(150, 200, 240), night ? Color.FromArgb(10, 12, 24) : Color.FromArgb(226, 238, 248), 90f)) g.FillRectangle(bg, ClientRectangle);

        queue.Clear(); behind.Clear();
        City(night);
        DrawCityGround(g, night);
        foreach (var kv in behind.OrderBy(k => k.Key)) kv.Value(g);
        Wires(g);
        DrawRoom(g, night);
        Furniture(night);
        foreach (var s in Team) StaffDesk(s);
        Boss();
        IdleWalks();
        Walkers();
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
        Poly(g, Color.FromArgb(120, 120, 140, 110), P(0.6f, 12.4f), P(16.4f, 12.4f), P(16.4f, 15.9f), P(0.6f, 15.9f));   // thảm dãy hậu cần
        // Vũng sáng đèn trần
        foreach (var l in new[] { new PointF(8, 7.5f), new PointF(12.5f, 7.5f), new PointF(3, 3), new PointF(2.5f, 10f), new PointF(10, 2.5f), new PointF(4, 14f), new PointF(9.5f, 14f), new PointF(14.5f, 14f) })
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
        if (wallW) foreach (var wy in new[] { 5.5f, 9.0f, 12.8f })
        {
            var r = new[] { P(0, wy, 1.0f), P(0, wy + 2.4f, 1.0f), P(0, wy + 2.4f, 2.6f), P(0, wy, 2.6f) };
            using (var b = new LinearGradientBrush(new PointF(0, r[3].Y - tile), new PointF(0, r[0].Y + 1), sky1, sky2)) g.FillPolygon(b, r);
            using (var pen = new Pen(Color.FromArgb(250, 250, 250), Math.Max(2, tile / 18))) g.DrawPolygon(pen, r);
        }
        // Đồng hồ treo tường chạy giờ thật
        if (wallN) WallClock(g, 5.0f, 2.25f, 0.5f);
        // Bảng trắng ghi phương châm
        if (wallW) { var r = new[] { P(0, 1.2f, 1.2f), P(0, 4.2f, 1.2f), P(0, 4.2f, 2.5f), P(0, 1.2f, 2.5f) };
          Poly(g, Color.White, r); using (var pen = new Pen(Color.FromArgb(150, 150, 160), 2)) g.DrawPolygon(pen, r); }
    }

    // Đồng hồ treo tường nằm đúng trên mặt tường bắc: mặt số 1–12, vạch phút, kim giờ/phút/giây chạy giờ thật
    void WallClock(Graphics g, float cx, float cz, float R)
    {
        Func<double, float, PointF> At = (a, r) => P(cx + (float)Math.Sin(a) * r, 0.03f, cz + (float)Math.Cos(a) * r);
        Func<float, PointF[]> Ring = r => Enumerable.Range(0, 48).Select(i => At(i / 48.0 * Math.PI * 2, r)).ToArray();
        Poly(g, Color.FromArgb(60, 0, 0, 0), Ring(R * 1.08f).Select(p => new PointF(p.X + tile * 0.04f, p.Y + tile * 0.05f)).ToArray());   // bóng trên tường
        Poly(g, Color.FromArgb(45, 52, 64), Ring(R * 1.08f));                                                                                // vành
        using (var path = new GraphicsPath()) { path.AddPolygon(Ring(R));
            using (var b = new PathGradientBrush(path) { CenterColor = Color.White, SurroundColors = new[] { Color.FromArgb(232, 228, 220) } }) g.FillPath(b, path); }
        if (tile < 16) return;
        for (int i = 0; i < 60; i++)
        {
            double a = i / 60.0 * Math.PI * 2; bool big = i % 5 == 0;
            using (var pen = new Pen(Color.FromArgb(50, 50, 60), big ? Math.Max(1.4f, tile / 34) : 0.8f)) g.DrawLine(pen, At(a, R * (big ? 0.82f : 0.9f)), At(a, R * 0.96f));
        }
        using (var f = new Font("Segoe UI", Math.Max(5.5f, tile * 0.11f * R / 0.5f), FontStyle.Bold))
        using (var b = new SolidBrush(Color.FromArgb(35, 38, 48)))
            for (int h = 1; h <= 12; h++)
            {
                var p = At(h / 12.0 * Math.PI * 2, R * 0.66f); var t = h.ToString(); var sz = g.MeasureString(t, f);
                g.DrawString(t, f, b, p.X - sz.Width / 2, p.Y - sz.Height / 2);
            }
        var now = DateTime.Now; double sec = now.Second + now.Millisecond / 1000.0;
        double ha = (now.Hour % 12 + now.Minute / 60.0) / 12 * Math.PI * 2, ma = (now.Minute + sec / 60) / 60 * Math.PI * 2, sa = sec / 60 * Math.PI * 2;
        var c = At(0, 0);
        using (var pen = new Pen(Color.FromArgb(30, 32, 40), Math.Max(2.5f, tile / 16)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, c, At(ha, R * 0.48f));
        using (var pen = new Pen(Color.FromArgb(30, 32, 40), Math.Max(1.6f, tile / 26)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, c, At(ma, R * 0.75f));
        using (var pen = new Pen(Color.FromArgb(220, 50, 50), 1.1f)) g.DrawLine(pen, At(sa + Math.PI, R * 0.15f), At(sa, R * 0.86f));
        using (var b = new SolidBrush(Color.FromArgb(220, 50, 50))) g.FillEllipse(b, c.X - tile * 0.035f, c.Y - tile * 0.035f, tile * 0.07f, tile * 0.07f);
    }

    // ---------- Phố xung quanh văn phòng ----------
    // Toà nhà nằm giữa một khu phố: vỉa hè, đường vòng quanh có xe máy/ô tô chạy, quán trà đá, cà phê, phở, bánh mì
    // vỉa hè trong (-1.8..0), đường (-5..-1.8), vỉa hè ngoài (-6.5..-5), dãy nhà ống (-10..-6.5) – đối xứng bốn phía.
    const float GZ = -0.55f;   // mặt đất (sàn văn phòng nâng cao hơn đường)
    readonly List<KeyValuePair<float, Action<Graphics>>> behind = new List<KeyValuePair<float, Action<Graphics>>>();
    /// <summary>Xếp một vật ngoài phố: sau toà nhà thì vẽ trước phòng, trước toà nhà thì vẽ sau cùng. front = đang ở phía người xem.</summary>
    void QO(float x, float y, Action<Graphics, bool> a)
    {
        float nx = Math.Max(0, Math.Min(W, x)), ny = Math.Max(0, Math.Min(H, y)), d = Depth(x, y);
        if (d < Depth(nx, ny)) behind.Add(new KeyValuePair<float, Action<Graphics>>(d, g => a(g, false)));
        else queue.Add(new KeyValuePair<float, Action<Graphics>>(1e5f + d, g => a(g, true)));
    }
    void Flat(Graphics g, Color c, float x0, float y0, float x1, float y1) { Poly(g, c, P(x0, y0, GZ), P(x1, y0, GZ), P(x1, y1, GZ), P(x0, y1, GZ)); }

    void DrawCityGround(Graphics g, bool night)
    {
        Flat(g, night ? Color.FromArgb(40, 52, 44) : Color.FromArgb(150, 178, 120), -12, -12, W + 12, H + 12);        // bãi cỏ / đất
        Flat(g, night ? Color.FromArgb(70, 72, 80) : Color.FromArgb(196, 190, 180), -6.5f, -6.5f, W + 6.5f, H + 6.5f);  // vỉa hè ngoài
        Flat(g, night ? Color.FromArgb(34, 36, 42) : Color.FromArgb(78, 82, 92), -5, -5, W + 5, H + 5);                  // mặt đường
        // Vạch kẻ giữa đường (nét đứt) quanh khu nhà
        using (var pen = new Pen(Color.FromArgb(night ? 150 : 220, 245, 235, 200), Math.Max(1.2f, tile / 22)))
        {
            float o = -3.4f;
            for (float t = o; t < W - o - 0.5f; t += 1.2f) { g.DrawLine(pen, P(t, o, GZ), P(t + 0.6f, o, GZ)); g.DrawLine(pen, P(t, H - o, GZ), P(t + 0.6f, H - o, GZ)); }
            for (float t = o; t < H - o - 0.5f; t += 1.2f) { g.DrawLine(pen, P(o, t, GZ), P(o, t + 0.6f, GZ)); g.DrawLine(pen, P(W - o, t, GZ), P(W - o, t + 0.6f, GZ)); }
        }
        // Vạch sang đường trước cửa văn phòng
        for (int i = 0; i < 6; i++) Flat(g, Color.FromArgb(night ? 120 : 235, 240, 240, 240), 7.4f + i * 0.4f, H + 1.9f, 7.6f + i * 0.4f, H + 4.9f);
        Flat(g, night ? Color.FromArgb(88, 90, 98) : Color.FromArgb(214, 206, 192), -1.8f, -1.8f, W + 1.8f, H + 1.8f);  // vỉa hè trong
        // Gạch lát vỉa hè
        using (var pen = new Pen(Color.FromArgb(night ? 30 : 45, 90, 70, 50), 1f))
            for (float t = -1.8f; t <= W + 1.8f; t += 0.9f) { g.DrawLine(pen, P(t, -1.8f, GZ), P(t, 0, GZ)); g.DrawLine(pen, P(t, H, GZ), P(t, H + 1.8f, GZ)); g.DrawLine(pen, P(-1.8f, t, GZ), P(0, t, GZ)); g.DrawLine(pen, P(W, t, GZ), P(W + 1.8f, t, GZ)); }
        // Đèn đường hắt vũng sáng ban đêm
        if (night) foreach (var l in Lamps())
        {
            var c = P(l.X, l.Y, GZ); float w = tile * 3.2f, h = w * ySc;
            using (var path = new GraphicsPath()) { path.AddEllipse(c.X - w / 2, c.Y - h / 2, w, h);
                using (var b = new PathGradientBrush(path) { CenterColor = Color.FromArgb(90, 255, 210, 140), SurroundColors = new[] { Color.FromArgb(0, 255, 210, 140) } }) g.FillPath(b, path); }
        }
    }

    static IEnumerable<PointF> Lamps()
    {
        for (float t = 1; t < W; t += 5) { yield return new PointF(t, -1.6f); yield return new PointF(t + 2.5f, H + 1.6f); }
        for (float t = 3; t < H; t += 5) { yield return new PointF(-1.6f, t); yield return new PointF(W + 1.6f, t); }
    }

    void City(bool night)
    {
        // Chỉ đường xá + cảnh quan: hàng cây, đèn đường trên vỉa hè, dải cây xanh ngoài đường, xe chạy
        for (float t = 3.5f; t < W; t += 5) { Tree(t, -1.2f); Tree(t - 1, H + 1.2f); }
        for (float t = 0.8f; t < H; t += 5) { Tree(-1.2f, t); Tree(W + 1.2f, t + 1.5f); }
        foreach (var l in Lamps()) Lamp(l.X, l.Y, night);
        for (float t = -6f; t < W + 6.5f; t += 3.2f) { Tree(t, -7.6f); Tree(t + 1.6f, H + 7.6f); }
        for (float t = -2.8f; t < H + 3f; t += 3.2f) { Tree(-7.6f, t + 1.6f); Tree(W + 7.6f, t); }
        Vehicles(night);
    }

    // Dây điện chằng chịt nối các cột đèn
    void Wires(Graphics g)
    {
        var l = Lamps().ToList();
        using (var pen = new Pen(Color.FromArgb(150, 30, 30, 35), 1f))
            for (int i = 0; i < l.Count; i++)
            {
                var a = l[i]; var b = l.Where(p => p != a && (Math.Abs(p.X - a.X) < 0.01f || Math.Abs(p.Y - a.Y) < 0.01f)).OrderBy(p => Math.Abs(p.X - a.X) + Math.Abs(p.Y - a.Y)).FirstOrDefault();
                if (b.IsEmpty) continue;
                for (int k = 0; k < 3; k++)
                {
                    var pa = P(a.X, a.Y, GZ + 2.2f - k * 0.12f); var pb = P(b.X, b.Y, GZ + 2.2f - k * 0.12f);
                    var mid = new PointF((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2 + tile * (0.35f + k * 0.12f));
                    g.DrawBezier(pen, pa, mid, mid, pb);
                }
            }
    }

    void Tree(float x, float y)
    {
        QO(x, y, (g, front) =>
        {
            Shadow(g, x, y, 0.6f, 0.6f, 40);
            Box(g, x - 0.08f, y - 0.08f, GZ, 0.16f, 0.16f, 1.3f, Color.FromArgb(110, 80, 50));
            var c = P(x, y, GZ + 1.6f); float sway = (float)Math.Sin(T * 0.9 + x + y) * tile * 0.04f;
            for (int k = 0; k < 6; k++)
            {
                double a = k / 6.0 * Math.PI * 2; float r = tile * 0.42f;
                using (var b = new SolidBrush(k % 2 == 0 ? Color.FromArgb(46, 125, 50) : Color.FromArgb(67, 160, 71)))
                    g.FillEllipse(b, c.X + (float)Math.Cos(a) * r * 0.6f - r * 0.6f + sway, c.Y + (float)Math.Sin(a) * r * 0.35f - r * 0.6f - (k % 2) * tile * 0.12f, r * 1.2f, r * 1.2f);
            }
        });
    }

    void Lamp(float x, float y, bool night)
    {
        QO(x, y, (g, front) =>
        {
            Box(g, x - 0.04f, y - 0.04f, GZ, 0.08f, 0.08f, 2.4f, Color.FromArgb(70, 75, 85));
            var c = P(x, y, GZ + 2.45f); float r = tile * 0.1f;
            if (night) using (var path = new GraphicsPath()) { path.AddEllipse(c.X - r * 4, c.Y - r * 4, r * 8, r * 8);
                using (var b = new PathGradientBrush(path) { CenterColor = Color.FromArgb(160, 255, 220, 150), SurroundColors = new[] { Color.FromArgb(0, 255, 220, 150) } }) g.FillPath(b, path); }
            using (var b = new SolidBrush(night ? Color.FromArgb(255, 236, 170) : Color.FromArgb(220, 220, 210))) g.FillEllipse(b, c.X - r, c.Y - r * 0.7f, r * 2, r * 1.4f);
        });
    }

    // Xe chạy vòng quanh khu nhà: làn trong theo chiều kim đồng hồ, làn ngoài ngược lại
    class Vehicle { public bool Car; public float Lane, Speed, Phase; public Color C; }
    static readonly Vehicle[] Traffic = {
        new Vehicle { Car = false, Lane = -2.6f, Speed = 2.6f, Phase = 0, C = Color.FromArgb(220, 38, 38) },
        new Vehicle { Car = true, Lane = -2.6f, Speed = 2.0f, Phase = 14, C = Color.FromArgb(245, 245, 245) },
        new Vehicle { Car = false, Lane = -2.6f, Speed = 2.9f, Phase = 31, C = Color.FromArgb(37, 99, 235) },
        new Vehicle { Car = false, Lane = -2.6f, Speed = 2.4f, Phase = 47, C = Color.FromArgb(30, 30, 30) },
        new Vehicle { Car = false, Lane = -2.6f, Speed = 2.2f, Phase = 62, C = Color.FromArgb(250, 204, 21) },
        new Vehicle { Car = false, Lane = -4.2f, Speed = -2.7f, Phase = 6, C = Color.FromArgb(16, 185, 129) },
        new Vehicle { Car = true, Lane = -4.2f, Speed = -2.1f, Phase = 22, C = Color.FromArgb(30, 64, 175) },
        new Vehicle { Car = false, Lane = -4.2f, Speed = -3.0f, Phase = 40, C = Color.FromArgb(234, 88, 12) },
        new Vehicle { Car = false, Lane = -4.2f, Speed = -2.3f, Phase = 55, C = Color.FromArgb(120, 120, 130) },
        new Vehicle { Car = false, Lane = -4.2f, Speed = -2.5f, Phase = 71, C = Color.FromArgb(190, 24, 93) } };

    // Vị trí trên vòng đường cách mép khu nhà một khoảng o (âm = ra ngoài)
    static void Ring(float u, float o, out float x, out float y, out float dx, out float dy)
    {
        float w = W - 2 * o, h = H - 2 * o, per = 2 * (w + h); u = ((u % per) + per) % per;
        if (u < w) { x = o + u; y = o; dx = 1; dy = 0; return; } u -= w;
        if (u < h) { x = W - o; y = o + u; dx = 0; dy = 1; return; } u -= h;
        if (u < w) { x = W - o - u; y = H - o; dx = -1; dy = 0; return; } u -= w;
        x = o; y = H - o - u; dx = 0; dy = -1;
    }

    void Vehicles(bool night)
    {
        foreach (var v in Traffic)
        {
            float x, y, dx, dy; Ring(v.Phase + v.Speed * T, v.Lane, out x, out y, out dx, out dy);
            if (v.Speed < 0) { dx = -dx; dy = -dy; }
            float len = v.Car ? 1.5f : 0.75f, wid = v.Car ? 0.75f : 0.26f;
            float bx = x - (dx != 0 ? len / 2 : wid / 2), by = y - (dy != 0 ? len / 2 : wid / 2), sx = dx != 0 ? len : wid, sy = dy != 0 ? len : wid;
            var vv = v; float fx = x, fy = y, fdx = dx, fdy = dy;
            QO(x, y, (g, front) =>
            {
                Shadow(g, fx, fy, sx * 0.8f, sy * 0.8f, 60);
                if (night)
                {
                    var hp = P(fx + fdx * len * 1.3f, fy + fdy * len * 1.3f, GZ); float r = tile * (vv.Car ? 0.8f : 0.5f);
                    using (var path = new GraphicsPath()) { path.AddEllipse(hp.X - r, hp.Y - r * ySc, r * 2, r * 2 * ySc);
                        using (var b = new PathGradientBrush(path) { CenterColor = Color.FromArgb(120, 255, 240, 190), SurroundColors = new[] { Color.FromArgb(0, 255, 240, 190) } }) g.FillPath(b, path); }
                }
                if (vv.Car)
                {
                    Box(g, bx, by, GZ + 0.12f, sx, sy, 0.38f, vv.C, true);
                    float cx0 = bx + (sx > sy ? sx * 0.22f : 0.06f), cy0 = by + (sy > sx ? sy * 0.22f : 0.06f);
                    Box(g, cx0, cy0, GZ + 0.5f, sx > sy ? sx * 0.5f : sx - 0.12f, sy > sx ? sy * 0.5f : sy - 0.12f, 0.3f, Color.FromArgb(140, 190, 220), true);
                }
                else
                {
                    Box(g, bx, by, GZ + 0.12f, sx, sy, 0.22f, vv.C, true);
                    Person(g, new Staff { Name = "guest", Shirt = Shade(vv.C, 0.3f), Hair = Color.FromArgb(30, 30, 30) }, fx, fy, GZ + 0.3f, false, false, false, 0);
                    // Mũ bảo hiểm
                    var hd = P(fx, fy, GZ + 0.35f); hd.Y -= tile * 0.42f + tile * 0.19f; float r = tile * 0.24f;
                    using (var b = new SolidBrush(vv.C)) g.FillPie(b, hd.X - r * 1.1f, hd.Y - r * 1.15f, r * 2.2f, r * 1.6f, 180, 180);
                }
            });
        }
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
        Plant(5.0f, 5.2f, 0.9f); Plant(16.2f, 2.0f, 1.3f); Plant(16.2f, 16.2f, 1.2f); Plant(4.9f, 16.2f, 1.0f); Plant(0.3f, 16.2f, 1.1f); Plant(16.3f, 12.0f, 0.9f);
        // Dãy hậu cần: tủ hồ sơ của thủ kho, kệ sách của thủ thư, hộp dụng cụ của IT
        Q(8.0f, 16.45f, g => { Box(g, 6.6f, 16.1f, 0, 2.6f, 0.6f, 1.5f, Color.FromArgb(120, 130, 145), true);
            for (int i = 0; i < 4; i++) for (int r = 0; r < 3; r++) { var c = P(6.9f + i * 0.62f, 16.1f, 0.3f + r * 0.45f); using (var b = new SolidBrush(Color.FromArgb(220, 225, 230))) g.FillRectangle(b, c.X - tile * 0.08f, c.Y - tile * 0.02f, tile * 0.16f, tile * 0.04f); }
            Label(g, P(8.6f, 16.85f, 0.25f), L.T("Archive", "Kho lưu trữ"), Color.FromArgb(100, 116, 139), true); });
        Q(11.5f, 16.45f, g => { Box(g, 10.2f, 16.15f, 0, 2.6f, 0.5f, 1.9f, Color.FromArgb(150, 105, 70), true);
            var rnd = new Random(21);
            for (int r = 0; r < 3; r++) for (int i = 0; i < 12; i++) Box(g, 10.3f + i * 0.2f, 16.2f, 0.15f + r * 0.6f, 0.15f, 0.4f, 0.42f, Color.FromArgb(rnd.Next(80, 230), rnd.Next(60, 200), rnd.Next(60, 200)));
            Label(g, P(11.5f, 16.4f, 2.2f), L.T("Index shelves", "Kệ chỉ mục"), Color.FromArgb(22, 163, 74), true); });
        Q(1.4f, 12.9f, g => { Box(g, 0.9f, 12.6f, 0, 0.9f, 0.6f, 0.5f, Color.FromArgb(220, 38, 38), true); Box(g, 1.05f, 12.75f, 0.5f, 0.6f, 0.3f, 0.1f, Color.FromArgb(60, 60, 60)); }); Plant(11.6f, 4.5f, 0.9f); Plant(0.3f, 4.6f, 1.1f); Plant(8.3f, 0.3f, 0.9f);
    }

    // Bàn làm việc + nhân viên
    void StaffDesk(Staff s)
    {
        float x = s.DeskX, y = s.DeskY;
        bool present = s.Present, working = present && s.Working, asleep = present && !working && !s.Online && !s.Internal && s.Key != "brain" && s.Key != "tools";
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
            if (OnErrand(s)) return;   // đang đi việc: ghế trống, người được vẽ ở chỗ đang đứng
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
        // Đầu chibi: to, tròn, quay mặt ra phía người xem
        float seed = cx * 7.3f + cy * 3.1f;
        float hr = tile * 0.25f;
        float bob = working ? (float)Math.Abs(Math.Sin(T * 6 + seed)) * tile * 0.025f            // gật gù theo nhịp gõ phím
                  : asleep ? 0 : (float)Math.Sin(T * 1.6f + seed) * tile * 0.012f;
        float hop = 0;                                                                            // thỉnh thoảng nhún nhảy vui vẻ
        if (!working && !asleep) { float ph = (T + seed) % 9f; if (ph < 0.5f) hop = (float)Math.Sin(ph / 0.5f * Math.PI) * tile * 0.08f; }
        float tilt = asleep ? 0.35f : (float)Math.Sin(T * 0.9f + seed) * 0.06f;
        var head = new PointF(hip.X + (asleep ? -tile * 0.1f : 0), hip.Y - bh - hr * 0.75f + breathe - bob - hop + (asleep ? tile * 0.12f : 0));
        var st0 = g.Save();
        g.TranslateTransform(head.X, head.Y); g.RotateTransform(tilt * 57.3f); g.TranslateTransform(-head.X, -head.Y);
        // Tóc phía sau (tóc dài cho nhân vật nữ)
        if (s.Skirt) using (var b = new SolidBrush(Shade(s.Hair, -0.1f))) g.FillEllipse(b, head.X - hr * 1.15f, head.Y - hr * 0.6f, hr * 2.3f, hr * 2.0f);
        using (var hp = new GraphicsPath())
        {
            hp.AddEllipse(head.X - hr, head.Y - hr * 0.95f, hr * 2, hr * 1.9f);
            using (var b = new PathGradientBrush(hp) { CenterPoint = new PointF(head.X - hr * 0.3f, head.Y - hr * 0.3f), CenterColor = Color.FromArgb(255, 230, 208), SurroundColors = new[] { Color.FromArgb(226, 176, 146) } })
                g.FillPath(b, hp);
        }
        // Tóc mái: mũ tóc phía trên + vài lọn rủ xuống trán
        using (var b = new SolidBrush(s.Hair))
        {
            g.FillPie(b, head.X - hr * 1.07f, head.Y - hr * 1.12f, hr * 2.14f, hr * 1.9f, 180, 180);
            g.FillEllipse(b, head.X - hr * 0.95f, head.Y - hr * 0.55f, hr * 0.7f, hr * 0.45f);
            g.FillEllipse(b, head.X - hr * 0.35f, head.Y - hr * 0.6f, hr * 0.8f, hr * 0.4f);
            g.FillEllipse(b, head.X + hr * 0.3f, head.Y - hr * 0.55f, hr * 0.65f, hr * 0.45f);
            if (!s.Skirt) g.FillEllipse(b, head.X - hr * 0.1f, head.Y - hr * 1.32f, hr * 0.35f, hr * 0.4f);   // chỏm tóc dựng ngộ nghĩnh
        }
        using (var hl = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1, hr * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(hl, head.X - hr * 0.75f, head.Y - hr * 0.95f, hr * 1.1f, hr * 0.8f, 200, 60);
        // Mắt: tròn to có đốm sáng, chớp mắt mỗi vài giây; nhắm khi ngủ, cười tít khi vừa nhún nhảy
        float ey = head.Y + hr * 0.15f, ex = hr * 0.38f, er = hr * 0.17f;
        bool blink = ((T + seed) % 4.2f) < 0.13f;
        float look = working ? -hr * 0.08f : (float)Math.Sin(T * 0.7f + seed) * hr * 0.06f;
        using (var eye = new SolidBrush(Color.FromArgb(40, 34, 48)))
        using (var line = new Pen(Color.FromArgb(40, 34, 48), Math.Max(1.2f, hr * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            foreach (var side in new[] { -1, 1 })
            {
                float x0 = head.X + side * ex + look;
                if (asleep || blink) g.DrawArc(line, x0 - er, ey - er * 0.6f, er * 2, er * 1.2f, 20, 140);
                else if (hop > 0) g.DrawArc(line, x0 - er, ey - er * 0.2f, er * 2, er * 1.4f, 200, 140);
                else
                {
                    g.FillEllipse(eye, x0 - er, ey - er * 1.15f, er * 2, er * 2.3f);
                    using (var w = new SolidBrush(Color.White)) { g.FillEllipse(w, x0 - er * 0.55f, ey - er * 0.85f, er * 0.8f, er * 0.8f); g.FillEllipse(w, x0 + er * 0.25f, ey + er * 0.3f, er * 0.35f, er * 0.35f); }
                }
            }
        // Má hồng
        using (var b = new SolidBrush(Color.FromArgb(110, 255, 120, 130)))
        { g.FillEllipse(b, head.X - hr * 0.78f, ey + er * 0.9f, hr * 0.32f, hr * 0.18f); g.FillEllipse(b, head.X + hr * 0.46f, ey + er * 0.9f, hr * 0.32f, hr * 0.18f); }
        // Miệng: cười khi rảnh, chữ "o" tập trung khi làm việc, hé mở khi ngủ
        using (var mouth = new Pen(Color.FromArgb(150, 70, 60), Math.Max(1.2f, hr * 0.08f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float my = ey + er * 1.7f;
            if (working) using (var b = new SolidBrush(Color.FromArgb(150, 70, 60))) g.FillEllipse(b, head.X + look - hr * 0.06f, my - hr * 0.03f, hr * 0.12f, hr * 0.12f);
            else if (asleep) g.DrawEllipse(mouth, head.X - hr * 0.05f, my, hr * 0.1f, hr * 0.08f);
            else g.DrawArc(mouth, head.X - hr * 0.18f + look, my - hr * 0.12f, hr * 0.36f, hr * 0.24f, 20, 140);
        }
        g.Restore(st0);
        if (asleep) using (var f = new Font("Segoe UI", Math.Max(7, tile * 0.16f), FontStyle.Bold)) using (var b = new SolidBrush(Color.FromArgb((int)(150 + 100 * Math.Sin(T * 2)), 120, 130, 160)))
            g.DrawString("z z", f, b, head.X + hr, head.Y - hr * 2 - (T * 6 % 8));
        // Biểu tượng cảm xúc nổi lên thỉnh thoảng: ý tưởng khi làm việc, cà phê/nhạc/tim khi rảnh
        if (s.Hat)   // nón lá
        {
            Poly(g, Color.FromArgb(225, 200, 135), new PointF(head.X - hr * 1.55f, head.Y - hr * 0.35f), new PointF(head.X + hr * 1.55f, head.Y - hr * 0.35f), new PointF(head.X, head.Y - hr * 1.9f));
            using (var pen = new Pen(Color.FromArgb(160, 130, 80), 1f)) { g.DrawLine(pen, head.X - hr * 0.8f, head.Y - hr * 1.1f, head.X + hr * 0.8f, head.Y - hr * 1.1f); g.DrawLine(pen, head.X - hr * 1.55f, head.Y - hr * 0.35f, head.X + hr * 1.55f, head.Y - hr * 0.35f); }
        }
        if (s.Name == "guest") return;   // người qua đường / khách quán: không có biểu tượng, không chấm trạng thái
        if (!asleep && s.Name != "boss")
        {
            float ph = (T * 0.5f + seed) % 7f;
            if (ph < 1.6f)
            {
                string[] pool = working ? new[] { "💡", "⚡", "✨", "🔥" } : new[] { "☕", "♪", "❤", "😊" };
                string emo = pool[(int)Math.Abs(seed + Math.Floor((T * 0.5f + seed) / 7f)) % pool.Length];
                int a = (int)(255 * Math.Min(1, Math.Min(ph / 0.25f, (1.6f - ph) / 0.4f)));
                using (var f = new Font("Segoe UI Emoji", Math.Max(8, tile * 0.2f)))
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, a), working ? Color.FromArgb(250, 190, 40) : Color.FromArgb(240, 90, 120))))
                    g.DrawString(emo, f, b, head.X + hr * 0.6f, head.Y - hr * 2.4f - ph * tile * 0.12f);
            }
        }
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

    // ---------- Đi lại thật trong văn phòng ----------
    // Nhân viên rời ghế, đi theo lối đi giữa các dãy bàn tới nơi cần (kho tri thức, bàn đồng nghiệp, phòng Chỉ huy),
    // dừng lại nói chuyện / lấy tài liệu, rồi ôm tài liệu đi về ghế.
    class Stop { public PointF P; public float Wait; public string Say; public bool Carry; }
    class Errand { public Staff Who; public List<Stop> Path = new List<Stop>(); public float Start; }
    readonly List<Errand> errands = new List<Errand>();
    const float WalkSpeed = 2.0f;           // ô sàn / giây
    const float Corridor = 5.85f;           // lối đi dọc bên trái khu bàn
    static float Aisle(float y) { return y <= 3.6f ? 3.6f : y <= 7.95f ? 7.95f : 11.2f; }   // các lối đi ngang
    static PointF Chair(Staff s) { return new PointF(s.DeskX + 1.95f, s.DeskY + 0.5f); }
    static PointF Beside(Staff s) { return new PointF(s.DeskX + 1.95f, s.DeskY + 1.4f); }
    static readonly PointF Vault = new PointF(14.4f, 1.55f), BossSpot = new PointF(5.0f, 2.6f);
    bool OnErrand(Staff s) { return errands.Any(e => e.Who == s); }

    // Đường đi: ra lối đi ngang gần nhất → (đổi lối qua hành lang dọc nếu cần) → tới đích
    static IEnumerable<PointF> Route(PointF a, PointF b)
    {
        float ya = Aisle(a.Y), yb = Aisle(b.Y);
        yield return new PointF(a.X, ya);
        if (ya != yb) { yield return new PointF(Corridor, ya); yield return new PointF(Corridor, yb); }
        yield return new PointF(b.X, yb);
        yield return b;
    }

    /// <summary>Giao cho một nhân viên một chuyến đi: các điểm dừng (đích, thời gian dừng, câu nói, có ôm tài liệu khi rời đi không).</summary>
    void Send(Staff s, params Tuple<PointF, float, string, bool>[] stops)
    {
        if (s == null || OnErrand(s)) return;
        var e = new Errand { Who = s, Start = T };
        var cur = Chair(s); e.Path.Add(new Stop { P = cur });
        bool carry = false;
        foreach (var st in stops.Concat(new[] { Tuple.Create(Chair(s), 0f, (string)null, false) }))
        {
            foreach (var p in Route(cur, st.Item1)) e.Path.Add(new Stop { P = p, Carry = carry });
            var last = e.Path[e.Path.Count - 1]; last.Wait = st.Item2; last.Say = st.Item3;
            carry = st.Item4; last.Carry = carry;
            cur = st.Item1;
        }
        errands.Add(e);
        if (errands.Count > 6) errands.RemoveAt(0);
    }

    // Vị trí hiện tại của chuyến đi: đang đi (walking) hay đang dừng nói chuyện (say)
    bool Where(Errand e, out PointF pos, out bool walking, out string say, out bool carry)
    {
        float t = T - e.Start; pos = e.Path[0].P; walking = false; say = null; carry = false;
        for (int i = 1; i < e.Path.Count; i++)
        {
            var a = e.Path[i - 1].P; var b = e.Path[i].P;
            float d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / WalkSpeed;
            carry = e.Path[i - 1].Carry;
            if (t < d) { float f = d > 0 ? t / d : 1; pos = new PointF(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f); walking = true; return true; }
            t -= d;
            if (t < e.Path[i].Wait) { pos = b; say = e.Path[i].Say; carry = e.Path[i].Carry; return true; }
            t -= e.Path[i].Wait;
        }
        return false;   // đã về tới ghế
    }

    void Walkers()
    {
        foreach (var e in errands.ToList())
        {
            PointF pos; bool walking; string say; bool carry;
            if (!Where(e, out pos, out walking, out say, out carry)) { errands.Remove(e); continue; }
            var s = e.Who; float wx = pos.X, wy = pos.Y;
            Q(wx, wy, g =>
            {
                Shadow(g, wx, wy, 0.35f, 0.35f, 55);
                Person(g, s, wx, wy, 0, false, false, true, walking ? T * 9 : 0);
                if (carry)
                {
                    // Ôm một tập hồ sơ trước ngực
                    var hand = P(wx, wy, 0.95f);
                    using (var b = new SolidBrush(Color.FromArgb(250, 246, 230))) g.FillRectangle(b, hand.X - tile * 0.13f, hand.Y - tile * 0.1f, tile * 0.24f, tile * 0.18f);
                    using (var b = new SolidBrush(s.Shirt)) g.FillRectangle(b, hand.X - tile * 0.13f, hand.Y - tile * 0.1f, tile * 0.24f, tile * 0.04f);
                }
                s.Hit = RectAround(P(wx, wy, 1.1f), tile * 0.9f, tile * 1.2f);
                if (say != null) Bubble(g, P(wx, wy, 2.1f), say, s.Shirt);
            });
        }
    }

    // Mỗi việc thật được diễn thành một chuyến đi thật:
    //  - Trưởng phòng nhận việc từ Chỉ huy → mang tới bàn Claude Code giao việc → về chỗ
    //  - Xong việc: Trưởng phòng ôm báo cáo tới phòng Chỉ huy
    //  - Nhân viên dùng công cụ tra cứu: đi tới Kho tri thức lấy hồ sơ → ôm về bàn
    //  - Hai người cùng làm trong lúc gần nhau: người sau sang bàn người trước xin tài liệu
    void ReactWithWalk(Staff st, ActivityEntry e, List<ActivityEntry> recent)
    {
        var a = (e.Action ?? "").ToLowerInvariant();
        string detail = string.IsNullOrEmpty(e.Detail) ? "" : (e.Detail.Length > 26 ? e.Detail.Substring(0, 26) + "…" : e.Detail);
        if (st.Key == "brain" && e.Channel == "engine")
        {
            if (a == "xong" || a == "done" || a == "lỗi" || a == "error")
                Send(st, Tuple.Create(BossSpot, 3.5f, a == "xong" || a == "done" ? L.T("Report: done ✓", "Báo cáo: đã xong ✓") : L.T("Report: a problem", "Báo cáo: có lỗi"), false));
            else
            {
                var coder = Team.FirstOrDefault(x => x.Key == "claude");
                Send(st, Tuple.Create(BossSpot, 2.2f, L.T("Got it, boss!", "Rõ thưa sếp!"), true),
                         Tuple.Create(Beside(coder), 2.5f, a == "task" ? L.T("New task for you 📋", "Việc mới cho bạn đây 📋") : L.T("Can you look this up? 🔎", "Tra giúp mình cái này 🔎"), false));
            }
            return;
        }
        if (!st.Present) { flyers.Add(new Flyer { To = st, Start = T, C = st.Shirt }); return; }
        // Đồng nghiệp vừa làm việc trong 20 giây trước → sang bàn xin tài liệu
        var mate = Team.FirstOrDefault(x => x != st && x.Present && x.Key != "brain" && !OnErrand(x) && (e.T - x.LastWork).TotalSeconds > 0 && (e.T - x.LastWork).TotalSeconds < 20);
        if (mate != null && !OnErrand(st))
            Send(st, Tuple.Create(Beside(mate), 2.4f, L.T("Can I borrow your notes? 📄", "Cho mình mượn tài liệu nhé 📄"), true));
        else if (!OnErrand(st))
            Send(st, Tuple.Create(Vault, 1.8f, (detail.Length > 0 ? "📂 " + detail : L.T("Fetching files 📂", "Lấy hồ sơ 📂")), true));
        else flyers.Add(new Flyer { To = st, Start = T, C = st.Shirt });   // đang bận đi rồi: hồ sơ bay tới
    }

    // Một chút sống động khi không có việc thật: thỉnh thoảng có người đi pha cà phê
    float nextIdleWalk = 20;
    void IdleWalks()
    {
        if (T < nextIdleWalk) return;
        nextIdleWalk = T + 35 + (float)(new Random().NextDouble() * 40);
        var idle = Team.Where(s => s.Present && !s.Working && !OnErrand(s) && s.Key != "brain").ToList();
        if (idle.Count == 0) return;
        var who = idle[new Random().Next(idle.Count)];
        Send(who, Tuple.Create(new PointF(1.2f, 10.9f), 2.5f, L.T("Coffee break ☕", "Đi pha cà phê ☕"), false));
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

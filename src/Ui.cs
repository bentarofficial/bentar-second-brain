// Language (English default, Vietnamese optional) and light/dark theme preferences.
// Stored in %LOCALAPPDATA%\BentarSecondBrain\ui.ini so every mode (/mcp, /serve, /auto) sees the same choice.
using System;
using System.Drawing;
using System.IO;
using System.Text;

static class L
{
    static readonly string File_ = Path.Combine(Cleaner.DataDir, "ui.ini");
    static readonly System.Collections.Generic.Dictionary<string, string> prefs = new System.Collections.Generic.Dictionary<string, string>();
    static bool loaded;
    static DateTime stamp;

    // Đọc lại khi file đổi, để trạm phát chạy ngầm nhận ngay công tắc vừa bật/tắt trong giao diện
    static void Load()
    {
        DateTime t;
        try { t = File.GetLastWriteTimeUtc(File_); } catch { t = DateTime.MinValue; }
        if (loaded && t == stamp) return;
        loaded = true; stamp = t;
        try
        {
            foreach (var line in File.ReadAllLines(File_, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i > 0) prefs[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
        }
        catch { }
    }

    public static bool Vi { get { return Get("lang", "en") == "vi"; } }
    public static bool Light { get { return Get("theme", "dark") == "light"; } }

    /// <summary>Pick the string for the current language.</summary>
    public static string T(string en, string viText) { return Vi ? viText : en; }

    public static string Get(string key, string def) { Load(); string v; return prefs.TryGetValue(key, out v) ? v : def; }
    public static bool On(string key, bool def) { return Get(key, def ? "1" : "0") == "1"; }

    public static void Set(string key, string value)
    {
        Load();
        prefs[key] = value;
        Directory.CreateDirectory(Cleaner.DataDir);
        var lines = new System.Collections.Generic.List<string>();
        foreach (var kv in prefs) lines.Add(kv.Key + "=" + kv.Value);
        File.WriteAllLines(File_, lines, Encoding.UTF8);
    }
    public static void Set(string key, bool on) { Set(key, on ? "1" : "0"); }

    public static void Save(bool vietnamese, bool lightTheme) { Set("lang", vietnamese ? "vi" : "en"); Set("theme", lightTheme ? "light" : "dark"); }
}

// Colour palette for the current theme
static class Theme
{
    static bool D { get { return !L.Light; } }
    // Phong cách công nghệ: nền xanh đêm sâu, nhấn xanh cyan + chàm (indigo) như màn hình điều khiển
    public static Color Bg { get { return D ? Color.FromArgb(8, 11, 22) : Color.FromArgb(242, 245, 250); } }
    public static Color Side { get { return D ? Color.FromArgb(12, 16, 32) : Color.FromArgb(228, 233, 243); } }
    public static Color Panel2 { get { return D ? Color.FromArgb(24, 32, 58) : Color.FromArgb(255, 255, 255); } }
    public static Color List { get { return D ? Color.FromArgb(15, 21, 41) : Color.FromArgb(255, 255, 255); } }
    public static Color Border { get { return D ? Color.FromArgb(40, 54, 92) : Color.FromArgb(203, 211, 228); } }
    public static Color Ink { get { return D ? Color.FromArgb(226, 234, 248) : Color.FromArgb(17, 24, 39); } }
    public static Color Dim { get { return D ? Color.FromArgb(132, 148, 182) : Color.FromArgb(92, 104, 132); } }
    public static Color Accent { get { return D ? Color.FromArgb(34, 211, 238) : Color.FromArgb(8, 145, 178); } }
    public static Color Violet { get { return D ? Color.FromArgb(99, 102, 241) : Color.FromArgb(79, 70, 229); } }
    public static Color Good { get { return D ? Color.FromArgb(52, 211, 153) : Color.FromArgb(5, 150, 105); } }
    public static Color Bad { get { return D ? Color.FromArgb(251, 113, 133) : Color.FromArgb(225, 29, 72); } }
    public static Color RowBad { get { return D ? Color.FromArgb(70, 32, 38) : Color.FromArgb(253, 226, 228); } }
    public static Color RowGood { get { return D ? Color.FromArgb(24, 58, 44) : Color.FromArgb(220, 245, 230); } }
    // The brain map is a night sky in both themes; its top bar follows it
    public static Color Space { get { return Color.FromArgb(2, 4, 10); } }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Make the Windows title bar follow the app theme (dark mode on Win10 1809+, exact colours on Win11).</summary>
    public static void TitleBar(System.Windows.Forms.Form f)
    {
        EventHandler apply = (s, e) =>
        {
            try
            {
                int dark = D ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref dark, 4) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                    DwmSetWindowAttribute(f.Handle, 19, ref dark, 4);        // older Win10 builds
                int cap = ColorTranslator.ToWin32(Side), txt = ColorTranslator.ToWin32(Ink);
                DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);             // DWMWA_CAPTION_COLOR (Win11)
                DwmSetWindowAttribute(f.Handle, 36, ref txt, 4);             // DWMWA_TEXT_COLOR (Win11)
            }
            catch { }
        };
        apply += (s, e) => { try { int round = 2; DwmSetWindowAttribute(f.Handle, 33, ref round, 4); } catch { } };   // bo góc cửa sổ (Win11)
        if (f.IsHandleCreated) apply(f, EventArgs.Empty);
        f.HandleCreated += apply;
    }
}

// Đường phân cách kéo được (giống Claude Code / VS Code): rê chuột để thấy, kéo để đổi độ rộng,
// bấm nút tròn ‹ › hoặc nhấp đúp để thu gọn / mở lại. Độ rộng được nhớ trong ui.ini.
static class Sizer
{
    /// <param name="side">bảng bên (Dock Left/Right)</param>
    /// <param name="collapsed">độ rộng khi thu gọn (0 = ẩn hẳn, ví dụ 64 = chỉ còn biểu tượng)</param>
    public static System.Windows.Forms.Control Grip(System.Windows.Forms.Control side, string key, int def, int min, int max, int collapsed = 0)
    {
        bool left = side.Dock == System.Windows.Forms.DockStyle.Left;
        var grip = new GripPanel { Dock = side.Dock, Width = 8, Cursor = System.Windows.Forms.Cursors.SizeWE };
        int saved; if (!int.TryParse(L.Get("w." + key, def.ToString()), out saved)) saved = def;
        int open = Math.Max(min, Math.Min(max, saved));
        bool isCollapsed = L.On("c." + key, false);
        Action apply = () =>
        {
            int w = isCollapsed ? collapsed : open;
            side.Visible = w > 0; if (w > 0) side.Width = w;
            grip.Left_ = left; grip.Collapsed = isCollapsed; grip.Invalidate();
        };
        Action toggle = () => { isCollapsed = !isCollapsed; L.Set("c." + key, isCollapsed); apply(); };
        int downX = 0, startW = 0; bool down = false, moved = false;
        grip.MouseDown += (s, e) => { if (e.Button != System.Windows.Forms.MouseButtons.Left) return; down = true; moved = false; downX = System.Windows.Forms.Cursor.Position.X; startW = isCollapsed ? collapsed : side.Width; };
        grip.MouseMove += (s, e) =>
        {
            if (!down) return;
            int dx = System.Windows.Forms.Cursor.Position.X - downX; if (!left) dx = -dx;
            if (!moved && Math.Abs(dx) < 4) return;
            moved = true;
            int w = startW + dx;
            if (w < min / 2) { if (!isCollapsed) { isCollapsed = true; apply(); } return; }   // kéo hẳn về mép: thu gọn
            isCollapsed = false; open = Math.Max(min, Math.Min(max, w)); apply();
        };
        grip.MouseUp += (s, e) =>
        {
            if (!down) return; down = false;
            if (!moved && grip.KnobHit(e.Location)) toggle();
            else if (moved) { L.Set("w." + key, open.ToString()); L.Set("c." + key, isCollapsed); }
        };
        grip.DoubleClick += (s, e) => toggle();
        grip.Toggle = toggle;
        apply();
        return grip;
    }

    class GripPanel : System.Windows.Forms.Panel
    {
        public bool Left_, Collapsed; public Action Toggle; bool hover;
        public GripPanel() { DoubleBuffered = true; SetStyle(System.Windows.Forms.ControlStyles.ResizeRedraw, true); }
        Rectangle Knob { get { return new Rectangle(0, Height / 2 - 18, Width, 36); } }
        public bool KnobHit(Point p) { var k = Knob; k.Inflate(4, 4); return k.Contains(p); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(System.Windows.Forms.MouseEventArgs e) { Cursor = KnobHit(e.Location) ? System.Windows.Forms.Cursors.Hand : System.Windows.Forms.Cursors.SizeWE; base.OnMouseMove(e); }
        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            using (var pen = new Pen(hover ? Theme.Accent : Theme.Border, hover ? 2f : 1f)) g.DrawLine(pen, Width / 2f, 0, Width / 2f, Height);
            // Nút tròn nhỏ ở giữa: mũi tên chỉ hướng thu gọn / mở ra
            var k = Knob;
            using (var path = Round.Path(new RectangleF(k.X + 0.5f, k.Y, k.Width - 1, k.Height), 4))
            using (var br = new SolidBrush(hover ? Theme.Accent : Theme.Panel2)) g.FillPath(br, path);
            bool pointLeft = Left_ != Collapsed;
            float cx = Width / 2f, cy = Height / 2f;
            using (var pen = new Pen(hover ? Theme.Bg : Theme.Dim, 1.6f))
                if (pointLeft) g.DrawLines(pen, new[] { new PointF(cx + 1.5f, cy - 4), new PointF(cx - 1.5f, cy), new PointF(cx + 1.5f, cy + 4) });
                else g.DrawLines(pen, new[] { new PointF(cx - 1.5f, cy - 4), new PointF(cx + 1.5f, cy), new PointF(cx - 1.5f, cy + 4) });
        }
    }
}

// Bo góc mềm: vẽ lại nút và thẻ bằng khử răng cưa (WinForms mặc định chỉ có góc vuông)
static class Round
{
    public static System.Drawing.Drawing2D.GraphicsPath Path(RectangleF r, float radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Color Mix(Color a, Color b, float t) { return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }

    static readonly System.Collections.Generic.HashSet<System.Windows.Forms.Button> done = new System.Collections.Generic.HashSet<System.Windows.Forms.Button>();

    /// <summary>Nút phẳng bo góc, sáng lên khi rê chuột.</summary>
    public static void Button(System.Windows.Forms.Button b, float radius = 8)
    {
        if (!done.Add(b)) return;
        b.Disposed += (s, e) => done.Remove(b);
        bool hover = false, down = false;
        b.MouseEnter += (s, e) => { hover = true; b.Invalidate(); };
        b.MouseLeave += (s, e) => { hover = false; down = false; b.Invalidate(); };
        b.MouseDown += (s, e) => { down = true; b.Invalidate(); };
        b.MouseUp += (s, e) => { down = false; b.Invalidate(); };
        b.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var parent = b.Parent != null ? b.Parent.BackColor : Theme.Bg;
            g.Clear(parent);
            var fill = b.BackColor;
            bool flatNav = b.FlatAppearance.BorderSize == 0;   // nút điều hướng: không viền, chỉ sáng khi rê chuột
            if (hover && b.Enabled) fill = flatNav ? Theme.Panel2 : Mix(fill, Color.White, down ? 0.04f : 0.12f);
            if (!b.Enabled) fill = Mix(fill, parent, 0.5f);
            var rect = new RectangleF(0.5f, 0.5f, b.Width - 1.5f, b.Height - 1.5f);
            using (var path = Path(rect, radius))
            {
                if (fill != parent) using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                if (!flatNav) using (var pen = new Pen(b.FlatAppearance.BorderColor == Color.Empty ? Theme.Border : b.FlatAppearance.BorderColor)) g.DrawPath(pen, path);
            }
            var flags = System.Windows.Forms.TextFormatFlags.VerticalCenter | System.Windows.Forms.TextFormatFlags.EndEllipsis | System.Windows.Forms.TextFormatFlags.SingleLine;
            flags |= b.TextAlign == ContentAlignment.MiddleLeft ? System.Windows.Forms.TextFormatFlags.Left : System.Windows.Forms.TextFormatFlags.HorizontalCenter;
            var tr = new Rectangle(b.Padding.Left + 4, 0, b.Width - b.Padding.Horizontal - 8, b.Height);
            System.Windows.Forms.TextRenderer.DrawText(g, b.Text, b.Font, tr, b.Enabled ? b.ForeColor : Theme.Dim, flags);
        };
        b.Invalidate();
    }

    /// <summary>Thẻ bo góc: panel lấy màu nền của cha, tự vẽ khối bo góc màu "fill" bên trong.</summary>
    public static void Card(System.Windows.Forms.Control c, Color fill, float radius = 10, bool border = true)
    {
        c.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Path(new RectangleF(0.5f, 0.5f, c.Width - 1.5f, c.Height - 1.5f), radius))
            {
                using (var br = new SolidBrush(fill)) g.FillPath(br, path);
                if (border) using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
            }
        };
        c.Resize += (s, e) => c.Invalidate();
    }
}

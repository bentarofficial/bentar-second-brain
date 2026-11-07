// Góc người dùng (dưới cùng bên trái) và trang Cài đặt: mọi thứ bật/tắt, kết nối/ngắt kết nối ở một chỗ.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

// Công tắc bật/tắt kiểu điện thoại
class Toggle : Control
{
    bool on;
    public event EventHandler Changed;
    public Toggle() { Size = new Size(44, 24); Cursor = Cursors.Hand; DoubleBuffered = true; SetStyle(ControlStyles.SupportsTransparentBackColor, true); }
    public bool Checked { get { return on; } set { on = value; Invalidate(); } }
    protected override void OnClick(EventArgs e) { on = !on; Invalidate(); if (Changed != null) Changed(this, e); base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent != null ? Parent.BackColor : BackColor);
        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        using (var path = new GraphicsPath())
        {
            int d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 90, 180); path.AddArc(r.Right - d, r.Y, d, d, 270, 180); path.CloseFigure();
            using (var b = new SolidBrush(on ? Theme.Violet : (Enabled ? Theme.Border : Theme.Side))) g.FillPath(b, path);
        }
        int k = r.Height - 6, x = on ? r.Right - k - 3 : r.X + 3;
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, r.Y + 3, k, k);
    }
}

partial class MainForm
{
    Control userBtn;
    bool userHover;
    FlowLayoutPanel setFlow;
    Label setBusy;
    bool setLoading;

    // ---------- Góc người dùng ----------
    Control BuildUserButton()
    {
        string name = Environment.UserName;
        var b = new Panel { Width = 196, Height = 54, BackColor = Side, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 0) };
        b.Paint += (s, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (userHover || currentPage == "settings")
                using (var path = Round.Path(new RectangleF(0.5f, 0.5f, b.Width - 1.5f, b.Height - 1.5f), 12)) using (var br = new SolidBrush(Panel2)) g.FillPath(br, path);
            using (var br = new LinearGradientBrush(new Rectangle(8, 11, 32, 32), Theme.Accent, Theme.Violet, 45f)) g.FillEllipse(br, 8, 11, 32, 32);
            TextRenderer.DrawText(g, name.Length > 0 ? name.Substring(0, 1).ToUpper() : "?", new Font("Segoe UI", 11f, FontStyle.Bold), new Rectangle(8, 11, 32, 32), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, name, new Font("Segoe UI", 10f, FontStyle.Bold), new Rectangle(48, 10, 120, 18), Ink, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, L.T("Settings · v", "Cài đặt · v") + Updater.Current.ToString(3), new Font("Segoe UI", 8.5f), new Rectangle(48, 29, 130, 16), Dim, TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, "⋯", new Font("Segoe UI", 12f), new Rectangle(168, 14, 24, 24), Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        b.MouseEnter += (s, e) => { userHover = true; b.Invalidate(); };
        b.MouseLeave += (s, e) => { userHover = false; b.Invalidate(); };
        b.Click += (s, e) => UserMenu().Show(b, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
        userBtn = b;
        return b;
    }

    ContextMenuStrip UserMenu()
    {
        var m = new ContextMenuStrip { Renderer = new DarkMenu(), ShowImageMargin = false, Font = new Font("Segoe UI", 10f) };
        Func<string, EventHandler, ToolStripMenuItem> item = (t, h) => { var i = new ToolStripMenuItem(t, null, h) { ForeColor = Ink, Padding = new Padding(4, 4, 4, 4) }; return i; };
        m.Items.Add(item(L.T("⚙  Settings", "⚙  Cài đặt"), (s, e) => ShowPage("settings")));
        m.Items.Add(new ToolStripSeparator());
        var lang = item(L.T("🌐  Language", "🌐  Ngôn ngữ"), null);
        lang.DropDown = new ToolStripDropDownMenu { Renderer = new DarkMenu(), ShowImageMargin = false };
        lang.DropDownItems.Add(item((L.Vi ? "   " : "✓ ") + "English", (s, e) => ChangeUi("lang", "en")));
        lang.DropDownItems.Add(item((L.Vi ? "✓ " : "   ") + "Tiếng Việt", (s, e) => ChangeUi("lang", "vi")));
        var theme = item(L.T("◐  Appearance", "◐  Giao diện"), null);
        theme.DropDown = new ToolStripDropDownMenu { Renderer = new DarkMenu(), ShowImageMargin = false };
        theme.DropDownItems.Add(item((L.Light ? "   " : "✓ ") + L.T("Dark", "Tối"), (s, e) => ChangeUi("theme", "dark")));
        theme.DropDownItems.Add(item((L.Light ? "✓ " : "   ") + L.T("Light", "Sáng"), (s, e) => ChangeUi("theme", "light")));
        m.Items.Add(lang); m.Items.Add(theme);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(item(L.T("↻  Check for updates", "↻  Kiểm tra cập nhật"), async (s, e) => await CheckUpdate(true)));
        m.Items.Add(item(L.T("📁  Open data folder", "📁  Mở thư mục dữ liệu"), (s, e) => OpenDataFolder()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem("Bentar Second Brain " + Updater.Current.ToString(3)) { Enabled = false, ForeColor = Dim });
        return m;
    }

    class DarkMenu : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(Theme.Panel2)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using (var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Theme.Side : Theme.Panel2)) e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Dim; base.OnRenderArrow(e); }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Ink : Theme.Dim; base.OnRenderItemText(e); }
    }

    void ChangeUi(string key, string value)
    {
        if (L.Get(key, key == "lang" ? "en" : "dark") == value) return;
        try { L.Set(key, value); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); return; }
        if (MessageBox.Show(this, L.T("Restart the app now to apply the change?", "Khởi động lại app ngay để áp dụng thay đổi?"), Text, MessageBoxButtons.YesNo) == DialogResult.Yes) Application.Restart();
    }

    static void OpenDataFolder() { try { Process.Start("explorer.exe", "\"" + Cleaner.DataDir + "\""); } catch { } }
    static string AppExe { get { return Installer.IsInstalled ? Installer.Exe : Application.ExecutablePath; } }

    // ---------- Trang Cài đặt ----------
    Panel BuildSettingsPage()
    {
        var p = Page();
        setFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Bg, Padding = new Padding(0, 0, 0, 20) };
        setBusy = Text2("", DockStyle.Top, 24, Dim);
        p.Controls.Add(setFlow); p.Controls.Add(setBusy);
        setFlow.Resize += (s, e) => { foreach (Control c in setFlow.Controls) c.Width = RowWidth; };
        return p;
    }

    int RowWidth { get { return Math.Max(560, Math.Min(900, setFlow.ClientSize.Width - 24)); } }

    // Trạng thái thu thập ngầm (gọi gh, claude... mất vài giây)
    class SetSnap
    {
        public bool HasGh, BackupOn, Encrypted, Station, AutoStart, Schedule;
        public string GhUser, Repo, LastOk;
        public List<AgentLinks.Agent> Agents = new List<AgentLinks.Agent>();
        public List<Tuple<BrainProject, Trust.Info>> Sources = new List<Tuple<BrainProject, Trust.Info>>();
        public int Pending;
    }

    async void RefreshSettings()
    {
        if (setLoading || setFlow == null) return;
        setLoading = true;
        setBusy.Text = L.T("Checking connections…", "Đang kiểm tra các kết nối…");
        if (setFlow.Controls.Count == 0) Render(null);
        SetSnap snap = null;
        try
        {
            snap = await Task.Run(() =>
            {
                var x = new SetSnap();
                try { x.HasGh = Backup.HasGh; if (x.HasGh) x.GhUser = Backup.GhUser(); } catch { }
                try { x.BackupOn = Backup.Configured; x.Encrypted = Backup.Encrypted; var st = Backup.Status(); x.Repo = st.Repo; x.LastOk = st.LastOk; } catch { }
                try { x.Agents = AgentLinks.List(); } catch { }
                try { x.Sources = Brain.Projects(null).Take(15).Select(p => Tuple.Create(p, Trust.Of(p.Path))).ToList(); } catch { }
                try { x.Pending = Memory.PendingCount(); } catch { }
                try { x.Station = PowerStation.IsRunning(); } catch { }
                try { x.AutoStart = Installer.AutoStart; } catch { }
                try { x.Schedule = Scheduler.Exists(); } catch { }
                return x;
            });
        }
        finally { setLoading = false; }
        setBusy.Text = "";
        Render(snap);
    }

    // Chạy một thao tác ngầm, báo kết quả (nếu có) rồi làm mới trang
    async void Act(Func<string> work)
    {
        setBusy.Text = L.T("Working…", "Đang thực hiện…");
        string r;
        try { r = await Task.Run(work); }
        catch (Exception ex) { r = ex.Message; }
        setBusy.Text = "";
        if (!string.IsNullOrEmpty(r)) MessageBox.Show(this, r, Text);
        RefreshSettings();
    }

    bool Ask(string q) { return MessageBox.Show(this, q, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }

    void Render(SetSnap x)
    {
        int keep = setFlow.VerticalScroll.Value;
        setFlow.SuspendLayout();
        foreach (Control c in setFlow.Controls.Cast<Control>().ToList()) c.Dispose();
        setFlow.Controls.Clear();
        string wait = L.T("checking…", "đang kiểm tra…");

        // ----- Chung -----
        Section(L.T("General", "Chung"));
        var cboLang = Combo(new[] { "English", "Tiếng Việt" }, L.Vi ? 1 : 0);
        cboLang.SelectionChangeCommitted += (s, e) => ChangeUi("lang", cboLang.SelectedIndex == 1 ? "vi" : "en");
        Row(L.T("Language", "Ngôn ngữ"), L.T("Language of the whole app.", "Ngôn ngữ của toàn bộ ứng dụng."), null, cboLang);
        var cboTheme = Combo(new[] { L.T("Dark", "Tối"), L.T("Light", "Sáng") }, L.Light ? 1 : 0);
        cboTheme.SelectionChangeCommitted += (s, e) => ChangeUi("theme", cboTheme.SelectedIndex == 1 ? "light" : "dark");
        Row(L.T("Appearance", "Giao diện"), L.T("Dark or light; the title bar follows too.", "Tối hoặc sáng; thanh tiêu đề cũng đổi theo."), null, cboTheme);

        // ----- Sao lưu GitHub -----
        Section(L.T("GitHub backup", "Sao lưu GitHub"));
        if (x == null) Row(L.T("GitHub account", "Tài khoản GitHub"), "", State(wait, Dim));
        else if (!x.HasGh)
            Row(L.T("GitHub account", "Tài khoản GitHub"), L.T("GitHub CLI (gh) is needed to sign in.", "Cần GitHub CLI (gh) để đăng nhập."), State(L.T("Not installed", "Chưa cài"), Bad),
                Link(L.T("Install gh", "Cài gh"), () => { try { Process.Start("https://cli.github.com/"); } catch { } }));
        else if (x.GhUser == null)
            Row(L.T("GitHub account", "Tài khoản GitHub"), L.T("Sign in to back up and restore the brain.", "Đăng nhập để sao lưu và khôi phục bộ não."), State(L.T("Signed out", "Chưa đăng nhập"), Dim),
                Btn2(L.T("Sign in", "Đăng nhập"), true, () => Act(() => { Backup.GhLogin(); return null; })));
        else
            Row(L.T("GitHub account", "Tài khoản GitHub"), "@" + x.GhUser, State(L.T("● Signed in", "● Đã đăng nhập"), Good),
                Btn2(L.T("Sign out", "Đăng xuất"), false, () =>
                {
                    if (Ask(L.T("Sign out of GitHub on this PC? Git and other apps using this sign-in will be signed out too; automatic backup stops until you sign in again.", "Đăng xuất GitHub trên máy này? Git và các app khác dùng chung đăng nhập này cũng bị đăng xuất; sao lưu tự động sẽ dừng cho tới khi đăng nhập lại.")))
                        Act(() => Backup.GhLogout());
                }));

        if (x == null) Row(L.T("Backup repo", "Kho sao lưu"), "", State(wait, Dim));
        else if (x.BackupOn)
        {
            DateTime ok; string last = x.LastOk != null && DateTime.TryParse(x.LastOk, out ok) ? ok.ToString("HH:mm dd/MM/yyyy") : L.T("never", "chưa có");
            Row(L.T("Backup repo", "Kho sao lưu"), x.Repo + "  ·  " + (x.Encrypted ? L.T("encrypted", "có mã hoá") : L.T("not encrypted", "không mã hoá")) + L.T("  ·  last ", "  ·  lần cuối ") + last,
                State(L.T("● Connected", "● Đã kết nối"), Good),
                Btn2(L.T("Back up now", "Sao lưu ngay"), false, () => Act(() => Backup.Run(true))),
                Btn2(L.T("Disconnect", "Ngắt kết nối"), false, () =>
                {
                    if (Ask(L.T("Disconnect the backup on this PC?\n\nThe repo on GitHub is NOT deleted – you can reconnect or restore from it any time. ", "Ngắt sao lưu trên máy này?\n\nRepo trên GitHub KHÔNG bị xoá – có thể kết nối lại hoặc khôi phục bất cứ lúc nào. ") +
                            (x.Encrypted ? L.T("Keep your password / recovery code: it is needed to reconnect.", "Hãy giữ mật khẩu / mã khôi phục: cần khi kết nối lại.") : "")))
                        Act(() => { Backup.Disconnect(); return null; });
                }));
        }
        else
            Row(L.T("Backup repo", "Kho sao lưu"), L.T("Back the brain up to a private GitHub repo.", "Sao lưu bộ não lên một repo GitHub riêng tư."), State(L.T("Not connected", "Chưa kết nối"), Bad),
                Btn2(L.T("Connect", "Kết nối"), true, () => { BackupUI.Setup(this); RefreshSettings(); }),
                Btn2(L.T("Restore", "Khôi phục"), false, () => { BackupUI.Restore(this); RefreshSettings(); }));
        Row(L.T("Automatic backup", "Tự động sao lưu"), L.T("Back up when memory changes (checked every 10 minutes) and test the backup weekly.", "Sao lưu khi trí nhớ thay đổi (10 phút kiểm tra một lần) và tự kiểm tra bản sao lưu mỗi tuần."),
            null, Switch(L.On("autobackup", true), v => L.Set("autobackup", v)));

        // ----- Agent / CLI -----
        Section(L.T("Agents & CLI (MCP)", "Agent & CLI (MCP)"));
        if (x == null) Row(L.T("Agents on this PC", "Agent trên máy"), "", State(wait, Dim));
        else
        {
            foreach (var a in x.Agents)
            {
                var name = a.Name;
                if (!a.Installed && !a.Connected) { Row(name, L.T("Not found on this PC.", "Không có trên máy."), State(L.T("Not installed", "Chưa cài"), Dim)); continue; }
                if (a.Connected)
                    Row(name, L.T("Can search, recall and remember with Second Brain.", "Dùng được tìm kiếm, nhớ lại và ghi nhớ của Second Brain."), State(L.T("● Connected", "● Đã kết nối"), Good),
                        Btn2(L.T("Disconnect", "Ngắt kết nối"), false, () => Act(() => AgentLinks.Disconnect(name))));
                else
                    Row(name, L.T("Installed – not using Second Brain yet.", "Đã cài – chưa dùng Second Brain."), State(L.T("Not connected", "Chưa kết nối"), Dim),
                        Btn2(L.T("Connect", "Kết nối"), true, () => Act(() => { if (!Installer.IsInstalled) Installer.Install(); return AgentLinks.Connect(name); })));
            }
            Row(L.T("All agents", "Tất cả agent"), L.T("Open a NEW agent session after changing connections.", "Sau khi đổi kết nối, mở phiên agent MỚI để có hiệu lực."), null,
                Btn2(L.T("Connect all", "Kết nối tất cả"), false, () => Act(() => { if (!Installer.IsInstalled) Installer.Install(); return string.Join("\n", AgentLinks.ConnectAll(false)); })),
                Btn2(L.T("Disconnect all", "Ngắt tất cả"), false, () => { if (Ask(L.T("Disconnect Second Brain from every agent?", "Ngắt Second Brain khỏi mọi agent?"))) Act(() => string.Join("\n", AgentLinks.DisconnectAll())); }));
        }

        // ----- Nguồn tin cậy: agent chỉ học theo nguồn đã kiểm chứng -----
        Section(L.T("Trusted sources (what agents may learn from)", "Nguồn tin cậy (agent được học theo)"));
        if (x == null) Row(L.T("Projects", "Dự án"), "", State(wait, Dim));
        else
        {
            Row(L.T("Memories awaiting your approval", "Ghi nhớ chờ bạn duyệt"),
                L.T("Agents' notes without checkable evidence are not shared until you approve them (Memory page).", "Ghi chú của agent chưa có bằng chứng kiểm tra được sẽ không được chia sẻ cho tới khi bạn duyệt (trang Trí nhớ)."),
                State(x.Pending.ToString(), x.Pending > 0 ? Accent : Dim), Btn2(L.T("Review", "Xem duyệt"), x.Pending > 0, () => ShowPage("memory")));
            foreach (var t in x.Sources)
            {
                var path = t.Item1.Path; var lv = t.Item2.Level;
                var stateText = lv == Trust.Level.Trusted ? "✓ " + Trust.Label(lv) : lv == Trust.Level.Excluded ? "✗ " + Trust.Label(lv) : "⚠ " + Trust.Label(lv);
                Row(t.Item1.Name, path + "  ·  " + string.Join(", ", t.Item2.Signals), State(stateText, lv == Trust.Level.Trusted ? Good : lv == Trust.Level.Excluded ? Bad : Dim),
                    t.Item2.ByUser ? Btn2(L.T("Auto", "Tự chấm"), false, () => Act(() => { Trust.Set(path, "auto"); return null; }))
                                   : Btn2(L.T("Trust", "Tin cậy"), false, () => Act(() => { Trust.Set(path, "trusted"); return null; })),
                    Btn2(L.T("Exclude", "Loại trừ"), false, () => Act(() => { Trust.Set(path, lv == Trust.Level.Excluded && t.Item2.ByUser ? "auto" : "excluded"); return null; })));
            }
        }

        // ----- Chia sẻ dữ liệu -----
        Section(L.T("Data sharing (local station)", "Chia sẻ dữ liệu (trạm phát)"));
        if (x == null) Row(L.T("Station", "Trạm phát"), "", State(wait, Dim));
        else
            Row(L.T("Station", "Trạm phát"), x.Station ? L.T("HTTP + MCP at ", "HTTP + MCP tại ") + PowerStation.BaseUrl + L.T(" – this PC only", " – chỉ máy này") : L.T("Tools using HTTP (n8n, scripts, extensions) cannot reach the brain.", "Công cụ dùng HTTP (n8n, script, tiện ích) không gọi được bộ não."),
                State(x.Station ? L.T("● Running", "● Đang chạy") : L.T("Stopped", "Đã tắt"), x.Station ? Good : Dim),
                Switch(x.Station, v => { L.Set("station", v); Act(() => { if (v) { PowerStation.EnsureRunning(AppExe); System.Threading.Thread.Sleep(800); } else PowerStation.Stop(); return null; }); }));
        if (x != null)
            Row(L.T("Start with Windows", "Chạy cùng Windows"), L.T("Start the station in the background when you sign in.", "Bật trạm phát ngầm khi đăng nhập Windows."), null,
                Switch(x.AutoStart, v => Act(() => { Installer.AutoStart = v; return null; })));
        Row(L.T("Access key", "Mã khoá truy cập"), L.T("Every HTTP call must carry this key. A new key cuts off every tool using the old one.", "Mọi lệnh gọi HTTP phải kèm mã này. Tạo mã mới sẽ cắt mọi công cụ đang dùng mã cũ."), null,
            Btn2(L.T("Copy key", "Chép mã"), false, () => { PowerStation.LoadConfig(); try { Clipboard.SetText(PowerStation.Token); setBusy.Text = L.T("Key copied.", "Đã chép mã khoá."); } catch { } }),
            Btn2(L.T("New key", "Tạo mã mới"), false, () => { if (Ask(L.T("Create a new key? Tools using the old key must be updated.", "Tạo mã khoá mới? Các công cụ dùng mã cũ phải cập nhật lại."))) Act(() => { PowerStation.NewToken(AppExe); return null; }); }));
        Row(L.T("Connection guide", "Hướng dẫn kết nối"), L.T("Snippets for Claude Code, Codex, Cursor, n8n, curl, Python…", "Mẫu lệnh cho Claude Code, Codex, Cursor, n8n, curl, Python…"), null,
            Btn2(L.T("Open Sockets", "Mở Ổ cắm"), false, () => ShowPage("socket")));

        // ----- Tự động -----
        Section(L.T("Automation", "Tự động"));
        Row(L.T("Check for updates", "Kiểm tra cập nhật"), L.T("Check for a new version when the app opens.", "Tìm bản mới mỗi khi mở app."), null,
            Btn2(L.T("Check now", "Kiểm tra ngay"), false, async () => await CheckUpdate(true)),
            Switch(L.On("autoupdate", true), v => L.Set("autoupdate", v)));
        if (x != null)
            Row(L.T("Automatic PC cleanup", "Tự động dọn máy"), L.T("Schedule and items are set on the PC maintenance page.", "Lịch và các mục dọn chỉnh ở trang Bảo trì máy."),
                State(x.Schedule ? L.T("● On", "● Đang bật") : L.T("Off", "Đang tắt"), x.Schedule ? Good : Dim),
                Btn2(L.T("Configure", "Thiết lập"), false, () => ShowPage("maintain")));

        // ----- Ứng dụng -----
        Section(L.T("App & data", "Ứng dụng & dữ liệu"));
        Row(L.T("Version", "Phiên bản"), Updater.Current.ToString(3) + "  ·  " + (Installer.RunningInstalled ? L.T("installed", "đã cài vào máy") : L.T("running without install", "đang chạy không cài")), null);
        Row(L.T("Data folder", "Thư mục dữ liệu"), Cleaner.DataDir, null, Btn2(L.T("Open", "Mở"), false, OpenDataFolder));

        setFlow.ResumeLayout();
        try { setFlow.VerticalScroll.Value = Math.Min(keep, setFlow.VerticalScroll.Maximum); setFlow.PerformLayout(); } catch { }
    }

    // ---------- Khối dựng ----------
    void Section(string t)
    {
        setFlow.Controls.Add(new Label { Text = t.ToUpper(), Width = RowWidth, Height = 34, ForeColor = Accent, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(0, 10, 0, 4) });
    }

    void Row(string title, string desc, Label state, params Control[] actions)
    {
        var r = new Panel { Width = RowWidth, Height = 62, BackColor = Bg, Margin = new Padding(0, 0, 0, 6), Padding = new Padding(16, 1, 14, 1) };
        Round.Card(r, Theme.List, 12);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.List, Padding = new Padding(0, 13, 0, 0) };
        foreach (var a in actions.Reverse()) { a.Margin = new Padding(8, a is Toggle ? 6 : 0, 0, 0); right.Controls.Add(a); }
        if (state != null) { state.Margin = new Padding(8, 10, 4, 0); right.Controls.Add(state); }
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.List };
        left.Controls.Add(new Label { Text = desc, Dock = DockStyle.Top, Height = 22, ForeColor = Dim, Font = new Font("Segoe UI", 8.75f), AutoEllipsis = true });
        left.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 32, ForeColor = Ink, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft, AutoEllipsis = true });
        r.Controls.Add(left); r.Controls.Add(right);
        setFlow.Controls.Add(r);
    }

    Label State(string t, Color c) { return new Label { Text = t, AutoSize = true, ForeColor = c, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), BackColor = Theme.List }; }

    Button Btn2(string t, bool primary, Action click)
    {
        var b = new Button { Text = t, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Height = 34, MinimumSize = new Size(96, 34), Padding = new Padding(8, 0, 8, 0) };
        Style(b, primary);
        b.Click += (s, e) => click();
        return b;
    }

    Button Link(string t, Action click) { return Btn2(t, false, click); }

    Toggle Switch(bool on, Action<bool> changed)
    {
        var t = new Toggle { Checked = on, BackColor = Theme.List };
        t.Changed += (s, e) => { try { changed(t.Checked); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); } };
        return t;
    }

    ComboBox Combo(string[] items, int sel)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Ink, Font = new Font("Segoe UI", 10f) };
        c.Items.AddRange(items); c.SelectedIndex = sel;
        return c;
    }
}

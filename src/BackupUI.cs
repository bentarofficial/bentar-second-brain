// Hộp thoại thiết lập sao lưu và khôi phục bộ não (dùng cho người không quen dòng lệnh).
// Bố cục tự co giãn (FlowLayout + AutoSize) nên không bị chồng chữ hay cắt chữ ở mọi mức phóng to màn hình,
// và theo đúng giao diện sáng/tối của app.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

static class BackupUI
{
    static readonly string AskedFile = System.IO.Path.Combine(Cleaner.DataDir, "da-hoi-sao-luu");

    // ---------- Khung giao diện dùng chung ----------
    static readonly float Dpi = GetDpi();
    static float GetDpi() { try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; } catch { return 1f; } }
    static int S(int px) { return (int)Math.Round(px * Dpi); }
    static int W { get { return S(480); } }   // bề rộng nội dung

    class Dlg { public Form F; public FlowLayoutPanel Body, Foot; }

    static FlowLayoutPanel Stack(int width = 0, bool inCard = false)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = inCard ? Color.Transparent : Theme.Bg };
        if (width > 0) { p.MinimumSize = new Size(width, 0); p.MaximumSize = new Size(width, 0); }
        return p;
    }

    static Dlg Dialog(string title, string subtitle)
    {
        var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9.75f), AutoScaleMode = AutoScaleMode.None,
            BackColor = Theme.Bg, ForeColor = Theme.Ink, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, S(26), S(22))
        };
        Theme.TitleBar(f);
        var root = Stack(W);
        root.Location = new Point(S(26), S(20));   // Padding của Form không đẩy control, nên đặt lề trái/trên trực tiếp
        root.Controls.Add(Text(title, Theme.Ink, 14f, true, S(4)));
        if (subtitle != null) root.Controls.Add(Text(subtitle, Theme.Dim, 9.75f, false, S(18)));
        var body = Stack(W);
        var foot = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(W, 0), Margin = new Padding(0, S(14), 0, 0), BackColor = Theme.Bg };
        root.Controls.Add(body); root.Controls.Add(foot);
        f.Controls.Add(root);
        return new Dlg { F = f, Body = body, Foot = foot };
    }

    static Label Text(string t, Color c, float size = 9.75f, bool bold = false, int bottom = 0, int width = 0)
    {
        return new Label { Text = t, ForeColor = c, AutoSize = true, MaximumSize = new Size(width > 0 ? width : W, 0), BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, bottom) };
    }

    static Button Btn(string text, bool primary, int width = 0)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme.Violet : Theme.Panel2, ForeColor = primary ? Color.White : Theme.Ink,
            Cursor = Cursors.Hand, Size = new Size(width > 0 ? width : Math.Max(S(112), TextRenderer.MeasureText(text, new Font("Segoe UI", 9.75f)).Width + S(32)), S(38)),
            Margin = new Padding(S(10), 0, 0, 0) };
        b.FlatAppearance.BorderColor = primary ? Theme.Violet : Theme.Border;
        if (primary) b.Font = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        Round.Button(b);
        return b;
    }

    // Ô nhập bo góc: TextBox không viền nằm trong khung tự vẽ, viền sáng màu nhấn khi đang gõ
    static Panel Input(TextBox tb, int width, int bottom = 0)
    {
        tb.BorderStyle = BorderStyle.None; tb.BackColor = Theme.List; tb.ForeColor = Theme.Ink;
        var box = new Panel { Size = new Size(width, tb.PreferredHeight + S(16)), BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, bottom), Cursor = Cursors.IBeam };
        tb.Location = new Point(S(12), S(8)); tb.Width = width - S(24);
        box.Controls.Add(tb);
        box.Click += (s, e) => tb.Focus();
        tb.GotFocus += (s, e) => box.Invalidate(); tb.LostFocus += (s, e) => box.Invalidate();
        box.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round.Path(new RectangleF(0.5f, 0.5f, box.Width - 1.5f, box.Height - 1.5f), S(7)))
            using (var br = new SolidBrush(Theme.List)) using (var pen = new Pen(tb.Focused ? Theme.Accent : Theme.Border, tb.Focused ? 1.5f : 1f))
            { e.Graphics.FillPath(br, path); e.Graphics.DrawPath(pen, path); }
        };
        return box;
    }

    // Thẻ lựa chọn: mark 0 = thẻ bấm được, 1 = nút chọn tròn, 2 = ô tích. Bấm vào thẻ (tiêu đề, mô tả) là chọn.
    static int CardInner(int mark) { return W - (mark > 0 ? S(46) : S(18)) - S(18); }
    static FlowLayoutPanel Card(string title, string desc, int mark, Func<bool> on, Action click, params Control[] extra)
    {
        var c = Stack(W);
        c.Padding = new Padding(mark > 0 ? S(46) : S(18), S(13), S(18), S(13));
        c.Margin = new Padding(0, 0, 0, S(10));
        c.Cursor = Cursors.Hand;
        int inner = CardInner(mark);
        var t = Text(title, Theme.Ink, 10f, true, 0, inner);
        c.Controls.Add(t);
        Label d = null;
        if (!string.IsNullOrEmpty(desc)) { d = Text(desc, Theme.Dim, 9f, false, 0, inner); d.Margin = new Padding(0, S(3), 0, 0); c.Controls.Add(d); }
        foreach (var x in extra) { x.Margin = new Padding(0, S(10), 0, x.Margin.Bottom); c.Controls.Add(x); }
        bool hover = false;
        EventHandler go = (s, e) => { click(); var f = c.FindForm(); if (f != null) f.Invalidate(true); };
        EventHandler enter = (s, e) => { hover = true; c.Invalidate(); }, leave = (s, e) => { hover = false; c.Invalidate(); };
        foreach (Control x in new Control[] { c, t, d }.Where(x => x != null)) { x.Click += go; x.MouseEnter += enter; x.MouseLeave += leave; x.Cursor = Cursors.Hand; }
        c.Paint += (s, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            bool sel = on != null && on();
            var fill = sel || hover ? Theme.Panel2 : Theme.List;
            using (var path = Round.Path(new RectangleF(0.75f, 0.75f, c.Width - 2f, c.Height - 2f), S(10)))
            using (var br = new SolidBrush(fill)) using (var pen = new Pen(sel ? Theme.Accent : Theme.Border, sel ? 1.6f : 1f))
            { g.FillPath(br, path); g.DrawPath(pen, path); }
            if (mark == 0) return;
            float sz = S(18), x0 = S(16), y0 = S(14);
            var r = new RectangleF(x0, y0, sz, sz);
            if (mark == 1)
            {
                using (var pen = new Pen(sel ? Theme.Accent : Theme.Dim, 1.6f)) g.DrawEllipse(pen, r);
                if (sel) using (var br = new SolidBrush(Theme.Accent)) g.FillEllipse(br, x0 + sz * 0.27f, y0 + sz * 0.27f, sz * 0.46f, sz * 0.46f);
            }
            else using (var path = Round.Path(r, S(4)))
            {
                if (sel)
                {
                    using (var br = new SolidBrush(Theme.Accent)) g.FillPath(br, path);
                    using (var pen = new Pen(Theme.Bg, 2.2f)) g.DrawLines(pen, new[] { new PointF(x0 + sz * 0.24f, y0 + sz * 0.52f), new PointF(x0 + sz * 0.43f, y0 + sz * 0.70f), new PointF(x0 + sz * 0.77f, y0 + sz * 0.32f) });
                }
                else using (var pen = new Pen(Theme.Dim, 1.6f)) g.DrawPath(pen, path);
            }
        };
        c.Resize += (s, e) => c.Invalidate();
        return c;
    }

    // Dòng thông báo trạng thái: ẩn khi trống
    static Label Msg() { var l = Text("", Theme.Dim, 9.5f, false, S(4)); l.Visible = false; return l; }
    static void Say(Label l, string text, Color c) { l.Text = text ?? ""; l.ForeColor = c; l.Visible = !string.IsNullOrEmpty(text); }

    // ---------- Lần đầu mở app: khôi phục bộ não cũ, tạo mới + sao lưu, hay để sau ----------
    public static void FirstRun(IWin32Window owner)
    {
        if (Backup.Configured || System.IO.File.Exists(AskedFile)) return;
        var d = Dialog(L.T("Welcome to Bentar Second Brain", "Chào mừng tới Bentar Second Brain"),
            L.T("Your brain keeps learning and remembering. Back it up to a private GitHub repo to keep it for life – get it back on a new PC or after losing one.",
                "Bộ não học và nhớ ngày càng nhiều. Hãy sao lưu nó lên một repo GitHub riêng tư để giữ được cả đời – đổi máy hay mất máy vẫn lấy lại được."));
        string choice = null;
        d.Body.Controls.Add(Card(L.T("Start a new brain", "Tạo bộ não mới"), L.T("Create it and back it up to a private GitHub repo.", "Tạo mới và sao lưu lên một repo GitHub riêng tư."), 0, null, () => { choice = "new"; d.F.Close(); }));
        d.Body.Controls.Add(Card(L.T("Restore an existing brain", "Khôi phục bộ não cũ"), L.T("Bring your memories back from a GitHub backup.", "Lấy lại trí nhớ từ bản sao lưu trên GitHub."), 0, null, () => { choice = "restore"; d.F.Close(); }));
        var later = Btn(L.T("Later", "Để sau"), false); later.DialogResult = DialogResult.Cancel;
        d.Foot.Controls.Add(later);
        d.F.CancelButton = later;
        d.F.ShowDialog(owner);
        try { System.IO.Directory.CreateDirectory(Cleaner.DataDir); System.IO.File.WriteAllText(AskedFile, ""); } catch { }
        if (choice == "restore") Restore(owner);
        else if (choice == "new") Setup(owner);
    }

    // ---------- Thiết lập sao lưu ----------
    public static void Setup(IWin32Window owner)
    {
        if (!Backup.GitAvailable()) { MessageBox.Show(owner, L.T("Git is not installed. Install Git (git-scm.com) and try again.", "Máy chưa cài Git. Hãy cài Git (git-scm.com) rồi thử lại."), Cleaner.AppName); return; }
        var d = Dialog(L.T("Back up the brain to GitHub", "Sao lưu bộ não lên GitHub"),
            L.T("Your brain is saved to a PRIVATE GitHub repo. Every backup is a version kept forever – on a new or lost PC, just sign in to GitHub to restore it.",
                "Bộ não được lưu vào một repo GitHub RIÊNG TƯ. Mỗi lần sao lưu là một phiên bản giữ được cả đời – đổi máy hay mất máy chỉ cần đăng nhập GitHub là khôi phục được."));
        var f = d.F;
        bool useNew = true, enc = false;
        int in1 = CardInner(1), in2 = CardInner(2);
        var txtName = new TextBox { Text = "bentar-second-brain-backup" };
        var txtUrl = new TextBox();
        var txtPass = new TextBox { UseSystemPasswordChar = true };
        var txtPass2 = new TextBox { UseSystemPasswordChar = true };
        var nameBox = Input(txtName, in1);
        var urlBox = Input(txtUrl, in1);
        var passPanel = Stack(in2, true);
        passPanel.Controls.Add(Text(L.T("Password (at least 8 characters)", "Mật khẩu (ít nhất 8 ký tự)"), Theme.Dim, 9f, false, S(4), in2));
        passPanel.Controls.Add(Input(txtPass, in2, S(10)));
        passPanel.Controls.Add(Text(L.T("Repeat password", "Nhập lại mật khẩu"), Theme.Dim, 9f, false, S(4), in2));
        passPanel.Controls.Add(Input(txtPass2, in2));
        Action sync = () =>
        {
            f.SuspendLayout();
            nameBox.Visible = useNew; urlBox.Visible = !useNew; passPanel.Visible = enc;
            f.ResumeLayout(true); f.Invalidate(true);
        };
        d.Body.Controls.Add(Card(L.T("Create a new private repo", "Tạo repo riêng tư mới"), L.T("The app creates it on your GitHub account.", "App tự tạo repo trên tài khoản GitHub của bạn."), 1,
            () => useNew, () => { useNew = true; sync(); txtName.Focus(); }, nameBox));
        d.Body.Controls.Add(Card(L.T("Use an existing repo", "Dùng repo có sẵn"), L.T("Its name (e.g. my-backup) or full address.", "Tên repo (vd. my-backup) hoặc địa chỉ đầy đủ."), 1,
            () => !useNew, () => { useNew = false; sync(); txtUrl.Focus(); }, urlBox));
        d.Body.Controls.Add(Card(L.T("Also encrypt with a password (optional)", "Mã hoá thêm bằng mật khẩu (không bắt buộc)"),
            L.T("Without it, anyone who gets into your GitHub account can read your memories. With it, restoring also needs the password – you get a recovery code in case you forget.",
                "Không mã hoá: ai vào được tài khoản GitHub của bạn đều đọc được trí nhớ. Có mã hoá: khi khôi phục cần thêm mật khẩu – app cho một mã khôi phục phòng khi quên."), 2,
            () => enc, () => { enc = !enc; sync(); if (enc) txtPass.Focus(); }, passPanel));
        var lblMsg = Msg();
        d.Body.Controls.Add(lblMsg);
        var cancel = Btn(L.T("Cancel", "Huỷ"), false); cancel.DialogResult = DialogResult.Cancel;
        var ok = Btn(L.T("Set up backup", "Thiết lập sao lưu"), true);
        d.Foot.Controls.Add(cancel); d.Foot.Controls.Add(ok);
        f.CancelButton = cancel; f.AcceptButton = ok;
        sync();
        string code = null;
        ok.Click += async (s, e) =>
        {
            if (enc && txtPass.Text != txtPass2.Text) { Say(lblMsg, L.T("The passwords do not match.", "Hai lần nhập mật khẩu không khớp."), Theme.Bad); return; }
            ok.Enabled = false; Say(lblMsg, L.T("Setting up and running the first backup…", "Đang thiết lập và sao lưu lần đầu…"), Theme.Dim);
            bool isNew = useNew; string name = txtName.Text.Trim(), url = txtUrl.Text.Trim(), pass = enc ? txtPass.Text : null;
            try
            {
                if (isNew)
                {
                    Say(lblMsg, L.T("Checking GitHub sign-in…", "Đang kiểm tra đăng nhập GitHub…"), Theme.Dim);
                    bool logged = await Task.Run(() => Backup.HasGh && Backup.GhLoggedIn());
                    if (Backup.HasGh && !logged)
                    {
                        MessageBox.Show(f, L.T("You need to sign in to GitHub once on this PC.\n\n1. A black window opens and shows a code (already copied to the clipboard).\n2. Click the black window and press Enter to open github.com/login/device.\n3. Paste the code (Ctrl+V), click Continue, then Authorize.\n\nThe black window closes when done and the app continues.", "Cần đăng nhập GitHub một lần trên máy này.\n\n1. Một cửa sổ đen sẽ mở ra và hiện một mã (đã tự chép vào clipboard).\n2. Bấm vào cửa sổ đen, nhấn Enter để mở trang github.com/login/device.\n3. Dán mã (Ctrl+V), bấm Continue rồi Authorize.\n\nCửa sổ đen tự đóng khi xong và app sẽ làm tiếp."), Cleaner.AppName);
                        Say(lblMsg, L.T("Waiting for you to sign in to GitHub in the black window…", "Đang chờ bạn đăng nhập GitHub trong cửa sổ đen…"), Theme.Dim);
                        await Task.Run(() => Backup.GhLogin());
                    }
                    Say(lblMsg, L.T("Creating the private repo and running the first backup…", "Đang tạo repo riêng tư và sao lưu lần đầu…"), Theme.Dim);
                }
                Action<string> progress = step => { try { f.BeginInvoke(new Action(() => Say(lblMsg, step, Theme.Dim))); } catch { } };
                code = await Task.Run(() => Backup.Setup(isNew ? Backup.PrepareRepo(name, progress) : url, pass));
                f.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { Say(lblMsg, ex.Message, Theme.Bad); ok.Enabled = true; }
        };
        var res = f.ShowDialog(owner);
        if (res == DialogResult.OK && code != null) ShowCode(owner, code);
        else if (res == DialogResult.OK) MessageBox.Show(owner, L.T("The brain is backed up to GitHub. From now on the app backs up whenever memory changes.\nNew or lost PC: click Restore and sign in to GitHub.", "Đã sao lưu bộ não lên GitHub. Từ giờ app tự sao lưu mỗi khi trí nhớ thay đổi.\nĐổi máy hay mất máy: bấm Khôi phục và đăng nhập GitHub."), Cleaner.AppName);
    }

    // ---------- Hiện mã khôi phục một lần; bắt người dùng xác nhận đã cất giữ ----------
    static void ShowCode(IWin32Window owner, string code)
    {
        var d = Dialog(L.T("Brain recovery code", "Mã khôi phục bộ não"),
            L.T("Backup succeeded. This is your RECOVERY CODE, shown only ONCE. Write it down or save it in a password manager. Use it if you forget the password or restore on a new PC.",
                "Đã sao lưu thành công. Đây là MÃ KHÔI PHỤC, chỉ hiện MỘT LẦN. Hãy chép ra giấy hoặc lưu vào trình quản lý mật khẩu. Dùng nó khi quên mật khẩu hoặc khôi phục trên máy mới."));
        var f = d.F;
        f.ControlBox = false;
        f.TopMost = true;   // không để mã khôi phục bị che khuất
        var copy = Btn(L.T("Copy", "Chép"), false);
        copy.Margin = Padding.Empty;
        var txt = new TextBox { Text = code, ReadOnly = true, Font = new Font("Consolas", 14f, FontStyle.Bold) };
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, S(12)), BackColor = Theme.Bg };
        var box = Input(txt, W - copy.Width - S(10));
        box.Margin = new Padding(0, 0, S(10), 0);
        txt.ForeColor = Theme.Accent;
        box.Height = Math.Max(box.Height, copy.Height); copy.Height = box.Height;
        row.Controls.Add(box); row.Controls.Add(copy);
        d.Body.Controls.Add(row);
        copy.Click += (s, e) => { try { Clipboard.SetText(code); copy.Text = L.T("Copied ✓", "Đã chép ✓"); } catch { } };
        bool stored = false;
        var done = Btn(L.T("Done", "Xong"), true); done.Enabled = false; done.DialogResult = DialogResult.OK;
        d.Body.Controls.Add(Card(L.T("I have stored the recovery code somewhere safe", "Tôi đã cất mã khôi phục ở nơi an toàn"), null, 2, () => stored, () => { stored = !stored; done.Enabled = stored; done.Invalidate(); }));
        d.Foot.Controls.Add(done);
        f.Shown += (s, e) => { f.ActiveControl = copy; txt.SelectionLength = 0; };
        f.ShowDialog(owner);
    }

    // ---------- Khôi phục bộ não ----------
    public static void Restore(IWin32Window owner)
    {
        if (!Backup.GitAvailable()) { MessageBox.Show(owner, L.T("Git is not installed. Install Git (git-scm.com) and try again.", "Máy chưa cài Git. Hãy cài Git (git-scm.com) rồi thử lại."), Cleaner.AppName); return; }
        var d = Dialog(L.T("Restore the brain", "Khôi phục bộ não"),
            L.T("Bring back all memories from the GitHub backup (new or lost PC). Signing in to GitHub and picking the repo is enough; an encrypted backup also needs the password or recovery code.",
                "Lấy lại toàn bộ trí nhớ từ bản sao lưu trên GitHub (khi đổi máy hoặc mất máy). Đăng nhập GitHub và chọn repo là đủ; chỉ khi bản sao lưu có mã hoá mới cần thêm mật khẩu hoặc mã khôi phục."));
        var f = d.F;
        var btnLogin = Btn(L.T("Sign in to GitHub and load my repos", "Đăng nhập GitHub và tải danh sách repo"), false, W);
        btnLogin.Margin = new Padding(0, 0, 0, S(16)); btnLogin.Enabled = Backup.HasGh;
        var cbo = new ComboBox { Width = W, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.List, ForeColor = Theme.Ink, Margin = new Padding(0, 0, 0, S(14)), Font = new Font("Segoe UI", 10f),
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(26) };
        // Tự vẽ để theo màu giao diện thay cho thanh chọn xanh mặc định của Windows
        cbo.DrawItem += (s, e) =>
        {
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0, hot = !edit && (e.State & DrawItemState.Selected) != 0;
            using (var br = new SolidBrush(hot ? Theme.Panel2 : Theme.List)) e.Graphics.FillRectangle(br, e.Bounds);
            if (e.Index < 0) return;
            var item = cbo.Items[e.Index].ToString();
            TextRenderer.DrawText(e.Graphics, item, cbo.Font, new Rectangle(e.Bounds.X + S(8), e.Bounds.Y, e.Bounds.Width - S(12), e.Bounds.Height),
                hot || item.Contains("★") ? Theme.Accent : Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        var txtUrl = new TextBox { Text = Backup.Status().Repo ?? "" };
        var txtSecret = new TextBox { UseSystemPasswordChar = true };
        var lblMsg = Msg();
        d.Body.Controls.Add(btnLogin);
        d.Body.Controls.Add(Text(L.T("Pick a repo (★ = has a brain)", "Chọn repo (★ = có bộ não)"), Theme.Dim, 9f, false, S(5)));
        d.Body.Controls.Add(cbo);
        d.Body.Controls.Add(Text(L.T("Or paste the repo URL", "Hoặc dán địa chỉ repo"), Theme.Dim, 9f, false, S(5)));
        d.Body.Controls.Add(Input(txtUrl, W, S(14)));
        d.Body.Controls.Add(Text(L.T("Password or recovery code (only if encrypted)", "Mật khẩu hoặc mã khôi phục (chỉ khi có mã hoá)"), Theme.Dim, 9f, false, S(5)));
        d.Body.Controls.Add(Input(txtSecret, W, S(10)));
        d.Body.Controls.Add(lblMsg);
        if (!Backup.HasGh) Say(lblMsg, L.T("GitHub CLI (gh) is not installed, so repos cannot be listed – paste the repo URL.", "Máy chưa có GitHub CLI (gh) nên không chọn repo từ danh sách được – hãy dán địa chỉ repo."), Theme.Bad);
        var cancel = Btn(L.T("Close", "Đóng"), false); cancel.DialogResult = DialogResult.Cancel;
        var ok = Btn(L.T("Restore", "Khôi phục"), true);
        d.Foot.Controls.Add(cancel); d.Foot.Controls.Add(ok);
        f.CancelButton = cancel;
        cbo.SelectedIndexChanged += (s, e) => { var r = cbo.SelectedItem as Backup.RepoInfo; if (r != null) txtUrl.Text = r.Url; };
        Func<bool, Task> load = async login =>
        {
            btnLogin.Enabled = false;
            Say(lblMsg, login ? L.T("Waiting for you to sign in to GitHub in the browser…", "Đang chờ bạn đăng nhập GitHub trên trình duyệt…") : L.T("Loading repos…", "Đang tải danh sách repo…"), Theme.Dim);
            try
            {
                var repos = await Task.Run(() => { if (login && !Backup.GhLoggedIn()) Backup.GhLogin(); return Backup.ListRepos(); });
                cbo.Items.Clear();
                foreach (var r in repos) cbo.Items.Add(r);
                if (cbo.Items.Count > 0) cbo.SelectedIndex = 0;
                if (repos.Any(r => r.IsBackup)) Say(lblMsg, L.T("Found a repo with a brain (★). Click Restore.", "Đã tìm thấy repo có bộ não (★). Bấm Khôi phục."), Theme.Good);
                else Say(lblMsg, L.T("No repo with a brain found – pick the right repo or paste its URL.", "Chưa thấy repo nào có bộ não – hãy chọn đúng repo hoặc dán địa chỉ."), Theme.Dim);
            }
            catch (Exception ex) { Say(lblMsg, ex.Message, Theme.Bad); }
            btnLogin.Enabled = Backup.HasGh;
        };
        btnLogin.Click += async (s, e) => await load(true);
        if (Backup.HasGh) f.Shown += async (s, e) => { if (await Task.Run(() => Backup.GhLoggedIn())) await load(false); };
        ok.Click += async (s, e) =>
        {
            ok.Enabled = false; Say(lblMsg, L.T("Downloading and decrypting…", "Đang tải và giải mã…"), Theme.Dim);
            string url = txtUrl.Text.Trim(), secret = txtSecret.Text;
            try { Say(lblMsg, await Task.Run(() => Backup.Restore(url, secret)), Theme.Good); cancel.Text = L.T("Done", "Xong"); cancel.Invalidate(); }
            catch (Exception ex) { Say(lblMsg, ex.Message, Theme.Bad); ok.Enabled = true; }
        };
        f.ShowDialog(owner);
    }
}

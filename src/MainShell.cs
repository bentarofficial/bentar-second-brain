// Giao diện chính: chủ đề tối, thanh điều hướng bên trái, bộ não là trung tâm.
// Trang: Bộ não · Tìm kiếm · Trí nhớ · Hoạt động · Ổ cắm · Sao lưu · Bảo trì (dọn dẹp máy)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class MainForm
{
    // ---------- Bảng màu ----------
    static readonly Color Bg = Color.FromArgb(9, 13, 28), Side = Color.FromArgb(13, 18, 38), Panel2 = Color.FromArgb(20, 27, 54),
        Ink = Color.FromArgb(222, 230, 247), Dim = Color.FromArgb(140, 155, 190), Accent = Color.FromArgb(110, 180, 255),
        Violet = Color.FromArgb(108, 84, 230), Good = Color.FromArgb(90, 210, 150), Bad = Color.FromArgb(255, 110, 110);

    readonly Label lblBrain = new Label(), lblBackup = new Label(), lblPills = new Label(), lblPageTitle = new Label();
    readonly TextBox txtBrain = new TextBox();
    readonly ListView brainHits = new ListView(), memList = new ListView(), devList = new ListView(), actList = new ListView();
    readonly Dictionary<string, Panel> pages = new Dictionary<string, Panel>();
    readonly Dictionary<string, Button> navButtons = new Dictionary<string, Button>();
    readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
    BrainMap map;
    string currentPage;
    int backupTick;

    void BuildShell()
    {
        ClientSize = new Size(1240, 780);
        MinimumSize = SizeFromClientSize(new Size(1000, 640));
        BackColor = Bg; ForeColor = Ink;

        // ----- Thanh bên trái -----
        var side = new Panel { Dock = DockStyle.Left, Width = 216, BackColor = Side, Padding = new Padding(0, 0, 0, 10) };
        var logo = new Label { Text = "Bentar\nSecond Brain", Dock = DockStyle.Top, Height = 78, ForeColor = Ink, Font = new Font("Segoe UI", 15f, FontStyle.Bold), Padding = new Padding(18, 14, 0, 0) };
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Side, Padding = new Padding(10, 6, 10, 0) };
        var navItems = new[] {
            Tuple.Create("brain", "◉   Bộ não"), Tuple.Create("search", "⌕   Tìm kiếm"), Tuple.Create("memory", "✦   Trí nhớ"),
            Tuple.Create("activity", "⚡   Hoạt động"), Tuple.Create("socket", "⇄   Ổ cắm"), Tuple.Create("backup", "☁   Sao lưu"),
            Tuple.Create("maintain", "⚙   Bảo trì máy") };
        foreach (var it in navItems)
        {
            var key = it.Item1;
            var b = new Button { Text = it.Item2, Width = 196, Height = 42, TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat, ForeColor = Dim,
                BackColor = Side, Font = new Font("Segoe UI", 10.5f), Padding = new Padding(10, 0, 0, 0), Margin = new Padding(0, 2, 0, 2), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Panel2;
            b.Click += (s, e) => ShowPage(key);
            navButtons[key] = b;
            nav.Controls.Add(b);
        }
        var foot = new Panel { Dock = DockStyle.Bottom, Height = 104, BackColor = Side, Padding = new Padding(12, 0, 12, 0) };
        var lblVer = new Label { Text = "Phiên bản " + Updater.Current.ToString(3), Dock = DockStyle.Top, Height = 22, ForeColor = Dim, Font = new Font("Segoe UI", 8.5f) };
        btnUpdate.Text = "Kiểm tra cập nhật";
        Style(btnUpdate); btnUpdate.Dock = DockStyle.Top; btnUpdate.Height = 34;
        btnUpdate.Click += async (s, e) => await CheckUpdate(true);
        foot.Controls.Add(btnUpdate);
        if (!Installer.RunningInstalled)
        {
            var btnInstall = new Button { Text = Installer.IsInstalled ? "Mở bản đã cài" : "Cài vào máy", Dock = DockStyle.Top, Height = 34 };
            Style(btnInstall, true);
            btnInstall.Click += (s, e) =>
            {
                try { if (!Installer.IsInstalled || Updater.Current > Installer.InstalledVersion) Installer.Install(); Process.Start(Installer.Exe); Close(); }
                catch (Exception ex) { MessageBox.Show("Cài đặt không thành công:\n" + ex.Message, Text); }
            };
            foot.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Side });
            foot.Controls.Add(btnInstall);
        }
        foot.Controls.Add(lblVer);
        side.Controls.Add(nav); side.Controls.Add(foot); side.Controls.Add(logo);

        // ----- Thanh trên: tên trang + các chỉ số -----
        var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Bg, Padding = new Padding(20, 0, 20, 0) };
        lblPageTitle.Dock = DockStyle.Left; lblPageTitle.Width = 300; lblPageTitle.ForeColor = Ink; lblPageTitle.TextAlign = ContentAlignment.MiddleLeft;
        lblPageTitle.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
        lblPills.Dock = DockStyle.Fill; lblPills.TextAlign = ContentAlignment.MiddleRight; lblPills.ForeColor = Dim; lblPills.Font = new Font("Segoe UI", 9f);
        lblPills.Cursor = Cursors.Hand;
        lblPills.Click += (s, e) => { if (Backup.Warning() != null) ShowPage("backup"); };
        top.Controls.Add(lblPills); top.Controls.Add(lblPageTitle);

        pages["brain"] = BuildBrainPage();
        pages["search"] = BuildSearchPage();
        pages["memory"] = BuildMemoryPage();
        pages["activity"] = BuildActivityPage();
        pages["socket"] = BuildSocketPage();
        pages["backup"] = BuildBackupPage();
        pages["maintain"] = BuildMaintainPage();
        foreach (var p in pages.Values) { p.Dock = DockStyle.Fill; p.Visible = false; content.Controls.Add(p); }

        Controls.Add(content); Controls.Add(top); Controls.Add(side);
        ShowPage("brain");

        var timer = new Timer { Interval = 1000 };
        timer.Tick += (s, e) => ShowBrainStatus();
        timer.Start();
        Task.Run(() => { Brain.EnsureLoaded(); Brain.RefreshIfStale(TimeSpan.FromHours(6)); });
        Shown += async (s, e) => await CheckUpdate(false);
        Shown += (s, e) => BeginInvoke(new Action(() => BackupUI.FirstRun(this)));   // lần đầu: khôi phục / tạo mới + sao lưu / để sau
    }

    static readonly Dictionary<string, string> PageTitles = new Dictionary<string, string> {
        { "brain", "Bộ não" }, { "search", "Tìm trên toàn máy" }, { "memory", "Trí nhớ" }, { "activity", "Hoạt động" },
        { "socket", "Ổ cắm – cắm agent và công cụ" }, { "backup", "Sao lưu bộ não" }, { "maintain", "Bảo trì máy" } };

    void ShowPage(string key)
    {
        currentPage = key;
        foreach (var kv in pages) kv.Value.Visible = kv.Key == key;
        foreach (var kv in navButtons)
        {
            bool on = kv.Key == key;
            kv.Value.BackColor = on ? Panel2 : Side;
            kv.Value.ForeColor = on ? Ink : Dim;
            kv.Value.Font = new Font("Segoe UI", 10.5f, on ? FontStyle.Bold : FontStyle.Regular);
        }
        lblPageTitle.Text = PageTitles[key];
        if (key == "memory") FillMemories();
        if (key == "activity") FillActivity();
        if (key == "backup") FillBackup();
        if (key == "brain" && map != null) map.Reload();
    }

    // ---------- Kiểu dáng tối cho các điều khiển ----------
    static void Style(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = primary ? Violet : Panel2;
        b.ForeColor = primary ? Color.White : Ink;
        b.FlatAppearance.BorderColor = primary ? Violet : Color.FromArgb(48, 60, 100);
        b.Cursor = Cursors.Hand;
    }

    static Button Btn(string text, int width, bool primary = false)
    {
        var b = new Button { Text = text, Width = width, Height = 36, Margin = new Padding(0, 0, 8, 0) };
        Style(b, primary);
        return b;
    }

    static void Dark(TextBox t) { t.BackColor = Panel2; t.ForeColor = Ink; t.BorderStyle = BorderStyle.FixedSingle; }

    // ListView tối: tự vẽ tiêu đề cột (Windows không cho đổi màu tiêu đề), cột cuối lấp chỗ trống
    static void Dark(ListView lv)
    {
        lv.BackColor = Color.FromArgb(14, 20, 42); lv.ForeColor = Ink; lv.BorderStyle = BorderStyle.None;
        lv.View = View.Details; lv.FullRowSelect = true; lv.OwnerDraw = true;
        lv.DrawColumnHeader += (s, e) =>
        {
            using (var br = new SolidBrush(Panel2)) e.Graphics.FillRectangle(br, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, new Font("Segoe UI", 9f, FontStyle.Bold), new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        lv.DrawItem += (s, e) => e.DrawDefault = true;
        lv.DrawSubItem += (s, e) => e.DrawDefault = true;
        lv.Resize += (s, e) => FillLastColumn(lv);
    }

    static void FillLastColumn(ListView lv)
    {
        if (lv.Columns.Count == 0) return;
        int used = 0;
        for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
        lv.Columns[lv.Columns.Count - 1].Width = Math.Max(120, lv.ClientSize.Width - used - 4);
    }

    static Panel Page() { return new Panel { BackColor = Bg, Padding = new Padding(20, 4, 20, 16) }; }
    static FlowLayoutPanel Row(DockStyle dock, int h) { return new FlowLayoutPanel { Dock = dock, Height = h, BackColor = Bg, WrapContents = false, Padding = new Padding(0, 6, 0, 0) }; }
    static Label Text2(string t, DockStyle dock, int h, Color c, float size = 9.5f)
    { return new Label { Text = t, Dock = dock, Height = h, ForeColor = c, Font = new Font("Segoe UI", size), BackColor = Color.Transparent }; }

    // ---------- Bộ não ----------
    Panel BuildBrainPage()
    {
        var p = new Panel { BackColor = Color.FromArgb(2, 4, 10) };
        map = new BrainMap { Dock = DockStyle.Fill };
        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(2, 4, 10), Padding = new Padding(16, 6, 16, 4) };
        var txt = new TextBox { Dock = DockStyle.Left, Width = 320 };
        Dark(txt);
        var lbl = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(170, 190, 230), Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0),
            Text = "Gõ rồi Enter để tìm trên bản đồ · kéo chuột để xoay · lăn để phóng to" };
        map.StatusChanged += s => lbl.Text = s;
        txt.KeyDown += (s, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            int n = map.Highlight(txt.Text);
            lbl.Text = txt.Text.Trim().Length == 0 ? "" : "\"" + txt.Text.Trim() + "\": " + n + " nơ-ron sáng lên";
        };
        bar.Controls.Add(lbl); bar.Controls.Add(txt);
        p.Controls.Add(map); p.Controls.Add(bar);
        map.Reload();
        return p;
    }

    // ---------- Tìm trên toàn máy ----------
    Panel BuildSearchPage()
    {
        var p = Page();
        var bar = Row(DockStyle.Top, 48);
        txtBrain.Width = 520; txtBrain.Font = new Font("Segoe UI", 11f); txtBrain.Margin = new Padding(0, 4, 8, 0);
        Dark(txtBrain);
        var btnFind = Btn("Tìm", 100, true);
        var btnIndex = Btn("Cập nhật chỉ mục", 150);
        bar.Controls.AddRange(new Control[] { txtBrain, btnFind, btnIndex });
        lblBrain.Dock = DockStyle.Top; lblBrain.Height = 40; lblBrain.ForeColor = Dim; lblBrain.Font = new Font("Segoe UI", 8.5f);
        Dark(brainHits);
        brainHits.Dock = DockStyle.Fill; brainHits.ShowItemToolTips = true;
        brainHits.Columns.Add("File", 420); brainHits.Columns.Add("Sửa", 90); brainHits.Columns.Add("Đoạn khớp", 400);
        brainHits.DoubleClick += (s, e) => { if (brainHits.SelectedItems.Count > 0 && brainHits.SelectedItems[0].Tag != null) Process.Start("explorer.exe", "/select,\"" + brainHits.SelectedItems[0].Tag + "\""); };
        Func<Task> find = async () =>
        {
            var q = txtBrain.Text;
            var hits = await Task.Run(() => Brain.Search(q, null, null, false, 80));
            brainHits.Items.Clear();
            foreach (var h in hits)
            {
                var snip = (h.Snippet ?? "").Replace("\r", "").Replace("\n", "  ·  ");
                brainHits.Items.Add(new ListViewItem(new[] { h.File.Path, DateTime.FromBinary(h.File.Mtime).ToString("dd/MM/yyyy"), snip }) { Tag = h.File.Path, ToolTipText = h.File.Path + "\n" + h.Snippet });
            }
            if (hits.Count == 0) brainHits.Items.Add(new ListViewItem(new[] { "Không tìm thấy.", "", "" }));
        };
        btnFind.Click += async (s, e) => await find();
        txtBrain.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await find(); } };
        btnIndex.Click += (s, e) => { if (!Brain.Building) Task.Run(() => { try { Brain.Build(); } catch (Exception ex) { Brain.Progress = "Lỗi: " + ex.Message; } }); };
        p.Controls.Add(brainHits); p.Controls.Add(lblBrain); p.Controls.Add(bar);
        return p;
    }

    // ---------- Trí nhớ ----------
    readonly ComboBox cboKind = new ComboBox();
    readonly TextBox txtMemFilter = new TextBox();
    static readonly string[] KindKeys = { "", "quyet-dinh", "bai-hoc", "su-that", "viec-can-lam", "ghi-chu" };

    Panel BuildMemoryPage()
    {
        var p = Page();
        var bar = Row(DockStyle.Top, 48);
        cboKind.DropDownStyle = ComboBoxStyle.DropDownList; cboKind.Width = 170; cboKind.FlatStyle = FlatStyle.Flat; cboKind.BackColor = Panel2; cboKind.ForeColor = Ink; cboKind.Margin = new Padding(0, 6, 8, 0);
        cboKind.Items.AddRange(new object[] { "Mọi loại", "Quyết định", "Bài học", "Sự thật", "Việc cần làm", "Ghi chú" });
        cboKind.SelectedIndex = 0;
        txtMemFilter.Width = 300; txtMemFilter.Margin = new Padding(0, 6, 8, 0); Dark(txtMemFilter);
        var bEdit = Btn("Sửa", 80); var bOld = Btn("Đánh dấu đã cũ", 140); var bDel = Btn("Xoá", 80); var bRef = Btn("Làm mới", 90);
        bar.Controls.AddRange(new Control[] { cboKind, txtMemFilter, bEdit, bOld, bDel, bRef });
        Dark(memList);
        memList.Dock = DockStyle.Fill;
        memList.Columns.Add("Loại", 100); memList.Columns.Add("Tiêu đề", 360); memList.Columns.Add("Agent", 110); memList.Columns.Add("Cập nhật", 95); memList.Columns.Add("Nhớ lại", 70); memList.Columns.Add("Nguồn", 300);
        var detail = new Label { Dock = DockStyle.Bottom, Height = 96, ForeColor = Ink, BackColor = Panel2, Padding = new Padding(12, 8, 12, 8), Font = new Font("Segoe UI", 9.5f),
            Text = "Chọn một ghi nhớ để xem nội dung. Agent ghi nhớ bằng công cụ remember; chỉ bạn mới xoá được ghi nhớ." };
        memList.SelectedIndexChanged += (s, e) =>
        {
            var n = memList.SelectedItems.Count > 0 ? memList.SelectedItems[0].Tag as Neuron : null;
            if (n != null) detail.Text = n.Title + (n.Outdated ? "  (đã cũ)" : "") + "\n" + n.Text;
        };
        cboKind.SelectedIndexChanged += (s, e) => FillMemories();
        txtMemFilter.TextChanged += (s, e) => FillMemories();
        bRef.Click += (s, e) => FillMemories();
        Func<Neuron> sel = () => memList.SelectedItems.Count > 0 ? memList.SelectedItems[0].Tag as Neuron : null;
        bEdit.Click += (s, e) =>
        {
            var n = sel(); if (n == null) return;
            var text = Prompt("Sửa ghi nhớ", n.Title, n.Text);
            if (text != null) { Memory.Revise(n.Id, text, n.Outdated); FillMemories(); }
        };
        bOld.Click += (s, e) => { var n = sel(); if (n == null) return; Memory.Revise(n.Id, null, !n.Outdated); FillMemories(); };
        bDel.Click += (s, e) =>
        {
            var n = sel(); if (n == null) return;
            if (MessageBox.Show(this, "Xoá hẳn ghi nhớ \"" + n.Title + "\"?\nKhông khôi phục được (trừ từ bản sao lưu cũ).", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            { Memory.Delete(n.Id); FillMemories(); }
        };
        p.Controls.Add(memList); p.Controls.Add(detail); p.Controls.Add(bar);
        return p;
    }

    void FillMemories()
    {
        List<Neuron> all;
        try { all = Memory.Snapshot().Item1; } catch { return; }
        string kind = KindKeys[Math.Max(0, cboKind.SelectedIndex)];
        var q = Brain.Tokens(txtMemFilter.Text).ToList();
        memList.BeginUpdate();
        memList.Items.Clear();
        foreach (var n in all.Where(n => n.Kind != "file" && n.Kind != "du-an" && (kind == "" || n.Kind == kind)).OrderByDescending(n => n.Updated))
        {
            if (q.Count > 0) { var t = new HashSet<string>(Brain.Tokens(n.Title + " " + n.Text + " " + n.Tags)); if (!q.All(t.Contains)) continue; }
            memList.Items.Add(new ListViewItem(new[] { Memory.Label(n.Kind), n.Title, n.Agent ?? "", n.Updated.ToLocalTime().ToString("dd/MM/yyyy"), n.Uses.ToString(), n.Source ?? "" })
                { Tag = n, ForeColor = n.Outdated ? Dim : Ink });
        }
        if (memList.Items.Count == 0) memList.Items.Add(new ListViewItem(new[] { "", "Chưa có ghi nhớ nào. Agent sẽ ghi nhớ khi làm việc (công cụ remember).", "", "", "", "" }) { ForeColor = Dim });
        memList.EndUpdate();
    }

    string Prompt(string title, string label, string value)
    {
        var f = new Form { Text = title, ClientSize = new Size(560, 300), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Bg, ForeColor = Ink, Font = new Font("Segoe UI", 9.5f) };
        var l = new Label { Text = label, Location = new Point(14, 12), Size = new Size(530, 22) };
        var t = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(14, 38), Size = new Size(530, 200), Text = value };
        Dark(t);
        var ok = Btn("Lưu", 90, true); ok.Location = new Point(358, 250); ok.DialogResult = DialogResult.OK;
        var no = Btn("Huỷ", 90); no.Location = new Point(454, 250); no.DialogResult = DialogResult.Cancel;
        f.Controls.AddRange(new Control[] { l, t, ok, no });
        f.AcceptButton = null; f.CancelButton = no;
        return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
    }

    // ---------- Hoạt động: thiết bị đã cắm + nhật ký ----------
    Panel BuildActivityPage()
    {
        var p = Page();
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 200, BackColor = Bg };
        split.Panel1.BackColor = split.Panel2.BackColor = Bg;
        Dark(devList); devList.Dock = DockStyle.Fill;
        devList.Columns.Add("Thiết bị (bóng đèn)", 260); devList.Columns.Add("Trạng thái", 140); devList.Columns.Add("Lần cuối", 150); devList.Columns.Add("Số lần dùng", 100); devList.Columns.Add("Ổ cắm", 200);
        Dark(actList); actList.Dock = DockStyle.Fill;
        actList.Columns.Add("Lúc", 140); actList.Columns.Add("Thiết bị", 180); actList.Columns.Add("Việc", 140); actList.Columns.Add("Chi tiết", 500);
        split.Panel1.Controls.Add(devList);
        split.Panel1.Controls.Add(Text2("Các agent và công cụ đã cắm vào bộ não trong 30 ngày qua", DockStyle.Top, 26, Dim));
        split.Panel2.Controls.Add(actList);
        split.Panel2.Controls.Add(Text2("Nhật ký hoạt động (mới nhất trên cùng)", DockStyle.Top, 26, Dim));
        p.Controls.Add(split);
        var t = new Timer { Interval = 3000 };
        t.Tick += (s, e) => { if (currentPage == "activity") FillActivity(); };
        t.Start();
        return p;
    }

    void FillActivity()
    {
        Task.Run(() => Tuple.Create(Activity.Devices(30), Activity.Recent(300))).ContinueWith(r =>
        {
            if (r.IsFaulted || IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                devList.BeginUpdate(); devList.Items.Clear();
                foreach (var d in r.Result.Item1)
                {
                    var ago = DateTime.Now - d.LastSeen;
                    string state = ago.TotalMinutes < 2 ? "● đang sáng" : ago.TotalHours < 24 ? "○ hôm nay" : "○ nghỉ";
                    devList.Items.Add(new ListViewItem(new[] { d.Name, state, d.LastSeen.ToString("HH:mm dd/MM/yyyy"), d.Uses.ToString(), d.Channels }) { ForeColor = ago.TotalMinutes < 2 ? Good : Ink });
                }
                if (devList.Items.Count == 0) devList.Items.Add(new ListViewItem(new[] { "Chưa có thiết bị nào cắm vào. Xem trang Ổ cắm.", "", "", "", "" }) { ForeColor = Dim });
                devList.EndUpdate();
                actList.BeginUpdate(); actList.Items.Clear();
                foreach (var a in r.Result.Item2) actList.Items.Add(new ListViewItem(new[] { a.T.ToString("HH:mm:ss dd/MM"), a.Client, a.Action, a.Detail }));
                actList.EndUpdate();
            }));
        });
    }

    // ---------- Ổ cắm: cách cắm mọi loại agent / công cụ ----------
    Panel BuildSocketPage()
    {
        var p = Page();
        var bar = Row(DockStyle.Top, 48);
        var bAuto = Btn("Tự cắm các agent trên máy", 220, true);
        var bShow = Btn("Hiện mã khoá", 130);
        var bCopy = Btn("Chép hướng dẫn", 140);
        var bStart = Btn("Bật trạm phát", 130);
        bar.Controls.AddRange(new Control[] { bAuto, bShow, bCopy, bStart });
        var lblState = Text2("", DockStyle.Top, 28, Dim);
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 10f), WordWrap = true };
        Dark(box);
        bool reveal = false;
        Action refresh = () =>
        {
            PowerStation.LoadConfig();
            bool running = PowerStation.IsRunning();
            lblState.ForeColor = running ? Good : Bad;
            lblState.Text = running ? "● Trạm phát đang chạy tại " + PowerStation.BaseUrl + " – mọi thiết bị dưới đây dùng chung một bộ não." : "● Trạm phát chưa chạy – bấm \"Bật trạm phát\".";
            box.Text = SocketGuide(reveal ? PowerStation.Token : "<bấm Hiện mã khoá>");
        };
        bAuto.Click += async (s, e) => await ConnectAgents();
        bShow.Click += (s, e) => { reveal = !reveal; bShow.Text = reveal ? "Ẩn mã khoá" : "Hiện mã khoá"; refresh(); };
        bCopy.Click += (s, e) => { try { Clipboard.SetText(SocketGuide(PowerStation.Token)); bCopy.Text = "Đã chép"; } catch { } };
        bStart.Click += (s, e) => { PowerStation.EnsureRunning(Installer.IsInstalled ? Installer.Exe : Application.ExecutablePath); System.Threading.Thread.Sleep(800); refresh(); };
        p.VisibleChanged += (s, e) => { if (p.Visible) refresh(); };
        p.Controls.Add(box); p.Controls.Add(lblState); p.Controls.Add(bar);
        return p;
    }

    static string SocketGuide(string token)
    {
        string exe = Installer.IsInstalled ? Installer.Exe : Application.ExecutablePath, url = PowerStation.BaseUrl;
        string nl = Environment.NewLine;
        return string.Join(nl, new[] {
            "MỘT BỘ NÃO – MỌI AGENT VÀ CÔNG CỤ CẮM VÀO",
            "Mã khoá: " + token,
            "",
            "1) Coding agent hỗ trợ MCP (Claude Code, Codex, Gemini CLI, Cursor, Claude Desktop...)",
            "   Bấm \"Tự cắm các agent trên máy\", hoặc tự thêm:",
            "   Claude Code (stdio):  claude mcp add --scope user second-brain -- \"" + exe + "\" /mcp",
            "   Claude Code (HTTP):   claude mcp add --transport http second-brain " + url + "/mcp --header \"Authorization: Bearer " + token + "\"",
            "   Codex (~/.codex/config.toml):",
            "     [mcp_servers.second-brain]",
            "     command = '" + exe + "'",
            "     args = [\"/mcp\"]",
            "   Cursor / Claude Desktop / Windsurf / Gemini (JSON):",
            "     \"second-brain\": { \"command\": \"" + exe.Replace("\\", "\\\\") + "\", \"args\": [\"/mcp\"] }",
            "",
            "2) Agent hỗ trợ MCP qua HTTP",
            "   URL: " + url + "/mcp     Header: Authorization: Bearer " + token,
            "",
            "3) Mọi công cụ gọi được HTTP (n8n, Make, script, tiện ích trình duyệt, agent tự xây...)",
            "   Mô tả OpenAPI:  " + url + "/openapi.json",
            "   Tìm:            GET  " + url + "/api/search?query=bao%20cao",
            "   Nhớ lại:        GET  " + url + "/api/recall?query=khach%20hang",
            "   Ghi nhớ:        POST " + url + "/api/remember   {\"text\":\"...\",\"kind\":\"lesson\",\"source\":\"D:\\\\du-an\"}",
            "   Header bắt buộc: Authorization: Bearer " + token + "     (đặt tên thiết bị: X-Client-Name: ten-cong-cu)",
            "",
            "   curl:   curl -H \"Authorization: Bearer " + token + "\" \"" + url + "/api/recall?query=bao%20cao\"",
            "   Python: requests.get(\"" + url + "/api/recall\", params={\"query\": \"bao cao\"}, headers={\"Authorization\": \"Bearer " + token + "\"}).json()[\"text\"]",
            "   n8n:    nút HTTP Request · Method GET/POST · URL như trên · Header Authorization = Bearer <mã khoá>",
            "",
            "Công cụ có sẵn: search, list_projects, project_overview, read_file, list_dir, copy, recall, remember, revise_memory, connect, index_status",
            "An toàn: ổ cắm chỉ nghe trên máy này (127.0.0.1); không có mã khoá thì không gọi được; Second Brain không bao giờ sửa/xoá file của bạn." });
    }

    // ---------- Sao lưu ----------
    readonly Label bkState = new Label(), bkInfo = new Label();
    Panel BuildBackupPage()
    {
        var p = Page();
        var intro = Text2("Bộ não được mã hoá rồi sao lưu lên một repo GitHub riêng tư; mỗi lần sao lưu là một phiên bản, giữ được cả đời. " +
            "Đổi máy hay mất máy: bấm Khôi phục, đăng nhập GitHub, chọn repo, nhập mật khẩu hoặc mã khôi phục.", DockStyle.Top, 48, Dim);
        bkState.Dock = DockStyle.Top; bkState.Height = 40; bkState.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        bkInfo.Dock = DockStyle.Top; bkInfo.Height = 110; bkInfo.ForeColor = Ink; bkInfo.Font = new Font("Segoe UI", 10f);
        var bar = Row(DockStyle.Top, 52);
        var bSetup = Btn("Thiết lập sao lưu", 170, true); var bNow = Btn("Sao lưu ngay", 130); var bVerify = Btn("Kiểm tra bản sao lưu", 180); var bRestore = Btn("Khôi phục", 120);
        bar.Controls.AddRange(new Control[] { bSetup, bNow, bVerify, bRestore });
        bSetup.Click += (s, e) => { BackupUI.Setup(this); FillBackup(); };
        bRestore.Click += (s, e) => { BackupUI.Restore(this); FillBackup(); };
        bNow.Click += async (s, e) => { bNow.Enabled = false; var r = await Task.Run(() => Backup.Run(true)); bNow.Enabled = true; FillBackup(); MessageBox.Show(this, r, Text); };
        bVerify.Click += async (s, e) => { bVerify.Enabled = false; var r = await Task.Run(() => Backup.Verify()); bVerify.Enabled = true; FillBackup(); MessageBox.Show(this, r, Text); };
        p.Controls.Add(bar); p.Controls.Add(bkInfo); p.Controls.Add(bkState); p.Controls.Add(intro);
        return p;
    }

    void FillBackup()
    {
        var w = Backup.Warning();
        var st = Backup.Status();
        bkState.ForeColor = w == null ? Good : Bad;
        bkState.Text = w == null ? "● Bộ não đang được sao lưu an toàn" : "● " + w;
        Func<string, string> when = v => { DateTime d; return DateTime.TryParse(v, out d) ? d.ToString("HH:mm dd/MM/yyyy") : "chưa có"; };
        bkInfo.Text = Backup.Configured
            ? "Repo: " + st.Repo + "\nSao lưu gần nhất: " + when(st.LastOk) + "\nKiểm tra gần nhất: " + when(st.LastVerified) + "\nTrạm phát tự sao lưu khi trí nhớ thay đổi (10 phút kiểm tra một lần) và tự mở thử bản sao lưu mỗi tuần."
            : "Chưa thiết lập. Bấm \"Thiết lập sao lưu\" để tạo repo GitHub riêng tư, hoặc \"Khôi phục\" nếu bạn đã có bộ não cũ.";
    }

    // ---------- Bảo trì máy (các tính năng dọn dẹp cũ) ----------
    Panel BuildMaintainPage()
    {
        var p = Page();
        var head = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Bg };
        btnScan.Text = "Quét toàn bộ"; Style(btnScan, true); btnScan.SetBounds(0, 8, 160, 40);
        btnScan.Click += async (s, e) => await Scan();
        lblStatus.SetBounds(172, 6, 800, 42); lblStatus.ForeColor = Ink; lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lblStatus.Text = "Bấm \"Quét toàn bộ\" để phân loại file trên máy. App chỉ TỰ XOÁ nhóm 1; nhóm 2 bạn tự quyết; nhóm 3 không bao giờ bị đụng tới.";
        lblDisk.SetBounds(0, 52, 900, 20); lblDisk.ForeColor = Dim; lblDisk.Font = new Font("Segoe UI", 9f);
        head.Controls.AddRange(new Control[] { btnScan, lblStatus, lblDisk });

        // Nhóm 1
        Dark(list); list.CheckBoxes = true; list.Dock = DockStyle.Fill;
        list.Columns.Add("Mục dọn dẹp (an toàn – app tự xoá được)", 520); list.Columns.Add("Dung lượng", 160);
        foreach (var it in items)
        {
            bool on = settings.Loaded ? settings.Selected.Contains(it.Id) : it.DefaultOn;
            list.Items.Add(new ListViewItem(new[] { it.Name, "—" }) { Checked = on, Tag = it });
        }
        btnClean.Text = "Dọn mục an toàn"; Style(btnClean, true); btnClean.Size = new Size(170, 36); btnClean.Margin = new Padding(0, 0, 8, 0);
        btnClean.Click += async (s, e) => await CleanNow();
        // Nhóm 2
        Dark(review); review.CheckBoxes = true; review.Dock = DockStyle.Fill; review.ShowItemToolTips = true;
        review.Columns.Add("File / thư mục", 300); review.Columns.Add("Dung lượng", 90); review.Columns.Add("Ngày sửa", 90); review.Columns.Add("Vì sao nên xem xét", 300);
        review.DoubleClick += (s, e) => OpenLocation();
        btnOpen.Text = "Mở vị trí"; btnRecycle.Text = "Chuyển vào Thùng rác"; btnMove.Text = "Chuyển sang ổ D";
        foreach (var b in new[] { btnOpen, btnRecycle, btnMove }) { Style(b); b.Size = new Size(b == btnOpen ? 110 : 180, 36); b.Margin = new Padding(0, 0, 8, 0); }
        btnOpen.Click += (s, e) => OpenLocation();
        btnRecycle.Click += async (s, e) => await ReviewAction(false);
        btnMove.Click += async (s, e) => await ReviewAction(true);
        // Nhóm 3
        Dark(keep); keep.Dock = DockStyle.Fill;
        keep.Columns.Add("Mục", 300); keep.Columns.Add("Dung lượng", 90); keep.Columns.Add("Vì sao cần giữ", 330);
        foreach (var k in Analyzer.KeepItems()) keep.Items.Add(new ListViewItem(new[] { k.Name, "—", k.Why }) { Tag = k });
        // Nhóm 4
        Dark(stale); stale.CheckBoxes = true; stale.Dock = DockStyle.Fill; stale.ShowItemToolTips = true;
        stale.Columns.Add("Thư mục", 300); stale.Columns.Add("Dung lượng", 90); stale.Columns.Add("Dùng lần cuối", 100); stale.Columns.Add("Đánh giá", 300);
        stale.DoubleClick += (s, e) => OpenStale();
        btnStaleOpen.Text = "Mở thư mục"; btnStaleRecycle.Text = "Chuyển thư mục đã tích vào Thùng rác";
        Style(btnStaleOpen); btnStaleOpen.Size = new Size(120, 36); btnStaleOpen.Margin = new Padding(0, 0, 8, 0);
        Style(btnStaleRecycle); btnStaleRecycle.Size = new Size(290, 36);
        btnStaleOpen.Click += (s, e) => OpenStale();
        btnStaleRecycle.Click += async (s, e) => await RecycleStale();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        Func<string, Control, Control[], TabPage> tab = (title, body, buttons) =>
        {
            var tp = new TabPage(title) { BackColor = Bg, Padding = new Padding(6) };
            var row = Row(DockStyle.Bottom, 46);
            row.Controls.AddRange(buttons);
            tp.Controls.Add(body); tp.Controls.Add(row);
            return tp;
        };
        tabs.TabPages.Add(tab("1. Xoá an toàn (tự động)", list, new Control[] { btnClean }));
        tabs.TabPages.Add(tab("2. Cần bạn xem xét", review, new Control[] { btnOpen, btnRecycle, btnMove }));
        tabs.TabPages.Add(tab("3. Thư mục lâu không dùng", stale, new Control[] { btnStaleOpen, btnStaleRecycle }));
        tabs.TabPages.Add(tab("4. Cần giữ – không nên xoá", keep, new Control[0]));

        // Tự động dọn dẹp
        var grp = new GroupBox { Text = "Tự động dọn dẹp (chỉ nhóm 1)", Dock = DockStyle.Bottom, Height = 170, ForeColor = Ink };
        chkAuto.Text = "Bật tự động dọn dẹp (dọn ngầm các mục đang tích ở nhóm 1)"; chkAuto.SetBounds(14, 24, 600, 24);
        chkAuto.Checked = settings.AutoEnabled && Scheduler.Exists();
        var lblFreq = new Label { Text = "Tần suất:", Location = new Point(14, 58), AutoSize = true };
        cboFreq.DropDownStyle = ComboBoxStyle.DropDownList;
        cboFreq.Items.AddRange(new object[] { "Hằng ngày", "Mỗi 3 ngày", "Hằng tuần", "Mỗi khi bật máy" });
        cboFreq.SelectedIndex = Math.Max(0, Math.Min(3, settings.Frequency));
        cboFreq.SetBounds(90, 54, 170, 28);
        var lblTime = new Label { Text = "Lúc:", Location = new Point(280, 58), AutoSize = true };
        dtTime.Format = DateTimePickerFormat.Custom; dtTime.CustomFormat = "HH:mm"; dtTime.ShowUpDown = true; dtTime.SetBounds(320, 54, 90, 28);
        DateTime t;
        dtTime.Value = DateTime.TryParse(settings.Time, out t) ? DateTime.Today.Add(t.TimeOfDay) : DateTime.Today.AddHours(12);
        cboFreq.SelectedIndexChanged += (s, e) => dtTime.Enabled = cboFreq.SelectedIndex != 3;
        dtTime.Enabled = cboFreq.SelectedIndex != 3;
        chkRecycle.Text = "Cho phép tự động dọn cả Thùng rác (không khôi phục được)"; chkRecycle.SetBounds(14, 86, 600, 24); chkRecycle.Checked = settings.AutoRecycle;
        chkNotify.Text = "Hiện thông báo nhỏ sau mỗi lần tự động dọn"; chkNotify.SetBounds(14, 110, 600, 24); chkNotify.Checked = settings.Notify;
        btnSave.Text = "Lưu cài đặt"; Style(btnSave); btnSave.SetBounds(640, 24, 140, 34);
        btnSave.Click += (s, e) => SaveSettings();
        btnLog.Text = "Xem nhật ký"; Style(btnLog); btnLog.SetBounds(790, 24, 120, 34);
        btnLog.Click += (s, e) => { if (File.Exists(Cleaner.LogFile)) Process.Start("notepad.exe", "\"" + Cleaner.LogFile + "\""); else MessageBox.Show("Chưa có lần dọn nào.", Text); };
        lblLast.SetBounds(640, 66, 520, 64); lblLast.ForeColor = Dim; lblLast.Font = new Font("Segoe UI", 8.5f);
        grp.Controls.AddRange(new Control[] { chkAuto, lblFreq, cboFreq, lblTime, dtTime, chkRecycle, chkNotify, btnSave, btnLog, lblLast });

        p.Controls.Add(tabs); p.Controls.Add(grp); p.Controls.Add(head);
        return p;
    }

    // ---------- Chỉ số trên thanh trên cùng (mỗi giây) ----------
    void ShowBrainStatus()
    {
        Tuple<int, int> mc;
        try { mc = Memory.Counts(); } catch { mc = Tuple.Create(0, 0); }
        var p = Brain.Progress;
        if (p.Length > 110) p = "…" + p.Substring(p.Length - 110);
        lblBrain.Text = "Chỉ mục: " + Brain.FileCount.ToString("N0") + " file, " + Brain.ProjectCount + " dự án · cập nhật " +
            (Brain.BuiltAt == DateTime.MinValue ? "chưa có" : Brain.BuiltAt.ToString("dd/MM/yyyy HH:mm")) +
            (Brain.Building ? "\n" + p : "\nGõ từ khoá để tìm trên toàn máy (không cần gõ dấu). Nhấp đúp một kết quả để mở vị trí.");
        if (backupTick++ % 5 == 0)
        {
            var w = Backup.Warning();
            int devices = 0;
            try { devices = Activity.Devices(30).Count(d => d.Name != "trạm phát" && d.Name != "sao lưu"); } catch { }
            lblPills.ForeColor = w != null ? Bad : Dim;
            lblPills.Text = Brain.FileCount.ToString("N0") + " file  ·  " + Brain.ProjectCount + " dự án  ·  " + mc.Item1 + " ghi nhớ  ·  " + mc.Item2 + " liên kết  ·  " +
                devices + " thiết bị  ·  " + (w == null ? "✓ đã sao lưu" : "⚠ " + w);
            if (currentPage == "backup") FillBackup();
        }
    }
}

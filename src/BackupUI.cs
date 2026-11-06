// Hộp thoại thiết lập sao lưu và khôi phục bộ não (dùng cho người không quen dòng lệnh)
using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

static class BackupUI
{
    static readonly string AskedFile = System.IO.Path.Combine(Cleaner.DataDir, "da-hoi-sao-luu");

    // Lần đầu mở app: hỏi khôi phục bộ não cũ, tạo mới + sao lưu, hay để sau
    public static void FirstRun(IWin32Window owner)
    {
        if (Backup.Configured || System.IO.File.Exists(AskedFile)) return;
        var f = Dialog("Chào mừng tới Bentar Second Brain", 250);
        f.Controls.Add(new Label { Text = "Bạn đã có bộ não cũ chưa?", Location = new Point(16, 14), AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold) });
        f.Controls.Add(Note("Bộ não học và nhớ ngày càng nhiều. Hãy sao lưu nó lên một repo GitHub riêng tư (đã mã hoá) để giữ được cả đời – đổi máy hay mất máy vẫn lấy lại được.", 48, 40));
        var bRestore = new Button { Text = "Khôi phục bộ não cũ", Location = new Point(16, 100), Size = new Size(488, 36) };
        var bNew = new Button { Text = "Tạo bộ não mới và sao lưu lên GitHub", Location = new Point(16, 142), Size = new Size(488, 36), BackColor = Color.FromArgb(58, 42, 160), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var bLater = new Button { Text = "Để sau", Location = new Point(404, 200), Size = new Size(100, 32), DialogResult = DialogResult.Cancel };
        string choice = null;
        bRestore.Click += (s, e) => { choice = "restore"; f.Close(); };
        bNew.Click += (s, e) => { choice = "new"; f.Close(); };
        f.Controls.AddRange(new Control[] { bRestore, bNew, bLater });
        f.CancelButton = bLater;
        f.ShowDialog(owner);
        try { System.IO.Directory.CreateDirectory(Cleaner.DataDir); System.IO.File.WriteAllText(AskedFile, ""); } catch { }
        if (choice == "restore") Restore(owner);
        else if (choice == "new") Setup(owner);
    }
    static Form Dialog(string title, int h)
    {
        return new Form { Text = title, ClientSize = new Size(520, h), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9.5f), ShowInTaskbar = false };
    }
    static Label Note(string t, int y, int h) { return new Label { Text = t, Location = new Point(16, y), Size = new Size(488, h), ForeColor = Color.DimGray }; }

    public static void Setup(IWin32Window owner)
    {
        if (!Backup.GitAvailable()) { MessageBox.Show(owner, "Máy chưa cài Git. Hãy cài Git (git-scm.com) rồi thử lại.", Cleaner.AppName); return; }
        var f = Dialog("Sao lưu bộ não lên GitHub", 330);
        f.Controls.Add(Note("Bộ não được mã hoá rồi sao lưu lên một repo GitHub RIÊNG TƯ. Mỗi lần sao lưu là một phiên bản, giữ được cả đời; đổi máy hay mất máy vẫn khôi phục lại được.", 12, 40));
        var rbNew = new RadioButton { Text = "Tạo repo riêng tư mới tên:", Location = new Point(16, 60), AutoSize = true, Checked = true };
        var txtName = new TextBox { Text = "bentar-second-brain-backup", Location = new Point(230, 58), Width = 274 };
        var rbUrl = new RadioButton { Text = "Dùng repo có sẵn:", Location = new Point(16, 92), AutoSize = true };
        var txtUrl = new TextBox { Location = new Point(230, 90), Width = 274, Enabled = false };
        rbUrl.CheckedChanged += (s, e) => { txtUrl.Enabled = rbUrl.Checked; txtName.Enabled = !rbUrl.Checked; };
        var lp = new Label { Text = "Mật khẩu (ít nhất 8 ký tự):", Location = new Point(16, 132), AutoSize = true };
        var txtPass = new TextBox { Location = new Point(230, 130), Width = 274, UseSystemPasswordChar = true };
        var lp2 = new Label { Text = "Nhập lại mật khẩu:", Location = new Point(16, 164), AutoSize = true };
        var txtPass2 = new TextBox { Location = new Point(230, 162), Width = 274, UseSystemPasswordChar = true };
        f.Controls.Add(Note("Sau khi thiết lập, app cho bạn một MÃ KHÔI PHỤC. Quên mật khẩu vẫn mở được bằng mã đó. Mất cả hai thì không ai mở được, kể cả nhà phát triển.", 196, 40));
        var lblMsg = new Label { Location = new Point(16, 240), Size = new Size(488, 36), ForeColor = Color.Firebrick };
        var ok = new Button { Text = "Thiết lập", Location = new Point(300, 284), Size = new Size(100, 32) };
        var cancel = new Button { Text = "Huỷ", Location = new Point(404, 284), Size = new Size(100, 32), DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { rbNew, txtName, rbUrl, txtUrl, lp, txtPass, lp2, txtPass2, lblMsg, ok, cancel });
        f.CancelButton = cancel;
        string code = null;
        ok.Click += async (s, e) =>
        {
            if (txtPass.Text != txtPass2.Text) { lblMsg.Text = "Hai lần nhập mật khẩu không khớp."; return; }
            ok.Enabled = false; lblMsg.ForeColor = Color.DimGray; lblMsg.Text = "Đang thiết lập và sao lưu lần đầu…";
            bool useNew = rbNew.Checked; string name = txtName.Text.Trim(), url = txtUrl.Text.Trim(), pass = txtPass.Text;
            try
            {
                if (useNew)
                {
                    lblMsg.Text = "Đang kiểm tra đăng nhập GitHub…";
                    bool logged = await Task.Run(() => Backup.HasGh && Backup.GhLoggedIn());
                    if (Backup.HasGh && !logged)
                    {
                        MessageBox.Show(f, "Cần đăng nhập GitHub. Một cửa sổ đen sẽ mở ra: bấm Enter trong cửa sổ đó, đăng nhập trên trình duyệt, rồi quay lại đây.", Cleaner.AppName);
                        lblMsg.Text = "Đang chờ bạn đăng nhập GitHub trong cửa sổ đen…";
                        await Task.Run(() => Backup.GhLogin());
                    }
                    lblMsg.Text = "Đang tạo repo riêng tư và sao lưu lần đầu…";
                }
                code = await Task.Run(() => Backup.Setup(useNew ? Backup.CreateGithubRepo(name) : url, pass));
                f.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { lblMsg.ForeColor = Color.Firebrick; lblMsg.Text = ex.Message; ok.Enabled = true; }
        };
        if (f.ShowDialog(owner) == DialogResult.OK && code != null) ShowCode(owner, code);
    }

    // Hiện mã khôi phục một lần; bắt người dùng xác nhận đã cất giữ
    static void ShowCode(IWin32Window owner, string code)
    {
        var f = Dialog("Mã khôi phục bộ não", 250);
        f.ControlBox = false;
        f.Controls.Add(Note("Đã sao lưu thành công. Đây là MÃ KHÔI PHỤC, chỉ hiện MỘT LẦN. Hãy chép ra giấy hoặc lưu vào trình quản lý mật khẩu. Dùng nó khi quên mật khẩu hoặc khôi phục trên máy mới.", 12, 56));
        var txt = new TextBox { Text = code, ReadOnly = true, Location = new Point(16, 74), Width = 400, Font = new Font("Consolas", 14f, FontStyle.Bold) };
        var copy = new Button { Text = "Chép", Location = new Point(424, 73), Size = new Size(80, 32) };
        copy.Click += (s, e) => { try { Clipboard.SetText(code); copy.Text = "Đã chép"; } catch { } };
        var chk = new CheckBox { Text = "Tôi đã cất mã khôi phục ở nơi an toàn", Location = new Point(16, 130), AutoSize = true };
        var done = new Button { Text = "Xong", Location = new Point(404, 200), Size = new Size(100, 32), Enabled = false, DialogResult = DialogResult.OK };
        chk.CheckedChanged += (s, e) => done.Enabled = chk.Checked;
        f.Controls.AddRange(new Control[] { txt, copy, chk, done });
        f.ShowDialog(owner);
    }

    public static void Restore(IWin32Window owner)
    {
        if (!Backup.GitAvailable()) { MessageBox.Show(owner, "Máy chưa cài Git. Hãy cài Git (git-scm.com) rồi thử lại.", Cleaner.AppName); return; }
        var f = Dialog("Khôi phục bộ não", 330);
        f.Controls.Add(Note("Lấy lại toàn bộ trí nhớ từ bản sao lưu trên GitHub (khi đổi máy hoặc mất máy). Đăng nhập GitHub để chọn repo, rồi nhập mật khẩu bộ não hoặc mã khôi phục để mở khoá.", 12, 40));
        var btnLogin = new Button { Text = "Đăng nhập GitHub và tải danh sách repo", Location = new Point(16, 58), Size = new Size(488, 32), Enabled = Backup.HasGh };
        var cbo = new ComboBox { Location = new Point(200, 98), Width = 304, DropDownStyle = ComboBoxStyle.DropDownList };
        var l0 = new Label { Text = "Chọn repo (★ = có bộ não):", Location = new Point(16, 101), AutoSize = true };
        var l1 = new Label { Text = "Hoặc dán địa chỉ repo:", Location = new Point(16, 136), AutoSize = true };
        var txtUrl = new TextBox { Location = new Point(200, 134), Width = 304, Text = Backup.Status().Repo ?? "" };
        var l2 = new Label { Text = "Mật khẩu hoặc mã khôi phục:", Location = new Point(16, 172), AutoSize = true };
        var txtSecret = new TextBox { Location = new Point(200, 170), Width = 304, UseSystemPasswordChar = true };
        var lblMsg = new Label { Location = new Point(16, 206), Size = new Size(488, 66), ForeColor = Color.Firebrick };
        if (!Backup.HasGh) lblMsg.Text = "Máy chưa có GitHub CLI (gh) nên không chọn repo từ danh sách được – hãy dán địa chỉ repo.";
        var ok = new Button { Text = "Khôi phục", Location = new Point(300, 284), Size = new Size(100, 32) };
        var cancel = new Button { Text = "Đóng", Location = new Point(404, 284), Size = new Size(100, 32), DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { btnLogin, l0, cbo, l1, txtUrl, l2, txtSecret, lblMsg, ok, cancel });
        f.CancelButton = cancel;
        cbo.SelectedIndexChanged += (s, e) => { var r = cbo.SelectedItem as Backup.RepoInfo; if (r != null) txtUrl.Text = r.Url; };
        Func<bool, Task> load = async login =>
        {
            btnLogin.Enabled = false; lblMsg.ForeColor = Color.DimGray; lblMsg.Text = login ? "Đang chờ bạn đăng nhập GitHub trên trình duyệt…" : "Đang tải danh sách repo…";
            try
            {
                var repos = await Task.Run(() => { if (login && !Backup.GhLoggedIn()) Backup.GhLogin(); return Backup.ListRepos(); });
                cbo.Items.Clear();
                foreach (var r in repos) cbo.Items.Add(r);
                if (cbo.Items.Count > 0) cbo.SelectedIndex = 0;
                lblMsg.Text = repos.Any(r => r.IsBackup) ? "Đã tìm thấy repo có bộ não (★). Nhập mật khẩu rồi bấm Khôi phục." : "Chưa thấy repo nào có bộ não – hãy chọn đúng repo hoặc dán địa chỉ.";
            }
            catch (Exception ex) { lblMsg.ForeColor = Color.Firebrick; lblMsg.Text = ex.Message; }
            btnLogin.Enabled = Backup.HasGh;
        };
        btnLogin.Click += async (s, e) => await load(true);
        if (Backup.HasGh) f.Shown += async (s, e) => { if (await Task.Run(() => Backup.GhLoggedIn())) await load(false); };
        ok.Click += async (s, e) =>
        {
            ok.Enabled = false; lblMsg.ForeColor = Color.DimGray; lblMsg.Text = "Đang tải và giải mã…";
            string url = txtUrl.Text.Trim(), secret = txtSecret.Text;
            try { lblMsg.Text = await Task.Run(() => Backup.Restore(url, secret)); lblMsg.ForeColor = Color.SeaGreen; cancel.Text = "Xong"; }
            catch (Exception ex) { lblMsg.ForeColor = Color.Firebrick; lblMsg.Text = ex.Message; ok.Enabled = true; }
        };
        f.ShowDialog(owner);
    }
}

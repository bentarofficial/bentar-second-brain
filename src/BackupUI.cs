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
        var f = Dialog(L.T("Welcome to Bentar Second Brain", "Chào mừng tới Bentar Second Brain"), 250);
        f.Controls.Add(new Label { Text = L.T("Do you already have a brain?", "Bạn đã có bộ não cũ chưa?"), Location = new Point(16, 14), AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold) });
        f.Controls.Add(Note(L.T("Your brain keeps learning and remembering. Back it up to a private GitHub repo to keep it for life – get it back on a new PC or after losing one.", "Bộ não học và nhớ ngày càng nhiều. Hãy sao lưu nó lên một repo GitHub riêng tư (đã mã hoá) để giữ được cả đời – đổi máy hay mất máy vẫn lấy lại được."), 48, 40));
        var bRestore = new Button { Text = L.T("Restore an existing brain", "Khôi phục bộ não cũ"), Location = new Point(16, 100), Size = new Size(488, 36) };
        var bNew = new Button { Text = L.T("Create a new brain and back it up to GitHub", "Tạo bộ não mới và sao lưu lên GitHub"), Location = new Point(16, 142), Size = new Size(488, 36), BackColor = Color.FromArgb(58, 42, 160), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var bLater = new Button { Text = L.T("Later", "Để sau"), Location = new Point(404, 200), Size = new Size(100, 32), DialogResult = DialogResult.Cancel };
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
        if (!Backup.GitAvailable()) { MessageBox.Show(owner, L.T("Git is not installed. Install Git (git-scm.com) and try again.", "Máy chưa cài Git. Hãy cài Git (git-scm.com) rồi thử lại."), Cleaner.AppName); return; }
        var f = Dialog(L.T("Back up the brain to GitHub", "Sao lưu bộ não lên GitHub"), 330);
        f.Controls.Add(Note(L.T("The brain is backed up to a PRIVATE GitHub repo. Every backup is a version kept forever; on a new or lost PC, just sign in to GitHub to restore it.", "Bộ não được sao lưu lên một repo GitHub RIÊNG TƯ. Mỗi lần sao lưu là một phiên bản, giữ được cả đời; đổi máy hay mất máy chỉ cần đăng nhập GitHub là khôi phục lại được."), 12, 40));
        var rbNew = new RadioButton { Text = L.T("Create a new private repo named:", "Tạo repo riêng tư mới tên:"), Location = new Point(16, 60), AutoSize = true, Checked = true };
        var txtName = new TextBox { Text = "bentar-second-brain-backup", Location = new Point(230, 58), Width = 274 };
        var rbUrl = new RadioButton { Text = L.T("Use an existing repo:", "Dùng repo có sẵn:"), Location = new Point(16, 92), AutoSize = true };
        var txtUrl = new TextBox { Location = new Point(230, 90), Width = 274, Enabled = false };
        rbUrl.CheckedChanged += (s, e) => { txtUrl.Enabled = rbUrl.Checked; txtName.Enabled = !rbUrl.Checked; };
        var chkEnc = new CheckBox { Text = L.T("Also encrypt with a password (optional)", "Mã hoá thêm bằng mật khẩu (không bắt buộc)"), Location = new Point(16, 124), AutoSize = true };
        var lp = new Label { Text = L.T("Password (at least 8 characters):", "Mật khẩu (ít nhất 8 ký tự):"), Location = new Point(36, 154), AutoSize = true };
        var txtPass = new TextBox { Location = new Point(230, 152), Width = 274, UseSystemPasswordChar = true, Enabled = false };
        var lp2 = new Label { Text = L.T("Repeat password:", "Nhập lại mật khẩu:"), Location = new Point(36, 184), AutoSize = true };
        var txtPass2 = new TextBox { Location = new Point(230, 182), Width = 274, UseSystemPasswordChar = true, Enabled = false };
        chkEnc.CheckedChanged += (s, e) => txtPass.Enabled = txtPass2.Enabled = chkEnc.Checked;
        f.Controls.Add(Note(L.T("Unencrypted: anyone with access to your GitHub account can read your memories. Encrypted: restoring also needs the password; the app gives you a recovery code in case you forget it.", "Không mã hoá: ai vào được tài khoản GitHub của bạn đều đọc được trí nhớ. Có mã hoá: cần thêm mật khẩu khi khôi phục, app cho một mã khôi phục phòng khi quên."), 208, 32));
        var lblMsg = new Label { Location = new Point(16, 240), Size = new Size(488, 36), ForeColor = Color.Firebrick };
        var ok = new Button { Text = L.T("Set up", "Thiết lập"), Location = new Point(300, 284), Size = new Size(100, 32) };
        var cancel = new Button { Text = L.T("Cancel", "Huỷ"), Location = new Point(404, 284), Size = new Size(100, 32), DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { rbNew, txtName, rbUrl, txtUrl, chkEnc, lp, txtPass, lp2, txtPass2, lblMsg, ok, cancel });
        f.CancelButton = cancel;
        string code = null;
        ok.Click += async (s, e) =>
        {
            if (chkEnc.Checked && txtPass.Text != txtPass2.Text) { lblMsg.Text = L.T("The passwords do not match.", "Hai lần nhập mật khẩu không khớp."); return; }
            ok.Enabled = false; lblMsg.ForeColor = Color.DimGray; lblMsg.Text = L.T("Setting up and running the first backup…", "Đang thiết lập và sao lưu lần đầu…");
            bool useNew = rbNew.Checked; string name = txtName.Text.Trim(), url = txtUrl.Text.Trim(), pass = chkEnc.Checked ? txtPass.Text : null;
            try
            {
                if (useNew)
                {
                    lblMsg.Text = L.T("Checking GitHub sign-in…", "Đang kiểm tra đăng nhập GitHub…");
                    bool logged = await Task.Run(() => Backup.HasGh && Backup.GhLoggedIn());
                    if (Backup.HasGh && !logged)
                    {
                        MessageBox.Show(f, L.T("You need to sign in to GitHub once on this PC.\n\n1. A black window opens and shows a code (already copied to the clipboard).\n2. Click the black window and press Enter to open github.com/login/device.\n3. Paste the code (Ctrl+V), click Continue, then Authorize.\n\nThe black window closes when done and the app continues.", "Cần đăng nhập GitHub một lần trên máy này.\n\n1. Một cửa sổ đen sẽ mở ra và hiện một mã (đã tự chép vào clipboard).\n2. Bấm vào cửa sổ đen, nhấn Enter để mở trang github.com/login/device.\n3. Dán mã (Ctrl+V), bấm Continue rồi Authorize.\n\nCửa sổ đen tự đóng khi xong và app sẽ làm tiếp."), Cleaner.AppName);
                        lblMsg.Text = L.T("Waiting for you to sign in to GitHub in the black window…", "Đang chờ bạn đăng nhập GitHub trong cửa sổ đen…");
                        await Task.Run(() => Backup.GhLogin());
                    }
                    lblMsg.Text = L.T("Creating the private repo and running the first backup…", "Đang tạo repo riêng tư và sao lưu lần đầu…");
                }
                Action<string> progress = step => { try { f.BeginInvoke(new Action(() => lblMsg.Text = step)); } catch { } };
                code = await Task.Run(() => Backup.Setup(useNew ? Backup.PrepareRepo(name, progress) : url, pass));
                f.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { lblMsg.ForeColor = Color.Firebrick; lblMsg.Text = ex.Message; ok.Enabled = true; }
        };
        var res = f.ShowDialog(owner);
        if (res == DialogResult.OK && code != null) ShowCode(owner, code);
        else if (res == DialogResult.OK) MessageBox.Show(owner, L.T("The brain is backed up to GitHub. From now on the app backs up whenever memory changes.\nNew or lost PC: click Restore and sign in to GitHub.", "Đã sao lưu bộ não lên GitHub. Từ giờ app tự sao lưu mỗi khi trí nhớ thay đổi.\nĐổi máy hay mất máy: bấm Khôi phục và đăng nhập GitHub."), Cleaner.AppName);
    }

    // Hiện mã khôi phục một lần; bắt người dùng xác nhận đã cất giữ
    static void ShowCode(IWin32Window owner, string code)
    {
        var f = Dialog(L.T("Brain recovery code", "Mã khôi phục bộ não"), 250);
        f.ControlBox = false;
        f.TopMost = true;   // không để mã khôi phục bị che khuất
        f.Controls.Add(Note(L.T("Backup succeeded. This is your RECOVERY CODE, shown only ONCE. Write it down or save it in a password manager. Use it if you forget the password or restore on a new PC.", "Đã sao lưu thành công. Đây là MÃ KHÔI PHỤC, chỉ hiện MỘT LẦN. Hãy chép ra giấy hoặc lưu vào trình quản lý mật khẩu. Dùng nó khi quên mật khẩu hoặc khôi phục trên máy mới."), 12, 56));
        var txt = new TextBox { Text = code, ReadOnly = true, Location = new Point(16, 74), Width = 400, Font = new Font("Consolas", 14f, FontStyle.Bold) };
        var copy = new Button { Text = L.T("Copy", "Chép"), Location = new Point(424, 73), Size = new Size(80, 32) };
        copy.Click += (s, e) => { try { Clipboard.SetText(code); copy.Text = L.T("Copied", "Đã chép"); } catch { } };
        var chk = new CheckBox { Text = L.T("I have stored the recovery code somewhere safe", "Tôi đã cất mã khôi phục ở nơi an toàn"), Location = new Point(16, 130), AutoSize = true };
        var done = new Button { Text = L.T("Done", "Xong"), Location = new Point(404, 200), Size = new Size(100, 32), Enabled = false, DialogResult = DialogResult.OK };
        chk.CheckedChanged += (s, e) => done.Enabled = chk.Checked;
        f.Controls.AddRange(new Control[] { txt, copy, chk, done });
        f.ShowDialog(owner);
    }

    public static void Restore(IWin32Window owner)
    {
        if (!Backup.GitAvailable()) { MessageBox.Show(owner, L.T("Git is not installed. Install Git (git-scm.com) and try again.", "Máy chưa cài Git. Hãy cài Git (git-scm.com) rồi thử lại."), Cleaner.AppName); return; }
        var f = Dialog(L.T("Restore the brain", "Khôi phục bộ não"), 330);
        f.Controls.Add(Note(L.T("Bring back all memories from the GitHub backup (new or lost PC). Signing in to GitHub and picking the repo is enough; an encrypted backup also needs the password or recovery code.", "Lấy lại toàn bộ trí nhớ từ bản sao lưu trên GitHub (khi đổi máy hoặc mất máy). Đăng nhập GitHub và chọn repo là đủ; chỉ khi bản sao lưu có mã hoá mới cần thêm mật khẩu hoặc mã khôi phục."), 12, 40));
        var btnLogin = new Button { Text = L.T("Sign in to GitHub and load repos", "Đăng nhập GitHub và tải danh sách repo"), Location = new Point(16, 58), Size = new Size(488, 32), Enabled = Backup.HasGh };
        var cbo = new ComboBox { Location = new Point(200, 98), Width = 304, DropDownStyle = ComboBoxStyle.DropDownList };
        var l0 = new Label { Text = L.T("Pick a repo (★ = has a brain):", "Chọn repo (★ = có bộ não):"), Location = new Point(16, 101), AutoSize = true };
        var l1 = new Label { Text = L.T("Or paste the repo URL:", "Hoặc dán địa chỉ repo:"), Location = new Point(16, 136), AutoSize = true };
        var txtUrl = new TextBox { Location = new Point(200, 134), Width = 304, Text = Backup.Status().Repo ?? "" };
        var l2 = new Label { Text = L.T("Password (if encrypted):", "Mật khẩu (nếu có mã hoá):"), Location = new Point(16, 172), AutoSize = true };
        var txtSecret = new TextBox { Location = new Point(200, 170), Width = 304, UseSystemPasswordChar = true };
        var lblMsg = new Label { Location = new Point(16, 206), Size = new Size(488, 66), ForeColor = Color.Firebrick };
        if (!Backup.HasGh) lblMsg.Text = L.T("GitHub CLI (gh) is not installed, so repos cannot be listed – paste the repo URL.", "Máy chưa có GitHub CLI (gh) nên không chọn repo từ danh sách được – hãy dán địa chỉ repo.");
        var ok = new Button { Text = L.T("Restore", "Khôi phục"), Location = new Point(300, 284), Size = new Size(100, 32) };
        var cancel = new Button { Text = L.T("Close", "Đóng"), Location = new Point(404, 284), Size = new Size(100, 32), DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { btnLogin, l0, cbo, l1, txtUrl, l2, txtSecret, lblMsg, ok, cancel });
        f.CancelButton = cancel;
        cbo.SelectedIndexChanged += (s, e) => { var r = cbo.SelectedItem as Backup.RepoInfo; if (r != null) txtUrl.Text = r.Url; };
        Func<bool, Task> load = async login =>
        {
            btnLogin.Enabled = false; lblMsg.ForeColor = Color.DimGray; lblMsg.Text = login ? L.T("Waiting for you to sign in to GitHub in the browser…", "Đang chờ bạn đăng nhập GitHub trên trình duyệt…") : L.T("Loading repos…", "Đang tải danh sách repo…");
            try
            {
                var repos = await Task.Run(() => { if (login && !Backup.GhLoggedIn()) Backup.GhLogin(); return Backup.ListRepos(); });
                cbo.Items.Clear();
                foreach (var r in repos) cbo.Items.Add(r);
                if (cbo.Items.Count > 0) cbo.SelectedIndex = 0;
                lblMsg.Text = repos.Any(r => r.IsBackup) ? L.T("Found a repo with a brain (★). Click Restore.", "Đã tìm thấy repo có bộ não (★). Bấm Khôi phục.") : L.T("No repo with a brain found – pick the right repo or paste its URL.", "Chưa thấy repo nào có bộ não – hãy chọn đúng repo hoặc dán địa chỉ.");
            }
            catch (Exception ex) { lblMsg.ForeColor = Color.Firebrick; lblMsg.Text = ex.Message; }
            btnLogin.Enabled = Backup.HasGh;
        };
        btnLogin.Click += async (s, e) => await load(true);
        if (Backup.HasGh) f.Shown += async (s, e) => { if (await Task.Run(() => Backup.GhLoggedIn())) await load(false); };
        ok.Click += async (s, e) =>
        {
            ok.Enabled = false; lblMsg.ForeColor = Color.DimGray; lblMsg.Text = L.T("Downloading and decrypting…", "Đang tải và giải mã…");
            string url = txtUrl.Text.Trim(), secret = txtSecret.Text;
            try { lblMsg.Text = await Task.Run(() => Backup.Restore(url, secret)); lblMsg.ForeColor = Color.SeaGreen; cancel.Text = L.T("Done", "Xong"); }
            catch (Exception ex) { lblMsg.ForeColor = Color.Firebrick; lblMsg.Text = ex.Message; ok.Enabled = true; }
        };
        f.ShowDialog(owner);
    }
}

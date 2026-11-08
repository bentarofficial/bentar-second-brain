// Trang Chat: nói chuyện với bộ não, và giao việc cho Claude Code làm trong một dự án.
// Hỏi: chỉ đọc. Giao việc: Claude lập kế hoạch (không sửa gì) → người dùng duyệt → mới bắt đầu làm.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class MainForm
{
    readonly RichTextBox chatLog = new RichTextBox();
    readonly TextBox chatInput = new TextBox();
    readonly ComboBox chatProject = new ComboBox();
    readonly Label chatStatus = new Label();
    Button bAsk, bTask, bApprove, bReject, bStop, bNew, bToken;
    ClaudeEngine.Run chatRun;
    string askSession, planSession, pendingTask, pendingDir;
    List<BrainProject> chatProjects = new List<BrainProject>();
    bool shownReadiness;

    Panel BuildChatPage()
    {
        var p = Page();
        // --- thanh trên: dự án + trạng thái ---
        var top = Row(DockStyle.Top, 48);
        chatProject.DropDownStyle = ComboBoxStyle.DropDownList; chatProject.Width = 360; chatProject.FlatStyle = FlatStyle.Flat; chatProject.BackColor = Panel2; chatProject.ForeColor = Ink;
        chatProject.Margin = new Padding(0, 6, 8, 0); chatProject.Font = new Font("Segoe UI", 10f);
        bNew = Btn(L.T("New chat", "Chat mới"), 110);
        bToken = Btn(L.T("Claude sign-in…", "Đăng nhập Claude…"), 160);
        chatStatus.AutoSize = true; chatStatus.ForeColor = Dim; chatStatus.Margin = new Padding(6, 14, 0, 0); chatStatus.Font = new Font("Segoe UI", 9.5f);
        top.Controls.AddRange(new Control[] { chatProject, bNew, bToken, chatStatus });

        // --- khung hội thoại ---
        chatLog.Dock = DockStyle.Fill; chatLog.ReadOnly = true; chatLog.BorderStyle = BorderStyle.None; chatLog.BackColor = Theme.List; chatLog.ForeColor = Ink;
        chatLog.Font = new Font("Segoe UI", 10.5f); chatLog.DetectUrls = true; chatLog.HideSelection = false;
        var logWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.List, Padding = new Padding(16, 12, 16, 12) };
        logWrap.Controls.Add(chatLog);

        // --- ô nhập + nút ---
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 132, BackColor = Bg, Padding = new Padding(0, 10, 0, 0) };
        chatInput.Multiline = true; chatInput.Dock = DockStyle.Fill; chatInput.ScrollBars = ScrollBars.Vertical; chatInput.Font = new Font("Segoe UI", 10.5f); Dark(chatInput);
        var btns = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 300, FlowDirection = FlowDirection.TopDown, WrapContents = true, BackColor = Bg, Padding = new Padding(10, 0, 0, 0) };
        bAsk = Btn(L.T("Ask  (Ctrl+Enter)", "Hỏi  (Ctrl+Enter)"), 140, true);
        bTask = Btn(L.T("Do task…", "Giao việc…"), 140);
        bApprove = Btn(L.T("✓ Approve plan", "✓ Duyệt kế hoạch"), 140, true); bApprove.Visible = false;
        bReject = Btn(L.T("Cancel plan", "Huỷ kế hoạch"), 140); bReject.Visible = false;
        bStop = Btn(L.T("■ Stop", "■ Dừng"), 140); bStop.Visible = false;
        foreach (var b in new[] { bAsk, bTask, bApprove, bReject, bStop }) b.Margin = new Padding(0, 0, 8, 8);
        btns.Controls.AddRange(new Control[] { bAsk, bTask, bApprove, bReject, bStop });
        bottom.Controls.Add(chatInput); bottom.Controls.Add(btns);

        p.Controls.Add(logWrap); p.Controls.Add(bottom); p.Controls.Add(top);

        bAsk.Click += (s, e) => Ask();
        bTask.Click += (s, e) => PlanTask();
        bApprove.Click += (s, e) => DoTask();
        bReject.Click += (s, e) => { pendingTask = null; planSession = null; SetChatBusy(false); Say(L.T("Plan cancelled. Nothing was changed.", "Đã huỷ kế hoạch. Chưa có gì bị thay đổi."), Dim); };
        bStop.Click += (s, e) => { if (chatRun != null) chatRun.Stop(); };
        bNew.Click += (s, e) => { askSession = null; chatLog.Clear(); Welcome(); };
        bToken.Click += (s, e) => TokenDialog();
        chatInput.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ask(); } };
        chatProject.SelectedIndexChanged += (s, e) => askSession = null;   // đổi dự án = cuộc trò chuyện mới
        p.VisibleChanged += (s, e) => { if (p.Visible) RefreshChat(); };
        Welcome();
        return p;
    }

    void Welcome()
    {
        Say(L.T("Talk to your Second Brain.", "Trò chuyện với Second Brain của bạn."), Accent, true);
        Say(L.T("• Ask: questions about your projects, documents and verified memories. Read-only – nothing is changed.\n" +
                "• Do task: pick a project, describe the work. Claude makes a plan first; it only starts after you approve. It can edit files in that project and run tests, but never pushes, releases or deletes.\n" +
                "Powered by your Claude plan (Claude Code).",
                "• Hỏi: hỏi về dự án, tài liệu và các ghi nhớ đã xác minh. Chỉ đọc – không thay đổi gì.\n" +
                "• Giao việc: chọn dự án, mô tả việc cần làm. Claude lập kế hoạch trước; chỉ bắt đầu sau khi bạn duyệt. Claude được sửa file trong dự án đó và chạy test, nhưng không bao giờ đẩy code, phát hành hay xoá.\n" +
                "Chạy bằng gói Claude của bạn (Claude Code)."), Dim);
    }

    async void RefreshChat()
    {
        if (chatProject.Items.Count == 0)
        {
            try { chatProjects = await Task.Run(() => Brain.Projects(null).Where(x => Directory.Exists(x.Path)).Take(60).ToList()); } catch { }
            chatProject.Items.Clear();
            chatProject.Items.Add(L.T("(no project – just ask)", "(không chọn dự án – chỉ hỏi)"));
            foreach (var x in chatProjects) chatProject.Items.Add(x.Name + "  —  " + x.Path);
            chatProject.SelectedIndex = 0;
        }
        chatStatus.Text = L.T("Checking Claude…", "Đang kiểm tra Claude…");
        var r = await Task.Run(() => ClaudeEngine.Readiness());
        chatStatus.Text = r == null ? L.T("● Claude ready", "● Claude sẵn sàng") : ClaudeEngine.Installed ? L.T("● Claude not signed in", "● Claude chưa đăng nhập") : L.T("● Claude Code not installed", "● Chưa cài Claude Code");
        chatStatus.ForeColor = r == null ? Good : Bad;
        if (r != null && !shownReadiness) { shownReadiness = true; Say(r, Bad); }
    }

    string ChatDir() { int i = chatProject.SelectedIndex - 1; return i >= 0 && i < chatProjects.Count ? chatProjects[i].Path : null; }

    void Say(string text, Color c, bool bold = false)
    {
        if (chatLog.IsDisposed) return;
        chatLog.SelectionStart = chatLog.TextLength; chatLog.SelectionLength = 0;
        chatLog.SelectionColor = c; chatLog.SelectionFont = new Font("Segoe UI", bold ? 10.5f : 10.5f, bold ? FontStyle.Bold : FontStyle.Regular);
        chatLog.AppendText(text + "\n\n");
        chatLog.ScrollToCaret();
    }
    void SayAsync(string text, Color c, bool bold = false) { try { BeginInvoke(new Action(() => Say(text, c, bold))); } catch { } }

    void SetChatBusy(bool busy, bool awaitingApproval = false)
    {
        bAsk.Enabled = bTask.Enabled = !busy && !awaitingApproval;
        bApprove.Visible = bReject.Visible = awaitingApproval;
        bStop.Visible = busy;
        chatProject.Enabled = !busy && !awaitingApproval;
    }

    bool EnsureReady()
    {
        var r = ClaudeEngine.Readiness();
        if (r == null) return true;
        Say(r, Bad);
        if (ClaudeEngine.Installed) TokenDialog();
        return false;
    }

    void Ask()
    {
        var q = chatInput.Text.Trim();
        if (q.Length == 0 || chatRun != null) return;
        if (!EnsureReady()) return;
        chatInput.Clear();
        Say(L.T("You: ", "Bạn: ") + q, Ink, true);
        var dir = ChatDir();
        string prompt = q + (dir != null ? "\n\n(" + L.T("Current project: ", "Dự án đang chọn: ") + dir + ")" : "");
        RunEngine(prompt, dir, ClaudeEngine.Mode.Ask, askSession, res => { if (!res.Error) askSession = res.SessionId; });
    }

    void PlanTask()
    {
        var t = chatInput.Text.Trim();
        var dir = ChatDir();
        if (t.Length == 0) { Say(L.T("Describe the task in the box below first.", "Hãy mô tả việc cần làm ở ô bên dưới trước."), Dim); return; }
        if (dir == null) { Say(L.T("Pick the project to work in (top left) first.", "Hãy chọn dự án cần làm (góc trên bên trái) trước."), Bad); return; }
        if (chatRun != null || !EnsureReady()) return;
        chatInput.Clear();
        Say(L.T("Task for ", "Việc cho ") + Path.GetFileName(dir) + ": " + t, Ink, true);
        Say(L.T("Making a plan (nothing will be changed yet)…", "Đang lập kế hoạch (chưa thay đổi gì)…"), Dim);
        pendingTask = t; pendingDir = dir;
        RunEngine(L.T("Task: ", "Việc cần làm: ") + t + "\n\n" + L.T(
            "Investigate this project and write a short numbered plan for the task: the files you will change, how you will verify it (tests/build), and any risk. Do NOT change anything yet – the user must approve the plan first.",
            "Hãy tìm hiểu dự án này và viết một kế hoạch ngắn, đánh số cho việc trên: các file sẽ sửa, cách kiểm tra (test/build), và rủi ro nếu có. CHƯA được thay đổi gì – người dùng phải duyệt kế hoạch trước."),
            dir, ClaudeEngine.Mode.Plan, null, res =>
            {
                if (res.Error) { pendingTask = null; return; }
                planSession = res.SessionId;
                BeginInvoke(new Action(() =>
                {
                    Say(L.T("Approve this plan? Claude will then edit files in ", "Duyệt kế hoạch này? Sau đó Claude sẽ sửa file trong ") + pendingDir + L.T(" and run checks. It cannot push, release or delete.", " và chạy kiểm tra. Claude không được đẩy code, phát hành hay xoá."), Accent);
                    SetChatBusy(false, true);
                }));
            }, true);
    }

    void DoTask()
    {
        if (pendingTask == null || chatRun != null) return;
        var dir = pendingDir; var session = planSession;
        Say(L.T("Approved. Working…", "Đã duyệt. Đang làm…"), Good, true);
        pendingTask = null; planSession = null;
        RunEngine(L.T("The user approved the plan. Carry it out now in this project, verify it, then give a short report: files changed, how you verified, what is left. Do not commit or push.",
                      "Người dùng đã duyệt kế hoạch. Hãy thực hiện ngay trong dự án này, kiểm tra lại, rồi báo cáo ngắn: các file đã sửa, đã kiểm tra thế nào, còn gì chưa xong. Không commit, không đẩy code."),
            dir, ClaudeEngine.Mode.Task, session, res =>
            {
                if (!res.Error) BeginInvoke(new Action(() => Say(L.T("Done. Review the changes in ", "Xong. Hãy xem lại thay đổi trong ") + dir + L.T(" (e.g. git diff) before you commit.", " (vd. git diff) trước khi commit."), Dim)));
            });
    }

    void RunEngine(string prompt, string dir, ClaudeEngine.Mode mode, string resume, Action<ClaudeEngine.Result> after, bool keepApproval = false)
    {
        SetChatBusy(true);
        try
        {
            chatRun = ClaudeEngine.Start(prompt, dir ?? Cleaner.DataDir, mode, resume,
                ev => SayAsync(ev.Kind == "tool" ? ev.Text : "Second Brain: " + ev.Text, ev.Kind == "tool" ? Dim : Ink),
                res =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            chatRun = null;
                            if (res.Error) Say("⚠ " + res.Text, Bad);
                            else if (res.Turns > 0) Say(L.T("(", "(") + res.Turns + L.T(" steps)", " bước)"), Dim);
                            SetChatBusy(false);
                            after(res);
                        }));
                    }
                    catch { }
                });
        }
        catch (Exception ex) { chatRun = null; SetChatBusy(false); Say("⚠ " + ex.Message, Bad); }
    }

    void TokenDialog()
    {
        using (var f = new Form { Text = L.T("Claude sign-in", "Đăng nhập Claude"), ClientSize = new Size(560, 230), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = Bg, ForeColor = Ink, Font = new Font("Segoe UI", 9.75f), ShowInTaskbar = false })
        {
            Theme.TitleBar(f);
            f.Controls.Add(new Label { Text = L.T("Second Brain uses your Claude plan through Claude Code.\n1. Open a terminal and run:  claude setup-token\n2. Sign in in the browser, then paste the token below.\nIt is stored encrypted with your Windows account and never leaves this PC.",
                "Second Brain dùng gói Claude của bạn qua Claude Code.\n1. Mở cửa sổ dòng lệnh và chạy:  claude setup-token\n2. Đăng nhập trên trình duyệt rồi dán mã vào ô dưới.\nMã được mã hoá bằng tài khoản Windows của bạn và không rời khỏi máy."),
                Location = new Point(16, 14), Size = new Size(528, 92), ForeColor = Ink });
            var tb = new TextBox { Location = new Point(16, 114), Width = 528, UseSystemPasswordChar = true }; Dark(tb);
            var ok = Btn(L.T("Save", "Lưu"), 100, true); ok.Location = new Point(338, 176);
            var clear = Btn(L.T("Remove", "Xoá mã"), 100); clear.Location = new Point(16, 176); clear.Visible = ClaudeEngine.HasToken;
            var cancel = Btn(L.T("Cancel", "Huỷ"), 100); cancel.Location = new Point(444, 176); cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => { try { ClaudeEngine.SetToken(tb.Text); f.DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(f, ex.Message, Text); } };
            clear.Click += (s, e) => { ClaudeEngine.ClearToken(); f.DialogResult = DialogResult.OK; };
            f.Controls.AddRange(new Control[] { tb, ok, clear, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog(this) == DialogResult.OK) RefreshChat();
        }
    }
}

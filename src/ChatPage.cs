// Trang Chat: thanh bên Project / Chat (rê chuột hiện ⋯ và +), trò chuyện với bộ não, giao việc cho Claude Code.
// Hỏi: chỉ đọc. Giao việc: Claude lập kế hoạch (không sửa gì) → người dùng duyệt → mới bắt đầu làm.
// Mọi thao tác đều có thao tác ngược: tạo/xoá (thùng rác 30 ngày), lưu trữ/bỏ lưu trữ, ghim/bỏ ghim, phân nhánh, chuyển project.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

// ---------- Thanh bên: danh sách project và chat, tự vẽ ----------
class ChatTree : Control
{
    public class Row { public string Kind, Id; public int Indent; public string Text, Sub; public bool Expanded, Pinned, Bold; public Rectangle Bounds, Dots, Plus; }
    public readonly List<Row> Rows = new List<Row>();
    public string SelectedChat, SelectedProject;
    public bool ShowArchived;
    public event Action<Row> RowClick, DotsClick, PlusClick;
    readonly HashSet<string> collapsed = new HashSet<string>();
    Row hover; const int RowH = 34;

    public ChatTree()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI", 9.75f);
        MouseMove += (s, e) => { var r = Rows.FirstOrDefault(x => x.Bounds.Contains(e.Location)); if (r != hover) { hover = r; Invalidate(); } Cursor = r != null && r.Kind != "label" ? Cursors.Hand : Cursors.Default; };
        MouseLeave += (s, e) => { hover = null; Invalidate(); };
        MouseUp += (s, e) =>
        {
            var r = Rows.FirstOrDefault(x => x.Bounds.Contains(e.Location)); if (r == null || r.Kind == "label") return;
            if (e.Button == MouseButtons.Right || r.Dots.Contains(e.Location)) { if (DotsClick != null && r.Kind != "trash" && r.Kind != "archived" && r.Kind != "newproject") DotsClick(r); return; }
            if (r.Plus.Contains(e.Location)) { if (PlusClick != null) PlusClick(r); return; }
            if (r.Kind == "project") { if (!collapsed.Add(r.Id)) collapsed.Remove(r.Id); }
            if (RowClick != null) RowClick(r);
        };
    }

    public void Rebuild()
    {
        Rows.Clear();
        Rows.Add(new Row { Kind = "newproject", Text = L.T("＋  New project", "＋  Project mới"), Bold = true });
        Rows.Add(new Row { Kind = "label", Text = ShowArchived ? L.T("ARCHIVED", "ĐÃ LƯU TRỮ") : L.T("PROJECTS", "PROJECT") });
        foreach (var p in Workspace.LiveProjects(ShowArchived))
        {
            var chats = Workspace.LiveChats(p.Id, ShowArchived).ToList();
            if (ShowArchived && !p.Archived && chats.Count == 0) continue;
            bool exp = !collapsed.Contains(p.Id);
            Rows.Add(new Row { Kind = "project", Id = p.Id, Text = (exp ? "▾  " : "▸  ") + p.Name, Pinned = p.Pinned, Bold = true, Expanded = exp, Sub = p.Path });
            if (!exp) continue;
            foreach (var c in chats) Rows.Add(new Row { Kind = "chat", Id = c.Id, Indent = 1, Text = c.Title, Pinned = c.Pinned });
            if (chats.Count == 0 && !ShowArchived) Rows.Add(new Row { Kind = "label", Indent = 1, Text = L.T("No chats yet – click +", "Chưa có chat – bấm +") });
        }
        Rows.Add(new Row { Kind = "label", Text = "" });
        Rows.Add(new Row { Kind = "archived", Text = ShowArchived ? L.T("← Back to projects", "← Về danh sách project") : L.T("🗄  Archived", "🗄  Đã lưu trữ") });
        int trash = Workspace.TrashCount;
        Rows.Add(new Row { Kind = "trash", Text = L.T("🗑  Trash", "🗑  Thùng rác") + (trash > 0 ? "  (" + trash + ")" : "") });
        int y = 6;
        foreach (var r in Rows) { r.Bounds = new Rectangle(6, y, Width - 12, r.Kind == "label" && r.Text == "" ? 10 : RowH); y += r.Bounds.Height + 2; }
        Height = Math.Max(y + 10, Parent != null ? Parent.ClientSize.Height : 0);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Theme.Side);
        foreach (var r in Rows)
        {
            var b = r.Bounds; b.Width = Width - 12;
            bool sel = r.Kind == "chat" && r.Id == SelectedChat || r.Kind == "project" && r.Id == SelectedProject && SelectedChat == null;
            bool hot = r == hover && r.Kind != "label";
            if (sel || hot) using (var path = Round.Path(b, 8)) using (var br = new SolidBrush(sel ? Theme.Panel2 : Color.FromArgb(Theme.Panel2.A, Theme.Panel2))) { g.FillPath(br, path); if (sel) using (var pen = new Pen(Theme.Accent)) g.DrawPath(pen, path); }
            int x = b.X + 10 + r.Indent * 18;
            Color col = r.Kind == "label" ? Theme.Dim : r.Kind == "newproject" ? Theme.Accent : Theme.Ink;
            var font = r.Bold ? new Font(Font, FontStyle.Bold) : r.Kind == "label" ? new Font("Segoe UI", 8f, FontStyle.Bold) : Font;
            int right = b.Right - (hot && (r.Kind == "project" || r.Kind == "chat") ? (r.Kind == "project" ? 54 : 30) : 8);
            var tr = new Rectangle(x, b.Y, Math.Max(10, right - x - (r.Pinned ? 16 : 0)), b.Height);
            TextRenderer.DrawText(g, r.Text, font, tr, col, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            if (r.Pinned) TextRenderer.DrawText(g, "📌", new Font("Segoe UI Emoji", 7.5f), new Rectangle(tr.Right, b.Y, 18, b.Height), Theme.Dim, TextFormatFlags.VerticalCenter);
            r.Dots = r.Plus = Rectangle.Empty;
            if (hot && (r.Kind == "project" || r.Kind == "chat"))
            {
                r.Dots = new Rectangle(b.Right - 28, b.Y + 5, 24, b.Height - 10);
                TextRenderer.DrawText(g, "⋯", new Font("Segoe UI", 12f, FontStyle.Bold), r.Dots, Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (r.Kind == "project" && !ShowArchived)
                {
                    r.Plus = new Rectangle(b.Right - 52, b.Y + 5, 24, b.Height - 10);
                    TextRenderer.DrawText(g, "+", new Font("Segoe UI", 12f, FontStyle.Bold), r.Plus, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
            if (font != Font) font.Dispose();
        }
    }
}

partial class MainForm
{
    readonly RichTextBox chatLog = new RichTextBox();
    readonly TextBox chatInput = new TextBox();
    readonly Label chatStatus = new Label(), chatTitle = new Label();
    ChatTree chatTree; Panel chatTreeHost;
    Button bAsk, bTask, bApprove, bReject, bStop, bToken;
    ClaudeEngine.Run chatRun; WsChat runChat;
    WsChat curChat; string curProjectId = Workspace.GeneralId;
    bool awaitingApproval, shownReadiness;
    readonly List<int> msgOffsets = new List<int>();

    Panel BuildChatPage()
    {
        var p = new Panel { BackColor = Bg };
        // ----- Thanh bên trái -----
        chatTreeHost = new Panel { Dock = DockStyle.Left, Width = 270, BackColor = Theme.Side, AutoScroll = true };
        chatTree = new ChatTree { Location = new Point(0, 0), Width = 252 };
        chatTreeHost.Controls.Add(chatTree);
        chatTreeHost.Resize += (s, e) => { chatTree.Width = chatTreeHost.ClientSize.Width; chatTree.Rebuild(); };

        // ----- Khu trò chuyện -----
        var main = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(16, 4, 16, 14) };
        var top = Row(DockStyle.Top, 48);
        chatTitle.AutoSize = false; chatTitle.Width = 520; chatTitle.Height = 40; chatTitle.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold); chatTitle.ForeColor = Ink; chatTitle.TextAlign = ContentAlignment.MiddleLeft; chatTitle.AutoEllipsis = true;
        bToken = Btn(L.T("Claude sign-in…", "Đăng nhập Claude…"), 160);
        chatStatus.AutoSize = true; chatStatus.ForeColor = Dim; chatStatus.Margin = new Padding(6, 14, 0, 0); chatStatus.Font = new Font("Segoe UI", 9.5f);
        top.Controls.AddRange(new Control[] { chatTitle, bToken, chatStatus });

        chatLog.Dock = DockStyle.Fill; chatLog.ReadOnly = true; chatLog.BorderStyle = BorderStyle.None; chatLog.BackColor = Theme.List; chatLog.ForeColor = Ink;
        chatLog.Font = new Font("Segoe UI", 10.5f); chatLog.DetectUrls = true; chatLog.HideSelection = false;
        var logWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.List, Padding = new Padding(16, 12, 16, 12) };
        logWrap.Controls.Add(chatLog);

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
        main.Controls.Add(logWrap); main.Controls.Add(bottom); main.Controls.Add(top);
        p.Controls.Add(main); p.Controls.Add(chatTreeHost);

        // ----- Sự kiện -----
        chatTree.RowClick += r =>
        {
            if (r.Kind == "newproject") { NewProjectDialog(); return; }
            if (r.Kind == "trash") { TrashDialog(); return; }
            if (r.Kind == "archived") { chatTree.ShowArchived = !chatTree.ShowArchived; RefreshTree(); return; }
            if (r.Kind == "project") { curProjectId = r.Id; OpenChat(null); }
            if (r.Kind == "chat") OpenChat(Workspace.Chat(r.Id));
        };
        chatTree.PlusClick += r => { curProjectId = r.Id; OpenChat(Workspace.NewChat(r.Id)); chatInput.Focus(); };
        chatTree.DotsClick += r => { var m = r.Kind == "project" ? ProjectMenu(Workspace.Project(r.Id)) : ChatMenu(Workspace.Chat(r.Id)); if (m != null) m.Show(chatTree, new Point(r.Bounds.Right - 20, r.Bounds.Bottom)); };
        bAsk.Click += (s, e) => Ask();
        bTask.Click += (s, e) => PlanTask();
        bApprove.Click += (s, e) => DoTask();
        bReject.Click += (s, e) => { awaitingApproval = false; SetChatBusy(false); AddMsg(runChat ?? curChat, "note", L.T("Plan cancelled. Nothing was changed.", "Đã huỷ kế hoạch. Chưa có gì bị thay đổi.")); };
        bStop.Click += (s, e) => { if (chatRun != null) chatRun.Stop(); };
        bToken.Click += (s, e) => TokenDialog();
        chatInput.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ask(); } };
        // Bấm chuột phải vào một tin nhắn: phân nhánh từ đó
        var logMenu = new ContextMenuStrip();
        logMenu.Items.Add(L.T("Branch from this message", "Phân nhánh từ tin nhắn này"), null, (s, e) =>
        {
            if (curChat == null) return;
            int idx = MessageAt(chatLog.GetCharIndexFromPosition(chatLog.PointToClient(logMenu.Bounds.Location)));
            if (idx >= 0) { var b = Workspace.Branch(curChat.Id, idx); OpenChat(b); }
        });
        logMenu.Items.Add(L.T("Copy", "Chép"), null, (s, e) => { if (chatLog.SelectionLength > 0) chatLog.Copy(); });
        chatLog.ContextMenuStrip = logMenu;
        p.VisibleChanged += (s, e) => { if (p.Visible) { RefreshTree(); RefreshReadiness(); } };
        OpenChat(null);
        return p;
    }

    void RefreshTree() { chatTree.SelectedChat = curChat != null ? curChat.Id : null; chatTree.SelectedProject = curProjectId; chatTree.Rebuild(); }

    async void RefreshReadiness()
    {
        chatStatus.Text = L.T("Checking Claude…", "Đang kiểm tra Claude…");
        var r = await Task.Run(() => ClaudeEngine.Readiness());
        chatStatus.Text = r == null ? L.T("● Claude ready", "● Claude sẵn sàng") : ClaudeEngine.Installed ? L.T("● Claude not signed in", "● Claude chưa đăng nhập") : L.T("● Claude Code not installed", "● Chưa cài Claude Code");
        chatStatus.ForeColor = r == null ? Good : Bad;
        if (r != null && !shownReadiness) { shownReadiness = true; Say(r, Bad); }
    }

    WsProject CurProject { get { return Workspace.Project(curProjectId) ?? Workspace.Project(Workspace.GeneralId); } }

    // ---------- Hiển thị ----------
    void OpenChat(WsChat c)
    {
        curChat = c;
        if (c != null) curProjectId = c.ProjectId;
        var proj = CurProject;
        chatTitle.Text = (c != null ? c.Title : proj.Name) + (proj.Path != null ? "   ·   " + proj.Path : "");
        chatLog.Clear(); msgOffsets.Clear();
        if (c == null || c.Messages.Count == 0)
        {
            Say(c == null ? proj.Name : c.Title, Accent, true);
            Say(L.T("• Ask: questions about your projects, documents and verified memories. Read-only.\n" +
                    "• Do task: describe the work. Claude plans first and starts only after you approve. It may edit files in this project and run tests, but never pushes, commits, releases or deletes.\n" +
                    "• Hover a project or chat in the sidebar and click ⋯ to rename, branch, move, archive or delete. Right-click a message to branch from it.",
                    "• Hỏi: hỏi về dự án, tài liệu và ghi nhớ đã xác minh. Chỉ đọc.\n" +
                    "• Giao việc: mô tả việc cần làm. Claude lập kế hoạch trước, chỉ bắt đầu khi bạn duyệt. Claude được sửa file trong project này và chạy test, nhưng không bao giờ đẩy code, commit, phát hành hay xoá.\n" +
                    "• Rê chuột vào project hoặc chat ở thanh bên, bấm ⋯ để đổi tên, phân nhánh, chuyển, lưu trữ hoặc xoá. Bấm chuột phải vào tin nhắn để phân nhánh từ đó."), Dim);
            if (proj.Path == null) Say(L.T("Tip: tasks need a project with a folder. Create one with ＋ New project.", "Mẹo: giao việc cần một project có thư mục. Tạo bằng ＋ Project mới."), Dim);
        }
        else foreach (var m in c.Messages) RenderMsg(m);
        RefreshTree();
        SetChatBusy(chatRun != null, awaitingApproval && runChat == curChat);
    }

    void RenderMsg(WsMessage m)
    {
        msgOffsets.Add(chatLog.TextLength);
        if (m.Role == "user") Say(L.T("You: ", "Bạn: ") + m.Text, Ink, true);
        else if (m.Role == "tool") Say(m.Text, Dim);
        else if (m.Role == "error") Say("⚠ " + m.Text, Bad);
        else if (m.Role == "note") Say(m.Text, Accent);
        else Say("Second Brain: " + m.Text, Ink);
    }

    int MessageAt(int charIndex) { int idx = -1; for (int i = 0; i < msgOffsets.Count; i++) if (msgOffsets[i] <= charIndex) idx = i; return idx; }

    void Say(string text, Color c, bool bold = false)
    {
        if (chatLog.IsDisposed) return;
        chatLog.SelectionStart = chatLog.TextLength; chatLog.SelectionLength = 0;
        chatLog.SelectionColor = c; chatLog.SelectionFont = new Font("Segoe UI", 10.5f, bold ? FontStyle.Bold : FontStyle.Regular);
        chatLog.AppendText(text + "\n\n");
        chatLog.ScrollToCaret();
    }

    // Thêm tin nhắn vào một chat (lưu lại); chỉ vẽ nếu chat đó đang mở
    void AddMsg(WsChat c, string role, string text)
    {
        if (c == null) { Say(text, role == "error" ? Bad : Dim); return; }
        var m = new WsMessage { Role = role, Text = text, T = DateTime.Now };
        c.Messages.Add(m); c.Updated = DateTime.Now;
        if (role == "user" && (c.Title == L.T("New chat", "Chat mới") || c.Title == "New chat" || c.Title == "Chat mới")) { c.Title = text.Length > 48 ? text.Substring(0, 48) + "…" : text; RefreshTree(); }
        Workspace.Save();
        if (c == curChat) { if (c.Messages.Count == 1) { chatLog.Clear(); msgOffsets.Clear(); } RenderMsg(m); }
    }
    void AddMsgAsync(WsChat c, string role, string text) { try { BeginInvoke(new Action(() => AddMsg(c, role, text))); } catch { } }

    void SetChatBusy(bool busy, bool approval = false)
    {
        bAsk.Enabled = bTask.Enabled = !busy && !approval;
        bApprove.Visible = bReject.Visible = approval;
        bStop.Visible = busy;
    }

    bool EnsureReady()
    {
        var r = ClaudeEngine.Readiness();
        if (r == null) return true;
        Say(r, Bad);
        if (ClaudeEngine.Installed) TokenDialog();
        return false;
    }

    WsChat EnsureChat() { if (curChat == null) { curChat = Workspace.NewChat(curProjectId); RefreshTree(); } return curChat; }

    // ---------- Hỏi / Giao việc ----------
    void Ask()
    {
        var q = chatInput.Text.Trim();
        if (q.Length == 0 || chatRun != null || awaitingApproval) return;
        if (!EnsureReady()) return;
        var c = EnsureChat(); var proj = CurProject;
        chatInput.Clear();
        string history = HistoryFor(c);
        AddMsg(c, "user", q);
        string prompt = history + q + (proj.Path != null ? "\n\n(" + L.T("Current project: ", "Project đang chọn: ") + proj.Path + ")" : "");
        RunEngine(c, prompt, ClaudeEngine.Mode.Ask, null);
    }

    // Nhánh tách từ giữa cuộc trò chuyện chưa có phiên Claude: gửi kèm phần hội thoại đã chép để Claude nắm ngữ cảnh
    static string HistoryFor(WsChat c)
    {
        if (c.SessionId != null) return "";
        var prior = c.Messages.Where(m => m.Role == "user" || m.Role == "assistant").ToList();
        if (prior.Count == 0) return "";
        var sb = new System.Text.StringBuilder(L.T("Earlier conversation (for context):\n", "Hội thoại trước đó (để nắm ngữ cảnh):\n"));
        foreach (var m in prior.Skip(Math.Max(0, prior.Count - 20)))
            sb.Append(m.Role == "user" ? "User: " : "Assistant: ").Append(m.Text.Length > 1500 ? m.Text.Substring(0, 1500) + "…" : m.Text).Append("\n");
        return sb.Append("\n---\n").ToString();
    }

    void PlanTask()
    {
        var t = chatInput.Text.Trim(); var proj = CurProject;
        if (t.Length == 0) { Say(L.T("Describe the task in the box below first.", "Hãy mô tả việc cần làm ở ô bên dưới trước."), Dim); return; }
        if (proj.Path == null || !Directory.Exists(proj.Path)) { Say(L.T("Tasks run inside a project folder. Pick or create a project with a folder first.", "Việc phải làm trong thư mục của một project. Hãy chọn hoặc tạo project có thư mục trước."), Bad); return; }
        if (chatRun != null || !EnsureReady()) return;
        var c = EnsureChat();
        chatInput.Clear();
        AddMsg(c, "user", L.T("Task: ", "Việc cần làm: ") + t);
        AddMsg(c, "note", L.T("Making a plan (nothing will be changed yet)…", "Đang lập kế hoạch (chưa thay đổi gì)…"));
        RunEngine(c, L.T("Task: ", "Việc cần làm: ") + t + "\n\n" + L.T(
            "Investigate this project and write a short numbered plan for the task: the files you will change, how you will verify it (tests/build), and any risk. Do NOT change anything yet – the user must approve the plan first.",
            "Hãy tìm hiểu dự án này và viết một kế hoạch ngắn, đánh số cho việc trên: các file sẽ sửa, cách kiểm tra (test/build), và rủi ro nếu có. CHƯA được thay đổi gì – người dùng phải duyệt kế hoạch trước."),
            ClaudeEngine.Mode.Plan, res =>
            {
                if (res.Error) return;
                awaitingApproval = true;
                AddMsg(c, "note", L.T("Approve this plan? Claude will then edit files in ", "Duyệt kế hoạch này? Sau đó Claude sẽ sửa file trong ") + proj.Path + L.T(" and run checks. It cannot push, commit, release or delete.", " và chạy kiểm tra. Claude không được đẩy code, commit, phát hành hay xoá."));
                SetChatBusy(false, true);
            });
    }

    void DoTask()
    {
        var c = runChat ?? curChat; if (!awaitingApproval || c == null || chatRun != null) return;
        awaitingApproval = false;
        var proj = Workspace.Project(c.ProjectId);
        AddMsg(c, "note", L.T("Approved. Working…", "Đã duyệt. Đang làm…"));
        RunEngine(c, L.T("The user approved the plan. Carry it out now in this project, verify it, then give a short report: files changed, how you verified, what is left. Do not commit or push.",
                         "Người dùng đã duyệt kế hoạch. Hãy thực hiện ngay trong dự án này, kiểm tra lại, rồi báo cáo ngắn: các file đã sửa, đã kiểm tra thế nào, còn gì chưa xong. Không commit, không đẩy code."),
            ClaudeEngine.Mode.Task, res => { if (!res.Error) AddMsg(c, "note", L.T("Done. Review the changes in ", "Xong. Hãy xem lại thay đổi trong ") + proj.Path + L.T(" (e.g. git diff) before you commit.", " (vd. git diff) trước khi commit.")); });
    }

    void RunEngine(WsChat c, string prompt, ClaudeEngine.Mode mode, Action<ClaudeEngine.Result> after)
    {
        var proj = Workspace.Project(c.ProjectId) ?? CurProject;
        SetChatBusy(true); runChat = c;
        try
        {
            bool fork = c.ForkNext && c.SessionId != null;
            chatRun = ClaudeEngine.Start(prompt, proj.Path ?? Cleaner.DataDir, mode, c.SessionId,
                ev => AddMsgAsync(c, ev.Kind == "tool" ? "tool" : "assistant", ev.Text),
                res =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            chatRun = null;
                            if (res.SessionId != null) { c.SessionId = res.SessionId; c.ForkNext = false; Workspace.Save(); }
                            if (res.Error) AddMsg(c, "error", res.Text);
                            SetChatBusy(false);
                            if (after != null) after(res);
                        }));
                    }
                    catch { }
                }, fork, proj.Instructions);
        }
        catch (Exception ex) { chatRun = null; SetChatBusy(false); AddMsg(c, "error", ex.Message); }
    }

    // ---------- Menu ⋯ ----------
    ContextMenuStrip Menu() { return new ContextMenuStrip { Font = new Font("Segoe UI", 9.75f), ShowImageMargin = false }; }

    ContextMenuStrip ProjectMenu(WsProject p)
    {
        if (p == null) return null;
        var m = Menu(); bool general = p.Id == Workspace.GeneralId;
        m.Items.Add(L.T("New chat", "Chat mới"), null, (s, e) => { curProjectId = p.Id; OpenChat(Workspace.NewChat(p.Id)); });
        if (!general) m.Items.Add(L.T("Rename…", "Đổi tên…"), null, (s, e) => { var n = InputBox(L.T("Rename project", "Đổi tên project"), p.Name, false); if (n != null && n.Trim().Length > 0) { p.Name = n.Trim(); Workspace.Save(); OpenChat(curChat); } });
        m.Items.Add(L.T("Project instructions…", "Hướng dẫn riêng…"), null, (s, e) => { var n = InputBox(L.T("Instructions Claude follows in this project", "Hướng dẫn Claude tuân theo trong project này"), p.Instructions ?? "", true); if (n != null) { p.Instructions = n; Workspace.Save(); } });
        if (p.Path != null) m.Items.Add(L.T("Open folder", "Mở thư mục"), null, (s, e) => { try { Process.Start("explorer.exe", "\"" + p.Path + "\""); } catch { } });
        if (!general)
        {
            m.Items.Add(p.Pinned ? L.T("Unpin", "Bỏ ghim") : L.T("Pin", "Ghim"), null, (s, e) => { p.Pinned = !p.Pinned; Workspace.Save(); RefreshTree(); });
            m.Items.Add(p.Archived ? L.T("Unarchive", "Bỏ lưu trữ") : L.T("Archive", "Lưu trữ"), null, (s, e) => { p.Archived = !p.Archived; Workspace.Save(); if (curProjectId == p.Id) { curProjectId = Workspace.GeneralId; OpenChat(null); } else RefreshTree(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(L.T("Remove from Second Brain (keeps the folder)", "Gỡ khỏi Second Brain (giữ thư mục)"), null, (s, e) =>
            {
                if (!Confirm(L.T("Move project \"", "Chuyển project \"") + p.Name + L.T("\" and its chats to Trash? You can restore it for 30 days. The folder stays untouched.", "\" và các chat của nó vào thùng rác? Khôi phục được trong 30 ngày. Thư mục giữ nguyên."))) return;
                Workspace.DeleteProject(p.Id, false); if (curProjectId == p.Id) { curProjectId = Workspace.GeneralId; curChat = null; } OpenChat(curChat);
            });
            if (p.CreatedHere)
            {
                var del = m.Items.Add(L.T("Delete project and folder…", "Xoá project và thư mục…"), null, (s, e) =>
                {
                    if (!Confirm(L.T("Delete \"", "Xoá \"") + p.Name + L.T("\" and move its folder to the Windows Recycle Bin?\n", "\" và chuyển thư mục vào Thùng rác của Windows?\n") + p.Path)) return;
                    try { Workspace.DeleteProject(p.Id, true); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); return; }
                    if (curProjectId == p.Id) { curProjectId = Workspace.GeneralId; curChat = null; } OpenChat(curChat);
                });
                del.ForeColor = Bad;
            }
        }
        return m;
    }

    ContextMenuStrip ChatMenu(WsChat c)
    {
        if (c == null) return null;
        var m = Menu();
        m.Items.Add(L.T("Rename…", "Đổi tên…"), null, (s, e) => { var n = InputBox(L.T("Rename chat", "Đổi tên chat"), c.Title, false); if (n != null && n.Trim().Length > 0) { c.Title = n.Trim(); Workspace.Save(); OpenChat(curChat); } });
        m.Items.Add(c.Pinned ? L.T("Unpin", "Bỏ ghim") : L.T("Pin", "Ghim"), null, (s, e) => { c.Pinned = !c.Pinned; Workspace.Save(); RefreshTree(); });
        m.Items.Add(L.T("Branch (copy and continue separately)", "Phân nhánh (bản sao, đi tiếp riêng)"), null, (s, e) => OpenChat(Workspace.Branch(c.Id)));
        var move = new ToolStripMenuItem(L.T("Move to project", "Chuyển sang project"));
        foreach (var p in Workspace.LiveProjects(false).Where(x => x.Id != c.ProjectId))
        {
            var pp = p;
            move.DropDownItems.Add(p.Name, null, (s, e) => { c.ProjectId = pp.Id; c.SessionId = c.SessionId; Workspace.Save(); if (curChat == c) curProjectId = pp.Id; OpenChat(curChat); });
        }
        if (move.DropDownItems.Count > 0) m.Items.Add(move);
        m.Items.Add(L.T("Export as Markdown…", "Xuất ra Markdown…"), null, (s, e) =>
        {
            using (var d = new SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = string.Join("_", c.Title.Split(Path.GetInvalidFileNameChars())) + ".md" })
                if (d.ShowDialog(this) == DialogResult.OK) File.WriteAllText(d.FileName, Workspace.ExportMarkdown(c), new System.Text.UTF8Encoding(false));
        });
        m.Items.Add(c.Archived ? L.T("Unarchive", "Bỏ lưu trữ") : L.T("Archive", "Lưu trữ"), null, (s, e) => { c.Archived = !c.Archived; Workspace.Save(); if (curChat == c) curChat = null; OpenChat(curChat); });
        m.Items.Add(new ToolStripSeparator());
        var del = m.Items.Add(L.T("Delete (to Trash, 30 days)", "Xoá (vào thùng rác 30 ngày)"), null, (s, e) => { c.Deleted = DateTime.Now; Workspace.Save(); if (curChat == c) curChat = null; OpenChat(curChat); });
        del.ForeColor = Bad;
        return m;
    }

    bool Confirm(string text) { return MessageBox.Show(this, text, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes; }

    // ---------- Hộp thoại ----------
    Form SmallDialog(string title, int w, int h)
    {
        var f = new Form { Text = title, ClientSize = new Size(w, h), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = Bg, ForeColor = Ink, Font = new Font("Segoe UI", 9.75f), ShowInTaskbar = false };
        Theme.TitleBar(f);
        return f;
    }

    string InputBox(string title, string value, bool multiline)
    {
        using (var f = SmallDialog(title, 520, multiline ? 300 : 130))
        {
            var tb = new TextBox { Text = value, Location = new Point(16, 16), Width = 488, Multiline = multiline, Height = multiline ? 220 : 26, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None }; Dark(tb);
            var ok = Btn(L.T("Save", "Lưu"), 100, true); ok.Location = new Point(298, f.ClientSize.Height - 48); ok.DialogResult = DialogResult.OK;
            var cancel = Btn(L.T("Cancel", "Huỷ"), 100); cancel.Location = new Point(404, f.ClientSize.Height - 48); cancel.DialogResult = DialogResult.Cancel;
            f.Controls.AddRange(new Control[] { tb, ok, cancel });
            if (!multiline) f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
        }
    }

    void NewProjectDialog()
    {
        using (var f = SmallDialog(L.T("New project", "Project mới"), 600, 470))
        {
            Func<string, int, Label> lbl = (t, y) => new Label { Text = t, Location = new Point(16, y), AutoSize = true, ForeColor = Dim };
            var name = new TextBox { Location = new Point(16, 36), Width = 568 }; Dark(name);
            var rbNew = new RadioButton { Text = L.T("Create a new folder in:", "Tạo thư mục mới trong:"), Location = new Point(16, 76), AutoSize = true, Checked = true, ForeColor = Ink };
            var root = new TextBox { Location = new Point(36, 102), Width = 448, Text = Workspace.DefaultRoot }; Dark(root);
            var bRoot = Btn(L.T("Browse…", "Chọn…"), 90); bRoot.Location = new Point(494, 99);
            var git = new CheckBox { Text = L.T("Start a git repository (recommended for code)", "Khởi tạo git (nên dùng cho code)"), Location = new Point(36, 134), AutoSize = true, Checked = true, ForeColor = Ink };
            var rbOld = new RadioButton { Text = L.T("Use an existing folder (e.g. a project on this PC):", "Dùng thư mục có sẵn (vd. một dự án trên máy):"), Location = new Point(16, 172), AutoSize = true, ForeColor = Ink };
            var existing = new ComboBox { Location = new Point(36, 198), Width = 448, DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, BackColor = Panel2, ForeColor = Ink, Enabled = false };
            try { foreach (var bp in Brain.Projects(null).Where(x => Directory.Exists(x.Path)).Take(80)) existing.Items.Add(bp.Path); } catch { }
            var bOld = Btn(L.T("Browse…", "Chọn…"), 90); bOld.Location = new Point(494, 195); bOld.Enabled = false;
            var ins = new TextBox { Location = new Point(16, 262), Width = 568, Height = 120, Multiline = true, ScrollBars = ScrollBars.Vertical }; Dark(ins);
            var msg = new Label { Location = new Point(16, 392), Size = new Size(568, 22), ForeColor = Bad };
            var ok = Btn(L.T("Create", "Tạo"), 110, true); ok.Location = new Point(368, 420);
            var cancel = Btn(L.T("Cancel", "Huỷ"), 100); cancel.Location = new Point(484, 420); cancel.DialogResult = DialogResult.Cancel;
            rbOld.CheckedChanged += (s, e) => { existing.Enabled = bOld.Enabled = rbOld.Checked; root.Enabled = bRoot.Enabled = git.Enabled = !rbOld.Checked; };
            existing.SelectedIndexChanged += (s, e) => { if (name.Text.Trim().Length == 0) name.Text = Path.GetFileName(existing.Text.TrimEnd('\\')); };
            Action<TextBox, ComboBox> browse = (tb, cb) => { using (var d = new FolderBrowserDialog()) if (d.ShowDialog(f) == DialogResult.OK) { if (tb != null) tb.Text = d.SelectedPath; else { cb.Text = d.SelectedPath; if (name.Text.Trim().Length == 0) name.Text = Path.GetFileName(d.SelectedPath); } } };
            bRoot.Click += (s, e) => browse(root, null); bOld.Click += (s, e) => browse(null, existing);
            WsProject made = null;
            ok.Click += (s, e) =>
            {
                try { made = Workspace.CreateProject(name.Text, rbNew.Checked ? root.Text : existing.Text, rbNew.Checked, git.Checked, ins.Text); f.DialogResult = DialogResult.OK; }
                catch (Exception ex) { msg.Text = ex.Message; }
            };
            f.Controls.AddRange(new Control[] { lbl(L.T("Name", "Tên project"), 14), name, rbNew, root, bRoot, git, rbOld, existing, bOld,
                lbl(L.T("Instructions for Claude in this project (optional)", "Hướng dẫn cho Claude trong project này (không bắt buộc)"), 240), ins, msg, ok, cancel });
            f.CancelButton = cancel;
            if (f.ShowDialog(this) == DialogResult.OK && made != null) { curProjectId = made.Id; OpenChat(Workspace.NewChat(made.Id)); chatInput.Focus(); }
        }
    }

    void TrashDialog()
    {
        using (var f = SmallDialog(L.T("Trash (kept 30 days)", "Thùng rác (giữ 30 ngày)"), 620, 400))
        {
            var lv = new ListView { Location = new Point(16, 16), Size = new Size(588, 300) }; Dark(lv);
            lv.Columns.Add(L.T("Item", "Mục"), 330); lv.Columns.Add(L.T("Kind", "Loại"), 90); lv.Columns.Add(L.T("Deleted", "Đã xoá"), 150);
            Action fill = () =>
            {
                lv.Items.Clear();
                foreach (var p in Workspace.Data.Projects.Where(x => x.Deleted != null)) lv.Items.Add(new ListViewItem(new[] { p.Name, "Project", p.Deleted.Value.ToString("dd/MM HH:mm") }) { Tag = p.Id });
                foreach (var c in Workspace.Data.Chats.Where(x => x.Deleted != null)) lv.Items.Add(new ListViewItem(new[] { c.Title, "Chat", c.Deleted.Value.ToString("dd/MM HH:mm") }) { Tag = c.Id });
                if (lv.Items.Count == 0) lv.Items.Add(new ListViewItem(new[] { L.T("Trash is empty.", "Thùng rác trống."), "", "" }));
            };
            fill();
            var restore = Btn(L.T("Restore", "Khôi phục"), 120, true); restore.Location = new Point(16, 336);
            var purge = Btn(L.T("Delete forever", "Xoá vĩnh viễn"), 140); purge.Location = new Point(146, 336); purge.ForeColor = Bad;
            var close = Btn(L.T("Close", "Đóng"), 100); close.Location = new Point(504, 336); close.DialogResult = DialogResult.Cancel;
            restore.Click += (s, e) => { foreach (ListViewItem it in lv.SelectedItems) if (it.Tag != null) Workspace.Restore((string)it.Tag); fill(); };
            purge.Click += (s, e) =>
            {
                var ids = lv.SelectedItems.Cast<ListViewItem>().Where(i => i.Tag != null).Select(i => (string)i.Tag).ToList();
                if (ids.Count == 0 || !Confirm(L.T("Permanently delete ", "Xoá vĩnh viễn ") + ids.Count + L.T(" item(s)? This cannot be undone. Project folders are not touched.", " mục? Không thể hoàn tác. Thư mục project không bị đụng tới."))) return;
                foreach (var id in ids) Workspace.Purge(id); fill();
            };
            f.Controls.AddRange(new Control[] { lv, restore, purge, close });
            f.CancelButton = close;
            f.ShowDialog(this);
            OpenChat(curChat);
        }
    }

    void TokenDialog()
    {
        using (var f = SmallDialog(L.T("Claude sign-in", "Đăng nhập Claude"), 560, 230))
        {
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
            if (f.ShowDialog(this) == DialogResult.OK) { shownReadiness = false; RefreshReadiness(); }
        }
    }
}

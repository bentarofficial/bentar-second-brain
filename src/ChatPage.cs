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
        Rows.Add(new Row { Kind = "newproject", Text = L.T("+  New project", "+  Project mới"), Bold = true });
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

// Thanh kéo mức effort: Tự động (mặc định của model) · low · medium · high · xhigh · max
class EffortSlider : Control
{
    static readonly string[] Stops = { null, "low", "medium", "high", "xhigh", "max" };
    int idx; bool drag;
    public string DefaultLevel;                 // mức mặc định của model đang chọn (để hiện ở nấc Tự động)
    public event Action<string> Changed;
    public string Level { get { return Stops[idx]; } set { idx = Math.Max(0, Array.IndexOf(Stops, string.IsNullOrEmpty(value) ? null : value)); Invalidate(); } }
    readonly ToolTip tip = new ToolTip();
    public EffortSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        tip.SetToolTip(this, L.T("How hard Claude thinks. Higher = smarter on hard tasks, slower and uses more of your plan. Auto = the model's default.",
            "Claude suy nghĩ kỹ tới đâu. Cao hơn = giỏi hơn với việc khó, nhưng chậm hơn và tốn hạn mức hơn. Tự động = mặc định của model."));
    }
    const int LabelW = 104;
    RectangleF Track { get { return new RectangleF(LabelW + 8, Height / 2f - 2, Width - LabelW - 20, 4); } }
    float X(int i) { var t = Track; return t.X + t.Width * i / (Stops.Length - 1); }
    void SetFrom(int x)
    {
        var t = Track; int i = (int)Math.Round((x - t.X) / t.Width * (Stops.Length - 1));
        i = Math.Max(0, Math.Min(Stops.Length - 1, i));
        if (i != idx) { idx = i; Invalidate(); if (Changed != null) Changed(Stops[idx]); }
    }
    protected override void OnMouseDown(MouseEventArgs e) { drag = true; SetFrom(e.X); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFrom(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { SetFrom((int)X(Math.Max(0, Math.Min(Stops.Length - 1, idx + (e.Delta > 0 ? 1 : -1))))); base.OnMouseWheel(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        string name = idx == 0 ? L.T("Auto", "Tự động") + (DefaultLevel != null ? " (" + DefaultLevel + ")" : "") : Stops[idx];
        TextRenderer.DrawText(g, L.T("Effort: ", "Effort: ") + name, new Font("Segoe UI", 8.5f, FontStyle.Bold), new Rectangle(4, 0, LabelW + 4, Height), idx >= 4 ? Theme.Accent : Theme.Dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        var t = Track;
        using (var b = new SolidBrush(Theme.Border)) g.FillRectangle(b, t);
        // Đoạn đã chọn: chuyển màu từ xanh lá (nhẹ) sang cyan/tím (tối đa)
        if (idx > 0)
        {
            var filled = new RectangleF(t.X, t.Y, X(idx) - t.X, t.Height);
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(t.X - 1, t.Y, t.Width + 2, t.Height), Theme.Good, Theme.Violet, 0f)) g.FillRectangle(b, filled);
        }
        for (int i = 0; i < Stops.Length; i++)
        {
            float x = X(i), r = i == idx ? 7 : 3;
            using (var b = new SolidBrush(i <= idx ? (i == idx ? Color.White : Theme.Accent) : Theme.Border)) g.FillEllipse(b, x - r, Height / 2f - r, r * 2, r * 2);
            if (i == idx) using (var p = new Pen(Theme.Accent, 2)) g.DrawEllipse(p, x - r, Height / 2f - r, r * 2, r * 2);
        }
    }
}

partial class MainForm
{
    readonly RichTextBox chatLog = new RichTextBox();
    readonly TextBox chatInput = new TextBox();
    readonly Label chatStatus = new Label(), chatTitle = new Label();
    ChatTree chatTree; Panel chatTreeHost;
    Button bAsk, bTask, bApprove, bReject, bStop, bToken, bTasks;
    WsChat curChat; string curProjectId = Workspace.GeneralId;
    bool shownReadiness;
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
        chatTitle.AutoSize = false; chatTitle.Width = 340; chatTitle.Height = 40; chatTitle.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold); chatTitle.ForeColor = Ink; chatTitle.TextAlign = ContentAlignment.MiddleLeft; chatTitle.AutoEllipsis = true;
        bTasks = Btn(L.T("Tasks", "Việc"), 120);
        bToken = Btn(L.T("Claude sign-in…", "Đăng nhập Claude…"), 160);
        chatStatus.AutoSize = true; chatStatus.ForeColor = Dim; chatStatus.Margin = new Padding(6, 14, 0, 0); chatStatus.Font = new Font("Segoe UI", 9.5f);
        top.Controls.AddRange(new Control[] { chatTitle, bTasks, bToken, chatStatus });

        chatLog.Dock = DockStyle.Fill; chatLog.ReadOnly = true; chatLog.BorderStyle = BorderStyle.None; chatLog.BackColor = Theme.List; chatLog.ForeColor = Ink;
        chatLog.Font = new Font("Segoe UI", 10.5f); chatLog.DetectUrls = true; chatLog.HideSelection = false;
        var logWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.List, Padding = new Padding(16, 12, 16, 12) };
        logWrap.Controls.Add(chatLog);

        // Ô soạn tin kiểu Claude Code: một thẻ bo góc, chữ ở trên, thanh nhỏ bên dưới (chế độ · gợi ý · nút gửi tròn)
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 112, BackColor = Bg, Padding = new Padding(0, 10, 0, 0) };
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(16, 12, 10, 6) };
        Round.Card(card, Panel2, 14);
        chatInput.Multiline = true; chatInput.Dock = DockStyle.Fill; chatInput.ScrollBars = ScrollBars.None; chatInput.Font = new Font("Segoe UI", 10.5f); Dark(chatInput);
        chatInput.BorderStyle = BorderStyle.None; chatInput.BackColor = Panel2;
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Panel2 };
        bTask = Btn(L.T("✧ Ask", "✧ Hỏi"), 118); bTask.Dock = DockStyle.Left; bTask.BackColor = Panel2;
        var hint = new Label { Dock = DockStyle.Fill, ForeColor = Dim, Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0),
            Text = L.T("Enter to send · Shift+Enter new line", "Enter để gửi · Shift+Enter xuống dòng") };
        bAsk = Btn("↑", 36, true); bAsk.Dock = DockStyle.Right; bAsk.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        bStop = Btn("■", 36); bStop.Dock = DockStyle.Right; bStop.Visible = false;
        Round.Button(bAsk, 17); Round.Button(bStop, 17);
        // Chọn model (menu) + thanh kéo effort, nhớ lựa chọn cho các lần sau
        var bModel = Btn("", 150); bModel.Dock = DockStyle.Left; bModel.BackColor = Panel2;
        var effort = new EffortSlider { Dock = DockStyle.Left, Width = 250, BackColor = Panel2 };
        Action syncModel = () =>
        {
            var m = ClaudeEngine.ModelPref();
            var item = ClaudeEngine.Models.FirstOrDefault(x => x.Item1 == m) ?? ClaudeEngine.Models[0];
            bModel.Text = "◆ " + (L.Vi ? item.Item2 : item.Item3).Split('—')[0].Trim() + "  ▾";
            effort.DefaultLevel = ClaudeEngine.DefaultEffort(m);
            effort.Invalidate();
        };
        bModel.Click += (s, e) =>
        {
            var menu = new ContextMenuStrip { Renderer = new DarkMenu(), ShowImageMargin = false, Font = new Font("Segoe UI", 10f) };
            var cur = ClaudeEngine.ModelPref();
            foreach (var m in ClaudeEngine.Models)
            {
                var id = m.Item1;
                menu.Items.Add(new ToolStripMenuItem((cur == id ? "✓  " : "     ") + (L.Vi ? m.Item2 : m.Item3), null, (s2, e2) => { L.Set("chat.model", id); syncModel(); }) { ForeColor = Ink });
            }
            menu.Show(bModel, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
        };
        effort.Level = L.Get("chat.effort", "");
        effort.Changed += lv => L.Set("chat.effort", lv ?? "");
        syncModel();
        bar.Controls.AddRange(new Control[] { hint, effort, bModel, bTask, bStop, bAsk });
        card.Controls.Add(chatInput); card.Controls.Add(bar);
        // Thanh duyệt kế hoạch: chỉ hiện khi có kế hoạch đang chờ
        var approveBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, BackColor = Bg, Visible = false, Padding = new Padding(0, 6, 0, 0) };
        bApprove = Btn(L.T("✓ Approve plan", "✓ Duyệt kế hoạch"), 160, true);
        bReject = Btn(L.T("Cancel plan", "Huỷ kế hoạch"), 130);
        bApprove.Margin = bReject.Margin = new Padding(0, 0, 8, 0);
        approveBar.Controls.AddRange(new Control[] { bApprove, bReject });
        bottom.Controls.Add(card);
        // Ô nhập tự cao lên theo số dòng (tối đa ~10 dòng), giống Claude Code
        Action fit = () =>
        {
            int lines = Math.Max(1, chatInput.GetLineFromCharIndex(Math.Max(0, chatInput.TextLength)) + 1);
            int lh = chatInput.Font.Height;
            bottom.Height = Math.Min(10, Math.Max(2, lines)) * lh + 10 + 18 + 36 + 6;
            chatInput.ScrollBars = lines > 10 ? ScrollBars.Vertical : ScrollBars.None;
        };
        chatInput.TextChanged += (s, e) => fit();
        chatInput.Resize += (s, e) => fit();
        main.Controls.Add(logWrap); main.Controls.Add(approveBar); main.Controls.Add(bottom); main.Controls.Add(top);
        var treeGrip = Sizer.Grip(chatTreeHost, "chattree", 270, 180, 480);
        p.Controls.Add(main); p.Controls.Add(treeGrip); p.Controls.Add(chatTreeHost);

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
        bAsk.Click += (s, e) => Send();
        bTask.Click += (s, e) => { taskMode = !taskMode; bTask.Text = taskMode ? L.T("⚒ Do task", "⚒ Giao việc") : L.T("✧ Ask", "✧ Hỏi"); bTask.ForeColor = taskMode ? Theme.Accent : Ink; chatInput.Focus(); };
        bApprove.Click += (s, e) => DoTask();
        bReject.Click += (s, e) => { if (curChat == null) return; approvals.Remove(curChat.Id); UpdateButtons(); AddMsg(curChat, "note", L.T("Plan cancelled. Nothing was changed.", "Đã huỷ kế hoạch. Chưa có gì bị thay đổi.")); };
        bStop.Click += (s, e) => { ClaudeEngine.Run r; if (curChat != null && runs.TryGetValue(curChat.Id, out r)) r.Stop(); };
        bTasks.Click += (s, e) => TasksDialog();
        bToken.Click += (s, e) => TokenDialog();
        chatInput.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); } };
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
            if (proj.Path == null) Say(L.T("Tip: tasks need a project with a folder. Create one with + New project.", "Mẹo: giao việc cần một project có thư mục. Tạo bằng + Project mới."), Dim);
        }
        else foreach (var m in c.Messages) RenderMsg(m);
        RefreshTree();
        UpdateButtons();
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


    bool EnsureReady()
    {
        var r = ClaudeEngine.Readiness();
        if (r == null) return true;
        Say(r, Bad);
        if (ClaudeEngine.Installed) TokenDialog();
        return false;
    }

    WsChat EnsureChat() { if (curChat == null) { curChat = Workspace.NewChat(curProjectId); RefreshTree(); } return curChat; }

    // ---------- Hỏi / Giao việc (mỗi chat một lượt chạy riêng → nhiều việc chạy song song) ----------
    readonly Dictionary<string, ClaudeEngine.Run> runs = new Dictionary<string, ClaudeEngine.Run>();
    readonly HashSet<string> approvals = new HashSet<string>();
    bool CurBusy { get { return curChat != null && runs.ContainsKey(curChat.Id); } }
    bool CurAwaiting { get { return curChat != null && approvals.Contains(curChat.Id); } }

    void UpdateButtons()
    {
        bool busy = CurBusy, appr = CurAwaiting;
        bAsk.Enabled = bTask.Enabled = !busy && !appr;
        if (bApprove.Parent != null) bApprove.Parent.Visible = appr && !busy;   // thanh duyệt kế hoạch
        bStop.Visible = busy; bAsk.Visible = !busy;
        int open = Workspace.Data.Tasks.Count(t => t.ProjectId == curProjectId && t.Open), running = runs.Count;
        bTasks.Text = L.T("Tasks", "Việc") + (open > 0 ? " (" + open + ")" : "") + (running > 0 ? "  ●" + running : "");
    }
    void SetChatBusy(bool busy, bool approval = false) { UpdateButtons(); }

    bool taskMode;
    void Send() { if (!bAsk.Enabled) return; if (taskMode) PlanTask(); else Ask(); }

    void Ask()
    {
        var q = chatInput.Text.Trim();
        if (q.Length == 0 || CurBusy || CurAwaiting) return;
        if (!EnsureReady()) return;
        var c = EnsureChat(); var proj = CurProject;
        chatInput.Clear();
        string history = HistoryFor(c);
        AddMsg(c, "user", q);
        string prompt = history + q + (proj.Path != null ? "\n\n(" + L.T("Current project: ", "Project đang chọn: ") + proj.Path + ")" : "");
        RunEngine(c, prompt, ClaudeEngine.Mode.Ask, null, null);
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

    readonly Dictionary<string, string> taskTitles = new Dictionary<string, string>();

    void PlanTask()
    {
        var t = chatInput.Text.Trim(); var proj = CurProject;
        if (t.Length == 0) { Say(L.T("Describe the task in the box below first.", "Hãy mô tả việc cần làm ở ô bên dưới trước."), Dim); return; }
        if (proj.Path == null || !Directory.Exists(proj.Path)) { Say(L.T("Tasks run inside a project folder. Pick or create a project with a folder first.", "Việc phải làm trong thư mục của một project. Hãy chọn hoặc tạo project có thư mục trước."), Bad); return; }
        if (CurBusy || !EnsureReady()) return;
        var c = EnsureChat();
        chatInput.Clear();
        taskTitles[c.Id] = t;
        AddMsg(c, "user", L.T("Task: ", "Việc cần làm: ") + t);
        AddMsg(c, "note", L.T("Making a plan (nothing will be changed yet)…", "Đang lập kế hoạch (chưa thay đổi gì)…"));
        RunEngine(c, HistoryFor(c) + L.T("Task: ", "Việc cần làm: ") + t + "\n\n" + L.T(
            "Investigate this project and write a short numbered plan for the task: the files you will change, how you will verify it (tests/build), and any risk. Do NOT change anything yet – the user must approve the plan first.",
            "Hãy tìm hiểu dự án này và viết một kế hoạch ngắn, đánh số cho việc trên: các file sẽ sửa, cách kiểm tra (test/build), và rủi ro nếu có. CHƯA được thay đổi gì – người dùng phải duyệt kế hoạch trước."),
            ClaudeEngine.Mode.Plan, null, res =>
            {
                if (res.Error) return;
                approvals.Add(c.Id);
                bool git = TaskBranches.IsRepo(proj.Path);
                AddMsg(c, "note", git
                    ? L.T("Approve this plan? Claude will work on a separate branch, so your current code stays untouched. You review the changes and choose to merge or discard.", "Duyệt kế hoạch này? Claude sẽ làm trên một nhánh riêng nên code hiện tại của bạn không bị đụng tới. Xong việc bạn xem thay đổi rồi chọn gộp hoặc bỏ.")
                    : L.T("Approve this plan? This folder is not a git repository, so Claude will edit files directly in ", "Duyệt kế hoạch này? Thư mục này chưa phải repo git nên Claude sẽ sửa thẳng trong ") + proj.Path + L.T(" (no undo). Tip: create the project with git to work on safe branches.", " (không hoàn tác được). Mẹo: tạo project có git để làm trên nhánh an toàn."));
                UpdateButtons();
            });
    }

    void DoTask()
    {
        var c = curChat; if (c == null || !approvals.Contains(c.Id) || CurBusy) return;
        approvals.Remove(c.Id);
        var proj = Workspace.Project(c.ProjectId);
        string title; if (!taskTitles.TryGetValue(c.Id, out title)) title = c.Title;
        WsTask task = null;
        if (TaskBranches.IsRepo(proj.Path))
        {
            try { task = TaskBranches.Start(proj, c.Id, title); }
            catch (Exception ex) { AddMsg(c, "error", ex.Message); UpdateButtons(); return; }
            AddMsg(c, "note", L.T("Approved. Working on branch ", "Đã duyệt. Đang làm trên nhánh ") + task.Branch + L.T(" – your current code is untouched.", " – code hiện tại của bạn không bị đụng tới."));
        }
        else AddMsg(c, "note", L.T("Approved. Working directly in the folder…", "Đã duyệt. Đang sửa trực tiếp trong thư mục…"));
        RunEngine(c, L.T("The user approved the plan. Carry it out now in this folder, verify it, then give a short report: files changed, how you verified, what is left. Do not commit or push.",
                         "Người dùng đã duyệt kế hoạch. Hãy thực hiện ngay trong thư mục này, kiểm tra lại, rồi báo cáo ngắn: các file đã sửa, đã kiểm tra thế nào, còn gì chưa xong. Không commit, không đẩy code."),
            ClaudeEngine.Mode.Task, task != null ? task.Worktree : null, res =>
            {
                if (task != null)
                {
                    TaskBranches.Finish(task, res.Error ? (res.Text == L.T("Stopped.", "Đã dừng.") ? "stopped" : "failed") : "done");
                    AddMsg(c, "note", task.Status == "nochange" ? L.T("Finished – no files were changed.", "Xong – không có file nào thay đổi.")
                        : task.Files + L.T(" file(s) changed on branch ", " file thay đổi trên nhánh ") + task.Branch + L.T(". Open Tasks to review the diff, then Merge or Discard.", ". Mở mục Việc để xem thay đổi, rồi chọn Gộp hoặc Bỏ."));
                }
                else if (!res.Error) AddMsg(c, "note", L.T("Done. Review the changes in ", "Xong. Hãy xem lại thay đổi trong ") + proj.Path + ".");
                UpdateButtons();
            });
    }

    void RunEngine(WsChat c, string prompt, ClaudeEngine.Mode mode, string cwd, Action<ClaudeEngine.Result> after)
    {
        var proj = Workspace.Project(c.ProjectId) ?? CurProject;
        try
        {
            bool fork = c.ForkNext && c.SessionId != null;
            runs[c.Id] = ClaudeEngine.Start(prompt, cwd ?? proj.Path ?? Cleaner.DataDir, mode, c.SessionId,
                ev => AddMsgAsync(c, ev.Kind == "tool" ? "tool" : "assistant", ev.Text),
                res =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            runs.Remove(c.Id);
                            if (res.SessionId != null) { c.SessionId = res.SessionId; c.ForkNext = false; Workspace.Save(); }
                            if (res.Error) AddMsg(c, "error", res.Text);
                            if (after != null) after(res);
                            UpdateButtons(); RefreshTree();
                        }));
                    }
                    catch { }
                }, fork, proj.Instructions);
        }
        catch (Exception ex) { runs.Remove(c.Id); AddMsg(c, "error", ex.Message); }
        UpdateButtons(); RefreshTree();
    }

    // ---------- Bảng Việc: xem thay đổi, gộp, bỏ ----------
    void TasksDialog()
    {
        var proj = CurProject;
        using (var f = SmallDialog(L.T("Tasks – ", "Việc – ") + proj.Name, 820, 470))
        {
            var lv = new ListView { Location = new Point(16, 16), Size = new Size(788, 360) }; Dark(lv);
            lv.Columns.Add(L.T("Task", "Việc"), 300); lv.Columns.Add(L.T("Status", "Trạng thái"), 130); lv.Columns.Add(L.T("Files", "File"), 50); lv.Columns.Add(L.T("Branch", "Nhánh"), 190); lv.Columns.Add(L.T("Started", "Bắt đầu"), 100);
            var showAll = new CheckBox { Text = L.T("Show merged / discarded", "Hiện cả việc đã gộp / đã bỏ"), Location = new Point(16, 384), AutoSize = true, ForeColor = Ink };
            Action fill = () =>
            {
                lv.Items.Clear();
                foreach (var t in Workspace.Data.Tasks.Where(x => x.ProjectId == proj.Id && (showAll.Checked || x.Open)).OrderByDescending(x => x.Created))
                {
                    bool live = runs.ContainsKey(t.ChatId) && t.Status == "running";
                    lv.Items.Add(new ListViewItem(new[] { t.Title, TaskBranches.Label(t.Status == "running" && !live ? "interrupted" : t.Status), t.Files.ToString(), t.Branch, t.Created.ToString("dd/MM HH:mm") })
                        { Tag = t, ForeColor = t.Status == "done" ? Good : t.Status == "failed" ? Bad : t.Open ? Ink : Dim });
                }
                if (lv.Items.Count == 0) lv.Items.Add(new ListViewItem(new[] { L.T("No tasks yet. Use \"Do task…\" in a chat.", "Chưa có việc nào. Dùng \"Giao việc…\" trong một chat."), "", "", "", "" }));
            };
            showAll.CheckedChanged += (s, e) => fill();
            Func<WsTask> sel = () => lv.SelectedItems.Count > 0 ? lv.SelectedItems[0].Tag as WsTask : null;
            var bDiff = Btn(L.T("View changes", "Xem thay đổi"), 130, true); bDiff.Location = new Point(16, 418);
            var bMerge = Btn(L.T("Merge", "Gộp"), 90); bMerge.Location = new Point(154, 418);
            var bDiscard = Btn(L.T("Discard", "Bỏ"), 90); bDiscard.Location = new Point(252, 418); bDiscard.ForeColor = Bad;
            var bFolder = Btn(L.T("Open folder", "Mở thư mục"), 110); bFolder.Location = new Point(350, 418);
            var bCode = Btn("VS Code", 90); bCode.Location = new Point(468, 418);
            var bStopT = Btn(L.T("Stop", "Dừng"), 80); bStopT.Location = new Point(566, 418);
            var close = Btn(L.T("Close", "Đóng"), 100); close.Location = new Point(704, 418); close.DialogResult = DialogResult.Cancel;
            bDiff.Click += (s, e) => { var t = sel(); if (t != null) DiffDialog(t, proj); };
            bMerge.Click += (s, e) =>
            {
                var t = sel(); if (t == null || !t.Open || t.Status == "running") return;
                if (!Confirm(L.T("Merge branch ", "Gộp nhánh ") + t.Branch + L.T(" (", " (") + t.Files + L.T(" files) into your current branch of ", " file) vào nhánh hiện tại của ") + proj.Name + "?")) return;
                try { MessageBox.Show(f, TaskBranches.Merge(t, proj), Text); } catch (Exception ex) { MessageBox.Show(f, ex.Message, Text); }
                fill(); UpdateButtons();
            };
            bDiscard.Click += (s, e) =>
            {
                var t = sel(); if (t == null || !t.Open) return;
                if (runs.ContainsKey(t.ChatId) && t.Status == "running") { MessageBox.Show(f, L.T("Stop the task first.", "Hãy dừng việc trước."), Text); return; }
                if (!Confirm(L.T("Discard branch ", "Bỏ nhánh ") + t.Branch + L.T(" and all its changes? This cannot be undone. Your main code is not affected.", " và mọi thay đổi trên đó? Không hoàn tác được. Code chính của bạn không bị ảnh hưởng."))) return;
                TaskBranches.Discard(t, proj); fill(); UpdateButtons();
            };
            bFolder.Click += (s, e) => { var t = sel(); if (t != null && Directory.Exists(t.Worktree)) try { Process.Start("explorer.exe", "\"" + t.Worktree + "\""); } catch { } };
            bCode.Click += (s, e) => { var t = sel(); if (t != null && Directory.Exists(t.Worktree)) try { Process.Start(new ProcessStartInfo("cmd.exe", "/c code \"" + t.Worktree + "\"") { CreateNoWindow = true, UseShellExecute = false }); } catch { } };
            bStopT.Click += (s, e) => { var t = sel(); ClaudeEngine.Run r; if (t != null && runs.TryGetValue(t.ChatId, out r)) r.Stop(); };
            lv.DoubleClick += (s, e) => { var t = sel(); if (t != null) DiffDialog(t, proj); };
            var refresh = new Timer { Interval = 2000 }; refresh.Tick += (s, e) => { var id = sel() != null ? sel().Id : null; fill(); if (id != null) foreach (ListViewItem it in lv.Items) if (it.Tag is WsTask && ((WsTask)it.Tag).Id == id) it.Selected = true; }; refresh.Start();
            fill();
            f.Controls.AddRange(new Control[] { lv, showAll, bDiff, bMerge, bDiscard, bFolder, bCode, bStopT, close });
            f.CancelButton = close;
            f.ShowDialog(this);
            refresh.Dispose();
        }
    }

    void DiffDialog(WsTask t, WsProject proj)
    {
        using (var f = SmallDialog(t.Branch + "  ·  " + t.Title, 980, 680))
        {
            f.FormBorderStyle = FormBorderStyle.Sizable; f.MaximizeBox = true;
            var rt = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.List, ForeColor = Ink, Font = new Font("Consolas", 9.75f), WordWrap = false };
            f.Controls.Add(rt);
            Action<string, Color> put = (s, c) => { rt.SelectionStart = rt.TextLength; rt.SelectionColor = c; rt.AppendText(s + "\n"); };
            put(TaskBranches.Diff(t, proj, true), Accent); put("", Ink);
            foreach (var line in TaskBranches.Diff(t, proj, false).Split('\n'))
            {
                var l = line.TrimEnd('\r');
                put(l, l.StartsWith("+++") || l.StartsWith("---") || l.StartsWith("diff ") ? Accent : l.StartsWith("+") ? Good : l.StartsWith("-") ? Bad : l.StartsWith("@@") ? Color.FromArgb(139, 92, 246) : Ink);
            }
            rt.SelectionStart = 0; rt.ScrollToCaret();
            f.ShowDialog(this);
        }
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
        using (var f = SmallDialog(L.T("Claude sign-in", "Đăng nhập Claude"), 560, 300))
        {
            f.Controls.Add(new Label { Text = L.T("Second Brain uses your Claude plan through Claude Code.\nClick the button: your browser opens, sign in with Google or your Claude account, done.",
                "Second Brain dùng gói Claude của bạn qua Claude Code.\nBấm nút dưới: trình duyệt sẽ mở, đăng nhập bằng Google hoặc tài khoản Claude là xong."),
                Location = new Point(16, 14), Size = new Size(528, 46), ForeColor = Ink });
            var login = Btn(L.T("Sign in with browser", "Đăng nhập bằng trình duyệt"), 260, true); login.Location = new Point(16, 66);
            var status = new Label { Location = new Point(286, 72), Size = new Size(258, 40), ForeColor = Ink };
            f.Controls.Add(new Label { Text = L.T("Advanced: paste a token from `claude setup-token` (stored encrypted, never leaves this PC).",
                "Nâng cao: dán mã từ lệnh `claude setup-token` (được mã hoá, không rời khỏi máy)."),
                Location = new Point(16, 128), Size = new Size(528, 36), ForeColor = Ink });
            var tb = new TextBox { Location = new Point(16, 168), Width = 528, UseSystemPasswordChar = true }; Dark(tb);
            var ok = Btn(L.T("Save token", "Lưu mã"), 100); ok.Location = new Point(338, 236);
            var clear = Btn(L.T("Remove", "Xoá mã"), 100); clear.Location = new Point(16, 236); clear.Visible = ClaudeEngine.HasToken;
            var cancel = Btn(L.T("Close", "Đóng"), 100); cancel.Location = new Point(444, 236); cancel.DialogResult = DialogResult.Cancel;
            login.Click += (s, e) =>
            {
                System.Diagnostics.Process p;
                try { p = ClaudeEngine.StartBrowserLogin(); } catch (Exception ex) { MessageBox.Show(f, ex.Message, Text); return; }
                login.Enabled = false;
                status.Text = L.T("Finish signing in in the browser…", "Hoàn tất đăng nhập trên trình duyệt…");
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { if (p != null) p.WaitForExit(); } catch { }
                    var okNow = ClaudeEngine.CliLoggedIn();
                    try
                    {
                        f.BeginInvoke((Action)(() =>
                        {
                            login.Enabled = true;
                            if (okNow) f.DialogResult = DialogResult.OK;
                            else status.Text = L.T("Not signed in yet. Try again.", "Chưa đăng nhập được. Thử lại nhé.");
                        }));
                    }
                    catch { }
                });
            };
            ok.Click += (s, e) => { try { ClaudeEngine.SetToken(tb.Text); f.DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(f, ex.Message, Text); } };
            clear.Click += (s, e) => { ClaudeEngine.ClearToken(); f.DialogResult = DialogResult.OK; };
            f.Controls.AddRange(new Control[] { login, status, tb, ok, clear, cancel });
            f.CancelButton = cancel;
            if (f.ShowDialog(this) == DialogResult.OK) { shownReadiness = false; RefreshReadiness(); }
        }
    }
}

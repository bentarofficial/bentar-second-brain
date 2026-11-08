// Trang Phòng làm việc: văn phòng sống động + bảng quản lý nhân viên được chọn (kết nối / ngắt kết nối / giao việc)
using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class MainForm
{
    OfficeView office;
    readonly Label offName = new Label(), offRole = new Label(), offState = new Label(), offRecent = new Label();
    Button offConnect, offTask, offLog;

    Panel BuildOfficePage()
    {
        var p = new Panel { BackColor = Bg };
        office = new OfficeView { Dock = DockStyle.Fill };
        var side = new Panel { Dock = DockStyle.Right, Width = 300, BackColor = Panel2, Padding = new Padding(18, 18, 18, 12) };
        offName.Dock = DockStyle.Top; offName.Height = 34; offName.Font = new Font("Segoe UI", 14f, FontStyle.Bold); offName.ForeColor = Ink;
        offRole.Dock = DockStyle.Top; offRole.Height = 42; offRole.ForeColor = Dim; offRole.Font = new Font("Segoe UI", 9.5f);
        offState.Dock = DockStyle.Top; offState.Height = 30; offState.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        offRecent.Dock = DockStyle.Fill; offRecent.ForeColor = Ink; offRecent.Font = new Font("Segoe UI", 9f); offRecent.Padding = new Padding(0, 8, 0, 0);
        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 140, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Panel2 };
        offConnect = Btn("", 262, true); offTask = Btn(L.T("Assign a task…", "Giao việc…"), 262); offLog = Btn(L.T("See all activity", "Xem toàn bộ hoạt động"), 262);
        foreach (var b in new[] { offConnect, offTask, offLog }) b.Margin = new Padding(0, 0, 0, 8);
        btns.Controls.AddRange(new Control[] { offConnect, offTask, offLog });
        side.Controls.Add(offRecent); side.Controls.Add(btns); side.Controls.Add(offState); side.Controls.Add(offRole); side.Controls.Add(offName);
        p.Controls.Add(office); p.Controls.Add(side);

        office.SelectionChanged += s => ShowStaff(s);
        office.TeamChanged += () => { if (!IsDisposed) BeginInvoke(new Action(() => ShowStaff(office.Selected))); };
        offTask.Click += (s, e) => ShowPage("chat");
        offLog.Click += (s, e) => ShowPage("activity");
        offConnect.Click += async (s, e) =>
        {
            var st = office.Selected;
            if (st == null) return;
            if (st.Key == "brain") { ShowPage("chat"); TokenDialog(); return; }
            if (st.LinkName == null) { ShowPage("socket"); return; }
            offConnect.Enabled = false;
            string msg;
            try
            {
                bool disconnect = st.Connected;
                if (disconnect && MessageBox.Show(this, L.T("Disconnect ", "Ngắt kết nối ") + st.Name + L.T(" from Second Brain? Its hooks, skill and instructions are removed too.", " khỏi Second Brain? Hook, skill và hướng dẫn của agent này cũng được gỡ."), Text, MessageBoxButtons.YesNo) != DialogResult.Yes) { offConnect.Enabled = true; return; }
                msg = await Task.Run(() => { if (!disconnect && !Installer.IsInstalled) Installer.Install(); return disconnect ? AgentLinks.Disconnect(st.LinkName) : AgentLinks.Connect(st.LinkName); });
            }
            catch (Exception ex) { msg = ex.Message; }
            offConnect.Enabled = true;
            if (!string.IsNullOrEmpty(msg)) MessageBox.Show(this, st.Name + ": " + msg, Text);
            office.PollLinks();
        };
        ShowStaff(null);
        return p;
    }

    void ShowStaff(OfficeView.Staff s)
    {
        if (s == null)
        {
            offName.Text = L.T("Your office", "Phòng làm việc");
            offRole.Text = L.T("You are the commander. Each agent is a staff member at a desk. Click someone to manage them.", "Bạn là Chỉ huy. Mỗi agent là một nhân viên ngồi một bàn. Bấm vào một người để quản lý.");
            offState.Text = ""; offRecent.Text = L.T("• Green dot: working right now\n• Yellow: online, ready\n• Grey + zz: connected, idle\n• Empty desk: not connected or not installed\n\nPapers flying from the Knowledge vault are real tool calls. When Claude finishes a task, the manager brings you the report.",
                "• Chấm xanh: đang làm việc\n• Vàng: đang trực, sẵn sàng\n• Xám + zz: đã kết nối, đang nghỉ\n• Bàn trống: chưa kết nối hoặc chưa cài\n\nHồ sơ bay ra từ Kho tri thức là các lần gọi công cụ thật. Khi Claude làm xong việc, Trưởng phòng mang báo cáo tới cho bạn.");
            offConnect.Visible = offTask.Visible = false;
            return;
        }
        offName.Text = s.Name; offRole.Text = s.Role;
        string state; Color c;
        if (s.Present && s.Working) { state = L.T("● Working", "● Đang làm việc"); c = Good; }
        else if (s.Present && s.Online) { state = L.T("● Online", "● Đang trực"); c = Color.FromArgb(234, 179, 8); }
        else if (s.Present) { state = L.T("● Connected · idle", "● Đã kết nối · đang nghỉ"); c = Dim; }
        else if (!s.Known) { state = L.T("Checking…", "Đang kiểm tra…"); c = Dim; }
        else if (s.Installed) { state = L.T("○ Not connected", "○ Chưa kết nối"); c = Bad; }
        else { state = L.T("○ Not installed on this PC", "○ Chưa cài trên máy"); c = Dim; }
        offState.Text = state; offState.ForeColor = c;
        var recent = Activity.Recent(300).Where(e => s.Clients.Any(k => (e.Client ?? "").ToLowerInvariant().StartsWith(k)) && e.Action != "cắm vào").Take(8).ToList();
        offRecent.Text = (s.LastSeen > DateTime.MinValue ? L.T("Last seen: ", "Lần cuối: ") + s.LastSeen.ToString("dd/MM HH:mm") + "\n" : "") +
            L.T("Recent work:\n", "Việc gần đây:\n") + (recent.Count == 0 ? L.T("  (nothing yet)", "  (chưa có)") :
            string.Join("\n", recent.Select(e => "  " + e.T.ToString("HH:mm") + "  " + e.Action + (string.IsNullOrEmpty(e.Detail) ? "" : ": " + (e.Detail.Length > 40 ? e.Detail.Substring(0, 40) + "…" : e.Detail)))));
        offTask.Visible = true;
        offConnect.Visible = true;
        if (s.Key == "brain") offConnect.Text = L.T("Claude sign-in…", "Đăng nhập Claude…");
        else if (s.LinkName == null) offConnect.Text = L.T("Open Sockets", "Mở Ổ cắm");
        else if (!s.Installed && s.Known) { offConnect.Visible = false; }
        else offConnect.Text = s.Connected ? L.T("Disconnect", "Ngắt kết nối") : L.T("Connect", "Kết nối");
    }
}

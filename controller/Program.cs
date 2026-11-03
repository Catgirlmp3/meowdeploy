using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowDeploy.Controller;

internal static class Program
{
    [STAThread] static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var api = new ControllerApi(); api.Start(); Application.Run(new Dashboard(api));
    }
}

public sealed class ControllerApi : IDisposable
{
    readonly HttpListener listener = new();
    readonly ConcurrentDictionary<string, ConcurrentQueue<Command>> queues = new();
    readonly ConcurrentDictionary<string, DateTime> checkins = new();
    readonly ConcurrentDictionary<string, JsonElement> statuses = new();
    readonly string token = Environment.GetEnvironmentVariable("MEOWDEPLOY_TOKEN") ?? "meowmeowmeow";
    readonly CancellationTokenSource stop = new();
    public ControllerApi() => listener.Prefixes.Add("http://127.0.0.1:18765/");
    public void Start() { listener.Start(); _ = Task.Run(Loop); }
    async Task Loop() { while (!stop.IsCancellationRequested) { try { var c = await listener.GetContextAsync(); _ = Task.Run(() => Handle(c)); } catch { } } }
    async Task Handle(HttpListenerContext context)
    {
        using var stream = new MemoryStream(); await context.Request.InputStream.CopyToAsync(stream); var raw = stream.ToArray();
        if (!Auth(context.Request, raw)) { context.Response.StatusCode = 401; context.Response.Close(); return; }
        var data = JsonSerializer.Deserialize<JsonElement>(raw); var id = data.GetProperty("agent_id").GetString()!;
        checkins[id] = DateTime.UtcNow; if (data.TryGetProperty("status", out var status)) statuses[id] = status;
        object result = context.Request.Url!.AbsolutePath switch { "/api/poll" => new { commands = Drain(id) }, "/api/ack" => new { ok = true }, _ => new { error = "not found" } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result); context.Response.ContentType = "application/json"; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
    }
    bool Auth(HttpListenerRequest request, byte[] raw)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(token)); var expected = Convert.ToHexString(h.ComputeHash(raw)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Headers["X-MeowDeploy-Signature"] ?? ""), Encoding.ASCII.GetBytes(expected));
    }
    object[] Drain(string id) { var q = queues.GetOrAdd(id, _ => new()); var items = new List<Command>(); while (q.TryDequeue(out var item)) items.Add(item); return items.Cast<object>().ToArray(); }
    public void Queue(string id, string name, object payload) => queues.GetOrAdd(id, _ => new()).Enqueue(new Command(Guid.NewGuid().ToString("N"), name, payload));
    public bool TryStatus(string id, out JsonElement status) => statuses.TryGetValue(id, out status);
    public bool IsOnline(string id) => checkins.TryGetValue(id, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(2);
    public IReadOnlyDictionary<string, DateTime> Checkins => checkins;
    public record Command(string id, string name, object payload);
    public void Stop() { stop.Cancel(); listener.Stop(); }
    public void Dispose() => Stop();
}

public sealed class Dashboard : Form
{
    readonly ControllerApi api; readonly DataGridView grid = new(); readonly Label summary = new(); readonly Label details = new(); readonly TextBox search = new(); readonly System.Windows.Forms.Timer refresh = new() { Interval = 3000 }; readonly string serverFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowDeploy", "servers.json"); readonly List<string> hosts;
    readonly Color bg = Color.FromArgb(17, 15, 22), surface = Color.FromArgb(28, 25, 35), surface2 = Color.FromArgb(39, 34, 47), pink = Color.FromArgb(232, 169, 212), muted = Color.FromArgb(166, 153, 174);
    public Dashboard(ControllerApi api)
    {
        this.api = api; hosts = LoadHosts(); Text = "MeowDeploy"; BackColor = bg; ForeColor = Color.White; Width = 1450; Height = 860; MinimumSize = new Size(1180, 720); AutoScaleMode = AutoScaleMode.Dpi; AutoSizeMode = AutoSizeMode.GrowAndShrink; Font = new Font("Segoe UI", 9); StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = bg }; root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(root); root.Controls.Add(Rail(), 0, 0); root.Controls.Add(Workspace(), 1, 0);
        grid.SelectionChanged += (_, _) => UpdateDetails(); search.TextChanged += (_, _) => FilterRows(); Shown += (_, _) => { LoadRows(); refresh.Tick += (_, _) => RefreshRows(); refresh.Start(); };
    }
    Control Rail()
    {
        var rail = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 19, 29), Padding = new Padding(18, 24, 18, 18) }; rail.Controls.Add(new Label { Text = "◈  MEOW\n    DEPLOY", ForeColor = pink, Font = new Font("Segoe UI", 16, FontStyle.Bold), Dock = DockStyle.Top, Height = 72 }); var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 25, 0, 0) }; foreach (var text in new[] { "▦   Dashboard", "▤   Servers", "◷   Schedules", "⇩   Deployment", "⚙   Settings" }) nav.Controls.Add(Nav(text)); rail.Controls.Add(nav); rail.Controls.Add(new Label { Text = "MEOWDEPLOY\nController  •  Local API\n\nTor transport ready", ForeColor = muted, Dock = DockStyle.Bottom, Height = 92 }); return rail;
    }
    Button Nav(string text) { var b = new Button { Text = text, Width = 174, Height = 44, TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat, BackColor = Color.Transparent, ForeColor = muted, Font = new Font("Segoe UI", 10), Cursor = Cursors.Hand }; b.FlatAppearance.BorderSize = 0; b.MouseEnter += (_, _) => b.ForeColor = pink; b.MouseLeave += (_, _) => b.ForeColor = muted; return b; }
    Control Workspace()
    {
        var work = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(28, 24, 28, 22) }; work.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); work.RowStyles.Add(new RowStyle(SizeType.Absolute, 105)); work.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var head = new Panel { Dock = DockStyle.Fill }; head.Controls.Add(new Label { Text = "Fleet overview", Font = new Font("Segoe UI", 22, FontStyle.Bold), ForeColor = Color.White, AutoSize = true }); summary.Text = "Fleet ready"; summary.ForeColor = muted; summary.Location = new Point(2, 38); summary.AutoSize = true; head.Controls.Add(summary); var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false }; actions.Controls.Add(Action("＋ Add server", (_, _) => AddServer())); actions.Controls.Add(Action("Fleet CPU", (_, _) => FleetOverride())); actions.Controls.Add(Action("Clear all", (_, _) => FleetCommand("clear_override"))); actions.Controls.Add(Action("Restart all", (_, _) => FleetCommand("restart_miner"))); actions.Controls.Add(Action("↻ Sync", (_, _) => RefreshRows())); head.Controls.Add(actions); work.Controls.Add(head, 0, 0);
        var cards = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 0, 0, 4) }; cards.Controls.Add(Card("ONLINE", "0", "hosts responding", pink)); cards.Controls.Add(Card("OFFLINE", "18", "awaiting check-in", Color.FromArgb(238, 130, 160))); cards.Controls.Add(Card("AVG CPU", "—", "effective allocation", Color.FromArgb(180, 150, 235))); cards.Controls.Add(Card("LAST SYNC", "—", "controller time", Color.FromArgb(130, 195, 220))); work.Controls.Add(cards, 0, 1);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 67)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33)); body.Controls.Add(ServerPanel(), 0, 0); body.Controls.Add(DetailPanel(), 1, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill, Appearance = TabAppearance.FlatButtons, ItemSize = new Size(110, 30), SizeMode = TabSizeMode.Fixed }; var dashboardTab = new TabPage("Dashboard") { BackColor = bg, Padding = new Padding(0) }; dashboardTab.Controls.Add(body); var serversTab = new TabPage("Servers") { BackColor = bg }; serversTab.Controls.Add(new Label { Text = "The fleet table is available on the Dashboard tab. Select a host there to use quick actions.", ForeColor = muted, Dock = DockStyle.Fill, Padding = new Padding(28), Font = new Font("Segoe UI", 12) }); var scheduleTab = new TabPage("Schedules") { BackColor = bg }; scheduleTab.Controls.Add(new Label { Text = "Schedules\n\nUse the server actions to apply the Dublin default schedule. The visual schedule editor is next in the workflow.", ForeColor = muted, Font = new Font("Segoe UI", 12), Dock = DockStyle.Fill, Padding = new Padding(28) }); var deployTab = new TabPage("Deployment") { BackColor = bg }; deployTab.Controls.Add(new Label { Text = "Deployment\n\nRun deployment/deploy.py after reviewing SSH host keys. Results appear in the terminal and can be audited per host.", ForeColor = muted, Font = new Font("Segoe UI", 12), Dock = DockStyle.Fill, Padding = new Padding(28) }); var settingsTab = new TabPage("Settings") { BackColor = bg }; settingsTab.Controls.Add(new Label { Text = "Settings\n\nController API: 127.0.0.1:18765\nAuthentication: HMAC token\nDefault test token: meowmeowmeow", ForeColor = muted, Font = new Font("Segoe UI", 12), Dock = DockStyle.Fill, Padding = new Padding(28) }); tabs.TabPages.AddRange(new[] { dashboardTab, serversTab, scheduleTab, deployTab, settingsTab }); work.Controls.Add(tabs, 0, 2); return work;
    }
    Panel Card(string label, string value, string hint, Color color) { var p = new Panel { Width = 190, Height = 92, BackColor = surface, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(16) }; p.Paint += (_, e) => { using var pen = new Pen(color, 3); e.Graphics.DrawLine(pen, 0, 0, 0, p.Height); }; p.Controls.Add(new Label { Text = hint, ForeColor = muted, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Bottom, Height = 20 }); p.Controls.Add(new Label { Text = value, ForeColor = color, Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top }); p.Controls.Add(new Label { Text = label, ForeColor = muted, AutoSize = true, Dock = DockStyle.Top }); return p; }
    Button Action(string text, EventHandler click) { var b = new Button { Text = text, AutoSize = true, Height = 36, FlatStyle = FlatStyle.Flat, BackColor = surface2, ForeColor = pink, Padding = new Padding(12, 0, 12, 0), Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = Color.FromArgb(75, 61, 84); b.Click += click; return b; }
    Control ServerPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = surface, Padding = new Padding(16) }; var top = new Panel { Dock = DockStyle.Top, Height = 48 }; top.Controls.Add(new Label { Text = "Servers", Font = new Font("Segoe UI", 13, FontStyle.Bold), AutoSize = true, Location = new Point(0, 7) }); search.PlaceholderText = "Search address"; search.Width = 230; search.Dock = DockStyle.Right; search.BackColor = surface2; search.ForeColor = Color.White; top.Controls.Add(search); p.Controls.Add(top);
        grid.Dock = DockStyle.Fill; grid.BackgroundColor = surface; grid.BorderStyle = BorderStyle.None; grid.RowHeadersVisible = false; grid.AllowUserToAddRows = false; grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.AutoGenerateColumns = false; grid.RowTemplate.Height = 38; grid.ColumnHeadersHeight = 36; grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = surface2, ForeColor = muted, Font = new Font("Segoe UI", 8, FontStyle.Bold), WrapMode = DataGridViewTriState.False }; grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = surface, ForeColor = Color.White, SelectionBackColor = Color.FromArgb(73, 52, 77), SelectionForeColor = Color.White, Padding = new Padding(6, 0, 6, 0), WrapMode = DataGridViewTriState.False }; foreach (var c in new[] { ("Host", "host"), ("State", "state"), ("CPU", "cpu"), ("Agent", "agent"), ("Last check-in", "checkin") }) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Item1, Name = c.Item2, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 88 }); var menu = new ContextMenuStrip(); foreach (var item in new[] { ("Set CPU…", "temporary_override"), ("Start miner", "start_miner"), ("Stop miner", "stop_miner"), ("Restart miner", "restart_miner"), ("Clear override", "clear_override") }) { var mi = menu.Items.Add(item.Item1); mi.Click += (_, _) => SendSelected(item.Item2); } grid.ContextMenuStrip = menu; p.Controls.Add(grid); grid.BringToFront(); return p;
    }
    Control DetailPanel() { var p = new Panel { Dock = DockStyle.Fill, BackColor = surface2, Padding = new Padding(18) }; var title = new Label { Text = "Selected host", Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = Color.White, Dock = DockStyle.Top, Height = 40 }; details.Text = "Select a server to view status and actions."; details.ForeColor = muted; details.Dock = DockStyle.Top; details.Height = 110; var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 180, FlowDirection = FlowDirection.TopDown, WrapContents = false }; buttons.Controls.Add(Action("Set CPU allocation", (_, _) => SendSelected("temporary_override"))); buttons.Controls.Add(Action("Clear override", (_, _) => SendSelected("clear_override"))); buttons.Controls.Add(Action("Restart miner", (_, _) => SendSelected("restart_miner"))); buttons.Controls.Add(Action("Get status", (_, _) => SendSelected("get_status"))); p.Controls.Add(buttons); p.Controls.Add(details); p.Controls.Add(title); return p; }
    void LoadRows() { foreach (var host in hosts) grid.Rows.Add(host, "Offline", "—", "—", "Never"); RefreshRows(); }
    void RefreshRows() { for (var i = 0; i < grid.Rows.Count; i++) { var host = grid.Rows[i].Cells["host"].Value?.ToString() ?? ""; var online = api.IsOnline(host); grid.Rows[i].Cells["state"].Value = online ? "● Online" : "○ Offline"; grid.Rows[i].Cells["state"].Style.ForeColor = online ? Color.FromArgb(130, 220, 170) : Color.FromArgb(230, 145, 165); if (api.TryStatus(host, out var s)) { if (s.TryGetProperty("effective_percent", out var cpu)) grid.Rows[i].Cells["cpu"].Value = $"{cpu.GetDouble():0}%"; if (s.TryGetProperty("version", out var v)) grid.Rows[i].Cells["agent"].Value = v.GetString(); } grid.Rows[i].Cells["checkin"].Value = api.Checkins.TryGetValue(host, out var t) ? t.ToLocalTime().ToString("g") : "Never"; } summary.Text = $"{hosts.Count} configured hosts  ·  {hosts.Count(api.IsOnline)} online  ·  {DateTime.Now:t}"; UpdateDetails(); }
    void FilterRows() { var q = search.Text.Trim().ToLowerInvariant(); foreach (DataGridViewRow row in grid.Rows) row.Visible = q.Length == 0 || (row.Cells["host"].Value?.ToString()?.ToLowerInvariant().Contains(q) ?? false); }
    void UpdateDetails() { if (grid.CurrentRow is null) return; var host = grid.CurrentRow.Cells["host"].Value?.ToString() ?? ""; details.Text = api.TryStatus(host, out var s) ? $"{(api.IsOnline(host) ? "Online" : "Offline")}\nEffective CPU: {(s.TryGetProperty("effective_percent", out var p) ? p.GetDouble().ToString("0") : "—")} %\nLast check-in: {(api.Checkins.TryGetValue(host, out var t) ? t.ToLocalTime().ToString("g") : "Never")}" : "No status received yet."; }
    void SendSelected(string command) { if (grid.CurrentRow is null) { MessageBox.Show("Select a server first.", "MeowDeploy"); return; } var host = grid.CurrentRow.Cells["host"].Value!.ToString()!; object payload = command == "temporary_override" ? new { percent = PromptNumber("CPU percentage", 35), duration_seconds = 3600 } : new { }; api.Queue(host, command, payload); MessageBox.Show($"{command.Replace('_', ' ')} queued for {host}.", "Command queued", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    void AddServer() { var host = Microsoft.VisualBasic.Interaction.InputBox("IPv4 address or onion hostname:", "Add server", "172.16.3."); if (!string.IsNullOrWhiteSpace(host) && !hosts.Contains(host)) { hosts.Add(host); SaveHosts(); grid.Rows.Add(host, "Offline", "—", "—", "Never"); summary.Text = $"Added {host} · server list saved"; } }
    void FleetOverride() { var percent = PromptNumber("Fleet CPU percentage", 35); foreach (var host in hosts) api.Queue(host, "temporary_override", new { percent, duration_seconds = 3600 }); summary.Text = $"Fleet override queued · {percent:0}% for 1 hour"; }
    void FleetCommand(string command) { foreach (var host in hosts) api.Queue(host, command, new { }); summary.Text = $"{command.Replace('_', ' ')} queued for {hosts.Count} hosts"; }
    List<string> LoadHosts() { try { if (File.Exists(serverFile)) { var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(serverFile)); if (saved is { Count: > 0 }) return saved; } } catch { } return Enumerable.Range(3, 18).Select(i => $"172.16.3.{i}").ToList(); }
    void SaveHosts() { Directory.CreateDirectory(Path.GetDirectoryName(serverFile)!); File.WriteAllText(serverFile, JsonSerializer.Serialize(hosts, new JsonSerializerOptions { WriteIndented = true })); }
    static double PromptNumber(string title, double initial) { using var d = new Form { Text = title, Width = 300, Height = 145, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog }; var n = new NumericUpDown { Minimum = 0, Maximum = 100, Value = (decimal)initial, Dock = DockStyle.Top, Height = 32 }; var ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom }; d.Controls.Add(n); d.Controls.Add(ok); d.AcceptButton = ok; return d.ShowDialog() == DialogResult.OK ? (double)n.Value : initial; }
}

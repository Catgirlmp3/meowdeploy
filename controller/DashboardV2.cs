using System.Text.Json;

namespace MeowDeploy.Controller;

/// <summary>
/// Redesigned controller workspace. Keeps the existing authenticated ControllerApi
/// and command queue; this class is presentation and operator workflow only.
/// </summary>
public sealed class DashboardV2 : Form
{
    readonly ControllerApi api;
    readonly List<string> hosts;
    readonly string serverFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowDeploy", "servers.json");
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 5000 };
    readonly DataGridView machines = new();
    readonly TextBox search = new();
    readonly Label pageTitle = new(), pageSubtitle = new(), onlineMetric = new(), offlineMetric = new(),
        cpuMetric = new(), syncMetric = new(), selectedName = new(), selectedState = new(), selectedCpu = new(),
        selectedTemp = new(), selectedHash = new(), selectedUptime = new(), footer = new();
    readonly FlowLayoutPanel nav = new(), content = new();
    readonly Panel stage = new();
    string activePage = "Overview";
    string selectedHost = "";
    readonly Color bg = Color.FromArgb(15, 14, 20), rail = Color.FromArgb(21, 19, 28),
        surface = Color.FromArgb(28, 25, 36), surface2 = Color.FromArgb(38, 33, 48),
        pink = Color.FromArgb(232, 169, 212), text = Color.FromArgb(246, 241, 249),
        muted = Color.FromArgb(166, 155, 177), green = Color.FromArgb(112, 218, 165),
        red = Color.FromArgb(239, 119, 139), blue = Color.FromArgb(145, 174, 239),
        amber = Color.FromArgb(235, 190, 110);

    public DashboardV2(ControllerApi api)
    {
        this.api = api;
        hosts = LoadHosts();
        Text = "MeowDeploy — Fleet Control";
        BackColor = bg; ForeColor = text; Font = new Font("Segoe UI", 9);
        MinimumSize = new Size(1120, 720); Size = new Size(1540, 920);
        StartPosition = FormStartPosition.CenterScreen;
        BuildShell();
        Shown += (_, _) => { RefreshData(); refresh.Start(); };
        refresh.Tick += (_, _) => RefreshData();
        FormClosed += (_, _) => refresh.Stop();
    }

    void BuildShell()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = bg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = rail, Padding = new Padding(12, 22, 10, 14), MinimumSize = new Size(0, 0) };
        root.Controls.Add(sidebar, 0, 0);
        var brand = new Label { Text = "◈  MEOWDEPLOY", ForeColor = pink, Font = new Font("Segoe UI", 16, FontStyle.Bold), Dock = DockStyle.Top, Height = 58 };
        sidebar.Controls.Add(brand);
        var navHost = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 330, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        sidebar.Controls.Add(navHost);
        foreach (var item in new[] { ("⌂", "Overview"), ("▦", "Machines"), ("◷", "Monitoring"), ("⇧", "Deployments"), ("☷", "Schedules"), ("⚙", "Settings") })
        {
            var b = new Button { Text = item.Item1 + "    " + item.Item2, Width = 160, Height = 44, TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat, BackColor = rail, ForeColor = muted, Font = new Font("Segoe UI", 10), Cursor = Cursors.Hand, Margin = new Padding(0, 3, 0, 3) };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => ShowPage(item.Item2);
            navHost.Controls.Add(b);
        }
        var bottom = new Label { Text = "CONTROLLER\nLocal API · 127.0.0.1:18765\n\nRefreshes every 5 seconds", ForeColor = muted, Dock = DockStyle.Bottom, Height = 90 };
        sidebar.Controls.Add(bottom);

        var main = new Panel { Dock = DockStyle.Fill, BackColor = bg, Padding = new Padding(16, 16, 16, 12) };
        root.Controls.Add(main, 1, 0);
        var header = new Panel { Dock = DockStyle.Top, Height = 128, BackColor = bg };
        main.Controls.Add(header);
        pageTitle.Text = "Overview"; pageTitle.Font = new Font("Segoe UI", 25, FontStyle.Bold); pageTitle.ForeColor = text;
        pageTitle.AutoSize = false; pageTitle.Location = new Point(0, 0); pageTitle.Size = new Size(300, 42); header.Controls.Add(pageTitle);
        pageSubtitle.Text = "Your infrastructure, at a glance."; pageSubtitle.ForeColor = muted; pageSubtitle.AutoSize = false; pageSubtitle.Size = new Size(320, 34);
        pageSubtitle.Location = new Point(3, 48); header.Controls.Add(pageSubtitle);
        var topActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Width = 290, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Controls.Add(topActions);
        topActions.Controls.Add(TopButton("↻  Refresh", (_, _) => RefreshData()));
        topActions.Controls.Add(TopButton("+  Add machine", (_, _) => AddMachine()));
        stage.Dock = DockStyle.Fill; stage.BackColor = bg; main.Controls.Add(stage);
        footer.Text = "Ready"; footer.ForeColor = muted; footer.Dock = DockStyle.Bottom; footer.Height = 26; main.Controls.Add(footer);
        ShowPage("Overview");
    }

    Button TopButton(string caption, EventHandler click)
    {
        var b = new Button { Text = caption, AutoSize = true, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = surface2, ForeColor = pink, Padding = new Padding(12, 0, 12, 0), Margin = new Padding(6, 4, 0, 0) };
        b.FlatAppearance.BorderColor = Color.FromArgb(78, 61, 87); b.Click += click; return b;
    }
    Label Txt(string value, int size = 10, bool bold = false, Color? color = null) =>
        new() { Text = value, AutoSize = true, ForeColor = color ?? text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
    Panel Card(string caption, Label value, string hint, Color accent)
    {
        var p = new Panel { BackColor = surface, Height = 112, Padding = new Padding(18), Margin = new Padding(0, 0, 14, 0) };
        p.Controls.Add(Txt(caption.ToUpperInvariant(), 9, true, muted));
        value.Text = "—"; value.Font = new Font("Segoe UI", 24, FontStyle.Bold); value.ForeColor = accent; value.Location = new Point(18, 29); p.Controls.Add(value);
        var h = Txt(hint, 9, false, muted); h.Location = new Point(19, 79); p.Controls.Add(h); return p;
    }

    void ShowPage(string page)
    {
        activePage = page; pageTitle.Text = page;
        pageSubtitle.Text = page switch {
            "Overview" => "Your infrastructure, at a glance.",
            "Machines" => "Manage workers, inspect telemetry and issue individual commands.",
            "Monitoring" => "Live fleet health and reported performance.",
            "Deployments" => "Agent rollout and connectivity guidance.",
            "Schedules" => "Configure CPU schedules for the worker fleet.",
            _ => "Controller configuration and local security."
        };
        stage.Controls.Clear();
        Control view = page switch {
            "Overview" => OverviewPage(),
            "Machines" => MachinesPage(),
            "Monitoring" => MonitoringPage(),
            "Deployments" => DeploymentsPage(),
            "Schedules" => SchedulesPage(),
            _ => SettingsPage()
        };
        view.Dock = DockStyle.Fill; stage.Controls.Add(view);
        RefreshData();
    }

    Control OverviewPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = bg, Padding = new Padding(0, 8, 0, 0) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var cards = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
        cards.Controls.Add(Card("Online machines", onlineMetric, "Agents checked in recently", green));
        cards.Controls.Add(Card("Offline machines", offlineMetric, "Waiting for agent check-in", red));
        cards.Controls.Add(Card("Average CPU limit", cpuMetric, "Effective allocation", blue));
        cards.Controls.Add(Card("Last refresh", syncMetric, "Controller local time", pink));
        layout.Controls.Add(cards, 0, 0);
        var section = new Panel { Dock = DockStyle.Fill };
        var label = Txt("MACHINE FLEET", 11, true); label.Location = new Point(0, 18); section.Controls.Add(label);
        var add = TopButton("+ Add machine", (_, _) => AddMachine()); add.Anchor = AnchorStyles.Top | AnchorStyles.Right; add.Location = new Point(350, 7); section.Controls.Add(add);
        layout.Controls.Add(section, 0, 1);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); split.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        split.Controls.Add(BuildMachineGrid(), 0, 0); split.Controls.Add(SelectedPanel(), 0, 1);
        layout.Controls.Add(split, 0, 2); return layout;
    }

    Control MachinesPage()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = bg };
        var bar = new Panel { Dock = DockStyle.Top, Height = 54 };
        search.PlaceholderText = "Search by worker name or address"; search.Width = 330; search.Location = new Point(0, 8);
        search.BackColor = surface; search.ForeColor = text; search.TextChanged += (_, _) => FilterMachines(); bar.Controls.Add(search);
        var hint = Txt("Select a row to inspect the machine. Right-click for actions.", 9, false, muted); hint.Location = new Point(350, 16); bar.Controls.Add(hint);
        panel.Controls.Add(BuildMachineGrid()); panel.Controls.Add(bar); return panel;
    }

    Control BuildMachineGrid()
    {
        machines.Dock = DockStyle.Fill; machines.BackgroundColor = surface; machines.BorderStyle = BorderStyle.None;
        machines.RowHeadersVisible = false; machines.AllowUserToAddRows = false; machines.AllowUserToResizeRows = false;
        machines.MultiSelect = false; machines.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        machines.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; machines.RowTemplate.Height = 42;
        machines.EnableHeadersVisualStyles = false;
        machines.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = surface2, ForeColor = muted, Font = new Font("Segoe UI", 9, FontStyle.Bold), Padding = new Padding(8) };
        machines.DefaultCellStyle = new DataGridViewCellStyle { BackColor = surface, ForeColor = text, SelectionBackColor = Color.FromArgb(73, 52, 77), SelectionForeColor = text, Padding = new Padding(8, 0, 8, 0) };
        machines.Columns.Clear();
        foreach (var c in new[] { ("Worker", "worker"), ("Status", "status"), ("CPU limit", "cpu"), ("CPU use", "util"), ("Temperature", "temp"), ("Hashrate", "hash"), ("Last seen", "seen") })
            machines.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Item1, Name = c.Item2, MinimumWidth = 75 });
        machines.SelectionChanged -= MachineSelectionChanged; machines.SelectionChanged += MachineSelectionChanged;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Set CPU limit…", null, (_, _) => SendSelected("temporary_override"));
        menu.Items.Add("Get status", null, (_, _) => SendSelected("get_status"));
        menu.Items.Add("Apply pending settings", null, (_, _) => SendSelected("apply_now"));
        menu.Items.Add("Restart miner", null, (_, _) => SendSelected("restart_miner"));
        menu.Items.Add("Stop miner", null, (_, _) => SendSelected("stop_miner"));
        machines.ContextMenuStrip = menu;
        return machines;
    }

    Control SelectedPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = surface, Padding = new Padding(12), AutoScroll = true };
        selectedName.Text = "Select a machine"; selectedName.Font = new Font("Segoe UI", 18, FontStyle.Bold);
        selectedName.Location = new Point(20, 20); p.Controls.Add(selectedName);
        selectedState.Location = new Point(22, 62); p.Controls.Add(selectedState);
        var metrics = new[] { ("Effective CPU", selectedCpu), ("Temperature", selectedTemp), ("Hashrate", selectedHash), ("Uptime", selectedUptime) };
        var y = 112;
        foreach (var (name, value) in metrics) { var l = Txt(name, 9, true, muted); l.Location = new Point(22, y); p.Controls.Add(l); value.Location = new Point(22, y + 22); value.Font = new Font("Segoe UI", 15, FontStyle.Bold); p.Controls.Add(value); y += 76; }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 132, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoScroll = true };
        actions.Controls.Add(TopButton("Set CPU allocation", (_, _) => SendSelected("temporary_override")));
        actions.Controls.Add(TopButton("Get latest status", (_, _) => SendSelected("get_status")));
        actions.Controls.Add(TopButton("Apply now", (_, _) => SendSelected("apply_now")));
        p.Controls.Add(actions); return p;
    }

    Control MonitoringPage()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = bg, Padding = new Padding(8) };
        var info = Txt("LIVE FLEET TELEMETRY", 12, true, pink); info.Dock = DockStyle.Top; info.Height = 40; panel.Controls.Add(info);
        var cards = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 130, WrapContents = false };
        var online = new Label(); var offline = new Label(); var cpu = new Label(); var sync = new Label();
        cards.Controls.Add(Card("Online", online, "Currently checking in", green));
        cards.Controls.Add(Card("Offline", offline, "No recent check-in", red));
        cards.Controls.Add(Card("Average CPU", cpu, "Reported allocation", blue));
        cards.Controls.Add(Card("Updated", sync, "Local time", pink)); panel.Controls.Add(cards);
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = surface, ForeColor = text, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 10) };
        panel.Controls.Add(list);
        void Update()
        {
            var states = hosts.Select(h => (Host:h, State:api.GetHostState(h))).ToList();
            online.Text = states.Count(x => x.State.Online).ToString(); offline.Text = states.Count(x => !x.State.Online).ToString();
            var samples = hosts.Select(h => api.TryStatus(h, out var s) && s.TryGetProperty("effective_percent", out var v) ? (double?)v.GetDouble() : null).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            cpu.Text = samples.Count == 0 ? "—" : $"{samples.Average():0}%"; sync.Text = DateTime.Now.ToString("HH:mm:ss");
            list.BeginUpdate(); list.Items.Clear();
            foreach (var h in hosts) { var state = api.GetHostState(h); var seen = api.Checkins.TryGetValue(h, out var t) ? t.ToLocalTime().ToString("g") : "Never"; list.Items.Add($"{(state.Online ? "ONLINE " : "OFFLINE")}  {h,-28}  {seen}"); }
            list.EndUpdate();
        }
        panel.Tag = (Action)Update; Update(); return panel;
    }

    Control DeploymentsPage()
    {
        var p = InfoPage("AGENT DEPLOYMENT", "Deploy agents using the repository's deployment tooling. Review SSH host keys and target inventory before running any rollout.",
            "Recommended workflow", "1. Verify each target's hostname and SSH fingerprint.\n2. Run deployment/deploy.py from a trusted administration machine.\n3. Confirm the agent service is enabled and healthy.\n4. Confirm the worker appears in Machines and reports telemetry.",
            "Safety", "Fleet commands are queued until an agent checks in. Avoid broad actions until the inventory has been reviewed.");
        return p;
    }

    Control SchedulesPage()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = bg, Padding = new Padding(8) };
        var title = Txt("DAILY CPU SCHEDULE", 12, true, pink); title.Dock = DockStyle.Top; title.Height = 40; p.Controls.Add(title);
        var grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = surface, BorderStyle = BorderStyle.None, AllowUserToAddRows = true, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = surface2, ForeColor = muted };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = surface, ForeColor = text, SelectionBackColor = Color.FromArgb(73, 52, 77) };
        grid.Columns.Add("start", "Start (HH:mm)"); grid.Columns.Add("end", "End (HH:mm)"); grid.Columns.Add("percent", "CPU %");
        grid.Rows.Add("00:00", "06:00", "75"); grid.Rows.Add("06:00", "17:00", "35"); grid.Rows.Add("17:00", "18:00", "50"); grid.Rows.Add("18:00", "00:00", "60");
        p.Controls.Add(grid);
        var apply = TopButton("Queue schedule for all workers", (_, _) => {
            var intervals = new List<object>();
            foreach (DataGridViewRow row in grid.Rows) if (!row.IsNewRow && double.TryParse(row.Cells[2].Value?.ToString(), out var pct) && pct is >= 0 and <= 100)
                intervals.Add(new { start = row.Cells[0].Value?.ToString(), end = row.Cells[1].Value?.ToString(), percent = pct });
            if (intervals.Count == 0) { MessageBox.Show("Add at least one valid interval."); return; }
            if (MessageBox.Show($"Queue this schedule for {hosts.Count} configured workers?", "Confirm fleet schedule", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            foreach (var h in hosts) api.Queue(h, "set_schedule", new { timezone = "Europe/Dublin", intervals });
            footer.Text = $"Schedule queued for {hosts.Count} workers.";
        });
        apply.Dock = DockStyle.Bottom; p.Controls.Add(apply); return p;
    }

    Control SettingsPage() => InfoPage("CONTROLLER SETTINGS", "The controller API listens on 127.0.0.1:18765. It is intentionally bound to loopback and should not be exposed directly to the internet.",
        "Authentication", "Set MEOWDEPLOY_TOKEN to a random secret of at least 32 characters. Configure the same value on every agent.",
        "Local data", $"Worker inventory: {serverFile}\nPending commands: %APPDATA%\\MeowDeploy\\pending-commands.json");

    Panel InfoPage(string heading, string intro, string h1, string body1, string h2, string body2)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = bg, Padding = new Padding(8) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        flow.Controls.Add(Txt(heading, 12, true, pink));
        flow.Controls.Add(Txt(intro, 11, false, muted));
        flow.Controls.Add(Txt(h1, 14, true));
        flow.Controls.Add(Txt(body1, 10, false, text));
        flow.Controls.Add(Txt(h2, 14, true));
        flow.Controls.Add(Txt(body2, 10, false, text));
        p.Controls.Add(flow); return p;
    }

    void RefreshData()
    {
        if (IsDisposed) return;
        var snapshot = hosts.Select(h => (Host:h, Online:api.GetHostState(h).Online)).ToList();
        if (activePage == "Overview") {
            onlineMetric.Text = snapshot.Count(x => x.Online).ToString();
            offlineMetric.Text = snapshot.Count(x => !x.Online).ToString();
            var cpus = hosts.Select(h => api.TryStatus(h, out var s) && s.TryGetProperty("effective_percent", out var v) ? (double?)v.GetDouble() : null).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            cpuMetric.Text = cpus.Count == 0 ? "—" : $"{cpus.Average():0}%";
            syncMetric.Text = DateTime.Now.ToString("HH:mm:ss");
        }
        if (machines.Columns.Count > 0) {
            var current = selectedHost;
            machines.Rows.Clear();
            foreach (var h in hosts) {
                var online = api.GetHostState(h).Online;
                string Read(string prop, string format = "{0:0.##}") {
                    if (!api.TryStatus(h, out var s) || !s.TryGetProperty("metrics", out var m) || !m.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number) return "—";
                    return string.Format(format, v.GetDouble());
                }
                var cpu = api.TryStatus(h, out var st) && st.TryGetProperty("effective_percent", out var e) ? $"{e.GetDouble():0}%" : "—";
                var util = Read("cpu_utilization_percent", "{0:0}%");
                var temp = Read("temperature_c", "{0:0.0} °C");
                var hash = Read("hashrate", "{0:0.##} H/s");
                var seen = api.Checkins.TryGetValue(h, out var t) ? t.ToLocalTime().ToString("g") : "Never";
                machines.Rows.Add(h, online ? "● Online" : "○ Offline", cpu, util, temp, hash, seen);
                var row = machines.Rows[machines.Rows.Count - 1]; row.Cells["status"].Style.ForeColor = online ? green : red;
                if (h == current) row.Selected = true;
            }
            if (machines.Rows.Count > 0 && machines.SelectedRows.Count == 0) machines.Rows[0].Selected = true;
        }
        if (activePage == "Monitoring" && stage.Controls.Count > 0 && stage.Controls[0].Tag is Action update) update();
        UpdateSelected();
    }

    void MachineSelectionChanged(object? sender, EventArgs e)
    {
        if (machines.CurrentRow?.Cells["worker"].Value is string host) { selectedHost = host; UpdateSelected(); }
    }
    void UpdateSelected()
    {
        if (selectedHost.Length == 0) return;
        selectedName.Text = selectedHost;
        var online = api.GetHostState(selectedHost).Online;
        selectedState.Text = online ? "● ONLINE" : "○ OFFLINE"; selectedState.ForeColor = online ? green : red;
        if (!api.TryStatus(selectedHost, out var s)) { selectedCpu.Text = selectedTemp.Text = selectedHash.Text = selectedUptime.Text = "No telemetry received"; return; }
        selectedCpu.Text = s.TryGetProperty("effective_percent", out var c) ? $"{c.GetDouble():0}%" : "—";
        string Metric(string key, string suffix) => s.TryGetProperty("metrics", out var m) && m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? $"{v.GetDouble():0.##}{suffix}" : "—";
        selectedTemp.Text = Metric("temperature_c", " °C"); selectedHash.Text = Metric("hashrate", " H/s");
        selectedUptime.Text = Metric("uptime_seconds", " sec");
    }
    void FilterMachines()
    {
        var q = search.Text.Trim();
        foreach (DataGridViewRow row in machines.Rows) row.Visible = q.Length == 0 || row.Cells["worker"].Value?.ToString()?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
    }
    void SendSelected(string command)
    {
        var host = selectedHost;
        if (string.IsNullOrWhiteSpace(host)) { MessageBox.Show("Select a worker first."); return; }
        object payload = command == "temporary_override" ? new { percent = PromptCpu(), duration_seconds = 3600 } : new { };
        if (MessageBox.Show($"Queue '{command.Replace('_', ' ')}' for {host}?", "Confirm command", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        api.Queue(host, command, payload); footer.Text = $"{command} queued for {host}.";
    }
    double PromptCpu()
    {
        using var d = new Form { Text = "CPU allocation", Width = 300, Height = 160, StartPosition = FormStartPosition.CenterParent, BackColor = surface, ForeColor = text, FormBorderStyle = FormBorderStyle.FixedDialog };
        var n = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 35, Location = new Point(20, 20), Width = 230 };
        var ok = new Button { Text = "Queue", DialogResult = DialogResult.OK, Location = new Point(20, 65), Width = 90 };
        d.Controls.Add(n); d.Controls.Add(ok); d.AcceptButton = ok;
        return d.ShowDialog(this) == DialogResult.OK ? (double)n.Value : 35;
    }
    void AddMachine()
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Enter worker name, IP address, or agent ID:", "Add machine", "");
        name = name.Trim();
        if (name.Length == 0 || hosts.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        hosts.Add(name); SaveHosts(); RefreshData(); footer.Text = $"Added {name}.";
    }
    List<string> LoadHosts()
    {
        try { if (File.Exists(serverFile)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(serverFile)) ?? new(); } catch { }
        return Enumerable.Range(3, 18).Select(i => $"172.16.3.{i}").ToList();
    }
    void SaveHosts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(serverFile)!);
        File.WriteAllText(serverFile, JsonSerializer.Serialize(hosts, new JsonSerializerOptions { WriteIndented = true }));
    }
}

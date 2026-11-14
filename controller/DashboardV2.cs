using System.Text.Json;

namespace MeowDeploy.Controller;

/// <summary>
/// Responsive operator dashboard for the existing controller API.
/// Layout is built from docked table/flow panels rather than fixed-position controls.
/// </summary>
public sealed class DashboardV2 : Form
{
    private readonly ControllerApi api;
    private readonly List<string> hosts;
    private readonly string serverFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowDeploy", "servers.json");
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 5000 };

    private readonly DataGridView machines = new();
    private readonly TextBox search = new();
    private readonly Panel stage = new();
    private readonly Label pageTitle = new(), pageSubtitle = new(), footer = new();
    private readonly Label onlineValue = new(), offlineValue = new(), cpuValue = new(), updatedValue = new();
    private readonly Label selectedName = new(), selectedState = new(), selectedCpu = new(),
        selectedTemp = new(), selectedHash = new(), selectedUptime = new();

    private string activePage = "Overview";
    private string selectedHost = "";

    private readonly Color bg = Color.FromArgb(13, 12, 18);
    private readonly Color sidebarColor = Color.FromArgb(19, 17, 26);
    private readonly Color surface = Color.FromArgb(25, 22, 33);
    private readonly Color surfaceRaised = Color.FromArgb(34, 29, 44);
    private readonly Color border = Color.FromArgb(55, 46, 66);
    private readonly Color accent = Color.FromArgb(232, 169, 212);
    private readonly Color foreground = Color.FromArgb(244, 239, 248);
    private readonly Color muted = Color.FromArgb(160, 149, 173);
    private readonly Color green = Color.FromArgb(112, 218, 165);
    private readonly Color red = Color.FromArgb(239, 119, 139);
    private readonly Color blue = Color.FromArgb(145, 174, 239);
    private readonly Color amber = Color.FromArgb(235, 190, 110);

    public DashboardV2(ControllerApi api)
    {
        this.api = api;
        hosts = LoadHosts();

        Text = "MeowDeploy | Fleet Control";
        BackColor = bg;
        ForeColor = foreground;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(1050, 700);
        Size = new Size(1480, 900);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildShell();
        Shown += (_, _) => { RefreshData(); refreshTimer.Start(); };
        refreshTimer.Tick += (_, _) => RefreshData();
        FormClosed += (_, _) => refreshTimer.Stop();
    }

    private void BuildShell()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = bg,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 204));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var sidebar = BuildSidebar();
        root.Controls.Add(sidebar, 0, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = bg,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(26, 20, 26, 14),
            Margin = Padding.Empty
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.Controls.Add(main, 1, 0);

        main.Controls.Add(BuildHeader(), 0, 0);
        stage.Dock = DockStyle.Fill;
        stage.BackColor = bg;
        stage.Margin = Padding.Empty;
        main.Controls.Add(stage, 0, 1);

        footer.Dock = DockStyle.Fill;
        footer.Text = "●  Controller ready";
        footer.ForeColor = muted;
        footer.TextAlign = ContentAlignment.MiddleLeft;
        footer.Font = new Font("Segoe UI", 8.5F);
        main.Controls.Add(footer, 0, 2);

        ShowPage("Overview");
    }

    private Control BuildSidebar()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = sidebarColor,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14, 22, 14, 14),
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));

        var brand = new Label
        {
            Text = "◈  MEOWDEPLOY",
            Dock = DockStyle.Fill,
            ForeColor = accent,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        panel.Controls.Add(brand, 0, 0);

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = sidebarColor,
            Padding = new Padding(0, 8, 0, 0),
            Margin = Padding.Empty
        };
        panel.Controls.Add(nav, 0, 1);

        foreach (var item in new[]
        {
            ("⌂", "Overview"), ("▦", "Machines"), ("◷", "Monitoring"),
            ("⇧", "Deployments"), ("☷", "Schedules"), ("⚙", "Settings")
        })
        {
            var button = new Button
            {
                Text = item.Item1 + "    " + item.Item2,
                Width = 172,
                Height = 44,
                TextAlign = ContentAlignment.MiddleLeft,
                FlatStyle = FlatStyle.Flat,
                BackColor = sidebarColor,
                ForeColor = muted,
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 3, 0, 3),
                Padding = new Padding(10, 0, 0, 0)
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = surfaceRaised;
            button.Click += (_, _) => ShowPage(item.Item2);
            nav.Controls.Add(button);
        }

        var status = new Label
        {
            Dock = DockStyle.Fill,
            Text = "CONTROLLER\n127.0.0.1 : 18765\n\nAuto-refresh · 5 sec",
            ForeColor = muted,
            Font = new Font("Segoe UI", 8F),
            TextAlign = ContentAlignment.BottomLeft
        };
        panel.Controls.Add(status, 0, 2);
        return panel;
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = bg,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 12)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        pageTitle.Dock = DockStyle.Fill;
        pageTitle.Text = "Overview";
        pageTitle.ForeColor = foreground;
        pageTitle.Font = new Font("Segoe UI", 23F, FontStyle.Bold);
        pageTitle.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(pageTitle, 0, 0);

        pageSubtitle.Dock = DockStyle.Fill;
        pageSubtitle.Text = "Your infrastructure, at a glance.";
        pageSubtitle.ForeColor = muted;
        pageSubtitle.Font = new Font("Segoe UI", 9.5F);
        pageSubtitle.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(pageSubtitle, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 6, 0, 0)
        };
        actions.Controls.Add(ActionButton("↻  Refresh", (_, _) => RefreshData(), false));
        actions.Controls.Add(ActionButton("+  Add machine", (_, _) => AddMachine(), true));
        header.Controls.Add(actions, 1, 0);
        header.SetRowSpan(actions, 2);
        return header;
    }

    private Button ActionButton(string caption, EventHandler click, bool primary)
    {
        var button = new Button
        {
            Text = caption,
            AutoSize = true,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(76, 48, 75) : surfaceRaised,
            ForeColor = primary ? accent : foreground,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Padding = new Padding(12, 0, 12, 0),
            Margin = new Padding(6, 2, 0, 2),
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(65, 48, 70);
        button.Click += click;
        return button;
    }

    private Label MakeLabel(string value, float size = 9F, bool bold = false, Color? color = null)
    {
        return new Label
        {
            Text = value,
            AutoSize = true,
            ForeColor = color ?? foreground,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, 3, 0, 3)
        };
    }

    private Panel MetricCard(string title, Label value, string note, Color color)
    {
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = surface,
            Padding = new Padding(16, 12, 14, 10),
            Margin = new Padding(0, 0, 10, 10),
            MinimumSize = new Size(150, 108)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        card.Controls.Add(layout);

        var heading = MakeLabel(title.ToUpperInvariant(), 8.2F, true, muted);
        heading.Dock = DockStyle.Fill;
        heading.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(heading, 0, 0);

        value.Dock = DockStyle.Fill;
        value.Text = "—";
        value.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
        value.ForeColor = color;
        value.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(value, 0, 1);

        var detail = MakeLabel(note, 8F, false, muted);
        detail.Dock = DockStyle.Fill;
        detail.AutoEllipsis = true;
        detail.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(detail, 0, 2);
        return card;
    }

    private void ShowPage(string page)
    {
        activePage = page;
        pageTitle.Text = page;
        pageSubtitle.Text = page switch
        {
            "Overview" => "Your infrastructure, at a glance.",
            "Machines" => "Inspect workers, telemetry and individual actions.",
            "Monitoring" => "Live fleet health and reported performance.",
            "Deployments" => "Agent rollout and connectivity guidance.",
            "Schedules" => "Configure CPU schedules for the worker fleet.",
            _ => "Controller configuration and local security."
        };

        stage.SuspendLayout();
        stage.Controls.Clear();
        Control view = page switch
        {
            "Overview" => OverviewPage(),
            "Machines" => MachinesPage(),
            "Monitoring" => MonitoringPage(),
            "Deployments" => DeploymentsPage(),
            "Schedules" => SchedulesPage(),
            _ => SettingsPage()
        };
        view.Dock = DockStyle.Fill;
        stage.Controls.Add(view);
        stage.ResumeLayout(true);
        RefreshData();
    }

    private Control OverviewPage()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty
        };
        for (int i = 0; i < 4; i++)
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        metrics.Controls.Add(MetricCard("Online machines", onlineValue, "Agents recently checked in", green), 0, 0);
        metrics.Controls.Add(MetricCard("Offline machines", offlineValue, "Awaiting agent check-in", red), 1, 0);
        metrics.Controls.Add(MetricCard("Average CPU limit", cpuValue, "Effective allocation", blue), 2, 0);
        metrics.Controls.Add(MetricCard("Last refresh", updatedValue, "Controller local time", accent), 3, 0);
        layout.Controls.Add(metrics, 0, 0);

        var lower = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        lower.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var section = new Panel { Dock = DockStyle.Fill, BackColor = bg };
        var sectionTitle = MakeLabel("MACHINE FLEET", 10F, true);
        sectionTitle.Dock = DockStyle.Left;
        sectionTitle.TextAlign = ContentAlignment.MiddleLeft;
        section.Controls.Add(sectionTitle);
        var add = ActionButton("+ Add machine", (_, _) => AddMachine(), false);
        add.Dock = DockStyle.Right;
        section.Controls.Add(add);
        lower.Controls.Add(section, 0, 0);

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        split.Controls.Add(BuildMachineGrid(), 0, 0);
        split.Controls.Add(SelectedPanel(), 1, 0);
        lower.Controls.Add(split, 0, 1);
        layout.Controls.Add(lower, 0, 1);
        return layout;
    }

    private Control MachinesPage()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        search.Dock = DockStyle.Fill;
        search.Margin = new Padding(0, 7, 12, 7);
        search.PlaceholderText = "Search worker or address";
        search.BackColor = surface;
        search.ForeColor = foreground;
        search.BorderStyle = BorderStyle.FixedSingle;
        search.TextChanged -= SearchChanged;
        search.TextChanged += SearchChanged;
        toolbar.Controls.Add(search, 0, 0);

        var hint = MakeLabel("Select a row to inspect it. Right-click for actions.", 8.5F, false, muted);
        hint.Dock = DockStyle.Fill;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        toolbar.Controls.Add(hint, 1, 0);

        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(BuildMachineGrid(), 0, 1);
        return layout;
    }

    private void SearchChanged(object? sender, EventArgs e) => FilterMachines();

    private DataGridView BuildMachineGrid()
    {
        machines.Dock = DockStyle.Fill;
        machines.BackgroundColor = surface;
        machines.BorderStyle = BorderStyle.None;
        machines.GridColor = border;
        machines.RowHeadersVisible = false;
        machines.AllowUserToAddRows = false;
        machines.AllowUserToResizeRows = false;
        machines.MultiSelect = false;
        machines.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        machines.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        machines.RowTemplate.Height = 38;
        machines.ColumnHeadersHeight = 38;
        machines.EnableHeadersVisualStyles = false;
        machines.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = surfaceRaised,
            ForeColor = muted,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            Padding = new Padding(8, 0, 8, 0),
            SelectionBackColor = surfaceRaised
        };
        machines.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = surface,
            ForeColor = foreground,
            SelectionBackColor = Color.FromArgb(72, 48, 76),
            SelectionForeColor = foreground,
            Padding = new Padding(8, 0, 8, 0),
            Font = new Font("Segoe UI", 8.5F)
        };
        machines.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(29, 25, 38),
            ForeColor = foreground,
            SelectionBackColor = Color.FromArgb(72, 48, 76),
            SelectionForeColor = foreground
        };

        if (machines.Columns.Count == 0)
        {
            machines.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "WORKER", Name = "worker", FillWeight = 150, MinimumWidth = 130 });
            machines.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "STATUS", Name = "status", FillWeight = 95, MinimumWidth = 85 });
            machines.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "CPU LIMIT", Name = "cpu", FillWeight = 75, MinimumWidth = 70 });
            machines.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "CPU USE", Name = "util", FillWeight = 70, MinimumWidth = 65 });
            machines.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "TEMP", Name = "temp", FillWeight = 75, MinimumWidth = 70 });
            machines.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "HASHRATE", Name = "hash", FillWeight = 90, MinimumWidth = 80 });
            machines.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "LAST SEEN", Name = "seen", FillWeight = 110, MinimumWidth = 95 });
        }

        machines.SelectionChanged -= MachineSelectionChanged;
        machines.SelectionChanged += MachineSelectionChanged;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Set CPU limit…", null, (_, _) => SendSelected("temporary_override"));
        menu.Items.Add("Get status", null, (_, _) => SendSelected("get_status"));
        menu.Items.Add("Apply pending settings", null, (_, _) => SendSelected("apply_now"));
        menu.Items.Add("Restart miner", null, (_, _) => SendSelected("restart_miner"));
        menu.Items.Add("Stop miner", null, (_, _) => SendSelected("stop_miner"));
        machines.ContextMenuStrip = menu;
        return machines;
    }

    private Control SelectedPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = surface,
            Padding = new Padding(16),
            Margin = new Padding(12, 0, 0, 0),
            MinimumSize = new Size(190, 0)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        panel.Controls.Add(layout);

        selectedName.Dock = DockStyle.Fill;
        selectedName.Text = "Select a machine";
        selectedName.ForeColor = foreground;
        selectedName.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        selectedName.AutoEllipsis = true;
        selectedName.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(selectedName, 0, 0);

        selectedState.Dock = DockStyle.Fill;
        selectedState.ForeColor = muted;
        selectedState.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        selectedState.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(selectedState, 0, 1);

        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 8, 0, 8)
        };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        AddDetailMetric(metrics, "CPU LIMIT", selectedCpu, 0, 0);
        AddDetailMetric(metrics, "TEMPERATURE", selectedTemp, 1, 0);
        AddDetailMetric(metrics, "HASHRATE", selectedHash, 0, 1);
        AddDetailMetric(metrics, "UPTIME", selectedUptime, 1, 1);
        layout.Controls.Add(metrics, 0, 2);

        layout.Controls.Add(DetailButton("Set CPU allocation", (_, _) => SendSelected("temporary_override")), 0, 3);
        layout.Controls.Add(DetailButton("Get latest status", (_, _) => SendSelected("get_status")), 0, 4);
        layout.Controls.Add(DetailButton("Apply pending settings", (_, _) => SendSelected("apply_now")), 0, 5);
        return panel;
    }

    private void AddDetailMetric(TableLayoutPanel parent, string title, Label value, int column, int row)
    {
        var cell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(3) };
        cell.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        cell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = MakeLabel(title, 7.5F, true, muted);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        cell.Controls.Add(label, 0, 0);
        value.Dock = DockStyle.Fill;
        value.Text = "—";
        value.ForeColor = foreground;
        value.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        value.AutoEllipsis = true;
        value.TextAlign = ContentAlignment.MiddleLeft;
        cell.Controls.Add(value, 0, 1);
        parent.Controls.Add(cell, column, row);
    }

    private Button DetailButton(string caption, EventHandler click)
    {
        var button = ActionButton(caption, click, false);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 3, 0, 3);
        button.TextAlign = ContentAlignment.MiddleLeft;
        return button;
    }

    private Control MonitoringPage()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        for (int i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        var online = new Label(); var offline = new Label(); var cpu = new Label(); var updated = new Label();
        cards.Controls.Add(MetricCard("Online", online, "Checking in", green), 0, 0);
        cards.Controls.Add(MetricCard("Offline", offline, "No recent check-in", red), 1, 0);
        cards.Controls.Add(MetricCard("Average CPU", cpu, "Reported allocation", blue), 2, 0);
        cards.Controls.Add(MetricCard("Updated", updated, "Local time", accent), 3, 0);
        layout.Controls.Add(cards, 0, 0);

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = surface,
            ForeColor = foreground,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9F)
        };
        list.Columns.Add("STATUS", 110);
        list.Columns.Add("WORKER", 240);
        list.Columns.Add("LAST CHECK-IN", 190);
        layout.Controls.Add(list, 0, 1);

        void Update()
        {
            var states = hosts.Select(h => (Host: h, State: api.GetHostState(h))).ToList();
            online.Text = states.Count(x => x.State.Online).ToString();
            offline.Text = states.Count(x => !x.State.Online).ToString();
            var samples = hosts.Select(h => api.TryStatus(h, out var s) && s.TryGetProperty("effective_percent", out var v)
                ? (double?)v.GetDouble() : null).Where(x => x.HasValue).Select(x => x!.Value).ToList();
            cpu.Text = samples.Count == 0 ? "—" : $"{samples.Average():0}%";
            updated.Text = DateTime.Now.ToString("HH:mm:ss");

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var h in hosts)
            {
                var state = api.GetHostState(h);
                var seen = api.Checkins.TryGetValue(h, out var time) ? time.ToLocalTime().ToString("g") : "Never";
                var item = new ListViewItem(state.Online ? "ONLINE" : "OFFLINE");
                item.ForeColor = state.Online ? green : red;
                item.SubItems.Add(h);
                item.SubItems.Add(seen);
                list.Items.Add(item);
            }
            list.EndUpdate();
        }

        layout.Tag = (Action)Update;
        Update();
        return layout;
    }

    private Control DeploymentsPage()
    {
        return InfoPage(
            "AGENT DEPLOYMENT",
            "Deploy agents using the repository's deployment tooling. Review SSH host keys and target inventory before running a rollout.",
            "RECOMMENDED WORKFLOW",
            "1. Verify each target's hostname and SSH fingerprint.\n2. Run deployment/deploy.py from a trusted administration machine.\n3. Confirm the agent service is enabled and healthy.\n4. Confirm the worker appears in Machines and reports telemetry.",
            "SAFETY",
            "Fleet commands are queued until an agent checks in. Review the inventory before issuing broad actions.");
    }

    private Control SettingsPage()
    {
        return InfoPage(
            "CONTROLLER SETTINGS",
            "The controller API listens on 127.0.0.1:18765. It is intentionally bound to loopback and should not be exposed directly to the internet.",
            "AUTHENTICATION",
            "Set MEOWDEPLOY_TOKEN to a random secret of at least 32 characters. Configure the same value on every agent.",
            "LOCAL DATA",
            $"Worker inventory: {serverFile}\nPending commands: %APPDATA%\\MeowDeploy\\pending-commands.json");
    }

    private Control InfoPage(string heading, string intro, string firstTitle, string firstBody, string secondTitle, string secondBody)
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = bg, Padding = new Padding(2) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(18),
            BackColor = surface,
            Margin = new Padding(0, 0, 12, 0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(InfoHeading(heading, 13F, accent), 0, 0);
        layout.Controls.Add(WrappedText(intro, 10F, muted, 600), 0, 1);
        layout.Controls.Add(InfoHeading(firstTitle, 10.5F, foreground), 0, 2);
        layout.Controls.Add(WrappedText(firstBody, 9.5F, foreground, 600), 0, 3);
        layout.Controls.Add(InfoHeading(secondTitle, 10.5F, foreground), 0, 4);
        layout.Controls.Add(WrappedText(secondBody, 9.5F, foreground, 600), 0, 5);
        for (int i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scroll.Controls.Add(layout);
        return scroll;
    }

    private Label InfoHeading(string text, float size, Color color)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = color,
            Font = new Font("Segoe UI", size, FontStyle.Bold),
            Margin = new Padding(0, 8, 0, 8)
        };
    }

    private Label WrappedText(string text, float size, Color color, int maxWidth)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(maxWidth, 0),
            ForeColor = color,
            Font = new Font("Segoe UI", size),
            Margin = new Padding(0, 0, 0, 18)
        };
    }

    private Control SchedulesPage()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var intro = MakeLabel("Daily CPU allocation windows · Europe/Dublin", 9F, false, muted);
        intro.Dock = DockStyle.Fill;
        intro.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(intro, 0, 0);

        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = surface,
            BorderStyle = BorderStyle.None,
            GridColor = border,
            AllowUserToAddRows = true,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 38,
            RowTemplate = { Height = 36 }
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = surfaceRaised,
            ForeColor = muted,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = surface,
            ForeColor = foreground,
            SelectionBackColor = Color.FromArgb(72, 48, 76),
            SelectionForeColor = foreground,
            Padding = new Padding(8, 0, 8, 0)
        };
        grid.Columns.Add("start", "START (HH:mm)");
        grid.Columns.Add("end", "END (HH:mm)");
        grid.Columns.Add("percent", "CPU LIMIT (%)");
        grid.Rows.Add("00:00", "06:00", "75");
        grid.Rows.Add("06:00", "17:00", "35");
        grid.Rows.Add("17:00", "18:00", "50");
        grid.Rows.Add("18:00", "00:00", "60");
        layout.Controls.Add(grid, 0, 1);

        var apply = ActionButton("Queue schedule for all workers", (_, _) =>
        {
            var intervals = new List<object>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                var start = row.Cells[0].Value?.ToString();
                var end = row.Cells[1].Value?.ToString();
                if (!TimeSpan.TryParse(start, out _) || !TimeSpan.TryParse(end, out _) ||
                    !double.TryParse(row.Cells[2].Value?.ToString(), out var pct) || pct is < 0 or > 100)
                {
                    MessageBox.Show("Check every schedule row. Use valid HH:mm times and a CPU limit from 0 to 100.",
                        "Invalid schedule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                intervals.Add(new { start, end, percent = pct });
            }

            if (intervals.Count == 0)
            {
                MessageBox.Show("Add at least one valid interval.");
                return;
            }
            if (MessageBox.Show($"Queue this schedule for all {hosts.Count} configured workers?",
                "Confirm fleet schedule", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            foreach (var host in hosts)
                api.Queue(host, "set_schedule", new { timezone = "Europe/Dublin", intervals });
            footer.Text = $"Schedule queued for {hosts.Count} workers.";
        }, true);
        apply.Dock = DockStyle.Right;
        apply.Margin = new Padding(0, 7, 0, 0);
        layout.Controls.Add(apply, 0, 2);
        return layout;
    }

    private void RefreshData()
    {
        if (IsDisposed || Disposing) return;

        var snapshot = hosts.Select(h => (Host: h, Online: api.GetHostState(h).Online)).ToList();
        if (activePage == "Overview")
        {
            onlineValue.Text = snapshot.Count(x => x.Online).ToString();
            offlineValue.Text = snapshot.Count(x => !x.Online).ToString();
            var cpus = hosts.Select(h => api.TryStatus(h, out var s) &&
                s.TryGetProperty("effective_percent", out var v) ? (double?)v.GetDouble() : null)
                .Where(x => x.HasValue).Select(x => x!.Value).ToList();
            cpuValue.Text = cpus.Count == 0 ? "—" : $"{cpus.Average():0}%";
            updatedValue.Text = DateTime.Now.ToString("HH:mm:ss");
        }

        if (machines.Columns.Count > 0)
        {
            var current = selectedHost;
            machines.Rows.Clear();
            foreach (var host in hosts)
            {
                var online = api.GetHostState(host).Online;
                string ReadMetric(string property, string format)
                {
                    if (!api.TryStatus(host, out var status) ||
                        !status.TryGetProperty("metrics", out var metrics) ||
                        !metrics.TryGetProperty(property, out var value) ||
                        value.ValueKind != JsonValueKind.Number)
                        return "—";
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, value.GetDouble());
                }

                var cpu = api.TryStatus(host, out var st) &&
                    st.TryGetProperty("effective_percent", out var effective) ? $"{effective.GetDouble():0}%" : "—";
                var seen = api.Checkins.TryGetValue(host, out var time) ? time.ToLocalTime().ToString("g") : "Never";
                machines.Rows.Add(host, online ? "● Online" : "○ Offline", cpu,
                    ReadMetric("cpu_utilization_percent", "{0:0}%"),
                    ReadMetric("temperature_c", "{0:0.0} °C"),
                    ReadMetric("hashrate", "{0:0.##} H/s"), seen);

                var row = machines.Rows[machines.Rows.Count - 1];
                row.Cells["status"].Style.ForeColor = online ? green : red;
                if (host == current) row.Selected = true;
            }

            if (machines.Rows.Count > 0 && machines.SelectedRows.Count == 0)
                machines.Rows[0].Selected = true;
            FilterMachines();
        }

        if (activePage == "Monitoring" && stage.Controls.Count > 0 &&
            stage.Controls[0] is TableLayoutPanel layout && layout.Tag is Action update)
            update();

        UpdateSelected();
    }

    private void MachineSelectionChanged(object? sender, EventArgs e)
    {
        if (machines.CurrentRow?.Cells["worker"].Value is string host)
        {
            selectedHost = host;
            UpdateSelected();
        }
    }

    private void UpdateSelected()
    {
        if (selectedHost.Length == 0) return;

        selectedName.Text = selectedHost;
        var online = api.GetHostState(selectedHost).Online;
        selectedState.Text = online ? "●  ONLINE" : "●  OFFLINE";
        selectedState.ForeColor = online ? green : red;

        if (!api.TryStatus(selectedHost, out var status))
        {
            selectedCpu.Text = selectedTemp.Text = selectedHash.Text = selectedUptime.Text = "No telemetry";
            return;
        }

        selectedCpu.Text = status.TryGetProperty("effective_percent", out var cpu) ? $"{cpu.GetDouble():0}%" : "—";
        string Metric(string key, string suffix) =>
            status.TryGetProperty("metrics", out var metrics) &&
            metrics.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
                ? $"{value.GetDouble():0.##}{suffix}" : "—";

        selectedTemp.Text = Metric("temperature_c", " °C");
        selectedHash.Text = Metric("hashrate", " H/s");
        selectedUptime.Text = Metric("uptime_seconds", " sec");
    }

    private void FilterMachines()
    {
        var query = search.Text.Trim();
        foreach (DataGridViewRow row in machines.Rows)
        {
            var match = query.Length == 0 ||
                row.Cells["worker"].Value?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
            if (row.Visible != match) row.Visible = match;
        }
    }

    private void SendSelected(string command)
    {
        var host = selectedHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("Select a worker first.");
            return;
        }

        object payload = command == "temporary_override"
            ? new { percent = PromptCpu(), duration_seconds = 3600 }
            : new { };

        if (MessageBox.Show($"Queue '{command.Replace('_', ' ')}' for {host}?",
            "Confirm command", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        api.Queue(host, command, payload);
        footer.Text = $"Command queued for {host}.";
    }

    private double PromptCpu()
    {
        using var dialog = new Form
        {
            Text = "CPU allocation",
            Width = 330,
            Height = 180,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = surface,
            ForeColor = foreground,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = MakeLabel("CPU allocation percentage", 9F, true);
        label.Dock = DockStyle.Fill;
        layout.Controls.Add(label, 0, 0);
        var numeric = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Value = 35,
            Dock = DockStyle.Left,
            Width = 120,
            BackColor = surfaceRaised,
            ForeColor = foreground
        };
        layout.Controls.Add(numeric, 0, 1);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var ok = ActionButton("Queue", (_, _) => { dialog.DialogResult = DialogResult.OK; dialog.Close(); }, true);
        var cancel = ActionButton("Cancel", (_, _) => { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }, false);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        return dialog.ShowDialog(this) == DialogResult.OK ? (double)numeric.Value : 35;
    }

    private void AddMachine()
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox(
            "Enter worker name, IP address, or agent ID:", "Add machine", "").Trim();
        if (name.Length == 0 || hosts.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;

        hosts.Add(name);
        SaveHosts();
        RefreshData();
        footer.Text = $"Added {name}.";
    }

    private List<string> LoadHosts()
    {
        try
        {
            if (File.Exists(serverFile))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(serverFile)) ?? new();
        }
        catch { }
        return Enumerable.Range(3, 18).Select(i => $"172.16.3.{i}").ToList();
    }

    private void SaveHosts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(serverFile)!);
        File.WriteAllText(serverFile, JsonSerializer.Serialize(hosts, new JsonSerializerOptions { WriteIndented = true }));
    }
}

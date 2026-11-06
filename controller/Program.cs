using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowDeploy.Controller;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var api = new ControllerApi();
        api.Start();
        Application.Run(new Dashboard(api));
    }
}

// ----------------------------------------------------------------------
// HTTP control-plane: HMAC-authenticated, localhost-only, command allowlist
// lives in the agent itself. This class only queues/returns JSON payloads.
// ----------------------------------------------------------------------
public sealed class ControllerApi : IDisposable
{
    readonly HttpListener listener = new();
    readonly ConcurrentDictionary<string, ConcurrentQueue<Command>> queues = new();
    readonly ConcurrentDictionary<string, DateTime> checkins = new();
    readonly Dictionary<string, HostState> hostStates = new(StringComparer.OrdinalIgnoreCase);
    readonly object stateGate = new();
    static readonly TimeSpan OnlineTimeout = TimeSpan.FromMinutes(2);
    readonly ConcurrentDictionary<string, JsonElement> statuses = new();
    readonly string token = Environment.GetEnvironmentVariable("MEOWDEPLOY_TOKEN") ?? "meowmeowmeow";
    readonly CancellationTokenSource stop = new();

    public ControllerApi() => listener.Prefixes.Add("http://127.0.0.1:18765/");

    public void Start()
    {
        listener.Start();
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var context = await listener.GetContextAsync();
                _ = Task.Run(() => Handle(context));
            }
            catch { /* listener stopping */ }
        }
    }

    async Task Handle(HttpListenerContext context)
    {
        using var stream = new MemoryStream();
        await context.Request.InputStream.CopyToAsync(stream);
        var raw = stream.ToArray();

        if (!Auth(context.Request, raw))
        {
            context.Response.StatusCode = 401;
            context.Response.Close();
            return;
        }

        var data = JsonSerializer.Deserialize<JsonElement>(raw);
        var id = data.GetProperty("agent_id").GetString()!;
        var checkedInAt = DateTime.UtcNow;
        lock (stateGate)
        {
            var wasOnline = checkins.TryGetValue(id, out var previousCheckin)
                && checkedInAt - previousCheckin < OnlineTimeout;
            checkins[id] = checkedInAt;
            if (!wasOnline || !hostStates.ContainsKey(id))
                hostStates[id] = new HostState(true, checkedInAt);
        }
        if (data.TryGetProperty("status", out var status)) statuses[id] = status;

        object result = context.Request.Url!.AbsolutePath switch
        {
            "/api/poll" => new { commands = Drain(id) },
            "/api/ack" => new { ok = true },
            _ => new { error = "not found" }
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    bool Auth(HttpListenerRequest request, byte[] raw)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        var expected = Convert.ToHexString(h.ComputeHash(raw)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(request.Headers["X-MeowDeploy-Signature"] ?? ""),
            Encoding.ASCII.GetBytes(expected));
    }

    object[] Drain(string id)
    {
        var q = queues.GetOrAdd(id, _ => new());
        var items = new List<Command>();
        while (q.TryDequeue(out var item)) items.Add(item);
        return items.Cast<object>().ToArray();
    }

    public void Queue(string id, string name, object payload) =>
        queues.GetOrAdd(id, _ => new()).Enqueue(new Command(Guid.NewGuid().ToString("N"), name, payload));

    public bool TryStatus(string id, out JsonElement status) => statuses.TryGetValue(id, out status);

    public bool IsOnline(string id) => GetHostState(id).Online;

    // Tracks the time the observable online/offline state changed. Offline
    // begins exactly at the two-minute check-in timeout, even if the UI refreshes later.
    public HostState GetHostState(string id)
    {
        lock (stateGate)
        {
            var now = DateTime.UtcNow;
            var hasCheckin = checkins.TryGetValue(id, out var lastCheckin);
            var online = hasCheckin && now - lastCheckin < OnlineTimeout;

            if (!hostStates.TryGetValue(id, out var previous))
            {
                var since = online ? lastCheckin : hasCheckin ? lastCheckin + OnlineTimeout : now;
                previous = new HostState(online, since);
                hostStates[id] = previous;
            }
            else if (previous.Online != online)
            {
                var since = online ? lastCheckin : hasCheckin ? lastCheckin + OnlineTimeout : now;
                previous = new HostState(online, since);
                hostStates[id] = previous;
            }

            return previous;
        }
    }

    public IReadOnlyDictionary<string, DateTime> Checkins => checkins;
    public readonly record struct HostState(bool Online, DateTime SinceUtc);

    public record Command(string id, string name, object payload);

    public void Stop()
    {
        stop.Cancel();
        listener.Stop();
    }

    public void Dispose() => Stop();
}

// ----------------------------------------------------------------------
// Theme — one accent color reserved for interactive/brand use, plus
// semantic status colors so severity reads at a glance.
// ----------------------------------------------------------------------
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(17, 15, 22);
    public static readonly Color Surface = Color.FromArgb(28, 25, 35);
    public static readonly Color Surface2 = Color.FromArgb(39, 34, 47);
    public static readonly Color Rail = Color.FromArgb(22, 19, 29);

    public static readonly Color Accent = Color.FromArgb(232, 169, 212);   // brand pink — interactive only
    public static readonly Color Text = Color.White;
    public static readonly Color Muted = Color.FromArgb(166, 153, 174);
    public static readonly Color Dim = Color.FromArgb(110, 100, 120);      // empty-state values

    public static readonly Color Good = Color.FromArgb(110, 215, 160);     // online / healthy
    public static readonly Color Warn = Color.FromArgb(235, 190, 110);     // degraded / partial
    public static readonly Color Bad = Color.FromArgb(235, 120, 135);      // offline / error
    public static readonly Color Info = Color.FromArgb(140, 170, 235);     // neutral metric

    public const int Space = 8;   // base spacing unit — multiples of this only
}

// A Panel that paints itself through an offscreen buffer. Plain WinForms
// Panels + transparent Labels whose text changes on a Timer (stat cards,
// the header clock) leave stale pixels behind on repaint — this is the
// "crunched / overlapping" ghosting seen in the dashboard. Using this
// everywhere a Timer mutates a child Label's text fixes it at the root.
class DoubleBufferedPanel : Panel
{
    public DoubleBufferedPanel() => DoubleBuffered = true;
}

public sealed class Dashboard : Form
{
    readonly ControllerApi api;
    readonly DataGridView grid = new();
    readonly Label summary = new();
    readonly Label detailsTitle = new();
    readonly FlowLayoutPanel detailsBody = new();
    readonly TextBox search = new();
    readonly ListBox history = new();
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 30_000 };
    readonly string serverFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowDeploy", "servers.json");
    readonly List<string> hosts;
    TabControl? navigationTabs;

    Label onlineValue = new(), offlineValue = new(), avgCpuValue = new(), lastSyncValue = new();
    Label totalHashrateValue = new(), fleetCpuValue = new(), fleetTempValue = new(), fleetUptimeValue = new();
    HashrateBarChart? hashrateChart;

    public Dashboard(ControllerApi api)
    {
        this.api = api;
        hosts = LoadHosts();

        Text = "MeowDeploy";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Width = 1450;
        Height = 860;
        MinimumSize = new Size(1180, 720);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Bg };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);
        root.Controls.Add(Rail(), 0, 0);
        root.Controls.Add(Workspace(), 1, 0);
        EnsureMetricColumns();

        grid.SelectionChanged += (_, _) => UpdateDetails();
        search.TextChanged += (_, _) => FilterRows();
        Shown += (_, _) =>
        {
            LoadRows();
            refresh.Tick += (_, _) => RefreshRows();
            refresh.Start();
        };
    }

    // ------------------------------------------------------------------
    // Left rail
    // ------------------------------------------------------------------
    Control Rail()
    {
        var rail = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Rail, Padding = new Padding(18, 24, 18, 18) };

        rail.Controls.Add(new Label
        {
            Text = "◈  MEOW\n    DEPLOY",
            ForeColor = Theme.Accent,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 72
        });

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0, 25, 0, 0)
        };
        var labels = new[] { "▦   Dashboard", "▤   Servers", "◷   Schedules", "⇩   Deployment", "⚙   Settings" };
        for (var i = 0; i < labels.Length; i++) nav.Controls.Add(Nav(labels[i], i));
        rail.Controls.Add(nav);

        rail.Controls.Add(new Label
        {
            Text = "MEOWDEPLOY\nController  •  Local API\n\nTor transport ready",
            ForeColor = Theme.Muted,
            Dock = DockStyle.Bottom,
            Height = 92
        });

        return rail;
    }

    Button Nav(string text, int pageIndex) => new Button
    {
        Text = text,
        Width = 174,
        Height = 44,
        TextAlign = ContentAlignment.MiddleLeft,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.Transparent,
        ForeColor = Theme.Muted,
        Font = new Font("Segoe UI", 10),
        Cursor = Cursors.Hand,
    }.Also(b =>
    {
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => { if (navigationTabs is not null) navigationTabs.SelectedIndex = pageIndex; };
        b.MouseEnter += (_, _) => b.ForeColor = Theme.Accent;
        b.MouseLeave += (_, _) => b.ForeColor = Theme.Muted;
    });

    // ------------------------------------------------------------------
    // Main workspace
    // ------------------------------------------------------------------
    Control Workspace()
    {
        var work = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(28, 24, 28, 22) };
        work.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        work.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        work.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        work.Controls.Add(Header(), 0, 0);
        work.Controls.Add(StatCards(), 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 67));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        body.Controls.Add(ServerPanel(), 0, 0);
        body.Controls.Add(DetailPanel(), 1, 0);

        navigationTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.FlatButtons,
            ItemSize = new Size(110, 30),
            SizeMode = TabSizeMode.Fixed
        };

        var dashboardTab = new TabPage("Dashboard") { BackColor = Theme.Bg, Padding = new Padding(0) };
        dashboardTab.Controls.Add(body);

        var serversTab = new TabPage("Servers") { BackColor = Theme.Bg };
        serversTab.Controls.Add(BuildFleetStatsPage());

        var scheduleTab = new TabPage("Schedules") { BackColor = Theme.Bg };
        scheduleTab.Controls.Add(BuildScheduleEditor());

        var deployTab = new TabPage("Deployment") { BackColor = Theme.Bg };
        deployTab.Controls.Add(InfoLabel("Deployment\n\nRun deployment/deploy.py after reviewing SSH host keys. Results appear in the terminal and can be audited per host."));

        var settingsTab = new TabPage("Settings") { BackColor = Theme.Bg };
        settingsTab.Controls.Add(InfoLabel("Settings\n\nController API: 127.0.0.1:18765\nAuthentication: HMAC token\nDefault test token: meowmeowmeow"));

        navigationTabs.TabPages.AddRange(new[] { dashboardTab, serversTab, scheduleTab, deployTab, settingsTab });
        navigationTabs.Appearance = TabAppearance.FlatButtons;
        navigationTabs.ItemSize = new Size(0, 1);
        navigationTabs.SizeMode = TabSizeMode.Fixed;
        navigationTabs.Multiline = true;
        work.Controls.Add(navigationTabs, 0, 2);
        return work;
    }

    // Fleet-wide view: what the dead "Servers" placeholder used to be.
    // Shows the combined output of every host rather than making you add
    // up per-host numbers yourself.
    Control BuildFleetStatsPage()
    {
        var page = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(28) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "Combined fleet output", ForeColor = Theme.Text, BackColor = Theme.Bg, Font = new Font("Segoe UI", 16, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Fill }, 0, 0);

        var cards = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        cards.Controls.Add(Card("TOTAL HASHRATE", out totalHashrateValue, "sum across online hosts", Theme.Accent));
        cards.Controls.Add(Card("FLEET CPU", out fleetCpuValue, "average effective allocation", Theme.Info));
        cards.Controls.Add(Card("FLEET TEMP", out fleetTempValue, "average across reporting hosts", Theme.Warn));
        cards.Controls.Add(Card("LONGEST UPTIME", out fleetUptimeValue, "best currently-online host", Theme.Good));
        layout.Controls.Add(cards, 0, 1);

        var chartPanel = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(20) };
        var chartTitle = new Label { Text = "Hashrate by host", ForeColor = Theme.Muted, BackColor = Theme.Surface, AutoSize = true, Dock = DockStyle.Top };
        hashrateChart = new HashrateBarChart { Dock = DockStyle.Fill };
        chartPanel.Controls.Add(hashrateChart);
        chartPanel.Controls.Add(chartTitle);
        layout.Controls.Add(chartPanel, 0, 2);

        page.Controls.Add(layout);
        return page;
    }

    // Recomputes the aggregate cards + chart from whatever RefreshRows()
    // just pulled out of the grid. Called at the end of RefreshRows so the
    // stats page is never more than one tick stale.
    void RefreshFleetStats(List<(string Host, bool Online, double? Hashrate, double? Temp, TimeSpan? OnlineFor)> snapshot, string avgCpuText)
    {
        var onlineHosts = snapshot.Where(h => h.Online).ToList();
        var hashrates = onlineHosts.Where(h => h.Hashrate is > 0).ToList();
        var temps = onlineHosts.Where(h => h.Temp is not null).ToList();

        SetCard(totalHashrateValue, hashrates.Count > 0 ? $"{hashrates.Sum(h => h.Hashrate!.Value):0.##} H/s" : "—", Theme.Accent);
        SetCard(fleetTempValue, temps.Count > 0 ? $"{temps.Average(h => h.Temp!.Value):0.0} °C" : "—", Theme.Warn);
        SetCard(fleetCpuValue, avgCpuText, Theme.Info);

        var longest = onlineHosts.Where(h => h.OnlineFor is not null).OrderByDescending(h => h.OnlineFor).FirstOrDefault();
        SetCard(fleetUptimeValue, longest.OnlineFor is { } span ? FormatDuration(span) : "—", Theme.Good);

        hashrateChart?.SetData(onlineHosts.Select(h => (h.Host, h.Hashrate ?? 0)).ToList());
    }

    static Label InfoLabel(string text) => new()
    {
        Text = text,
        ForeColor = Theme.Muted,
        Font = new Font("Segoe UI", 12),
        Dock = DockStyle.Fill,
        Padding = new Padding(28)
    };

    Control Header()
    {
        // DoubleBufferedPanel, not Panel: summary.Text changes every refresh
        // tick, and a plain Panel doesn't clear the old string's pixels first.
        var head = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

        // Stacked via a FlowLayoutPanel instead of manual Point(2, 38) —
        // that fixed offset is what let the title and summary overlap
        // whenever either one's rendered height didn't match the guess.
        var titleStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Bg
        };
        titleStack.Controls.Add(new Label
        {
            Text = "Fleet overview",
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Theme.Bg,
            AutoSize = true,
            Margin = new Padding(0)
        });

        summary.Text = "Fleet ready";
        summary.ForeColor = Theme.Muted;
        summary.BackColor = Theme.Bg;   // explicit, not transparent — nothing left to ghost
        summary.AutoSize = true;
        summary.Margin = new Padding(2, 4, 0, 0);
        titleStack.Controls.Add(summary);
        head.Controls.Add(titleStack);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };

        var speedLabel = new Label { Text = "Refresh", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(8, 10, 3, 0) };
        var speed = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 90,
            BackColor = Theme.Surface2,
            ForeColor = Theme.Text,
            Margin = new Padding(2, 3, 8, 0)
        };
        speed.Items.AddRange(new object[] { "10 sec", "20 sec", "30 sec", "1 min" });
        speed.SelectedIndex = 2;
        speed.SelectedIndexChanged += (_, _) =>
            refresh.Interval = speed.SelectedIndex switch { 0 => 10_000, 1 => 20_000, 2 => 30_000, _ => 60_000 };

        actions.Controls.Add(speedLabel);
        actions.Controls.Add(speed);
        actions.Controls.Add(Action("＋ Add server", (_, _) => AddServer()));
        actions.Controls.Add(Action("Fleet CPU", (_, _) => FleetOverride()));
        actions.Controls.Add(Action("Clear all", (_, _) => FleetCommand("clear_override")));
        actions.Controls.Add(Action("Restart all", (_, _) => FleetCommand("restart_miner")));
        actions.Controls.Add(Action("↻ Sync", (_, _) => RefreshRows()));
        head.Controls.Add(actions);

        return head;
    }

    // ------------------------------------------------------------------
    // Stat cards — color now carries severity: offline hosts read as a
    // warning, not as the same "just a stat" gray as everything else.
    // ------------------------------------------------------------------
    Control StatCards()
    {
        var cards = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(0, 0, 0, Theme.Space / 2) };
        cards.Controls.Add(Card("ONLINE", out onlineValue, "hosts responding", Theme.Good));
        cards.Controls.Add(Card("OFFLINE", out offlineValue, "awaiting check-in", Theme.Bad));
        cards.Controls.Add(Card("AVG CPU", out avgCpuValue, "effective allocation", Theme.Info));
        cards.Controls.Add(Card("LAST SYNC", out lastSyncValue, "controller time", Theme.Muted));
        return cards;
    }

    Panel Card(string label, out Label valueLabel, string hint, Color color)
    {
        // DoubleBufferedPanel: this is exactly the control whose value
        // Label gets rewritten on every refresh tick, so it's the one
        // most responsible for the garbled "LAST SYNC" digits.
        var p = new DoubleBufferedPanel { Width = 190, Height = 92, BackColor = Theme.Surface, Margin = new Padding(0, 0, Theme.Space * 2, 0), Padding = new Padding(Theme.Space * 2) };
        p.Paint += (_, e) => { using var pen = new Pen(color, 3); e.Graphics.DrawLine(pen, 0, 0, 0, p.Height); };

        var value = new Label { Text = "—", ForeColor = Theme.Dim, BackColor = Theme.Surface, Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top };
        p.Controls.Add(new Label { Text = hint, ForeColor = Theme.Muted, BackColor = Theme.Surface, AutoEllipsis = true, AutoSize = false, Dock = DockStyle.Bottom, Height = 20 });
        p.Controls.Add(value);
        p.Controls.Add(new Label { Text = label, ForeColor = Theme.Muted, BackColor = Theme.Surface, AutoSize = true, Dock = DockStyle.Top });

        valueLabel = value;
        return p;
    }

    // Sets a card's value and recolors it — real data gets the card's
    // severity color, "no data yet" always renders in the same dim gray
    // so empty state never masquerades as a real reading. Pulses the
    // value briefly when it actually changes, so a live update reads as
    // alive rather than the UI just sitting there static.
    static void SetCard(Label label, string? text, Color activeColor)
    {
        var hasValue = !string.IsNullOrEmpty(text) && text != "—";
        var next = hasValue ? text! : "—";
        var changed = label.Text != next;
        label.Text = next;
        label.Invalidate(); // force a clean repaint so old glyphs don't ghost through under the DoubleBufferedPanel parent
        label.ForeColor = hasValue ? activeColor : Theme.Dim;
        if (changed && hasValue) Pulse(label, activeColor);
    }

    // Lightweight flash-and-settle animation: brightens a label toward
    // white then eases back to its target color over ~450ms. No extra
    // libraries — just a Timer stepping a lerp, which is the standard
    // way to animate in WinForms since controls don't have real opacity.
    static void Pulse(Label label, Color settleColor)
    {
        const int steps = 15;
        var i = 0;
        var t = new System.Windows.Forms.Timer { Interval = 30 };
        t.Tick += (_, _) =>
        {
            i++;
            if (i >= steps || label.IsDisposed) { label.ForeColor = settleColor; t.Stop(); t.Dispose(); return; }
            var frac = 1.0 - (i / (double)steps);
            label.ForeColor = Lerp(Color.White, settleColor, 1 - frac);
        };
        t.Start();
    }

    static Color Lerp(Color a, Color b, double f) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));

    Button Action(string text, EventHandler click)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface2,
            ForeColor = Theme.Accent,
            Padding = new Padding(12, 0, 12, 0),
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, Theme.Space, 0)
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(75, 61, 84);
        b.Click += click;
        return b;
    }

    // ------------------------------------------------------------------
    // Server table — host column is now fixed-width with a tooltip
    // instead of being squeezed to "172.16.3....", and offline/empty
    // cells get a visibly distinct dim style instead of a bare dash.
    // ------------------------------------------------------------------
    readonly ToolTip hostTip = new();

    Control ServerPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(Theme.Space * 2) };

        var top = new Panel { Dock = DockStyle.Top, Height = 48 };
        top.Controls.Add(new Label { Text = "Servers", Font = new Font("Segoe UI", 13, FontStyle.Bold), AutoSize = true, Location = new Point(0, 7) });
        search.PlaceholderText = "Search address";
        search.Width = 230;
        search.Dock = DockStyle.Right;
        search.BackColor = Theme.Surface2;
        search.ForeColor = Theme.Text;
        top.Controls.Add(search);
        p.Controls.Add(top);

        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Theme.Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoGenerateColumns = false;
        grid.RowTemplate.Height = 38;
        grid.ColumnHeadersHeight = 36;
        grid.ShowCellToolTips = true;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface2,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            WrapMode = DataGridViewTriState.False
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            SelectionBackColor = Color.FromArgb(73, 52, 77),
            SelectionForeColor = Theme.Text,
            Padding = new Padding(6, 0, 6, 0),
            WrapMode = DataGridViewTriState.False
        };

        // Host column: fixed width, never collapses to "172.16.3....".
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Host",
            Name = "host",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = 150,
            MinimumWidth = 150
        });
        foreach (var c in new[] { ("State", "state"), ("State duration", "duration"), ("CPU", "cpu"), ("Agent", "agent"), ("Last check-in", "checkin") })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Item1, Name = c.Item2, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 88 });

        var menu = new ContextMenuStrip();
        foreach (var item in new[] { ("Set CPU…", "temporary_override"), ("Start miner", "start_miner"), ("Stop miner", "stop_miner"), ("Restart miner", "restart_miner"), ("Clear override", "clear_override") })
        {
            var mi = menu.Items.Add(item.Item1);
            mi.Click += (_, _) => SendSelected(item.Item2);
        }
        grid.ContextMenuStrip = menu;

        p.Controls.Add(grid);
        grid.BringToFront();
        return p;
    }

    void EnsureMetricColumns()
    {
        foreach (var c in new[] { ("CPU use", "util"), ("Temp", "temp"), ("Hashrate", "hashrate") })
            if (!grid.Columns.Contains(c.Item2))
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Item1, Name = c.Item2, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 70 });
    }

    void LoadRows()
    {
        foreach (var host in hosts) grid.Rows.Add(host, "○ Offline", "—", "—", "—", "Never", "—", "—", "—");
        ApplyEmptyStateStyle();
        RefreshRows();
    }

    // Dash cells render dim/italic so "no data" is visibly different
    // from an actual reading, instead of identical gray text everywhere.
    void ApplyEmptyStateStyle()
    {
        foreach (DataGridViewRow row in grid.Rows)
            foreach (DataGridViewCell cell in row.Cells)
                if (cell.OwningColumn.Name != "host" && cell.OwningColumn.Name != "state" && (cell.Value?.ToString() == "—"))
                    cell.Style = new DataGridViewCellStyle(cell.Style) { ForeColor = Theme.Dim, Font = new Font(grid.Font, FontStyle.Italic) };
    }

    void RefreshRows()
    {
        var onlineCount = 0;
        var cpuSamples = new List<double>();
        var fleetSnapshot = new List<(string Host, bool Online, double? Hashrate, double? Temp, TimeSpan? OnlineFor)>();

        for (var i = 0; i < grid.Rows.Count; i++)
        {
            var row = grid.Rows[i];
            var host = row.Cells["host"].Value?.ToString() ?? "";
            row.Cells["host"].ToolTipText = host;

            var hostState = api.GetHostState(host);
            var online = hostState.Online;
            if (online) onlineCount++;

            row.Cells["state"].Value = online ? "● Online" : "○ Offline";
            row.Cells["duration"].Value = $"{(online ? "Online for " : "Offline for ")}{FormatDuration(DateTime.UtcNow - hostState.SinceUtc)}";
            row.Cells["duration"].Style.ForeColor = online ? Theme.Good : Theme.Bad;
            row.Cells["state"].Style.ForeColor = online ? Theme.Good : Theme.Bad;
            row.Cells["state"].Style.Font = new Font(grid.Font, FontStyle.Bold);

            SetCell(row, "cpu", "—", Theme.Text);
            SetCell(row, "agent", "—", Theme.Text);
            SetCell(row, "temp", "—", Theme.Text);
            SetCell(row, "hashrate", "—", Theme.Text);
            SetCell(row, "util", "—", Theme.Text);

            double? rowHashrate = null, rowTemp = null;
            if (api.TryStatus(host, out var s))
            {
                if (s.TryGetProperty("effective_percent", out var cpu))
                {
                    SetCell(row, "cpu", $"{cpu.GetDouble():0}%", Theme.Text);
                    cpuSamples.Add(cpu.GetDouble());
                }
                if (s.TryGetProperty("version", out var v)) SetCell(row, "agent", v.GetString() ?? "—", Theme.Text);
                if (s.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    if (m.TryGetProperty("temperature_c", out var temp) && temp.ValueKind == JsonValueKind.Number)
                    {
                        SetCell(row, "temp", $"{temp.GetDouble():0.0} °C", Theme.Text);
                        rowTemp = temp.GetDouble();
                    }
                    if (m.TryGetProperty("hashrate", out var hr) && hr.ValueKind == JsonValueKind.Number)
                    {
                        SetCell(row, "hashrate", $"{hr.GetDouble():0.##} H/s", Theme.Text);
                        rowHashrate = hr.GetDouble();
                    }
                    if (m.TryGetProperty("cpu_utilization_percent", out var u) && u.ValueKind == JsonValueKind.Number)
                        SetCell(row, "util", $"{u.GetDouble():0}%", Theme.Text);
                }
            }

            row.Cells["checkin"].Value = api.Checkins.TryGetValue(host, out var t) ? t.ToLocalTime().ToString("g") : "Never";
            if (!api.Checkins.ContainsKey(host)) row.Cells["checkin"].Style.ForeColor = Theme.Dim;

            fleetSnapshot.Add((host, online, rowHashrate, rowTemp, online ? DateTime.UtcNow - hostState.SinceUtc : null));
        }

        summary.Text = $"{hosts.Count} configured hosts  ·  {onlineCount} online  ·  {DateTime.Now:t}";

        SetCard(onlineValue, onlineCount.ToString(), Theme.Good);
        SetCard(offlineValue, (hosts.Count - onlineCount).ToString(), onlineCount == hosts.Count ? Theme.Good : Theme.Bad);
        var avgCpuText = cpuSamples.Count > 0 ? $"{cpuSamples.Average():0}%" : "—";
        SetCard(avgCpuValue, avgCpuText, Theme.Info);
        SetCard(lastSyncValue, DateTime.Now.ToString("HH:mm:ss"), Theme.Muted);

        RefreshFleetStats(fleetSnapshot, avgCpuText);
        UpdateDetails();
    }

    // Writes a cell value and, for a dash, applies the dim/italic
    // empty-state style; otherwise the normal readable style.
    void SetCell(DataGridViewRow row, string column, string value, Color activeColor)
    {
        var cell = row.Cells[column];
        cell.Value = value;
        cell.Style.ForeColor = value == "—" ? Theme.Dim : activeColor;
        cell.Style.Font = value == "—" ? new Font(grid.Font, FontStyle.Italic) : grid.Font;
    }

    void FilterRows()
    {
        var q = search.Text.Trim().ToLowerInvariant();
        foreach (DataGridViewRow row in grid.Rows)
            row.Visible = q.Length == 0 || (row.Cells["host"].Value?.ToString()?.ToLowerInvariant().Contains(q) ?? false);
    }

    // ------------------------------------------------------------------
    // Detail panel
    // ------------------------------------------------------------------
    Control DetailPanel()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface2, Padding = new Padding(18) };

        detailsTitle.Text = "Selected host";
        detailsTitle.Font = new Font("Segoe UI", 15, FontStyle.Bold);
        detailsTitle.ForeColor = Theme.Text;
        detailsTitle.Dock = DockStyle.Top;
        detailsTitle.Height = 40;

        detailsBody.Dock = DockStyle.Top;
        detailsBody.Height = 150;
        detailsBody.FlowDirection = FlowDirection.TopDown;
        detailsBody.WrapContents = false;
        RenderEmptyDetails();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 180, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        buttons.Controls.Add(Action("Set CPU allocation", (_, _) => SendSelected("temporary_override")));
        buttons.Controls.Add(Action("Clear override", (_, _) => SendSelected("clear_override")));
        buttons.Controls.Add(Action("Restart miner", (_, _) => SendSelected("restart_miner")));
        buttons.Controls.Add(Action("Get status", (_, _) => SendSelected("get_status")));

        var audit = new Label { Text = "Recent commands", ForeColor = Theme.Muted, Dock = DockStyle.Top, Height = 25 };
        history.Dock = DockStyle.Fill;
        history.BackColor = Theme.Surface2;
        history.ForeColor = Theme.Muted;
        history.BorderStyle = BorderStyle.FixedSingle;

        p.Controls.Add(history);
        p.Controls.Add(audit);
        p.Controls.Add(buttons);
        p.Controls.Add(detailsBody);
        p.Controls.Add(detailsTitle);
        return p;
    }

    void RenderEmptyDetails()
    {
        detailsBody.Controls.Clear();
        detailsBody.Controls.Add(new Label { Text = "Select a server to view status and actions.", ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(260, 0) });
    }

    void UpdateDetails()
    {
        if (grid.CurrentRow is null) { RenderEmptyDetails(); return; }
        var host = grid.CurrentRow.Cells["host"].Value?.ToString() ?? "";
        detailsBody.Controls.Clear();

        if (!api.TryStatus(host, out var s))
        {
            var state = api.GetHostState(host);
            detailsBody.Controls.Add(StatusPill(state.Online));
            detailsBody.Controls.Add(DetailRow($"{(state.Online ? "Online for " : "Offline for ")}{FormatDuration(DateTime.UtcNow - state.SinceUtc)}", state.Online ? Theme.Good : Theme.Bad));
            detailsBody.Controls.Add(DetailRow("No status received yet.", Theme.Dim));
            return;
        }

        var metrics = s.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        string Get(string prop, string fmt = "{0}") =>
            metrics.ValueKind == JsonValueKind.Object && metrics.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
                ? string.Format(fmt, v.GetDouble()) : "—";

        var effective = s.TryGetProperty("effective_percent", out var cpu) ? $"{cpu.GetDouble():0}%" : "—";
        var temp = Get("temperature_c", "{0:0.0} °C");
        var hash = Get("hashrate", "{0:0.##} H/s");
        var util = Get("cpu_utilization_percent", "{0:0}%");
        var uptime = metrics.ValueKind == JsonValueKind.Object && metrics.TryGetProperty("uptime_seconds", out var up) && up.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(up.GetDouble()).ToString(@"d\.hh\:mm") : "—";
        var hostState = api.GetHostState(host);
        var stateDuration = $"{(hostState.Online ? "Online for " : "Offline for ")}{FormatDuration(DateTime.UtcNow - hostState.SinceUtc)}";
        var lastCheckin = api.Checkins.TryGetValue(host, out var t) ? t.ToLocalTime().ToString("g") : "Never";

        detailsBody.Controls.Add(StatusPill(hostState.Online));
        detailsBody.Controls.Add(DetailRow(stateDuration, hostState.Online ? Theme.Good : Theme.Bad));
        detailsBody.Controls.Add(DetailRow($"Effective CPU  {effective}", effective == "—" ? Theme.Dim : Theme.Info));
        detailsBody.Controls.Add(DetailRow($"CPU use  {util}", util == "—" ? Theme.Dim : Theme.Text));
        detailsBody.Controls.Add(DetailRow($"Temperature  {temp}", temp == "—" ? Theme.Dim : Theme.Text));
        detailsBody.Controls.Add(DetailRow($"Hashrate  {hash}", hash == "—" ? Theme.Dim : Theme.Text));
        detailsBody.Controls.Add(DetailRow($"Uptime  {uptime}", uptime == "—" ? Theme.Dim : Theme.Text));
        detailsBody.Controls.Add(DetailRow($"Last check-in  {lastCheckin}", lastCheckin == "Never" ? Theme.Dim : Theme.Text));
    }

    static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        var parts = new List<string>();
        if (duration.Days > 0)
        {
            parts.Add($"{duration.Days}d");
            if (duration.Hours > 0) parts.Add($"{duration.Hours}h");
        }
        else if (duration.Hours > 0)
        {
            parts.Add($"{duration.Hours}h");
            parts.Add($"{duration.Minutes}m");
        }
        else parts.Add($"{duration.Minutes}m");
        return string.Join(" ", parts);
    }

    static Label DetailRow(string text, Color color) => new()
    {
        Text = text,
        ForeColor = color,
        AutoSize = true,
        Margin = new Padding(0, 3, 0, 3)
    };

    static Label StatusPill(bool online) => new()
    {
        Text = online ? "  ● Online  " : "  ○ Offline  ",
        ForeColor = online ? Theme.Good : Theme.Bad,
        BackColor = online ? Color.FromArgb(28, Theme.Good) : Color.FromArgb(28, Theme.Bad),
        Font = new Font("Segoe UI", 9, FontStyle.Bold),
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 8)
    };

    void SendSelected(string command)
    {
        if (grid.CurrentRow is null) { MessageBox.Show("Select a server first.", "MeowDeploy"); return; }
        var host = grid.CurrentRow.Cells["host"].Value!.ToString()!;
        object payload = command == "temporary_override"
            ? new { percent = PromptNumber("CPU percentage", 35), duration_seconds = 3600 }
            : new { };
        api.Queue(host, command, payload);
        Log($"{DateTime.Now:t}  {host}  {command}");
        MessageBox.Show($"{command.Replace('_', ' ')} queued for {host}.", "Command queued", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Log(string message)
    {
        history.Items.Insert(0, message);
        while (history.Items.Count > 100) history.Items.RemoveAt(history.Items.Count - 1);
    }

    // ------------------------------------------------------------------
    // Schedule editor
    // ------------------------------------------------------------------
    static readonly string[][] DefaultSchedule =
    {
        new[] { "00:00", "06:00", "75" },
        new[] { "06:00", "17:00", "35" },
        new[] { "17:00", "18:00", "50" },
        new[] { "18:00", "00:00", "60" },
    };

    Control BuildScheduleEditor()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        panel.Controls.Add(new Label { Text = "Daily schedule", ForeColor = Theme.Text, Font = new Font("Segoe UI", 18, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);

        var table = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Theme.Surface,
            ForeColor = Theme.Text,
            AllowUserToAddRows = true,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface2, ForeColor = Theme.Muted },
            DefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Surface, ForeColor = Theme.Text, SelectionBackColor = Color.FromArgb(73, 52, 77) }
        };
        table.Columns.Add("start", "Start");
        table.Columns.Add("end", "End");
        table.Columns.Add(new DataGridViewTextBoxColumn { Name = "percent", HeaderText = "CPU %" });
        foreach (var row in DefaultSchedule) table.Rows.Add(row);
        panel.Controls.Add(table, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        buttons.Controls.Add(Action("Apply to fleet", (_, _) =>
        {
            var intervals = new List<object>();
            foreach (DataGridViewRow r in table.Rows)
                if (!r.IsNewRow)
                    intervals.Add(new { start = r.Cells[0].Value?.ToString(), end = r.Cells[1].Value?.ToString(), percent = double.TryParse(r.Cells[2].Value?.ToString(), out var n) ? n : -1 });
            foreach (var host in hosts) api.Queue(host, "set_schedule", new { timezone = "Europe/Dublin", intervals });
            summary.Text = "Schedule queued for fleet";
        }));
        buttons.Controls.Add(Action("Reset default", (_, _) =>
        {
            table.Rows.Clear();
            foreach (var row in DefaultSchedule) table.Rows.Add(row);
        }));
        panel.Controls.Add(buttons, 0, 2);
        return panel;
    }

    // ------------------------------------------------------------------
    // Server / fleet management
    // ------------------------------------------------------------------
    void AddServer()
    {
        var host = PromptText("Add server", "IPv4 address or onion hostname", "172.16.3.");
        host = host?.Trim();
        if (string.IsNullOrWhiteSpace(host) || hosts.Contains(host)) return;
        hosts.Add(host);
        SaveHosts();
        grid.Rows.Add(host, "○ Offline", "—", "—", "—", "Never", "—", "—", "—");
        ApplyEmptyStateStyle();
        summary.Text = $"Added {host} · server list saved";
    }

    void FleetOverride()
    {
        var percent = PromptNumber("Fleet CPU percentage", 35);
        foreach (var host in hosts) api.Queue(host, "temporary_override", new { percent, duration_seconds = 3600 });
        summary.Text = $"Fleet override queued · {percent:0}% for 1 hour";
    }

    void FleetCommand(string command)
    {
        foreach (var host in hosts) api.Queue(host, command, new { });
        summary.Text = $"{command.Replace('_', ' ')} queued for {hosts.Count} hosts";
    }

    List<string> LoadHosts()
    {
        try
        {
            if (File.Exists(serverFile))
            {
                var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(serverFile));
                if (saved is { Count: > 0 }) return saved;
            }
        }
        catch { /* fall through to defaults */ }
        return Enumerable.Range(3, 18).Select(i => $"172.16.3.{i}").ToList();
    }

    void SaveHosts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(serverFile)!);
        File.WriteAllText(serverFile, JsonSerializer.Serialize(hosts, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Themed replacement for the bare default-grey NumericUpDown dialog.
    static double PromptNumber(string title, double initial)
    {
        using var d = ThemedDialog(title, out var body);
        var label = new Label { Text = "Percent", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        var n = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Value = (decimal)Math.Clamp(initial, 0, 100),
            Width = 240,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle
        };
        body.Controls.Add(label);
        body.Controls.Add(n);
        body.Controls.Add(ThemedDialogButtons(d));
        return d.ShowDialog() == DialogResult.OK ? (double)n.Value : initial;
    }

    // Themed replacement for Microsoft.VisualBasic.Interaction.InputBox,
    // which renders as a plain system-grey box that clashes with the rest
    // of the UI.
    static string? PromptText(string title, string label, string initial)
    {
        using var d = ThemedDialog(title, out var body);
        var lbl = new Label { Text = label, ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        var box = new TextBox { Text = initial, Width = 240, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        body.Controls.Add(lbl);
        body.Controls.Add(box);
        body.Controls.Add(ThemedDialogButtons(d));
        box.SelectAll();
        return d.ShowDialog() == DialogResult.OK ? box.Text : null;
    }

    static Form ThemedDialog(string title, out FlowLayoutPanel body)
    {
        var d = new Form
        {
            Text = title,
            Width = 320,
            Height = 190,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 9)
        };
        body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20) };
        d.Controls.Add(body);
        return d;
    }

    static FlowLayoutPanel ThemedDialogButtons(Form owner)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        var ok = new Button { Text = "Apply", DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Theme.Bg, Width = 90, Height = 32, Cursor = Cursors.Hand };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface2, ForeColor = Theme.Muted, Width = 90, Height = 32, Cursor = Cursors.Hand, Margin = new Padding(8, 0, 0, 0) };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(75, 61, 84);
        row.Controls.Add(ok);
        row.Controls.Add(cancel);
        owner.AcceptButton = ok;
        owner.CancelButton = cancel;
        return row;
    }
}

static class FluentExtensions
{
    public static T Also<T>(this T self, Action<T> block)
    {
        block(self);
        return self;
    }
}

// Self-drawn horizontal bar chart — no third-party charting package, so
// the project keeps building with nothing beyond the .NET SDK. Bars ease
// toward their target length on a Timer instead of snapping, which is
// the one place in the app where motion actually communicates something
// (a host's hashrate rising or falling), rather than animation for its
// own sake.
sealed class HashrateBarChart : DoubleBufferedPanel
{
    List<(string Host, double Value)> target = new();
    readonly Dictionary<string, double> displayed = new();
    readonly System.Windows.Forms.Timer ease = new() { Interval = 30 };

    public HashrateBarChart()
    {
        BackColor = Theme.Surface;
        ease.Tick += (_, _) => StepEase();
        ease.Start();
    }

    public void SetData(List<(string Host, double Value)> data)
    {
        target = data.OrderByDescending(d => d.Value).Take(12).ToList();
        foreach (var (host, _) in target)
            if (!displayed.ContainsKey(host)) displayed[host] = 0;
        foreach (var key in displayed.Keys.Where(k => target.All(t => t.Host != k)).ToList())
            displayed.Remove(key);
    }

    void StepEase()
    {
        var changed = false;
        foreach (var (host, value) in target)
        {
            var current = displayed.TryGetValue(host, out var c) ? c : 0;
            if (Math.Abs(current - value) < 0.01) continue;
            displayed[host] = current + (value - current) * 0.25;
            changed = true;
        }
        if (changed) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (target.Count == 0)
        {
            using var dim = new SolidBrush(Theme.Dim);
            g.DrawString("No online hosts reporting a hashrate yet.", Font, dim, 4, 4);
            return;
        }

        var max = Math.Max(target.Max(t => t.Value), 0.0001);
        var rowHeight = Math.Max(22, Height / Math.Max(target.Count, 1));
        var labelWidth = 150;
        using var barBrush = new SolidBrush(Theme.Accent);
        using var trackBrush = new SolidBrush(Theme.Surface2);
        using var textBrush = new SolidBrush(Theme.Text);
        using var mutedBrush = new SolidBrush(Theme.Muted);

        var y = 0;
        foreach (var (host, value) in target)
        {
            var shown = displayed.TryGetValue(host, out var d) ? d : 0;
            var trackWidth = Math.Max(0, Width - labelWidth - 70);
            var barWidth = (int)(trackWidth * (shown / max));

            g.DrawString(host, Font, mutedBrush, 0, y + rowHeight / 2f - 7);
            g.FillRectangle(trackBrush, labelWidth, y + rowHeight / 2 - 6, trackWidth, 12);
            if (barWidth > 0) g.FillRectangle(barBrush, labelWidth, y + rowHeight / 2 - 6, barWidth, 12);
            g.DrawString($"{value:0.##} H/s", Font, textBrush, labelWidth + trackWidth + 8, y + rowHeight / 2f - 7);

            y += rowHeight;
            if (y > Height) break;
        }
    }
}
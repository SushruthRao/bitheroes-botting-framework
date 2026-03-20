using System.Collections.Concurrent;
using BitHeroesClient.Bot;
using BitHeroesClient.Config;
using BitHeroesClient.Logging;
using BitHeroesClient.Models;

namespace BitHeroesClient.Gui;

public sealed class MainForm : Form
{
    // ── Config ─────────────────────────────────────────────────────────────────

    private readonly AppConfig _config;

    // ── Header controls ────────────────────────────────────────────────────────

    private Label _statusLabel = null!;
    private Button _startBtn = null!;
    private Button _stopBtn = null!;

    // ── Dungeon queue tab ──────────────────────────────────────────────────────

    private Button _runBtn = null!;   // starts the dungeon loop
    private Button _stopLoopBtn = null!;   // stops the dungeon loop (stays connected)
    private ListView _queueList = null!;
    private Button _addDunBtn = null!;
    private Button _remDunBtn = null!;
    private Button _moveUpBtn = null!;
    private Button _moveDnBtn = null!;
    // Edit fields for the selected queue entry
    private CheckBox _editEnabled = null!;
    private NumericUpDown _editZone = null!;
    private NumericUpDown _editNode = null!;
    private ComboBox _editDiff = null!;
    private CheckBox _editDmgGain = null!;
    private NumericUpDown _editDelay = null!;
    private NumericUpDown _editMaxRuns = null!;
    private Button _applyDunBtn = null!;
    private Label _zoneCapHint = null!;   // shows unlocked zone range

    // ── Automation tab ─────────────────────────────────────────────────────────

    private CheckBox _autoDailyReward = null!;
    private CheckBox _autoDailyQuests = null!;
    private CheckBox _autoDecline = null!;
    private CheckBox _abandonOrphaned = null!;
    private CheckBox _autoAssignTeam = null!;
    private NumericUpDown _energyWait = null!;
    private ComboBox _tutorialHandling = null!;

    // ── Team list (right panel Character tab) ─────────────────────────────────

    private ListView _teamList = null!;

    // ── Stats labels (right panel) ─────────────────────────────────────────────

    // Bot State
    private Label _stateLabel      = null!;
    private Label _zoneLabel       = null!;
    private Label _enemyProgress   = null!;
    private Label _waveLabel       = null!;
    private Label _actionLabel     = null!;
    private Label _retryLabel      = null!;
    // Session
    private Label _runtimeLabel    = null!;
    private Label _runsLabel       = null!;
    private Label _encountersLabel = null!;
    private Label _goldGainedLabel = null!;
    private Label _expGainedLabel  = null!;
    private Label _levelLabel      = null!;
    private Label _itemsLabel      = null!;
    private Label _dailiesLabel    = null!;
    // Character
    private Label _energyText      = null!;
    private Label _energyRegen     = null!;
    private Label _ticketsText     = null!;
    private Label _ticketsRegen    = null!;
    private Label _goldLabel       = null!;
    private Label _creditsLabel    = null!;
    private Label _shardsLabel     = null!;
    private Label _highestZoneLabel = null!;
    // Loot feed
    private ListBox _lootList      = null!;
    private int     _lootListCount = 0;

    // ── Dungeon debug tab ─────────────────────────────────────────────────────

    private ListView _dungeonObjList = null!;
    private Button _fightBtn = null!;
    private Label _dungeonStatus = null!;

    // ── Log ───────────────────────────────────────────────────────────────────

    private ListBox _logBox = null!;
    private SplitContainer _mainSplit = null!;

    // ── Timers ────────────────────────────────────────────────────────────────

    private System.Windows.Forms.Timer _tickTimer = null!;
    private System.Windows.Forms.Timer _uiTimer = null!;

    // ── State ─────────────────────────────────────────────────────────────────

    private bool _botRunning = false;
    private int _lastHighZone = 0;    // track changes to HighestZone for zone cap updates

    // Thread-safe queue: Logger.LineWritten enqueues from any thread;
    // _uiTimer drains it on the UI thread so WinForms is never touched from a background thread.
    private readonly ConcurrentQueue<(LogLevel Level, string Line)> _pendingLogLines = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainForm(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        LoadConfigToUi();
        Logger.LineWritten += OnLogLine;
    }

    // ── Form construction ─────────────────────────────────────────────────────

    private void InitializeComponent()
    {
        Text = "Bit Heroes Bot";
        Size = new Size(980, 740);
        MinimumSize = new Size(820, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(245, 245, 248);
        Font = new Font("Segoe UI", 9f);

        SuspendLayout();

        Controls.Add(BuildMainSplit());
        Controls.Add(BuildLogPanel());
        Controls.Add(BuildHeader());

        Shown += (_, _) =>
        {
            _mainSplit.Panel1MinSize = 300;
            _mainSplit.Panel2MinSize = 380;
            _mainSplit.SplitterDistance = 330;
        };

        ResumeLayout(true);

        _tickTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _tickTimer.Tick += (_, _) =>
        {
            try { BHBot.Tick(); }
            catch (Exception ex) { Logger.Error($"Tick error: {ex.Message}"); }
        };

        _uiTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _uiTimer.Tick += (_, _) => RefreshStats();
        _uiTimer.Start();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private Panel BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 52,
            BackColor = Color.FromArgb(22, 22, 22),
            Padding = new Padding(12, 0, 12, 0)
        };

        var title = new Label
        {
            Text = "BIT HEROES BOT",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(12, 14)
        };

        _statusLabel = new Label
        {
            Text = "● IDLE",
            ForeColor = Color.FromArgb(140, 140, 140),
            Font = new Font("Segoe UI", 10f),
            AutoSize = true,
            Location = new Point(210, 17)
        };

        _startBtn = MakeHeaderButton("▶  Start", Color.FromArgb(34, 139, 34));
        _startBtn.Click += (_, _) => StartBot();

        _stopBtn = MakeHeaderButton("■  Stop", Color.FromArgb(160, 30, 30));
        _stopBtn.Enabled = false;
        _stopBtn.Click += (_, _) => StopBot();

        panel.Controls.AddRange(new Control[] { title, _statusLabel, _startBtn, _stopBtn });
        panel.Resize += (_, _) =>
        {
            _stopBtn.Location = new Point(panel.Width - 12 - _stopBtn.Width, 10);
            _startBtn.Location = new Point(_stopBtn.Left - 8 - _startBtn.Width, 10);
        };
        return panel;
    }

    private static Button MakeHeaderButton(string text, Color bg)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(90, 32),
            BackColor = bg,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f)
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    // ── Log panel (bottom) ────────────────────────────────────────────────────

    private Panel BuildLogPanel()
    {
        var outer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 185,
            BackColor = Color.FromArgb(18, 18, 18)
        };

        var label = new Label
        {
            Text = "ACTIVITY LOG",
            ForeColor = Color.FromArgb(160, 160, 160),
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(10, 6)
        };

        _logBox = new ListBox
        {
            Dock                = DockStyle.Fill,
            BackColor           = Color.FromArgb(18, 18, 18),
            ForeColor           = Color.FromArgb(200, 200, 200),
            Font                = new Font("Consolas", 8.5f),
            BorderStyle         = BorderStyle.None,
            DrawMode            = DrawMode.OwnerDrawFixed,
            SelectionMode       = SelectionMode.MultiExtended,
            HorizontalScrollbar = false,
            ItemHeight          = 15,
        };
        _logBox.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= _logBox.Items.Count) return;
            // Draw selection highlight manually so it stays visible on the dark background
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var bgBrush = new SolidBrush(selected
                       ? Color.FromArgb(50, 80, 120)
                       : Color.FromArgb(18, 18, 18)))
                e.Graphics.FillRectangle(bgBrush, e.Bounds);

            var (color, text) = ((Color, string))_logBox.Items[e.Index]!;
            using var brush = new SolidBrush(color);
            e.Graphics.DrawString(text, e.Font!, brush,
                new RectangleF(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height));
        };
        // Ctrl+C copies selected lines; Ctrl+A selects all
        _logBox.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                for (int i = 0; i < _logBox.Items.Count; i++)
                    _logBox.SetSelected(i, true);
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                var lines = _logBox.SelectedItems
                    .Cast<(Color, string)>()
                    .Select(t => t.Item2);
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                e.Handled = true;
            }
        };

        outer.Controls.Add(_logBox);
        outer.Controls.Add(label);
        return outer;
    }

    // ── Main split ────────────────────────────────────────────────────────────

    private SplitContainer BuildMainSplit()
    {
        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(210, 210, 215)
        };
        BuildLeftPanel(_mainSplit.Panel1);
        BuildRightPanel(_mainSplit.Panel2);
        return _mainSplit;
    }

    // ── Left panel: configuration ─────────────────────────────────────────────

    private void BuildLeftPanel(SplitterPanel parent)
    {
        parent.BackColor = Color.FromArgb(245, 245, 248);
        parent.Padding = new Padding(8, 8, 4, 8);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9f),
            Padding = new Point(10, 4)
        };
        tabs.TabPages.Add(BuildDungeonTab());
        tabs.TabPages.Add(BuildAutomationTab());
        tabs.TabPages.Add(BuildDungeonDebugTab());
        parent.Controls.Add(tabs);
    }

    // ── Dungeon queue tab ─────────────────────────────────────────────────────

    private TabPage BuildDungeonTab()
    {
        var tab = new TabPage("⚔  Dungeon Queue") { BackColor = Color.FromArgb(245, 245, 248) };
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        int y = 8;

        // ── Run / Stop loop buttons ───────────────────────────────────────────

        _runBtn = new Button
        {
            Text = "▶  Run Dungeons",
            Location = new Point(8, y),
            Size = new Size(140, 34),
            BackColor = Color.FromArgb(30, 120, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Enabled = false   // enabled once InGame
        };
        _runBtn.FlatAppearance.BorderSize = 0;
        _runBtn.Click += (_, _) =>
        {
            SaveUiToConfig();
            ConfigLoader.Save(_config);
            BHBot.StartDungeonLoop();
        };

        _stopLoopBtn = new Button
        {
            Text = "⏹  Stop Loop",
            Location = new Point(156, y),
            Size = new Size(120, 34),
            BackColor = Color.FromArgb(160, 80, 20),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f),
            Enabled = false
        };
        _stopLoopBtn.FlatAppearance.BorderSize = 0;
        _stopLoopBtn.Click += (_, _) => BHBot.StopDungeonLoop();

        panel.Controls.AddRange(new Control[] { _runBtn, _stopLoopBtn });
        y += 42;

        // ── Zone cap hint ─────────────────────────────────────────────────────

        _zoneCapHint = new Label
        {
            Text = "Connect to see available zones",
            Location = new Point(8, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 100, 140),
            Font = new Font("Segoe UI", 8f, FontStyle.Italic)
        };
        panel.Controls.Add(_zoneCapHint);
        y += 20;

        // ── Queue list ────────────────────────────────────────────────────────

        panel.Controls.Add(new Label
        {
            Text = "Queue  (runs in order, loops forever):",
            Location = new Point(8, y),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        });
        y += 20;

        _queueList = new ListView
        {
            Location = new Point(8, y),
            Size = new Size(282, 130),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8f)
        };
        _queueList.Columns.Add("On", 30);
        _queueList.Columns.Add("Zone", 46);
        _queueList.Columns.Add("Node", 46);
        _queueList.Columns.Add("Diff", 46);
        _queueList.Columns.Add("Delay", 74);
        _queueList.SelectedIndexChanged += (_, _) => LoadSelectionToEditFields();
        panel.Controls.Add(_queueList);
        y += 138;

        // ── Queue management buttons ──────────────────────────────────────────

        int bx = 8;
        _addDunBtn = SmallBtn("+ Add", ref bx, y);
        _remDunBtn = SmallBtn("Remove", ref bx, y);
        _moveUpBtn = SmallBtn("▲ Up", ref bx, y);
        _moveDnBtn = SmallBtn("▼ Down", ref bx, y);
        panel.Controls.AddRange(new Control[] { _addDunBtn, _remDunBtn, _moveUpBtn, _moveDnBtn });

        _addDunBtn.Click += (_, _) => AddQueueEntry();
        _remDunBtn.Click += (_, _) => RemoveQueueEntry();
        _moveUpBtn.Click += (_, _) => MoveQueueEntry(-1);
        _moveDnBtn.Click += (_, _) => MoveQueueEntry(+1);
        y += 32;

        // ── Edit selected entry ───────────────────────────────────────────────

        panel.Controls.Add(new Label
        {
            Text = "──  Edit Selected Entry  ──",
            Location = new Point(8, y),
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f)
        });
        y += 18;

        _editEnabled = Chk(panel, "Enabled in queue", ref y, true);

        panel.Controls.Add(new Label { Text = "Zone ID:", Location = new Point(8, y + 3), Width = 58 });
        _editZone = new NumericUpDown { Location = new Point(68, y), Width = 60, Minimum = 1, Maximum = 999, Value = 1 };
        panel.Controls.Add(new Label { Text = "Node:", Location = new Point(136, y + 3), Width = 40 });
        _editNode = new NumericUpDown { Location = new Point(178, y), Width = 60, Minimum = 1, Maximum = 999, Value = 1 };
        panel.Controls.AddRange(new Control[] { _editZone, _editNode });
        y += 30;

        ComboRow(panel, "Difficulty", ref y, out _editDiff,
            new[] { "0 — Normal", "1 — Hard", "2 — Heroic" });
        _editDmgGain = Chk(panel, "Use Damage Gain (bat57)", ref y, true);
        SpinRow(panel, "Repeat Delay (ms)", ref y, out _editDelay, 0, 300_000, 3000);
        SpinRow(panel, "Max Runs (−1=∞)", ref y, out _editMaxRuns, -1, 99_999, -1);

        _applyDunBtn = new Button
        {
            Text = "Apply Changes",
            Location = new Point(8, y),
            Size = new Size(120, 28),
            BackColor = Color.FromArgb(45, 100, 180),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f)
        };
        _applyDunBtn.FlatAppearance.BorderSize = 0;
        _applyDunBtn.Click += (_, _) => ApplyEditToSelectedEntry();
        panel.Controls.Add(_applyDunBtn);

        tab.Controls.Add(panel);
        return tab;
    }

    // ── Queue management helpers ──────────────────────────────────────────────

    private void PopulateQueueList()
    {
        int sel = SelectedQueueIndex();
        _queueList.Items.Clear();
        foreach (var d in _config.DungeonQueue)
        {
            string diff = d.DifficultyId switch { 1 => "Hard", 2 => "Hero", _ => "Norm" };
            var item = new ListViewItem(d.Enabled ? "✓" : "");
            item.SubItems.Add(d.ZoneId.ToString());
            item.SubItems.Add(d.NodeId.ToString());
            item.SubItems.Add(diff);
            item.SubItems.Add($"{d.RepeatDelayMs}ms");

            // Highlight entries exceeding the player's unlocked zone range
            int highZ = SessionStats.HighestZone;
            if (highZ > 0 && d.ZoneId > highZ + 1)
                item.ForeColor = Color.FromArgb(180, 60, 60);

            _queueList.Items.Add(item);
        }
        if (sel >= 0 && sel < _queueList.Items.Count)
            _queueList.Items[sel].Selected = true;
    }

    private void LoadSelectionToEditFields()
    {
        int idx = SelectedQueueIndex();
        if (idx < 0) return;
        var d = _config.DungeonQueue[idx];
        _editEnabled.Checked = d.Enabled;
        _editZone.Value = Math.Clamp(d.ZoneId, 1, (int)_editZone.Maximum);
        _editNode.Value = Math.Clamp(d.NodeId, 1, 999);
        _editDiff.SelectedIndex = Math.Clamp(d.DifficultyId, 0, 2);
        _editDmgGain.Checked = d.UseDamageGain;
        _editDelay.Value = Math.Clamp(d.RepeatDelayMs, 0, 300_000);
        _editMaxRuns.Value = Math.Clamp(d.MaxRuns, -1, 99_999);
    }

    private void ApplyEditToSelectedEntry()
    {
        int idx = SelectedQueueIndex();
        if (idx < 0) return;
        var d = _config.DungeonQueue[idx];
        d.Enabled = _editEnabled.Checked;
        d.ZoneId = (int)_editZone.Value;
        d.NodeId = (int)_editNode.Value;
        d.DifficultyId = _editDiff.SelectedIndex;
        d.UseDamageGain = _editDmgGain.Checked;
        d.RepeatDelayMs = (int)_editDelay.Value;
        d.MaxRuns = (int)_editMaxRuns.Value;
        PopulateQueueList();
    }

    private void AddQueueEntry()
    {
        _config.DungeonQueue.Add(new DungeonConfig());
        PopulateQueueList();
        int last = _queueList.Items.Count - 1;
        _queueList.Items[last].Selected = true;
        _queueList.EnsureVisible(last);
    }

    private void RemoveQueueEntry()
    {
        int idx = SelectedQueueIndex();
        if (idx < 0 || _config.DungeonQueue.Count <= 1) return;
        _config.DungeonQueue.RemoveAt(idx);
        PopulateQueueList();
        int newSel = Math.Min(idx, _queueList.Items.Count - 1);
        if (newSel >= 0) _queueList.Items[newSel].Selected = true;
    }

    private void MoveQueueEntry(int delta)
    {
        int idx = SelectedQueueIndex();
        int newIdx = idx + delta;
        if (idx < 0 || newIdx < 0 || newIdx >= _config.DungeonQueue.Count) return;
        (_config.DungeonQueue[idx], _config.DungeonQueue[newIdx]) =
            (_config.DungeonQueue[newIdx], _config.DungeonQueue[idx]);
        PopulateQueueList();
        _queueList.Items[newIdx].Selected = true;
    }

    private int SelectedQueueIndex()
    {
        if (_queueList.SelectedIndices.Count == 0) return -1;
        return _queueList.SelectedIndices[0];
    }

    /// <summary>
    /// Called whenever HighestZone changes: updates the zone spinner cap, the hint label,
    /// and recolours queue entries that exceed the player's unlocked range.
    /// </summary>
    private void ApplyZoneCap(int highestZone)
    {
        if (highestZone <= 0) return;

        // Player can enter any zone they've completed + 1 ahead
        int maxPlayable = highestZone + 1;
        _editZone.Maximum = maxPlayable;
        if (_editZone.Value > maxPlayable) _editZone.Value = maxPlayable;

        _zoneCapHint.Text = $"Unlocked zones: 1 – {highestZone}  (can attempt up to zone {maxPlayable})";
        _zoneCapHint.ForeColor = Color.FromArgb(30, 110, 60);
        PopulateQueueList();  // recolour out-of-range entries
    }

    // ── Dungeon debug tab ─────────────────────────────────────────────────────

    private TabPage BuildDungeonDebugTab()
    {
        var tab = new TabPage("🗺  Dungeon") { BackColor = Color.FromArgb(245, 245, 248) };
        var panel = new Panel { Dock = DockStyle.Fill };

        _dungeonStatus = new Label
        {
            Text = "Enter a dungeon to see the object list.",
            Location = new Point(8, 8),
            AutoSize = true,
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic)
        };
        panel.Controls.Add(_dungeonStatus);

        _dungeonObjList = new ListView
        {
            Location = new Point(8, 28),
            Size = new Size(282, 340),
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5f)
        };
        _dungeonObjList.Columns.Add("Row", 44);
        _dungeonObjList.Columns.Add("Col", 44);
        _dungeonObjList.Columns.Add("Type", 74);
        _dungeonObjList.Columns.Add("Used", 44);
        _dungeonObjList.Columns.Add("Empty", 50);
        panel.Controls.Add(_dungeonObjList);

        _fightBtn = new Button
        {
            Text = "⚔  Fight Selected",
            Location = new Point(8, 376),
            Size = new Size(140, 32),
            BackColor = Color.FromArgb(160, 30, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Enabled = false
        };
        _fightBtn.FlatAppearance.BorderSize = 0;
        _fightBtn.Click += (_, _) => FightSelectedDungeonObject();
        panel.Controls.Add(_fightBtn);

        tab.Controls.Add(panel);
        return tab;
    }

    private void OnDungeonLoaded(IReadOnlyList<DungeonObjectInfo> objects)
    {
        if (InvokeRequired) { BeginInvoke(() => OnDungeonLoaded(objects)); return; }

        _dungeonObjList.Items.Clear();
        foreach (var o in objects)
        {
            var item = new ListViewItem(o.Row.ToString());
            item.SubItems.Add(o.Col.ToString());
            item.SubItems.Add(o.TypeName);
            item.SubItems.Add(o.Used ? "yes" : "no");
            item.SubItems.Add(o.Empty ? "yes" : "no");
            item.Tag = o;

            // Highlight enemies and bosses
            if (o.Type == 0) item.ForeColor = Color.FromArgb(180, 40, 40);
            else if (o.Type == 2) item.ForeColor = Color.FromArgb(140, 0, 140);
            else item.ForeColor = Color.FromArgb(60, 60, 60);

            _dungeonObjList.Items.Add(item);
        }

        _dungeonStatus.Text = $"{objects.Count} objects loaded. Player at ({BHBot.PlayerRow},{BHBot.PlayerCol}). Select an enemy and click Fight.";
        _dungeonStatus.ForeColor = Color.FromArgb(30, 110, 60);
        _fightBtn.Enabled = true;
    }

    private void FightSelectedDungeonObject()
    {
        if (_dungeonObjList.SelectedItems.Count == 0) return;
        if (_dungeonObjList.SelectedItems[0].Tag is not DungeonObjectInfo obj) return;
        if (!BHBot.IsInGame) return;
        BHBot.ActivateDungeonObject(obj.Row, obj.Col);
    }

    // ── Automation tab ────────────────────────────────────────────────────────

    private TabPage BuildAutomationTab()
    {
        var tab = new TabPage("⚙  Automation") { BackColor = Color.FromArgb(245, 245, 248) };
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        int y = 10;
        _autoDailyReward = Chk(panel, "Auto Claim Daily Reward", ref y, true);
        _autoDailyQuests = Chk(panel, "Auto Claim Daily Quests (unverified — keep OFF)", ref y, false);
        _autoDecline = Chk(panel, "Auto Decline Captures", ref y, true);
        _abandonOrphaned = Chk(panel, "Abandon Orphaned Dungeon", ref y, true);
        _autoAssignTeam = Chk(panel, "Auto-Assign Teammates (strongest available)", ref y, true);
        y += 6;
        SpinRow(panel, "Energy Wait (min, fallback)", ref y, out _energyWait, 0, 120, 10);
        ComboRow(panel, "Tutorial Handling", ref y, out _tutorialHandling,
            new[] { "Warn", "Stop", "Skip" });

        tab.Controls.Add(panel);
        return tab;
    }

    // ── Right panel: tabbed live stats ───────────────────────────────────────

    private void BuildRightPanel(SplitterPanel parent)
    {
        parent.BackColor = Color.FromArgb(235, 236, 240);
        parent.Padding   = new Padding(0);

        var tabs = new TabControl
        {
            Dock    = DockStyle.Fill,
            Font    = new Font("Segoe UI", 8.5f),
            Padding = new Point(8, 4),
        };
        tabs.TabPages.Add(BuildStatusTab());
        tabs.TabPages.Add(BuildSessionTab());
        tabs.TabPages.Add(BuildCharacterTab());
        tabs.TabPages.Add(BuildLootTab());
        parent.Controls.Add(tabs);
    }

    private TabPage BuildStatusTab()
    {
        var tab    = new TabPage("Status") { BackColor = Color.FromArgb(245, 245, 248) };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 6, 8, 6) };

        int y = 8;
        TabSection(scroll, "Bot State", ref y);
        _stateLabel    = StatRow(scroll, "State:",      ref y, "IDLE");
        _zoneLabel     = StatRow(scroll, "Zone/Node:",  ref y, "—");
        _enemyProgress = StatRow(scroll, "Enemies:",    ref y, "—");
        _waveLabel     = StatRow(scroll, "Battle #:",   ref y, "—");
        _actionLabel   = StatRow(scroll, "Action:",     ref y, "—");
        _retryLabel    = StatRow(scroll, "Retries:",    ref y, "0");

        tab.Controls.Add(scroll);
        return tab;
    }

    private TabPage BuildSessionTab()
    {
        var tab    = new TabPage("Session") { BackColor = Color.FromArgb(245, 245, 248) };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 6, 8, 6) };

        int y = 8;
        TabSection(scroll, "Timing", ref y);
        _runtimeLabel = StatRow(scroll, "Runtime:", ref y, "00:00:00");

        TabSection(scroll, "Dungeon Runs", ref y);
        _runsLabel = StatRow(scroll, "Runs:",  ref y, "0  (0W / 0L)");

        TabSection(scroll, "Encounters", ref y);
        _encountersLabel = StatRow(scroll, "Battles:",     ref y, "0  (0W / 0L)");
        _goldGainedLabel = StatRow(scroll, "Gold Earned:", ref y, "0");
        _expGainedLabel  = StatRow(scroll, "EXP Earned:",  ref y, "0");
        _levelLabel      = StatRow(scroll, "Level:",       ref y, "—");

        TabSection(scroll, "Items & Quests", ref y);
        _itemsLabel   = StatRow(scroll, "Items Looted:", ref y, "0");
        _dailiesLabel = StatRow(scroll, "Dailies:",       ref y, "0");

        tab.Controls.Add(scroll);
        return tab;
    }

    private TabPage BuildCharacterTab()
    {
        var tab    = new TabPage("Character") { BackColor = Color.FromArgb(245, 245, 248) };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 6, 8, 6) };

        int y = 8;
        TabSection(scroll, "Resources", ref y);

        _energyText = StatRow(scroll, "Energy:", ref y, "—");
        _energyRegen = new Label
        {
            Text      = "",
            Location  = new Point(90, y),
            AutoSize  = true,
            ForeColor = Color.FromArgb(80, 110, 160),
            Font      = new Font("Segoe UI", 7.5f, FontStyle.Italic)
        };
        scroll.Controls.Add(_energyRegen);
        y += 14;

        _ticketsText = StatRow(scroll, "Tickets:", ref y, "—");
        _ticketsRegen = new Label
        {
            Text      = "",
            Location  = new Point(90, y),
            AutoSize  = true,
            ForeColor = Color.FromArgb(80, 110, 160),
            Font      = new Font("Segoe UI", 7.5f, FontStyle.Italic)
        };
        scroll.Controls.Add(_ticketsRegen);
        y += 14;

        TabSection(scroll, "Currency", ref y);
        _goldLabel        = StatRow(scroll, "Gold:",       ref y, "—");
        _creditsLabel     = StatRow(scroll, "Credits:",    ref y, "—");
        _shardsLabel      = StatRow(scroll, "Shards:",     ref y, "—");

        TabSection(scroll, "Progression", ref y);
        _highestZoneLabel = StatRow(scroll, "High Zone:",  ref y, "—");

        TabSection(scroll, "Active Team", ref y);
        scroll.Controls.Add(new Label
        {
            Text      = "Slot  Type       ID        P      S      A    Total",
            Location  = new Point(8, y),
            AutoSize  = true,
            ForeColor = Color.FromArgb(100, 100, 115),
            Font      = new Font("Consolas", 7.5f)
        });
        y += 16;

        _teamList = new ListView
        {
            Location        = new Point(8, y),
            Size            = new Size(330, 120),
            View            = View.Details,
            FullRowSelect   = true,
            GridLines       = true,
            MultiSelect     = false,
            BorderStyle     = BorderStyle.FixedSingle,
            Font            = new Font("Consolas", 8f),
            HeaderStyle     = ColumnHeaderStyle.None,
        };
        _teamList.Columns.Add("#",     28);
        _teamList.Columns.Add("Type",  58);
        _teamList.Columns.Add("ID",    64);
        _teamList.Columns.Add("Pwr",   36);
        _teamList.Columns.Add("Sta",   36);
        _teamList.Columns.Add("Agi",   36);
        _teamList.Columns.Add("Total", 42);
        scroll.Controls.Add(_teamList);

        tab.Controls.Add(scroll);
        return tab;
    }

    private TabPage BuildLootTab()
    {
        var tab = new TabPage("Loot") { BackColor = Color.FromArgb(16, 20, 28), Padding = new Padding(0) };

        _lootList = new ListBox
        {
            Dock           = DockStyle.Fill,
            Font           = new Font("Consolas", 8f),
            BackColor      = Color.FromArgb(16, 20, 28),
            ForeColor      = Color.FromArgb(210, 215, 220),
            BorderStyle    = BorderStyle.None,
            IntegralHeight = false,
            SelectionMode  = SelectionMode.None,
            DrawMode       = DrawMode.OwnerDrawFixed,
            ItemHeight     = 16,
            HorizontalScrollbar = true,
        };
        _lootList.DrawItem += DrawLootItem;
        tab.Controls.Add(_lootList);
        return tab;
    }

    private static void DrawLootItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox lb) return;
        if (e.Index < 0 || e.Index >= lb.Items.Count) return;

        var entry = lb.Items[e.Index] as LootEntry;
        if (entry == null) return;

        // Row background
        Color bg = entry.NewLevel > 0
            ? Color.FromArgb(20, 50, 20)   // level-up = dark green bg
            : entry.GoldDelta > 0 || entry.Items.Any(i => !i.IsCurrency)
                ? Color.FromArgb(28, 24, 14) // rewards = dark gold bg
                : Color.FromArgb(36, 18, 18); // loss = dark red bg
        using (var bgBrush = new SolidBrush(bg))
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

        // Row text colour
        Color fg = entry.NewLevel > 0
            ? Color.FromArgb(120, 220, 100)
            : entry.GoldDelta > 0 || entry.Items.Count > 0
                ? Color.FromArgb(220, 190, 100)
                : Color.FromArgb(180, 80, 80);

        using var fgBrush = new SolidBrush(fg);
        e.Graphics.DrawString(entry.Summary(), e.Font!, fgBrush,
            new RectangleF(e.Bounds.X + 4, e.Bounds.Y + 1, e.Bounds.Width - 4, e.Bounds.Height));
    }

    // ── Tab layout helpers ────────────────────────────────────────────────────

    private static void TabSection(Panel p, string title, ref int y)
    {
        if (y > 8) y += 4;  // spacing above all but first section
        var lbl = new Label
        {
            Text      = title.ToUpperInvariant(),
            Location  = new Point(8, y),
            AutoSize  = true,
            ForeColor = Color.FromArgb(100, 130, 180),
            Font      = new Font("Segoe UI", 7.5f, FontStyle.Bold),
        };
        p.Controls.Add(lbl);
        y += 18;

        // Thin separator line
        var line = new Panel
        {
            Location  = new Point(8, y),
            Size      = new Size(200, 1),
            BackColor = Color.FromArgb(210, 212, 220),
        };
        p.Controls.Add(line);
        y += 5;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────



    private static Label StatRow(Control parent, string caption, ref int y, string initial)
    {
        const int capW = 90;
        var cap = new Label
        {
            Text      = caption,
            Location  = new Point(8, y),
            Width     = capW,
            ForeColor = Color.FromArgb(100, 100, 115),
            Font      = new Font("Segoe UI", 8.5f)
        };
        var val = new Label
        {
            Text     = initial,
            Location = new Point(capW + 10, y),
            AutoSize = true,
            Font     = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        parent.Controls.Add(cap);
        parent.Controls.Add(val);
        y += 22;
        return val;
    }

    private static void SpinRow(Panel p, string label, ref int y,
                                 out NumericUpDown spin, decimal min, decimal max, decimal val)
    {
        const int lw = 158; const int sx = 170; const int sw = 100;
        p.Controls.Add(new Label { Text = label, Location = new Point(8, y + 3), Width = lw });
        spin = new NumericUpDown
        {
            Location = new Point(sx, y),
            Width = sw,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(val, min, max),
            DecimalPlaces = 0
        };
        p.Controls.Add(spin);
        y += 30;
    }

    private static void ComboRow(Panel p, string label, ref int y,
                                  out ComboBox combo, string[] items)
    {
        const int lw = 158; const int cx = 170; const int cw = 110;
        p.Controls.Add(new Label { Text = label, Location = new Point(8, y + 3), Width = lw });
        combo = new ComboBox
        {
            Location = new Point(cx, y),
            Width = cw,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        combo.Items.AddRange(items);
        combo.SelectedIndex = 0;
        p.Controls.Add(combo);
        y += 30;
    }

    private static CheckBox Chk(Panel p, string text, ref int y, bool @checked, bool bold = false)
    {
        var cb = new CheckBox
        {
            Text = text,
            Location = new Point(8, y),
            AutoSize = true,
            Checked = @checked,
            Font = bold ? new Font("Segoe UI", 9f, FontStyle.Bold) : new Font("Segoe UI", 9f)
        };
        p.Controls.Add(cb);
        y += 26;
        return cb;
    }

    private static Button SmallBtn(string text, ref int x, int y)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(64, 26),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8f)
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(180, 180, 180);
        x += 68;
        return b;
    }

    // ── Bot control ───────────────────────────────────────────────────────────

    private void StartBot()
    {
        if (_botRunning) return;

        SaveUiToConfig();
        ConfigLoader.Save(_config);

        BHBot.OnInGame += OnBotInGame;
        BHBot.OnDungeonComplete += OnDungeonComplete;
        BHBot.OnServerError += OnServerError;
        BHBot.OnDungeonLoaded += OnDungeonLoaded;

        try { BHBot.Start(_config); }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start: {ex.Message}");
            BHBot.OnInGame -= OnBotInGame;
            BHBot.OnDungeonComplete -= OnDungeonComplete;
            BHBot.OnServerError -= OnServerError;
            return;
        }

        _botRunning = true;
        _startBtn.Enabled = false;
        _stopBtn.Enabled = true;
        SetStatus("CONNECTING…", Color.FromArgb(200, 150, 30));
        _tickTimer.Start();
    }

    private void StopBot()
    {
        if (!_botRunning) return;

        _tickTimer.Stop();
        try { BHBot.Stop(); } catch { }

        BHBot.OnInGame -= OnBotInGame;
        BHBot.OnDungeonComplete -= OnDungeonComplete;
        BHBot.OnServerError -= OnServerError;
        BHBot.OnDungeonLoaded -= OnDungeonLoaded;

        _botRunning = false;
        _startBtn.Enabled = true;
        _stopBtn.Enabled = false;
        _runBtn.Enabled = false;
        _stopLoopBtn.Enabled = false;
        _lastHighZone = 0;
        _dungeonObjList.Items.Clear();
        _fightBtn.Enabled = false;
        _dungeonStatus.Text = "Enter a dungeon to see the object list.";
        _dungeonStatus.ForeColor = Color.Gray;
        SetStatus("STOPPED", Color.FromArgb(150, 150, 150));
    }

    private void OnBotInGame()
    {
        // Must be on UI thread for control updates
        if (InvokeRequired) { BeginInvoke(OnBotInGame); return; }
        SetStatus("ONLINE", Color.FromArgb(34, 139, 34));
        _runBtn.Enabled = true;
        // Apply zone cap immediately if we already have the value
        int hz = SessionStats.HighestZone;
        if (hz > 0) { _lastHighZone = hz; ApplyZoneCap(hz); }
    }

    private void OnDungeonComplete(DungeonResult _) { }
    private void OnServerError(int d, int a, int e) { }

    // ── Stats refresh (300 ms timer) ──────────────────────────────────────────

    private void RefreshStats()
    {
        if (IsDisposed || !IsHandleCreated) return;

        // ── Run / Stop loop button states ────────────────────────────────────
        bool inGame = BHBot.IsInGame;
        bool loopOn = BHBot.IsDungeonLoopRunning;
        _runBtn.Enabled = inGame && !loopOn;
        _stopLoopBtn.Enabled = inGame && loopOn;

        // ── Zone cap update (fires once when HighestZone first populates) ────
        int hz = SessionStats.HighestZone;
        if (hz > 0 && hz != _lastHighZone)
        {
            _lastHighZone = hz;
            ApplyZoneCap(hz);
        }

        // ── Bot state ─────────────────────────────────────────────────────────
        _stateLabel.Text = SessionStats.CurrentState;

        int cz = SessionStats.CurrentZone, cn = SessionStats.CurrentNode;
        _zoneLabel.Text = cz > 0
            ? $"Z{cz} / N{cn}"
              + (SessionStats.QueueTotal > 1
                    ? $"  [{SessionStats.CurrentQueueIndex + 1}/{SessionStats.QueueTotal}]" : "")
            : "—";

        int et = SessionStats.EnemiesTotal, ec = SessionStats.EnemiesCleared;
        _enemyProgress.Text = et > 0 ? $"{ec} / {et} cleared" : "—";

        int cw = SessionStats.CurrentWave;
        _waveLabel.Text = cw > 0 ? $"Battle #{cw}" : "—";
        _actionLabel.Text = string.IsNullOrEmpty(SessionStats.CurrentAction) ? "—" : SessionStats.CurrentAction;
        _retryLabel.Text  = SessionStats.RetryCount > 0 ? SessionStats.RetryCount.ToString() : "0";

        // ── Session ───────────────────────────────────────────────────────────
        var rt = SessionStats.Runtime;
        _runtimeLabel.Text    = $"{(int)rt.TotalHours:D2}:{rt.Minutes:D2}:{rt.Seconds:D2}";
        _runsLabel.Text       = $"{SessionStats.TotalRuns}  ({SessionStats.Wins}W / {SessionStats.Losses}L)";
        _encountersLabel.Text = $"{SessionStats.EncountersWon + SessionStats.EncountersLost}" +
                                $"  ({SessionStats.EncountersWon}W / {SessionStats.EncountersLost}L)";
        _goldGainedLabel.Text = SessionStats.GoldGained > 0 ? $"+{SessionStats.GoldGained:N0}" : "0";
        _expGainedLabel.Text  = SessionStats.ExpGained  > 0 ? $"+{SessionStats.ExpGained:N0}"  : "0";
        _levelLabel.Text      = SessionStats.Level > 0 ? SessionStats.Level.ToString() : "—";
        _itemsLabel.Text      = SessionStats.TotalItems.ToString();
        _dailiesLabel.Text    = SessionStats.DailiesClaimed.ToString();

        // ── Character ─────────────────────────────────────────────────────────
        int en = SessionStats.Energy;
        _energyText.Text = en >= 0 ? en.ToString() : "—";
        var nextEn = SessionStats.NextEnergyAt;
        _energyRegen.Text = nextEn > DateTime.MinValue
            ? $"next +1 @ {nextEn.ToLocalTime():HH:mm:ss}" : "";

        int tk = SessionStats.Tickets;
        _ticketsText.Text = tk >= 0 ? tk.ToString() : "—";
        var nextTk = SessionStats.NextTicketAt;
        _ticketsRegen.Text = nextTk > DateTime.MinValue
            ? $"next +1 @ {nextTk.ToLocalTime():HH:mm:ss}" : "";

        _goldLabel.Text        = SessionStats.Gold    > 0 ? SessionStats.Gold.ToString("N0")    : "—";
        _creditsLabel.Text     = SessionStats.Credits > 0 ? SessionStats.Credits.ToString("N0") : "—";
        _shardsLabel.Text      = SessionStats.Shards  > 0 ? SessionStats.Shards.ToString("N0")  : "—";
        _highestZoneLabel.Text = SessionStats.HighestZone > 0 ? SessionStats.HighestZone.ToString() : "—";

        // ── Team list ─────────────────────────────────────────────────────────
        var team = SessionStats.CurrentTeam;
        if (team.Count != _teamList.Items.Count ||
            (team.Count > 0 && team[0].Id.ToString() != (_teamList.Items.Count > 0 ? _teamList.Items[0].SubItems[2].Text : "")))
        {
            _teamList.Items.Clear();
            for (int i = 0; i < team.Count; i++)
            {
                var t = team[i];
                var item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(t.TypeName);
                item.SubItems.Add(t.Id.ToString());
                item.SubItems.Add(t.Power   > 0 ? t.Power.ToString()   : "—");
                item.SubItems.Add(t.Stamina > 0 ? t.Stamina.ToString() : "—");
                item.SubItems.Add(t.Agility > 0 ? t.Agility.ToString() : "—");
                item.SubItems.Add(t.Total   > 0 ? t.Total.ToString()   : "—");
                item.ForeColor = t.Type == 2
                    ? Color.FromArgb(120, 80, 180)   // familiar = purple
                    : Color.FromArgb(30, 100, 170);  // player   = blue
                _teamList.Items.Add(item);
            }
        }

        // ── Loot feed ─────────────────────────────────────────────────────────
        var loot = SessionStats.RecentLoot;
        if (loot.Count != _lootListCount)
        {
            _lootListCount = loot.Count;
            _lootList.BeginUpdate();
            _lootList.Items.Clear();
            foreach (var entry in loot) _lootList.Items.Add(entry);
            _lootList.EndUpdate();
            if (_lootList.Items.Count > 0)
                _lootList.TopIndex = _lootList.Items.Count - 1;
        }

        // ── Log lines ─────────────────────────────────────────────────────────
        FlushPendingLogLines();
    }

    // ── Log callback (enqueue only — no WinForms calls here) ─────────────────

    // Called on whatever thread writes to the Logger (SFS2X socket thread, UI thread, etc.).
    // We NEVER touch WinForms controls here. The queue is drained by _uiTimer on the UI thread.
    private void OnLogLine(LogLevel level, string line) =>
        _pendingLogLines.Enqueue((level, line));

    // ── Log flush (called by RefreshStats on the UI timer) ───────────────────

    private void FlushPendingLogLines()
    {
        if (_pendingLogLines.IsEmpty) return;
        if (IsDisposed || !IsHandleCreated) return;

        bool added = false;
        _logBox.BeginUpdate();
        while (_pendingLogLines.TryDequeue(out var entry))
        {
            Color c = entry.Level switch
            {
                LogLevel.Warn  => Color.FromArgb(255, 200, 60),
                LogLevel.Error => Color.FromArgb(230, 90, 90),
                LogLevel.Debug => Color.FromArgb(120, 120, 120),
                _              => Color.FromArgb(200, 200, 200)
            };
            _logBox.Items.Add((c, entry.Line));
            added = true;
        }

        // Trim oldest lines if buffer grew too large.
        while (_logBox.Items.Count > 550)
            _logBox.Items.RemoveAt(0);

        _logBox.EndUpdate();

        if (added)
            _logBox.TopIndex = _logBox.Items.Count - 1;
    }

    // ── Config bind ───────────────────────────────────────────────────────────

    private void LoadConfigToUi()
    {
        PopulateQueueList();
        if (_queueList.Items.Count > 0)
        {
            _queueList.Items[0].Selected = true;
            LoadSelectionToEditFields();
        }

        _autoDailyReward.Checked = _config.Automation.AutoClaimDailyReward;
        _autoDailyQuests.Checked = _config.Automation.AutoClaimDailyQuests;
        _autoDecline.Checked = _config.Automation.AutoDeclineCaptures;
        _abandonOrphaned.Checked = _config.Automation.AbandonOrphanedDungeon;
        _autoAssignTeam.Checked = _config.Automation.AutoAssignTeammates;
        _energyWait.Value = Math.Clamp(_config.Automation.EnergyWaitMinutes, 0, 120);
        _tutorialHandling.SelectedIndex = (int)_config.Automation.TutorialHandling;
    }

    private void SaveUiToConfig()
    {
        ApplyEditToSelectedEntry();

        _config.Automation.AutoClaimDailyReward = _autoDailyReward.Checked;
        _config.Automation.AutoClaimDailyQuests = _autoDailyQuests.Checked;
        _config.Automation.AutoDeclineCaptures = _autoDecline.Checked;
        _config.Automation.AbandonOrphanedDungeon = _abandonOrphaned.Checked;
        _config.Automation.AutoAssignTeammates = _autoAssignTeam.Checked;
        _config.Automation.EnergyWaitMinutes = (int)_energyWait.Value;
        _config.Automation.TutorialHandling = (TutorialHandling)_tutorialHandling.SelectedIndex;
    }

    // ── Misc ──────────────────────────────────────────────────────────────────

    private void SetStatus(string text, Color colour)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text, colour)); return; }
        _statusLabel.Text = $"●  {text}";
        _statusLabel.ForeColor = colour;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopBot();
        _uiTimer.Stop();
        Logger.LineWritten -= OnLogLine;
        base.OnFormClosing(e);
    }
}

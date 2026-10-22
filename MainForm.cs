using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OBSClipper;

sealed class MainForm : Form
{
    static readonly Icon IconReady = MakeIcon(Theme.Success);
    static readonly Icon IconWarn = MakeIcon(Theme.Warning);
    static readonly Icon IconOff = MakeIcon(Color.FromArgb(140, 140, 140));

    readonly AppConfig config = AppConfig.Load();
    readonly ObsClient obs = new();
    readonly HotkeyPoller poller = new();
    readonly ClipSaver saver;
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip trayMenu = new() { Renderer = new DarkMenuRenderer(), Font = Theme.Body };
    readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 2000 };

    readonly StatusLabel obsStatus = new() { Margin = new Padding(0, 0, 18, 0) };
    readonly StatusLabel bufferStatus = new() { Margin = Padding.Empty };
    readonly ModernButton obsButton = new() { AutoSize = true, MinimumSize = new Size(116, 32) };
    readonly ModernButton bufferButton = new() { AutoSize = true, MinimumSize = new Size(116, 32) };
    readonly TableLayoutPanel hotkeyCards = new() { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
    readonly InputBox clipsFolder = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
    readonly ToggleSwitch connectBox = new() { Text = "Connect to OBS" };
    readonly ToggleSwitch startObsBox = new() { Text = "Start OBS when OBS Clipper starts" };
    readonly ToggleSwitch keepBufferBox = new() { Text = "Turn the replay buffer on automatically" };
    readonly ToggleSwitch startupBox = new() { Text = "Start OBS Clipper with Windows (in the tray)" };
    readonly ToggleSwitch soundBox = new() { Text = "Play a sound when a clip is saved" };

    bool allowShow, exiting, loading, busy, bufferActive, toldAboutTray;
    HotkeyBinding? capturing;
    ModernButton? captureButton;

    public MainForm(bool startHidden, EventWaitHandle showSignal, EventWaitHandle exitSignal)
    {
        allowShow = !startHidden;
        saver = new ClipSaver(() => config);
        Text = "OBS Clipper";
        Font = Theme.Body;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ClientSize = new Size(600, 493);
        // Can't be made smaller than it opens at.
        MinimumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = IconReady;

        BuildLayout();
        LoadValues();
        _ = Handle;

        tray.Icon = IconOff;
        tray.Text = "OBS Clipper";
        tray.ContextMenuStrip = trayMenu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        tray.Visible = true;
        RebuildTrayMenu();

        poller.Pressed += b => _ = SaveClipAsync(b);
        poller.SetBindings(config.Hotkeys);
        saver.ClipReady += _ => Ui(() =>
        {
            if (config.PlaySound) SystemSounds.Asterisk.Play();
        });
        obs.ConnectionChanged += connected => Ui(() =>
        {
            if (!connected) bufferActive = false;
            UpdateStatus();
            if (connected) _ = OnConnectedAsync();
        });
        obs.ReplayBufferChanged += active => Ui(() => { bufferActive = active; UpdateStatus(); });
        obs.ReplaySaved += saver.HandleReplay;
        obs.RecordingStopped += saver.HandleRecording;
        obs.Start();

        new Thread(() => WaitForSignal(showSignal, ShowFromTray)) { IsBackground = true }.Start();
        // A newer version being installed asks this one to exit.
        new Thread(() => WaitForSignal(exitSignal, ExitApp)) { IsBackground = true }.Start();
        statusTimer.Tick += (_, _) => UpdateStatus();
        statusTimer.Start();

        if (!config.SetupDone) BeginInvoke(RunSetup);
        else if (config.StartObs && !ObsProcess.IsRunning()) TryStartObs();
        UpdateStatus();
    }

    void RunSetup()
    {
        if (busy) return;
        busy = true;
        try
        {
            using var form = new SetupForm(new Setup(config, obs), config.ClipsFolder, IconReady);
            form.ShowDialog();
            if (form.Completed)
            {
                config.SetupDone = true;
                config.Save();
            }
        }
        finally
        {
            busy = false;
        }
        loading = true;
        connectBox.Checked = ObsWebSocketConfig.Read().Enabled;
        loading = false;
        UpdateStatus();
        ShowFromTray();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    void BuildLayout()
    {
        // Hotkeys page
        var hotkeys = Column();
        var headers = new Panel { Height = 22, Dock = DockStyle.Top, Margin = Padding.Empty };
        headers.Controls.Add(Caption("KEY", 14));
        headers.Controls.Add(Caption("CLIP LENGTH", 124));
        hotkeys.Controls.Add(headers);
        hotkeys.Controls.Add(hotkeyCards);
        var add = new ModernButton { Text = "Add hotkey", Suffix = "+", SuffixFont = new Font(Theme.Body.FontFamily, 15f), Kind = ButtonKind.Ghost,
                                     AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        add.Click += (_, _) =>
        {
            config.Hotkeys.Add(new HotkeyBinding { Seconds = 30 });
            SaveHotkeys();
            RebuildHotkeyCards();
        };
        hotkeys.Controls.Add(add);

        // Settings page
        var settings = Column();
        settings.Controls.Add(SectionTitle("CLIPS FOLDER", first: true));
        var folderRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new ModernButton { Text = "Browse…", AutoSize = true, Margin = Padding.Empty };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = clipsFolder.Box.Text, Description = "Folder Medal imports clips from" };
            if (dialog.ShowDialog(this) == DialogResult.OK) clipsFolder.Box.Text = dialog.SelectedPath;
        };
        folderRow.Controls.Add(clipsFolder, 0, 0);
        folderRow.Controls.Add(browse, 1, 0);
        settings.Controls.Add(folderRow);
        settings.Controls.Add(Muted("Medal imports clips from this folder."));

        var connectHint = Muted("Needed to save clips. Turns on OBS's WebSocket server.");
        connectHint.Margin = new Padding(46, 0, 0, 8);
        var obsButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 2) };
        obsButton.Margin = new Padding(0, 0, 8, 0);
        bufferButton.Margin = new Padding(0, 0, 8, 0);
        var showObs = new ModernButton { Text = "Show OBS", AutoSize = true, MinimumSize = new Size(116, 32), Margin = Padding.Empty };
        showObs.Click += (_, _) =>
        {
            if (!ObsProcess.Show()) MessageBox.Show(this, "OBS isn't running.", "OBS Clipper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        showObs.Margin = new Padding(0, 0, 8, 0);
        var help = new ModernButton { Text = "Get help", Suffix = "▾", AutoSize = true, MinimumSize = new Size(116, 32), Margin = Padding.Empty };
        var helpMenu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), Font = Theme.Body };
        helpMenu.Items.Add("Copy diagnostics", null, async (_, _) => await CopyDiagnosticsAsync());
        helpMenu.Items.Add("Remote help (Quick Assist)", null, (_, _) =>
        {
            if (!RemoteHelp.Open()) MessageBox.Show(this, "Couldn't open Quick Assist. Search for it in the Start menu.", "OBS Clipper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        help.Click += (_, _) => helpMenu.Show(help, new Point(0, help.Height + 2));
        obsButtons.Controls.Add(obsButton);
        obsButtons.Controls.Add(bufferButton);
        obsButtons.Controls.Add(showObs);
        obsButtons.Controls.Add(help);
        var toggles = Card(connectBox, connectHint, startObsBox, keepBufferBox, startupBox, soundBox, obsButtons);
        toggles.Margin = new Padding(0, 20, 0, 0);
        settings.Controls.Add(toggles);
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionLabel = Muted($"OBS Clipper {version?.ToString(3)}");
        versionLabel.Margin = new Padding(2, 12, 0, 0);
        settings.Controls.Add(versionLabel);

        var hotkeysPage = Page(hotkeys);
        var settingsPage = Page(settings);
        settingsPage.Visible = false;

        // Tabs
        var header = new Panel { Dock = DockStyle.Top, Height = 44 };
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(14, 4, 0, 0) };
        var hotkeysTab = new TabButton("Hotkeys") { Selected = true };
        var settingsTab = new TabButton("Settings");
        hotkeysTab.Click += (_, _) => SelectTab(hotkeysTab, hotkeysPage);
        settingsTab.Click += (_, _) => SelectTab(settingsTab, settingsPage);
        void SelectTab(TabButton tab, Control page)
        {
            CancelCapture();
            hotkeysTab.Selected = tab == hotkeysTab;
            settingsTab.Selected = tab == settingsTab;
            hotkeysPage.Visible = page == hotkeysPage;
            settingsPage.Visible = page == settingsPage;
        }
        tabs.Controls.Add(hotkeysTab);
        tabs.Controls.Add(settingsTab);
        header.Controls.Add(tabs);
        header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border });

        // Status bar under both tabs
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Theme.Bar };
        var statuses = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(20, 7, 0, 0) };
        statuses.Controls.Add(obsStatus);
        statuses.Controls.Add(bufferStatus);
        // "Done" sends both OBS and this window to the tray.
        var minimizeAll = new ModernButton { Text = "Done", AutoSize = true, MinimumSize = new Size(80, 32), Margin = Padding.Empty };
        minimizeAll.Click += (_, _) =>
        {
            ObsProcess.HideToTray();
            CancelCapture();
            Hide();
        };
        var barButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, 7, 18, 0) };
        barButtons.Controls.Add(minimizeAll);
        bar.Controls.Add(statuses);
        bar.Controls.Add(barButtons);
        bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border });
        obsButton.Click += async (_, _) => await RestartObsAsync();
        bufferButton.Click += async (_, _) => await obs.RequestAsync(bufferActive ? "StopReplayBuffer" : "StartReplayBuffer");

        // Fill-docked pages first, so the header and bar take their space before them.
        Controls.Add(hotkeysPage);
        Controls.Add(settingsPage);
        Controls.Add(header);
        Controls.Add(bar);

        clipsFolder.Box.TextChanged += (_, _) => { if (loading) return; config.ClipsFolder = clipsFolder.Box.Text.Trim(); config.Save(); };
        connectBox.CheckedChanged += async (_, _) => { if (!loading) await SetConnectAsync(connectBox.Checked); };
        startObsBox.CheckedChanged += (_, _) => { if (loading) return; config.StartObs = startObsBox.Checked; config.Save(); };
        keepBufferBox.CheckedChanged += async (_, _) =>
        {
            if (loading) return;
            config.KeepReplayBufferOn = keepBufferBox.Checked;
            config.Save();
            if (config.KeepReplayBufferOn && obs.Connected && !bufferActive) await obs.RequestAsync("StartReplayBuffer");
        };
        startupBox.CheckedChanged += (_, _) => { if (!loading) Startup.Set(startupBox.Checked); };
        soundBox.CheckedChanged += (_, _) => { if (loading) return; config.PlaySound = soundBox.Checked; config.Save(); };
    }

    void LoadValues()
    {
        loading = true;
        clipsFolder.Box.Text = config.ClipsFolder;
        connectBox.Checked = ObsWebSocketConfig.Read().Enabled;
        startObsBox.Checked = config.StartObs;
        keepBufferBox.Checked = config.KeepReplayBufferOn;
        startupBox.Checked = Startup.IsEnabled;
        soundBox.Checked = config.PlaySound;
        RebuildHotkeyCards();
        loading = false;
    }

    void RebuildHotkeyCards()
    {
        hotkeyCards.SuspendLayout();
        foreach (var control in hotkeyCards.Controls.Cast<Control>().ToList()) control.Dispose();
        hotkeyCards.Controls.Clear();
        foreach (var binding in config.Hotkeys)
        {
            var card = new RoundedPanel { Height = 52, Width = 540, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            var key = new ModernButton { Text = KeyNames.Describe(binding), Kind = ButtonKind.Key, Font = Theme.Strong, Bounds = new Rectangle(10, 10, 100, 32) };
            // Seconds 0 (whole buffer) from older configs shows as 60, the buffer length.
            var seconds = new Stepper { Location = new Point(120, 10), Value = binding.Seconds > 0 ? binding.Seconds : 60 };
            var unit = new Label { Text = "seconds", AutoSize = true, ForeColor = Theme.Muted, Location = new Point(216, 18) };
            var remove = new ModernButton { Text = "✕", Kind = ButtonKind.Danger, Padding = Padding.Empty, Bounds = new Rectangle(card.Width - 42, 10, 32, 32),
                                            Anchor = AnchorStyles.Top | AnchorStyles.Right };

            key.Click += (_, _) => StartCapture(binding, key);
            key.MouseDown += (_, e) =>
            {
                if (capturing != binding) return;
                int vk = e.Button switch { MouseButtons.Middle => 0x04, MouseButtons.XButton1 => 0x05, MouseButtons.XButton2 => 0x06, _ => 0 };
                if (vk != 0) FinishCapture(vk, ModifierKeys.HasFlag(Keys.Control), ModifierKeys.HasFlag(Keys.Alt), ModifierKeys.HasFlag(Keys.Shift));
            };
            seconds.ValueChanged += (_, _) =>
            {
                binding.Seconds = seconds.Value;
                SaveHotkeys();
            };
            remove.Click += (_, _) =>
            {
                CancelCapture();
                config.Hotkeys.Remove(binding);
                SaveHotkeys();
                // Not inline: this would dispose the button whose click is still running.
                BeginInvoke(RebuildHotkeyCards);
            };
            card.Controls.AddRange(new Control[] { key, seconds, unit, remove });
            hotkeyCards.Controls.Add(card);
        }
        hotkeyCards.ResumeLayout();
    }

    void SaveHotkeys()
    {
        config.Save();
        poller.SetBindings(config.Hotkeys);
        RebuildTrayMenu();
    }

    void StartCapture(HotkeyBinding binding, ModernButton button)
    {
        CancelCapture();
        capturing = binding;
        captureButton = button;
        poller.Paused = true;
        button.Text = "Press a key…";
        button.Highlighted = true;
        button.Focus();
    }

    void FinishCapture(int vk, bool ctrl, bool alt, bool shift)
    {
        if (capturing == null || captureButton == null) return;
        capturing.Key = vk;
        capturing.Ctrl = ctrl;
        capturing.Alt = alt;
        capturing.Shift = shift;
        captureButton.Text = KeyNames.Describe(capturing);
        captureButton.Highlighted = false;
        SaveHotkeys();
        EndCapture();
    }

    void CancelCapture()
    {
        if (capturing == null || captureButton == null) return;
        if (!captureButton.IsDisposed)
        {
            captureButton.Text = KeyNames.Describe(capturing);
            captureButton.Highlighted = false;
        }
        EndCapture();
    }

    void EndCapture()
    {
        capturing = null;
        captureButton = null;
        poller.Paused = false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        if (capturing != null && (msg.Msg == WM_KEYDOWN || msg.Msg == WM_SYSKEYDOWN))
        {
            var code = keyData & Keys.KeyCode;
            if (code == Keys.Escape) CancelCapture();
            else if (code is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin))
                FinishCapture((int)code, keyData.HasFlag(Keys.Control), keyData.HasFlag(Keys.Alt), keyData.HasFlag(Keys.Shift));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        CancelCapture();
        base.OnDeactivate(e);
    }

    async Task SaveClipAsync(HotkeyBinding binding)
    {
        if (!obs.Connected)
        {
            Log.Write("Hotkey pressed, but OBS isn't connected");
            return;
        }
        saver.Expect(binding.Seconds);
        if (!ObsClient.Succeeded(await obs.RequestAsync("SaveReplayBuffer")))
        {
            saver.Unexpect(binding.Seconds);
            Log.Write("OBS couldn't save the replay (is the replay buffer on?)");
        }
    }

    async Task CopyDiagnosticsAsync()
    {
        Cursor = Cursors.WaitCursor;
        string report;
        try
        {
            report = await Diagnostics.BuildAsync(config, obs);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
        Clipboard.SetText(report);
        MessageBox.Show(this, "Diagnostics copied. Paste them in a message to whoever's helping you.", "OBS Clipper", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    async Task OnConnectedAsync()
    {
        bufferActive = ObsClient.OutputActive(await obs.RequestAsync("GetReplayBufferStatus"));
        if (!bufferActive && config.KeepReplayBufferOn) await obs.RequestAsync("StartReplayBuffer");
        UpdateStatus();
    }

    async Task SetConnectAsync(bool enabled)
    {
        if (busy || ObsWebSocketConfig.Read().Enabled == enabled) return;
        bool running = ObsProcess.IsRunning();
        if (running && MessageBox.Show(this, "OBS has to restart for this to take effect. Restart OBS now?", "OBS Clipper",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            SetChecked(connectBox, !enabled);
            return;
        }
        busy = true;
        try
        {
            if (running && !await CloseObsAsync())
            {
                SetChecked(connectBox, !enabled);
                return;
            }
            ObsWebSocketConfig.SetEnabled(enabled);
            if (running || config.StartObs) TryStartObs();
        }
        finally
        {
            busy = false;
            UpdateStatus();
        }
    }

    async Task RestartObsAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            if (ObsProcess.IsRunning() && !await CloseObsAsync()) return;
            TryStartObs();
        }
        finally
        {
            busy = false;
            UpdateStatus();
        }
    }

    async Task<bool> CloseObsAsync()
    {
        obsButton.Enabled = connectBox.Enabled = false;
        obsStatus.Text = "Closing OBS…";
        obsStatus.DotColor = Theme.Warning;
        bool closed = await ObsProcess.CloseAsync(obs);
        if (!closed) MessageBox.Show(this, "OBS didn't close. Close it yourself, then try again.", "OBS Clipper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return closed;
    }

    void TryStartObs()
    {
        try
        {
            ObsProcess.Start(config.ObsPath);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Log.Write("Could not start OBS: " + ex.Message);
            if (Visible) MessageBox.Show(this, "Couldn't start OBS:\n" + ex.Message, "OBS Clipper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void UpdateStatus()
    {
        bool running = ObsProcess.IsRunning();
        bool wsEnabled = ObsWebSocketConfig.Read().Enabled;
        if (!busy)
        {
            if (connectBox.Checked != wsEnabled) SetChecked(connectBox, wsEnabled);
            obsButton.Enabled = connectBox.Enabled = true;
            obsStatus.Text = obs.Connected ? "OBS connected"
                : !running ? "OBS not running"
                : !wsEnabled ? "OBS not connected (see Settings)"
                : "Connecting to OBS…";
            obsStatus.DotColor = obs.Connected ? Theme.Success : running && wsEnabled ? Theme.Warning : Theme.Off;
        }
        obsButton.Text = running ? "Restart OBS" : "Start OBS";

        bufferStatus.Text = !obs.Connected ? "" : bufferActive ? "Replay buffer on" : "Replay buffer off";
        bufferStatus.DotColor = bufferActive ? Theme.Success : Theme.Warning;
        bufferButton.Text = bufferActive ? "Stop buffer" : "Start buffer";
        bufferButton.Enabled = obs.Connected;

        bool ready = obs.Connected && bufferActive;
        var icon = ready ? IconReady : obs.Connected ? IconWarn : IconOff;
        if (tray.Icon != icon) tray.Icon = icon;
        tray.Text = ready ? "OBS Clipper: ready" : obs.Connected ? "OBS Clipper: replay buffer off" : "OBS Clipper: OBS not connected";
    }

    void RebuildTrayMenu()
    {
        trayMenu.Items.Clear();
        trayMenu.Items.Add("Open OBS Clipper", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        foreach (var binding in config.Hotkeys.Where(b => b.Key != 0).GroupBy(b => b.Seconds).Select(g => g.First()).OrderBy(b => b.Seconds == 0 ? int.MaxValue : b.Seconds))
        {
            var copy = binding.Copy();
            trayMenu.Items.Add(copy.Seconds > 0 ? $"Save last {copy.Seconds} s" : "Save whole buffer", null, (_, _) => _ = SaveClipAsync(copy));
        }
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Run setup again…", null, (_, _) => RunSetup());
        trayMenu.Items.Add("Exit OBS Clipper", null, (_, _) => ExitApp());
    }

    void ShowFromTray()
    {
        allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        exiting = true;
        tray.Visible = false;
        poller.Dispose();
        obs.Stop();
        Application.Exit();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            // Closing the window keeps the hotkeys working from the tray.
            e.Cancel = true;
            CancelCapture();
            Hide();
            if (!toldAboutTray)
            {
                tray.ShowBalloonTip(3000, "OBS Clipper is still running", "Hotkeys keep working. Right-click the tray icon to exit.", ToolTipIcon.Info);
                toldAboutTray = true;
            }
            return;
        }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    void WaitForSignal(EventWaitHandle signal, Action action)
    {
        try
        {
            while (signal.WaitOne()) Ui(action);
        }
        catch (ObjectDisposedException) { }
    }

    void Ui(Action action)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
    }

    void SetChecked(CheckBox box, bool value)
    {
        loading = true;
        box.Checked = value;
        loading = false;
    }

    static Panel Page(Control content)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22, 16, 22, 16) };
        page.Controls.Add(content);
        Theme.DarkScrollbars(page);
        return page;
    }

    static TableLayoutPanel Column() =>
        new() { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty, ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) } };

    // A rounded card holding rows of controls, stacked top to bottom.
    static RoundedPanel Card(params Control[] rows)
    {
        var card = new RoundedPanel { Dock = DockStyle.Top, Margin = Padding.Empty };
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(14, 10), Margin = Padding.Empty };
        foreach (var row in rows)
        {
            if (row is ToggleSwitch) row.Margin = new Padding(0, 4, 0, 4);
            stack.Controls.Add(row);
        }
        card.Controls.Add(stack);
        card.Height = stack.PreferredSize.Height + 20;
        return card;
    }

    static Label SectionTitle(string text, bool first = false) =>
        new() { Text = text, AutoSize = true, Font = Theme.Caption, ForeColor = Theme.Muted, Margin = new Padding(2, first ? 0 : 20, 0, 8) };

    static Label Caption(string text, int x) =>
        new() { Text = text, AutoSize = true, Font = Theme.Caption, ForeColor = Theme.Muted, Location = new Point(x, 4) };

    static Label Muted(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Theme.Muted, MaximumSize = new Size(520, 0), Margin = new Padding(2, 6, 0, 6) };

    static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var dot = new SolidBrush(Color.White);
            g.FillEllipse(dot, 11, 11, 10, 10);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}

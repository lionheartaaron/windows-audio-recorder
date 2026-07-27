using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WindowAudioRecorder;

public sealed class MainForm : Form
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyRecord = 1;
    private const int HotkeyPause = 2;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int RecentLimit = 25;

    private static readonly Color Accent = Color.FromArgb(214, 62, 62);
    private static readonly Color Ink = Color.FromArgb(32, 36, 44);
    private static readonly Color Muted = Color.FromArgb(112, 118, 130);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceWatcher _watcher = new();
    private readonly LoopbackRecorder _recorder = new();
    private readonly List<DeviceItem> _devices = [];

    private readonly ComboBox _deviceBox = new();
    private readonly Button _refreshButton = new();
    private readonly CheckBox _followDefault = new();
    private readonly Label _formatInfo = new();

    private readonly Button _recordButton = new();
    private readonly Button _pauseButton = new();
    private readonly Label _timerLabel = new();
    private readonly LevelMeter _leftMeter = new();
    private readonly LevelMeter _rightMeter = new();

    private readonly ComboBox _formatBox = new();
    private readonly ComboBox _bitrateBox = new();
    private readonly ComboBox _rateBox = new();
    private readonly NumericUpDown _customRate = new();
    private readonly ComboBox _channelBox = new();
    private readonly TrackBar _gainTrack = new();
    private readonly Label _gainLabel = new();

    private readonly TextBox _folderBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _openButton = new();
    private readonly TextBox _templateBox = new();
    private readonly NumericUpDown _splitBox = new();
    private readonly NumericUpDown _maxBox = new();
    private readonly CheckBox _monitorIdle = new();
    private readonly CheckBox _trayCheck = new();
    private readonly Label _diskLabel = new();

    private readonly ListView _recentList = new();
    private readonly Label _statusLabel = new();
    private readonly Label _fileLabel = new();

    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private TableLayoutPanel _root = null!;
    private bool _sized;
    private bool _loadingDevices;
    private bool _busy;
    private bool _hotkeysRegistered;
    private int _diskTicks;

    public MainForm()
    {
        BuildLayout();
        ApplySettings();

        _recorder.SegmentCompleted += OnSegmentCompleted;
        _recorder.Aborted += OnRecorderAborted;
        _recorder.AutoStopped += OnAutoStopped;

        _watcher.ListChanged += () => BeginInvoke(OnDeviceListChanged);
        _watcher.DefaultChanged += id => BeginInvoke(() => OnDefaultDeviceChanged(id));
        _enumerator.RegisterEndpointNotificationCallback(_watcher);

        _uiTimer.Interval = 33;
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        LoadDevices();
        LoadRecent();
    }

    // ---- layout ---------------------------------------------------------------------------

    private void BuildLayout()
    {
        Text = "Windows Audio Recorder";
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;
        ForeColor = Ink;
        Icon = AppIcons.Idle;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;

        // Everything is laid out by preferred size rather than fixed pixels, so nothing clips
        // when the display scaling (and therefore the font) differs from the design machine.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(14, 12, 14, 10)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        foreach (var size in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize })
        {
            root.RowStyles.Add(new RowStyle(size));
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AddSpan(root, BuildDeviceRow(), 0);
        AddSpan(root, BuildTransport(), 1);
        root.Controls.Add(BuildOutputGroup(), 0, 2);
        root.Controls.Add(BuildFilesGroup(), 1, 2);
        AddSpan(root, BuildRecentGroup(), 3);

        _statusLabel.Text = "Ready.";
        _statusLabel.Margin = new Padding(2, 8, 2, 0);
        _fileLabel.ForeColor = Muted;
        _fileLabel.Margin = new Padding(2, 2, 2, 0);
        foreach (var label in new[] { _statusLabel, _fileLabel })
        {
            // AutoEllipsis needs a fixed width, so these two stay docked rather than auto-sized.
            label.AutoSize = false;
            label.UseMnemonic = false;   // file names contain underscores
            label.AutoEllipsis = true;
            label.Height = Font.Height + 4;
            label.Dock = DockStyle.Fill;
        }
        AddSpan(root, _statusLabel, 4);
        AddSpan(root, _fileLabel, 5);

        Controls.Add(root);
        _root = root;

        BuildTray();
        ActiveControl = _recordButton;
    }

    private static void AddSpan(TableLayoutPanel root, Control control, int row)
    {
        root.Controls.Add(control, 0, row);
        root.SetColumnSpan(control, 2);
    }

    private Control BuildDeviceRow()
    {
        _deviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceBox.Dock = DockStyle.Fill;
        _deviceBox.Margin = new Padding(0, 4, 6, 4);
        _deviceBox.SelectedIndexChanged += OnDeviceSelected;

        _refreshButton.Text = "⟳";
        StyleButton(_refreshButton, new Padding(5, 2, 5, 2));
        _refreshButton.Click += (_, _) => LoadDevices();
        new ToolTip().SetToolTip(_refreshButton, "Rescan playback devices");

        _followDefault.Text = "Follow default";
        _followDefault.AutoSize = true;
        _followDefault.Anchor = AnchorStyles.Left;
        _followDefault.Margin = new Padding(10, 4, 0, 4);
        _followDefault.CheckedChanged += (_, _) =>
        {
            _settings.FollowDefaultDevice = _followDefault.Checked;
            if (_followDefault.Checked) SelectDefaultDevice();
        };

        _formatInfo.AutoSize = true;
        _formatInfo.UseMnemonic = false;
        _formatInfo.Anchor = AnchorStyles.Left;
        _formatInfo.ForeColor = Muted;
        _formatInfo.Margin = new Padding(10, 4, 0, 4);

        var row = Row([FieldLabel("Output device"), _deviceBox, _refreshButton, _followDefault],
                      [0f, 100f, 0f, 0f]);
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        stack.Controls.Add(row, 0, 0);
        stack.Controls.Add(_formatInfo, 0, 1);
        return stack;
    }

    private Control BuildTransport()
    {
        _recordButton.Text = "●  Record";
        _recordButton.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold);
        _recordButton.FlatStyle = FlatStyle.Flat;
        _recordButton.FlatAppearance.BorderSize = 0;
        _recordButton.BackColor = Accent;
        _recordButton.ForeColor = Color.White;
        _recordButton.AutoSize = true;
        _recordButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _recordButton.Padding = new Padding(20, 18, 20, 18);
        _recordButton.Margin = new Padding(0, 0, 10, 0);
        _recordButton.Anchor = AnchorStyles.Left;
        _recordButton.Click += OnRecordClicked;

        _pauseButton.Text = "❚❚  Pause";
        _pauseButton.Font = new Font("Segoe UI Semibold", 10F);
        StyleButton(_pauseButton, new Padding(12, 18, 12, 18));
        _pauseButton.Margin = new Padding(0, 0, 16, 0);
        // The themed button paints its rounded chrome several pixels inside its own bounds, so next
        // to the flat Record block it reads as a smaller box floating off the shared edges. Drawing
        // Pause flat as well makes the painted rectangle and the layout rectangle the same thing.
        _pauseButton.FlatStyle = FlatStyle.Flat;
        _pauseButton.FlatAppearance.BorderSize = 1;
        _pauseButton.FlatAppearance.BorderColor = Color.FromArgb(205, 209, 217);
        _pauseButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 244, 246);
        _pauseButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(232, 234, 238);
        _pauseButton.BackColor = Color.White;
        _pauseButton.ForeColor = Ink;
        // Left-only anchor puts both buttons on one centre line. Record's heavier font makes it the
        // taller of the two, so Pause tracks whatever height Record actually ends up with - a size
        // captured here instead would drift by a pixel once display scaling has been applied.
        _pauseButton.Anchor = AnchorStyles.Left;
        _recordButton.SizeChanged += (_, _) =>
            _pauseButton.MinimumSize = _pauseButton.MaximumSize = new Size(0, _recordButton.Height);
        _pauseButton.Enabled = false;
        _pauseButton.Click += OnPauseClicked;

        _timerLabel.Text = "00:00:00";
        _timerLabel.Font = new Font("Consolas", 20F);
        _timerLabel.AutoSize = true;
        _timerLabel.UseMnemonic = false;
        _timerLabel.ForeColor = Muted;
        _timerLabel.Margin = new Padding(0, 0, 0, 6);

        foreach (var (meter, caption) in new[] { (_leftMeter, "L"), (_rightMeter, "R") })
        {
            meter.Caption = caption;
            meter.Dock = DockStyle.Fill;
            meter.Height = Font.Height + 4;
            meter.Margin = new Padding(0, 2, 0, 2);
        }

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        right.Controls.Add(_timerLabel, 0, 0);
        right.Controls.Add(_leftMeter, 0, 1);
        right.Controls.Add(_rightMeter, 0, 2);

        var transport = Row([_recordButton, _pauseButton, right], [0f, 0f, 100f]);
        transport.Margin = new Padding(0, 12, 0, 12);
        return transport;
    }

    private Control BuildOutputGroup()
    {
        _formatBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _formatBox.Items.AddRange([
            new Choice<OutputFormat>("WAV · 16-bit PCM", OutputFormat.Wav16),
            new Choice<OutputFormat>("WAV · 24-bit PCM", OutputFormat.Wav24),
            new Choice<OutputFormat>("WAV · 32-bit float", OutputFormat.Wav32Float),
            new Choice<OutputFormat>("MP3", OutputFormat.Mp3)
        ]);
        _formatBox.SelectedIndexChanged += (_, _) =>
        {
            _settings.Format = Selected<OutputFormat>(_formatBox);
            UpdateFormatDependants();
        };

        foreach (int bitrate in AppSettings.Mp3Bitrates)
        {
            _bitrateBox.Items.Add(new Choice<int>($"{bitrate} kbps", bitrate));
        }
        _bitrateBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _bitrateBox.SelectedIndexChanged += (_, _) =>
        {
            _settings.Mp3Bitrate = Selected<int>(_bitrateBox);
            UpdateEstimates();
        };

        _rateBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _rateBox.Items.Add(new Choice<int>("Device native", 0));
        foreach (int rate in AppSettings.StandardRates)
        {
            _rateBox.Items.Add(new Choice<int>($"{rate:n0} Hz", rate));
        }
        _rateBox.Items.Add(new Choice<int>("Custom…", -1));
        _rateBox.SelectedIndexChanged += (_, _) =>
        {
            int value = Selected<int>(_rateBox);
            _customRate.Enabled = value == -1;
            _settings.SampleRate = value == -1 ? (int)_customRate.Value : value;
            UpdateEstimates();
        };

        _customRate.Minimum = 4000;
        _customRate.Maximum = 384000;
        _customRate.Increment = 1000;
        _customRate.ThousandsSeparator = true;
        _customRate.Enabled = false;
        _customRate.ValueChanged += (_, _) =>
        {
            if (_customRate.Enabled) _settings.SampleRate = (int)_customRate.Value;
            UpdateEstimates();
        };

        _channelBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _channelBox.Items.AddRange([
            new Choice<ChannelMode>("Device native", ChannelMode.Native),
            new Choice<ChannelMode>("Stereo (downmix)", ChannelMode.Stereo),
            new Choice<ChannelMode>("Mono (downmix)", ChannelMode.Mono)
        ]);
        _channelBox.SelectedIndexChanged += (_, _) =>
        {
            _settings.Channels = Selected<ChannelMode>(_channelBox);
            UpdateEstimates();
        };

        _gainTrack.Minimum = -20;
        _gainTrack.Maximum = 20;
        _gainTrack.TickFrequency = 5;
        _gainTrack.Width = Font.Height * 11;
        _gainTrack.Anchor = AnchorStyles.Left;
        _gainTrack.ValueChanged += (_, _) =>
        {
            _settings.GainDb = _gainTrack.Value;
            _gainLabel.Text = $"{_gainTrack.Value:+0;-0;0} dB";
            _recorder.SetGain(_gainTrack.Value);   // takes effect mid-take
        };
        _gainLabel.AutoSize = true;
        _gainLabel.Anchor = AnchorStyles.Left;
        _gainLabel.UseMnemonic = false;
        _gainLabel.Margin = new Padding(8, 6, 0, 4);

        foreach (var box in new[] { _formatBox, _bitrateBox, _rateBox, _channelBox })
        {
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 4, 0, 4);
        }
        _customRate.Dock = DockStyle.Fill;
        _customRate.Margin = new Padding(0, 4, 0, 4);

        var grid = Grid();
        AddField(grid, 0, "Format", _formatBox);
        AddField(grid, 1, "MP3 bitrate", _bitrateBox);
        AddField(grid, 2, "Sample rate", _rateBox);
        AddField(grid, 3, "Custom Hz", _customRate);
        AddField(grid, 4, "Channels", _channelBox);
        AddField(grid, 5, "Gain", Row([_gainTrack, _gainLabel], [0f, 100f]));
        return GroupBox("Output", grid);
    }

    private Control BuildFilesGroup()
    {
        _folderBox.ReadOnly = true;
        _folderBox.TabStop = false;
        _folderBox.Dock = DockStyle.Fill;
        _folderBox.Margin = new Padding(0, 4, 6, 4);

        _browseButton.Text = "Browse…";
        StyleButton(_browseButton, new Padding(6, 2, 6, 2));
        _browseButton.Click += OnBrowseClicked;

        _openButton.Text = "Open";
        StyleButton(_openButton, new Padding(6, 2, 6, 2));
        _openButton.Margin = new Padding(6, 4, 0, 4);
        _openButton.Click += (_, _) => OpenFolder();

        _templateBox.Dock = DockStyle.Fill;
        _templateBox.Margin = new Padding(0, 4, 0, 4);
        _templateBox.TextChanged += (_, _) => _settings.FileTemplate = _templateBox.Text;
        new ToolTip().SetToolTip(_templateBox, "Tokens: {date} {time} {datetime} {device} {n}");

        foreach (var box in new[] { _splitBox, _maxBox })
        {
            box.Minimum = 0;
            box.Maximum = 1440;
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(0, 4, 0, 4);
        }
        _splitBox.ValueChanged += (_, _) => _settings.SplitMinutes = (int)_splitBox.Value;
        _maxBox.ValueChanged += (_, _) => _settings.MaxMinutes = (int)_maxBox.Value;

        _monitorIdle.Text = "Monitor levels when idle";
        _trayCheck.Text = "Minimize to tray";
        foreach (var check in new[] { _monitorIdle, _trayCheck })
        {
            check.AutoSize = true;
            check.Anchor = AnchorStyles.Left;
            check.Margin = new Padding(0, 4, 12, 2);
        }
        _monitorIdle.CheckedChanged += OnMonitorIdleChanged;
        _trayCheck.CheckedChanged += (_, _) => _settings.MinimizeToTray = _trayCheck.Checked;

        _diskLabel.AutoSize = true;
        _diskLabel.UseMnemonic = false;
        _diskLabel.ForeColor = Muted;
        _diskLabel.Margin = new Padding(0, 6, 0, 0);

        var grid = Grid();
        AddField(grid, 0, "Save to", Row([_folderBox, _browseButton, _openButton], [100f, 0f, 0f]));
        AddField(grid, 1, "Name", _templateBox);
        AddField(grid, 2, "Split every", WithSuffix(_splitBox, "minutes  (0 = one file)"));
        AddField(grid, 3, "Stop after", WithSuffix(_maxBox, "minutes  (0 = no limit)"));
        grid.Controls.Add(Row([_monitorIdle, _trayCheck], [0f, 100f]), 1, 4);
        grid.Controls.Add(_diskLabel, 1, 5);
        return GroupBox("Files && limits", grid);   // && escapes the mnemonic marker
    }

    private Control BuildRecentGroup()
    {
        _recentList.View = View.Details;
        _recentList.FullRowSelect = true;
        _recentList.MultiSelect = false;
        _recentList.HideSelection = false;
        _recentList.Dock = DockStyle.Fill;
        _recentList.Columns.Add("File", 100);
        _recentList.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _recentList.Columns.Add("Saved", 100);
        _recentList.DoubleClick += (_, _) => OpenSelectedRecording();

        // Proportional rather than fixed: ListView column widths are not DPI-scaled by WinForms,
        // so hard-coded pixels overflow the control on a scaled display.
        _recentList.Resize += (_, _) => LayoutRecentColumns();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenSelectedRecording());
        menu.Items.Add("Show in Explorer", null, (_, _) => RevealSelectedRecording());
        menu.Items.Add("Copy path", null, (_, _) => CopySelectedPath());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove from list", null, (_, _) => RemoveSelected(deleteFile: false));
        menu.Items.Add("Delete file…", null, (_, _) => RemoveSelected(deleteFile: true));
        _recentList.ContextMenuStrip = menu;

        return GroupBox("Recent recordings", _recentList, fill: true);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show window", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Start / stop recording", null, (_, _) => OnRecordClicked(this, EventArgs.Empty));
        menu.Items.Add("Open folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _tray.Icon = AppIcons.Idle;
        _tray.Text = "Windows Audio Recorder";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    // ---- small layout helpers --------------------------------------------------------------

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        UseMnemonic = false,
        Anchor = AnchorStyles.Left,
        ForeColor = Color.FromArgb(72, 78, 90),
        Margin = new Padding(0, 8, 12, 8)
    };

    private static void StyleButton(Button button, Padding padding)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Padding = padding;
        button.Margin = new Padding(0, 4, 0, 4);
    }

    /// <summary>Builds one horizontal strip; a weight of 0 means "size to the control".</summary>
    private static TableLayoutPanel Row(Control[] controls, float[] weights)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = controls.Length,
            Margin = Padding.Empty
        };
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        for (int i = 0; i < controls.Length; i++)
        {
            row.ColumnStyles.Add(weights[i] > 0f
                ? new ColumnStyle(SizeType.Percent, weights[i])
                : new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(controls[i], i, 0);
        }

        return row;
    }

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        return grid;
    }

    private static void AddField(TableLayoutPanel grid, int row, string label, Control control)
    {
        grid.Controls.Add(FieldLabel(label), 0, row);
        grid.Controls.Add(control, 1, row);
    }

    private static Control WithSuffix(Control control, string suffix)
    {
        control.Width = 70;
        control.Anchor = AnchorStyles.Left;
        var label = FieldLabel(suffix);
        label.ForeColor = Muted;
        label.Margin = new Padding(8, 8, 0, 8);
        return Row([control, label], [0f, 100f]);
    }

    private static GroupBox GroupBox(string title, Control content, bool fill = false)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = !fill,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 8, 8)
        };
        box.Controls.Add(content);
        return box;
    }

    private sealed class Choice<T>(string text, T value)
    {
        public T Value { get; } = value;
        public override string ToString() => text;
    }

    private static T Selected<T>(ComboBox box) => ((Choice<T>)box.SelectedItem!).Value;

    private static void Select<T>(ComboBox box, T value)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0 && box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    // ---- settings ---------------------------------------------------------------------------

    private void ApplySettings()
    {
        Select(_formatBox, _settings.Format);
        Select(_bitrateBox, _settings.Mp3Bitrate);
        Select(_channelBox, _settings.Channels);

        _customRate.Value = Math.Clamp(_settings.SampleRate == 0 ? 48000 : _settings.SampleRate,
                                       (int)_customRate.Minimum, (int)_customRate.Maximum);
        bool standard = _settings.SampleRate == 0 || AppSettings.StandardRates.Contains(_settings.SampleRate);
        Select(_rateBox, standard ? _settings.SampleRate : -1);
        _customRate.Enabled = !standard;

        _gainTrack.Value = Math.Clamp(_settings.GainDb, _gainTrack.Minimum, _gainTrack.Maximum);
        _gainLabel.Text = $"{_gainTrack.Value:+0;-0;0} dB";

        _folderBox.Text = _settings.Folder;
        _templateBox.Text = _settings.FileTemplate;
        _splitBox.Value = Math.Clamp(_settings.SplitMinutes, 0, 1440);
        _maxBox.Value = Math.Clamp(_settings.MaxMinutes, 0, 1440);
        _followDefault.Checked = _settings.FollowDefaultDevice;
        _monitorIdle.Checked = _settings.MonitorWhenIdle;
        _trayCheck.Checked = _settings.MinimizeToTray;

        UpdateFormatDependants();
    }

    private void UpdateFormatDependants()
    {
        bool mp3 = _settings.Format == OutputFormat.Mp3;
        _bitrateBox.Enabled = mp3 && !_recorder.IsRecording;
        UpdateEstimates();
    }

    private void UpdateEstimates()
    {
        long perSecond = EstimateBytesPerSecond();
        string rate = perSecond > 0 ? $"≈ {FormatSize(perSecond * 60)}/min" : "";

        string free = "";
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(_settings.Folder)) ?? "";
            if (root.Length > 0) free = $"{FormatSize(new DriveInfo(root).AvailableFreeSpace)} free";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Disk space unavailable: {ex.Message}");
        }

        _diskLabel.Text = string.Join("   ·   ", new[] { rate, free }.Where(s => s.Length > 0));
    }

    private long EstimateBytesPerSecond()
    {
        var source = _recorder.SourceFormat;
        if (_settings.Format == OutputFormat.Mp3) return _settings.Mp3Bitrate * 125L;

        int rate = _settings.SampleRate > 0 ? _settings.SampleRate : source?.SampleRate ?? 48000;
        int channels = _settings.Channels switch
        {
            ChannelMode.Mono => 1,
            ChannelMode.Stereo => Math.Min(2, source?.Channels ?? 2),
            _ => source?.Channels ?? 2
        };
        int bytes = _settings.Format switch
        {
            OutputFormat.Wav24 => 3,
            OutputFormat.Wav32Float => 4,
            _ => 2
        };
        return (long)rate * channels * bytes;
    }

    // ---- devices ----------------------------------------------------------------------------

    private void LoadDevices()
    {
        if (_recorder.IsRecording) return;

        _loadingDevices = true;
        string? keep = SelectedDevice()?.ID ?? _settings.DeviceId;

        _deviceBox.Items.Clear();
        foreach (var item in _devices) item.Dispose();
        _devices.Clear();

        string? defaultId = DefaultDeviceId();

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _devices.Add(new DeviceItem(device, device.ID == defaultId));
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not list playback devices: {ex.Message}");
        }

        foreach (var item in _devices) _deviceBox.Items.Add(item);

        int index = _settings.FollowDefaultDevice
            ? _devices.FindIndex(d => d.IsDefault)
            : _devices.FindIndex(d => d.Device.ID == keep);
        if (index < 0) index = _devices.FindIndex(d => d.Device.ID == keep);
        if (index < 0) index = _devices.FindIndex(d => d.IsDefault);
        if (index < 0 && _devices.Count > 0) index = 0;

        _loadingDevices = false;
        if (index >= 0) _deviceBox.SelectedIndex = index;

        _recordButton.Enabled = _devices.Count > 0;
        if (_devices.Count == 0)
        {
            _formatInfo.Text = string.Empty;
            SetStatus("No active playback device found.");
        }
    }

    private string? DefaultDeviceId()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"No default render endpoint: {ex.Message}");
            return null;
        }
    }

    private void SelectDefaultDevice()
    {
        string? id = DefaultDeviceId();
        int index = _devices.FindIndex(d => d.Device.ID == id);
        if (index >= 0 && index != _deviceBox.SelectedIndex) _deviceBox.SelectedIndex = index;
    }

    private MMDevice? SelectedDevice() => (_deviceBox.SelectedItem as DeviceItem)?.Device;

    private async void OnDeviceSelected(object? sender, EventArgs e)
    {
        if (_loadingDevices) return;
        _settings.DeviceId = SelectedDevice()?.ID;
        await RefreshCaptureAsync();
        ShowFormatInfo();
    }

    private void OnDeviceListChanged()
    {
        if (_recorder.IsRecording) return;
        LoadDevices();
    }

    private void OnDefaultDeviceChanged(string deviceId)
    {
        if (!_settings.FollowDefaultDevice || _recorder.IsRecording) return;
        LoadDevices();
    }

    private async Task RefreshCaptureAsync()
    {
        var device = SelectedDevice();
        if (device is null) return;

        if (_recorder.IsCapturing && _recorder.CaptureDeviceId == device.ID) return;
        if (_recorder.IsCapturing) await _recorder.StopCaptureAsync();
        if (!_settings.MonitorWhenIdle && !_recorder.IsRecording) return;

        StartCapture(device.ID);
    }

    private bool StartCapture(string deviceId)
    {
        try
        {
            _recorder.StartCapture(deviceId);
            _recorder.SetGain(_gainTrack.Value);
            UpdateEstimates();
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot open {_deviceBox.Text}: {ex.Message}");
            return false;
        }
    }

    private async void OnMonitorIdleChanged(object? sender, EventArgs e)
    {
        _settings.MonitorWhenIdle = _monitorIdle.Checked;
        if (_recorder.IsRecording) return;

        if (_monitorIdle.Checked) await RefreshCaptureAsync();
        else if (_recorder.IsCapturing)
        {
            await _recorder.StopCaptureAsync();
            _leftMeter.Reset();
            _rightMeter.Reset();
        }
    }

    private void ShowFormatInfo()
    {
        var format = _recorder.SourceFormat;
        if (format is null && SelectedDevice() is { } device) format = MixFormatOf(device);
        _formatInfo.Text = format is null ? string.Empty : $"Endpoint: {Describe(format)}";
    }

    private static WaveFormat? MixFormatOf(MMDevice device)
    {
        try
        {
            return device.AudioClient.MixFormat;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Mix format unavailable: {ex.Message}");
            return null;
        }
    }

    private static string Describe(WaveFormat? format)
    {
        if (format is null) return string.Empty;

        // A device mix format is usually WAVE_FORMAT_EXTENSIBLE, whose Encoding hides whether the
        // samples are float or PCM; the standard form spells it out.
        if (format is WaveFormatExtensible extensible)
        {
            try { format = extensible.ToStandardWaveFormat(); }
            catch (Exception ex) { Debug.WriteLine($"Cannot simplify mix format: {ex.Message}"); }
        }

        return $"{format.SampleRate:n0} Hz · {format.Channels} ch · {format.BitsPerSample}-bit " +
               (format.Encoding == WaveFormatEncoding.IeeeFloat ? "float" : "PCM");
    }

    // ---- transport ---------------------------------------------------------------------------

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _recordButton.Enabled = false;

        try
        {
            if (_recorder.IsRecording)
            {
                _recorder.StopRecording();
                if (!_settings.MonitorWhenIdle) await _recorder.StopCaptureAsync();
            }
            else
            {
                if (SelectedDevice() is not { } device)
                {
                    SetStatus("Select a playback device first.");
                    return;
                }

                if (!_recorder.IsCapturing && !StartCapture(device.ID)) return;

                try
                {
                    _recorder.StartRecording(BuildRequest());
                }
                catch (Exception ex)
                {
                    SetStatus("Could not start recording.");
                    MessageBox.Show(this, ex.Message, "Recording failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _fileLabel.Text = Path.GetFileName(_recorder.CurrentPath);
                SetStatus(_recorder.Notice ?? $"Recording from {_deviceBox.Text}");
            }
        }
        finally
        {
            _busy = false;
            UpdateTransport();
        }
    }

    private RecordingRequest BuildRequest() => new(
        _settings.Folder,
        _settings.FileTemplate,
        _settings.Format,
        _settings.Mp3Bitrate,
        _settings.SampleRate,
        _settings.Channels,
        _gainTrack.Value,
        _settings.SplitMinutes,
        _settings.MaxMinutes);

    private void OnPauseClicked(object? sender, EventArgs e)
    {
        if (!_recorder.IsRecording) return;

        if (_recorder.IsPaused) _recorder.Resume();
        else _recorder.Pause();

        UpdateTransport();
        SetStatus(_recorder.IsPaused ? "Paused." : $"Recording from {_deviceBox.Text}");
    }

    private void UpdateTransport()
    {
        bool recording = _recorder.IsRecording;
        bool paused = _recorder.IsPaused;

        _recordButton.Text = recording ? "■  Stop" : "●  Record";
        _recordButton.BackColor = recording ? Color.FromArgb(64, 68, 78) : Accent;
        _recordButton.Enabled = _devices.Count > 0;
        _pauseButton.Text = paused ? "▶  Resume" : "❚❚  Pause";
        _pauseButton.Enabled = recording;
        _timerLabel.ForeColor = recording && !paused ? Accent : Muted;

        foreach (Control control in new Control[]
                 { _deviceBox, _refreshButton, _followDefault, _formatBox, _rateBox, _channelBox,
                   _browseButton, _templateBox, _splitBox, _maxBox, _monitorIdle })
        {
            control.Enabled = !recording;
        }
        _bitrateBox.Enabled = !recording && _settings.Format == OutputFormat.Mp3;
        _customRate.Enabled = !recording && Selected<int>(_rateBox) == -1;

        _tray.Icon = recording && !paused ? AppIcons.Recording : AppIcons.Idle;
        _tray.Text = recording
            ? $"Recording — {_recorder.Elapsed:hh\\:mm\\:ss}"
            : "Windows Audio Recorder";
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        var (left, right) = _recorder.ReadPeaks();
        bool clipped = _recorder.ReadClip();
        _leftMeter.SetPeak(left, clipped);
        _rightMeter.SetPeak(right, clipped);

        if (_recorder.IsRecording)
        {
            _timerLabel.Text = _recorder.Elapsed.ToString(@"hh\:mm\:ss");
            _fileLabel.Text = $"{Path.GetFileName(_recorder.CurrentPath)}  ·  {FormatSize(_recorder.EstimatedFileBytes)}";
            if (++_diskTicks % 60 == 0) UpdateEstimates();
        }
    }

    // ---- recorder events ----------------------------------------------------------------------

    private void OnSegmentCompleted(object? sender, string path)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnSegmentCompleted(sender, path));
            return;
        }

        AddRecent(path);
        if (File.Exists(path)) SetStatus($"Saved · {FormatSize(new FileInfo(path).Length)}");
        _fileLabel.Text = path;

        if (!Visible && _settings.MinimizeToTray)
        {
            _tray.ShowBalloonTip(3000, "Recording saved", Path.GetFileName(path), ToolTipIcon.Info);
        }
    }

    private void OnAutoStopped(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnAutoStopped(sender, e));
            return;
        }

        UpdateTransport();
        SetStatus($"Stopped after the {_settings.MaxMinutes} minute limit.");
    }

    private void OnRecorderAborted(object? sender, Exception? error)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnRecorderAborted(sender, error));
            return;
        }

        _leftMeter.Reset();
        _rightMeter.Reset();
        UpdateTransport();
        SetStatus(error is null ? "Capture ended unexpectedly." : $"Capture ended: {error.Message}");
    }

    // ---- recent list ----------------------------------------------------------------------------

    private void LoadRecent()
    {
        foreach (string path in _settings.Recent.Where(File.Exists).Reverse())
        {
            AddRecent(path, moveToTop: false);
        }
    }

    private void AddRecent(string path, bool moveToTop = true)
    {
        foreach (ListViewItem existing in _recentList.Items)
        {
            if (string.Equals((string?)existing.Tag, path, StringComparison.OrdinalIgnoreCase))
            {
                _recentList.Items.Remove(existing);
                break;
            }
        }

        var info = new FileInfo(path);
        var item = new ListViewItem(Path.GetFileName(path)) { Tag = path, ToolTipText = path };
        item.SubItems.Add(info.Exists ? FormatSize(info.Length) : "—");
        item.SubItems.Add(info.Exists ? info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") : "—");

        _recentList.Items.Insert(moveToTop ? 0 : _recentList.Items.Count, item);
        while (_recentList.Items.Count > RecentLimit) _recentList.Items.RemoveAt(_recentList.Items.Count - 1);
    }

    private void LayoutRecentColumns()
    {
        int width = _recentList.ClientSize.Width;
        if (width <= 40) return;

        int size = (int)(width * 0.16);
        int saved = (int)(width * 0.26);
        _recentList.Columns[1].Width = size;
        _recentList.Columns[2].Width = saved;
        _recentList.Columns[0].Width = Math.Max(40, width - size - saved - 4);
    }

    private string? SelectedRecording() => _recentList.SelectedItems.Count > 0
        ? (string?)_recentList.SelectedItems[0].Tag
        : null;

    private void OpenSelectedRecording()
    {
        if (SelectedRecording() is not { } path || !File.Exists(path)) return;
        TryShell(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void RevealSelectedRecording()
    {
        if (SelectedRecording() is not { } path || !File.Exists(path)) return;
        TryShell(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void CopySelectedPath()
    {
        if (SelectedRecording() is { } path) Clipboard.SetText(path);
    }

    private void RemoveSelected(bool deleteFile)
    {
        if (_recentList.SelectedItems.Count == 0) return;
        var item = _recentList.SelectedItems[0];
        string path = (string)item.Tag!;

        if (deleteFile)
        {
            var answer = MessageBox.Show(this, $"Delete {Path.GetFileName(path)} permanently?", "Delete recording",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not delete", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        _recentList.Items.Remove(item);
    }

    // ---- folders & shell ------------------------------------------------------------------------

    private void OnBrowseClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where recordings are saved",
            SelectedPath = Directory.Exists(_settings.Folder) ? _settings.Folder : AppSettings.DefaultFolder(),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _settings.Folder = dialog.SelectedPath;
        _folderBox.Text = _settings.Folder;
        UpdateEstimates();
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.Folder);
            TryShell(new ProcessStartInfo(_settings.Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open folder: {ex.Message}");
        }
    }

    private void TryShell(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open: {ex.Message}");
        }
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.0} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024d * 1024d):0.0} MB",
        _ => $"{bytes / (1024d * 1024d * 1024d):0.00} GB"
    };

    // ---- tray & hotkeys -------------------------------------------------------------------------

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
        {
            Hide();
            _tray.Visible = true;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _hotkeysRegistered =
            RegisterHotKey(Handle, HotkeyRecord, ModControl | ModAlt | ModNoRepeat, (uint)Keys.R) &
            RegisterHotKey(Handle, HotkeyPause, ModControl | ModAlt | ModNoRepeat, (uint)Keys.P);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotkeysRegistered)
        {
            UnregisterHotKey(Handle, HotkeyRecord);
            UnregisterHotKey(Handle, HotkeyPause);
            _hotkeysRegistered = false;
        }

        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            switch ((int)m.WParam)
            {
                case HotkeyRecord:
                    OnRecordClicked(this, EventArgs.Empty);
                    return;
                case HotkeyPause:
                    OnPauseClicked(this, EventArgs.Empty);
                    return;
            }
        }

        base.WndProc(ref m);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (!_sized)
        {
            // Sized here rather than in the constructor: only now has the window landed on its
            // monitor and had WinForms' font/DPI scaling applied, so the layout's preferred size
            // is finally measured in the units the window actually uses.
            _sized = true;
            var preferred = _root.PreferredSize;
            ClientSize = new Size(preferred.Width + Font.Height * 2, preferred.Height + Font.Height * 12);
            MinimumSize = new Size(Width - ClientSize.Width + preferred.Width,
                                   Height - ClientSize.Height + preferred.Height);
            CenterToScreen();
            LayoutRecentColumns();
        }

        _tray.Visible = true;

        // Opened explicitly rather than through the combo's SelectedIndexChanged: the initial
        // selection is assigned in the constructor, before the combo has a window handle, so
        // that event cannot be relied on to have started monitoring.
        await RefreshCaptureAsync();

        ShowFormatInfo();

        // Only advertise the hotkeys if nothing more important happened during start-up —
        // a device error reported here must not be overwritten.
        if (_statusLabel.Text == "Ready.")
        {
            SetStatus(_hotkeysRegistered
                ? "Ready.   Ctrl+Alt+R record · Ctrl+Alt+P pause"
                : "Ready.   (global hotkeys are already taken by another app)");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_recorder.IsRecording)
        {
            // Finalise the file before the window goes away.
            _recorder.StopRecording();
        }

        _settings.Recent = [.. _recentList.Items.Cast<ListViewItem>().Select(i => (string)i.Tag!)];
        _settings.Save();

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _uiTimer.Dispose();

        _recorder.SegmentCompleted -= OnSegmentCompleted;
        _recorder.Aborted -= OnRecorderAborted;
        _recorder.AutoStopped -= OnAutoStopped;
        _recorder.Dispose();

        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); }
        catch (Exception ex) { Debug.WriteLine($"Unregister failed: {ex.Message}"); }

        foreach (var item in _devices) item.Dispose();
        _devices.Clear();
        _enumerator.Dispose();

        _tray.Visible = false;
        _tray.Dispose();

        base.OnFormClosed(e);
    }

    private sealed class DeviceItem(MMDevice device, bool isDefault) : IDisposable
    {
        public MMDevice Device { get; } = device;
        public bool IsDefault { get; } = isDefault;

        public override string ToString()
        {
            string name;
            try { name = Device.FriendlyName; }
            catch { name = Device.ID; }
            return IsDefault ? $"{name}  (default)" : name;
        }

        public void Dispose() => Device.Dispose();
    }
}

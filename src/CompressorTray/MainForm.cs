using System.Drawing.Drawing2D;
using Microsoft.Win32;
using SystemCompressor.Dsp;

namespace SystemCompressor.Tray;

/// <summary>
/// The whole app: a dark window with a gold frame and its own title bar. Opening the app switches the
/// compressor on; closing the window with the X switches it off. Changes are saved on every move so the
/// plug-in (which watches settings.json) follows live.
/// </summary>
public sealed class MainForm : Form
{
    private const int TitleHeight = 38;
    private const int WmNcHitTest = 0x84;
    private const int HtCaption = 2;

    private readonly CompressorConfiguration _configuration;
    private readonly KnobControl _threshold, _ratio, _attack, _release, _gain, _knee, _speech, _speechFreq;
    private readonly GoldButton _power = new();
    private readonly GoldButton _reset = new();
    private readonly Label _status = new();
    private readonly MeterControl _meter = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 60 };   // coalesces writes while a knob is dragged
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 300 };
    private long _lastTick = Environment.TickCount64;
    private int _statusCountdown;
    private bool _loading = true;
    private bool _hoverClose, _hoverMinimize;

    // Windows 11 rounds the corners of every top-level window; our frame is a gold rectangle, so ask for
    // square corners. Windows 10 does not know the attribute and the call just fails harmlessly.
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var squareCorners = 1;   // DWMWCP_DONOTROUND
        try { _ = DwmSetWindowAttribute(Handle, 33, ref squareCorners, sizeof(int)); }   // DWMWA_WINDOW_CORNER_PREFERENCE
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private Rectangle CloseRect => new(Width - 46, 3, 42, 32);
    private Rectangle MinimizeRect => new(Width - 90, 3, 42, 32);

    public MainForm()
    {
        CompressorConfiguration.PrepareDirectory();
        // Earlier versions lived in the tray and started with Windows; that entry is obsolete now.
        try { Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)?.DeleteValue("SystemCompressor", false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }

        _configuration = CompressorConfiguration.Load();
        _configuration.Settings = _configuration.Settings with { Enabled = true };   // opening the app = compressor on
        _configuration.TrySave();
        var settings = _configuration.Settings;

        Text = "System Compressor";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9f);
        DoubleBuffered = true;
        ClientSize = new Size(492, 556);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { }

        _power.SetBounds(18, TitleHeight + 12, 456, 42);
        _power.Click += (_, _) => SetEnabled(!_configuration.Settings.Enabled);
        _status.SetBounds(18, TitleHeight + 60, 456, 20);
        _status.ForeColor = Theme.Muted;
        _status.BackColor = Theme.Background;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        Controls.AddRange([_power, _status]);

        var defaults = new CompressorSettings();
        _threshold = AddKnob(0, 0, "Threshold", "dB", -60, 0, defaults.ThresholdDb, settings.ThresholdDb, 0,
            "Level above which the compressor starts turning loud passages down. Lower = more of the signal is compressed.");
        _ratio = AddKnob(1, 0, "Ratio", ":1", 1, 20, defaults.Ratio, settings.Ratio, 1,
            "How strongly levels above the threshold are reduced. 3.5:1 means 3.5 dB more input gives only 1 dB more output.");
        _attack = AddKnob(2, 0, "Attack", "ms", 0.1f, 200, defaults.AttackMs, settings.AttackMs, 1,
            "How fast the compressor reacts to loud passages. Short = a bang is caught immediately.", logarithmic: true);
        _release = AddKnob(0, 1, "Release", "ms", 10, 2000, defaults.ReleaseMs, settings.ReleaseMs, 0,
            "How fast the volume recovers after a loud passage. Longer = calmer, less pumping.", logarithmic: true);
        _gain = AddKnob(1, 1, "Gain", "dB", -24, CompressorSettings.MaxMakeupGainDb, defaults.MakeupGainDb, settings.MakeupGainDb, 1,
            "Overall volume after compression (maximum +5 dB). Also lifts the quiet parts (whispers).", showSign: true, step: 0.5f);

        _knee = AddKnob(2, 1, "Knee", "dB", 0, 24, defaults.KneeDb, settings.KneeDb, 1,
            "How softly compression sets in around the threshold. 0 = hard (abrupt, can pump), higher = smoother transition and a more natural sound.");

        _reset.SetBounds(250 + 12, TitleHeight + 94 + 2 * 128 + 24 + 41, 92, 38);
        _reset.Text = "Reset";
        _reset.Click += (_, _) => ResetToDefaults();
        _tips.SetToolTip(_reset, "Resets all knobs to the film preset.");
        Controls.Add(_reset);

        _speech = AddKnob(0, 2, "EQ Boost", "dB", 0, 8, defaults.SpeechBoostDb, settings.SpeechBoostDb, 1,
            "Boosts a frequency band where consonants (s, t, k, f) live. Makes voices clearer and easier to understand without making everything louder. 0 = off.",
            showSign: true, step: 0.5f, yOffset: 24);
        _speechFreq = AddKnob(1, 2, "Frequency", "Hz", 1000, 8000, defaults.SpeechFreqHz, settings.SpeechFreqHz, 0,
            "Centre of the boosted band. 3000 Hz suits most voices; lower = fuller, higher = more sibilant.",
            logarithmic: true, yOffset: 24);

        _meter.Location = new Point(372, TitleHeight + 94);
        _meter.Size = new Size(108, 404);
        _meter.BackColor = Theme.Background;
        Controls.Add(_meter);

        Controls.Add(new Label
        {
            Text = "Drag or mouse wheel: change · Shift: fine · Double-click: default",
            Left = 18, Top = ClientSize.Height - 28, Width = 450, ForeColor = Theme.Muted, BackColor = Theme.Background,
            Font = Theme.Ui(8f)
        });

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _saveTimer.Tick += (_, _) => FlushSave();
        _loading = false;
        ApplyPowerState();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000;        // WS_MINIMIZEBOX: taskbar click minimises, animations work
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            return cp;
        }
    }

    /// <summary>A second launch of the app brings this window forward.</summary>
    public void BringForward()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private KnobControl AddKnob(int column, int row, string caption, string unit, float min, float max,
        float defaultValue, float value, int decimals, string tip, bool logarithmic = false, bool showSign = false,
        float step = 1f, int yOffset = 0)
    {
        var knob = new KnobControl
        {
            Left = 12 + column * 118, Top = TitleHeight + 94 + row * 128 + yOffset, Caption = caption, Unit = unit,
            Minimum = min, Maximum = max, DefaultValue = defaultValue, Decimals = decimals,
            Logarithmic = logarithmic, ShowSign = showSign, Step = step, Value = value
        };
        knob.ValueChanged += (_, _) => Publish();
        _tips.SetToolTip(knob, tip);
        Controls.Add(knob);
        return knob;
    }

    private void Publish()
    {
        if (_loading) return;
        _configuration.Settings = _configuration.Settings with
        {
            ThresholdDb = _threshold.Value,
            Ratio = _ratio.Value,
            AttackMs = _attack.Value,
            ReleaseMs = _release.Value,
            MakeupGainDb = _gain.Value,
            KneeDb = _knee.Value,
            SpeechBoostDb = _speech.Value,
            SpeechFreqHz = _speechFreq.Value
        };
        // Save at most every 60 ms while a knob moves (and once more right after the last move): the plug-in polls the
        // file every ~250 ms anyway, so this keeps the sound live but avoids dozens of file writes per second.
        if (!_saveTimer.Enabled) _saveTimer.Start();
    }

    private void FlushSave()
    {
        _saveTimer.Stop();
        _configuration.TrySave();
    }

    private void ResetToDefaults()
    {
        foreach (var knob in new[] { _threshold, _ratio, _attack, _release, _gain, _knee, _speech, _speechFreq })
            knob.Value = knob.DefaultValue;
    }

    private void SetEnabled(bool enabled)
    {
        _configuration.Settings = _configuration.Settings with { Enabled = enabled };
        FlushSave();
        ApplyPowerState();
    }

    private void ApplyPowerState()
    {
        var on = _configuration.Settings.Enabled;
        _power.Text = on ? "COMPRESSOR  ON" : "COMPRESSOR  OFF";
        _power.Filled = on;
        _status.Text = PluginStatus.Read().Describe(on);
    }

    private void Tick()
    {
        var now = Environment.TickCount64;
        var dt = Math.Clamp((now - _lastTick) / 1000f, 0.001f, 0.25f);
        _lastTick = now;
        if (WindowState == FormWindowState.Minimized) return;   // nobody is looking: no file reads, no repaints
        var status = PluginStatus.Read();
        _meter.Advance(dt, status.Processing ? status.InputDb : -120f, status.Processing ? status.OutputDb : -120f,
            status.Processing ? status.ReductionDb : 0f);
        if (--_statusCountdown <= 0)
        {
            _statusCountdown = 15;   // text twice a second is plenty
            _status.Text = _configuration.LastSaveSucceeded
                ? status.Describe(_configuration.Settings.Enabled)
                : @"Cannot save settings - check permissions of C:\ProgramData\SystemCompressor";
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window (X, Alt+F4, taskbar 'close window') switches the compressor off.
        // Only a Windows shutdown/logoff keeps the saved state, so the compressor survives a reboot.
        if (e.CloseReason != CloseReason.WindowsShutDown)
        {
            _configuration.Settings = _configuration.Settings with { Enabled = false };
        }
        _timer.Stop();
        FlushSave();   // also writes a pending knob change that was still waiting for the save timer
        base.OnFormClosing(e);
    }

    // ---- custom frame -------------------------------------------------------
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            var raw = m.LParam.ToInt64();
            var point = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));
            if (point.Y >= 0 && point.Y < TitleHeight && !CloseRect.Contains(point) && !MinimizeRect.Contains(point))
            {
                m.Result = HtCaption;   // drag the window by its title bar
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var close = CloseRect.Contains(e.Location);
        var minimize = MinimizeRect.Contains(e.Location);
        if (close != _hoverClose || minimize != _hoverMinimize)
        {
            _hoverClose = close;
            _hoverMinimize = minimize;
            Invalidate(new Rectangle(Width - 100, 0, 100, TitleHeight));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverClose = _hoverMinimize = false;
        Invalidate(new Rectangle(Width - 100, 0, 100, TitleHeight));
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (CloseRect.Contains(e.Location)) Close();
            else if (MinimizeRect.Contains(e.Location)) WindowState = FormWindowState.Minimized;
        }
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // title bar: subtle gradient, gold hairline, logo and spaced title
        using (var bar = new LinearGradientBrush(new Rectangle(0, 0, Width, TitleHeight), Color.FromArgb(46, 47, 54), Theme.Background, 90f))
            g.FillRectangle(bar, 0, 0, Width, TitleHeight);
        using (var line = new LinearGradientBrush(new Rectangle(0, TitleHeight - 1, Width, 2), Theme.GoldDim, Theme.Gold, 0f))
            g.FillRectangle(line, 14, TitleHeight - 1, Width - 28, 1.4f);
        if (Icon is not null) g.DrawIcon(Icon, new Rectangle(14, 8, 22, 22));
        using (var title = Theme.Ui(10.5f, FontStyle.Bold))
            TextRenderer.DrawText(g, "S Y S T E M   C O M P R E S S O R", title, new Rectangle(44, 0, Width - 150, TitleHeight),
                Theme.Gold, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // window buttons
        DrawWindowButton(g, MinimizeRect, _hoverMinimize, Theme.Gold, closeButton: false);
        DrawWindowButton(g, CloseRect, _hoverClose, Theme.Danger, closeButton: true);

        // gold frame
        using var frame = new Pen(Theme.Gold, 2f);
        g.DrawRectangle(frame, 1, 1, Width - 3, Height - 3);
        using var inner = new Pen(Color.FromArgb(70, Theme.Gold), 1f);
        g.DrawRectangle(inner, 4, 4, Width - 9, Height - 9);

        // section header for the speech EQ
        var y = TitleHeight + 94 + 2 * 128 + 4;
        using var header = Theme.Ui(8f, FontStyle.Bold);
        TextRenderer.DrawText(g, "SPEECH EQ", header, new Rectangle(18, y, 120, 16), Theme.Gold, TextFormatFlags.NoPadding);
        using var rule = new Pen(Theme.GoldDim, 1f);
        g.DrawLine(rule, 96, y + 8, 350, y + 8);
    }

    private static void DrawWindowButton(Graphics g, Rectangle rect, bool hover, Color hoverColor, bool closeButton)
    {
        if (hover)
        {
            using var back = new SolidBrush(Color.FromArgb(closeButton ? 200 : 60, hoverColor));
            g.FillRectangle(back, rect);
        }
        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f;
        using var pen = new Pen(hover && closeButton ? Color.White : Theme.GoldLight, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (closeButton)
        {
            g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
            g.DrawLine(pen, cx - 5, cy + 5, cx + 5, cy - 5);
        }
        else g.DrawLine(pen, cx - 6, cy + 3, cx + 6, cy + 3);
    }
}

using System.Drawing.Drawing2D;
using System.Globalization;

namespace SystemCompressor.Tray;

/// <summary>Rotary knob: drag vertically, mouse wheel, arrow keys, double-click resets to default.</summary>
public sealed class KnobControl : Control
{
    private const float StartAngle = 135f;   // degrees, clockwise from 3 o'clock
    private const float SweepAngle = 270f;
    private float _value;
    private bool _dragging;
    private int _dragStartY;
    private float _dragStartFraction;
    private readonly Font _captionFont = Theme.Ui(7.5f, FontStyle.Bold);

    public KnobControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Size = new Size(110, 120);
        Font = Theme.Ui(9f);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _captionFont.Dispose();
        base.Dispose(disposing);
    }

    public float Minimum { get; set; } = -24f;
    public float Maximum { get; set; } = 24f;
    public float DefaultValue { get; set; }
    /// <summary>Linear knobs move by this much per wheel notch / key press.</summary>
    public float Step { get; set; } = 0.5f;
    /// <summary>Logarithmic scaling suits time constants (attack, release).</summary>
    public bool Logarithmic { get; set; }
    public int Decimals { get; set; } = 1;
    public bool ShowSign { get; set; }
    public string Unit { get; set; } = "dB";
    public string Caption { get; set; } = "Gain";

    public float Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            var rounded = MathF.Round(clamped, Decimals);
            if (rounded == _value) return;
            _value = rounded;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    private float Fraction => ToFraction(_value);

    private float ToFraction(float value) => Logarithmic
        ? MathF.Log(value / Minimum) / MathF.Log(Maximum / Minimum)
        : (value - Minimum) / (Maximum - Minimum);

    private float FromFraction(float fraction)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        return Logarithmic
            ? Minimum * MathF.Pow(Maximum / Minimum, fraction)
            : Minimum + fraction * (Maximum - Minimum);
    }

    private void Nudge(int direction)
    {
        if (Logarithmic) Value = FromFraction(Fraction + direction * 0.02f);
        else Value += direction * Step;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _dragging = true;
        _dragStartY = e.Y;
        _dragStartFraction = Fraction;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            var fine = (ModifierKeys & Keys.Shift) != 0 ? 0.2f : 1f;
            Value = FromFraction(_dragStartFraction + (_dragStartY - e.Y) / 150f * fine);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { Value = DefaultValue; base.OnMouseDoubleClick(e); }
    protected override void OnMouseWheel(MouseEventArgs e) { Nudge(Math.Sign(e.Delta)); base.OnMouseWheel(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up or Keys.Right: Nudge(1); break;
            case Keys.Down or Keys.Left: Nudge(-1); break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var diameter = Math.Min(Width, Height - 38) - 14;
        var bounds = new RectangleF((Width - diameter) / 2f, 7, diameter, diameter);
        var fraction = Fraction;

        using (var track = new Pen(Color.FromArgb(58, 58, 66), 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, bounds, StartAngle, SweepAngle);
        using (var arc = new Pen(Theme.Gold, 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            if (fraction > 0.001f) g.DrawArc(arc, bounds, StartAngle, SweepAngle * fraction);

        var body = RectangleF.Inflate(bounds, -9, -9);
        using (var fill = new LinearGradientBrush(body, Color.FromArgb(82, 82, 90), Color.FromArgb(30, 30, 34), 90f))
            g.FillEllipse(fill, body);

        var angle = (StartAngle + SweepAngle * fraction) * MathF.PI / 180f;
        var centre = new PointF(body.Left + body.Width / 2, body.Top + body.Height / 2);
        var radius = body.Width / 2;
        using (var pointer = new Pen(Theme.GoldLight, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pointer,
                centre.X + MathF.Cos(angle) * radius * 0.35f, centre.Y + MathF.Sin(angle) * radius * 0.35f,
                centre.X + MathF.Cos(angle) * radius * 0.85f, centre.Y + MathF.Sin(angle) * radius * 0.85f);

        if (Focused)
            using (var focus = new Pen(Color.FromArgb(120, Theme.Gold)) { DashStyle = DashStyle.Dot })
                g.DrawEllipse(focus, RectangleF.Inflate(bounds, 3, 3));

        var number = _value.ToString((ShowSign ? "+" : "") + "0." + new string('0', Decimals) + ";-0." + new string('0', Decimals), CultureInfo.CurrentCulture);
        TextRenderer.DrawText(g, $"{number} {Unit}", Font, new Rectangle(0, Height - 34, Width, 17), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Caption.ToUpperInvariant(), _captionFont, new Rectangle(0, Height - 17, Width, 17), Theme.Gold,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
    }
}

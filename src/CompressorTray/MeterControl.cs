using System.Drawing.Drawing2D;
using System.Globalization;

namespace SystemCompressor.Tray;

/// <summary>
/// Compressor meter in the style of stock plug-ins: segmented IN and OUT level bars, a gain-reduction (GR)
/// bar that hangs from the top, peak-hold marks and numeric readouts.
/// </summary>
public sealed class MeterControl : Control
{
    private const float Floor = -60f;       // bottom of the level bars
    private const float GrRange = 24f;      // full scale of the GR bar
    private const int Segments = 30;
    private const float LevelFall = 40f;    // dB per second
    private const float GrFall = 45f;
    private const float HoldSeconds = 1.5f;
    private const float HoldFall = 12f;

    private float _in = Floor, _out = Floor, _gr;
    private float _inHold = Floor, _outHold = Floor, _grHold;
    private float _inHoldAge, _outHoldAge, _grHoldAge;
    private readonly Font _captionFont = Theme.Ui(7.5f, FontStyle.Bold);
    private readonly Font _scaleFont = Theme.Ui(7f);
    private readonly Font _rowFont = Theme.Ui(8.5f);
    private readonly Font _rowBold = Theme.Ui(8.5f, FontStyle.Bold);

    public MeterControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Ui(8f);
        Size = new Size(108, 390);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _captionFont.Dispose(); _scaleFont.Dispose(); _rowFont.Dispose(); _rowBold.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Feeds the newest measured peaks; applies meter ballistics (instant attack, timed fall).</summary>
    public void Advance(float seconds, float inputDb, float outputDb, float reductionDb)
    {
        Ballistics(ref _in, ref _inHold, ref _inHoldAge, seconds, Math.Max(inputDb, Floor), LevelFall, Floor);
        Ballistics(ref _out, ref _outHold, ref _outHoldAge, seconds, Math.Max(outputDb, Floor), LevelFall, Floor);
        Ballistics(ref _gr, ref _grHold, ref _grHoldAge, seconds, Math.Clamp(reductionDb, 0f, GrRange), GrFall, 0f);
        Invalidate();
    }

    private static void Ballistics(ref float level, ref float hold, ref float holdAge, float dt, float measured, float fall, float floor)
    {
        level = Math.Max(measured, Math.Max(level - fall * dt, floor));
        if (level >= hold) { hold = level; holdAge = 0; }
        else
        {
            holdAge += dt;
            if (holdAge > HoldSeconds) hold = Math.Max(hold - HoldFall * dt, level);
        }
        if (hold < floor) hold = floor;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);

        using (var panel = GoldButton.RoundedRectangle(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), 10))
        {
            using var fill = new SolidBrush(Theme.Panel);
            g.FillPath(fill, panel);
            using var border = new Pen(Theme.GoldDim, 1.2f);
            g.DrawPath(border, panel);
        }

        const int scaleLeft = 4, scaleWidth = 22, barWidth = 18;
        int inX = scaleLeft + scaleWidth + 2, outX = inX + barWidth + 8, grX = outX + barWidth + 12;
        const int barTop = 30;
        var barBottom = Height - 66;
        var barHeight = barBottom - barTop;

        DrawCentered(g, "IN", _captionFont, inX, barWidth, 9, Theme.Gold);
        DrawCentered(g, "OUT", _captionFont, outX, barWidth, 9, Theme.Gold);
        DrawCentered(g, "GR", _captionFont, grX, barWidth, 9, Theme.Gold);

        // dB scale for the level bars
        using (var tick = new Pen(Color.FromArgb(90, Theme.Muted), 1f))
            for (var db = 0; db >= -60; db -= 12)
            {
                var y = barBottom - (db - Floor) / -Floor * barHeight;
                g.DrawLine(tick, scaleLeft + scaleWidth - 3, y, scaleLeft + scaleWidth, y);
                TextRenderer.DrawText(g, db.ToString(CultureInfo.InvariantCulture), _scaleFont,
                    new Rectangle(scaleLeft - 4, (int)y - 7, scaleWidth - 2, 14), Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            }

        DrawLevelBar(g, inX, barWidth, barTop, barHeight, _in, _inHold);
        DrawLevelBar(g, outX, barWidth, barTop, barHeight, _out, _outHold);
        DrawGrBar(g, grX, barWidth, barTop, barHeight, _gr, _grHold);

        DrawReadout(g, "IN", _inHold <= Floor ? "-" : $"{_inHold:0.0}", _rowFont, _rowBold, Height - 60, Theme.Text);
        DrawReadout(g, "OUT", _outHold <= Floor ? "-" : $"{_outHold:0.0}", _rowFont, _rowBold, Height - 42, Theme.Text);
        DrawReadout(g, "GR", $"{_grHold:0.0}", _rowFont, _rowBold, Height - 24,
            _grHold > 12 ? Theme.Danger : _grHold > 6 ? Theme.Warn : Theme.GoldLight);
    }

    private static void DrawCentered(Graphics g, string text, Font font, int x, int width, int y, Color color) =>
        TextRenderer.DrawText(g, text, font, new Rectangle(x - 4, y, width + 8, 14), color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

    private void DrawReadout(Graphics g, string label, string value, Font normal, Font bold, int y, Color valueColor)
    {
        TextRenderer.DrawText(g, label, bold, new Rectangle(10, y, 38, 16), Theme.Muted, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, value + " dB", normal, new Rectangle(40, y, Width - 48, 16), valueColor,
            TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    private static void DrawLevelBar(Graphics g, int x, int width, int top, int height, float level, float hold)
    {
        var step = height / (float)Segments;
        var dbPerSegment = -Floor / Segments;
        using var unlit = new SolidBrush(Theme.Unlit);
        for (var i = 0; i < Segments; i++)
        {
            var thresholdDb = Floor + (i + 1) * dbPerSegment;
            var y = top + height - (i + 1) * step;
            var lit = level >= thresholdDb - dbPerSegment * 0.5f;
            var color = thresholdDb > -3f ? Theme.Danger : thresholdDb > -12f ? Theme.GoldLight : Theme.Gold;
            using var litBrush = new SolidBrush(color);
            g.FillRectangle(lit ? litBrush : unlit, x, y + 1, width, step - 1.5f);
        }
        if (hold > Floor)
        {
            var y = top + height - (hold - Floor) / -Floor * height;
            using var mark = new Pen(Color.FromArgb(235, 255, 244, 214), 2f);
            g.DrawLine(mark, x - 2, y, x + width + 2, y);
        }
    }

    private static void DrawGrBar(Graphics g, int x, int width, int top, int height, float reduction, float hold)
    {
        var step = height / (float)Segments;
        var dbPerSegment = GrRange / Segments;
        using var unlit = new SolidBrush(Theme.Unlit);
        for (var i = 0; i < Segments; i++)
        {
            var thresholdDb = (i + 1) * dbPerSegment;   // top segment = first dB of reduction
            var y = top + i * step;
            var lit = reduction >= thresholdDb - dbPerSegment * 0.5f;
            var color = thresholdDb > 12f ? Theme.Danger : thresholdDb > 6f ? Theme.Warn : Theme.Gold;
            using var litBrush = new SolidBrush(color);
            g.FillRectangle(lit ? litBrush : unlit, x, y + 0.5f, width, step - 1.5f);
        }
        if (hold > 0.2f)
        {
            var y = top + hold / GrRange * height;
            using var mark = new Pen(Color.FromArgb(235, 255, 244, 214), 2f);
            g.DrawLine(mark, x - 2, y, x + width + 2, y);
        }
    }
}

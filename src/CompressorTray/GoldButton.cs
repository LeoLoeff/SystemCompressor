using System.Drawing.Drawing2D;

namespace SystemCompressor.Tray;

/// <summary>Flat button with a gold outline; filled gold when <see cref="Filled"/> is set.</summary>
public sealed class GoldButton : Control
{
    private bool _hover;
    private bool _filled;

    public GoldButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Font = Theme.Ui(9.5f, FontStyle.Bold);
    }

    public bool Filled
    {
        get => _filled;
        set { _filled = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);

        var rect = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = RoundedRectangle(rect, 9);
        if (_filled)
        {
            using var fill = new LinearGradientBrush(rect, _hover ? Theme.GoldLight : Theme.Gold, Theme.GoldDim, 90f);
            g.FillPath(fill, path);
        }
        else if (_hover)
        {
            using var fill = new SolidBrush(Color.FromArgb(40, Theme.Gold));
            g.FillPath(fill, path);
        }
        using (var border = new Pen(_hover || _filled ? Theme.GoldLight : Theme.Gold, 1.6f))
            g.DrawPath(border, path);

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, _filled ? Color.FromArgb(28, 24, 12) : Theme.GoldLight,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused)
            using (var focus = new Pen(Color.FromArgb(120, Theme.GoldLight)) { DashStyle = DashStyle.Dot })
                g.DrawPath(focus, RoundedRectangle(new RectangleF(4, 4, Width - 9, Height - 9), 6));
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    internal static GraphicsPath RoundedRectangle(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

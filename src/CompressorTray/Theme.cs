namespace SystemCompressor.Tray;

/// <summary>Dark grey surfaces with gold accents.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb(31, 32, 36);
    public static readonly Color Panel = Color.FromArgb(39, 40, 46);
    public static readonly Color Unlit = Color.FromArgb(52, 53, 60);
    public static readonly Color Gold = Color.FromArgb(212, 175, 55);
    public static readonly Color GoldLight = Color.FromArgb(240, 211, 124);
    public static readonly Color GoldDim = Color.FromArgb(128, 105, 38);
    public static readonly Color Text = Color.FromArgb(232, 229, 222);
    public static readonly Color Muted = Color.FromArgb(150, 150, 158);
    public static readonly Color Warn = Color.FromArgb(232, 140, 40);
    public static readonly Color Danger = Color.FromArgb(214, 72, 60);

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);
}

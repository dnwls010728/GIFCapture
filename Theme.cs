using System.Drawing.Drawing2D;

namespace GifCapture;

static class Theme
{
    // Contrast against Bg: Text ≈ 16:1, Muted ≈ 8:1, Faint ≈ 4.6:1 (still readable when disabled).
    public static readonly Color Bg = Color.FromArgb(0x14, 0x15, 0x19);
    public static readonly Color Surface = Color.FromArgb(0x20, 0x22, 0x28);
    public static readonly Color SurfaceHover = Color.FromArgb(0x2B, 0x2E, 0x37);
    public static readonly Color SurfacePressed = Color.FromArgb(0x35, 0x39, 0x43);
    public static readonly Color Border = Color.FromArgb(0x3A, 0x3E, 0x49);
    public static readonly Color Text = Color.FromArgb(0xF1, 0xF2, 0xF5);
    public static readonly Color Muted = Color.FromArgb(0xB0, 0xB4, 0xC0);
    public static readonly Color Faint = Color.FromArgb(0x80, 0x85, 0x92);
    public static readonly Color Accent = Color.FromArgb(0x5B, 0x8D, 0xFF);
    public static readonly Color AccentHover = Color.FromArgb(0x74, 0x9F, 0xFF);
    public static readonly Color AccentPressed = Color.FromArgb(0x4A, 0x78, 0xE0);
    public static readonly Color AccentSoft = Color.FromArgb(0x1F, 0x2B, 0x47);
    public static readonly Color Rec = Color.FromArgb(0xFF, 0x4D, 0x5E);
    public static readonly Color Danger = Color.FromArgb(0xFF, 0x7A, 0x86);
    public static readonly Color Success = Color.FromArgb(0x3D, 0xD6, 0x8C);
    public static readonly Color Popup = Color.FromArgb(0x2A, 0x2D, 0x35);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    static Icon? _appIcon;

    /// <summary>Window icon loaded from the embedded app.ico.</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (_appIcon != null) return _appIcon;
            using var s = typeof(Theme).Assembly.GetManifestResourceStream("app.ico");
            return _appIcon = s == null ? null : new Icon(s);
        }
    }

    public static void StyleTextBox(TextBox box)
    {
        box.BackColor = Surface;
        box.ForeColor = Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = new Font("Segoe UI", 10f);
    }

    public static Label MutedLabel(string text, Rectangle bounds) =>
        new() { Text = text, ForeColor = Muted, BackColor = Bg, Bounds = bounds, TextAlign = ContentAlignment.MiddleLeft };

    /// <summary>Small bold heading that separates groups of controls.</summary>
    public static Label SectionLabel(string text, Rectangle bounds) =>
        new()
        {
            Text = text,
            ForeColor = Muted,
            BackColor = Bg,
            Bounds = bounds,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };

    /// <summary>Owner-drawn tooltip that matches the dark theme.</summary>
    public static ToolTip CreateToolTip()
    {
        var font = new Font("Segoe UI", 9f);
        var tip = new ToolTip { OwnerDraw = true, InitialDelay = 450, AutoPopDelay = 10000, ReshowDelay = 150 };
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        tip.Popup += (_, e) =>
        {
            var text = e.AssociatedControl is { } c ? tip.GetToolTip(c) : "";
            var size = TextRenderer.MeasureText(text, font, new Size(320, 0), flags | TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(size.Width + 20, size.Height + 12);
        };
        tip.Draw += (_, e) =>
        {
            using (var bg = new SolidBrush(Popup)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            var r = Rectangle.Inflate(e.Bounds, -10, -6);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, font, r, Text, flags | TextFormatFlags.WordBreak);
        };
        tip.Disposed += (_, _) => font.Dispose();
        return tip;
    }

    public static void HighQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }
}

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GifCapture;

/// <summary>Draws a pixel magnifier: zoomed cells, a highlighted centre pixel, optional edge guides and a caption.</summary>
static class LoupeRenderer
{
    public const int Cells = 15;

    /// <param name="source">Image to magnify; <paramref name="center"/> is in its pixel coordinates.</param>
    /// <param name="edgeX">Draw a guide on the left edge of the centre column (a selection edge sits there).</param>
    /// <param name="edgeY">Draw a guide on the top edge of the centre row.</param>
    public static void Draw(Graphics g, Image source, Point center, Rectangle view, int zoom, string caption, Font font, bool edgeX = false, bool edgeY = false)
    {
        int half = Cells / 2;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var bg = new SolidBrush(Color.Black)) g.FillRectangle(bg, view);
        g.DrawImage(source, view, new Rectangle(center.X - half, center.Y - half, Cells, Cells), GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.SmoothingMode = SmoothingMode.None;

        var cell = new Rectangle(view.X + half * zoom, view.Y + half * zoom, zoom, zoom);
        if (edgeX || edgeY)
        {
            using var guide = new Pen(Theme.Accent, 2);
            if (edgeX) g.DrawLine(guide, cell.X, view.Y, cell.X, view.Bottom);
            if (edgeY) g.DrawLine(guide, view.X, cell.Y, view.Right, cell.Y);
        }
        else
        {
            using (var dark = new Pen(Color.Black)) g.DrawRectangle(dark, Rectangle.Inflate(cell, 1, 1));
            using (var light = new Pen(Color.White)) g.DrawRectangle(light, cell);
        }
        using (var border = new Pen(Theme.Accent, 2)) g.DrawRectangle(border, view.X + 1, view.Y + 1, view.Width - 2, view.Height - 2);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var size = TextRenderer.MeasureText(caption, font);
        var chip = new Rectangle(view.X + (view.Width - size.Width - 16) / 2, view.Bottom + 4, size.Width + 16, size.Height + 6);
        using (var path = Theme.RoundRect(chip, chip.Height / 2f))
        using (var b = new SolidBrush(Color.FromArgb(240, 28, 30, 36)))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, caption, font, chip, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Floating magnifier that follows the cursor while the region outline is being dragged.</summary>
sealed class LoupeWindow : Form
{
    readonly float _s;
    readonly Font _font;
    readonly Bitmap _grab = new(LoupeRenderer.Cells, LoupeRenderer.Cells, PixelFormat.Format32bppArgb);

    int Zoom => Math.Max(6, (int)(8 * _s));
    int ViewSize => LoupeRenderer.Cells * Zoom;
    int CaptionH => (int)(30 * _s);

    public LoupeWindow(float scale)
    {
        _s = scale;
        _font = new Font("Segoe UI", 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Size = new Size(ViewSize, ViewSize + CaptionH);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Keeps the loupe out of its own screen grabs and out of recordings.
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Magnifies the live screen around <paramref name="target"/> and places the loupe near <paramref name="cursor"/>.</summary>
    public void ShowAt(Point target, Point cursor, string caption, bool edgeX, bool edgeY)
    {
        int half = LoupeRenderer.Cells / 2;
        using (var g = Graphics.FromImage(_grab))
        {
            g.Clear(Color.Black);
            g.CopyFromScreen(target.X - half, target.Y - half, 0, 0, _grab.Size);
        }

        var size = new Size(ViewSize, ViewSize + CaptionH);
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            LoupeRenderer.Draw(g, _grab, new Point(half, half), new Rectangle(0, 0, ViewSize, ViewSize), Zoom, caption, _font, edgeX, edgeY);
        }

        var screen = Screen.FromPoint(cursor).Bounds;
        int gap = (int)(24 * _s);
        int x = cursor.X + gap, y = cursor.Y + gap;
        if (x + size.Width > screen.Right) x = cursor.X - gap - size.Width;
        if (y + size.Height > screen.Bottom) y = cursor.Y - gap - size.Height;

        if (!IsHandleCreated) CreateHandle();
        LayeredWindow.Update(this, bmp, new Point(x, y));
        if (!Visible) Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _grab.Dispose();
        }
        base.Dispose(disposing);
    }
}

static class LayeredWindow
{
    /// <summary>Pushes a 32bpp ARGB bitmap as the per-pixel-alpha content of a WS_EX_LAYERED window.</summary>
    public static bool Update(Form form, Bitmap bmp, Point location)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Native.SelectObject(memDc, hBitmap);
        try
        {
            var dst = new Native.POINT { X = location.X, Y = location.Y };
            var size = new Native.SIZE { Width = bmp.Width, Height = bmp.Height };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            return Native.UpdateLayeredWindow(form.Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace GifCapture;

/// <summary>Which corner, edge or centre of the frame a watermark hugs.</summary>
enum Placement { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

/// <summary>
/// Mark stamped on every frame: text in a chosen font, or an image such as a logo. Sizes and offsets are fractions of the
/// output, so the mark keeps its place and proportion through crops, scaling and rotation.
/// </summary>
sealed record Watermark
{
    public bool Enabled { get; init; }
    public bool IsImage { get; init; }
    public string Text { get; init; } = "";
    public string FontFamily { get; init; } = "Segoe UI";
    public FontStyle FontStyle { get; init; } = FontStyle.Bold;
    public int ColorArgb { get; init; } = Color.White.ToArgb();
    /// <summary>Dark outline that keeps text readable over any background.</summary>
    public bool Outline { get; init; } = true;
    public string? ImagePath { get; init; }
    /// <summary>Text: letter height as % of the output height. Image: width as % of the output width.</summary>
    public int SizePercent { get; init; } = 6;
    public int Opacity { get; init; } = 80;
    public Placement Anchor { get; init; } = Placement.BottomRight;
    /// <summary>Shift from the anchored spot, as fractions of the output width and height (set by dragging in the preview).</summary>
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }

    [JsonIgnore] public bool Visible => Enabled && (IsImage ? ImagePath != null : !string.IsNullOrWhiteSpace(Text));
}

/// <summary>What an item placed on frames is.</summary>
enum StickerKind { Image, Text, Mosaic }

/// <summary>Look of a text item. The defaults give the classic caption: white bold letters on a dark box.</summary>
sealed record TextStyle
{
    public string FontFamily { get; init; } = "Segoe UI";
    public FontStyle FontStyle { get; init; } = FontStyle.Bold;
    public int ColorArgb { get; init; } = Color.White.ToArgb();
    public bool Outline { get; init; }
    /// <summary>Dark rounded box behind the letters.</summary>
    public bool Box { get; init; } = true;
    /// <summary>Letter height as a fraction of the output height.</summary>
    public float Size { get; init; } = 0.065f;
}

/// <summary>
/// An item placed on some frames: an image, a line of text, or a pixelated patch. The centre is a fraction of the output.
/// An image's <see cref="Width"/> is a fraction of the output width and its height follows the image's aspect; text is
/// sized by its <see cref="Style"/>; a mosaic has both <see cref="Width"/> and <see cref="Height"/>.
/// <see cref="Angle"/> turns it clockwise about its centre, in degrees.
/// </summary>
sealed record Sticker(int Id, int ImageId, float X, float Y, float Width, int Opacity = 100, float Angle = 0)
{
    public StickerKind Kind { get; init; }
    public string Text { get; init; } = "";
    public TextStyle Style { get; init; } = new();
    /// <summary>Mosaic: height as a fraction of the output height.</summary>
    public float Height { get; init; }
    /// <summary>Mosaic: cell size from 1 (fine) to 10 (coarse).</summary>
    public int Cell { get; init; } = 4;
}

/// <summary>Pixels the decorations go over, which a mosaic averages: a BGRA buffer and the part of it the output area shows.</summary>
readonly record struct PixelSource(byte[] Bgra, int Width, Rectangle Region);

/// <summary>Decoded pixels of an inserted image (straight-alpha BGRA). Immutable, so any thread can turn it into a bitmap.</summary>
sealed class ImageAsset(Size size, byte[] bgra)
{
    public Size Size { get; } = size;
    public byte[] Bgra { get; } = bgra;
    public float Aspect => (float)Size.Height / Size.Width;

    /// <summary>Largest side kept on import: plenty for any GIF, and it keeps per-frame drawing cheap.</summary>
    const int MaxSide = 2048;

    public static ImageAsset FromImage(Image image)
    {
        float k = Math.Min(1f, (float)MaxSide / Math.Max(image.Width, image.Height));
        var size = new Size(Math.Max(1, (int)(image.Width * k)), Math.Max(1, (int)(image.Height * k)));
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(image, new Rectangle(Point.Empty, size));
        }
        return new ImageAsset(size, ReadArgb(bmp));
    }

    public static ImageAsset FromFile(string path)
    {
        // Read through a copy so the file is not left locked while the editor is open.
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var image = Image.FromStream(stream);
        return FromImage(image);
    }

    /// <summary>High-quality copy at <paramref name="size"/>.</summary>
    public ImageAsset Resize(Size size)
    {
        if (size == Size) return this;
        using var source = CreateBitmap();
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY); // no faded rim from sampling outside the image
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(Point.Empty, size), 0, 0, Size.Width, Size.Height, GraphicsUnit.Pixel, attrs);
        }
        return new ImageAsset(size, ReadArgb(bmp));
    }

    public Bitmap CreateBitmap()
    {
        var bmp = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(Point.Empty, Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Size.Height; y++)
                Marshal.Copy(Bgra, y * Size.Width * 4, data.Scan0 + y * data.Stride, Size.Width * 4);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    static byte[] ReadArgb(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int row = bmp.Width * 4;
            var buf = new byte[row * bmp.Height];
            for (int y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * row, row);
            return buf;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}

/// <summary>Everything drawn over the frames, shared by the preview and the export.</summary>
sealed record DecorSet(Watermark Watermark, ImageAsset? WatermarkImage, IReadOnlyDictionary<int, Sticker> Stickers,
    IReadOnlyDictionary<int, ImageAsset> Images)
{
    public static readonly DecorSet Empty = new(new Watermark(), null, new Dictionary<int, Sticker>(), new Dictionary<int, ImageAsset>());

    public bool HasWatermark => Watermark.Visible && (!Watermark.IsImage || WatermarkImage != null);

    /// <summary>A watermark on its own, with its image file loaded; an unreadable file just leaves the mark out.</summary>
    public static DecorSet FromWatermark(Watermark w) => Empty with { Watermark = w, WatermarkImage = w.Visible && w.IsImage ? TryLoad(w.ImagePath!) : null };

    public static ImageAsset? TryLoad(string path)
    {
        try { return ImageAsset.FromFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or NotSupportedException)
        {
            return null; // OutOfMemoryException is how GDI+ reports a file it cannot decode
        }
    }

    public bool Any(int[]? frameStickers) => HasWatermark || frameStickers is { Length: > 0 };
}

/// <summary>Geometry and drawing of watermarks and stickers. Positions are worked out from the output area alone.</summary>
static class Decor
{
    /// <summary>Gap between an anchored watermark and the frame edge, as a fraction of the shorter side.</summary>
    const float Margin = 0.03f;

    /// <summary>Mosaic cell edge per <see cref="Sticker.Cell"/> step, as a fraction of the shorter side.</summary>
    const float CellStep = 0.012f;

    /// <summary>Unturned box of a sticker, or null when its image is missing.</summary>
    public static RectangleF? StickerRect(Sticker s, DecorSet set, RectangleF area)
    {
        SizeF size;
        switch (s.Kind)
        {
            case StickerKind.Text:
                using (TextBlock(s, area, out var box)) size = box.Size;
                break;
            case StickerKind.Mosaic:
                size = new SizeF(s.Width * area.Width, s.Height * area.Height);
                break;
            default:
                if (!set.Images.TryGetValue(s.ImageId, out var image)) return null;
                float w = s.Width * area.Width;
                size = new SizeF(w, w * image.Aspect);
                break;
        }
        var c = StickerCenter(s, area);
        return new RectangleF(c.X - size.Width / 2, c.Y - size.Height / 2, size.Width, size.Height);
    }

    static PointF StickerCenter(Sticker s, RectangleF area) => new(area.X + s.X * area.Width, area.Y + s.Y * area.Height);

    static PointF Center(RectangleF r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Turns <paramref name="p"/> by <paramref name="degrees"/> clockwise about <paramref name="c"/> (screen axes, y down).</summary>
    public static PointF Rotate(PointF p, PointF c, float degrees)
    {
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        float dx = p.X - c.X, dy = p.Y - c.Y;
        return new PointF(c.X + (float)(dx * cos - dy * sin), c.Y + (float)(dx * sin + dy * cos));
    }

    /// <summary>Corners of a turned sticker: top-left, top-right, bottom-right, bottom-left.</summary>
    public static PointF[]? StickerCorners(Sticker s, DecorSet set, RectangleF area)
    {
        if (StickerRect(s, set, area) is not { } r) return null;
        var c = Center(r);
        return [Rotate(r.Location, c, s.Angle), Rotate(new(r.Right, r.Top), c, s.Angle),
            Rotate(new(r.Right, r.Bottom), c, s.Angle), Rotate(new(r.Left, r.Bottom), c, s.Angle)];
    }

    /// <summary>Whether <paramref name="p"/> lands on the sticker, taking its turn into account.</summary>
    public static bool StickerContains(Sticker s, DecorSet set, RectangleF area, PointF p, float slack = 0)
    {
        if (StickerRect(s, set, area) is not { } r) return false;
        return RectangleF.Inflate(r, slack, slack).Contains(Rotate(p, Center(r), -s.Angle));
    }

    static FontFamily Family(string name)
    {
        try { return new FontFamily(name); }
        catch (ArgumentException) { return new FontFamily("Segoe UI"); } // font uninstalled since it was chosen
    }

    /// <summary>
    /// Text as a path at the origin, with its tight bounds and the height of its lines (ascent to descent, so the same for
    /// every text in a font). Centred text is centred on x = 0.
    /// </summary>
    static GraphicsPath TextPath(string text, string familyName, FontStyle style, float em, bool centered,
        out RectangleF bounds, out float linesHeight)
    {
        var path = new GraphicsPath();
        using (var family = Family(familyName))
        {
            if (!family.IsStyleAvailable(style)) style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            float unit = em / family.GetEmHeight(style);
            int lines = text.Split('\n').Length;
            linesHeight = (lines - 1) * family.GetLineSpacing(style) * unit + (family.GetCellAscent(style) + family.GetCellDescent(style)) * unit;
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Alignment = centered ? StringAlignment.Center : StringAlignment.Near,
            };
            if (text.Length > 0) path.AddString(text, family, (int)style, em, PointF.Empty, sf);
        }
        bounds = path.PointCount > 0 ? path.GetBounds() : RectangleF.Empty;
        return path;
    }

    static float TextEm(Sticker s, RectangleF area) => Math.Max(4, s.Style.Size * area.Height);

    /// <summary>
    /// A text item as a path around the origin, with the box it takes up: its backdrop when it has one. The box spans whole
    /// lines rather than the inked letters, so items in one font line up whatever their letters.
    /// </summary>
    static GraphicsPath TextBlock(Sticker s, RectangleF area, out RectangleF box)
    {
        float em = TextEm(s, area);
        var path = TextPath(s.Text.Trim(), s.Style.FontFamily, s.Style.FontStyle, em, centered: true, out var b, out float height);
        box = b.IsEmpty
            ? new RectangleF(-em / 2, 0, em, height) // blank text keeps a box so it can still be picked and typed into
            : RectangleF.FromLTRB(b.Left, Math.Min(0, b.Top), b.Right, Math.Max(height, b.Bottom));
        box = s.Style.Box ? RectangleF.Inflate(box, em * 0.5f, em * 0.25f)
            : s.Style.Outline ? RectangleF.Inflate(box, em * 0.06f, em * 0.06f) : box;
        return path;
    }

    /// <summary>Size of the watermark's content before placement.</summary>
    static SizeF WatermarkSize(Watermark w, ImageAsset? image, RectangleF area)
    {
        if (w.IsImage)
        {
            if (image == null) return SizeF.Empty;
            float width = w.SizePercent / 100f * area.Width;
            return new SizeF(width, width * image.Aspect);
        }
        using var path = WatermarkPath(w, area, out var b);
        return b.Size;
    }

    static GraphicsPath WatermarkPath(Watermark w, RectangleF area, out RectangleF bounds) =>
        TextPath(w.Text.Trim(), w.FontFamily, w.FontStyle, Math.Max(4, w.SizePercent / 100f * area.Height), centered: false, out bounds, out _);

    public static RectangleF WatermarkRect(Watermark w, ImageAsset? image, RectangleF area)
    {
        var size = WatermarkSize(w, image, area);
        var origin = AnchoredOrigin(w.Anchor, size, area);
        return new RectangleF(origin.X + w.OffsetX * area.Width, origin.Y + w.OffsetY * area.Height, size.Width, size.Height);
    }

    static PointF AnchoredOrigin(Placement anchor, SizeF size, RectangleF area)
    {
        float m = Margin * Math.Min(area.Width, area.Height);
        int col = (int)anchor % 3, row = (int)anchor / 3;
        float x = col switch { 0 => area.X + m, 1 => area.X + (area.Width - size.Width) / 2, _ => area.Right - m - size.Width };
        float y = row switch { 0 => area.Y + m, 1 => area.Y + (area.Height - size.Height) / 2, _ => area.Bottom - m - size.Height };
        return new PointF(x, y);
    }

    /// <summary>Offset that puts the watermark's top-left at <paramref name="location"/>, for dragging.</summary>
    public static (float X, float Y) OffsetFor(Watermark w, ImageAsset? image, RectangleF area, PointF location)
    {
        var origin = AnchoredOrigin(w.Anchor, WatermarkSize(w, image, area), area);
        return ((location.X - origin.X) / area.Width, (location.Y - origin.Y) / area.Height);
    }

    /// <summary>Size percent that makes the watermark <paramref name="width"/> wide, for resizing by its handle.</summary>
    public static int SizeFor(Watermark w, ImageAsset? image, RectangleF area, float width)
    {
        var current = WatermarkSize(w, image, area);
        if (current.Width <= 0) return w.SizePercent;
        return Math.Clamp((int)Math.Round(w.SizePercent * width / current.Width), 1, 100);
    }

    static int Alpha(int opacity) => (int)Math.Round(255 * Math.Clamp(opacity, 0, 100) / 100.0);

    /// <summary>
    /// Draws the stickers listed for a frame, then the watermark. <paramref name="bitmapFor"/> turns an image (keyed by
    /// asset id, null for the watermark image) into a bitmap for the given drawn size, so callers can cache bitmaps in
    /// whatever way suits their thread; with <paramref name="disposeBitmaps"/> each one is disposed after drawing.
    /// <paramref name="pixels"/> supplies the frame under the decorations, asked for only when a mosaic needs it.
    /// </summary>
    public static void Draw(Graphics g, RectangleF area, DecorSet set, int[]? frameStickers,
        Func<int?, ImageAsset, Size, Bitmap> bitmapFor, bool disposeBitmaps, Func<PixelSource>? pixels = null)
    {
        void Image(int? id, ImageAsset asset, RectangleF r, int opacity, float angle = 0)
        {
            if (r.Width < 1 || r.Height < 1) return;
            var bmp = bitmapFor(id, asset, new Size((int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height)));
            var turned = angle % 360 != 0 ? g.Save() : null;
            try
            {
                if (turned != null)
                {
                    // Turn about the centre: draw centred on the origin of a rotated coordinate system.
                    var c = Center(r);
                    g.TranslateTransform(c.X, c.Y);
                    g.RotateTransform(angle);
                    r = new RectangleF(-r.Width / 2, -r.Height / 2, r.Width, r.Height);
                }
                DrawImage(g, bmp, r, opacity);
            }
            finally
            {
                if (turned != null) g.Restore(turned);
                if (disposeBitmaps) bmp.Dispose();
            }
        }

        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceOver;
        g.SetClip(area);

        if (frameStickers != null)
        {
            // Mosaics hide what was filmed, so they go first: they never blur the text and images placed over them.
            PixelSource? source = null;
            if (pixels != null)
                foreach (var id in frameStickers)
                    if (set.Stickers.TryGetValue(id, out var s) && s.Kind == StickerKind.Mosaic)
                        DrawMosaic(g, s, area, source ??= pixels());
            foreach (var id in frameStickers)
            {
                if (!set.Stickers.TryGetValue(id, out var s)) continue;
                if (s.Kind == StickerKind.Text) DrawText(g, s, area);
                else if (s.Kind == StickerKind.Image && set.Images.TryGetValue(s.ImageId, out var asset))
                    Image(s.ImageId, asset, StickerRect(s, set, area)!.Value, s.Opacity, s.Angle);
            }
        }

        if (set.HasWatermark)
        {
            var w = set.Watermark;
            if (w.IsImage) Image(null, set.WatermarkImage!, WatermarkRect(w, set.WatermarkImage, area), w.Opacity);
            else DrawWatermarkText(g, w, area);
        }
        g.Restore(state);
    }

    static void DrawImage(Graphics g, Bitmap bmp, RectangleF r, int opacity)
    {
        if (opacity >= 100)
        {
            g.DrawImage(bmp, r);
            return;
        }
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = opacity / 100f });
        g.DrawImage(bmp, [r.Location, new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom)],
            new RectangleF(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel, attrs);
    }

    /// <summary>Fills letters, under a dark outline that keeps them readable over any background when asked.</summary>
    static void FillLetters(Graphics g, GraphicsPath path, int argb, int alpha, bool outline, float em)
    {
        if (outline)
        {
            using var pen = new Pen(Color.FromArgb(alpha * 3 / 4, 0, 0, 0), Math.Max(1.5f, em * 0.12f)) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
        using var fill = new SolidBrush(Color.FromArgb(alpha, Color.FromArgb(argb)));
        g.FillPath(fill, path);
    }

    static void DrawWatermarkText(Graphics g, Watermark w, RectangleF area)
    {
        using var path = WatermarkPath(w, area, out var b);
        var r = WatermarkRect(w, null, area);
        using var move = new Matrix();
        move.Translate(r.X - b.X, r.Y - b.Y);
        path.Transform(move);
        FillLetters(g, path, w.ColorArgb, Alpha(w.Opacity), w.Outline, w.SizePercent / 100f * area.Height);
    }

    static void DrawText(Graphics g, Sticker s, RectangleF area)
    {
        using var path = TextBlock(s, area, out var box);
        float em = TextEm(s, area);
        int alpha = Alpha(s.Opacity);
        var state = g.Save();
        // The box's centre goes on the sticker's centre, turned about it.
        var c = StickerCenter(s, area);
        g.TranslateTransform(c.X, c.Y);
        if (s.Angle % 360 != 0) g.RotateTransform(s.Angle);
        g.TranslateTransform(-(box.X + box.Width / 2), -(box.Y + box.Height / 2));
        if (s.Style.Box)
        {
            using var backdrop = Theme.RoundRect(box, em * 0.3f);
            using var bg = new SolidBrush(Color.FromArgb(alpha * 205 / 255, 16, 16, 20));
            g.FillPath(bg, backdrop);
        }
        FillLetters(g, path, s.Style.ColorArgb, alpha, s.Style.Outline, em);
        g.Restore(state);
    }

    /// <summary>
    /// Pixelates the frame under a mosaic: each cell of a grid fixed to the frame (so cells stay put while the patch moves)
    /// is filled with the average colour of the pixels it covers.
    /// </summary>
    static void DrawMosaic(Graphics g, Sticker s, RectangleF area, PixelSource px)
    {
        if (StickerCorners(s, DecorSet.Empty, area) is not { } corners || px.Region.Width <= 0 || px.Region.Height <= 0) return;
        float cell = Math.Max(s.Cell * CellStep * Math.Min(area.Width, area.Height), 2 * area.Width / px.Region.Width);
        float left = Math.Max(area.Left, corners.Min(p => p.X)), right = Math.Min(area.Right, corners.Max(p => p.X));
        float top = Math.Max(area.Top, corners.Min(p => p.Y)), bottom = Math.Min(area.Bottom, corners.Max(p => p.Y));
        if (right <= left || bottom <= top) return;

        var state = g.Save();
        using (var clip = new GraphicsPath())
        {
            clip.AddPolygon(corners);
            g.SetClip(clip, CombineMode.Intersect);
        }
        // Without anti-aliasing neighbouring cells meet exactly, with no seams between them.
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        int alpha = Alpha(s.Opacity);
        float kx = px.Region.Width / area.Width, ky = px.Region.Height / area.Height;
        int i0 = (int)Math.Floor((left - area.X) / cell), i1 = (int)Math.Ceiling((right - area.X) / cell);
        int j0 = (int)Math.Floor((top - area.Y) / cell), j1 = (int)Math.Ceiling((bottom - area.Y) / cell);
        var bgra = px.Bgra;
        using var brush = new SolidBrush(Color.Black);
        for (int j = j0; j < j1; j++)
        {
            float y0 = area.Y + j * cell, y1 = Math.Min(y0 + cell, area.Bottom);
            int sy0 = Math.Clamp((int)((y0 - area.Y) * ky), 0, px.Region.Height - 1);
            int sy1 = Math.Clamp((int)Math.Ceiling((y1 - area.Y) * ky), sy0 + 1, px.Region.Height);
            for (int i = i0; i < i1; i++)
            {
                float x0 = area.X + i * cell, x1 = Math.Min(x0 + cell, area.Right);
                int sx0 = Math.Clamp((int)((x0 - area.X) * kx), 0, px.Region.Width - 1);
                int sx1 = Math.Clamp((int)Math.Ceiling((x1 - area.X) * kx), sx0 + 1, px.Region.Width);
                long b = 0, gr = 0, r = 0;
                for (int y = sy0; y < sy1; y++)
                {
                    int o = ((px.Region.Y + y) * px.Width + px.Region.X + sx0) * 4;
                    for (int x = sx0; x < sx1; x++, o += 4)
                    {
                        b += bgra[o];
                        gr += bgra[o + 1];
                        r += bgra[o + 2];
                    }
                }
                long n = (long)(sy1 - sy0) * (sx1 - sx0);
                brush.Color = Color.FromArgb(alpha, (int)(r / n), (int)(gr / n), (int)(b / n));
                g.FillRectangle(brush, x0, y0, x1 - x0, y1 - y0);
            }
        }
        g.Restore(state);
    }
}

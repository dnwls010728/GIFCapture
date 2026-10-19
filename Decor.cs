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

/// <summary>
/// An image placed on some frames. Centre and width are fractions of the output; height follows the image's aspect.
/// <see cref="Angle"/> turns it clockwise about its centre, in degrees.
/// </summary>
sealed record Sticker(int Id, int ImageId, float X, float Y, float Width, int Opacity = 100, float Angle = 0);

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

/// <summary>Everything drawn over the frames besides captions, shared by the preview and the export.</summary>
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

    public static RectangleF StickerRect(Sticker s, ImageAsset image, RectangleF area)
    {
        float w = s.Width * area.Width, h = w * image.Aspect;
        return new RectangleF(area.X + s.X * area.Width - w / 2, area.Y + s.Y * area.Height - h / 2, w, h);
    }

    static PointF Center(RectangleF r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Turns <paramref name="p"/> by <paramref name="degrees"/> clockwise about <paramref name="c"/> (screen axes, y down).</summary>
    public static PointF Rotate(PointF p, PointF c, float degrees)
    {
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        float dx = p.X - c.X, dy = p.Y - c.Y;
        return new PointF(c.X + (float)(dx * cos - dy * sin), c.Y + (float)(dx * sin + dy * cos));
    }

    /// <summary>Corners of a turned sticker: top-left, top-right, bottom-right, bottom-left.</summary>
    public static PointF[] StickerCorners(Sticker s, ImageAsset image, RectangleF area)
    {
        var r = StickerRect(s, image, area);
        var c = Center(r);
        return [Rotate(r.Location, c, s.Angle), Rotate(new(r.Right, r.Top), c, s.Angle),
            Rotate(new(r.Right, r.Bottom), c, s.Angle), Rotate(new(r.Left, r.Bottom), c, s.Angle)];
    }

    /// <summary>Whether <paramref name="p"/> lands on the sticker, taking its turn into account.</summary>
    public static bool StickerContains(Sticker s, ImageAsset image, RectangleF area, PointF p, float slack = 0)
    {
        var r = StickerRect(s, image, area);
        return RectangleF.Inflate(r, slack, slack).Contains(Rotate(p, Center(r), -s.Angle));
    }

    /// <summary>Watermark text as a path at the origin, with its tight bounds.</summary>
    static GraphicsPath TextPath(Watermark w, RectangleF area, out RectangleF bounds)
    {
        var path = new GraphicsPath();
        float em = Math.Max(4, w.SizePercent / 100f * area.Height);
        FontFamily family;
        try { family = new FontFamily(w.FontFamily); }
        catch (ArgumentException) { family = new FontFamily("Segoe UI"); } // font uninstalled since it was chosen
        using (family)
        {
            var style = w.FontStyle;
            if (!family.IsStyleAvailable(style)) style = family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            using var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
            path.AddString(w.Text.Trim(), family, (int)style, em, PointF.Empty, sf);
        }
        bounds = path.GetBounds();
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
        using var path = TextPath(w, area, out var b);
        return b.Size;
    }

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

    /// <summary>
    /// Draws the stickers listed for a frame, then the watermark. <paramref name="bitmapFor"/> turns an image (keyed by
    /// asset id, null for the watermark image) into a bitmap for the given drawn size, so callers can cache bitmaps in
    /// whatever way suits their thread; with <paramref name="disposeBitmaps"/> each one is disposed after drawing.
    /// </summary>
    public static void Draw(Graphics g, RectangleF area, DecorSet set, int[]? frameStickers,
        Func<int?, ImageAsset, Size, Bitmap> bitmapFor, bool disposeBitmaps)
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
            foreach (var id in frameStickers)
            {
                if (!set.Stickers.TryGetValue(id, out var s) || !set.Images.TryGetValue(s.ImageId, out var asset)) continue;
                Image(s.ImageId, asset, StickerRect(s, asset, area), s.Opacity, s.Angle);
            }

        if (set.HasWatermark)
        {
            var w = set.Watermark;
            if (w.IsImage) Image(null, set.WatermarkImage!, WatermarkRect(w, set.WatermarkImage, area), w.Opacity);
            else DrawText(g, w, area);
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

    static void DrawText(Graphics g, Watermark w, RectangleF area)
    {
        using var path = TextPath(w, area, out var b);
        var r = WatermarkRect(w, null, area);
        using var move = new Matrix();
        move.Translate(r.X - b.X, r.Y - b.Y);
        path.Transform(move);

        int alpha = (int)Math.Round(255 * Math.Clamp(w.Opacity, 0, 100) / 100.0);
        if (w.Outline)
        {
            float em = w.SizePercent / 100f * area.Height;
            using var pen = new Pen(Color.FromArgb(alpha * 3 / 4, 0, 0, 0), Math.Max(1.5f, em * 0.12f)) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
        using var fill = new SolidBrush(Color.FromArgb(alpha, Color.FromArgb(w.ColorArgb)));
        g.FillPath(fill, path);
    }
}

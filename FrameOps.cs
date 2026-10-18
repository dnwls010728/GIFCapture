using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GifCapture;

/// <summary>How enlargements are drawn. Shrinking always uses smooth filtering.</summary>
enum ResampleMode
{
    Sharp,  // every pixel becomes a hard-edged block
    Smooth, // bicubic
    Smart,  // Scale2x/Scale3x: blocks, with diagonal edges rounded off
}

/// <summary>Helpers over tightly packed top-down BGRA buffers.</summary>
static class FrameOps
{
    public static byte[] Crop(byte[] src, Size size, Rectangle r)
    {
        var dst = new byte[r.Width * r.Height * 4];
        int srcRow = size.Width * 4, dstRow = r.Width * 4;
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(src, (r.Y + y) * srcRow + r.X * 4, dst, y * dstRow, dstRow);
        return dst;
    }

    public static byte[] Scale(byte[] src, Size size, Size target, ResampleMode mode = ResampleMode.Smooth)
    {
        bool enlarging = target.Width >= size.Width && target.Height >= size.Height;
        if (!enlarging) mode = ResampleMode.Smooth;
        if (mode == ResampleMode.Smart)
        {
            // Whole passes first (6× = 3× then 2×); any fractional remainder is smoothed below.
            while (Math.Min(target.Width / (double)size.Width, target.Height / (double)size.Height) is var r && r >= 2)
            {
                int f = r >= 3 && (r < 4 || r % 3 == 0) ? 3 : 2;
                src = f == 3 ? Scale3x(src, size) : Scale2x(src, size);
                size = new Size(size.Width * f, size.Height * f);
            }
            if (size == target) return src;
            mode = ResampleMode.Smooth;
        }

        using var source = ToBitmap(src, size);
        using var scaled = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(scaled))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY); // avoids dark fringes at the edges
            bool blocks = mode == ResampleMode.Sharp;
            g.InterpolationMode = blocks ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = blocks ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(source, new Rectangle(Point.Empty, target), 0, 0, size.Width, size.Height, GraphicsUnit.Pixel, attrs);
        }
        return FromBitmap(scaled);
    }

    /// <summary>Pixels as 0xRRGGBB, with the unused alpha byte cleared so equal colours compare equal.</summary>
    static uint[] Pixels(byte[] src)
    {
        var p = MemoryMarshal.Cast<byte, uint>(src).ToArray();
        for (int i = 0; i < p.Length; i++) p[i] &= 0xFFFFFF;
        return p;
    }

    /// <summary>Scale2x (AdvMAME2x): doubles the size, rounding off staircase edges between flat colours.</summary>
    static byte[] Scale2x(byte[] src, Size size)
    {
        int w = size.Width, h = size.Height, ow = w * 2;
        var p = Pixels(src);
        var o = new uint[ow * h * 2];
        for (int y = 0; y < h; y++)
        {
            int up = Math.Max(y - 1, 0) * w, row = y * w, down = Math.Min(y + 1, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int l = Math.Max(x - 1, 0), r = Math.Min(x + 1, w - 1);
                uint b = p[up + x], d = p[row + l], e = p[row + x], f = p[row + r], hh = p[down + x];
                int i = y * 2 * ow + x * 2;
                if (b != hh && d != f)
                {
                    o[i] = d == b ? d : e;
                    o[i + 1] = b == f ? f : e;
                    o[i + ow] = d == hh ? d : e;
                    o[i + ow + 1] = hh == f ? f : e;
                }
                else o[i] = o[i + 1] = o[i + ow] = o[i + ow + 1] = e;
            }
        }
        return MemoryMarshal.AsBytes(o.AsSpan()).ToArray();
    }

    /// <summary>Scale3x (AdvMAME3x): triples the size, rounding off staircase edges between flat colours.</summary>
    static byte[] Scale3x(byte[] src, Size size)
    {
        int w = size.Width, h = size.Height, ow = w * 3;
        var p = Pixels(src);
        var o = new uint[ow * h * 3];
        for (int y = 0; y < h; y++)
        {
            int up = Math.Max(y - 1, 0) * w, row = y * w, down = Math.Min(y + 1, h - 1) * w;
            for (int x = 0; x < w; x++)
            {
                int l = Math.Max(x - 1, 0), r = Math.Min(x + 1, w - 1);
                uint a = p[up + l], b = p[up + x], c = p[up + r];
                uint d = p[row + l], e = p[row + x], f = p[row + r];
                uint g = p[down + l], hh = p[down + x], k = p[down + r];
                int i = y * 3 * ow + x * 3;
                if (b != hh && d != f)
                {
                    o[i] = d == b ? d : e;
                    o[i + 1] = (d == b && e != c) || (b == f && e != a) ? b : e;
                    o[i + 2] = b == f ? f : e;
                    o[i + ow] = (d == b && e != g) || (d == hh && e != a) ? d : e;
                    o[i + ow + 1] = e;
                    o[i + ow + 2] = (b == f && e != k) || (hh == f && e != c) ? f : e;
                    o[i + ow * 2] = d == hh ? d : e;
                    o[i + ow * 2 + 1] = (d == hh && e != k) || (hh == f && e != g) ? hh : e;
                    o[i + ow * 2 + 2] = hh == f ? f : e;
                }
                else
                    for (int j = 0; j < 3; j++) o[i + j * ow] = o[i + j * ow + 1] = o[i + j * ow + 2] = e;
            }
        }
        return MemoryMarshal.AsBytes(o.AsSpan()).ToArray();
    }

    public static byte[] RotateFlip(byte[] src, Size size, RotateFlipType type)
    {
        using var bmp = ToBitmap(src, size);
        bmp.RotateFlip(type);
        return FromBitmap(bmp);
    }

    public static Bitmap ToBitmap(byte[] bgra, Size size)
    {
        var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
        CopyInto(bmp, bgra);
        return bmp;
    }

    /// <summary>Overwrites <paramref name="bmp"/> (same size as the buffer) with <paramref name="bgra"/>.</summary>
    public static void CopyInto(Bitmap bmp, byte[] bgra)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(bgra, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public static byte[] FromBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            var buf = new byte[rowBytes * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * rowBytes, rowBytes);
            return buf;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Downscaled copy that fits inside <paramref name="box"/>, for thumbnails.</summary>
    public static Bitmap Thumbnail(byte[] bgra, Size size, Size box)
    {
        float k = Math.Min((float)box.Width / size.Width, (float)box.Height / size.Height);
        var target = new Size(Math.Max(1, (int)(size.Width * k)), Math.Max(1, (int)(size.Height * k)));
        using var source = ToBitmap(bgra, size);
        var thumb = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(thumb);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(Point.Empty, target));
        return thumb;
    }
}

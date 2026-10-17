using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GifCapture;

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

    public static byte[] Scale(byte[] src, Size size, Size target)
    {
        using var source = ToBitmap(src, size);
        using var scaled = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(scaled))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY); // avoids dark fringes at the edges
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(source, new Rectangle(Point.Empty, target), 0, 0, size.Width, size.Height, GraphicsUnit.Pixel, attrs);
        }
        return FromBitmap(scaled);
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

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GifCapture;

/// <summary>How enlargements are drawn. Shrinking always uses smooth filtering.</summary>
enum ResampleMode
{
    Sharp,  // every pixel becomes a hard-edged block
    Smooth, // bicubic
    Smart,  // xBR: pixel art with diagonals and curves rounded off
    Crisp,  // Lanczos-3 plus an unsharp mask: clean edges for screenshots and text
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
        double ratio = Math.Max(target.Width / (double)size.Width, target.Height / (double)size.Height);
        switch (mode)
        {
            case ResampleMode.Smart:
            {
                // One xBR pass to the next whole factor, then a Lanczos shrink for any fraction: shrinking keeps the edges
                // xBR drew, where enlarging the remainder would blur them.
                int n = (int)Math.Ceiling(ratio - 1e-9);
                if (n > 1)
                {
                    src = Xbr(src, size, n);
                    size = new Size(size.Width * n, size.Height * n);
                }
                return size == target ? src : Lanczos(src, size, target);
            }
            case ResampleMode.Crisp:
                // The blur radius follows the scale so the boost lands on the edges of the original pixels.
                return Sharpen(Lanczos(src, size, target), target, Math.Clamp(0.5 * ratio, 0.7, 2.5), 0.7f);
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

    // ───────────── Lanczos & sharpening ─────────────

    /// <summary>For each output index, <see cref="Taps"/> clamped source indices and their normalised weights.</summary>
    readonly record struct Kernel(int Taps, int[] Index, float[] Weight);

    static Kernel LanczosKernel(int from, int to)
    {
        const int lobes = 3;
        double scale = to / (double)from, stretch = Math.Max(1, 1 / scale); // widen the filter when shrinking
        double support = lobes * stretch;
        int taps = (int)Math.Ceiling(support * 2) + 1;
        var index = new int[to * taps];
        var weight = new float[to * taps];
        for (int o = 0; o < to; o++)
        {
            double center = (o + 0.5) / scale - 0.5;
            int left = (int)Math.Floor(center - support) + 1;
            double sum = 0;
            for (int j = 0; j < taps; j++)
            {
                double x = (left + j - center) / stretch;
                double w = x == 0 ? 1 : Math.Abs(x) < lobes ? lobes * Math.Sin(Math.PI * x) * Math.Sin(Math.PI * x / lobes) / (Math.PI * Math.PI * x * x) : 0;
                index[o * taps + j] = Math.Clamp(left + j, 0, from - 1);
                weight[o * taps + j] = (float)w;
                sum += w;
            }
            for (int j = 0; j < taps; j++) weight[o * taps + j] /= (float)sum;
        }
        return new Kernel(taps, index, weight);
    }

    /// <summary>Separable Lanczos-3 resize to any size.</summary>
    static byte[] Lanczos(byte[] src, Size size, Size target)
    {
        int sw = size.Width, sh = size.Height, tw = target.Width, th = target.Height;
        var kx = LanczosKernel(sw, tw);
        var ky = LanczosKernel(sh, th);
        var mid = new float[tw * sh * 3];
        Parallel.For(0, sh, y =>
        {
            int row = y * sw;
            for (int x = 0; x < tw; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int j = 0, k = x * kx.Taps; j < kx.Taps; j++, k++)
                {
                    int i = (row + kx.Index[k]) * 4;
                    float w = kx.Weight[k];
                    b += src[i] * w; g += src[i + 1] * w; r += src[i + 2] * w;
                }
                int m = (y * tw + x) * 3;
                mid[m] = b; mid[m + 1] = g; mid[m + 2] = r;
            }
        });
        var dst = new byte[tw * th * 4];
        Parallel.For(0, th, y =>
        {
            for (int x = 0; x < tw; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int j = 0, k = y * ky.Taps; j < ky.Taps; j++, k++)
                {
                    int m = (ky.Index[k] * tw + x) * 3;
                    float w = ky.Weight[k];
                    b += mid[m] * w; g += mid[m + 1] * w; r += mid[m + 2] * w;
                }
                int i = (y * tw + x) * 4;
                dst[i] = ToByte(b); dst[i + 1] = ToByte(g); dst[i + 2] = ToByte(r); dst[i + 3] = 255;
            }
        });
        return dst;
    }

    /// <summary>Unsharp mask: pushes each pixel away from its Gaussian-blurred surroundings by <paramref name="amount"/>.</summary>
    static byte[] Sharpen(byte[] src, Size size, double sigma, float amount)
    {
        int w = size.Width, h = size.Height, radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[radius * 2 + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++) sum += kernel[i + radius] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var mid = new float[w * h * 3];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int j = -radius; j <= radius; j++)
                {
                    int i = (y * w + Math.Clamp(x + j, 0, w - 1)) * 4;
                    float k = kernel[j + radius];
                    b += src[i] * k; g += src[i + 1] * k; r += src[i + 2] * k;
                }
                int m = (y * w + x) * 3;
                mid[m] = b; mid[m + 1] = g; mid[m + 2] = r;
            }
        });
        var dst = new byte[src.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int j = -radius; j <= radius; j++)
                {
                    int m = (Math.Clamp(y + j, 0, h - 1) * w + x) * 3;
                    float k = kernel[j + radius];
                    b += mid[m] * k; g += mid[m + 1] * k; r += mid[m + 2] * k;
                }
                int i = (y * w + x) * 4;
                dst[i] = ToByte(src[i] + (src[i] - b) * amount);
                dst[i + 1] = ToByte(src[i + 1] + (src[i + 1] - g) * amount);
                dst[i + 2] = ToByte(src[i + 2] + (src[i + 2] - r) * amount);
                dst[i + 3] = 255;
            }
        });
        return dst;
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    // ───────────── xBR ─────────────

    /// <summary>Pixels as 0xRRGGBB, with the unused alpha byte cleared so equal colours compare equal.</summary>
    static uint[] Pixels(byte[] src)
    {
        var p = MemoryMarshal.Cast<byte, uint>(src).ToArray();
        for (int i = 0; i < p.Length; i++) p[i] &= 0xFFFFFF;
        return p;
    }

    /// <summary>Perceptual colour difference: luma dominates, chroma still separates colours of equal brightness.</summary>
    static float Diff(uint a, uint b)
    {
        int r = (int)(a >> 16 & 255) - (int)(b >> 16 & 255);
        int g = (int)(a >> 8 & 255) - (int)(b >> 8 & 255);
        int bl = (int)(a & 255) - (int)(b & 255);
        float y = 0.299f * r + 0.587f * g + 0.114f * bl;
        float u = -0.169f * r - 0.331f * g + 0.5f * bl;
        float v = 0.5f * r - 0.419f * g - 0.081f * bl;
        return Math.Abs(y) + 0.5f * (Math.Abs(u) + Math.Abs(v));
    }

    static bool Near(uint a, uint b) => Diff(a, b) < 24;

    static uint Mix(uint a, uint b, float t)
    {
        uint c = 0;
        for (int s = 0; s < 24; s += 8)
        {
            float x = a >> s & 255, y = b >> s & 255;
            c |= (uint)(x + (y - x) * t + 0.5f) << s;
        }
        return c;
    }

    /// <summary>
    /// xBR level 2 (after Hyllian's MIT-licensed shader) at any whole factor. Each corner of a source pixel looks for an edge
    /// running across it (45°, or a shallow or steep 2:1 slope) and blends toward the colour on the far side. The blend is
    /// anti-aliased over one output pixel, so diagonals and curves come out smooth while flat areas stay solid.
    /// </summary>
    static byte[] Xbr(byte[] src, Size size, int n)
    {
        int w = size.Width, h = size.Height, ow = w * n;
        var p = Pixels(src);
        var o = new uint[ow * h * n];
        float soft = 1f / n; // edge softness: one output pixel, in source pixels
        Parallel.For(0, h, y =>
        {
            var nb = new uint[25]; // 5×5 neighbourhood, row-major, centre at 12
            Span<bool> diag = stackalloc bool[4], shallow = stackalloc bool[4], steep = stackalloc bool[4];
            Span<uint> toward = stackalloc uint[4];
            for (int x = 0; x < w; x++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    int row = Math.Clamp(y + dy, 0, h - 1) * w;
                    for (int dx = -2; dx <= 2; dx++) nb[(dy + 2) * 5 + dx + 2] = p[row + Math.Clamp(x + dx, 0, w - 1)];
                }
                uint e = nb[12];
                bool any = false;
                for (int c = 0; c < 4; c++)
                {
                    // Mirror the neighbourhood so this corner becomes the bottom-right one; xBR's rules are mirror-symmetric.
                    int sx = (c & 1) == 0 ? 1 : -1, sy = c < 2 ? 1 : -1;
                    uint At(int dx, int dy) => nb[(sy * dy + 2) * 5 + sx * dx + 2];
                    uint b = At(0, -1), cc = At(1, -1), d = At(-1, 0), f = At(1, 0);
                    uint g = At(-1, 1), hh = At(0, 1), i = At(1, 1);
                    uint f4 = At(2, 0), i4 = At(2, 1), h5 = At(0, 2), i5 = At(1, 2);
                    diag[c] = shallow[c] = steep[c] = false;
                    if (e == f || e == hh) continue;
                    bool allowed = (!Near(f, b) && !Near(hh, d)) || (Near(e, i) && !Near(f, i4) && !Near(hh, i5)) || Near(e, g) || Near(e, cc);
                    if (!allowed) continue;
                    // The edge runs along f–h when colours differ more across that line than along it.
                    float across = Diff(e, cc) + Diff(e, g) + Diff(i, h5) + Diff(i, f4) + 4 * Diff(hh, f);
                    float along = Diff(hh, d) + Diff(hh, i5) + Diff(f, i4) + Diff(f, b) + 4 * Diff(e, i);
                    if (across >= along) continue;
                    diag[c] = any = true;
                    float fg = Diff(f, g), hc = Diff(hh, cc);
                    shallow[c] = 2 * fg <= hc && e != g && d != g;
                    steep[c] = fg >= 2 * hc && e != cc && b != cc;
                    toward[c] = Diff(e, f) <= Diff(e, hh) ? f : hh;
                }

                int at = y * n * ow + x * n;
                if (!any)
                {
                    for (int oy = 0; oy < n; oy++) o.AsSpan(at + oy * ow, n).Fill(e);
                    continue;
                }
                for (int oy = 0; oy < n; oy++)
                    for (int ox = 0; ox < n; ox++)
                    {
                        float best = 0;
                        uint color = e;
                        for (int c = 0; c < 4; c++)
                        {
                            if (!diag[c]) continue;
                            float fx = (ox + 0.5f) / n, fy = (oy + 0.5f) / n;
                            if ((c & 1) != 0) fx = 1 - fx;
                            if (c >= 2) fy = 1 - fy;
                            float t = Coverage(fx + fy - 1.5f, 1.414f);
                            if (shallow[c]) t = Math.Max(t, Coverage(0.5f * fx + fy - 1, 1.118f));
                            if (steep[c]) t = Math.Max(t, Coverage(fx + 0.5f * fy - 1, 1.118f));
                            if (t > best) { best = t; color = toward[c]; }
                        }
                        o[at + oy * ow + ox] = best > 0 ? Mix(e, color, best) : e;
                    }
            }

            // Signed distance past an edge line (scaled by the line's normal length), turned into pixel coverage.
            float Coverage(float d, float norm) => Math.Clamp(d / (norm * soft) + 0.5f, 0, 1);
        });
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

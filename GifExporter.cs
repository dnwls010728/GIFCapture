using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GifCapture;

/// <summary>Everything about an export that is not the frame list itself.</summary>
sealed record ExportOptions(
    Rectangle Crop,              // in source pixels; empty = whole frame
    RotateFlipType Rotate,
    int ScalePercent,
    int Quality,                 // 10..100
    int MaxFps,                  // 0 = keep every frame
    bool MergeSimilar,
    int PlayCount,               // 0 = loop forever
    ResampleMode Resample = ResampleMode.Sharp, // used when enlarging
    DecorSet? Decor = null)      // watermark and stickers; null = none
{
    public DecorSet Decorations => Decor ?? DecorSet.Empty;

    public int MaxColors => Quality >= 100 ? 256 : 16 + (int)Math.Round(240 * (Quality - 10) / 90.0);
    public int Tolerance => Quality >= 100 ? 0 : (int)Math.Round((100 - Quality) * 0.3);

    public static bool SwapsAxes(RotateFlipType r) => r is RotateFlipType.Rotate90FlipNone or RotateFlipType.Rotate270FlipNone
        or RotateFlipType.Rotate90FlipX or RotateFlipType.Rotate270FlipX;

    /// <summary>Size after crop and scale, before rotation.</summary>
    public Size ScaledSize(Size source)
    {
        var b = Crop.IsEmpty ? source : Crop.Size;
        return new Size(Math.Max(1, (int)Math.Round(b.Width * ScalePercent / 100.0)), Math.Max(1, (int)Math.Round(b.Height * ScalePercent / 100.0)));
    }

    /// <summary>Final image dimensions.</summary>
    public Size OutputSize(Size source)
    {
        var s = ScaledSize(source);
        return SwapsAxes(Rotate) ? new Size(s.Height, s.Width) : s;
    }
}

static class GifExporter
{
    /// <summary>Shortest frame time GIF viewers honour (2 cs); the editor never goes below it, so previews match exports.</summary>
    public const int MinDelayMs = 20;

    /// <summary>Changed-pixel share below which two frames count as the same picture.</summary>
    const double SimilarRatio = 0.0005;

    /// <summary>Encodes <paramref name="frames"/> as a GIF into <paramref name="output"/>. Thread-safe with respect to <paramref name="store"/>.</summary>
    /// <returns>Number of frames written.</returns>
    public static int Export(Stream output, FrameStore store, IReadOnlyList<EditFrame> frames, ExportOptions o,
        IProgress<float>? progress, CancellationToken ct)
    {
        var outSize = o.OutputSize(store.FrameSize);
        var encoder = new GifEncoder(output, outSize.Width, outSize.Height, o.PlayCount, o.MaxColors, o.Tolerance);

        // Round on the running total so per-frame centisecond rounding never drifts.
        long elapsedMs = 0;
        int writtenCs = 0, written = 0;
        foreach (var (px, delayMs) in Render(store, frames, o, progress, ct))
        {
            elapsedMs += delayMs;
            int delayCs = Math.Max(2, (int)Math.Round(elapsedMs / 10.0) - writtenCs);
            writtenCs += delayCs;
            encoder.AddFrame(px, delayCs);
            written++;
        }
        encoder.Finish();
        return written;
    }

    /// <summary>
    /// Writes each frame as <c>{baseName}_0001.png …</c> into <paramref name="folder"/>, plus <c>{baseName}_timing.csv</c>
    /// with each frame's duration, for game engines and other tools that want frames rather than a GIF.
    /// </summary>
    public static int ExportPngSequence(string folder, string baseName, FrameStore store, IReadOnlyList<EditFrame> frames, ExportOptions o,
        IProgress<float>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var size = o.OutputSize(store.FrameSize);
        var timing = new List<string> { "file,duration_ms" };
        int n = 0;
        foreach (var (px, delayMs) in Render(store, frames, o, progress, ct))
        {
            var name = $"{baseName}_{++n:D4}.png";
            using var bmp = FrameOps.ToBitmap(px, size);
            bmp.Save(Path.Combine(folder, name), ImageFormat.Png);
            timing.Add($"{name},{delayMs}");
        }
        File.WriteAllLines(Path.Combine(folder, $"{baseName}_timing.csv"), timing);
        return n;
    }

    /// <summary>
    /// The shared pipeline: FPS cap, then per frame crop → scale → rotate → caption, then merging of near-identical
    /// neighbours. Yields finished pixels with their durations.
    /// </summary>
    static IEnumerable<(byte[] Pixels, int DelayMs)> Render(FrameStore store, IReadOnlyList<EditFrame> frames, ExportOptions o,
        IProgress<float>? progress, CancellationToken ct)
    {
        var src = store.FrameSize;
        var cropped = o.Crop.IsEmpty ? src : o.Crop.Size;
        var scaled = o.ScaledSize(src);
        var outSize = o.OutputSize(src);

        // 1. FPS cap: fold frames that start too soon after the previous kept one into it (timing preserved).
        var groups = new List<EditFrame>(frames.Count);
        double minInterval = o.MaxFps > 0 ? 1000.0 / o.MaxFps : 0;
        long t = 0, lastStart = long.MinValue;
        foreach (var f in frames)
        {
            // 10% slack so e.g. 33ms frames under a 10 fps cap keep every third frame rather than every fourth.
            if (groups.Count > 0 && t - lastStart < minInterval * 0.9)
                groups[^1] = groups[^1] with { DelayMs = groups[^1].DelayMs + f.DelayMs };
            else
            {
                groups.Add(f);
                lastStart = t;
            }
            t += f.DelayMs;
        }

        // 2. Frames are independent until merging, so worker threads render a few ahead while the caller encodes.
        // The look-ahead is capped by memory as well as cores: enlarged frames can be tens of megabytes each.
        long frameBytes = Math.Max((long)scaled.Width * scaled.Height, (long)cropped.Width * cropped.Height) * 4;
        int ahead = (int)Math.Clamp(256L * 1024 * 1024 / Math.Max(1, frameBytes), 1, Math.Max(2, Environment.ProcessorCount));
        var decor = o.Decorations;
        var sized = new ConcurrentDictionary<(int? Id, Size Size), ImageAsset>();
        var queue = new Queue<Task<byte[]>>();
        int next = 0;

        // 3. Merge in order. One frame is held back so merges can extend its delay.
        byte[]? pending = null;
        int pendingDelay = 0;
        try
        {
            for (int i = 0; i < groups.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                while (next < groups.Count && queue.Count < ahead)
                {
                    var f = groups[next++];
                    queue.Enqueue(Task.Run(() => RenderFrame(f), ct));
                }
                var px = queue.Dequeue().GetAwaiter().GetResult();

                if (pending != null && o.MergeSimilar && NearlyEqual(pending, px, o.Tolerance))
                {
                    pendingDelay += groups[i].DelayMs;
                }
                else
                {
                    if (pending != null) yield return (pending, pendingDelay);
                    pending = px;
                    pendingDelay = groups[i].DelayMs;
                }
                progress?.Report((i + 1f) / groups.Count);
            }
            if (pending != null) yield return (pending, pendingDelay);
        }
        finally
        {
            // Stopped early (cancelled or failed): observe the workers still running so their errors go nowhere.
            foreach (var task in queue) _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }

        byte[] RenderFrame(EditFrame f)
        {
            ct.ThrowIfCancellationRequested();
            var px = store.Read(f.Source);
            if (!o.Crop.IsEmpty) px = FrameOps.Crop(px, src, o.Crop);
            if (scaled != cropped) px = FrameOps.Scale(px, cropped, scaled, o.Resample);
            if (o.Rotate != RotateFlipType.RotateNoneFlipNone) px = FrameOps.RotateFlip(px, scaled, o.Rotate);
            bool caption = !string.IsNullOrWhiteSpace(f.Caption);
            if (caption || decor.Any(f.Stickers)) px = Burn(px, outSize, decor, f.Stickers, caption ? f.Caption : null, sized);
            return px;
        }
    }

    /// <summary>Draws stickers, watermark and caption over finished pixels in one bitmap round trip.</summary>
    static byte[] Burn(byte[] px, Size size, DecorSet decor, int[]? stickers, string? caption,
        ConcurrentDictionary<(int? Id, Size Size), ImageAsset> sized)
    {
        using var bmp = FrameOps.ToBitmap(px, size);
        var area = new RectangleF(0, 0, size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            // Images are shrunk to their drawn size once per export; each frame then draws a small bitmap of its own,
            // since GDI+ bitmaps cannot be shared between threads.
            Decor.Draw(g, area, decor, stickers,
                (id, asset, target) => sized.GetOrAdd((id, target), _ => asset.Resize(target)).CreateBitmap(), disposeBitmaps: true);
            if (caption != null) Caption.Draw(g, area, caption);
        }
        return FrameOps.FromBitmap(bmp);
    }

    static bool NearlyEqual(byte[] a, byte[] b, int tolerance)
    {
        var x = MemoryMarshal.Cast<byte, uint>(a.AsSpan());
        var y = MemoryMarshal.Cast<byte, uint>(b.AsSpan());
        int limit = (int)(x.Length * SimilarRatio);
        int t = Math.Max(tolerance, 2);
        int diff = 0;
        for (int i = 0; i < x.Length; i++)
        {
            uint p = x[i], q = y[i];
            if (p == q) continue;
            if (Math.Abs((int)(p & 0xFF) - (int)(q & 0xFF)) <= t
                && Math.Abs((int)((p >> 8) & 0xFF) - (int)((q >> 8) & 0xFF)) <= t
                && Math.Abs((int)((p >> 16) & 0xFF) - (int)((q >> 16) & 0xFF)) <= t)
                continue;
            if (++diff > limit) return false;
        }
        return true;
    }
}

/// <summary>Caption bar along the bottom of a frame, used identically by the preview and the export.</summary>
static class Caption
{
    /// <summary>Draws the caption into <paramref name="g"/> over an image area <paramref name="area"/>.</summary>
    public static void Draw(Graphics g, RectangleF area, string text)
    {
        float px = Math.Clamp(area.Height * 0.065f, 10f, 64f);
        using var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
        float maxW = area.Width * 0.92f;
        var size = g.MeasureString(text, font, (int)maxW);
        float padX = px * 0.7f, padY = px * 0.35f;
        float w = Math.Min(maxW, size.Width) + padX * 2, h = size.Height + padY * 2;
        var box = new RectangleF(area.X + (area.Width - w) / 2, area.Bottom - h - px * 0.6f, w, h);

        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var path = Theme.RoundRect(box, px * 0.4f))
        using (var bg = new SolidBrush(Color.FromArgb(205, 16, 16, 20)))
            g.FillPath(bg, path);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, Brushes.White, box, sf);
        g.Restore(state);
    }
}

/// <summary>Write-only stream that only counts bytes; used to estimate file size.</summary>
sealed class CountingStream : Stream
{
    long _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _length += count;
    public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    public override void WriteByte(byte value) => _length++;
}

/// <summary>Decodes an existing (animated) GIF into a <see cref="FrameStore"/>.</summary>
static class GifLoader
{
    const int PropertyTagFrameDelay = 0x5100;

    public static (FrameStore Store, List<int> Delays) Load(string path, IProgress<float>? progress, CancellationToken ct)
    {
        using var image = Image.FromFile(path);
        var dim = new FrameDimension(image.FrameDimensionsList[0]);
        int count = Math.Max(1, image.GetFrameCount(dim));

        int[] delaysCs = new int[count];
        try
        {
            var raw = image.GetPropertyItem(PropertyTagFrameDelay)?.Value;
            if (raw != null)
                for (int i = 0; i < count && i * 4 + 3 < raw.Length; i++)
                    delaysCs[i] = BitConverter.ToInt32(raw, i * 4);
        }
        catch (ArgumentException)
        {
            // Still image without a delay table.
        }

        var store = new FrameStore(image.Size);
        var delays = new List<int>(count);
        try
        {
            using var canvas = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(canvas);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (count > 1) image.SelectActiveFrame(dim, i);
                // GDI+ hands back each frame already composited; transparent pixels are flattened onto white.
                g.Clear(Color.White);
                g.DrawImage(image, 0, 0, image.Width, image.Height);
                store.Append(FrameOps.FromBitmap(canvas));
                // Browsers show delays under 2cs as 10cs; match that so playback speed is what people expect.
                delays.Add(delaysCs[i] < 2 ? 100 : delaysCs[i] * 10);
                progress?.Report((i + 1f) / count);
            }
        }
        catch
        {
            store.Dispose();
            throw;
        }
        return (store, delays);
    }
}

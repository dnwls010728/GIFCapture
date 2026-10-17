using System.IO.Compression;

namespace GifCapture;

/// <summary>Append-only, zlib-compressed BGRA frames in a temp file that deletes itself on dispose.</summary>
sealed class FrameStore : IDisposable
{
    readonly FileStream _file;
    readonly List<(long Offset, int Length)> _index = [];
    readonly object _lock = new();

    public Size FrameSize { get; }
    public int FrameBytes => FrameSize.Width * FrameSize.Height * 4;

    public int Count
    {
        get { lock (_lock) return _index.Count; }
    }

    public FrameStore(Size frameSize)
    {
        FrameSize = frameSize;
        var path = Path.Combine(Path.GetTempPath(), $"GifCapture_{Guid.NewGuid():N}.frames");
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
    }

    /// <returns>Index of the stored frame.</returns>
    public int Append(byte[] bgra)
    {
        using var ms = new MemoryStream(bgra.Length / 4);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(bgra);

        lock (_lock)
        {
            long offset = _file.Seek(0, SeekOrigin.End);
            _file.Write(ms.GetBuffer(), 0, (int)ms.Length);
            _index.Add((offset, (int)ms.Length));
            return _index.Count - 1;
        }
    }

    /// <summary>Safe to call from any thread.</summary>
    public byte[] Read(int index)
    {
        byte[] compressed;
        lock (_lock)
        {
            var (offset, length) = _index[index];
            compressed = new byte[length];
            _file.Seek(offset, SeekOrigin.Begin);
            _file.ReadExactly(compressed);
        }

        var pixels = new byte[FrameBytes];
        using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        z.ReadExactly(pixels);
        return pixels;
    }

    public void Dispose() => _file.Dispose();
}

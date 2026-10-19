using System.Runtime.InteropServices;

namespace GifCapture;

/// <summary>
/// Streaming animated GIF writer. Each frame gets its own palette; after the first frame only the
/// bounding box of changed pixels is written, with unchanged pixels left transparent.
/// </summary>
sealed class GifEncoder
{
    readonly Stream _s;
    readonly int _w, _h;
    readonly int _maxColors, _tolerance;
    readonly Quantizer _quantizer = new();
    readonly LzwEncoder _lzw = new();
    readonly byte[] _indices;
    readonly byte[] _sub;
    readonly bool[] _mask;
    // What the viewer currently shows at each pixel (in source colors). Comparing against this rather than the
    // previous source frame keeps lossy "unchanged" decisions from drifting more than the tolerance.
    byte[]? _shown;

    /// <param name="maxColors">Palette size per frame (16–256); fewer colors give smaller files.</param>
    /// <param name="tolerance">Per-channel difference treated as "unchanged" (0 = exact). Higher values shrink files of noisy content.</param>
    /// <param name="playCount">How many times viewers play the animation: 0 = forever, 1 = once, N = N times.</param>
    public GifEncoder(Stream s, int width, int height, int playCount = 0, int maxColors = 256, int tolerance = 0)
    {
        _s = s;
        _w = width;
        _h = height;
        _maxColors = Math.Clamp(maxColors, 2, 256);
        _tolerance = Math.Max(0, tolerance);
        _indices = new byte[width * height];
        _sub = new byte[width * height * 4];
        _mask = new bool[width * height];

        WriteAscii("GIF89a");
        WriteShort(width);
        WriteShort(height);
        _s.WriteByte(0x00); // no global color table
        _s.WriteByte(0x00); // background index
        _s.WriteByte(0x00); // pixel aspect ratio

        if (playCount != 1)
        {
            // NETSCAPE2.0 extension: repeat count after the first play (0 = forever)
            _s.Write([0x21, 0xFF, 0x0B]);
            WriteAscii("NETSCAPE2.0");
            int repeats = playCount <= 0 ? 0 : Math.Min(playCount - 1, 65535);
            _s.Write([0x03, 0x01, (byte)repeats, (byte)(repeats >> 8), 0x00]);
        }
    }

    /// <param name="bgra">32bpp pixels, top-down, no row padding.</param>
    /// <param name="delayCs">Frame duration in hundredths of a second.</param>
    public void AddFrame(byte[] bgra, int delayCs)
    {
        if (_shown == null)
        {
            var palette = _quantizer.Quantize(bgra, null, _w * _h, _indices, _maxColors);
            WriteFrame(new Rectangle(0, 0, _w, _h), palette, _quantizer.ColorCount, delayCs, transparentIndex: -1);
            _shown = (byte[])bgra.Clone();
            return;
        }

        var box = ChangedBounds(bgra, _shown);
        if (box.IsEmpty) box = new Rectangle(0, 0, 1, 1); // nothing changed: 1px transparent frame just carries the delay

        int n = 0;
        var cur = MemoryMarshal.Cast<byte, uint>(bgra);
        var shown = MemoryMarshal.Cast<byte, uint>(_shown.AsSpan());
        var sub = MemoryMarshal.Cast<byte, uint>(_sub.AsSpan());
        for (int y = box.Top; y < box.Bottom; y++)
        {
            for (int x = box.Left; x < box.Right; x++, n++)
            {
                int i = y * _w + x;
                sub[n] = cur[i];
                bool changed = Differs(cur[i], shown[i]);
                _mask[n] = changed;
                if (changed) shown[i] = cur[i];
            }
        }

        var pal = _quantizer.Quantize(_sub, _mask, n, _indices, Math.Min(_maxColors, 255));

        // The transparent entry takes the last slot of the power-of-two table, which no palette color occupies
        int entries = _quantizer.ColorCount + 1;
        int transparent = (1 << TableBits(entries)) - 1;
        for (int i = 0; i < n; i++)
            if (!_mask[i]) _indices[i] = (byte)transparent;

        WriteFrame(box, pal, entries, delayCs, transparent);
    }

    bool Differs(uint a, uint b)
    {
        if (a == b) return false;
        if (_tolerance == 0) return true;
        int t = _tolerance;
        return Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) > t
            || Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) > t
            || Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) > t;
    }

    Rectangle ChangedBounds(byte[] cur, byte[] shown)
    {
        var c = MemoryMarshal.Cast<byte, uint>(cur.AsSpan());
        var p = MemoryMarshal.Cast<byte, uint>(shown.AsSpan());
        int minX = _w, maxX = -1, minY = -1, maxY = -1;
        for (int y = 0; y < _h; y++)
        {
            var cr = c.Slice(y * _w, _w);
            var pr = p.Slice(y * _w, _w);
            if (cr.SequenceEqual(pr)) continue;
            int l = 0;
            while (l < _w && !Differs(cr[l], pr[l])) l++;
            if (l == _w) continue; // only sub-tolerance differences in this row
            int r = _w - 1;
            while (!Differs(cr[r], pr[r])) r--;
            if (minY < 0) minY = y;
            maxY = y;
            if (l < minX) minX = l;
            if (r > maxX) maxX = r;
        }
        return minY < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>Smallest color table exponent (table size = 1 &lt;&lt; result) holding <paramref name="entries"/> colors; never below 2 entries.</summary>
    static int TableBits(int entries) => Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(entries - 1)));

    /// <param name="transparentIndex">Palette slot used for unchanged pixels, or -1 for an opaque frame.</param>
    void WriteFrame(Rectangle box, byte[] palette, int entries, int delayCs, int transparentIndex)
    {
        bool transparent = transparentIndex >= 0;

        // Graphic control extension; disposal 1 = keep previous frame underneath
        _s.Write([0x21, 0xF9, 0x04, (byte)(0x04 | (transparent ? 0x01 : 0x00))]);
        WriteShort(Math.Clamp(delayCs, 2, 65535));
        _s.WriteByte(transparent ? (byte)transparentIndex : (byte)0);
        _s.WriteByte(0x00);

        // Image descriptor with a local color table sized to the entries actually used
        int bits = TableBits(entries);
        _s.WriteByte(0x2C);
        WriteShort(box.X);
        WriteShort(box.Y);
        WriteShort(box.Width);
        WriteShort(box.Height);
        _s.WriteByte((byte)(0x80 | (bits - 1)));
        _s.Write(palette, 0, 3 << bits);

        _lzw.Encode(_s, _indices.AsSpan(0, box.Width * box.Height), Math.Max(2, bits));
    }

    public void Finish()
    {
        _s.WriteByte(0x3B);
        _s.Flush();
    }

    void WriteShort(int v)
    {
        _s.WriteByte((byte)v);
        _s.WriteByte((byte)(v >> 8));
    }

    void WriteAscii(string text)
    {
        foreach (var c in text) _s.WriteByte((byte)c);
    }
}

/// <summary>Median-cut quantizer over a 6-bit-per-channel histogram; palette entries are true color averages.</summary>
sealed class Quantizer
{
    const int Bits = 6;
    const int Size = 1 << (Bits * 3);

    // Per-histogram-cell accumulator; only cells listed in _keys are non-zero between calls.
    struct Bin
    {
        public int Count;
        public long R, G, B;
    }

    readonly Bin[] _bins = new Bin[Size];
    readonly int[] _keys = new int[Size];
    readonly byte[] _map = new byte[Size];
    readonly byte[] _palette = new byte[256 * 3];

    struct Box
    {
        public int Start, End, Axis;
        public long Score;
    }

    /// <summary>Number of palette entries produced by the last <see cref="Quantize"/> call.</summary>
    public int ColorCount { get; private set; }

    static int Key(byte r, byte g, byte b) => ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);

    static int Channel(int key, int axis) => (key >> (12 - axis * 6)) & 63;

    // Moves the given channel into the top bits so a plain integer sort orders by it (ties fall back to the other channels).
    static int ToSortKey(int key, int axis) =>
        (Channel(key, axis) << 12) | (Channel(key, axis == 0 ? 1 : 0) << 6) | Channel(key, axis == 2 ? 1 : 2);

    static int FromSortKey(int sortKey, int axis)
    {
        int hi = sortKey >> 12, mid = (sortKey >> 6) & 63, lo = sortKey & 63;
        return axis switch
        {
            0 => sortKey,
            1 => (mid << 12) | (hi << 6) | lo,
            _ => (mid << 12) | (lo << 6) | hi,
        };
    }

    /// <param name="mask">When given, only pixels whose mask entry is true shape the palette.</param>
    /// <returns>The RGB palette (<see cref="ColorCount"/> entries used); <paramref name="indices"/> receives one palette index per pixel.</returns>
    public byte[] Quantize(byte[] bgra, bool[]? mask, int pixelCount, byte[] indices, int maxColors)
    {
        int n = 0;
        for (int p = 0, i = 0; p < pixelCount; p++, i += 4)
        {
            if (mask != null && !mask[p]) continue;
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            int k = Key(r, g, b);
            ref var bin = ref _bins[k];
            if (bin.Count++ == 0) _keys[n++] = k;
            bin.R += r;
            bin.G += g;
            bin.B += b;
        }

        var boxes = new List<Box>(maxColors) { MakeBox(0, n) };
        while (boxes.Count < maxColors)
        {
            int best = -1;
            for (int i = 0; i < boxes.Count; i++)
                if (boxes[i].Score > 0 && (best < 0 || boxes[i].Score > boxes[best].Score))
                    best = i;
            if (best < 0) break;

            var box = boxes[best];
            var run = _keys.AsSpan(box.Start, box.End - box.Start);
            for (int i = 0; i < run.Length; i++) run[i] = ToSortKey(run[i], box.Axis);
            run.Sort();
            for (int i = 0; i < run.Length; i++) run[i] = FromSortKey(run[i], box.Axis);

            long pop = 0;
            for (int i = box.Start; i < box.End; i++) pop += _bins[_keys[i]].Count;
            long cum = 0;
            int mid = box.End - 1;
            for (int i = box.Start; i < box.End; i++)
            {
                cum += _bins[_keys[i]].Count;
                if (cum * 2 >= pop) { mid = i + 1; break; }
            }
            mid = Math.Clamp(mid, box.Start + 1, box.End - 1);

            boxes[best] = MakeBox(box.Start, mid);
            boxes.Add(MakeBox(mid, box.End));
        }

        Array.Clear(_palette);
        for (int bi = 0; bi < boxes.Count; bi++)
        {
            long c = 0, r = 0, g = 0, b = 0;
            for (int i = boxes[bi].Start; i < boxes[bi].End; i++)
            {
                int k = _keys[i];
                ref var bin = ref _bins[k];
                c += bin.Count;
                r += bin.R;
                g += bin.G;
                b += bin.B;
                _map[k] = (byte)bi;
            }
            if (c == 0) continue;
            _palette[bi * 3] = (byte)(r / c);
            _palette[bi * 3 + 1] = (byte)(g / c);
            _palette[bi * 3 + 2] = (byte)(b / c);
        }

        for (int i = 0, p = 0; p < pixelCount; i += 4, p++)
            indices[p] = mask == null || mask[p] ? _map[Key(bgra[i + 2], bgra[i + 1], bgra[i])] : (byte)0;

        for (int i = 0; i < n; i++) _bins[_keys[i]] = default; // reset only the touched cells
        ColorCount = boxes.Count;
        return _palette;
    }

    Box MakeBox(int start, int end)
    {
        var box = new Box { Start = start, End = end };
        if (end - start < 2) return box;

        Span<int> min = [63, 63, 63];
        Span<int> max = [0, 0, 0];
        long pop = 0;
        for (int i = start; i < end; i++)
        {
            int k = _keys[i];
            pop += _bins[k].Count;
            for (int a = 0; a < 3; a++)
            {
                int v = Channel(k, a);
                if (v < min[a]) min[a] = v;
                if (v > max[a]) max[a] = v;
            }
        }

        int range = -1;
        for (int a = 0; a < 3; a++)
        {
            if (max[a] - min[a] > range)
            {
                range = max[a] - min[a];
                box.Axis = a;
            }
        }
        box.Score = pop * range;
        return box;
    }
}

/// <summary>GIF-flavored variable-width LZW.</summary>
sealed class LzwEncoder
{
    const int MaxCodes = 4096;
    const int TableBits = 13; // 8192 slots keep the load factor at or below 0.5 with 4096 codes
    const int TableMask = (1 << TableBits) - 1;

    // Open-addressing dictionary. Entry = generation << 32 | (prefix << 8 | byte) << 12 | code; stale generations read as empty.
    readonly ulong[] _table = new ulong[1 << TableBits];
    uint _generation;

    readonly byte[] _block = new byte[255];
    int _blockLen;
    int _bitBuf, _bitCount, _codeSize, _minCodeSize;
    Stream _s = Stream.Null;

    public void Encode(Stream s, ReadOnlySpan<byte> pixels, int minCodeSize)
    {
        _s = s;
        _blockLen = _bitBuf = _bitCount = 0;
        _minCodeSize = minCodeSize;
        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        s.WriteByte((byte)minCodeSize);

        int next = Reset();
        Emit(clearCode);

        int prefix = pixels[0];
        for (int i = 1; i < pixels.Length; i++)
        {
            int px = pixels[i];
            uint key = (uint)((prefix << 8) | px);
            ulong stamp = (ulong)_generation << 32;
            int slot = (int)((key * 2654435761u) >> (32 - TableBits));
            bool found = false;
            while (true)
            {
                ulong e = _table[slot];
                if ((e & 0xFFFFFFFF00000000UL) != stamp) break;
                if ((uint)e >> 12 == key)
                {
                    prefix = (int)(e & 0xFFF);
                    found = true;
                    break;
                }
                slot = (slot + 1) & TableMask;
            }
            if (found) continue;

            Emit(prefix);
            if (next < MaxCodes)
            {
                _table[slot] = stamp | ((ulong)key << 12) | (uint)next;
                next++;
                if (next > (1 << _codeSize) && _codeSize < 12) _codeSize++;
            }
            else
            {
                Emit(clearCode);
                next = Reset();
            }
            prefix = px;
        }

        Emit(prefix);
        Emit(endCode);
        if (_bitCount > 0) PutByte((byte)_bitBuf);
        FlushBlock();
        s.WriteByte(0); // block terminator
    }

    int Reset()
    {
        _generation++;
        _codeSize = _minCodeSize + 1;
        return (1 << _minCodeSize) + 2;
    }

    void Emit(int code)
    {
        _bitBuf |= code << _bitCount;
        _bitCount += _codeSize;
        while (_bitCount >= 8)
        {
            PutByte((byte)_bitBuf);
            _bitBuf >>= 8;
            _bitCount -= 8;
        }
    }

    void PutByte(byte b)
    {
        _block[_blockLen++] = b;
        if (_blockLen == 255) FlushBlock();
    }

    void FlushBlock()
    {
        if (_blockLen == 0) return;
        _s.WriteByte((byte)_blockLen);
        _s.Write(_block, 0, _blockLen);
        _blockLen = 0;
    }
}

using System.Drawing.Drawing2D;

namespace GifCapture;

/// <summary>Shows the current frame, cropped to <see cref="Crop"/>; in crop mode shows the full frame with an editable crop box.</summary>
sealed class EditorCanvas : Control
{
    enum Zone { None, New, Move, N, S, E, W, NE, NW, SE, SW }

    Bitmap? _frame;
    Rectangle _crop, _editCrop;
    bool _cropMode;
    RectangleF _dest;
    float _scale = 1;
    Zone _drag;
    Point _dragStartImg;
    Rectangle _dragOrigin;
    readonly Font _labelFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    readonly Font _toastFont = new("Segoe UI", 10f);
    readonly System.Windows.Forms.Timer _toastTimer = new();
    string? _toast;
    bool _toastError;
    Rectangle _toastRect;

    public event EventHandler? EditCropChanged;

    public EditorCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = false;
        BackColor = Color.FromArgb(0x0E, 0x0F, 0x12);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast = null;
            Invalidate(Rectangle.Inflate(_toastRect, 4, 4));
        };
    }

    /// <summary>Shows a pill message at the bottom of the preview for <paramref name="durationMs"/>.</summary>
    public void ShowToast(string text, bool error = false, int durationMs = 2600)
    {
        Invalidate(Rectangle.Inflate(_toastRect, 4, 4));
        _toast = text;
        _toastError = error;
        _toastTimer.Stop();
        _toastTimer.Interval = durationMs;
        _toastTimer.Start();
        Invalidate();
    }

    void DrawToast(Graphics g, float s)
    {
        if (_toast == null) return;
        var size = TextRenderer.MeasureText(_toast, _toastFont);
        int w = size.Width + (int)(40 * s), h = size.Height + (int)(18 * s);
        _toastRect = new Rectangle((Width - w) / 2, Height - h - (int)(20 * s), w, h);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundRect(_toastRect, h / 2f))
        {
            using var bg = new SolidBrush(Color.FromArgb(245, Theme.Popup));
            g.FillPath(bg, path);
            using var pen = new Pen(_toastError ? Theme.Rec : Theme.Border, _toastError ? 1.5f * s : 1f);
            g.DrawPath(pen, path);
        }
        var dot = new RectangleF(_toastRect.X + 16 * s, _toastRect.Y + (h - 8 * s) / 2, 8 * s, 8 * s);
        using (var b = new SolidBrush(_toastError ? Theme.Rec : Theme.Success)) g.FillEllipse(b, dot);
        TextRenderer.DrawText(g, _toast, _toastFont, new Rectangle(_toastRect.X + (int)(30 * s), _toastRect.Y, w - (int)(36 * s), h),
            Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>Full-size current frame. Owned by the caller, who may update its pixels and call Invalidate.</summary>
    public Bitmap? Frame
    {
        get => _frame;
        set { _frame = value; Invalidate(); }
    }

    /// <summary>Committed crop in frame pixels; empty means the whole frame.</summary>
    public Rectangle Crop
    {
        get => _crop;
        set { _crop = value; _rotatedVersion = -1; Invalidate(); }
    }

    public bool CropMode
    {
        get => _cropMode;
        set
        {
            _cropMode = value;
            _editCrop = _crop;
            Cursor = value ? Cursors.Cross : Cursors.Default;
            Invalidate();
        }
    }

    /// <summary>Crop box being edited in crop mode, in frame pixels.</summary>
    public Rectangle EditCrop => _editCrop;

    public bool Playing { get; set; }

    // ───────────── Rotation preview ─────────────

    RotateFlipType _rotate;
    Bitmap? _rotated;
    int _version, _rotatedVersion = -1;

    /// <summary>Rotation/flip shown outside crop mode (crop mode always shows the unrotated source).</summary>
    public RotateFlipType Rotate
    {
        get => _rotate;
        set { _rotate = value; _rotatedVersion = -1; Invalidate(); }
    }

    /// <summary>Call after changing the pixels of <see cref="Frame"/>.</summary>
    public void FrameUpdated()
    {
        _version++;
        Invalidate();
    }

    /// <summary>Cropped and rotated copy of the frame, rebuilt only when something it depends on changed.</summary>
    Bitmap RotatedView()
    {
        if (_rotated != null && _rotatedVersion == _version) return _rotated;
        _rotated?.Dispose();
        var src = _crop.IsEmpty ? FrameRect : _crop;
        _rotated = _frame!.Clone(src, _frame.PixelFormat);
        _rotated.RotateFlip(_rotate);
        _rotatedVersion = _version;
        return _rotated;
    }

    // ───────────── Output scale preview ─────────────

    float _outputScale = 1;
    ResampleMode _resample;
    Bitmap? _piece;
    (int Version, RotateFlipType Rotate, Rectangle Src, Rectangle Piece, float Scale, ResampleMode Mode) _pieceKey;

    /// <summary>Export scale (1 = 100%). Zoom is measured against the scaled output, so 100% shows real output pixels.</summary>
    public float OutputScale
    {
        get => _outputScale;
        set { if (_outputScale == value) return; _outputScale = value; Invalidate(); }
    }

    public ResampleMode Resample
    {
        get => _resample;
        set { if (_resample == value) return; _resample = value; Invalidate(); }
    }

    /// <summary>
    /// Draws the visible part of the output exactly as the export renders it. Only the on-screen region is scaled, so
    /// memory stays bounded by the window size however large the output is. Resampling commutes with rotation and flips,
    /// so working on the already rotated view matches the export.
    /// </summary>
    void DrawOutputPiece(Graphics g, Bitmap image, Rectangle src, float k)
    {
        var visible = RectangleF.Intersect(_dest, ClientRectangle);
        if (visible.IsEmpty) return;
        float d = _scale * k; // screen pixels per source pixel
        // A margin gives the filters real neighbours at the piece edges: Lanczos reaches 3 source pixels, the sharpening
        // blur about 2 more, xBR 2.
        const int margin = 6;
        int x0 = Math.Max(0, (int)Math.Floor((visible.Left - _dest.X) / d) - margin);
        int y0 = Math.Max(0, (int)Math.Floor((visible.Top - _dest.Y) / d) - margin);
        int x1 = Math.Min(src.Width, (int)Math.Ceiling((visible.Right - _dest.X) / d) + margin);
        int y1 = Math.Min(src.Height, (int)Math.Ceiling((visible.Bottom - _dest.Y) / d) + margin);
        if (x1 <= x0 || y1 <= y0) return;
        var piece = new Rectangle(src.X + x0, src.Y + y0, x1 - x0, y1 - y0);

        var key = (_version, _cropMode ? RotateFlipType.RotateNoneFlipNone : _rotate, src, piece, k, _resample);
        if (_piece == null || _pieceKey != key)
        {
            var target = new Size(Math.Max(1, (int)Math.Round(piece.Width * k)), Math.Max(1, (int)Math.Round(piece.Height * k)));
            byte[] px;
            using (var part = image.Clone(piece, image.PixelFormat)) px = FrameOps.FromBitmap(part);
            _piece?.Dispose();
            _piece = FrameOps.ToBitmap(FrameOps.Scale(px, piece.Size, target, _resample), target);
            _pieceKey = key;
        }
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_piece, new RectangleF(_dest.X + x0 * d, _dest.Y + y0 * d, piece.Width * d, piece.Height * d));
    }

    // ───────────── Zoom & pan ─────────────

    float _zoom; // 0 = fit
    PointF _pan;
    bool _panning;
    Point _panStart;
    PointF _panOrigin;

    public event EventHandler? ZoomChanged;

    /// <summary>Current display scale in percent.</summary>
    public int ZoomPercent => (int)Math.Round((_zoom > 0 ? _zoom : _scale) * 100);
    public bool IsFit => _zoom == 0;

    public void ZoomToFit()
    {
        _zoom = 0;
        _pan = PointF.Empty;
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomTo(float scale, Point? anchor = null)
    {
        scale = Math.Clamp(scale, 0.1f, 16f);
        var a = anchor ?? new Point(Width / 2, Height / 2);
        // Keep the image point under the anchor fixed while zooming.
        float ix = (a.X - _dest.X) / _scale, iy = (a.Y - _dest.Y) / _scale;
        _zoom = scale;
        var size = ViewSize();
        float w = size.Width * scale, h = size.Height * scale;
        float cx = (Width - w) / 2, cy = (Height - h) / 2;
        _pan = new PointF(a.X - ix * scale - cx, a.Y - iy * scale - cy);
        Invalidate();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    SizeF ViewSize()
    {
        if (_frame == null) return SizeF.Empty;
        var src = _cropMode || _crop.IsEmpty ? FrameRect : _crop;
        bool swap = !_cropMode && ExportOptions.SwapsAxes(_rotate);
        float k = _cropMode ? 1 : _outputScale;
        return swap ? new SizeF(src.Height * k, src.Width * k) : new SizeF(src.Width * k, src.Height * k);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_frame == null) return;
        float factor = e.Delta > 0 ? 1.25f : 0.8f;
        ZoomTo(_scale * factor, e.Location);
    }

    float S => DeviceDpi / 96f;

    Rectangle FrameRect => _frame == null ? Rectangle.Empty : new Rectangle(0, 0, _frame.Width, _frame.Height);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_frame == null) return;

        float s = S;
        bool rotated = !_cropMode && _rotate != RotateFlipType.RotateNoneFlipNone;
        Image image = rotated ? RotatedView() : _frame;
        var src = rotated ? new Rectangle(0, 0, image.Width, image.Height) : _cropMode || _crop.IsEmpty ? FrameRect : _crop;

        // _scale is screen pixels per output pixel; outside crop mode the output is the source times OutputScale.
        float k = _cropMode ? 1 : _outputScale;
        var area = RectangleF.Inflate(ClientRectangle, -20 * s, -20 * s);
        float fit = Math.Min(area.Width / (src.Width * k), area.Height / (src.Height * k));
        _scale = _zoom > 0 ? _zoom : fit;
        float w = src.Width * k * _scale, h = src.Height * k * _scale;
        var offset = _zoom > 0 ? _pan : PointF.Empty;
        _dest = new RectangleF((Width - w) / 2 + offset.X, (Height - h) / 2 + offset.Y, w, h);

        // Playback goes through the same path as a paused frame, so the picture doesn't change when it starts moving.
        // The piece covers at most the window, which keeps every resampler fast enough for each frame.
        if (k != 1 && _scale >= 1)
            DrawOutputPiece(g, (Bitmap)image, src, k);
        else
        {
            // Output pixels are smaller than screen pixels here: drawing the source directly looks the same.
            bool smooth = _scale * k < 1 || (k > 1 && _resample is ResampleMode.Smooth or ResampleMode.Crisp);
            // Bilinear is the cheap stand-in for bicubic while playing; blocks stay blocks either way.
            g.InterpolationMode = !smooth ? InterpolationMode.NearestNeighbor : Playing ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(image, _dest, src, GraphicsUnit.Pixel);
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;

        // Thin outline separates the frame from the dark backdrop, which matters for dark content.
        using (var outline = new Pen(Color.FromArgb(0x30, 0x33, 0x3C)))
            g.DrawRectangle(outline, _dest.X - 1, _dest.Y - 1, _dest.Width + 1, _dest.Height + 1);

        if (!_cropMode)
        {
            GifCapture.Decor.Draw(g, _dest, _liveDecor ?? _decor, _frameStickers, PreviewBitmap, disposeBitmaps: false,
                () => ViewPixels((Bitmap)image, src));
            DrawDecorSelection(g, s);
        }
        if (_cropMode) DrawCropOverlay(g, s);
        DrawToast(g, s);
    }

    // ───────────── Watermark & stickers ─────────────

    enum DecorDrag { None, Move, Resize, Rotate }

    DecorSet _decor = DecorSet.Empty;
    DecorSet? _liveDecor; // the set as it looks mid-drag
    int[]? _frameStickers;
    int? _selectedSticker;
    bool _watermarkSelected;
    DecorDrag _decorDrag;
    Point _decorStart;
    RectangleF _decorStartRect;
    Sticker? _decorStartSticker;
    readonly Dictionary<int, (ImageAsset Asset, Size Size, Bitmap Bitmap)> _bitmaps = [];

    /// <summary>A sticker, the watermark, or nothing was clicked: (sticker id, watermark).</summary>
    public event Action<int?, bool>? DecorSelected;
    /// <summary>A sticker was moved, resized or turned in the preview.</summary>
    public event Action<Sticker>? StickerEdited;
    /// <summary>A sticker was double-clicked, e.g. to edit its text.</summary>
    public event Action<int>? StickerActivated;
    /// <summary>The watermark was moved or resized in the preview.</summary>
    public event Action<Watermark>? WatermarkEdited;

    public DecorSet Decor
    {
        get => _decor;
        set { _decor = value; Invalidate(); }
    }

    /// <summary>Stickers on the frame being shown.</summary>
    public int[]? FrameStickers
    {
        get => _frameStickers;
        set { if (_frameStickers == value) return; _frameStickers = value; Invalidate(); }
    }

    public int? SelectedSticker
    {
        get => _selectedSticker;
        set { _selectedSticker = value; if (value != null) _watermarkSelected = false; Invalidate(); }
    }

    public bool WatermarkSelected
    {
        get => _watermarkSelected;
        set { _watermarkSelected = value; if (value) _selectedSticker = null; Invalidate(); }
    }

    /// <summary>
    /// Bitmap of an image at its drawn size, kept while neither changes. Never larger than the image itself: beyond
    /// that, letting DrawImage stretch it looks the same and keeps memory bounded when zoomed in.
    /// </summary>
    Bitmap PreviewBitmap(int? id, ImageAsset asset, Size size)
    {
        if (size.Width > asset.Size.Width || size.Height > asset.Size.Height) size = asset.Size;
        int key = id ?? -1;
        if (_bitmaps.TryGetValue(key, out var c) && c.Asset == asset && c.Size == size) return c.Bitmap;
        c.Bitmap?.Dispose();
        var bmp = asset.Resize(size).CreateBitmap();
        _bitmaps[key] = (asset, size, bmp);
        return bmp;
    }

    // Frame pixels for mosaics, read once per shown frame.
    byte[]? _pixels;
    Bitmap? _pixelsImage;
    int _pixelsVersion = -1;

    PixelSource ViewPixels(Bitmap image, Rectangle src)
    {
        if (_pixels == null || _pixelsImage != image || _pixelsVersion != _version)
        {
            _pixels = FrameOps.FromBitmap(image);
            _pixelsImage = image;
            _pixelsVersion = _version;
        }
        return new PixelSource(_pixels, image.Width, src);
    }

    RectangleF? StickerScreenRect(DecorSet d, int id) =>
        d.Stickers.TryGetValue(id, out var s) ? GifCapture.Decor.StickerRect(s, d, _dest) : null;

    RectangleF? WatermarkScreenRect(DecorSet d) => d.HasWatermark ? GifCapture.Decor.WatermarkRect(d.Watermark, d.WatermarkImage, _dest) : null;

    RectangleF? SelectedRect(DecorSet d) =>
        _watermarkSelected ? WatermarkScreenRect(d) : _selectedSticker is { } id && _frameStickers?.Contains(id) == true ? StickerScreenRect(d, id) : null;

    /// <summary>Outline of the selection as drawn, turned with it: top-left, top-right, bottom-right, bottom-left.</summary>
    PointF[]? SelectedCorners(DecorSet d)
    {
        if (SelectedRect(d) is not { } r) return null;
        if (_watermarkSelected || !d.Stickers.TryGetValue(_selectedSticker!.Value, out var s))
            return [r.Location, new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];
        return GifCapture.Decor.StickerCorners(s, d, _dest);
    }

    static PointF Mid(PointF a, PointF b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Rotation knob: out from the middle of the top edge, away from the centre. Stickers only.</summary>
    PointF? RotateHandle(PointF[] corners)
    {
        if (_watermarkSelected) return null;
        var top = Mid(corners[0], corners[1]);
        var center = Mid(corners[0], corners[2]);
        float len = Math.Max(1, Distance(top, center)), reach = 26 * S;
        return new PointF(top.X + (top.X - center.X) / len * reach, top.Y + (top.Y - center.Y) / len * reach);
    }

    void DrawDecorSelection(Graphics g, float s)
    {
        if (SelectedCorners(_liveDecor ?? _decor) is not { } c) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var dark = new Pen(Color.FromArgb(160, 0, 0, 0), 3 * s)) g.DrawPolygon(dark, c);
        using (var pen = new Pen(Theme.AccentHover, 1.5f * s) { DashStyle = DashStyle.Dash }) g.DrawPolygon(pen, c);

        float k = 10 * s;
        using var outline = new Pen(Theme.AccentFill, 1.5f * s);
        if (RotateHandle(c) is { } knob)
        {
            using (var stem = new Pen(Theme.AccentHover, 1.5f * s)) g.DrawLine(stem, Mid(c[0], c[1]), knob);
            var dot = new RectangleF(knob.X - k * 0.6f, knob.Y - k * 0.6f, k * 1.2f, k * 1.2f);
            g.FillEllipse(Brushes.White, dot);
            g.DrawEllipse(outline, dot);
        }
        var h = new RectangleF(c[2].X - k / 2, c[2].Y - k / 2, k, k);
        g.FillRectangle(Brushes.White, h);
        g.DrawRectangle(outline, h.X, h.Y, h.Width, h.Height);
    }

    /// <summary>What sits under <paramref name="p"/>: the selection's handles first, then the topmost item.</summary>
    (int? Sticker, bool Watermark, DecorDrag Part) HitDecor(Point p)
    {
        if (_cropMode || _frame == null) return (null, false, DecorDrag.None);
        float reach = 8 * S;
        if (SelectedCorners(_decor) is { } c)
        {
            if (RotateHandle(c) is { } knob && Distance(p, knob) <= reach) return (_selectedSticker, false, DecorDrag.Rotate);
            if (Distance(p, c[2]) <= reach) return (_selectedSticker, _watermarkSelected, DecorDrag.Resize);
        }
        if (WatermarkScreenRect(_decor) is { } w && w.Contains(p)) return (null, true, DecorDrag.Move);
        // Topmost first, as drawn: mosaics lie under everything else.
        if (_frameStickers != null)
            foreach (bool mosaics in new[] { false, true })
                for (int i = _frameStickers.Length - 1; i >= 0; i--)
                    if (_decor.Stickers.TryGetValue(_frameStickers[i], out var s) && (s.Kind == StickerKind.Mosaic) == mosaics
                        && GifCapture.Decor.StickerContains(s, _decor, _dest, p))
                        return (_frameStickers[i], false, DecorDrag.Move);
        return (null, false, DecorDrag.None);
    }

    static Cursor DecorCursor(DecorDrag part) => part switch
    {
        DecorDrag.Move => Cursors.SizeAll,
        DecorDrag.Resize => Cursors.SizeNWSE,
        DecorDrag.Rotate => Cursors.Hand,
        _ => Cursors.Default,
    };

    bool BeginDecorDrag(MouseEventArgs e)
    {
        var (sticker, watermark, part) = HitDecor(e.Location);
        if (sticker != _selectedSticker || watermark != _watermarkSelected)
        {
            _selectedSticker = sticker;
            _watermarkSelected = watermark;
            Invalidate();
            DecorSelected?.Invoke(sticker, watermark);
        }
        if (part == DecorDrag.None) return false;
        _decorDrag = part;
        _decorStart = e.Location;
        _decorStartRect = SelectedRect(_decor)!.Value;
        _decorStartSticker = sticker is { } id ? _decor.Stickers[id] : null;
        return true;
    }

    void ContinueDecorDrag(Point p)
    {
        float dx = p.X - _decorStart.X, dy = p.Y - _decorStart.Y;
        var r = _decorStartRect;
        if (_watermarkSelected)
        {
            if (_decorDrag == DecorDrag.Move) r.Offset(dx, dy);
            else
            {
                // Corner handle: keep the top-left fixed and the aspect ratio, follow whichever axis moved further.
                float k = Math.Max((r.Width + dx) / r.Width, (r.Height + dy) / r.Height);
                k = Math.Max(k, 8 * S / Math.Min(r.Width, r.Height));
                r = new RectangleF(r.X, r.Y, r.Width * k, r.Height * k);
            }
            var w = _decor.Watermark;
            if (_decorDrag == DecorDrag.Resize) w = w with { SizePercent = GifCapture.Decor.SizeFor(w, _decor.WatermarkImage, _dest, r.Width) };
            var (ox, oy) = GifCapture.Decor.OffsetFor(w, _decor.WatermarkImage, _dest, r.Location);
            _liveDecor = _decor with { Watermark = w with { OffsetX = ox, OffsetY = oy } };
        }
        else if (_decorStartSticker is { } s)
        {
            // Stickers turn and scale about their centre, which keeps both gestures predictable at any angle.
            var center = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
            float k = Math.Max(Distance(p, center) / Math.Max(1, Distance(_decorStart, center)), 8 * S / Math.Max(1, Math.Min(r.Width, r.Height)));
            var changed = _decorDrag switch
            {
                DecorDrag.Move => s with { X = s.X + dx / _dest.Width, Y = s.Y + dy / _dest.Height },
                DecorDrag.Resize when s.Kind == StickerKind.Mosaic => ResizeMosaic(s, center, p),
                DecorDrag.Resize when s.Kind == StickerKind.Text => s with { Style = s.Style with { Size = Math.Clamp(s.Style.Size * k, 0.01f, 0.5f) } },
                DecorDrag.Resize => s with { Width = s.Width * k },
                _ => s with { Angle = AngleTo(center, p) },
            };
            _liveDecor = _decor with { Stickers = new Dictionary<int, Sticker>(_decor.Stickers) { [s.Id] = changed } };
        }
        Invalidate();
    }

    /// <summary>A mosaic takes any shape: its corner follows the pointer, measured along the mosaic's own turned axes.</summary>
    Sticker ResizeMosaic(Sticker s, PointF center, PointF p)
    {
        var local = GifCapture.Decor.Rotate(p, center, -s.Angle);
        float min = 8 * S;
        return s with
        {
            Width = Math.Max(2 * Math.Abs(local.X - center.X), min) / _dest.Width,
            Height = Math.Max(2 * Math.Abs(local.Y - center.Y), min) / _dest.Height,
        };
    }

    /// <summary>Turn that points the sticker's top at <paramref name="p"/>; Shift snaps to 15° steps.</summary>
    static float AngleTo(PointF center, PointF p)
    {
        float a = MathF.Atan2(p.Y - center.Y, p.X - center.X) * 180 / MathF.PI + 90;
        if ((ModifierKeys & Keys.Shift) != 0) a = MathF.Round(a / 15) * 15;
        a = (a % 360 + 360) % 360;
        return a > 180 ? a - 360 : a;
    }

    void EndDecorDrag()
    {
        _decorDrag = DecorDrag.None;
        if (_liveDecor is not { } live) return;
        _liveDecor = null;
        if (_watermarkSelected) WatermarkEdited?.Invoke(live.Watermark);
        else if (_selectedSticker is { } id) StickerEdited?.Invoke(live.Stickers[id]);
        Invalidate();
    }

    RectangleF ToScreen(Rectangle img) => new(_dest.X + img.X * _scale, _dest.Y + img.Y * _scale, img.Width * _scale, img.Height * _scale);

    Point ToImage(Point p) => new(
        (int)Math.Round((p.X - _dest.X) / _scale),
        (int)Math.Round((p.Y - _dest.Y) / _scale));

    void DrawCropOverlay(Graphics g, float s)
    {
        var c = ToScreen(_editCrop.IsEmpty ? FrameRect : _editCrop);
        using (var dim = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
        {
            g.FillRectangle(dim, _dest.X, _dest.Y, _dest.Width, c.Top - _dest.Y);
            g.FillRectangle(dim, _dest.X, c.Bottom, _dest.Width, _dest.Bottom - c.Bottom);
            g.FillRectangle(dim, _dest.X, c.Top, c.Left - _dest.X, c.Height);
            g.FillRectangle(dim, c.Right, c.Top, _dest.Right - c.Right, c.Height);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var thirds = new Pen(Color.FromArgb(70, 255, 255, 255)))
        {
            for (int i = 1; i <= 2; i++)
            {
                g.DrawLine(thirds, c.X + c.Width * i / 3, c.Y, c.X + c.Width * i / 3, c.Bottom);
                g.DrawLine(thirds, c.X, c.Y + c.Height * i / 3, c.Right, c.Y + c.Height * i / 3);
            }
        }
        using (var pen = new Pen(Theme.Accent, 2 * s)) g.DrawRectangle(pen, c.X, c.Y, c.Width, c.Height);

        float k = 9 * s;
        using var fill = new SolidBrush(Color.White);
        using var outline = new Pen(Theme.Accent, 1.5f * s);
        foreach (var p in HandlePoints(c))
        {
            g.FillRectangle(fill, p.X - k / 2, p.Y - k / 2, k, k);
            g.DrawRectangle(outline, p.X - k / 2, p.Y - k / 2, k, k);
        }

        var crop = _editCrop.IsEmpty ? FrameRect : _editCrop;
        var text = $"{crop.Width} × {crop.Height}";
        var size = TextRenderer.MeasureText(text, _labelFont);
        var chip = new Rectangle((int)c.X, (int)(c.Y - size.Height - 12 * s), size.Width + (int)(14 * s), size.Height + (int)(6 * s));
        if (chip.Y < 0) chip.Y = (int)(c.Y + 6 * s);
        using (var path = Theme.RoundRect(chip, chip.Height / 2f))
        using (var bg = new SolidBrush(Theme.AccentFill))
            g.FillPath(bg, path);
        TextRenderer.DrawText(g, text, _labelFont, chip, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    static PointF[] HandlePoints(RectangleF c)
    {
        float cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2;
        return [new(c.Left, c.Top), new(cx, c.Top), new(c.Right, c.Top), new(c.Left, cy), new(c.Right, cy),
            new(c.Left, c.Bottom), new(cx, c.Bottom), new(c.Right, c.Bottom)];
    }

    Zone HitTest(Point p)
    {
        var c = ToScreen(_editCrop.IsEmpty ? FrameRect : _editCrop);
        float t = 8 * S;
        bool l = Math.Abs(p.X - c.Left) <= t, r = Math.Abs(p.X - c.Right) <= t;
        bool top = Math.Abs(p.Y - c.Top) <= t, b = Math.Abs(p.Y - c.Bottom) <= t;
        bool inX = p.X >= c.Left - t && p.X <= c.Right + t, inY = p.Y >= c.Top - t && p.Y <= c.Bottom + t;
        if (inX && inY)
        {
            if (top && l) return Zone.NW;
            if (top && r) return Zone.NE;
            if (b && l) return Zone.SW;
            if (b && r) return Zone.SE;
            if (top) return Zone.N;
            if (b) return Zone.S;
            if (l) return Zone.W;
            if (r) return Zone.E;
        }
        return c.Contains(p) ? Zone.Move : Zone.New;
    }

    static Cursor CursorFor(Zone z) => z switch
    {
        Zone.Move => Cursors.SizeAll,
        Zone.N or Zone.S => Cursors.SizeNS,
        Zone.E or Zone.W => Cursors.SizeWE,
        Zone.NW or Zone.SE => Cursors.SizeNWSE,
        Zone.NE or Zone.SW => Cursors.SizeNESW,
        _ => Cursors.Cross,
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_frame == null) return;
        Focus(); // so Delete and Esc reach the editor for a picked sticker
        if (!_cropMode && e.Button == MouseButtons.Left && BeginDecorDrag(e)) return;
        if (e.Button == MouseButtons.Middle || (!_cropMode && e.Button == MouseButtons.Left && _zoom > 0))
        {
            _panning = true;
            _panStart = e.Location;
            _panOrigin = _pan;
            if (_zoom == 0) { _zoom = _scale; _panOrigin = _pan = PointF.Empty; }
            Cursor = Cursors.SizeAll;
            return;
        }
        if (!_cropMode || e.Button != MouseButtons.Left) return;
        _drag = HitTest(e.Location);
        _dragStartImg = ClampToFrame(ToImage(e.Location));
        _dragOrigin = _editCrop.IsEmpty ? FrameRect : _editCrop;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            _pan = new PointF(_panOrigin.X + e.X - _panStart.X, _panOrigin.Y + e.Y - _panStart.Y);
            Invalidate();
            return;
        }
        if (_decorDrag != DecorDrag.None) { ContinueDecorDrag(e.Location); return; }
        if (!_cropMode || _frame == null)
        {
            var part = HitDecor(e.Location).Part;
            Cursor = part != DecorDrag.None ? DecorCursor(part) : _zoom > 0 ? Cursors.Hand : Cursors.Default;
            return;
        }
        if (_drag == Zone.None)
        {
            Cursor = CursorFor(HitTest(e.Location));
            return;
        }

        var p = ClampToFrame(ToImage(e.Location));
        int dx = p.X - _dragStartImg.X, dy = p.Y - _dragStartImg.Y;
        var o = _dragOrigin;
        int l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
        const int min = 8;
        switch (_drag)
        {
            case Zone.New:
                l = Math.Min(_dragStartImg.X, p.X); r = Math.Max(_dragStartImg.X, p.X);
                t = Math.Min(_dragStartImg.Y, p.Y); b = Math.Max(_dragStartImg.Y, p.Y);
                break;
            case Zone.Move:
                dx = Math.Clamp(dx, -o.Left, _frame.Width - o.Right);
                dy = Math.Clamp(dy, -o.Top, _frame.Height - o.Bottom);
                l += dx; r += dx; t += dy; b += dy;
                break;
            default:
                if (_drag is Zone.N or Zone.NW or Zone.NE) t = Math.Min(o.Top + dy, b - min);
                if (_drag is Zone.S or Zone.SW or Zone.SE) b = Math.Max(o.Bottom + dy, t + min);
                if (_drag is Zone.W or Zone.NW or Zone.SW) l = Math.Min(o.Left + dx, r - min);
                if (_drag is Zone.E or Zone.NE or Zone.SE) r = Math.Max(o.Right + dx, l + min);
                break;
        }
        var rect = Rectangle.Intersect(Rectangle.FromLTRB(l, t, r, b), FrameRect);
        if (rect.Width < min || rect.Height < min) return;
        _editCrop = rect == FrameRect ? Rectangle.Empty : rect;
        Invalidate();
        EditCropChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = Zone.None;
        if (_decorDrag != DecorDrag.None) EndDecorDrag();
        if (_panning) { _panning = false; Cursor = _cropMode ? Cursors.Cross : _zoom > 0 ? Cursors.Hand : Cursors.Default; }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && HitDecor(e.Location).Sticker is { } id) StickerActivated?.Invoke(id);
    }

    Point ClampToFrame(Point p) => _frame == null ? p : new(Math.Clamp(p.X, 0, _frame.Width), Math.Clamp(p.Y, 0, _frame.Height));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont.Dispose();
            _toastFont.Dispose();
            _toastTimer.Dispose();
            _rotated?.Dispose();
            _piece?.Dispose();
            foreach (var c in _bitmaps.Values) c.Bitmap.Dispose();
            _bitmaps.Clear();
        }
        base.Dispose(disposing);
    }
}

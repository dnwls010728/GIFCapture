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
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
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

    string? _caption;

    /// <summary>Caption of the current frame, drawn the same way the export burns it in.</summary>
    public string? Caption
    {
        get => _caption;
        set { if (_caption == value) return; _caption = value; Invalidate(); }
    }

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
        return swap ? new SizeF(src.Height, src.Width) : new SizeF(src.Width, src.Height);
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

        var area = RectangleF.Inflate(ClientRectangle, -20 * s, -20 * s);
        float fit = Math.Min(area.Width / src.Width, area.Height / src.Height);
        _scale = _zoom > 0 ? _zoom : fit;
        float w = src.Width * _scale, h = src.Height * _scale;
        var offset = _zoom > 0 ? _pan : PointF.Empty;
        _dest = new RectangleF((Width - w) / 2 + offset.X, (Height - h) / 2 + offset.Y, w, h);

        g.InterpolationMode = Playing ? InterpolationMode.Bilinear : (_scale >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic);
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(image, _dest, src, GraphicsUnit.Pixel);
        g.PixelOffsetMode = PixelOffsetMode.Default;

        // Thin outline separates the frame from the dark backdrop, which matters for dark content.
        using (var outline = new Pen(Color.FromArgb(0x30, 0x33, 0x3C)))
            g.DrawRectangle(outline, _dest.X - 1, _dest.Y - 1, _dest.Width + 1, _dest.Height + 1);

        if (!_cropMode && !string.IsNullOrWhiteSpace(_caption)) GifCapture.Caption.Draw(g, _dest, _caption);
        if (_cropMode) DrawCropOverlay(g, s);
        DrawToast(g, s);
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
        using (var bg = new SolidBrush(Theme.Accent))
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
        if (!_cropMode || _frame == null) { Cursor = _zoom > 0 ? Cursors.Hand : Cursors.Default; return; }
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
        if (_panning) { _panning = false; Cursor = _cropMode ? Cursors.Cross : _zoom > 0 ? Cursors.Hand : Cursors.Default; }
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
        }
        base.Dispose(disposing);
    }
}

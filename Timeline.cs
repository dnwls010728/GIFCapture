namespace GifCapture;

/// <summary>Horizontally scrolling strip of frame thumbnails with selection and playhead highlight.</summary>
sealed class Timeline : Control
{
    int _count, _current, _scroll, _hover = -1;
    bool _pressingItem, _draggingBar;
    int _barGrabX, _barGrabScroll;
    readonly Font _small;

    /// <summary>Thumbnail for a timeline position, or null while it is still being generated.</summary>
    public Func<int, Bitmap?>? ThumbnailFor { get; set; }
    public Func<int, int>? DelayFor { get; set; }
    /// <summary>Whether the frame at a position carries a caption (drawn as a small badge).</summary>
    public Func<int, bool>? HasCaption { get; set; }
    /// <summary>Whether the frame at a position carries inserted images (drawn as a second badge).</summary>
    public Func<int, bool>? HasStickers { get; set; }
    public IReadOnlySet<int> Selection { get; set; } = new HashSet<int>();
    public float Aspect { get; set; } = 16 / 9f;

    /// <summary>Mouse pressed on an item; carries the modifier keys held.</summary>
    public event Action<int, Keys>? ItemPressed;
    /// <summary>Mouse dragged across items with the button held.</summary>
    public event Action<int>? ItemDragged;

    public Timeline()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        _small = new Font("Segoe UI", 9f);
    }

    public int Count
    {
        get => _count;
        set { _count = value; _scroll = Math.Clamp(_scroll, 0, MaxScroll); Invalidate(); }
    }

    public int Current
    {
        get => _current;
        set { _current = value; Invalidate(); }
    }

    float S => DeviceDpi / 96f;
    int Pad => (int)(14 * S);
    int Gap => (int)(14 * S);
    int ThumbTop => (int)(10 * S);
    int LabelH => (int)(22 * S);
    int BarH => (int)(6 * S);
    int ThumbH => Math.Max(20, Height - ThumbTop - LabelH - BarH - (int)(12 * S));
    int ItemW => Math.Clamp((int)(ThumbH * Aspect), (int)(44 * S), (int)(220 * S));
    int Stride => ItemW + Gap;
    int ContentW => Pad * 2 + _count * Stride - Gap;
    int MaxScroll => Math.Max(0, ContentW - Width);

    Rectangle ItemRect(int i) => new(Pad + i * Stride - _scroll, ThumbTop, ItemW, ThumbH);

    Rectangle TrackRect => new(Pad, Height - BarH - (int)(5 * S), Width - Pad * 2, BarH);

    Rectangle BarThumbRect
    {
        get
        {
            var t = TrackRect;
            int w = Math.Max((int)(40 * S), (int)((long)t.Width * Width / Math.Max(1, ContentW)));
            int x = t.X + (MaxScroll == 0 ? 0 : (int)((long)(t.Width - w) * _scroll / MaxScroll));
            return new Rectangle(x, t.Y, w, t.Height);
        }
    }

    int IndexAt(int x) => Math.Clamp((x + _scroll - Pad + Gap / 2) / Stride, 0, Math.Max(0, _count - 1));

    public void EnsureVisible(int i)
    {
        var r = ItemRect(i);
        if (r.Left < Pad) _scroll += r.Left - Pad;
        else if (r.Right > Width - Pad) _scroll += r.Right - (Width - Pad);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        Invalidate();
    }

    /// <summary>Small filled tag ending at <paramref name="right"/>; returns where the next one to its left should end.</summary>
    float Badge(Graphics g, string text, Color fill, float right, float top, float s)
    {
        var badge = new RectangleF(right - 18 * s, top, 18 * s, 16 * s);
        using (var path = Theme.RoundRect(badge, 4 * s))
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, text, _small, Rectangle.Round(badge), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return badge.X - 3 * s;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.HighQuality(g);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        if (_count == 0) return;

        float s = S;
        int first = Math.Max(0, (_scroll - Pad) / Stride);
        int last = Math.Min(_count - 1, (_scroll + Width) / Stride + 1);
        using var slot = new SolidBrush(Color.FromArgb(0x1E, 0x20, 0x25));
        using var selFill = new SolidBrush(Color.FromArgb(60, Theme.Accent));
        using var selPen = new Pen(Theme.Accent, 2 * s);
        using var curPen = new Pen(Color.White, 2 * s);

        for (int i = first; i <= last; i++)
        {
            var r = ItemRect(i);
            using (var path = Theme.RoundRect(r, 6 * s)) g.FillPath(slot, path);

            if (ThumbnailFor?.Invoke(i) is { } thumb)
            {
                float k = Math.Min((float)r.Width / thumb.Width, (float)r.Height / thumb.Height);
                float w = thumb.Width * k, h = thumb.Height * k;
                g.DrawImage(thumb, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            }

            bool selected = Selection.Contains(i);
            if (selected)
            {
                using var path = Theme.RoundRect(r, 6 * s);
                g.FillPath(selFill, path);
                g.DrawPath(selPen, path);
            }
            if (i == _hover && i != _current && !selected)
            {
                using var hoverPen = new Pen(Theme.Faint, 1.5f * s);
                using var path = Theme.RoundRect(r, 6 * s);
                g.DrawPath(hoverPen, path);
            }
            if (i == _current)
            {
                using var path = Theme.RoundRect(RectangleF.Inflate(r, 2 * s, 2 * s), 8 * s);
                g.DrawPath(curPen, path);
            }

            var labelRect = new Rectangle(r.X, r.Bottom + (int)(3 * s), r.Width, LabelH);
            TextRenderer.DrawText(g, $"{i + 1}", _small, labelRect, i == _current ? Theme.Text : Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            float badgeRight = r.Right - 4 * s;
            if (HasCaption?.Invoke(i) == true) badgeRight = Badge(g, "T", Theme.AccentFill, badgeRight, r.Y + 4 * s, s);
            if (HasStickers?.Invoke(i) == true) Badge(g, "▣", Color.FromArgb(0x1B, 0x7F, 0x53), badgeRight, r.Y + 4 * s, s);
            if (DelayFor != null && r.Width >= 60 * s)
                TextRenderer.DrawText(g, $"{DelayFor(i)}ms", _small, labelRect, Theme.Muted,
                    TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }

        if (MaxScroll > 0)
        {
            using (var track = new SolidBrush(Theme.Surface))
            using (var path = Theme.RoundRect(TrackRect, BarH / 2f))
                g.FillPath(track, path);
            using (var thumbBrush = new SolidBrush(_draggingBar ? Theme.Muted : Theme.Faint))
            using (var path = Theme.RoundRect(BarThumbRect, BarH / 2f))
                g.FillPath(thumbBrush, path);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scroll = Math.Clamp(_scroll - e.Delta / 120 * Stride * 2, 0, MaxScroll);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _count == 0) return;

        var bar = Rectangle.Inflate(TrackRect, 0, (int)(6 * S));
        if (MaxScroll > 0 && bar.Contains(e.Location))
        {
            _draggingBar = true;
            var thumb = BarThumbRect;
            if (!thumb.Contains(e.X, thumb.Y))
                _scroll = Math.Clamp((int)((long)(e.X - TrackRect.X - thumb.Width / 2) * MaxScroll / Math.Max(1, TrackRect.Width - thumb.Width)), 0, MaxScroll);
            _barGrabX = e.X;
            _barGrabScroll = _scroll;
            Invalidate();
            return;
        }

        _pressingItem = true;
        ItemPressed?.Invoke(IndexAt(e.X), ModifierKeys);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingBar)
        {
            var t = TrackRect;
            int range = Math.Max(1, t.Width - BarThumbRect.Width);
            _scroll = Math.Clamp(_barGrabScroll + (int)((long)(e.X - _barGrabX) * MaxScroll / range), 0, MaxScroll);
            Invalidate();
        }
        else if (!_pressingItem)
        {
            int hover = _count > 0 && e.Y < ThumbTop + ThumbH + LabelH ? IndexAt(e.X) : -1;
            if (hover != _hover) { _hover = hover; Invalidate(); }
        }
        else
        {
            // Auto-scroll when dragging a selection past either edge.
            if (e.X < 0) _scroll = Math.Max(0, _scroll - Stride / 2);
            else if (e.X > Width) _scroll = Math.Min(MaxScroll, _scroll + Stride / 2);
            ItemDragged?.Invoke(IndexAt(Math.Clamp(e.X, 0, Width)));
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0) { _hover = -1; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressingItem = false;
        if (_draggingBar) { _draggingBar = false; Invalidate(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _small.Dispose();
        base.Dispose(disposing);
    }
}

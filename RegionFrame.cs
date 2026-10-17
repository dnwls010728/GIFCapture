using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GifCapture;

/// <summary>
/// On-screen outline of the capture region, drawn as a per-pixel-alpha layered window so the inside stays
/// click-through. While editing, drag the size tab to move and the edges or handles to resize. While recording
/// it turns red and ignores the mouse entirely.
/// </summary>
sealed class RegionFrame : Form
{
    enum Zone { None, Move, Close, N, S, E, W, NE, NW, SE, SW }

    const int MinSize = 16;

    readonly float _s;
    readonly Font _font;
    Rectangle _sel;
    bool _recording, _captureHidden;
    Zone _drag, _hover;
    Point _dragStart;
    Rectangle _dragOrigin;
    Size _renderedSize;

    /// <summary>Raised while dragging (<c>final</c> = false) and once on release (<c>final</c> = true).</summary>
    public event Action<Rectangle, bool>? SelectionChanged;
    public event EventHandler? CloseRequested;

    public Rectangle Selection => _sel;
    public bool IsDragging => _drag != Zone.None;

    int Band => (int)(10 * _s);
    int TabH => (int)(30 * _s);
    int Line => Math.Max(2, (int)(2 * _s));

    public RegionFrame(Rectangle selection, float scale)
    {
        _sel = selection;
        _s = scale;
        _font = new Font("Segoe UI", 12.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = WindowBounds;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateCaptureAffinity();
        Render();
    }

    /// <summary>Hides the outline from screen captures on behalf of the owner (recording, previews, region picking).</summary>
    public void SetCaptureHidden(bool hidden)
    {
        _captureHidden = hidden;
        UpdateCaptureAffinity();
    }

    // Excluded only when needed, so other programs can capture the outline while idle.
    // During a drag it must stay out of the loupe's magnified screen grab.
    void UpdateCaptureAffinity()
    {
        if (IsHandleCreated) Native.SetCaptureExcluded(Handle, _captureHidden || _recording || _drag != Zone.None);
    }

    Rectangle WindowBounds => new(_sel.X - Band, _sel.Y - Band - TabH, _sel.Width + Band * 2, _sel.Height + Band * 2 + TabH);
    Rectangle ClientSel => new(Band, Band + TabH, _sel.Width, _sel.Height);

    string TabText => _recording ? _recordText : $"{_sel.Width} × {_sel.Height}";

    string _recordText = "REC";
    int _countdown;

    /// <summary>Tab caption while recording, e.g. "REC 00:12".</summary>
    public void SetRecordingText(string text)
    {
        if (text == _recordText) return;
        _recordText = text;
        if (_recording) Render();
    }

    /// <summary>Big number in the middle of the region before recording starts; 0 hides it.</summary>
    public void SetCountdown(int seconds)
    {
        _countdown = seconds;
        Render();
    }

    Rectangle TabRect
    {
        get
        {
            var text = TextRenderer.MeasureText(TabText, _font);
            int closeW = _recording ? 0 : (int)(22 * _s);
            int lead = _recording ? (int)(16 * _s) : 0;
            return new Rectangle(Band - Line, 0, text.Width + (int)(16 * _s) + closeW + lead, TabH - (int)(4 * _s));
        }
    }

    Rectangle CloseRect
    {
        get
        {
            var t = TabRect;
            int w = (int)(22 * _s);
            return new Rectangle(t.Right - w, t.Top, w, t.Height);
        }
    }

    public void SetSelection(Rectangle sel)
    {
        if (sel == _sel) return;
        bool sameSize = sel.Size == _sel.Size;
        _sel = sel;
        if (sameSize && _renderedSize == WindowBounds.Size)
            Location = WindowBounds.Location;
        else
            Render();
    }

    public void SetRecording(bool recording)
    {
        _recording = recording;
        if (IsHandleCreated)
        {
            long ex = (long)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            ex = recording ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);
        }
        UpdateCaptureAffinity();
        Render();
    }

    void Render()
    {
        if (!IsHandleCreated) return;
        var b = WindowBounds;
        using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var r = ClientSel;
            var color = _recording ? Theme.Rec : Theme.Accent;

            if (!_recording)
            {
                // Nearly invisible band: alpha 1 is enough to receive the mouse, alpha 0 inside stays click-through.
                using var grab = new Region(Rectangle.Inflate(r, Band, Band));
                grab.Exclude(Rectangle.Inflate(r, -(int)(3 * _s), -(int)(3 * _s)));
                using var grabBrush = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
                g.FillRegion(grabBrush, grab);
            }

            using (var line = new SolidBrush(color))
            {
                int t = Line;
                g.FillRectangle(line, r.Left - t, r.Top - t, r.Width + t * 2, t);
                g.FillRectangle(line, r.Left - t, r.Bottom, r.Width + t * 2, t);
                g.FillRectangle(line, r.Left - t, r.Top, t, r.Height);
                g.FillRectangle(line, r.Right, r.Top, t, r.Height);
            }

            if (!_recording) DrawHandles(g, r);
            if (_countdown > 0) DrawCountdown(g, r);
            DrawTab(g, color);
        }

        if (LayeredWindow.Update(this, bmp, b.Location))
            _renderedSize = b.Size;
    }

    void DrawCountdown(Graphics g, Rectangle r)
    {
        float d = Math.Min(140 * _s, Math.Min(r.Width, r.Height) * 0.8f);
        var circle = new RectangleF(r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        using (var bg = new SolidBrush(Color.FromArgb(210, 14, 15, 18))) g.FillEllipse(bg, circle);
        using (var ring = new Pen(Theme.Rec, 3 * _s)) g.DrawEllipse(ring, RectangleF.Inflate(circle, -3 * _s, -3 * _s));
        using var font = new Font("Segoe UI", d * 0.45f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(_countdown.ToString(), font, Brushes.White, circle, sf);
    }

    void DrawHandles(Graphics g, Rectangle r)
    {
        int k = (int)(9 * _s);
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        var points = new PointF[]
        {
            new(r.Left, r.Top), new(cx, r.Top), new(r.Right, r.Top),
            new(r.Left, cy), new(r.Right, cy),
            new(r.Left, r.Bottom), new(cx, r.Bottom), new(r.Right, r.Bottom),
        };
        using var fill = new SolidBrush(Color.White);
        using var pen = new Pen(Theme.Accent, 1.5f * _s);
        foreach (var p in points)
        {
            var h = new RectangleF(p.X - k / 2f, p.Y - k / 2f, k, k);
            g.FillRectangle(fill, h);
            g.DrawRectangle(pen, h.X, h.Y, h.Width, h.Height);
        }
    }

    void DrawTab(Graphics g, Color color)
    {
        var t = TabRect;
        using (var path = Theme.RoundRect(t, 6 * _s))
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, path);

        int x = t.X + (int)(8 * _s);
        if (_recording)
        {
            float d = 7 * _s;
            using var dot = new SolidBrush(Color.White);
            g.FillEllipse(dot, x, t.Y + (t.Height - d) / 2, d, d);
            x += (int)(14 * _s);
        }
        var textRect = new Rectangle(x, t.Y, t.Width, t.Height);
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
            g.DrawString(TabText, _font, Brushes.White, textRect, sf);

        if (!_recording)
        {
            var c = CloseRect;
            float m = 6.5f * _s, cx = c.X + c.Width / 2f - 2 * _s, cy = c.Y + c.Height / 2f;
            using var pen = new Pen(Color.FromArgb(_hover == Zone.Close ? 255 : 200, Color.White), 1.6f * _s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, cx - m / 2, cy - m / 2, cx + m / 2, cy + m / 2);
            g.DrawLine(pen, cx + m / 2, cy - m / 2, cx - m / 2, cy + m / 2);
        }
    }

    Zone HitTest(Point p)
    {
        if (_recording) return Zone.None;
        if (CloseRect.Contains(p)) return Zone.Close;
        if (TabRect.Contains(p)) return Zone.Move;

        var r = ClientSel;
        int band = Band;
        bool nearL = Math.Abs(p.X - r.Left) <= band, nearR = Math.Abs(p.X - r.Right) <= band;
        bool nearT = Math.Abs(p.Y - r.Top) <= band, nearB = Math.Abs(p.Y - r.Bottom) <= band;
        bool inX = p.X >= r.Left - band && p.X <= r.Right + band;
        bool inY = p.Y >= r.Top - band && p.Y <= r.Bottom + band;
        if (!inX || !inY) return Zone.None;

        if (nearT && nearL) return Zone.NW;
        if (nearT && nearR) return Zone.NE;
        if (nearB && nearL) return Zone.SW;
        if (nearB && nearR) return Zone.SE;
        if (nearT) return Zone.N;
        if (nearB) return Zone.S;
        if (nearL) return Zone.W;
        if (nearR) return Zone.E;
        return Zone.None;
    }

    static Cursor CursorFor(Zone z) => z switch
    {
        Zone.Move => Cursors.SizeAll,
        Zone.Close => Cursors.Hand,
        Zone.N or Zone.S => Cursors.SizeNS,
        Zone.E or Zone.W => Cursors.SizeWE,
        Zone.NW or Zone.SE => Cursors.SizeNWSE,
        Zone.NE or Zone.SW => Cursors.SizeNESW,
        _ => Cursors.Default,
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var zone = HitTest(e.Location);
        if (zone is Zone.None or Zone.Close) return;
        _drag = zone;
        UpdateCaptureAffinity();
        _dragStart = Cursor.Position;
        _dragOrigin = _sel;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag == Zone.None)
        {
            var zone = HitTest(e.Location);
            if (zone != _hover)
            {
                bool repaint = zone == Zone.Close || _hover == Zone.Close;
                _hover = zone;
                Cursor = CursorFor(zone);
                if (repaint) Render();
            }
            return;
        }

        var now = Cursor.Position;
        int dx = now.X - _dragStart.X, dy = now.Y - _dragStart.Y;
        var o = _dragOrigin;
        int l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
        switch (_drag)
        {
            case Zone.Move: l += dx; r += dx; t += dy; b += dy; break;
            case Zone.N: t = Math.Min(t + dy, b - MinSize); break;
            case Zone.S: b = Math.Max(b + dy, t + MinSize); break;
            case Zone.W: l = Math.Min(l + dx, r - MinSize); break;
            case Zone.E: r = Math.Max(r + dx, l + MinSize); break;
            case Zone.NW: t = Math.Min(t + dy, b - MinSize); l = Math.Min(l + dx, r - MinSize); break;
            case Zone.NE: t = Math.Min(t + dy, b - MinSize); r = Math.Max(r + dx, l + MinSize); break;
            case Zone.SW: b = Math.Max(b + dy, t + MinSize); l = Math.Min(l + dx, r - MinSize); break;
            case Zone.SE: b = Math.Max(b + dy, t + MinSize); r = Math.Max(r + dx, l + MinSize); break;
        }
        Snap(ref l, ref t, ref r, ref b, now);
        SetSelection(Rectangle.FromLTRB(l, t, r, b));
        SelectionChanged?.Invoke(_sel, false);
        ShowLoupe(now);
    }

    /// <summary>Pulls edges that come within a few pixels of the monitor's edges onto them.</summary>
    void Snap(ref int l, ref int t, ref int r, ref int b, Point cursor)
    {
        var screen = Screen.FromPoint(cursor).Bounds;
        int k = (int)(8 * _s);
        bool moveX = _drag is Zone.Move, moveY = _drag is Zone.Move;
        bool left = moveX || _drag is Zone.W or Zone.NW or Zone.SW, right = moveX || _drag is Zone.E or Zone.NE or Zone.SE;
        bool top = moveY || _drag is Zone.N or Zone.NW or Zone.NE, bottom = moveY || _drag is Zone.S or Zone.SW or Zone.SE;
        int w = r - l, h = b - t;

        if (left && Math.Abs(l - screen.Left) <= k) { l = screen.Left; if (moveX) r = l + w; }
        else if (right && Math.Abs(r - screen.Right) <= k) { r = screen.Right; if (moveX) l = r - w; }
        if (top && Math.Abs(t - screen.Top) <= k) { t = screen.Top; if (moveY) b = t + h; }
        else if (bottom && Math.Abs(b - screen.Bottom) <= k) { b = screen.Bottom; if (moveY) t = b - h; }
    }

    LoupeWindow? _loupe;

    /// <summary>Magnifies the edge or corner being dragged so it can be placed on an exact pixel.</summary>
    void ShowLoupe(Point cursor)
    {
        bool edgeX = _drag is Zone.Move or Zone.W or Zone.NW or Zone.SW or Zone.E or Zone.NE or Zone.SE;
        bool edgeY = _drag is Zone.Move or Zone.N or Zone.NW or Zone.NE or Zone.S or Zone.SW or Zone.SE;
        int x = _drag is Zone.E or Zone.NE or Zone.SE ? _sel.Right : edgeX ? _sel.Left : cursor.X;
        int y = _drag is Zone.S or Zone.SW or Zone.SE ? _sel.Bottom : edgeY ? _sel.Top : cursor.Y;
        string caption = _drag == Zone.Move ? $"{_sel.X}, {_sel.Y}" : $"{_sel.Width} × {_sel.Height}";
        _loupe ??= new LoupeWindow(_s);
        _loupe.ShowAt(new Point(x, y), cursor, caption, edgeX, edgeY);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag != Zone.None)
        {
            _drag = Zone.None;
            UpdateCaptureAffinity();
            _loupe?.Hide();
            SelectionChanged?.Invoke(_sel, true);
        }
        else if (e.Button == MouseButtons.Left && HitTest(e.Location) == Zone.Close)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == Zone.Close) { _hover = Zone.None; Render(); }
        _hover = Zone.None;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            _loupe?.Dispose();
        }
        base.Dispose(disposing);
    }
}

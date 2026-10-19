using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GifCapture;

enum SelectMode { Region, Window, Screen }

/// <summary>
/// Full-desktop overlay over a frozen screenshot. Region: drag a rectangle. Window/Screen: hover to highlight, click to pick.
/// Esc or right-click cancels.
/// </summary>
sealed class RegionSelector : Form
{
    readonly SelectMode _mode;
    readonly Rectangle _virtual;
    readonly Bitmap _shot, _dimmed;
    readonly Font _labelFont, _hintFont;
    readonly List<(Rectangle Bounds, string Label, IntPtr Handle)> _targets = [];
    Point? _dragStart;
    Rectangle _sel;             // client coordinates
    string _selLabel = "";
    IntPtr _selHandle;
    Rectangle _hintRect;

    /// <summary>Selected rectangle in screen coordinates, clipped to the desktop.</summary>
    public Rectangle Selection => new(_sel.X + _virtual.X, _sel.Y + _virtual.Y, _sel.Width, _sel.Height);
    /// <summary>Picked window in Window mode, otherwise zero.</summary>
    public IntPtr SelectedWindow => _mode == SelectMode.Window ? _selHandle : IntPtr.Zero;
    public string SelectedLabel => _selLabel;

    public RegionSelector(SelectMode mode)
    {
        _mode = mode;
        _virtual = SystemInformation.VirtualScreen;
        _shot = new Bitmap(_virtual.Width, _virtual.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(_shot))
            g.CopyFromScreen(_virtual.Location, Point.Empty, _virtual.Size);

        _dimmed = new Bitmap(_shot);
        using (var g = Graphics.FromImage(_dimmed))
        using (var dim = new SolidBrush(Color.FromArgb(140, 8, 9, 12)))
            g.FillRectangle(dim, 0, 0, _dimmed.Width, _dimmed.Height);

        if (mode == SelectMode.Window)
        {
            foreach (var w in WindowFinder.Enumerate())
            {
                var r = Rectangle.Intersect(w.Bounds, _virtual);
                if (r.Width >= 8 && r.Height >= 8)
                    _targets.Add((ToClient(r), w.Title, w.Handle));
            }
        }
        else if (mode == SelectMode.Screen)
        {
            int i = 1;
            foreach (var screen in Screen.AllScreens)
                _targets.Add((ToClient(screen.Bounds), L.F("모니터 {0}", i++) + (screen.Primary ? L.T(" (주)") : ""), IntPtr.Zero));
        }

        _labelFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        _hintFont = new Font("Segoe UI", 11f);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = mode == SelectMode.Region ? Cursors.Cross : Cursors.Hand;
        Bounds = _virtual;
    }

    Rectangle ToClient(Rectangle screen) => new(screen.X - _virtual.X, screen.Y - _virtual.Y, screen.Width, screen.Height);

    /// <summary>Snapshot of the selected area taken when the overlay opened.</summary>
    public Bitmap CropSnapshot() => _shot.Clone(Rectangle.Intersect(_sel, new Rectangle(Point.Empty, _shot.Size)), PixelFormat.Format32bppArgb);

    string Hint => _mode switch
    {
        SelectMode.Region => L.T("드래그해서 영역 선택   ·   Esc 취소"),
        SelectMode.Window => L.T("녹화할 창 클릭   ·   Esc 취소"),
        _ => L.T("녹화할 모니터 클릭   ·   Esc 취소"),
    };

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _virtual;
        Activate();
        UpdateHover(PointToClient(Cursor.Position));
        UpdateHint(PointToClient(Cursor.Position));
        UpdateCursor(PointToClient(Cursor.Position));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(_dimmed, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);

        if (_sel.Width > 0 && _sel.Height > 0)
        {
            var visible = Rectangle.Intersect(_sel, e.ClipRectangle);
            if (!visible.IsEmpty)
                g.DrawImage(_shot, visible, visible, GraphicsUnit.Pixel);
        }
        g.CompositingMode = CompositingMode.SourceOver;
        g.PixelOffsetMode = PixelOffsetMode.Default;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_sel.Width > 0 && _sel.Height > 0)
        {
            using (var pen = new Pen(Theme.Accent, 2))
                g.DrawRectangle(pen, _sel.X + 1, _sel.Y + 1, _sel.Width - 2, _sel.Height - 2);
            if (_mode == SelectMode.Region) DrawHandles(g);

            var text = _mode == SelectMode.Region || _selLabel.Length == 0
                ? $"{_sel.Width} × {_sel.Height}"
                : $"{Truncate(_selLabel, 60)}   {_sel.Width} × {_sel.Height}";
            DrawChip(g, text, LabelRect(_sel, text), _labelFont, Theme.AccentFill, Color.White);
        }

        if (_mode == SelectMode.Region && _cursorPos is { } c)
        {
            DrawCrosshair(g, c);
            DrawLoupe(g, c);
        }

        if (!_hintRect.IsEmpty)
            DrawChip(g, Hint, _hintRect, _hintFont, Color.FromArgb(240, 28, 30, 36), Theme.Text);
    }

    // ───────────── Precision aids (Region mode) ─────────────

    const int LoupeCells = LoupeRenderer.Cells;
    Point? _cursorPos;

    float S => DeviceDpi / 96f;
    int LoupeZoom => Math.Max(6, (int)(8 * S));
    int LoupeSize => LoupeCells * LoupeZoom;

    Rectangle LoupeRect(Point p)
    {
        int size = LoupeSize, labelH = (int)(34 * S), gap = (int)(22 * S);
        var screen = ToClient(Screen.FromPoint(new Point(p.X + _virtual.X, p.Y + _virtual.Y)).Bounds);
        int x = p.X + gap, y = p.Y + gap;
        if (x + size > screen.Right) x = p.X - gap - size;
        if (y + size + labelH > screen.Bottom) y = p.Y - gap - size - labelH;
        return new Rectangle(x, y, size, size + labelH);
    }

    void DrawCrosshair(Graphics g, Point p)
    {
        using var pen = new Pen(Color.FromArgb(110, 255, 255, 255)) { DashStyle = DashStyle.Dash };
        g.SmoothingMode = SmoothingMode.None;
        g.DrawLine(pen, 0, p.Y, ClientSize.Width, p.Y);
        g.DrawLine(pen, p.X, 0, p.X, ClientSize.Height);
        g.SmoothingMode = SmoothingMode.AntiAlias;
    }

    /// <summary>Magnified pixels around the cursor plus its screen coordinates, for pixel-exact edges.</summary>
    void DrawLoupe(Graphics g, Point p)
    {
        var r = LoupeRect(p);
        LoupeRenderer.Draw(g, _shot, p, new Rectangle(r.X, r.Y, LoupeSize, LoupeSize), LoupeZoom,
            $"{p.X + _virtual.X}, {p.Y + _virtual.Y}", _labelFont);
    }

    void UpdateCursor(Point p)
    {
        if (_mode != SelectMode.Region || _cursorPos == p) return;
        if (_cursorPos is { } old) InvalidateCursorArea(old);
        _cursorPos = p;
        InvalidateCursorArea(p);
    }

    void InvalidateCursorArea(Point p)
    {
        Invalidate(new Rectangle(0, p.Y - 1, ClientSize.Width, 3));
        Invalidate(new Rectangle(p.X - 1, 0, 3, ClientSize.Height));
        Invalidate(Rectangle.Inflate(LoupeRect(p), 4, 4));
    }

    void DrawHandles(Graphics g)
    {
        const int k = 6;
        using var brush = new SolidBrush(Color.White);
        foreach (var p in new[] { new Point(_sel.Left, _sel.Top), new Point(_sel.Right, _sel.Top), new Point(_sel.Left, _sel.Bottom), new Point(_sel.Right, _sel.Bottom) })
            g.FillRectangle(brush, p.X - k / 2, p.Y - k / 2, k, k);
    }

    Rectangle LabelRect(Rectangle sel, string text)
    {
        var size = TextRenderer.MeasureText(text, _labelFont);
        int w = size.Width + 16, h = size.Height + 8;
        int x = sel.X, y = sel.Y - h - 6;
        if (y < 0) y = sel.Y + 8; // no room above: put inside the top edge
        if (_mode != SelectMode.Region) { x = sel.X + 8; y = sel.Y + 8; }
        return new Rectangle(Math.Min(x, ClientSize.Width - w), y, w, h);
    }

    static void DrawChip(Graphics g, string text, Rectangle r, Font font, Color bg, Color fg)
    {
        using (var path = Theme.RoundRect(r, r.Height / 2f))
        using (var brush = new SolidBrush(bg))
            g.FillPath(brush, path);
        TextRenderer.DrawText(g, text, font, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            DialogResult = DialogResult.Cancel;
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (_mode == SelectMode.Region)
        {
            _dragStart = e.Location;
            SetSelection(new Rectangle(e.Location, Size.Empty), "", IntPtr.Zero);
        }
        else if (_sel.Width >= 8 && _sel.Height >= 8)
        {
            DialogResult = DialogResult.OK;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        UpdateHint(e.Location);
        UpdateCursor(e.Location);
        if (_mode != SelectMode.Region)
        {
            UpdateHover(e.Location);
            return;
        }
        if (_dragStart is not { } s) return;
        int x1 = Math.Min(s.X, e.X), y1 = Math.Min(s.Y, e.Y);
        int x2 = Math.Max(s.X, e.X), y2 = Math.Max(s.Y, e.Y);
        SetSelection(Rectangle.FromLTRB(x1, y1, x2 + 1, y2 + 1), "", IntPtr.Zero);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_mode != SelectMode.Region || e.Button != MouseButtons.Left || _dragStart is null) return;
        _dragStart = null;
        if (_sel.Width >= 8 && _sel.Height >= 8)
            DialogResult = DialogResult.OK;
        else
            SetSelection(Rectangle.Empty, "", IntPtr.Zero);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
    }

    void UpdateHover(Point p)
    {
        if (_mode == SelectMode.Region) return;
        foreach (var t in _targets)
        {
            if (t.Bounds.Contains(p))
            {
                if (t.Bounds != _sel || t.Handle != _selHandle) SetSelection(t.Bounds, t.Label, t.Handle);
                return;
            }
        }
        if (!_sel.IsEmpty) SetSelection(Rectangle.Empty, "", IntPtr.Zero);
    }

    /// <summary>Keeps the hint chip at the top-centre of whichever monitor the cursor is on.</summary>
    void UpdateHint(Point p)
    {
        var screen = ToClient(Screen.FromPoint(new Point(p.X + _virtual.X, p.Y + _virtual.Y)).Bounds);
        var size = TextRenderer.MeasureText(Hint, _hintFont);
        var r = new Rectangle(screen.X + (screen.Width - size.Width - 36) / 2, screen.Y + 24, size.Width + 36, size.Height + 16);
        if (r == _hintRect) return;
        Invalidate(Rectangle.Inflate(_hintRect, 2, 2));
        _hintRect = r;
        Invalidate(Rectangle.Inflate(r, 2, 2));
    }

    void SetSelection(Rectangle sel, string label, IntPtr handle)
    {
        // Repaint only the old and new selection plus their labels.
        Invalidate(DirtyRect(_sel, _selLabel));
        _sel = sel;
        _selLabel = label;
        _selHandle = handle;
        Invalidate(DirtyRect(_sel, _selLabel));
    }

    Rectangle DirtyRect(Rectangle sel, string label)
    {
        if (sel.IsEmpty) return Rectangle.Empty;
        var text = _mode == SelectMode.Region || label.Length == 0 ? $"{sel.Width} × {sel.Height}" : $"{Truncate(label, 60)}   {sel.Width} × {sel.Height}";
        var labelRect = LabelRect(sel, text);
        var dirty = Rectangle.Union(Rectangle.Inflate(sel, 6, 6), labelRect);
        dirty.Width += 40; // label width changes as the size text grows
        return dirty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shot.Dispose();
            _dimmed.Dispose();
            _labelFont.Dispose();
            _hintFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

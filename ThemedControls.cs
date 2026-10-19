using System.Drawing.Drawing2D;

namespace GifCapture;

/// <summary>Owner-drawn base: tracks hover/press, supports keyboard focus, and paints on the window background.</summary>
abstract class ThemedControl : Control
{
    protected bool Hover, Pressed;

    protected ThemedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected float Dpi => DeviceDpi / 96f;

    /// <summary>True when the control has keyboard focus that should be visible (not after a mouse click).</summary>
    protected bool ShowFocusRing => Focused && ShowFocusCues;

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = Pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        Pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    /// <summary>Enter activates the focused control like a click.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && ActivatesOnEnter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected virtual bool ActivatesOnEnter => true;

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected Color SurfaceColor => !Enabled ? Theme.Bg : Pressed ? Theme.SurfacePressed : Hover ? Theme.SurfaceHover : Theme.Surface;

    protected Graphics Begin(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.HighQuality(g);
        return g;
    }

    protected void DrawFocusRing(Graphics g, GraphicsPath path)
    {
        if (!ShowFocusRing) return;
        using var pen = new Pen(Theme.AccentHover, 2 * Dpi);
        g.DrawPath(pen, path);
    }

    readonly Dictionary<(string Text, int Width), Font> _fitted = [];

    /// <summary>
    /// <see cref="Control.Font"/>, or a slightly smaller copy when <paramref name="text"/> would not fit in
    /// <paramref name="width"/>, so labels that run long in some languages shrink instead of being cut off.
    /// </summary>
    protected Font FitFont(string text, int width)
    {
        if (_fitted.TryGetValue((text, width), out var font)) return font;
        if (_fitted.Count > 32) ClearFitted(); // labels like progress text change constantly
        font = Font;
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        for (float size = Font.Size - 0.5f; size >= Font.Size * 0.75f && TextRenderer.MeasureText(text, font, Size.Empty, flags).Width > width; size -= 0.5f)
        {
            if (font != Font) font.Dispose();
            font = new Font(Font.FontFamily, size, Font.Style);
        }
        return _fitted[(text, width)] = font;
    }

    void ClearFitted()
    {
        foreach (var f in _fitted.Values) if (f != Font) f.Dispose();
        _fitted.Clear();
    }

    protected override void OnFontChanged(EventArgs e) { ClearFitted(); base.OnFontChanged(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearFitted();
        base.Dispose(disposing);
    }
}

enum CardIcon { Region, Window, Screen }

/// <summary>Large selectable card with a line icon, title and hint.</summary>
sealed class ModeCard : ThemedControl
{
    bool _selected;
    Font _titleFont = null!, _subFont = null!;

    public CardIcon Icon { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public ModeCard() => CreateFonts();

    protected override void OnFontChanged(EventArgs e) { CreateFonts(); base.OnFontChanged(e); }

    void CreateFonts()
    {
        _titleFont?.Dispose();
        _subFont?.Dispose();
        _titleFont = new Font(Font.FontFamily, Font.Size * 1.1f, FontStyle.Bold);
        _subFont = new Font(Font.FontFamily, Font.Size * 0.95f);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float s = Dpi;
        var r = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
        using (var path = Theme.RoundRect(r, 10 * s))
        {
            using var fill = new SolidBrush(_selected ? Theme.AccentSoft : Enabled ? SurfaceColor : Theme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(_selected ? Theme.Accent : Hover && Enabled ? Theme.Faint : Theme.Border, _selected ? 2f * s : 1f);
            g.DrawPath(pen, path);
            DrawFocusRing(g, path);
        }

        if (_selected)
        {
            // Check badge in the top-right corner makes the active target obvious at a glance.
            float d = 16 * s;
            var badge = new RectangleF(Width - d - 10 * s, 10 * s, d, d);
            using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, badge);
            using var pen = new Pen(Color.White, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new PointF[] { new(badge.X + d * 0.28f, badge.Y + d * 0.52f), new(badge.X + d * 0.44f, badge.Y + d * 0.68f), new(badge.X + d * 0.73f, badge.Y + d * 0.36f) });
        }

        var iconColor = !Enabled ? Theme.Faint : _selected ? Theme.Accent : Theme.Text;
        DrawIcon(g, new RectangleF(15 * s, 15 * s, 28 * s, 22 * s), iconColor, s);

        TextRenderer.DrawText(g, Title, _titleFont, new Point((int)(14 * s), (int)(46 * s)), Enabled ? Theme.Text : Theme.Faint, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Subtitle, _subFont, new Point((int)(14 * s), (int)(68 * s)), Enabled ? Theme.Muted : Theme.Faint, TextFormatFlags.NoPadding);
    }

    void DrawIcon(Graphics g, RectangleF b, Color color, float s)
    {
        using var pen = new Pen(color, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (Icon)
        {
            case CardIcon.Region:
            {
                float c = 7 * s;
                g.DrawLines(pen, new PointF[] { new(b.Left, b.Top + c), new(b.Left, b.Top), new(b.Left + c, b.Top) });
                g.DrawLines(pen, new PointF[] { new(b.Right - c, b.Top), new(b.Right, b.Top), new(b.Right, b.Top + c) });
                g.DrawLines(pen, new PointF[] { new(b.Right, b.Bottom - c), new(b.Right, b.Bottom), new(b.Right - c, b.Bottom) });
                g.DrawLines(pen, new PointF[] { new(b.Left + c, b.Bottom), new(b.Left, b.Bottom), new(b.Left, b.Bottom - c) });
                float cx = b.Left + b.Width / 2, cy = b.Top + b.Height / 2, k = 4 * s;
                g.DrawLine(pen, cx - k, cy, cx + k, cy);
                g.DrawLine(pen, cx, cy - k, cx, cy + k);
                break;
            }
            case CardIcon.Window:
            {
                using (var path = Theme.RoundRect(b, 3 * s)) g.DrawPath(pen, path);
                g.DrawLine(pen, b.Left, b.Top + 6 * s, b.Right, b.Top + 6 * s);
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, b.Left + 3 * s, b.Top + 2 * s, 2.4f * s, 2.4f * s);
                g.FillEllipse(dot, b.Left + 7 * s, b.Top + 2 * s, 2.4f * s, 2.4f * s);
                break;
            }
            case CardIcon.Screen:
            {
                var screen = new RectangleF(b.Left, b.Top, b.Width, b.Height - 6 * s);
                using (var path = Theme.RoundRect(screen, 3 * s)) g.DrawPath(pen, path);
                float cx = b.Left + b.Width / 2;
                g.DrawLine(pen, cx, screen.Bottom, cx, b.Bottom);
                g.DrawLine(pen, cx - 6 * s, b.Bottom, cx + 6 * s, b.Bottom);
                break;
            }
        }
    }
}

/// <summary>Round record button: red dot when idle, rounded square while recording, number during countdown.</summary>
sealed class RecordButton : ThemedControl
{
    bool _recording;
    int _countdown;
    Font _numberFont = null!;

    public RecordButton() => CreateFonts();

    protected override void OnFontChanged(EventArgs e) { CreateFonts(); base.OnFontChanged(e); }

    void CreateFonts()
    {
        _numberFont?.Dispose();
        _numberFont = new Font(Font.FontFamily, Font.Size * 1.6f, FontStyle.Bold);
    }

    public bool Recording
    {
        get => _recording;
        set { _recording = value; Invalidate(); }
    }

    /// <summary>Seconds left before recording starts; 0 hides it.</summary>
    public int Countdown
    {
        get => _countdown;
        set { _countdown = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float s = Dpi;
        var r = new RectangleF(1.5f, 1.5f, Width - 4, Height - 4);
        using (var fill = new SolidBrush(Enabled ? SurfaceColor : Theme.Surface)) g.FillEllipse(fill, r);
        var ring = _recording || _countdown > 0 ? Theme.Rec : Hover && Enabled ? Theme.Faint : Theme.Border;
        using (var pen = new Pen(ring, (_recording ? 2f : 1.5f) * s)) g.DrawEllipse(pen, r);
        if (ShowFocusRing)
        {
            using var focus = new Pen(Theme.AccentHover, 2 * s);
            g.DrawEllipse(focus, RectangleF.Inflate(r, -3 * s, -3 * s));
        }

        float cx = Width / 2f, cy = Height / 2f;
        if (_countdown > 0)
        {
            TextRenderer.DrawText(g, _countdown.ToString(), _numberFont, ClientRectangle, Theme.Rec,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }

        using var brush = new SolidBrush(Enabled ? Theme.Rec : Theme.Faint);
        if (_recording)
        {
            float k = 9 * s;
            using var path = Theme.RoundRect(new RectangleF(cx - k, cy - k, k * 2, k * 2), 4 * s);
            g.FillPath(brush, path);
        }
        else
        {
            float k = (Pressed ? 11 : Hover && Enabled ? 13 : 12) * s;
            g.FillEllipse(brush, cx - k, cy - k, k * 2, k * 2);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _numberFont.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Pill-shaped segmented picker over integer values. Arrow keys move the selection when focused.</summary>
sealed class Segmented : ThemedControl
{
    int[] _values = [];
    int _index, _hoverIndex = -1;

    public event EventHandler? ValueChanged;

    public int[] Values
    {
        get => _values;
        set { _values = value; _index = Math.Clamp(_index, 0, Math.Max(0, value.Length - 1)); Invalidate(); }
    }

    /// <summary>Appended to each label, e.g. "%".</summary>
    public string Suffix { get; set; } = "";

    /// <summary>Text per segment instead of the number (e.g. tab names).</summary>
    public string[]? Labels { get; set; }

    /// <summary>Selected preset, or 0 when nothing is selected.</summary>
    public int Value
    {
        get => _index < 0 || _index >= _values.Length ? 0 : _values[_index];
        set
        {
            int i = Array.IndexOf(_values, value);
            if (i < 0 || i == _index) return;
            _index = i;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Highlights <paramref name="value"/> if it is a preset (otherwise nothing) without raising <see cref="ValueChanged"/>.</summary>
    public void ShowValue(int value)
    {
        _index = Array.IndexOf(_values, value);
        Invalidate();
    }

    protected override bool ActivatesOnEnter => false;

    int IndexAt(int x) => _values.Length == 0 ? -1 : Math.Clamp(x * _values.Length / Math.Max(1, Width), 0, _values.Length - 1);

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_values.Length > 0 && e.KeyCode is Keys.Left or Keys.Right)
        {
            int i = Math.Clamp((_index < 0 ? 0 : _index) + (e.KeyCode == Keys.Left ? -1 : 1), 0, _values.Length - 1);
            Value = _values[i];
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexAt(e.X);
        if (i != _hoverIndex) { _hoverIndex = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hoverIndex = -1; base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && IndexAt(e.X) is var i and >= 0) Value = _values[i];
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float s = Dpi;
        var r = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
        using (var path = Theme.RoundRect(r, 8 * s))
        {
            using var fill = new SolidBrush(Theme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
            DrawFocusRing(g, path);
        }
        if (_values.Length == 0) return;

        float w = r.Width / _values.Length;
        for (int i = 0; i < _values.Length; i++)
        {
            var seg = new RectangleF(r.X + i * w + 3 * s, r.Y + 3 * s, w - 6 * s, r.Height - 6 * s);
            bool selected = i == _index;
            if (selected || (i == _hoverIndex && Enabled))
            {
                using var path = Theme.RoundRect(seg, 6 * s);
                using var fill = new SolidBrush(selected ? (Enabled ? Theme.AccentFill : Theme.SurfacePressed) : Theme.SurfaceHover);
                g.FillPath(fill, path);
            }
            var color = !Enabled ? Theme.Faint : selected ? Color.White : Theme.Muted;
            var label = Labels != null && i < Labels.Length ? Labels[i] : _values[i] + Suffix;
            TextRenderer.DrawText(g, label, FitFont(label, (int)seg.Width - (int)(4 * s)), Rectangle.Round(seg), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Number field with − / + buttons. Click a button (hold to repeat), scroll, use ↑↓, or type digits and press Enter.</summary>
sealed class NumberStepper : ThemedControl
{
    int _value, _min, _max = 100;
    string? _typed;
    int _hoverZone; // -1 minus, 1 plus, 0 text
    readonly System.Windows.Forms.Timer _repeat = new();
    int _repeatDir;

    public event EventHandler? ValueChanged;

    public int Minimum { get => _min; set { _min = value; Value = _value; } }
    public int Maximum { get => _max; set { _max = value; Value = _value; } }
    public int Step { get; set; } = 1;
    /// <summary>Shown instead of the number when the value is 0 (e.g. "무한").</summary>
    public string? ZeroText { get; set; }
    /// <summary>Unit drawn after the number, e.g. "fps".</summary>
    public string Suffix { get; set; } = "";

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (v == _value) { Invalidate(); return; }
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True while digits have been typed but not committed.</summary>
    public bool IsEditing => _typed != null;

    public NumberStepper()
    {
        Cursor = Cursors.IBeam;
        _repeat.Tick += (_, _) =>
        {
            _repeat.Interval = 60;
            Value += _repeatDir * Step;
        };
    }

    protected override bool ActivatesOnEnter => false;

    int ButtonW => Height;

    int ZoneAt(int x) => x < ButtonW ? -1 : x >= Width - ButtonW ? 1 : 0;

    /// <summary>Applies typed digits, if any.</summary>
    public void Commit()
    {
        if (_typed == null) return;
        var typed = _typed;
        _typed = null;
        if (int.TryParse(typed, out int v)) Value = v;
        else Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Up: Commit(); Value += Step; e.Handled = true; break;
            case Keys.Down: Commit(); Value -= Step; e.Handled = true; break;
            case Keys.Escape when _typed != null: _typed = null; Invalidate(); e.Handled = true; break;
            case Keys.Back when _typed != null:
                _typed = _typed.Length > 1 ? _typed[..^1] : "";
                Invalidate();
                e.Handled = true;
                break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (char.IsDigit(e.KeyChar))
        {
            _typed = ((_typed ?? "") + e.KeyChar)[..Math.Min(6, (_typed?.Length ?? 0) + 1)];
            Invalidate();
            e.Handled = true;
        }
        base.OnKeyPress(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Commit();
        base.OnLostFocus(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!Enabled) return;
        Commit();
        Value += Math.Sign(e.Delta) * Step;
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int z = ZoneAt(e.X);
        if (z != _hoverZone)
        {
            _hoverZone = z;
            Cursor = !Enabled ? Cursors.Default : z == 0 ? Cursors.IBeam : Cursors.Hand;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hoverZone = 0; base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        int z = ZoneAt(e.X);
        if (z == 0) return;
        Commit();
        _repeatDir = z;
        Value += z * Step;
        _repeat.Interval = 400;
        _repeat.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _repeat.Stop();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float s = Dpi;
        var r = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
        using (var path = Theme.RoundRect(r, 8 * s))
        {
            using var fill = new SolidBrush(Theme.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(Focused ? Theme.Accent : Theme.Border, Focused ? 1.5f * s : 1f);
            g.DrawPath(pen, path);
        }

        DrawButton(g, new RectangleF(r.X, r.Y, ButtonW, r.Height), -1, s);
        DrawButton(g, new RectangleF(r.Right - ButtonW, r.Y, ButtonW, r.Height), 1, s);

        bool zero = _typed == null && _value == 0 && ZeroText != null;
        var text = _typed ?? (zero ? ZeroText! : _value.ToString());
        var textArea = new Rectangle(ButtonW, 0, Width - ButtonW * 2, Height);
        var numberColor = !Enabled ? Theme.Faint : _typed != null ? Theme.AccentHover : Theme.Text;
        var numSize = TextRenderer.MeasureText(g, text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var suffixSize = Suffix.Length == 0 || zero ? Size.Empty : TextRenderer.MeasureText(g, " " + Suffix, Font, Size.Empty, TextFormatFlags.NoPadding);
        int x = textArea.X + (textArea.Width - numSize.Width - suffixSize.Width) / 2;
        int y = (Height - numSize.Height) / 2;
        TextRenderer.DrawText(g, text, Font, new Point(x, y), numberColor, TextFormatFlags.NoPadding);
        if (_typed != null)
        {
            using var caret = new Pen(Theme.AccentHover, 1.2f * s);
            g.DrawLine(caret, x + numSize.Width + 1, y + 2 * s, x + numSize.Width + 1, y + numSize.Height - 2 * s);
        }
        if (Suffix.Length > 0 && !zero)
            TextRenderer.DrawText(g, " " + Suffix, Font, new Point(x + numSize.Width, y), Enabled ? Theme.Muted : Theme.Faint, TextFormatFlags.NoPadding);
    }

    void DrawButton(Graphics g, RectangleF b, int dir, float s)
    {
        bool canStep = Enabled && (dir < 0 ? _value > _min : _value < _max);
        if (canStep && _hoverZone == dir && Hover)
        {
            using var hover = new SolidBrush(Theme.SurfaceHover);
            using var path = Theme.RoundRect(RectangleF.Inflate(b, -3 * s, -3 * s), 6 * s);
            g.FillPath(hover, path);
        }
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, k = 5 * s;
        using var pen = new Pen(canStep ? Theme.Text : Theme.Faint, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - k, cy, cx + k, cy);
        if (dir > 0) g.DrawLine(pen, cx, cy - k, cx, cy + k);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _repeat.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>iOS-style switch with a label on the right. Space or Enter toggles when focused.</summary>
sealed class ToggleSwitch : ThemedControl
{
    bool _checked;

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        if (Enabled) Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float s = Dpi;
        float tw = 36 * s, th = 20 * s;
        var track = new RectangleF(2, (Height - th) / 2, tw, th);
        using (var path = Theme.RoundRect(track, th / 2))
        {
            var on = _checked && Enabled;
            using var fill = new SolidBrush(on ? (Hover ? Theme.AccentFillHover : Theme.AccentFill) : _checked ? Theme.SurfacePressed : (Hover && Enabled ? Theme.SurfaceHover : Theme.Surface));
            g.FillPath(fill, path);
            if (!_checked)
            {
                using var pen = new Pen(Theme.Faint);
                g.DrawPath(pen, path);
            }
            if (ShowFocusRing)
            {
                using var focus = Theme.RoundRect(RectangleF.Inflate(track, 2 * s, 2 * s), th / 2 + 2 * s);
                DrawFocusRing(g, focus);
            }
        }

        float k = 14 * s, pad = (th - k) / 2;
        float kx = _checked ? track.Right - pad - k : track.Left + pad;
        using (var knob = new SolidBrush(!Enabled ? Theme.Faint : _checked ? Color.White : Theme.Muted))
            g.FillEllipse(knob, kx, track.Top + pad, k, k);

        var textRect = new Rectangle((int)(tw + 12 * s), 0, Width - (int)(tw + 12 * s), Height);
        TextRenderer.DrawText(g, Text, FitFont(Text, textRect.Width), textRect, Enabled ? Theme.Text : Theme.Faint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Rounded text button. Accent = primary action (can show progress); Danger = destructive action.</summary>
sealed class PillButton : ThemedControl
{
    bool _accent, _danger;
    float _progress = -1;

    public bool Accent
    {
        get => _accent;
        set { _accent = value; Invalidate(); }
    }

    public bool Danger
    {
        get => _danger;
        set { _danger = value; Invalidate(); }
    }

    /// <summary>0..1 shows a progress fill behind the text; negative hides it.</summary>
    public float Progress
    {
        get => _progress;
        set { _progress = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float s = Dpi;
        var r = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
        using (var path = Theme.RoundRect(r, 8 * s))
        {
            Color fill;
            if (_accent)
                fill = _progress >= 0 ? Theme.AccentSoft : !Enabled ? Theme.Surface : Pressed ? Theme.AccentFillPressed : Hover ? Theme.AccentFillHover : Theme.AccentFill;
            else if (_danger && Enabled && (Hover || Pressed))
                fill = Color.FromArgb(Pressed ? 0x4A : 0x3A, 0x22, 0x28);
            else
                fill = SurfaceColor;
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);

            if (_progress >= 0)
            {
                var state = g.Save();
                g.SetClip(path);
                using var bar = new SolidBrush(Theme.AccentFill);
                g.FillRectangle(bar, r.X, r.Y, r.Width * Math.Clamp(_progress, 0, 1), r.Height);
                g.Restore(state);
            }
            if (!_accent)
            {
                var border = !Enabled ? Color.FromArgb(0x2A, 0x2D, 0x35) : _danger && Hover ? Color.FromArgb(0x8A, 0x3A, 0x44) : Theme.Border;
                using var pen = new Pen(border);
                g.DrawPath(pen, path);
            }
            DrawFocusRing(g, path);
        }

        Color color = _accent ? (Enabled || _progress >= 0 ? Color.White : Theme.Faint)
            : !Enabled ? Theme.Faint
            : _danger ? Theme.Danger
            : Theme.Text;
        TextRenderer.DrawText(g, Text, FitFont(Text, Width - (int)(12 * s)), ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

enum Glyph { Play, Pause, Prev, Next }

/// <summary>Square button with a vector media glyph; <see cref="Primary"/> fills it with the accent color.</summary>
sealed class IconButton : ThemedControl
{
    Glyph _glyph;

    public Glyph Glyph
    {
        get => _glyph;
        set { _glyph = value; Invalidate(); }
    }

    public bool Primary { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float s = Dpi;
        var r = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
        using (var path = Theme.RoundRect(r, 8 * s))
        {
            var fillColor = Primary && Enabled ? (Pressed ? Theme.AccentFillPressed : Hover ? Theme.AccentFillHover : Theme.AccentFill) : SurfaceColor;
            using var fill = new SolidBrush(fillColor);
            g.FillPath(fill, path);
            if (!Primary)
            {
                using var pen = new Pen(Theme.Border);
                g.DrawPath(pen, path);
            }
            DrawFocusRing(g, path);
        }

        float cx = Width / 2f, cy = Height / 2f, k = 6 * s;
        using var brush = new SolidBrush(!Enabled ? Theme.Faint : Primary ? Color.White : Theme.Text);
        switch (_glyph)
        {
            case Glyph.Play:
                g.FillPolygon(brush, new PointF[] { new(cx - k * 0.6f, cy - k), new(cx + k * 1.0f, cy), new(cx - k * 0.6f, cy + k) });
                break;
            case Glyph.Pause:
                g.FillRectangle(brush, cx - k * 0.8f, cy - k, k * 0.55f, k * 2);
                g.FillRectangle(brush, cx + k * 0.25f, cy - k, k * 0.55f, k * 2);
                break;
            case Glyph.Prev:
                g.FillRectangle(brush, cx - k, cy - k * 0.8f, 1.8f * s, k * 1.6f);
                g.FillPolygon(brush, new PointF[] { new(cx + k * 0.8f, cy - k * 0.8f), new(cx - k * 0.6f, cy), new(cx + k * 0.8f, cy + k * 0.8f) });
                break;
            case Glyph.Next:
                g.FillRectangle(brush, cx + k - 1.8f * s, cy - k * 0.8f, 1.8f * s, k * 1.6f);
                g.FillPolygon(brush, new PointF[] { new(cx - k * 0.8f, cy - k * 0.8f), new(cx + k * 0.6f, cy), new(cx - k * 0.8f, cy + k * 0.8f) });
                break;
        }
    }
}

/// <summary>Rounded frame showing an image scaled to fit, with an optional REC badge and a guided empty state.</summary>
sealed class PreviewBox : Control
{
    Image? _image;
    bool _live;
    string _badge = "REC";

    public PreviewBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    /// <summary>Displayed image. The caller owns it and disposes the previous one.</summary>
    public Image? Image
    {
        get => _image;
        set { _image = value; Invalidate(); }
    }

    public bool Live
    {
        get => _live;
        set { _live = value; Invalidate(); }
    }

    /// <summary>Text in the live badge, e.g. "REC 00:12".</summary>
    public string BadgeText
    {
        get => _badge;
        set { _badge = value; if (_live) Invalidate(); }
    }

    public string PlaceholderTitle { get; set; } = "";
    /// <summary>Lines shown under the title when there is no image.</summary>
    public string[] PlaceholderSteps { get; set; } = [];

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        Theme.HighQuality(g);
        float s = DeviceDpi / 96f;
        var r = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);

        using var frame = Theme.RoundRect(r, 12 * s);
        using (var fill = new SolidBrush(Color.FromArgb(0x1A, 0x1B, 0x20))) g.FillPath(fill, frame);

        if (_image == null)
        {
            using var pen = new Pen(Theme.Border, 1.2f * s) { DashStyle = DashStyle.Dash };
            g.DrawPath(pen, frame);
            DrawEmptyState(g, s);
            return;
        }

        float pad = 10 * s;
        var area = RectangleF.Inflate(r, -pad, -pad);
        float scale = Math.Min(area.Width / _image.Width, area.Height / _image.Height);
        float w = _image.Width * scale, h = _image.Height * scale;
        var dest = new RectangleF(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);

        var state = g.Save();
        using (var clip = Theme.RoundRect(dest, 6 * s)) g.SetClip(clip);
        g.InterpolationMode = _live ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
        g.DrawImage(_image, dest);
        g.Restore(state);

        using (var pen = new Pen(_live ? Theme.Rec : Theme.Border, _live ? 1.5f * s : 1f)) g.DrawPath(pen, frame);

        if (_live)
        {
            using var badgeFont = new Font(Font.FontFamily, Font.Size * 0.95f, FontStyle.Bold);
            var textSize = TextRenderer.MeasureText(_badge, badgeFont);
            var badge = new RectangleF(dest.X + 8 * s, dest.Y + 8 * s, textSize.Width + 28 * s, 24 * s);
            using (var path = Theme.RoundRect(badge, 12 * s))
            using (var fill = new SolidBrush(Color.FromArgb(230, 18, 18, 22)))
                g.FillPath(fill, path);
            using (var dot = new SolidBrush(Theme.Rec))
                g.FillEllipse(dot, badge.X + 9 * s, badge.Y + 8.5f * s, 7 * s, 7 * s);
            TextRenderer.DrawText(g, _badge, badgeFont, new Rectangle((int)(badge.X + 21 * s), (int)badge.Y, textSize.Width + 4, (int)badge.Height),
                Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    void DrawEmptyState(Graphics g, float s)
    {
        using var titleFont = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold);
        int lineH = (int)(22 * s);
        int titleH = (int)(28 * s);
        int total = titleH + PlaceholderSteps.Length * lineH;
        int y = (Height - total) / 2;
        TextRenderer.DrawText(g, PlaceholderTitle, titleFont, new Rectangle(0, y, Width, titleH), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        y += titleH + (int)(4 * s);

        // Numbered steps, left-aligned as a block in the centre.
        int blockW = PlaceholderSteps.Length == 0 ? 0 : PlaceholderSteps.Max(t => TextRenderer.MeasureText(t, Font).Width) + (int)(30 * s);
        int x = (Width - blockW) / 2;
        float d = 18 * s;
        using var numFont = new Font(Font.FontFamily, Font.Size * 0.85f, FontStyle.Bold);
        using var circle = new SolidBrush(Theme.SurfaceHover);
        for (int i = 0; i < PlaceholderSteps.Length; i++, y += lineH)
        {
            var c = new RectangleF(x, y + (lineH - d) / 2, d, d);
            g.FillEllipse(circle, c);
            TextRenderer.DrawText(g, (i + 1).ToString(), numFont, Rectangle.Round(c), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, PlaceholderSteps[i], Font, new Rectangle(x + (int)(28 * s), y, blockW, lineH), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

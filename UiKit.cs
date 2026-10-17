namespace GifCapture;

/// <summary>Labelled horizontal slider: caption and value on top, track below. Drag, click, wheel or ←→.</summary>
sealed class ThemedSlider : ThemedControl
{
    int _value, _min, _max = 100;
    bool _dragging;

    public event EventHandler? ValueChanged;

    public int Minimum { get => _min; set { _min = value; Value = _value; } }
    public int Maximum { get => _max; set { _max = value; Value = _value; } }
    public int Step { get; set; } = 1;
    public string Suffix { get; set; } = "";
    /// <summary>Optional text describing the current value, shown after the number (e.g. "작게").</summary>
    public Func<int, string>? Describe { get; set; }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _min, _max);
            if (Step > 1) v = Math.Clamp(_min + (int)Math.Round((v - _min) / (double)Step) * Step, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override bool ActivatesOnEnter => false;

    float S => Dpi;
    RectangleF Track => new(8 * S, Height - 14 * S, Width - 16 * S, 4 * S);

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Right)
        {
            Value += (e.KeyCode == Keys.Left ? -1 : 1) * Math.Max(1, Step);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Enabled) Value += Math.Sign(e.Delta) * Math.Max(1, Step);
        base.OnMouseWheel(e);
    }

    void SetFromX(int x)
    {
        var t = Track;
        float k = Math.Clamp((x - t.X) / Math.Max(1, t.Width), 0, 1);
        Value = _min + (int)Math.Round(k * (_max - _min));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left || !Enabled) return;
        _dragging = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        float s = S;
        var top = new Rectangle(0, 0, Width, (int)(Height - 22 * s));
        TextRenderer.DrawText(g, Text, Font, top, Enabled ? Theme.Text : Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        var valueText = $"{_value}{Suffix}" + (Describe != null ? $"  {Describe(_value)}" : "");
        TextRenderer.DrawText(g, valueText, Font, top, Enabled ? Theme.AccentHover : Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var t = Track;
        float k = _max == _min ? 0 : (float)(_value - _min) / (_max - _min);
        using (var path = Theme.RoundRect(t, t.Height / 2))
        using (var bg = new SolidBrush(Theme.SurfacePressed))
            g.FillPath(bg, path);
        var fill = new RectangleF(t.X, t.Y, t.Width * k, t.Height);
        if (fill.Width > 0)
        {
            using var path = Theme.RoundRect(fill, t.Height / 2);
            using var b = new SolidBrush(Enabled ? Theme.Accent : Theme.Faint);
            g.FillPath(b, path);
        }
        float d = (Hover || _dragging ? 16 : 14) * s;
        var knob = new RectangleF(t.X + t.Width * k - d / 2, t.Y + t.Height / 2 - d / 2, d, d);
        using (var kb = new SolidBrush(Enabled ? Color.White : Theme.Faint)) g.FillEllipse(kb, knob);
        if (ShowFocusRing)
        {
            using var focus = new Pen(Theme.AccentHover, 2 * s);
            g.DrawEllipse(focus, RectangleF.Inflate(knob, 3 * s, 3 * s));
        }
    }
}

/// <summary>Menu colors that match the dark theme.</summary>
sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.Faint : e.Item.Tag as string == "header" ? Theme.Muted : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Muted;
        base.OnRenderArrow(e);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Popup;
        public override Color ImageMarginGradientBegin => Theme.Popup;
        public override Color ImageMarginGradientMiddle => Theme.Popup;
        public override Color ImageMarginGradientEnd => Theme.Popup;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.SurfacePressed;
        public override Color MenuItemSelected => Theme.SurfacePressed;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfacePressed;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfacePressed;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }

    public static ContextMenuStrip CreateMenu(Font font) => new()
    {
        Renderer = new DarkMenuRenderer(),
        BackColor = Theme.Popup,
        ForeColor = Theme.Text,
        Font = font,
        ShowImageMargin = false,
        Padding = new Padding(4),
    };

    public static ToolStripLabel Header(string text) => new(text) { Tag = "header", Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Padding = new Padding(4, 6, 4, 2) };
}

static class ClipboardHelper
{
    /// <summary>Puts the file on the clipboard so Ctrl+V pastes it into chat apps, mail and Explorer.</summary>
    public static bool CopyFile(string path)
    {
        try
        {
            Clipboard.SetFileDropList([path]);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false; // another app is holding the clipboard
        }
    }
}

/// <summary>Minimal dark single-line text prompt.</summary>
sealed class InputDialog : Form
{
    readonly TextBox _box = new();

    public string Value => _box.Text.Trim();

    InputDialog(string title, string prompt, string initial)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 150);

        var label = new Label { Text = prompt, ForeColor = Theme.Muted, Bounds = new Rectangle(20, 16, 320, 22) };
        _box.Bounds = new Rectangle(20, 44, 320, 28);
        _box.Text = initial;
        Theme.StyleTextBox(_box);
        var ok = new PillButton { Text = L.T("저장"), Accent = true, Bounds = new Rectangle(160, 96, 86, 36) };
        var cancel = new PillButton { Text = L.T("취소"), Bounds = new Rectangle(254, 96, 86, 36) };
        ok.Click += (_, _) => { if (Value.Length > 0) { DialogResult = DialogResult.OK; } };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.AddRange([label, _box, ok, cancel]);
        ResumeLayout(false);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyDarkTitleBar(Handle, Theme.Bg);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _box.Focus();
        _box.SelectAll();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && Value.Length > 0) { DialogResult = DialogResult.OK; return true; }
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public static string? Ask(IWin32Window owner, string title, string prompt, string initial)
    {
        using var dlg = new InputDialog(title, prompt, initial);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Value : null;
    }
}

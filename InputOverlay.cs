using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace GifCapture;

/// <summary>
/// Global low-level keyboard hook that keeps a short caption of recent key presses ("Ctrl + S", "hello").
/// Install on a thread with a message loop (the UI thread); read <see cref="CurrentText"/> from any thread.
/// </summary>
sealed class KeyboardTracker : IDisposable
{
    const int ShowMs = 1500;
    const int MaxTyped = 24;

    readonly Native.LowLevelKeyboardProc _proc; // kept alive for the lifetime of the hook
    readonly IntPtr _hook;
    readonly object _lock = new();
    string _text = "";
    bool _typing;
    long _at;

    public KeyboardTracker()
    {
        _proc = HookProc;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
    }

    /// <summary>Caption to draw now, or null when nothing was pressed recently.</summary>
    public string? CurrentText
    {
        get
        {
            lock (_lock)
                return _text.Length > 0 && Environment.TickCount64 - _at < ShowMs ? _text : null;
        }
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            OnKey((Keys)info.vkCode);
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    void OnKey(Keys key)
    {
        if (IsModifier(key)) return;
        bool ctrl = Native.IsKeyDown(0x11), alt = Native.IsKeyDown(0x12), shift = Native.IsKeyDown(0x10);
        bool win = Native.IsKeyDown(0x5B) || Native.IsKeyDown(0x5C);
        string name = KeyName(key);
        bool printable = name.Length == 1 || key == Keys.Space;
        long now = Environment.TickCount64;

        lock (_lock)
        {
            if (printable && !ctrl && !alt && !win)
            {
                // Plain typing accumulates into one caption.
                var ch = key == Keys.Space ? " " : shift ? name : name.ToLowerInvariant();
                bool continueTyping = _typing && now - _at < ShowMs;
                _text = continueTyping ? (_text + ch)[^Math.Min(MaxTyped, _text.Length + 1)..] : ch;
                _typing = true;
            }
            else
            {
                var sb = new StringBuilder();
                if (ctrl) sb.Append("Ctrl + ");
                if (alt) sb.Append("Alt + ");
                if (shift) sb.Append("Shift + ");
                if (win) sb.Append("Win + ");
                sb.Append(name);
                _text = sb.ToString();
                _typing = false;
            }
            _at = now;
        }
    }

    static bool IsModifier(Keys k) => k is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey or Keys.ShiftKey or Keys.LShiftKey
        or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    static string KeyName(Keys k) => k switch
    {
        >= Keys.A and <= Keys.Z => ((char)('A' + (k - Keys.A))).ToString(),
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => ((char)('0' + (k - Keys.NumPad0))).ToString(),
        Keys.Return => "Enter",
        Keys.Back => "Backspace",
        Keys.Escape => "Esc",
        Keys.Space => "Space",
        Keys.Delete => "Del",
        Keys.Insert => "Ins",
        Keys.Prior => "PgUp",
        Keys.Next => "PgDn",
        Keys.Left => "←",
        Keys.Right => "→",
        Keys.Up => "↑",
        Keys.Down => "↓",
        Keys.OemPeriod => ".",
        Keys.Oemcomma => ",",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemQuestion => "/",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemPipe => "\\",
        Keys.Oemtilde => "`",
        Keys.Multiply => "*",
        Keys.Add => "+",
        Keys.Subtract => "-",
        Keys.Divide => "/",
        Keys.Decimal => ".",
        _ => k.ToString(),
    };

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
    }
}

/// <summary>Draws click rings and the key caption onto captured frames.</summary>
sealed class InputOverlay
{
    readonly bool _clicks;
    readonly KeyboardTracker? _keys;
    readonly float _scale;

    public InputOverlay(bool showClicks, KeyboardTracker? keys, float scale)
    {
        _clicks = showClicks;
        _keys = keys;
        _scale = scale;
    }

    public bool IsActive => _clicks || _keys != null;

    public void Draw(Graphics g, Point origin, Size frame)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_clicks) DrawClick(g, origin);
        if (_keys?.CurrentText is { } text) DrawKeys(g, frame, text);
    }

    void DrawClick(Graphics g, Point origin)
    {
        bool swapped = Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0;
        bool left = Native.IsKeyDown(swapped ? 0x02 : 0x01), right = Native.IsKeyDown(swapped ? 0x01 : 0x02);
        if (!left && !right) return;
        Native.GetCursorPos(out var pt);
        float r = 18 * _scale;
        var c = new RectangleF(pt.X - origin.X - r, pt.Y - origin.Y - r, r * 2, r * 2);
        var color = left ? Color.FromArgb(255, 210, 40) : Color.FromArgb(80, 200, 255);
        using (var fill = new SolidBrush(Color.FromArgb(90, color))) g.FillEllipse(fill, c);
        using (var pen = new Pen(color, 3 * _scale)) g.DrawEllipse(pen, c);
    }

    static void DrawKeys(Graphics g, Size frame, string text)
    {
        float px = Math.Clamp(frame.Height * 0.055f, 13f, 30f);
        using var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        float padX = px * 0.8f, padY = px * 0.35f;
        var box = new RectangleF((frame.Width - size.Width) / 2 - padX, frame.Height - size.Height - padY * 2 - px * 0.8f,
            size.Width + padX * 2, size.Height + padY * 2);
        using (var path = Theme.RoundRect(box, px * 0.5f))
        using (var bg = new SolidBrush(Color.FromArgb(215, 20, 20, 24)))
            g.FillPath(bg, path);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString(text, font, Brushes.White, box.X + padX, box.Y + padY);
    }
}

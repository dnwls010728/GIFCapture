using System.Diagnostics;
using System.Drawing.Imaging;

namespace GifCapture;

sealed class MainForm : Form
{
    const int HotkeyId = 1;
    const int CountdownSeconds = 3;

    readonly AppSettings _settings = AppSettings.Current;

    readonly PillButton _openGifBtn = new() { Text = L.T("GIF 불러오기") };
    readonly PillButton _settingsBtn = new() { Text = L.T("설정") };
    readonly ModeCard _regionCard = new() { Icon = CardIcon.Region, Title = L.T("영역 지정"), Subtitle = L.T("드래그로 선택") };
    readonly ModeCard _windowCard = new() { Icon = CardIcon.Window, Title = L.T("창 선택"), Subtitle = L.T("클릭해서 추적") };
    readonly ModeCard _screenCard = new() { Icon = CardIcon.Screen, Title = L.T("전체 화면"), Subtitle = L.T("모니터 선택") };
    readonly PreviewBox _preview = new()
    {
        PlaceholderTitle = L.T("무엇을 녹화할까요?"),
        PlaceholderSteps = [L.T("위 카드나 크기 프리셋으로 녹화 대상 선택"), L.T("화면의 파란 테두리로 위치·크기 조절"), L.T("녹화 버튼 또는 F9로 시작 / 정지")],
    };
    readonly Label _targetLabel = new() { ForeColor = Theme.Text, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly NumberStepper _width = new() { Minimum = 16, Maximum = 7680, Step = 10, Value = 640, Suffix = "W", Enabled = false };
    readonly NumberStepper _height = new() { Minimum = 16, Maximum = 4320, Step = 10, Value = 360, Suffix = "H", Enabled = false };
    readonly PillButton _presetBtn = new() { Text = L.T("크기 프리셋") + "  ▾" };
    readonly Segmented _fpsPresets = new() { Values = [10, 15, 24, 30] };
    readonly NumberStepper _fps = new() { Minimum = 1, Maximum = 60, Value = 15, Suffix = "fps" };
    readonly Segmented _afterRecord = new() { Values = [0, 1], Labels = [L.T("편집기 열기"), L.T("바로 저장")] };
    readonly ToggleSwitch _cursor = new() { Text = L.T("마우스 커서"), Checked = true };
    readonly ToggleSwitch _clicks = new() { Text = L.T("클릭 강조") };
    readonly ToggleSwitch _keys = new() { Text = L.T("키 입력 표시") };
    readonly Panel _divider = new() { BackColor = Theme.Border };
    readonly RecordButton _recordButton = new() { Enabled = false };
    readonly Label _statusTitle = new() { ForeColor = Theme.Text, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _statusSub = new() { ForeColor = Theme.Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly PillButton _openFolder = new() { Text = L.T("폴더 열기"), Visible = false };
    readonly PillButton _editLast = new() { Text = L.T("편집하기"), Visible = false };
    readonly ToolTip _tips = Theme.CreateToolTip();
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 200 };
    readonly System.Windows.Forms.Timer _trackTimer = new() { Interval = 30 };

    SelectMode _mode;
    Rectangle _region;
    bool _hasTarget, _syncingSize;
    IntPtr _targetWindow;
    Size _windowOffset;          // region top-left relative to the target window
    string _targetName = "";
    RegionFrame? _frame;
    bool _captureHidden;
    Recorder? _recorder;
    KeyboardTracker? _keyTracker;
    CancellationTokenSource? _countdownCts;
    byte[]? _shownFrame;
    bool _busy, _hotkeyOk;
    string? _savedPath;
    // The last quick-saved recording, kept so "편집하기" can still open it in the editor.
    FrameStore? _lastStore;
    List<int>? _lastDelays;

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "GIF Capture";
        Icon = Theme.AppIcon;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        // Drag-and-drop needs an STA thread (Program.Main has [STAThread]); skip it rather than crash elsewhere.
        AllowDrop = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
        ClientSize = new Size(460, 684);

        var targetSection = Theme.SectionLabel(L.T("녹화 대상"), new Rectangle(20, 12, 150, 28));
        _openGifBtn.Bounds = new Rectangle(244, 10, 124, 32);
        _settingsBtn.Bounds = new Rectangle(374, 10, 66, 32);
        _regionCard.Bounds = new Rectangle(20, 48, 134, 96);
        _windowCard.Bounds = new Rectangle(163, 48, 134, 96);
        _screenCard.Bounds = new Rectangle(306, 48, 134, 96);
        _preview.Bounds = new Rectangle(20, 156, 420, 190);
        _targetLabel.Bounds = new Rectangle(22, 350, 418, 24);


        _width.Bounds = new Rectangle(20, 380, 140, 34);
        var times = Theme.MutedLabel("×", new Rectangle(162, 380, 16, 34));
        times.TextAlign = ContentAlignment.MiddleCenter;
        _height.Bounds = new Rectangle(180, 380, 140, 34);
        _presetBtn.Bounds = new Rectangle(328, 380, 112, 34);

        var settingsSection = Theme.SectionLabel(L.T("녹화 설정"), new Rectangle(20, 428, 200, 24));
        var fpsLabel = Theme.MutedLabel("FPS", new Rectangle(20, 456, 38, 34));
        _fpsPresets.Bounds = new Rectangle(58, 456, 212, 34);
        _fps.Bounds = new Rectangle(280, 456, 160, 34);
        var afterLabel = Theme.MutedLabel(L.T("녹화 후"), new Rectangle(20, 500, 62, 34));
        _afterRecord.Bounds = new Rectangle(84, 500, 356, 34);
        _cursor.Bounds = new Rectangle(20, 546, 128, 30);
        _clicks.Bounds = new Rectangle(154, 546, 140, 30);
        _keys.Bounds = new Rectangle(298, 546, 142, 30);

        _divider.Bounds = new Rectangle(0, 590, 460, 1);
        _recordButton.Bounds = new Rectangle(20, 604, 64, 64);
        _statusTitle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _statusTitle.Bounds = new Rectangle(98, 609, 236, 28);
        _statusSub.Bounds = new Rectangle(99, 637, 236, 24);
        _openFolder.Bounds = new Rectangle(340, 602, 100, 32);
        _editLast.Bounds = new Rectangle(340, 638, 100, 32);

        Controls.AddRange([targetSection, _openGifBtn, _settingsBtn, _regionCard, _windowCard, _screenCard, _preview, _targetLabel,
            _width, times, _height, _presetBtn, settingsSection, fpsLabel, _fpsPresets, _fps, afterLabel, _afterRecord,
            _cursor, _clicks, _keys, _divider, _recordButton, _statusTitle, _statusSub, _openFolder, _editLast]);
        ResumeLayout(false);

        _fps.Value = Math.Clamp(_settings.Fps, 1, 60);
        _fpsPresets.ShowValue(_fps.Value);
        _afterRecord.Value = _settings.QuickSave ? 1 : 0;
        _cursor.Checked = _settings.DrawCursor;
        _clicks.Checked = _settings.ShowClicks;
        _keys.Checked = _settings.ShowKeys;

        _tips.SetToolTip(_openGifBtn, L.T("기존 GIF 파일을 편집기로 엽니다 (Ctrl+O). 창에 GIF를 끌어다 놓아도 됩니다."));
        _tips.SetToolTip(_settingsBtn, L.T("언어, 카운트다운, 저장 폴더, 클립보드 복사"));
        _tips.SetToolTip(_regionCard, L.T("화면에서 드래그해 녹화할 사각형을 지정합니다."));
        _tips.SetToolTip(_windowCard, L.T("창을 클릭해 선택합니다. 창을 옮기면 녹화 영역도 따라갑니다."));
        _tips.SetToolTip(_screenCard, L.T("모니터 하나를 통째로 녹화합니다."));
        _tips.SetToolTip(_width, L.T("녹화 영역 너비 (픽셀). 휠, ↑↓, 숫자 입력 후 Enter."));
        _tips.SetToolTip(_height, L.T("녹화 영역 높이 (픽셀). 휠, ↑↓, 숫자 입력 후 Enter."));
        _tips.SetToolTip(_presetBtn, L.T("자주 쓰는 크기를 바로 적용하거나 지금 영역을 프리셋으로 저장합니다."));
        _tips.SetToolTip(_fpsPresets, L.T("초당 프레임 수. 높을수록 부드럽지만 파일이 커집니다."));
        _tips.SetToolTip(_fps, L.T("1–60 사이로 직접 지정. 휠, ↑↓, 숫자 입력 후 Enter."));
        _tips.SetToolTip(_afterRecord, L.T("편집기 열기: 자르기·자막 등을 손본 뒤 저장\n바로 저장: 마지막 내보내기 설정으로 곧바로 GIF 저장"));
        _tips.SetToolTip(_cursor, L.T("GIF에 마우스 포인터를 그립니다."));
        _tips.SetToolTip(_clicks, L.T("클릭할 때 포인터 주위에 원을 그립니다. 설명용 GIF에 좋습니다."));
        _tips.SetToolTip(_keys, L.T("누른 키와 단축키를 화면 아래에 표시합니다.\n비밀번호를 입력할 때는 끄세요."));
        _tips.SetToolTip(_recordButton, L.T("녹화 시작 / 정지 (F9)"));
        _tips.SetToolTip(_openFolder, L.T("저장한 GIF를 탐색기에서 보기"));
        _tips.SetToolTip(_editLast, L.T("방금 녹화를 편집기에서 다시 열기"));

        _openGifBtn.Click += async (_, _) => await OpenGifDialog();
        _settingsBtn.Click += (_, _) => OpenSettings();
        _regionCard.Click += (_, _) => PickTarget(SelectMode.Region);
        _windowCard.Click += (_, _) => PickTarget(SelectMode.Window);
        _screenCard.Click += (_, _) => PickTarget(SelectMode.Screen);
        _width.ValueChanged += (_, _) => OnSizeEdited();
        _height.ValueChanged += (_, _) => OnSizeEdited();
        _presetBtn.Click += (_, _) => ShowPresetMenu();
        _fpsPresets.ValueChanged += (_, _) => _fps.Value = _fpsPresets.Value;
        _fps.ValueChanged += (_, _) => { _fpsPresets.ShowValue(_fps.Value); _settings.Fps = _fps.Value; };
        _afterRecord.ValueChanged += (_, _) => _settings.QuickSave = _afterRecord.Value == 1;
        _cursor.CheckedChanged += (_, _) => _settings.DrawCursor = _cursor.Checked;
        _clicks.CheckedChanged += (_, _) => _settings.ShowClicks = _clicks.Checked;
        _keys.CheckedChanged += (_, _) => _settings.ShowKeys = _keys.Checked;
        _recordButton.Click += async (_, _) => await ToggleRecording();
        _uiTimer.Tick += async (_, _) => await OnUiTick();
        _trackTimer.Tick += (_, _) => FollowTarget();
        _openFolder.Click += (_, _) =>
        {
            if (_savedPath != null && File.Exists(_savedPath))
                Process.Start("explorer.exe", $"/select,\"{_savedPath}\"");
        };
        _editLast.Click += (_, _) =>
        {
            if (_lastStore != null && _lastDelays != null && !_busy && _recorder == null)
                OpenEditor(_lastStore, _lastDelays, null);
        };

        ShowIdleStatus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyDarkTitleBar(Handle, Theme.Bg);
        Native.SetCaptureExcluded(Handle, _captureHidden);
        _hotkeyOk = Native.RegisterHotKey(Handle, HotkeyId, Native.MOD_NOREPEAT, (uint)Keys.F9);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == Native.WM_HOTKEY && m.WParam == HotkeyId && _recordButton.Enabled && CanFocus)
            BeginInvoke(async () => await ToggleRecording());
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _countdownCts != null)
        {
            _countdownCts.Cancel();
            return true;
        }
        if (keyData == (Keys.Control | Keys.O) && _recorder == null)
        {
            _ = OpenGifDialog();
            return true;
        }
        // Fallback when another program owns the global F9.
        if (keyData == Keys.F9 && !_hotkeyOk && _recordButton.Enabled)
        {
            _ = ToggleRecording();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // The region outline belongs to this window: hide it while minimized.
        if (_frame != null && _recorder == null && _hasTarget)
            _frame.Visible = WindowState != FormWindowState.Minimized;
    }

    // Separate sentences per case: Korean particles differ after "F9" and "버튼".
    string StopHint => _hotkeyOk ? L.T("F9로 정지") : L.T("버튼으로 정지");
    string CancelHint => _hotkeyOk ? L.T("F9 또는 Esc로 취소") : L.T("Esc로 취소");
    string RerecordHint => _hotkeyOk ? L.T("F9로 다시 녹화") : L.T("녹화 버튼으로 다시 녹화");

    void ShowIdleStatus() => SetStatus(L.T("대상을 선택하세요"), L.T("카드나 크기 프리셋, 또는 GIF 불러오기"), Theme.Text);

    void ShowReadyStatus() => SetStatus(L.T("준비됨"), _hotkeyOk ? L.T("F9로 녹화 시작") : L.T("버튼으로 녹화 시작  ·  F9 사용 불가"), Theme.Text);

    void OpenSettings()
    {
        if (_recorder != null || _busy || _countdownCts != null) return;
        using var dlg = new SettingsForm();
        WithoutTopMost(() => dlg.ShowDialog(this));
        UpdateIdleTracking();
        if (!dlg.RestartRequested) return;
        _settings.Save();
        // Ask in the language just picked: that is the one the user can read for sure.
        var answer = WithoutTopMost(() => MessageBox.Show(this, L.In(_settings.Language, "언어를 바꾸려면 앱을 다시 시작해야 합니다. 지금 다시 시작할까요?"),
            "GIF Capture", MessageBoxButtons.YesNo, MessageBoxIcon.Question));
        if (answer == DialogResult.Yes) Application.Restart();
    }

    /// <summary>
    /// This window is TopMost, and a normal modal window it owns can end up behind it with no way to reach it.
    /// Drop TopMost for as long as the dialog is open.
    /// </summary>
    T WithoutTopMost<T>(Func<T> show)
    {
        bool was = TopMost;
        TopMost = false;
        try { return show(); }
        finally { TopMost = was; }
    }

    // ───────────── Target selection ─────────────

    void PickTarget(SelectMode mode)
    {
        if (_recorder != null || _busy || _countdownCts != null) return;

        Rectangle region;
        IntPtr window = IntPtr.Zero;
        string label;

        if (mode == SelectMode.Screen && Screen.AllScreens.Length == 1)
        {
            region = Screen.PrimaryScreen!.Bounds;
            label = L.F("모니터 {0}", 1);
        }
        else
        {
            RegionSelector selector = null!;
            GrabHidden(() => selector = new RegionSelector(mode));
            using var _ = selector;
            if (selector.ShowDialog(this) != DialogResult.OK) return;
            region = selector.Selection;
            window = selector.SelectedWindow;
            label = selector.SelectedLabel;
        }
        Activate();
        SetTarget(mode, region, window, label);
    }

    void SetTarget(SelectMode mode, Rectangle region, IntPtr window, string label)
    {
        _mode = mode;
        _hasTarget = true;
        _targetWindow = window;
        _targetName = label;
        _windowOffset = Size.Empty;
        if (window != IntPtr.Zero && Native.TryGetWindowBounds(window, out var wb))
            _windowOffset = new Size(region.X - wb.X, region.Y - wb.Y);
        _regionCard.Selected = mode == SelectMode.Region;
        _windowCard.Selected = mode == SelectMode.Window;
        _screenCard.Selected = mode == SelectMode.Screen;
        _width.Enabled = _height.Enabled = true;
        _recordButton.Enabled = true;

        SetRegion(region);
        ShowFrame();
        RefreshPreview();
        UpdateIdleTracking();
        ShowReadyStatus();
    }

    void SetRegion(Rectangle region)
    {
        _region = region;
        string size = $"{region.Width} × {region.Height}";
        _targetLabel.Text = _mode switch
        {
            SelectMode.Window => $"{L.T("창")}  ·  {_targetName}  ·  {size}",
            SelectMode.Screen => $"{_targetName}  ·  {size}",
            _ => $"{L.T("영역")}  ·  {size}  ·  {L.F("위치 ({0}, {1})", region.X, region.Y)}",
        };
        _syncingSize = true;
        _width.Value = region.Width;
        _height.Value = region.Height;
        _syncingSize = false;
    }

    /// <summary>W/H typed by hand: resize around the current top-left.</summary>
    void OnSizeEdited()
    {
        if (_syncingSize || !_hasTarget || _recorder != null) return;
        var r = new Rectangle(_region.Location, new Size(_width.Value, _height.Value));
        ApplyRegionChange(KeepOnScreen(r));
        RefreshPreview();
    }

    /// <summary>Common path for any manual change of the region (drag, typed size, preset).</summary>
    void ApplyRegionChange(Rectangle region)
    {
        if (_mode == SelectMode.Screen)
        {
            // A hand-adjusted monitor is just a region now.
            _mode = SelectMode.Region;
            _regionCard.Selected = true;
            _screenCard.Selected = false;
        }
        if (_targetWindow != IntPtr.Zero && Native.TryGetWindowBounds(_targetWindow, out var wb))
            _windowOffset = new Size(region.X - wb.X, region.Y - wb.Y);
        SetRegion(region);
        _frame?.SetSelection(region);
    }

    static Rectangle KeepOnScreen(Rectangle r)
    {
        var screen = Screen.FromRectangle(r).Bounds;
        int x = Math.Clamp(r.X, screen.Left, Math.Max(screen.Left, screen.Right - r.Width));
        int y = Math.Clamp(r.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - r.Height));
        return new Rectangle(x, y, r.Width, r.Height);
    }

    void ShowFrame()
    {
        if (_frame == null)
        {
            _frame = new RegionFrame(_region, DeviceDpi / 96f);
            _frame.SelectionChanged += OnFrameDragged;
            _frame.CloseRequested += (_, _) => ClearTarget();
            _frame.SetCaptureHidden(_captureHidden);
        }
        else
        {
            _frame.SetSelection(_region);
        }
        _frame.Show();
    }

    void OnFrameDragged(Rectangle region, bool final)
    {
        if (_recorder != null) return;
        ApplyRegionChange(region);
        if (final) RefreshPreview();
    }

    void ClearTarget()
    {
        if (_recorder != null || _busy || _countdownCts != null) return;
        _trackTimer.Stop();
        _frame?.Hide();
        _hasTarget = false;
        _targetWindow = IntPtr.Zero;
        _regionCard.Selected = _windowCard.Selected = _screenCard.Selected = false;
        _width.Enabled = _height.Enabled = false;
        _recordButton.Enabled = false;
        _targetLabel.Text = "";
        ReplacePreview(null);
        ShowIdleStatus();
    }

    // ───────────── Size presets ─────────────

    void ShowPresetMenu()
    {
        var menu = DarkMenuRenderer.CreateMenu(Font);
        menu.Items.Add(DarkMenuRenderer.Header(L.T("기본 크기")));
        foreach (var p in AppSettings.BuiltInPresets)
            menu.Items.Add(PresetItem(p));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(DarkMenuRenderer.Header(L.T("내 프리셋")));
        if (_settings.Presets.Count == 0)
            menu.Items.Add(new ToolStripMenuItem(L.T("저장된 프리셋 없음")) { Enabled = false });
        foreach (var p in _settings.Presets)
            menu.Items.Add(PresetItem(p));

        menu.Items.Add(new ToolStripSeparator());
        var save = new ToolStripMenuItem(L.T("지금 영역을 프리셋으로 저장…")) { Enabled = _hasTarget };
        save.Click += (_, _) => SaveCurrentAsPreset();
        menu.Items.Add(save);

        if (_settings.Presets.Count > 0)
        {
            var remove = new ToolStripMenuItem(L.T("내 프리셋 삭제"));
            foreach (var p in _settings.Presets.ToList())
            {
                var item = new ToolStripMenuItem(PresetText(p));
                item.Click += (_, _) =>
                {
                    _settings.Presets.Remove(p);
                    _settings.Save();
                    SetStatus(L.T("프리셋 삭제됨"), p.Name, Theme.Muted);
                };
                remove.DropDownItems.Add(item);
            }
            ((ToolStripDropDownMenu)remove.DropDown).ShowImageMargin = false;
            remove.DropDown.Renderer = menu.Renderer;
            remove.DropDown.BackColor = Theme.Popup;
            menu.Items.Add(remove);
        }

        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_presetBtn, new Point(0, _presetBtn.Height + 4));
    }

    static string PresetText(SizePreset p) =>
        $"{p.Name}     {p.Width} × {p.Height}" + (p.HasPosition ? $"   @ {p.X}, {p.Y}" : "");

    ToolStripMenuItem PresetItem(SizePreset p)
    {
        var item = new ToolStripMenuItem(PresetText(p)) { Enabled = _recorder == null && _countdownCts == null };
        item.Click += (_, _) => ApplyPreset(p);
        return item;
    }

    void ApplyPreset(SizePreset p)
    {
        var size = new Size(p.Width, p.Height);
        Rectangle region;
        if (p.HasPosition)
            region = new Rectangle(p.X!.Value, p.Y!.Value, p.Width, p.Height);
        else if (_hasTarget)
            region = new Rectangle(_region.Location, size);
        else
        {
            // No target yet: centre on the monitor this window is on.
            var screen = Screen.FromControl(this).Bounds;
            region = new Rectangle(screen.X + (screen.Width - size.Width) / 2, screen.Y + (screen.Height - size.Height) / 2, size.Width, size.Height);
        }
        region = KeepOnScreen(region);

        if (_hasTarget)
        {
            ApplyRegionChange(region);
            RefreshPreview();
            SetStatus(L.T("프리셋 적용"), $"{p.Name}  ·  {p.Width} × {p.Height}", Theme.Text);
        }
        else
        {
            SetTarget(SelectMode.Region, region, IntPtr.Zero, "");
        }
    }

    void SaveCurrentAsPreset()
    {
        var name = WithoutTopMost(() => InputDialog.Ask(this, L.T("프리셋 저장"), L.F("{0} × {1} 영역의 이름 (위치도 함께 저장됩니다)", _region.Width, _region.Height),
            L.F("내 영역 {0}", _settings.Presets.Count + 1)));
        if (name == null) return;
        _settings.Presets.RemoveAll(p => p.Name == name);
        _settings.Presets.Add(new SizePreset(name, _region.Width, _region.Height, _region.X, _region.Y));
        _settings.Save();
        SetStatus(L.T("프리셋 저장됨"), $"{name}  ·  {_region.Width} × {_region.Height}", Theme.Success);
    }

    // ───────────── Preview & tracking ─────────────

    /// <summary>Fresh still of the region; this window and the outline are excluded from capture.</summary>
    void RefreshPreview()
    {
        var r = Rectangle.Intersect(_region, SystemInformation.VirtualScreen);
        if (r.Width <= 0 || r.Height <= 0) return;
        var bmp = new Bitmap(_region.Width, _region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            GrabHidden(() => g.CopyFromScreen(r.Location, new Point(r.X - _region.X, r.Y - _region.Y), r.Size));
        }
        ReplacePreview(bmp);
    }

    /// <summary>
    /// Keeps this window and the outline out of screen captures. Only on while recording, so other
    /// programs (screenshots, screen sharing) can still see the app the rest of the time.
    /// </summary>
    void SetCaptureHidden(bool hidden)
    {
        _captureHidden = hidden;
        if (IsHandleCreated) Native.SetCaptureExcluded(Handle, hidden);
        _frame?.SetCaptureHidden(hidden);
    }

    /// <summary>Runs one of the app's own screen grabs with this window and the outline hidden from it.</summary>
    void GrabHidden(Action grab)
    {
        if (_captureHidden)
        {
            grab();
            return;
        }
        SetCaptureHidden(true);
        Native.DwmFlush();
        try { grab(); }
        finally { SetCaptureHidden(false); }
    }

    void ReplacePreview(Image? image)
    {
        var old = _preview.Image;
        _preview.Image = image;
        old?.Dispose();
    }

    bool Following => _targetWindow != IntPtr.Zero && _settings.FollowWindow;

    /// <summary>While idle, keep the outline glued to a followed window.</summary>
    void UpdateIdleTracking()
    {
        if (_recorder != null) return;
        if (Following) _trackTimer.Start();
        else _trackTimer.Stop();
    }

    void FollowTarget()
    {
        if (_frame == null) return;
        if (_recorder is { } r)
        {
            _frame.SetSelection(r.CurrentRegion);
            return;
        }
        if (_frame.IsDragging || !Native.IsWindow(_targetWindow) || Native.IsIconic(_targetWindow)) return;
        if (!Native.TryGetWindowBounds(_targetWindow, out var wb)) return;
        var region = new Rectangle(wb.X + _windowOffset.Width, wb.Y + _windowOffset.Height, _region.Width, _region.Height);
        if (region == _region) return;
        SetRegion(region);
        _frame.SetSelection(region);
    }

    void SetStatus(string title, string sub, Color titleColor)
    {
        _statusTitle.Text = title;
        _statusTitle.ForeColor = titleColor;
        _statusSub.Text = sub;
    }

    // ───────────── Opening GIFs ─────────────

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Effect = _recorder == null && !_busy && GifFromDrop(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override async void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (GifFromDrop(e) is { } path) await OpenGif(path);
    }

    static string? GifFromDrop(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && files[0].EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            ? files[0] : null;

    async Task OpenGifDialog()
    {
        if (_recorder != null || _busy || _countdownCts != null) return;
        using var dialog = new OpenFileDialog
        {
            Filter = L.T("GIF 이미지") + " (*.gif)|*.gif",
            Title = L.T("편집할 GIF 열기"),
            InitialDirectory = Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (WithoutTopMost(() => dialog.ShowDialog(this)) == DialogResult.OK) await OpenGif(dialog.FileName);
    }

    async Task OpenGif(string path)
    {
        if (_recorder != null || _busy || _countdownCts != null) return;
        _busy = true;
        SetControlsEnabled(false);
        _recordButton.Enabled = false;
        SetStatus(L.T("GIF 불러오는 중…"), Path.GetFileName(path), Theme.Text);
        var progress = new Progress<float>(p => _statusSub.Text = $"{Path.GetFileName(path)}  ·  {p * 100:0}%");
        try
        {
            var (store, delays) = await Task.Run(() => GifLoader.Load(path, progress, CancellationToken.None));
            try
            {
                OpenEditor(store, delays, path);
            }
            finally
            {
                store.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or OutOfMemoryException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            SetStatus(L.T("GIF를 열 수 없습니다"), ex.Message, Theme.Rec);
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
            _recordButton.Enabled = _hasTarget;
        }
    }

    // ───────────── Recording ─────────────

    async Task ToggleRecording()
    {
        if (_busy) return;
        if (_countdownCts != null)
            _countdownCts.Cancel();
        else if (_recorder == null)
            await StartRecording();
        else
            await StopRecording();
    }

    async Task StartRecording()
    {
        if (_targetWindow != IntPtr.Zero && !Native.IsWindow(_targetWindow))
        {
            ClearTarget();
            SetStatus(L.T("창이 닫혔습니다"), L.T("대상을 다시 선택하세요"), Theme.Rec);
            return;
        }
        _fps.Commit();
        _width.Commit();
        _height.Commit();
        _openFolder.Visible = _editLast.Visible = false;
        SetControlsEnabled(false);
        ShowFrame();
        SetCaptureHidden(true);

        if (_settings.Countdown && !await RunCountdown())
        {
            SetCaptureHidden(false);
            _frame?.SetRecording(false);
            SetControlsEnabled(true);
            ShowReadyStatus();
            return;
        }

        // A new recording replaces the one kept for "편집하기".
        _lastStore?.Dispose();
        _lastStore = null;
        _lastDelays = null;

        var track = Following ? _targetWindow : IntPtr.Zero;
        _keyTracker = _keys.Checked ? new KeyboardTracker() : null;
        var overlay = _clicks.Checked || _keyTracker != null ? new InputOverlay(_clicks.Checked, _keyTracker, DeviceDpi / 96f) : null;
        _shownFrame = null;
        _recorder = new Recorder(_region, track, _fps.Value, _cursor.Checked, overlay);
        _frame!.SetRecordingText("REC 00:00");
        _frame.SetRecording(true);
        _recorder.Start();

        _recordButton.Recording = true;
        _preview.Live = true;
        _preview.BadgeText = "REC 00:00";
        _uiTimer.Start();
        if (track != IntPtr.Zero) _trackTimer.Start();
        else _trackTimer.Stop();
        SetStatus(L.F("● 녹화 중  {0}", "00:00"), StopHint, Theme.Rec);
    }

    /// <returns>False when the countdown was cancelled.</returns>
    async Task<bool> RunCountdown()
    {
        using var cts = new CancellationTokenSource();
        _countdownCts = cts;
        _frame!.SetRecordingText(L.T("곧 시작"));
        _frame.SetRecording(true);
        try
        {
            for (int n = CountdownSeconds; n > 0; n--)
            {
                _recordButton.Countdown = n;
                _frame.SetCountdown(n);
                SetStatus(L.F("{0}초 뒤 시작", n), CancelHint, Theme.Rec);
                await Task.Delay(1000, cts.Token);
            }
            return true;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        finally
        {
            _countdownCts = null;
            _recordButton.Countdown = 0;
            _frame.SetCountdown(0);
        }
    }

    void SetControlsEnabled(bool enabled)
    {
        _regionCard.Enabled = _windowCard.Enabled = _screenCard.Enabled = _fpsPresets.Enabled = _fps.Enabled = _cursor.Enabled
            = _clicks.Enabled = _keys.Enabled = _openGifBtn.Enabled = _settingsBtn.Enabled = _presetBtn.Enabled = _afterRecord.Enabled = enabled;
        _width.Enabled = _height.Enabled = enabled && _hasTarget;
    }

    async Task StopRecording()
    {
        var recorder = _recorder!;
        _busy = true;
        _recordButton.Enabled = false;
        _uiTimer.Stop();
        _trackTimer.Stop();
        SetStatus(L.T("마무리 중…"), L.T("프레임을 정리하고 있습니다"), Theme.Text);

        await recorder.StopAsync();
        _keyTracker?.Dispose();
        _keyTracker = null;

        _recorder = null;
        _recordButton.Recording = false;
        _preview.Live = false;
        SetCaptureHidden(false);
        _frame?.SetRecording(false);
        if (recorder.Error == null && _mode != SelectMode.Screen) SetRegion(recorder.CurrentRegion);
        _frame?.SetSelection(_region);
        SetControlsEnabled(true);

        bool keepStore = false;
        try
        {
            if (recorder.Error != null)
            {
                SetStatus(L.T("녹화 실패"), recorder.Error.Message, Theme.Rec);
                WithoutTopMost(() => MessageBox.Show(this, recorder.Error.Message, L.T("녹화 오류"), MessageBoxButtons.OK, MessageBoxIcon.Error));
                return;
            }
            if (recorder.Frames.Count == 0)
            {
                SetStatus(L.T("저장할 프레임 없음"), L.T("창이 최소화돼 있었는지 확인하세요"), Theme.Rec);
                return;
            }
            if (_settings.QuickSave)
            {
                keepStore = true;
                _lastStore = recorder.Frames;
                _lastDelays = recorder.Delays.ToList();
                await QuickSave(_lastStore, _lastDelays);
            }
            else
            {
                OpenEditor(recorder.Frames, recorder.Delays, null);
            }
        }
        finally
        {
            if (!keepStore) recorder.Frames.Dispose();
            _busy = false;
            _recordButton.Enabled = true;
            UpdateIdleTracking();
        }
    }

    /// <summary>"바로 저장": encode with the last export settings and drop the file in the save folder.</summary>
    async Task QuickSave(FrameStore store, IReadOnlyList<int> delays)
    {
        var frames = delays.Select((d, i) => new EditFrame(i, Math.Max(10, d))).ToList();
        var s = _settings;
        var options = new ExportOptions(Rectangle.Empty, RotateFlipType.RotateNoneFlipNone, s.ScalePercent, s.Quality,
            s.LimitFps ? s.MaxFps : 0, s.MergeSimilar, s.PlayCount);
        var progress = new Progress<float>(p => _statusSub.Text = $"{p * 100:0}%");
        SetStatus(L.T("저장 중…"), "0%", Theme.Text);
        try
        {
            Directory.CreateDirectory(s.SaveFolder);
            var path = UniquePath(s.SaveFolder, $"GIF_{DateTime.Now:yyyyMMdd_HHmmss}");
            await Task.Run(() =>
            {
                var temp = path + ".part";
                try
                {
                    using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        GifExporter.Export(fs, store, frames, options, progress, CancellationToken.None);
                    File.Move(temp, path);
                }
                catch
                {
                    try { File.Delete(temp); } catch (IOException) { }
                    throw;
                }
            });
            _savedPath = path;
            var size = EditorForm.FormatSize(new FileInfo(path).Length);
            bool copied = s.CopyToClipboard && ClipboardHelper.CopyFile(path);
            SetStatus(L.T("저장 완료"), $"{size}  ·  " + (copied ? L.T("클립보드에 복사됨") : Path.GetFileName(path)), Theme.Success);
            _openFolder.Visible = _editLast.Visible = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(L.T("저장 실패"), ex.Message, Theme.Rec);
            _editLast.Visible = true;
        }
    }

    internal static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".gif");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}).gif");
        return path;
    }

    void OpenEditor(FrameStore store, IReadOnlyList<int> delays, string? sourcePath)
    {
        SetStatus(L.T("편집 중"), L.F("{0} 프레임 · 편집기에서 저장하세요", store.Count), Theme.Text);
        _frame?.Hide();
        TopMost = false;
        using (var editor = new EditorForm(store, delays, sourcePath))
        {
            editor.ShowDialog(this);
            if (editor.SavedPath != null)
            {
                _savedPath = editor.SavedPath;
                SetStatus(L.T("저장 완료"), $"{Path.GetFileName(_savedPath)}  ·  {EditorForm.FormatSize(editor.SavedBytes)}", Theme.Success);
                _openFolder.Visible = true;
            }
            else if (!_editLast.Visible)
            {
                SetStatus(L.T("저장 안 함"), _hasTarget ? RerecordHint : L.T("카드나 크기 프리셋으로 대상 선택"), Theme.Muted);
            }
        }
        TopMost = true;
        if (_frame != null && _hasTarget)
        {
            _frame.SetSelection(_region);
            _frame.Show();
        }
        Activate();
    }

    async Task OnUiTick()
    {
        if (_recorder is not { } r || _busy) return;
        if (r.Error != null)
        {
            await StopRecording();
            return;
        }

        var t = r.Elapsed;
        var clock = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        _statusTitle.Text = L.F("● 녹화 중  {0}", clock);
        _statusSub.Text = L.F("{0} 프레임", r.CapturedFrames) + "  ·  " + StopHint;
        _preview.BadgeText = $"REC {clock}";
        _frame?.SetRecordingText($"REC {clock}");

        if (r.LatestFrame is { } frame && !ReferenceEquals(frame, _shownFrame))
        {
            _shownFrame = frame;
            ReplacePreview(FrameOps.ToBitmap(frame, r.FrameSize));
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _countdownCts?.Cancel();
        if (_recorder is { } r)
        {
            _uiTimer.Stop();
            _trackTimer.Stop();
            r.StopAsync().Wait();
            r.Frames.Dispose();
        }
        _keyTracker?.Dispose();
        _lastStore?.Dispose();
        _frame?.Close();
        _settings.Save();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}

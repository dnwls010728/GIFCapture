using System.Collections.Concurrent;
using System.Diagnostics;

namespace GifCapture;

/// <param name="Stickers">Ids of the images, texts and mosaics shown on this frame, drawn in order; null = none.</param>
readonly record struct EditFrame(int Source, int DelayMs, int[]? Stickers = null);

/// <summary>Post-recording editor: browse, trim, retime, decorate, crop and rotate frames, tune export settings, then save.</summary>
sealed class EditorForm : Form
{
    sealed record Snapshot(EditFrame[] Frames, Rectangle Crop, int Angle, bool Flip, int Current, Watermark Watermark, Sticker[] Stickers);

    /// <summary>Undo steps kept; older ones are dropped.</summary>
    const int UndoLimit = 100;

    enum OutputFormat { Gif, PngSequence }

    readonly FrameStore _store;
    readonly Size _srcSize;
    readonly List<EditFrame> _frames;
    readonly HashSet<int> _sel = [];
    readonly Stack<Snapshot> _undo = new(), _redo = new();
    readonly AppSettings _settings = AppSettings.Current;
    Rectangle _crop;
    int _angle;      // clockwise rotation, applied first
    bool _flip;      // horizontal flip, applied after rotation
    int _current, _anchor;
    bool _dirty = true, _exporting, _applyingPreset;

    // Display. While playing, the next frames are decoded ahead on a worker so the UI thread only copies pixels.
    readonly Bitmap _view;
    int _viewSource = -1;
    readonly ConcurrentDictionary<int, Task<byte[]>> _prefetch = new();

    // Watermark & stickers. Images are only ever added, so undo never has to bring one back.
    Watermark _watermark;
    ImageAsset? _watermarkImage;
    string? _watermarkImagePath;
    readonly Dictionary<int, Sticker> _stickers = [];
    readonly Dictionary<int, ImageAsset> _images = [];
    int _nextId = 1;
    bool _syncingDecor;
    TextStyle _textStyle;     // look of the next text added
    int? _textBoxOwner;       // text item whose words the text box shows; null = a draft for the next one
    int _layerLayout = -1;    // which set of item controls is laid out (see LayoutItemControls)

    // Rapid edits of one kind (slider drags, typing) within a moment share an undo step.
    Snapshot? _mergeSnap;
    object? _mergeKey;
    readonly Stopwatch _mergeClock = new();

    // Thumbnails, generated in the background
    readonly ConcurrentDictionary<int, Bitmap> _thumbs = new();
    readonly ConcurrentStack<int> _thumbRequests = new();
    readonly ConcurrentDictionary<int, byte> _thumbQueued = new(); // keeps repaints from stacking duplicate requests
    readonly SemaphoreSlim _thumbSignal = new(0);
    readonly CancellationTokenSource _cts = new();
    Task? _thumbWorker;
    int _thumbRepaintPending;
    readonly Size _thumbBox;

    // Playback
    readonly System.Windows.Forms.Timer _playTimer = new() { Interval = 10 };
    readonly Stopwatch _playClock = new();
    long _nextAt;
    bool _playing;

    // File size estimate / target-size fitting
    readonly System.Windows.Forms.Timer _estimateTimer = new() { Interval = 600 };
    CancellationTokenSource? _estimateCts, _fitCts, _exportCts;
    string _estimateText = "";

    // Canvas & playback bar
    readonly EditorCanvas _canvas = new() { Dock = DockStyle.Fill };
    readonly Timeline _timeline = new() { Dock = DockStyle.Fill };
    readonly IconButton _prevBtn = new() { Glyph = Glyph.Prev };
    readonly IconButton _playBtn = new() { Glyph = Glyph.Play, Primary = true };
    readonly IconButton _nextBtn = new() { Glyph = Glyph.Next };
    readonly Label _posLabel = new() { ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _timeLabel = new() { ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _selLabel = new() { ForeColor = Theme.AccentHover, TextAlign = ContentAlignment.MiddleLeft };
    readonly PillButton _undoBtn = new() { Text = L.T("실행 취소") };
    readonly PillButton _redoBtn = new() { Text = L.T("다시 실행") };
    readonly PillButton _fitBtn = new() { Text = L.T("맞춤") };
    readonly PillButton _actualBtn = new() { Text = "100%" };
    readonly PillButton _keysBtn = new() { Text = L.T("단축키") };

    // Sidebar
    readonly Segmented _tabs = new() { Values = [0, 1, 2], Labels = [L.T("편집"), L.T("꾸미기"), L.T("내보내기")] };
    readonly Panel _editPanel = new() { BackColor = Theme.Bg, AutoScroll = true };
    readonly Panel _decorPanel = new() { BackColor = Theme.Bg, AutoScroll = true, Visible = false };
    readonly Panel _exportPanel = new() { BackColor = Theme.Bg, AutoScroll = true, Visible = false };

    readonly ToggleSwitch _wmOn = new() { Text = L.T("워터마크 넣기") };
    readonly Segmented _wmKind = new() { Values = [0, 1], Labels = [L.T("텍스트"), L.T("이미지")] };
    readonly TextBox _wmText = new();
    readonly PillButton _wmImageBtn = new() { Text = L.T("이미지 파일 선택") };
    readonly PillButton _wmFontBtn = new();
    readonly SwatchButton _wmColorBtn = new() { Text = L.T("색상") };
    readonly ThemedSlider _wmSize = new() { Text = L.T("크기"), Minimum = 1, Maximum = 60, Suffix = "%" };
    readonly ThemedSlider _wmOpacity = new() { Text = L.T("불투명도"), Minimum = 10, Maximum = 100, Step = 5, Suffix = "%" };
    readonly AnchorPicker _wmAnchor = new();
    readonly ToggleSwitch _wmOutline = new() { Text = L.T("외곽선") };
    readonly PillButton _stickerAddBtn = new() { Text = L.T("이미지 추가") };
    readonly PillButton _textAddBtn = new() { Text = L.T("텍스트 추가") };
    readonly PillButton _mosaicAddBtn = new() { Text = L.T("모자이크 추가") };
    readonly PillButton _stickerDeleteBtn = new() { Text = L.T("삭제"), Danger = true };
    readonly Label _pickLabel = new() { ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    readonly TextBox _textBox = new();
    readonly PillButton _textFontBtn = new();
    readonly SwatchButton _textColorBtn = new() { Text = L.T("색상") };
    readonly ThemedSlider _textSize = new() { Text = L.T("글자 크기"), Minimum = 2, Maximum = 30, Suffix = "%" };
    readonly ToggleSwitch _textOutline = new() { Text = L.T("외곽선") };
    readonly ToggleSwitch _textBackdrop = new() { Text = L.T("배경 상자") };
    readonly ThemedSlider _mosaicCell = new() { Text = L.T("칸 크기"), Minimum = 1, Maximum = 10 };
    readonly Label _layerHint = Theme.MutedLabel(L.T("텍스트는 두 번 클릭해 고치기 · 이미지는 Ctrl+V 붙여넣기, 끌어다 놓기 · Del 삭제"), Rectangle.Empty);
    readonly PillButton _stickerShowBtn = new() { Text = L.T("선택 프레임에도 표시") };
    readonly PillButton _stickerHideBtn = new() { Text = L.T("선택 프레임에서 빼기") };
    readonly ThemedSlider _stickerOpacity = new() { Text = L.T("불투명도"), Minimum = 10, Maximum = 100, Step = 5, Suffix = "%" };
    readonly ThemedSlider _stickerAngle = new() { Text = L.T("회전"), Minimum = -180, Maximum = 180, Suffix = "°" };

    readonly PillButton _deleteBtn = new() { Text = L.T("삭제"), Danger = true };
    readonly PillButton _keepBtn = new() { Text = L.T("선택만 남기기") };
    readonly PillButton _trimStartBtn = new() { Text = L.T("여기서 시작"), Danger = true };
    readonly PillButton _trimEndBtn = new() { Text = L.T("여기서 끝"), Danger = true };
    readonly PillButton _dupBtn = new() { Text = L.T("복제") };
    readonly PillButton _reverseBtn = new() { Text = L.T("역재생") };
    readonly PillButton _yoyoBtn = new() { Text = L.T("왕복 재생") };
    readonly NumberStepper _delayBox = new() { Minimum = GifExporter.MinDelayMs, Maximum = 10000, Step = 10, Suffix = "ms" };
    readonly PillButton _delayBtn = new() { Text = L.T("선택에 적용") };
    readonly PillButton _slowerBtn = new() { Text = L.T("느리게 ×0.5") };
    readonly PillButton _fasterBtn = new() { Text = L.T("빠르게 ×2") };
    readonly PillButton _cropBtn = new() { Text = L.T("자르기") };
    readonly PillButton _cropResetBtn = new() { Text = L.T("자르기 해제") };
    readonly PillButton _rotateBtn = new() { Text = L.T("90° 회전") };
    readonly PillButton _flipBtn = new() { Text = L.T("좌우 반전") };

    readonly Segmented _preset = new() { Values = [0, 1, 2, 3], Labels = [L.T("직접"), L.T("문서용"), L.T("SNS용"), L.T("게임용")] };
    readonly ThemedSlider _quality = new() { Text = L.T("화질"), Minimum = 10, Maximum = 100, Step = 5, Suffix = "%" };
    readonly ThemedSlider _scale = new() { Text = L.T("이미지 크기"), Minimum = 10, Maximum = 600, Step = 5, Suffix = "%",
        Describe = v => v > 100 ? $"×{v / 100.0:0.##}" : "" };
    readonly Segmented _scalePreset = new() { Values = [100, 200, 300, 400, 500, 600], Labels = ["×1", "×2", "×3", "×4", "×5", "×6"] };
    readonly Segmented _resample = new()
    {
        Values = [(int)ResampleMode.Sharp, (int)ResampleMode.Smooth, (int)ResampleMode.Crisp, (int)ResampleMode.Smart],
        Labels = [L.T("그대로"), L.T("부드럽게"), L.T("선명하게"), L.T("픽셀아트")],
    };
    readonly ToggleSwitch _limitFps = new() { Text = L.T("초당 프레임 제한") };
    readonly NumberStepper _maxFps = new() { Minimum = 1, Maximum = 50, Suffix = "fps" };
    readonly ToggleSwitch _merge = new() { Text = L.T("비슷한 프레임 합치기") };
    readonly NumberStepper _playCount = new() { Minimum = 0, Maximum = 99, Suffix = L.T("회"), ZeroText = L.T("무한 반복") };
    readonly NumberStepper _targetMb = new() { Minimum = 1, Maximum = 200, Suffix = "MB" };
    readonly PillButton _fitBtnSize = new() { Text = L.T("이 용량에 맞추기") };
    readonly Segmented _format = new() { Values = [0, 1], Labels = ["GIF", L.T("PNG 시퀀스")] };
    readonly Label _folderLabel = new() { ForeColor = Theme.Muted, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly TextBox _nameBox = new();
    readonly Label _extLabel = Theme.MutedLabel(".gif", Rectangle.Empty);
    readonly PillButton _folderBtn = new() { Text = L.T("폴더 변경") };

    readonly Label _statusLabel = new() { ForeColor = Theme.Muted };
    readonly PillButton _openFileBtn = new() { Text = L.T("파일 열기"), Visible = false };
    readonly PillButton _openFolderBtn = new() { Text = L.T("폴더 열기"), Visible = false };
    readonly PillButton _copyBtn = new() { Text = L.T("복사"), Visible = false };
    readonly PillButton _exportBtn = new() { Text = L.T("GIF 저장"), Accent = true };
    readonly ToolTip _tips = Theme.CreateToolTip();

    public string? SavedPath { get; private set; }
    public long SavedBytes { get; private set; }

    OutputFormat Format => _format.Value == 1 ? OutputFormat.PngSequence : OutputFormat.Gif;

    /// <param name="sourceName">File the frames came from, if any; used for the window title and default file name.</param>
    public EditorForm(FrameStore store, IReadOnlyList<int> delays, string? sourceName = null)
    {
        _store = store;
        _srcSize = store.FrameSize;
        _frames = Enumerable.Range(0, store.Count).Select(i => new EditFrame(i, Math.Max(GifExporter.MinDelayMs, i < delays.Count ? delays[i] : 100))).ToList();
        _watermark = _settings.Watermark;
        _textStyle = _settings.TextStyle;
        _view = new Bitmap(_srcSize.Width, _srcSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        _thumbBox = new Size((int)(220 * DeviceDpi / 96f), (int)(124 * DeviceDpi / 96f));

        BuildLayout();
        Text = sourceName == null ? L.T("GIF 편집") : $"{L.T("GIF 편집")} — {Path.GetFileName(sourceName)}";
        _nameBox.Text = sourceName == null ? $"GIF_{DateTime.Now:yyyyMMdd_HHmmss}" : Path.GetFileNameWithoutExtension(sourceName) + L.T("_편집");

        _applyingPreset = true;
        _quality.Value = _settings.Quality;
        _scale.Value = _settings.ScalePercent;
        _scalePreset.ShowValue(_scale.Value);
        _resample.Value = (int)_settings.Resample;
        _resample.Enabled = _scale.Value > 100;
        _limitFps.Checked = _settings.LimitFps;
        _maxFps.Value = _settings.MaxFps;
        _maxFps.Enabled = _limitFps.Checked;
        _merge.Checked = _settings.MergeSimilar;
        _playCount.Value = _settings.PlayCount;
        _targetMb.Value = _settings.TargetSizeMb;
        _preset.Value = 0;
        _applyingPreset = false;
        _folderLabel.Text = _settings.SaveFolder;
        _quality.Describe = q => q >= 90 ? L.T("선명") : q >= 60 ? L.T("보통") : L.T("작은 용량");

        WireEvents();

        _canvas.Frame = _view;
        _canvas.OutputScale = _scale.Value / 100f;
        _canvas.Resample = (ResampleMode)_resample.Value;
        _timeline.Aspect = (float)_srcSize.Width / _srcSize.Height;
        _timeline.ThumbnailFor = ThumbnailAt;
        _timeline.DelayFor = i => _frames[i].DelayMs;
        _timeline.HasText = i => HasKind(i, StickerKind.Text);
        _timeline.HasImages = i => HasKind(i, StickerKind.Image);
        _timeline.HasMosaic = i => HasKind(i, StickerKind.Mosaic);
        _timeline.Selection = _sel;
        _sel.Add(0);

        LoadWatermarkImage(quiet: true);
        SyncWatermarkControls();
        RefreshAll();
    }

    void BuildLayout()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Theme.AppIcon;
        KeyPreview = true;
        ClientSize = new Size(1200, 840);
        // Short screens still fit: the sidebar tabs scroll when the window is lower than their content.
        MinimumSize = new Size(1060, 700);

        // Bottom: playback bar over the timeline.
        const int barW = 1200 - 284;
        // Panels get their final design size up front: anchored children measure their margins from it.
        var bottom = new Panel { Dock = DockStyle.Bottom, Width = barW, Height = 210, BackColor = Theme.Bg };
        var playbar = new Panel { Dock = DockStyle.Top, Width = barW, Height = 56, BackColor = Theme.Bg };
        var topLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border };
        _prevBtn.Bounds = new Rectangle(16, 10, 38, 36);
        _playBtn.Bounds = new Rectangle(60, 10, 38, 36);
        _nextBtn.Bounds = new Rectangle(104, 10, 38, 36);
        _posLabel.Bounds = new Rectangle(154, 10, 96, 36);
        _posLabel.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _timeLabel.Bounds = new Rectangle(250, 10, 132, 36);
        _selLabel.Bounds = new Rectangle(382, 10, 92, 36);
        _selLabel.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _undoBtn.Bounds = new Rectangle(476, 10, 96, 36);
        _redoBtn.Bounds = new Rectangle(578, 10, 96, 36);
        _keysBtn.Bounds = new Rectangle(barW - 16 - 76, 10, 76, 36);
        _actualBtn.Bounds = new Rectangle(_keysBtn.Left - 8 - 60, 10, 60, 36);
        _fitBtn.Bounds = new Rectangle(_actualBtn.Left - 6 - 60, 10, 60, 36);
        foreach (var c in new Control[] { _keysBtn, _actualBtn, _fitBtn })
            c.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        playbar.Controls.AddRange([_prevBtn, _playBtn, _nextBtn, _posLabel, _timeLabel, _selLabel, _undoBtn, _redoBtn, _fitBtn, _actualBtn, _keysBtn]);
        bottom.Controls.Add(_timeline);
        bottom.Controls.Add(playbar);
        bottom.Controls.Add(topLine);

        // Right: tabbed tools sidebar with the save area pinned to the bottom.
        var side = new Panel { Dock = DockStyle.Right, Width = 284, Height = 840, BackColor = Theme.Bg };
        var sideLine = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Theme.Border };
        side.Controls.Add(sideLine);

        const int L0 = 20, R = 146, W = 118, Full = 244, H = 840;
        _tabs.Bounds = new Rectangle(L0, 14, Full, 38);
        _tabs.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        side.Controls.Add(_tabs);

        foreach (var p in new[] { _editPanel, _decorPanel, _exportPanel })
        {
            p.Bounds = new Rectangle(1, 64, 283, H - 64 - 160);
            p.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            p.HandleCreated += (_, _) => Native.ApplyDarkScrollBars(p.Handle);
            side.Controls.Add(p);
        }

        // Edit tab
        var ep = _editPanel;
        ep.Controls.Add(Theme.SectionLabel(L.T("프레임"), new Rectangle(L0 - 1, 4, Full, 24)));
        Place(ep, _deleteBtn, L0, 32, W); Place(ep, _keepBtn, R, 32, W);
        Place(ep, _trimStartBtn, L0, 74, W); Place(ep, _trimEndBtn, R, 74, W);
        Place(ep, _dupBtn, L0, 116, W); Place(ep, _reverseBtn, R, 116, W);
        Place(ep, _yoyoBtn, L0, 158, W);
        ep.Controls.Add(Theme.SectionLabel(L.T("재생 시간"), new Rectangle(L0 - 1, 206, Full, 24)));
        Place(ep, _delayBox, L0, 234, W); Place(ep, _delayBtn, R, 234, W);
        Place(ep, _slowerBtn, L0, 276, W); Place(ep, _fasterBtn, R, 276, W);
        ep.Controls.Add(Theme.SectionLabel(L.T("자르기 · 회전"), new Rectangle(L0 - 1, 324, Full, 24)));
        Place(ep, _cropBtn, L0, 352, W); Place(ep, _cropResetBtn, R, 352, W);
        Place(ep, _rotateBtn, L0, 394, W); Place(ep, _flipBtn, R, 394, W);

        // Decorate tab: watermark on every frame, images on chosen frames
        var dp = _decorPanel;
        dp.Controls.Add(Theme.SectionLabel(L.T("워터마크"), new Rectangle(L0 - 1, 4, Full, 24)));
        _wmOn.Bounds = new Rectangle(L0, 30, Full, 30);
        _wmKind.Bounds = new Rectangle(L0, 66, Full, 32);
        Theme.StyleTextBox(_wmText);
        _wmText.Bounds = new Rectangle(L0, 106, Full, 30);
        _wmText.PlaceholderText = L.T("워터마크 문구 입력");
        _wmImageBtn.Bounds = new Rectangle(L0, 104, Full, 36);
        _wmFontBtn.Bounds = new Rectangle(L0, 146, W, 36);
        _wmColorBtn.Bounds = new Rectangle(R, 146, W, 36);
        _wmSize.Bounds = new Rectangle(L0, 188, Full, 50);
        _wmOpacity.Bounds = new Rectangle(L0, 242, Full, 50);
        _wmAnchor.Bounds = new Rectangle(L0, 300, 96, 72);
        _wmOutline.Bounds = new Rectangle(L0 + 108, 300, Full - 108, 30);
        var wmHint = Theme.MutedLabel(L.T("미리보기에서 끌어 옮기기"), new Rectangle(L0 + 108, 330, Full - 108, 44));
        wmHint.TextAlign = ContentAlignment.TopLeft;
        dp.Controls.AddRange([_wmOn, _wmKind, _wmText, _wmImageBtn, _wmFontBtn, _wmColorBtn, _wmSize, _wmOpacity, _wmAnchor, _wmOutline, wmHint]);

        // Items on chosen frames. Only the rows for what is picked are shown; LayoutItemControls stacks them.
        dp.Controls.Add(Theme.SectionLabel(L.T("텍스트 · 이미지 · 모자이크"), new Rectangle(L0 - 1, 382, Full, 24)));
        Place(dp, _textAddBtn, L0, 408, W); Place(dp, _stickerAddBtn, R, 408, W);
        Place(dp, _mosaicAddBtn, L0, 450, W); Place(dp, _stickerDeleteBtn, R, 450, W);
        Theme.StyleTextBox(_textBox);
        _textBox.PlaceholderText = L.T("텍스트 입력 후 Enter");
        _layerHint.TextAlign = ContentAlignment.TopLeft;
        dp.Controls.AddRange([_pickLabel, _textBox, _textFontBtn, _textColorBtn, _textSize, _textOutline, _textBackdrop, _mosaicCell,
            _stickerOpacity, _stickerAngle, _stickerShowBtn, _stickerHideBtn, _layerHint]);
        LayoutItemControls(null);

        // Export tab
        var xp = _exportPanel;
        xp.Controls.Add(Theme.SectionLabel(L.T("용도"), new Rectangle(L0 - 1, 4, Full, 24)));
        _preset.Bounds = new Rectangle(L0, 30, Full, 34);
        xp.Controls.Add(_preset);
        _quality.Bounds = new Rectangle(L0, 74, Full, 50);
        _scale.Bounds = new Rectangle(L0, 128, Full, 50);
        _scalePreset.Bounds = new Rectangle(L0, 182, Full, 32);
        _resample.Bounds = new Rectangle(L0, 220, Full, 32);
        _limitFps.Bounds = new Rectangle(L0, 264, 150, 34);
        _maxFps.Bounds = new Rectangle(L0 + 152, 264, Full - 152, 34);
        _merge.Bounds = new Rectangle(L0, 304, Full, 30);
        var playLabel = Theme.MutedLabel(L.T("반복"), new Rectangle(L0, 340, 90, 34));
        _playCount.Bounds = new Rectangle(L0 + 92, 340, Full - 92, 34);
        xp.Controls.AddRange([_quality, _scale, _scalePreset, _resample, _limitFps, _maxFps, _merge, playLabel, _playCount]);

        xp.Controls.Add(Theme.SectionLabel(L.T("목표 용량"), new Rectangle(L0 - 1, 386, Full, 24)));
        _targetMb.Bounds = new Rectangle(L0, 412, 104, 36);
        _fitBtnSize.Bounds = new Rectangle(L0 + 112, 412, Full - 112, 36);
        xp.Controls.AddRange([_targetMb, _fitBtnSize]);

        xp.Controls.Add(Theme.SectionLabel(L.T("저장"), new Rectangle(L0 - 1, 462, Full, 24)));
        _format.Bounds = new Rectangle(L0, 488, Full, 34);
        _folderLabel.Bounds = new Rectangle(L0, 528, Full - 96, 34);
        _folderBtn.Bounds = new Rectangle(L0 + Full - 90, 528, 90, 34);
        Theme.StyleTextBox(_nameBox);
        _nameBox.Bounds = new Rectangle(L0, 568, Full - 44, 30);
        _extLabel.Bounds = new Rectangle(L0 + Full - 42, 568, 42, 30);
        xp.Controls.Add(_folderBtn);
        xp.Controls.AddRange([_format, _folderLabel, _nameBox, _extLabel]);

        // Pinned save area
        _statusLabel.Bounds = new Rectangle(L0, H - 152, Full, 44);
        int third = (Full - 12) / 3;
        _openFileBtn.Bounds = new Rectangle(L0, H - 104, third, 36);
        _openFolderBtn.Bounds = new Rectangle(L0 + third + 6, H - 104, third, 36);
        _copyBtn.Bounds = new Rectangle(L0 + (third + 6) * 2, H - 104, third, 36);
        _exportBtn.Bounds = new Rectangle(L0, H - 60, Full, 46);
        _exportBtn.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        foreach (var c in new Control[] { _statusLabel, _openFileBtn, _openFolderBtn, _copyBtn, _exportBtn })
        {
            c.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            side.Controls.Add(c);
        }

        // Dock order: the fill control first, edges after (edges added later claim space first).
        Controls.Add(_canvas);
        Controls.Add(bottom);
        Controls.Add(side);
        ResumeLayout(false);

        _tips.SetToolTip(_prevBtn, L.T("이전 프레임 (←)"));
        _tips.SetToolTip(_playBtn, L.T("재생 / 일시정지 (Space)"));
        _tips.SetToolTip(_nextBtn, L.T("다음 프레임 (→)"));
        _tips.SetToolTip(_undoBtn, "Ctrl+Z");
        _tips.SetToolTip(_redoBtn, "Ctrl+Y");
        _tips.SetToolTip(_fitBtn, L.T("창에 맞추기 (Ctrl+0)"));
        _tips.SetToolTip(_actualBtn, L.T("실제 크기 (Ctrl+1). 미리보기에서 휠로 확대/축소, 드래그로 이동"));
        _tips.SetToolTip(_keysBtn, L.T("Space  재생 / 일시정지\n←  →  이전 / 다음 프레임   (Shift: 선택 확장)\nHome  End  처음 / 끝\nShift·Ctrl + 클릭, 드래그  여러 개 선택\nCtrl+A  전체 선택     Esc  선택 해제\nDel  삭제 (미리보기에서 고른 항목이 있으면 그 항목)\nCtrl+D  복제     Ctrl+V  이미지 붙여넣기\nCtrl+Z / Ctrl+Y  실행 취소 / 다시 실행\n휠  확대/축소     Ctrl+0  맞춤     Ctrl+1  100%\nCtrl+S  저장     Ctrl+Shift+C  복사"));
        _tips.SetToolTip(_deleteBtn, L.T("선택한 프레임 삭제 (Del)"));
        _tips.SetToolTip(_keepBtn, L.T("선택하지 않은 프레임을 모두 삭제"));
        _tips.SetToolTip(_trimStartBtn, L.T("현재 프레임보다 앞의 프레임을 모두 삭제"));
        _tips.SetToolTip(_trimEndBtn, L.T("현재 프레임보다 뒤의 프레임을 모두 삭제"));
        _tips.SetToolTip(_dupBtn, L.T("선택한 프레임을 바로 뒤에 복제 (Ctrl+D)"));
        _tips.SetToolTip(_reverseBtn, L.T("재생 순서를 뒤집습니다.\n여러 개 선택 시 선택 범위만."));
        _tips.SetToolTip(_yoyoBtn, L.T("끝까지 재생한 뒤 거꾸로 되돌아오도록 프레임을 추가합니다."));
        _tips.SetToolTip(_delayBox, L.T("프레임 하나가 보이는 시간. 휠, ↑↓, 숫자 입력 후 Enter."));
        _tips.SetToolTip(_delayBtn, L.T("선택한 프레임에 표시 시간 적용 (Enter)"));
        _tips.SetToolTip(_slowerBtn, L.T("표시 시간을 두 배로.\n여러 개 선택 시 선택 범위만."));
        _tips.SetToolTip(_fasterBtn, L.T("표시 시간을 절반으로.\n여러 개 선택 시 선택 범위만."));
        _tips.SetToolTip(_cropBtn, L.T("미리보기에서 드래그해 남길 영역을 지정합니다."));
        _tips.SetToolTip(_preset, L.T("문서용: 작은 용량 · 10fps\nSNS용: 가로 1280px 이하 · 15fps\n게임용: 높은 화질 · 30fps"));
        _tips.SetToolTip(_quality, L.T("낮추면 색 수를 줄이고 미세한 변화를 무시해 용량이 줄어듭니다."));
        _tips.SetToolTip(_scale, L.T("내보낼 GIF의 가로·세로 비율. 줄이면 용량이 크게 줄어듭니다."));
        _tips.SetToolTip(_scalePreset, L.T("배율로 확대합니다. 미리보기에서 Ctrl+1로 실제 크기를 확인하세요."));
        _tips.SetToolTip(_resample, L.T("확대할 때 픽셀을 그리는 방식.\n그대로: 각 픽셀을 선명한 사각형으로\n부드럽게: 경계를 부드럽게 보간\n선명하게: 보간한 뒤 윤곽을 또렷하게 (화면 녹화·글자에 추천)\n픽셀아트: 도트 그림의 계단진 대각선과 곡선을 매끄럽게"));
        _tips.SetToolTip(_limitFps, L.T("초당 프레임 수를 제한해 용량을 줄입니다. 재생 시간은 그대로."));
        _tips.SetToolTip(_merge, L.T("거의 똑같은 연속 프레임을 하나로 합칩니다. 재생 시간은 그대로."));
        _tips.SetToolTip(_playCount, L.T("GIF가 재생되는 횟수. 0이면 무한 반복."));
        _tips.SetToolTip(_fitBtnSize, L.T("화질과 크기를 단계적으로 낮춰 목표 용량 이하가 되는 설정을 찾습니다."));
        _tips.SetToolTip(_format, L.T("PNG 시퀀스: 프레임마다 PNG 파일과 표시 시간 CSV를 폴더에 저장 (게임 엔진용)"));
        _tips.SetToolTip(_folderBtn, L.T("저장할 폴더를 고릅니다."));
        _tips.SetToolTip(_copyBtn, L.T("저장한 파일을 클립보드에 복사 (Ctrl+Shift+C). 메신저에 Ctrl+V로 붙여넣기."));
        _tips.SetToolTip(_exportBtn, L.T("저장 위치에 바로 저장 (Ctrl+S). 저장 중에 다시 누르면 취소합니다."));
        _tips.SetToolTip(_wmOn, L.T("모든 프레임에 같은 워터마크를 넣습니다. 설정은 다음에도 유지됩니다."));
        _tips.SetToolTip(_wmFontBtn, L.T("워터마크 글꼴과 굵기를 고릅니다."));
        _tips.SetToolTip(_wmColorBtn, L.T("워터마크 글자 색을 고릅니다."));
        _tips.SetToolTip(_wmSize, L.T("텍스트: 글자 높이 (화면 높이 대비)\n이미지: 너비 (화면 너비 대비)"));
        _tips.SetToolTip(_wmAnchor, L.T("워터마크를 붙일 위치. 고르면 끌어 옮긴 위치는 초기화됩니다.\n미리보기에서 끌어 옮기고 모서리 핸들로 크기를 바꿀 수 있습니다."));
        _tips.SetToolTip(_wmOutline, L.T("글자 둘레에 어두운 테두리를 그려 어떤 배경에서도 잘 보이게 합니다."));
        _tips.SetToolTip(_stickerAddBtn, L.T("이미지 파일을 골라 선택한 프레임에 넣습니다."));
        _tips.SetToolTip(_textAddBtn, L.T("선택한 프레임 아래쪽에 텍스트를 넣습니다. 자막도 이것으로 넣습니다.\n미리보기에서 옮기고, 크기를 바꾸고, 돌릴 수 있습니다."));
        _tips.SetToolTip(_mosaicAddBtn, L.T("선택한 프레임의 일부를 모자이크로 가립니다.\n미리보기에서 옮기고 모서리 핸들로 영역을 바꿉니다."));
        _tips.SetToolTip(_stickerDeleteBtn, L.T("고른 항목을 모든 프레임에서 지웁니다 (Del)."));
        _tips.SetToolTip(_stickerShowBtn, L.T("고른 항목을 타임라인에서 선택한 프레임에도 표시합니다."));
        _tips.SetToolTip(_stickerAngle, L.T("고른 항목을 돌립니다. 미리보기에서 위쪽 동그란 핸들을 끌어도 되고, Shift를 누르면 15° 단위로 맞춰집니다."));
        _tips.SetToolTip(_stickerHideBtn, L.T("고른 항목을 타임라인에서 선택한 프레임에서만 뺍니다."));
        _tips.SetToolTip(_textBox, L.T("고른 텍스트를 고칩니다. 아무것도 고르지 않았으면 Enter로 새 텍스트를 넣습니다.\n미리보기에서 텍스트를 두 번 클릭해도 고칠 수 있습니다."));
        _tips.SetToolTip(_textFontBtn, L.T("텍스트 글꼴과 굵기를 고릅니다."));
        _tips.SetToolTip(_textColorBtn, L.T("텍스트 색을 고릅니다."));
        _tips.SetToolTip(_textSize, L.T("글자 높이 (화면 높이 대비). 미리보기에서 모서리 핸들을 끌어도 됩니다."));
        _tips.SetToolTip(_textBackdrop, L.T("글자 뒤에 어두운 상자를 깔아 자막처럼 보이게 합니다."));
        _tips.SetToolTip(_textOutline, L.T("글자 둘레에 어두운 테두리를 그려 어떤 배경에서도 잘 보이게 합니다."));
        _tips.SetToolTip(_mosaicCell, L.T("모자이크 한 칸의 크기. 클수록 알아보기 어렵습니다."));
        _tips.SetToolTip(_cropResetBtn, L.T("자르기를 없애고 전체 화면으로 되돌립니다."));
        _tips.SetToolTip(_rotateBtn, L.T("시계 방향으로 90° 돌립니다."));
        _tips.SetToolTip(_flipBtn, L.T("좌우를 뒤집습니다."));
    }

    /// <summary>Keeps the whole output in view while the user tunes enlargement, so the preview does not jump around.</summary>
    void FitPreview()
    {
        if (_applyingPreset || _canvas.CropMode || _canvas.IsFit) return;
        _canvas.ZoomToFit();
    }

    static void Place(Control parent, Control c, int x, int y, int w)
    {
        c.Bounds = new Rectangle(x, y, w, 36);
        parent.Controls.Add(c);
    }

    void WireEvents()
    {
        _timeline.ItemPressed += OnItemPressed;
        _timeline.ItemDragged += i =>
        {
            SelectRange(_anchor, i);
            _current = i;
            ShowCurrent();
        };
        _prevBtn.Click += (_, _) => Step(-1, false);
        _nextBtn.Click += (_, _) => Step(1, false);
        _playBtn.Click += (_, _) => TogglePlay();
        _playTimer.Tick += (_, _) => PlayTick();
        _undoBtn.Click += (_, _) => Undo();
        _redoBtn.Click += (_, _) => Redo();
        _fitBtn.Click += (_, _) => _canvas.ZoomToFit();
        _actualBtn.Click += (_, _) => _canvas.ZoomTo(1f);
        _keysBtn.Click += (_, _) =>
        {
            // Open the full list above the button; hovering shows the same text.
            var text = _tips.GetToolTip(_keysBtn);
            using var font = new Font("Segoe UI", 9f);
            int h = TextRenderer.MeasureText(text, font, new Size(320, 0), TextFormatFlags.WordBreak).Height + 18;
            _tips.Show(text, _keysBtn, _keysBtn.Width - 340, -h - 6, 12000);
        };
        _canvas.ZoomChanged += (_, _) => Notify(_canvas.IsFit ? L.T("창에 맞춤") : L.F("확대 {0}%", _canvas.ZoomPercent), durationMs: 900);

        _tabs.ValueChanged += (_, _) =>
        {
            if (_canvas.CropMode) CommitCrop();
            _editPanel.Visible = _tabs.Value == 0;
            _decorPanel.Visible = _tabs.Value == 1;
            _exportPanel.Visible = _tabs.Value == 2;
        };

        _wmOn.CheckedChanged += (_, _) => SetWatermark(WatermarkFromControls());
        _wmKind.ValueChanged += (_, _) =>
        {
            // Text is sized by height and images by width, so each kind starts from its own sensible size.
            bool image = _wmKind.Value == 1;
            SetWatermark(WatermarkFromControls() with { SizePercent = image ? 20 : 6, OffsetX = 0, OffsetY = 0 });
        };
        _wmText.TextChanged += (_, _) => SetWatermark(WatermarkFromControls());
        _wmSize.ValueChanged += (_, _) => SetWatermark(WatermarkFromControls());
        _wmOpacity.ValueChanged += (_, _) => SetWatermark(WatermarkFromControls());
        _wmOutline.CheckedChanged += (_, _) => SetWatermark(WatermarkFromControls());
        _wmAnchor.ValueChanged += (_, _) => SetWatermark(WatermarkFromControls() with { OffsetX = 0, OffsetY = 0 });
        _wmFontBtn.Click += (_, _) => ChooseWatermarkFont();
        _wmColorBtn.Click += (_, _) => ChooseWatermarkColor();
        _wmImageBtn.Click += (_, _) => ChooseWatermarkImage();
        _stickerAddBtn.Click += (_, _) => { if (PickImage(L.T("넣을 이미지")) is { } a) AddSticker(a); };
        _textAddBtn.Click += (_, _) => AddText(_textBoxOwner == null ? _textBox.Text : "");
        _mosaicAddBtn.Click += (_, _) => AddMosaic();
        _textBox.TextChanged += (_, _) =>
        {
            if (_syncingDecor || _textBoxOwner is not { } id || !_stickers.TryGetValue(id, out var s)) return;
            MergedEdit(("text", id), () => _stickers[id] = s with { Text = _textBox.Text });
        };
        _textFontBtn.Click += (_, _) => ChooseTextFont();
        _textColorBtn.Click += (_, _) => ChooseTextColor();
        _textSize.ValueChanged += (_, _) => SetTextStyle(CurrentTextStyle() with { Size = _textSize.Value / 100f });
        _textOutline.CheckedChanged += (_, _) => SetTextStyle(CurrentTextStyle() with { Outline = _textOutline.Checked });
        _textBackdrop.CheckedChanged += (_, _) => SetTextStyle(CurrentTextStyle() with { Box = _textBackdrop.Checked });
        _mosaicCell.ValueChanged += (_, _) =>
        {
            if (_syncingDecor || Picked is not { Kind: StickerKind.Mosaic } s) return;
            MergedEdit(("cell", s.Id), () => _stickers[s.Id] = s with { Cell = _mosaicCell.Value });
        };
        _canvas.StickerActivated += id =>
        {
            if (!_stickers.TryGetValue(id, out var s) || s.Kind != StickerKind.Text) return;
            _textBox.Focus();
            _textBox.SelectAll();
        };
        _stickerDeleteBtn.Click += (_, _) => { if (_canvas.SelectedSticker is { } id) DeleteSticker(id); };
        _stickerShowBtn.Click += (_, _) => { if (_canvas.SelectedSticker is { } id) ShowStickerOnSelection(id); };
        _stickerHideBtn.Click += (_, _) => { if (_canvas.SelectedSticker is { } id) HideStickerFromSelection(id); };
        _stickerOpacity.ValueChanged += (_, _) =>
        {
            if (_syncingDecor || _canvas.SelectedSticker is not { } id || !_stickers.TryGetValue(id, out var s)) return;
            MergedEdit(("opacity", id), () => _stickers[id] = s with { Opacity = _stickerOpacity.Value });
        };
        _stickerAngle.ValueChanged += (_, _) =>
        {
            if (_syncingDecor || _canvas.SelectedSticker is not { } id || !_stickers.TryGetValue(id, out var s)) return;
            MergedEdit(("angle", id), () => _stickers[id] = s with { Angle = _stickerAngle.Value });
        };
        _canvas.DecorSelected += (sticker, watermark) =>
        {
            if (sticker != null || watermark) _tabs.Value = 1; // bring the controls for what was picked into view
            UpdateDecorControls();
        };
        _canvas.StickerEdited += s =>
        {
            if (s.Kind == StickerKind.Text) _textStyle = _settings.TextStyle = s.Style; // a resized text sets the size of the next
            Apply(L.F("{0} 위치·크기·각도 바꿈", KindName(s.Kind)), () => { _stickers[s.Id] = s; return true; });
        };
        _canvas.WatermarkEdited += w =>
        {
            SetWatermark(w);
            SyncWatermarkControls();
        };
        _canvas.AllowDrop = true;
        _canvas.DragEnter += (_, e) => e.Effect = DroppedImagePath(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        _canvas.DragDrop += (_, e) =>
        {
            if (DroppedImagePath(e.Data) is not { } path) return;
            if (DecorSet.TryLoad(path) is { } asset) AddSticker(asset);
            else Notify(L.T("이미지를 열 수 없습니다"), error: true);
        };

        _deleteBtn.Click += (_, _) => DeleteSelected();
        _keepBtn.Click += (_, _) => KeepSelected();
        _trimStartBtn.Click += (_, _) => TrimStart();
        _trimEndBtn.Click += (_, _) => TrimEnd();
        _dupBtn.Click += (_, _) => Duplicate();
        _reverseBtn.Click += (_, _) => Reverse();
        _yoyoBtn.Click += (_, _) => Yoyo();
        _delayBtn.Click += (_, _) => { _delayBox.Commit(); SetDelay(_delayBox.Value); };
        _slowerBtn.Click += (_, _) => ChangeSpeed(0.5);
        _fasterBtn.Click += (_, _) => ChangeSpeed(2);
        _cropBtn.Click += (_, _) => { if (_canvas.CropMode) CommitCrop(); else EnterCropMode(); };
        _cropResetBtn.Click += (_, _) => { if (_canvas.CropMode) CancelCrop(); else ResetCrop(); };
        _rotateBtn.Click += (_, _) => Transform(L.T("90° 회전"), () => _angle = (_angle + (_flip ? 270 : 90)) % 360);
        _flipBtn.Click += (_, _) => Transform(L.T("좌우 반전"), () => _flip = !_flip);
        _canvas.EditCropChanged += (_, _) => UpdateSummary();

        _preset.ValueChanged += (_, _) => ApplyExportPreset(_preset.Value);
        _quality.ValueChanged += (_, _) => OptionsChanged();
        _scale.ValueChanged += (_, _) =>
        {
            _scalePreset.ShowValue(_scale.Value);
            _resample.Enabled = _scale.Value > 100;
            OptionsChanged();
            FitPreview();
        };
        _scalePreset.ValueChanged += (_, _) => { if (_scalePreset.Value != 0) _scale.Value = _scalePreset.Value; };
        _resample.ValueChanged += (_, _) => { OptionsChanged(keepPreset: true); FitPreview(); };
        _limitFps.CheckedChanged += (_, _) => { _maxFps.Enabled = _limitFps.Checked; OptionsChanged(); };
        _maxFps.ValueChanged += (_, _) => OptionsChanged();
        _merge.CheckedChanged += (_, _) => OptionsChanged();
        _playCount.ValueChanged += (_, _) => OptionsChanged(keepPreset: true);
        _targetMb.ValueChanged += (_, _) => _settings.TargetSizeMb = _targetMb.Value;
        _fitBtnSize.Click += async (_, _) => await FitToTargetSize();
        _format.ValueChanged += (_, _) =>
        {
            _extLabel.Text = Format == OutputFormat.Gif ? ".gif" : L.T("폴더");
            _exportBtn.Text = Format == OutputFormat.Gif ? L.T("GIF 저장") : L.T("PNG 저장");
            OptionsChanged(keepPreset: true);
        };
        _folderBtn.Click += (_, _) => ChooseFolder();
        _estimateTimer.Tick += (_, _) => { _estimateTimer.Stop(); StartEstimate(); };

        _exportBtn.Click += async (_, _) => await Export();
        _openFileBtn.Click += (_, _) =>
        {
            if (SavedPath != null && (File.Exists(SavedPath) || Directory.Exists(SavedPath)))
                Process.Start(new ProcessStartInfo(SavedPath) { UseShellExecute = true });
        };
        _openFolderBtn.Click += (_, _) =>
        {
            if (SavedPath != null && (File.Exists(SavedPath) || Directory.Exists(SavedPath)))
                Process.Start("explorer.exe", $"/select,\"{SavedPath}\"");
        };
        _copyBtn.Click += (_, _) => CopySaved();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyDarkTitleBar(Handle, Theme.Bg);
        // Started only now: the workers repaint through BeginInvoke, which needs a window handle.
        _thumbWorker ??= Task.Run(() => ThumbLoop(_cts.Token));
        ScheduleEstimate();
    }

    // ───────────── Selection & navigation ─────────────

    List<int> Targets() => (_sel.Count > 0 ? _sel.Where(i => i < _frames.Count) : [_current]).Order().ToList();

    /// <summary>Positions an operation should touch: the selection when it has several frames, otherwise everything.</summary>
    List<int> RangeOrAll()
    {
        var t = Targets();
        return t.Count > 1 ? t : Enumerable.Range(0, _frames.Count).ToList();
    }

    void SelectRange(int a, int b)
    {
        _sel.Clear();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _sel.Add(i);
    }

    void SelectAll()
    {
        SelectRange(0, _frames.Count - 1);
        ShowCurrent();
    }

    void OnItemPressed(int i, Keys mods)
    {
        StopPlayback();
        if (_canvas.CropMode) CommitCrop();
        if ((mods & Keys.Shift) != 0)
        {
            SelectRange(_anchor, i);
        }
        else if ((mods & Keys.Control) != 0)
        {
            if (!_sel.Remove(i)) _sel.Add(i);
            _anchor = i;
        }
        else
        {
            _sel.Clear();
            _sel.Add(i);
            _anchor = i;
        }
        _current = i;
        ShowCurrent();
    }

    void Step(int delta, bool extend)
    {
        StopPlayback();
        _current = Math.Clamp(_current + delta, 0, _frames.Count - 1);
        if (extend)
        {
            SelectRange(_anchor, _current);
        }
        else
        {
            _sel.Clear();
            _sel.Add(_current);
            _anchor = _current;
        }
        ShowCurrent();
    }

    void Jump(int index, bool extend) => Step(index - _current, extend);

    void ShowCurrent()
    {
        if (_frames.Count == 0) return;
        _current = Math.Clamp(_current, 0, _frames.Count - 1);
        int src = _frames[_current].Source;
        if (src != _viewSource)
        {
            var px = _prefetch.TryRemove(src, out var ready) ? ready.GetAwaiter().GetResult() : _store.Read(src);
            FrameOps.CopyInto(_view, px);
            _viewSource = src;
            _canvas.FrameUpdated();
        }
        if (_playing) Prefetch();
        _canvas.FrameStickers = _frames[_current].Stickers;
        _canvas.Invalidate();
        _timeline.Current = _current;
        _timeline.EnsureVisible(_current);
        _timeline.Invalidate();

        long before = 0, total = 0;
        for (int i = 0; i < _frames.Count; i++)
        {
            if (i < _current) before += _frames[i].DelayMs;
            total += _frames[i].DelayMs;
        }
        _posLabel.Text = $"{_current + 1} / {_frames.Count}";
        _timeLabel.Text = L.F("{0:0.00} / {1:0.00}초", before / 1000.0, total / 1000.0);
        if (!_playing && !_delayBox.IsEditing) _delayBox.Value = _frames[_current].DelayMs;

        int count = Targets().Count;
        bool multi = count > 1;
        _deleteBtn.Text = multi ? L.F("삭제 ({0})", count) : L.T("삭제");
        _selLabel.Text = multi ? L.F("{0}개 선택", count) : "";
        UpdateDecorControls();
    }

    /// <summary>Starts decoding the next couple of frames in playback order, dropping ones that are no longer near.</summary>
    void Prefetch()
    {
        var wanted = new HashSet<int>();
        for (int k = 1; k <= 2 && _frames.Count > 1; k++) wanted.Add(_frames[(_current + k) % _frames.Count].Source);
        foreach (var src in _prefetch.Keys)
            if (!wanted.Contains(src)) _prefetch.TryRemove(src, out _);
        foreach (var src in wanted)
            if (src != _viewSource) _prefetch.GetOrAdd(src, s => Task.Run(() => _store.Read(s)));
    }

    void RefreshAll()
    {
        _sel.RemoveWhere(i => i >= _frames.Count);
        _current = Math.Clamp(_current, 0, Math.Max(0, _frames.Count - 1));
        _anchor = Math.Clamp(_anchor, 0, Math.Max(0, _frames.Count - 1));
        _timeline.Count = _frames.Count;
        _canvas.Crop = _crop;
        _canvas.Rotate = RotateType;
        _undoBtn.Enabled = _undo.Count > 0;
        _redoBtn.Enabled = _redo.Count > 0;
        _cropResetBtn.Enabled = _canvas.CropMode || !_crop.IsEmpty;
        _canvas.Decor = CurrentDecor();
        ShowCurrent();
        UpdateSummary();
        ScheduleEstimate();
    }

    RotateFlipType RotateType => (_angle, _flip) switch
    {
        (90, false) => RotateFlipType.Rotate90FlipNone,
        (180, false) => RotateFlipType.Rotate180FlipNone,
        (270, false) => RotateFlipType.Rotate270FlipNone,
        (0, true) => RotateFlipType.RotateNoneFlipX,
        (90, true) => RotateFlipType.Rotate90FlipX,
        (180, true) => RotateFlipType.Rotate180FlipX,
        (270, true) => RotateFlipType.Rotate270FlipX,
        _ => RotateFlipType.RotateNoneFlipNone,
    };

    ExportOptions CurrentOptions(Rectangle? crop = null) => new(
        crop ?? _crop, RotateType, _scale.Value, _quality.Value,
        _limitFps.Checked ? _maxFps.Value : 0, _merge.Checked, _playCount.Value, (ResampleMode)_resample.Value, CurrentDecor());

    /// <summary>Copies of the decoration state, safe to hand to background estimates and exports.</summary>
    DecorSet CurrentDecor() => new(_watermark, _watermarkImage, new Dictionary<int, Sticker>(_stickers), new Dictionary<int, ImageAsset>(_images));

    /// <summary>Bottom-of-sidebar summary: output size, length and estimated file size.</summary>
    void UpdateSummary()
    {
        if (SavedPath != null && !_dirty) return; // keep the "saved" message until something changes
        long total = _frames.Sum(f => (long)f.DelayMs);
        var crop = _canvas.CropMode ? _canvas.EditCrop : _crop;
        var size = CurrentOptions(crop).OutputSize(_srcSize);
        SetStatus(L.F("{0} × {1} px  ·  {2} 프레임  ·  {3:0.0}초", size.Width, size.Height, _frames.Count, total / 1000.0) + "\n" + _estimateText);
    }

    void SetStatus(string text, Color? color = null)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color ?? Theme.Muted;
    }

    /// <summary>Short-lived message over the preview, so feedback appears where the user is looking.</summary>
    void Notify(string text, bool undoable = false, bool error = false, int durationMs = 2600) =>
        _canvas.ShowToast(undoable ? $"{text}    {L.T("Ctrl+Z 되돌리기")}" : text, error, durationMs);

    void OptionsChanged(bool keepPreset = false)
    {
        if (!_applyingPreset && !keepPreset && _preset.Value != 0) _preset.ShowValue(0);
        _dirty = true;
        _settings.Quality = _quality.Value;
        _settings.ScalePercent = _scale.Value;
        _settings.Resample = (ResampleMode)_resample.Value;
        _canvas.OutputScale = _scale.Value / 100f;
        _canvas.Resample = (ResampleMode)_resample.Value;
        _settings.LimitFps = _limitFps.Checked;
        _settings.MaxFps = _maxFps.Value;
        _settings.MergeSimilar = _merge.Checked;
        _settings.PlayCount = _playCount.Value;
        if (_applyingPreset) return;
        UpdateSummary();
        ScheduleEstimate();
    }

    /// <summary>Use-case presets from the export tab: documents, social posts, game footage.</summary>
    void ApplyExportPreset(int preset)
    {
        if (preset == 0) return;
        var b = _crop.IsEmpty ? _srcSize : _crop.Size;
        int longSide = ExportOptions.SwapsAxes(RotateType) ? b.Height : b.Width;
        _applyingPreset = true;
        switch (preset)
        {
            case 1: // 문서용: small files, plenty for UI walkthroughs and bug reports
                _quality.Value = 70; _scale.Value = 100; _limitFps.Checked = true; _maxFps.Value = 10; _merge.Checked = true;
                break;
            case 2: // SNS용: at most 1280px wide, smooth enough for a feed
                _quality.Value = 80;
                _scale.Value = Math.Clamp(longSide <= 1280 ? 100 : 1280 * 100 / longSide / 5 * 5, 10, 100);
                _limitFps.Checked = true; _maxFps.Value = 15; _merge.Checked = true;
                break;
            case 3: // 게임용: keep motion and colour
                _quality.Value = 95; _scale.Value = 100; _limitFps.Checked = true; _maxFps.Value = 30; _merge.Checked = false;
                break;
        }
        _maxFps.Enabled = _limitFps.Checked;
        _applyingPreset = false;
        OptionsChanged(keepPreset: true);
        Notify(L.F("{0} 설정 적용", _preset.Labels![preset]));
    }

    // ───────────── File size estimate ─────────────

    void ScheduleEstimate()
    {
        if (!IsHandleCreated) return;
        _estimateCts?.Cancel();
        _estimateText = Format == OutputFormat.Gif ? L.T("예상 크기 계산 중…") : "";
        UpdateSummary();
        _estimateTimer.Stop();
        if (Format == OutputFormat.Gif) _estimateTimer.Start();
    }

    void StartEstimate()
    {
        if (_exporting || _fitCts != null) return;
        _estimateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        var frames = _frames.ToArray();
        var options = CurrentOptions();
        var progress = new Progress<float>(p =>
        {
            if (cts.IsCancellationRequested) return;
            _estimateText = $"{L.T("예상 크기 계산 중…")}  {p * 100:0}%";
            UpdateSummary();
        });
        Task.Run(() => Measure(frames, options, progress, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || IsDisposed) return;
            _estimateText = t.IsCompletedSuccessfully ? L.F("예상 크기  약 {0}", FormatSize(t.Result)) : "";
            UpdateSummary();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    long Measure(EditFrame[] frames, ExportOptions options, IProgress<float>? progress, CancellationToken ct)
    {
        var counter = new CountingStream();
        GifExporter.Export(counter, _store, frames, options, progress, ct);
        return counter.Length;
    }

    /// <summary>Lowers quality, then size, until the GIF fits under the target. Click again to stop.</summary>
    async Task FitToTargetSize()
    {
        if (_fitCts != null)
        {
            _fitCts.Cancel();
            return;
        }
        if (_exporting) return;
        _targetMb.Commit();
        long target = _targetMb.Value * 1024L * 1024;
        _estimateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _fitCts = cts;
        _fitBtnSize.Text = L.T("맞추는 중… (취소)");

        int q0 = _quality.Value, s0 = _scale.Value;
        var steps = new List<(int Q, int S)> { (q0, s0) };
        foreach (var q in new[] { 70, 50 }) if (q < q0) steps.Add((q, s0));
        int qLow = Math.Min(q0, 50);
        foreach (var s in new[] { 500, 400, 300, 200, 100, 90, 75, 60, 50, 40, 30, 25 }) if (s < s0) steps.Add((qLow, s));
        steps.Add((Math.Min(qLow, 35), Math.Min(s0, 25)));
        steps.Add((Math.Min(qLow, 20), Math.Min(s0, 20)));

        var frames = _frames.ToArray();
        var baseOptions = CurrentOptions() with { MergeSimilar = true };
        (int Q, int S, long Bytes)? found = null;
        long lastBytes = 0;
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var (q, s) = steps[i];
                _estimateText = L.F("목표 용량 맞추는 중  ({0}/{1})", i + 1, steps.Count);
                UpdateSummary();
                var opts = baseOptions with { Quality = q, ScalePercent = s };
                lastBytes = await Task.Run(() => Measure(frames, opts, null, cts.Token), cts.Token);
                if (lastBytes <= target) { found = (q, s, lastBytes); break; }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _fitCts = null;
            _fitBtnSize.Text = L.T("이 용량에 맞추기");
        }

        if (cts.IsCancellationRequested)
        {
            Notify(L.T("용량 맞추기 취소"));
            ScheduleEstimate();
            return;
        }
        if (found is { } f)
        {
            _applyingPreset = true;
            _quality.Value = f.Q;
            _scale.Value = f.S;
            _merge.Checked = true;
            _applyingPreset = false;
            OptionsChanged();
            _estimateTimer.Stop();
            _estimateText = L.F("예상 크기  약 {0}", FormatSize(f.Bytes));
            UpdateSummary();
            Notify(L.F("화질 {0}% · 크기 {1}%로 {2}에 맞춤", f.Q, f.S, FormatSize(f.Bytes)));
        }
        else
        {
            Notify(L.F("가장 작게 해도 {0}입니다. 프레임을 줄이거나 잘라 보세요", FormatSize(lastBytes)), error: true, durationMs: 4500);
            ScheduleEstimate();
        }
    }

    // ───────────── Edit operations ─────────────

    Snapshot TakeSnapshot() => new(_frames.ToArray(), _crop, _angle, _flip, _current, _watermark, _stickers.Values.ToArray());

    void PushUndo(Snapshot before)
    {
        _undo.Push(before);
        _redo.Clear();
        if (_undo.Count <= UndoLimit + 20) return; // trim in batches; rebuilding the stack is not free
        var keep = _undo.Take(UndoLimit).Reverse().ToArray();
        _undo.Clear();
        foreach (var s in keep) _undo.Push(s);
    }

    /// <summary>Pushes an undo step unless the last one was the same kind of edit (<paramref name="key"/>) moments ago.</summary>
    void MergedEdit(object key, Action mutate)
    {
        StopPlayback();
        bool merge = _mergeSnap != null && _undo.TryPeek(out var top) && top == _mergeSnap
            && Equals(key, _mergeKey) && _mergeClock.ElapsedMilliseconds < 1500;
        if (!merge)
        {
            _mergeSnap = TakeSnapshot();
            _mergeKey = key;
            PushUndo(_mergeSnap);
        }
        _mergeClock.Restart();
        mutate();
        _dirty = true;
        RefreshAll();
    }

    void Apply(string done, Func<bool> mutate)
    {
        StopPlayback();
        if (_canvas.CropMode) CommitCrop();
        var snap = TakeSnapshot();
        if (!mutate()) return;
        Commit(snap, done);
    }

    void Commit(Snapshot before, string done)
    {
        PushUndo(before);
        _dirty = true;
        Notify(done, undoable: true);
        RefreshAll();
    }

    void Restore(Snapshot s)
    {
        _frames.Clear();
        _frames.AddRange(s.Frames);
        _crop = s.Crop;
        _angle = s.Angle;
        _flip = s.Flip;
        _current = s.Current;
        _watermark = _settings.Watermark = s.Watermark;
        _stickers.Clear();
        foreach (var st in s.Stickers) _stickers[st.Id] = st;
        LoadWatermarkImage(quiet: true);
        SyncWatermarkControls();
        _sel.Clear();
        _sel.Add(Math.Clamp(_current, 0, _frames.Count - 1));
        _anchor = _current;
        _dirty = true;
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        StopPlayback();
        if (_canvas.CropMode) CancelCrop();
        _redo.Push(TakeSnapshot());
        Restore(_undo.Pop());
        Notify(L.T("실행 취소함"));
        RefreshAll();
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        StopPlayback();
        if (_canvas.CropMode) CancelCrop();
        _undo.Push(TakeSnapshot());
        Restore(_redo.Pop());
        Notify(L.T("다시 실행함"));
        RefreshAll();
    }

    void SelectOnly(int i)
    {
        _sel.Clear();
        _sel.Add(i);
        _anchor = _current = i;
    }

    void DeleteSelected()
    {
        var t = Targets();
        if (t.Count >= _frames.Count)
        {
            Notify(L.T("모든 프레임을 지울 수는 없습니다"), error: true);
            return;
        }
        Apply(L.F("{0}개 프레임 삭제", t.Count), () =>
        {
            for (int k = t.Count - 1; k >= 0; k--) _frames.RemoveAt(t[k]);
            SelectOnly(Math.Min(t[0], _frames.Count - 1));
            return true;
        });
    }

    void KeepSelected()
    {
        var t = Targets();
        if (t.Count >= _frames.Count)
        {
            Notify(L.T("남길 프레임을 먼저 선택하세요 (Shift·Ctrl+클릭)"), error: true);
            return;
        }
        Apply(L.F("선택한 {0}개만 남김", t.Count), () =>
        {
            var keep = t.Select(i => _frames[i]).ToList();
            _frames.Clear();
            _frames.AddRange(keep);
            SelectOnly(0);
            return true;
        });
    }

    void TrimStart()
    {
        if (_current == 0) { Notify(L.T("첫 프레임입니다. 앞에 지울 프레임이 없습니다"), error: true); return; }
        int n = _current;
        Apply(L.F("앞쪽 {0}개 프레임 삭제", n), () =>
        {
            _frames.RemoveRange(0, n);
            SelectOnly(0);
            return true;
        });
    }

    void TrimEnd()
    {
        int n = _frames.Count - 1 - _current;
        if (n <= 0) { Notify(L.T("마지막 프레임입니다. 뒤에 지울 프레임이 없습니다"), error: true); return; }
        Apply(L.F("뒤쪽 {0}개 프레임 삭제", n), () =>
        {
            _frames.RemoveRange(_current + 1, n);
            SelectOnly(_frames.Count - 1);
            return true;
        });
    }

    void Duplicate()
    {
        var t = Targets();
        Apply(L.F("{0}개 프레임 복제", t.Count), () =>
        {
            int insertAt = t[^1] + 1;
            _frames.InsertRange(insertAt, t.Select(i => _frames[i]).ToList());
            SelectRange(insertAt, insertAt + t.Count - 1);
            _anchor = insertAt;
            _current = insertAt;
            return true;
        });
    }

    void Reverse()
    {
        var range = RangeOrAll();
        if (range.Count < 2) return;
        Apply(range.Count == _frames.Count ? L.T("전체 순서 뒤집음") : L.F("선택한 {0}개 순서 뒤집음", range.Count), () =>
        {
            var values = range.Select(i => _frames[i]).Reverse().ToList();
            for (int k = 0; k < range.Count; k++) _frames[range[k]] = values[k];
            return true;
        });
    }

    void Yoyo()
    {
        if (_frames.Count < 3)
        {
            Notify(L.T("왕복 재생은 프레임이 3개 이상일 때 쓸 수 있습니다"), error: true);
            return;
        }
        Apply(L.T("왕복 재생 프레임 추가"), () =>
        {
            _frames.AddRange(_frames.Skip(1).Take(_frames.Count - 2).Reverse().ToList());
            return true;
        });
    }

    void SetDelay(int ms)
    {
        var t = Targets();
        Apply(L.F("{0}개 프레임 표시 시간 {1}ms", t.Count, ms), () =>
        {
            foreach (var i in t) _frames[i] = _frames[i] with { DelayMs = ms };
            return true;
        });
    }

    void ChangeSpeed(double factor)
    {
        var range = RangeOrAll();
        Apply(factor > 1 ? L.T("2배 빠르게") : L.T("2배 느리게"), () =>
        {
            foreach (var i in range)
                _frames[i] = _frames[i] with { DelayMs = Math.Clamp((int)Math.Round(_frames[i].DelayMs / factor), GifExporter.MinDelayMs, 60000) };
            return true;
        });
    }

    void Transform(string done, Action change)
    {
        Apply(done, () =>
        {
            change();
            return true;
        });
    }

    // ───────────── Watermark ─────────────

    Watermark WatermarkFromControls() => _watermark with
    {
        Enabled = _wmOn.Checked,
        IsImage = _wmKind.Value == 1,
        Text = _wmText.Text,
        SizePercent = _wmSize.Value,
        Opacity = _wmOpacity.Value,
        Outline = _wmOutline.Checked,
        Anchor = _wmAnchor.Value,
    };

    void SetWatermark(Watermark w)
    {
        if (_syncingDecor || w == _watermark) return;
        MergedEdit("watermark", () =>
        {
            _watermark = _settings.Watermark = w;
            LoadWatermarkImage(quiet: false);
        });
        SyncWatermarkControls();
    }

    /// <summary>Loads the watermark image when its file changes. An unreadable file is reported (unless quiet) and left out.</summary>
    void LoadWatermarkImage(bool quiet)
    {
        var path = _watermark.IsImage ? _watermark.ImagePath : null;
        if (path == _watermarkImagePath) return;
        _watermarkImagePath = path;
        _watermarkImage = path == null ? null : DecorSet.TryLoad(path);
        if (path != null && _watermarkImage == null && !quiet) Notify(L.T("워터마크 이미지를 열 수 없습니다"), error: true);
    }

    /// <summary>Shows the watermark in the controls without the changes counting as edits.</summary>
    void SyncWatermarkControls()
    {
        _syncingDecor = true;
        var w = _watermark;
        _wmOn.Checked = w.Enabled;
        _wmKind.Value = w.IsImage ? 1 : 0;
        if (_wmText.Text != w.Text) _wmText.Text = w.Text;
        _wmSize.Value = w.SizePercent;
        _wmOpacity.Value = w.Opacity;
        _wmOutline.Checked = w.Outline;
        _wmAnchor.Value = w.Anchor;
        _wmColorBtn.Color = Color.FromArgb(w.ColorArgb);
        _wmFontBtn.Text = w.FontFamily;
        _wmImageBtn.Text = w.ImagePath != null ? Path.GetFileName(w.ImagePath) : L.T("이미지 파일 선택");
        _tips.SetToolTip(_wmImageBtn, w.ImagePath ?? L.T("로고 같은 이미지를 고릅니다. 투명 PNG가 잘 어울립니다."));

        _wmText.Visible = _wmFontBtn.Visible = _wmColorBtn.Visible = _wmOutline.Visible = !w.IsImage;
        _wmImageBtn.Visible = w.IsImage;
        foreach (var c in new Control[] { _wmKind, _wmText, _wmImageBtn, _wmFontBtn, _wmColorBtn, _wmSize, _wmOpacity, _wmAnchor, _wmOutline })
            c.Enabled = w.Enabled;
        _syncingDecor = false;
    }

    void ChooseWatermarkFont()
    {
        using var current = new Font(_watermark.FontFamily, 14f, _watermark.FontStyle);
        using var dialog = new FontDialog { Font = current, ShowEffects = false, FontMustExist = true, AllowVerticalFonts = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetWatermark(_watermark with { FontFamily = dialog.Font.FontFamily.Name, FontStyle = dialog.Font.Style & (FontStyle.Bold | FontStyle.Italic) });
    }

    void ChooseWatermarkColor()
    {
        using var dialog = new ColorDialog { Color = Color.FromArgb(_watermark.ColorArgb), FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetWatermark(_watermark with { ColorArgb = Color.FromArgb(255, dialog.Color).ToArgb() });
    }

    void ChooseWatermarkImage()
    {
        using var dialog = new OpenFileDialog { Title = L.T("워터마크 이미지"), Filter = ImageFilter };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (DecorSet.TryLoad(dialog.FileName) == null)
        {
            Notify(L.T("이미지를 열 수 없습니다"), error: true);
            return;
        }
        SetWatermark(_watermark with { ImagePath = dialog.FileName, Enabled = true });
    }

    // ───────────── Stickers ─────────────

    static string ImageFilter => L.T("이미지") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" + L.T("모든 파일") + "|*.*";

    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    static string? DroppedImagePath(IDataObject? data) =>
        data?.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            : null;

    ImageAsset? PickImage(string title)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = ImageFilter };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        var asset = DecorSet.TryLoad(dialog.FileName);
        if (asset == null) Notify(L.T("이미지를 열 수 없습니다"), error: true);
        return asset;
    }

    void PasteImage()
    {
        ImageAsset? asset = null;
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                using (image) asset = ImageAsset.FromImage(image);
            else if (DroppedImagePath(Clipboard.GetDataObject()) is { } path)
                asset = DecorSet.TryLoad(path);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            // Clipboard held by another app.
        }
        if (asset != null) AddSticker(asset);
        else Notify(L.T("클립보드에 이미지가 없습니다"), error: true);
    }

    /// <summary>Places a new image in the middle of the selected frames, at its own size if that fits.</summary>
    void AddSticker(ImageAsset asset)
    {
        var output = CurrentOptions().OutputSize(_srcSize);
        float width = Math.Min(asset.Size.Width / (float)output.Width, 0.4f);
        width = Math.Min(width, 0.6f * output.Height / (asset.Aspect * output.Width)); // keep tall images inside the frame too
        int imageId = _nextId++;
        _images[imageId] = asset;
        AddItem(new Sticker(_nextId++, imageId, 0.5f, 0.5f, Math.Max(width, 0.02f)));
    }

    /// <summary>Puts text along the bottom of the selected frames, where a caption goes, in the last look used.</summary>
    void AddText(string text)
    {
        text = text.Trim();
        bool placeholder = text.Length == 0;
        if (placeholder) text = L.T("텍스트");
        var output = new RectangleF(PointF.Empty, CurrentOptions().OutputSize(_srcSize));
        var sticker = new Sticker(_nextId++, 0, 0.5f, 0.5f, 0) { Kind = StickerKind.Text, Text = text, Style = _textStyle };
        float h = Decor.StickerRect(sticker, DecorSet.Empty, output)!.Value.Height / output.Height;
        AddItem(sticker with { Y = Math.Max(h / 2, 1 - h / 2 - 0.04f) });
        // Straight on to typing: a placeholder is selected so the first key replaces it.
        _textBox.Focus();
        if (placeholder) _textBox.SelectAll();
        else _textBox.Select(_textBox.TextLength, 0);
    }

    /// <summary>Covers a patch in the middle of the selected frames, to be dragged over whatever should not be seen.</summary>
    void AddMosaic()
    {
        var output = CurrentOptions().OutputSize(_srcSize);
        float w = 0.3f, h = Math.Min(w * output.Width / output.Height, 0.6f); // square where it fits
        AddItem(new Sticker(_nextId++, 0, 0.5f, 0.5f, w) { Kind = StickerKind.Mosaic, Height = h });
    }

    void AddItem(Sticker sticker)
    {
        var t = Targets();
        Apply(L.F("{0}개 프레임에 {1} 넣음", t.Count, KindName(sticker.Kind)), () =>
        {
            _stickers[sticker.Id] = sticker;
            foreach (var i in t) _frames[i] = _frames[i] with { Stickers = [.. _frames[i].Stickers ?? [], sticker.Id] };
            return true;
        });
        _tabs.Value = 1;
        _canvas.SelectedSticker = sticker.Id;
        UpdateDecorControls();
    }

    /// <summary>Kind of item as a word used mid-sentence (lower case where the language has case).</summary>
    static string KindName(StickerKind kind) => (kind switch
    {
        StickerKind.Text => L.T("텍스트"),
        StickerKind.Mosaic => L.T("모자이크"),
        _ => L.T("이미지"),
    }).ToLowerInvariant();

    bool HasKind(int position, StickerKind kind) =>
        _frames[position].Stickers?.Any(id => _stickers.TryGetValue(id, out var s) && s.Kind == kind) == true;

    /// <summary>The item picked in the preview, if it still exists.</summary>
    Sticker? Picked => _canvas.SelectedSticker is { } id && _stickers.TryGetValue(id, out var s) ? s : null;

    void DeleteSticker(int id)
    {
        if (!_stickers.TryGetValue(id, out var sticker)) return;
        Apply(L.F("{0} 삭제", KindName(sticker.Kind)), () =>
        {
            _stickers.Remove(id);
            for (int i = 0; i < _frames.Count; i++)
                if (_frames[i].Stickers?.Contains(id) == true) _frames[i] = _frames[i] with { Stickers = Without(_frames[i].Stickers!, id) };
            return true;
        });
        _canvas.SelectedSticker = null;
        UpdateDecorControls();
    }

    void ShowStickerOnSelection(int id)
    {
        var t = Targets().Where(i => _frames[i].Stickers?.Contains(id) != true).ToList();
        if (t.Count == 0) { Notify(L.T("선택한 프레임에 이미 표시되어 있습니다"), error: true); return; }
        Apply(L.F("{0}개 프레임에 {1} 표시", t.Count, KindName(_stickers[id].Kind)), () =>
        {
            foreach (var i in t) _frames[i] = _frames[i] with { Stickers = [.. _frames[i].Stickers ?? [], id] };
            return true;
        });
    }

    void HideStickerFromSelection(int id)
    {
        var name = KindName(_stickers[id].Kind);
        var t = Targets().Where(i => _frames[i].Stickers?.Contains(id) == true).ToList();
        if (t.Count == 0) { Notify(L.F("선택한 프레임에는 이 {0}가 없습니다", name), error: true); return; }
        Apply(L.F("{0}개 프레임에서 {1} 뺌", t.Count, name), () =>
        {
            foreach (var i in t) _frames[i] = _frames[i] with { Stickers = Without(_frames[i].Stickers!, id) };
            if (!_frames.Any(f => f.Stickers?.Contains(id) == true)) _stickers.Remove(id); // shown nowhere: gone
            return true;
        });
    }

    static int[]? Without(int[] ids, int id)
    {
        var rest = ids.Where(x => x != id).ToArray();
        return rest.Length == 0 ? null : rest;
    }

    // ───────────── Text look ─────────────

    /// <summary>Look the text controls edit: the picked text's, or with none picked, the next new text's.</summary>
    TextStyle CurrentTextStyle() => Picked is { Kind: StickerKind.Text } s ? s.Style : _textStyle;

    void SetTextStyle(TextStyle style)
    {
        if (_syncingDecor) return;
        _textStyle = _settings.TextStyle = style; // new text follows the look last chosen
        if (Picked is { Kind: StickerKind.Text } s && s.Style != style)
            MergedEdit(("style", s.Id), () => _stickers[s.Id] = s with { Style = style });
        else UpdateDecorControls();
    }

    void ChooseTextFont()
    {
        var style = CurrentTextStyle();
        using var current = new Font(style.FontFamily, 14f, style.FontStyle);
        using var dialog = new FontDialog { Font = current, ShowEffects = false, FontMustExist = true, AllowVerticalFonts = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetTextStyle(style with { FontFamily = dialog.Font.FontFamily.Name, FontStyle = dialog.Font.Style & (FontStyle.Bold | FontStyle.Italic) });
    }

    void ChooseTextColor()
    {
        var style = CurrentTextStyle();
        using var dialog = new ColorDialog { Color = Color.FromArgb(style.ColorArgb), FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetTextStyle(style with { ColorArgb = Color.FromArgb(255, dialog.Color).ToArgb() });
    }

    /// <summary>Enter in the text box: adds the typed text, or with a text picked, finishes editing it.</summary>
    void CommitText()
    {
        if (_textBoxOwner == null)
        {
            if (_textBox.Text.Trim().Length == 0) { Notify(L.T("넣을 텍스트를 입력하세요"), error: true); return; }
            AddText(_textBox.Text);
            return;
        }
        _canvas.SelectedSticker = null;
        UpdateDecorControls();
    }

    /// <summary>Shows the picked item in the item controls, with only the rows that apply to its kind.</summary>
    void UpdateDecorControls()
    {
        // An item not on the frame being shown can't be seen or dragged, so it doesn't stay picked.
        var picked = Picked;
        if (picked != null && (_frames.Count == 0 || _frames[_current].Stickers?.Contains(picked.Id) != true)) picked = null;
        if (picked == null && _canvas.SelectedSticker != null) _canvas.SelectedSticker = null;
        var text = picked is { Kind: StickerKind.Text } ? picked : null;

        _syncingDecor = true;
        _stickerDeleteBtn.Enabled = _stickerShowBtn.Enabled = _stickerHideBtn.Enabled = picked != null;
        _pickLabel.Text = picked == null ? L.T("미리보기에서 클릭해 고르기") : L.F("고른 항목  ·  {0}", KindName(picked.Kind));
        _pickLabel.ForeColor = picked == null ? Theme.Muted : Theme.AccentHover;
        _stickerOpacity.Value = picked?.Opacity ?? 100;
        _stickerAngle.Value = (int)Math.Round(picked?.Angle ?? 0);
        // The box keeps a draft for the next text while nothing is picked; undo can change a picked text's words.
        if (text?.Id != _textBoxOwner || (text != null && _textBox.Text != text.Text))
        {
            _textBox.Text = text?.Text ?? "";
            _textBox.Select(_textBox.TextLength, 0);
            _textBoxOwner = text?.Id;
        }
        var style = text?.Style ?? _textStyle;
        _textFontBtn.Text = style.FontFamily;
        _textColorBtn.Color = Color.FromArgb(style.ColorArgb);
        _textSize.Value = Math.Clamp((int)Math.Round(style.Size * 100), _textSize.Minimum, _textSize.Maximum);
        _textOutline.Checked = style.Outline;
        _textBackdrop.Checked = style.Box;
        if (picked is { Kind: StickerKind.Mosaic }) _mosaicCell.Value = picked.Cell;
        _syncingDecor = false;
        LayoutItemControls(picked?.Kind);
    }

    /// <summary>
    /// Stacks the rows for the picked item under the add buttons: text settings for a text (or, with nothing picked, for
    /// the next text), the cell size for a mosaic, then the settings every item has.
    /// </summary>
    void LayoutItemControls(StickerKind? kind)
    {
        int key = kind is { } k ? (int)k : -1;
        if (key == _layerLayout) return;
        _layerLayout = key;
        bool text = kind is null or StickerKind.Text, mosaic = kind == StickerKind.Mosaic, any = kind != null;

        // Design units, scaled the way the form scaled everything else; the panel may be scrolled.
        float s = _textAddBtn.Height / 36f;
        int top = _decorPanel.AutoScrollPosition.Y;
        const int L0 = 20, R = 146, W = 118, Full = 244;
        int y = 494;
        void Put(Control c, int x, int w, int h)
        {
            c.Bounds = new Rectangle((int)Math.Round(x * s), (int)Math.Round(y * s) + top, (int)Math.Round(w * s), (int)Math.Round(h * s));
            c.Visible = true;
        }
        foreach (var c in new Control[] { _textBox, _textFontBtn, _textColorBtn, _textSize, _textOutline, _textBackdrop, _mosaicCell,
            _stickerOpacity, _stickerAngle, _stickerShowBtn, _stickerHideBtn })
            c.Visible = false;

        Put(_pickLabel, L0, Full, 24); y += 30;
        if (text)
        {
            Put(_textBox, L0, Full, 30); y += 38;
            Put(_textFontBtn, L0, W, 36); Put(_textColorBtn, R, W, 36); y += 42;
            Put(_textSize, L0, Full, 50); y += 54;
            Put(_textBackdrop, L0, W, 30); Put(_textOutline, R, W, 30); y += 38;
        }
        if (mosaic) { Put(_mosaicCell, L0, Full, 50); y += 54; }
        if (any)
        {
            Put(_stickerOpacity, L0, W, 50); Put(_stickerAngle, R, W, 50); y += 54;
            Put(_stickerShowBtn, L0, Full, 36); y += 42;
            Put(_stickerHideBtn, L0, Full, 36); y += 42;
        }
        Put(_layerHint, L0, Full, 40);
    }

    // ───────────── Crop ─────────────

    void EnterCropMode()
    {
        StopPlayback();
        _canvas.CropMode = true;
        _cropBtn.Text = L.T("자르기 적용");
        _cropResetBtn.Text = L.T("취소");
        _cropResetBtn.Enabled = true;
        Notify(L.T("드래그해 남길 영역 지정  ·  Enter 적용  ·  Esc 취소"), durationMs: 4000);
        UpdateSummary();
    }

    void ExitCropMode()
    {
        _canvas.CropMode = false;
        _cropBtn.Text = L.T("자르기");
        _cropResetBtn.Text = L.T("자르기 해제");
    }

    void CommitCrop()
    {
        var crop = _canvas.EditCrop;
        ExitCropMode();
        if (crop == _crop)
        {
            RefreshAll();
            return;
        }
        var snap = TakeSnapshot();
        _crop = crop;
        Commit(snap, crop.IsEmpty ? L.T("자르기 해제") : L.F("{0} × {1}로 자름", crop.Width, crop.Height));
    }

    void CancelCrop()
    {
        ExitCropMode();
        Notify(L.T("자르기 취소"));
        RefreshAll();
    }

    void ResetCrop()
    {
        if (_crop.IsEmpty) return;
        Apply(L.T("자르기 해제"), () =>
        {
            _crop = Rectangle.Empty;
            return true;
        });
    }

    // ───────────── Playback ─────────────

    void TogglePlay()
    {
        if (_playing)
        {
            StopPlayback();
            return;
        }
        if (_canvas.CropMode) CommitCrop();
        if (_frames.Count < 2) return;
        if (_current >= _frames.Count - 1) _current = 0;
        _playing = true;
        _canvas.Playing = true;
        _playBtn.Glyph = Glyph.Pause;
        _playClock.Restart();
        _nextAt = _frames[_current].DelayMs;
        ShowCurrent();
        _playTimer.Start();
    }

    void StopPlayback()
    {
        if (!_playing) return;
        _playing = false;
        _canvas.Playing = false;
        _playTimer.Stop();
        _playBtn.Glyph = Glyph.Play;
        ShowCurrent();
    }

    void PlayTick()
    {
        long t = _playClock.ElapsedMilliseconds;
        if (t < _nextAt) return;
        int guard = 0;
        while (t >= _nextAt && guard++ < _frames.Count)
        {
            _current = (_current + 1) % _frames.Count;
            _nextAt += Math.Max(10, _frames[_current].DelayMs);
        }
        if (t - _nextAt > 500) _nextAt = t + _frames[_current].DelayMs; // too far behind: resync
        ShowCurrent();
    }

    // ───────────── Thumbnails ─────────────

    Bitmap? ThumbnailAt(int position)
    {
        if (position >= _frames.Count) return null;
        int src = _frames[position].Source;
        if (_thumbs.TryGetValue(src, out var bmp)) return bmp;
        if (!_thumbQueued.TryAdd(src, 0)) return null; // already waiting
        _thumbRequests.Push(src);
        if (_thumbSignal.CurrentCount == 0) _thumbSignal.Release();
        return null;
    }

    async Task ThumbLoop(CancellationToken ct)
    {
        int sweep = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Visible requests first (newest on top), then a background sweep over everything.
                if (!_thumbRequests.TryPop(out int src))
                {
                    if (sweep < _store.Count) src = sweep++;
                    else { await _thumbSignal.WaitAsync(ct); continue; }
                }
                if (_thumbs.ContainsKey(src)) continue;

                var thumb = FrameOps.Thumbnail(_store.Read(src), _srcSize, _thumbBox);
                if (!_thumbs.TryAdd(src, thumb)) thumb.Dispose();
                _thumbQueued.TryRemove(src, out _);

                if (Interlocked.Exchange(ref _thumbRepaintPending, 1) == 0)
                {
                    BeginInvoke(() =>
                    {
                        _thumbRepaintPending = 0;
                        _timeline.Invalidate();
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { } // BeginInvoke after the window closed
    }

    // ───────────── Save ─────────────

    void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = L.T("GIF를 저장할 폴더"),
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_settings.SaveFolder) ? _settings.SaveFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _settings.SaveFolder = dialog.SelectedPath;
        _folderLabel.Text = dialog.SelectedPath;
        _settings.Save();
    }

    string BaseName()
    {
        var name = string.Concat(_nameBox.Text.Trim().Split(Path.GetInvalidFileNameChars()));
        return name.Length == 0 ? $"GIF_{DateTime.Now:yyyyMMdd_HHmmss}" : name;
    }

    /// <summary>Target path: saving again after a save overwrites that file; otherwise a free name is picked.</summary>
    string ResolvePath()
    {
        var folder = _settings.SaveFolder;
        var name = BaseName();
        if (Format == OutputFormat.PngSequence)
        {
            var dir = Path.Combine(folder, name);
            if (dir.Equals(SavedPath, StringComparison.OrdinalIgnoreCase)) return dir;
            for (int n = 2; Directory.Exists(dir) || File.Exists(dir); n++) dir = Path.Combine(folder, $"{name} ({n})");
            return dir;
        }
        var path = Path.Combine(folder, name + ".gif");
        return path.Equals(SavedPath, StringComparison.OrdinalIgnoreCase) ? path : MainForm.UniquePath(folder, name);
    }

    async Task Export()
    {
        if (_exporting)
        {
            _exportCts?.Cancel(); // the save button doubles as cancel while saving
            return;
        }
        if (_fitCts != null)
        {
            Notify(L.T("용량 맞추기가 끝난 뒤 저장할 수 있습니다"), error: true);
            return;
        }
        StopPlayback();
        if (_canvas.CropMode) CommitCrop();
        _estimateCts?.Cancel();
        _estimateTimer.Stop();

        string path;
        try
        {
            Directory.CreateDirectory(_settings.SaveFolder);
            path = ResolvePath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(L.T("저장 폴더를 쓸 수 없습니다") + "\n" + ex.Message, Theme.Rec);
            return;
        }

        var frames = _frames.ToArray();
        var options = CurrentOptions();
        var format = Format;
        var baseName = Path.GetFileName(path);

        _exporting = true;
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        bool folderExisted = Directory.Exists(path);
        _exportBtn.Progress = 0;
        _openFolderBtn.Visible = _openFileBtn.Visible = _copyBtn.Visible = false;
        SetStatus(L.T("저장 중…") + "\n" + baseName);
        var idleText = _exportBtn.Text;
        var progress = new Progress<float>(p =>
        {
            if (cts.IsCancellationRequested) return;
            _exportBtn.Progress = p;
            _exportBtn.Text = $"{L.T("저장 중")}  {p * 100:0}%  ·  {L.T("취소")}";
        });

        try
        {
            await Task.Run(() =>
            {
                if (format == OutputFormat.PngSequence)
                {
                    try
                    {
                        GifExporter.ExportPngSequence(path, baseName, _store, frames, options, progress, cts.Token);
                    }
                    catch (OperationCanceledException) when (!folderExisted)
                    {
                        try { Directory.Delete(path, recursive: true); } catch (IOException) { } // half-written folder
                        throw;
                    }
                    return;
                }
                var temp = path + ".part";
                try
                {
                    using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        GifExporter.Export(fs, _store, frames, options, progress, cts.Token);
                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    try { File.Delete(temp); } catch (IOException) { }
                    throw;
                }
            });
            SavedPath = path;
            SavedBytes = format == OutputFormat.Gif
                ? new FileInfo(path).Length
                : new DirectoryInfo(path).EnumerateFiles().Sum(f => f.Length);
            _dirty = false;
            _nameBox.Text = Path.GetFileNameWithoutExtension(path);
            if (format == OutputFormat.Gif) _estimateText = L.F("예상 크기  약 {0}", FormatSize(SavedBytes));
            bool copied = format == OutputFormat.Gif && _settings.CopyToClipboard && ClipboardHelper.CopyFile(path);
            SetStatus($"{L.T("저장 완료")}  ·  {FormatSize(SavedBytes)}" + (copied ? $"  ·  {L.T("클립보드에 복사됨")}" : "") + "\n" + Path.GetFileName(path), Theme.Success);
            _openFolderBtn.Visible = _openFileBtn.Visible = true;
            _copyBtn.Visible = format == OutputFormat.Gif;
            _settings.Save();
            Notify(copied ? L.F("저장하고 클립보드에 복사했습니다  ·  {0}", FormatSize(SavedBytes)) : L.F("저장했습니다  ·  {0}", FormatSize(SavedBytes)));
        }
        catch (OperationCanceledException)
        {
            SetStatus(L.T("저장을 취소했습니다"));
            Notify(L.T("저장을 취소했습니다"));
        }
        catch (Exception ex)
        {
            SetStatus(L.T("저장 실패") + "\n" + ex.Message, Theme.Rec);
        }
        finally
        {
            _exporting = false;
            _exportCts = null;
            cts.Dispose();
            _exportBtn.Progress = -1;
            _exportBtn.Text = idleText;
        }
    }

    void CopySaved()
    {
        if (SavedPath == null || !File.Exists(SavedPath))
        {
            Notify(L.T("먼저 GIF로 저장하세요"), error: true);
            return;
        }
        bool ok = ClipboardHelper.CopyFile(SavedPath);
        Notify(ok ? L.T("클립보드에 복사했습니다. Ctrl+V로 붙여넣으세요") : L.T("클립보드를 사용할 수 없습니다"), error: !ok);
    }

    public static string FormatSize(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0} KB";

    // ───────────── Keyboard & lifetime ─────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_nameBox.Focused)
        {
            if (keyData == Keys.Enter) { _ = Export(); return true; }
            if (keyData is not (Keys.Control | Keys.S)) return base.ProcessCmdKey(ref msg, keyData);
        }
        if (_textBox.Focused)
        {
            if (keyData == Keys.Enter) { CommitText(); return true; }
            if (keyData is not (Keys.Control | Keys.S) and not (Keys.Control | Keys.Z) and not (Keys.Control | Keys.Y))
                return base.ProcessCmdKey(ref msg, keyData);
        }
        if (ActiveControl is TextBox box && box != _nameBox && box != _textBox)
        {
            // Other text fields (the watermark text) keep every key except save.
            if (keyData is not (Keys.Control | Keys.S)) return base.ProcessCmdKey(ref msg, keyData);
        }
        if (ActiveControl is NumberStepper stepper)
        {
            if (keyData == Keys.Enter && stepper == _delayBox)
            {
                _delayBox.Commit();
                SetDelay(_delayBox.Value);
                return true;
            }
            // Let the field handle typing, ↑↓, Backspace and Esc itself.
            if (keyData is not (Keys.Control | Keys.Z) and not (Keys.Control | Keys.Y) and not (Keys.Control | Keys.S))
                return base.ProcessCmdKey(ref msg, keyData);
        }
        // Sliders and segmented pickers use ←→ for their value.
        if (ActiveControl is ThemedSlider or Segmented && keyData is Keys.Left or Keys.Right)
            return base.ProcessCmdKey(ref msg, keyData);
        if (ActiveControl is AnchorPicker && keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
            return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData)
        {
            case Keys.Space: TogglePlay(); return true;
            case Keys.Left: Step(-1, false); return true;
            case Keys.Right: Step(1, false); return true;
            case Keys.Shift | Keys.Left: Step(-1, true); return true;
            case Keys.Shift | Keys.Right: Step(1, true); return true;
            case Keys.Home: Jump(0, false); return true;
            case Keys.End: Jump(_frames.Count - 1, false); return true;
            case Keys.Shift | Keys.Home: Jump(0, true); return true;
            case Keys.Shift | Keys.End: Jump(_frames.Count - 1, true); return true;
            case Keys.Delete:
                // With an image or the watermark picked in the preview, Delete removes that instead of frames.
                if (_canvas.Focused && _canvas.SelectedSticker is { } sticker) DeleteSticker(sticker);
                else if (_canvas.Focused && _canvas.WatermarkSelected) SetWatermark(_watermark with { Enabled = false });
                else DeleteSelected();
                return true;
            case Keys.Control | Keys.V: PasteImage(); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y:
            case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            case Keys.Control | Keys.A: SelectAll(); return true;
            case Keys.Control | Keys.D: Duplicate(); return true;
            case Keys.Control | Keys.S: _ = Export(); return true;
            case Keys.Control | Keys.Shift | Keys.C: CopySaved(); return true;
            case Keys.Control | Keys.D0: _canvas.ZoomToFit(); return true;
            case Keys.Control | Keys.D1: _canvas.ZoomTo(1f); return true;
            case Keys.Enter when _canvas.CropMode: CommitCrop(); return true;
            case Keys.Escape:
                if (_canvas.CropMode) CancelCrop();
                else if (_canvas.SelectedSticker != null || _canvas.WatermarkSelected)
                {
                    _canvas.SelectedSticker = null;
                    _canvas.WatermarkSelected = false;
                    UpdateDecorControls();
                }
                else { SelectOnly(_current); ShowCurrent(); }
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_exporting)
        {
            e.Cancel = true;
            Notify(L.T("저장 중입니다. 저장 버튼을 눌러 취소한 뒤 닫으세요"), error: true);
            return;
        }
        if (_dirty && e.CloseReason == CloseReason.UserClosing)
        {
            var answer = MessageBox.Show(this,
                SavedPath == null ? L.T("아직 저장하지 않았습니다. 편집 내용을 버리고 닫을까요?") : L.T("마지막 저장 이후 바뀐 내용이 있습니다. 저장하지 않고 닫을까요?"),
                L.T("GIF 편집"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        StopPlayback();
        _estimateCts?.Cancel();
        _fitCts?.Cancel();
        _cts.Cancel();
        try { _thumbWorker?.Wait(2000); } catch (AggregateException) { }
        _settings.Save();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _playTimer.Dispose();
            _estimateTimer.Dispose();
            _view.Dispose();
            foreach (var t in _thumbs.Values) t.Dispose();
            _thumbs.Clear();
            _cts.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}

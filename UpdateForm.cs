namespace GifCapture;

/// <summary>Offers a newer release: shows its notes, then installs it in place, opens its page, or skips it.</summary>
sealed class UpdateForm : Form
{
    readonly ReleaseInfo _release;
    readonly string? _exe = Updater.ReplaceableExe;
    readonly PillButton _updateBtn = new() { Accent = true };
    readonly PillButton _laterBtn = new() { Text = L.T("나중에") };
    readonly PillButton _skipBtn = new() { Text = L.T("이 버전 건너뛰기") };
    readonly PillButton _pageBtn = new() { Text = L.T("릴리스 페이지") };
    readonly Label _status = new() { ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolTip _tips = Theme.CreateToolTip();
    CancellationTokenSource? _download;

    /// <summary>True when the user chose to skip this version for good.</summary>
    public bool Skipped { get; private set; }

    public UpdateForm(ReleaseInfo release)
    {
        _release = release;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Text = L.T("업데이트");
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(480, 420);

        const int L0 = 24, W = 432;
        var title = new Label
        {
            Text = L.F("새 버전 {0}이(가) 나왔습니다", release.Version.ToString(3)),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Theme.Text,
            Bounds = new Rectangle(L0, 18, W, 32),
        };
        var current = Theme.MutedLabel(L.F("지금 쓰는 버전  {0}", Updater.CurrentVersion.ToString(3)), new Rectangle(L0, 52, W, 22));
        var notes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.IsNullOrWhiteSpace(release.Notes) ? L.T("변경 사항 설명이 없습니다.") : PlainNotes(release.Notes),
            Bounds = new Rectangle(L0, 84, W, 236),
        };
        Theme.StyleTextBox(notes);
        notes.HandleCreated += (_, _) => Native.ApplyDarkScrollBars(notes.Handle);
        _status.Bounds = new Rectangle(L0, 326, W, 24);

        _pageBtn.Bounds = new Rectangle(L0, 362, 110, 38);
        _skipBtn.Bounds = new Rectangle(L0 + 116, 362, 118, 38);
        _laterBtn.Bounds = new Rectangle(L0 + 240, 362, 80, 38);
        _updateBtn.Bounds = new Rectangle(L0 + 326, 362, W - 326, 38);
        _updateBtn.Text = _exe != null && release.DownloadUrl != null ? L.T("지금 업데이트") : L.T("내려받기");
        Controls.AddRange([title, current, notes, _status, _pageBtn, _skipBtn, _laterBtn, _updateBtn]);
        ActiveControl = _updateBtn; // a focused text box would open with all of its text selected
        ResumeLayout(false);

        _tips.SetToolTip(_updateBtn, _exe != null && release.DownloadUrl != null
            ? L.T("새 버전을 내려받아 바꾼 뒤 다시 시작합니다.")
            : L.T("이 위치에서는 자동으로 바꿀 수 없어 내려받기 페이지를 엽니다."));
        _tips.SetToolTip(_skipBtn, L.T("이 버전은 다시 알리지 않습니다. 더 새 버전이 나오면 다시 알려 줍니다."));

        _pageBtn.Click += (_, _) => Updater.OpenPage(release.PageUrl);
        _laterBtn.Click += (_, _) => Close(); // also "cancel" while downloading
        _skipBtn.Click += (_, _) => { Skipped = true; Close(); };
        _updateBtn.Click += async (_, _) => await Install();
    }

    async Task Install()
    {
        if (_download != null) return;
        if (_exe == null || _release.DownloadUrl == null)
        {
            Updater.OpenPage(_release.DownloadUrl ?? _release.PageUrl);
            Close();
            return;
        }
        // Restarting would throw away an open editor's unsaved work.
        if (Application.OpenForms.OfType<EditorForm>().Any())
        {
            SetStatus(L.T("열려 있는 편집기를 먼저 닫아 주세요."), Theme.Rec);
            return;
        }

        _download = new CancellationTokenSource();
        _updateBtn.Progress = 0;
        _updateBtn.Text = L.T("내려받는 중…");
        _skipBtn.Enabled = _pageBtn.Enabled = false;
        _laterBtn.Text = L.T("취소");
        var progress = new Progress<float>(p =>
        {
            _updateBtn.Progress = p;
            _updateBtn.Text = $"{p * 100:0}%";
        });
        try
        {
            var file = await Updater.DownloadAsync(_release, _exe, progress, _download.Token);
            SetStatus(L.T("설치하고 다시 시작합니다…"), Theme.Success);
            Updater.InstallAndRestart(_exe, file);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) Close(); // cancelled with the button; closing the window already got here
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus(L.T("업데이트 실패") + "  ·  " + e.Message, Theme.Rec);
            _updateBtn.Progress = -1;
            _updateBtn.Text = L.T("다시 시도");
            _skipBtn.Enabled = _pageBtn.Enabled = true;
            _laterBtn.Text = L.T("나중에");
            _download.Dispose();
            _download = null;
        }
    }

    /// <summary>Release notes are Markdown; drop the markup a plain text box would show literally.</summary>
    static string PlainNotes(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var t = line.TrimEnd().Replace("**", "").Replace("`", "");
            var trimmed = t.TrimStart();
            if (trimmed.StartsWith('#')) return trimmed.TrimStart('#').Trim();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) return t[..(t.Length - trimmed.Length)] + "• " + trimmed[2..];
            return t;
        });
        return string.Join("\r\n", lines);
    }

    void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyDarkTitleBar(Handle, Theme.Bg);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _download?.Cancel(); // closing mid-download cancels it; the partial file is removed
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _download?.Dispose();
        }
        base.Dispose(disposing);
    }
}

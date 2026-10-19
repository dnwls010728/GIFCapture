using System.Diagnostics;

namespace GifCapture;

/// <summary>Less frequently changed preferences, kept out of the main window to keep it simple.</summary>
sealed class SettingsForm : Form
{
    readonly AppSettings _settings = AppSettings.Current;
    readonly Segmented _language = new() { Values = [0, 1, 2], Labels = ["한국어", "日本語", "English"] };
    readonly ToggleSwitch _countdown = new() { Text = L.T("녹화 전 3초 카운트다운") };
    readonly ToggleSwitch _follow = new() { Text = L.T("창 선택 시 창을 따라 영역 이동") };
    readonly ToggleSwitch _clipboard = new() { Text = L.T("저장 후 GIF 파일을 클립보드에 복사") };
    readonly Label _folderLabel = new() { ForeColor = Theme.Text, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToggleSwitch _checkUpdates = new() { Text = L.T("시작할 때 새 버전 확인") };
    readonly PillButton _checkNow = new() { Text = L.T("지금 확인") };
    readonly Label _updateStatus = new() { ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolTip _tips = Theme.CreateToolTip();

    /// <summary>True when the language changed and the app should restart to apply it.</summary>
    public bool RestartRequested { get; private set; }

    public SettingsForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = L.T("설정");
        Icon = Theme.AppIcon;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        ClientSize = new Size(420, 656);

        const int L0 = 24, W = 372;
        Controls.Add(Theme.SectionLabel("언어 / 言語 / Language", new Rectangle(L0, 16, W, 24)));
        _language.Bounds = new Rectangle(L0, 44, W, 34);
        var langNote = Theme.MutedLabel(L.T("바꾸면 앱을 다시 시작합니다"), new Rectangle(L0, 80, W, 22));

        Controls.Add(Theme.SectionLabel(L.T("녹화"), new Rectangle(L0, 116, W, 24)));
        _countdown.Bounds = new Rectangle(L0, 144, W, 30);
        _follow.Bounds = new Rectangle(L0, 180, W, 30);

        Controls.Add(Theme.SectionLabel(L.T("저장"), new Rectangle(L0, 228, W, 24)));
        _clipboard.Bounds = new Rectangle(L0, 256, W, 30);
        var folderCaption = Theme.MutedLabel(L.T("저장 폴더"), new Rectangle(L0, 294, W, 22));
        _folderLabel.Bounds = new Rectangle(L0, 316, W, 24);
        var change = new PillButton { Text = L.T("폴더 변경"), Bounds = new Rectangle(L0, 346, 118, 36) };
        var open = new PillButton { Text = L.T("폴더 열기"), Bounds = new Rectangle(L0 + 126, 346, 118, 36) };

        Controls.Add(Theme.SectionLabel(L.T("업데이트"), new Rectangle(L0, 400, W, 24)));
        _checkUpdates.Bounds = new Rectangle(L0, 428, W, 30);
        _checkNow.Bounds = new Rectangle(L0, 464, 118, 36);
        _updateStatus.Bounds = new Rectangle(L0 + 128, 464, W - 128, 36);
        _updateStatus.Text = L.F("지금 버전  {0}", Updater.CurrentVersion.ToString(3));

        Controls.Add(Theme.SectionLabel(L.T("개인정보"), new Rectangle(L0, 516, W, 24)));
        var privacy = new Label
        {
            Text = L.T("녹화한 화면과 GIF는 이 PC에만 저장됩니다. 인터넷으로 보내는 것은 새 버전 확인(GitHub) 요청뿐입니다."),
            ForeColor = Theme.Muted,
            Bounds = new Rectangle(L0, 542, W, 50),
        };
        var close = new PillButton { Text = L.T("닫기"), Accent = true, Bounds = new Rectangle(420 - 24 - 110, 600, 110, 38) };

        Controls.AddRange([_language, langNote, _countdown, _follow, _clipboard, folderCaption, _folderLabel, change, open,
            _checkUpdates, _checkNow, _updateStatus, privacy, close]);
        ResumeLayout(false);

        _language.Value = Math.Max(0, Array.IndexOf(L.Languages, _settings.Language));
        _countdown.Checked = _settings.Countdown;
        _follow.Checked = _settings.FollowWindow;
        _clipboard.Checked = _settings.CopyToClipboard;
        _folderLabel.Text = _settings.SaveFolder;
        _tips.SetToolTip(_clipboard, L.T("메신저나 문서에 Ctrl+V로 바로 붙여넣을 수 있습니다."));

        _countdown.CheckedChanged += (_, _) => _settings.Countdown = _countdown.Checked;
        _follow.CheckedChanged += (_, _) => _settings.FollowWindow = _follow.Checked;
        _clipboard.CheckedChanged += (_, _) => _settings.CopyToClipboard = _clipboard.Checked;
        _checkUpdates.Checked = _settings.CheckUpdates;
        _checkUpdates.CheckedChanged += (_, _) => _settings.CheckUpdates = _checkUpdates.Checked;
        _checkNow.Click += async (_, _) => await CheckNow();
        _tips.SetToolTip(_checkUpdates, L.T("앱을 켤 때 GitHub Releases에서 새 버전을 찾고, 있으면 알려 줍니다."));
        _language.ValueChanged += (_, _) =>
        {
            var lang = L.Languages[_language.Value];
            if (lang == _settings.Language) return;
            _settings.Language = lang;
            RestartRequested = true;
            DialogResult = DialogResult.OK;
        };
        change.Click += (_, _) => ChooseFolder();
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(_settings.SaveFolder);
            Process.Start("explorer.exe", $"\"{_settings.SaveFolder}\"");
        };
        close.Click += (_, _) => DialogResult = DialogResult.OK;
    }

    /// <summary>Checks on demand; unlike the startup check it also offers a version the user skipped before.</summary>
    async Task CheckNow()
    {
        _checkNow.Enabled = false;
        _updateStatus.ForeColor = Theme.Muted;
        _updateStatus.Text = L.T("확인 중…");
        try
        {
            var release = await Updater.CheckAsync(CancellationToken.None);
            if (IsDisposed) return;
            if (release == null)
            {
                _updateStatus.ForeColor = Theme.Success;
                _updateStatus.Text = L.F("최신 버전입니다  ({0})", Updater.CurrentVersion.ToString(3));
                return;
            }
            _updateStatus.Text = L.F("새 버전 {0}", release.Version.ToString(3));
            using var dlg = new UpdateForm(release);
            dlg.ShowDialog(this);
            if (dlg.Skipped) _settings.SkippedVersion = release.Version.ToString(3);
        }
        catch (Exception ex) when (Updater.IsCheckFailure(ex))
        {
            if (IsDisposed) return;
            _updateStatus.ForeColor = Theme.Rec;
            _updateStatus.Text = L.T("확인하지 못했습니다. 인터넷 연결을 확인하세요.");
        }
        finally
        {
            if (!IsDisposed) _checkNow.Enabled = true;
        }
    }

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
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ApplyDarkTitleBar(Handle, Theme.Bg);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.OK; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _settings.Save();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}

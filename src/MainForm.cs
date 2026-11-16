using System.Globalization;

namespace ClipShrink;

public sealed class MainForm : Form
{
    private const int TicksPerSecond = 10; // スライダーは 0.1 秒単位
    private const int PreviewWidth = 480;

    private readonly string _ffmpeg = FFmpeg.DefaultPath;
    private MediaInfo? _info;
    private string _inputPath = "";
    private double _start, _end;
    private bool _syncing;
    private bool _busy;
    private CancellationTokenSource? _encodeCts;
    private readonly CancellationTokenSource?[] _previewCts = new CancellationTokenSource?[2];
    private readonly System.Windows.Forms.Timer[] _previewTimers = new System.Windows.Forms.Timer[2];
    private readonly bool[] _seekPlayerOnPreview = new bool[2];
    private const double SampleSeconds = 5;
    private bool _sampleMode; // プレーヤーで圧縮プレビューを表示中か

    // 元動画をそのまま再生できない（HEVC など）ときのプレビュー専用 H.264 ファイル
    private string? _proxyFile;          // 作業フォルダー内のファイル名
    private bool _proxyBuilding;
    private CancellationTokenSource? _proxyCts;

    // プレーヤー
    private readonly VideoPlayer _player = new() { Dock = DockStyle.Fill, Margin = new Padding(4) };
    private readonly Button _btnPlayRange = new() { Text = "▶ 範囲を再生", AutoSize = true };
    private readonly Button _btnStopPlay = new() { Text = "■ 停止", AutoSize = true };
    private readonly Button _btnSetStartHere = new() { Text = "この位置を開始に", AutoSize = true };
    private readonly Button _btnSetEndHere = new() { Text = "この位置を終了に", AutoSize = true };
    private readonly Button _btnSamplePreview = new() { Text = $"圧縮プレビュー（{SampleSeconds:0}秒）", AutoSize = true };
    private readonly Label _lblPlayer = new() { AutoSize = true, Margin = new Padding(6, 8, 3, 0) };

    // UI
    private readonly Button _btnOpen = new() { Text = "動画を開く...", AutoSize = true };
    private readonly TextBox _txtInput = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Label _lblInfo = new() { AutoSize = true, Text = "MP4 ファイルを開くか、ウィンドウにドラッグ＆ドロップしてください。" };
    private readonly PictureBox _picStart = NewPreviewBox();
    private readonly PictureBox _picEnd = NewPreviewBox();
    private readonly Label _lblPicStart = new() { AutoSize = true, Text = "開始位置" };
    private readonly Label _lblPicEnd = new() { AutoSize = true, Text = "終了位置" };
    private readonly TrackBar _trkStart = NewTrack();
    private readonly TrackBar _trkEnd = NewTrack();
    private readonly TextBox _txtStart = new() { Width = 110, TextAlign = HorizontalAlignment.Center };
    private readonly TextBox _txtEnd = new() { Width = 110, TextAlign = HorizontalAlignment.Center };
    private readonly Label _lblLength = new() { AutoSize = true, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold) };

    private readonly RadioButton _rdoQuality = new() { Text = "画質で指定", AutoSize = true, Checked = true };
    private readonly RadioButton _rdoTarget = new() { Text = "目標サイズで指定", AutoSize = true };
    private readonly ComboBox _cmbQuality = NewCombo();
    private readonly NumericUpDown _numTarget = new() { Minimum = 0.5m, Maximum = 100000, DecimalPlaces = 1, Increment = 1, Value = 10, Width = 90 };
    private readonly Label _lblTargetInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ComboBox _cmbCodec = NewCombo();
    private readonly ComboBox _cmbResolution = NewCombo();
    private readonly ComboBox _cmbFps = NewCombo();
    private readonly ComboBox _cmbAudio = NewCombo();
    private readonly ComboBox _cmbPreset = NewCombo();

    private readonly TextBox _txtOutput = new() { Dock = DockStyle.Fill };
    private readonly Button _btnBrowse = new() { Text = "参照...", AutoSize = true };
    private readonly Button _btnExport = new() { Text = "書き出し", AutoSize = true, Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold), Padding = new Padding(12, 2, 12, 2) };
    private readonly Button _btnCancel = new() { Text = "中止", AutoSize = true, Enabled = false };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly Label _lblStatus = new() { AutoSize = true, Text = "" };

    private readonly (string Label, int Crf)[] _qualities =
    [
        ("高画質（CRF 23）", 23), ("標準（CRF 26）", 26), ("小さめ（CRF 28）", 28),
        ("かなり小さく（CRF 31）", 31), ("最小（CRF 35）", 35),
    ];
    private readonly (string Label, int Height)[] _resolutions =
    [
        ("元のまま", 0), ("1080p", 1080), ("720p", 720), ("540p", 540), ("480p", 480), ("360p", 360),
    ];
    private readonly (string Label, double Fps)[] _fpsList = [("元のまま", 0), ("30 fps", 30), ("24 fps", 24), ("15 fps", 15)];
    private readonly (string Label, int Kbps)[] _audios = [("128 kbps", 128), ("96 kbps", 96), ("64 kbps", 64), ("音声なし", 0)];
    private readonly (string Label, string Preset)[] _presets =
        [("速い", "veryfast"), ("標準", "medium"), ("高圧縮（遅い）", "slow")];

    public MainForm(string[] args)
    {
        Text = "ClipShrink - MP4 トリム＆圧縮";
        Font = new Font("Yu Gothic UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 960);
        ClientSize = new Size(Math.Min(1080, wa.Width - 40), Math.Min(880, wa.Height - 60));
        MinimumSize = new Size(Math.Min(760, wa.Width), Math.Min(600, wa.Height));
        Load += (_, _) =>
        {
            // DPI スケーリング後に画面からはみ出す場合は作業領域に収める
            var area = Screen.FromControl(this).WorkingArea;
            if (Width > area.Width) Width = area.Width;
            if (Height > area.Height) Height = area.Height;
            Left = Math.Max(area.Left, area.Left + (area.Width - Width) / 2);
            Top = Math.Max(area.Top, area.Top + (area.Height - Height) / 2);
        };
        AllowDrop = true;

        foreach (var q in _qualities) _cmbQuality.Items.Add(q.Label);
        _cmbQuality.SelectedIndex = 2;
        _cmbCodec.Items.AddRange(["H.264（互換性重視）", "H.265 / HEVC（より小さい）"]);
        _cmbCodec.SelectedIndex = 0;
        foreach (var r in _resolutions) _cmbResolution.Items.Add(r.Label);
        _cmbResolution.SelectedIndex = 0;
        foreach (var f in _fpsList) _cmbFps.Items.Add(f.Label);
        _cmbFps.SelectedIndex = 0;
        foreach (var a in _audios) _cmbAudio.Items.Add(a.Label);
        _cmbAudio.SelectedIndex = 1;
        foreach (var p in _presets) _cmbPreset.Items.Add(p.Label);
        _cmbPreset.SelectedIndex = 1;

        BuildLayout();
        WireEvents();
        SetFileControlsEnabled(false);
        UpdateModeControls();

        for (int i = 0; i < 2; i++)
        {
            int side = i;
            _previewTimers[i] = new System.Windows.Forms.Timer { Interval = 200 };
            _previewTimers[i].Tick += async (_, _) =>
            {
                _previewTimers[side].Stop();
                await LoadPreviewAsync(side == 1);
            };
        }

        Shown += async (_, _) =>
        {
            if (!File.Exists(_ffmpeg))
            {
                MessageBox.Show(this, $"ffmpeg.exe が見つかりません。\n{_ffmpeg}\n\nClipShrink.exe と同じフォルダーに ffmpeg.exe を置いてから起動してください。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                _btnOpen.Enabled = false;
                return;
            }
            DeleteSampleFiles();
            await _player.InitializeAsync();
            UpdatePlayerButtons();
            if (args.Length > 0 && File.Exists(args[0])) await OpenFileAsync(args[0]);
        };
        FormClosed += (_, _) =>
        {
            ResetProxy();
            _player.Dispose(); // ファイルのロックを外してから消す
            DeleteSampleFiles();
        };
    }

    // ---------------- レイアウト ----------------

    private static PictureBox NewPreviewBox() => new()
    {
        Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Margin = new Padding(4),
    };

    private static TrackBar NewTrack() => new()
    {
        Dock = DockStyle.Fill, Minimum = 0, Maximum = 1, TickStyle = TickStyle.BottomRight, AutoSize = false, Height = 36,
    };

    private static ComboBox NewCombo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // ファイル
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 情報
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // プレビュー
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // トリム
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 圧縮設定
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 出力
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // 実行

        // ファイル行
        var fileRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileRow.Controls.Add(_btnOpen, 0, 0);
        _txtInput.Margin = new Padding(6, 6, 3, 3);
        fileRow.Controls.Add(_txtInput, 1, 0);
        root.Controls.Add(fileRow);

        _lblInfo.Margin = new Padding(3, 6, 3, 6);
        root.Controls.Add(_lblInfo);

        // プレビュー（左: 再生プレーヤー、右: 開始・終了フレーム）
        var preview = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        preview.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));

        var playerPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        playerPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        playerPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        playerPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        playerPane.Controls.Add(_player, 0, 0);
        var playerBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        playerBar.Controls.AddRange([_btnPlayRange, _btnStopPlay, _btnSetStartHere, _btnSetEndHere, _btnSamplePreview]);
        playerPane.Controls.Add(playerBar, 0, 1);
        _lblPlayer.AutoSize = false;
        _lblPlayer.Dock = DockStyle.Fill;
        _lblPlayer.AutoEllipsis = true;
        _lblPlayer.Margin = new Padding(6, 0, 3, 0);
        playerPane.Controls.Add(_lblPlayer, 0, 2);
        preview.Controls.Add(playerPane, 0, 0);

        var frames = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        frames.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frames.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        frames.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frames.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        frames.Controls.Add(_lblPicStart, 0, 0);
        frames.Controls.Add(_picStart, 0, 1);
        frames.Controls.Add(_lblPicEnd, 0, 2);
        frames.Controls.Add(_picEnd, 0, 3);
        preview.Controls.Add(frames, 1, 0);
        root.Controls.Add(preview);

        // トリム
        var trim = new GroupBox { Text = "トリム（前後のカット）", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var tt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, AutoSize = true };
        tt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddTrimRow(tt, 0, "開始", _trkStart, _txtStart, isEnd: false);
        AddTrimRow(tt, 1, "終了", _trkEnd, _txtEnd, isEnd: true);
        _lblLength.Margin = new Padding(3, 6, 3, 0);
        tt.Controls.Add(_lblLength, 0, 2);
        tt.SetColumnSpan(_lblLength, 5);
        trim.Controls.Add(tt);
        root.Controls.Add(trim);

        // 圧縮設定
        var comp = new GroupBox { Text = "圧縮設定", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var ct = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        ct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ct.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        ct.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ct.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var targetPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        targetPanel.Controls.Add(_numTarget);
        targetPanel.Controls.Add(new Label { Text = "MB", AutoSize = true, Margin = new Padding(3, 7, 8, 0) });
        targetPanel.Controls.Add(_lblTargetInfo);
        _lblTargetInfo.Margin = new Padding(3, 7, 3, 0);

        ct.Controls.Add(_rdoQuality, 0, 0); ct.Controls.Add(_cmbQuality, 1, 0);
        ct.Controls.Add(_rdoTarget, 2, 0); ct.Controls.Add(targetPanel, 3, 0);
        ct.Controls.Add(Caption("コーデック"), 0, 1); ct.Controls.Add(_cmbCodec, 1, 1);
        ct.Controls.Add(Caption("解像度"), 2, 1); ct.Controls.Add(_cmbResolution, 3, 1);
        ct.Controls.Add(Caption("フレームレート"), 0, 2); ct.Controls.Add(_cmbFps, 1, 2);
        ct.Controls.Add(Caption("音声"), 2, 2); ct.Controls.Add(_cmbAudio, 3, 2);
        ct.Controls.Add(Caption("エンコード速度"), 0, 3); ct.Controls.Add(_cmbPreset, 1, 3);
        comp.Controls.Add(ct);
        root.Controls.Add(comp);

        // 出力
        var outRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outRow.Controls.Add(Caption("保存先"), 0, 0);
        _txtOutput.Margin = new Padding(3, 5, 3, 3);
        outRow.Controls.Add(_txtOutput, 1, 0);
        outRow.Controls.Add(_btnBrowse, 2, 0);
        root.Controls.Add(outRow);

        // 実行
        var run = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        run.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        run.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        run.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        run.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        run.Controls.Add(_btnExport, 0, 0);
        run.Controls.Add(_btnCancel, 1, 0);
        _progress.Margin = new Padding(8, 8, 8, 8);
        run.Controls.Add(_progress, 2, 0);
        _lblStatus.Margin = new Padding(3, 9, 3, 0);
        run.Controls.Add(_lblStatus, 3, 0);
        root.Controls.Add(run);

        Controls.Add(root);
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 8, 3) };

    private void AddTrimRow(TableLayoutPanel t, int row, string caption, TrackBar trk, TextBox txt, bool isEnd)
    {
        t.Controls.Add(Caption(caption), 0, row);
        t.Controls.Add(trk, 1, row);
        txt.Margin = new Padding(3, 6, 3, 3);
        t.Controls.Add(txt, 2, row);

        var back = new Button { Text = "◀ 0.1秒", AutoSize = true };
        var fwd = new Button { Text = "0.1秒 ▶", AutoSize = true };
        back.Click += (_, _) => Nudge(isEnd, -0.1);
        fwd.Click += (_, _) => Nudge(isEnd, +0.1);
        t.Controls.Add(back, 3, row);
        t.Controls.Add(fwd, 4, row);
    }

    // ---------------- イベント ----------------

    private void WireEvents()
    {
        _btnOpen.Click += async (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "動画ファイル|*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi|すべてのファイル|*.*",
                Title = "トリムする動画を選択",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) await OpenFileAsync(dlg.FileName);
        };

        DragEnter += (_, e) =>
        {
            if (!_busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await OpenFileAsync(files[0]);
        };

        _trkStart.Scroll += (_, _) => OnTrackScrolled(isEnd: false);
        _trkEnd.Scroll += (_, _) => OnTrackScrolled(isEnd: true);
        _txtStart.Leave += (_, _) => OnTimeTextCommitted(isEnd: false);
        _txtEnd.Leave += (_, _) => OnTimeTextCommitted(isEnd: true);
        _txtStart.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OnTimeTextCommitted(false); e.SuppressKeyPress = true; } };
        _txtEnd.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OnTimeTextCommitted(true); e.SuppressKeyPress = true; } };

        _rdoQuality.CheckedChanged += (_, _) => UpdateModeControls();
        _rdoTarget.CheckedChanged += (_, _) => UpdateModeControls();
        _numTarget.ValueChanged += (_, _) => UpdateTargetInfo();
        _cmbAudio.SelectedIndexChanged += (_, _) => UpdateTargetInfo();

        _btnBrowse.Click += (_, _) =>
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "MP4 動画|*.mp4",
                DefaultExt = "mp4",
                FileName = Path.GetFileName(_txtOutput.Text),
                InitialDirectory = SafeDir(_txtOutput.Text),
                OverwritePrompt = false,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtOutput.Text = dlg.FileName;
        };

        _btnPlayRange.Click += async (_, _) =>
        {
            if (_info is null) return;
            if (_sampleMode) await ShowSourceInPlayerAsync(_start);
            await _player.PlayRangeAsync(_start, _end);
        };
        _btnStopPlay.Click += async (_, _) => await _player.StopAsync();
        _btnSetStartHere.Click += async (_, _) =>
        {
            await _player.StopAsync();
            SetStart(_player.CurrentTime, seekPlayer: false);
        };
        _btnSetEndHere.Click += async (_, _) =>
        {
            await _player.StopAsync();
            SetEnd(_player.CurrentTime, seekPlayer: false);
        };
        _btnSamplePreview.Click += async (_, _) => await SamplePreviewAsync();
        _player.TimeChanged += t =>
        {
            if (_sampleMode) return;
            _lblPlayer.Text = $"再生位置 {FormatTime(t)}";
        };
        _player.PlaybackError += async detail => await OnPlaybackErrorAsync(detail);

        _btnExport.Click += async (_, _) => await ExportAsync();
        _btnCancel.Click += (_, _) => _encodeCts?.Cancel();

        FormClosing += (_, e) =>
        {
            if (!_busy) return;
            var r = MessageBox.Show(this, "書き出し中です。中止して終了しますか？", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) _encodeCts?.Cancel();
            else e.Cancel = true;
        };
    }

    private static string SafeDir(string path)
    {
        try { var d = Path.GetDirectoryName(path); return d is { Length: > 0 } && Directory.Exists(d) ? d : ""; }
        catch { return ""; }
    }

    // ---------------- ファイル読み込み ----------------

    private async Task OpenFileAsync(string path)
    {
        if (_busy) return;
        _lblInfo.Text = "読み込み中...";
        MediaInfo info;
        try { info = await FFmpeg.ProbeAsync(_ffmpeg, path); }
        catch (Exception ex)
        {
            _lblInfo.Text = "読み込みに失敗しました: " + ex.Message;
            return;
        }

        if (!info.HasVideo || info.Duration <= 0)
        {
            MessageBox.Show(this, "動画として読み込めませんでした。\n" + path, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _lblInfo.Text = "動画として読み込めませんでした。";
            return;
        }

        ResetProxy();
        _info = info;
        _inputPath = path;
        _txtInput.Text = path;
        long bytes = new FileInfo(path).Length;
        _lblInfo.Text = $"長さ {FormatTime(info.Duration)}　{info.Width}×{info.Height}" +
                        (info.Fps > 0 ? $"　{info.Fps:0.##} fps" : "") +
                        $"　{info.VideoCodec}" + (info.HasAudio ? "" : "（音声なし）") +
                        (info.BitrateKbps > 0 ? $"　{info.BitrateKbps} kbps" : "") +
                        $"　{FormatSize(bytes)}";

        int max = Math.Max(1, (int)Math.Floor(info.Duration * TicksPerSecond));
        _syncing = true;
        _trkStart.Maximum = max;
        _trkEnd.Maximum = max;
        _trkStart.TickFrequency = _trkEnd.TickFrequency = Math.Max(1, max / 20);
        _trkStart.LargeChange = _trkEnd.LargeChange = Math.Max(1, max / 20);
        _syncing = false;

        _start = 0;
        _end = info.Duration;
        SyncTrimUi();

        var dir = Path.GetDirectoryName(path) ?? "";
        _txtOutput.Text = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + "_trim.mp4");

        // 解像度の選択肢を、元より大きいものは選んでも意味がないので注記だけ
        SetFileControlsEnabled(true);
        UpdateTargetInfo();
        _progress.Value = 0;
        _lblStatus.Text = "";

        RequestPreview(false, immediate: true);
        RequestPreview(true, immediate: true);
        await ShowSourceInPlayerAsync(0);
    }

    private async Task ShowSourceInPlayerAsync(double seekTo)
    {
        _sampleMode = false;
        _lblPlayer.Text = _proxyFile is not null ? "プレビュー用に変換した動画を表示中（書き出しは元の動画から行います）" : "";
        UpdatePlayerButtons();
        if (_proxyFile is not null) await _player.LoadWorkFileAsync(_proxyFile, seekTo, autoplay: false);
        else if (_proxyBuilding) await _player.ShowMessageAsync("プレビュー用に変換中です...");
        else if (_inputPath.Length > 0) await _player.LoadSourceAsync(_inputPath, seekTo);
    }

    // ---------------- 再生できない形式への対応 ----------------

    private async Task OnPlaybackErrorAsync(string detail)
    {
        if (_info is null) return;
        if (_sampleMode)
        {
            _lblPlayer.Text = "圧縮プレビューを再生できません（H.265 はこの PC で再生できない場合があります）";
            await _player.ShowMessageAsync("この圧縮プレビューは再生できません。\nコーデックを H.264 にすると確認できます。");
            return;
        }
        if (_proxyFile is not null || _proxyBuilding)
        {
            // 変換済みの動画でも再生できなかった
            if (_proxyFile is not null)
                await _player.ShowMessageAsync("プレビュー再生できませんでした。\n書き出しはそのまま行えます。\n(" + detail + ")");
            return;
        }
        await BuildProxyAsync();
    }

    /// <summary>
    /// ブラウザーで再生できない動画（iPhone の HEVC など）を、プレビュー専用の軽い H.264 に変換して再生する。
    /// 時刻は元動画と同じなので、再生位置をそのままトリム位置に使える。
    /// </summary>
    private async Task BuildProxyAsync()
    {
        if (_info is null) return;
        var info = _info;
        var input = _inputPath;

        _proxyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _proxyCts = cts;
        _proxyBuilding = true;
        UpdatePlayerButtons();

        Directory.CreateDirectory(VideoPlayer.WorkFolder);
        var fileName = $"proxy_{DateTime.Now:HHmmssfff}.mp4";
        var outPath = Path.Combine(VideoPlayer.WorkFolder, fileName);

        // 長い辺を 960px までに縮める（縦長の画面録画にも対応）
        const int maxSide = 960;
        string scale = info.Width >= info.Height
            ? $"scale=-2:{Math.Min(maxSide, info.Height / 2 * 2)}"
            : $"scale={Math.Min(maxSide, info.Width / 2 * 2)}:-2";
        if (info.Width <= 0 || info.Height <= 0) scale = $"scale=-2:{540}";

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-i", input,
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", scale + ",format=yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-g", "30",
            "-c:a", "aac", "-b:a", "128k",
            "-movflags", "+faststart",
            "-progress", "pipe:1", "-nostats", outPath,
        };

        string msg = "この動画（" + info.VideoCodec + "）はそのままではプレビューできないため、プレビュー用に変換しています...";
        _lblPlayer.Text = msg;
        await _player.ShowMessageAsync(msg);

        int exit;
        string err;
        try
        {
            (exit, err) = await FFmpeg.RunAsync(_ffmpeg, args, sec =>
            {
                double pct = Math.Clamp(sec / Math.Max(0.1, info.Duration) * 100, 0, 100);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(() =>
                    {
                        if (_proxyCts != cts) return;
                        _lblPlayer.Text = $"プレビュー用に変換中... {pct:0}%（変換中もトリム設定・書き出しは行えます）";
                    });
                }
                catch (InvalidOperationException) { }
            }, cts.Token);
        }
        catch (Exception ex)
        {
            exit = -1;
            err = ex.Message;
        }

        if (_proxyCts != cts || cts.IsCancellationRequested || input != _inputPath)
        {
            TryDelete(outPath); // 別のファイルを開いた・閉じたなど
            return;
        }

        _proxyBuilding = false;
        UpdatePlayerButtons();
        if (exit != 0 || !File.Exists(outPath))
        {
            TryDelete(outPath);
            _lblPlayer.Text = "プレビュー用の変換に失敗しました（書き出しは行えます）";
            await _player.ShowMessageAsync("プレビュー用の変換に失敗しました。\n書き出しはそのまま行えます。\n" + LastErrorLines(err));
            return;
        }

        _proxyFile = fileName;
        if (!_sampleMode) await ShowSourceInPlayerAsync(_start);
    }

    private void ResetProxy()
    {
        _proxyCts?.Cancel();
        _proxyCts = null;
        _proxyBuilding = false;
        _proxyFile = null; // ファイル自体は再生中でロックされている可能性があるので終了時にまとめて消す
    }

    private void SetFileControlsEnabled(bool enabled)
    {
        foreach (Control c in new Control[] { _trkStart, _trkEnd, _txtStart, _txtEnd, _txtOutput, _btnBrowse, _btnExport })
            c.Enabled = enabled;
        foreach (var b in _trkStart.Parent!.Controls.OfType<Button>()) b.Enabled = enabled;
        UpdatePlayerButtons();
    }

    private void UpdatePlayerButtons()
    {
        bool loaded = _info is not null && _player.IsReady && !_proxyBuilding;
        _btnPlayRange.Enabled = loaded;
        _btnStopPlay.Enabled = loaded;
        // 圧縮プレビュー表示中は再生位置が元動画の時刻ではないので無効にする
        _btnSetStartHere.Enabled = loaded && !_sampleMode && !_busy;
        _btnSetEndHere.Enabled = loaded && !_sampleMode && !_busy;
        _btnSamplePreview.Enabled = _info is not null && !_busy;
    }

    // ---------------- トリム操作 ----------------

    private double MinGap => _info is { Fps: > 0 } i ? Math.Max(0.1, 1.0 / i.Fps) : 0.1;

    private void OnTrackScrolled(bool isEnd)
    {
        if (_syncing || _info is null) return;
        if (isEnd) SetEnd(_trkEnd.Value == _trkEnd.Maximum ? _info.Duration : _trkEnd.Value / (double)TicksPerSecond);
        else SetStart(_trkStart.Value / (double)TicksPerSecond);
    }

    private void OnTimeTextCommitted(bool isEnd)
    {
        if (_info is null) return;
        var box = isEnd ? _txtEnd : _txtStart;
        if (box.Text == FormatTime(isEnd ? _end : _start)) return; // 変更なし
        if (TryParseTime(box.Text, out var sec))
        {
            if (isEnd) SetEnd(sec); else SetStart(sec);
        }
        else
        {
            SyncTrimUi(); // 不正な入力は元に戻す
        }
    }

    private void Nudge(bool isEnd, double delta)
    {
        if (_info is null) return;
        if (isEnd) SetEnd(_end + delta); else SetStart(_start + delta);
    }

    private void SetStart(double sec, bool seekPlayer = true)
    {
        if (_info is null) return;
        _start = Math.Clamp(sec, 0, Math.Max(0, _info.Duration - MinGap));
        if (_end - _start < MinGap)
        {
            _end = Math.Min(_info.Duration, _start + MinGap);
            RequestPreview(true);
        }
        SyncTrimUi();
        _seekPlayerOnPreview[0] = seekPlayer;
        RequestPreview(false);
    }

    private void SetEnd(double sec, bool seekPlayer = true)
    {
        if (_info is null) return;
        _end = Math.Clamp(sec, MinGap, _info.Duration);
        if (_end - _start < MinGap)
        {
            _start = Math.Max(0, _end - MinGap);
            RequestPreview(false);
        }
        SyncTrimUi();
        _seekPlayerOnPreview[1] = seekPlayer;
        RequestPreview(true);
    }

    private void SyncTrimUi()
    {
        _syncing = true;
        _trkStart.Value = Math.Clamp((int)Math.Round(_start * TicksPerSecond), _trkStart.Minimum, _trkStart.Maximum);
        _trkEnd.Value = Math.Clamp((int)Math.Round(_end * TicksPerSecond), _trkEnd.Minimum, _trkEnd.Maximum);
        _syncing = false;
        _txtStart.Text = FormatTime(_start);
        _txtEnd.Text = FormatTime(_end);
        _lblPicStart.Text = $"開始位置  {FormatTime(_start)}";
        _lblPicEnd.Text = $"終了位置  {FormatTime(_end)}";
        if (_info is not null)
            _lblLength.Text = $"切り出す長さ: {FormatTime(_end - _start)}（元の長さ {FormatTime(_info.Duration)}）";
        UpdateTargetInfo();
    }

    // ---------------- プレビュー ----------------

    private void RequestPreview(bool isEnd, bool immediate = false)
    {
        var t = _previewTimers[isEnd ? 1 : 0];
        if (t is null) return;
        t.Stop();
        t.Interval = immediate ? 1 : 200;
        t.Start();
    }

    private async Task LoadPreviewAsync(bool isEnd)
    {
        if (_info is null) return;
        int side = isEnd ? 1 : 0;
        _previewCts[side]?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts[side] = cts;

        string file = _inputPath;
        double frame = _info.Fps > 0 ? 1.0 / _info.Fps : 0.04;
        // 終了位置は「その直前のフレーム」を表示する
        double t = isEnd ? Math.Max(0, _end - frame) : _start;

        var bmp = await FFmpeg.GrabFrameAsync(_ffmpeg, file, t, PreviewWidth, cts.Token);
        if (bmp is null && isEnd && !cts.IsCancellationRequested)
            bmp = await FFmpeg.GrabFrameAsync(_ffmpeg, file, Math.Max(0, _end - 0.5), PreviewWidth, cts.Token);

        if (cts.IsCancellationRequested || file != _inputPath || IsDisposed)
        {
            bmp?.Dispose();
            return;
        }

        var pic = isEnd ? _picEnd : _picStart;
        var old = pic.Image;
        pic.Image = bmp;
        old?.Dispose();

        // スライダー操作に合わせてプレーヤーもその位置へ移動する
        if (_seekPlayerOnPreview[side])
        {
            _seekPlayerOnPreview[side] = false;
            if (_sampleMode) await ShowSourceInPlayerAsync(t);
            else await _player.SeekAsync(t);
        }
    }

    // ---------------- 圧縮設定 ----------------

    private void UpdateModeControls()
    {
        bool target = _rdoTarget.Checked;
        _cmbQuality.Enabled = !target;
        _numTarget.Enabled = target;
        _lblTargetInfo.Visible = target;
        UpdateTargetInfo();
    }

    private void UpdateTargetInfo()
    {
        if (!_rdoTarget.Checked || _info is null) { _lblTargetInfo.Text = ""; return; }
        var s = CollectSettings();
        int vk = ArgumentBuilder.VideoKbpsForTarget(s, _info);
        _lblTargetInfo.Text = $"→ 映像 約 {vk} kbps" + (vk < 150 ? "（かなり粗くなります。解像度を下げるのがおすすめ）" : "");
    }

    private EncodeSettings CollectSettings() => new()
    {
        Input = _inputPath,
        Output = _txtOutput.Text.Trim(),
        Start = _start,
        End = _end,
        Mode = _rdoTarget.Checked ? SizeMode.TargetSize : SizeMode.Quality,
        Crf = _qualities[Math.Max(0, _cmbQuality.SelectedIndex)].Crf,
        TargetMB = (double)_numTarget.Value,
        Hevc = _cmbCodec.SelectedIndex == 1,
        MaxHeight = _resolutions[Math.Max(0, _cmbResolution.SelectedIndex)].Height,
        MaxFps = _fpsList[Math.Max(0, _cmbFps.SelectedIndex)].Fps,
        AudioKbps = _audios[Math.Max(0, _cmbAudio.SelectedIndex)].Kbps,
        Preset = _presets[Math.Max(0, _cmbPreset.SelectedIndex)].Preset,
    };

    // ---------------- 書き出し ----------------

    private async Task ExportAsync()
    {
        if (_info is null || _busy) return;
        var s = CollectSettings();

        if (s.Output.Length == 0)
        {
            MessageBox.Show(this, "保存先を指定してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!s.Output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) s.Output += ".mp4";
        string fullOut, fullIn;
        try
        {
            fullOut = Path.GetFullPath(s.Output);
            fullIn = Path.GetFullPath(s.Input);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存先のパスが正しくありません。\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.Equals(fullOut, fullIn, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "元の動画と同じファイルには書き出せません。別の名前を指定してください。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var outDir = Path.GetDirectoryName(fullOut);
        if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir))
        {
            MessageBox.Show(this, "保存先のフォルダーが存在しません。\n" + outDir, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (File.Exists(fullOut) &&
            MessageBox.Show(this, $"{Path.GetFileName(fullOut)} は既にあります。上書きしますか？", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        s.Output = fullOut;
        _txtOutput.Text = fullOut;
        var passes = ArgumentBuilder.Build(s, _info);

        await _player.StopAsync();
        var (exit, err, canceled) = await RunPassesAsync(passes, s.Length);

        if (canceled || exit != 0)
        {
            TryDelete(fullOut);
            _progress.Value = 0;
            if (canceled)
            {
                _lblStatus.Text = "中止しました";
            }
            else
            {
                _lblStatus.Text = "失敗しました";
                MessageBox.Show(this, "書き出しに失敗しました。\n\n" + LastErrorLines(err), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        _progress.Value = 1000;
        long inBytes = new FileInfo(fullIn).Length;
        long outBytes = File.Exists(fullOut) ? new FileInfo(fullOut).Length : 0;
        double ratio = inBytes > 0 ? outBytes * 100.0 / inBytes : 0;
        _lblStatus.Text = $"完了  {FormatSize(outBytes)}";

        var msg = $"書き出しが完了しました。\n\n{fullOut}\n\n元の動画: {FormatSize(inBytes)}\n書き出し: {FormatSize(outBytes)}（元の {ratio:0.#}%）";
        var icon = MessageBoxIcon.Information;
        if (outBytes >= inBytes)
        {
            msg += "\n\n※ 元の動画より大きくなりました。画質を「かなり小さく」にするか、解像度を下げてください。";
            icon = MessageBoxIcon.Warning;
        }
        msg += "\n\n保存先のフォルダーを開きますか？";
        if (MessageBox.Show(this, msg, Text, MessageBoxButtons.YesNo, icon) == DialogResult.Yes)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullOut}\""); } catch { }
        }
    }

    /// <summary>ffmpeg のパスを順に実行する。進捗バーと状態表示を更新する。</summary>
    private async Task<(int Exit, string Error, bool Canceled)> RunPassesAsync(List<EncodePass> passes, double len)
    {
        SetBusy(true);
        _encodeCts = new CancellationTokenSource();
        var token = _encodeCts.Token;
        int exit = 0;
        string err = "";

        try
        {
            for (int i = 0; i < passes.Count; i++)
            {
                int index = i;
                var pass = passes[i];
                _lblStatus.Text = pass.Label;
                (exit, err) = await FFmpeg.RunAsync(_ffmpeg, pass.Args, sec =>
                {
                    double frac = (index + Math.Clamp(sec / Math.Max(0.1, len), 0, 1)) / passes.Count;
                    if (IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke(() =>
                        {
                            if (!_busy) return;
                            _progress.Value = (int)Math.Clamp(frac * 1000, 0, 1000);
                            _lblStatus.Text = $"{pass.Label}  {frac * 100:0}%";
                        });
                    }
                    catch (InvalidOperationException) { }
                }, token);
                if (token.IsCancellationRequested || exit != 0) break;
            }
        }
        catch (Exception ex)
        {
            exit = -1;
            err = ex.Message;
        }
        finally
        {
            foreach (var log in passes.Select(p => p.PassLogFile).OfType<string>().Distinct())
                DeletePassLogs(log);
        }

        bool canceled = token.IsCancellationRequested;
        SetBusy(false);
        return (exit, err, canceled);
    }

    // ---------------- 圧縮プレビュー ----------------

    /// <summary>開始位置から数秒だけ今の設定で圧縮し、プレーヤーで再生する。</summary>
    private async Task SamplePreviewAsync()
    {
        if (_info is null || _busy) return;
        if (!_player.IsReady)
        {
            MessageBox.Show(this, "プレーヤーが使えないため、圧縮プレビューを再生できません。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var full = CollectSettings();
        var s = CollectSettings();
        s.End = Math.Min(_end, _start + SampleSeconds);
        double sampleLen = s.Length;
        if (full.Mode == SizeMode.TargetSize)
            s.VideoKbpsOverride = ArgumentBuilder.VideoKbpsForTarget(full, _info); // 全体と同じビットレートで試す

        Directory.CreateDirectory(VideoPlayer.WorkFolder);
        var fileName = $"sample_{DateTime.Now:HHmmssfff}.mp4"; // 再生中のファイルはロックされるので毎回別名
        s.Output = Path.Combine(VideoPlayer.WorkFolder, fileName);

        await _player.StopAsync();
        var passes = ArgumentBuilder.Build(s, _info);
        var (exit, err, canceled) = await RunPassesAsync(passes, sampleLen);

        if (canceled || exit != 0 || !File.Exists(s.Output))
        {
            TryDelete(s.Output);
            _progress.Value = 0;
            _lblStatus.Text = canceled ? "中止しました" : "プレビュー作成に失敗しました";
            if (!canceled)
                MessageBox.Show(this, "圧縮プレビューを作れませんでした。\n\n" + LastErrorLines(err), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _progress.Value = 1000;
        long bytes = new FileInfo(s.Output).Length;
        double estimate = full.Mode == SizeMode.TargetSize
            ? full.TargetMB * 1024 * 1024
            : bytes * (full.Length / Math.Max(0.1, sampleLen));
        _lblStatus.Text = "プレビュー作成完了";

        _sampleMode = true;
        UpdatePlayerButtons();
        _lblPlayer.Text = $"圧縮プレビュー再生中（{sampleLen:0.#}秒 {FormatSize(bytes)}）→ 全体の推定 約 {FormatSize((long)estimate)}";
        if (full.Hevc) _lblPlayer.Text += "　※H.265 は環境によって再生できません";
        await _player.LoadWorkFileAsync(fileName, 0, autoplay: true);
    }

    private static void DeleteSampleFiles()
    {
        try
        {
            if (!Directory.Exists(VideoPlayer.WorkFolder)) return;
            foreach (var f in Directory.GetFiles(VideoPlayer.WorkFolder, "sample_*.mp4")) TryDelete(f);
            foreach (var f in Directory.GetFiles(VideoPlayer.WorkFolder, "proxy_*.mp4")) TryDelete(f);
        }
        catch { }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnExport.Enabled = !busy;
        _btnCancel.Enabled = busy;
        _btnOpen.Enabled = !busy;
        _btnBrowse.Enabled = !busy;
        _txtOutput.Enabled = !busy;
        foreach (Control c in new Control[] { _rdoQuality, _rdoTarget, _cmbCodec, _cmbResolution, _cmbFps, _cmbAudio, _cmbPreset })
            c.Enabled = !busy;
        _cmbQuality.Enabled = !busy && _rdoQuality.Checked;
        _numTarget.Enabled = !busy && _rdoTarget.Checked;
        if (busy) _progress.Value = 0;
        UpdatePlayerButtons();
    }

    private static void DeletePassLogs(string log)
    {
        try
        {
            var dir = Path.GetDirectoryName(log)!;
            foreach (var f in Directory.GetFiles(dir, Path.GetFileName(log) + "*")) TryDelete(f);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string LastErrorLines(string err)
    {
        var lines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "（詳細不明）" : string.Join("\n", lines.TakeLast(5));
    }

    // ---------------- 書式 ----------------

    public static string FormatTime(double sec)
    {
        if (sec < 0) sec = 0;
        var ts = TimeSpan.FromMilliseconds(Math.Round(sec * 1000));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    /// <summary>"90", "1:30", "1:30.5", "0:01:30.25" などを秒に変換する。</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Replace('：', ':').Replace('．', '.').Split(':');
        if (parts.Length is < 1 or > 3) return false;
        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}

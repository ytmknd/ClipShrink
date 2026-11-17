using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClipShrink;

/// <summary>WebView2 の HTML5 video を使った簡易プレーヤー（音声付き再生・範囲再生）。</summary>
public sealed class VideoPlayer : UserControl
{
    private const string AppHost = "app.example";
    private const string MediaHost = "media.example";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private readonly Label _overlay = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.Gainsboro, BackColor = Color.Black, Text = L.T("プレーヤーを準備しています...", "Preparing the player..."),
    };
    private string? _sourcePath;
    private int _sourceSerial;

    public bool IsReady { get; private set; }
    /// <summary>プレーヤーの現在位置（秒）。</summary>
    public double CurrentTime { get; private set; }
    public event Action<double>? TimeChanged;
    /// <summary>再生できなかったとき。引数はエラーの詳細。</summary>
    public event Action<string>? PlaybackError;

    /// <summary>プレーヤーの HTML と圧縮プレビュー用ファイルを置く一時フォルダー。</summary>
    public static string WorkFolder { get; } = Path.Combine(Path.GetTempPath(), "ClipShrink_preview");

    public VideoPlayer()
    {
        BackColor = Color.Black;
        Controls.Add(_web);
        Controls.Add(_overlay);
        _overlay.BringToFront();
    }

    public async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(WorkFolder);
            File.WriteAllText(Path.Combine(WorkFolder, "player.html"), PlayerHtml);

            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipShrink", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(AppHost, WorkFolder, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnMessage;
            // 元の動画はフォルダー割り当てではなく自前で配信する（シーク用の Range 要求にも対応）
            core.AddWebResourceRequestedFilter($"https://{MediaHost}/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;

            var tcs = new TaskCompletionSource();
            void Done(object? s, CoreWebView2NavigationCompletedEventArgs e) { core.NavigationCompleted -= Done; tcs.TrySetResult(); }
            core.NavigationCompleted += Done;
            core.Navigate($"https://{AppHost}/player.html");
            await tcs.Task;

            IsReady = true;
            _overlay.Text = L.T("動画を開くとここで再生できます", "Open a video to play it here");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _overlay.Text = L.T(
                "プレビュー再生には Microsoft Edge WebView2 ランタイムが必要です。\n（トリムと書き出しはそのまま使えます）",
                "Preview playback needs the Microsoft Edge WebView2 Runtime.\n(Trimming and export still work.)");
        }
        catch (Exception ex)
        {
            _overlay.Text = L.T("プレーヤーを初期化できませんでした。\n", "Could not initialize the player.\n") + ex.Message;
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "time":
                    CurrentTime = root.GetProperty("t").GetDouble();
                    TimeChanged?.Invoke(CurrentTime);
                    break;
                case "error":
                    PlaybackError?.Invoke(root.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "");
                    break;
            }
        }
        catch { /* 不正なメッセージは無視 */ }
    }

    /// <summary>元の動画を読み込む。</summary>
    public Task LoadSourceAsync(string path, double seekTo)
    {
        if (!IsReady) return Task.CompletedTask;
        _sourcePath = Path.GetFullPath(path);
        _sourceSerial++;
        return LoadUrlAsync($"https://{MediaHost}/{_sourceSerial}/{Uri.EscapeDataString(Path.GetFileName(path))}", seekTo, autoplay: false);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = _web.CoreWebView2.Environment;
        var path = _sourcePath;
        if (path is null || !File.Exists(path))
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        try
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            long length = fs.Length;
            string type = ContentType(path);

            string range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : "";
            if (TryParseRange(range, length, out long from, out long to))
            {
                long count = to - from + 1;
                fs.Seek(from, SeekOrigin.Begin);
                Stream body = fs;
                if (to < length - 1)
                {
                    // 途中までの範囲指定はその分だけ読んで返す
                    var buf = new byte[count];
                    fs.ReadExactly(buf);
                    fs.Dispose();
                    body = new MemoryStream(buf);
                }
                e.Response = env.CreateWebResourceResponse(body, 206, "Partial Content",
                    $"Content-Type: {type}\r\nAccept-Ranges: bytes\r\nContent-Length: {count}\r\n" +
                    $"Content-Range: bytes {from}-{to}/{length}");
            }
            else
            {
                e.Response = env.CreateWebResourceResponse(fs, 200, "OK",
                    $"Content-Type: {type}\r\nAccept-Ranges: bytes\r\nContent-Length: {length}");
            }
        }
        catch (Exception ex)
        {
            e.Response = env.CreateWebResourceResponse(null, 500, "Error", "");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private static bool TryParseRange(string header, long length, out long from, out long to)
    {
        from = 0; to = length - 1;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || length <= 0) return false;
        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var a = spec[..dash].Trim();
        var b = spec[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            // bytes=-N（末尾 N バイト）
            if (!long.TryParse(b, out var suffix) || suffix <= 0) return false;
            from = Math.Max(0, length - suffix);
            return true;
        }
        if (!long.TryParse(a, out from) || from >= length) return false;
        if (b.Length > 0 && long.TryParse(b, out var end)) to = Math.Min(end, length - 1);
        return to >= from;
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        _ => "video/mp4",
    };

    /// <summary>作業フォルダー内のファイル（圧縮プレビュー・プレビュー用変換）を読み込む。</summary>
    public Task LoadWorkFileAsync(string fileName, double seekTo, bool autoplay) =>
        LoadUrlAsync($"https://{AppHost}/{Uri.EscapeDataString(fileName)}", seekTo, autoplay);

    /// <summary>プレーヤー画面の中央にメッセージを表示する（空文字で消す）。</summary>
    public Task ShowMessageAsync(string text) => Run($"setMessage({JsonSerializer.Serialize(text)})");

    private async Task LoadUrlAsync(string url, double seekTo, bool autoplay)
    {
        _overlay.Visible = false;
        CurrentTime = seekTo;
        await Run($"load({JsonSerializer.Serialize(url)},{Num(seekTo)},{(autoplay ? "true" : "false")})");
    }

    public Task SeekAsync(double t) => Run($"seek({Num(t)})");
    public Task PlayRangeAsync(double from, double to) => Run($"playRange({Num(from)},{Num(to)})");
    public Task StopAsync() => Run("stop()");

    private async Task Run(string script)
    {
        if (!IsReady || _web.CoreWebView2 is null) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private const string PlayerHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8">
        <style>
          html,body{margin:0;height:100%;background:#000;overflow:hidden}
          video{width:100%;height:100%;object-fit:contain;background:#000;outline:none}
          #msg{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;
               color:#ddd;font:14px "Yu Gothic UI",sans-serif;text-align:center;white-space:pre-line;pointer-events:none}
        </style></head>
        <body>
        <video id="v" controls preload="auto"></video>
        <div id="msg"></div>
        <script>
          const v = document.getElementById('v'), msg = document.getElementById('msg');
          let stopAt = null;
          const post = o => window.chrome.webview.postMessage(o);
          function load(src, t, autoplay) {
            stopAt = null; msg.textContent = '';
            v.src = src; v.load();
            v.addEventListener('loadedmetadata', () => {
              v.currentTime = t || 0;
              if (autoplay) v.play();
            }, { once: true });
          }
          function setMessage(text) { msg.textContent = text; }
          function seek(t) { stopAt = null; v.pause(); v.currentTime = t; }
          function playRange(a, b) { stopAt = b; v.currentTime = a; v.play(); }
          function stop() { stopAt = null; v.pause(); }
          function tick() {
            if (stopAt !== null && !v.paused && v.currentTime >= stopAt) {
              v.pause(); v.currentTime = stopAt; stopAt = null;
            }
            requestAnimationFrame(tick);
          }
          requestAnimationFrame(tick);
          const report = () => post({ type: 'time', t: v.currentTime });
          v.addEventListener('timeupdate', report);
          v.addEventListener('seeked', report);
          v.addEventListener('pause', report);
          v.addEventListener('error', () => {
            if (!v.getAttribute('src')) return;
            const e = v.error;
            post({ type: 'error', detail: e ? (e.code + ' ' + (e.message || '')) : '' });
          });
        </script>
        </body></html>
        """;
}

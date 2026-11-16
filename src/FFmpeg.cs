using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipShrink;

/// <summary>ffmpeg -i の出力から読み取った動画情報（ffprobe 不要）。</summary>
public sealed class MediaInfo
{
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public string VideoCodec { get; set; } = "";
    public bool HasAudio { get; set; }
    public int BitrateKbps { get; set; }
    public bool HasVideo => VideoCodec.Length > 0;
}

public static class FFmpeg
{
    /// <summary>アプリと同じフォルダーに同梱した ffmpeg。</summary>
    public static string BundledPath { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    /// <summary>同梱版が無いときの予備の場所。</summary>
    public const string FallbackPath = @"D:\WebMtoMP4Converter\ffmpeg.exe";

    /// <summary>使用する ffmpeg（同梱版を優先）。</summary>
    public static string DefaultPath => File.Exists(BundledPath) || !File.Exists(FallbackPath) ? BundledPath : FallbackPath;

    private static readonly Regex RxDuration = new(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex RxBitrate = new(@"Duration:.*?bitrate:\s*(\d+)\s*kb/s", RegexOptions.Compiled);
    private static readonly Regex RxVideo = new(@"Stream #\d+:\d+.*?:\s*Video:\s*([A-Za-z0-9_]+)([^\r\n]*)", RegexOptions.Compiled);
    private static readonly Regex RxAudio = new(@"Stream #\d+:\d+.*?:\s*Audio:", RegexOptions.Compiled);
    private static readonly Regex RxSize = new(@"\b(\d{2,5})x(\d{2,5})\b", RegexOptions.Compiled);
    private static readonly Regex RxFps = new(@",\s*(\d+(?:\.\d+)?)\s*fps", RegexOptions.Compiled);

    private static ProcessStartInfo CreatePsi(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { }
    }

    public static async Task<MediaInfo> ProbeAsync(string exe, string file)
    {
        using var p = new Process { StartInfo = CreatePsi(exe, ["-hide_banner", "-i", file]) };
        p.Start();
        p.StandardInput.Close();
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        await Task.WhenAll(errTask, outTask).ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return Parse(errTask.Result);
    }

    public static MediaInfo Parse(string text)
    {
        var info = new MediaInfo();
        var ci = CultureInfo.InvariantCulture;

        var d = RxDuration.Match(text);
        if (d.Success)
            info.Duration = int.Parse(d.Groups[1].Value, ci) * 3600 + int.Parse(d.Groups[2].Value, ci) * 60
                            + double.Parse(d.Groups[3].Value, ci);

        var b = RxBitrate.Match(text);
        if (b.Success) info.BitrateKbps = int.Parse(b.Groups[1].Value, ci);

        var v = RxVideo.Match(text);
        if (v.Success)
        {
            info.VideoCodec = v.Groups[1].Value.ToLowerInvariant();
            var rest = v.Groups[2].Value;
            var sz = RxSize.Match(rest);
            if (sz.Success)
            {
                info.Width = int.Parse(sz.Groups[1].Value, ci);
                info.Height = int.Parse(sz.Groups[2].Value, ci);
            }
            var fps = RxFps.Match(rest);
            if (fps.Success) info.Fps = double.Parse(fps.Groups[1].Value, ci);
        }

        info.HasAudio = RxAudio.IsMatch(text);
        return info;
    }

    /// <summary>指定秒のフレームを 1 枚 PNG で取り出す。取れなければ null。</summary>
    public static async Task<Bitmap?> GrabFrameAsync(string exe, string file, double seconds, int width, CancellationToken ct)
    {
        string[] args =
        [
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-ss", Sec(Math.Max(0, seconds)), "-i", file,
            "-frames:v", "1", "-an", "-vf", $"scale={width}:-2",
            "-f", "image2pipe", "-c:v", "png", "-",
        ];
        using var p = new Process { StartInfo = CreatePsi(exe, args) };
        p.Start();
        p.StandardInput.Close();
        using var reg = ct.Register(() => KillQuietly(p));

        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, CancellationToken.None);
        var err = p.StandardError.ReadToEndAsync();
        try
        {
            await Task.WhenAll(copy, err).ConfigureAwait(false);
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch { return null; }

        if (ct.IsCancellationRequested || ms.Length == 0) return null;
        try
        {
            ms.Position = 0;
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    /// <summary>
    /// ffmpeg を実行し、-progress pipe:1 の out_time を秒で通知する。
    /// 戻り値は終了コードと stderr の末尾。
    /// </summary>
    public static async Task<(int ExitCode, string ErrorTail)> RunAsync(
        string exe, IReadOnlyList<string> args, Action<double> onSeconds, CancellationToken ct)
    {
        using var p = new Process { StartInfo = CreatePsi(exe, args) };
        var tail = new Queue<string>();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(e.Data.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) &&
                us >= 0)
            {
                onSeconds(us / 1_000_000.0);
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (tail)
            {
                tail.Enqueue(e.Data.Trim());
                while (tail.Count > 30) tail.Dequeue();
            }
        };

        if (!p.Start()) return (-1, "ffmpeg を起動できませんでした");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.StandardInput.Close();

        using (ct.Register(() => KillQuietly(p)))
        {
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        string err;
        lock (tail) err = string.Join(Environment.NewLine, tail);
        return (p.ExitCode, err);
    }

    public static string Sec(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}

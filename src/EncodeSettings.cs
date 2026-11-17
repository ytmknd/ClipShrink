using System.Globalization;

namespace ClipShrink;

public enum SizeMode { Quality, TargetSize }

public sealed class EncodeSettings
{
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public double Length => Math.Max(0, End - Start);

    public SizeMode Mode { get; set; } = SizeMode.Quality;
    /// <summary>画質優先モードの CRF（大きいほど小さく粗い）。</summary>
    public int Crf { get; set; } = 28;
    /// <summary>目標サイズモードの目標ファイルサイズ (MB)。</summary>
    public double TargetMB { get; set; } = 10;

    public bool Hevc { get; set; }
    /// <summary>libx264/libx265 の preset。</summary>
    public string Preset { get; set; } = "medium";
    /// <summary>縮小後の高さ。0 なら元のまま。</summary>
    public int MaxHeight { get; set; }
    /// <summary>フレームレート上限。0 なら元のまま。</summary>
    public double MaxFps { get; set; }
    /// <summary>音声ビットレート (kbps)。0 なら音声を削除。</summary>
    public int AudioKbps { get; set; } = 96;
    /// <summary>目標サイズモードで映像ビットレートを固定したいとき（圧縮プレビュー用）。0 なら自動計算。</summary>
    public int VideoKbpsOverride { get; set; }
}

public sealed record EncodePass(string Label, List<string> Args, string? PassLogFile = null);

public static class ArgumentBuilder
{
    /// <summary>目標サイズから映像ビットレート (kbps) を計算する。</summary>
    public static int VideoKbpsForTarget(EncodeSettings s, MediaInfo info)
    {
        if (s.VideoKbpsOverride > 0) return s.VideoKbpsOverride;
        double len = Math.Max(0.1, s.Length);
        double totalKbps = s.TargetMB * 1024 * 1024 * 8 / 1000 / len;
        int audio = info.HasAudio ? s.AudioKbps : 0;
        // コンテナや音声のオーバーヘッド分として 5% 余裕を持たせる
        double video = totalKbps * 0.95 - audio;
        return (int)Math.Max(30, Math.Floor(video));
    }

    public static List<EncodePass> Build(EncodeSettings s, MediaInfo info)
    {
        var passes = new List<EncodePass>();

        if (s.Mode == SizeMode.TargetSize)
        {
            int vk = VideoKbpsForTarget(s, info);
            if (!s.Hevc)
            {
                // H.264 は 2 パスで目標サイズに近づける
                var log = Path.Combine(Path.GetTempPath(), "ClipShrink_" + Guid.NewGuid().ToString("N"));
                var p1 = Common(s, info, includeAudio: false);
                p1.AddRange(VideoCodecArgs(s));
                p1.AddRange(["-b:v", $"{vk}k", "-pass", "1", "-passlogfile", log, "-f", "null", "NUL"]);
                passes.Add(new EncodePass(L.T("解析中 (1/2)", "Analyzing (1/2)"), p1, log));

                var p2 = Common(s, info, includeAudio: true);
                p2.AddRange(VideoCodecArgs(s));
                p2.AddRange(["-b:v", $"{vk}k", "-maxrate", $"{(int)(vk * 1.5)}k", "-bufsize", $"{vk * 2}k",
                             "-pass", "2", "-passlogfile", log]);
                p2.AddRange(AudioArgs(s, info));
                p2.AddRange(Tail(s));
                passes.Add(new EncodePass(L.T("書き出し中 (2/2)", "Encoding (2/2)"), p2, log));
            }
            else
            {
                var p = Common(s, info, includeAudio: true);
                p.AddRange(VideoCodecArgs(s));
                p.AddRange(["-b:v", $"{vk}k", "-maxrate", $"{(int)(vk * 1.2)}k", "-bufsize", $"{vk * 2}k"]);
                p.AddRange(AudioArgs(s, info));
                p.AddRange(Tail(s));
                passes.Add(new EncodePass(L.T("書き出し中", "Encoding"), p));
            }
        }
        else
        {
            var p = Common(s, info, includeAudio: true);
            p.AddRange(VideoCodecArgs(s));
            p.AddRange(["-crf", s.Crf.ToString(CultureInfo.InvariantCulture)]);
            p.AddRange(AudioArgs(s, info));
            p.AddRange(Tail(s));
            passes.Add(new EncodePass(L.T("書き出し中", "Encoding"), p));
        }
        return passes;
    }

    private static List<string> Common(EncodeSettings s, MediaInfo info, bool includeAudio)
    {
        var a = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-ss", FFmpeg.Sec(s.Start), "-i", s.Input,
            "-t", FFmpeg.Sec(s.Length),
            "-map", "0:v:0",
        };
        if (includeAudio && info.HasAudio && s.AudioKbps > 0) a.AddRange(["-map", "0:a:0?"]);
        else a.Add("-an");

        var filters = new List<string>();
        if (s.MaxHeight > 0 && info.Height > s.MaxHeight)
            filters.Add($"scale=-2:{s.MaxHeight}");
        else if (info.Width % 2 != 0 || info.Height % 2 != 0)
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2"); // yuv420p は偶数サイズ必須
        if (s.MaxFps > 0 && (info.Fps <= 0 || info.Fps > s.MaxFps + 0.01))
            filters.Add("fps=" + FFmpeg.Sec(s.MaxFps));
        if (filters.Count > 0) a.AddRange(["-vf", string.Join(",", filters)]);

        a.AddRange(["-progress", "pipe:1", "-nostats"]);
        return a;
    }

    private static List<string> VideoCodecArgs(EncodeSettings s)
    {
        var a = new List<string> { "-c:v", s.Hevc ? "libx265" : "libx264", "-preset", s.Preset, "-pix_fmt", "yuv420p" };
        if (s.Hevc) a.AddRange(["-tag:v", "hvc1", "-x265-params", "log-level=error"]);
        return a;
    }

    private static List<string> AudioArgs(EncodeSettings s, MediaInfo info) =>
        info.HasAudio && s.AudioKbps > 0 ? ["-c:a", "aac", "-b:a", $"{s.AudioKbps}k"] : [];

    private static List<string> Tail(EncodeSettings s) => ["-movflags", "+faststart", s.Output];
}

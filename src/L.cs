using System.Globalization;

namespace ClipShrink;

/// <summary>
/// UI メッセージの言語切り替え。Windows の表示言語が日本語なら日本語、それ以外は英語。
/// 環境変数 CLIPSHRINK_LANG=ja / en で強制できる（動作確認用）。
/// </summary>
internal static class L
{
    public static bool Ja { get; } = Detect();

    private static bool Detect()
    {
        var forced = Environment.GetEnvironmentVariable("CLIPSHRINK_LANG");
        if (!string.IsNullOrWhiteSpace(forced))
            return forced.Trim().StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
    }

    /// <summary>日本語環境なら ja、それ以外なら en を返す。</summary>
    public static string T(string ja, string en) => Ja ? ja : en;

    /// <summary>UI フォント名。</summary>
    public static string FontName => Ja ? "Yu Gothic UI" : "Segoe UI";

    /// <summary>情報表示の区切り。</summary>
    public static string Sep => Ja ? "　" : "   ";
}

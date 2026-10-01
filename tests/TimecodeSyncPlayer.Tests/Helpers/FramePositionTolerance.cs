using System.Globalization;
using System.Text.RegularExpressions;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// E2E の位置の許容（1 フレーム）。判定の対象のトラックの実の fps から作る（29.97 = 30000/1001 を 30 に丸めない）。
/// 生成したプロジェクトは frameRate を持たず、以前は先頭トラックの「frameRate が無ければ 30」で 1/30 s にしていたため、
/// 29.97 の素材で 1 フレーム（0.03337 s）のずれが落ちていた（検証機の R-1・R-5、2026-10-02）。
/// </summary>
internal static class FramePositionTolerance
{
    /// <summary>fps が分からないときの値（プロジェクトの既定と同じ）。</summary>
    public const double FallbackFps = 30.0;

    /// <summary>
    /// 浮動小数の丸めの余裕（1 マイクロ秒）。位置はフレーム番号を同じ fps で秒にするので、ちょうど 1 フレームのずれが
    /// 許容と同じ値になり、丸めでわずかに超えることがある。どの素材の 1 フレームよりも十分小さい。
    /// </summary>
    public const double EpsilonSeconds = 1e-6;

    private static readonly Regex MetaLineFps = new(@"(\d+(?:\.\d+)?)\s*fps", RegexOptions.CultureInvariant);

    /// <summary>1 フレームの秒数。fps が分からなければ 30fps。</summary>
    public static double FrameSeconds(double fps) =>
        1.0 / (double.IsFinite(fps) && fps > 0 ? fps : FallbackFps);

    /// <summary>位置の許容（1 フレーム + 丸めの余裕）。</summary>
    public static double OneFrame(double fps) => FrameSeconds(fps) + EpsilonSeconds;

    /// <summary>
    /// 位置の許容を、これまでの値より狭めない形で作る: max(これまでの値, 実の 1 フレーム + 丸めの余裕)。
    /// 29.97・25・24 では広がり、30fps 以上ではこれまでどおり（狭める向きは別に決める）。
    /// </summary>
    public static double OneFrameAtLeast(double fps, double previousSeconds) =>
        Math.Max(previousSeconds, OneFrame(fps));

    public static bool IsWithinOneFrame(double observedSeconds, double expectedSeconds, double fps) =>
        double.IsFinite(observedSeconds) && Math.Abs(observedSeconds - expectedSeconds) <= OneFrame(fps);

    /// <summary>アプリのメタデータ行（MetaLineText、例 "29.970 fps"）から、読み込んでいる素材の fps を読む。</summary>
    public static bool TryParseMetaLineFps(string? metaLine, out double fps)
    {
        fps = 0.0;
        if (string.IsNullOrEmpty(metaLine))
            return false;
        Match rate = MetaLineFps.Match(metaLine);
        if (!rate.Success)
            return false;
        fps = double.Parse(rate.Groups[1].Value, CultureInfo.InvariantCulture);
        return fps > 0;
    }
}

using System.Globalization;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// V3 LTC fps マトリクス（24 / 25 / 29.97 / 30）の入力解釈。
/// スクリプトが渡すトークンを実 fps とアプリの LtcFpsModeCombo インデックスへ写す。
/// 0=Auto, 1=Fixed24, 2=Fixed25, 3=Fixed29_97, 4=Fixed30（SyncViewModel と同じ）。
/// </summary>
internal static class V3LtcFpsMatrix
{
    public const string FpsEnvironmentVariable = "TCS_V3_LTC_FPS";
    public const string ModeEnvironmentVariable = "TCS_V3_LTC_FPS_MODE";

    private const double Fps29_97 = 30000.0 / 1001.0;
    private const string Token24 = "24";
    private const string Token25 = "25";
    private const string Token29_97 = "29.97";
    private const string Token30 = "30";
    private const string ModeAuto = "auto";
    private const string ModeFixed = "fixed";

    public static double ResolveFps(string? token) =>
        string.IsNullOrEmpty(token) ? 25.0
        : token == Token24 ? 24.0
        : token == Token25 ? 25.0
        : token == Token29_97 ? Fps29_97
        : token == Token30 ? 30.0
        : throw new ArgumentException(
            $"Unsupported LTC fps token '{token}'. Use 24, 25, 29.97 or 30.", nameof(token));

    /// <summary>
    /// "fixed" のときは fps に対応する固定モードのコンボインデックスを返す。
    /// "auto"（既定）は 0。
    /// </summary>
    public static int ResolveFpsModeIndex(string? token, double fps)
    {
        string mode = string.IsNullOrEmpty(token) ? ModeAuto : token.ToLowerInvariant();
        return mode switch
        {
            ModeAuto => 0,
            ModeFixed => fps switch
            {
                _ when IsFps(fps, 24.0) => 1,
                _ when IsFps(fps, 25.0) => 2,
                _ when IsFps(fps, Fps29_97) => 3,
                _ when IsFps(fps, 30.0) => 4,
                _ => throw new ArgumentException($"No fixed LTC fps mode for {fps}.", nameof(fps))
            },
            _ => throw new ArgumentException(
                $"Unsupported LTC fps mode '{token}'. Use auto or fixed.", nameof(token))
        };
    }

    /// <summary>アプリの計測トレースへ渡す参照 fps（インバリアント表記）。</summary>
    public static string FormatReferenceFps(double fps) =>
        fps.ToString("R", CultureInfo.InvariantCulture);

    private static bool IsFps(double left, double right) => Math.Abs(left - right) < 0.01;
}

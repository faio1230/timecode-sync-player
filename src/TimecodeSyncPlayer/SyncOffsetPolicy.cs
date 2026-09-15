using System.Globalization;

namespace TimecodeSyncPlayer;

/// <summary>
/// T3: 全体に効く同期オフセット（入力側の遅延と下流の遅延の両方を吸収する）。
/// 単位は ms。プラスで映像が先行し、下流（LED ウォール等）の遅延を補正する向き。
/// 適用は同期の入口で 1 回だけ行い、判定・シーク・切替・ギャップのすべてに同じ値が効く。
/// </summary>
public static class SyncOffsetPolicy
{
    public const double MinimumMilliseconds = -1000.0;
    public const double MaximumMilliseconds = 1000.0;
    public const double DefaultMilliseconds = 0.0;

    public static bool IsOutOfRange(double milliseconds) =>
        !double.IsFinite(milliseconds) ||
        milliseconds < MinimumMilliseconds ||
        milliseconds > MaximumMilliseconds;

    /// <summary>範囲外と非有限値を範囲内（非有限は既定 0）へ収める。</summary>
    public static double Clamp(double milliseconds) =>
        double.IsFinite(milliseconds)
            ? Math.Clamp(milliseconds, MinimumMilliseconds, MaximumMilliseconds)
            : DefaultMilliseconds;

    public static double ToSeconds(double milliseconds) => milliseconds / 1000.0;

    /// <summary>同期入口で 1 回だけ足す。</summary>
    public static double Apply(double seconds, double offsetMilliseconds) =>
        seconds + ToSeconds(Clamp(offsetMilliseconds));

    /// <summary>
    /// UI 表示。単位は ms のみとし、フレーム換算は出さない
    /// （異なる fps のタイムコードと素材を実時間で吸収する設計のため）。
    /// </summary>
    public static string FormatMilliseconds(double milliseconds) =>
        Clamp(milliseconds).ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture) + " ms";
}

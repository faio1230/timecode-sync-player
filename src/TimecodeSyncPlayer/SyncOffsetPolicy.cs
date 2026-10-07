using System.Globalization;
using System.Text;

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
        FormatInput(milliseconds) + " ms";

    /// <summary>R-9: 数値の入力欄に出す文字（単位なし）。表示の文字と同じ符号の付け方にする。</summary>
    public static string FormatInput(double milliseconds) =>
        Clamp(milliseconds).ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);

    /// <summary>
    /// R-9: 数値の入力欄の解釈。受けるのは ms の整数だけ。
    /// 前後の空白と先頭の + / - は許す。全角の数字・符号・空白は半角に直してから読む。
    /// 小数・指数・桁区切り・数字以外・空は拒む（呼び出し側は前の値に戻す）。
    /// 範囲の外はここでは丸めない（値を返し、反映する側が <see cref="Clamp"/> で丸める）。
    /// </summary>
    public static bool TryParseInput(string? text, out double milliseconds)
    {
        milliseconds = DefaultMilliseconds;
        if (text is null)
            return false;

        string normalized = text.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length == 0)
            return false;

        int start = normalized[0] is '+' or '-' ? 1 : 0;
        if (start == normalized.Length)
            return false;
        for (int i = start; i < normalized.Length; i++)
        {
            if (normalized[i] is < '0' or > '9')
                return false;
        }

        double value = double.Parse(normalized, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        // 桁があふれて無限大になったら、範囲の外の大きな値として扱う（Clamp は非有限を 0 にするため）。
        if (double.IsInfinity(value))
            value = value > 0 ? double.MaxValue : double.MinValue;
        milliseconds = value + 0.0; // "-0" を +0 にそろえる
        return true;
    }
}

using System.IO;

namespace Erytheia;

/// <summary>
/// v0.6.7: ログのファイルの名前。アプリは erytheia-YYYYMMDD.log、shim は erytheia-gst-YYYYMMDD.log に書く。
/// v0.6.6 までの名前（timecodesyncplayer-*.log・tcs-gst-*.log）は書かないが、読む側と 7 日の掃除は新旧の両方を見る。
/// erytheia-*.log には shim のログも当たるので、アプリのログは <see cref="IsAppLog"/> で見分ける。
/// </summary>
internal static class LogFileNames
{
    public const string AppPrefix = "erytheia-";
    public const string ShimPrefix = "erytheia-gst-";
    public const string LegacyAppPrefix = "timecodesyncplayer-";
    public const string LegacyShimPrefix = "tcs-gst-";

    /// <summary>Serilog の日ごとのファイルの元の名前（Serilog が日付を足して erytheia-YYYYMMDD.log にする）。</summary>
    public static string SerilogPath(string logDirectory) => Path.Combine(logDirectory, AppPrefix + ".log");

    public static string AppFileName(DateTime day) => $"{AppPrefix}{day:yyyyMMdd}.log";

    public static string ShimFileName(DateTime day) => $"{ShimPrefix}{day:yyyyMMdd}.log";

    /// <summary>アプリのログ（新旧）。shim のログ（erytheia-gst-*.log）は入れない。</summary>
    public static bool IsAppLog(string fileName)
    {
        string name = Path.GetFileName(fileName);
        if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.StartsWith(LegacyAppPrefix, StringComparison.OrdinalIgnoreCase))
            return true;
        return name.StartsWith(AppPrefix, StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith(ShimPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// アプリのログを古い順に並べる鍵。名前の日付、同じ日は旧の名前を先（v0.6.6 から上げた日は旧の名前の方が先に書かれる。
    /// 旧の名前の続き _001 なども新しい名前より先）、その後に日付の後ろ（Serilog が同じ日に作る _001 などの続き）。
    /// </summary>
    public static string AppLogSortKey(string fileName)
    {
        string name = Path.GetFileName(fileName);
        bool legacy = name.StartsWith(LegacyAppPrefix, StringComparison.OrdinalIgnoreCase);
        string rest = name[(legacy ? LegacyAppPrefix.Length : AppPrefix.Length)..];
        string day = rest.Length >= 8 ? rest[..8] : rest;
        return day + "|" + (legacy ? "0" : "1") + "|" + rest;
    }

    /// <summary>shim のログ（新旧）。</summary>
    public static bool IsShimLog(string fileName)
    {
        string name = Path.GetFileName(fileName);
        return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith(ShimPrefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(LegacyShimPrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>起動時の掃除で shim のログ（新旧）を残す期間（v0.6.6 から同じ）。</summary>
    public static readonly TimeSpan ShimLogRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// 起動時の掃除で v0.6.6 までの名前のアプリのログを残す期間。v0.6.6 までは Serilog の既定（31 ファイル）で
    /// 残っていた。現場の報告は logs の zip で受け取るので、7 日では消さない（最後に書かれてから 31 日）。
    /// </summary>
    public static readonly TimeSpan LegacyAppLogRetention = TimeSpan.FromDays(31);

    /// <summary>
    /// 起動時の掃除で、最後に書かれてからこの期間を過ぎたら消す。対象でなければ null。
    /// shim のログ（新旧）は 7 日、v0.6.6 までのアプリのログは 31 日。新しいアプリのログは Serilog が数を保つので対象外。
    /// </summary>
    public static TimeSpan? StartupRetention(string fileName)
    {
        if (IsShimLog(fileName))
            return ShimLogRetention;
        string name = Path.GetFileName(fileName);
        if (name.StartsWith(LegacyAppPrefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return LegacyAppLogRetention;
        return null;
    }
}

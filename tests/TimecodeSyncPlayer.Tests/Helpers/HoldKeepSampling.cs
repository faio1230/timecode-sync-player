namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// v0.6.4 設計書 3-1（#12 C-2 の黒の診断回）: C-2 の hold の判定（黒でない・参照が合う）が失敗した後も、
/// <see cref="Duration"/> の間 <see cref="Interval"/> ごとに採取を続け、黒が戻るか・いつ戻るかを harness の記録に残す。
/// 試験だけの口で、既定（<see cref="EnvironmentVariable"/> が無い）では採取しない。合否は変えない
/// （採取の後に、これまでと同じ判定をそのまま行う）。合否の回（一式）では使わない。
/// </summary>
internal static class HoldKeepSampling
{
    /// <summary>"1" のときだけ有効（名前に TEST を入れる。設計書 7 節）。</summary>
    public const string EnvironmentVariable = "TCS_TEST_C2_KEEP_SAMPLING";

    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    public static bool IsEnabled(Func<string, string?> getEnvironmentVariable) =>
        getEnvironmentVariable(EnvironmentVariable) == "1";

    /// <summary>口が有効で、C-2 の hold（名前が c2-）の判定が失敗するときだけ採取する。</summary>
    public static bool ShouldRun(bool enabled, string holdName, bool holdFailed) =>
        enabled && holdFailed && holdName.StartsWith("c2-", StringComparison.Ordinal);

    /// <summary><see cref="Interval"/> 待っては 1 回採取し、<see cref="Duration"/> に達するまで続ける。</summary>
    public static IReadOnlyList<T> Collect<T>(Func<T> take, Func<double> elapsedSeconds, Action<TimeSpan> sleep)
    {
        var samples = new List<T>();
        while (elapsedSeconds() < Duration.TotalSeconds - 1e-9)
        {
            sleep(Interval);
            samples.Add(take());
        }
        return samples;
    }
}

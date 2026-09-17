using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D25-b: fence 未完了のリースの保留規則。OutputEngine はこの規則で
/// 「未完了 → 次 tick で完了 → 描画」「3 秒超過 → D28 経路」を決める。
/// </summary>
public class GstFencePendingPolicyTests
{
    [Fact]
    public void PendingTickSequence_HoldsUntilTheFenceCompletes()
    {
        long frequency = Stopwatch.Frequency;
        long start = 1_000;

        // tick 1: 初回は未完了なので保留（リースは返さず保持）。
        GstFencePendingPolicy.Decide(fenceComplete: false, hasPending: false, 0, start, frequency)
            .Should().Be(GstFencePendingAction.Hold);

        // tick 2: まだ未完了。保留のまま（Held を描く）。
        GstFencePendingPolicy.Decide(fenceComplete: false, hasPending: true, start, start + frequency / 60, frequency)
            .Should().Be(GstFencePendingAction.Hold);

        // tick 3: 完了したので同じリースを描画・公開する。
        GstFencePendingPolicy.Decide(fenceComplete: true, hasPending: true, start, start + 2 * frequency / 60, frequency)
            .Should().Be(GstFencePendingAction.Draw);
    }

    [Fact]
    public void PendingTickSequence_TimesOutAtThreeSeconds()
    {
        long frequency = Stopwatch.Frequency;
        long start = 5_000;

        GstFencePendingPolicy.Decide(fenceComplete: false, hasPending: true, start,
            start + (long)(frequency * 2.999), frequency).Should().Be(GstFencePendingAction.Hold);
        GstFencePendingPolicy.Decide(fenceComplete: false, hasPending: true, start,
            start + (long)(frequency * 3.0), frequency).Should().Be(GstFencePendingAction.Timeout);
    }

    [Fact]
    public void PendingTickSequence_CompleteWinsOverTimeout()
    {
        long frequency = Stopwatch.Frequency;
        long start = 7_000;

        // 3 秒を過ぎていても完了していれば描く（D28 と同じで完了が優先）。
        GstFencePendingPolicy.Decide(fenceComplete: true, hasPending: true, start,
            start + (long)(frequency * 10.0), frequency).Should().Be(GstFencePendingAction.Draw);
    }
}

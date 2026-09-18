using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 0.4.5-A フェーズ 1: 評価位置（shadow）の純ロジック。着地済みはクエリ値、
/// 着地未確認は配信 PTS + 実測レート（上限は素材フレーム 2 枚）で外挿する。
/// </summary>
public class PlaybackPositionFeedbackTests
{
    private static PlaybackPositionSample Sample(
        double seconds, ulong generation, double deliveredSeconds,
        ulong deliveredGeneration, ulong currentGeneration) =>
        new(seconds, PlaybackPositionBasis.Pipeline, generation,
            deliveredSeconds, deliveredGeneration, currentGeneration);

    [Fact]
    public void Landed_UsesQuerySecondsAndConfirmsLanding()
    {
        var feedback = new PlaybackPositionFeedback(() => 0, Stopwatch.Frequency);

        PlaybackPositionReading reading = feedback.Observe(Sample(10.0, 5, 9.98, 5, 5), 25.0);

        reading.EvaluationSeconds.Should().Be(10.0);
        reading.Basis.Should().Be(PlaybackPositionBasis.Pipeline);
        reading.LandingConfirmed.Should().BeTrue();
    }

    [Fact]
    public void Unlanded_FreezesAtDeliveredAndCapsAtTwoFrames()
    {
        long qpc = 0;
        var feedback = new PlaybackPositionFeedback(() => qpc, Stopwatch.Frequency);
        feedback.Observe(Sample(11.0, 5, 10.0, 4, 5), 25.0);

        qpc += Stopwatch.Frequency / 100;                       // +10ms
        PlaybackPositionReading early = feedback.Observe(Sample(11.02, 5, 10.0, 4, 5), 25.0);
        early.EvaluationSeconds.Should().BeApproximately(10.010, 1e-6);
        early.Basis.Should().Be(PlaybackPositionBasis.Delivered);
        early.LandingConfirmed.Should().BeFalse();

        qpc += Stopwatch.Frequency / 2;                         // +500ms（上限を大きく超える）
        PlaybackPositionReading capped = feedback.Observe(Sample(11.52, 5, 10.0, 4, 5), 25.0);
        capped.EvaluationSeconds.Should().BeApproximately(10.0 + (2.0 / 25.0), 1e-6);
    }

    [Fact]
    public void Cap_FollowsVideoFps()
    {
        long qpc = 0;
        var feedback = new PlaybackPositionFeedback(() => qpc, Stopwatch.Frequency);
        feedback.Observe(Sample(1.0, 2, 0.5, 1, 2), 60.0);
        qpc += Stopwatch.Frequency;                             // +1s
        feedback.Observe(Sample(2.0, 2, 0.5, 1, 2), 60.0)
            .EvaluationSeconds.Should().BeApproximately(0.5 + (2.0 / 60.0), 1e-6);
    }

    [Fact]
    public void Rate_IsMeasuredFromDeliveredProgression()
    {
        long qpc = 0;
        var feedback = new PlaybackPositionFeedback(() => qpc, Stopwatch.Frequency);
        feedback.Observe(Sample(0, 1, 10.0, 1, 1), 25.0);

        qpc += 40 * Stopwatch.Frequency / 1000;
        feedback.Observe(Sample(0, 1, 10.05, 1, 1), 25.0);      // 0.05s / 40ms = 1.25

        feedback.HasMeasuredRate.Should().BeTrue();
        feedback.Rate.Should().BeApproximately(1.25, 1e-9);
    }

    [Fact]
    public void BackwardsDelivered_ResetsRate()
    {
        long qpc = 0;
        var feedback = new PlaybackPositionFeedback(() => qpc, Stopwatch.Frequency);
        feedback.Observe(Sample(0, 1, 10.0, 1, 1), 25.0);
        qpc += 40 * Stopwatch.Frequency / 1000;
        feedback.Observe(Sample(0, 1, 10.05, 1, 1), 25.0);
        feedback.Rate.Should().BeApproximately(1.25, 1e-9);

        qpc += 40 * Stopwatch.Frequency / 1000;
        feedback.Observe(Sample(0, 1, 9.0, 1, 1), 25.0);        // 逆行（ロード・巻き戻し）

        feedback.HasMeasuredRate.Should().BeFalse();
        feedback.Rate.Should().Be(1.0);
    }

    [Fact]
    public void WithoutDelivered_UsesQueryAndDoesNotConfirmLanding()
    {
        var feedback = new PlaybackPositionFeedback(() => 0, Stopwatch.Frequency);

        PlaybackPositionReading reading = feedback.Observe(
            new PlaybackPositionSample(5.0, PlaybackPositionBasis.Pipeline, 1, 0, 0, 1), 25.0);

        reading.EvaluationSeconds.Should().Be(5.0);
        reading.Basis.Should().Be(PlaybackPositionBasis.Pipeline);
        reading.LandingConfirmed.Should().BeFalse();
    }
}

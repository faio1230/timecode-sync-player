using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>D37-b: シーク中・着地未確認の再生位置を信用しない判定の固定。</summary>
public sealed class PlaybackPositionTrustTests
{
    [Fact]
    public void StartsTrusted()
    {
        var trust = new PlaybackPositionTrust();

        trust.IsTrusted.Should().BeTrue();
        trust.IsReacquiring.Should().BeFalse();
        trust.Observe(1.0, 1.0).Should().BeTrue();
    }

    [Fact]
    public void PendingSeek_MakesPositionUntrustedUntilLanded()
    {
        var trust = new PlaybackPositionTrust();
        trust.InvalidateForPendingSeek();

        trust.IsTrusted.Should().BeFalse();
        trust.Observe(1.0, 1.0).Should().BeFalse("保留中は位置を観測しない");

        trust.MarkLanded();

        trust.IsTrusted.Should().BeTrue("着地が確認できたらその場で再開する");
        trust.IsReacquiring.Should().BeFalse();
    }

    [Fact]
    public void Timeout_RequiresThreeStableSamples()
    {
        var trust = new PlaybackPositionTrust();
        trust.RequireReacquire();

        double now = 0.0;
        double position = 10.0;
        trust.Observe(position, now).Should().BeFalse("前のサンプルが無い");
        for (int i = 1; i <= 2; i++)
        {
            now += 0.1;
            position += 0.1;
            trust.Observe(position, now).Should().BeFalse($"{i} サンプル目");
        }

        now += 0.1;
        position += 0.1;
        trust.Observe(position, now).Should().BeTrue("3 サンプル続けば再開する");
    }

    [Fact]
    public void UnstableRate_ResetsTheStableCount()
    {
        var trust = new PlaybackPositionTrust();
        trust.RequireReacquire();

        double now = 0.0;
        trust.Observe(10.0, now);
        now += 0.1;
        trust.Observe(10.1, now);
        now += 0.1;
        trust.Observe(10.2, now);
        trust.StableSamples.Should().Be(2);

        // 実時間 0.1 秒に対して 0.5 秒進んだ（レート 5.0）→ 不安定として数え直す。
        now += 0.1;
        trust.Observe(10.7, now).Should().BeFalse();
        trust.StableSamples.Should().Be(0);

        now += 0.1;
        trust.Observe(10.8, now).Should().BeFalse();
        now += 0.1;
        trust.Observe(10.9, now).Should().BeFalse();
        now += 0.1;
        trust.Observe(11.0, now).Should().BeTrue();
    }

    [Fact]
    public void Reset_RestoresTrust_AfterTimeout()
    {
        var trust = new PlaybackPositionTrust();
        trust.RequireReacquire();

        trust.Reset();

        trust.IsTrusted.Should().BeTrue();
        trust.IsReacquiring.Should().BeFalse();
    }
}

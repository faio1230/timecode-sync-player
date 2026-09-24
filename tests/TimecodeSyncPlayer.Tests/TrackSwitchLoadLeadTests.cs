using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TrackSwitchLoadLeadTests
{
    private static readonly Guid TrackA = Guid.NewGuid();
    private static readonly Guid TrackB = Guid.NewGuid();

    // 先回り lead で読み込み、最初の評価で residual が残った切替を 1 回。
    private static void Switch(TrackSwitchLoadLead lead, Guid track, double usedLead, double residual)
    {
        lead.MarkLoadSent(track, usedLead);
        lead.ObserveFirstResidual(track, residual);
    }

    [Fact]
    public void Unlearned_IsZero()
    {
        new TrackSwitchLoadLead(enabled: true).LeadForTrack(TrackA).Should().Be(0.0);
    }

    [Fact]
    public void FirstSwitch_IsColdAndNotUsed()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Switch(lead, TrackA, 0.0, 2.4);

        lead.LeadForTrack(TrackA).Should().Be(0.0);

        Switch(lead, TrackA, 0.0, 0.06);
        lead.LeadForTrack(TrackA).Should().BeApproximately(0.06, 1e-9);
    }

    [Fact]
    public void Needed_IsUsedLeadPlusResidual()
    {
        // 候補 1 の M1: 0.15 秒先回りして、映像が 0.10 秒先に出た（residual −0.10）→ 必要だったのは 0.05 秒。
        var lead = new TrackSwitchLoadLead(enabled: true);
        Switch(lead, TrackA, 0.0, 0.06);            // 冷えた 1 回目
        Switch(lead, TrackA, 0.15, -0.10);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public void Lead_IsMedianOfRecentWarmSwitches()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Switch(lead, TrackA, 0.0, 1.3);
        foreach (double r in new[] { 1.24, 1.25, 1.37, 1.23, 1.26 })
            Switch(lead, TrackA, 0.0, r);

        lead.LeadForTrack(TrackA).Should().BeApproximately(1.25, 1e-9, "外れ値 1.37 に引っ張られない");
    }

    [Fact]
    public void Lead_ForgetsOlderThanWindow()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Switch(lead, TrackA, 0.0, 1.0);
        for (int i = 0; i < TrackSwitchLoadLead.WarmWindow; i++)
            Switch(lead, TrackA, 0.0, 0.9);
        for (int i = 0; i < TrackSwitchLoadLead.WarmWindow; i++)
            Switch(lead, TrackA, 0.0, 0.3);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.3, 1e-9);
    }

    [Fact]
    public void Tracks_AreLearnedSeparately()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Switch(lead, TrackA, 0.0, 2.0);
        Switch(lead, TrackB, 0.0, 2.0);
        Switch(lead, TrackA, 0.0, 0.06);
        Switch(lead, TrackB, 0.0, 1.25);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.06, 1e-9);
        lead.LeadForTrack(TrackB).Should().BeApproximately(1.25, 1e-9);
    }

    [Theory]
    [InlineData(-0.3)]                                       // 必要量が負（読み込み中に LTC が戻ったなど）
    [InlineData(TrackSwitchLoadLead.MaxLeadSeconds + 0.5)]   // 上限超え
    public void OutOfRangeNeeded_IsNotLearned(double residual)
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Switch(lead, TrackA, 0.0, 0.2);
        Switch(lead, TrackA, 0.0, 0.2);
        Switch(lead, TrackA, 0.0, residual);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void ResidualForAnotherTrack_OrWithoutSwitch_IsIgnored()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Switch(lead, TrackA, 0.0, 1.0);
        Switch(lead, TrackA, 0.0, 0.2);

        lead.ObserveFirstResidual(TrackA, 0.9);              // 測定中でない
        lead.MarkLoadSent(TrackA, 0.2);
        lead.ObserveFirstResidual(TrackB, 0.9);              // 別のトラック（測定は終わる）
        lead.ObserveFirstResidual(TrackA, 0.9);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void CancelledMeasurement_IsNotRecorded()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Switch(lead, TrackA, 0.0, 1.0);
        Switch(lead, TrackA, 0.0, 0.2);
        lead.MarkLoadSent(TrackA, 0.2);
        lead.CancelMeasurement();
        lead.ObserveFirstResidual(TrackA, 2.0);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void Disabled_AlwaysZero()
    {
        var lead = new TrackSwitchLoadLead(enabled: false);
        Switch(lead, TrackA, 0.0, 1.0);
        Switch(lead, TrackA, 0.0, 0.5);

        lead.LeadForTrack(TrackA).Should().Be(0.0);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData(" OFF ", false)]
    public void EnvironmentValue_OnlyOffDisables(string? value, bool expected)
    {
        TrackSwitchLoadLead.IsEnabledValue(value).Should().Be(expected);
    }
}

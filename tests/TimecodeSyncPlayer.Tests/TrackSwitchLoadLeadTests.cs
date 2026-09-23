using System.Diagnostics;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TrackSwitchLoadLeadTests
{
    private static readonly Guid TrackA = Guid.NewGuid();
    private static readonly Guid TrackB = Guid.NewGuid();

    private int _generation;

    private void Load(TrackSwitchLoadLead lead, Guid track, double seconds)
    {
        lead.MarkLoadSent(track, 1_000);
        lead.ObserveFrameReady(1_000 + (long)(seconds * Stopwatch.Frequency), ++_generation, sourceSequence: 1);
    }

    [Fact]
    public void Unlearned_IsZero()
    {
        new TrackSwitchLoadLead(enabled: true).LeadForTrack(TrackA).Should().Be(0.0);
    }

    [Fact]
    public void FirstLoad_IsColdAndNotUsed()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Load(lead, TrackA, 2.4);

        lead.LeadForTrack(TrackA).Should().Be(0.0);

        Load(lead, TrackA, 0.15);
        lead.LeadForTrack(TrackA).Should().BeApproximately(0.15, 1e-6);
    }

    [Fact]
    public void Lead_IsMedianOfRecentWarmLoads()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Load(lead, TrackA, 1.3);                       // 冷えた 1 回目
        foreach (double s in new[] { 1.24, 1.25, 1.37, 1.23, 1.26 })
            Load(lead, TrackA, s);

        lead.LeadForTrack(TrackA).Should().BeApproximately(1.25, 1e-6, "外れ値 1.37 に引っ張られない");
    }

    [Fact]
    public void Lead_ForgetsOlderThanWindow()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Load(lead, TrackA, 1.0);
        for (int i = 0; i < TrackSwitchLoadLead.WarmWindow; i++)
            Load(lead, TrackA, 0.9);
        for (int i = 0; i < TrackSwitchLoadLead.WarmWindow; i++)
            Load(lead, TrackA, 0.3);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.3, 1e-6);
    }

    [Fact]
    public void Tracks_AreLearnedSeparately()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Load(lead, TrackA, 2.0);
        Load(lead, TrackB, 2.0);
        Load(lead, TrackA, 0.06);
        Load(lead, TrackB, 1.25);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.06, 1e-6);
        lead.LeadForTrack(TrackB).Should().BeApproximately(1.25, 1e-6);
    }

    [Fact]
    public void LeadAboveMax_IsNotUsed()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Load(lead, TrackA, 5.0);
        Load(lead, TrackA, TrackSwitchLoadLead.MaxLeadSeconds + 0.5);

        lead.LeadForTrack(TrackA).Should().Be(0.0);
    }

    [Fact]
    public void FrameFromBeforeTheLoad_IsNotCounted()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        lead.ObserveFrameReady(500, generation: 5, sourceSequence: 10);   // 読み込み前の絵
        lead.MarkLoadSent(TrackA, 1_000);
        lead.ObserveFrameReady(1_100, generation: 5, sourceSequence: 10);  // 同じ絵がもう一度
        lead.ObserveFrameReady(1_000 + (long)(0.4 * Stopwatch.Frequency), generation: 6, sourceSequence: 1);
        lead.MarkLoadSent(TrackA, 2_000);
        lead.ObserveFrameReady(2_000 + (long)(0.2 * Stopwatch.Frequency), generation: 7, sourceSequence: 1);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.2, 1e-6, "1 回目（0.4）は冷えた状態、2 回目が 0.2");
    }

    [Fact]
    public void CancelledMeasurement_IsNotRecorded()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        Load(lead, TrackA, 1.0);
        Load(lead, TrackA, 0.2);
        lead.MarkLoadSent(TrackA, 1_000);
        lead.CancelMeasurement();
        lead.ObserveFrameReady(1_000 + (long)(3.0 * Stopwatch.Frequency), ++_generation, sourceSequence: 1);

        lead.LeadForTrack(TrackA).Should().BeApproximately(0.2, 1e-6);
    }

    [Fact]
    public void Disabled_AlwaysZero()
    {
        var lead = new TrackSwitchLoadLead(enabled: false);
        Load(lead, TrackA, 1.0);
        Load(lead, TrackA, 0.5);

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

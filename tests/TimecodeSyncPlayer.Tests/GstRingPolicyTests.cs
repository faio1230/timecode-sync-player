using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>ステージ 6b: 共有リングのリース選択とフェンス待ち順序（純粋規則）。</summary>
public class GstRingPolicyTests
{
    [Theory]
    [InlineData(-1, 3, true, (int)GstRingLeasePlan.UseLegacy)]
    [InlineData(-1, 0, false, (int)GstRingLeasePlan.UseLegacy)]
    [InlineData(0, 3, true, (int)GstRingLeasePlan.UseRing)]
    [InlineData(2, 3, true, (int)GstRingLeasePlan.UseRing)]
    [InlineData(3, 3, true, (int)GstRingLeasePlan.Reject)]
    [InlineData(0, 3, false, (int)GstRingLeasePlan.Reject)]
    [InlineData(0, 0, false, (int)GstRingLeasePlan.Reject)]
    public void Decide_SelectsRingLegacyOrReject(int slot, int ringCount, bool ringOpen, int expected)
        => GstRingPolicy.Decide(slot, ringCount, ringOpen).Should().Be((GstRingLeasePlan)expected);

    [Theory]
    [InlineData(0, 5, 4, true)]   // 新しい seq -> 待つ
    [InlineData(0, 5, 5, false)]  // 同じ seq -> 待ち直さない
    [InlineData(0, 4, 5, false)]  // 古い seq -> 待たない
    [InlineData(2, 1, -1, true)]  // 初回
    [InlineData(-1, 5, 4, false)] // 旧サンプル経路 -> フェンス無し
    public void ShouldWaitFence_OnlyForNewerRingSequence(int slot, long sequence, long lastWaited, bool expected)
        => GstRingPolicy.ShouldWaitFence(slot, sequence, lastWaited).Should().Be(expected);
}

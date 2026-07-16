using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapStateExitPolicyTests
{
    [Theory]
    [InlineData(false, SyncMode.Single, true)]   // Sync OFF + Single
    [InlineData(false, SyncMode.Continue, true)] // Sync OFF + Continue
    [InlineData(true, SyncMode.Single, true)]    // Sync ON + Single
    public void ShouldExit_GapActiveAndManualControl_ReturnsTrue(bool syncEnabled, SyncMode mode, bool _)
    {
        GapStateExitPolicy.ShouldExit(syncEnabled, mode, gapStateActive: true).Should().BeTrue();
    }

    [Fact]
    public void ShouldExit_GapActiveWithSyncOnContinue_ReturnsFalse()
    {
        // Sync ON + Continue はギャップ演出の正規の持ち主なので解除しない
        GapStateExitPolicy.ShouldExit(syncEnabled: true, SyncMode.Continue, gapStateActive: true)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(false, SyncMode.Single)]
    [InlineData(true, SyncMode.Continue)]
    public void ShouldExit_GapInactive_ReturnsFalse(bool syncEnabled, SyncMode mode)
    {
        GapStateExitPolicy.ShouldExit(syncEnabled, mode, gapStateActive: false).Should().BeFalse();
    }
}

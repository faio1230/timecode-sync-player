using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class PlaybackActivityLedgerTests
{
    private const long Freq = 1000;

    [Fact]
    public void RateIntegral_IsTheTimeWeightedCommandedRate()
    {
        long now = 0;
        var ledger = new PlaybackActivityLedger(() => now, Freq);

        now = 1000;                 // 1 秒、1.0 倍
        ledger.NoteRate(1.2);
        now = 2000;                 // 1 秒、1.2 倍
        ledger.NoteRate(0.8);
        now = 3000;                 // 1 秒、0.8 倍

        ledger.RateIntegralSeconds().Should().BeApproximately(3.0, 1e-9, "1.0 + 1.2 + 0.8");
    }

    [Fact]
    public void Disturbances_CountUp()
    {
        var ledger = new PlaybackActivityLedger(() => 0, Freq);
        ledger.NoteDisturbance();
        ledger.NoteDisturbance();
        ledger.Disturbances.Should().Be(2);
    }

    [Fact]
    public void AnInvalidRateIsIgnored()
    {
        long now = 0;
        var ledger = new PlaybackActivityLedger(() => now, Freq);
        ledger.NoteRate(double.NaN);
        ledger.NoteRate(0);
        now = 1000;
        ledger.RateIntegralSeconds().Should().BeApproximately(1.0, 1e-9);
    }
}

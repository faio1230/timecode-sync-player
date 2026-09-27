using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T9: 粗い同期シークの発行を外へ知らせる配線（SeekIssued）。
/// v0.5.4 B6b: 着地から 1.0 秒だけ Smooth の上限を ±0.20 にする窓は畳んだ（上限は ±0.10 の 1 つ。
/// 「relocate の直後の 1 サンプルは varispeed しない」に置き換え）。窓の 2 本は削除し、
/// 着地直後の収束は実機の前後比較で見る（設計 v0.5.4-gate-unification の B6b 区分表 §2 の T9 行）。
/// </summary>
public class T9ConvergenceTests
{
    [Fact]
    public void ReportSeekSent_RaisesSeekIssued()
    {
        var service = new TimecodeSyncService(new SyncDecisionEngine(), new TimecodeSyncSeekState());
        int issued = 0;
        service.SeekIssued += () => issued++;

        service.ReportSeekSent(1.0);

        issued.Should().Be(1);
    }
}

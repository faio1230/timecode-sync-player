using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests.Output;

/// <summary>
/// v0.6.4 段 3（設計書 2-2）: `Playback perf` の composedSourceFrames。GPU 経路で出力が新しいソース
/// フレーム（世代と通番の組が前回と違うもの）を合成した数を窓ごとに数える。同じフレームの再合成
/// （Held・Present だけの tick）は数えない。
/// </summary>
public class ComposedSourceFrameCounterTests
{
    [Fact]
    public void NewFrames_AreCountedOncePerWindow()
    {
        var c = new ComposedSourceFrameCounter();
        for (long seq = 1; seq <= 120; seq++)
            c.RecordCompose(drewSourceFrame: true, generation: 3, sequence: seq);

        c.Take().Should().Be(120);
        c.Take().Should().Be(0, "取り出すと窓を数え直す");
    }

    [Fact]
    public void RecomposingTheSameFrame_IsNotCounted()
    {
        var c = new ComposedSourceFrameCounter();
        c.RecordCompose(true, 3, 10);
        c.RecordCompose(true, 3, 10);   // 同じリースの再利用（同じフレームの再合成）
        c.RecordCompose(false, 3, 10);  // Held・黒・Freeze（ソースフレームを描いていない）
        c.RecordCompose(false, -1, -1);

        c.Take().Should().Be(1);
    }

    [Fact]
    public void NewGeneration_WithTheSameSequence_IsCounted()
    {
        var c = new ComposedSourceFrameCounter();
        c.RecordCompose(true, 3, 10);
        c.RecordCompose(true, 4, 10);   // シーク・ロードで世代が進んだ（通番は shim の都合で重なりうる）

        c.Take().Should().Be(2);
    }

    [Fact]
    public void HeldOnlyWindow_ReadsZero()
    {
        var c = new ComposedSourceFrameCounter();
        c.RecordCompose(true, 3, 10);
        c.Take().Should().Be(1);

        for (int i = 0; i < 120; i++)
            c.RecordCompose(true, 3, 10);   // 黒・止まりの窓: 同じフレームしか描いていない
        c.Take().Should().Be(0, "窓の間に新しいフレームの合成が 0 なら 0 と読める");
    }
}

using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.2 段 2e: <see cref="FileLoadState"/> の各メソッドが変える値と段階の遷移を 1 件ずつ。
/// 段階: なし／ロード中（開始時刻・開始位置・開始時の描画枚数）／解除の回収待ち（解除時刻）。
/// </summary>
public class FileLoadStateTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Begin_EntersLoadingStage()
    {
        var state = new FileLoadState();

        state.Begin(T0, startPositionSeconds: 12.5, startedRenderedFrames: 7);

        state.IsLoadingFile.Should().BeTrue();
        state.HasPendingRelease.Should().BeFalse();
        state.StartedAt.Should().Be(T0);
        state.StartPositionSeconds.Should().Be(12.5);
        state.StartedRenderedFrames.Should().Be(7);
    }

    [Fact]
    public void Release_MovesFromLoadingToReleasePending()
    {
        var state = new FileLoadState();
        state.Begin(T0, 12.5, 7);

        state.Release(T0.AddSeconds(2));

        state.IsLoadingFile.Should().BeFalse();
        state.HasPendingRelease.Should().BeTrue();
        state.ReleasedAt.Should().Be(T0.AddSeconds(2));
    }

    [Fact]
    public void MarkTimedOut_LeavesLoadingStageWithoutPending()
    {
        var state = new FileLoadState();
        state.Begin(T0, 12.5, 7);

        state.MarkTimedOut();

        state.IsLoadingFile.Should().BeFalse();
        state.HasPendingRelease.Should().BeFalse("安全タイムアウトは解除の回収待ちにしない");
    }

    [Fact]
    public void CollectRelease_LowersPendingAndKeepsReleasedAt()
    {
        var state = new FileLoadState();
        state.Begin(T0, 12.5, 7);
        state.Release(T0.AddSeconds(2));

        state.CollectRelease();

        state.HasPendingRelease.Should().BeFalse();
        state.ReleasedAt.Should().Be(T0.AddSeconds(2), "今のコードも解除時刻は残す");
    }

    [Fact]
    public void ClearReleasePending_LowersPendingOnly()
    {
        var state = new FileLoadState();
        state.Begin(T0, 12.5, 7);
        state.Release(T0.AddSeconds(2));
        state.Begin(T0.AddSeconds(3), 0.0, 0);   // 新しいロード中でも古い回収待ちは残る

        state.ClearReleasePending();

        state.HasPendingRelease.Should().BeFalse();
        state.IsLoadingFile.Should().BeTrue("新しいロードは続いている");
    }

    [Fact]
    public void OutOfStageReads_ReturnInitialValues()
    {
        var state = new FileLoadState();

        state.StartedAt.Should().Be(DateTime.MinValue);
        state.StartPositionSeconds.Should().Be(0.0);
        state.StartedRenderedFrames.Should().Be(0);
        state.ReleasedAt.Should().Be(DateTime.MinValue);
    }
}

using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapFrameCaptureCoordinatorTests
{
    [Fact]
    public void Decide_ReturnsNone_WhileNoFrameArrivedSinceCapture()
    {
        // D21: 進入・再ロード後、位置が目標でもフレーム到着前はキャプチャしない。
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: false,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 9.95,
            targetSeconds: 10.0,
            fps: 30.0,
            allowRedraw: true,
            frameSeenSinceCapture: false);

        result.Should().Be(GapFrameCaptureDecision.None);
    }

    [Fact]
    public void Decide_ReturnsRenderAndCapture_AfterFrameArrivedSinceCapture()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: false,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 9.95,
            targetSeconds: 10.0,
            fps: 30.0,
            allowRedraw: true,
            frameSeenSinceCapture: true);

        result.Should().Be(GapFrameCaptureDecision.RenderAndCapture);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenNoFrame()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: false,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 9.95,
            targetSeconds: 10.0,
            fps: 30.0);

        result.Should().Be(GapFrameCaptureDecision.None);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenPathIsUnexpected()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: true,
            isExpectedPath: false,
            hasTimePosition: true,
            actualPositionSeconds: 9.95,
            targetSeconds: 10.0,
            fps: 30.0);

        result.Should().Be(GapFrameCaptureDecision.None);
    }

    [Fact]
    public void Decide_ReturnsRenderAndCapture_WhenEnteringFreezeReachedTarget()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: true,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 9.95,
            targetSeconds: 10.0,
            fps: 30.0);

        result.Should().Be(GapFrameCaptureDecision.RenderAndCapture);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenEnteringFreezeHasNotReachedTarget()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: true,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 9.80,
            targetSeconds: 10.0,
            fps: 30.0);

        result.Should().Be(GapFrameCaptureDecision.None);
    }

    [Fact]
    public void Decide_ReturnsRenderAndCapture_WhenWaitingForFrameStepReachedTarget()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.WaitingForFrameStep,
            hasFrame: true,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 10.02,
            targetSeconds: 10.0,
            fps: 30.0);

        result.Should().Be(GapFrameCaptureDecision.RenderAndCapture);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenWaitingForFrameStepHasNotReachedTarget()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.WaitingForFrameStep,
            hasFrame: true,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 9.80,
            targetSeconds: 10.0,
            fps: 30.0);

        result.Should().Be(GapFrameCaptureDecision.None);
    }

    [Fact]
    public void Decide_UsesTheDeliveredFramePosition_WhenTheQueryIsOutsideTheWindow()
    {
        // C-4: シーク直後の照会はパイプライン値（尺 + 2 フレーム = 20.033）へ落ちることがある。
        // D21-b で受け入れた配信フレームの PTS（19.9833）で窓を見る。
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: true,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 20.0333,
            targetSeconds: 19.983333333333334,
            fps: 60.0,
            frameSeenSinceCapture: true,
            deliveredFramePositionSeconds: 19.983333333333334);

        result.Should().Be(GapFrameCaptureDecision.RenderAndCapture);
    }

    [Fact]
    public void Decide_UsesTheQuery_WhenNoDeliveredFramePositionIsKnown()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: true,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 20.0333,
            targetSeconds: 19.983333333333334,
            fps: 60.0,
            frameSeenSinceCapture: true);

        result.Should().Be(GapFrameCaptureDecision.None);
    }
}

using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapFrameCaptureCoordinatorTests
{
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
    public void Decide_ReturnsSendFrameStep_WhenEnteringFreezeReachedTarget()
    {
        GapFrameCaptureDecision result = GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze,
            hasFrame: true,
            isExpectedPath: true,
            hasTimePosition: true,
            actualPositionSeconds: 9.95,
            targetSeconds: 10.0,
            fps: 30.0);

        result.Should().Be(GapFrameCaptureDecision.SendFrameStep);
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
}

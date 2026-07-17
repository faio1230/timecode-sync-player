namespace TimecodeSyncPlayer;

internal sealed class DeferredGapFrameOperation
{
    private readonly GapRenderFrameDecision _expectedDecision;
    private readonly Func<GapRenderFrameDecision> _getCurrentDecision;
    private readonly Action _operation;

    public DeferredGapFrameOperation(
        GapRenderFrameDecision expectedDecision,
        Func<GapRenderFrameDecision> getCurrentDecision,
        Action operation)
    {
        _expectedDecision = expectedDecision;
        _getCurrentDecision = getCurrentDecision;
        _operation = operation;
    }

    public void RunIfCurrent()
    {
        if (_getCurrentDecision() == _expectedDecision)
            _operation();
    }
}

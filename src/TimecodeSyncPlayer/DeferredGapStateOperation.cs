namespace TimecodeSyncPlayer;

internal sealed class DeferredGapStateOperation
{
    private readonly GapState _expectedState;
    private readonly Func<GapState> _getCurrentState;
    private readonly Action _operation;

    public DeferredGapStateOperation(
        GapState expectedState,
        Func<GapState> getCurrentState,
        Action operation)
    {
        _expectedState = expectedState;
        _getCurrentState = getCurrentState;
        _operation = operation;
    }

    public void RunIfCurrent()
    {
        if (_getCurrentState() == _expectedState)
            _operation();
    }
}

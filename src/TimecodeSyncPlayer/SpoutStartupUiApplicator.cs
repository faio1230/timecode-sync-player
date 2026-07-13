namespace TimecodeSyncPlayer;

internal sealed class SpoutStartupUiApplicator
{
    private readonly Action<bool> _setButtonEnabled;
    private readonly Action<string> _setToggleLabel;

    public SpoutStartupUiApplicator(Action<bool> setButtonEnabled, Action<string> setToggleLabel)
    {
        _setButtonEnabled = setButtonEnabled;
        _setToggleLabel = setToggleLabel;
    }

    public void Apply(SpoutStartupState state)
    {
        _setButtonEnabled(state.IsButtonEnabled);
        if (state.ToggleLabel != null)
            _setToggleLabel(state.ToggleLabel);
    }
}

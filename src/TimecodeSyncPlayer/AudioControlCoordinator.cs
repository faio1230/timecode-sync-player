using System.Globalization;

namespace TimecodeSyncPlayer;

internal sealed record AudioControlSnapshot(
    bool IsMuted,
    double Volume,
    string MuteToggleLabel);

internal sealed class AudioControlState
{
    private const string MutedLabel = "MUTE ON";
    private const string UnmutedLabel = "MUTE OFF";

    public AudioControlState(bool isMuted, double volume)
    {
        IsMuted = isMuted;
        Volume = ClampVolume(volume);
    }

    public bool IsMuted { get; private set; }
    public double Volume { get; private set; }
    public string MuteToggleLabel => IsMuted ? MutedLabel : UnmutedLabel;

    public AudioControlSnapshot Snapshot => new(IsMuted, Volume, MuteToggleLabel);

    public void ToggleMute() => IsMuted = !IsMuted;

    public bool SetVolume(double volume)
    {
        double clamped = ClampVolume(volume);
        if (Volume.Equals(clamped))
            return false;

        Volume = clamped;
        return true;
    }

    private static double ClampVolume(double volume) =>
        double.IsFinite(volume) ? Math.Clamp(volume, 0, 100) : 100;
}

internal sealed record AudioControlEffects(
    Func<string, string, int> SetPropertyString,
    Action<AudioControlSnapshot> ApplyUi,
    Action<AudioControlSnapshot> Persist);

internal sealed class AudioControlCoordinator
{
    private readonly AudioControlState _state;
    private readonly AudioControlEffects _effects;

    public AudioControlCoordinator(AudioControlState state, AudioControlEffects effects)
    {
        _state = state;
        _effects = effects;
    }

    public AudioControlSnapshot State => _state.Snapshot;

    public void ApplyStartup()
    {
        _effects.SetPropertyString("mute", _state.IsMuted ? "yes" : "no");
        _effects.SetPropertyString("volume", FormatVolume(_state.Volume));
        _effects.ApplyUi(_state.Snapshot);
    }

    public void ToggleMute()
    {
        _state.ToggleMute();
        _effects.SetPropertyString("mute", _state.IsMuted ? "yes" : "no");
        PublishState();
    }

    public void SetVolume(double volume)
    {
        if (!_state.SetVolume(volume))
            return;

        _effects.SetPropertyString("volume", FormatVolume(_state.Volume));
        PublishState();
    }

    private void PublishState()
    {
        AudioControlSnapshot snapshot = _state.Snapshot;
        _effects.ApplyUi(snapshot);
        _effects.Persist(snapshot);
    }

    private static string FormatVolume(double volume) =>
        volume.ToString("0.###", CultureInfo.InvariantCulture);
}

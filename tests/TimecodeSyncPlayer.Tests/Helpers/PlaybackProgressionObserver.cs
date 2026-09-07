namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>Collects the increasing samples required by the playback-progression E2E wait.</summary>
internal sealed class PlaybackProgressionObserver
{
    private readonly List<double> _positions = [];

    public IReadOnlyList<double> Positions => _positions;

    public bool Observe(double current)
    {
        // A sync seek can rewind from an earlier clip position while playback recovers.
        // Require a fresh increasing run instead of retaining the old high-water mark.
        if (_positions.Count > 0 && current < _positions[^1] - 0.01)
            _positions.Clear();
        if (_positions.Count == 0 || current > _positions[^1] + 0.01)
            _positions.Add(current);
        return _positions.Count >= 3;
    }
}

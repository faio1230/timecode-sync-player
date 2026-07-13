namespace TimecodeSyncPlayer;

internal sealed class TimecodeFpsSelector : Contracts.ITimecodeFpsSelector
{
    private readonly int _confirmCount;
    private readonly int _changeCount;
    private double _lockedAutoFps;
    private double _candidateFps;
    private int _candidateCount;

    public TimecodeFpsSelector(int confirmCount = 3, int changeCount = 8)
    {
        _confirmCount = Math.Max(1, confirmCount);
        _changeCount = Math.Max(_confirmCount, changeCount);
    }

    public double Resolve(TimecodeFpsMode mode, double detectedFps, bool dropFrame)
    {
        double fixedFps = mode.ToFps();
        if (fixedFps > 0)
            return fixedFps;

        double normalized = NormalizeDetectedFps(detectedFps, dropFrame);
        if (normalized <= 0)
            return _lockedAutoFps;

        if (_lockedAutoFps <= 0)
            return ConfirmInitialAutoFps(normalized);

        if (IsSameFps(normalized, _lockedAutoFps))
        {
            _candidateFps = 0;
            _candidateCount = 0;
            return _lockedAutoFps;
        }

        if (!IsSameFps(normalized, _candidateFps))
        {
            _candidateFps = normalized;
            _candidateCount = 1;
            return _lockedAutoFps;
        }

        _candidateCount++;
        if (_candidateCount < _changeCount)
            return _lockedAutoFps;

        _lockedAutoFps = normalized;
        _candidateFps = 0;
        _candidateCount = 0;
        return _lockedAutoFps;
    }

    public void Reset()
    {
        _lockedAutoFps = 0;
        _candidateFps = 0;
        _candidateCount = 0;
    }

    private double ConfirmInitialAutoFps(double normalized)
    {
        if (!IsSameFps(normalized, _candidateFps))
        {
            _candidateFps = normalized;
            _candidateCount = 1;
            if (_candidateCount >= _confirmCount)
            {
                _lockedAutoFps = normalized;
                _candidateFps = 0;
                _candidateCount = 0;
                return _lockedAutoFps;
            }

            return 0.0;
        }

        _candidateCount++;
        if (_candidateCount < _confirmCount)
            return 0.0;

        _lockedAutoFps = normalized;
        _candidateFps = 0;
        _candidateCount = 0;
        return _lockedAutoFps;
    }

    private static double NormalizeDetectedFps(double detectedFps, bool dropFrame)
    {
        if (!double.IsFinite(detectedFps) || detectedFps <= 0)
            return 0.0;
        if (dropFrame)
            return 30000.0 / 1001.0;

        return detectedFps switch
        {
            < 24.5 => 24.0,
            < 27.5 => 25.0,
            _ => 30.0
        };
    }

    private static bool IsSameFps(double left, double right) =>
        Math.Abs(left - right) < 0.01;
}

public enum TimecodeFpsMode
{
    Auto,
    Fixed24,
    Fixed25,
    Fixed29_97,
    Fixed30
}

internal static class TimecodeFpsModeExtensions
{
    public static double ToFps(this TimecodeFpsMode mode) =>
        mode switch
        {
            TimecodeFpsMode.Fixed24 => 24.0,
            TimecodeFpsMode.Fixed25 => 25.0,
            TimecodeFpsMode.Fixed29_97 => 30000.0 / 1001.0,
            TimecodeFpsMode.Fixed30 => 30.0,
            _ => 0.0
        };
}

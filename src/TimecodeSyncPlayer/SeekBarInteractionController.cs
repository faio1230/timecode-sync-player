namespace TimecodeSyncPlayer;

internal sealed class SeekBarInteractionController
{
    public bool IsSeeking { get; private set; }
    public bool IsUpdatingFromPlayer { get; private set; }

    public void BeginSeek()
    {
        IsSeeking = true;
    }

    public void EndSeek()
    {
        IsSeeking = false;
    }

    public void BeginPlayerUpdate()
    {
        IsUpdatingFromPlayer = true;
    }

    public void EndPlayerUpdate()
    {
        IsUpdatingFromPlayer = false;
    }

    public SeekBarPointerUpdate TrySetFromPointer(double pointerX, double width, double oldValue, double durationSeconds)
    {
        if (!SeekBarUpdateState.IsUsableDuration(durationSeconds) || width <= 0)
            return new SeekBarPointerUpdate(false, oldValue);

        double newValue = SeekBarUpdateState.ToSliderValueFromPointer(pointerX, width, oldValue);
        return new SeekBarPointerUpdate(true, newValue);
    }

    public SeekBarPreview CreatePreview(double sliderValue, double durationSeconds)
    {
        if (IsUpdatingFromPlayer || !IsSeeking || !SeekBarUpdateState.IsUsableDuration(durationSeconds))
            return new SeekBarPreview(false, 0.0);

        return new SeekBarPreview(true, sliderValue * durationSeconds);
    }

    public SeekBarCommit CreateCommit(double sliderValue, double minimum, double maximum, double durationSeconds)
    {
        if (IsUpdatingFromPlayer || !SeekBarUpdateState.IsUsableDuration(durationSeconds))
            return new SeekBarCommit(false, sliderValue, 0.0);

        double boundedValue = Math.Clamp(sliderValue, minimum, maximum);
        return new SeekBarCommit(true, boundedValue, boundedValue * durationSeconds);
    }
}

internal sealed record SeekBarPointerUpdate(bool Applied, double SliderValue);

internal sealed record SeekBarPreview(bool HasValue, double PositionSeconds);

internal sealed record SeekBarCommit(bool ShouldCommit, double SliderValue, double TargetSeconds);

namespace TimecodeSyncPlayer;

/// <summary>
/// TimelinePanel の入力イベントをタイムライン操作へ変換する。
/// WPF のイベント引数やコントロール参照は保持しない。
/// </summary>
internal sealed class TimelineInputInterpreter
{
    private readonly TimelineInputEffects _effects;

    internal TimelineInputInterpreter(TimelineInputEffects effects)
    {
        _effects = effects;
    }

    internal void ZoomIn()
    {
        _effects.ZoomIn();
        _effects.UpdateScrollBarRanges();
    }

    internal void ZoomOut()
    {
        _effects.ZoomOut();
        _effects.UpdateScrollBarRanges();
    }

    internal void ScrollHorizontal(double newValue)
    {
        double? currentValue = _effects.GetHorizontalScrollSeconds();
        if (!currentValue.HasValue) return;

        double delta = newValue - currentValue.Value;
        _effects.ScrollHorizontal(delta);
        _effects.UpdateScrollBarRanges();
    }

    internal void ScrollVertical(double newValue)
    {
        int? currentValue = _effects.GetVerticalScrollOffset();
        if (!currentValue.HasValue) return;

        int delta = (int)newValue - currentValue.Value;
        _effects.ScrollVertical(delta);
        _effects.UpdateScrollBarRanges();
    }

    internal void RequestSeek(TimelineSeekEventArgs args)
    {
        _effects.RequestSeek(args);
    }
}

internal sealed record TimelineInputEffects(
    Action ZoomIn,
    Action ZoomOut,
    Func<double?> GetHorizontalScrollSeconds,
    Action<double> ScrollHorizontal,
    Func<int?> GetVerticalScrollOffset,
    Action<int> ScrollVertical,
    Action UpdateScrollBarRanges,
    Action<TimelineSeekEventArgs> RequestSeek);

namespace TimecodeSyncPlayer;

internal readonly record struct PlaylistDragPoint(double X, double Y);

/// <summary>
/// プレイリスト D&amp;D の開始判定・並べ替え副作用を調整する。
/// </summary>
internal sealed class PlaylistDragDropCoordinator
{
    private readonly PlaylistDragDropEffects _effects;
    private PlaylistDragPoint? _dragStartPoint;

    internal PlaylistDragDropCoordinator(PlaylistDragDropEffects effects)
    {
        _effects = effects;
    }

    internal void SetDragStart(double x, double y)
    {
        _dragStartPoint = new PlaylistDragPoint(x, y);
    }

    internal void HandleMouseMove(
        PlaylistTrack? track,
        double currentX,
        double currentY,
        bool isLeftButtonPressed,
        double minimumHorizontalDistance,
        double minimumVerticalDistance)
    {
        PlaylistDragPoint startPoint = _dragStartPoint.GetValueOrDefault();
        double dx = Math.Abs(currentX - startPoint.X);
        double dy = Math.Abs(currentY - startPoint.Y);
        if (!PlaylistDragInitiationPolicy.ShouldBeginDrag(
                _dragStartPoint.HasValue,
                isLeftButtonPressed,
                track != null,
                dx,
                dy,
                minimumHorizontalDistance,
                minimumVerticalDistance))
            return;

        _effects.BeginDrag(track!);
        _dragStartPoint = null;
    }

    internal static bool CanAcceptDrop(bool hasPlaylistTrackData) => hasPlaylistTrackData;

    internal bool HandleDrop(PlaylistTrack? draggedTrack, int hitIndex)
    {
        if (draggedTrack == null)
            return false;

        int fromIndex = _effects.IndexOf(draggedTrack);
        int toIndex = PlaylistDropTargetPolicy.ResolveTargetIndex(hitIndex, _effects.GetTrackCount());
        if (toIndex < 0)
            return false;

        if (!_effects.MoveTrack(fromIndex, toIndex)) return false;

        _effects.SetSelectedIndex(toIndex);
        _effects.UpdateCurrentTrackLabel();
        _effects.UpdatePlaylistTimelineDisplay();
        return true;
    }
}

internal sealed record PlaylistDragDropEffects(
    Action<PlaylistTrack> BeginDrag,
    Func<PlaylistTrack, int> IndexOf,
    Func<int> GetTrackCount,
    Func<int, int, bool> MoveTrack,
    Action<int> SetSelectedIndex,
    Action UpdateCurrentTrackLabel,
    Action UpdatePlaylistTimelineDisplay);

namespace TimecodeSyncPlayer;

internal sealed record ContinueModeEndAdvanceDecision(
    ContinueModeEndAdvanceAction Action,
    PlaylistTrack? NextTrack);

internal static class PlaylistEndAdvancePlanner
{
    public static ContinueModeEndAdvanceDecision Decide(
        IReadOnlyList<PlaylistTrack> tracks,
        Guid? loadedTrackId,
        double positionSeconds,
        bool alreadyTriggered,
        bool isPaused,
        bool isSeeking,
        double thresholdSeconds)
    {
        if (!loadedTrackId.HasValue)
            return new ContinueModeEndAdvanceDecision(ContinueModeEndAdvanceAction.None, null);

        int currentIndex = -1;
        PlaylistTrack? currentTrack = null;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Id != loadedTrackId.Value) continue;
            currentIndex = i;
            currentTrack = tracks[i];
            break;
        }

        if (currentTrack == null)
            return new ContinueModeEndAdvanceDecision(ContinueModeEndAdvanceAction.None, null);

        PlaylistTrack? nextTrack = null;
        for (int i = currentIndex + 1; i < tracks.Count; i++)
        {
            if (!tracks[i].IsEnabled) continue;
            nextTrack = tracks[i];
            break;
        }

        ContinueModeEndAdvanceAction action = ContinueModePlaybackPolicy.DecideEndAdvanceAction(
            positionSeconds,
            currentTrack.GetEffectiveDuration().TotalSeconds,
            hasNextEnabledTrack: nextTrack != null,
            alreadyTriggered,
            isPaused,
            isSeeking,
            thresholdSeconds);

        return new ContinueModeEndAdvanceDecision(
            action,
            action == ContinueModeEndAdvanceAction.LoadNextTrack ? nextTrack : null);
    }
}

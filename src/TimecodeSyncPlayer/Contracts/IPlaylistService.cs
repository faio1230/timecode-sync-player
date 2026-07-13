using System.Collections.ObjectModel;

namespace TimecodeSyncPlayer.Contracts;

internal interface IPlaylistService
{
    ObservableCollection<PlaylistTrack> Tracks { get; }
    void AddTrack(string path);
    bool RemoveAt(int index);
    void MoveTrack(int from, int to);
    void UpdateMediaDuration(Guid trackId, TimeSpan duration, bool recalculate = true);
    void RecalculateTimelineFrom(int index);
    PlaylistTrack? FindTrackById(Guid id);
    int FindIndexById(Guid id);
}

using System.Collections.ObjectModel;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

public sealed class PlaylistState : IPlaylistService
{
    public ObservableCollection<PlaylistTrack> Tracks { get; } = [];

    public int CurrentIndex { get; private set; } = -1;

    public PlaylistTrack? Current
        => CurrentIndex >= 0 && CurrentIndex < Tracks.Count
            ? Tracks[CurrentIndex]
            : null;

    public void AddTrack(string path)
    {
        int startIndex = Tracks.Count;
        bool wasEmpty = Tracks.Count == 0;

        Tracks.Add(new PlaylistTrack(
            Id: Guid.NewGuid(),
            FilePath: path,
            Name: System.IO.Path.GetFileNameWithoutExtension(path),
            MediaIn: TimeSpan.Zero,
            MediaOut: null,
            TimelineOffset: TimeSpan.Zero,
            MediaDuration: TimeSpan.Zero,
            SyncOffset: TimeSpan.Zero,
            FrameRate: null,
            IsEnabled: true));

        if (wasEmpty && Tracks.Count > 0)
            CurrentIndex = 0;

        Serilog.Log.Debug("AddTrack: path={Path} startIndex={StartIndex}", path, startIndex);
        RecalculateTimelineFrom(startIndex);
    }

    public void AddFiles(IEnumerable<string> paths, bool autoOffset = true)
    {
        int startIndex = Tracks.Count;
        bool wasEmpty = Tracks.Count == 0;

        foreach (string path in paths)
        {
            Tracks.Add(new PlaylistTrack(
                Id: Guid.NewGuid(),
                FilePath: path,
                Name: System.IO.Path.GetFileNameWithoutExtension(path),
                MediaIn: TimeSpan.Zero,
                MediaOut: null,
                TimelineOffset: TimeSpan.Zero,
                MediaDuration: TimeSpan.Zero,
                SyncOffset: TimeSpan.Zero,
                FrameRate: null,
                IsEnabled: true));
        }

        if (wasEmpty && Tracks.Count > 0)
            CurrentIndex = 0;

        Serilog.Log.Debug("AddFiles: count={Count} autoOffset={AutoOffset} startIndex={StartIndex}", 
            Tracks.Count - startIndex, autoOffset, startIndex);
        if (autoOffset)
            RecalculateTimelineFrom(startIndex);
    }

    public bool Select(int index)
    {
        if (index < 0 || index >= Tracks.Count)
            return false;

        CurrentIndex = index;
        return true;
    }

    public bool MoveNext()
    {
        if (CurrentIndex < 0 || CurrentIndex >= Tracks.Count - 1)
            return false;

        CurrentIndex++;
        return true;
    }

    public bool MovePrevious()
    {
        if (CurrentIndex <= 0 || CurrentIndex >= Tracks.Count)
            return false;

        CurrentIndex--;
        return true;
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= Tracks.Count)
            return false;

        Tracks.RemoveAt(index);

        if (Tracks.Count == 0)
        {
            CurrentIndex = -1;
        }
        else if (CurrentIndex == index)
        {
            CurrentIndex = Math.Min(index, Tracks.Count - 1);
        }
        else if (index < CurrentIndex)
        {
            CurrentIndex--;
        }
        else if (CurrentIndex >= Tracks.Count)
        {
            CurrentIndex = Tracks.Count - 1;
        }

        return true;
    }

    public bool MoveTrackUp(int index)
        => MoveTrack(index, index - 1);

    public bool MoveTrackDown(int index)
        => MoveTrack(index, index + 1);

    public bool MoveTrack(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Tracks.Count)
            return false;
        if (toIndex < 0 || toIndex >= Tracks.Count)
            return false;
        if (fromIndex == toIndex)
            return false;

        PlaylistTrack? current = Current;
        PlaylistTrack track = Tracks[fromIndex];
        Tracks.RemoveAt(fromIndex);
        Tracks.Insert(toIndex, track);

        CurrentIndex = current != null ? Tracks.IndexOf(current) : -1;
        return true;
    }

    void IPlaylistService.MoveTrack(int from, int to)
    {
        MoveTrack(from, to);
    }

    public void Clear()
    {
        Tracks.Clear();
        CurrentIndex = -1;
    }

    /// <summary>
    /// Timeline位置からアクティブトラックを探索する。
    /// playlist indexが小さい順に走査し、最初にヒットしたトラックを返す。
    /// </summary>
    public TimelineQueryResult FindTrackAtTimelinePosition(double timelineSeconds)
    {
        PlaylistTrack? previousTrack = null;
        bool hasEnabledTrack = false;

        for (int i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            if (!track.IsEnabled) continue;

            hasEnabledTrack = true;

            double tlIn = track.GetActualTimelineIn().TotalSeconds;
            double tlOut = track.GetActualTimelineOut().TotalSeconds;

            if (timelineSeconds >= tlIn && timelineSeconds < tlOut)
            {
                double mediaIn = track.MediaIn.TotalSeconds;
                double mediaOut = (track.MediaOut ?? track.MediaDuration).TotalSeconds;
                double syncOffset = track.SyncOffset.TotalSeconds;

                double mediaPos = (timelineSeconds - tlIn) + mediaIn + syncOffset;
                mediaPos = Math.Clamp(mediaPos, mediaIn, mediaOut);

                return new TimelineQueryResult(
                    TimelineQueryStatus.OnTrack,
                    track,
                    mediaPos,
                    null);
            }

            if (tlOut <= timelineSeconds)
                previousTrack = track;
        }

        if (!hasEnabledTrack)
            return new TimelineQueryResult(TimelineQueryStatus.NoTracks, null, 0, null);

        return new TimelineQueryResult(TimelineQueryStatus.Gap, null, 0, previousTrack);
    }

    /// <summary>
    /// 指定インデックス以降のトラックのTimelineOffsetを自動配置し直す。
    /// </summary>
    public void RecalculateTimelineFrom(int startIndex)
    {
        if (startIndex < 0 || startIndex >= Tracks.Count)
            return;

        Serilog.Log.Debug("RecalculateTimelineFrom: startIndex={StartIndex} trackCount={TrackCount}", startIndex, Tracks.Count);

        TimeSpan currentTime = startIndex > 0 ? Tracks[startIndex - 1].GetActualTimelineOut() : TimeSpan.Zero;

        for (int i = startIndex; i < Tracks.Count; i++)
        {
            var track = Tracks[i];
            TimeSpan duration = track.GetEffectiveDuration();
            TimeSpan oldOffset = track.TimelineOffset;

            var updated = track with
            {
                TimelineOffset = currentTime
            };

            Tracks[i] = updated;
            Serilog.Log.Debug("RecalculateTimelineFrom: track[{I}] name={Name} oldOffset={OldOffset:F3} newOffset={NewOffset:F3}",
                i, track.Name, oldOffset.TotalSeconds, currentTime.TotalSeconds);
            currentTime = updated.GetActualTimelineOut();
        }
    }

    /// <summary>
    /// 指定トラックのMediaDurationを更新し、以降のトラックのTimelineOffsetを再計算する。
    /// </summary>
    public void UpdateMediaDuration(Guid trackId, TimeSpan duration, bool recalculate = true)
    {
        int index = -1;
        for (int i = 0; i < Tracks.Count; i++)
        {
            if (Tracks[i].Id == trackId)
            {
                index = i;
                break;
            }
        }

        if (index < 0) return;

        var track = Tracks[index];
        Serilog.Log.Debug("UpdateMediaDuration: trackId={TrackId} index={Index} name={Name} oldDuration={OldDuration:F3} newDuration={NewDuration:F3} recalculate={Recalculate}",
            trackId, index, track.Name, track.MediaDuration.TotalSeconds, duration.TotalSeconds, recalculate);
        var updated = track with { MediaDuration = duration };

        Tracks[index] = updated;
        if (recalculate)
            RecalculateTimelineFrom(index);
    }

    public PlaylistTrack? FindTrackById(Guid id)
    {
        foreach (var track in Tracks)
        {
            if (track.Id == id)
                return track;
        }
        return null;
    }

    public int FindIndexById(Guid id)
    {
        for (int i = 0; i < Tracks.Count; i++)
        {
            if (Tracks[i].Id == id)
                return i;
        }
        return -1;
    }
}

using System;

namespace TimecodeSyncPlayer;

/// <summary>
/// タイムラインのクリックシーク要求イベントの引数（v0.6.6 F-2 からドラッグを離したときも）。
/// </summary>
internal sealed class TimelineSeekEventArgs : EventArgs
{
    /// <summary>
    /// シーク目標時間（秒）。
    /// クリップのTimelineOffset + クリック位置の相対時間で計算される。
    /// </summary>
    public double TargetSeconds { get; }

    /// <summary>
    /// クリックされたトラックのインデックス。
    /// </summary>
    public int TrackIndex { get; }

    /// <summary>
    /// v0.6.6 F-2: タイムラインのドラッグ（スクラブ）を離したときの要求か（スクラブの最後の 1 本）。
    /// ただのクリックは false。
    /// </summary>
    public bool EndsScrub { get; }

    public TimelineSeekEventArgs(double targetSeconds, int trackIndex, bool endsScrub = false)
    {
        TargetSeconds = targetSeconds;
        TrackIndex = trackIndex;
        EndsScrub = endsScrub;
    }
}

/// <summary>
/// v0.6.6 F-2: タイムラインのドラッグ（スクラブ）の途中の目標。行は押したときの行に固定。
/// <see cref="Started"/> はドラッグが始まった最初の動き。
/// </summary>
internal sealed class TimelineScrubEventArgs : EventArgs
{
    public double TargetSeconds { get; }

    public int TrackIndex { get; }

    public bool Started { get; }

    public TimelineScrubEventArgs(double targetSeconds, int trackIndex, bool started)
    {
        TargetSeconds = targetSeconds;
        TrackIndex = trackIndex;
        Started = started;
    }
}

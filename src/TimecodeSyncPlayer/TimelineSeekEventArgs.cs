using System;

namespace TimecodeSyncPlayer;

/// <summary>
/// タイムラインのクリックシーク要求イベントの引数。
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

    public TimelineSeekEventArgs(double targetSeconds, int trackIndex)
    {
        TargetSeconds = targetSeconds;
        TrackIndex = trackIndex;
    }
}

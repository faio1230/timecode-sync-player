using System;

namespace TimecodeSyncPlayer;

/// <summary>
/// タイムラインの表示状態（ズームレベル、スクロール位置、表示/非表示）を管理します。
/// </summary>
internal sealed class TimelineDisplayState
{
    private double _horizontalScrollSeconds;
    private int _verticalScrollOffset;
    private double _secondsPerPixel = 1.0;
    public const double DefaultTrackHeight = 24;
    public const double TrackHeightMin = 12;
    public const double TrackHeightMax = 80;
    public const double SecondsPerPixelMin = 0.01;
    public const double SecondsPerPixelMax = 60.0;
    public const double ZoomFactor = 1.2;

    private double _trackHeight = DefaultTrackHeight;
    private bool _isVisible;

    /// <summary>
    /// 水平スクロール位置（秒単位）。時間軸の左端の時間。
    /// </summary>
    public double HorizontalScrollSeconds
    {
        get => _horizontalScrollSeconds;
        set => _horizontalScrollSeconds = Math.Max(0, value);
    }

    /// <summary>
    /// 垂直スクロールオフセット（トラック単位）。表示開始トラックのインデックス。
    /// </summary>
    public int VerticalScrollOffset
    {
        get => _verticalScrollOffset;
        set => _verticalScrollOffset = Math.Max(0, value);
    }

    /// <summary>
    /// 1ピクセルあたりの秒数。値が小さいほどズームイン。
    /// 最小: 0.01秒/px（最大ズーム）、最大: 60秒/px（最小ズーム）
    /// </summary>
    public double SecondsPerPixel
    {
        get => _secondsPerPixel;
        set => _secondsPerPixel = Math.Clamp(value, SecondsPerPixelMin, SecondsPerPixelMax);
    }

    /// <summary>
    /// トラック行の高さ（ピクセル）。
    /// 範囲: 12px 〜 80px
    /// </summary>
    public double TrackHeight
    {
        get => _trackHeight;
        set => _trackHeight = Math.Clamp(value, TrackHeightMin, TrackHeightMax);
    }

    /// <summary>
    /// タイムラインの表示状態。
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => _isVisible = value;
    }

    /// <summary>
    /// 現在の表示範囲（秒）。
    /// </summary>
    public double VisibleSeconds(double timelineWidth) => timelineWidth * _secondsPerPixel;

    /// <summary>
    /// 水平ズームイン（1ノッチ）。
    /// </summary>
    public void ZoomIn(double pivotSeconds)
    {
        var oldSeconds = _secondsPerPixel;
        _secondsPerPixel = Math.Max(SecondsPerPixelMin, _secondsPerPixel / ZoomFactor);
        AdjustScrollForZoom(oldSeconds, pivotSeconds);
    }

    /// <summary>
    /// 水平ズームアウト（1ノッチ）。
    /// </summary>
    public void ZoomOut(double pivotSeconds)
    {
        var oldSeconds = _secondsPerPixel;
        _secondsPerPixel = Math.Min(SecondsPerPixelMax, _secondsPerPixel * ZoomFactor);
        AdjustScrollForZoom(oldSeconds, pivotSeconds);
    }

    /// <summary>
    /// ズーム変更時にスクロール位置を調整し、ピボット位置の時間を維持します。
    /// </summary>
    private void AdjustScrollForZoom(double oldSecondsPerPixel, double pivotSeconds)
    {
        // pivotSecondsは画面内の相対位置（秒）
        // ズーム前後でpivotの絶対時間が変わらないようにスクロール位置を調整
        var pivotAbsoluteTime = _horizontalScrollSeconds + pivotSeconds;
        var newVisibleWidth = pivotSeconds / oldSecondsPerPixel * _secondsPerPixel;
        _horizontalScrollSeconds = Math.Max(0, pivotAbsoluteTime - newVisibleWidth);
    }

    /// <summary>
    /// 水平スクロール（秒単位）。
    /// </summary>
    public void ScrollHorizontal(double seconds)
    {
        _horizontalScrollSeconds = Math.Max(0, _horizontalScrollSeconds + seconds);
    }

    /// <summary>
    /// 垂直スクロール（トラック単位）。
    /// </summary>
    public void ScrollVertical(int tracks)
    {
        _verticalScrollOffset = Math.Max(0, _verticalScrollOffset + tracks);
    }
}

using System.Globalization;
using System.Text;

namespace TimecodeSyncPlayer;

/// <summary>v0.6.4 段 6: 起動の区間の点（行に並ぶ順 = 起動の順）。</summary>
internal enum StartupTimingPoint
{
    MainWindowConstructor,
    OutputEngineCreated,
    WaitForDeviceStart,
    WaitForDeviceEnd,
    WindowLoaded,
    PlayerInitializeStart,
    PlayerInitializeEnd,
}

/// <summary>
/// v0.6.4 段 6（設計書 3-2、#15）: 起動直後の UI の止まり（ui.heartbeat seq=1 の lateMs 約 0.8 秒）が
/// どの区間にあるかを切り分ける要約。各点をプロセスの開始からの ms で記録し、Window_Loaded（とその中の
/// 再生の初期化）と ui.heartbeat の seq=1 の両方が済んだ時点で、起動 1 回に 1 行だけ書く
/// （`Startup timing: mainWindowCtorMs=… … firstHeartbeatMs=… firstHeartbeatLateMs=…`）。
/// 通らなかった点（再生が使えない構成など）は -1。観測だけで、製品の判断・状態には使わない。
/// UI スレッドだけが呼ぶ（ロックは無い）。
/// </summary>
internal sealed class StartupTimingRecorder
{
    private static readonly (StartupTimingPoint Point, string Key)[] Points =
    {
        (StartupTimingPoint.MainWindowConstructor, "mainWindowCtorMs"),
        (StartupTimingPoint.OutputEngineCreated, "outputEngineCreatedMs"),
        (StartupTimingPoint.WaitForDeviceStart, "waitForDeviceStartMs"),
        (StartupTimingPoint.WaitForDeviceEnd, "waitForDeviceEndMs"),
        (StartupTimingPoint.WindowLoaded, "windowLoadedMs"),
        (StartupTimingPoint.PlayerInitializeStart, "playerInitStartMs"),
        (StartupTimingPoint.PlayerInitializeEnd, "playerInitEndMs"),
    };

    private readonly DateTime _processStartUtc;
    private readonly Action<string> _write;
    private readonly Dictionary<StartupTimingPoint, double> _marks = new();
    private double _firstHeartbeatMs = double.NaN;
    private double _firstHeartbeatLateMs = double.NaN;
    private bool _written;

    /// <param name="processStartUtc">プロセスの開始（UTC）。</param>
    /// <param name="write">行の中身（`Startup timing: ` の後ろ）を書く。</param>
    public StartupTimingRecorder(DateTime processStartUtc, Action<string> write)
    {
        _processStartUtc = processStartUtc;
        _write = write;
    }

    /// <summary>点を記録する（最初の 1 回だけ）。</summary>
    public void Mark(StartupTimingPoint point, DateTime nowUtc)
    {
        if (_written || _marks.ContainsKey(point))
            return;
        _marks[point] = (nowUtc - _processStartUtc).TotalMilliseconds;
        TryWrite();
    }

    /// <summary>ui.heartbeat の seq=1（最初の 1 回だけ）。</summary>
    public void FirstHeartbeat(DateTime nowUtc, double lateMs)
    {
        if (_written || !double.IsNaN(_firstHeartbeatMs))
            return;
        _firstHeartbeatMs = (nowUtc - _processStartUtc).TotalMilliseconds;
        _firstHeartbeatLateMs = lateMs;
        TryWrite();
    }

    private void TryWrite()
    {
        if (_written || double.IsNaN(_firstHeartbeatMs) || !_marks.ContainsKey(StartupTimingPoint.WindowLoaded))
            return;
        _written = true;
        var fields = new StringBuilder();
        foreach ((StartupTimingPoint point, string key) in Points)
        {
            fields.Append(key).Append('=')
                .Append(_marks.TryGetValue(point, out double ms) ? Whole(ms) : "-1").Append(' ');
        }
        fields.Append("firstHeartbeatMs=").Append(Whole(_firstHeartbeatMs))
            .Append(" firstHeartbeatLateMs=")
            .Append(_firstHeartbeatLateMs.ToString("F1", CultureInfo.InvariantCulture));
        _write(fields.ToString());
    }

    private static string Whole(double ms) => Math.Round(ms).ToString("F0", CultureInfo.InvariantCulture);
}

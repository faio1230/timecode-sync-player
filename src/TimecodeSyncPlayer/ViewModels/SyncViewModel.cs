using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.ViewModels;

internal sealed class SyncViewModel : INotifyPropertyChanged
{
    private readonly ILtcMonitor _ltcMonitor;
    private readonly RelayCommand _startLtcCommand;
    private readonly RelayCommand _stopLtcCommand;
    private readonly RelayCommand _toggleSyncCommand;
    private string _ltcTimecodeText = "--:--:--:--";
    private string _ltcTimecodeForeground = "#55D86A";
    private string _ltcRealTimeText = "-.--- s";
    private string _ltcFormatText = "LTC 停止中";
    private string _ltcSignalLossPauseReason = string.Empty;
    private string _spoutToggleLabel = "Spout OFF";
    private string _timelineToggleLabel = "Timeline OFF";
    private bool _isLtcRunning;
    private bool _syncEnabled;
    private int _syncModeIndex;      // 0=Single, 1=Continue
    private int _gapBehaviorIndex;   // 0=Black, 1=Freeze
    private int _ltcSignalLossModeIndex; // 0=RunThrough, 1=Stop
    private int _ltcFpsModeIndex;    // 0=Auto, 1=Fixed24, 2=Fixed25, 3=Fixed29_97, 4=Fixed30
    private string? _selectedDevice;

    public SyncViewModel(ILtcMonitor ltcMonitor)
    {
        _ltcMonitor = ltcMonitor;

        _startLtcCommand = new RelayCommand(
            () =>
            {
                try
                {
                    _ltcMonitor.Start(_selectedDevice);
                    IsLtcRunning = true;
                    LtcFormatText = "fps: 検出中...";
                }
                catch (Exception ex)
                {
                    StartLtcFailed?.Invoke(this, ex);
                }
            },
            () => !_isLtcRunning);

        _stopLtcCommand = new RelayCommand(
            () =>
            {
                try
                {
                    _ltcMonitor.Stop();
                }
                catch (Exception ex)
                {
                    StopLtcFailed?.Invoke(this, ex);
                }
                IsLtcRunning = false;
            },
            () => _isLtcRunning);

        _toggleSyncCommand = new RelayCommand(() =>
        {
            SyncEnabled = !_syncEnabled;
            SyncEnabledChanged?.Invoke(this, _syncEnabled);
        });
    }

    public ICommand StartLtcCommand  => _startLtcCommand;
    public ICommand StopLtcCommand   => _stopLtcCommand;
    public ICommand ToggleSyncCommand => _toggleSyncCommand;

    public event EventHandler<Exception>?  StartLtcFailed;
    public event EventHandler<Exception>?  StopLtcFailed;
    public event EventHandler<bool>?       SyncEnabledChanged;

    public string? SelectedDevice
    {
        get => _selectedDevice;
        set { _selectedDevice = value; OnPropertyChanged(); }
    }

    public bool IsLtcRunning
    {
        get => _isLtcRunning;
        set
        {
            bool started = value && !_isLtcRunning;
            _isLtcRunning = value;
            if (started)
                SetLtcReception(LtcReceptionPolicy.Initial);
            OnPropertyChanged();
            _startLtcCommand.RaiseCanExecuteChanged();
            _stopLtcCommand.RaiseCanExecuteChanged();
        }
    }

    // v0.6.6 R-13: LTC の入力のメーターと受信の表示。LTC を止めている間は窓の側で隠す（IsLtcRunning）。
    private LtcReceptionDisplay _ltcReception = LtcReceptionPolicy.Initial;

    /// <summary>受信の表示の文字（「LTC 受信中」「信号あり・LTC なし」「無音」）。</summary>
    public string LtcReceptionText => _ltcReception.Text;

    /// <summary>受信の表示とメーターの色。</summary>
    public string LtcReceptionForeground => _ltcReception.Foreground;

    /// <summary>受信の表示の段（UIA の ItemStatus に出す。Receiving・SignalWithoutLtc・Silent）。</summary>
    public string LtcReceptionStatus => _ltcReception.State.ToString();

    /// <summary>メーターの値（0〜100。-60 dBFS が 0、0 dBFS が 100）。</summary>
    public double LtcMeterPercent => _ltcReception.MeterPercent;

    /// <summary>今の受信の段（表示だけ。同期は読まない）。</summary>
    internal LtcReceptionState LtcReception => _ltcReception.State;

    /// <summary>
    /// 監視から届いたレベルを表示へ移す（UI スレッド）。<paramref name="fps"/> は判定に使う fps
    /// （<see cref="LtcReceptionPolicy.ReceptionFps"/>）。LTC を止めている間に遅れて届いた値は捨てる。
    /// 段が変わったら true。
    /// </summary>
    public bool ApplyLtcInputLevel(LtcInputLevel level, double fps)
    {
        if (!_isLtcRunning)
            return false;
        LtcReceptionState previous = _ltcReception.State;
        SetLtcReception(LtcReceptionPolicy.Describe(level, fps));
        return previous != _ltcReception.State;
    }

    private void SetLtcReception(LtcReceptionDisplay display)
    {
        LtcReceptionDisplay previous = _ltcReception;
        _ltcReception = display;
        if (previous.State != display.State)
        {
            OnPropertyChanged(nameof(LtcReceptionText));
            OnPropertyChanged(nameof(LtcReceptionForeground));
            OnPropertyChanged(nameof(LtcReceptionStatus));
        }
        if (!previous.MeterPercent.Equals(display.MeterPercent))
            OnPropertyChanged(nameof(LtcMeterPercent));
    }

    public bool SyncEnabled
    {
        get => _syncEnabled;
        set { _syncEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(SyncToggleLabel)); }
    }

    public int SyncModeIndex
    {
        get => _syncModeIndex;
        set
        {
            _syncModeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SyncMode));
            OnPropertyChanged(nameof(IsContinueMode));
        }
    }

    public SyncMode SyncMode => _syncModeIndex == 1 ? SyncMode.Continue : SyncMode.Single;

    public bool IsContinueMode => _syncModeIndex == 1;

    public int GapBehaviorIndex
    {
        get => _gapBehaviorIndex;
        set
        {
            // U1 計測: バインディング更新から同期ハンドラ（保存・ギャップ再評価）完了までの
            // UI スレッド所要。UIA のコンボ選択もこの setter を通る。
            long started = Stopwatch.GetTimestamp();
            _gapBehaviorIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GapBehavior));
            Log.Debug("GapBehaviorIndex set: index={Index} elapsedMs={ElapsedMs:F1}",
                value, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    // GapBehaviorCombo: index 0 = Black, index 1 = Freeze
    public GapBehavior GapBehavior => _gapBehaviorIndex == 1 ? GapBehavior.Freeze : GapBehavior.Black;

    public int LtcSignalLossModeIndex
    {
        get => _ltcSignalLossModeIndex;
        set
        {
            _ltcSignalLossModeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LtcSignalLossMode));
        }
    }

    public LtcSignalLossMode LtcSignalLossMode =>
        _ltcSignalLossModeIndex == 1 ? LtcSignalLossMode.Stop : LtcSignalLossMode.RunThrough;

    public int LtcFpsModeIndex
    {
        get => _ltcFpsModeIndex;
        set
        {
            _ltcFpsModeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LtcFpsMode));
            OnPropertyChanged(nameof(ShowLtcFpsAutoNote));
        }
    }

    // 0=Auto, 1=Fixed24, 2=Fixed25, 3=Fixed29_97, 4=Fixed30
    private static readonly TimecodeFpsMode[] FpsModes =
        [TimecodeFpsMode.Auto, TimecodeFpsMode.Fixed24, TimecodeFpsMode.Fixed25,
         TimecodeFpsMode.Fixed29_97, TimecodeFpsMode.Fixed30];

    public TimecodeFpsMode LtcFpsMode =>
        _ltcFpsModeIndex >= 0 && _ltcFpsModeIndex < FpsModes.Length
            ? FpsModes[_ltcFpsModeIndex]
            : TimecodeFpsMode.Auto;

    /// <summary>
    /// v0.6.4: Auto の間だけ「Auto は確認用です。本番は固定にしてください」を出す。
    /// 補正の状態と同じ行に重ねて置くので、補正の状態が出ている間はそちらを優先して隠す（行の高さを変えない）。
    /// </summary>
    public bool ShowLtcFpsAutoNote =>
        LtcFpsMode == TimecodeFpsMode.Auto && string.IsNullOrEmpty(_syncCorrectionStatus);

    // 0=Smooth, 1=Jump
    private int _syncCorrectionModeIndex;
    public int SyncCorrectionModeIndex
    {
        get => _syncCorrectionModeIndex;
        set
        {
            _syncCorrectionModeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SyncCorrectionMode));
        }
    }

    public SyncCorrectionMode SyncCorrectionMode =>
        _syncCorrectionModeIndex == 1 ? SyncCorrectionMode.Jump : SyncCorrectionMode.Smooth;

    // v0.6.0: ProRes の GPU 復号。0=auto, 1=on, 2=off。適用は再起動の後（shim は最初のロードの前だけ受け付ける）。
    private static readonly ProResGpuMode[] ProResGpuModes =
        [ProResGpuMode.Auto, ProResGpuMode.On, ProResGpuMode.Off];

    private ProResGpuMode _proResGpuStartupMode;
    private int _proResGpuModeIndex;
    public int ProResGpuModeIndex
    {
        get => _proResGpuModeIndex;
        set
        {
            _proResGpuModeIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProResGpuMode));
            OnPropertyChanged(nameof(ProResGpuRestartNotice));
        }
    }

    public ProResGpuMode ProResGpuMode =>
        _proResGpuModeIndex >= 0 && _proResGpuModeIndex < ProResGpuModes.Length
            ? ProResGpuModes[_proResGpuModeIndex]
            : ProResGpuMode.Auto;

    /// <summary>選んだ値が起動時の値と違うときだけ「再起動の後に反映」。</summary>
    public string ProResGpuRestartNotice => ProResGpuPolicy.RestartNotice(_proResGpuStartupMode, ProResGpuMode);

    /// <summary>起動時の設定の値を選択状態にし、再起動の案内の基準にする。</summary>
    public void InitializeProResGpu(ProResGpuMode startupMode)
    {
        _proResGpuStartupMode = startupMode;
        ProResGpuModeIndex = Array.IndexOf(ProResGpuModes, startupMode);
    }

    private string _syncCorrectionStatus = "";
    public string SyncCorrectionStatus
    {
        get => _syncCorrectionStatus;
        set
        {
            _syncCorrectionStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowLtcFpsAutoNote));
        }
    }

    // 0.4.5-C: ロング GOP 素材の注意書き（表示のみ。空文字 = 非表示）。
    private string _longGopWarning = "";
    public string LongGopWarning
    {
        get => _longGopWarning;
        set
        {
            _longGopWarning = value;
            OnPropertyChanged();
        }
    }

    // 0.4.7: 推奨外コーデックの注意書き（表示のみ。空文字 = 非表示）。
    private string _codecWarning = "";
    public string CodecWarning
    {
        get => _codecWarning;
        set
        {
            if (_codecWarning == value) return;
            _codecWarning = value;
            OnPropertyChanged();
        }
    }

    // 0.4.7: 「デコードが追いついていない」表示（表示のみ。空文字 = 非表示）。
    private string _decodeHealthWarning = "";
    public string DecodeHealthWarning
    {
        get => _decodeHealthWarning;
        set
        {
            if (_decodeHealthWarning == value) return;
            _decodeHealthWarning = value;
            OnPropertyChanged();
        }
    }

    // T3: 全体に効く同期オフセット（ms）。UI には ms のみを出し、フレーム換算はしない。
    private double _syncOffsetMs;
    public double SyncOffsetMs
    {
        get => _syncOffsetMs;
        set
        {
            if (SyncOffsetPolicy.IsOutOfRange(value))
            {
                Serilog.Log.Warning(
                    "SyncOffsetMs {Value} は範囲外 [{Min}, {Max}] ms のため clamp しました",
                    value, SyncOffsetPolicy.MinimumMilliseconds, SyncOffsetPolicy.MaximumMilliseconds);
            }
            double clamped = SyncOffsetPolicy.Clamp(value);
            if (clamped.Equals(_syncOffsetMs))
                return;
            _syncOffsetMs = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SyncOffsetText));
            OnPropertyChanged(nameof(SyncOffsetInputText));
        }
    }

    public string SyncOffsetText => SyncOffsetPolicy.FormatMilliseconds(_syncOffsetMs);

    /// <summary>
    /// R-9: オフセットの数値入力欄。読むと今の値、書くと解釈して <see cref="SyncOffsetMs"/> へ反映する
    /// （範囲の外は SyncOffsetMs の setter が丸める）。解釈できない文字は反映せず、前の値のまま。
    /// どちらの場合も変更を通知し、入力欄に確定した値を表示し直させる。
    /// </summary>
    public string SyncOffsetInputText
    {
        get => SyncOffsetPolicy.FormatInput(_syncOffsetMs);
        set
        {
            if (SyncOffsetPolicy.TryParseInput(value, out double milliseconds))
                SyncOffsetMs = milliseconds;
            else
                Serilog.Log.Information("Sync offset input rejected input='{Input}' keep={OffsetMs}", value, _syncOffsetMs);
            OnPropertyChanged();
        }
    }

    public string SyncToggleLabel => _syncEnabled ? "Sync ON" : "Sync OFF";

    public string LtcTimecodeText
    {
        get => _ltcTimecodeText;
        set { _ltcTimecodeText = value; OnPropertyChanged(); }
    }

    public string LtcTimecodeForeground
    {
        get => _ltcTimecodeForeground;
        set { _ltcTimecodeForeground = value; OnPropertyChanged(); }
    }

    public string LtcRealTimeText
    {
        get => _ltcRealTimeText;
        set { _ltcRealTimeText = value; OnPropertyChanged(); }
    }

    public string LtcFormatText
    {
        get => _ltcFormatText;
        set { _ltcFormatText = value; OnPropertyChanged(); }
    }

    public string LtcSignalLossPauseReason
    {
        get => _ltcSignalLossPauseReason;
        set { _ltcSignalLossPauseReason = value; OnPropertyChanged(); }
    }

    public string SpoutToggleLabel
    {
        get => _spoutToggleLabel;
        set { _spoutToggleLabel = value; OnPropertyChanged(); }
    }

    public string TimelineToggleLabel
    {
        get => _timelineToggleLabel;
        set { _timelineToggleLabel = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

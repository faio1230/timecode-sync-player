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
            _isLtcRunning = value;
            OnPropertyChanged();
            _startLtcCommand.RaiseCanExecuteChanged();
            _stopLtcCommand.RaiseCanExecuteChanged();
        }
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

    private string _syncCorrectionStatus = "";
    public string SyncCorrectionStatus
    {
        get => _syncCorrectionStatus;
        set
        {
            _syncCorrectionStatus = value;
            OnPropertyChanged();
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
        }
    }

    public string SyncOffsetText => SyncOffsetPolicy.FormatMilliseconds(_syncOffsetMs);

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

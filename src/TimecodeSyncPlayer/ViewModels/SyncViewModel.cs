using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.ViewModels;

internal sealed class SyncViewModel : INotifyPropertyChanged
{
    private readonly ILtcMonitor _ltcMonitor;
    private readonly RelayCommand _startLtcCommand;
    private readonly RelayCommand _stopLtcCommand;
    private readonly RelayCommand _toggleSyncCommand;
    private string _ltcTimecodeText = "--:--:--:--";
    private string _ltcRealTimeText = "-.--- s";
    private string _ltcFormatText = "LTC 停止中";
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
            _gapBehaviorIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GapBehavior));
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

    public string SyncToggleLabel => _syncEnabled ? "Sync ON" : "Sync OFF";

    public string LtcTimecodeText
    {
        get => _ltcTimecodeText;
        set { _ltcTimecodeText = value; OnPropertyChanged(); }
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

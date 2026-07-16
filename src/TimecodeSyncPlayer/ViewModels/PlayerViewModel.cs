using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.ViewModels;

internal sealed class PlayerViewModel : INotifyPropertyChanged
{
    private string _playPauseIcon = "▶";
    private string _timeLabel = "0:00 / 0:00";
    private string _metaLine = "";
    private double _seekBarValue;
    private double _seekBarMaximum = 1.0;
    private string _speedLabel = "1×";
    private string _muteToggleLabel = "MUTE OFF";
    private double _volume = 100;

    public PlayerViewModel(IPlaybackController controller)
    {
        PlayPauseCommand = new RelayCommand(controller.TogglePlayPause);
        BackCommand      = new RelayCommand(() => controller.SeekRelative(-10.0));
        FwdCommand       = new RelayCommand(() => controller.SeekRelative(10.0));
        SpeedCommand     = new RelayCommand(controller.CycleSpeed);
    }

    public ICommand PlayPauseCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand FwdCommand { get; }
    public ICommand SpeedCommand { get; }

    public string PlayPauseIcon
    {
        get => _playPauseIcon;
        set { _playPauseIcon = value; OnPropertyChanged(); }
    }

    public string TimeLabel
    {
        get => _timeLabel;
        set { _timeLabel = value; OnPropertyChanged(); }
    }

    public string MetaLine
    {
        get => _metaLine;
        set { _metaLine = value; OnPropertyChanged(); }
    }

    public double SeekBarValue
    {
        get => _seekBarValue;
        set { _seekBarValue = value; OnPropertyChanged(); }
    }

    public double SeekBarMaximum
    {
        get => _seekBarMaximum;
        set { _seekBarMaximum = value; OnPropertyChanged(); }
    }

    public string SpeedLabel
    {
        get => _speedLabel;
        set { _speedLabel = value; OnPropertyChanged(); }
    }

    public string MuteToggleLabel
    {
        get => _muteToggleLabel;
        set { _muteToggleLabel = value; OnPropertyChanged(); }
    }

    public double Volume
    {
        get => _volume;
        set { _volume = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.ViewModels;

internal sealed class PlaylistViewModel : INotifyPropertyChanged
{
    private readonly PlaylistState _playlist;
    private readonly IMediaDurationReader _durationReader;
    private int _selectedIndex = -1;
    private string _currentTrackLabel = "";

    private readonly RelayCommand _removeTrackCommand;
    private readonly RelayCommand _moveUpCommand;
    private readonly RelayCommand _moveDownCommand;

    public PlaylistViewModel(PlaylistState playlist, IMediaDurationReader durationReader)
    {
        _playlist    = playlist;
        _durationReader = durationReader;

        _removeTrackCommand = new RelayCommand(
            () => { if (_playlist.RemoveAt(_selectedIndex)) TrackRemoved?.Invoke(); },
            () => _selectedIndex >= 0);

        _moveUpCommand = new RelayCommand(
            () =>
            {
                int selectedIndex = _selectedIndex;
                int finalIndex = selectedIndex - 1;
                if (_playlist.MoveTrackUp(selectedIndex))
                {
                    SelectedIndex = finalIndex;
                    TrackMoved?.Invoke();
                }
            },
            () => _selectedIndex > 0);

        _moveDownCommand = new RelayCommand(
            () =>
            {
                int selectedIndex = _selectedIndex;
                int finalIndex = selectedIndex + 1;
                if (_playlist.MoveTrackDown(selectedIndex))
                {
                    SelectedIndex = finalIndex;
                    TrackMoved?.Invoke();
                }
            },
            () => _selectedIndex >= 0 && _selectedIndex < _playlist.Tracks.Count - 1);

        ClearTracksCommand = new RelayCommand(() =>
        {
            _playlist.Clear();
            TracksCleared?.Invoke();
        });
    }

    public bool AutoOffsetOnAdd { get; set; } = true;

    public ICommand RemoveTrackCommand => _removeTrackCommand;
    public ICommand MoveUpCommand => _moveUpCommand;
    public ICommand MoveDownCommand => _moveDownCommand;
    public ICommand ClearTracksCommand { get; }

    public event Action? TrackRemoved;
    public event Action? TrackMoved;
    public event Action? TracksCleared;

    public ObservableCollection<PlaylistTrack> Tracks => _playlist.Tracks;
    public int CurrentIndex => _playlist.CurrentIndex;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = value;
            OnPropertyChanged();
            _removeTrackCommand.RaiseCanExecuteChanged();
            _moveUpCommand.RaiseCanExecuteChanged();
            _moveDownCommand.RaiseCanExecuteChanged();
        }
    }

    public string CurrentTrackLabel
    {
        get => _currentTrackLabel;
        set { _currentTrackLabel = value; OnPropertyChanged(); }
    }

    public async Task AddFilesAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var pathList = paths.ToList();
        int startIndex = _playlist.Tracks.Count;
        _playlist.AddFiles(pathList, AutoOffsetOnAdd);

        var snapshot = pathList.Zip(_playlist.Tracks.Skip(startIndex)).ToList();
        foreach (var (path, track) in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var duration = await _durationReader.ReadDurationAsync(path);
            ct.ThrowIfCancellationRequested();
            if (duration.HasValue)
                _playlist.UpdateMediaDuration(track.Id, duration.Value, recalculate: AutoOffsetOnAdd);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

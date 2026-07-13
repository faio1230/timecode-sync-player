namespace TimecodeSyncPlayer.ViewModels;

internal sealed class MainViewModel
{
    public PlaylistViewModel Playlist { get; set; } = null!;
    public PlayerViewModel Player { get; set; } = null!;
    public SyncViewModel Sync { get; set; } = null!;
}

namespace TimecodeSyncPlayer.Contracts;

public interface IMediaDurationReader
{
    Task<TimeSpan?> ReadDurationAsync(string filePath);
}

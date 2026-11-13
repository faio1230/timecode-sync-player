namespace Erytheia.Contracts;

public interface IMediaDurationReader
{
    Task<TimeSpan?> ReadDurationAsync(string filePath);
}

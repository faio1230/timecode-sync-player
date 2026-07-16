using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace TimecodeSyncPlayer;

internal interface IAtomicFileOperations
{
    bool Exists(string path);
    Task WriteAllTextAsync(string path, string contents, Encoding encoding);
    void Replace(string source, string destination);
    void Move(string source, string destination);
    void Delete(string path);
}

internal static class AtomicFileWriter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task WriteAllTextAsync(
        string filePath,
        string contents,
        Encoding encoding,
        IAtomicFileOperations? operations = null)
    {
        operations ??= SystemAtomicFileOperations.Instance;
        string destinationPath = Path.GetFullPath(filePath);
        string directory = Path.GetDirectoryName(destinationPath)!;
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        SemaphoreSlim pathLock = PathLocks.GetOrAdd(destinationPath, static _ => new SemaphoreSlim(1, 1));

        await pathLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await operations.WriteAllTextAsync(temporaryPath, contents, encoding).ConfigureAwait(false);
            if (operations.Exists(destinationPath))
                operations.Replace(temporaryPath, destinationPath);
            else
                operations.Move(temporaryPath, destinationPath);
        }
        finally
        {
            try
            {
                if (operations.Exists(temporaryPath))
                    operations.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the primary write/replace exception. A stale temp file is safer than masking it.
            }

            pathLock.Release();
        }
    }
}

internal sealed class SystemAtomicFileOperations : IAtomicFileOperations
{
    public static SystemAtomicFileOperations Instance { get; } = new();

    private SystemAtomicFileOperations()
    {
    }

    public bool Exists(string path) => File.Exists(path);

    public Task WriteAllTextAsync(string path, string contents, Encoding encoding) =>
        File.WriteAllTextAsync(path, contents, encoding);

    public void Replace(string source, string destination) => File.Replace(source, destination, null);

    public void Move(string source, string destination) => File.Move(source, destination);

    public void Delete(string path) => File.Delete(path);
}

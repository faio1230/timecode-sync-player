using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HapProbe;

/// <summary>
/// 読み出しが 4K60 の HAP に追いつくかを測る。**OS のキャッシュを通さずに読む**
/// （キャッシュに乗った状態だと毎秒 5GB 出てしまい、ディスクの実力にならない）。
/// FILE_FLAG_NO_BUFFERING はセクタ境界でしか読めないので、末尾の端数は読まない。
/// </summary>
internal static class DiskRead
{
    private const int NoBuffering = 0x20000000;
    private const int SequentialScan = 0x08000000;
    private const int Sector = 4096;

    public static void Measure(string directory, int bufferMegabytes = 4)
    {
        string[] files = Directory.GetFiles(directory, "*.hap").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) { Console.WriteLine($"コマが無い: {directory}"); return; }
        int bufferSize = bufferMegabytes * 1024 * 1024;
        nint buffer;
        unsafe { buffer = (nint)NativeMemory.AlignedAlloc((nuint)bufferSize, Sector); }
        try
        {
            long totalBytes = 0;
            var stopwatch = Stopwatch.StartNew();
            unsafe
            {
                var span = new Span<byte>((void*)buffer, bufferSize);
                foreach (string file in files)
                {
                    using SafeFileHandle handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                        (FileOptions)(NoBuffering | SequentialScan));
                    long length = RandomAccess.GetLength(handle) / Sector * Sector;
                    long offset = 0;
                    while (offset < length)
                    {
                        int size = (int)Math.Min(bufferSize, length - offset);
                        int read = RandomAccess.Read(handle, span[..size], offset);
                        if (read <= 0) break;
                        offset += read;
                        totalBytes += read;
                    }
                }
            }
            stopwatch.Stop();
            double megabytes = totalBytes / 1024.0 / 1024.0;
            double seconds = stopwatch.Elapsed.TotalSeconds;
            double perFrameMegabytes = megabytes / files.Length;
            Console.WriteLine($"{Path.GetFileName(directory)}: {megabytes:N0} MB を {seconds:F2} 秒で読んだ → " +
                $"{megabytes / seconds:N0} MB/s（キャッシュ無し）");
            Console.WriteLine($"  1 コマ {perFrameMegabytes:F2} MB → 60fps に必要な読み出しは {perFrameMegabytes * 60:N0} MB/s、" +
                $"余裕は {megabytes / seconds / (perFrameMegabytes * 60):F1} 倍");
        }
        finally
        {
            unsafe { NativeMemory.AlignedFree((void*)buffer); }
        }
    }
}

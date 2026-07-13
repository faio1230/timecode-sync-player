using System.Diagnostics;
using System.Globalization;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// ffprobe を使用して動画ファイルの再生時間を取得する。
/// </summary>
internal class MediaDurationReader : IMediaDurationReader
{
    private bool? _ffprobeAvailable;

    private async Task<bool> IsFfprobeAvailableAsync()
    {
        if (_ffprobeAvailable.HasValue) return _ffprobeAvailable.Value;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process == null)
            {
                _ffprobeAvailable = false;
                return false;
            }

            await process.WaitForExitAsync();
            _ffprobeAvailable = process.ExitCode == 0;
            if (!_ffprobeAvailable.Value)
            {
                Log.Warning("MediaDurationReader: ffprobe is not available on PATH");
            }
            return _ffprobeAvailable.Value;
        }
        catch
        {
            _ffprobeAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// ffprobe を使用して動画ファイルの再生時間を取得する。
    /// </summary>
    /// <param name="filePath">動画ファイルのパス</param>
    /// <returns>再生時間。ffprobe が見つからない場合または失敗した場合は null</returns>
    public async Task<TimeSpan?> ReadDurationAsync(string filePath)
    {
        if (!await IsFfprobeAvailableAsync())
        {
            Log.Warning("MediaDurationReader: ffprobe is not available, cannot read duration for {Path}", filePath);
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(filePath);

            using var process = Process.Start(psi);
            if (process == null)
            {
                Log.Warning("MediaDurationReader: ffprobe process could not be started for {Path}", filePath);
                return null;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(cts.Token);

                if (string.IsNullOrWhiteSpace(output))
                {
                    Log.Warning("MediaDurationReader: ffprobe returned empty output for {Path}", filePath);
                    return null;
                }

                if (double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration))
                {
                    return TimeSpan.FromSeconds(duration);
                }

                Log.Warning("MediaDurationReader: could not parse ffprobe output for {Path}: {Output}", filePath, output);
                return null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (ObjectDisposedException) { }
                Log.Warning("MediaDurationReader: ffprobe timed out after 30s for {Path}", filePath);
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MediaDurationReader: exception reading duration for {Path}", filePath);
            return null;
        }
    }
}

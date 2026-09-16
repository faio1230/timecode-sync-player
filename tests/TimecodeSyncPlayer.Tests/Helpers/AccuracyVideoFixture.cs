using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal sealed record AccuracyClip(int Id, string Name, int FpsNumerator, int FpsDenominator,
    int FrameCount, double TimelineOffset, double MediaIn, double MediaOut, string Path,
    int KeyframeIntervalFrames, double KeyframeIntervalSeconds);

internal sealed record AccuracyFixture(string ProjectPath, IReadOnlyList<AccuracyClip> Clips);

/// <summary>Generates a fresh, independently decode-verified 1080p fixture in the report directory.</summary>
internal static class AccuracyVideoFixture
{
    /// <summary>
    /// T11: キーフレーム間隔（フレーム数）の上書き。未設定なら 1 秒ぶん。
    /// 記録用に長い間隔（例 250）でも生成できるようにする。
    /// </summary>
    public const string GopOverrideEnvironmentVariable = "TCS_V3_GOP";

    public static async Task<AccuracyFixture> CreateAsync(
        string reportDirectory,
        Action<string>? progress = null,
        double ltcFps = 25.0)
    {
        string directory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(directory);
        var clips = new List<AccuracyClip>();
        var playlist = new PlaylistState();
        foreach (var spec in new[] { (Id: 1, Num: 24, Den: 1), (Id: 2, Num: 30000, Den: 1001), (Id: 3, Num: 60, Den: 1) })
        {
            int keyframeIntervalFrames = ResolveKeyframeIntervalFrames(spec.Num, spec.Den);
            var clip = new AccuracyClip(spec.Id, $"accuracy-{spec.Id}-{spec.Num}-{spec.Den}", spec.Num, spec.Den,
                (int)Math.Ceiling(12.0 * spec.Num / spec.Den), (spec.Id - 1) * 12, 0, 10,
                Path.Combine(directory, $"clip-{spec.Id}.mp4"),
                keyframeIntervalFrames, keyframeIntervalFrames * spec.Den / (double)spec.Num);
            progress?.Invoke($"encode-{clip.Id}");
            await EncodeAsync(clip);
            progress?.Invoke($"verify-{clip.Id}");
            await VerifyAsync(clip);
            clips.Add(clip);
            playlist.Tracks.Add(new PlaylistTrack(Guid.Parse($"00000000-0000-0000-0000-{clip.Id:D12}"),
                clip.Path, clip.Name, TimeSpan.Zero, TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(clip.TimelineOffset),
                TimeSpan.FromSeconds(clip.FrameCount * (double)clip.FpsDenominator / clip.FpsNumerator),
                TimeSpan.Zero, (double)clip.FpsNumerator / clip.FpsDenominator, true));
        }
        string projectPath = Path.Combine(directory, "accuracy.tsp");
        await ProjectSerializer.SaveAsync(projectPath, playlist, SyncMode.Continue, GapBehavior.Black,
            new CanvasData { Width = 1920, Height = 1080, DefaultFit = "fit-height" });
        await File.WriteAllTextAsync(Path.Combine(directory, "fixture.json"),
            BuildFixtureJson(ltcFps, clips), new UTF8Encoding(false));
        return new AccuracyFixture(projectPath, clips);
    }

    /// <summary>
    /// T11: キーフレーム間隔（フレーム数）。既定は 1 秒ぶん（現場で一般的な間隔）。
    /// fps をそのままフレーム数にするため四捨五入し、24 → 24、29.97 → 30、60 → 60 にする
    /// （29.97 は 29 だと 0.97 秒になり 1 秒から外れるため、最寄りの 30 を選ぶ）。
    /// </summary>
    internal static int ResolveKeyframeIntervalFrames(int fpsNumerator, int fpsDenominator)
    {
        string? overrideValue = Environment.GetEnvironmentVariable(GopOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            if (!int.TryParse(overrideValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int overrideFrames) ||
                overrideFrames < 1)
                throw new InvalidOperationException(
                    $"{GopOverrideEnvironmentVariable} は 1 以上のフレーム数で指定してください: '{overrideValue}'");
            return overrideFrames;
        }
        return (int)Math.Round(fpsNumerator / (double)fpsDenominator, MidpointRounding.AwayFromZero);
    }

    /// <summary>fixture.json の中身。ltcFps は V3 の LTC fps マトリクス（24/25/29.97/30）で変わる。</summary>
    internal static string BuildFixtureJson(double ltcFps, IReadOnlyList<AccuracyClip> clips)
        => JsonSerializer.Serialize(new { schema = 1, ltcFps, clips }, MonkeyJson.Options);

    internal static byte[] CreateMarkerPanel(int clipId, int frameIndex)
    {
        if (clipId is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(clipId));
        if (frameIndex is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        byte[] bytes = [0xDD, 0xAA, (byte)(frameIndex >> 8), (byte)frameIndex, (byte)clipId, 0];
        for (int i = 0; i < 5; i++) bytes[5] ^= bytes[i];
        var panel = new byte[768 * 32];
        for (int cell = 0; cell < 48; cell++)
        {
            if ((bytes[cell / 8] & (0x80 >> (cell % 8))) == 0) continue;
            for (int y = 0; y < 32; y++) panel.AsSpan(y * 768 + cell * 16, 16).Fill(255);
        }
        return panel;
    }

    private static async Task EncodeAsync(AccuracyClip clip)
    {
        string rate = $"{clip.FpsNumerator}/{clip.FpsDenominator}";
        string keyframeInterval = clip.KeyframeIntervalFrames.ToString(CultureInfo.InvariantCulture);
        using var process = NewProcess("ffmpeg", "-hide_banner", "-loglevel", "error", "-n",
            "-f", "lavfi", "-i", $"testsrc2=size=1920x1080:rate={rate}",
            "-f", "rawvideo", "-pixel_format", "gray", "-video_size", "768x32", "-framerate", rate, "-i", "pipe:0",
            "-filter_complex", "[0:v][1:v]overlay=32:32:shortest=1", "-an", "-frames:v", clip.FrameCount.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p",
            // T11: キーフレーム間隔を固定する。-sc_threshold 0 で内容による挿入を止め、
            // -keyint_min も同じ値にして、指定どおりの間隔だけにする。
            "-g", keyframeInterval, "-keyint_min", keyframeInterval, "-sc_threshold", "0",
            "-threads", "4", clip.Path);
        process.StartInfo.RedirectStandardInput = true;
        process.Start();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            for (int frame = 0; frame < clip.FrameCount; frame++)
                await process.StandardInput.BaseStream.WriteAsync(CreateMarkerPanel(clip.Id, frame), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg encode failed: {await stderr}");
            await stdout;
            await stderr;
        }
        catch
        {
            E2EAppRunner.KillProcess(process);
            throw;
        }
    }

    private static async Task VerifyAsync(AccuracyClip clip)
    {
        using var probe = NewProcess("ffprobe", "-v", "error", "-select_streams", "v:0", "-count_frames",
            "-show_entries", "stream=width,height,r_frame_rate,avg_frame_rate,nb_read_frames,start_time", "-of", "json", clip.Path);
        probe.Start();
        Task<string> probeError = probe.StandardError.ReadToEndAsync();
        Task<string> probeOutput = probe.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await probe.WaitForExitAsync(timeout.Token); }
        catch { E2EAppRunner.KillProcess(probe); throw; }
        string metadata = await probeOutput;
        if (probe.ExitCode != 0) throw new InvalidOperationException($"ffprobe failed: {await probeError}");
        await probeError;
        using (JsonDocument document = JsonDocument.Parse(metadata))
        {
            JsonElement stream = document.RootElement.GetProperty("streams")[0];
            string expectedRate = $"{clip.FpsNumerator}/{clip.FpsDenominator}";
            if (stream.GetProperty("width").GetInt32() != 1920 || stream.GetProperty("height").GetInt32() != 1080 ||
                stream.GetProperty("r_frame_rate").GetString() != expectedRate ||
                stream.GetProperty("avg_frame_rate").GetString() != expectedRate ||
                int.Parse(stream.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture) != clip.FrameCount ||
                double.Parse(stream.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException($"Unexpected encoded timing/dimensions: {metadata}");
        }
        await File.WriteAllTextAsync(clip.Path + ".probe.json", metadata);

        await VerifyKeyframesAsync(clip, timeout.Token);

        // Read every encoded frame. This decoder does not call the generator or app marker decoder.
        using var decoder = NewProcess("ffmpeg", "-hide_banner", "-loglevel", "error", "-i", clip.Path,
            "-vf", "crop=768:32:32:32", "-vsync", "0", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1");
        decoder.Start();
        Task<string> errors = decoder.StandardError.ReadToEndAsync();
        var panel = new byte[768 * 32];
        int decoded = 0;
        try
        {
            while (true)
            {
                int read = 0;
                while (read < panel.Length)
                {
                    int count = await decoder.StandardOutput.BaseStream.ReadAsync(panel.AsMemory(read), timeout.Token);
                    if (count == 0) break;
                    read += count;
                }
                if (read == 0) break;
                if (read != panel.Length) throw new InvalidDataException("Truncated decoded marker panel.");
                var bytes = new byte[6];
                for (int bit = 0; bit < 48; bit++)
                {
                    int intensity = panel[16 * 768 + bit * 16 + 8];
                    if (intensity is > 64 and < 192) throw new InvalidDataException($"Ambiguous marker at frame {decoded}.");
                    bytes[bit / 8] = (byte)((bytes[bit / 8] << 1) | (intensity >= 192 ? 1 : 0));
                }
                int frameNumber = bytes[2] * 256 + bytes[3];
                int checksum = bytes[0] ^ bytes[1] ^ bytes[2] ^ bytes[3] ^ bytes[4];
                if (bytes[0] != 221 || bytes[1] != 170 || frameNumber != decoded || bytes[4] != clip.Id || bytes[5] != checksum)
                    throw new InvalidDataException($"Marker mismatch: clip {clip.Id}, decoded ordinal {decoded}, bytes {Convert.ToHexString(bytes)}.");
                decoded++;
            }
            await decoder.WaitForExitAsync(timeout.Token);
            if (decoder.ExitCode != 0) throw new InvalidOperationException($"ffmpeg verification failed: {await errors}");
            await errors;
            if (decoded != clip.FrameCount) throw new InvalidDataException($"Expected {clip.FrameCount} markers; decoded {decoded}.");
        }
        catch { E2EAppRunner.KillProcess(decoder); throw; }
    }

    /// <summary>
    /// T11: 生成したクリップのキーフレーム位置を ffprobe で確認する。先頭が 0 で、
    /// 隣り合うキーフレームの間隔が指定（<see cref="AccuracyClip.KeyframeIntervalFrames"/>）と
    /// すべて一致し、本数も期待どおりでなければ例外にする。
    /// </summary>
    private static async Task VerifyKeyframesAsync(AccuracyClip clip, CancellationToken token)
    {
        using var probe = NewProcess("ffprobe", "-v", "error", "-select_streams", "v:0",
            "-skip_frame", "nokey", "-show_entries", "frame=key_frame,pts_time", "-of", "csv=p=0", clip.Path);
        probe.Start();
        Task<string> error = probe.StandardError.ReadToEndAsync();
        Task<string> outputTask = probe.StandardOutput.ReadToEndAsync();
        try
        {
            await probe.WaitForExitAsync(token);
        }
        catch
        {
            E2EAppRunner.KillProcess(probe);
            throw;
        }
        string output = await outputTask;
        if (probe.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe (keyframes) failed: {await error}");
        await error;

        double fps = clip.FpsNumerator / (double)clip.FpsDenominator;
        var keyframeFrames = new List<int>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Trim().Split(',');
            if (fields.Length < 2 || fields[0] != "1")
                continue;
            if (double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ptsSeconds))
                keyframeFrames.Add((int)Math.Round(ptsSeconds * fps, MidpointRounding.AwayFromZero));
        }

        int expectedCount = (clip.FrameCount - 1) / clip.KeyframeIntervalFrames + 1;
        if (keyframeFrames.Count != expectedCount || keyframeFrames.Count == 0 || keyframeFrames[0] != 0)
            throw new InvalidDataException(
                $"キーフレームの本数・位置が想定外です: clip {clip.Id} 期待 {expectedCount} 本（先頭 0、間隔 {clip.KeyframeIntervalFrames} フレーム）、" +
                $"実際 {keyframeFrames.Count} 本 [{string.Join(", ", keyframeFrames)}]");
        for (int i = 1; i < keyframeFrames.Count; i++)
        {
            int interval = keyframeFrames[i] - keyframeFrames[i - 1];
            if (interval != clip.KeyframeIntervalFrames)
                throw new InvalidDataException(
                    $"キーフレーム間隔が指定と違います: clip {clip.Id} 期待 {clip.KeyframeIntervalFrames}、" +
                    $"実際 {interval}（位置: {string.Join(", ", keyframeFrames)}）");
        }
    }

    private static Process NewProcess(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return new Process { StartInfo = info };
    }
}

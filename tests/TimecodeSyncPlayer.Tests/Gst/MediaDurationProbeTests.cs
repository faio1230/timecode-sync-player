using System.Diagnostics;
using System.Globalization;
using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Gst;

/// <summary>
/// v0.6.6 F-7: shim の軽い関数（tcs_probe_duration、容器の長さだけ）を本物の DLL で確かめる。
/// 形式ごとの短い素材を ffmpeg で作り（作れない形式はスキップ）、
/// (1) 長さが取れる、(2) 同じ素材で再生時の長さ（TryGetDuration、プレイヤーの demux）と一致する（同じ出所）、
/// (3) ffprobe との差が 1.5 フレーム以内（記録の試験。差は出力に残す）。
/// DLL・GStreamer・ffmpeg が無い環境ではスキップする。
/// </summary>
[Collection(MediaDurationProbeCollection.Name)]
public sealed class MediaDurationProbeTests : IClassFixture<MediaDurationProbeFixture>
{
    private readonly MediaDurationProbeFixture _fx;
    private readonly ITestOutputHelper _output;

    public MediaDurationProbeTests(MediaDurationProbeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _output = output;
    }

    public static TheoryData<string> Formats => new()
    {
        "h264_mp4", "h264_aac_mp4", "h264_mkv", "h264_ts", "hevc_mp4",
        "prores422hq_mov", "prores4444_mov", "hap_mov", "hapq_mov", "av1_mp4",
    };

    [SkippableTheory]
    [MemberData(nameof(Formats))]
    public void Probe_ReturnsContainerDuration_PerFormat(string format)
    {
        string path = _fx.RequireClip(format);

        var sw = Stopwatch.StartNew();
        double? seconds = GstPlaybackApi.ProbeMediaDuration(path);
        sw.Stop();

        seconds.Should().NotBeNull($"{format}: the container probe must give a duration");
        double nominal = MediaDurationProbeFixture.Seconds(format);
        seconds!.Value.Should().BeApproximately(nominal, 2.0 / MediaDurationProbeFixture.Fps(format),
            $"{format}: the clip is {nominal} seconds long");
        _output.WriteLine($"{format}: probeSec={seconds.Value:F6} elapsedMs={sw.Elapsed.TotalMilliseconds:F1}");
    }

    /// <summary>同期が使う再生時の長さ（プレイヤーの demux の値）と、プレイリストの長さが同じ出所であること。</summary>
    [SkippableTheory]
    [MemberData(nameof(Formats))]
    public void Probe_EqualsPlayerDuration_ForTheSameClip(string format)
    {
        string path = _fx.RequireClip(format);
        double? probe = GstPlaybackApi.ProbeMediaDuration(path);
        probe.Should().NotBeNull();

        var native = new GstNativeApi();
        IntPtr player = native.PlayerCreate("TCSTestProbeDuration", out string createError);
        Skip.If(player == IntPtr.Zero, $"player could not be created: {createError}");
        try
        {
            int rc = native.Load(player, path, 0.0, paused: true, out string loadError);
            Skip.If(rc != 0 && format.StartsWith("hap", StringComparison.Ordinal),
                $"{format}: HAP playback is not available here ({loadError})");
            rc.Should().Be(0, $"{format}: paused load ({loadError})");
            native.TryGetDuration(player, out double playerSeconds).Should().BeTrue();
            _output.WriteLine($"{format}: probeSec={probe!.Value:F6} playerSec={playerSeconds:F6}");
            probe.Value.Should().BeApproximately(playerSeconds, 1e-6,
                $"{format}: the playlist length and the playback length come from the same demuxer");
        }
        finally
        {
            native.PlayerDestroy(player);
        }
    }

    /// <summary>記録の試験: ffprobe（format=duration）との差は 1.5 フレーム以内。差は出力に残す。</summary>
    [SkippableTheory]
    [MemberData(nameof(Formats))]
    public void Probe_IsWithinOneAndAHalfFramesOfFfprobe_Record(string format)
    {
        string path = _fx.RequireClip(format);
        double? probe = GstPlaybackApi.ProbeMediaDuration(path);
        probe.Should().NotBeNull();
        double? ffprobe = MediaDurationProbeFixture.FfprobeDuration(path);
        Skip.If(ffprobe is null, "ffprobe is not available");
        double fps = MediaDurationProbeFixture.Fps(format);
        double diffFrames = (probe!.Value - ffprobe!.Value) * fps;
        _output.WriteLine(
            $"{format}: probeSec={probe.Value:F6} ffprobeSec={ffprobe.Value:F6} diffMs={(probe.Value - ffprobe.Value) * 1000:F1} diffFrames={diffFrames:F2}");
        // TS は推奨外の容器で、tsdemux の長さは推定（6 秒で −1.30 フレームと上限に近い）。揺れると偽の赤になるので記録だけにする。
        if (format.StartsWith("h264_ts", StringComparison.Ordinal))
            return;
        Math.Abs(diffFrames).Should().BeLessThanOrEqualTo(1.5, $"{format}: probe vs ffprobe");
    }

    [SkippableFact]
    public void Probe_MissingFile_ReturnsNull()
    {
        _fx.RequireNative();
        GstPlaybackApi.ProbeMediaDuration(TestTempPaths.Combine("f7-durations", "no-such-clip.mp4"))
            .Should().BeNull();
    }

    /// <summary>
    /// 取れない実例: 約 40KB の 2 秒の TS は tsdemux が長さを出さない（開発機の実測、約 100KB 以上で出る）。
    /// 再生時の長さも同じく出ないので、順の 3 番目（不明のまま）になる。同じ出所であることの裏返し。
    /// </summary>
    [SkippableFact]
    public void Probe_TinyTransportStream_GivesNone_AndSoDoesThePlayer()
    {
        string path = _fx.RequireClip("h264_ts_tiny");
        GstPlaybackApi.ProbeMediaDuration(path).Should().BeNull();

        var native = new GstNativeApi();
        IntPtr player = native.PlayerCreate("TCSTestProbeDurationTiny", out string createError);
        Skip.If(player == IntPtr.Zero, $"player could not be created: {createError}");
        try
        {
            native.Load(player, path, 0.0, paused: true, out string loadError).Should().Be(0, loadError);
            native.TryGetDuration(player, out _).Should().BeFalse("the player's demuxer gives no length either");
        }
        finally
        {
            native.PlayerDestroy(player);
        }
    }

    /// <summary>
    /// v0.6.6: 拡張子の表（プレイヤーと共通）に無い拡張子は、中身が読める素材でも「不明」（null）。
    /// typefind に落とさない（typefind が gio を読み込み、その裏の読みでアプリが落ちていた）。
    /// 長さは読み込んだときの再生時の長さで埋まる（PlaylistDurationFallback）。
    /// </summary>
    [SkippableFact]
    public void Probe_ExtensionOutsideTheTable_ReturnsNull()
    {
        string source = _fx.RequireClip("h264_mp4");
        string renamed = TestTempPaths.Combine("f7-durations", "h264_mp4.unknownext");
        File.Copy(source, renamed, overwrite: true);

        GstPlaybackApi.ProbeMediaDuration(renamed).Should().BeNull();
        GstPlaybackApi.ProbeMediaDuration(source).Should().NotBeNull("the same bytes with a known extension still probe");
    }

    /// <summary>
    /// v0.6.6: 表に無い拡張子は、長さの軽い関数も GOP 走査も同じ rc（TCS_ERR_UNSUPPORTED_EXTENSION）で失敗し、
    /// アプリの警告には「対応していない拡張子」と分かる文が添えられる。
    /// </summary>
    [SkippableFact]
    public void ExtensionOutsideTheTable_ProbeAndGopScan_ReturnUnsupportedExtension()
    {
        string source = _fx.RequireClip("h264_mp4");
        string renamed = TestTempPaths.Combine("f7-durations", "h264_mp4_gop.unknownext");
        File.Copy(source, renamed, overwrite: true);

        GstNative.Imports.tcs_probe_duration(renamed, 0, out double seconds)
            .Should().Be(GstNative.TcsErrUnsupportedExtension);
        seconds.Should().Be(0.0);
        GstNative.Imports.tcs_scan_gop(renamed, 0, out GstNative.TcsGopScan scan)
            .Should().Be(GstNative.TcsErrUnsupportedExtension);
        scan.Keyframes.Should().Be(0);
        GstPlaybackApi.ScanGop(renamed).Should().BeNull();
    }

    /// <summary>v0.6.6: 警告に添える文（DLL 不要）。</summary>
    [Fact]
    public void DescribeProbeFailure_NamesTheUnsupportedExtension()
    {
        GstNative.DescribeProbeFailure(GstNative.TcsErrUnsupportedExtension)
            .Should().Contain("unsupported extension").And.Contain("typefind is not used");
        GstNative.DescribeProbeFailure(GstNative.TcsErrTimeout).Should().Be(" (timeout)");
        GstNative.DescribeProbeFailure(-1).Should().BeEmpty();
    }

    [SkippableFact]
    public void Probe_FileThatIsNotMedia_ReturnsNullQuickly()
    {
        _fx.RequireNative();
        string junk = TestTempPaths.Combine("f7-durations", "not-media.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(junk)!);
        File.WriteAllBytes(junk, Enumerable.Range(0, 4096).Select(i => (byte)(i * 37)).ToArray());

        var sw = Stopwatch.StartNew();
        double? seconds = GstPlaybackApi.ProbeMediaDuration(junk);
        sw.Stop();

        seconds.Should().BeNull();
        sw.ElapsedMilliseconds.Should().BeLessThan(GstPlaybackApi.ProbeDurationTimeoutMs,
            "a file the demuxer cannot read must not hang until the timeout");
    }
}

[CollectionDefinition(Name)]
public sealed class MediaDurationProbeCollection
{
    public const string Name = "MediaDurationProbe";
}

/// <summary>形式ごとの 2 秒の素材を 1 回だけ作る（作れない形式は理由を覚えてスキップ）。</summary>
public sealed class MediaDurationProbeFixture
{
    private readonly Dictionary<string, (string? Path, string? Reason)> _clips = new();
    private readonly object _gate = new();
    private readonly string? _nativeSkipReason;

    public MediaDurationProbeFixture()
    {
        // アプリの DllImport の解決（tcs_gstreamer.dll と GStreamer の bin）は App の静的コンストラクタで登録する。
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(App).TypeHandle);
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, GstNative.Lib)))
            _nativeSkipReason = "tcs_gstreamer.dll is not next to the tests";
        else if (GstNativeLibraryResolver.FindGstRoot() is null)
            _nativeSkipReason = "GStreamer runtime is not installed";
    }

    internal void RequireNative() => Skip.If(_nativeSkipReason is not null, _nativeSkipReason);

    internal string RequireClip(string format)
    {
        RequireNative();
        (string? path, string? reason) = GetOrCreate(format);
        Skip.If(path is null, reason);
        return path!;
    }

    internal static double Fps(string format) => format switch
    {
        "h264_mkv" => 25,
        "h264_ts" or "h264_ts_tiny" => 50,
        "hevc_mp4" or "prores4444_mov" or "hap_mov" => 30000.0 / 1001,
        "prores422hq_mov" or "hapq_mov" => 60000.0 / 1001,
        _ => 30,
    };

    /// <summary>素材の長さ。TS は tsdemux が長さを出せる大きさ（約 100KB 以上）にするため 6 秒・720p。</summary>
    internal static double Seconds(string format) => format == "h264_ts" ? 6 : 2;

    private static string Size(string format) => format == "h264_ts" ? "1280x720" : "640x360";

    internal static string ExtensionOf(string format) => Recipe(format).Extension;

    private static (string Extension, string Args) Recipe(string format) => format switch
    {
        "h264_mp4" => (".mp4", "-c:v libx264 -pix_fmt yuv420p"),
        "h264_aac_mp4" => (".mp4", "-c:v libx264 -pix_fmt yuv420p -c:a aac"),
        "h264_mkv" => (".mkv", "-c:v libx264 -pix_fmt yuv420p"),
        "h264_ts" or "h264_ts_tiny" => (".ts", "-c:v libx264 -pix_fmt yuv420p -f mpegts"),
        "hevc_mp4" => (".mp4", "-c:v libx265 -pix_fmt yuv420p -tag:v hvc1"),
        "prores422hq_mov" => (".mov", "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le"),
        "prores4444_mov" => (".mov", "-c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le"),
        "hap_mov" => (".mov", "-c:v hap -format hap"),
        "hapq_mov" => (".mov", "-c:v hap -format hap_q"),
        "av1_mp4" => (".mp4", "-c:v libaom-av1 -cpu-used 8 -row-mt 1 -pix_fmt yuv420p"),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private (string? Path, string? Reason) GetOrCreate(string format)
    {
        lock (_gate)
        {
            if (_clips.TryGetValue(format, out var cached))
                return cached;
            var made = Create(format);
            _clips[format] = made;
            return made;
        }
    }

    private static (string? Path, string? Reason) Create(string format)
    {
        string dir = TestTempPaths.Combine("f7-durations");
        Directory.CreateDirectory(dir);
        return CreateClip(format, Seconds(format), Size(format), Path.Combine(dir, format + Recipe(format).Extension));
    }

    /// <summary>
    /// 形式・長さ・大きさを指定して素材を作る（E2E も使う）。作れなければ理由を返す。
    /// 拡張子は <paramref name="path"/> の側で形式に合わせること（<see cref="ExtensionOf"/>）。
    /// </summary>
    internal static (string? Path, string? Reason) CreateClip(string format, double durationSeconds, string size, string path)
    {
        if (!TestVideoFactory.FfmpegAvailable())
            return (null, "ffmpeg is not available");
        string args = Recipe(format).Args;
        if (File.Exists(path)) File.Delete(path);

        string rate = Fps(format) switch
        {
            25 => "25",
            50 => "50",
            30 => "30",
            var f when Math.Abs(f - 30000.0 / 1001) < 1e-6 => "30000/1001",
            _ => "60000/1001",
        };
        bool audio = format == "h264_aac_mp4";
        string seconds = durationSeconds.ToString(CultureInfo.InvariantCulture);
        string inputs = $"-f lavfi -i testsrc=duration={seconds}:size={size}:rate={rate}"
            + (audio ? $" -f lavfi -i sine=frequency=1000:duration={seconds}" : "");
        string arguments = $"-nostdin -y -hide_banner -loglevel error {inputs} {args} -t {seconds} \"{path}\"";
        (string? made, string? reason) = Run(FfmpegTool.Ffmpeg, arguments, path, format);
        // 試験の ffmpeg（TCS_FFMPEG → tools\ffmpeg → 既定 → PATH）に hap が無いことがある。HAP だけ PATH の ffmpeg も試す。
        // 試験基盤の 8 で、固定の版（tools\ffmpeg の 8.0.1 full）なら hap を含む。
        if (made is null && format.StartsWith("hap", StringComparison.Ordinal))
            (made, reason) = Run("ffmpeg", arguments, path, format);
        return (made, reason);
    }

    private static (string? Path, string? Reason) Run(string ffmpeg, string arguments, string path, string format)
    {
        var psi = new ProcessStartInfo(ffmpeg, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (null, "ffmpeg could not be started");
            Task<string> err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(120_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (null, $"{format}: ffmpeg timed out");
            }
            if (p.ExitCode != 0 || !File.Exists(path))
                return (null, $"{format}: this ffmpeg cannot make the clip ({err.Result.Trim()})");
        }
        catch (Exception ex)
        {
            return (null, $"{format}: {ex.Message}");
        }
        return (path, null);
    }

    internal static double? FfprobeDuration(string path)
    {
        var psi = new ProcessStartInfo(FfmpegTool.Ffprobe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000) || p.ExitCode != 0) return null;
            return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

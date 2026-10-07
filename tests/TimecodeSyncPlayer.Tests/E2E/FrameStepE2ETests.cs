using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// v0.6.6 R-4: 1 フレーム送り・戻しのボタン。位置は TimeLabel ではなく、配信フレームの PTS
/// （"FrameStep landed ... delivered=... deliveredFrame=..."）で読む。送った本数は "Seek command sent source=FrameStep" の原文で数える。
/// 素材は 29.97 と 59.94 の H.264（720p、10 秒）を試験で作る。実機（GPU）を使う。1 本ずつ回す。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class FrameStepE2ETests
{
    private const string SentText = "Seek command sent source=FrameStep ";
    private static readonly Regex Sent = new(
        @"Seek command sent source=FrameStep steps=(?<steps>-?\d+) base=(?<base>-?\d+) target=(?<target>-?\d+) targetSeconds=(?<ts>[0-9.]+) fps=(?<fps>[0-9.]+) delivered=(?<delivered>[0-9.]+) success=true",
        RegexOptions.Compiled);
    private static readonly Regex Landed = new(
        @"FrameStep landed reason=frame delivered=(?<delivered>[0-9.]+) deliveredFrame=(?<frame>-?\d+) target=(?<target>-?\d+) deliveredGen=\d+ seekGen=\d+ next=(?<next>\w+)",
        RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public FrameStepE2ETests(ITestOutputHelper output) => _output = output;

    public static readonly TheoryData<string> Media = new() { "h264_2997_720p", "h264_5994_720p" };

    /// <summary>再生中に押すと止まり、止まったフレームから 1 フレーム進む。</summary>
    [SkippableTheory]
    [MemberData(nameof(Media))]
    public void PressWhilePlaying_PausesAndStepsOneFrame(string media)
    {
        (string exe, string video) = Prerequisites(media);
        using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{video}\"");
        WaitForDuration(app);
        EnsurePlaying(app);
        Thread.Sleep(1500);

        DateTime since = DateTime.Now;
        app.Button("BtnFrameFwd").Invoke();
        E2EAssert.WaitUntil(() => app.Button("BtnPlay").Name == "▶", TimeSpan.FromSeconds(3));
        List<string> lines = WaitForFinalLanding(exe, since);

        Match sent = lines.Select(l => Sent.Match(l)).Single(m => m.Success);
        long baseFrame = Long(sent, "base");
        Long(sent, "target").Should().Be(baseFrame + 1);
        Match landed = lines.Select(l => Landed.Match(l)).Last(m => m.Success);
        _output.WriteLine($"{media}: base={baseFrame} landedFrame={landed.Groups["frame"].Value} delivered={landed.Groups["delivered"].Value}");
        Long(landed, "frame").Should().Be(baseFrame + 1, "the delivered frame after the step is one frame after the paused frame");
        app.Button("BtnPlay").Name.Should().Be("▶", "the step leaves playback paused");
        lines.Should().Contain(l => l.Contains("Sync lifecycle", StringComparison.Ordinal) && l.Contains("frame-step", StringComparison.Ordinal),
            "the step cancels pending sync like the 10 second skip");

        Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
    }

    /// <summary>一時停止中に 10 連打で 10 フレーム進み、10 連打で戻すと元の PTS に戻る。</summary>
    [SkippableTheory]
    [MemberData(nameof(Media))]
    public void TenPresses_MoveTenFrames_AndBackReturnsToTheOriginalPts(string media)
    {
        (string exe, string video) = Prerequisites(media);
        using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{video}\"");
        WaitForDuration(app);
        EnsurePaused(app);
        // 先頭の端から離す（3 割の位置へ）。
        app.Slider("SeekBar").Patterns.RangeValue.Pattern.SetValue(0.3);
        Thread.Sleep(1500);

        // 10 回送る。
        DateTime forwardSince = DateTime.Now;
        Button fwd = app.Button("BtnFrameFwd");
        for (int i = 0; i < 10; i++) fwd.Invoke();
        List<string> forward = WaitForFinalLanding(exe, forwardSince);
        Match firstSent = forward.Select(l => Sent.Match(l)).First(m => m.Success);
        long startFrame = Long(firstSent, "base");
        double startPts = double.Parse(firstSent.Groups["delivered"].Value, CultureInfo.InvariantCulture);
        double fps = double.Parse(firstSent.Groups["fps"].Value, CultureInfo.InvariantCulture);
        int forwardSends = forward.Count(l => l.Contains(SentText, StringComparison.Ordinal));
        Match forwardLanded = forward.Select(l => Landed.Match(l)).Last(m => m.Success);
        _output.WriteLine($"{media}: forward start={startFrame} pts={startPts:F6} sends={forwardSends} landedFrame={forwardLanded.Groups["frame"].Value} delivered={forwardLanded.Groups["delivered"].Value}");
        forwardSends.Should().BeInRange(1, 10);
        Long(forwardLanded, "frame").Should().Be(startFrame + 10, "ten presses move ten frames");

        // 10 回戻す。
        DateTime backSince = DateTime.Now;
        Button back = app.Button("BtnFrameBack");
        for (int i = 0; i < 10; i++) back.Invoke();
        List<string> backward = WaitForFinalLanding(exe, backSince);
        int backSends = backward.Count(l => l.Contains(SentText, StringComparison.Ordinal));
        Match backLanded = backward.Select(l => Landed.Match(l)).Last(m => m.Success);
        double backPts = double.Parse(backLanded.Groups["delivered"].Value, CultureInfo.InvariantCulture);
        _output.WriteLine($"{media}: back sends={backSends} landedFrame={backLanded.Groups["frame"].Value} delivered={backPts:F6} diff={backPts - startPts:F6}");
        backSends.Should().BeInRange(1, 10);
        Long(backLanded, "frame").Should().Be(startFrame, "ten presses back return to the original frame");
        // 正確なシークの後の配信 PTS は、目標（フレームの頭 + 半フレーム）に切り詰められて出る（例 29.97 で 1.651650）。
        // 元の PTS（再生中・送りの前のフレームの頭）とは同じフレームの中で最大半フレームずれる。
        Math.Abs(backPts - startPts).Should().BeLessThanOrEqualTo((0.5 / fps) + 1e-5, "the delivered PTS is inside the original frame");

        app.Button("BtnPlay").Name.Should().Be("▶");
        Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
    }

    /// <summary>最後の "FrameStep landed" が next=none になるまで待ち（飛行中が無い）、since からの行を返す。</summary>
    private List<string> WaitForFinalLanding(string exe, DateTime since)
    {
        List<string> lines = [];
        int stableChecks = 0;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            lines = ReadLines(exe, since);
            Match last = lines.Select(l => Landed.Match(l)).LastOrDefault(m => m.Success) ?? Match.Empty;
            int lastLanded = lines.FindLastIndex(l => Landed.IsMatch(l));
            int lastSent = lines.FindLastIndex(l => l.Contains(SentText, StringComparison.Ordinal));
            bool settled = last.Success && last.Groups["next"].Value == "none" && lastLanded > lastSent;
            stableChecks = settled ? stableChecks + 1 : 0;
            if (stableChecks >= 3) break;
            Thread.Sleep(200);
        }
        foreach (string line in lines.Where(l => l.Contains("FrameStep", StringComparison.Ordinal) || l.Contains("frame-step", StringComparison.Ordinal)))
            _output.WriteLine(line.TrimEnd());
        stableChecks.Should().BeGreaterThanOrEqualTo(3, "the last frame step lands with nothing queued");
        return lines;
    }

    private static long Long(Match m, string group) => long.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    private static (string Exe, string Video) Prerequisites(string media)
    {
        (string exe, string? reason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(reason is not null, reason);
        Skip.IfNot(TestVideoFactory.FfmpegAvailable(), "ffmpeg is not available");
        string dir = TestTempPaths.Combine("r4-framestep");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, media + ".mp4");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            string rate = media.Contains("5994", StringComparison.Ordinal) ? "60000/1001" : "30000/1001";
            var psi = new ProcessStartInfo(FfmpegTool.Ffmpeg,
                $"-nostdin -y -hide_banner -loglevel error -f lavfi -i testsrc=duration=10:size=1280x720:rate={rate} -c:v libx264 -pix_fmt yuv420p -t 10 \"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg could not be started");
            if (!p.WaitForExit(120_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("ffmpeg timed out");
            }
            if (p.ExitCode != 0 || !File.Exists(path))
                throw new InvalidOperationException($"ffmpeg failed to make {media} (exit={p.ExitCode})");
        }
        return (exe, path);
    }

    private static List<string> ReadLines(string exe, DateTime since) =>
        AppLogReader.ReadLinesSince(AppLogReader.LogDirectoryForExe(exe), since.AddMilliseconds(-200)).ToList();

    private static void WaitForDuration(E2EAppRunner app) =>
        E2EAssert.WaitUntil(() => app.Text("TimeLabel").Contains('/') && !app.Text("TimeLabel").EndsWith("00:00:00:00", StringComparison.Ordinal),
            TimeSpan.FromSeconds(8));

    private static void EnsurePlaying(E2EAppRunner app)
    {
        Button play = app.Button("BtnPlay");
        if (play.Name == "▶")
            play.Invoke();
        E2EAssert.WaitUntil(() => play.Name == "⏸", TimeSpan.FromSeconds(3));
    }

    private static void EnsurePaused(E2EAppRunner app)
    {
        Button play = app.Button("BtnPlay");
        if (play.Name == "⏸")
            play.Invoke();
        E2EAssert.WaitUntil(() => play.Name == "▶", TimeSpan.FromSeconds(3));
    }
}

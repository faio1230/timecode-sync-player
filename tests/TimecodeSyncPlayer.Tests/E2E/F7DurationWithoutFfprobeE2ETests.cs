using System.Globalization;
using System.IO;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Tests.Gst;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// v0.6.6 F-7（現場の報告）: ffprobe の無い機体でクリップの長さが 0 のままになり、タイムラインの幅・
/// 追加時の自動オフセット・Continue の「今どのクリップか」が壊れていた。
/// <b>アプリのプロセスの環境だけ</b> PATH から ffmpeg / ffprobe のあるフォルダを外して起動し、
/// 3 本（HAP を 1 本含む）を足して、(1) 自動オフセット、(2) タイムラインの幅、(3) Continue の判定を確かめる。
/// ランナーと試験のプロセスの PATH は変えない（素材と LTC の wav は試験側の ffmpeg で作る）。
/// 実機（GPU・VB-Cable の LTC）を使う。回すのは実機が空いているときだけ。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class F7DurationWithoutFfprobeE2ETests
{
    private const double Fps = 25;
    private readonly ITestOutputHelper _output;

    public F7DurationWithoutFfprobeE2ETests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AppWithoutFfprobe_ThreeClipsIncludingHap_GetLengthsOffsetsTimelineAndContinue()
    {
        (string exe, string? reason) = E2EAppRunner.ResolvePrereqs();
        reason.Should().BeNull("F-7 is a mandatory E2E");
        Assert.True(LtcSignalPlayer.TryCreateCablePlayer(out var signal, out string? cableReason), cableReason);
        using LtcSignalPlayer signalOwner = signal!;

        // 素材は試験側で作る（試験のプロセスの PATH はそのまま）。
        var sources = new (string Format, double Seconds)[] { ("h264_mp4", 6), ("hap_mov", 6), ("h264_mp4", 14) };
        var copies = new List<string>();
        try
        {
            for (int i = 0; i < sources.Length; i++)
            {
                (string format, double seconds) = sources[i];
                string made = TestTempPaths.Combine("f7-e2e", $"clip{i}_{format}{MediaDurationProbeFixture.ExtensionOf(format)}");
                Directory.CreateDirectory(Path.GetDirectoryName(made)!);
                (string? path, string? why) = MediaDurationProbeFixture.CreateClip(format, seconds, "1280x720", made);
                path.Should().NotBeNull($"the {format} clip must be made ({why})");
                copies.Add(SystemScenarioE2ETests.CreateDialogFileCopy(path!, $"f7-{i}"));
            }

            // 期待の長さはアプリと同じ軽い関数で読む（同じ出所）。ffprobe には頼らない。
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(App).TypeHandle);
            double[] lengths = copies.Select(c => GstPlaybackApi.ProbeMediaDuration(c)
                ?? throw new InvalidOperationException($"probe failed: {c}")).ToArray();
            _output.WriteLine("lengths: " + string.Join(", ", lengths.Select(l => l.ToString("F3", CultureInfo.InvariantCulture))));

            // 試験基盤の 9: 同梱の GStreamer があるときは GStreamer の bin も外す（アプリは同梱だけで起動する）。
            string appPath = PathWithoutFfmpeg(Environment.GetEnvironmentVariable("PATH"), Path.GetDirectoryName(exe)!);
            FfprobePresence.FindOnPath(appPath).Should().BeNull("the app process must not see ffprobe");
            DateTime started = DateTime.Now;
            using var app = E2EAppRunner.Start(exe, "--vo null", settingsFilePath: null,
                environment: new Dictionary<string, string?> { ["PATH"] = appPath });

            SystemScenarioE2ETests.AddPlaylistFiles(app, copies.ToArray());
            ListBox playlist = SystemScenarioE2ETests.Playlist(app);
            E2EAssert.WaitUntil(() => playlist.Items.Length == 3, TimeSpan.FromSeconds(8));

            // (1) 自動オフセット: 2 行目は 1 本目の長さ、3 行目は 1・2 本目の和（追加時に自動オフセット、既定 ON）。
            double[] expectedOffsets = { 0, lengths[0], lengths[0] + lengths[1] };
            try
            {
                E2EAssert.WaitUntil(() =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        double? offset = RowOffsetSeconds(playlist, i);
                        // 許す幅: 表示は 30fps の hh:mm:ss:ff に丸めるので 1 フレーム（1/30 秒）まで。
                        if (offset is null || Math.Abs(offset.Value - expectedOffsets[i]) > 1.0 / 30) return false;
                    }
                    return true;
                }, TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException("auto offsets: " + string.Join(", ", Enumerable.Range(0, 3).Select(i =>
                    $"row {i} text='{RowOffsetText(playlist, i)}' expected={expectedOffsets[i]:F3}")), ex);
            }
            for (int i = 0; i < 3; i++)
                _output.WriteLine($"row {i}: offset={RowOffsetSeconds(playlist, i):F3} expected={expectedOffsets[i]:F3}");

            // 試験基盤の 9: アプリに渡した環境と、アプリが読んだ GStreamer（長さが入った後なので読み込み済み）。
            _output.WriteLine(app.AppEnvironment.Describe());
            (string? gstPath, string gstOrigin) = app.LoadedGstreamer();
            _output.WriteLine($"app-gstreamer: loaded={gstPath ?? "<not loaded>"} origin={gstOrigin}");
            if (app.AppEnvironment.BundledNextToExe)
            {
                app.AppEnvironment.RootPassed.Should().BeNull("the app runs with the bundled GStreamer only, as in production");
                app.AppEnvironment.PathGstEntriesPassed.Should().Be(0, "no GStreamer bin is passed on the app's PATH");
                gstOrigin.Should().Be("bundled", "the app must load the GStreamer bundled next to the exe");
            }

            // (2) タイムラインの幅: 長さが 0 だと全長 0 で、どれだけ寄せても横のスクロールの最大値が 0 のまま（F-1）。
            double total = lengths.Sum();
            app.Button("BtnTimeline").Invoke();
            AutomationElement? hscroll = null;
            E2EAssert.WaitUntil(() =>
            {
                hscroll = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("HorizontalScrollBar"));
                return hscroll != null;
            }, TimeSpan.FromSeconds(5));
            AutomationElement? zoomIn = app.MainWindow
                .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                .FirstOrDefault(b => b.Name == "+");
            zoomIn.Should().NotBeNull("the timeline zoom-in button");
            double ReadMaximum() => hscroll!.Patterns.RangeValue.Pattern.Maximum.Value;
            // ズームは 1 段 1.2 倍、最小 0.01 秒/px。全長が表示幅を超えるまで寄せる（既定の 1 秒/px では全部が収まり最大値は 0）。
            double maximum = 0;
            int zooms = 0;
            for (; zooms < 40 && maximum <= 0; zooms++)
            {
                zoomIn!.AsButton().Invoke();
                Thread.Sleep(100);
                maximum = ReadMaximum();
            }
            maximum.Should().BeGreaterThan(0, "the timeline is as wide as the clips");
            maximum.Should().BeLessThan(total, "the scroll range is the total length minus the visible part");
            // さらに 2 段寄せる。最大値 = 全長 − 表示幅、表示幅は 1 段で 1/1.2 になるので、
            // (全長 − 最大値) の比が 1.2 になるのは、アプリの全長が試験の全長（長さの和）と同じときだけ。
            // 偶然の一致を避けるため 2 か所（段 n→n+1 と n+1→n+2）で確かめる。
            var maxima = new List<double> { maximum };
            for (int step = 0; step < 2; step++)
            {
                zoomIn!.AsButton().Invoke();
                Thread.Sleep(100);
                maxima.Add(ReadMaximum());
            }
            _output.WriteLine($"timeline: total={total:F3} zooms={zooms} horizontalMaxima=" +
                string.Join(" -> ", maxima.Select(m => m.ToString("F3", CultureInfo.InvariantCulture))));
            for (int k = 0; k + 1 < maxima.Count; k++)
            {
                double ratio = (total - maxima[k]) / (total - maxima[k + 1]);
                _output.WriteLine($"timeline: ratio[{k}]={ratio:F4}");
                maxima[k + 1].Should().BeGreaterThan(maxima[k], "zooming in widens the scroll range");
                ratio.Should().BeApproximately(1.2, 0.02,
                    "the app's timeline length equals the sum of the clip lengths");
            }

            // (3) Continue: 各クリップの中ほどの LTC で、そのクリップが選ばれる（長さ 0 だと一度も当たらない）。
            ConfigureContinue(app);
            for (int i = 0; i < 3; i++)
            {
                double at = Math.Round((expectedOffsets[i] + lengths[i] / 2) * Fps) / Fps;
                string name = Path.GetFileNameWithoutExtension(copies[i]);
                signalOwner.PlayHeld(at, Fps, TimeSpan.FromSeconds(15));
                Wait(app, () => app.Text("CurrentTrackLabel") == $"Sync: {name}",
                    $"TC {at:F2}: expected clip {i} ({sources[i].Format})");
                _output.WriteLine($"TC {at:F2}: {app.Text("CurrentTrackLabel")}");
            }
            signalOwner.Stop();

            // 現場のログで分かるように、起動時に ffprobe の有無が 1 行出ている。長さの読み取りの失敗は無い。
            string log = AppLogReader.ReadTextSince(AppLogReader.LogDirectoryForExe(exe), started.AddSeconds(-1));
            log.Should().Contain("ffprobe: not found on PATH");
            log.Should().NotContain("Media duration probe failed");
            log.Should().NotContain("ffprobe is not available, cannot read duration");

            Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            signalOwner.Stop();
            foreach (string copy in copies)
                SystemScenarioE2ETests.DeleteDialogFile(copy);
        }
    }

    /// <summary>
    /// PATH から ffmpeg.exe / ffprobe.exe のあるフォルダを外す（アプリのプロセスに渡す分だけ）。
    /// exeDir を渡し、その隣に同梱の gstreamer\bin があるときは GStreamer の bin も外す（試験基盤の 9）。
    /// </summary>
    internal static string PathWithoutFfmpeg(string? path, string? exeDir = null)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (exeDir != null) path = E2EAppRunner.PathForApp(path, exeDir);
        IEnumerable<string> kept = path.Split(Path.PathSeparator).Where(entry =>
        {
            string dir = entry.Trim().Trim('"');
            if (dir.Length == 0) return false;
            try
            {
                return !File.Exists(Path.Combine(dir, "ffmpeg.exe")) && !File.Exists(Path.Combine(dir, "ffprobe.exe"));
            }
            catch (ArgumentException)
            {
                return true;
            }
        });
        return string.Join(Path.PathSeparator, kept);
    }

    private static double? RowOffsetSeconds(ListBox playlist, int index) =>
        RowOffsetText(playlist, index) is { } text ? TimecodeSeconds(text, 30) : null;

    private static string? RowOffsetText(ListBox playlist, int index)
    {
        try
        {
            AutomationElement[] items = playlist.Items;
            if (items.Length <= index) return $"<no row; items={items.Length}>";
            // 一覧の高さに入らない行は中身が UIA に出ない（仮想化）。読む前に見える位置へ送る。
            items[index].Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
            AutomationElement? box = items[index].FindFirstDescendant(cf => cf.ByAutomationId("TimelineOffsetTextBox"));
            return box is null ? "<no TimelineOffsetTextBox>" : box.AsTextBox().Text;
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}: {ex.Message}>";
        }
    }

    private static double? TimecodeSeconds(string text, double fps)
    {
        string[] parts = text.Split(':', ';');
        if (parts.Length != 4) return null;
        if (!parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) return null;
        int[] n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return n[0] * 3600 + n[1] * 60 + n[2] + n[3] / fps;
    }

    private static void ConfigureContinue(E2EAppRunner app)
    {
        app.Button("BtnRefreshLtcDevices").Invoke();
        var devices = app.Combo("LtcDeviceCombo");
        int index = Array.FindIndex(devices.Items,
            item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, "CABLE Output not listed.");
        devices.Select(index);
        app.Combo("LtcFpsModeCombo").Select(2);
        app.Combo("LtcSignalLossModeCombo").Select(0);
        app.Button("BtnStartLtc").Invoke();
        app.Combo("SyncModeCombo").Select(1);
        app.Combo("GapBehaviorCombo").Select(0);
        if (!app.Button("BtnToggleSync").Name.Contains("ON", StringComparison.OrdinalIgnoreCase))
            app.Button("BtnToggleSync").Invoke();
    }

    private static void Wait(E2EAppRunner app, Func<bool> condition, string description)
    {
        try
        {
            E2EAssert.WaitUntil(() => !app.Process.HasExited && condition(), TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                description + $"; track={app.Text("CurrentTrackLabel")}; ltc={app.Text("LtcTimecodeText")}", ex);
        }
    }
}

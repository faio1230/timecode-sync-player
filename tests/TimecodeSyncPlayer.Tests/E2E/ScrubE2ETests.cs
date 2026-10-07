using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// v0.6.6 F-3・R-3（現場の報告）: シークバーをつかんだまま動かすと、再生位置も動く（スクラブ）。
/// 押したまま 3 段動かし、動かしている間にスクラブのシーク（"Seek command sent source=Scrub"）が 2 本以上出ること、
/// 離した後の位置が離した点から 1 フレーム以内であることを確かめる。位置は TimeLabel（ドラッグ中は予告の表示）
/// ではなく、再生の位置（離したときのシークが着地した配信フレームの PTS、"Scrub landed ... released=True" の delivered）で読む。
/// 実機（GPU）とマウスの入力を使う。1 本ずつ回す。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class ScrubE2ETests
{
    private static readonly Regex ScrubSent = new(@"Seek command sent source=Scrub ", RegexOptions.Compiled);
    private static readonly Regex ReleaseSent = new(
        @"Seek command sent source=MouseUp value=(?<value>[0-9.]+) duration=(?<duration>[0-9.]+) target=(?<target>[0-9.]+) success=true",
        RegexOptions.Compiled);
    private static readonly Regex ReleaseLanded = new(
        @"Scrub landed reason=frame surface=SeekBar flightMs=(?<ms>[0-9.]+) delivered=(?<delivered>[0-9.]+) deliveredGen=\d+ seekGen=\d+ released=true",
        RegexOptions.Compiled);
    private static readonly Regex Metadata = new(@"FetchMetadata: \d+x\d+ (?<fps>[0-9.]+)fps", RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public ScrubE2ETests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ScrubWhilePlaying_SendsSeeksWhileHeld_AndLandsOnTheReleasePoint() =>
        ScrubAndCheck(playing: true);

    [Fact]
    public void ScrubWhilePaused_SendsSeeksWhileHeld_AndLandsOnTheReleasePoint() =>
        ScrubAndCheck(playing: false);

    /// <summary>つまみをつかんで動かす（つまみが自分で捕捉する経路）。つまみは値 0.5 のとき帯の中央にある。</summary>
    [Fact]
    public void ScrubByThumbWhilePaused_SendsSeeksWhileHeld_AndLandsOnTheReleasePoint() =>
        ScrubAndCheck(playing: false, ratios: [0.50, 0.60, 0.70, 0.80], presetValue: 0.5);

    /// <summary>
    /// Sync ON（Continue）で LTC を流しながらスクラブし、離した後に同期が戻ること。手動のシーク（離したときの 1 本）
    /// の後と同じく、同期のシークの目標が LTC の位置に戻る。確かめは Release の exe でも出る Information の行だけで行う。
    /// </summary>
    [Fact]
    public void ScrubWithSyncOn_SyncReturnsAfterRelease()
    {
        (string exe, string video) = SystemScenarioE2ETests.RequirePrerequisites();
        Assert.True(LtcSignalPlayer.TryCreateCablePlayer(out var signal, out string? cableReason), cableReason);
        using LtcSignalPlayer signalOwner = signal!;
        DateTime started = DateTime.Now;
        using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{video}\"");
        try
        {
            WaitForDuration(app);
            ConfigureSync(app);
            EnsurePlaying(app);

            // LTC は 2 秒から 25fps で流す。同期が乗ってからつかむ。
            const double ltcStart = 2.0;
            var ltcClock = Stopwatch.StartNew();
            signalOwner.Play(new LtcTimecode(0, 0, 2, 0, false), 25, TimeSpan.FromSeconds(20));
            Thread.Sleep(3000);

            DateTime scrubStarted = DateTime.Now;
            // 同期の位置（5 秒前後）から離れた 60〜75% （12〜15 秒）でスクラブする。
            DragSeekBar(app, [0.60, 0.65, 0.70, 0.75], dwellMs: 400);
            double ltcAtRelease = ltcStart + ltcClock.Elapsed.TotalSeconds;
            Thread.Sleep(4000);
            signalOwner.Stop();

            List<string> lines = ReadLines(exe, scrubStarted);
            DumpTail(lines, "Seek command sent", "Scrub landed", "Scrub summary", "Sync lifecycle", "Continue mode: sync seek",
                "sync.gate seek-settled", "Timecode sync");
            lines.Count(l => ScrubSent.IsMatch(l)).Should().BeGreaterThanOrEqualTo(2, "seeks are sent while the bar is held");

            int releaseIndex = lines.FindLastIndex(l => ReleaseSent.IsMatch(l));
            releaseIndex.Should().BeGreaterThanOrEqualTo(0, "the release seek is sent");
            // 手動のシークの後と同じ行: 手動のシークの知らせ（同期の保留の取り消し）と、その後の同期のシークの着地。
            lines.Take(releaseIndex + 1).Should().Contain(l => Regex.IsMatch(l, "Sync lifecycle: \"?ManualSeek\"? source=seekbar-commit"),
                "the release goes through the manual seek path");
            lines.Should().Contain(l => Regex.IsMatch(l, "Sync lifecycle: \"?ManualSeek\"? source=seekbar-scrub"),
                "the scrub seeks cancel pending sync like a manual seek");
            // 離した後（"Seek MouseUp" の行から）の手動のシークのできごとは 1 回だけ。
            int mouseUpIndex = lines.FindLastIndex(l => l.Contains("Seek MouseUp ", StringComparison.Ordinal));
            mouseUpIndex.Should().BeGreaterThanOrEqualTo(0);
            List<string> afterRelease = lines.Skip(mouseUpIndex)
                .Where(l => Regex.IsMatch(l, "Sync lifecycle: \"?ManualSeek\"?")).ToList();
            foreach (string l in afterRelease) _output.WriteLine("after release: " + l.TrimEnd());
            afterRelease.Should().ContainSingle("only the release is a manual seek after letting go");
            // 手動のシークの知らせは 1 回のドラッグにつき離したときの 1 本だけ。Release の exe でも出る Information の行
            // （"Sync lifecycle: ManualSeek"）で確かめる: 離した後は source=seekbar-commit がちょうど 1 回、
            // 押している間（"Seek MouseDown" から "Seek MouseUp" まで）は source=seekbar-scrub だけ。
            int mouseDownIndex = lines.FindLastIndex(mouseUpIndex, l => l.Contains("Seek MouseDown ", StringComparison.Ordinal));
            mouseDownIndex.Should().BeGreaterThanOrEqualTo(0);
            afterRelease.Count(l => Regex.IsMatch(l, "Sync lifecycle: \"?ManualSeek\"? source=seekbar-commit"))
                .Should().Be(1, "the release is the only manual seek notification after letting go");
            List<string> whileHeld = lines.Skip(mouseDownIndex).Take(mouseUpIndex - mouseDownIndex)
                .Where(l => Regex.IsMatch(l, "Sync lifecycle: \"?ManualSeek\"?")).ToList();
            foreach (string l in whileHeld) _output.WriteLine("while held: " + l.TrimEnd());
            whileHeld.Should().OnlyContain(l => Regex.IsMatch(l, "Sync lifecycle: \"?ManualSeek\"? source=seekbar-scrub"),
                "mid-scrub seeks do not notify a manual seek");
            // 離した後の同期のシーク（Information の "Continue mode: sync seek"、Single なら "Timecode sync seek"）の目標が
            // LTC の位置に戻る。
            var syncSeeks = lines.Skip(releaseIndex + 1)
                .Select(l => Regex.Match(l, @"(?:Continue mode: sync seek|Timecode sync seek) ltc=[-0-9.]+ playback=[-0-9.]+ target=(?<t>[0-9.]+) .*success=true"))
                .Where(m => m.Success)
                .Select(m => double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture))
                .ToList();
            _output.WriteLine($"ltcAtRelease={ltcAtRelease:F2} sync seeks after release: {string.Join(", ", syncSeeks.Select(t => t.ToString("F3", CultureInfo.InvariantCulture)))}");
            syncSeeks.Should().Contain(t => Math.Abs(t - ltcAtRelease) < 2.0,
                "after the release, sync seeks back to the LTC position");

            Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            signalOwner.Stop();
        }
    }

    /// <summary>
    /// v0.6.6 F-2: タイムラインの行をつかんだまま動かすと、間引いたシークが出る（"Seek command sent source=TimelineScrub"）。
    /// 離したときの 1 本は今のクリックと同じ "Timeline seek" の行（endsScrub=True）で、その着地が離した点から 1 フレーム以内。
    /// </summary>
    [Fact]
    public void TimelineDragWhilePaused_SendsSeeksWhileHeld_AndLandsOnTheReleasePoint()
    {
        (string exe, string video) = SystemScenarioE2ETests.RequirePrerequisites();
        DateTime started = DateTime.Now;
        using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{video}\"");
        WaitForDuration(app);
        EnsurePaused(app);
        if (app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("HorizontalScrollBar")) is null)
            app.Button("BtnTimeline").Invoke();
        AutomationElement? hscroll = null;
        E2EAssert.WaitUntil(() =>
        {
            hscroll = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("HorizontalScrollBar"));
            return hscroll != null && hscroll.BoundingRectangle.Width > 0;
        }, TimeSpan.FromSeconds(5));
        AutomationElement zoomIn = app.MainWindow
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .First(b => b.Name == "+");
        Thread.Sleep(300);

        // 描画面はズームのボタンの帯の下から横のスクロールバーの上まで。既定は 1 秒/px（20 秒の素材は 20px）。
        // 1 行目は時間軸（20px）の下の 24px（論理 px）。倍率は「+」のボタン（高さ 22）から求める。
        var plus = zoomIn.BoundingRectangle;
        double scale = plus.Height / 22.0;
        var bar = hscroll!.BoundingRectangle;
        int top = plus.Bottom + (int)Math.Round(2 * scale);
        int y = top + (int)Math.Round(32 * scale);
        int X(double logical) => bar.Left + (int)Math.Round(logical * scale);

        DateTime dragStarted = DateTime.Now;
        DragPoints([(X(3), y), (X(8), y), (X(12), y), (X(16), y)], dwellMs: 400);
        Thread.Sleep(1500);

        List<string> all = ReadLines(exe, started);
        List<string> lines = ReadLines(exe, dragStarted);
        DumpTail(lines, "Seek command sent", "Timeline seek", "Scrub landed", "Scrub summary");

        lines.Count(l => l.Contains("Seek command sent source=TimelineScrub ", StringComparison.Ordinal))
            .Should().BeGreaterThanOrEqualTo(2, "seeks are sent while the timeline row is held and moved");
        Match release = lines.Select(l => Regex.Match(l,
                @"Timeline seek target=(?<target>[0-9.]+) trackIndex=0 success=true endsScrub=True"))
            .LastOrDefault(m => m.Success) ?? Match.Empty;
        release.Success.Should().BeTrue("the release sends the last seek on the pressed row");
        double target = double.Parse(release.Groups["target"].Value, CultureInfo.InvariantCulture);
        int releaseIndex = lines.FindLastIndex(l => l.Contains("endsScrub=True", StringComparison.Ordinal));
        Match landed = lines.Skip(releaseIndex + 1).Select(l => Regex.Match(l,
                @"Scrub landed reason=frame surface=Timeline flightMs=(?<ms>[0-9.]+) delivered=(?<delivered>[0-9.]+) deliveredGen=\d+ seekGen=\d+ released=true"))
            .FirstOrDefault(m => m.Success) ?? Match.Empty;
        landed.Success.Should().BeTrue("the release seek lands");
        double delivered = double.Parse(landed.Groups["delivered"].Value, CultureInfo.InvariantCulture);
        double fps = all.Select(l => Metadata.Match(l)).Where(m => m.Success)
            .Select(m => double.Parse(m.Groups["fps"].Value, CultureInfo.InvariantCulture)).LastOrDefault();
        if (fps <= 0) fps = 30;
        _output.WriteLine($"timeline releaseTarget={target:F3} landedDelivered={delivered:F3} diff={delivered - target:F4} scale={scale:F2}");
        Math.Abs(delivered - target).Should().BeLessThanOrEqualTo(1.0 / fps + 1e-3);

        Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
    }

    /// <summary>
    /// 計測専用（記録だけ）: TCS_SCRUB_MEDIA に素材（例: 4K60 の ProRes）を指定したときだけ動く。一時停止中と再生中に
    /// シークバーを 約 1.5 秒細かく動かし続け、"Scrub summary"（送った本数・1 秒あたりの本数・飛行の所要の中央値と最大）を
    /// 出力に写す。合否は付けない（スクラブの行が出たことだけ確かめる）。
    /// </summary>
    [SkippableFact]
    public void ScrubMeasurement_WithGivenMedia()
    {
        string? media = Environment.GetEnvironmentVariable("TCS_SCRUB_MEDIA");
        Skip.If(string.IsNullOrWhiteSpace(media) || !File.Exists(media), "Set TCS_SCRUB_MEDIA to a media file to measure scrubbing.");
        (string exe, string? reason) = E2EAppRunner.ResolvePrereqs();
        reason.Should().BeNull();
        using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{media}\"");
        WaitForDuration(app);
        foreach (bool playing in new[] { false, true })
        {
            if (playing) EnsurePlaying(app); else EnsurePaused(app);
            Thread.Sleep(800);
            DateTime since = DateTime.Now;
            // 10% → 80% を 120 段（1 段 約 10ms、約 1.5 秒）で動かし、最後に離す（飛行の所要より速く動かして間引きを効かせる）。
            double[] ratios = Enumerable.Range(0, 121).Select(i => 0.10 + (0.70 * i / 120.0)).ToArray();
            Slider slider = app.Slider("SeekBar");
            app.MainWindow.Focus();
            var rect = slider.BoundingRectangle;
            int y = rect.Top + (rect.Height / 2);
            DragPoints(ratios.Select(r => (rect.Left + (int)(rect.Width * r), y)).ToArray(), dwellMs: 5, midMs: 5);
            Thread.Sleep(2500);
            List<string> lines = ReadLines(exe, since);
            _output.WriteLine($"--- playing={playing}");
            DumpTail(lines, "Scrub summary", "Scrub landed", "pump: held");
            lines.Should().Contain(l => l.Contains("Scrub summary surface=SeekBar", StringComparison.Ordinal));
        }
        Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
    }

    private void ScrubAndCheck(bool playing, double[]? ratios = null, double? presetValue = null)
    {
        (string exe, string video) = SystemScenarioE2ETests.RequirePrerequisites();
        DateTime started = DateTime.Now;
        using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{video}\"");
        WaitForDuration(app);
        if (playing) EnsurePlaying(app); else EnsurePaused(app);
        if (presetValue is double preset)
            app.Slider("SeekBar").Patterns.RangeValue.Pattern.SetValue(preset);
        Thread.Sleep(500);

        DateTime scrubStarted = DateTime.Now;
        DragSeekBar(app, ratios ?? [0.20, 0.35, 0.50, 0.65], dwellMs: 400);
        Thread.Sleep(1500);

        List<string> all = ReadLines(exe, started);
        List<string> lines = ReadLines(exe, scrubStarted);
        DumpTail(lines, "Seek ", "Scrub landed", "Scrub summary", "pump: held");

        int scrubSeeks = lines.Count(l => ScrubSent.IsMatch(l));
        scrubSeeks.Should().BeGreaterThanOrEqualTo(2, "seeks are sent while the bar is held and moved in three steps");

        Match release = lines.Select(l => ReleaseSent.Match(l)).LastOrDefault(m => m.Success) ?? Match.Empty;
        release.Success.Should().BeTrue("the release sends the last seek");
        double target = double.Parse(release.Groups["target"].Value, CultureInfo.InvariantCulture);

        int releaseIndex = lines.FindLastIndex(l => ReleaseSent.IsMatch(l));
        Match landed = lines.Skip(releaseIndex + 1).Select(l => ReleaseLanded.Match(l)).FirstOrDefault(m => m.Success) ?? Match.Empty;
        landed.Success.Should().BeTrue("the release seek lands on a delivered frame of its generation");
        double delivered = double.Parse(landed.Groups["delivered"].Value, CultureInfo.InvariantCulture);

        double fps = all.Select(l => Metadata.Match(l)).Where(m => m.Success)
            .Select(m => double.Parse(m.Groups["fps"].Value, CultureInfo.InvariantCulture)).LastOrDefault();
        if (fps <= 0) fps = 30;
        double frame = 1.0 / fps;
        _output.WriteLine($"playing={playing} scrubSeeks={scrubSeeks} releaseTarget={target:F3} landedDelivered={delivered:F3} " +
            $"diff={delivered - target:F4} frame={frame:F4} flightMs={landed.Groups["ms"].Value}");
        Math.Abs(delivered - target).Should().BeLessThanOrEqualTo(frame + 1e-3,
            "the position after the release is within one frame of the release point");

        Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
    }

    /// <summary>シークバーの比率の位置で押し、残りの位置へ順に動かして（各 dwellMs 待つ）、最後の位置で離す。</summary>
    private static void DragSeekBar(E2EAppRunner app, double[] ratios, int dwellMs)
    {
        Slider slider = app.Slider("SeekBar");
        app.MainWindow.Focus();
        var rect = slider.BoundingRectangle;
        int y = rect.Top + (rect.Height / 2);
        DragPoints(ratios.Select(r => (rect.Left + (int)(rect.Width * r), y)).ToArray(), dwellMs);
    }

    /// <summary>最初の点で押し、残りの点へ順に動かして（各 dwellMs 待つ）、最後の点で離す。</summary>
    private static void DragPoints((int X, int Y)[] points, int dwellMs, int midMs = 30)
    {
        bool down = false;
        try
        {
            Mouse.Position = new System.Drawing.Point(points[0].X, points[0].Y);
            Thread.Sleep(100);
            Mouse.Down(MouseButton.Left);
            down = true;
            Thread.Sleep(dwellMs);
            for (int i = 1; i < points.Length; i++)
            {
                // 1 段を 2 回に分けて動かす（WPF の移動の通知を確実に出す）。
                (int fromX, int fromY) = points[i - 1];
                (int toX, int toY) = points[i];
                Mouse.Position = new System.Drawing.Point((fromX + toX) / 2, (fromY + toY) / 2);
                Thread.Sleep(midMs);
                Mouse.Position = new System.Drawing.Point(toX, toY);
                Thread.Sleep(dwellMs);
            }
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 5)
        {
            throw new SkipException("SendInput 拒否環境のためスキップ");
        }
        finally
        {
            if (down)
                Mouse.Up(MouseButton.Left);
        }
    }

    private static List<string> ReadLines(string exe, DateTime since) =>
        AppLogReader.ReadLinesSince(AppLogReader.LogDirectoryForExe(exe), since.AddMilliseconds(-200)).ToList();

    private void DumpTail(List<string> lines, params string[] keys)
    {
        foreach (string line in lines.Where(l => keys.Any(k => l.Contains(k, StringComparison.Ordinal))).TakeLast(80))
            _output.WriteLine(line.TrimEnd());
    }

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

    private static void ConfigureSync(E2EAppRunner app)
    {
        app.Button("BtnRefreshLtcDevices").Invoke();
        var devices = app.Combo("LtcDeviceCombo");
        int index = Array.FindIndex(devices.Items,
            item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, "CABLE Output not listed.");
        devices.Select(index);
        app.Combo("LtcFpsModeCombo").Select(2); // Fixed 25 fps.
        app.Combo("LtcSignalLossModeCombo").Select(0);
        app.Button("BtnStartLtc").Invoke();
        app.Combo("SyncModeCombo").Select(1); // Continue
        app.Combo("GapBehaviorCombo").Select(0);
        if (!app.Button("BtnToggleSync").Name.Contains("ON", StringComparison.OrdinalIgnoreCase))
            app.Button("BtnToggleSync").Invoke();
    }
}

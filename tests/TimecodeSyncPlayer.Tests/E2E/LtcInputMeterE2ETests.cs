using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.6 R-13: LTC の入力のメーターと受信の表示（ケーブルの折り返し）。LTC・音（正弦波・雑音）・無音を流し、
/// 受信の表示の 3 段とメーターを確かめる。VB-CABLE が無い環境ではスキップ。
/// 環境変数 TCS_R13_PNG_DIR を置くと、各段の LTC の欄を PNG に切り出す（窓の中だけ）。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class LtcInputMeterE2ETests : IClassFixture<TimecodeSyncPlayerFixture>
{
    private const int Fps = 25;
    private readonly TimecodeSyncPlayerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LtcInputMeterE2ETests(TimecodeSyncPlayerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SkippableFact]
    public void CableLoop_Ltc_ShowsReceivingAndMeterMoves()
    {
        RunWithCable(player =>
        {
            StartLtcMonitor();
            // 始めた直後は何も流していないので無音（メーターは 0）。
            WaitForReception("Silent", TimeSpan.FromSeconds(3));
            ReadMeterPercent().Should().BeLessThan(1.0);

            player.Play(new LtcTimecode(1, 0, 0, 0, false), Fps, TimeSpan.FromSeconds(12));

            WaitForReception("Receiving", TimeSpan.FromSeconds(5));
            ReadText("LtcReceptionText").Should().Be("LTC 受信中");
            // 試験の LTC は振幅 1.0（0 dBFS）。メーターは右の端の近くまで振れる。
            E2EAssert.WaitUntil(() => ReadMeterPercent() > 80.0, TimeSpan.FromSeconds(2));
            AssertStaysIn("Receiving", TimeSpan.FromSeconds(2));
            CapturePanel("r13-receiving.png");

            player.Stop();
            WaitForReception("Silent", TimeSpan.FromSeconds(4));
            E2EAssert.WaitUntil(() => ReadMeterPercent() < 1.0, TimeSpan.FromSeconds(2));
        });
    }

    [SkippableFact]
    public void CableLoop_Sine_ShowsSignalWithoutLtc()
    {
        RunWithCable(player =>
        {
            StartLtcMonitor();
            player.SendSamples(Sine(player.SampleRate, 1000.0, 0.5f, TimeSpan.FromSeconds(8)));

            WaitForReception("SignalWithoutLtc", TimeSpan.FromSeconds(5));
            ReadText("LtcReceptionText").Should().Be("信号あり・LTC なし");
            // 0.5 は -6 dBFS（メーターの 90）。
            E2EAssert.WaitUntil(() => ReadMeterPercent() is > 80.0 and < 95.0, TimeSpan.FromSeconds(2));
            AssertStaysIn("SignalWithoutLtc", TimeSpan.FromSeconds(3));
            CapturePanel("r13-signal-without-ltc.png");
        });
    }

    [SkippableFact]
    public void CableLoop_Noise_ShowsSignalWithoutLtc()
    {
        RunWithCable(player =>
        {
            StartLtcMonitor();
            player.SendSamples(Noise(player.SampleRate, 0.3f, TimeSpan.FromSeconds(8), seed: 4242));

            WaitForReception("SignalWithoutLtc", TimeSpan.FromSeconds(5));
            AssertStaysIn("SignalWithoutLtc", TimeSpan.FromSeconds(3));
        });
    }

    [SkippableFact]
    public void CableLoop_Silence_ShowsSilent()
    {
        RunWithCable(player =>
        {
            StartLtcMonitor();
            player.SendSamples(new float[player.SampleRate * 8]);

            // 始めた直後の表示も無音なので、1 秒以上置いてから続けて無音であることを見る。
            Thread.Sleep(1500);
            WaitForReception("Silent", TimeSpan.FromSeconds(3));
            ReadText("LtcReceptionText").Should().Be("無音");
            AssertStaysIn("Silent", TimeSpan.FromSeconds(3));
            ReadMeterPercent().Should().BeLessThan(1.0);
            CapturePanel("r13-silent.png");
        });
    }

    [SkippableFact]
    public void Stopped_HidesReceptionAndMeter()
    {
        SkipIfAppUnavailable();
        RestoreLtcUiState();

        // 隠した要素は UIA の木から外れる（残っていれば画面の外）。
        E2EAssert.WaitUntil(() => IsHidden("LtcReceptionText"), TimeSpan.FromSeconds(3));
        IsHidden("LtcLevelMeter").Should().BeTrue("LTC を止めている間はメーターを隠す");
        ReadText("LtcFormatText").Should().NotBeEmpty();
    }

    private void RunWithCable(Action<LtcSignalPlayer> body)
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        bool available = LtcSignalPlayer.TryCreateCablePlayer(out LtcSignalPlayer? created, out string? reason);
        Skip.If(!available, reason ?? "CABLE Input を利用できません。");
        using LtcSignalPlayer player = created!;
        try
        {
            SelectFixed25Fps();
            body(player);
        }
        finally
        {
            player.Stop();
            RestoreLtcUiState();
        }
    }

    private void WaitForReception(string status, TimeSpan timeout)
    {
        try
        {
            E2EAssert.WaitUntil(() => ReadItemStatus("LtcReceptionText") == status, timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"受信の表示が {status} になりません（今: {ReadItemStatus("LtcReceptionText")} / " +
                $"{ReadText("LtcReceptionText")}、メーター {ReadMeterPercent():F1}）", ex);
        }
    }

    private void AssertStaysIn(string status, TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            string current = ReadItemStatus("LtcReceptionText");
            current.Should().Be(status, $"{clock.Elapsed.TotalSeconds:F1} 秒の時点でも {status} のまま");
            Thread.Sleep(100);
        }
    }

    private double ReadMeterPercent() =>
        Find("LtcLevelMeter").Patterns.RangeValue.Pattern.Value.Value;

    private static float[] Sine(int sampleRate, double frequency, float amplitude, TimeSpan duration)
    {
        var samples = new float[(int)(sampleRate * duration.TotalSeconds)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / sampleRate);
        return samples;
    }

    private static float[] Noise(int sampleRate, float amplitude, TimeSpan duration, int seed)
    {
        var random = new Random(seed);
        var samples = new float[(int)(sampleRate * duration.TotalSeconds)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = amplitude * (float)(random.NextDouble() * 2.0 - 1.0);
        return samples;
    }

    /// <summary>LTC の欄だけを切り出す（TCS_R13_PNG_DIR があるときだけ）。窓を前に出してから窓の中を写す。</summary>
    private void CapturePanel(string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("TCS_R13_PNG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        Directory.CreateDirectory(directory);
        _fixture.MainWindow!.SetForeground();
        Thread.Sleep(300);
        System.Drawing.Rectangle window = _fixture.MainWindow.BoundingRectangle;
        System.Drawing.Rectangle combo = Find("LtcDeviceCombo").BoundingRectangle;
        System.Drawing.Rectangle refresh = Find("BtnRefreshLtcDevices").BoundingRectangle;
        System.Drawing.Rectangle meter = Find("LtcLevelMeter").BoundingRectangle;
        int left = Math.Max(window.Left, combo.Left - 10);
        int right = Math.Min(window.Right, refresh.Right + 10);
        int top = Math.Max(window.Top, combo.Top - 34);
        int bottom = Math.Min(window.Bottom, meter.Bottom + 22);
        string path = Path.Combine(directory, fileName);
        FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(left, top, right - left, bottom - top)).ToFile(path);
        _output.WriteLine($"PNG: {path}");
    }

    // ── 既存の LTC の E2E と同じ操作 ─────────────────────────────

    private void SkipIfAppUnavailable()
    {
        if (_fixture.Skipped)
            throw new SkipException(_fixture.SkipReason ?? "E2E アプリを起動できませんでした。");
    }

    private void SelectCableCaptureDevice()
    {
        string? captureDeviceName = LtcSignalPlayer.FindCableCaptureDeviceName();
        Skip.If(captureDeviceName is null, "有効な VB-CABLE 録音デバイス（CABLE Output）が見つかりません。");

        Find("BtnRefreshLtcDevices").AsButton().Invoke();
        ComboBox deviceCombo = Find("LtcDeviceCombo").AsComboBox();
        int selectedIndex = -1;
        E2EAssert.WaitUntil(
            () =>
            {
                selectedIndex = Array.FindIndex(
                    deviceCombo.Items,
                    item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
                return selectedIndex >= 0;
            },
            TimeSpan.FromSeconds(5));
        deviceCombo.Select(selectedIndex);
        E2EAssert.WaitUntil(
            () => deviceCombo.SelectedItem?.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(3));
    }

    private void SelectFixed25Fps()
    {
        ComboBox fpsCombo = Find("LtcFpsModeCombo").AsComboBox();
        fpsCombo.Select(2);
        E2EAssert.WaitUntil(
            () => fpsCombo.SelectedItem?.Name.Contains("25", StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(3));
    }

    private void StartLtcMonitor()
    {
        Button startButton = Find("BtnStartLtc").AsButton();
        E2EAssert.WaitUntil(() => startButton.IsEnabled, TimeSpan.FromSeconds(3));
        startButton.Invoke();
        E2EAssert.WaitUntil(() => !Find("LtcReceptionText").IsOffscreen, TimeSpan.FromSeconds(3));
    }

    private void RestoreLtcUiState()
    {
        if (_fixture.Skipped || _fixture.MainWindow is null)
            return;
        Button? stopButton = _fixture.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("BtnStopLtc"))?.AsButton();
        if (stopButton?.IsEnabled == true)
            stopButton.Invoke();
    }

    private AutomationElement Find(string automationId) =>
        _fixture.MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(automationId))
        ?? throw new InvalidOperationException($"{automationId} が見つかりません。");

    private bool IsHidden(string automationId) =>
        _fixture.MainWindow!.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is not { } element
        || element.IsOffscreen;

    private string ReadText(string automationId)
    {
        AutomationElement element = Find(automationId);
        return element.Patterns.Text.IsSupported
            ? element.Patterns.Text.Pattern.DocumentRange.GetText(-1).Trim()
            : element.Name.Trim();
    }

    private string ReadItemStatus(string automationId) =>
        Find(automationId).Properties.ItemStatus.ValueOrDefault?.Trim() ?? string.Empty;
}

/// <summary>
/// v0.6.6 R-13: LTC を流している間の UI の heartbeat の遅れ（メーターあり）。heartbeat は MainWindow の構築から
/// 30 秒だけ記録され、区間の終わりに Information の要約「UI heartbeat summary: firstLateMs=… maxLateMs=… ticks=…
/// elapsedMs=… reason=…」が 1 行出る。起動したらすぐ LTC を始め、その要約で判定する。
/// 試験基盤の束 1（2026-10）: 1 tick ごとの ui.heartbeat の行は Debug で、Release の構成では出ないので使わない。
/// 判定は 2 つ: (1) maxLateMs が起動の 1 回目（firstLateMs）と同じか、1000ms 未満。(2) reason=window かつ ticks が 200 以上。
/// 要約の maxLateMs はほぼ常に起動の 1 回目（中央 838ms、230 件中 8 件が 1000ms 以上）なので、受信中の窓に限った
/// 数字は取れない。Release の構成では、受信中の 1 秒未満の止まりは見ていない（ticks の床で見えるのは数秒の止まり）。
/// 環境変数 TCS_R13_REPORT_DIR を置くと、要約を heartbeat.txt に書く。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class LtcInputMeterHeartbeatE2ETests
{
    private static readonly Regex SummaryLine = new(
        @"UI heartbeat summary: firstLateMs=(-?[0-9.]+|NaN) maxLateMs=(-?[0-9.]+) ticks=(\d+) elapsedMs=([0-9.]+) reason=(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// 30 秒の区間の tick の床。100ms ごとなら 300 前後で、開発機の 2026-10 までのログでは reason=window の回が
    /// 248〜267（起動の 1 回目の遅れと、1 tick ごとの数 ms の遅れの分だけ減る）。床との差の 48 tick ≒ 5 秒を超える
    /// 止まりで落ちる。
    /// </summary>
    private const long MinTicksInWindow = 200;

    private readonly ITestOutputHelper _output;

    public LtcInputMeterHeartbeatE2ETests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void CableLoop_WhileLtcFlows_UiHeartbeatStaysOnTime()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(skipReason != null, skipReason);
        Skip.If(LtcSignalPlayer.FindCableCaptureDeviceName() is null,
            "有効な VB-CABLE 録音デバイス（CABLE Output）が見つかりません。");
        Skip.IfNot(LtcSignalPlayer.TryCreateCablePlayer(out LtcSignalPlayer? created, out string? reason),
            reason ?? "CABLE Input を利用できません。");
        using LtcSignalPlayer player = created!;

        string video = TestVideoFactory.GetOrCreate();
        string logDirectory = AppLogReader.LogDirectoryForExe(exePath);
        DateTime launchedLocal = DateTime.Now.AddSeconds(-1);
        player.Play(new LtcTimecode(1, 0, 0, 0, false), 25, TimeSpan.FromSeconds(45));
        using E2EAppRunner app = E2EAppRunner.Start(exePath, $"--vo null --open \"{video}\" --playlist \"{video}\"");
        try
        {
            Window window = app.MainWindow;
            AutomationElement Find(string id) =>
                window.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException($"{id} が見つかりません。");

            Find("BtnRefreshLtcDevices").AsButton().Invoke();
            ComboBox deviceCombo = Find("LtcDeviceCombo").AsComboBox();
            int index = -1;
            E2EAssert.WaitUntil(() =>
            {
                index = Array.FindIndex(deviceCombo.Items,
                    item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
                return index >= 0;
            }, TimeSpan.FromSeconds(5));
            deviceCombo.Select(index);
            ComboBox fpsCombo = Find("LtcFpsModeCombo").AsComboBox();
            fpsCombo.Select(2);
            Find("BtnStartLtc").AsButton().Invoke();
            DateTime ltcStartedLocal = DateTime.Now;

            E2EAssert.WaitUntil(
                () => Find("LtcReceptionText").Properties.ItemStatus.ValueOrDefault == "Receiving",
                TimeSpan.FromSeconds(5));
            DateTime receivingLocal = DateTime.Now;

            // 区間は MainWindow の構築から 30 秒（UiHeartbeatRecorder.Window）。起動の手前の 1 秒の余裕と、
            // 構築までの数秒を見て、起動から 45 秒まで要約の行を待つ。
            string summaryLine = string.Empty;
            E2EAssert.WaitUntil(() =>
            {
                summaryLine = AppLogReader.ReadLinesSince(logDirectory, launchedLocal)
                    .LastOrDefault(line => line.Contains("UI heartbeat summary:", StringComparison.Ordinal))
                    ?? string.Empty;
                return summaryLine.Length > 0;
            }, TimeSpan.FromSeconds(45));
            Find("LtcReceptionText").Properties.ItemStatus.ValueOrDefault.Should().Be("Receiving");

            Match match = SummaryLine.Match(summaryLine);
            match.Success.Should().BeTrue($"要約の行の形が想定と違う: {summaryLine}");
            string firstText = match.Groups[1].Value;
            double max = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            long ticks = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            string summaryReason = match.Groups[5].Value;
            bool maxIsFirstTick = firstText == match.Groups[2].Value;

            string summary = string.Create(CultureInfo.InvariantCulture,
                $"heartbeat summary (window from MainWindow construction): firstLateMs={firstText} maxLateMs={max:F1} " +
                $"ticks={ticks} reason={summaryReason} maxIsFirstTick={maxIsFirstTick} " +
                $"ltcStartToReceivingMs={(receivingLocal - ltcStartedLocal).TotalMilliseconds:F0}");
            _output.WriteLine(summary);
            string? reportDirectory = Environment.GetEnvironmentVariable("TCS_R13_REPORT_DIR");
            if (!string.IsNullOrWhiteSpace(reportDirectory))
            {
                Directory.CreateDirectory(reportDirectory);
                File.AppendAllText(Path.Combine(reportDirectory, "heartbeat.txt"),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + summary + Environment.NewLine);
            }

            summaryReason.Should().Be("window", "30 秒の区間を終わりまで回った");
            ticks.Should().BeGreaterThanOrEqualTo(MinTicksInWindow, "区間の中で UI が数秒止まっていない");
            (maxIsFirstTick || max < 1000.0).Should().BeTrue(
                $"起動の 1 回目より後の tick の遅れが 1000ms 未満（maxLateMs={max:F1} firstLateMs={firstText}）");
        }
        finally
        {
            player.Stop();
        }
    }
}

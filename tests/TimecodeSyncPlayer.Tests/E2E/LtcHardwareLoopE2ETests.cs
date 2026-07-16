using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class LtcHardwareLoopE2ETests : IClassFixture<TimecodeSyncPlayerFixture>
{
    private const int Fps = 25;
    private readonly TimecodeSyncPlayerFixture _fixture;

    public LtcHardwareLoopE2ETests(TimecodeSyncPlayerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public void CableLoop_TimecodeProgressesMonotonically_AndStopKeepsLastValueStable()
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            SelectFixed25Fps();

            signalPlayer.Play(
                new LtcTimecode(1, 0, 0, 0, false),
                Fps,
                TimeSpan.FromSeconds(20));
            StartLtcMonitor();

            IReadOnlyList<ObservedTimecode> observed = WaitForProgression(
                expectedHour: 1,
                timeout: TimeSpan.FromSeconds(8));

            observed.Should().OnlyContain(value =>
                value.Hours == 1 &&
                value.Minutes == 0 &&
                value.Seconds >= 0 && value.Seconds <= 9);

            StopLtcMonitor();

            long stableFrame = WaitForStableTimecode(
                stableFor: TimeSpan.FromSeconds(2),
                timeout: TimeSpan.FromSeconds(5));

            TryReadTimecode(out ObservedTimecode stoppedValue).Should().BeTrue();
            stoppedValue.TotalFrames.Should().Be(stableFrame);
            ReadText("LtcTimecodeText").Should().NotBe("--:--:--:--");
        }
        finally
        {
            signalPlayer.Stop();
            RestoreLtcUiState();
        }
    }

    [SkippableFact]
    public void CableLoop_WhenSignalIsLost_TimecodeStopsProgressing()
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            SelectFixed25Fps();

            signalPlayer.Play(
                new LtcTimecode(1, 0, 20, 0, false),
                Fps,
                TimeSpan.FromSeconds(10));
            StartLtcMonitor();
            WaitForProgression(expectedHour: 1, timeout: TimeSpan.FromSeconds(8));

            signalPlayer.Stop();

            long stableFrame = WaitForStableTimecode(
                stableFor: TimeSpan.FromSeconds(1),
                timeout: TimeSpan.FromSeconds(5));

            TryReadTimecode(out ObservedTimecode finalValue).Should().BeTrue();
            finalValue.TotalFrames.Should().Be(stableFrame);
            ReadText("LtcTimecodeText").Should().NotBe("--:--:--:--");
        }
        finally
        {
            signalPlayer.Stop();
            RestoreLtcUiState();
        }
    }

    [SkippableFact]
    public void CableLoop_SyncSeeksVideoNearLtcPosition()
    {
        SkipIfAppUnavailable();
        Skip.IfNot(TestVideoFactory.FfmpegAvailable(),
            "ffmpeg が見つからないため、LTC 同期シーク E2E をスキップします。");
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            SelectFixed25Fps();
            DisableSyncIfEnabled();
            SeekToStart();

            signalPlayer.Play(
                new LtcTimecode(0, 0, 10, 0, false),
                Fps,
                TimeSpan.FromSeconds(8));
            StartLtcMonitor();
            EnableSync();

            E2EAssert.WaitUntil(
                () => TryReadPlaybackPosition(out double seconds) && seconds >= 8 && seconds <= 19.5,
                TimeSpan.FromSeconds(8));

            TryReadPlaybackPosition(out double finalSeconds).Should().BeTrue();
            finalSeconds.Should().BeInRange(8, 19.5);
        }
        finally
        {
            signalPlayer.Stop();
            RestoreLtcUiState();
        }
    }

    [SkippableFact]
    public void CableLoop_NoisySignal_TimecodeProgresses()
    {
        AssertDegradedSignalProgresses(
            new LtcTimecode(2, 0, 0, 0, false),
            new LtcTestSignalGenerator.Options
            {
                NoiseAmplitude = 0.15,
                NoiseSeed = 4242,
            });
    }

    [SkippableFact]
    public void CableLoop_LowAmplitudeSignal_TimecodeProgresses()
    {
        AssertDegradedSignalProgresses(
            new LtcTimecode(3, 0, 0, 0, false),
            new LtcTestSignalGenerator.Options { Amplitude = 0.1f });
    }

    [SkippableFact]
    public void CableLoop_InvertedSignal_TimecodeProgresses()
    {
        AssertDegradedSignalProgresses(
            new LtcTimecode(4, 0, 0, 0, false),
            new LtcTestSignalGenerator.Options { Invert = true });
    }

    [SkippableFact]
    public void CableLoop_AfterSilence_TimecodeStopsThenRecoversProgression()
    {
        SkipIfAppUnavailable();
        SkipIfFfmpegUnavailableForDegradedSignalTest();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            SelectFixed25Fps();

            signalPlayer.PlayWithSilence(
                new LtcTimecode(5, 0, 0, 0, false),
                Fps,
                signalBefore: TimeSpan.FromSeconds(4),
                silence: TimeSpan.FromSeconds(1.5),
                signalAfter: TimeSpan.FromSeconds(6));
            StartLtcMonitor();

            WaitForProgression(expectedHour: 5, timeout: TimeSpan.FromSeconds(5));
            long frameBeforeRecovery = WaitForStableTimecode(
                stableFor: TimeSpan.FromSeconds(1),
                timeout: TimeSpan.FromSeconds(6));

            IReadOnlyList<ObservedTimecode> recovered = WaitForProgression(
                expectedHour: 5,
                timeout: TimeSpan.FromSeconds(8));

            recovered[^1].TotalFrames.Should().BeGreaterThan(
                frameBeforeRecovery,
                "無音区間後にLTC表示の進行が再開する必要があるため");
        }
        finally
        {
            signalPlayer.Stop();
            RestoreLtcUiState();
        }
    }

    [SkippableFact]
    public void CableLoop_NoisyLowAmplitudeSignal_TimecodeProgresses()
    {
        AssertDegradedSignalProgresses(
            new LtcTimecode(6, 0, 0, 0, false),
            new LtcTestSignalGenerator.Options
            {
                Amplitude = 0.1f,
                NoiseAmplitude = 0.015,
                NoiseSeed = 4242,
            });
    }

    [SkippableFact]
    public void CableLoop_StopMode_WhenSignalIsLost_PausesPlaybackOnLastFrame()
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            PrepareSignalLossPlaybackTest(modeIndex: 1, expectedModeText: "停止");
            signalPlayer.Play(
                new LtcTimecode(0, 0, 0, 0, false),
                Fps,
                TimeSpan.FromSeconds(15));
            StartLtcMonitor();
            WaitForProgressionAfterSignalRestart(
                expectedHour: 0,
                initialWindow: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromSeconds(8));
            EnableSync();
            EnsurePlaybackRunning();
            WaitForPlaybackProgression(timeout: TimeSpan.FromSeconds(5));

            signalPlayer.Stop();

            double stablePosition = WaitForStablePlaybackPosition(
                stableFor: TimeSpan.FromSeconds(1),
                timeout: TimeSpan.FromSeconds(5));
            ReadButtonName("BtnPlay").Should().Be("▶");
            TryReadPlaybackPosition(out double finalPosition).Should().BeTrue();
            finalPosition.Should().BeApproximately(stablePosition, 0.05);
        }
        finally
        {
            signalPlayer.Stop();
            RestoreSignalLossTestState();
        }
    }

    [SkippableFact]
    public void CableLoop_StopMode_WhenSignalReturns_ResumesAndSyncsPlayback()
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            PrepareSignalLossPlaybackTest(modeIndex: 1, expectedModeText: "停止");
            signalPlayer.PlayWithSilence(
                new LtcTimecode(0, 0, 0, 0, false),
                Fps,
                signalBefore: TimeSpan.FromSeconds(7),
                silence: TimeSpan.FromSeconds(1.5),
                signalAfter: TimeSpan.FromSeconds(10));
            StartLtcMonitor();
            WaitForProgressionAfterSignalRestart(
                expectedHour: 0,
                initialWindow: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromSeconds(8));
            EnableSync();
            EnsurePlaybackRunning();
            E2EAssert.WaitUntil(
                () => ReadButtonName("BtnPlay") == "▶",
                TimeSpan.FromSeconds(8));
            TryReadPlaybackPosition(out double stoppedPosition).Should().BeTrue();

            E2EAssert.WaitUntil(
                () => ReadButtonName("BtnPlay") == "⏸",
                TimeSpan.FromSeconds(5));
            E2EAssert.WaitUntil(
                () => TryReadPlaybackPosition(out double playbackSeconds) &&
                      playbackSeconds > stoppedPosition + 0.5,
                TimeSpan.FromSeconds(5));
            double lastPlaybackSeconds = double.NaN;
            double lastLtcSeconds = double.NaN;
            double lastAlignedPlaybackSeconds = double.NaN;
            try
            {
                E2EAssert.WaitUntil(
                    () =>
                    {
                        if (!TryReadPlaybackPosition(out lastPlaybackSeconds))
                        {
                            return false;
                        }

                        long playbackObservedAt = Stopwatch.GetTimestamp();
                        if (!TryReadTimecode(out ObservedTimecode timecode))
                        {
                            return false;
                        }

                        long ltcObservedAt = Stopwatch.GetTimestamp();
                        lastLtcSeconds = timecode.TotalFrames / (double)Fps;
                        lastAlignedPlaybackSeconds = lastPlaybackSeconds +
                            Stopwatch.GetElapsedTime(playbackObservedAt, ltcObservedAt).TotalSeconds;
                        return Math.Abs(lastAlignedPlaybackSeconds - lastLtcSeconds) <= 2.0;
                    },
                    TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"Playback did not sync to recovered LTC. playback={lastPlaybackSeconds:F3}, " +
                    $"alignedPlayback={lastAlignedPlaybackSeconds:F3}, ltc={lastLtcSeconds:F3}",
                    ex);
            }
        }
        finally
        {
            signalPlayer.Stop();
            RestoreSignalLossTestState();
        }
    }

    [SkippableFact]
    public void CableLoop_RunThroughMode_WhenSignalIsLost_KeepsPlaybackRunning()
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            PrepareSignalLossPlaybackTest(modeIndex: 0, expectedModeText: "ランスルー");
            signalPlayer.Play(
                new LtcTimecode(0, 0, 0, 0, false),
                Fps,
                TimeSpan.FromSeconds(15));
            StartLtcMonitor();
            WaitForProgressionAfterSignalRestart(
                expectedHour: 0,
                initialWindow: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromSeconds(8));
            EnableSync();
            EnsurePlaybackRunning();
            WaitForPlaybackProgression(timeout: TimeSpan.FromSeconds(5));
            TryReadPlaybackPosition(out double positionBeforeLoss).Should().BeTrue();

            signalPlayer.Stop();

            E2EAssert.WaitUntil(
                () => TryReadPlaybackPosition(out double current) &&
                      current > positionBeforeLoss + 1.0,
                TimeSpan.FromSeconds(4));
            ReadButtonName("BtnPlay").Should().Be("⏸");
        }
        finally
        {
            signalPlayer.Stop();
            RestoreSignalLossTestState();
        }
    }

    [Fact]
    public void CableLoop_ContinueBlackGap_WhenSwitchingToSingle_RestoresVideoStateImmediately()
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            PrepareCombinationTest(
                signalLossModeIndex: 0,
                signalLossModeText: "ランスルー",
                syncModeIndex: 1,
                syncModeText: "Continue",
                gapBehaviorIndex: 0,
                gapBehaviorText: "Black");
            signalPlayer.Play(
                new LtcTimecode(0, 0, 34, 0, false),
                Fps,
                TimeSpan.FromSeconds(15));
            StartLtcMonitor();
            WaitForProgressionAfterSignalRestart(
                expectedHour: 0,
                initialWindow: TimeSpan.FromSeconds(36),
                timeout: TimeSpan.FromSeconds(8));
            EnableSync();

            E2EAssert.WaitUntil(
                () => TryReadPlaybackPosition(out double seconds) && seconds is >= 13 and < 20,
                TimeSpan.FromSeconds(5));
            E2EAssert.WaitUntil(
                () => ReadText("CurrentTrackLabel").Contains("Gap: Black", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));

            SelectSyncMode(index: 0, expectedText: "Single");

            E2EAssert.WaitUntil(
                () => !ReadText("CurrentTrackLabel").Contains("Gap:", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            ReadText("CurrentTrackLabel").Should().NotContain("Gap:");
            E2EAssert.WaitUntil(
                () => TryReadPlaybackPosition(out double seconds) && seconds >= 19,
                TimeSpan.FromSeconds(3));
        }
        finally
        {
            signalPlayer.Stop();
            RestoreCombinationTestState();
        }
    }

    [Fact]
    public void CableLoop_StopMode_SignalLossDuringGap_IsEvaluatedAfterGapRecovery()
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            PrepareCombinationTest(
                signalLossModeIndex: 1,
                signalLossModeText: "停止",
                syncModeIndex: 1,
                syncModeText: "Continue",
                gapBehaviorIndex: 0,
                gapBehaviorText: "Black");
            signalPlayer.Play(
                new LtcTimecode(0, 0, 36, 0, false),
                Fps,
                TimeSpan.FromSeconds(12));
            StartLtcMonitor();
            WaitForProgressionAfterSignalRestart(
                expectedHour: 0,
                initialWindow: TimeSpan.FromSeconds(38),
                timeout: TimeSpan.FromSeconds(8));
            EnableSync();
            E2EAssert.WaitUntil(
                () => ReadText("CurrentTrackLabel").Contains("Gap: Black", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));

            signalPlayer.Stop();
            WaitForStableTimecode(
                stableFor: TimeSpan.FromSeconds(1),
                timeout: TimeSpan.FromSeconds(5));
            ReadText("CurrentTrackLabel").Should().Contain("Gap: Black");

            SelectSyncMode(index: 0, expectedText: "Single");
            E2EAssert.WaitUntil(
                () => !ReadText("CurrentTrackLabel").Contains("Gap:", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            signalPlayer.Play(
                new LtcTimecode(0, 0, 5, 0, false),
                Fps,
                TimeSpan.FromSeconds(12));
            WaitForProgressionAfterSignalRestart(
                expectedHour: 0,
                initialWindow: TimeSpan.FromSeconds(10),
                timeout: TimeSpan.FromSeconds(8));
            EnsurePlaybackRunning();
            WaitForPlaybackProgression(timeout: TimeSpan.FromSeconds(5));

            signalPlayer.Stop();

            WaitForStablePlaybackPosition(
                stableFor: TimeSpan.FromSeconds(1),
                timeout: TimeSpan.FromSeconds(5));
            ReadButtonName("BtnPlay").Should().Be("▶");
        }
        finally
        {
            signalPlayer.Stop();
            RestoreCombinationTestState();
        }
    }

    [Fact]
    public void CableLoop_WhileSignalRuns_RepeatedModeSwitchesRecoverSyncEveryTime()
    {
        SkipIfAppUnavailable();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            PrepareCombinationTest(
                signalLossModeIndex: 0,
                signalLossModeText: "ランスルー",
                syncModeIndex: 0,
                syncModeText: "Single",
                gapBehaviorIndex: 0,
                gapBehaviorText: "Black");
            signalPlayer.Play(
                new LtcTimecode(0, 0, 0, 0, false),
                Fps,
                TimeSpan.FromSeconds(20));
            StartLtcMonitor();
            WaitForProgressionAfterSignalRestart(
                expectedHour: 0,
                initialWindow: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromSeconds(8));
            EnableSync();

            foreach ((int index, string text) in new[]
                     {
                         (1, "Continue"),
                         (0, "Single"),
                         (1, "Continue"),
                         (0, "Single"),
                     })
            {
                TryReadTimecode(out ObservedTimecode beforeSwitch).Should().BeTrue();
                SelectSyncMode(index, text);
                WaitForTimecodeAfter(beforeSwitch.TotalFrames, TimeSpan.FromSeconds(3));
                WaitForPlaybackAlignedToLtc(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            signalPlayer.Stop();
            RestoreCombinationTestState();
        }
    }

    private void PrepareCombinationTest(
        int signalLossModeIndex,
        string signalLossModeText,
        int syncModeIndex,
        string syncModeText,
        int gapBehaviorIndex,
        string gapBehaviorText)
    {
        SelectFixed25Fps();
        SelectSignalLossMode(signalLossModeIndex, signalLossModeText);
        DisableSyncIfEnabled();
        SelectSyncMode(syncModeIndex, syncModeText);
        if (syncModeIndex == 1)
            SelectGapBehavior(gapBehaviorIndex, gapBehaviorText);
        SeekToStart();
    }

    private void SelectSyncMode(int index, string expectedText)
    {
        ComboBox combo = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("SyncModeCombo"))!.AsComboBox();
        combo.Select(index);
        E2EAssert.WaitUntil(
            () => combo.SelectedItem?.Name.Contains(expectedText, StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(3));
    }

    private void SelectGapBehavior(int index, string expectedText)
    {
        ComboBox combo = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("GapBehaviorCombo"))!.AsComboBox();
        E2EAssert.WaitUntil(() => combo.IsEnabled, TimeSpan.FromSeconds(3));
        combo.Select(index);
        E2EAssert.WaitUntil(
            () => combo.SelectedItem?.Name.Contains(expectedText, StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(3));
    }

    private void WaitForTimecodeAfter(long previousFrame, TimeSpan timeout) =>
        E2EAssert.WaitUntil(
            () => TryReadTimecode(out ObservedTimecode current) &&
                  current.TotalFrames > previousFrame,
            timeout);

    private void WaitForPlaybackAlignedToLtc(TimeSpan timeout)
    {
        double lastPlaybackSeconds = double.NaN;
        double lastLtcSeconds = double.NaN;
        E2EAssert.WaitUntil(
            () =>
            {
                if (!TryReadPlaybackPosition(out lastPlaybackSeconds) ||
                    !TryReadTimecode(out ObservedTimecode timecode))
                {
                    return false;
                }

                lastLtcSeconds = timecode.TotalFrames / (double)Fps;
                return Math.Abs(lastPlaybackSeconds - lastLtcSeconds) <= 2.0;
            },
            timeout);
    }

    private void RestoreCombinationTestState()
    {
        RestoreSignalLossTestState();
        SelectSyncMode(index: 0, expectedText: "Single");
    }

    private void PrepareSignalLossPlaybackTest(int modeIndex, string expectedModeText)
    {
        SelectFixed25Fps();
        SelectSignalLossMode(modeIndex, expectedModeText);
        DisableSyncIfEnabled();
        SeekToStart();
    }

    private void SelectSignalLossMode(int index, string expectedText)
    {
        ComboBox modeCombo = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("LtcSignalLossModeCombo"))!.AsComboBox();
        modeCombo.Select(index);
        E2EAssert.WaitUntil(
            () => modeCombo.SelectedItem?.Name.Contains(
                expectedText,
                StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(3));
    }

    private void EnsurePlaybackRunning()
    {
        Button playButton = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("BtnPlay"))!.AsButton();
        if (playButton.Name == "▶")
            playButton.Invoke();

        E2EAssert.WaitUntil(
            () => playButton.Name == "⏸",
            TimeSpan.FromSeconds(3));
    }

    private IReadOnlyList<double> WaitForPlaybackProgression(TimeSpan timeout)
    {
        var observed = new List<double>();
        E2EAssert.WaitUntil(
            () =>
            {
                if (!TryReadPlaybackPosition(out double current))
                    return false;

                if (observed.Count == 0 || current > observed[^1] + 0.01)
                    observed.Add(current);

                return observed.Count >= 3;
            },
            timeout);

        return observed;
    }

    private double WaitForStablePlaybackPosition(TimeSpan stableFor, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        double? lastPosition = null;
        TimeSpan lastChange = stopwatch.Elapsed;

        E2EAssert.WaitUntil(
            () =>
            {
                if (!TryReadPlaybackPosition(out double current))
                    return false;

                if (!lastPosition.HasValue || Math.Abs(current - lastPosition.Value) > 0.01)
                {
                    lastPosition = current;
                    lastChange = stopwatch.Elapsed;
                    return false;
                }

                return stopwatch.Elapsed - lastChange >= stableFor;
            },
            timeout);

        return lastPosition!.Value;
    }

    private string ReadButtonName(string automationId) =>
        _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId(automationId))!.AsButton().Name;

    private void RestoreSignalLossTestState()
    {
        SelectSignalLossMode(index: 0, expectedText: "ランスルー");
        RestoreLtcUiState();

        Button playButton = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("BtnPlay"))!.AsButton();
        if (playButton.Name == "⏸")
            playButton.Invoke();
    }

    private void AssertDegradedSignalProgresses(
        LtcTimecode start,
        LtcTestSignalGenerator.Options options)
    {
        SkipIfAppUnavailable();
        SkipIfFfmpegUnavailableForDegradedSignalTest();
        SelectCableCaptureDevice();
        using LtcSignalPlayer signalPlayer = CreateCablePlayer();

        try
        {
            SelectFixed25Fps();

            signalPlayer.Play(start, Fps, TimeSpan.FromSeconds(15), options);
            StartLtcMonitor();

            IReadOnlyList<ObservedTimecode> observed = WaitForProgression(
                expectedHour: start.Hours,
                timeout: TimeSpan.FromSeconds(12));

            observed.Should().OnlyContain(value => value.Hours == start.Hours);
        }
        finally
        {
            signalPlayer.Stop();
            RestoreLtcUiState();
        }
    }

    private static void SkipIfFfmpegUnavailableForDegradedSignalTest()
    {
        Skip.IfNot(
            TestVideoFactory.FfmpegAvailable(),
            "ffmpeg が見つからないため、劣化LTC実機ループ E2E をスキップします。");
    }

    private void SkipIfAppUnavailable()
    {
        if (_fixture.Skipped)
        {
            throw new SkipException(_fixture.SkipReason ?? "E2E アプリを起動できませんでした。");
        }
    }

    private static LtcSignalPlayer CreateCablePlayer()
    {
        bool available = LtcSignalPlayer.TryCreateCablePlayer(
            out LtcSignalPlayer? player,
            out string? skipReason);
        Skip.If(!available, skipReason ?? "CABLE Input を利用できません。");

        return player!;
    }

    private void SelectCableCaptureDevice()
    {
        string? captureDeviceName = LtcSignalPlayer.FindCableCaptureDeviceName();
        Skip.If(captureDeviceName is null,
            "有効な VB-CABLE 録音デバイス（CABLE Output）が見つかりません。");

        Button refreshButton = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("BtnRefreshLtcDevices"))!.AsButton();
        refreshButton.Invoke();

        ComboBox deviceCombo = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByAutomationId("LtcDeviceCombo"))!.AsComboBox();
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
            () => deviceCombo.SelectedItem?.Name.Contains(
                "CABLE Output",
                StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(3));
    }

    private void SelectFixed25Fps()
    {
        ComboBox fpsCombo = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("LtcFpsModeCombo"))!.AsComboBox();
        fpsCombo.Select(2);
        E2EAssert.WaitUntil(
            () => fpsCombo.SelectedItem?.Name.Contains(
                "25",
                StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(3));
    }

    private void StartLtcMonitor()
    {
        Button startButton = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("BtnStartLtc"))!.AsButton();
        E2EAssert.WaitUntil(
            () => startButton.IsEnabled,
            TimeSpan.FromSeconds(3));
        startButton.Invoke();
    }

    private void StopLtcMonitor()
    {
        Button stopButton = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("BtnStopLtc"))!.AsButton();
        E2EAssert.WaitUntil(
            () => stopButton.IsEnabled,
            TimeSpan.FromSeconds(3));
        stopButton.Invoke();
    }

    private IReadOnlyList<ObservedTimecode> WaitForProgression(int expectedHour, TimeSpan timeout)
    {
        var observed = new List<ObservedTimecode>();
        bool regressed = false;

        E2EAssert.WaitUntil(
            () =>
            {
                if (!TryReadTimecode(out ObservedTimecode current) || current.Hours != expectedHour)
                {
                    return false;
                }

                if (observed.Count == 0)
                {
                    observed.Add(current);
                }
                else if (current.TotalFrames < observed[^1].TotalFrames)
                {
                    regressed = true;
                }
                else if (current.TotalFrames > observed[^1].TotalFrames)
                {
                    observed.Add(current);
                }

                return regressed || observed.Count >= 3;
            },
            timeout);

        regressed.Should().BeFalse("LTC 表示は巻き戻らない必要があるため");
        observed.Should().HaveCountGreaterThanOrEqualTo(3);
        return observed;
    }

    private void WaitForProgressionAfterSignalRestart(
        int expectedHour,
        TimeSpan initialWindow,
        TimeSpan timeout)
    {
        long initialWindowFrames = (long)(initialWindow.TotalSeconds * Fps);
        var observed = new List<ObservedTimecode>();
        bool acquiredRestartedSignal = false;

        E2EAssert.WaitUntil(
            () =>
            {
                if (!TryReadTimecode(out ObservedTimecode current) || current.Hours != expectedHour)
                {
                    return false;
                }

                if (!acquiredRestartedSignal)
                {
                    if (current.TotalFrames > initialWindowFrames)
                    {
                        return false;
                    }

                    acquiredRestartedSignal = true;
                    observed.Add(current);
                    return false;
                }

                if (current.TotalFrames > observed[^1].TotalFrames)
                {
                    observed.Add(current);
                }

                return observed.Count >= 3;
            },
            timeout);
    }

    private long WaitForStableTimecode(TimeSpan stableFor, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        long? lastFrame = null;
        TimeSpan lastChange = stopwatch.Elapsed;

        E2EAssert.WaitUntil(
            () =>
            {
                if (!TryReadTimecode(out ObservedTimecode current))
                {
                    return false;
                }

                if (lastFrame != current.TotalFrames)
                {
                    lastFrame = current.TotalFrames;
                    lastChange = stopwatch.Elapsed;
                    return false;
                }

                return stopwatch.Elapsed - lastChange >= stableFor;
            },
            timeout);

        return lastFrame!.Value;
    }

    private bool TryReadTimecode(out ObservedTimecode value)
    {
        string[] parts = ReadText("LtcTimecodeText").Split(':');
        if (parts.Length == 4 &&
            int.TryParse(parts[0], out int hours) &&
            int.TryParse(parts[1], out int minutes) &&
            int.TryParse(parts[2], out int seconds) &&
            int.TryParse(parts[3], out int frames))
        {
            value = new ObservedTimecode(hours, minutes, seconds, frames, Fps);
            return true;
        }

        value = default;
        return false;
    }

    private bool TryReadPlaybackPosition(out double seconds)
    {
        string current = ReadText("TimeLabel").Split('/')[0].Trim();
        string[] parts = current.Split(':');
        seconds = 0;

        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[0], out int hours) ||
                !int.TryParse(parts[1], out int minutes) ||
                !int.TryParse(parts[2], out int wholeSeconds) ||
                !int.TryParse(parts[3], out int frames))
            {
                return false;
            }

            // LTC 用 Fps を流用しているため、動画 fps と偶然一致する場合のみ
            // フレーム端数まで厳密な秒換算になる。このテストでは広い秒範囲だけを検証する。
            seconds = hours * 3600 + minutes * 60 + wholeSeconds + frames / (double)Fps;
            return true;
        }

        for (int index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], out double component))
            {
                return false;
            }

            seconds = seconds * 60 + component;
        }

        return parts.Length is 2 or 3;
    }

    private void SeekToStart()
    {
        AutomationElement seekBar = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("SeekBar"))!;
        IRangeValuePattern rangeValue = seekBar.Patterns.RangeValue.Pattern;
        if (!rangeValue.IsReadOnly)
        {
            rangeValue.SetValue(rangeValue.Minimum);
        }

        E2EAssert.WaitUntil(
            () => TryReadPlaybackPosition(out double seconds) && seconds <= 1,
            TimeSpan.FromSeconds(3));
    }

    private void EnableSync()
    {
        Button syncButton = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId("BtnToggleSync"))!.AsButton();
        if (!syncButton.Name.Contains("ON", StringComparison.OrdinalIgnoreCase))
        {
            syncButton.Invoke();
        }
    }

    private void DisableSyncIfEnabled()
    {
        Button? syncButton = _fixture.MainWindow?.FindFirstDescendant(
            cf => cf.ByAutomationId("BtnToggleSync"))?.AsButton();
        if (syncButton?.Name.Contains("ON", StringComparison.OrdinalIgnoreCase) == true)
        {
            syncButton.Invoke();
        }
    }

    private void RestoreLtcUiState()
    {
        if (_fixture.Skipped || _fixture.MainWindow is null)
        {
            return;
        }

        DisableSyncIfEnabled();
        Button? stopButton = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByAutomationId("BtnStopLtc"))?.AsButton();
        if (stopButton?.IsEnabled == true)
        {
            stopButton.Invoke();
        }
    }

    private string ReadText(string automationId)
    {
        AutomationElement element = _fixture.MainWindow!.FindFirstDescendant(
            cf => cf.ByAutomationId(automationId))!;
        if (element.Patterns.Text.IsSupported)
        {
            return element.Patterns.Text.Pattern.DocumentRange.GetText(-1).Trim();
        }

        return element.Name.Trim();
    }

    private readonly record struct ObservedTimecode(
        int Hours,
        int Minutes,
        int Seconds,
        int Frames,
        int FramesPerSecond)
    {
        public long TotalFrames =>
            (((Hours * 60L + Minutes) * 60L + Seconds) * FramesPerSecond) + Frames;
    }
}

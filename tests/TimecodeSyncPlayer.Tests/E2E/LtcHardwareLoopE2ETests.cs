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

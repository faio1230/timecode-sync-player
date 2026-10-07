using FluentAssertions;
using NAudio.Wave;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.6 R-13: LTC の入力のメーターと受信の表示（間引き・3 段の判定・UI へ渡す値）。</summary>
public class LtcInputLevelTests
{
    // 1 秒 = 1000 刻み（1 刻み = 1ms）にして時刻を読みやすくする。
    private const long TicksPerSecond = 1000;

    // ── 間引き ───────────────────────────────────────────────────

    [Fact]
    public void Meter_ManyFineCallbacks_ReportOncePer100ms()
    {
        var meter = new LtcInputLevelMeter(TicksPerSecond);
        var reports = new List<(long At, LtcInputLevel Level)>();

        // 1ms ごとに 1 秒分（1001 回）。最初の呼び出しが起点で、100ms ごとに 1 回だけ返す。
        for (long t = 0; t <= 1000; t++)
        {
            LtcInputLevel? level = meter.Observe(t, 0.5f, 0);
            if (level != null)
                reports.Add((t, level));
        }

        reports.Select(r => r.At).Should().Equal(100, 200, 300, 400, 500, 600, 700, 800, 900, 1000);
    }

    [Fact]
    public void Meter_CallbackJustBefore100ms_DoesNotReport()
    {
        var meter = new LtcInputLevelMeter(TicksPerSecond);

        meter.Observe(0, 0.1f, 0).Should().BeNull("最初の呼び出しは起点");
        meter.Observe(99, 0.1f, 0).Should().BeNull();
        meter.Observe(100, 0.1f, 0).Should().NotBeNull();
        meter.Observe(150, 0.1f, 0).Should().BeNull("次は前回の知らせから 100ms 後");
        meter.Observe(210, 0.1f, 0).Should().NotBeNull("コールバックの間が空いても次の 1 回で知らせる");
    }

    [Fact]
    public void Meter_PeakIsMaxSinceLastReport_AndResetsAfterReport()
    {
        var meter = new LtcInputLevelMeter(TicksPerSecond);
        meter.Observe(0, 0.1f, 0);
        meter.Observe(30, 1.0f, 0);
        LtcInputLevel first = meter.Observe(100, 0.01f, 0)!;
        first.PeakDbfs.Should().BeApproximately(0.0, 1e-9, "前回からの最大（1.0 = 0 dBFS）");

        LtcInputLevel second = meter.Observe(200, 0.01f, 0)!;
        second.PeakDbfs.Should().BeApproximately(-40.0, 1e-6, "知らせの後はピークを測り直す");
    }

    [Fact]
    public void Meter_NonFinitePeak_IsIgnored()
    {
        var meter = new LtcInputLevelMeter(TicksPerSecond);
        meter.Observe(0, float.NaN, 0);
        LtcInputLevel level = meter.Observe(100, float.PositiveInfinity, 0)!;

        level.PeakDbfs.Should().Be(LtcInputLevelMeter.FloorDbfs);
    }

    [Fact]
    public void Meter_CountsFramesInLastSecondOnly()
    {
        var meter = new LtcInputLevelMeter(TicksPerSecond);
        // 0〜960ms に 40ms ごとに 1 枚（25 枚）。知らせは 120・240…960ms に出る。
        for (long t = 0; t < 1000; t += 40)
            meter.Observe(t, 0.5f, 1);
        // 1060ms の時点の直近 1 秒（60ms より後）は 80〜960ms の 23 枚。
        meter.Observe(1060, 0.5f, 0)!.DecodedFramesLastSecond.Should().Be(23);

        // 信号が途切れて 1 秒経つと 0 に戻る（960ms の 1 枚は窓の端ちょうどなので外す）。
        meter.Observe(1960, 0f, 0)!.DecodedFramesLastSecond.Should().Be(0);
    }

    [Theory]
    [InlineData(1899, 1)] // 900ms の 1 枚は、999ms 後はまだ直近 1 秒の中
    [InlineData(1900, 0)] // ちょうど 1 秒後は外す
    public void Meter_FrameWindowBoundary(long reportAt, int expectedFrames)
    {
        var meter = new LtcInputLevelMeter(TicksPerSecond);
        meter.Observe(0, 0.5f, 0);
        meter.Observe(900, 0.5f, 1)!.DecodedFramesLastSecond.Should().Be(1);

        meter.Observe(reportAt, 0.5f, 0)!.DecodedFramesLastSecond.Should().Be(expectedFrames);
    }

    [Fact]
    public void Meter_ManyFramesInOneCallback_AreAllCounted()
    {
        var meter = new LtcInputLevelMeter(TicksPerSecond);
        meter.Observe(0, 0.5f, 3);
        meter.Observe(100, 0.5f, 2)!.DecodedFramesLastSecond.Should().Be(5);
    }

    [Theory]
    [InlineData(1.0f, 0.0)]
    [InlineData(0.1f, -20.0)]
    [InlineData(0.001f, -60.0)]
    [InlineData(0f, LtcInputLevelMeter.FloorDbfs)]
    [InlineData(-1f, LtcInputLevelMeter.FloorDbfs)]
    [InlineData(1e-9f, LtcInputLevelMeter.FloorDbfs)]
    public void ToDbfs_ConvertsLinearPeak(float peak, double expectedDbfs)
    {
        LtcInputLevelMeter.ToDbfs(peak).Should().BeApproximately(expectedDbfs, 1e-4);
    }

    [Fact]
    public void Meter_DecodedFramesFromRealProcessor_AreCountedAsDecoderOutputs()
    {
        // デコーダの今の出力（Process の Frames）を数えるだけ。25fps の LTC を 2 秒流すと直近 1 秒は 24〜26 枚。
        const int sampleRate = 48_000;
        const double fps = 25.0;
        LtcTimecode[] timecodes = Enumerable.Range(0, 50)
            .Select(i => new LtcTimecode(1, 0, i / 25, i % 25, false))
            .ToArray();
        float[] samples = LtcTestSignalGenerator.Generate(timecodes, fps, sampleRate);
        var processor = new LtcAudioSampleProcessor(new LtcDecoder(sampleRate, fps));
        var meter = new LtcInputLevelMeter(sampleRate); // サンプル数を時刻の刻みにする
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        const int chunk = 480; // 10ms
        int decodedTotal = 0;
        LtcInputLevel? last = null;
        for (int offset = 0; offset + chunk <= samples.Length; offset += chunk)
        {
            byte[] bytes = new byte[chunk * sizeof(float)];
            Buffer.BlockCopy(samples, offset * sizeof(float), bytes, 0, bytes.Length);
            LtcAudioSampleProcessingResult result = processor.Process(bytes, bytes.Length, format);
            decodedTotal += result.Frames.Count;
            last = meter.Observe(offset + chunk, result.Peak, result.Frames.Count) ?? last;
        }

        decodedTotal.Should().BeGreaterThan(40);
        last!.DecodedFramesLastSecond.Should().BeInRange(24, 26);
        last.PeakDbfs.Should().BeApproximately(0.0, 0.5);
        LtcReceptionPolicy.Classify(last, fps).Should().Be(LtcReceptionState.Receiving);
    }

    // ── 3 段の判定 ──────────────────────────────────────────────

    [Theory]
    [InlineData(-60.0001, 0, "Silent")]
    [InlineData(-60.0, 0, "SignalWithoutLtc")]
    [InlineData(-59.9999, 0, "SignalWithoutLtc")]
    [InlineData(-120.0, 0, "Silent")]
    [InlineData(-6.0, 0, "SignalWithoutLtc")]
    [InlineData(-6.0, 25, "Receiving")]
    public void Classify_SilenceThresholdBoundary(double peakDbfs, int frames, string expected)
    {
        LtcReceptionPolicy.Classify(new LtcInputLevel(peakDbfs, frames), 25.0).ToString().Should().Be(expected);
    }

    [Theory]
    // 24fps: 半分 = 12。12 は受信中、11 は受信中にしない。
    [InlineData(24.0, 12, "Receiving")]
    [InlineData(24.0, 11, "SignalWithoutLtc")]
    // 25fps: 半分 = 12.5。13 は受信中、12 は受信中にしない。
    [InlineData(25.0, 13, "Receiving")]
    [InlineData(25.0, 12, "SignalWithoutLtc")]
    // 29.97fps: 半分 = 14.985。15 は受信中、14 は受信中にしない。
    [InlineData(30000.0 / 1001.0, 15, "Receiving")]
    [InlineData(30000.0 / 1001.0, 14, "SignalWithoutLtc")]
    // 30fps: 半分 = 15。
    [InlineData(30.0, 15, "Receiving")]
    [InlineData(30.0, 14, "SignalWithoutLtc")]
    public void Classify_HalfFpsBoundary(double fps, int frames, string expected)
    {
        LtcReceptionPolicy.Classify(new LtcInputLevel(-10.0, frames), fps).ToString().Should().Be(expected);
    }

    [Fact]
    public void Classify_FramesWinOverLowPeak()
    {
        // フレームが読めていれば、ピークが低くても受信中（デコードできた事実を優先する）。
        LtcReceptionPolicy.Classify(new LtcInputLevel(-70.0, 25), 25.0).Should().Be(LtcReceptionState.Receiving);
        // 読めたフレームが半分に届かなければ、ピークで無音と音ありを分ける。
        LtcReceptionPolicy.Classify(new LtcInputLevel(-70.0, 5), 25.0).Should().Be(LtcReceptionState.Silent);
    }

    [Theory]
    [InlineData(TimecodeFpsMode.Fixed24, 0.0, 24.0)]
    [InlineData(TimecodeFpsMode.Fixed25, 0.0, 25.0)]
    [InlineData(TimecodeFpsMode.Fixed25, 30.0, 25.0)] // 固定は選んだ値（同期の側の値は見ない）
    [InlineData(TimecodeFpsMode.Fixed29_97, 0.0, 30000.0 / 1001.0)]
    [InlineData(TimecodeFpsMode.Fixed30, 0.0, 30.0)]
    [InlineData(TimecodeFpsMode.Auto, 0.0, 24.0)]   // Auto で未確定は 24
    [InlineData(TimecodeFpsMode.Auto, -1.0, 24.0)]
    [InlineData(TimecodeFpsMode.Auto, double.NaN, 24.0)]
    [InlineData(TimecodeFpsMode.Auto, 30.0, 30.0)]  // Auto で確定したらその値
    [InlineData(TimecodeFpsMode.Auto, 25.0, 25.0)]
    public void ReceptionFps_UsesSelectedFpsOr24WhenAutoUnresolved(
        TimecodeFpsMode mode, double resolvedFps, double expected)
    {
        LtcReceptionPolicy.ReceptionFps(mode, resolvedFps).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Classify_AutoUnresolved_Uses24_So12FramesIsReceiving()
    {
        double fps = LtcReceptionPolicy.ReceptionFps(TimecodeFpsMode.Auto, 0.0);
        LtcReceptionPolicy.Classify(new LtcInputLevel(-10, 12), fps).Should().Be(LtcReceptionState.Receiving);
        LtcReceptionPolicy.Classify(new LtcInputLevel(-10, 11), fps).Should().Be(LtcReceptionState.SignalWithoutLtc);
    }

    // ── 表示の値 ────────────────────────────────────────────────

    [Theory]
    [InlineData(-120.0, 0.0)]
    [InlineData(-60.0, 0.0)]
    [InlineData(-20.0, 66.6667)]
    [InlineData(0.0, 100.0)]
    [InlineData(3.0, 100.0)]
    [InlineData(double.NaN, 0.0)]
    public void MeterPercent_MapsMinus60To0dBfsLinearly(double peakDbfs, double expected)
    {
        LtcReceptionPolicy.MeterPercent(peakDbfs).Should().BeApproximately(expected, 1e-3);
    }

    [Theory]
    [InlineData(-12.0, 25, "Receiving", "LTC 受信中", "#56D364", 80.0)]
    [InlineData(-30.0, 0, "SignalWithoutLtc", "信号あり・LTC なし", "#D7A24B", 50.0)]
    [InlineData(-90.0, 0, "Silent", "無音", "#888888", 0.0)]
    public void Describe_TextsAndColors(
        double peakDbfs, int frames, string state, string text, string color, double percent)
    {
        LtcReceptionDisplay display = LtcReceptionPolicy.Describe(new LtcInputLevel(peakDbfs, frames), 25.0);

        display.State.ToString().Should().Be(state);
        display.Text.Should().Be(text);
        display.Foreground.Should().Be(color);
        display.MeterPercent.Should().BeApproximately(percent, 1e-9);
    }

    // ── UI へ渡す値（受け箱と ViewModel） ─────────────────────────

    [Fact]
    public void Mailbox_PostsOnceUntilTaken_AndKeepsLatestOnly()
    {
        var mailbox = new LatestValueMailbox<LtcInputLevel>();
        var a = new LtcInputLevel(-10, 1);
        var b = new LtcInputLevel(-20, 2);
        var c = new LtcInputLevel(-30, 3);

        mailbox.Offer(a).Should().BeTrue("最初は UI へ投げる");
        mailbox.Offer(b).Should().BeFalse("取り出されるまでは投げ直さない");
        mailbox.Take().Should().BeSameAs(b, "UI は最新だけを受ける");
        mailbox.Take().Should().BeNull();
        mailbox.Offer(c).Should().BeTrue("取り出した後は次を投げる");
        mailbox.Take().Should().BeSameAs(c);
    }

    [Fact]
    public void ViewModel_StartsAsSilent_AndAppliesLevels()
    {
        var vm = new SyncViewModel(new NoopLtcMonitor());
        vm.IsLtcRunning = true;

        vm.LtcReceptionText.Should().Be("無音");
        vm.LtcReceptionStatus.Should().Be("Silent");
        vm.LtcMeterPercent.Should().Be(0);

        vm.ApplyLtcInputLevel(new LtcInputLevel(-20, 0), 25).Should().BeTrue();
        vm.LtcReceptionText.Should().Be("信号あり・LTC なし");
        vm.LtcReceptionForeground.Should().Be("#D7A24B");
        vm.LtcReceptionStatus.Should().Be("SignalWithoutLtc");
        vm.LtcMeterPercent.Should().BeApproximately(66.667, 1e-3);

        vm.ApplyLtcInputLevel(new LtcInputLevel(-6, 25), 25).Should().BeTrue();
        vm.LtcReceptionText.Should().Be("LTC 受信中");
        vm.LtcReceptionForeground.Should().Be("#56D364");
        vm.LtcReceptionStatus.Should().Be("Receiving");

        vm.ApplyLtcInputLevel(new LtcInputLevel(-7, 25), 25).Should().BeFalse("段が変わらなければ false");
    }

    [Fact]
    public void ViewModel_RaisesTextOnlyOnStateChange_AndMeterOnValueChange()
    {
        var vm = new SyncViewModel(new NoopLtcMonitor());
        vm.IsLtcRunning = true;
        vm.ApplyLtcInputLevel(new LtcInputLevel(-6, 25), 25);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ApplyLtcInputLevel(new LtcInputLevel(-12, 25), 25);

        changed.Should().Equal(nameof(SyncViewModel.LtcMeterPercent));
    }

    [Fact]
    public void ViewModel_IgnoresLevelsWhileStopped_AndResetsOnRestart()
    {
        var vm = new SyncViewModel(new NoopLtcMonitor());
        vm.ApplyLtcInputLevel(new LtcInputLevel(-6, 25), 25).Should().BeFalse("止めている間に遅れて届いた値は捨てる");
        vm.LtcReceptionStatus.Should().Be("Silent");

        vm.IsLtcRunning = true;
        vm.ApplyLtcInputLevel(new LtcInputLevel(-6, 25), 25);
        vm.LtcReceptionStatus.Should().Be("Receiving");

        vm.IsLtcRunning = false;
        vm.ApplyLtcInputLevel(new LtcInputLevel(-30, 0), 25).Should().BeFalse();
        vm.LtcReceptionStatus.Should().Be("Receiving", "止めた後は表示を隠すだけで値は動かさない");

        vm.IsLtcRunning = true;
        vm.LtcReceptionStatus.Should().Be("Silent", "始め直したら前回の表示を持ち越さない");
        vm.LtcMeterPercent.Should().Be(0);
    }

    private sealed class NoopLtcMonitor : ILtcMonitor
    {
        public bool IsRunning => false;
        public string? DeviceName => null;
        public int SampleRate => 0;
        public void Start(string? deviceName) { }
        public void Stop() { }
        public IReadOnlyList<string> GetCaptureDeviceNames() => [];
        public event EventHandler<LtcFrameReceivedEventArgs>? FrameReceived { add { } remove { } }
        public event EventHandler<Exception?>? Stopped { add { } remove { } }
        public void Dispose() { }
    }
}

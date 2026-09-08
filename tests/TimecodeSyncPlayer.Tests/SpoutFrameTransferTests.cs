using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;

namespace TimecodeSyncPlayer.Tests;

public sealed class SpoutFrameTransferTests
{
    private sealed class Rig : ISpoutTransferBackend, ISpoutGpuCompletion, ISpoutTransferMutex
    {
        internal readonly List<string> Calls = [];
        internal readonly Queue<(int Hr, int Done)> Results = new();
        internal readonly Queue<long> Milliseconds = new();
        internal string Name = "Actual_1";
        internal bool SendResult = true, Acquired = true, Abandoned;
        internal long Tick, Step;
        internal long Qpc = Stopwatch.Frequency, DelayTicks = Stopwatch.Frequency / 50;
        internal string? DelayedStage, ThrowStage;
        internal bool ReleaseFails;
        internal int DeviceReason;
        internal bool DeviceReasonFails;
        private void Stage(string stage) { if (DelayedStage == stage) Qpc += DelayTicks; if (ThrowStage == stage) throw new InvalidOperationException($"Failure in {stage}"); }
        public string Prepare(uint width, uint height) { Calls.Add($"Prepare:{width}:{height}"); Stage("Prepare"); return Name; }
        public bool SendImage(IntPtr pixels, uint width, uint height, uint pitch) { Calls.Add("Send"); Stage("SendImage"); return SendResult; }
        public bool Wait(int milliseconds) { Calls.Add($"Wait:{milliseconds}"); Stage("WaitMutex"); if (Abandoned) throw new AbandonedMutexException(); return Acquired; }
        public void Release() { Calls.Add("Unlock"); Stage("ReleaseMutex"); if (ReleaseFails) throw new ApplicationException("Release failed"); }
        public void End() { Calls.Add("End"); Stage("End"); }
        public void Flush() { Calls.Add("Flush"); Stage("Flush"); }
        public int GetData(out int completed) { Calls.Add("Poll"); Stage("GetData"); var next = Results.Count > 0 ? Results.Dequeue() : (0, 1); completed = next.Item2; return next.Item1; }
        public int GetDeviceRemovedReason() { Calls.Add("DeviceReason"); if (DeviceReasonFails) throw new ApplicationException("Device reason read failed"); return DeviceReason; }
        public void Dispose() => Calls.Add("Dispose");
        internal SpoutFrameTransfer Create(ILogger? logger = null) => new(this, this, name => { Calls.Add($"Mutex:{name}"); Stage("CreateMutex"); return this; }, () => { if (Milliseconds.Count > 0) return Milliseconds.Dequeue(); long now = Tick; Tick += Step; return now; }, () => Qpc, logger);
    }

    private sealed class DiagnosticSink(Rig rig) : ILogEventSink
    {
        internal readonly List<LogEvent> Events = [];
        public void Emit(LogEvent logEvent)
        {
            // A logger may do I/O; diagnostics must never extend mutex ownership.
            rig.Calls.Where(x => x != "DeviceReason").Last().Should().Be("Unlock");
            Events.Add(logEvent);
        }
    }

    [Theory]
    [InlineData("Prepare", "PrepareMs")]
    [InlineData("CreateMutex", "CreateMutexMs")]
    [InlineData("WaitMutex", "MutexMs")]
    [InlineData("SendImage", "SendMs")]
    [InlineData("End", "EndMs")]
    [InlineData("Flush", "FlushMs")]
    [InlineData("GetData", "PollMs")]
    [InlineData("ReleaseMutex", "ReleaseMutexMs")]
    public void Send_SlowSuccessRecordsStageTimesAfterUnlock(string stage, string property)
    {
        var rig = new Rig { DelayedStage = stage };
        var sink = new DiagnosticSink(rig);
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var transfer = rig.Create(logger);
        transfer.SendImage(new(1), 3840, 2160, 15360).Should().BeTrue();
        var log = sink.Events.Should().ContainSingle().Which;
        log.Level.Should().Be(LogEventLevel.Warning);
        var details = (StructureValue)log.Properties["Transfer"];
        details.Properties.Single(x => x.Name == property).Value.Should().Be(new ScalarValue(20.0));
        details.Properties.Single(x => x.Name == "LastCompleted").Value.Should().Be(new ScalarValue(1));
        details.Properties.Single(x => x.Name == "Sender").Value.Should().Be(new ScalarValue("Actual_1"));
        rig.Calls.Should().NotContain("DeviceReason", "a slow successful send does not query device removal");
    }

    [Fact]
    public void Send_WithinBudgetDoesNotEmitDiagnostic()
    {
        var rig = new Rig(); var sink = new DiagnosticSink(rig);
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var transfer = rig.Create(logger);
        transfer.SendImage(new(1), 2, 3, 8).Should().BeTrue();
        sink.Events.Should().BeEmpty();
        rig.Calls.Should().NotContain("DeviceReason");
    }

    [Fact]
    public void Send_FalseResultRecordsRegisteredNameAndStageAfterUnlock()
    {
        var rig = new Rig { SendResult = false }; var sink = new DiagnosticSink(rig);
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var transfer = rig.Create(logger);
        transfer.SendImage(new(1), 2, 3, 8).Should().BeFalse();
        var log = sink.Events.Should().ContainSingle().Which;
        log.MessageTemplate.Text.Should().Contain("SendImage returned false");
        var details = (StructureValue)log.Properties["Transfer"];
        details.Properties.Single(x => x.Name == "Sender").Value.Should().Be(new ScalarValue("Actual_1"));
        details.Properties.Single(x => x.Name == "Stage").Value.Should().Be(new ScalarValue("SendImage"));
        details.Properties.Single(x => x.Name == "PollMs").Value.Should().Be(new ScalarValue(null));
    }

    [Fact]
    public void Send_HResultFailureRecordsElapsedGpuTime()
    {
        var rig = new Rig { Step = 23 };
        rig.Results.Enqueue((unchecked((int)0x887A0005), 0));
        using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        send.Should().Throw<System.Runtime.InteropServices.COMException>().Which.Data["SpoutTransfer"].Should().BeOfType<string>().Which.Should()
            .Contain("hr=887A0005; completed=0; gpuElapsedMs=23;");
    }

    [Fact]
    public void Send_MutexTimeoutRecordsActualWaitDuration()
    {
        var rig = new Rig { Acquired = false, DelayedStage = "WaitMutex", DelayTicks = Stopwatch.Frequency * 137 / 1000 };
        using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        send.Should().Throw<TimeoutException>().Which.Data["SpoutTransfer"].Should().BeOfType<string>().Which.Should()
            .Contain("mutexMs=137.000;").And.Contain("releaseMutexMs=not-started;");
    }

    [Theory]
    [InlineData("End")]
    [InlineData("Flush")]
    [InlineData("GetData")]
    public void Send_ReleaseFailurePreservesOriginalFailure(string stage)
    {
        var rig = new Rig { ThrowStage = stage, ReleaseFails = true };
        using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        var failure = send.Should().Throw<ApplicationException>().Which;
        failure.Data["SpoutTransferPriorException"].Should().BeOfType<InvalidOperationException>();
        failure.Data["SpoutTransfer"].Should().BeOfType<string>().Which.Should()
            .Contain("stage=ReleaseMutex;").And.Contain($"priorFailureStage={stage};").And.Contain($"Failure in {stage}");
    }

    [Fact]
    public void Send_HoldsActualNameMutexUntilGpuCompletion_AndPreparesEverySize()
    {
        var rig = new Rig(); rig.Results.Enqueue((1, 0)); rig.Results.Enqueue((0, 1));
        using var transfer = rig.Create();
        transfer.SendImage(new(1), 3840, 2160, 15360).Should().BeTrue();
        rig.Calls.Should().Equal("Prepare:3840:2160", "Mutex:Actual_1", "Wait:100", "Send", "End", "Flush", "Poll", "Poll", "Unlock");
        rig.Calls.Clear(); rig.Name = "Actual_2";
        transfer.SendImage(new(1), 1920, 1080, 7680).Should().BeTrue();
        rig.Calls.Take(5).Should().Equal("Prepare:1920:1080", "Dispose", "Mutex:Actual_2", "Wait:100", "Send");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Send_UnavailableOrAbandonedMutexNeverSends(bool abandoned)
    {
        var rig = new Rig { Acquired = false, Abandoned = abandoned };
        using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        if (abandoned) send.Should().Throw<AbandonedMutexException>();
        else
        {
            var failure = send.Should().Throw<TimeoutException>().Which;
            failure.Data["SpoutTransfer"].Should().BeOfType<string>().Which.Should()
                .Contain("stage=WaitMutex; sender=Actual_1;").And.Contain("polls=0; hr=n/a;");
        }
        rig.Calls.Should().NotContain("Send");
        rig.Calls.Should().NotContain("DeviceReason", "mutex failure never entered the GPU send path");
        rig.Calls.Count(x => x == "Unlock").Should().Be(abandoned ? 1 : 0);
    }

    [Fact]
    public void Send_NativeFailureSkipsQueryAndUnlocks()
    {
        var rig = new Rig { SendResult = false }; using var transfer = rig.Create();
        transfer.SendImage(new(1), 2, 3, 8).Should().BeFalse();
        rig.Calls.Should().NotContain("End"); rig.Calls.TakeLast(2).Should().Equal("Unlock", "DeviceReason");
    }

    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    public void Send_HResultFailureTakesPriorityOverCompletedAndDeadline(long elapsed)
    {
        const int hr = unchecked((int)0x887A0005);
        var rig = new Rig { Step = elapsed }; rig.Results.Enqueue((hr, 1)); using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        var failure = send.Should().Throw<System.Runtime.InteropServices.COMException>().Which;
        failure.HResult.Should().Be(hr);
        failure.Data["SpoutTransfer"].Should().BeOfType<string>().Which.Should()
            .Contain($"hr={hr:X8}; completed=1; gpuElapsedMs={elapsed};");
        rig.Calls.Should().Equal("Prepare:2:3", "Mutex:Actual_1", "Wait:100", "Send", "End", "Flush", "Poll", "Unlock", "DeviceReason");
    }

    [Theory]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    public void Send_ConfirmedCompletionIsAcceptedAcrossDeadlineAndStillReportsDelay(long elapsed)
    {
        var rig = new Rig { Step = elapsed, DelayedStage = "GetData", DelayTicks = Stopwatch.Frequency * elapsed / 1000 };
        rig.Results.Enqueue((0, 1));
        var sink = new DiagnosticSink(rig);
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var transfer = rig.Create(logger);
        transfer.SendImage(new(1), 3840, 2160, 15360).Should().BeTrue();
        rig.Calls.Should().Equal("Prepare:3840:2160", "Mutex:Actual_1", "Wait:100", "Send", "End", "Flush", "Poll", "Unlock");
        var log = sink.Events.Should().ContainSingle().Which;
        log.MessageTemplate.Text.Should().Contain("slow send");
        var details = (StructureValue)log.Properties["Transfer"];
        details.Properties.Single(x => x.Name == "GpuElapsedMs").Value.Should().Be(new ScalarValue(elapsed));
        details.Properties.Single(x => x.Name == "LastHResult").Value.Should().Be(new ScalarValue(0));
        details.Properties.Single(x => x.Name == "LastCompleted").Value.Should().Be(new ScalarValue(1));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void Send_PendingAt99MillisecondsKeepsMutexUntilConfirmedCompletion(int hr, int done)
    {
        var rig = new Rig();
        rig.Milliseconds.Enqueue(0); rig.Milliseconds.Enqueue(99); rig.Milliseconds.Enqueue(99);
        rig.Results.Enqueue((hr, done)); rig.Results.Enqueue((0, 1));
        using var transfer = rig.Create();
        transfer.SendImage(new(1), 2, 3, 8).Should().BeTrue();
        rig.Calls.Should().Equal("Prepare:2:3", "Mutex:Actual_1", "Wait:100", "Send", "End", "Flush", "Poll", "Poll", "Unlock");
    }

    [Theory]
    [InlineData(1, 0, 100)]
    [InlineData(0, 0, 100)]
    [InlineData(1, 1, 100)]
    [InlineData(1, 0, 101)]
    [InlineData(0, 0, 101)]
    [InlineData(1, 1, 101)]
    public void Send_PendingAtDeadlineStopsPollingAndPreservesLastQueryResult(int hr, int done, long elapsed)
    {
        // A nonzero completed value alone is insufficient: S_FALSE is pending.
        // Even if a following result would complete, do not poll past this deadline.
        var rig = new Rig { Step = elapsed };
        rig.Results.Enqueue((hr, done));
        rig.Results.Enqueue((0, 1));
        using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 3840, 2160, 15360);
        var failure = send.Should().Throw<TimeoutException>().Which;
        failure.Data["SpoutTransfer"].Should().BeOfType<string>().Which.Should()
            .Contain("stage=GetData; sender=Actual_1; size=3840x2160; polls=1;")
            .And.Contain($"hr={hr:X8}; completed={done}; gpuElapsedMs={elapsed};")
            .And.Contain("totalBeforeCleanupMs=");
        rig.Calls.Should().Equal("Prepare:3840:2160", "Mutex:Actual_1", "Wait:100", "Send", "End", "Flush", "Poll", "Unlock", "DeviceReason");
        rig.Results.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0x887A0006))]
    public void Send_PendingTimeoutRecordsDeviceReasonAfterUnlockAndStillFails(int reason)
    {
        var rig = new Rig { Step = 100, DeviceReason = reason };
        rig.Results.Enqueue((1, 0));
        using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        var failure = send.Should().Throw<TimeoutException>().Which;
        failure.Data["SpoutDeviceRemovedReason"].Should().Be(reason);
        failure.Data["SpoutTransfer"].Should().BeOfType<string>().Which.Should().Contain($"deviceRemovedReason={reason:X8}");
        rig.Calls.TakeLast(2).Should().Equal("Unlock", "DeviceReason");
    }

    [Theory]
    [InlineData("SendImage")]
    [InlineData("End")]
    [InlineData("Flush")]
    [InlineData("GetData")]
    public void Send_DeviceReasonDiagnosticFailureDoesNotReplaceTransferFailure(string stage)
    {
        var rig = new Rig { ThrowStage = stage, DeviceReasonFails = true };
        using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        var failure = send.Should().Throw<InvalidOperationException>().Which;
        failure.Message.Should().Be($"Failure in {stage}");
        failure.Data["SpoutDeviceRemovedReasonException"].Should().BeOfType<ApplicationException>();
        failure.Data["SpoutTransfer"].Should().BeOfType<string>().Which.Should()
            .Contain("deviceRemovedReason=n/a;").And.Contain("deviceReasonError=Device reason read failed");
        rig.Calls.TakeLast(2).Should().Equal("Unlock", "DeviceReason");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Send_FalseResultIncludesDeviceDiagnosticAndStillReturnsFalse(bool diagnosticFails)
    {
        var rig = new Rig { SendResult = false, DeviceReason = unchecked((int)0x887A0005), DeviceReasonFails = diagnosticFails };
        var sink = new DiagnosticSink(rig);
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var transfer = rig.Create(logger);
        transfer.SendImage(new(1), 2, 3, 8).Should().BeFalse();
        var details = (StructureValue)sink.Events.Should().ContainSingle().Which.Properties["Transfer"];
        details.Properties.Single(x => x.Name == "DeviceRemovedReason").Value.Should().Be(new ScalarValue(diagnosticFails ? null : (object)rig.DeviceReason));
        details.Properties.Single(x => x.Name == "DeviceReasonError").Value.Should().Be(new ScalarValue(diagnosticFails ? "Device reason read failed" : null));
        rig.Calls.TakeLast(2).Should().Equal("Unlock", "DeviceReason");
    }

    [Fact]
    public void Dispose_ReleasesResourcesOnceAndPreventsSend()
    {
        var rig = new Rig(); var transfer = rig.Create(); transfer.SendImage(new(1), 2, 3, 8); rig.Calls.Clear();
        transfer.Dispose(); transfer.Dispose();
        rig.Calls.Should().Equal("Dispose", "Dispose");
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        send.Should().Throw<ObjectDisposedException>();
    }
}

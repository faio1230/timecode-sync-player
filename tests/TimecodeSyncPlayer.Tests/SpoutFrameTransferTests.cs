using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class SpoutFrameTransferTests
{
    private sealed class Rig : ISpoutTransferBackend, ISpoutGpuCompletion, ISpoutTransferMutex
    {
        internal readonly List<string> Calls = [];
        internal readonly Queue<(int Hr, int Done)> Results = new();
        internal string Name = "Actual_1";
        internal bool SendResult = true, Acquired = true, Abandoned;
        internal long Tick, Step;
        public string Prepare(uint width, uint height) { Calls.Add($"Prepare:{width}:{height}"); return Name; }
        public bool SendImage(IntPtr pixels, uint width, uint height, uint pitch) { Calls.Add("Send"); return SendResult; }
        public bool Wait(int milliseconds) { Calls.Add($"Wait:{milliseconds}"); if (Abandoned) throw new AbandonedMutexException(); return Acquired; }
        public void Release() => Calls.Add("Unlock");
        public void End() => Calls.Add("End");
        public void Flush() => Calls.Add("Flush");
        public int GetData(out int completed) { Calls.Add("Poll"); var next = Results.Count > 0 ? Results.Dequeue() : (0, 1); completed = next.Item2; return next.Item1; }
        public void Dispose() => Calls.Add("Dispose");
        internal SpoutFrameTransfer Create() => new(this, this, name => { Calls.Add($"Mutex:{name}"); return this; }, () => { long now = Tick; Tick += Step; return now; });
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
        send.Should().Throw<Exception>();
        rig.Calls.Should().NotContain("Send");
        rig.Calls.Count(x => x == "Unlock").Should().Be(abandoned ? 1 : 0);
    }

    [Fact]
    public void Send_NativeFailureSkipsQueryAndUnlocks()
    {
        var rig = new Rig { SendResult = false }; using var transfer = rig.Create();
        transfer.SendImage(new(1), 2, 3, 8).Should().BeFalse();
        rig.Calls.Should().NotContain("End"); rig.Calls.Last().Should().Be("Unlock");
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0005), 1, 0)]
    [InlineData(1, 0, 101)]
    [InlineData(0, 0, 101)]
    public void Send_QueryFailureOrDeadlineReleasesMutex(int hr, int done, long step)
    {
        var rig = new Rig { Step = step }; rig.Results.Enqueue((hr, done)); using var transfer = rig.Create();
        Action send = () => transfer.SendImage(new(1), 2, 3, 8);
        if (hr < 0) send.Should().Throw<System.Runtime.InteropServices.COMException>().Which.HResult.Should().Be(hr);
        else send.Should().Throw<TimeoutException>();
        rig.Calls.Last().Should().Be("Unlock");
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

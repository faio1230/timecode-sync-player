using System.Runtime.InteropServices;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

// Hand-built COM vtables call managed delegates only: no device, DLL or GPU is used.
public sealed class SpoutGpuCompletionTests
{
    private sealed class ComFixture : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];
        private readonly List<Delegate> _delegates = [];
        internal IntPtr Device, Context, Query;
        internal int CreateHr, Releases, DeviceReason;
        internal bool NullQuery;
        internal readonly List<string> Calls = [];
        internal ComFixture()
        {
            Query = Object(3);
            Slot(Query, 2, new SpoutGpuCompletion.ReleaseDelegate(_ => { Releases++; return 0; }));
            Device = Object(40);
            Slot(Device, 24, new SpoutGpuCompletion.CreateQueryDelegate(Create));
            Slot(Device, 39, new SpoutGpuCompletion.GetDeviceRemovedReasonDelegate(self =>
            {
                self.Should().Be(Device); Calls.Add("DeviceReason"); return DeviceReason;
            }));
            Context = Object(112);
            Slot(Context, 28, new SpoutGpuCompletion.EndDelegate((self, query) => { self.Should().Be(Context); query.Should().Be(Query); Calls.Add("End"); }));
            Slot(Context, 111, new SpoutGpuCompletion.FlushDelegate(self => { self.Should().Be(Context); Calls.Add("Flush"); }));
            Slot(Context, 29, new SpoutGpuCompletion.GetDataDelegate(GetData));
        }
        private int Create(IntPtr self, ref SpoutGpuCompletion.QueryDesc desc, out IntPtr query)
        {
            self.Should().Be(Device); desc.Query.Should().Be(0); desc.MiscFlags.Should().Be(0);
            query = NullQuery ? IntPtr.Zero : Query; return CreateHr;
        }
        private int GetData(IntPtr self, IntPtr query, out int completed, uint size, uint flags)
        {
            self.Should().Be(Context); query.Should().Be(Query); size.Should().Be(4); flags.Should().Be(1);
            Calls.Add("Poll"); completed = 1; return 0;
        }
        private IntPtr Object(int slots)
        {
            var table = Marshal.AllocHGlobal(slots * IntPtr.Size); _allocations.Add(table);
            for (int i = 0; i < slots; i++) Marshal.WriteIntPtr(table, i * IntPtr.Size, IntPtr.Zero);
            var obj = Marshal.AllocHGlobal(IntPtr.Size); _allocations.Add(obj); Marshal.WriteIntPtr(obj, table); return obj;
        }
        private void Slot(IntPtr obj, int slot, Delegate method)
        {
            _delegates.Add(method); Marshal.WriteIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(method));
        }
        public void Dispose() { foreach (var pointer in _allocations) Marshal.FreeHGlobal(pointer); GC.KeepAlive(_delegates); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0x887A0006))]
    public void DeviceRemovedReason_ReturnsNativeHResultWithoutReleasingBorrowedDevice(int reason)
    {
        using var fixture = new ComFixture { DeviceReason = reason };
        var completion = new SpoutGpuCompletion(fixture.Device, fixture.Context);
        completion.GetDeviceRemovedReason().Should().Be(reason);
        fixture.Calls.Should().Equal("DeviceReason");
        fixture.Releases.Should().Be(0);
        completion.Dispose(); fixture.Releases.Should().Be(1);
    }

    [Fact]
    public void Completion_UsesEventBoolAndDontFlushAbi_ReleasesOnlyOwnedQueryOnce()
    {
        using var fixture = new ComFixture();
        var completion = new SpoutGpuCompletion(fixture.Device, fixture.Context);
        completion.End(); completion.Flush(); completion.GetData(out var done).Should().Be(0);
        done.Should().Be(1); fixture.Calls.Should().Equal("End", "Flush", "Poll");
        completion.Dispose(); completion.Dispose(); fixture.Releases.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Creation_FailedHresultCleansPartialQuery(bool nullQuery)
    {
        using var fixture = new ComFixture { CreateHr = unchecked((int)0x80004005), NullQuery = nullQuery };
        Action create = () => new SpoutGpuCompletion(fixture.Device, fixture.Context);
        create.Should().Throw<COMException>().Which.HResult.Should().Be(unchecked((int)0x80004005));
        fixture.Releases.Should().Be(nullQuery ? 0 : 1);
    }

    [Fact]
    public void Creation_SuccessWithoutQueryIsRejected()
    {
        using var fixture = new ComFixture { NullQuery = true };
        Action create = () => new SpoutGpuCompletion(fixture.Device, fixture.Context);
        create.Should().Throw<InvalidOperationException>();
        fixture.Releases.Should().Be(0);
    }
}

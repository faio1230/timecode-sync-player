using System.Buffers;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class LatestRenderedFrameMailboxTests
{
    [Fact]
    public void ReplacementKeepsOnlyLatestAndDoesNotMutateTakenFrame()
    {
        var pool = new CountingPool();
        using var mailbox = new LatestRenderedFrameMailbox();
        mailbox.Publish(RenderedFrameSnapshot.Copy(new byte[] { 1, 0, 0, 0 }, 1, 1, 1, 1, 0, pool));
        using var taken = mailbox.Take();
        for (int i = 2; i <= 100; i++)
            mailbox.Publish(RenderedFrameSnapshot.Copy(new byte[] { (byte)i, 0, 0, 0 }, 1, 1, 1, i, 0, pool));
        pool.Outstanding.Should().Be(2);
        pool.MaximumOutstanding.Should().BeLessThanOrEqualTo(3);
        taken!.Pixels[0].Should().Be(1);
        using var latest = mailbox.Take();
        latest!.Pixels[0].Should().Be(100);
        mailbox.Take().Should().BeNull();
    }

    [Fact]
    public void DisposeReturnsPendingAndRejectsLatePublication()
    {
        var pool = new CountingPool();
        var mailbox = new LatestRenderedFrameMailbox();
        mailbox.Publish(RenderedFrameSnapshot.Copy(new byte[4], 1, 1, 1, 1, 0, pool));
        mailbox.Dispose();
        mailbox.Publish(RenderedFrameSnapshot.Copy(new byte[4], 1, 1, 1, 2, 0, pool));
        pool.Outstanding.Should().Be(0);
    }

    [Fact]
    public void ObserverRecordsDiscardIdentityAndCannotPreventPoolReturns()
    {
        var pool = new CountingPool();
        var discarded = new List<(long Sequence, string Reason)>();
        var mailbox = new LatestRenderedFrameMailbox((frame, reason) =>
        {
            discarded.Add((frame.Sequence, reason));
            throw new InvalidOperationException("observer failure");
        });
        mailbox.Publish(RenderedFrameSnapshot.Copy(new byte[4], 1, 1, 7, 1, 0, pool));
        mailbox.Publish(RenderedFrameSnapshot.Copy(new byte[4], 1, 1, 7, 2, 0, pool));
        mailbox.Clear();
        mailbox.Publish(RenderedFrameSnapshot.Copy(new byte[4], 1, 1, 7, 3, 0, pool));
        mailbox.Dispose();
        mailbox.Publish(RenderedFrameSnapshot.Copy(new byte[4], 1, 1, 7, 4, 0, pool));
        discarded.Should().Equal((1, "mailbox-replaced"), (2, "mailbox-cleared"), (3, "mailbox-disposed"), (4, "mailbox-closed"));
        pool.Outstanding.Should().Be(0);
    }

    private sealed class CountingPool : ArrayPool<byte>
    {
        public int Outstanding;
        public int MaximumOutstanding;
        public override byte[] Rent(int minimumLength)
        {
            MaximumOutstanding = Math.Max(MaximumOutstanding, ++Outstanding);
            return new byte[minimumLength];
        }
        public override void Return(byte[] array, bool clearArray = false) => Outstanding--;
    }
}

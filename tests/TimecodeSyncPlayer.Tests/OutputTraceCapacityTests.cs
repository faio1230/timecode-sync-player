using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class OutputTraceCapacityTests
{
    [Theory]
    [InlineData(null, OutputTrace.DefaultCapacity)]
    [InlineData("", OutputTrace.DefaultCapacity)]
    [InlineData("abc", OutputTrace.DefaultCapacity)]
    [InlineData("0", OutputTrace.DefaultCapacity)]
    [InlineData("-5", OutputTrace.DefaultCapacity)]
    [InlineData("2000", 2000)]
    public void ParseCapacity_UsesDefaultForMissingOrInvalid(string? value, int expected)
        => OutputTrace.ParseCapacity(value).Should().Be(expected);

    [Fact]
    public void Record_DropsBeyondCapacityAndCounts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tcs-trace-cap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var trace = new OutputTrace(dir, capacity: 2);
            trace.Record(new OutputTraceEvent("a", "GPU", 1));
            trace.Record(new OutputTraceEvent("b", "GPU", 2));
            trace.Record(new OutputTraceEvent("c", "GPU", 3));
            trace.Record(new OutputTraceEvent("d", "GPU", 4));

            trace.Capacity.Should().Be(2);
            trace.Recorded.Should().Be(2);
            trace.Dropped.Should().Be(2);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
        }
    }
}

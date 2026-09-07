using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TimecodeSyncPlayer.Tests;

public class SyncAccuracyTraceTests
{
    [Fact]
    public void DisabledTrace_HasNoFileOrWorkerAndIgnoresInvalidPointer()
    {
        using var trace = SyncAccuracyTrace.Create(null);
        Assert.False(trace.IsEnabled);
        trace.RecordFrame("normal", new IntPtr(1), 1920, 1080, 7680, 1);
        trace.RecordLtc(new(new(0, 0, 1, 12, false), 0, 0));
    }

    [Fact]
    public void Dispose_FlushesMetaLtcAndPixelFrameWithReceiptAndPublicationTimestamps()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var trace = SyncAccuracyTrace.Create(path))
            {
                Assert.True(trace.IsEnabled);
                long before = Stopwatch.GetTimestamp();
                trace.RecordLtc(new(new(0, 0, 1, 12, false), 0, 0));
                var pixels = AccuracyFrameMarkerTests.GoldenPixels();
                var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try { trace.RecordFrame("frozen", pinned.AddrOfPinnedObject(), 1920, 1080, 7680, before); }
                finally { pinned.Free(); }
            }
            var lines = Read(path);
            Assert.Equal(new[] { "meta", "ltc", "frame", "end" }, lines.Select(x => x.GetProperty("type").GetString()));
            Assert.Equal(1, lines[0].GetProperty("schema").GetInt32());
            Assert.Equal(Stopwatch.Frequency, lines[0].GetProperty("frequency").GetInt64());
            Assert.Equal("bitmap-publication", lines[0].GetProperty("boundary").GetString());
            Assert.Equal("decoded-ltc-receipt", lines[0].GetProperty("reference").GetString());
            Assert.Equal(1.48, lines[1].GetProperty("seconds").GetDouble(), 6);
            Assert.Equal(25, lines[1].GetProperty("fps").GetDouble());
            Assert.True(lines[2].GetProperty("ticks").GetInt64() <= lines[1].GetProperty("ticks").GetInt64());
            Assert.Equal(0x1234, lines[2].GetProperty("frameIndex").GetInt32());
            Assert.True(lines[2].GetProperty("markerValid").GetBoolean());
            Assert.False(lines[2].GetProperty("isBlack").GetBoolean());
            Assert.True(lines[2].GetProperty("probeTicks").GetInt64() >= 0);
            Assert.Equal(0, lines[^1].GetProperty("dropped").GetInt64());
            Assert.Equal(0, lines[^1].GetProperty("errors").GetInt64());
            Assert.Equal(2, lines[^1].GetProperty("events").GetInt64());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExistingFile_IsNeverOverwritten()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "original");
            using var trace = SyncAccuracyTrace.Create(path);
            Assert.False(trace.IsEnabled);
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConcurrentProducers_AllEventsAreWrittenOrExplicitlyCountedAsDropped()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var trace = SyncAccuracyTrace.Create(path, capacity: 1))
                Parallel.For(0, 2000, i => trace.RecordLtc(new(new(0, 0, 1, i % 25, false), 25, 1)));
            var lines = Read(path);
            long written = lines.Count(x => x.GetProperty("type").GetString() == "ltc");
            Assert.Equal(2000, written + lines[^1].GetProperty("dropped").GetInt64());
            Assert.Equal(written, lines[^1].GetProperty("events").GetInt64());
            Assert.Equal(0, lines[^1].GetProperty("errors").GetInt64());
        }
        finally { File.Delete(path); }
    }

    internal static JsonElement[] Read(string path) => File.ReadAllLines(path)
        .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.Clone(); }).ToArray();
}

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
    public void DisabledRenderObserver_DoesNotAllocateEvents()
    {
        var trace = SyncAccuracyTrace.Disabled;
        trace.RecordRenderStage(1, 1, 0, null, "native-render", "completed", 1, 2, 2, 2, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            trace.RecordRenderStage(1, i, 0, null, "native-render", "completed", 1, 2, 2, 2, 0);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void RenderStagePressure_IsBoundedAndWrittenOrCountedAsDropped()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var trace = SyncAccuracyTrace.Create(path, capacity: 1))
                Parallel.For(0, 2000, i => trace.RecordRenderStage(1, i + 1, 0, null,
                    "native-render", "completed", i, i + 1, 16, 16, -1));
            var lines = Read(path);
            long written = lines.Count(x => x.GetProperty("type").GetString() == "render-stage");
            Assert.Equal(2000, written + lines[^1].GetProperty("dropped").GetInt64());
            Assert.Equal(written, lines[^1].GetProperty("events").GetInt64());
            Assert.Equal(0, lines[^1].GetProperty("errors").GetInt64());
            Assert.Equal(1, lines[0].GetProperty("renderStageSchema").GetInt32());
        }
        finally { File.Delete(path); }
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

    [Theory]
    [InlineData(null, 25.0)]
    [InlineData("", 25.0)]
    [InlineData("24", 24.0)]
    [InlineData("29.97002997002997", 30000.0 / 1001.0)]
    [InlineData("30", 30.0)]
    [InlineData("bad", 25.0)]
    [InlineData("0", 25.0)]
    [InlineData("-25", 25.0)]
    public void ParseReferenceLtcFps_FallsBackToNominal25(string? value, double expected)
    {
        Assert.Equal(expected, SyncAccuracyTrace.ParseReferenceLtcFps(value), 6);
    }

    [Fact]
    public void ReferenceLtcFps_At29_97_ControlsLtcSecondsAndFpsField()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var trace = SyncAccuracyTrace.Create(path, capacity: 64, referenceLtcFps: 30000.0 / 1001.0))
                trace.RecordLtc(new(new(0, 0, 1, 12, false), 0, 0));

            var lines = Read(path);
            Assert.Equal(30000.0 / 1001.0, lines[0].GetProperty("nominalLtcFps").GetDouble(), 6);
            // 29.97 NDF は総フレーム換算: (0,0,1,12) は 42 フレーム ÷ 29.97。
            Assert.Equal(42 / (30000.0 / 1001.0), lines[1].GetProperty("seconds").GetDouble(), 6);
            Assert.Equal(30000.0 / 1001.0, lines[1].GetProperty("fps").GetDouble(), 6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PreviewFrames_KeepSourceKindAndProbeData_AndRemainSeparateFromExternalFrames()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            using (var trace = SyncAccuracyTrace.Create(path))
            {
                var pixels = AccuracyFrameMarkerTests.GoldenPixels();
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    trace.RecordFrame("normal", pin.AddrOfPinnedObject(), 1920, 1080, 7680, 100);
                    trace.RecordPreviewFrame("frozen", pin.AddrOfPinnedObject(), 1920, 1080, 7680, 200);
                }
                finally { pin.Free(); }
            }
            var rows = Read(path);
            Assert.Equal(new[] { "meta", "frame", "preview-frame", "end" }, rows.Select(r => r.GetProperty("type").GetString()));
            Assert.Equal(1, rows[0].GetProperty("previewFrameSchema").GetInt32());
            Assert.Equal("frozen", rows[2].GetProperty("kind").GetString());
            Assert.Equal(200, rows[2].GetProperty("ticks").GetInt64());
            Assert.True(rows[2].GetProperty("markerValid").GetBoolean());
            Assert.Equal(0x1234, rows[2].GetProperty("frameIndex").GetInt32());
            Assert.Equal(2, rows[^1].GetProperty("events").GetInt64());
            Assert.Equal(0, rows[^1].GetProperty("errors").GetInt64());
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

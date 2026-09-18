using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class GopScanCacheTests
{
    private static GopScanResult Result(double maxGap, int keyframes = 10) =>
        new(keyframes, 60.0, 0.0, 0.0, maxGap, maxGap, maxGap);

    [Fact]
    public void 同じ素材を二度測らない()
    {
        int calls = 0;
        var cache = new GopScanCache(_ => { calls++; return Result(1.0); });

        cache.EnsureScanned("a.mp4").Should().BeTrue();
        cache.EnsureScanned("a.mp4").Should().BeFalse("2 回目は測らない");

        calls.Should().Be(1);
    }

    [Fact]
    public void 測る前は結果が無い()
    {
        var cache = new GopScanCache(_ => Result(1.0));

        cache.TryGet("a.mp4").Should().BeNull("測っていない素材では警告を出さない");
    }

    [Fact]
    public void 測定に失敗した素材は警告を出さないが再測定もしない()
    {
        int calls = 0;
        var cache = new GopScanCache(_ => { calls++; return null; });

        cache.EnsureScanned("broken.mp4");
        cache.EnsureScanned("broken.mp4");

        calls.Should().Be(1, "失敗しても毎回のロードで測り直さない");
        cache.TryGet("broken.mp4")!.Value.Quality.Should().Be(GopSeekQuality.Unknown);
    }

    [Fact]
    public void 長いギャップの素材は警告になる()
    {
        var cache = new GopScanCache(_ => Result(6.708, keyframes: 149));
        cache.EnsureScanned("m6.mp4");

        cache.TryGet("m6.mp4")!.Value.Quality.Should().Be(GopSeekQuality.Warning);
    }

    [Fact]
    public void プロダクション素材相当は警告にならない()
    {
        var cache = new GopScanCache(_ => Result(0.501, keyframes: 2438));
        cache.EnsureScanned("m8.mp4");

        cache.TryGet("m8.mp4")!.Value.Quality.Should().Be(GopSeekQuality.Ok);
    }

    [Fact]
    public void パスが空なら何もしない()
    {
        var cache = new GopScanCache(_ => throw new InvalidOperationException("呼ばれてはいけない"));

        cache.EnsureScanned(null).Should().BeFalse();
        cache.EnsureScanned(string.Empty).Should().BeFalse();
        cache.TryGet(null).Should().BeNull();
    }
}

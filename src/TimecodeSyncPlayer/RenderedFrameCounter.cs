namespace TimecodeSyncPlayer;

/// <summary>
/// D4: ロード安定ゲート（<see cref="TimecodeSyncService.TryMarkFileLoaded"/>）が数える
/// 「表示経路に到達したフレーム数」の供給元。
/// CPU 合成は WriteableBitmap の描画数、GPU 合成は <c>OutputEngine.PublishedFrameCount</c>
/// （合成プールへ公開した数）を使う。GPU 合成ではビットマップを描かないため、CPU の数は
/// 0 のままでゲートがタイムアウト（5 秒）まで開かない。
/// </summary>
internal sealed class RenderedFrameCounter
{
    private readonly bool gpuCompositing;
    private readonly Func<long> cpuRenderedFrames;
    private readonly Func<long> gpuPublishedFrames;

    public RenderedFrameCounter(bool gpuCompositing, Func<long> cpuRenderedFrames, Func<long> gpuPublishedFrames)
    {
        this.gpuCompositing = gpuCompositing;
        this.cpuRenderedFrames = cpuRenderedFrames;
        this.gpuPublishedFrames = gpuPublishedFrames;
    }

    /// <summary>いま有効な出力経路で表示に到達したフレーム数。</summary>
    public long Read() => gpuCompositing ? gpuPublishedFrames() : cpuRenderedFrames();
}

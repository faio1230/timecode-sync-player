namespace TimecodeSyncPlayer;

/// <summary>
/// D4: ロード安定ゲート（<see cref="TimecodeSyncService.TryMarkFileLoaded"/>）が数える
/// 「表示経路に到達したフレーム数」の供給元。出荷構成は GPU 合成だけで、
/// <c>OutputEngine.PublishedFrameCount</c>（合成プールへ公開した数）を使う。
/// </summary>
internal sealed class RenderedFrameCounter
{
    private readonly Func<long> gpuPublishedFrames;

    public RenderedFrameCounter(Func<long> gpuPublishedFrames)
    {
        this.gpuPublishedFrames = gpuPublishedFrames;
    }

    /// <summary>表示経路に到達したフレーム数。</summary>
    public long Read() => gpuPublishedFrames();
}

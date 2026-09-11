namespace TimecodeSyncPlayer.Output;

/// <summary>
/// キャンバス変更時の旧世代の破棄判断（GPU 非依存、段階 4.5 管理テスト対象）。
/// 新しい合成画像が公開され、かつ旧 pool の lease が全て返るまで旧世代を保持する。
/// 新画像の公開を条件に含めるのは、作り直し中に黒を挟まないため（段階 4.2）。
/// </summary>
internal sealed class CanvasSwapPlan
{
    public bool HasRetiredGeneration { get; private set; }

    public bool NewGenerationPublished { get; private set; }

    /// <summary>新しいキャンバスへの切替を開始した。</summary>
    public void Request()
    {
        HasRetiredGeneration = true;
        NewGenerationPublished = false;
    }

    /// <summary>新世代の画像が 1 枚 publish された。</summary>
    public void NewImagePublished()
    {
        if (HasRetiredGeneration) NewGenerationPublished = true;
    }

    /// <summary>旧 pool の lease が全て返っていれば破棄できる。</summary>
    public bool CanDiscardOld(int activeOldLeases)
        => HasRetiredGeneration && NewGenerationPublished && activeOldLeases <= 0;

    public void Discarded()
    {
        HasRetiredGeneration = false;
        NewGenerationPublished = false;
    }
}

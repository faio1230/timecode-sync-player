namespace TimecodeSyncPlayer.Output;

/// <summary>
/// L-3: ソース接続・世代変更（gst.generation／load／seek）・共有リング作成の直後は
/// 合成位相が乱れるため lead 学習を 1 秒除外する。すべて GPU worker から呼ぶ。
/// </summary>
internal sealed class ComposeLeadSuspension(Func<long> nowQpc)
{
    private ComposeLeadController? controller;

    public void Attach(ComposeLeadController value) => controller = value;

    public void OnSourceGenerationChanged() => Suspend();

    public void OnSourceAttached() => Suspend();

    public void OnSharedRingOpened() => Suspend();

    private void Suspend() => controller?.SuspendLearning(nowQpc());
}

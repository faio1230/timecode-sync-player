namespace TimecodeSyncPlayer.Output;

internal enum SpoutCopyDecision { NoImage, SameHeld, Copy }
internal enum SpoutWorkerAction { None, Start, Stop }

/// <summary>
/// Spout 送信経路の選択と、保持画像の再送・worker 起動停止の方針（段階 3）。
/// Gpu backend では合成層の SendTexture 経路だけを使い、Cpu backend の SendImage 経路は
/// 従来どおり ISpoutOutput に残す。判断を純粋関数にして管理テストで固定する。
/// </summary>
internal static class SpoutOutputPolicy
{
    /// <summary>CPU 側 spoutDX（SendImage）を初期化するのは Cpu backend のときだけ。</summary>
    public static bool InitializeCpuSpout(OutputBackend backend) => backend == OutputBackend.Cpu;

    /// <summary>CPU 画素の SendImage を呼ぶのは Cpu backend かつ有効時だけ。</summary>
    public static bool SendCpuFrame(OutputBackend backend, bool enabled)
        => backend == OutputBackend.Cpu && enabled;

    /// <summary>選択画像と保持画像の関係。同じ画像・画像なしは保持画像を再送する（コピーしない）。</summary>
    public static SpoutCopyDecision DecideCopy(long selectedId, long heldId)
        => selectedId == 0 ? SpoutCopyDecision.NoImage
            : selectedId == heldId ? SpoutCopyDecision.SameHeld
            : SpoutCopyDecision.Copy;

    /// <summary>送信 worker の操作。無効化は常に停止、有効化で未起動なら開始、起動中は維持。</summary>
    public static SpoutWorkerAction EvaluateWorker(bool enabled, bool running)
        => enabled ? (running ? SpoutWorkerAction.None : SpoutWorkerAction.Start)
                   : (running ? SpoutWorkerAction.Stop : SpoutWorkerAction.None);
}

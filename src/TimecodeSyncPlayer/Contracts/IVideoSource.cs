using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Contracts;

/// <summary>
/// 映像ソース契約（docs/GPU-SOURCE-CONTRACT-SPEC.md）。ソースは再生位置に対する最適な GPU 画像を lease で渡し、
/// 出力やタイムラインの判断を持たない。規則 1〜4・6 は SourceImageRing と管理テストで固定する。
/// </summary>
public readonly record struct SourceImageStamp(int Generation, long Sequence, double PositionSeconds, long DecodedQpc);

public enum SourceStatus { Ready, NotReady, Ended }

/// <summary>契約拡張: IsNv12 だけでなく形式を持つ（HAP の BC 経路も同じ契約に載せる）。</summary>
public enum SourceImageFormat { Bgra8, Nv12, Bc1, Bc3, Bc7 }

/// <summary>lease が公開するテクスチャ情報。GPU 無しのフェイク・テストでは Texture は null。</summary>
public readonly record struct SourceImageDescription(ID3D11Texture2D? Texture, int Width, int Height, SourceImageFormat Format);

/// <summary>合成層が所有する画像 lease。Dispose で1回だけ返却する。</summary>
public interface ISourceImageLease : IDisposable
{
    SourceImageStamp Stamp { get; }
    ID3D11Texture2D? Texture { get; }
    int Width { get; }
    int Height { get; }
    SourceImageFormat Format { get; }
    bool IsNv12 => Format == SourceImageFormat.Nv12;
    void BeginGpuUse();
    void CompleteGpuUse();
}

public interface IVideoSource : IDisposable
{
    /// <summary>以後、古い世代の画像は返さない。</summary>
    void SetGeneration(int generation);

    /// <summary>ブロックしない。準備中・読込み失敗・世代不一致は NotReady、末尾は Ended。</summary>
    SourceStatus TryAcquire(int generation, double positionSeconds, out ISourceImageLease? lease);

    SourceDiagnostics Diagnostics { get; }

    /// <summary>lease が残る間は false。強制解放しない。呼び出し側はポーリングする。</summary>
    bool TryDispose();
}

/// <summary>
/// generationRejected: 非現世代の Offer 拒否と SetGeneration による退避。notReady: Ready 以外の TryAcquire。
/// replaced: 未 lease 画像が新しい Offer に置き換えられた数。dropped: 全画像 lease 中で Offer を捨てた数。
/// </summary>
public sealed record SourceDiagnostics(string Decoder, string Gpu, string Format, long GenerationRejected, long NotReady, long Replaced,
    long Dropped, int PeakLeases, long Offered, long Ready, long Ended);

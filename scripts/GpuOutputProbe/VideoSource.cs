using Vortice.Direct3D11;

namespace GpuOutputProbe;

// Source contract (docs/GPU-SOURCE-CONTRACT-SPEC.md): a source supplies the best available GPU image for a playback position
// and holds no output or timeline decisions. Rules 1-4 and 6 are fixed by SourceImageRing and its self-tests.
internal readonly record struct SourceImageStamp(int Generation, long Sequence, double PositionSeconds, long DecodedQpc);
internal enum SourceStatus { Ready, NotReady, Ended }
// Spec extension: a format instead of IsNv12 only, so HAP (BC) paths fit the same contract.
internal enum SourceImageFormat { Bgra8, Nv12, Bc1, Bc3, Bc7 }
// What a lease exposes about its texture. Texture is null in GPU-free fakes/tests.
internal readonly record struct SourceImageDescription(ID3D11Texture2D? Texture, int Width, int Height, SourceImageFormat Format);

internal interface ISourceImageLease : IDisposable // Owned by the compose layer; Dispose returns the image (once).
{
    SourceImageStamp Stamp { get; }
    ID3D11Texture2D? Texture { get; } // Read-only for the holder. Null only in fake/test implementations.
    int Width { get; }
    int Height { get; }
    SourceImageFormat Format { get; }
    bool IsNv12 => Format == SourceImageFormat.Nv12;
    void BeginGpuUse(); // Same rules as LatestPool.Lease: no nesting, no Dispose while in use.
    void CompleteGpuUse();
}

internal interface IVideoSource : IDisposable
{
    void SetGeneration(int generation); // Images of older generations are never returned afterwards.
    SourceStatus TryAcquire(int generation, double positionSeconds, out ISourceImageLease? lease); // Never blocks.
    SourceDiagnostics Diagnostics { get; }
    // Dispose never force-releases: it refuses (throws) while a lease is outstanding. Callers that must wait poll TryDispose.
    bool TryDispose();
}

// generationRejected: offers of a non-current generation dropped plus images retired by SetGeneration.
// notReady: TryAcquire results that were not Ready (generation mismatch, no image, Ended).
// replaced: an un-leased image evicted by a newer offer. dropped: offers refused because every image was leased.
internal sealed record SourceDiagnostics(string Decoder, string Gpu, string Format, long GenerationRejected, long NotReady, long Replaced,
    long Dropped, int PeakLeases, long Offered, long Ready, long Ended);

using System;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// tcs_gstreamer.dll の薄いラッパ。テストでは fake を注入する。
/// 戻り値の規約はネイティブ側のヘッダ (tcs_gstreamer.h) と同じ。
/// </summary>
internal interface IGstNativeApi
{
    IntPtr PlayerCreate(string senderName, out string error);

    /// <summary>
    /// 合成層の ID3D11Device を shim に Adopt させてプレイヤーを生成する（outputBackend=Gpu 時）。
    /// externalDevice が IntPtr.Zero の場合は shim 所有デバイスになる。
    /// </summary>
    IntPtr PlayerCreate(string senderName, IntPtr externalDevice, out string error);
    void PlayerDestroy(IntPtr player);
    int Load(IntPtr player, string path, double startSeconds, bool paused, out string error);
    int Stop(IntPtr player);
    int SetPaused(IntPtr player, bool paused);
    bool IsPaused(IntPtr player);
    ulong Seek(IntPtr player, double seconds);
    ulong StepFrame(IntPtr player);
    ulong GetGeneration(IntPtr player);
    ulong SetGeneration(IntPtr player, ulong generation);
    int SetSpeed(IntPtr player, double rate);
    int SetVolume(IntPtr player, double volume0To100);
    int SetMute(IntPtr player, bool mute);
    bool TryGetTimePos(IntPtr player, out double seconds);
    bool TryGetDuration(IntPtr player, out double seconds);
    bool TryGetFps(IntPtr player, out double fps);
    string GetPath(IntPtr player);
    bool TryGetSize(IntPtr player, out int width, out int height);
    void SetFrameCallback(IntPtr player, GstNative.TcsFrameNotifyDelegate? callback);
    int ConsumeUpdate(IntPtr player);
    bool Acquire(IntPtr player, ulong generation, out GstNative.TcsFrameInfo info);
    bool TryGetLeasedTexture(IntPtr player, out IntPtr texture, out uint subresource, out uint dxgiFormat);
    int LeasedCpuCopy(IntPtr player, IntPtr dst, int dstStride);
    void Release(IntPtr player);
    int PublishSpoutVerification(IntPtr player);
    int SendImage(IntPtr player, IntPtr bgra, int width, int height, int pitch);
    string DecoderName(IntPtr player);
    bool SpoutReady(IntPtr player);

    /// <summary>配信トレース（問題 H の計測）。qpc は QPC 時計で events.jsonl と同じ基準。</summary>
    int DrainDeliveryEvents(IntPtr player, GstNative.TcsDeliveryEvent[] buffer, uint capacity, out uint count);
    int GetDeliveryStats(IntPtr player, out GstNative.TcsDeliveryStats stats);
}

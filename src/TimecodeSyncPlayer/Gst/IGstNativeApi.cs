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
    /// outputBackend=Gpu 時は合成層の ID3D11Device を渡す。shim はこれを Adopt せず、
    /// アダプター LUID の読み取りにのみ使い、同じ LUID 上に自前のデバイスを作る（ステージ 6b）。
    /// externalDevice が IntPtr.Zero の場合は既定アダプターの shim 所有デバイスになる。
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
    /// <summary>T5: 非フラッシュのレート変更。TCS_OK / TCS_ERR_GENERIC / TCS_ERR_NOT_LOADED。</summary>
    int SetRateInstant(IntPtr player, double rate);
    int SetVolume(IntPtr player, double volume0To100);
    int SetMute(IntPtr player, bool mute);

    /// <summary>最初の load 前に 1 回だけ。GstNative.DecodeModeHardware / DecodeModeSoftware。</summary>
    int SetDecodeMode(IntPtr player, int mode);
    bool TryGetTimePos(IntPtr player, out double seconds);
    bool TryGetDuration(IntPtr player, out double seconds);
    bool TryGetFps(IntPtr player, out double fps);
    string GetPath(IntPtr player);
    bool TryGetSize(IntPtr player, out int width, out int height);
    void SetFrameCallback(IntPtr player, GstNative.TcsFrameNotifyDelegate? callback);
    int ConsumeUpdate(IntPtr player);
    /// <summary>1 = frame, 0 = none, -6 = Ended, その他負 = error。</summary>
    int Acquire(IntPtr player, ulong generation, out GstNative.TcsFrameInfo info);
    bool TryGetLeasedTexture(IntPtr player, out IntPtr texture, out uint subresource, out uint dxgiFormat);
    void Release(IntPtr player);
    int PublishSpoutVerification(IntPtr player);
    int SendImage(IntPtr player, IntPtr bgra, int width, int height, int pitch);
    string DecoderName(IntPtr player);
    bool SpoutReady(IntPtr player);

    /// <summary>配信トレース（問題 H の計測）。qpc は QPC 時計で events.jsonl と同じ基準。</summary>
    int DrainDeliveryEvents(IntPtr player, GstNative.TcsDeliveryEvent[] buffer, uint capacity, out uint count);
    int GetDeliveryStats(IntPtr player, out GstNative.TcsDeliveryStats stats);

    /// <summary>
    /// ステージ 6b: 共有リングの NT ハンドル・共有フェンス・寸法を取得する。
    /// リング未作成（load 前/CPU 経路）は TCS_ERR_NO_FRAME。ハンドルは shim 所有。
    /// </summary>
    int GetRingInfo(IntPtr player, IntPtr[] handles, uint capacity, out uint count,
        out IntPtr fence, out int width, out int height);

    /// <summary>D8: 現在のリング世代（0 = リング無し）。解像度変更でリングが作り直されると +1 される。</summary>
    int GetRingEpoch(IntPtr player, out uint epoch);
}

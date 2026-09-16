namespace TimecodeSyncPlayer.Contracts;

/// <summary>
/// フレーム更新通知とコンテキスト寿命の境界。
/// <see cref="RenderUpdateFn"/> の契約は変えない（デリゲートは呼び出し側が保持する）。
/// 実装は GStreamer バックエンド（GstRenderUpdateSource）。
/// </summary>
public interface IRenderUpdateSource
{
    /// <summary>更新フラグのうち「新しいフレーム」を表すビット（従来 MPV_RENDER_UPDATE_FRAME）。</summary>
    ulong FrameUpdateFlag { get; }

    /// <summary>フレーム通知コンテキストを作成する。player は既存のプレイヤーハンドル。</summary>
    bool TryCreateContext(IntPtr player, out IntPtr context);

    /// <summary>更新フラグを消費する（破壊的でない参照は実装側の判断にしない）。</summary>
    ulong ConsumeUpdate(IntPtr context);

    /// <summary>更新コールバックを登録する。null で解除する。</summary>
    void SetUpdateCallback(IntPtr context, RenderUpdateFn? callback);

    void FreeContext(IntPtr context);
}

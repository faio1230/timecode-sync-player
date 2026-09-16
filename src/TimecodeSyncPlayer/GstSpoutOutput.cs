using System;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer;

/// <summary>
/// ISpoutOutput の GStreamer バックエンド用。送信者 (sender) は shim が所有する
/// D3D11 デバイス上の spoutDX インスタンスそのもので、GPU 合成の送信は
/// OutputEngine の Spout worker（SpoutSender）が行う。ここは有効/無効の状態だけを持つ。
/// </summary>
internal sealed class GstSpoutOutput : ISpoutOutput
{
    private readonly GstBackendState _state;

    public GstSpoutOutput(GstBackendState state)
    {
        _state = state;
    }

    public bool IsEnabled { get; set; } = false;

    public bool IsAvailable
    {
        get
        {
            IntPtr player = _state.Player;
            return player != IntPtr.Zero && _state.Native.SpoutReady(player);
        }
    }

    public bool TryInitialize() => IsAvailable;

    public void Dispose()
    {
        // sender の所有と解放はプレイヤー (GstBackendState) の側で行う。
    }
}

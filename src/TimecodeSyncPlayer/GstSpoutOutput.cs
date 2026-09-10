using System;
using Serilog;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer;

/// <summary>
/// ISpoutOutput の GStreamer バックエンド用。
/// 送信者 (sender) は shim が所有する D3D11 デバイス上の spoutDX インスタンスそのもの。
/// - 通常フレーム: IGpuSpoutPublisher.TryPublishCurrentGpuFrame（GPU テクスチャ送信）
/// - Freeze/Black: SendFrame → CPU BGRA 画像送信（同一 sender 経由）
/// </summary>
internal sealed class GstSpoutOutput : ISpoutOutput, IGpuSpoutPublisher
{
    private readonly GstBackendState _state;
    private long _sendCount;

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

    public void SendFrame(IntPtr pixels, int width, int height)
    {
        IntPtr player = _state.Player;
        if (!IsEnabled || player == IntPtr.Zero || pixels == IntPtr.Zero) return;
        if (width <= 0 || height <= 0) return;

        try
        {
            int rc = _state.Native.SendImage(player, pixels, width, height, width * 4);
            if (rc == 0)
            {
                _sendCount++;
                if (_sendCount == 1)
                    Log.Information("GstSpoutOutput: CPU 画像送信開始 {W}x{H} sender='{Name}'",
                        width, height, _state.SenderName);
            }
            else if (rc == -4)
            {
                Log.Debug("GstSpoutOutput: Spout 未準備のため CPU 画像送信をスキップ");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstSpoutOutput: CPU 画像送信で例外");
        }
    }

    /// <summary>
    /// shim が保持する最新フレーム（GPU テクスチャならそれを、無ければ CPU 像）を
    /// Spout へ送信する検証済み経路。renderer 直後の同一フレームを publish するため、
    /// WriteableBitmap と Spout の画像が対応する。
    /// </summary>
    public bool TryPublishCurrentGpuFrame()
    {
        IntPtr player = _state.Player;
        if (!IsEnabled || player == IntPtr.Zero) return false;
        try
        {
            int rc = _state.Native.PublishSpoutVerification(player);
            if (rc == 0)
            {
                _sendCount++;
                return true;
            }
            if (rc == -4)
                Log.Debug("GstSpoutOutput: Spout 未準備のため GPU publish をスキップ");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GstSpoutOutput: GPU publish で例外");
            return false;
        }
    }

    public void Dispose()
    {
        // sender の所有と解放はプレイヤー (GstBackendState) の側で行う。
    }
}

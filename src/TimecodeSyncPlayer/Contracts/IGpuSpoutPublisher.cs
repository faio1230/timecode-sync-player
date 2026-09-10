using System;
using Serilog;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer;

/// <summary>
/// GPU メモリ上の最新フレームをそのまま Spout 送信できる出力経路。
/// 実装がこれを持つ場合、SpoutFramePublisher は CPU ピクセル送信の代わりに
/// GPU 画像を優先する（失敗時だけ CPU へフォールバック）。
/// </summary>
public interface IGpuSpoutPublisher
{
    bool TryPublishCurrentGpuFrame();
}

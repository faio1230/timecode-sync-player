namespace TimecodeSyncPlayer.Contracts;

/// <summary>
/// 再生操作と状態取得の境界（型付き）。文字列コマンドやプロパティ名を呼び出し側へ漏らさない。
/// 相対シークは含めない（<see cref="Seek"/> とクライアント側の加算で表現する）。
/// 実装は GStreamer バックエンド（GstPlaybackApi）。
/// </summary>
public interface IPlaybackApi
{
    /// <summary>位置つきロード。startSeconds が null のときは先頭から。paused は初期再生状態。</summary>
    PlaybackResult Load(string path, double? startSeconds, bool paused);

    /// <summary>絶対シーク（秒）。負値は 0 に clamp する。相対シークは呼び出し側で加算する。</summary>
    PlaybackResult Seek(double seconds);

    PlaybackResult Stop();

    PlaybackResult SetPaused(bool paused);

    /// <summary>再生レート（速度切替）。正の値のみ受け付ける。</summary>
    PlaybackResult SetRate(double rate);

    /// <summary>非フラッシュのレート変更（T5 Smooth）。失敗は Smooth 使用不可として扱う。</summary>
    PlaybackResult SetRateInstant(double rate);

    /// <summary>音量（0〜100）。失敗は実装がログに残す。</summary>
    void SetVolume(double volume0To100);

    void SetMute(bool mute);

    bool TryGetTimePos(out double seconds);

    /// <summary>
    /// 0.4.5-A: 位置・基準・世代・最新配信 PTS を同じ瞬間の 1 スナップショットで返す。
    /// ネイティブ DLL が未対応（_ex が無い）のときは旧 <see cref="TryGetTimePos"/> に落ちる。
    /// </summary>
    bool TryGetPositionSample(out PlaybackPositionSample sample);

    bool TryGetDuration(out double seconds);

    bool TryGetFps(out double fps);

    /// <summary>現在ロード中のパス。不明なら空文字。</summary>
    string GetPath();

    bool TryGetSize(out int width, out int height);

    /// <summary>映像デコーダ名（表示用）。不明なら空文字。</summary>
    string GetVideoCodec();

    bool IsPaused();

    /// <summary>シーク発行後、新位置のフレームが届くまで true（実装内の到着数ベース）。</summary>
    bool IsSeeking();
}

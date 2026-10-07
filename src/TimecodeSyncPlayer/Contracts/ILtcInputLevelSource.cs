namespace TimecodeSyncPlayer.Contracts;

/// <summary>
/// v0.6.6 R-13: LTC の入力のレベルの知らせ。監視（音声のスレッド）が 100ms ごとに 1 回出す。
/// 受け取り側は UI へ移してから使う（このイベントの中で重い処理をしない）。
/// <see cref="ILtcMonitor"/> とは別の口にして、レベルを出さない監視（試験の偽物）に実装を求めない。
/// </summary>
public interface ILtcInputLevelSource
{
    event EventHandler<LtcInputLevel>? LevelReported;
}

/// <summary>
/// v0.6.6 R-13: 1 回分のレベル。<paramref name="PeakDbfs"/> は前回の知らせから今回までのピーク（dBFS、
/// 無音は <see cref="LtcInputLevelMeter.FloorDbfs"/>）。<paramref name="DecodedFramesLastSecond"/> は
/// 直近 1 秒にデコーダが出した LTC のフレームの数（デコーダの判定はそのまま、出力を数えるだけ）。
/// </summary>
public sealed record LtcInputLevel(double PeakDbfs, int DecodedFramesLastSecond);

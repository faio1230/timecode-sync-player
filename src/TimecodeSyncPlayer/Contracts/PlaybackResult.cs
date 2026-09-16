namespace TimecodeSyncPlayer.Contracts;

/// <summary>
/// 再生操作の結果。失敗は理由を持ち、呼び出し側が成功と取り違えない
/// （文字列経路の「未知コマンドでも 0」を型付き API では作らない）。
/// </summary>
public readonly record struct PlaybackResult(bool Success, string? Error)
{
    public static PlaybackResult Ok { get; } = new(true, null);

    public static PlaybackResult Fail(string error) => new(false, error);
}

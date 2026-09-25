namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.2 段 2a: シークバーの表示位置を保留するインスタンス状態（書くだけで読む側がいなかった）を消し、
/// 使われている static のヘルパーだけを残した。
/// </summary>
public static class SeekBarUpdateState
{
    public static double ToSliderValue(double positionSeconds, double durationSeconds, double fallbackValue)
    {
        if (!IsUsableDuration(durationSeconds))
            return Math.Clamp(fallbackValue, 0, 1);

        return Math.Clamp(positionSeconds / durationSeconds, 0, 1);
    }

    public static double ToSliderValueFromPointer(double pointerX, double actualWidth, double fallbackValue)
    {
        if (!double.IsFinite(pointerX) || !double.IsFinite(actualWidth) || actualWidth <= 0)
            return Math.Clamp(fallbackValue, 0, 1);

        return Math.Clamp(pointerX / actualWidth, 0, 1);
    }

    public static bool IsUsableDuration(double durationSeconds)
        => double.IsFinite(durationSeconds) && durationSeconds > 0;
}

using System.Globalization;
using System.Windows.Data;

namespace TimecodeSyncPlayer;

/// <summary>v0.6.6 R-6: ツールチップを付けるボタン。</summary>
internal enum ButtonToolTipKey
{
    Open,
    Fullscreen,
    PreviousTrack,
    NextTrack,
    Back,
    FrameBack,
    PlayPause,
    FrameForward,
    Forward,
    Mute,
    Speed,
    Spout,
    TestCard,
    Timeline,
    RefreshLtcDevices,
    StartLtc,
    StopLtc,
    ToggleSync,
    ApplyCanvas,
    GpuRetry,
    AddToPlaylist,
    RemoveFromPlaylist,
    ClearPlaylist,
    MoveTrackUp,
    MoveTrackDown,
    SaveProject,
    LoadProject,
}

/// <summary>
/// v0.6.6 R-6: ボタンのツールチップの文字（純粋関数）。押せるときはボタンの説明、
/// 押せないときは説明の下に押せない理由を足す。見せ方だけで、押せるかどうかの判断はしない
/// （判断は今までどおり各コマンドの CanExecute やコードビハインドが持つ）。
/// </summary>
internal static class ButtonToolTipText
{
    public const string ReasonPrefix = "押せない理由: ";

    /// <summary>説明と押せない理由を組む。理由が無い（押せる）ときは説明だけ。</summary>
    public static string Compose(string description, string? disabledReason)
        => string.IsNullOrWhiteSpace(disabledReason)
            ? description
            : description + "\n" + ReasonPrefix + disabledReason;

    /// <summary>ボタンのツールチップ。押せないときは <see cref="DisabledReason"/> を足す。</summary>
    public static string For(ButtonToolTipKey key, bool isEnabled)
        => Compose(Describe(key), isEnabled ? null : DisabledReason(key));

    public static string Describe(ButtonToolTipKey key) => key switch
    {
        ButtonToolTipKey.Open => "動画を 1 本開く（プレイリストをその 1 本に置き換える）",
        ButtonToolTipKey.Fullscreen => "選んだディスプレイへ全画面で出力する。出力中に押すと閉じる",
        ButtonToolTipKey.PreviousTrack => "前のトラック",
        ButtonToolTipKey.NextTrack => "次のトラック",
        ButtonToolTipKey.Back => "10 秒戻す",
        ButtonToolTipKey.FrameBack => "1 フレーム戻す",
        ButtonToolTipKey.PlayPause => "再生／一時停止",
        ButtonToolTipKey.FrameForward => "1 フレーム送る",
        ButtonToolTipKey.Forward => "10 秒送る",
        ButtonToolTipKey.Mute => "音を消す／戻す（ON で消音）",
        ButtonToolTipKey.Speed => "再生の速度を切り替える",
        ButtonToolTipKey.Spout => "Spout の送信を ON/OFF する",
        ButtonToolTipKey.TestCard => "出力にテストカードを出す／消す（再生には影響しない）",
        ButtonToolTipKey.Timeline => "タイムラインを表示する／隠す",
        ButtonToolTipKey.RefreshLtcDevices => "LTC を受ける録音デバイスの一覧を読み直す",
        ButtonToolTipKey.StartLtc => "選んだデバイスで LTC の受信を始める",
        ButtonToolTipKey.StopLtc => "LTC の受信を止める",
        ButtonToolTipKey.ToggleSync => "LTC に合わせて再生する（Sync）を ON/OFF する",
        ButtonToolTipKey.ApplyCanvas => "キャンバスの大きさと配置を出力へ適用する",
        ButtonToolTipKey.GpuRetry => "GPU の復旧をもう一度試す",
        ButtonToolTipKey.AddToPlaylist => "動画をプレイリストの末尾に追加する",
        ButtonToolTipKey.RemoveFromPlaylist => "選んだトラックをプレイリストから削除する",
        ButtonToolTipKey.ClearPlaylist => "プレイリストを空にする",
        ButtonToolTipKey.MoveTrackUp => "選んだトラックを 1 つ上へ動かす",
        ButtonToolTipKey.MoveTrackDown => "選んだトラックを 1 つ下へ動かす",
        ButtonToolTipKey.SaveProject => "プレイリストをプロジェクト（.tsp）として保存する",
        ButtonToolTipKey.LoadProject => "保存したプロジェクト（.tsp）を読み込む",
        _ => "",
    };

    /// <summary>
    /// 押せないときの理由。押せない状態が 1 つに決まるボタンだけ持つ。決まらないボタン
    /// （ふだんは常に押せるボタン）は null で、ツールチップは説明だけになる。
    /// キャンバスの適用は理由が状態で変わるので、呼び出し側が <see cref="CanvasChangeGate.DescribeReason"/> を <see cref="Compose"/> に渡す。
    /// </summary>
    public static string? DisabledReason(ButtonToolTipKey key) => key switch
    {
        ButtonToolTipKey.Fullscreen => "出力先のディスプレイが見つかりません",
        ButtonToolTipKey.Spout => "映像出力の準備ができていないため使えません",
        ButtonToolTipKey.StartLtc => "LTC を受信中です。止めるときは STOP を押してください",
        ButtonToolTipKey.StopLtc => "LTC は止まっています。START で受信を始めます",
        ButtonToolTipKey.RemoveFromPlaylist => "トラックを選んでください",
        ButtonToolTipKey.MoveTrackUp => "トラックを選んでください（先頭のトラックは上へ動かせません）",
        ButtonToolTipKey.MoveTrackDown => "トラックを選んでください（末尾のトラックは下へ動かせません）",
        _ => null,
    };
}

/// <summary>
/// ボタンの IsEnabled をツールチップの文字にする（XAML 用）。ConverterParameter は <see cref="ButtonToolTipKey"/> の名前。
/// </summary>
internal sealed class ButtonToolTipConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string name || !Enum.TryParse(name, out ButtonToolTipKey key))
            return null;
        bool isEnabled = value is not bool enabled || enabled;
        return ButtonToolTipText.For(key, isEnabled);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

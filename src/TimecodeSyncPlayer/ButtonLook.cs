using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 R-7: ON/OFF のボタンの見せ方。ボタンのテンプレートは <see cref="IsOnProperty"/> が真のとき緑の縁と背景にする。
/// 見せ方だけで、ボタンの文字（「ON/OFF」）や押したときの動きは変えない。
/// </summary>
internal static class ButtonLook
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(ButtonLook), new FrameworkPropertyMetadata(false));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);

    /// <summary>
    /// ボタンの文字から ON かを決める（純粋関数）。末尾が「ON」の語なら ON
    /// （「Sync ON」「Spout ON」「Timeline ON」「Card: ON」「MUTE ON」）。
    /// 「Spout N/A」や「… OFF」、空は ON でない。
    /// </summary>
    public static bool IsOnLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return false;
        string trimmed = label.TrimEnd();
        if (!trimmed.EndsWith("ON", StringComparison.Ordinal))
            return false;
        // 「ON」の前は語の切れ目（空白か「:」）か、文字が「ON」だけ。
        if (trimmed.Length == 2)
            return true;
        char before = trimmed[^3];
        return char.IsWhiteSpace(before) || before == ':';
    }
}

/// <summary>ボタンの文字を <see cref="ButtonLook.IsOnLabel"/> で bool にする（XAML 用）。</summary>
internal sealed class OnLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ButtonLook.IsOnLabel(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

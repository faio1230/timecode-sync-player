using System.Windows;
using System.Windows.Controls;

namespace TimecodeSyncPlayer;

/// <summary>
/// 主画面に全画面を出す前の確認（v0.6.6 R-8）。ShowDialog が true なら「出す」、それ以外（「やめる」・Enter・Esc・×）は出さない。
/// </summary>
internal partial class FullscreenConfirmDialog : Window
{
    public FullscreenConfirmDialog(Window owner)
    {
        Owner = owner;
        // 見せ方の束（R-2）の暗いボタンのテンプレートは MainWindow の資源にある。別の窓へは継がれないので、持ち主の暗黙のスタイルを写す。
        if (owner.TryFindResource(typeof(Button)) is Style buttonStyle)
            Resources[typeof(Button)] = buttonStyle;
        InitializeComponent();
        // 既定のフォーカスは「やめる」（Enter の誤爆で全画面にしない）。
        Loaded += (_, _) => BtnCancel.Focus();
    }

    private void BtnShow_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

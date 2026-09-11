using System.Windows;

namespace TimecodeSyncPlayer;

/// <summary>
/// 終了確認と終了手順の進捗表示（段階 5.1）。表示中も再生・LTC・出力は止めない。
/// 既定ボタンはキャンセル（Enter）、IsCancel で Esc もキャンセル。
/// </summary>
internal partial class ExitDialog : Window
{
    public ExitDialog() => InitializeComponent();

    public event Action? CancelClicked;
    public event Action? NormalExitClicked;
    public event Action? ForceExitClicked;

    public void ConfigureConfirmation()
    {
        DialogMessage.Text = "終了しますか？（再生と出力は続いています）";
        ProgressPanel.Visibility = Visibility.Collapsed;
        BtnExitCancel.IsEnabled = true;
        BtnExitNormal.IsEnabled = true;
        BtnExitForce.IsEnabled = true;
    }

    public void ConfigureProgress()
    {
        DialogMessage.Text = "終了しています。";
        ProgressPanel.Visibility = Visibility.Visible;
        BtnExitCancel.IsEnabled = false;
        BtnExitNormal.IsEnabled = false;
        BtnExitForce.IsEnabled = true;
    }

    public void SetProgressStep(string stepName) => ProgressStep.Text = stepName;

    private void BtnExitCancel_Click(object sender, RoutedEventArgs e) => CancelClicked?.Invoke();

    private void BtnExitNormal_Click(object sender, RoutedEventArgs e) => NormalExitClicked?.Invoke();

    private void BtnExitForce_Click(object sender, RoutedEventArgs e) => ForceExitClicked?.Invoke();
}

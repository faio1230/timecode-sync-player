using System.Windows;

namespace TimecodeSyncPlayer;

/// <summary>
/// ExitDialog を ExitCoordinator の IExitDialogHost として扱う WPF 実装。
/// 確認→進捗の切り替え、×／Alt+F4 クローズのキャンセル扱い、Coordinator 起点のクローズを区別する。
/// </summary>
internal sealed class ExitDialogHost : IExitDialogHost
{
    private readonly Window owner;
    private ExitDialog? dialog;
    private bool closingForShutdown;

    public ExitDialogHost(Window owner) => this.owner = owner;

    public event Action? CancelRequested;
    public event Action? NormalExitRequested;
    public event Action? ForceExitRequested;

    public void ShowConfirmation()
    {
        closingForShutdown = false;
        dialog = CreateDialog();
        dialog.ConfigureConfirmation();
        dialog.ShowDialog();
        if (!closingForShutdown)
            CancelRequested?.Invoke(); // ×／Alt+F4 で閉じた場合はキャンセルとして扱う。
        dialog = null;
    }

    public void SwitchToProgress() => dialog?.ConfigureProgress();

    public void UpdateStep(string stepName) => dialog?.SetProgressStep(stepName);

    public void CloseDialog()
    {
        closingForShutdown = true;
        dialog?.Close();
    }

    private ExitDialog CreateDialog()
    {
        var value = new ExitDialog { Owner = owner };
        value.CancelClicked += () => CancelRequested?.Invoke();
        value.NormalExitClicked += () => NormalExitRequested?.Invoke();
        value.ForceExitClicked += () => ForceExitRequested?.Invoke();
        return value;
    }
}

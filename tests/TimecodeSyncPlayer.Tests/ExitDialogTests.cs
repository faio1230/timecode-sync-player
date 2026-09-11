using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

[Collection("WpfWindow")]
public sealed class ExitDialogTests
{
    [Fact]
    public Task ConfirmationMode_CancelIsDefaultAndCancelKey_AndIdsAreStable() => OnUi(() =>
    {
        var dialog = new ExitDialog();
        try
        {
            dialog.ConfigureConfirmation();

            Button cancel = (Button)dialog.FindName("BtnExitCancel");
            Button normal = (Button)dialog.FindName("BtnExitNormal");
            Button force = (Button)dialog.FindName("BtnExitForce");
            TextBlock progress = (TextBlock)dialog.FindName("ProgressStep");

            cancel.IsDefault.Should().BeTrue("既定ボタンはキャンセル（Enter）");
            cancel.IsCancel.Should().BeTrue("Esc もキャンセル");
            normal.IsDefault.Should().BeFalse();
            force.IsDefault.Should().BeFalse();
            cancel.IsEnabled.Should().BeTrue();
            normal.IsEnabled.Should().BeTrue();
            force.IsEnabled.Should().BeTrue();
            progress.Parent.Should().BeAssignableTo<FrameworkElement>()
                .Which.Visibility.Should().Be(Visibility.Collapsed);

            AutomationProperties.GetAutomationId(dialog).Should().Be("ExitDialog");
            AutomationProperties.GetAutomationId(cancel).Should().Be("BtnExitCancel");
            AutomationProperties.GetAutomationId(normal).Should().Be("BtnExitNormal");
            AutomationProperties.GetAutomationId(force).Should().Be("BtnExitForce");
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ProgressMode_DisablesCancelAndNormal_KeepsForceEnabled() => OnUi(() =>
    {
        var dialog = new ExitDialog();
        try
        {
            dialog.ConfigureConfirmation();
            dialog.ConfigureProgress();
            dialog.SetProgressStep(MainWindowResourceDisposer.StopOutputStepName);

            Button cancel = (Button)dialog.FindName("BtnExitCancel");
            Button normal = (Button)dialog.FindName("BtnExitNormal");
            Button force = (Button)dialog.FindName("BtnExitForce");
            TextBlock progress = (TextBlock)dialog.FindName("ProgressStep");
            TextBlock message = (TextBlock)dialog.FindName("DialogMessage");

            cancel.IsEnabled.Should().BeFalse();
            normal.IsEnabled.Should().BeFalse();
            force.IsEnabled.Should().BeTrue();
            progress.Text.Should().Be(MainWindowResourceDisposer.StopOutputStepName);
            message.Text.Should().Contain("終了しています");
        }
        finally { dialog.Close(); }
        return Task.CompletedTask;
    });

    private static Task OnUi(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}

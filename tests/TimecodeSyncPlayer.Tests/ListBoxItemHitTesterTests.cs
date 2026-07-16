using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ListBoxItemHitTesterTests
{
    [Fact]
    public void GetItemIndexAt_PointOnItem_ReturnsItemIndex()
    {
        RunOnSta(() =>
        {
            var listBox = CreateRealizedListBox(width: 200, height: 200, itemCount: 3);
            var container = (System.Windows.Controls.ListBoxItem)listBox.ItemContainerGenerator.ContainerFromIndex(1);
            Point pointOnSecondItem = container
                .TranslatePoint(new Point(container.ActualWidth / 2, container.ActualHeight / 2), listBox);

            int index = ListBoxItemHitTester.GetItemIndexAt(listBox, pointOnSecondItem);

            index.Should().Be(1);
        });
    }

    [Fact]
    public void GetItemIndexAt_PointOnEmptyAreaBelowItems_ReturnsMinusOne()
    {
        RunOnSta(() =>
        {
            var listBox = CreateRealizedListBox(width: 200, height: 200, itemCount: 2);

            // 2項目分の高さより十分下の空白領域
            int index = ListBoxItemHitTester.GetItemIndexAt(listBox, new Point(100, 190));

            index.Should().Be(-1);
        });
    }

    [Fact]
    public void GetItemIndexAt_PointOutsideBounds_ReturnsMinusOne()
    {
        RunOnSta(() =>
        {
            var listBox = CreateRealizedListBox(width: 200, height: 200, itemCount: 2);

            int index = ListBoxItemHitTester.GetItemIndexAt(listBox, new Point(-10, -10));

            index.Should().Be(-1);
        });
    }

    private static System.Windows.Controls.ListBox CreateRealizedListBox(int width, int height, int itemCount)
    {
        var listBox = new System.Windows.Controls.ListBox
        {
            Width = width,
            Height = height,
            ItemsSource = Enumerable.Range(0, itemCount).Select(i => $"track {i}").ToList(),
        };
        System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(listBox, false);

        // 項目コンテナの実体化には可視ツリーが必要なため、画面外ウィンドウでホストする
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -30000,
            Top = -30000,
            Width = width + 20,
            Height = height + 20,
            Content = listBox,
        };
        window.Show();
        listBox.UpdateLayout();
        return listBox;
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}

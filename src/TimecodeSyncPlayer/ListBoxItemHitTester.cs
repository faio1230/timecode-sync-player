using System.Windows;
using System.Windows.Media;

namespace TimecodeSyncPlayer;

/// <summary>
/// ListBox 内の座標がどの項目上かを判定する。スクロールバーや空白領域は -1。
/// </summary>
internal static class ListBoxItemHitTester
{
    internal static int GetItemIndexAt(System.Windows.Controls.ListBox listBox, Point point)
    {
        var element = listBox.InputHitTest(point) as DependencyObject;
        while (element != null)
        {
            if (element is System.Windows.Controls.ListBoxItem item)
                return listBox.ItemContainerGenerator.IndexFromContainer(item);
            element = VisualTreeHelper.GetParent(element);
        }

        return -1;
    }
}

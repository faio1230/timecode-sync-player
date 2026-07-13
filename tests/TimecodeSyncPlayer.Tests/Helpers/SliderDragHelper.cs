using System.ComponentModel;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class SliderDragHelper
{
    private const int InitialPositionDelayMs = 50;
    private const int MouseDownDelayMs       = 80;
    private const int IntermediateSteps      = 8;
    private const int IntermediateStepDelayMs = 20;
    private const int PreReleaseDelayMs      = 80;
    private const int PostReleaseDelayMs     = 150;

    /// <summary>
    /// スライダーの相対位置（0.0 から 1.0）にマウスをドラッグする。
    /// SendInput 拒否環境では SkipException をスローする。
    /// </summary>
    public static void DragSliderOrSkip(Slider slider, double fromRatio, double toRatio)
    {
        bool mouseDown = false;
        try
        {
            var rect = slider.BoundingRectangle;
            int y = rect.Top + (rect.Height / 2);
            int fromX = rect.Left + (int)(rect.Width * fromRatio);
            int toX   = rect.Left + (int)(rect.Width * toRatio);

            Mouse.Position = new System.Drawing.Point(fromX, y);
            Thread.Sleep(InitialPositionDelayMs);
            Mouse.Down(MouseButton.Left);
            mouseDown = true;
            Thread.Sleep(MouseDownDelayMs);

            // 中間ステップで滑らかにドラッグ
            for (int i = 1; i <= IntermediateSteps; i++)
            {
                int x = fromX + ((toX - fromX) * i / IntermediateSteps);
                Mouse.Position = new System.Drawing.Point(x, y);
                Thread.Sleep(IntermediateStepDelayMs);
            }

            Thread.Sleep(PreReleaseDelayMs);
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 5)
        {
            throw new SkipException("SendInput 拒否環境のためスキップ");
        }
        finally
        {
            if (mouseDown)
            {
                try { Mouse.Up(MouseButton.Left); } catch { /* finally では握り潰す */ }
                Thread.Sleep(PostReleaseDelayMs);
            }
        }
    }
}

using System.ComponentModel;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class KeyboardInputHelper
{
    private const int FocusDelayMs       = 80;
    private const int AfterModifierDelayMs = 40;
    private const int AfterTypeDelayMs   = 40;
    private const int PostEnterDelayMs   = 150;

    /// <summary>
    /// TextBox にフォーカスし、内容を完全に置換した上で Enter を送る。
    /// SendInput 拒否環境では SkipException をスローする。
    /// </summary>
    public static void ReplaceTextAndEnterOrSkip(TextBox textBox, string text)
    {
        try
        {
            textBox.Focus();
            Thread.Sleep(FocusDelayMs);

            // Ctrl+A で全選択 → Delete でクリア
            Keyboard.Pressing(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_A);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(AfterModifierDelayMs);
            Keyboard.Type(VirtualKeyShort.DELETE);
            Thread.Sleep(AfterModifierDelayMs);

            // 文字列入力
            Keyboard.Type(text);
            Thread.Sleep(AfterTypeDelayMs);

            // 確定
            Keyboard.Type(VirtualKeyShort.RETURN);
            Thread.Sleep(PostEnterDelayMs);
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 5)
        {
            throw new SkipException("SendInput 拒否環境のためスキップ");
        }
    }
}

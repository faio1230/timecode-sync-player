using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.6 R-8: 全画面を出す前に確認を出すかの判定。</summary>
public class FullscreenConfirmationPolicyTests
{
    private static readonly DisplayTarget Primary =
        new(@"\\.\DISPLAY1", new DisplayBounds(0, 0, 1920, 1080), IsPrimary: true);

    private static readonly DisplayTarget Secondary =
        new(@"\\.\DISPLAY2", new DisplayBounds(1920, 0, 1920, 1080), IsPrimary: false);

    [Fact]
    public void ShouldConfirm_PrimaryByUserButton_ReturnsTrue()
    {
        FullscreenConfirmationPolicy.ShouldConfirm(Primary, FullscreenRequestOrigin.UserButton)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldConfirm_NonPrimaryByUserButton_ReturnsFalse()
    {
        FullscreenConfirmationPolicy.ShouldConfirm(Secondary, FullscreenRequestOrigin.UserButton)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldConfirm_SingleDisplay_SelectsPrimaryAndConfirms()
    {
        // 画面が 1 枚だけなら、選ばれるのはその主画面で、全画面は作業中の画面を覆うので確認する。
        DisplayTarget? selected = DisplaySelectionPolicy.Select([Primary], savedDeviceName: null);

        selected.Should().Be(Primary);
        FullscreenConfirmationPolicy.ShouldConfirm(selected, FullscreenRequestOrigin.UserButton)
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldConfirm_SavedSecondaryAmongTwo_DoesNotConfirm()
    {
        DisplayTarget? selected = DisplaySelectionPolicy.Select([Primary, Secondary], Secondary.DeviceName);

        FullscreenConfirmationPolicy.ShouldConfirm(selected, FullscreenRequestOrigin.UserButton)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldConfirm_NotByUser_NeverConfirms(bool isPrimary)
    {
        // 起動時の引数・プロジェクトの読み込み・設定の復元・自動の再全画面化は、無人の立ち上げを止めないため確認しない。
        DisplayTarget target = isPrimary ? Primary : Secondary;

        FullscreenConfirmationPolicy.ShouldConfirm(target, FullscreenRequestOrigin.Automatic)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldConfirm_NoTarget_ReturnsFalse()
    {
        FullscreenConfirmationPolicy.ShouldConfirm(null, FullscreenRequestOrigin.UserButton)
            .Should().BeFalse();
    }

    [Fact]
    public void ConfirmDialogXaml_DefaultAndEscapeAreCancel()
    {
        // 既定のボタン（Enter）と Esc は「やめる」。「出す」は既定にしない。「今後表示しない」は付けない。
        string xaml = File.ReadAllText(Path.Combine(SourceRoot(), "FullscreenConfirmDialog.xaml"));

        string cancel = Regex.Match(xaml, "<Button x:Name=\"BtnCancel\"[^>]*>", RegexOptions.Singleline).Value;
        string show = Regex.Match(xaml, "<Button x:Name=\"BtnShow\"[^>]*>", RegexOptions.Singleline).Value;
        cancel.Should().Contain("Content=\"やめる\"").And.Contain("IsDefault=\"True\"").And.Contain("IsCancel=\"True\"");
        show.Should().Contain("Content=\"出す\"").And.NotContain("IsDefault").And.NotContain("IsCancel");
        xaml.Should().Contain("Esc").And.Contain("プライマリ").And.NotContain("CheckBox");
    }

    private static string SourceRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TimecodeSyncPlayer"));
}

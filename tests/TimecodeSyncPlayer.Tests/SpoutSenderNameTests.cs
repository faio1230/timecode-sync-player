using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.6 R-11: Spout の送信名の決め方（1 つの純粋関数）と、出力の欄の状態の文字。
/// 環境変数を書き換える試験は、同じ変数を触る GstBackendStateTests の側に置く（並列で走らせない）。
/// </summary>
public class SpoutSenderNameTests
{
    [Theory]
    [InlineData(null, "TimecodeSyncPlayer")]
    [InlineData("", "TimecodeSyncPlayer")]
    [InlineData("   ", "TimecodeSyncPlayer")]
    [InlineData("\t \r\n", "TimecodeSyncPlayer")]
    [InlineData("  Stage A  ", "Stage A")]
    [InlineData("\tStageB\n", "StageB")]
    [InlineData("StageB", "StageB")]
    [InlineData("Stage  B", "Stage  B")]
    public void ResolveSenderName_DefaultsAndTrims(string? configured, string expected)
    {
        SpoutDefaults.ResolveSenderName(configured).Should().Be(expected);
    }

    [Fact]
    public void ResolveSenderName_NameWithoutOuterWhitespace_IsUnchanged()
    {
        // 前後に空白が無い名前は、以前の 2 か所のどちらの決め方とも同じ値になる（送る名前は変わらない）。
        foreach (string name in new[] { "TimecodeSyncPlayer", "TCS-Main", "日本語の名前", "a b c" })
        {
            SpoutDefaults.ResolveSenderName(name).Should().Be(name);
            LegacyOutputEngineSide(name).Should().Be(name);
            LegacyShimSide(name).Should().Be(name);
        }
    }

    [Fact]
    public void ResolveSenderName_OuterWhitespace_NowMatchesTheShimSide()
    {
        // 以前は OutputEngine 側（実際に Spout で送る側）だけが Trim しておらず、前後の空白ごと送っていた。
        const string padded = "  Stage A ";
        LegacyOutputEngineSide(padded).Should().Be("  Stage A ");
        LegacyShimSide(padded).Should().Be("Stage A");
        SpoutDefaults.ResolveSenderName(padded).Should().Be("Stage A");
    }

    [Fact]
    public void EnvironmentVariableName_IsSharedByBothSides()
    {
        SpoutDefaults.SenderNameEnvironmentVariable.Should().Be("TIMECODE_SYNC_PLAYER_SPOUT_NAME");
        TimecodeSyncPlayer.Gst.GstBackendState.SenderNameEnvVar.Should().Be(SpoutDefaults.SenderNameEnvironmentVariable);
        TimecodeSyncPlayer.Output.SpoutSender.SenderNameEnvironmentVariable.Should().Be(SpoutDefaults.SenderNameEnvironmentVariable);
    }

    [Fact]
    public void BothSides_UseTheSameFunction()
    {
        string mainWindow = ReadSource("MainWindow.xaml.cs");
        string backend = ReadSource("Gst", "GstBackendState.cs");

        mainWindow.Should().Contain("SpoutDefaults.SenderNameFromEnvironment()",
            "OutputEngine に渡す送信名は共通の関数で決める");
        mainWindow.Should().Contain("SenderName = _spoutSenderName,");
        mainWindow.Should().NotContain("SenderNameEnvironmentVariable",
            "MainWindow で環境変数を直接読まない");
        backend.Should().Contain("ResolveSenderName() => SpoutDefaults.SenderNameFromEnvironment();",
            "shim に渡す送信名も同じ関数で決める");
        backend.Should().NotContain("GetEnvironmentVariable(SenderNameEnvVar)");
    }

    [Fact]
    public void StatusText_OnShowsName_OffIsEmpty()
    {
        SpoutStatusFormatter.Format(true, "TimecodeSyncPlayer").Should().Be("Spout: TimecodeSyncPlayer");
        SpoutStatusFormatter.Format(true, "Stage A").Should().Be("Spout: Stage A");
        SpoutStatusFormatter.Format(false, "TimecodeSyncPlayer").Should().BeEmpty();
    }

    [Fact]
    public void ViewModel_SpoutStatus_FollowsToggleAndNotifies()
    {
        var vm = new OutputControlViewModel();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SpoutStatusText.Should().BeEmpty("起動時は Spout OFF");

        vm.SetSpoutStatus(true, "TimecodeSyncPlayer");
        vm.SpoutStatusText.Should().Be("Spout: TimecodeSyncPlayer");

        vm.SetSpoutStatus(false, "TimecodeSyncPlayer");
        vm.SpoutStatusText.Should().BeEmpty();

        changed.Should().Equal(nameof(OutputControlViewModel.SpoutStatusText), nameof(OutputControlViewModel.SpoutStatusText));

        vm.SetSpoutStatus(false, "TimecodeSyncPlayer");
        changed.Should().HaveCount(2, "値が変わらなければ知らせない");
    }

    [Fact]
    public void Xaml_StatusTextIsBoundAndHiddenWhenEmpty()
    {
        string xaml = ReadSource("MainWindow.xaml");
        int at = xaml.IndexOf("x:Name=\"SpoutStatusText\"", StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        string block = xaml.Substring(at, xaml.IndexOf("</TextBlock>", at, StringComparison.Ordinal) - at);
        block.Should().Contain("Text=\"{Binding Output.SpoutStatusText}\"");
        block.Should().Contain("AutomationProperties.AutomationId=\"SpoutStatusText\"");
        block.Should().Contain("Foreground=\"#56D364\"", "ON のボタンの緑にそろえる");
        block.Should().Contain("ToolTip=");
        block.Should().Contain("<DataTrigger Binding=\"{Binding Output.SpoutStatusText}\" Value=\"\">");

        string code = ReadSource("MainWindow.xaml.cs");
        code.Should().Contain("_vm.Output.SetSpoutStatus(_spoutOutput.IsEnabled, _spoutSenderName);");
    }

    // 以前の 2 か所の決め方（比較のためだけに写す）。
    private static string LegacyOutputEngineSide(string? fromEnv) =>
        string.IsNullOrWhiteSpace(fromEnv) ? SpoutDefaults.DefaultSenderName : fromEnv;

    private static string LegacyShimSide(string? fromEnv) =>
        string.IsNullOrWhiteSpace(fromEnv) ? SpoutDefaults.DefaultSenderName : fromEnv.Trim();

    private static string ReadSource(params string[] parts)
    {
        string path = Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TimecodeSyncPlayer" }.Concat(parts).ToArray()));
        return File.ReadAllText(path);
    }
}

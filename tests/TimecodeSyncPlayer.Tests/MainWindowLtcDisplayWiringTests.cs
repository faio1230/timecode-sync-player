using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class MainWindowLtcDisplayWiringTests
{
    [Fact]
    public void DeviceEnumerationFailure_UpdatesBackingFormatBeforeRefresh_AndPersistsAcrossTickRefresh()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "TimecodeSyncPlayer", "MainWindow.xaml.cs"));
        string source = File.ReadAllText(sourcePath);
        Match refreshMethod = Regex.Match(
            source,
            @"private void RefreshLtcDevices\(\).*?catch \(Exception ex\)\s*\{(?<catchBody>.*?)\}\s*finally",
            RegexOptions.Singleline);

        refreshMethod.Success.Should().BeTrue();
        string catchBody = refreshMethod.Groups["catchBody"].Value;
        int backingAssignment = catchBody.IndexOf(
            "_lastLtcFormatText = \"LTC デバイス列挙失敗\";",
            StringComparison.Ordinal);
        int refreshCall = catchBody.IndexOf("RefreshLtcDisplayState();", StringComparison.Ordinal);

        backingAssignment.Should().BeGreaterThanOrEqualTo(0);
        refreshCall.Should().BeGreaterThan(backingAssignment);
        catchBody.Should().NotContain("_vm.Sync.LtcFormatText =");

        const string lastFormatText = "LTC デバイス列挙失敗";
        LtcDisplayState initialRefresh = LtcDisplayStateFormatter.Format(
            isMonitoring: false,
            isSignalLost: false,
            lastFormatText);
        LtcDisplayState tickRefresh = LtcDisplayStateFormatter.Format(
            isMonitoring: false,
            isSignalLost: false,
            lastFormatText);

        initialRefresh.FormatText.Should().Be(lastFormatText);
        tickRefresh.FormatText.Should().Be(lastFormatText);
    }
}

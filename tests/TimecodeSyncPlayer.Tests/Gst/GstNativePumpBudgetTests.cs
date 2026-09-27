using System.Globalization;
using FluentAssertions;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

/// <summary>
/// v0.5.4 K3 (3): アプリは起動（shim の DLL ロード）前に、明示が無ければ shim のポンプの予算
/// （着地の時間切れ − 0.5 秒）を TCS_PUMP_BUDGET_MS で渡す。明示があればそれを優先する
/// （shim は DLL ロード時に 1 回だけ読む。native/gst-shim/src/tcs_gstreamer.cpp:253-274）。
/// </summary>
public class GstNativePumpBudgetTests
{
    [Fact]
    public void ConfigurePumpBudget_SetsTheDerivedBudgetWhenUnset()
    {
        string? previous = Environment.GetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable, null);

            GstNativeLibraryResolver.ConfigurePumpBudget();

            Environment.GetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable)
                .Should().Be(SeekTimeBudget.PumpBudgetMilliseconds.ToString(CultureInfo.InvariantCulture),
                    "明示が無ければ「着地の時間切れ − 0.5 秒」を渡す");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void ConfigurePumpBudget_DoesNotOverrideUserSetting()
    {
        string? previous = Environment.GetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable, "800");

            GstNativeLibraryResolver.ConfigurePumpBudget();

            Environment.GetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable)
                .Should().Be("800", "明示された環境変数を優先する（E2E の上書きを保つ）");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SeekTimeBudget.PumpBudgetEnvironmentVariable, previous);
        }
    }
}

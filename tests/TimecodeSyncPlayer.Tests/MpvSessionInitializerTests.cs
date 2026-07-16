using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class MpvSessionInitializerTests
{
    private static readonly IntPtr s_mpv = new(321);

    [Fact]
    public void Initialize_ReturnsFailure_WhenCreateReturnsZero()
    {
        var api = new FakeMpvApi { CreateResult = IntPtr.Zero };
        var initializer = new MpvSessionInitializer(api, new MpvStartupPropertyApplier(api));

        MpvSessionInitializationResult result = initializer.Initialize();

        result.Success.Should().BeFalse();
        result.Failure.Should().Be(MpvSessionInitializationFailure.CreateFailed);
        result.Mpv.Should().Be(IntPtr.Zero);
        api.Calls.Should().Equal("create");
    }

    [Fact]
    public void Initialize_AppliesStartupPropertiesBeforeInitialize()
    {
        var api = new FakeMpvApi { CreateResult = s_mpv, InitializeResult = 0 };
        var initializer = new MpvSessionInitializer(api, new MpvStartupPropertyApplier(api));

        MpvSessionInitializationResult result = initializer.Initialize();

        result.Success.Should().BeTrue();
        result.Mpv.Should().Be(s_mpv);
        api.Calls.Should().NotContain(call => call.StartsWith("terminate-destroy:", StringComparison.Ordinal));
        api.Calls[0].Should().Be("create");
        api.Calls[1].Should().StartWith("set:");
        api.Calls.Should().Contain("set:osd-level=1");
        api.Calls.Last().Should().Be("initialize");
    }

    [Fact]
    public void Initialize_WhenDebugOsdIsEnabled_AppliesLevelThree()
    {
        var api = new FakeMpvApi { CreateResult = s_mpv, InitializeResult = 0 };
        var initializer = new MpvSessionInitializer(api, new MpvStartupPropertyApplier(api));

        initializer.Initialize(showDebugOsd: true);

        api.Calls.Should().Contain("set:osd-level=3");
    }

    [Fact]
    public void Initialize_WhenInitializeFailsDestroysHandleAndReturnsFailure()
    {
        var api = new FakeMpvApi { CreateResult = s_mpv, InitializeResult = -1 };
        var initializer = new MpvSessionInitializer(api, new MpvStartupPropertyApplier(api));

        MpvSessionInitializationResult result = initializer.Initialize();

        result.Success.Should().BeFalse();
        result.Failure.Should().Be(MpvSessionInitializationFailure.InitializeFailed);
        result.Mpv.Should().Be(IntPtr.Zero);
        api.Calls.Last().Should().Be($"terminate-destroy:{s_mpv}");
        api.Calls.Should().ContainInOrder("create", "initialize", $"terminate-destroy:{s_mpv}");
    }

    private sealed class FakeMpvApi : IMpvApi
    {
        public IntPtr CreateResult { get; init; }
        public int InitializeResult { get; init; }
        public List<string> Calls { get; } = [];

        public IntPtr Create()
        {
            Calls.Add("create");
            return CreateResult;
        }

        public int Initialize(IntPtr ctx)
        {
            Calls.Add("initialize");
            return InitializeResult;
        }

        public void TerminateDestroy(IntPtr ctx) => Calls.Add($"terminate-destroy:{ctx}");
        public int SetPropertyString(IntPtr ctx, string name, string value)
        {
            Calls.Add($"set:{name}={value}");
            return 0;
        }

        public int GetProperty(IntPtr ctx, string name, int format, out double result)
        {
            result = 0;
            return -1;
        }

        public string GetPropertyString(IntPtr ctx, string name) => "";
        public int CommandString(IntPtr ctx, string args) => 0;
        public void Free(IntPtr data) { }
        public int FormatDouble => 4;
    }
}

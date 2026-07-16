using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SpoutOutputTests
{
    private sealed class FakeNativeApi : ISpoutNativeApi
    {
        public IntPtr Object { get; set; } = new(42);
        public bool OpenResult { get; set; } = true;
        public bool SetNameResult { get; set; } = true;
        public bool SendResult { get; set; } = true;
        public Exception? ValidateException { get; set; }
        public Exception? CreateException { get; set; }
        public Exception? SendException { get; set; }
        public Exception? ReleaseException { get; set; }
        public Exception? DestroyException { get; set; }
        public List<string> Calls { get; } = [];
        public List<(IntPtr Self, IntPtr Pixels, uint Width, uint Height, uint Pitch)> SentImages { get; } = [];

        public void ValidateObjectSize()
        {
            Calls.Add("Validate");
            if (ValidateException != null) throw ValidateException;
        }

        public IntPtr Create()
        {
            Calls.Add("Create");
            if (CreateException != null) throw CreateException;
            return Object;
        }

        public bool OpenDirectX11(IntPtr self, IntPtr device)
        {
            Calls.Add($"Open:{self}:{device}");
            return OpenResult;
        }

        public bool SetSenderName(IntPtr self, string name)
        {
            Calls.Add($"SetName:{self}:{name}");
            return SetNameResult;
        }

        public bool SendImage(IntPtr self, IntPtr pixels, uint width, uint height, uint pitch)
        {
            Calls.Add("Send");
            if (SendException != null) throw SendException;
            SentImages.Add((self, pixels, width, height, pitch));
            return SendResult;
        }

        public void ReleaseSender(IntPtr self)
        {
            Calls.Add($"Release:{self}");
            if (ReleaseException != null) throw ReleaseException;
        }

        public void Destroy(IntPtr self)
        {
            Calls.Add($"Destroy:{self}");
            if (DestroyException != null) throw DestroyException;
        }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(1280, 5120)]
    [InlineData(1920, 7680)]
    public void GetTightlyPackedBgraPitch_ReturnsWidthTimesFour(int width, uint expected)
    {
        SpoutOutput.GetTightlyPackedBgraPitch(width).Should().Be(expected);
    }

    [Fact]
    public void TryInitialize_SuccessCallsNativeInOrderAndBecomesAvailable()
    {
        var native = new FakeNativeApi();
        using var output = new SpoutOutput(native);

        bool first = output.TryInitialize();
        bool second = output.TryInitialize();

        first.Should().BeTrue();
        second.Should().BeTrue();
        output.IsAvailable.Should().BeTrue();
        native.Calls.Take(5).Should().Equal(
            "Validate",
            "Create",
            $"Open:{native.Object}:0",
            $"SetName:{native.Object}:{SpoutOutput.DefaultSenderName}",
            "Validate");
    }

    [Fact]
    public void TryInitialize_CreateReturnsZeroReturnsFalseWithoutCleanup()
    {
        var native = new FakeNativeApi { Object = IntPtr.Zero };
        using var output = new SpoutOutput(native);

        output.TryInitialize().Should().BeFalse();

        output.IsAvailable.Should().BeFalse();
        native.Calls.Should().Equal("Validate", "Create");
    }

    [Fact]
    public void TryInitialize_OpenFailureDestroysObjectAndReturnsFalse()
    {
        var native = new FakeNativeApi { OpenResult = false };
        using var output = new SpoutOutput(native);

        output.TryInitialize().Should().BeFalse();

        output.IsAvailable.Should().BeFalse();
        native.Calls.Should().Equal(
            "Validate", "Create", $"Open:{native.Object}:0", $"Destroy:{native.Object}");
    }

    [Fact]
    public void TryInitialize_SetNameFailureDestroysObjectAndReturnsFalse()
    {
        var native = new FakeNativeApi { SetNameResult = false };
        using var output = new SpoutOutput(native);

        output.TryInitialize().Should().BeFalse();

        output.IsAvailable.Should().BeFalse();
        native.Calls.Should().Equal(
            "Validate",
            "Create",
            $"Open:{native.Object}:0",
            $"SetName:{native.Object}:{SpoutOutput.DefaultSenderName}",
            $"Destroy:{native.Object}");
    }

    [Fact]
    public void TryInitialize_DllNotFoundCleansUpObjectAndReturnsFalse()
    {
        var native = new FakeNativeApi { CreateException = new DllNotFoundException("missing") };
        using var output = new SpoutOutput(native);

        output.TryInitialize().Should().BeFalse();

        output.IsAvailable.Should().BeFalse();
        native.Calls.Should().Equal("Validate", "Create");
    }

    [Fact]
    public void TryInitialize_OpenFailureAndDestroyExceptionReturnsFalse()
    {
        var native = new FakeNativeApi();
        native.SetNameResult = true;
        native.DestroyException = new InvalidOperationException("destroy failed");
        native.OpenResult = false;
        using var output = new SpoutOutput(native);

        output.TryInitialize().Should().BeFalse();

        native.Calls.Should().Contain($"Destroy:{native.Object}");
    }

    [Fact]
    public void TryInitialize_AfterDisposeValidatesThenReturnsFalse()
    {
        var native = new FakeNativeApi();
        var output = new SpoutOutput(native);
        output.Dispose();
        native.Calls.Clear();

        output.TryInitialize().Should().BeFalse();

        native.Calls.Should().Equal("Validate");
    }

    [Theory]
    [InlineData(false, true, 1, 2, 2)]
    [InlineData(true, false, 1, 2, 2)]
    [InlineData(true, true, 0, 2, 2)]
    [InlineData(true, true, 1, 0, 2)]
    [InlineData(true, true, 1, 2, 0)]
    public void SendFrame_InvalidStateOrArgumentsDoesNotCallNative(
        bool enabled,
        bool initialize,
        long pixels,
        int width,
        int height)
    {
        var native = new FakeNativeApi();
        using var output = new SpoutOutput(native) { IsEnabled = enabled };
        if (initialize) output.TryInitialize().Should().BeTrue();
        native.Calls.Clear();

        output.SendFrame(new IntPtr(pixels), width, height);

        native.SentImages.Should().BeEmpty();
        native.Calls.Should().BeEmpty();
    }

    [Fact]
    public void SendFrame_ValidFramePassesObjectPixelsDimensionsAndPitch()
    {
        var native = new FakeNativeApi();
        using var output = CreateInitializedEnabledOutput(native);
        var pixels = new IntPtr(1234);

        output.SendFrame(pixels, 1920, 1080);

        native.SentImages.Should().ContainSingle().Which.Should().Be(
            (native.Object, pixels, 1920u, 1080u, 7680u));
    }

    [Fact]
    public void SendFrame_NativeFalseInvalidatesOutputAndTryInitializeCanRecover()
    {
        var native = new FakeNativeApi { SendResult = false };
        using var output = CreateInitializedEnabledOutput(native);

        output.SendFrame(new IntPtr(1), 2, 3);

        output.IsAvailable.Should().BeFalse();
        output.IsEnabled.Should().BeFalse();

        native.SendResult = true;
        output.TryInitialize().Should().BeTrue();
        output.IsAvailable.Should().BeTrue();
        output.IsEnabled.Should().BeFalse("reinitialization must not override the disabled safety state");

        output.IsEnabled = true;
        output.SendFrame(new IntPtr(2), 4, 5);
        native.SentImages.Should().HaveCount(2);
    }

    [Fact]
    public void SendFrame_NativeExceptionIsSwallowed()
    {
        var native = new FakeNativeApi { SendException = new InvalidOperationException("send failed") };
        using var output = CreateInitializedEnabledOutput(native);

        Action act = () => output.SendFrame(new IntPtr(1), 2, 3);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_InitializedOutputReleasesThenDestroysAndIsIdempotent()
    {
        var native = new FakeNativeApi();
        var output = new SpoutOutput(native);
        output.TryInitialize().Should().BeTrue();
        native.Calls.Clear();

        output.Dispose();
        output.Dispose();

        native.Calls.Should().Equal($"Release:{native.Object}", $"Destroy:{native.Object}");
    }

    [Fact]
    public void Dispose_ReleaseAndDestroyExceptionsAreSwallowed()
    {
        var native = new FakeNativeApi
        {
            ReleaseException = new InvalidOperationException("release failed"),
            DestroyException = new InvalidOperationException("destroy failed")
        };
        var output = new SpoutOutput(native);
        output.TryInitialize().Should().BeTrue();

        Action act = output.Dispose;

        act.Should().NotThrow();
        native.Calls.Should().Contain($"Release:{native.Object}");
        native.Calls.Should().Contain($"Destroy:{native.Object}");
    }

    private static SpoutOutput CreateInitializedEnabledOutput(FakeNativeApi native)
    {
        var output = new SpoutOutput(native) { IsEnabled = true };
        output.TryInitialize().Should().BeTrue();
        native.Calls.Clear();
        return output;
    }
}

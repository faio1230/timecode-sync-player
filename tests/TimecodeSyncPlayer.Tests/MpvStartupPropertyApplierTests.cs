using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class MpvStartupPropertyApplierTests
{
    [Fact]
    public void Apply_SetsStartupPropertiesInOrder()
    {
        var api = new FakeMpvApi();
        var applier = new MpvStartupPropertyApplier(api);
        var mpv = new IntPtr(123);

        applier.Apply(mpv);

        api.SetProperties.Should().Equal(
            ("vo", "libmpv"),
            ("hwdec", "auto-copy"),
            ("keep-open", "always"),
            ("pause", "yes"),
            ("osd-level", "3"),
            ("osd-font-size", "20"),
            ("osd-bar", "yes"),
            ("osd-color", "#FFFFFF"),
            ("osd-border-color", "#000000"),
            ("osd-border-size", "2"));
    }

    private sealed class FakeMpvApi : IMpvApi
    {
        public List<(string Name, string Value)> SetProperties { get; } = [];

        public IntPtr Create() => IntPtr.Zero;
        public int Initialize(IntPtr ctx) => 0;
        public void TerminateDestroy(IntPtr ctx) { }
        public int SetPropertyString(IntPtr ctx, string name, string value)
        {
            SetProperties.Add((name, value));
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

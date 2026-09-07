namespace TimecodeSyncPlayer.Tests;

// WPF resource packaging is process-wide; real Window construction must not race
// across dispatcher threads. Tests using only isolated rendering buffers stay parallel.
[CollectionDefinition("WpfWindow", DisableParallelization = true)]
public sealed class WpfWindowCollection
{
}

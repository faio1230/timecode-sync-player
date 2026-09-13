namespace TimecodeSyncPlayer.Tests;

// OutputTrace.Current is a process-wide static that tests replace while recording.
// Replacing it while another test records redirects the events into the wrong trace,
// so classes that swap it must not run in parallel with each other (C1 rework).
[CollectionDefinition("OutputTrace", DisableParallelization = true)]
public sealed class OutputTraceCollection
{
}

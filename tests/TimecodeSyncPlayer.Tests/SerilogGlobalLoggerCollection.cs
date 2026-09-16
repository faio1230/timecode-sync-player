namespace TimecodeSyncPlayer.Tests;

// U1: Serilog の Log.Logger はプロセス共通で、テストが一時的に差し替えて捕捉する。
// 差し替え中に別のテストが並行実行されると、捕捉先や復元がずれるため直列化する。
[CollectionDefinition("Serilog global logger", DisableParallelization = true)]
public sealed class SerilogGlobalLoggerCollection
{
}

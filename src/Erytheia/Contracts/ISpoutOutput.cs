namespace Erytheia;

public interface ISpoutOutput : IDisposable
{
    bool IsEnabled { get; set; }
    bool IsAvailable { get; }
    bool TryInitialize();
}

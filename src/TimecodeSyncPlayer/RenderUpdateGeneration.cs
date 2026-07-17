namespace TimecodeSyncPlayer;

internal sealed class RenderUpdateGeneration
{
    private int _value;

    public int Capture() => Volatile.Read(ref _value);

    public void Advance() => Interlocked.Increment(ref _value);

    public bool IsCurrent(int generation) => generation == Volatile.Read(ref _value);
}

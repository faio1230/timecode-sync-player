namespace TimecodeSyncPlayer.Contracts;

public interface ITimecodeFpsSelector
{
    double Resolve(TimecodeFpsMode mode, double detectedFps, bool dropFrame);
    void Reset();
}

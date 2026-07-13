namespace TimecodeSyncPlayer;

public interface IRenderUpdateScheduler
{
    bool RequestDispatch();
    bool CompleteDispatch();
    RenderUpdateSchedulerStats ConsumeStats();
    void Reset();
}

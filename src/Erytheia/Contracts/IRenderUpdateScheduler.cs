namespace Erytheia;

public interface IRenderUpdateScheduler
{
    bool RequestDispatch();
    bool CompleteDispatch();
    RenderUpdateSchedulerStats ConsumeStats();
    void Reset();
}

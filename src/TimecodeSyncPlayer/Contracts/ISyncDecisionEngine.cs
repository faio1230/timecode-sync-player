namespace TimecodeSyncPlayer;

public interface ISyncDecisionEngine
{
    SyncDecision Decide(double ltcSeconds, SyncPlaybackState state);
}

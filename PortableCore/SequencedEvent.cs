namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>
/// Reliable event envelope. Consumers process an event at most once for a session
/// and level generation.
/// </summary>
internal sealed record SequencedEvent<TPayload>(
    Guid SessionId,
    int LevelGeneration,
    long Sequence,
    int SenderPlayerId,
    TPayload Payload)
{
    public bool IsNewerThan(long lastAppliedSequence) => Sequence > lastAppliedSequence;
}

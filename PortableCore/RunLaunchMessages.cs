namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>Client validation result for a host run commit.</summary>
internal sealed record RunLaunchAck(
    Guid SessionId,
    long RunId,
    int Sequence,
    bool Accepted,
    string Error);

/// <summary>Host permission for both peers to invoke the loader for a committed launch.</summary>
internal sealed record RunLaunchExecute(
    Guid SessionId,
    long RunId,
    int Sequence);

/// <summary>
/// Client confirmation that it has received the execute, holds the authoritative seed, and has
/// queued the identical native launch on its game thread. The host must not begin native loading
/// until this arrives, which guarantees both loaders consume the same seed and configuration.
/// </summary>
internal sealed record RunLaunchQueued(
    Guid SessionId,
    long RunId,
    int Sequence,
    bool Queued,
    string Error);

/// <summary>Peer notification that the local hero and level are ready for gameplay.</summary>
internal sealed record RunLevelReady(
    Guid SessionId,
    long RunId,
    int Sequence,
    string LevelId);

/// <summary>Host cancellation for a committed launch that was not executed successfully.</summary>
internal sealed record RunLaunchCancel(
    Guid SessionId,
    long RunId,
    int Sequence,
    string Reason);

namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>Immutable read model for session consumers that must not access the coordinator internals.</summary>
internal readonly record struct CoopSessionSnapshot(
    CoopSessionPhase Phase,
    long TransitionSequence,
    string LastReason);

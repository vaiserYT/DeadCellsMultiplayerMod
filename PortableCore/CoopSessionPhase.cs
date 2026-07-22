namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>
/// Transport- and game-independent lifecycle for one cooperative session.
/// </summary>
internal enum CoopSessionPhase
{
    Disconnected = 0,
    Lobby = 1,
    LaunchCommitted = 2,
    LoadingLevel = 3,
    Playing = 4,
    TransitionCommitted = 5,
    RunEnded = 6,
    Faulted = 7,
}

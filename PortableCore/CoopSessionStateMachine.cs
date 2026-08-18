namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>
/// Minimal deterministic session state machine. Game-specific side effects belong
/// in an integration adapter and happen only after a transition is accepted.
/// </summary>
internal sealed class CoopSessionStateMachine
{
    private static readonly IReadOnlyDictionary<CoopSessionPhase, HashSet<CoopSessionPhase>> Allowed =
        new Dictionary<CoopSessionPhase, HashSet<CoopSessionPhase>>
        {
            [CoopSessionPhase.Disconnected] = [CoopSessionPhase.Lobby],
            [CoopSessionPhase.Lobby] = [CoopSessionPhase.LaunchCommitted, CoopSessionPhase.Disconnected, CoopSessionPhase.Faulted],
            [CoopSessionPhase.LaunchCommitted] = [CoopSessionPhase.LoadingLevel, CoopSessionPhase.Lobby, CoopSessionPhase.Faulted],
            [CoopSessionPhase.LoadingLevel] = [CoopSessionPhase.Playing, CoopSessionPhase.TransitionCommitted, CoopSessionPhase.Lobby, CoopSessionPhase.Faulted, CoopSessionPhase.Disconnected],
            [CoopSessionPhase.Playing] = [CoopSessionPhase.TransitionCommitted, CoopSessionPhase.RunEnded, CoopSessionPhase.Faulted, CoopSessionPhase.Disconnected],
            [CoopSessionPhase.TransitionCommitted] = [CoopSessionPhase.LoadingLevel, CoopSessionPhase.Lobby, CoopSessionPhase.RunEnded, CoopSessionPhase.Faulted],
            [CoopSessionPhase.RunEnded] = [CoopSessionPhase.Lobby, CoopSessionPhase.Disconnected],
            [CoopSessionPhase.Faulted] = [CoopSessionPhase.Lobby, CoopSessionPhase.Disconnected],
        };

    public CoopSessionPhase Phase { get; private set; } = CoopSessionPhase.Disconnected;
    public long TransitionSequence { get; private set; }
    public string LastReason { get; private set; } = string.Empty;
    public CoopSessionSnapshot Snapshot => new(Phase, TransitionSequence, LastReason);

    public bool TryTransition(
        CoopSessionPhase next,
        long sequence,
        string reason,
        out string error)
    {
        if (sequence <= TransitionSequence)
        {
            error = $"Stale transition sequence {sequence}; current is {TransitionSequence}.";
            return false;
        }

        if (!Allowed.TryGetValue(Phase, out var nextPhases) || !nextPhases.Contains(next))
        {
            error = $"Illegal co-op session transition {Phase} -> {next}.";
            return false;
        }

        Phase = next;
        TransitionSequence = sequence;
        LastReason = reason ?? string.Empty;
        error = string.Empty;
        return true;
    }
}

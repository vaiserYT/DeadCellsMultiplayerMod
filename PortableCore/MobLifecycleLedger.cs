namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>
/// Small allocation-free-per-operation lifecycle ledger for host-authoritative mob deaths.
/// Callers serialize access with their existing synchronization gate.
/// </summary>
internal sealed class MobLifecycleLedger
{
    private readonly Dictionary<long, MobLifecycleState> _states = new();

    public bool IsDead(int generation, int netId)
    {
        return netId > 0 &&
               _states.TryGetValue(MakeKey(generation, netId), out var state) &&
               state == MobLifecycleState.Dead;
    }

    public void MarkActive(int generation, int netId)
    {
        if (netId <= 0)
            return;

        var key = MakeKey(generation, netId);
        if (!_states.TryGetValue(key, out var state) || state != MobLifecycleState.Dead)
            _states[key] = MobLifecycleState.Active;
    }

    public void MarkDead(int generation, int netId)
    {
        if (netId > 0)
            _states[MakeKey(generation, netId)] = MobLifecycleState.Dead;
    }

    public void Clear() => _states.Clear();

    private static long MakeKey(int generation, int netId) =>
        ((long)generation << 32) ^ (uint)netId;
}

internal enum MobLifecycleState
{
    Active = 1,
    Dead = 2
}

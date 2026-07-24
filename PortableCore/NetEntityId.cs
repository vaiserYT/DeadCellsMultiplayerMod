namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>
/// Stable identity assigned by the host authority when an entity is spawned.
/// Runtime object addresses, native game ids, list indexes, names, and positions are not identities.
/// Wire traffic uses a compact int NetId + level generation; this struct is the conceptual form
/// (generation + spawn sequence + archetype) used by host registry bookkeeping.
/// </summary>
internal readonly record struct NetEntityId(
    int LevelGeneration,
    long SpawnSequence,
    string Archetype)
{
    public bool IsValid =>
        LevelGeneration > 0 &&
        SpawnSequence > 0 &&
        !string.IsNullOrWhiteSpace(Archetype);

    public override string ToString() =>
        $"{LevelGeneration}:{SpawnSequence}:{Archetype}";
}

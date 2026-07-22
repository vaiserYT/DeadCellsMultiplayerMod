namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>
/// Stable identity assigned by the authority when an entity is spawned.
/// Runtime object addresses, list indexes, names, and positions are not identities.
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

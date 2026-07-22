namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>
/// Host-authored description of one run launch. A client must validate this descriptor and
/// receive the matching execute message before it invokes any game loader.
///
/// Authoritative generation input is the <see cref="RunSeed"/> (and, for Boss Rush,
/// <see cref="BossRushSeed"/>, which today equals the run seed): in Dead Cells the boss order,
/// arena order and run modifiers are all deterministic functions of that seed, so sharing and
/// validating the seed is what actually keeps generation in lockstep. The per-level generation
/// seed is synchronized separately through the dedicated SEED / level-seed path, not here.
///
/// Only values that are NOT derivable from the seed are carried as real, runtime-sourced fields
/// and cross-checked:
///   * <see cref="Route"/> — the native Boss Rush variant read from the door (BossRushDoor.bossRushType).
///   * <see cref="TargetArena"/> — the concrete entry arena id.
///   * <see cref="Difficulty"/>/<see cref="BossCells"/> — the active boss-cell count (also synced via BossRune).
///
/// Honesty note (do not treat these as "synchronized"): <see cref="DlcFlags"/>,
/// <see cref="Modifiers"/>, <see cref="BossRushTier"/> and <see cref="BossSequence"/> are RESERVED.
/// The current runtime binding does not expose an authoritative source for them here, so they are
/// left at 0/empty and are NOT independently validated. DLC-ownership divergence between peers is a
/// known, un-detected gap; it is not papered over with a placeholder that pretends to be in sync.
/// </summary>
internal sealed record RunLaunchDescriptor(
    Guid SessionId,
    long RunId,
    int Sequence,
    int ProtocolVersion,
    int RunSeed,
    string LaunchKind,
    string InitialLevelId,
    double? InitialLevelSeed,
    int Difficulty,
    int BossCells,
    bool BossRush,
    ulong DlcFlags,
    int SaveSlot,
    // --- Protocol 17 generation fields (field order is wire-significant; do not reorder) ---
    int BossRushSeed,   // real: authoritative Boss Rush seed (== RunSeed today)
    int LevelGenSeed,   // RESERVED: level-gen seed uses the dedicated SEED sync, not this field
    int BossRushTier,   // RESERVED: no authoritative runtime source bound today
    string Route,       // real: BossRushDoor.bossRushType (native Boss Rush variant)
    string BossSequence,// RESERVED: derives from the seed; not independently transmitted
    ulong Modifiers,    // RESERVED: derives from the seed; not independently transmitted
    string TargetArena) // real: concrete entry arena id
{
    public bool TryValidate(out string error)
    {
        if (SessionId == Guid.Empty)
            return Fail("SessionId is empty.", out error);
        if (RunId <= 0)
            return Fail("RunId must be positive.", out error);
        if (Sequence <= 0)
            return Fail("Sequence must be positive.", out error);
        if (ProtocolVersion <= 0)
            return Fail("ProtocolVersion must be positive.", out error);
        if (RunSeed <= 0)
            return Fail("RunSeed must be positive.", out error);
        if (string.IsNullOrWhiteSpace(LaunchKind))
            return Fail("LaunchKind is required.", out error);
        if (string.IsNullOrWhiteSpace(InitialLevelId))
            return Fail("InitialLevelId is required.", out error);
        if (InitialLevelSeed.HasValue &&
            (double.IsNaN(InitialLevelSeed.Value) || double.IsInfinity(InitialLevelSeed.Value)))
        {
            return Fail("InitialLevelSeed is invalid.", out error);
        }
        if (Difficulty < 0)
            return Fail("Difficulty cannot be negative.", out error);
        if (BossCells < 0)
            return Fail("BossCells cannot be negative.", out error);
        if (SaveSlot < 0)
            return Fail("SaveSlot cannot be negative.", out error);

        // Protocol 17 fields. Optional values default to 0/"" and must not be null; a Boss Rush
        // launch must carry a positive authoritative Boss Rush seed so the client can never fall
        // back to a locally generated run.
        if (BossRushSeed < 0)
            return Fail("BossRushSeed cannot be negative.", out error);
        if (LevelGenSeed < 0)
            return Fail("LevelGenSeed cannot be negative.", out error);
        if (BossRushTier < 0)
            return Fail("BossRushTier cannot be negative.", out error);
        if (Route == null)
            return Fail("Route must not be null.", out error);
        if (BossSequence == null)
            return Fail("BossSequence must not be null.", out error);
        if (TargetArena == null)
            return Fail("TargetArena must not be null.", out error);
        if (BossRush && BossRushSeed <= 0)
            return Fail("BossRush launch requires a positive BossRushSeed.", out error);

        error = string.Empty;
        return true;
    }

    public bool HasSameIdentity(RunLaunchDescriptor? other)
    {
        return other != null &&
               SessionId == other.SessionId &&
               RunId == other.RunId &&
               Sequence == other.Sequence;
    }

    /// <summary>The arena the encounter should begin in; falls back to the initial level id.</summary>
    public string EffectiveTargetArena =>
        string.IsNullOrWhiteSpace(TargetArena) ? InitialLevelId : TargetArena;

    /// <summary>Single-line summary for structured diagnostics (no per-frame use).</summary>
    public string ToDiagnosticString() =>
        $"session={SessionId} run={RunId} seq={Sequence} proto={ProtocolVersion} " +
        $"kind={LaunchKind} bossRush={BossRush} runSeed={RunSeed} brSeed={BossRushSeed} " +
        $"lvlSeed={LevelGenSeed} tier={BossRushTier} diff={Difficulty} bc={BossCells} " +
        $"dlc={DlcFlags} mods={Modifiers} save={SaveSlot} level={InitialLevelId} arena={EffectiveTargetArena} " +
        $"route='{Route}' bosses='{BossSequence}'";

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

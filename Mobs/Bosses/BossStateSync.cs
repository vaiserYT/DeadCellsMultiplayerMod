using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using dc.en;
using dc.en.mob;
using dc.en.mob.boss;

namespace DeadCellsMultiplayerMod.Mobs.Bosses;

public static class BossStateSync
{
    private const string BossMarker = "bb:1";
    private const string PhasePrefix = "bp:";
    private const string ActionPrefix = "ba:";
    private const string EmergedPrefix = "bemg:";
    // NOTE: there are deliberately no "bproj:" / "btent:" / "barena:" payload fields.
    //
    // Boss-owned entities — Conjunctivius' tentacles, Death's scythe, spawner projectiles and the
    // arena objects a fight creates — are NOT special-cased here. They are ordinary Mobs in
    // dc.en.mob.*, so IsSyncMobByType already admits them to the normal authoritative registry and
    // they inherit the full pipeline: a host-assigned sync id, MOBREG (re-armed for runtime spawns
    // by QueueInitialMobSync), authoritative HP, authoritative death with tombstone resends, and
    // ScanHostBossPartDespawns for parts that vanish without dying. Duplicating that state into the
    // boss payload would create a second, competing description of the same entities.
    //
    // This file therefore carries only state that belongs to the boss ENTITY ITSELF and has no
    // entity of its own: phase, action and emergence.
    // Phase 2: stable host-assigned boss identity token. Carried alongside the boss marker so the
    // client can rebind the boss by identity across native phase/proxy rebuilds and sync-id churn
    // instead of guessing by proximity. Absent (id <= 0) => pre-Phase-2 behaviour.
    private const string EntityIdPrefix = "bid:";

    public static string AppendBossState(string basePayload, Mob mob, int bossEntityId = 0)
    {
        if (mob == null)
            return basePayload ?? string.Empty;

        if (!BossSyncHelpers.IsBossMob(mob))
            return basePayload ?? string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(basePayload))
            parts.Add(basePayload);

        // Every full boss state carries an explicit marker.  Client registries can then recover the
        // authoritative boss after a native phase/proxy rebuild without guessing from coordinates
        // or requiring the client's (potentially stale) HP to match the host first.
        parts.Add(BossMarker);

        if (bossEntityId > 0)
            parts.Add(EntityIdPrefix + bossEntityId.ToString(CultureInfo.InvariantCulture));

        if (mob is GardenerBoss gardener)
        {
            try
            {
                var phase = gardener.phase;
                parts.Add(PhasePrefix + phase.ToString(CultureInfo.InvariantCulture));

                try
                {
                    var idx = (int)gardener.action.Index;
                    parts.Add(ActionPrefix + idx.ToString(CultureInfo.InvariantCulture));
                }
                catch
                {
                    // action may be unset
                }
            }
            catch
            {
                // ignore
            }
        }
        else if (mob is Collector collector)
        {
            try
            {
                var phase = collector.phase;
                parts.Add(PhasePrefix + phase.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // ignore
            }
        }
        else
        {
            // Generic boss phase/action sync for every other boss type.
            try
            {
                var phase = GetBossPhase(mob);
                if (phase.HasValue)
                    parts.Add(PhasePrefix + phase.Value.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // ignore
            }

            try
            {
                var action = GetBossActionIndex(mob);
                if (action.HasValue)
                    parts.Add(ActionPrefix + action.Value.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                // ignore
            }

            // Mama Tick's emergence is a separate boolean from her level-up stage: while buried she
            // is a different encounter shape entirely. The verified proxy member is isEmerged - an
            // "emerge" member has never existed on the type, so the previous Mama Tick special case
            // read nothing and wrote nothing, and her phase never crossed the wire at all.
            try
            {
                if (BossReflection.TryReadMember(mob, "isEmerged") is bool emerged)
                    parts.Add(EmergedPrefix + (emerged ? "1" : "0"));
            }
            catch
            {
                // ignore
            }
        }

        // Boss-specific arena state that has no entity of its own (Conjunctivius vulnerability and
        // arena platforms). Appended last so it never interferes with the generic fields above.
        BeholderArenaSync.AppendState(parts, mob);

        return parts.Count == 0 ? (basePayload ?? string.Empty) : string.Join(".", parts);
    }

    public static bool IsBossStatePayload(string? wirePayload)
    {
        if (string.IsNullOrWhiteSpace(wirePayload))
            return false;

        var payload = wirePayload;
        try { payload = Uri.UnescapeDataString(payload); } catch { }

        var parts = payload.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i]?.Trim(), BossMarker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts the stable host-assigned boss identity ("bid:") from a boss state payload.
    /// Returns 0 when no identity is present (a peer that never stamped one, or a non-boss payload).
    /// </summary>
    public static int TryGetEntityId(string? wirePayload)
    {
        if (string.IsNullOrWhiteSpace(wirePayload))
            return 0;

        var payload = wirePayload;
        try { payload = Uri.UnescapeDataString(payload); } catch { }

        var parts = payload.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in parts)
        {
            var t = token?.Trim();
            if (string.IsNullOrEmpty(t) || !t.StartsWith(EntityIdPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var s = t[EntityIdPrefix.Length..].Trim();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
                return id;
        }

        return 0;
    }

    public static void ApplyBossStateFromPayload(Mob mob, string? payload)
    {
        // While a boss-intro or phase-transition cinematic runs locally, the cine owns the
        // boss: forcing host actions/phase here (e.g. DookuBeast state onto a still-phase-1
        // Dooku) is a hard-crash vector. Full state sync resumes when the cine ends.
        if (DeadCellsMultiplayerMod.ModEntry.IsLocalBossIntroCineActive())
            return;

        if (mob == null || mob.destroyed || string.IsNullOrWhiteSpace(payload))
            return;

        int? phaseVal = null;
        int? actionVal = null;
        bool? emergedVal = null;
        bool? vulnerableVal = null;
        bool? platformsVal = null;

        var parts = payload.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in parts)
        {
            var t = token?.Trim();
            if (string.IsNullOrEmpty(t))
                continue;

            if (t.StartsWith(BeholderArenaSync.VulnerablePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var s = t[BeholderArenaSync.VulnerablePrefix.Length..].Trim();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    vulnerableVal = v != 0;
            }
            else if (t.StartsWith(BeholderArenaSync.PlatformsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var s = t[BeholderArenaSync.PlatformsPrefix.Length..].Trim();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                    platformsVal = p != 0;
            }
            else if (t.StartsWith(EmergedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var s = t[EmergedPrefix.Length..].Trim();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
                    emergedVal = e != 0;
            }
            else if (t.StartsWith(PhasePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var s = t[PhasePrefix.Length..].Trim();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                    phaseVal = p;
            }
            else if (t.StartsWith(ActionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var s = t[ActionPrefix.Length..].Trim();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
                    actionVal = a;
            }
        }

        if (mob is GardenerBoss gardener)
        {
            try
            {
                if (phaseVal.HasValue)
                {
#pragma warning disable CS8604, CS8625 // Gardener phase/action are Haxe-bound; compare via runtime equality
                    var currentPhase = gardener.phase;
                    if (!Equals(currentPhase, phaseVal.Value))
                        gardener.phase = phaseVal.Value;
#pragma warning restore CS8604, CS8625
                }

                if (actionVal.HasValue)
                {
                    var currentAction = gardener.action;
                    var currentActionIndex = TryGetBossActionIndex(currentAction);
                    if (!currentActionIndex.HasValue || currentActionIndex.Value != actionVal.GetValueOrDefault())
                    {
                        BossAction? newAction = CreateBossActionByIndex(actionVal.Value);
                        if (newAction is not null)
                            gardener.action = newAction;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
        else if (mob is Collector collector && phaseVal.HasValue)
        {
            try
            {
                var currentPhase = collector.phase;
                if (currentPhase != phaseVal.Value)
                    collector.phase = phaseVal.Value;
            }
            catch
            {
                // ignore
            }
        }
        else
        {
            // Generic boss phase/action application for every other boss type.
            if (phaseVal.HasValue)
            {
                try
                {
                    // Compare against the last phase this replica actually consumed, not against a
                    // fresh read. For bosses whose phase member is read-only to us (bossLevel), a
                    // re-read still reports the old value after we decline to write it, so a
                    // re-read comparison would treat every single state packet as a fresh phase
                    // change and interrupt the boss's skills continuously - which looks exactly
                    // like a boss that never attacks on the client.
                    var isFirstObservation = !TryGetLastAppliedBossPhase(mob, out var before);
                    if (isFirstObservation || before != phaseVal.Value)
                    {
                        SetBossPhase(mob, phaseVal.Value);
                        SetLastAppliedBossPhase(mob, phaseVal.Value);

                        // A phase CHANGE means the host's boss switched behaviour (e.g.
                        // Conjunctivius entering the shield/tentacle stage). The client brain is
                        // locked, so a stale looping action (poison orb barrage) survives the
                        // switch unless it is interrupted here. Alive bosses only — interrupting
                        // a dying boss breaks its native death sequence and stalls the victory
                        // cinematic. The first observation only seeds the baseline: there is no
                        // transition to react to, and interrupting there would cancel whatever the
                        // replica legitimately started before the first boss state packet landed.
                        bool aliveForInterrupt;
                        try { aliveForInterrupt = !mob.destroyed && mob.life > 0; }
                        catch { aliveForInterrupt = false; }
                        if (!isFirstObservation && aliveForInterrupt)
                            BossReflection.TryInterruptMobSkills(mob);
                        BossSyncDiag.Trace("generic boss phase applied phase={Phase} type={Type}", phaseVal.Value, mob.GetType().Name);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (actionVal.HasValue)
            {
                try
                {
                    SetBossAction(mob, actionVal.Value);
                }
                catch
                {
                    // ignore
                }
            }

            if (emergedVal.HasValue)
            {
                try
                {
                    if (BossReflection.TryReadMember(mob, "isEmerged") is bool localEmerged &&
                        localEmerged != emergedVal.Value)
                    {
                        BossReflection.TryWriteMember(mob, "isEmerged", emergedVal.Value);
                        BossSyncDiag.Trace(
                            "boss emerged applied emerged={Emerged} type={Type}",
                            emergedVal.Value,
                            mob.GetType().Name);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        // Conjunctivius vulnerability / arena platforms. Applied for every boss type branch above,
        // because it is keyed on the concrete Beholder type and is a no-op for anything else.
        BeholderArenaSync.ApplyState(mob, vulnerableVal, platformsVal);

        // Boss-owned entities (tentacles, projectiles, arena parts) are intentionally not applied
        // here — see the note on the payload prefixes above: they are synchronized as ordinary
        // authoritative registry mobs, not as fields of the boss payload.
    }

    private static int? TryGetBossActionIndex(BossAction? action)
    {
        if (action == null)
            return null;

        try
        {
            return (int)action.Index;
        }
        catch
        {
            return null;
        }
    }

    private static BossAction? CreateBossActionByIndex(int index)
    {
        return index switch
        {
            (int)BossAction.Indexes.Idle => new BossAction.Idle(),
            (int)BossAction.Indexes.Run => new BossAction.Run(),
            (int)BossAction.Indexes.Walk => new BossAction.Walk(),
            (int)BossAction.Indexes.Fall => new BossAction.Fall(),
            (int)BossAction.Indexes.Attack => new BossAction.Attack(),
            (int)BossAction.Indexes.Hoe => new BossAction.Hoe(),
            (int)BossAction.Indexes.PitchFork => new BossAction.PitchFork(),
            (int)BossAction.Indexes.Sickles => new BossAction.Sickles(),
            (int)BossAction.Indexes.SicklesStun => new BossAction.SicklesStun(),
            (int)BossAction.Indexes.Shovel => new BossAction.Shovel(),
            (int)BossAction.Indexes.ShovelAtk => new BossAction.ShovelAtk(),
            (int)BossAction.Indexes.ShovelUp => new BossAction.ShovelUp(),
            (int)BossAction.Indexes.ShovelAppear => new BossAction.ShovelAppear(),
            (int)BossAction.Indexes.ShovelDisappear => new BossAction.ShovelDisappear(),
            (int)BossAction.Indexes.Vine => new BossAction.Vine(),
            (int)BossAction.Indexes.Spore => new BossAction.Spore(),
            (int)BossAction.Indexes.JumpLoad => new BossAction.JumpLoad(),
            (int)BossAction.Indexes.Jump => new BossAction.Jump(),
            (int)BossAction.Indexes.Land => new BossAction.Land(),
            (int)BossAction.Indexes.Dashing => new BossAction.Dashing(),
            (int)BossAction.Indexes.DigUp => new BossAction.DigUp(),
            (int)BossAction.Indexes.DigDown => new BossAction.DigDown(),
            (int)BossAction.Indexes.Stun => new BossAction.Stun(),
            _ => null
        };
    }
    
    // Helper methods for entity access
    private static double GetWorldX(Mob mob)
    {
        try { return (mob.cx + mob.xr) * 24.0; } catch { return 0.0; }
    }
    
    private static double GetWorldY(Mob mob)
    {
        try { return (mob.cy + mob.yr) * 24.0; } catch { return 0.0; }
    }
    
    // Generic boss phase/action helpers for boss types without a typed branch.
    //
    // NOTE: these previously used GetField() only, which returns null on Haxe proxy classes
    // (proxy fields are C# properties) — so phase/action sync was a silent no-op for every
    // boss without a typed branch, Conjunctivius included. BossReflection fixed the lookup
    // mechanism, but the member NAMES were still wrong for almost every boss.
    //
    // Verified against dc.en.mob.Boss and all 14 of its subclasses in GameProxy:
    //   phase       -> Collector, GardenerBoss only
    //   combatPhase -> KingsHand
    //   bossLevel   -> dc.en.mob.Boss base, so every boss (the vanilla level-up stage)
    //   state       -> matched NO boss type; it was dead weight
    // Without combatPhase/bossLevel the generic path transmitted nothing for 12 of 14 bosses.
    private static readonly string[] BossPhaseReadMemberNames = { "phase", "combatPhase", "bossLevel" };

    // Writes are deliberately narrower than reads. phase/combatPhase are plain behaviour ints, but
    // bossLevel is the counter vanilla itself compares against levelUpSteps to fire a level-up:
    // writing it on a replica risks running a second, local phase transition, which is precisely
    // the "client advances the boss on its own" failure this sync exists to prevent. Reading it is
    // still worthwhile — it is what lets the client notice the host's phase change and drop a stale
    // looping attack.
    private static readonly string[] BossPhaseWriteMemberNames = { "phase", "combatPhase" };

    // Action member is also per-boss: GardenerBoss/KingsHand use action, Dooku and DookuBeast use
    // curAction, Death uses currentAction.
    private static readonly string[] BossActionMemberNames = { "action", "curAction", "currentAction" };

    private static readonly ConditionalWeakTable<Mob, StrongBox<int>> LastAppliedBossPhase = new();

    private static bool TryGetLastAppliedBossPhase(Mob mob, out int phase)
    {
        phase = 0;
        if (mob == null || !LastAppliedBossPhase.TryGetValue(mob, out var box) || box == null)
            return false;

        phase = box.Value;
        return true;
    }

    private static void SetLastAppliedBossPhase(Mob mob, int phase)
    {
        if (mob == null)
            return;

        LastAppliedBossPhase.Remove(mob);
        LastAppliedBossPhase.Add(mob, new StrongBox<int>(phase));
    }

    private static int? GetBossPhase(Mob mob)
    {
        if (mob == null)
            return null;

        for (var i = 0; i < BossPhaseReadMemberNames.Length; i++)
        {
            var value = BossReflection.TryReadInt(mob, BossPhaseReadMemberNames[i]);
            if (value.HasValue)
                return value;
        }

        return null;
    }

    private static object? TryGetBossActionMember(Mob mob)
    {
        for (var i = 0; i < BossActionMemberNames.Length; i++)
        {
            var action = BossReflection.TryReadMember(mob, BossActionMemberNames[i]);
            if (action != null)
                return action;
        }

        return null;
    }

    private static int? GetBossActionIndex(Mob mob)
    {
        if (mob == null)
            return null;

        var action = TryGetBossActionMember(mob);
        if (action == null)
            return null;

        return BossReflection.TryReadInt(action, "Index");
    }

    private static void SetBossPhase(Mob mob, int phase)
    {
        if (mob == null)
            return;

        for (var i = 0; i < BossPhaseWriteMemberNames.Length; i++)
        {
            if (BossReflection.TryWriteMember(mob, BossPhaseWriteMemberNames[i], phase))
                return;
        }
    }

    private static void SetBossAction(Mob mob, int actionIndex)
    {
        if (mob == null)
            return;

        var action = TryGetBossActionMember(mob);
        if (action != null)
            BossReflection.TryWriteMember(action, "Index", actionIndex);
    }
}

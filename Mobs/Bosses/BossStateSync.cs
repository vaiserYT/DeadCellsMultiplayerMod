using System.Globalization;
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
    private const string ProjectilePrefix = "bproj:";
    private const string TentaclePrefix = "btent:";
    private const string ArenaPrefix = "barena:";
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
        else if (mob.GetType().Name.Contains("MamaTick", StringComparison.OrdinalIgnoreCase))
        {
            // Mama Tick specific sync - it uses different state fields
            try
            {
                // Try to access emerge state or similar field
                var emergeValueObj = BossReflection.TryReadMember(mob, "emerge");
                if (emergeValueObj is bool emergeValue)
                    parts.Add(PhasePrefix + (emergeValue ? "1" : "0"));
            }
            catch
            {
                // Fallback to generic phase
                try
                {
                    var phase = GetBossPhase(mob);
                    if (phase.HasValue)
                        parts.Add(PhasePrefix + phase.Value.ToString(CultureInfo.InvariantCulture));
                }
                catch { }
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
        }
        else
        {
            // Generic boss phase/action sync for other boss types
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
        }
        
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

        var parts = payload.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in parts)
        {
            var t = token?.Trim();
            if (string.IsNullOrEmpty(t))
                continue;

            if (t.StartsWith(PhasePrefix, StringComparison.OrdinalIgnoreCase))
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
        else if (mob.GetType().Name.Contains("MamaTick", StringComparison.OrdinalIgnoreCase))
        {
            // Mama Tick specific state application
            if (phaseVal.HasValue)
            {
                try
                {
                    // Try to set emerge state
                    if (!BossReflection.TryWriteMember(mob, "emerge", phaseVal.Value != 0))
                    {
                        // Fallback to generic phase
                        SetBossPhase(mob, phaseVal.Value);
                    }
                }
                catch
                {
                    // Fallback to generic phase
                    try
                    {
                        SetBossPhase(mob, phaseVal.Value);
                    }
                    catch { }
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
        }
        else
        {
            // Generic boss phase/action application for other boss types
            if (phaseVal.HasValue)
            {
                try
                {
                    var before = GetBossPhase(mob);
                    if (!before.HasValue || before.Value != phaseVal.Value)
                    {
                        SetBossPhase(mob, phaseVal.Value);
                        // A phase change means the host's boss switched behaviour (e.g.
                        // Conjunctivius entering the shield/tentacle stage). The client brain is
                        // locked, so a stale looping action (poison orb barrage) survives the
                        // switch unless it is interrupted here. Alive bosses only — interrupting
                        // a dying boss breaks its native death sequence and stalls the victory
                        // cinematic.
                        bool aliveForInterrupt;
                        try { aliveForInterrupt = !mob.destroyed && mob.life > 0; }
                        catch { aliveForInterrupt = false; }
                        if (aliveForInterrupt)
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
        }
        
        // Note: Boss-owned entity sync (projectiles, tentacles) requires additional type resolution
        // and is deferred to avoid compilation issues with Entity type references.
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
    
    // Generic boss phase/action helpers for unsupported boss types.
    // NOTE: these previously used GetField() only, which returns null on Haxe proxy classes
    // (proxy fields are C# properties) — so phase/action sync was a silent no-op for every
    // boss without a typed branch, Conjunctivius included. BossReflection fixes that.
    private static int? GetBossPhase(Mob mob)
    {
        if (mob == null)
            return null;

        return BossReflection.TryReadInt(mob, "phase") ?? BossReflection.TryReadInt(mob, "state");
    }

    private static int? GetBossActionIndex(Mob mob)
    {
        if (mob == null)
            return null;

        var action = BossReflection.TryReadMember(mob, "action");
        if (action == null)
            return null;

        return BossReflection.TryReadInt(action, "Index");
    }

    private static void SetBossPhase(Mob mob, int phase)
    {
        if (mob == null)
            return;

        if (!BossReflection.TryWriteMember(mob, "phase", phase))
            BossReflection.TryWriteMember(mob, "state", phase);
    }

    private static void SetBossAction(Mob mob, int actionIndex)
    {
        if (mob == null)
            return;

        var action = BossReflection.TryReadMember(mob, "action");
        if (action != null)
            BossReflection.TryWriteMember(action, "Index", actionIndex);
    }
}

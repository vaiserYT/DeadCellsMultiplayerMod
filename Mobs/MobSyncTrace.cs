using System;
using System.Collections.Generic;
using DeadCellsMultiplayerMod;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization;

/// <summary>Opt-in verbose tracing for mob sync (env DCCM_MOB_SYNC_TRACE=1 or debug settings).</summary>
internal static class MobSyncTrace
{
    private static readonly bool EnvTraceEnabled = string.Equals(
        Environment.GetEnvironmentVariable("DCCM_MOB_SYNC_TRACE"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool EnvAssertEnabled = string.Equals(
        Environment.GetEnvironmentVariable("DCCM_MOB_SYNC_ASSERT"),
        "1",
        StringComparison.Ordinal);

    public static bool Enabled => EnvTraceEnabled || MultiplayerSettingsStorage.DebugMobsSyncTrace;
    public static bool AssertEnabled => EnvAssertEnabled || MultiplayerSettingsStorage.DebugMobsSyncTrace;

    public static void LogSendStatesBatch(string role, IReadOnlyList<NetNode.MobStateSnapshot> states)
    {
        if (!Enabled || states == null || states.Count == 0)
            return;

        MinMaxSyncId(states, static s => s.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] → SEND states {Role} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            role,
            states.Count,
            minId,
            maxId);
    }

    public static void LogSendDrawBatch(string role, IReadOnlyList<NetNode.MobDraw> draws)
    {
        if (!Enabled || draws == null || draws.Count == 0)
            return;

        MinMaxSyncId(draws, static d => d.MobIndex, out var minId, out var maxId);
        Log.Information(
            "[MobSync] → SEND draws {Role} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            role,
            draws.Count,
            minId,
            maxId);
    }

    public static void LogSendMovesBatch(string role, IReadOnlyList<NetNode.MobMoveSnapshot> moves)
    {
        if (!Enabled || moves == null || moves.Count == 0)
            return;

        MinMaxSyncId(moves, static m => m.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] в†’ SEND moves {Role} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            role,
            moves.Count,
            minId,
            maxId);
    }

    public static void LogRecvStates(string context, IReadOnlyList<NetNode.MobStateSnapshot> states)
    {
        if (!Enabled || states == null || states.Count == 0)
            return;

        MinMaxSyncId(states, static s => s.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV states {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            states.Count,
            minId,
            maxId);
    }

    public static void LogRecvAttacks(string context, IReadOnlyList<NetNode.MobAttack> attacks)
    {
        if (!Enabled || attacks == null || attacks.Count == 0)
            return;

        MinMaxSyncId(attacks, static a => a.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV attacks {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            attacks.Count,
            minId,
            maxId);
    }

    public static void LogRecvMoves(string context, IReadOnlyList<NetNode.MobMoveSnapshot> moves)
    {
        if (!Enabled || moves == null || moves.Count == 0)
            return;

        MinMaxSyncId(moves, static m => m.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] в†ђ RECV moves {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            moves.Count,
            minId,
            maxId);
    }

    public static void LogRecvDraws(string context, IReadOnlyList<NetNode.MobDraw> draws)
    {
        if (!Enabled || draws == null || draws.Count == 0)
            return;

        MinMaxSyncId(draws, static d => d.MobIndex, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV draws {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            draws.Count,
            minId,
            maxId);
    }

    public static void LogRecvHits(string context, IReadOnlyList<NetNode.MobHit> hits)
    {
        if (!Enabled || hits == null || hits.Count == 0)
            return;

        MinMaxSyncId(hits, static h => h.MobIndex, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV hits {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            hits.Count,
            minId,
            maxId);
    }

    public static void LogRecvDies(string context, IReadOnlyList<NetNode.MobDie> dies)
    {
        if (!Enabled || dies == null || dies.Count == 0)
            return;

        MinMaxSyncId(dies, static d => d.MobIndex, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV dies {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            dies.Count,
            minId,
            maxId);
    }

    public static void LogSendMobEvents(string role, IReadOnlyList<NetNode.MobEventUpdate> updates)
    {
        if (!Enabled || updates == null || updates.Count == 0)
            return;

        for (var i = 0; i < updates.Count; i++)
        {
            var u = updates[i];
            var events = u.Events;
            var summary = SummarizeEvents(events);
            Log.Information(
                "[MobSync] → SEND mobEvent {Role} syncId={SyncId} x={X} y={Y} dir={Dir} type={MobType} events={EventSummary}",
                role,
                u.Index,
                u.X,
                u.Y,
                u.Dir,
                u.Type ?? string.Empty,
                summary);
        }
    }

    public static void LogRegisterTracked(string role, int syncId, int localIndex, string mobType)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] ◎ REGISTER mob tracked {Role} syncId={SyncId} localIndex={LocalIndex} type={MobType}",
            role,
            syncId,
            localIndex,
            mobType ?? string.Empty);
    }

    public static void LogBindSyncId(string reason, int syncId, string mobType, double x, double y)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] ◎ BIND syncId reason={Reason} syncId={SyncId} type={MobType} x={X} y={Y}",
            reason,
            syncId,
            mobType ?? string.Empty,
            x,
            y);
    }

    public static void LogRegistryRebuild(
        string role,
        string levelId,
        int trackedBefore,
        int trackedAfter,
        int registryCount,
        int minSyncId,
        int maxSyncId,
        int nextRuntimeSyncId,
        int generation,
        int identityToken)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] REBUILD role={Role} level={LevelId} trackedBefore={TrackedBefore} trackedAfter={TrackedAfter} registryCount={RegistryCount} minSyncId={MinSyncId} maxSyncId={MaxSyncId} nextRuntimeSyncId={NextRuntimeSyncId} generation={Generation} identityToken={IdentityToken}",
            role ?? string.Empty,
            levelId ?? string.Empty,
            trackedBefore,
            trackedAfter,
            registryCount,
            minSyncId,
            maxSyncId,
            nextRuntimeSyncId,
            generation,
            identityToken);
    }

    public static void LogLevelReset(string reason, string levelId, int trackedBefore)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] RESET reason={Reason} level={LevelId} trackedBefore={TrackedBefore}",
            reason ?? string.Empty,
            levelId ?? string.Empty,
            trackedBefore);
    }

    public static void LogEntitiesPostCreateHookEntered(
        string role,
        string levelId,
        string levelKey,
        int entityCount,
        int trackedBefore,
        string currentLevelKey,
        bool identityReady,
        int currentIdentityToken,
        string lastResetReason)
    {
        Log.Information(
            "[MobSync] entitiesPostCreate hook entered level={LevelId} levelRef={LevelRef} role={Role} entityCount={EntityCount} trackedBefore={TrackedBefore} currentLevelRef={CurrentLevelRef} identityReady={IdentityReady} currentIdentityToken={CurrentIdentityToken} lastResetReason={LastResetReason}",
            levelId ?? string.Empty,
            levelKey ?? string.Empty,
            role ?? string.Empty,
            entityCount,
            trackedBefore,
            currentLevelKey ?? string.Empty,
            identityReady,
            currentIdentityToken,
            lastResetReason ?? string.Empty);
    }

    public static void LogEntitiesPostCreateDuplicateIgnored(
        string role,
        string levelId,
        string levelKey,
        int entityCount,
        int trackedCurrent,
        int candidateIdentityToken,
        string currentLevelKey)
    {
        Log.Information(
            "[MobSync] entitiesPostCreate duplicate ignored reason=committed_identity_duplicate level={LevelId} levelRef={LevelRef} role={Role} entityCount={EntityCount} trackedCurrent={TrackedCurrent} candidateIdentityToken={CandidateIdentityToken} currentLevelRef={CurrentLevelRef}",
            levelId ?? string.Empty,
            levelKey ?? string.Empty,
            role ?? string.Empty,
            entityCount,
            trackedCurrent,
            candidateIdentityToken,
            currentLevelKey ?? string.Empty);
    }

    public static void LogRebuildCandidate(
        string role,
        string levelId,
        string levelKey,
        int entityCount,
        int candidateTracked,
        int candidateIdentityToken,
        int trackedBefore,
        int currentIdentityToken,
        string currentLevelKey,
        string lastResetLevelKey,
        int lastResetTrackedCount,
        int lastResetIdentityToken,
        string lastCommittedLevelKey,
        int lastCommittedTrackedCount,
        int lastCommittedIdentityToken,
        string lastResetReason)
    {
        Log.Information(
            "[MobSync] rebuild candidate entityCount={EntityCount} candidateTracked={CandidateTracked} level={LevelId} levelRef={LevelRef} role={Role} trackedBefore={TrackedBefore} candidateIdentityToken={CandidateIdentityToken} currentIdentityToken={CurrentIdentityToken} currentLevelRef={CurrentLevelRef} lastResetLevelRef={LastResetLevelRef} lastResetTracked={LastResetTracked} lastResetIdentityToken={LastResetIdentityToken} lastCommittedLevelRef={LastCommittedLevelRef} lastCommittedTracked={LastCommittedTracked} lastCommittedIdentityToken={LastCommittedIdentityToken} lastResetReason={LastResetReason}",
            entityCount,
            candidateTracked,
            levelId ?? string.Empty,
            levelKey ?? string.Empty,
            role ?? string.Empty,
            trackedBefore,
            candidateIdentityToken,
            currentIdentityToken,
            currentLevelKey ?? string.Empty,
            lastResetLevelKey ?? string.Empty,
            lastResetTrackedCount,
            lastResetIdentityToken,
            lastCommittedLevelKey ?? string.Empty,
            lastCommittedTrackedCount,
            lastCommittedIdentityToken,
            lastResetReason ?? string.Empty);
    }

    public static void LogRebuildDecision(
        string role,
        string levelId,
        string levelKey,
        string decision,
        string reason,
        int trackedBefore,
        int trackedAfter,
        int entityCount,
        int candidateTracked,
        int baselineTrackedCount,
        string baselineSource,
        bool currentIdentityReady,
        int currentIdentityToken,
        int candidateIdentityToken,
        string currentLevelKey,
        string lastResetLevelKey,
        string lastCommittedLevelKey,
        string lastResetReason)
    {
        Log.Information(
            "[MobSync] rebuild decision {Decision} reason={Reason} level={LevelId} levelRef={LevelRef} role={Role} trackedBefore={TrackedBefore} trackedAfter={TrackedAfter} entityCount={EntityCount} candidateTracked={CandidateTracked} baselineTracked={BaselineTracked} baselineSource={BaselineSource} currentIdentityReady={CurrentIdentityReady} currentIdentityToken={CurrentIdentityToken} candidateIdentityToken={CandidateIdentityToken} currentLevelRef={CurrentLevelRef} lastResetLevelRef={LastResetLevelRef} lastCommittedLevelRef={LastCommittedLevelRef} lastResetReason={LastResetReason}",
            decision ?? string.Empty,
            reason ?? string.Empty,
            levelId ?? string.Empty,
            levelKey ?? string.Empty,
            role ?? string.Empty,
            trackedBefore,
            trackedAfter,
            entityCount,
            candidateTracked,
            baselineTrackedCount,
            baselineSource ?? string.Empty,
            currentIdentityReady,
            currentIdentityToken,
            candidateIdentityToken,
            currentLevelKey ?? string.Empty,
            lastResetLevelKey ?? string.Empty,
            lastCommittedLevelKey ?? string.Empty,
            lastResetReason ?? string.Empty);
    }

    public static void LogRebuildCommit(
        string role,
        string levelId,
        string levelKey,
        int trackedAfter,
        int registryCount,
        int generation,
        int identityToken)
    {
        Log.Information(
            "[MobSync] rebuild commit trackedAfter={TrackedAfter} level={LevelId} levelRef={LevelRef} role={Role} registryCount={RegistryCount} generation={Generation} identityToken={IdentityToken}",
            trackedAfter,
            levelId ?? string.Empty,
            levelKey ?? string.Empty,
            role ?? string.Empty,
            registryCount,
            generation,
            identityToken);
    }

    public static void LogTrackingReset(
        string reason,
        string role,
        string levelId,
        string levelKey,
        int trackedBefore,
        bool identityReady,
        int currentIdentityToken,
        string lastResetLevelKey,
        int lastResetTrackedCount,
        int lastResetIdentityToken,
        string lastCommittedLevelKey,
        int lastCommittedTrackedCount,
        int lastCommittedIdentityToken)
    {
        Log.Information(
            "[MobSync] reset path reason={Reason} level={LevelId} levelRef={LevelRef} role={Role} trackedBefore={TrackedBefore} identityReady={IdentityReady} currentIdentityToken={CurrentIdentityToken} lastResetLevelRef={LastResetLevelRef} lastResetTracked={LastResetTracked} lastResetIdentityToken={LastResetIdentityToken} lastCommittedLevelRef={LastCommittedLevelRef} lastCommittedTracked={LastCommittedTracked} lastCommittedIdentityToken={LastCommittedIdentityToken}",
            reason ?? string.Empty,
            levelId ?? string.Empty,
            levelKey ?? string.Empty,
            role ?? string.Empty,
            trackedBefore,
            identityReady,
            currentIdentityToken,
            lastResetLevelKey ?? string.Empty,
            lastResetTrackedCount,
            lastResetIdentityToken,
            lastCommittedLevelKey ?? string.Empty,
            lastCommittedTrackedCount,
            lastCommittedIdentityToken);
    }

    public static void LogRebuildRejected(
        string reason,
        string role,
        string levelId,
        int trackedCurrent,
        int entityCount,
        int candidateTracked,
        int currentIdentityToken,
        int candidateIdentityToken)
    {
        if (string.Equals(reason, "same_identity_empty", StringComparison.Ordinal) ||
            string.Equals(reason, "replace_empty", StringComparison.Ordinal))
        {
            Log.Information(
                "[MobSync] REBUILD rejected reason={Reason} role={Role} level={LevelId} trackedCurrent={TrackedCurrent} entityCount={EntityCount} candidateTracked={CandidateTracked} currentIdentityToken={CurrentIdentityToken} candidateIdentityToken={CandidateIdentityToken}",
                reason ?? string.Empty,
                role ?? string.Empty,
                levelId ?? string.Empty,
                trackedCurrent,
                entityCount,
                candidateTracked,
                currentIdentityToken,
                candidateIdentityToken);
        }
        else
        {
            Log.Warning(
                "[MobSync] REBUILD rejected reason={Reason} role={Role} level={LevelId} trackedCurrent={TrackedCurrent} entityCount={EntityCount} candidateTracked={CandidateTracked} currentIdentityToken={CurrentIdentityToken} candidateIdentityToken={CandidateIdentityToken}",
                reason ?? string.Empty,
                role ?? string.Empty,
                levelId ?? string.Empty,
                trackedCurrent,
                entityCount,
                candidateTracked,
                currentIdentityToken,
                candidateIdentityToken);
        }
    }

    public static void LogDeferredMobRegistration(string role, string levelId, string mobType)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] REGISTER deferred role={Role} level={LevelId} type={MobType}",
            role ?? string.Empty,
            levelId ?? string.Empty,
            mobType ?? string.Empty);
    }

    public static void LogStaleTrackedMapping(int syncId, int localIndex, string reason)
    {
        Log.Warning(
            "[MobSync] stale tracked sync mapping syncId={SyncId} localIndex={LocalIndex} reason={Reason}",
            syncId,
            localIndex,
            reason ?? string.Empty);
    }

    public static void LogIncomingMappingMismatch(
        string context,
        int syncId,
        string expectedType,
        string actualType,
        string reason)
    {
        Log.Warning(
            "[MobSync] mapping mismatch context={Context} syncId={SyncId} expectedType={ExpectedType} actualType={ActualType} reason={Reason}",
            context ?? string.Empty,
            syncId,
            expectedType ?? string.Empty,
            actualType ?? string.Empty,
            reason ?? string.Empty);
    }

    public static void LogAmbiguousMatchRejected(
        string context,
        int syncId,
        string mobType,
        double x,
        double y,
        int candidateCount)
    {
        Log.Warning(
            "[MobSync] ambiguous fallback rejected context={Context} syncId={SyncId} type={MobType} x={X} y={Y} candidateCount={CandidateCount}",
            context ?? string.Empty,
            syncId,
            mobType ?? string.Empty,
            x,
            y,
            candidateCount);
    }

    public static void LogFallbackMatchResolved(
        string context,
        int syncId,
        string mobType,
        double x,
        double y,
        int candidateCount,
        bool rebound)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] fallback resolved context={Context} syncId={SyncId} type={MobType} x={X} y={Y} candidateCount={CandidateCount} rebound={Rebound}",
            context ?? string.Empty,
            syncId,
            mobType ?? string.Empty,
            x,
            y,
            candidateCount,
            rebound);
    }

    public static void LogPacketGenerationRejected(string context, int packetGeneration, int currentGeneration, int count)
    {
        Log.Warning(
            "[MobSync] packet generation rejected context={Context} packetGeneration={PacketGeneration} currentGeneration={CurrentGeneration} count={Count}",
            context ?? string.Empty,
            packetGeneration,
            currentGeneration,
            count);
    }

    public static void LogInvariantViolation(string reason, string detail)
    {
        Log.Warning(
            "[MobSync] invariant violation reason={Reason} detail={Detail}",
            reason ?? string.Empty,
            detail ?? string.Empty);
    }

    public static void LogIncomingHitApply(
        int syncId,
        int hp,
        int userId,
        bool replaySpecial,
        bool forceDie)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] ◎ APPLY hit syncId={SyncId} hp={Hp} userId={UserId} replaySpecial={ReplaySpecial} forceDie={ForceDie}",
            syncId,
            hp,
            userId,
            replaySpecial,
            forceDie);
    }

    public static void LogClientAttackRoute(string route, int syncId, string skillId)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] ◎ CLIENT attack route={Route} syncId={SyncId} skillId={SkillId}",
            route,
            syncId,
            skillId ?? string.Empty);
    }

    private static void MinMaxSyncId<T>(IReadOnlyList<T> items, Func<T, int> getIndex, out int minId, out int maxId)
    {
        minId = int.MaxValue;
        maxId = int.MinValue;
        for (var i = 0; i < items.Count; i++)
        {
            var id = getIndex(items[i]);
            if (id < minId)
                minId = id;
            if (id > maxId)
                maxId = id;
        }

        if (minId == int.MaxValue)
        {
            minId = -1;
            maxId = -1;
        }
    }

    private static string SummarizeEvents(IReadOnlyList<string>? events)
    {
        if (events == null || events.Count == 0)
            return string.Empty;

        var parts = new List<string>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            parts.Add(SummarizeOneEvent(events[i]));
        }

        return string.Join("; ", parts);
    }

    private static string SummarizeOneEvent(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "?";

        var pipe = raw.IndexOf('|', StringComparison.Ordinal);
        var head = pipe >= 0 ? raw[..pipe] : raw;
        if (string.Equals(head, "hit", StringComparison.Ordinal))
        {
            var lifePart = pipe >= 0 && pipe + 1 < raw.Length ? raw[(pipe + 1)..] : string.Empty;
            return $"hit life={lifePart}";
        }

        if (!string.Equals(head, "attack", StringComparison.Ordinal))
            return raw.Length <= 120 ? raw : raw[..117] + "...";

        var rest = pipe >= 0 && pipe + 1 < raw.Length ? raw[(pipe + 1)..] : string.Empty;
        var seg = rest.Split('|', 8);
        var skillEnc = seg.Length > 0 ? seg[0] : string.Empty;
        var skill = skillEnc;
        try
        {
            skill = Uri.UnescapeDataString(skillEnc);
        }
        catch
        {
        }

        return $"attack skill={skill}";
    }
}

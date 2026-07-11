using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using dc;
using dc.en;
using dc.h2d;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.pr;
using dc.tool;
using dc.tool.atk;
using dc.tool.skill;
using DeadCellsMultiplayerMod.Ghost;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using DeadCellsMultiplayerMod.Tools;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization :
    IOnAdvancedModuleInitializing,
    IOnFrameUpdate,
    IEventReceiver
    {

        private readonly ModEntry modEntry;
        private static bool s_eventReceiverInstalled;
        private static bool s_hooksInstalled;

        private static readonly object Sync = new();
        private static readonly List<Mob> trackedMobs = new();
        private static readonly Dictionary<Mob, int> trackedMobIndices = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<int, Mob> IdToMob = new();
        private static readonly Dictionary<Mob, int> MobToId = new(ReferenceEqualityComparer.Instance);
        private static int nextRuntimeSyncId;

        private static readonly Dictionary<Mob, ClientMobState> clientMobTargets = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, Entity?> clientCachedAttackTargetByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, int> hostLastSentContactTargetUserIdByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, string> clientQueuedOldSkillMarkers = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, int> clientLastReportedMobLife = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<int, string> clientLastSentAffectPayloadBySyncId = new();
        private static readonly Dictionary<int, ClientDrawSentState> clientLastSentDrawStateBySyncId = new();
        private static readonly Dictionary<int, string> clientLastAppliedHostAffectPayloadBySyncId = new();
        private static readonly Dictionary<int, string> hostLastAppliedClientAffectPayloadBySyncId = new();
        private static readonly Dictionary<Mob, string> clientLastAppliedAnimPayloadByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, double> clientLastAnimationApplyFrameByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, double> clientNetworkAttackStartFrame = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<string, ParsedAnimPayload> parsedAnimPayloadCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> hostMobTypeBySyncId = new();
        private static readonly Dictionary<int, HashSet<int>> hostClientInterestUsersBySyncId = new();
        private static readonly Dictionary<int, HostMobSentState> hostLastSentMobStatesBySyncId = new();
        private static readonly List<Entity> hostDetectedTargets = new();
        private static readonly List<Entity> s_clientDetectedTargetsScratch = new();
        private static readonly List<Mob> s_batchMobsScratch = new();
        private static readonly List<NetNode.MobStateSnapshot> s_batchSnapshotsScratch = new();
        private static readonly List<PendingClientAffectApply> s_clientAffectAppliesScratch = new();
        private static readonly List<PendingHostStateApply> s_hostStateAppliesScratch = new();
        private static readonly List<PendingMobHitApply> s_pendingMobHitAppliesScratch = new();
        private static readonly List<NetNode.MobHit> s_mobHitMergeScratch = new();
        private static readonly List<NetNode.MobDraw> s_drawsScratch = new();
        private static readonly List<NetNode.MobMoveSnapshot> s_moveSnapshotsScratch = new();
        private static readonly List<Mob> s_dieVictimsScratch = new();
        private static readonly HashSet<Mob> s_dieVictimDedupScratch = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<Mob> s_usedTrackedMobsScratch = new(ReferenceEqualityComparer.Instance);

        // Client-side deaths deferred because the mob is culled locally (far from the local hero,
        // vanilla never proximity-initialized it). Running vanilla onDie() on such a mob leaves a
        // half-started death sequence that vanilla update null-derefs a frame later (the
        // "Null access .cx" fatal when the other player kills a mob far away). The real death runs
        // in Hook_Mob_postUpdate once vanilla itself starts simulating the mob. Guarded by Sync.
        private static readonly HashSet<Mob> s_pendingCulledMobDeaths = new(ReferenceEqualityComparer.Instance);

        // Full mob-sync quiescence during level transitions: from door activation until the new
        // level's registry commit, NOTHING is applied to mobs and nothing is sent. The v0.8.25
        // crash log showed 3 seconds of move/state application to the old level's 75 mobs between
        // the door activation and the mid-load render fatal - mutating mobs the transition is
        // dismantling, behind a fading screen where none of it is visible anyway.
        private static long s_syncQuiescedUntilTicks;

        internal static void QuiesceForLevelTransition()
        {
            s_syncQuiescedUntilTicks = System.Diagnostics.Stopwatch.GetTimestamp()
                + (long)(System.Diagnostics.Stopwatch.Frequency * 8.0);
            Log.Information("[MobSync] quiesced for level transition");
        }

        private static bool IsSyncQuiescedForTransition()
        {
            if (s_syncQuiescedUntilTicks == 0)
                return false;
            if (System.Diagnostics.Stopwatch.GetTimestamp() >= s_syncQuiescedUntilTicks)
            {
                s_syncQuiescedUntilTicks = 0;
                Log.Information("[MobSync] quiesce window expired (timeout)");
                return false;
            }
            return true;
        }

        private static void ClearSyncQuiesceAfterRebuild()
        {
            if (s_syncQuiescedUntilTicks == 0)
                return;
            s_syncQuiescedUntilTicks = 0;
            Log.Information("[MobSync] resumed after rebuild commit");
        }
        private static int suppressMobDieSendDepth;
        private static int suppressMobHitSendDepth;

        // Ghost-mob despawn echo (host only). Tracks repeated missing_sync_id hits per syncId —
        // a client persistently hitting a mob the host no longer tracks means the client has an
        // unkillable ghost. After enough misses over enough time the host echoes an authoritative
        // life=0 state for that syncId so the client's existing forced-death path cleans it up.
        // All access guarded by Sync.
        private sealed class GhostHitMissRecord
        {
            public int Count;
            public long FirstMissTicks;
            public long LastEchoTicks;
        }

        private static readonly Dictionary<int, GhostHitMissRecord> s_ghostHitMissBySyncId = new();
        private static readonly List<NetNode.MobStateSnapshot> s_ghostDespawnEchoScratch = new();
        private static int s_ghostHitMissGeneration;
        private const int GhostHitMissMinCount = 3;
        private const double GhostHitMissMinSeconds = 2.0;
        private const double GhostHitEchoMinIntervalSeconds = 2.0;

        private static Level? currentLevel;
        private static bool s_levelIdentityReady;
        private static int s_levelIdentityGeneration;
        private static int s_levelIdentityToken;
        private static WeakReference<Level>? s_lastResetLevelRef;
        private static string s_lastResetLevelId = string.Empty;
        private static int s_lastResetIdentityToken;
        private static int s_lastResetTrackedCount;
        private static WeakReference<Level>? s_lastCommittedLevelRef;
        private static string s_lastCommittedLevelId = string.Empty;
        private static int s_lastCommittedIdentityToken;
        private static int s_lastCommittedTrackedCount;
        private static string s_lastIgnoredDuplicateLevelId = string.Empty;
        private static int s_lastIgnoredDuplicateIdentityToken;
        private static string s_lastResetReason = string.Empty;
        private static int forceExactNemesisTargetDepth;
        private static int clientNetworkQueuedAttackDepth;
        private static Mob? clientNetworkQueuedAttackMob;
        private static readonly HashSet<Mob> clientActiveNetworkAttackMobs = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<Mob> clientAiLockedMobs = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<Mob> clientPendingSuppressedBossDies = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<int> clientAuthoritativeStateSeenSyncIds = new();
        private static readonly HashSet<Mob> s_validationSeenMobsScratch = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<int> s_validationSeenSyncIdsScratch = new();
        private static int authoritativeClientBossDieDepth;
        private const string MobSyncWorkerDisableEnv = "DCCM_MOB_SYNC_WORKER";
        private const string MobSyncAsyncInProcEnv = "DCCM_MOB_SYNC_ASYNC_INPROC";
        private static bool s_trackedMobValidationPending = true;
        private const string ExplicitEmptyStatePayloadMarker = "~";

        /// <summary>Per-type eligibility cache so IsSyncMob never allocates a string on the hot per-frame path.</summary>
        private static readonly ConcurrentDictionary<System.Type, bool> s_syncMobTypeCache = new();

        /// <summary>Thread-local reuse buffer for single-element string[] event arrays; avoids per-event GC allocation.</summary>
        [ThreadStatic]
        private static string[]? s_singleEventBuf;

        /// <summary>Thread-local reuse buffer for single-element MobEventUpdate[] arrays; avoids per-send GC allocation.</summary>
        [ThreadStatic]
        private static NetNode.MobEventUpdate[]? s_singleUpdateBuf;

        private static string[] SingleEvent(string ev)
        {
            var buf = s_singleEventBuf ??= new string[1];
            buf[0] = ev;
            return buf;
        }

        private static NetNode.MobEventUpdate[] SingleUpdate(NetNode.MobEventUpdate update)
        {
            var buf = s_singleUpdateBuf ??= new NetNode.MobEventUpdate[1];
            buf[0] = update;
            return buf;
        }

        private readonly struct ClientMobAttackIntent
        {
            public readonly string SkillId;
            public readonly bool RequiresTargetInArea;
            public readonly int? Data;
            public readonly int TargetUserId;
            public readonly int AttackDir;

            public ClientMobAttackIntent(string skillId, bool requiresTargetInArea, int? data, int targetUserId, int attackDir)
            {
                SkillId = skillId ?? string.Empty;
                RequiresTargetInArea = requiresTargetInArea;
                Data = data;
                TargetUserId = targetUserId;
                AttackDir = attackDir;
            }
        }

        private readonly struct ClientMobState
        {
            public readonly double X;
            public readonly double Y;
            public readonly int Dir;
            public readonly int Life;
            public readonly int MaxLife;
            public readonly string AnimPayload;
            public readonly string StatePayload;
            public readonly double Time;
            public readonly double Dx;
            public readonly double Dy;

            public ClientMobState(double x, double y, int dir, int life, int maxLife, string animPayload, string statePayload, double time = 0.0, double dx = 0.0, double dy = 0.0)
            {
                X = x;
                Y = y;
                Dir = dir;
                Life = life;
                MaxLife = maxLife;
                AnimPayload = animPayload ?? string.Empty;
                StatePayload = statePayload ?? string.Empty;
                Time = time;
                Dx = dx;
                Dy = dy;
            }
        }

        private readonly struct HostMobSentState
        {
            public readonly double X;
            public readonly double Y;
            public readonly int Dir;
            public readonly int Life;
            public readonly int MaxLife;
            public readonly string AnimPayload;
            public readonly string Type;
            public readonly string StatePayload;

            public HostMobSentState(double x, double y, int dir, int life, int maxLife, string animPayload, string type, string statePayload)
            {
                X = x;
                Y = y;
                Dir = dir;
                Life = life;
                MaxLife = maxLife;
                AnimPayload = animPayload ?? string.Empty;
                Type = type ?? string.Empty;
                StatePayload = statePayload ?? string.Empty;
            }
        }

        private readonly struct PendingClientAffectApply
        {
            public readonly int SyncId;
            public readonly Mob Mob;
            public readonly string StatePayload;

            public PendingClientAffectApply(int syncId, Mob mob, string statePayload)
            {
                SyncId = syncId;
                Mob = mob;
                StatePayload = statePayload ?? string.Empty;
            }
        }

        private readonly struct PendingHostStateApply
        {
            public readonly int SyncId;
            public readonly Mob Mob;
            public readonly int Life;
            public readonly int MaxLife;
            public readonly int Dir;
            public readonly string StatePayload;

            public PendingHostStateApply(int syncId, Mob mob, int life, int maxLife, int dir, string statePayload)
            {
                SyncId = syncId;
                Mob = mob;
                Life = life;
                MaxLife = maxLife;
                Dir = dir;
                StatePayload = statePayload ?? string.Empty;
            }
        }

        private readonly struct PendingMobHitApply
        {
            public readonly Mob Mob;
            public readonly int SourceUserId;
            public readonly int PreviousLife;
            public readonly int TargetLife;
            public readonly int TargetMaxLife;
            public readonly bool ForceDie;
            public readonly int SyncId;
            public readonly bool IsBoss;
            public readonly bool ReplaySpecialHit;

            public PendingMobHitApply(Mob mob, int sourceUserId, int previousLife, int targetLife, int targetMaxLife, bool forceDie, int syncId, bool isBoss, bool replaySpecialHit)
            {
                Mob = mob;
                SourceUserId = sourceUserId;
                PreviousLife = previousLife;
                TargetLife = targetLife;
                TargetMaxLife = targetMaxLife;
                ForceDie = forceDie;
                SyncId = syncId;
                IsBoss = isBoss;
                ReplaySpecialHit = replaySpecialHit;
            }
        }

        private enum HostMobSyncPriority
        {
            Active,
            MidRange,
            Dormant
        }

        private readonly struct ClientDrawSentState
        {
            public readonly bool IsOutOfGame;
            public readonly bool IsOnScreen;

            public ClientDrawSentState(bool isOutOfGame, bool isOnScreen)
            {
                IsOutOfGame = isOutOfGame;
                IsOnScreen = isOnScreen;
            }
        }

        public MobsSynchronization(ModEntry entry)
        {
            modEntry = entry;
            if (!s_eventReceiverInstalled)
            {
                EventSystem.AddReceiver(this);
                s_eventReceiverInstalled = true;
            }
        }

        public static void ClearTrackingForLevelChange()
        {
            var trackedBeforeReset = 0;
            var levelId = string.Empty;
            lock (Sync)
            {
                trackedBeforeReset = trackedMobs.Count;
                levelId = GetLevelTraceIdSafe(currentLevel);
                ResetMobTrackingLocked("level_change_external");
            }
            try { GameMenu.NetRef?.ClearMobSyncQueues(); } catch { }
            MobSyncTrace.LogLevelReset("external", levelId, trackedBeforeReset);
        }

        public void OnAdvancedModuleInitializing(ModEntry entry)
        {
            if (s_hooksInstalled)
                return;

            s_hooksInstalled = true;
            entry.Logger.Information("\x1b[32m[[ModEntry.MobsSynchronization] Initializing MobsSynchronization...]\x1b[0m ");
            try
            {
                // Keep mob encoding in-process by default; worker process can increase overhead under heavy mob counts.
                Environment.SetEnvironmentVariable(MobSyncWorkerDisableEnv, "0");
                Environment.SetEnvironmentVariable(MobSyncAsyncInProcEnv, "0");
            }
            catch
            {
            }

            Hook_Level.entitiesPostCreate += Hook_Level_entitiesPostCreate;
            Hook_Level.registerEntity += Hook_Level_registerEntity;
            Hook_Level.unregisterEntity += Hook_Level_unregisterEntity;
            Hook_Level.onDispose += Hook_Level_onDispose;

            Hook_Mob.setAttackTarget += Hook_Mob_setAttackTarget;
            Hook_Mob.setNemesisTarget += Hook_Mob_setNemesisTarget;
            Hook_Mob.preUpdate += Hook_Mob_preUpdate;
            Hook_Mob.fixedUpdate += Hook_Mob_fixedupdate;
            Hook_Mob.postUpdate += Hook_Mob_postUpdate;
            Hook_Mob.onDamage += Hook_Mob_onDamage;
            Hook_Mob.onDie += Hook_Mob_onDie;
            Hook_Mob.contactAttack += Hook_Mob_contactAttack;
            Hook_Mob.onTouch += Hook_Mob_onTouch;
            Hook_Mob.queueAttack += Hook_Mob_queueAttack;
            Hook_OldSkill.prepare += Hook_OldSkill_prepare;
            Hook_OldSkill.execute += Hook_OldSkill_execute;
            Hook_OldMobSkill.prepareOnOwnerTarget += Hook_OldMobSkill_prepareOnOwnerTarget;
            Hook_OldMobSkill.execute += Hook_OldMobSkill_execute;
            Hook_MobSkill.execute += Hook_MobSkill_execute;
            Hook_Entity.setAffectS += Hook_Entity_setAffectS_MobSync;
            Hook_Entity.addTimeToAffect += Hook_Entity_addTimeToAffect_MobSync;
            Hook_Entity.removeAffects += Hook_Entity_removeAffects_MobSync;
            Hook_Entity.removeAllAffects += Hook_Entity_removeAllAffects_MobSync;
        }

        void IOnFrameUpdate.OnFrameUpdate(double dt)
        {
            if (!MultiplayerSettingsStorage.EnableMobsSync)
            {
                lock (Sync)
                {
                    if (trackedMobs.Count > 0 || currentLevel != null)
                        ResetMobTrackingLocked("frame_update_sync_disabled");
                }
                return;
            }

            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive)
                return;

            if (IsHost(net))
            {
                var consumeStart = RuntimeHitchWatch.Start();
                RunHostIncomingFrameConsume(net);
                var consumeMs = RuntimeHitchWatch.GetElapsedMilliseconds(consumeStart);
                if (consumeMs >= RuntimeHitchWatch.MobSyncConsumeSlowThresholdMs)
                    RuntimeHitchWatch.LogSlow(modEntry.Logger, "MobsSynchronization.HostConsume", consumeMs, BuildRuntimeQueueDetails());

                var flushStart = RuntimeHitchWatch.Start();
                FlushHostDirtyMobQueue(net);
                var flushMs = RuntimeHitchWatch.GetElapsedMilliseconds(flushStart);
                if (flushMs >= RuntimeHitchWatch.MobSyncFlushSlowThresholdMs)
                    RuntimeHitchWatch.LogSlow(modEntry.Logger, "MobsSynchronization.HostFlush", flushMs, BuildRuntimeQueueDetails());

                return;
            }

            if (IsClient(net))
            {
                var consumeStart = RuntimeHitchWatch.Start();
                RunClientIncomingFrameConsume(net);
                var consumeMs = RuntimeHitchWatch.GetElapsedMilliseconds(consumeStart);
                if (consumeMs >= RuntimeHitchWatch.MobSyncConsumeSlowThresholdMs)
                    RuntimeHitchWatch.LogSlow(modEntry.Logger, "MobsSynchronization.ClientConsume", consumeMs, BuildRuntimeQueueDetails());

                var flushStart = RuntimeHitchWatch.Start();
                FlushClientDirtyMobQueue(net);
                var flushMs = RuntimeHitchWatch.GetElapsedMilliseconds(flushStart);
                if (flushMs >= RuntimeHitchWatch.MobSyncFlushSlowThresholdMs)
                    RuntimeHitchWatch.LogSlow(modEntry.Logger, "MobsSynchronization.ClientFlush", flushMs, BuildRuntimeQueueDetails());
            }
        }

        private static bool IsHost(NetNode? net) => net != null && net.IsAlive && net.IsHost;
        private static bool IsClient(NetNode? net) => net != null && net.IsAlive && !net.IsHost;

        private static string GetLevelTraceIdSafe(Level? level)
        {
            if (level == null)
                return string.Empty;

            try
            {
                return level.map?.id?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int GetEntityCountSafe(Level? level)
        {
            try
            {
                return level?.entities?.length ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string GetLevelRuntimeKey(Level? level)
        {
            if (level == null)
                return string.Empty;

            try
            {
                return RuntimeHelpers.GetHashCode(level).ToString("X8", CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryGetAuthoritativeGameplayLevel(out Level? level, out string source)
        {
            level = null;
            source = string.Empty;

            try
            {
                var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                var heroLevel = localHero?._level;
                if (heroLevel != null)
                {
                    level = heroLevel;
                    source = "hero";
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                var game = dc.pr.Game.Class.ME;
                var currentGameLevel = game?.curLevel;
                if (currentGameLevel != null)
                {
                    level = currentGameLevel;
                    source = "game";
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool DoesLevelMatchIdentity(Level? candidateLevel, int candidateIdentityToken, Level? authoritativeLevel)
        {
            if (candidateLevel == null || authoritativeLevel == null || candidateIdentityToken <= 0)
                return false;

            if (ReferenceEquals(candidateLevel, authoritativeLevel))
                return true;

            var candidateLevelId = GetLevelTraceIdSafe(candidateLevel);
            var authoritativeLevelId = GetLevelTraceIdSafe(authoritativeLevel);
            if (!string.IsNullOrEmpty(candidateLevelId) &&
                !string.IsNullOrEmpty(authoritativeLevelId) &&
                !string.Equals(candidateLevelId, authoritativeLevelId, StringComparison.Ordinal))
            {
                return false;
            }

            var authoritativeIdentityToken = ComputeLevelIdentityToken(authoritativeLevel);
            return authoritativeIdentityToken > 0 && authoritativeIdentityToken == candidateIdentityToken;
        }

        private static bool DoesStoredIdentityMatchLevel(string storedLevelId, int storedIdentityToken, Level? authoritativeLevel)
        {
            if (authoritativeLevel == null ||
                storedIdentityToken <= 0 ||
                string.IsNullOrEmpty(storedLevelId))
            {
                return false;
            }

            var authoritativeLevelId = GetLevelTraceIdSafe(authoritativeLevel);
            if (string.IsNullOrEmpty(authoritativeLevelId) ||
                !string.Equals(storedLevelId, authoritativeLevelId, StringComparison.Ordinal))
            {
                return false;
            }

            var authoritativeIdentityToken = ComputeLevelIdentityToken(authoritativeLevel);
            return authoritativeIdentityToken > 0 && authoritativeIdentityToken == storedIdentityToken;
        }

        private static bool TryGetTrackedWeakReferenceTargetLocked(WeakReference<Level>? reference, out Level? level)
        {
            level = null;
            return reference != null &&
                   reference.TryGetTarget(out level) &&
                   level != null;
        }

        private static string GetLastResetLevelRuntimeKeyLocked()
        {
            return TryGetTrackedWeakReferenceTargetLocked(s_lastResetLevelRef, out var level)
                ? GetLevelRuntimeKey(level)
                : string.Empty;
        }

        private static string GetLastCommittedLevelRuntimeKeyLocked()
        {
            return TryGetTrackedWeakReferenceTargetLocked(s_lastCommittedLevelRef, out var level)
                ? GetLevelRuntimeKey(level)
                : string.Empty;
        }

        private static void RememberCommittedRebuildLocked(Level? level, int identityToken, int trackedCount)
        {
            s_lastCommittedLevelRef = level == null ? null : new WeakReference<Level>(level);
            s_lastCommittedLevelId = GetLevelTraceIdSafe(level);
            s_lastCommittedIdentityToken = identityToken;
            s_lastCommittedTrackedCount = trackedCount;
            s_lastIgnoredDuplicateLevelId = string.Empty;
            s_lastIgnoredDuplicateIdentityToken = 0;
        }

        private static bool ShouldIgnoreCommittedIdentityEntitiesPostCreateLocked(Level? level, int candidateIdentityToken)
        {
            if (level == null ||
                candidateIdentityToken <= 0 ||
                !s_levelIdentityReady ||
                currentLevel == null ||
                trackedMobs.Count <= 0)
            {
                return false;
            }

            var candidateLevelId = GetLevelTraceIdSafe(level);
            if (string.IsNullOrEmpty(candidateLevelId))
                return false;

            var currentLevelId = GetLevelTraceIdSafe(currentLevel);
            if (!string.Equals(currentLevelId, candidateLevelId, StringComparison.Ordinal))
                return false;

            if (s_levelIdentityToken != candidateIdentityToken)
                return false;

            return s_lastCommittedIdentityToken == candidateIdentityToken &&
                   s_lastCommittedTrackedCount > 0 &&
                   string.Equals(s_lastCommittedLevelId, candidateLevelId, StringComparison.Ordinal);
        }

        private static bool TryIgnoreCommittedIdentityEntitiesPostCreate(Level? level)
        {
            var candidateIdentityToken = ComputeLevelIdentityToken(level);
            var levelId = GetLevelTraceIdSafe(level);
            var levelKey = GetLevelRuntimeKey(level);
            var entityCount = GetEntityCountSafe(level);
            var role = MobSyncNetRoleForTrace(GameMenu.NetRef);
            var trackedCurrent = 0;
            var currentLevelKey = string.Empty;
            var shouldLog = false;

            lock (Sync)
            {
                if (!ShouldIgnoreCommittedIdentityEntitiesPostCreateLocked(level, candidateIdentityToken))
                    return false;

                trackedCurrent = trackedMobs.Count;
                currentLevelKey = GetLevelRuntimeKey(currentLevel);
                shouldLog = s_lastIgnoredDuplicateIdentityToken != candidateIdentityToken ||
                            !string.Equals(s_lastIgnoredDuplicateLevelId, levelId, StringComparison.Ordinal);
                if (shouldLog)
                {
                    s_lastIgnoredDuplicateIdentityToken = candidateIdentityToken;
                    s_lastIgnoredDuplicateLevelId = levelId;
                }
            }

            if (shouldLog)
            {
                MobSyncTrace.LogEntitiesPostCreateDuplicateIgnored(
                    role,
                    levelId,
                    levelKey,
                    entityCount,
                    trackedCurrent,
                    candidateIdentityToken,
                    currentLevelKey);
            }

            return true;
        }

        private static bool TryGetCurrentLevelIdentityToken(out int identityToken)
        {
            lock (Sync)
            {
                return TryGetCurrentLevelIdentityTokenLocked(out identityToken);
            }
        }

        private static bool TryGetCurrentLevelIdentityTokenLocked(out int identityToken)
        {
            identityToken = s_levelIdentityToken;
            return s_levelIdentityReady &&
                   currentLevel != null &&
                   identityToken > 0;
        }

        private static bool IsPacketGenerationCurrentLocked(int packetGeneration)
        {
            return packetGeneration > 0 &&
                   TryGetCurrentLevelIdentityTokenLocked(out var currentGeneration) &&
                   packetGeneration == currentGeneration;
        }

        private static bool ShouldAcceptPacketGenerationLocked(int packetGeneration, ref int rejectedCount, ref int rejectedGeneration)
        {
            if (IsPacketGenerationCurrentLocked(packetGeneration))
                return true;

            rejectedCount++;
            if (rejectedGeneration == 0)
                rejectedGeneration = packetGeneration;
            return false;
        }

        private static void LogRejectedPacketGeneration(string context, int rejectedCount, int rejectedGeneration)
        {
            if (rejectedCount <= 0)
                return;

            int currentGeneration;
            lock (Sync)
            {
                currentGeneration = s_levelIdentityToken;
            }

            MobSyncTrace.LogPacketGenerationRejected(context, rejectedGeneration, currentGeneration, rejectedCount);
        }

        private static int ComputeLevelIdentityToken(Level? level)
        {
            if (level == null)
                return 0;

            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;

            try
            {
                AppendStableHash(ref hash, level.map?.id?.ToString());
            }
            catch
            {
            }

            try
            {
                var mapSeed = level.map?.seed ?? 0.0;
                var seedBits = BitConverter.DoubleToInt64Bits(mapSeed);
                hash ^= (uint)(seedBits & uint.MaxValue);
                hash *= prime;
                hash ^= (uint)((seedBits >> 32) & uint.MaxValue);
                hash *= prime;
            }
            catch
            {
            }

            var token = (int)(hash & 0x7fffffff);
            return token == 0 ? 1 : token;
        }

        private static void AppendStableHash(ref uint hash, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            const uint prime = 16777619;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= prime;
            }
        }

        private static void Hook_Level_entitiesPostCreate(Hook_Level.orig_entitiesPostCreate orig, Level self)
        {
            var levelId = GetLevelTraceIdSafe(self);
            var levelKey = GetLevelRuntimeKey(self);
            var entityCount = GetEntityCountSafe(self);
            var net = GameMenu.NetRef;
            var role = MobSyncNetRoleForTrace(net);
            var trackedBefore = 0;
            var currentLevelKey = string.Empty;
            var currentIdentityToken = 0;
            var identityReady = false;
            var lastResetReason = string.Empty;
            var shouldSuppressEnteredLog = false;
            lock (Sync)
            {
                trackedBefore = trackedMobs.Count;
                currentLevelKey = GetLevelRuntimeKey(currentLevel);
                currentIdentityToken = s_levelIdentityToken;
                identityReady = s_levelIdentityReady;
                lastResetReason = s_lastResetReason;
                shouldSuppressEnteredLog = ShouldIgnoreCommittedIdentityEntitiesPostCreateLocked(
                    self,
                    ComputeLevelIdentityToken(self));
            }

            if (!shouldSuppressEnteredLog)
            {
                MobSyncTrace.LogEntitiesPostCreateHookEntered(
                    role,
                    levelId,
                    levelKey,
                    entityCount,
                    trackedBefore,
                    currentLevelKey,
                    identityReady,
                    currentIdentityToken,
                    lastResetReason);
            }

            orig(self);

            if (TryIgnoreCommittedIdentityEntitiesPostCreate(self))
                return;

            var rebuildAccepted = RebuildMobArray(self);
            if (rebuildAccepted)
            {
                try { GameMenu.NetRef?.ClearMobSyncQueues(); } catch { }
            }

            // Native entitiesPostCreate rewrites mob HP (difficulty, affixes, etc.) which overwrites
            // the multiplier we applied in registerEntity.  Re-apply it now that the mob is fully set up.
            ApplyHpMultiplierToTrackedMobs();
        }

        private static void Hook_Level_registerEntity(Hook_Level.orig_registerEntity orig, Level self, Entity clid)
        {
            orig(self, clid);

            // Do not write an empty groupName into every vanilla entity. The current game build
            // can legitimately register sprites before their animation group is assigned. Only
            // validate the multiplayer-created KingSkin, and repair it through the animation API.
            if (clid is DeadCellsMultiplayerMod.Ghost.GhostBase.GhostKing registeredKing)
                ModEntry.EnsureGhostKingRenderSafe(registeredKing, "Level.registerEntity", detachForTransition: false);

            if (clid is not Mob mob)
                return;

            if (!IsSyncMob(mob))
                return;

            // Scale HP for later-spawned mobs (entitiesPostCreate already ran, won't run again).
            BossHpScaling.ScaleForMultiplayer(mob);

            var registerSyncId = -1;
            var registerLocalIndex = -1;
            var shouldQueueInitialSync = false;
            var registerDeferred = false;

            lock (Sync)
            {
                if (!IsLevelIdentityReadyLocked(self))
                {
                    registerDeferred = true;
                }
                else
                {
                    if (FindTrackedMobIndexLocked(mob) >= 0)
                        return;

                    if (!TryGetMobSyncId(mob, out registerSyncId))
                        return;

                    registerLocalIndex = AddTrackedMobLocked(mob);
                    shouldQueueInitialSync = registerLocalIndex >= 0;
                }
            }

            if (registerDeferred)
            {
                MobSyncTrace.LogDeferredMobRegistration(
                    GameMenu.NetRef?.IsHost == true ? "host" : (GameMenu.NetRef?.IsAlive == true ? "client" : "none"),
                    GetLevelTraceIdSafe(self),
                    BuildMobStateTypeSignature(mob));
                return;
            }

            var regNet = GameMenu.NetRef;
            var regRole = regNet == null || !regNet.IsAlive ? "none" : (regNet.IsHost ? "host" : "client");
            if (registerLocalIndex >= 0)
                MobSyncTrace.LogRegisterTracked(regRole, registerSyncId, registerLocalIndex, BuildMobStateTypeSignature(mob));

            if (shouldQueueInitialSync)
                QueueInitialMobSync(mob);
        }

        private static void Hook_Level_unregisterEntity(Hook_Level.orig_unregisterEntity orig, Level self, Entity clid)
        {
            var mob = clid as Mob;
            if (mob != null)
            {
                lock (Sync)
                {
                    RemoveTrackedMobLocked(mob);
                }
            }

            orig(self, clid);
        }

        private static void Hook_Level_onDispose(Hook_Level.orig_onDispose orig, Level self)
        {
            var trackedBeforeReset = 0;
            var levelId = string.Empty;
            lock (Sync)
            {
                trackedBeforeReset = trackedMobs.Count;
                levelId = GetLevelTraceIdSafe(self);
                ResetMobTrackingLocked("level_dispose_before_orig");
            }
            try { GameMenu.NetRef?.ClearMobSyncQueues(); } catch { }
            MobSyncTrace.LogLevelReset("dispose", levelId, trackedBeforeReset);

            orig(self);

            lock (Sync)
            {
                ResetMobTrackingLocked("level_dispose_after_orig");
            }
        }

        private void Hook_Mob_preUpdate(Hook_Mob.orig_preUpdate orig, Mob self)
        {
            var net = GameMenu.NetRef;
            var isHost = IsHost(net);
            var isClient = IsClient(net);
            
            if (!isHost && !isClient)
            {
                lock (Sync)
                {
                    if (trackedMobs.Count > 0 || currentLevel != null)
                        ResetMobTrackingLocked("pre_update_net_unavailable");
                }
                orig(self);
                return;
            }

            var isSyncMob = IsSyncMob(self);
            if (isSyncMob)
                EnsureMobTracked(self);

            if (isClient && isSyncMob)
                UpdateClientMobAiAuthority(self);

            if (isHost && isSyncMob)
            {
                TryApplyHostClientVisibilityInterest(self);
                TryAssignHostAttackTarget(self);
            }

            orig(self);

        }

        private void Hook_Mob_fixedupdate(Hook_Mob.orig_fixedUpdate orig, Mob self)
        {
            var net = GameMenu.NetRef;
            if (IsClient(net) && IsSyncMob(self))
            {
                orig(self);
                ApplyInterpolatedState(self);
                return;
            }

            orig(self);
        }

        private void Hook_Mob_postUpdate(Hook_Mob.orig_postUpdate orig, Mob self)
        {
            var net = GameMenu.NetRef;
            var isHost = IsHost(net);
            
            if (!isHost)
            {
                orig(self);
                if (IsClient(net) && IsSyncMob(self))
                {
                    if (TryRunPendingCulledMobDeath(self))
                        return;

                    ObserveClientMobForDirtyQueue(self);
                    ApplyClientAnimationStateBeforeUpdate(self);
                    TryRepairClientMobAttackTarget(self);
                }

                return;
            }

            orig(self);
            if (IsSyncMob(self))
            {
                ObserveHostMobForDirtyQueue(self);
                TryAssignHostAttackTarget(self);
            }
        }

        private static void Hook_Mob_onDie(Hook_Mob.orig_onDie orig, Mob self)
        {
            if (ShouldSuppressClientBossDie(self))
            {
                MarkSuppressedClientBossDie(self);
                return;
            }

            var shouldSendDie = false;
            var dieSyncId = -1;
            var dieX = 0.0;
            var dieY = 0.0;
            NetNode? dieNet = null;
            var isClient = false;
            if (self != null && suppressMobDieSendDepth <= 0)
            {
                dieNet = GameMenu.NetRef;
                isClient = IsClient(dieNet);

                // Client is not authoritative for mob death; wait for host die/hit confirmation.
                if (isClient && IsSyncMob(self))
                {
                    try
                    {
                        if (self.life <= 0)
                            self.life = 1;
                    }
                    catch
                    {
                    }

                    return;
                }

                if (dieNet != null &&
                    dieNet.IsAlive &&
                    dieNet.IsHost &&
                    TryGetMobSyncId(self, out dieSyncId))
                {
                    shouldSendDie = true;
                    dieX = GetSyncX(self);
                    dieY = GetSyncY(self);
                }
            }

            orig(self);

            if (self == null)
                return;

            ClearSuppressedClientBossDie(self);

            if (shouldSendDie && dieNet != null && dieNet.IsAlive && dieSyncId >= 0)
            {
                if (TryGetCurrentLevelIdentityToken(out var identityToken))
                {
                    var update = new NetNode.MobEventUpdate(dieSyncId, dieX, dieY, 0, SingleEvent("die"), generation: identityToken);
                    MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(dieNet), SingleUpdate(update));
                    dieNet.SendMobEvents(SingleUpdate(update));
                }
            }

            lock (Sync)
            {
                RemoveTrackedMobLocked(self);
            }
        }

        private static void RunWithSuppressedMobDieSend(Action action)
        {
            if (action == null)
                return;

            suppressMobDieSendDepth++;
            try
            {
                action();
            }
            finally
            {
                suppressMobDieSendDepth--;
            }
        }

        private static void RunWithSuppressedMobHitSend(Action action)
        {
            if (action == null)
                return;

            suppressMobHitSendDepth++;
            try
            {
                action();
            }
            finally
            {
                suppressMobHitSendDepth--;
            }
        }

        private void Hook_Mob_onDamage(Hook_Mob.orig_onDamage orig, Mob self, AttackData i)
        {
            var preDamageLife = GetMobLifeOrFallback(self, 0);
            // Lethal damage runs onDie inside orig, which removes the mob from tracking before this hook resumes.
            // Cache ids before orig so hit|life still sends when the mob is already untracked/destroyed.
            var preSyncOk = false;
            var cachedMobSyncId = -1;
            if (self != null && i != null && GameMenu.NetRef != null && IsSyncMob(self))
            {
                preSyncOk = TryGetMobSyncId(self, out cachedMobSyncId);
            }

            orig(self, i);

            try
            {
                if (self == null || i == null)
                    return;

                var net = GameMenu.NetRef;
                if (net == null)
                    return;

                if (!IsSyncMob(self) && !preSyncOk)
                    return;

                var isClient = IsClient(net);
                var tookLifeDelta = self.life < preDamageLife;
                var becameDead = self.life <= 0;
                bool shouldReport = false;
                if (IsHost(net))
                {
                    shouldReport = true;
                }
                else if (isClient)
                {
                    shouldReport = IsDamageFromLocalPlayer(i);
                    // Fallback for damage-source edge cases: never drop a lethal hit report.
                    if (!shouldReport && tookLifeDelta && becameDead)
                        shouldReport = true;
                }

                if (!shouldReport)
                    return;

                if (System.Threading.Volatile.Read(ref suppressMobHitSendDepth) > 0)
                    return;

                if (!TryGetMobSyncId(self, out var mobSyncId))
                {
                    if (!preSyncOk || !shouldReport)
                        return;
                    mobSyncId = cachedMobSyncId;
                }

                var life = GetMobLifeOrFallback(self, 0);
                var x = GetSyncX(self);
                var y = GetSyncY(self);
                var mobType = BuildMobStateTypeSignature(self);

                if (IsHost(net))
                {
                    var hx = GetWorldX(self);
                    var hy = GetWorldY(self);
                    var hitEvent = $"hit|{life.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    if (TryGetCurrentLevelIdentityToken(out var identityToken))
                    {
                        var update = new NetNode.MobEventUpdate(mobSyncId, hx, hy, NormalizeDir(self.dir), SingleEvent(hitEvent), mobType, identityToken);
                        MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(update));
                        net.SendMobEvents(SingleUpdate(update));
                    }
                }

                if (IsClient(net))
                {
                    lock (Sync)
                    {
                        if (!clientLastReportedMobLife.TryGetValue(self, out var lastLife))
                        {
                            // First locally-confirmed hit for this tracked mob: establish baseline and
                            // propagate immediately when damage actually reduced life.
                            clientLastReportedMobLife[self] = life;
                            var maxLife = self.maxLife;
                            if (life >= maxLife && life > 0)
                                return;
                        }
                        else
                        {
                            // Never drop a killing blow: stale lastLife or host sync can make life look non-decreasing.
                            if (life >= lastLife)
                            {
                                var lethalReport = shouldReport && life <= 0 && lastLife > 0 && preDamageLife > 0;
                                if (!lethalReport)
                                    return;
                            }

                            clientLastReportedMobLife[self] = life;
                        }
                    }

                    var clientHitEvent = $"hit|{life.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    if (TryGetCurrentLevelIdentityToken(out var identityToken))
                    {
                        var clientUpdate = new NetNode.MobEventUpdate(mobSyncId, x, y, 0, SingleEvent(clientHitEvent), mobType, identityToken);
                        MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(clientUpdate));
                        net.SendMobEvents(SingleUpdate(clientUpdate));
                    }
                }
            }
            finally
            {
                TryRecoverClientSyncMobLifeAfterLocalDamage(self, preDamageLife);
                TryRecoverSuppressedClientBossDie(self, preDamageLife);
            }
        }

        private static string MobSyncNetRoleForTrace(NetNode? net) =>
            net == null || !net.IsAlive ? "none" : (net.IsHost ? "host" : "client");

        private static bool ShouldSuppressClientBossDie(Mob? mob)
        {
            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return false;

            var net = GameMenu.NetRef;
            if (!IsClient(net))
                return false;
            if (!IsSyncMob(mob))
                return false;

            return System.Threading.Volatile.Read(ref authoritativeClientBossDieDepth) <= 0;
        }

        private static void MarkSuppressedClientBossDie(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientPendingSuppressedBossDies.Add(mob);
            }
        }

        private static void ClearSuppressedClientBossDie(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientPendingSuppressedBossDies.Remove(mob);
            }
        }

        private static void TryRecoverSuppressedClientBossDie(Mob? mob, int fallbackLife)
        {
            if (mob == null || mob.destroyed)
                return;

            var net = GameMenu.NetRef;
            if (!IsClient(net))
            {
                ClearSuppressedClientBossDie(mob);
                return;
            }

            bool hadSuppressedDie;
            lock (Sync)
            {
                hadSuppressedDie = clientPendingSuppressedBossDies.Remove(mob);
            }

            if (!hadSuppressedDie)
                return;

            try
            {
                if (mob.life <= 0)
                    mob.life = System.Math.Max(1, fallbackLife);
            }
            catch
            {
            }
        }

        private static void RunWithAuthoritativeClientBossDie(Mob? mob, Action action)
        {
            if (action == null)
                return;

            var net = GameMenu.NetRef;
            if (!IsClient(net) || mob == null || !BossSyncHelpers.IsBossMob(mob))
            {
                action();
                return;
            }

            authoritativeClientBossDieDepth++;
            try
            {
                action();
            }
            finally
            {
                authoritativeClientBossDieDepth--;
            }
        }

        private static bool IsDamageFromLocalPlayer(AttackData attack)
        {
            if (attack == null)
                return false;

            var localHero = (Entity?)(ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance);
            var gameHero = (Entity?)ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero == null && gameHero == null)
                return false;

            try
            {
                var source = attack.source;
                if (IsLocalPlayerDamageSource(source, localHero, gameHero))
                    return true;
            }
            catch
            {
            }

            try
            {
                var carrier = attack.carrier;
                if (IsLocalPlayerDamageSource(carrier, localHero, gameHero))
                    return true;
            }
            catch
            {
            }

            try
            {
                var sourceWeapon = attack.sourceWeapon;
                if (sourceWeapon == null)
                    return false;

                var owner = (Entity?)sourceWeapon.owner;
                if (IsLocalPlayerDamageSource(owner, localHero, gameHero))
                    return true;
            }
            catch
            {
            }

            try
            {
                InventItem sourceItem;
                try
                {
                    sourceItem = attack.sourceItem;
                }
                catch
                {
                    sourceItem = null!;
                }

                if (sourceItem != null &&
                    KingWeaponSupport.TryGetSourceByItem(sourceItem, out var kingSkin) &&
                    kingSkin != null &&
                    !IsKnownRemoteClientEntity(kingSkin))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool IsLocalPlayerDamageSource(Entity? source, Entity? localHero, Entity? gameHero)
        {
            if (source == null)
                return false;

            if (localHero != null && IsSameEntityForDamage(source, localHero))
                return true;

            if (gameHero != null && IsSameEntityForDamage(source, gameHero))
                return true;

            try
            {
                if ((source is Hero || source is KingSkin) && !IsKnownRemoteClientEntity(source))
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static bool IsKnownRemoteClientEntity(Entity? source)
        {
            if (source == null)
                return false;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null && IsSameEntityForDamage(source, client))
                    return true;
            }

            return false;
        }

        private static bool IsSameEntityForDamage(Entity? left, Entity? right)
        {
            if (left == null || right == null)
                return false;

            if (ReferenceEquals(left, right))
                return true;

            try
            {
                var leftUid = left.__uid;
                var rightUid = right.__uid;
                if (leftUid > 0 && rightUid > 0 && leftUid == rightUid)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static string BuildRuntimeQueueDetails()
        {
            lock (Sync)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"tracked={trackedMobs.Count} hostDirty={hostDirtyMobQueue.Count}/{hostDirtyFlagsBySyncId.Count} clientDirty={clientDirtyMobQueue.Count}/{clientDirtyFlagsBySyncId.Count} moves={s_moveSnapshotsScratch.Count} states={s_batchSnapshotsScratch.Count}");
            }
        }

        private static void ApplyHpMultiplierToTrackedMobs()
        {
            if (!MultiplayerSettingsStorage.EnableMobsSync)
                return;

            try
            {
                List<Mob> snapshot;
                lock (Sync)
                {
                    if (trackedMobs.Count == 0)
                        return;
                    snapshot = new List<Mob>(trackedMobs);
                }

                for (var i = 0; i < snapshot.Count; i++)
                {
                    try
                    {
                        BossHpScaling.ScaleForMultiplayer(snapshot[i]);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

    }
}

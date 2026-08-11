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
        private static long s_lastFrameRecoveryLogTicks;

        private static readonly object Sync = new();
        private static readonly List<Mob> trackedMobs = new();
        private static readonly Dictionary<Mob, int> trackedMobIndices = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<int, Mob> IdToMob = new();
        private static readonly Dictionary<Mob, int> MobToId = new(ReferenceEqualityComparer.Instance);
        private sealed class MobSyncAlias
        {
            public int SyncId;
            public int Generation;
        }
        private static ConditionalWeakTable<Mob, MobSyncAlias> s_mobSyncAliases = new();
        /// <summary>
        /// Host NetId allocator. Starts at 1 so wire Index 0 stays a reserved "none/invalid" sentinel
        /// and cannot be reintroduced by ghost-echo / hit-fallback rebinds after the owning mob dies.
        /// </summary>
        private static int nextRuntimeSyncId = 1;

        private static readonly Dictionary<Mob, ClientMobState> clientMobTargets = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, Entity?> clientCachedAttackTargetByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, int> hostLastSentContactTargetUserIdByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, string> clientQueuedOldSkillMarkers = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, int> clientLastReportedMobLife = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<int, string> clientLastSentAffectPayloadBySyncId = new();
        private static readonly Dictionary<int, ClientDrawSentState> clientLastSentDrawStateBySyncId = new();
        private static readonly Dictionary<int, string> clientLastAppliedHostAffectPayloadBySyncId = new();
        private static readonly Dictionary<int, Mob> clientLastAppliedHostAffectMobBySyncId = new();
        private static readonly Dictionary<int, string> hostLastAppliedClientAffectPayloadBySyncId = new();
        // Affect ids that were actually created on the host from a client report. This prevents
        // a later client empty payload from pruning unrelated host/elite affects.
        private static readonly Dictionary<Mob, HashSet<int>> hostClientOwnedAffectIdsByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, string> clientLastAppliedAnimPayloadByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, (string Group, double Frame)> clientLastForcedBossAnimByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, double> clientLastAnimationApplyFrameByMob = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, double> clientNetworkAttackStartFrame = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<string, ParsedAnimPayload> parsedAnimPayloadCache = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> hostMobTypeBySyncId = new();
        private static readonly Dictionary<int, HashSet<int>> hostClientInterestUsersBySyncId = new();
        private static readonly Dictionary<int, HostMobSentState> hostLastSentMobStatesBySyncId = new();
        // Latest host simulation frame accepted for each client mob position. Steam move packets are
        // intentionally unreliable and may arrive out of order; older coordinates must never rewind
        // a mob after a newer state has already been applied. Guarded by Sync.
        private static readonly Dictionary<int, double> clientLastAcceptedHostPositionFrameBySyncId = new();
        private static double s_lastHostActiveReliableKeyframeFrame = -99999.0;
        private static int s_lastHostActiveReliableKeyframeToken;
        private static double s_lastHostBossReliableKeyframeFrame = -99999.0;
        private static int s_lastHostBossReliableKeyframeToken;
        private static double s_lastHostAuthoritativeFullResyncFrame = -99999.0;
        private static int s_lastHostAuthoritativeFullResyncToken;
        private static int s_hostAuthoritativeBootstrapResyncsRemaining;
        private static readonly List<Entity> hostDetectedTargets = new();
        private static readonly List<Entity> s_clientDetectedTargetsScratch = new();
        private static readonly List<Mob> s_batchMobsScratch = new();
        private static readonly List<NetNode.MobStateSnapshot> s_batchSnapshotsScratch = new();
        private static readonly List<PendingClientAffectApply> s_clientAffectAppliesScratch = new();
        private static readonly List<PendingHostStateApply> s_hostStateAppliesScratch = new();
        private static readonly List<PendingMobHitApply> s_pendingMobHitAppliesScratch = new();
        private static readonly List<NetNode.MobHit> s_mobHitMergeScratch = new();
        private static readonly List<PendingClientBossAttack> clientPendingBossAttacks = new();
        private static readonly List<ResolvedClientBossAttack> s_resolvedClientBossAttacksScratch = new();
        private static readonly List<NetNode.MobDraw> s_drawsScratch = new();
        private static readonly List<NetNode.MobMoveSnapshot> s_moveSnapshotsScratch = new();
        private static readonly List<Mob> s_dieVictimsScratch = new();
        private static readonly HashSet<Mob> s_dieVictimDedupScratch = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<Mob> s_usedTrackedMobsScratch = new(ReferenceEqualityComparer.Instance);
        // Incoming state/move lines can be chunked and several snapshots for the same sync id
        // may accumulate before one main-thread consume. Walk newest-to-oldest and use this set
        // so stale snapshots cannot overwrite a newer authoritative state. Guarded by Sync.
        private static readonly HashSet<int> s_latestPacketSyncIdsScratch = new();

        // Client-side deaths deferred because the mob is culled locally (far from the local hero,
        // vanilla never proximity-initialized it). Running vanilla onDie() on such a mob leaves a
        // half-started death sequence that vanilla update null-derefs a frame later (the
        // "Null access .cx" fatal when the other player kills a mob far away). The real death runs
        // in Hook_Mob_postUpdate once vanilla itself starts simulating the mob. Guarded by Sync.
        private static readonly HashSet<Mob> s_pendingCulledMobDeaths = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<Mob, double> s_pendingCulledMobDeathFirstFrame = new(ReferenceEqualityComparer.Instance);

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

        // Host-side rate limit for republishing authoritative state after an unresolvable client
        // hit (see RequestAuthoritativeHitReconcileLocked). Guarded by Sync.
        private static readonly Dictionary<int, long> s_lastHitReconcileTicksBySyncId = new();
        private const double HitReconcileMinIntervalSeconds = 1.0;

        private sealed class HostDeathTombstone
        {
            public int SyncId;
            public int Generation;
            public double X;
            public double Y;
            public int Dir;
            public int MaxLife;
            public string Type = string.Empty;
            public string StatePayload = string.Empty;
            public double CreatedFrame;
            public double LastSentFrame = -99999.0;
            public int SendsRemaining;
        }

        private static readonly Dictionary<int, HostDeathTombstone> s_hostDeathTombstonesBySyncId = new();
        private static readonly List<HostDeathTombstone> s_hostDeathTombstoneScratch = new();
        private static readonly List<NetNode.MobStateSnapshot> s_hostDeathTombstoneStateScratch = new();
        private static readonly List<int> s_hostDeathTombstoneRemoveScratch = new();
        private static int s_ghostHitMissGeneration;
        private const int GhostHitMissMinCount = 3;
        private const double GhostHitMissMinSeconds = 2.0;
        private const double GhostHitEchoMinIntervalSeconds = 2.0;

        private static Level? currentLevel;
        private static bool s_levelIdentityReady;
        private static int s_levelIdentityGeneration;
        private static int s_levelIdentityToken;
        private static int s_lastAmbiguousFallbackLogFrame = -99999;
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
        private static int clientNetworkAttackReplayDepth;
        private static Mob? clientNetworkAttackReplayMob;
        private static readonly HashSet<Mob> clientActiveNetworkAttackMobs = new(ReferenceEqualityComparer.Instance);
        // Direct boss skill callbacks are allowed only while an attack packet or an accepted host
        // boss animation has opened this presentation lease. Guarded by Sync.
        private static readonly HashSet<Mob> clientBossSkillCallbackLeaseMobs = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<Mob> clientAiLockedMobs = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<Mob> clientPendingSuppressedBossDies = new(ReferenceEqualityComparer.Instance);
        // Prevent repeated typed tombstones from running a native
        // final boss onDie twice and duplicating rewards/cinematics. Guarded by Sync.
        private static readonly HashSet<Mob> clientCompletedAuthoritativeBossDeaths = new(ReferenceEqualityComparer.Instance);
        // Local client lethal damage is suppressed until the host confirms death. Track that
        // suppression explicitly so Hook_Mob_onDamage can still report hit|0 instead of the
        // temporary life=1 recovery value.
        private static readonly HashSet<Mob> clientPendingSuppressedMobDies = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<int> clientAuthoritativeStateSeenSyncIds = new();
        private static readonly HashSet<Mob> s_validationSeenMobsScratch = new(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<int> s_validationSeenSyncIdsScratch = new();
        private static int authoritativeClientBossDieDepth;
        // Allows host-confirmed deaths to execute vanilla onDie on clients. Without this, the
        // generic client death guard also blocked authoritative non-boss/elite deaths, leaving
        // half-dead AI and 0-HP ghosts.
        private static int authoritativeClientMobDieDepth;
        private static int suppressClientAffectDirtyDepth;
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

        private readonly struct PendingClientBossAttack
        {
            public readonly NetNode.MobAttack Attack;
            public readonly double ExpiresAtFrame;

            public PendingClientBossAttack(NetNode.MobAttack attack, double expiresAtFrame)
            {
                Attack = attack;
                ExpiresAtFrame = expiresAtFrame;
            }
        }

        private readonly struct ResolvedClientBossAttack
        {
            public readonly Mob Mob;
            public readonly NetNode.MobAttack Attack;

            public ResolvedClientBossAttack(Mob mob, NetNode.MobAttack attack)
            {
                Mob = mob;
                Attack = attack;
            }
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
            public readonly double ReceivedFrame;
            public readonly double Dx;
            public readonly double Dy;
            public readonly bool ForcePositionSnap;
            public readonly bool ForceVerticalPositionSnap;

            public ClientMobState(
                double x,
                double y,
                int dir,
                int life,
                int maxLife,
                string animPayload,
                string statePayload,
                double time = 0.0,
                double dx = 0.0,
                double dy = 0.0,
                double receivedFrame = 0.0,
                bool forcePositionSnap = false,
                bool forceVerticalPositionSnap = false)
            {
                X = x;
                Y = y;
                Dir = dir;
                Life = life;
                MaxLife = maxLife;
                AnimPayload = animPayload ?? string.Empty;
                StatePayload = statePayload ?? string.Empty;
                Time = time;
                ReceivedFrame = receivedFrame;
                Dx = dx;
                Dy = dy;
                ForcePositionSnap = forcePositionSnap;
                ForceVerticalPositionSnap = forceVerticalPositionSnap;
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
            public readonly double DamageHint;

            public PendingMobHitApply(Mob mob, int sourceUserId, int previousLife, int targetLife, int targetMaxLife, bool forceDie, int syncId, bool isBoss, bool replaySpecialHit, double damageHint)
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
                DamageHint = double.IsFinite(damageHint) && damageHint > 0.0 ? damageHint : 0.0;
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
            try { LobbySession.NetRef?.ClearMobSyncQueues(); } catch { }
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

            // Boss-specific adapters for state the generic mob/boss pipeline structurally cannot
            // carry (see BeholderArenaSync). Installed last and self-guarded, so a binding mismatch
            // for one boss cannot prevent the generic hooks above from being active.
            BeholderArenaSync.InstallHooks();
        }

        void IOnFrameUpdate.OnFrameUpdate(double dt)
        {
            try
            {
                OnFrameUpdateCore(dt);
            }
            catch (Exception ex)
            {
                // A malformed/stale packet or a weapon-specific Hashlink wrapper exception must not
                // escape the frame receiver and close the entire game. Preserve the mob registry,
                // discard only transient network work, and let the periodic host full-resync heal it.
                try { LobbySession.NetRef?.ClearMobSyncQueues(); } catch { }

                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var minTicks = System.Diagnostics.Stopwatch.Frequency * 3L;
                if (s_lastFrameRecoveryLogTicks == 0 || now - s_lastFrameRecoveryLogTicks >= minTicks)
                {
                    s_lastFrameRecoveryLogTicks = now;
                    modEntry.Logger.Warning(ex, "[MobSync] frame exception contained; transient queues cleared for recovery");
                }
            }
        }

        private void OnFrameUpdateCore(double dt)
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

            var net = LobbySession.NetRef;
            if (net == null || !net.IsAlive)
                return;

            if (IsHost(net))
            {
                // These recovery routines already existed in the working-sync source but were
                // never connected to the frame loop. Run the one-shot player-target repair before
                // processing/sending mob state so enemies cannot keep a destroyed remote GhostKing
                // as their attack or nemesis target after a door, sublevel or boss-cell reload.
                RunHostPlayerCombatStateRepairIfPending();

                var consumeStart = RuntimeHitchWatch.Start();
                RunHostIncomingFrameConsume(net);
                var consumeMs = RuntimeHitchWatch.GetElapsedMilliseconds(consumeStart);
                if (consumeMs >= RuntimeHitchWatch.MobSyncConsumeSlowThresholdMs)
                    RuntimeHitchWatch.LogSlow(modEntry.Logger, "MobsSynchronization.HostConsume", consumeMs, BuildRuntimeQueueDetails());

                // Must run before the dirty flush: vanilla culls enemies against the HOST's hero, so
                // this is the only thing that keeps a mob standing next to the second player awake
                // and able to acquire it. Anything it activates streams out on this same frame.
                RunHostRemotePlayerActivationPass(net);

                var flushStart = RuntimeHitchWatch.Start();
                FlushHostDirtyMobQueue(net);
                ScanHostBossPartDespawns(net);
                FlushHostMobRegistry(net);
                FlushHostPriorityResync(net);
                FlushHostDeathTombstoneResends(net);
                var flushMs = RuntimeHitchWatch.GetElapsedMilliseconds(flushStart);
                if (flushMs >= RuntimeHitchWatch.MobSyncFlushSlowThresholdMs)
                    RuntimeHitchWatch.LogSlow(modEntry.Logger, "MobsSynchronization.HostFlush", flushMs, BuildRuntimeQueueDetails());

                return;
            }

            if (IsClient(net))
            {
                var consumeStart = RuntimeHitchWatch.Start();
                RunClientIncomingFrameConsume(net);
                FlushPendingClientAuthoritativeDeaths();
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
            var role = MobSyncNetRoleForTrace(LobbySession.NetRef);
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
            var net = LobbySession.NetRef;
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

            // Level disposal already clears the previous level's queues, and every mob packet is
            // fenced by the committed level-generation token. Clearing here used to discard the
            // first valid bootstrap chunks that arrived while a client was finishing level load.
            RebuildMobArray(self);

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
                    LobbySession.NetRef?.IsHost == true ? "host" : (LobbySession.NetRef?.IsAlive == true ? "client" : "none"),
                    GetLevelTraceIdSafe(self),
                    BuildMobStateTypeSignature(mob));
                return;
            }

            var regNet = LobbySession.NetRef;
            var regRole = regNet == null || !regNet.IsAlive ? "none" : (regNet.IsHost ? "host" : "client");
            if (registerLocalIndex >= 0)
                MobSyncTrace.LogRegisterTracked(regRole, registerSyncId, registerLocalIndex, BuildMobStateTypeSignature(mob));

            if (shouldQueueInitialSync)
            {
                MobSyncTrace.LogMobSpawnRegistered(regRole, registerSyncId, BuildMobStateTypeSignature(mob));
                QueueInitialMobSync(mob);
            }
        }

        private static void Hook_Level_unregisterEntity(Hook_Level.orig_unregisterEntity orig, Level self, Entity clid)
        {
            var mob = clid as Mob;
            if (mob != null)
            {
                lock (Sync)
                {
                    if (ShouldRetainMobSyncIdOnTemporaryUnregisterLocked(self, mob))
                        DetachTrackedMobForTemporaryUnregisterLocked(mob);
                    else
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
            try { LobbySession.NetRef?.ClearMobSyncQueues(); } catch { }
            MobSyncTrace.LogLevelReset("dispose", levelId, trackedBeforeReset);
            // Homunculus.dispose writes hero.controller.manualLock with no null check. Heal and
            // pre-dispose Homunculi before the native Level.onDispose → runEntitiesGC path.
            try { ModEntry.PrepareLevelProcessTeardown(self, "level_dispose_before"); } catch { }

            orig(self);

            lock (Sync)
            {
                ResetMobTrackingLocked("level_dispose_after_orig");
            }
        }

        private void Hook_Mob_preUpdate(Hook_Mob.orig_preUpdate orig, Mob self)
        {
            var net = LobbySession.NetRef;
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
                TryMaintainHostBossSurvivorTarget(self);

            if (isHost && isSyncMob)
                TryApplyHostClientVisibilityInterest(self);

            orig(self);

            // Vanilla gets the first chance to update its behavior tree. Only afterward fill a
            // genuinely missing immediate target; never unlock/wake/rewrite elite state here.
            if (isHost && isSyncMob)
                TryAssignHostAttackTarget(self);

        }

        private void Hook_Mob_fixedupdate(Hook_Mob.orig_fixedUpdate orig, Mob self)
        {
            var net = LobbySession.NetRef;
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
            var net = LobbySession.NetRef;
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
                // The host is the authoritative vanilla simulation. Observe what vanilla produced,
                // but never unlock/wake/move the mob merely because a network watchdog thinks it is
                // stationary. That kind of recovery can corrupt legitimate elite/teleport/skill
                // phases and makes enemy behavior depend on synchronization timing.
                ObserveHostMobForDirtyQueue(self);
            }
        }

        private static void Hook_Mob_onDie(Hook_Mob.orig_onDie orig, Mob self)
        {
            if (ShouldSuppressClientBossDie(self))
            {
                MarkSuppressedClientBossDie(self);
                MarkSuppressedClientMobDie(self);
                return;
            }

            var shouldSendDie = false;
            var dieSyncId = -1;
            var dieX = 0.0;
            var dieY = 0.0;
            var dieType = string.Empty;
            NetNode? dieNet = null;
            var isClient = false;
            var isBossDeathCandidate = false;
            if (self != null && suppressMobDieSendDepth <= 0)
            {
                dieNet = LobbySession.NetRef;
                isClient = IsClient(dieNet);
                isBossDeathCandidate = BossSyncHelpers.IsBossMob(self);

                // Client is not authoritative for mob death; wait for host confirmation. The
                // authoritative depth is set only while applying a host-confirmed death, allowing
                // vanilla onDie to run exactly once for normal mobs, elites and bosses.
                if (isClient && IsSyncMob(self) &&
                    System.Threading.Volatile.Read(ref authoritativeClientMobDieDepth) <= 0)
                {
                    MarkSuppressedClientMobDie(self);
                    try
                    {
                        if (self.life <= 0)
                            self.life = GetClientAuthoritativeLifeFallback(self, 1);
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
                    dieType = BuildMobStateTypeSignature(self);
                }
            }

            orig(self);

            if (self == null)
                return;

            ClearSuppressedClientBossDie(self);

            // Some multi-phase bosses route a depleted phase through onDie(), then rebuild the
            // same encounter with positive life.  That is not a victory.  Keep its authoritative
            // mapping and immediately publish the rebuilt phase instead of sending a death packet,
            // removing tracking, or reviving players early.
            if (shouldSendDie && isBossDeathCandidate)
            {
                var bossContinues = false;
                try { bossContinues = !self.destroyed && self.life > 0; } catch { }
                if (bossContinues)
                {
                    QueueHostMobDirty(self, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                    return;
                }

                // Some phase transitions destroy the depleted native object outright and rebuild
                // the encounter behind a new one, so "!destroyed" alone misreads them as a real
                // death. If a different living Level.boss carries the same stable identity, this
                // is a hand-off: publish the rebuilt phase immediately instead of a death packet.
                if (TryGetHostBossPhaseSuccessor(self, out var phaseSuccessor))
                {
                    QueueHostMobDirty(phaseSuccessor, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                    return;
                }
            }

            if (shouldSendDie && dieNet != null && dieNet.IsAlive && dieSyncId >= 0)
            {
                if (TryGetCurrentLevelIdentityToken(out var identityToken))
                {
                    lock (Sync)
                    {
                        RememberHostDeathTombstoneLocked(self, dieSyncId, dieX, dieY, identityToken);
                    }

                    // Authoritative death: one reliable MOBDIE path (no dual MOBEVENT|die).
                    dieNet.SendMobDie(dieSyncId, dieX, dieY, identityToken, dieType);
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
            if (self != null && i != null && LobbySession.NetRef != null && IsSyncMob(self))
            {
                preSyncOk = TryGetMobSyncId(self, out cachedMobSyncId);
            }

            // Read the attack's construction input before vanilla resolves the hit: resolution
            // rewrites the per-target damage fields, and AttackData instances are recycled.
            var attackIntentDamage = ReadAttackIntentDamage(i);

            orig(self, i);

            try
            {
                if (self == null || i == null)
                    return;

                var net = LobbySession.NetRef;
                if (net == null)
                    return;

                if (!IsSyncMob(self) && !preSyncOk)
                    return;

                var isClient = IsClient(net);
                var suppressedClientLethal = isClient && WasClientMobDeathSuppressed(self);
                var tookLifeDelta = self.life < preDamageLife || suppressedClientLethal;
                var becameDead = self.life <= 0 || suppressedClientLethal;
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

                // NetId 0 is reserved. Never report damage against it — that is the courtyard
                // syncId=0 thrash vector once the real owner was gone.
                if (mobSyncId <= 0)
                    return;

                // A locally lethal client hit temporarily restores the mob so the client does not
                // run an unsanctioned death. Still report life=0 to the host; reporting the restored
                // value (usually 1) was the source of elites becoming permanently unkillable.
                var life = suppressedClientLethal ? 0 : GetMobLifeOrFallback(self, 0);
                var damageHint = isClient && shouldReport
                    ? EstimateClientAttackDamageHint(attackIntentDamage, preDamageLife, life, suppressedClientLethal)
                    : 0.0;
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
                            if (life >= maxLife && life > 0 && damageHint <= 0.0)
                                return;
                        }
                        else
                        {
                            // Never drop a killing blow: stale lastLife or host sync can make life look non-decreasing.
                            if (life >= lastLife)
                            {
                                var lethalReport = shouldReport && life <= 0 && lastLife > 0 && preDamageLife > 0;
                                var authoritativeProbe = shouldReport && damageHint > 0.0;
                                if (!lethalReport && !authoritativeProbe)
                                    return;
                            }

                            clientLastReportedMobLife[self] = life;
                        }
                    }

                    var clientHitEvent = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"hit|{life}|{damageHint:R}");
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
                TryRecoverSuppressedClientMobDie(self, preDamageLife);
                TryRecoverSuppressedClientBossDie(self, preDamageLife);
            }
        }

        private static string MobSyncNetRoleForTrace(NetNode? net) =>
            net == null || !net.IsAlive ? "none" : (net.IsHost ? "host" : "client");

        private static void MarkSuppressedClientMobDie(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientPendingSuppressedMobDies.Add(mob);
            }
        }

        private static bool WasClientMobDeathSuppressed(Mob? mob)
        {
            if (mob == null)
                return false;

            lock (Sync)
            {
                return clientPendingSuppressedMobDies.Contains(mob) ||
                       clientPendingSuppressedBossDies.Contains(mob);
            }
        }

        private static int GetClientAuthoritativeLifeFallback(Mob? mob, int fallbackLife)
        {
            var recovered = System.Math.Max(1, fallbackLife);
            if (mob == null)
                return recovered;

            lock (Sync)
            {
                if (clientMobTargets.TryGetValue(mob, out var target) && target.Life > 0)
                    recovered = System.Math.Max(recovered, target.Life);
            }

            return recovered;
        }

        private static void TryRecoverSuppressedClientMobDie(Mob? mob, int fallbackLife)
        {
            if (mob == null)
                return;

            bool hadSuppressedDie;
            lock (Sync)
            {
                hadSuppressedDie = clientPendingSuppressedMobDies.Remove(mob);
            }

            if (!hadSuppressedDie)
                return;

            try
            {
                if (!mob.destroyed && mob.life <= 0)
                    mob.life = GetClientAuthoritativeLifeFallback(mob, fallbackLife);
            }
            catch
            {
            }

            // Restore the older native handoff after a locally suppressed lethal callback. The
            // regular client authority update will relock the replica when no native attack/phase
            // callback remains active.
            TryUnlockClientMobAiAuthority(mob);
        }

        private static bool ShouldSuppressClientBossDie(Mob? mob)
        {
            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return false;

            var net = LobbySession.NetRef;
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

            var net = LobbySession.NetRef;
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

        private static void RunWithAuthoritativeClientMobDie(Mob? mob, Action action)
        {
            if (action == null)
                return;

            var net = LobbySession.NetRef;
            if (!IsClient(net) || mob == null || !IsSyncMob(mob))
            {
                action();
                return;
            }

            var isBoss = BossSyncHelpers.IsBossMob(mob);
            authoritativeClientMobDieDepth++;
            if (isBoss)
                authoritativeClientBossDieDepth++;
            try
            {
                action();
            }
            finally
            {
                if (isBoss)
                    authoritativeClientBossDieDepth--;
                authoritativeClientMobDieDepth--;
            }
        }

        /// <summary>
        /// Reads the pre-target-resolution damage the attack was built from.
        /// <c>AttackUtils.createFromHero(source, baseDmg, tier)</c> takes exactly this value, so it
        /// is the only scalar the host can feed back into the native hit path without re-deriving a
        /// number that was already derived once. <c>finalDmg</c>/<c>inflictedDmg</c> are produced by
        /// <c>updateDamages(attack, target)</c> and <c>applyHitResult</c>: they are per-target and
        /// already contain this replica's armour, resistances and invulnerability, so transmitting
        /// them would let a client-side outcome dictate the authoritative result.
        /// </summary>
        private static double ReadAttackIntentDamage(AttackData? attack)
        {
            if (attack == null)
                return 0.0;

            try
            {
                // baseDmg is Haxe Dynamic on the proxy; bind it statically before converting.
                object? rawBaseDmg = attack.baseDmg;
                if (TryConvertAttackDamageScalar(rawBaseDmg, out var baseDmg))
                    return baseDmg;
            }
            catch
            {
            }

            // Dots, environmental relays and a few scripted sources build the attack without a
            // baseDmg. rawFinalDmg is the rolled attacker-side figure; unlike inflictedDmg it is not
            // the post-mitigation result, but it is computed with the target known, so treat it only
            // as an approximation used to keep the hit alive instead of reporting "no damage".
            try
            {
                if (TryConvertAttackDamageScalar(attack.rawFinalDmg, out var rawFinalDmg))
                    return rawFinalDmg;
            }
            catch
            {
            }

            return 0.0;
        }

        private static bool TryConvertAttackDamageScalar(object? raw, out double value)
        {
            value = 0.0;
            if (raw == null)
                return false;

            try
            {
                value = System.Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }

            if (!double.IsFinite(value) || value <= 0.0)
            {
                value = 0.0;
                return false;
            }

            return true;
        }

        private static double EstimateClientAttackDamageHint(double attackIntentDamage, int previousLife, int reportedLife, bool suppressedLethal)
        {
            // The attack's own construction input is the authoritative intent: it does not depend on
            // what this replica happened to absorb, so it stays correct even while the host stream is
            // continuously rewriting local HP. Preferring it over the observed delta is what stops a
            // client hit from being reported as "no damage" and dropped by the host.
            //
            // No upper bound is applied here on purpose. Clamping against this replica's HP would
            // put the client's own (frequently stale) life back into a value whose whole point is to
            // be independent of it, and would silently under-report every hit landed while the
            // replica showed less HP than the host. The sanity bound belongs on the authority:
            // TryReplayIncomingSpecialHitReaction already clamps against the host mob's real life.
            if (attackIntentDamage > 0.0)
                return System.Math.Max(1.0, attackIntentDamage);

            // Last resort for attacks that expose no readable input. A local delta has already passed
            // through this replica's mitigation, so it is a lower bound on the real intent, never the
            // truth - the host still resolves it natively against its own mob.
            var observedDelta = System.Math.Max(0, previousLife - reportedLife);
            if (suppressedLethal && previousLife > 0)
                observedDelta = System.Math.Max(observedDelta, previousLife);

            return observedDelta > 0 ? observedDelta : 0.0;
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using dc;
using dc.en;
using dc.h2d;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.pr;
using dc.tool.atk;
using dc.tool.skill;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using Hashlink.Virtuals;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private static bool RebuildMobArray(Level? level)
        {
            var candidateIdentityToken = ComputeLevelIdentityToken(level);
            var candidateEntityCount = 0;
            var candidateTrackedMobs = new List<Mob>();
            var role = MobSyncNetRoleForTrace(LobbySession.NetRef);
            var levelId = GetLevelTraceIdSafe(level);
            var levelKey = GetLevelRuntimeKey(level);
            if (level?.entities != null)
            {
                var entities = level.entities;
                candidateEntityCount = entities.length;
                if (candidateIdentityToken > 0)
                {
                    for (int i = 0; i < entities.length; i++)
                    {
                        var mob = entities.getDyn(i) as Mob;
                        if (mob == null || !IsSyncMob(mob))
                            continue;

                        candidateTrackedMobs.Add(mob);
                    }
                }
            }

            var trackedBeforeReset = 0;
            var trackedAfterRebuild = 0;
            var rebuildAccepted = false;
            var generationAfterRebuild = 0;
            var rejectionReason = string.Empty;
            var currentIdentityTokenBefore = 0;
            var currentIdentityReadyBefore = false;
            var currentLevelKeyBefore = string.Empty;
            var lastResetLevelKeyBefore = string.Empty;
            var lastResetTrackedCountBefore = 0;
            var lastResetIdentityTokenBefore = 0;
            var lastCommittedLevelKeyBefore = string.Empty;
            var lastCommittedTrackedCountBefore = 0;
            var lastCommittedIdentityTokenBefore = 0;
            var baselineTrackedCount = 0;
            var baselineSource = string.Empty;
            var lastResetReasonBefore = string.Empty;
            lock (Sync)
            {
                trackedBeforeReset = trackedMobs.Count;
                currentIdentityTokenBefore = s_levelIdentityToken;
                currentIdentityReadyBefore = s_levelIdentityReady;
                currentLevelKeyBefore = GetLevelRuntimeKey(currentLevel);
                lastResetLevelKeyBefore = GetLastResetLevelRuntimeKeyLocked();
                lastResetTrackedCountBefore = s_lastResetTrackedCount;
                lastResetIdentityTokenBefore = s_lastResetIdentityToken;
                lastCommittedLevelKeyBefore = GetLastCommittedLevelRuntimeKeyLocked();
                lastCommittedTrackedCountBefore = s_lastCommittedTrackedCount;
                lastCommittedIdentityTokenBefore = s_lastCommittedIdentityToken;
                lastResetReasonBefore = s_lastResetReason;
                if (!ShouldAcceptRebuildCandidateLocked(
                        level,
                        candidateIdentityToken,
                        candidateEntityCount,
                        candidateTrackedMobs.Count,
                        out rejectionReason,
                        out baselineTrackedCount,
                        out baselineSource))
                {
                    trackedAfterRebuild = trackedMobs.Count;
                    generationAfterRebuild = s_levelIdentityGeneration;
                }
                else
                {
                    ResetMobTrackingLocked("rebuild_prepare");
                    currentLevel = level;
                    // Host-owned NetIds: only the host assigns identity. Clients track unbound
                    // locals and bind from MOBREG / first authoritative state (type + spawn).
                    AssignHostNetIdsForRebuildLocked(candidateTrackedMobs);

                    trackedAfterRebuild = trackedMobs.Count;
                    s_levelIdentityToken = candidateIdentityToken;
                    s_levelIdentityReady = level != null && s_levelIdentityToken > 0;
                    if (s_levelIdentityReady)
                        s_levelIdentityGeneration++;

                    generationAfterRebuild = s_levelIdentityGeneration;
                    rebuildAccepted = true;
                    RememberCommittedRebuildLocked(level, candidateIdentityToken, trackedAfterRebuild);
                    ValidateTrackedIntegrityLocked("rebuild");
                }
            }

            MobSyncTrace.LogRebuildCandidate(
                role,
                levelId,
                levelKey,
                candidateEntityCount,
                candidateTrackedMobs.Count,
                candidateIdentityToken,
                trackedBeforeReset,
                currentIdentityTokenBefore,
                currentLevelKeyBefore,
                lastResetLevelKeyBefore,
                lastResetTrackedCountBefore,
                lastResetIdentityTokenBefore,
                lastCommittedLevelKeyBefore,
                lastCommittedTrackedCountBefore,
                lastCommittedIdentityTokenBefore,
                lastResetReasonBefore);

            MobSyncTrace.LogRebuildDecision(
                role,
                levelId,
                levelKey,
                rebuildAccepted ? "accepted" : "rejected",
                rejectionReason,
                trackedBeforeReset,
                trackedAfterRebuild,
                candidateEntityCount,
                candidateTrackedMobs.Count,
                baselineTrackedCount,
                baselineSource,
                currentIdentityReadyBefore,
                currentIdentityTokenBefore,
                candidateIdentityToken,
                currentLevelKeyBefore,
                lastResetLevelKeyBefore,
                lastCommittedLevelKeyBefore,
                lastResetReasonBefore);

            if (!rebuildAccepted)
            {
                MobSyncTrace.LogRebuildRejected(
                    rejectionReason,
                    role,
                    levelId,
                    trackedBeforeReset,
                    candidateEntityCount,
                    candidateTrackedMobs.Count,
                    currentIdentityTokenBefore,
                    candidateIdentityToken);
                return false;
            }

            lock (Sync)
            {
                s_batchMobsScratch.Clear();
                s_batchMobsScratch.AddRange(trackedMobs);
            }

            var minSyncId = -1;
            var maxSyncId = -1;
            var registryCount = 0;
            lock (Sync)
            {
                foreach (var pair in MobToId)
                {
                    if (pair.Value <= 0)
                        continue;
                    registryCount++;
                    if (minSyncId < 0 || pair.Value < minSyncId)
                        minSyncId = pair.Value;
                    if (pair.Value > maxSyncId)
                        maxSyncId = pair.Value;
                }
            }

            MobSyncTrace.LogRegistryRebuild(
                role,
                levelId,
                trackedBeforeReset,
                trackedAfterRebuild,
                registryCount,
                minSyncId,
                maxSyncId,
                nextRuntimeSyncId,
                generationAfterRebuild,
                s_levelIdentityToken);
            MobSyncTrace.LogRebuildCommit(
                role,
                levelId,
                levelKey,
                trackedAfterRebuild,
                registryCount,
                generationAfterRebuild,
                s_levelIdentityToken);

            lock (Sync)
            {
                // The client may finish level loading a few frames after the host. Queue initial
                // dirty states immediately, but also force short bootstrap full-resync bursts so
                // the next-level mob table cannot be missed by a single early packet/chunk.
                s_hostAuthoritativeBootstrapResyncsRemaining = trackedAfterRebuild > 0
                    ? HostAuthoritativeBootstrapResyncCount
                    : 0;
                s_lastHostBossReliableKeyframeFrame = -99999.0;
                s_lastHostBossReliableKeyframeToken = s_levelIdentityToken;
                s_lastHostAuthoritativeFullResyncFrame = -99999.0;
                s_lastHostAuthoritativeFullResyncToken = s_levelIdentityToken;
            }

            ClearSyncQuiesceAfterRebuild();
            QueueHostMobRegistryAfterRebuild();

            for (int i = 0; i < s_batchMobsScratch.Count; i++)
                QueueInitialMobSync(s_batchMobsScratch[i]);

            s_batchMobsScratch.Clear();
            return true;
        }

        private static bool ShouldAcceptRebuildCandidateLocked(
            Level? level,
            int candidateIdentityToken,
            int candidateEntityCount,
            int candidateTrackedCount,
            out string reason,
            out int baselineTrackedCount,
            out string baselineSource)
        {
            baselineTrackedCount = 0;
            baselineSource = string.Empty;

            if (level == null)
            {
                reason = "level_null";
                return false;
            }

            if (level.entities == null)
            {
                reason = "entities_missing";
                return false;
            }

            if (candidateIdentityToken <= 0)
            {
                reason = "identity_invalid";
                return false;
            }

            if (trackedMobs.Count > 0 && candidateTrackedCount <= 0)
            {
                reason = "replace_empty";
                return false;
            }

            var candidateLevelId = GetLevelTraceIdSafe(level);
            var currentLevelId = GetLevelTraceIdSafe(currentLevel);
            var sameIdentity = currentLevel != null &&
                               s_levelIdentityReady &&
                               s_levelIdentityToken > 0 &&
                               s_levelIdentityToken == candidateIdentityToken &&
                               string.Equals(currentLevelId, candidateLevelId, StringComparison.Ordinal);
            var sameLastCommittedIdentity = s_lastCommittedIdentityToken > 0 &&
                                            s_lastCommittedIdentityToken == candidateIdentityToken &&
                                            string.Equals(s_lastCommittedLevelId, candidateLevelId, StringComparison.Ordinal);
            var replacingExplicitlyDisposedSameIdentityLevel =
                sameLastCommittedIdentity &&
                trackedMobs.Count == 0 &&
                !s_levelIdentityReady &&
                currentLevel == null &&
                candidateTrackedCount > 0 &&
                (string.Equals(s_lastResetReason, "level_dispose_before_orig", StringComparison.Ordinal) ||
                 string.Equals(s_lastResetReason, "level_dispose_after_orig", StringComparison.Ordinal));

            if (sameIdentity && trackedMobs.Count > 0)
            {
                baselineTrackedCount = trackedMobs.Count;
                baselineSource = "live";
            }
            else if (sameLastCommittedIdentity &&
                     s_lastCommittedTrackedCount > 0 &&
                     !replacingExplicitlyDisposedSameIdentityLevel)
            {
                baselineTrackedCount = s_lastCommittedTrackedCount;
                baselineSource = "last_commit";
            }
            else if (replacingExplicitlyDisposedSameIdentityLevel)
            {
                // Boss-cell/main-level replacement can intentionally dispose the old Level
                // and rebuild the same run/level identity with a different native Level object.
                // The old last-commit count is not a valid completeness baseline here; rejecting
                // this first non-empty registry leaves the client at zero tracked mobs forever.
                baselineTrackedCount = 0;
                baselineSource = "disposed_same_identity_replacement";
            }

            if (TryGetAuthoritativeGameplayLevel(out var authoritativeLevel, out _))
            {
                var candidateMatchesAuthoritative =
                    DoesLevelMatchIdentity(level, candidateIdentityToken, authoritativeLevel);
                var currentMatchesAuthoritative =
                    DoesLevelMatchIdentity(currentLevel, s_levelIdentityToken, authoritativeLevel);
                var lastCommittedMatchesAuthoritative =
                    DoesStoredIdentityMatchLevel(s_lastCommittedLevelId, s_lastCommittedIdentityToken, authoritativeLevel);

                var authoritativeBaselineTrackedCount = 0;
                var authoritativeBaselineSource = string.Empty;
                if (currentMatchesAuthoritative && trackedMobs.Count > 0)
                {
                    authoritativeBaselineTrackedCount = trackedMobs.Count;
                    authoritativeBaselineSource = "live_authoritative";
                }
                else if (lastCommittedMatchesAuthoritative &&
                         s_lastCommittedTrackedCount > 0 &&
                         !replacingExplicitlyDisposedSameIdentityLevel)
                {
                    authoritativeBaselineTrackedCount = s_lastCommittedTrackedCount;
                    authoritativeBaselineSource = "last_commit_authoritative";
                }

                if (authoritativeBaselineTrackedCount > baselineTrackedCount)
                {
                    baselineTrackedCount = authoritativeBaselineTrackedCount;
                    baselineSource = authoritativeBaselineSource;
                }

                // Do not let a side/stale level replace the live gameplay combat level with an empty tracked set.
                if (authoritativeBaselineTrackedCount > 0 &&
                    !candidateMatchesAuthoritative &&
                    candidateTrackedCount <= 0)
                {
                    reason = "non_active_level_rebuild";
                    return false;
                }
            }

            if (replacingExplicitlyDisposedSameIdentityLevel)
            {
                if (candidateEntityCount <= 0)
                {
                    reason = "disposed_same_identity_entities_empty";
                    return false;
                }

                reason = "accepted_disposed_same_identity_replacement";
                return true;
            }

            if (baselineTrackedCount > 0)
            {
                if (candidateTrackedCount <= 0)
                {
                    reason = "same_identity_empty";
                    return false;
                }

                if (candidateTrackedCount < baselineTrackedCount)
                {
                    reason = "same_identity_partial";
                    return false;
                }

                if (candidateEntityCount <= 0)
                {
                    reason = "same_identity_entities_empty";
                    return false;
                }
            }

            reason = "accepted";
            return true;
        }

        private static int AddTrackedMobLocked(Mob mob)
        {
            if (mob == null)
                return -1;

            var existingIndex = FindTrackedMobIndexLocked(mob);
            if (existingIndex >= 0)
                return existingIndex;

            var syncId = -1;
            if (TryGetMobSyncId(mob, out syncId) && TryGetTrackedMobBySyncIdLocked(syncId, out var existingMob) && existingMob != null)
            {
                existingIndex = FindExactTrackedMobIndexLocked(existingMob);
                if (existingIndex < 0)
                {
                    LogRemoveAttemptLocked(existingMob, syncId, "add_tracked_stale_forward");
                    IdToMob.Remove(syncId);
                }
                else
                {
                    // A second HaxeProxy wrapper may refer to the same native mob. Never replace the
                    // canonical tracked wrapper with that transient alias: unregistering the alias
                    // would then remove the real mob and every later hit becomes missing_sync_id.
                    if (!ReferenceEquals(existingMob, mob))
                    {
                        s_mobSyncAliases.Remove(mob);
                        s_mobSyncAliases.Add(mob, new MobSyncAlias
                        {
                            SyncId = syncId,
                            Generation = s_levelIdentityGeneration
                        });
                    }
                    ValidateTrackedIntegrityLocked("track_existing");
                    return existingIndex;
                }
            }

            // Clients must not append an unbound transient proxy wrapper. Their sync ids come
            // from the level registry/host; adding a wrapper with no id bloats trackedMobs and can
            // later displace the canonical entry. Hosts may allocate ids for runtime spawns.
            if (syncId < 0 && LobbySession.NetRef?.IsHost != true)
                return -1;

            trackedMobs.Add(mob);
            var addedIndex = trackedMobs.Count - 1;
            trackedMobIndices[mob] = addedIndex;
            if (syncId >= 0)
            {
                IdToMob[syncId] = mob;
                MobToId[mob] = syncId;
            }
            ValidateTrackedIntegrityLocked("track_add");
            return addedIndex;
        }

        private static void ResetMobTrackingLocked(string reason)
        {
            s_lastResetReason = reason ?? string.Empty;
            s_lastResetLevelRef = currentLevel == null ? null : new WeakReference<Level>(currentLevel);
            s_lastResetLevelId = GetLevelTraceIdSafe(currentLevel);
            s_lastResetIdentityToken = s_levelIdentityToken;
            s_lastResetTrackedCount = trackedMobs.Count;
            MobSyncTrace.LogTrackingReset(
                s_lastResetReason,
                MobSyncNetRoleForTrace(LobbySession.NetRef),
                GetLevelTraceIdSafe(currentLevel),
                GetLevelRuntimeKey(currentLevel),
                trackedMobs.Count,
                s_levelIdentityReady,
                s_levelIdentityToken,
                GetLastResetLevelRuntimeKeyLocked(),
                s_lastResetTrackedCount,
                s_lastResetIdentityToken,
                GetLastCommittedLevelRuntimeKeyLocked(),
                s_lastCommittedTrackedCount,
                s_lastCommittedIdentityToken);
            ResetMobTrackingStateLocked();
        }

        internal static void ResetForFullGameDispose(string reason)
        {
            lock (Sync)
            {
                ResetMobTrackingLocked(string.IsNullOrWhiteSpace(reason)
                    ? "full_game_dispose"
                    : reason);

                // A synchronized restart may reuse the same run/level identity token.
                // Previous-level duplicate protection must not reject the first, still
                // partially constructed entity pass of the new Game instance.
                s_lastCommittedLevelRef = null;
                s_lastCommittedLevelId = string.Empty;
                s_lastCommittedIdentityToken = 0;
                s_lastCommittedTrackedCount = 0;
                s_lastResetLevelRef = null;
                s_lastResetLevelId = string.Empty;
                s_lastResetIdentityToken = 0;
                s_lastResetTrackedCount = 0;
                s_lastIgnoredDuplicateLevelId = string.Empty;
                s_lastIgnoredDuplicateIdentityToken = 0;
                s_levelIdentityGeneration = 0;
            }

            try { LobbySession.NetRef?.ClearMobSyncQueues(); } catch { }
        }

        private static void ResetMobTrackingStateLocked()
        {
            trackedMobs.Clear();
            trackedMobIndices.Clear();
            IdToMob.Clear();
            MobToId.Clear();
            s_mobSyncAliases = new ConditionalWeakTable<Mob, MobSyncAlias>();
            ClearHostMobStallRecoveryLocked();
            ResetPlayerCombatStateRepairLocked();
            // nextRuntimeSyncId is deliberately NOT reset here. It is the host's NetId allocator and
            // must stay monotonic for the whole session: the wire generation is a pure hash of map
            // id + seed, so two rebuilds of the SAME level carry the same generation and restarting
            // the counter would hand id 0..N to a different set of mobs while packets addressed to
            // the previous set are still in flight. Monotonic ids make that mis-binding impossible;
            // a stale id simply resolves to nothing instead of resolving to the wrong enemy.
            s_pendingCulledMobDeaths.Clear();
            s_pendingCulledMobDeathFirstFrame.Clear();
            clientMobTargets.Clear();
            clientCachedAttackTargetByMob.Clear();
            clientQueuedOldSkillMarkers.Clear();
            hostLastSentContactTargetUserIdByMob.Clear();
            clientLastReportedMobLife.Clear();
            clientLastSentAffectPayloadBySyncId.Clear();
            clientLastSentDrawStateBySyncId.Clear();
            clientLastAppliedHostAffectPayloadBySyncId.Clear();
            clientLastAppliedHostAffectMobBySyncId.Clear();
            hostLastAppliedClientAffectPayloadBySyncId.Clear();
            hostClientOwnedAffectIdsByMob.Clear();
            clientLastAppliedAnimPayloadByMob.Clear();
            clientLastAnimationApplyFrameByMob.Clear();
            clientLastForcedBossAnimByMob.Clear();
            s_hostBossPartWatch.Clear();
            clientActiveNetworkAttackMobs.Clear();
            clientBossSkillCallbackLeaseMobs.Clear();
            clientNetworkAttackStartFrame.Clear();
            clientAiLockedMobs.Clear();
            clientPendingSuppressedBossDies.Clear();
            clientCompletedAuthoritativeBossDeaths.Clear();
            ResetBossDeathWatchdogStateLocked();
            ResetBossIdentityStateLocked();
            clientPendingSuppressedMobDies.Clear();
            clientAuthoritativeStateSeenSyncIds.Clear();
            parsedAnimPayloadCache.Clear();
            hostMobTypeBySyncId.Clear();
            ClearHostClientInterestLocked();
            hostLastSentMobStatesBySyncId.Clear();
            clientLastAcceptedHostPositionFrameBySyncId.Clear();
            s_lastHostActiveReliableKeyframeFrame = -99999.0;
            s_lastHostActiveReliableKeyframeToken = 0;
            s_lastHostBossReliableKeyframeFrame = -99999.0;
            s_lastHostBossReliableKeyframeToken = 0;
            s_ghostHitMissBySyncId.Clear();
            s_ghostHitMissGeneration = 0;
            // Sync ids are re-issued per level, so a retained rate-limit entry would suppress the
            // first reconcile for an unrelated mob in the new level.
            s_lastHitReconcileTicksBySyncId.Clear();
            // Boss-part despawn watch is keyed by sync id, and sync ids are re-issued per level.
            // Carrying entries across a level change let a stale watch for the OLD level fire a
            // despawn MOBDIE against whatever now owns that id in the NEW level. (The client's
            // generation fence rejected it, so it never killed anything - but the host was emitting
            // bogus deaths and the watch grew without bound.) Its reset existed and was never
            // called; this is that call site.
            ResetHostBossPartWatchLocked();
            // Per-encounter boss arena state must not survive into the next level/fight.
            BeholderArenaSync.Reset();
            s_hostDeathTombstonesBySyncId.Clear();
            s_clientMobTombstonesBySyncId.Clear();
            s_lastHostAuthoritativeFullResyncFrame = -99999.0;
            s_lastHostAuthoritativeFullResyncToken = 0;
            s_hostAuthoritativeBootstrapResyncsRemaining = 0;
            s_lastHostMobRegistryToken = 0;
            s_lastHostMobRegistrySendFrame = -99999.0;
            s_hostMobRegistryResendsRemaining = 0;
            hostDetectedTargets.Clear();

            // Scratch collections can retain destroyed Haxe proxy references across levels when an
            // exception interrupts a consume/send pass. They are not game state; always drop them.
            s_clientDetectedTargetsScratch.Clear();
            s_batchMobsScratch.Clear();
            s_batchSnapshotsScratch.Clear();
            s_clientAffectAppliesScratch.Clear();
            s_hostStateAppliesScratch.Clear();
            s_pendingMobHitAppliesScratch.Clear();
            s_mobHitMergeScratch.Clear();
            clientPendingBossAttacks.Clear();
            s_resolvedClientBossAttacksScratch.Clear();
            s_drawsScratch.Clear();
            s_moveSnapshotsScratch.Clear();
            s_dieVictimsScratch.Clear();
            s_dieVictimDedupScratch.Clear();
            s_usedTrackedMobsScratch.Clear();
            s_latestPacketSyncIdsScratch.Clear();
            s_ghostDespawnEchoScratch.Clear();
            s_hostDeathTombstoneScratch.Clear();
            s_hostDeathTombstoneStateScratch.Clear();
            s_hostDeathTombstoneRemoveScratch.Clear();
            s_validationSeenMobsScratch.Clear();
            s_validationSeenSyncIdsScratch.Clear();

            clientNetworkQueuedAttackDepth = 0;
            clientNetworkQueuedAttackMob = null;
            clientNetworkAttackReplayDepth = 0;
            clientNetworkAttackReplayMob = null;
            authoritativeClientBossDieDepth = 0;
            authoritativeClientMobDieDepth = 0;
            suppressClientAffectDirtyDepth = 0;
            suppressMobDieSendDepth = 0;
            suppressMobHitSendDepth = 0;
            forceExactNemesisTargetDepth = 0;

            s_trackedMobValidationPending = true;
            s_syncMobTypeCache.Clear();
            ClearQueuedDirtyStateLocked();
            currentLevel = null;
            s_levelIdentityReady = false;
            s_levelIdentityToken = 0;
            s_lastIgnoredDuplicateLevelId = string.Empty;
            s_lastIgnoredDuplicateIdentityToken = 0;
        }

        private static bool IsLevelIdentityReadyLocked(Level? level)
        {
            return DoesLevelMatchCurrentIdentityLocked(level);
        }

        private static bool DoesLevelMatchCurrentIdentityLocked(Level? level)
        {
            if (!s_levelIdentityReady || level == null || s_levelIdentityToken <= 0)
                return false;

            if (currentLevel != null && ReferenceEquals(currentLevel, level))
                return true;

            var currentLevelId = GetLevelTraceIdSafe(currentLevel);
            var candidateLevelId = GetLevelTraceIdSafe(level);
            if (!string.IsNullOrEmpty(currentLevelId) &&
                !string.IsNullOrEmpty(candidateLevelId) &&
                !string.Equals(currentLevelId, candidateLevelId, StringComparison.Ordinal))
            {
                return false;
            }

            var candidateIdentityToken = ComputeLevelIdentityToken(level);
            return candidateIdentityToken > 0 && candidateIdentityToken == s_levelIdentityToken;
        }

        private static bool IsIncomingMobIdentityReady()
        {
            lock (Sync)
            {
                return s_levelIdentityReady && currentLevel != null && s_levelIdentityToken > 0;
            }
        }

        private static void RemoveTrackedMobLocked(Mob mob, string reason)
        {
            if (mob == null)
                return;

            s_trackedMobValidationPending = true;
            var mappedSyncId = -1;
            if (MobToId.TryGetValue(mob, out var ownedSyncId))
                mappedSyncId = ownedSyncId;
            LogRemoveAttemptLocked(mob, mappedSyncId, reason);
            var index = FindExactTrackedMobIndexLocked(mob);
            if (index >= 0)
            {
                RemoveTrackedMobAtIndexLocked(index, reason, alreadyLogged: true);
                return;
            }

            // Client-only: a canonical owner that is no longer in trackedMobs can still hold a
            // live host-owned mapping. Capture the tombstone before wiping it. An alias whose
            // MobToId is missing, or whose IdToMob points at a different wrapper, must not wipe
            // the canonical identity. Host keeps the previous alias-only cleanup.
            if (mappedSyncId > 0 && IsClient(LobbySession.NetRef))
            {
                var forwardIsThis = IdToMob.TryGetValue(mappedSyncId, out var forwardMob) &&
                                    ReferenceEquals(forwardMob, mob);
                var forwardMissing = !IdToMob.TryGetValue(mappedSyncId, out var existingForward) ||
                                     existingForward == null;
                if (forwardIsThis || forwardMissing)
                {
                    TryCaptureClientTombstoneForMappedMobLocked(mob, reason);
                    IdToMob.Remove(mappedSyncId);
                    MobToId.Remove(mob);
                }
            }

            // This can be a temporary managed wrapper for a still-live canonical native mob. Remove
            // only alias-local caches; never remove IdToMob/MobToId owned by the canonical wrapper.
            s_mobSyncAliases.Remove(mob);
            clientMobTargets.Remove(mob);
            clientCachedAttackTargetByMob.Remove(mob);
            clientQueuedOldSkillMarkers.Remove(mob);
            hostLastSentContactTargetUserIdByMob.Remove(mob);
            clientLastReportedMobLife.Remove(mob);
            clientLastAppliedAnimPayloadByMob.Remove(mob);
            clientLastAnimationApplyFrameByMob.Remove(mob);
            clientLastForcedBossAnimByMob.Remove(mob);
            clientActiveNetworkAttackMobs.Remove(mob);
            clientBossSkillCallbackLeaseMobs.Remove(mob);
            clientNetworkAttackStartFrame.Remove(mob);
            clientAiLockedMobs.Remove(mob);
        }

        private static bool ShouldRetainMobSyncIdOnTemporaryUnregisterLocked(Level? level, Mob mob)
        {
            if (mob == null || level == null)
                return false;
            if (!MobToId.ContainsKey(mob))
                return false;

            try
            {
                if (mob.destroyed || mob.life <= 0)
                    return false;
                if (!DoesLevelMatchCurrentIdentityLocked(level) || !DoesLevelMatchCurrentIdentityLocked(mob._level))
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static void DetachTrackedMobForTemporaryUnregisterLocked(Mob mob)
        {
            if (mob == null || !MobToId.TryGetValue(mob, out var syncId) || syncId < 0)
                return;

            // A live mob can be unregistered briefly while sleeping, teleporting, transforming,
            // or entering/leaving an elite phase. Removing IdToMob here made the peer's next hit
            // fail with missing_sync_id even though MobToId still claimed that the mob owned the id.
            //
            // Keep the complete canonical mapping and tracked-list entry until the mob is actually
            // destroyed, dies, changes level, or the level registry is rebuilt. Only transient
            // attack/visual caches are cleared so an interrupted skill is not replayed forever.
            IdToMob[syncId] = mob;
            var index = FindExactTrackedMobIndexLocked(mob);
            if (index < 0)
            {
                trackedMobs.Add(mob);
                index = trackedMobs.Count - 1;
            }
            trackedMobIndices[mob] = index;

            s_mobSyncAliases.Remove(mob);
            clientCachedAttackTargetByMob.Remove(mob);
            clientQueuedOldSkillMarkers.Remove(mob);
            hostLastSentContactTargetUserIdByMob.Remove(mob);
            clientActiveNetworkAttackMobs.Remove(mob);
            clientBossSkillCallbackLeaseMobs.Remove(mob);
            clientNetworkAttackStartFrame.Remove(mob);
            clientAiLockedMobs.Remove(mob);
            s_trackedMobValidationPending = true;

            MobSyncTrace.LogBindSyncId(
                "temporary_unregister_retained",
                syncId,
                BuildMobStateTypeSignature(mob),
                GetWorldX(mob),
                GetWorldY(mob));
        }

        private static int FindExactTrackedMobIndexLocked(Mob mob)
        {
            if (mob == null)
                return -1;

            if (trackedMobIndices.TryGetValue(mob, out var directIndex))
            {
                if (directIndex >= 0 && directIndex < trackedMobs.Count && ReferenceEquals(trackedMobs[directIndex], mob))
                    return directIndex;
                trackedMobIndices.Remove(mob);
            }

            for (var i = 0; i < trackedMobs.Count; i++)
            {
                if (ReferenceEquals(trackedMobs[i], mob))
                {
                    trackedMobIndices[mob] = i;
                    return i;
                }
            }

            return -1;
        }

        private static void RemoveTrackedMobAtIndexLocked(int index, string reason, bool alreadyLogged = false)
        {
            if (index < 0 || index >= trackedMobs.Count)
                return;

            s_trackedMobValidationPending = true;
            var mob = trackedMobs[index];
            if (!alreadyLogged)
            {
                var mappedSyncId = -1;
                if (mob != null && MobToId.TryGetValue(mob, out var ownedSyncId))
                    mappedSyncId = ownedSyncId;
                LogRemoveAttemptLocked(mob, mappedSyncId, reason);
            }
            TryCaptureClientTombstoneForMappedMobLocked(mob, reason);
            CleanupTrackedMobCachesLocked(mob);
            if (mob != null)
            {
                if (MobToId.Remove(mob, out var _sid))
                    IdToMob.Remove(_sid);
                s_mobSyncAliases.Remove(mob);
                trackedMobIndices.Remove(mob);
            }

            var lastIndex = trackedMobs.Count - 1;
            if (index != lastIndex)
            {
                var movedMob = trackedMobs[lastIndex];
                trackedMobs[index] = movedMob;
                if (movedMob != null)
                    trackedMobIndices[movedMob] = index;
            }

            trackedMobs.RemoveAt(lastIndex);
            ValidateTrackedIntegrityLocked("track_remove");
        }

        private static void CleanupTrackedMobCachesLocked(Mob? mob)
        {
            if (mob == null)
                return;

            clientPendingSuppressedBossDies.Remove(mob);
            clientPendingSuppressedMobDies.Remove(mob);
            try
            {
                if (mob.destroyed)
                    hostClientOwnedAffectIdsByMob.Remove(mob);
            }
            catch
            {
            }
            trackedMobIndices.Remove(mob);
            clientMobTargets.Remove(mob);
            clientCachedAttackTargetByMob.Remove(mob);
            clientQueuedOldSkillMarkers.Remove(mob);
            hostLastSentContactTargetUserIdByMob.Remove(mob);
            clientLastReportedMobLife.Remove(mob);
            clientLastAppliedAnimPayloadByMob.Remove(mob);
            clientLastAnimationApplyFrameByMob.Remove(mob);
            clientLastForcedBossAnimByMob.Remove(mob);
            clientActiveNetworkAttackMobs.Remove(mob);
            clientBossSkillCallbackLeaseMobs.Remove(mob);
            clientNetworkAttackStartFrame.Remove(mob);
            clientAiLockedMobs.Remove(mob);

            // Cache cleanup must only clear a mob that owns this exact managed registration. A
            // transient HaxeProxy wrapper must never delete the canonical entity's sync mapping.
            if (!MobToId.TryGetValue(mob, out var syncId))
                return;

            ClearPerSyncIdCachesLocked(syncId);
        }

        private static void ClearPerSyncIdCachesLocked(int syncId)
        {
            if (syncId < 0)
                return;

            IdToMob.Remove(syncId);
            RemoveHostMobStallRecoveryLocked(syncId);
            clientLastSentAffectPayloadBySyncId.Remove(syncId);
            clientLastSentDrawStateBySyncId.Remove(syncId);
            clientLastAppliedHostAffectPayloadBySyncId.Remove(syncId);
            clientLastAppliedHostAffectMobBySyncId.Remove(syncId);
            hostLastAppliedClientAffectPayloadBySyncId.Remove(syncId);
            hostMobTypeBySyncId.Remove(syncId);
            hostClientInterestUsersBySyncId.Remove(syncId);
            hostLastSentMobStatesBySyncId.Remove(syncId);
            hostObservedMobStatesBySyncId.Remove(syncId);
            hostDirtyFlagsBySyncId.Remove(syncId);
            hostDirtyQueuedSyncIds.Remove(syncId);
            clientAuthoritativeStateSeenSyncIds.Remove(syncId);
            clientObservedDrawStateBySyncId.Remove(syncId);
            clientDirtyFlagsBySyncId.Remove(syncId);
            clientDirtyQueuedSyncIds.Remove(syncId);
        }

        private static int FindTrackedMobIndexLocked(Mob mob)
        {
            if (mob == null || trackedMobs.Count == 0)
                return -1;

            if (trackedMobIndices.TryGetValue(mob, out var directIndex))
            {
                if (directIndex >= 0 && directIndex < trackedMobs.Count && ReferenceEquals(trackedMobs[directIndex], mob))
                    return directIndex;

                trackedMobIndices.Remove(mob);
                s_trackedMobValidationPending = true;
            }

            // HaxeProxy can expose another managed wrapper for the same native mob. Match it by
            // current level, runtime class and near-identical position. Unlike Mob.__uid, this does
            // not alias every enemy of the same class.
            //
            // Crowded packs used to return -1 on any ambiguity, which made TryGetMobSyncId mint a
            // SECOND NetId for the same native enemy (registry_duplicate_twin / missing_sync_id).
            // Prefer an already-mapped candidate; only reject when several unmapped equals collide.
            var matchedIndex = -1;
            var matchedSyncId = int.MaxValue;
            var matchedHasId = false;
            var unmappedEquals = 0;
            for (int i = 0; i < trackedMobs.Count; i++)
            {
                var candidate = trackedMobs[i];
                if (!AreLikelySameNativeMobProxy(candidate, mob))
                    continue;

                var hasId = false;
                var candidateSyncId = 0;
                if (candidate != null && MobToId.TryGetValue(candidate, out candidateSyncId) && candidateSyncId > 0)
                    hasId = true;
                if (hasId)
                {
                    if (!matchedHasId || candidateSyncId < matchedSyncId)
                    {
                        matchedIndex = i;
                        matchedSyncId = candidateSyncId;
                        matchedHasId = true;
                    }
                    continue;
                }

                if (!matchedHasId)
                {
                    unmappedEquals++;
                    if (matchedIndex < 0)
                        matchedIndex = i;
                }
            }

            if (matchedIndex < 0)
                return -1;

            // Multiple unmapped equals and no mapped owner — refuse rather than guess.
            if (!matchedHasId && unmappedEquals > 1)
                return -1;

            var canonical = trackedMobs[matchedIndex];
            if (canonical != null)
                trackedMobIndices[canonical] = matchedIndex;
            return matchedIndex;
        }

        /// <summary>
        /// Host-only: if any already-mapped tracked wrapper is the same native enemy, reuse that
        /// NetId instead of minting a duplicate. Caller holds <see cref="Sync"/>.
        /// </summary>
        private static bool TryAttachAliasToMappedNativeProxyLocked(Mob mob, out int syncId)
        {
            syncId = -1;
            if (mob == null)
                return false;

            Mob? best = null;
            var bestSyncId = int.MaxValue;
            for (var i = 0; i < trackedMobs.Count; i++)
            {
                var candidate = trackedMobs[i];
                if (candidate == null || ReferenceEquals(candidate, mob))
                    continue;
                if (!MobToId.TryGetValue(candidate, out var candidateSyncId) || candidateSyncId <= 0)
                    continue;
                if (!AreLikelySameNativeMobProxy(candidate, mob))
                    continue;
                if (candidateSyncId >= bestSyncId)
                    continue;

                best = candidate;
                bestSyncId = candidateSyncId;
            }

            if (best == null || bestSyncId == int.MaxValue)
                return false;

            syncId = bestSyncId;
            s_mobSyncAliases.Remove(mob);
            s_mobSyncAliases.Add(mob, new MobSyncAlias
            {
                SyncId = syncId,
                Generation = s_levelIdentityGeneration
            });
            return true;
        }

        private static bool AreLikelySameNativeMobProxy(Mob? left, Mob? right)
        {
            if (left == null || right == null)
                return false;
            if (ReferenceEquals(left, right))
                return true;

            try
            {
                if (!DoesLevelMatchCurrentIdentityLocked(left._level) ||
                    !DoesLevelMatchCurrentIdentityLocked(right._level))
                {
                    return false;
                }
                if (!string.Equals(
                        GetMobRuntimeClassKeySafe(left),
                        GetMobRuntimeClassKeySafe(right),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var dx = GetWorldX(left) - GetWorldX(right);
                var dy = GetWorldY(left) - GetWorldY(right);
                if (double.IsFinite(dx) && double.IsFinite(dy) && dx * dx + dy * dy <= PixelsPerCase * PixelsPerCase)
                    return true;

                // Pixel distance alone can rule out two copies of the SAME native enemy when the
                // peers' coordinates drift by a fraction of a tile (spawn snapping, gravity
                // landing, interpolation: 1000.0 vs 1000.1). Same-cell is the tie-breaker.
                GetMobWorldCells(left, out var lcx, out var lcy);
                GetMobWorldCells(right, out var rcx, out var rcy);
                return lcx == rcx && lcy == rcy;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetTrackedMobBySyncIdLocked(int syncId, out Mob? mob)
        {
            mob = null;
            if (syncId < 0)
                return false;

            if (!IdToMob.TryGetValue(syncId, out var mappedMob) || mappedMob == null)
            {
                // Repair a one-sided registry entry. Earlier temporary-unregister handling could
                // leave MobToId intact while removing IdToMob, which made every later client hit
                // look like a missing sync id. Recover only a unique live same-level owner.
                Mob? reverseCandidate = null;
                var reverseCandidates = 0;
                foreach (var pair in MobToId)
                {
                    if (pair.Value != syncId || pair.Key == null)
                        continue;
                    if (!IsStateRebindCandidateLocked(pair.Key))
                        continue;

                    reverseCandidate = pair.Key;
                    reverseCandidates++;
                    if (reverseCandidates > 1)
                        break;
                }

                if (reverseCandidates == 1 && reverseCandidate != null)
                {
                    mappedMob = reverseCandidate;
                    IdToMob[syncId] = mappedMob;
                    var repairedIndex = FindExactTrackedMobIndexLocked(mappedMob);
                    if (repairedIndex < 0)
                    {
                        trackedMobs.Add(mappedMob);
                        repairedIndex = trackedMobs.Count - 1;
                    }
                    trackedMobIndices[mappedMob] = repairedIndex;
                    s_trackedMobValidationPending = true;
                    MobSyncTrace.LogBindSyncId(
                        "reverse_registry_repair",
                        syncId,
                        BuildMobStateTypeSignature(mappedMob),
                        GetWorldX(mappedMob),
                        GetWorldY(mappedMob));
                }
                else
                {
                    return false;
                }
            }

            if (mappedMob == null)
            {
                MobSyncTrace.LogStaleTrackedMapping(syncId, -1, "null_mob");
                LogRemoveAttemptLocked(null, syncId, "stale_null_mob");
                IdToMob.Remove(syncId);
                s_trackedMobValidationPending = true;
                return false;
            }

            var localIndex = FindTrackedMobIndexLocked(mappedMob);
            if (localIndex < 0)
            {
                // An alive mob can be temporarily unregistered/re-wrapped when sleeping, teleporting
                // or changing elite phase. Keep its authoritative id and reattach the live object
                // instead of pruning the mapping and allocating a replacement id.
                if (IsStateRebindCandidateLocked(mappedMob))
                {
                    trackedMobs.Add(mappedMob);
                    localIndex = trackedMobs.Count - 1;
                    trackedMobIndices[mappedMob] = localIndex;
                    MobToId[mappedMob] = syncId;
                    s_trackedMobValidationPending = true;
                    MobSyncTrace.LogBindSyncId(
                        "reattach_live_mapping",
                        syncId,
                        BuildMobStateTypeSignature(mappedMob),
                        GetWorldX(mappedMob),
                        GetWorldY(mappedMob));
                }
                else
                {
                    MobSyncTrace.LogStaleTrackedMapping(syncId, localIndex, "untracked_mob");
                    LogRemoveAttemptLocked(mappedMob, syncId, "stale_untracked_mob");
                    IdToMob.Remove(syncId);
                    s_trackedMobValidationPending = true;
                    return false;
                }
            }

            var canonicalMob = trackedMobs[localIndex];
            if (canonicalMob == null)
            {
                LogRemoveAttemptLocked(null, syncId, "stale_canonical_null");
                IdToMob.Remove(syncId);
                s_trackedMobValidationPending = true;
                return false;
            }

            if (!ReferenceEquals(mappedMob, canonicalMob))
            {
                // Repair a wrapper swap without changing the native mob identity or sync id.
                IdToMob[syncId] = canonicalMob;
                MobToId[canonicalMob] = syncId;
                mappedMob = canonicalMob;
            }

            if (!MobToId.TryGetValue(mappedMob, out var mappedSyncId) || mappedSyncId != syncId)
            {
                // Two failure modes share this branch:
                //  * the reverse mapping (MobToId) was lost while the forward entry survived, or
                //  * the same enemy was rebound to a different id — host wrapper/proxy duplication
                //    hands one native mob two NetIds, so IdToMob[syncId] points at a mob whose
                //    MobToId now names another id.
                // The old remove-and-fail turned both into dropped states and lost client damage,
                // which is exactly the registry_mismatch / missing_sync_id churn. Heal instead.

                // If another live tracked mob genuinely owns this id, repair the forward entry to
                // point at it (the previous owner is a stale wrapper of the same native enemy).
                Mob? trueOwner = null;
                var ownerCount = 0;
                for (var i = 0; i < trackedMobs.Count; i++)
                {
                    var candidate = trackedMobs[i];
                    if (candidate == null || ReferenceEquals(candidate, mappedMob))
                        continue;
                    if (MobToId.TryGetValue(candidate, out var candidateId) &&
                        candidateId == syncId &&
                        IsStateRebindCandidateLocked(candidate))
                    {
                        trueOwner = candidate;
                        ownerCount++;
                        if (ownerCount > 1)
                            break;
                    }
                }

                if (ownerCount == 1 && trueOwner != null)
                {
                    IdToMob[syncId] = trueOwner;
                    s_trackedMobValidationPending = true;
                    MobSyncTrace.LogBindSyncId(
                        "registry_mismatch_repaired",
                        syncId,
                        BuildMobStateTypeSignature(trueOwner),
                        GetWorldX(trueOwner),
                        GetWorldY(trueOwner));
                    mob = trueOwner;
                    return true;
                }

                // The forward entry points at a mob that now owns a different id — usually a
                // duplicate NetId minted for a second HaxeProxy wrapper of the same native enemy.
                // Keep BOTH forward ids resolving to the canonical wrapper so peer hits addressed
                // to the duplicate id still land. Dropping IdToMob[dup] was the missing_sync_id
                // path for legitimate high syncIds after registry_alias.
                if (IsStateRebindCandidateLocked(mappedMob))
                {
                    if (mappedSyncId <= 0)
                    {
                        IdToMob[syncId] = mappedMob;
                        MobToId[mappedMob] = syncId;
                    }
                    else
                    {
                        // Canonical reverse mapping stays on mappedSyncId; duplicate syncId only
                        // keeps a forward alias so old packets/hits still resolve.
                        IdToMob[syncId] = mappedMob;
                        IdToMob[mappedSyncId] = mappedMob;
                        MobToId[mappedMob] = mappedSyncId;
                    }

                    s_trackedMobValidationPending = true;
                    MobSyncTrace.LogIncomingMappingMismatch(
                        "registry_alias",
                        syncId,
                        BuildMobStateTypeSignature(mappedMob),
                        string.Empty,
                        mappedSyncId > 0 ? $"aliased_to:{mappedSyncId}" : "reverse_mapping_lost");
                    mob = mappedMob;
                    return true;
                }

                MobSyncTrace.LogStaleTrackedMapping(
                    syncId,
                    localIndex,
                    mappedSyncId == syncId ? "registry_missing" : $"registry_mismatch:{mappedSyncId}");
                LogRemoveAttemptLocked(
                    mappedMob,
                    syncId,
                    mappedSyncId == syncId ? "stale_registry_missing" : "stale_registry_mismatch");
                IdToMob.Remove(syncId);
                s_trackedMobValidationPending = true;
                return false;
            }

            mob = mappedMob;
            return true;
        }

        private static void InvalidateTrackedSyncCacheLocked(int syncId, string reason)
        {
            if (syncId < 0)
                return;

            if (IdToMob.TryGetValue(syncId, out var mappedMob) && mappedMob != null)
            {
                MobSyncTrace.LogStaleTrackedMapping(syncId, FindTrackedMobIndexLocked(mappedMob), reason);
                LogRemoveAttemptLocked(mappedMob, syncId, "invalidate:" + reason);
                if (MobToId.TryGetValue(mappedMob, out var reverseSyncId) && reverseSyncId == syncId)
                    MobToId.Remove(mappedMob);
            }
            else
            {
                LogRemoveAttemptLocked(null, syncId, "invalidate:" + reason);
            }

            IdToMob.Remove(syncId);
            s_trackedMobValidationPending = true;
        }

        private static void ValidateTrackedIntegrityLocked(string reason)
        {
            if (!MobSyncTrace.AssertEnabled)
                return;

            s_validationSeenMobsScratch.Clear();
            s_validationSeenSyncIdsScratch.Clear();

            for (int i = 0; i < trackedMobs.Count; i++)
            {
                var mob = trackedMobs[i];
                if (mob == null)
                {
                    MobSyncTrace.LogInvariantViolation(reason, $"null tracked mob at localIndex={i}");
                    continue;
                }

                if (!s_validationSeenMobsScratch.Add(mob))
                    MobSyncTrace.LogInvariantViolation(reason, $"duplicate tracked mob localIndex={i} type={BuildMobStateTypeSignature(mob)}");

                if (!trackedMobIndices.TryGetValue(mob, out var directIndex) || directIndex != i)
                    MobSyncTrace.LogInvariantViolation(reason, $"trackedMobIndices mismatch localIndex={i} directIndex={directIndex}");

                if (!MobToId.TryGetValue(mob, out var syncId))
                    continue;

                if (!s_validationSeenSyncIdsScratch.Add(syncId))
                    MobSyncTrace.LogInvariantViolation(reason, $"duplicate syncId among tracked mobs syncId={syncId} localIndex={i}");

                if (!IdToMob.TryGetValue(syncId, out var mappedMob) || !ReferenceEquals(mappedMob, mob))
                    MobSyncTrace.LogInvariantViolation(reason, $"IdToMob mismatch syncId={syncId} localIndex={i}");
            }

            // The first pass intentionally records every tracked mob/id. Start fresh before
            // validating IdToMob itself; otherwise every valid dictionary entry is reported as a
            // duplicate merely because it was already seen through trackedMobs.
            s_validationSeenMobsScratch.Clear();
            s_validationSeenSyncIdsScratch.Clear();

            foreach (var pair in IdToMob)
            {
                var syncId = pair.Key;
                var mob = pair.Value;
                if (mob == null)
                {
                    MobSyncTrace.LogInvariantViolation(reason, $"IdToMob null mob syncId={syncId}");
                    continue;
                }

                if (!s_validationSeenSyncIdsScratch.Add(syncId))
                    MobSyncTrace.LogInvariantViolation(reason, $"duplicate syncId in IdToMob syncId={syncId}");
                if (!s_validationSeenMobsScratch.Add(mob))
                    MobSyncTrace.LogInvariantViolation(reason, $"mob mapped to multiple syncIds type={BuildMobStateTypeSignature(mob)}");

                if (trackedMobIndices.TryGetValue(mob, out var localIndex) &&
                    (localIndex < 0 || localIndex >= trackedMobs.Count || !ReferenceEquals(trackedMobs[localIndex], mob)))
                {
                    MobSyncTrace.LogInvariantViolation(reason, $"IdToMob tracked index drift syncId={syncId} localIndex={localIndex}");
                }

                if (!MobToId.TryGetValue(mob, out var reverseSyncId) || reverseSyncId != syncId)
                    MobSyncTrace.LogInvariantViolation(reason, $"IdToMob reverse lookup mismatch syncId={syncId} reverseSyncId={reverseSyncId}");
            }
        }

        private static void PruneInvalidTrackedMobsLocked()
        {
            if (trackedMobs.Count == 0)
                return;

            if (!s_trackedMobValidationPending)
                return;

            s_trackedMobValidationPending = false;

            for (int i = trackedMobs.Count - 1; i >= 0; i--)
            {
                var mob = trackedMobs[i];
                if (mob == null)
                {
                    RemoveTrackedMobAtIndexLocked(i, "prune_null");
                    continue;
                }

                var shouldRemove = false;
                try
                {
                    // Do not prune by life<=0: some bosses spawn/transition with temporary zero life
                    // and must stay tracked to receive authoritative host life.
                    shouldRemove = mob.destroyed || mob._level == null;
                }
                catch
                {
                    shouldRemove = true;
                }

                if (!shouldRemove)
                {
                    try
                    {
                        var mobLevel = mob._level;
                        shouldRemove = !DoesLevelMatchCurrentIdentityLocked(mobLevel);
                    }
                    catch
                    {
                        shouldRemove = true;
                    }
                }

                if (shouldRemove)
                    RemoveTrackedMobAtIndexLocked(i, "prune_invalid");
            }
        }

        private static bool IsSyncMob(Mob? mob)
        {
            if (!MultiplayerSettingsStorage.EnableMobsSync)
                return false;

            if (mob == null)
                return false;

            try
            {
                if (mob.destroyed || mob._level == null)
                    return false;

                if (BossSyncConstants.DisableBossSyncTemporarily && BossSyncHelpers.IsBossMob(mob))
                    return false;

                // Primary rule: any combat-hostile mob (including bosses) must be synced.
                if (IsMobHostileToPlayers(mob))
                    return true;

                return IsSyncMobByType(mob);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSyncMobByType(Mob mob)
        {
            return s_syncMobTypeCache.GetOrAdd(mob.GetType(), static (System.Type t) =>
            {
                var typeName = t.FullName ?? t.Name;
                return typeName.Contains("dc.en.boss.", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains(".boss.", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("dc.en.mob.", StringComparison.Ordinal)
                    || typeName.Contains(".Mob", StringComparison.Ordinal)
                    || typeName.Contains(".mob.", StringComparison.Ordinal);
            });
        }

        private static void EnsureMobTracked(Mob mob)
        {
            if (!IsSyncMob(mob))
                return;

            var shouldQueueInitialSync = false;
            lock (Sync)
            {
                var mobLevel = mob._level;
                if (!IsLevelIdentityReadyLocked(mobLevel))
                    return;

                if (FindTrackedMobIndexLocked(mob) >= 0)
                    return;

                if (mob != null)
                {
                    shouldQueueInitialSync = AddTrackedMobLocked(mob) >= 0;
                }
            }

            if (shouldQueueInitialSync && mob != null)
                QueueInitialMobSync(mob);
        }

        private static bool TryGetMobSyncId(Mob mob, out int syncId)
        {
            syncId = -1;
            if (!IsSyncMob(mob))
                return false;

            lock (Sync)
            {
                if (MobToId.TryGetValue(mob, out syncId))
                    return true;

                if (s_mobSyncAliases.TryGetValue(mob, out var alias) &&
                    alias.Generation == s_levelIdentityGeneration &&
                    IdToMob.TryGetValue(alias.SyncId, out var aliasedCanonical) &&
                    aliasedCanonical != null &&
                    FindExactTrackedMobIndexLocked(aliasedCanonical) >= 0)
                {
                    syncId = alias.SyncId;
                    return true;
                }

                s_mobSyncAliases.Remove(mob);
                var canonicalIndex = FindTrackedMobIndexLocked(mob);
                if (canonicalIndex >= 0 && canonicalIndex < trackedMobs.Count)
                {
                    var canonicalMob = trackedMobs[canonicalIndex];
                    if (canonicalMob != null && MobToId.TryGetValue(canonicalMob, out syncId))
                    {
                        if (!ReferenceEquals(canonicalMob, mob))
                        {
                            s_mobSyncAliases.Add(mob, new MobSyncAlias
                            {
                                SyncId = syncId,
                                Generation = s_levelIdentityGeneration
                            });
                        }
                        return true;
                    }
                }

                if (!IsLevelIdentityReadyLocked(mob._level))
                    return false;

                // Clients never invent NetIds — host is the sole authority (native ids diverge).
                if (LobbySession.NetRef?.IsHost != true)
                    return false;

                // Never mint a second NetId for another HaxeProxy wrapper of the same native enemy.
                // Duplicate ids are what produce registry_duplicate_twin on the client and then
                // missing_sync_id hits once the host drops the alias forward map.
                if (TryAttachAliasToMappedNativeProxyLocked(mob, out syncId))
                    return true;

                if (nextRuntimeSyncId < 1)
                    nextRuntimeSyncId = 1;
                syncId = nextRuntimeSyncId++;
                MobToId[mob] = syncId;
                IdToMob[syncId] = mob;
                StampHostBossNetIdLocked(mob, syncId);
                // Dynamic/runtime-spawned mobs must be in the canonical tracked list immediately;
                // otherwise the first dirty packet creates an IdToMob entry that is rejected as
                // untracked_mob on the next dequeue.
                if (FindExactTrackedMobIndexLocked(mob) < 0)
                {
                    trackedMobs.Add(mob);
                    trackedMobIndices[mob] = trackedMobs.Count - 1;
                }
                return true;
            }
        }

        private static Mob? ResolveTrackedMobBySyncIdLocked(int syncId)
        {
            if (syncId < 0)
                return null;

            var hadIdToMob = IdToMob.TryGetValue(syncId, out var preMob) && preMob != null;
            var hadMobToId = preMob != null && MobToId.TryGetValue(preMob, out var preRev) && preRev == syncId;
            var preDestroyed = ReadMobDestroyedSafe(preMob);

            if (TryGetTrackedMobBySyncIdLocked(syncId, out var mappedMob) && mappedMob != null)
            {
                if (IsClient(LobbySession.NetRef) &&
                    IsInvalidMappedReplicaLocked(mappedMob) &&
                    MappingOwnsSyncIdLocked(mappedMob, syncId))
                {
                    RemoveTrackedMobLocked(mappedMob, "mapped_but_invalid");
                    LogResolveFailLocked(
                        syncId,
                        hadIdToMob,
                        hadMobToId,
                        true,
                        "mapped_but_invalid");
                    return null;
                }

                LogFocusSyncLifecycleLocked(syncId, "RESOLVE_OK", "tracked");
                return mappedMob;
            }

            if (!IdToMob.TryGetValue(syncId, out var mob) || mob == null || !IsSyncMob(mob))
            {
                LogResolveFailLocked(
                    syncId,
                    hadIdToMob,
                    hadMobToId,
                    preDestroyed,
                    !hadIdToMob ? "missing_idtomob" : (mob == null ? "forward_null" : "not_sync_mob"));
                return null;
            }

            try
            {
                if (!DoesLevelMatchCurrentIdentityLocked(mob._level))
                {
                    LogResolveFailLocked(syncId, true, hadMobToId, ReadMobDestroyedSafe(mob), "level_mismatch");
                    return null;
                }
            }
            catch
            {
                LogResolveFailLocked(syncId, true, hadMobToId, true, "level_check_threw");
                return null;
            }

            if (AddTrackedMobLocked(mob) >= 0)
            {
                LogFocusSyncLifecycleLocked(syncId, "RESOLVE_OK", "readded");
                return mob;
            }

            LogResolveFailLocked(syncId, true, hadMobToId, ReadMobDestroyedSafe(mob), "add_tracked_failed");
            return null;
        }

        private static Mob? ResolveTrackedMobForIncomingStateLocked(NetNode.MobStateSnapshot state, HashSet<Mob>? reservedMobs)
        {
            // Boss identity (bid:) folded into NetId space; still used across phase/proxy rebuilds.
            var bossEntityId = BossStateSync.TryGetEntityId(state.StatePayload);

            var mappedMob = ResolveTrackedMobBySyncIdLocked(state.Index);
            if (mappedMob != null)
            {
                var reserved = reservedMobs != null && reservedMobs.Contains(mappedMob);
                if (!reserved && DoesMobMatchStateType(mappedMob, state.Type))
                {
                    if (bossEntityId > 0)
                        RememberClientBossEntityIdLocked(mappedMob, bossEntityId);
                    return mappedMob;
                }

                if (!reserved)
                {
                    // NetId is the host-owned identity. Elite rune mobs can replace their native
                    // wrapper (and therefore their type/class signature) during promotion, phase
                    // setup, or the final death transition. Rejecting that packet invalidates the
                    // only binding we have and turns the next host life=0/MOBDIE into a permanent
                    // client ghost. The resolver already validated the generation, level and
                    // forward/reverse mapping, so preserve the binding and refresh the cached type.
                    // Do not use position here: a teleport/elite transform may legitimately move
                    // the same authoritative mob between packets.
                    if (IsStateRebindCandidateLocked(mappedMob) &&
                        MappingOwnsSyncIdLocked(mappedMob, state.Index))
                    {
                        if (!string.IsNullOrWhiteSpace(state.Type))
                            hostMobTypeBySyncId[state.Index] = state.Type;

                        MobSyncTrace.LogFallbackMatchResolved(
                            state.Life <= 0
                                ? "state_type_mismatch_accepted_by_authoritative_id"
                                : "state_type_changed_preserved_authoritative_id",
                            state.Index,
                            state.Type ?? string.Empty,
                            state.X,
                            state.Y,
                            candidateCount: 1,
                            rebound: false);
                        if (bossEntityId > 0)
                            RememberClientBossEntityIdLocked(mappedMob, bossEntityId);
                        return mappedMob;
                    }

                    InvalidateTrackedSyncCacheLocked(state.Index, "state_type_mismatch");
                    MobSyncTrace.LogIncomingMappingMismatch(
                        "state",
                        state.Index,
                        state.Type ?? string.Empty,
                        mappedMob != null ? BuildMobStateTypeSignature(mappedMob) : string.Empty,
                        "type_mismatch");
                }
            }

            // Boss phase/proxy rebuild: follow learned EntityId without proximity.
            if (bossEntityId > 0 &&
                TryResolveClientBossByEntityIdLocked(bossEntityId, state.Index, reservedMobs, out var identityBoss) &&
                identityBoss != null)
            {
                RememberClientBossEntityIdLocked(identityBoss, bossEntityId);
                TryRebindTrackedMobSyncIdLocked(identityBoss, state.Index);
                MobSyncTrace.LogBindSyncId(
                    "boss_identity_rebind",
                    state.Index,
                    state.Type ?? string.Empty,
                    state.X,
                    state.Y);
                return identityBoss;
            }

            // Unique authoritative boss (payload marked) when identity not yet learned.
            if (TryResolveUniqueAuthoritativeBossLocked(
                    state.Type,
                    state.StatePayload,
                    reservedMobs,
                    bossEntityId,
                    out var authoritativeBoss,
                    out _) &&
                authoritativeBoss != null)
            {
                TryRebindTrackedMobSyncIdLocked(authoritativeBoss, state.Index);
                if (bossEntityId > 0)
                    RememberClientBossEntityIdLocked(authoritativeBoss, bossEntityId);
                MobSyncTrace.LogBindSyncId(
                    "boss_authoritative_state_repair",
                    state.Index,
                    state.Type ?? string.Empty,
                    state.X,
                    state.Y);
                return authoritativeBoss;
            }

            // One-shot unbound bind: type + spawn position. No ongoing proximity combat rebind.
            if (TryBindUnboundMobByTypeAndSpawnLocked(
                    state.Index,
                    state.Type,
                    state.X,
                    state.Y,
                    reservedMobs,
                    out var unboundMob) &&
                unboundMob != null)
            {
                if (bossEntityId > 0)
                    RememberClientBossEntityIdLocked(unboundMob, bossEntityId);
                MobSyncTrace.LogBindSyncId(
                    "state_oneshot_bind",
                    state.Index,
                    state.Type ?? string.Empty,
                    state.X,
                    state.Y);
                return unboundMob;
            }

            // Level.boss anchor for encounter bosses only (proximity-free, type-checked).
            if (TryResolveLevelBossAnchorForStateLocked(state, reservedMobs, bossEntityId, out var anchoredBoss) &&
                anchoredBoss != null)
            {
                TryRebindTrackedMobSyncIdLocked(anchoredBoss, state.Index);
                if (bossEntityId > 0)
                    RememberClientBossEntityIdLocked(anchoredBoss, bossEntityId);
                MobSyncTrace.LogBindSyncId(
                    "level_boss_anchor",
                    state.Index,
                    state.Type ?? string.Empty,
                    state.X,
                    state.Y);
                return anchoredBoss;
            }

            // Last-resort client tombstone recovery: the host still owns this sync id and pushes
            // states for it, but the local replica was removed without a confirmed death. Recreate
            // the replica from the host's push data instead of dropping the state forever.
            if (TryRecoverTombstonedSyncIdLocked(
                    state.Index,
                    state.Type,
                    state.X,
                    state.Y,
                    out var tombstoneRecovered) &&
                tombstoneRecovered != null)
            {
                if (bossEntityId > 0)
                    RememberClientBossEntityIdLocked(tombstoneRecovered, bossEntityId);
                return tombstoneRecovered;
            }

            return null;
        }

        private static bool TryResolveLevelBossAnchorForStateLocked(
            NetNode.MobStateSnapshot state,
            HashSet<Mob>? reservedMobs,
            int bossEntityId,
            out Mob? levelBoss)
        {
            levelBoss = null;

            try
            {
                var boss = currentLevel?.boss as Mob;
                if (boss == null || boss.destroyed)
                    return false;
                if (reservedMobs != null && reservedMobs.Contains(boss))
                    return false;
                if (!DoesMobMatchStateType(boss, state.Type))
                    return false;

                // Identity gate: if both sides carry identities and they disagree, this state
                // belongs to a different boss of the same type (duo arenas) — do not anchor.
                if (bossEntityId > 0 &&
                    s_clientEntityIdByBoss.TryGetValue(boss, out var knownId) &&
                    knownId > 0 &&
                    knownId != bossEntityId)
                {
                    return false;
                }

                // The anchor must have a registry home for the rebind to land in.
                if (AddTrackedMobLocked(boss) < 0)
                    return false;

                levelBoss = boss;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveUniqueAuthoritativeBossLocked(
            string? expectedType,
            string? statePayload,
            HashSet<Mob>? reservedMobs,
            int currentEntityId,
            out Mob? uniqueBoss,
            out int candidateCount)
        {
            uniqueBoss = null;
            candidateCount = 0;
            if (!BossStateSync.IsBossStatePayload(statePayload))
                return false;

            for (var i = 0; i < trackedMobs.Count; i++)
            {
                var candidate = trackedMobs[i];
                if (candidate == null || (reservedMobs != null && reservedMobs.Contains(candidate)))
                    continue;
                if (!IsStateRebindCandidateLocked(candidate) || !BossSyncHelpers.IsBossMob(candidate))
                    continue;
                if (!DoesBossMatchAuthoritativeType(candidate, expectedType))
                    continue;
                // Phase 2: a boss already bound to a different, still-living identity is not a
                // candidate for this (different) id. This keeps the fallback unambiguous when one
                // boss of a duo rebuilds: only the unbound, newly rebuilt boss remains.
                if (IsBossClaimedByOtherLivingEntityLocked(candidate, currentEntityId))
                    continue;

                candidateCount++;
                uniqueBoss = candidate;
                if (candidateCount > 1)
                {
                    uniqueBoss = null;
                    return false;
                }
            }

            return candidateCount == 1 && uniqueBoss != null;
        }

        private static bool DoesBossMatchAuthoritativeType(Mob boss, string? expectedType)
        {
            if (boss == null || !BossSyncHelpers.IsBossMob(boss))
                return false;
            if (string.IsNullOrWhiteSpace(expectedType) || DoesMobMatchStateType(boss, expectedType))
                return true;

            // A proxy class can legitimately change across a native boss phase.  The stable mob
            // type id is sufficient when it is present on both peers; otherwise require the class.
            if (TrySplitStateTypeSignature(expectedType, out var expectedTypeId, out var expectedClass))
            {
                var actualTypeId = GetMobTypeIdSafe(boss);
                if (!string.IsNullOrWhiteSpace(expectedTypeId) &&
                    !string.IsNullOrWhiteSpace(actualTypeId) &&
                    string.Equals(expectedTypeId, actualTypeId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var actualClass = GetMobRuntimeClassKeySafe(boss);
                return !string.IsNullOrWhiteSpace(expectedClass) &&
                       !string.IsNullOrWhiteSpace(actualClass) &&
                       string.Equals(expectedClass, actualClass, StringComparison.OrdinalIgnoreCase);
            }

            var legacyExpected = NormalizeMobTypeKey(expectedType);
            return string.Equals(legacyExpected, GetMobTypeIdSafe(boss), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(legacyExpected, GetMobRuntimeClassKeySafe(boss), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBossRelatedEntity(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return false;
            
            var lowerType = type.ToLowerInvariant();
            return lowerType.Contains("tentacle") ||
                   lowerType.Contains("claw") ||
                   lowerType.Contains("hand") ||
                   lowerType.Contains("eye") ||
                   lowerType.Contains("scythe") ||
                   lowerType.Contains("appendage") ||
                   lowerType.Contains("proxy") ||
                   lowerType.Contains("ttcl");
        }

        /// <summary>
        /// Quantizes world coordinates to integer CELLS (floor(world / PixelsPerCase)). A mob's
        /// cell is stable across host/client even when the peers' pixel coordinates drift by a
        /// fraction of a tile (spawn snapping, gravity landing, interpolation: 1000.0 vs 1000.1
        /// both live in the same cell), so cells are the correct granularity for cross-peer
        /// identity/position matching. The old int32-pixel quantization flipped at a 0.5px
        /// boundary, which is far below the real host/client coordinate divergence.
        /// </summary>
        private static void QuantizeWorldPositionToCells(double x, double y, out int cx, out int cy)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                cx = 0;
                cy = 0;
                return;
            }

            const double lim = int.MaxValue - 8;
            cx = (int)System.Math.Clamp(System.Math.Floor(x / PixelsPerCase), -lim, lim);
            cy = (int)System.Math.Clamp(System.Math.Floor(y / PixelsPerCase), -lim, lim);
        }

        /// <summary>Computes a mob's integer cell from its live world position.</summary>
        private static void GetMobWorldCells(Mob? mob, out int cx, out int cy)
        {
            if (mob == null)
            {
                cx = 0;
                cy = 0;
                return;
            }

            try
            {
                QuantizeWorldPositionToCells(GetWorldX(mob), GetWorldY(mob), out cx, out cy);
            }
            catch
            {
                cx = 0;
                cy = 0;
            }
        }

        private static Mob? ResolveTrackedMobForIncomingAttackLocked(NetNode.MobAttack attack)
        {
            var mappedMob = ResolveTrackedMobBySyncIdLocked(attack.Index);
            var expectedType = attack.Type;
            if (string.IsNullOrWhiteSpace(expectedType))
                hostMobTypeBySyncId.TryGetValue(attack.Index, out expectedType);

            if (mappedMob != null)
            {
                if (string.IsNullOrWhiteSpace(expectedType) || DoesMobMatchStateType(mappedMob, expectedType))
                    return mappedMob;

                MobSyncTrace.LogIncomingMappingMismatch(
                    "attack",
                    attack.Index,
                    expectedType ?? string.Empty,
                    mappedMob != null ? BuildMobStateTypeSignature(mappedMob) : string.Empty,
                    "type_mismatch");
                InvalidateTrackedSyncCacheLocked(attack.Index, "attack_type_mismatch");
            }

            if (string.IsNullOrWhiteSpace(expectedType))
                return null;

            if (!string.IsNullOrWhiteSpace(attack.Type))
                hostMobTypeBySyncId[attack.Index] = attack.Type;

            if (TryResolveBossForIncomingAttackLocked(
                    attack,
                    expectedType,
                    out var authoritativeBoss,
                    out var bossCandidateCount) &&
                authoritativeBoss != null)
            {
                TryRebindTrackedMobSyncIdLocked(authoritativeBoss, attack.Index);
                MobSyncTrace.LogBindSyncId(
                    "boss_attack_authoritative_repair",
                    attack.Index,
                    expectedType,
                    attack.X,
                    attack.Y);
                return authoritativeBoss;
            }

            if (bossCandidateCount > 1)
            {
                MobSyncTrace.LogAmbiguousMatchRejected(
                    "boss_attack",
                    attack.Index,
                    expectedType,
                    attack.X,
                    attack.Y,
                    bossCandidateCount);
            }

            return null;
        }

        private static bool TryResolveBossForIncomingAttackLocked(
            NetNode.MobAttack attack,
            string expectedType,
            out Mob? resolvedBoss,
            out int candidateCount)
        {
            resolvedBoss = null;
            candidateCount = 0;
            if (string.IsNullOrWhiteSpace(expectedType))
                return false;

            Mob? best = null;
            var bestDistanceSq = double.MaxValue;
            var secondDistanceSq = double.MaxValue;

            for (var i = 0; i < trackedMobs.Count; i++)
            {
                var candidate = trackedMobs[i];
                if (candidate == null || !IsStateRebindCandidateLocked(candidate) ||
                    !BossSyncHelpers.IsBossMob(candidate) ||
                    !DoesBossMatchAuthoritativeType(candidate, expectedType))
                {
                    continue;
                }

                candidateCount++;
                var dx = GetWorldX(candidate) - attack.X;
                var dy = GetWorldY(candidate) - attack.Y;
                var distanceSq = double.IsFinite(dx) && double.IsFinite(dy)
                    ? dx * dx + dy * dy
                    : double.MaxValue;

                if (distanceSq < bestDistanceSq)
                {
                    secondDistanceSq = bestDistanceSq;
                    bestDistanceSq = distanceSq;
                    best = candidate;
                }
                else if (distanceSq < secondDistanceSq)
                {
                    secondDistanceSq = distanceSq;
                }
            }

            if (best == null)
                return false;

            if (candidateCount == 1)
            {
                resolvedBoss = best;
                return true;
            }

            var maxDistanceSq = ClientStateRebindMaxDistancePx * ClientStateRebindMaxDistancePx;
            if (bestDistanceSq > maxDistanceSq || secondDistanceSq == double.MaxValue)
                return false;

            // Boss parts (Giant hands/eye, Conjunctivius tentacles) cluster by identical type.
            // Bind the nearest in-range candidate rather than rejecting the whole cluster on a
            // small gap; the caller reserves each bound part before resolving the next, so the
            // batch stays deterministic. Non-part bosses keep the strict gap.
            if (IsBossRelatedEntity(expectedType))
            {
                resolvedBoss = best;
                return true;
            }

            var bestDistance = System.Math.Sqrt(System.Math.Max(0.0, bestDistanceSq));
            var secondDistance = System.Math.Sqrt(System.Math.Max(0.0, secondDistanceSq));
            if (secondDistance - bestDistance < ClientStateRebindMinimumGapPx)
                return false;

            resolvedBoss = best;
            return true;
        }

        private static void TryRebindTrackedMobSyncIdLocked(Mob mob, int syncId)
        {
            // NetId 0 is reserved / retired. Rebinding onto 0 reintroduces the syncId=0 type thrash
            // seen on PrisonCourtyard (host ghost-echo + client state mismatch cascade).
            if (mob == null || syncId <= 0)
                return;

            if (!IsLevelIdentityReadyLocked(mob._level))
                return;

            var hadOldSyncId = MobToId.TryGetValue(mob, out var oldSyncId);
            if (hadOldSyncId && oldSyncId >= 0 && oldSyncId != syncId)
            {
                LogRemoveAttemptLocked(mob, oldSyncId, "rebind_vacate_old_id");
                ClearPerSyncIdCachesLocked(oldSyncId);
            }

            if (IdToMob.TryGetValue(syncId, out var displacedMob) && displacedMob != null &&
                !ReferenceEquals(displacedMob, mob))
            {
                MobToId.Remove(displacedMob);
                s_mobSyncAliases.Remove(displacedMob);
            }
            ClearPerSyncIdCachesLocked(syncId);

            if (mob != null)
            {
                if (MobToId.TryGetValue(mob, out var _oldId))
                    IdToMob.Remove(_oldId);
                IdToMob.Remove(syncId);
                MobToId[mob] = syncId;
                IdToMob[syncId] = mob;
                s_mobSyncAliases.Remove(mob);
                if (FindExactTrackedMobIndexLocked(mob) < 0)
                {
                    trackedMobs.Add(mob);
                    trackedMobIndices[mob] = trackedMobs.Count - 1;
                }
                if (syncId >= nextRuntimeSyncId)
                    nextRuntimeSyncId = syncId + 1;
                clientAuthoritativeStateSeenSyncIds.Add(syncId);
            }

            ValidateTrackedIntegrityLocked("track_rebind");
        }

        private static bool IsStateRebindCandidateLocked(Mob? mob)
        {
            if (mob == null || !IsSyncMob(mob))
                return false;

            try
            {
                if (mob.destroyed || mob._level == null)
                    return false;

                if (!DoesLevelMatchCurrentIdentityLocked(mob._level))
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static string BuildMobStateTypeSignature(Mob mob)
        {
            var typeId = GetMobTypeIdSafe(mob);
            var runtimeClass = GetMobRuntimeClassKeySafe(mob);

            if (!string.IsNullOrWhiteSpace(typeId) && !string.IsNullOrWhiteSpace(runtimeClass))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{typeId}|{runtimeClass}");
            }

            if (!string.IsNullOrWhiteSpace(typeId))
                return typeId;

            return runtimeClass;
        }

        private static bool DoesMobMatchStateType(Mob? mob, string? stateType)
        {
            if (mob == null)
                return false;

            if (string.IsNullOrWhiteSpace(stateType))
                return true;

            var actualType = GetMobTypeIdSafe(mob);
            var actualClass = GetMobRuntimeClassKeySafe(mob);

            if (TrySplitStateTypeSignature(stateType, out var expectedType, out var expectedClass))
            {
                var typeMatches = string.IsNullOrWhiteSpace(expectedType) ||
                                  (!string.IsNullOrWhiteSpace(actualType) &&
                                   string.Equals(expectedType, actualType, StringComparison.OrdinalIgnoreCase));

                var classMatches = string.IsNullOrWhiteSpace(expectedClass) ||
                                   (!string.IsNullOrWhiteSpace(actualClass) &&
                                    string.Equals(expectedClass, actualClass, StringComparison.OrdinalIgnoreCase));

                return typeMatches && classMatches;
            }

            var legacyExpected = NormalizeMobTypeKey(stateType);
            if (string.IsNullOrWhiteSpace(legacyExpected))
                return true;

            if (!string.IsNullOrWhiteSpace(actualType) &&
                string.Equals(legacyExpected, actualType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(actualClass) &&
                string.Equals(legacyExpected, actualClass, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

    }
}

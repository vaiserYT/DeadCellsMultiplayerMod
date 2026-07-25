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
    public partial class MobsSynchronization :
    IOnAdvancedModuleInitializing,
    IOnFrameUpdate,
    IEventReceiver
    {
        private static void TrySendHostMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, Entity? explicitTarget = null)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            var net = GameMenu.NetRef;
            if (!IsHost(net))
                return;

            if (!IsSyncMob(mob))
                return;

            if (!TryGetMobSyncId(mob, out var mobSyncId))
                return;
            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return;

            var targetEntity = ResolveMobAttackTargetEntity(mob, explicitTarget);

            var targetUserId = ResolveHostTargetUserId(targetEntity, net!.id);

            var x = GetWorldX(mob);
            var y = GetWorldY(mob);
            var dir = NormalizeDir(mob.dir);
            var encodedSkill = Uri.EscapeDataString(skillId);
            var reqTarget = requiresTargetInArea ? 1 : 0;
            var dataVal = data ?? 0;
            // Phase 3: per-boss monotonic attack sequence (0 for non-bosses) so the client can drop
            // replayed / out-of-order boss attacks deterministically via a high-water mark.
            var attackSeq = NextHostBossAttackSeq(mob);
            var attackEvent = $"attack|{encodedSkill}|0|0|{reqTarget}|{dataVal}|{targetUserId}|{dir}|{attackSeq}";
            var mobType = BuildMobStateTypeSignature(mob);
            var update = new NetNode.MobEventUpdate(mobSyncId, x, y, dir, SingleEvent(attackEvent), mobType, identityToken);
            MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(update));
            net.SendMobEvents(SingleUpdate(update));
            
            // For bosses, force a state sync immediately after attack to sync spawned entities
            if (BossSyncHelpers.IsBossMob(mob))
            {
                lock (Sync)
                {
                    EnqueueHostMobDirtyLocked(mobSyncId, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                }
            }
        }

        private void Hook_Mob_setAttackTarget(Hook_Mob.orig_setAttackTarget orig, Mob self, Entity e)
        {
            // Never substitute another target from inside vanilla's target setter. Elite skills and
            // normal AI deliberately clear/swap targets while changing state; replacing null or an
            // invalid target here can leave the behavior tree waiting forever on the old phase.
            orig(self, e);
        }

        private void Hook_Mob_setNemesisTarget(Hook_Mob.orig_setNemesisTarget orig, Mob self, Entity e)
        {
            // Keep vanilla target-container transitions intact. Co-op may repair only the immediate
            // attack target later, after the mob's own update has completed.
            orig(self, e);
        }

        private static bool TryResolveFallbackPlayerCombatTarget(Mob? mob, Entity? currentTarget, out Entity fallbackTarget)
        {
            fallbackTarget = null!;
            if (mob == null)
                return false;
            if (!IsMobHostileToPlayers(mob))
                return false;
            if (currentTarget == null)
            {
                if (TryGetCurrentHostAttackTarget(mob, out _))
                    return false;

                if (TryGetCurrentHostNemesisTarget(mob, out var livingNemesisTarget))
                {
                    fallbackTarget = livingNemesisTarget;
                    return true;
                }

                return false;
            }
            else if (!IsInvalidPlayerTargetEntity(currentTarget))
                return false;

            if (TryGetAlternateCurrentHostCombatTarget(mob, currentTarget, out var existingTarget))
            {
                fallbackTarget = existingTarget;
                return true;
            }

            if (TryResolveDetectedHostCombatTarget(mob, out var detectedTarget))
            {
                fallbackTarget = detectedTarget;
                return true;
            }

            return false;
        }

        private static bool RebuildMobArray(Level? level)
        {
            var candidateIdentityToken = ComputeLevelIdentityToken(level);
            var candidateEntityCount = 0;
            var candidateTrackedMobs = new List<Mob>();
            var role = MobSyncNetRoleForTrace(GameMenu.NetRef);
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

            MobSyncTrace.LogRegistryRebuild(
                role,
                levelId,
                trackedBeforeReset,
                trackedAfterRebuild,
                trackedMobs.Count,
                trackedMobs.Count > 0 ? 0 : -1,
                trackedMobs.Count > 0 ? trackedMobs.Count - 1 : -1,
                nextRuntimeSyncId,
                generationAfterRebuild,
                s_levelIdentityToken);
            MobSyncTrace.LogRebuildCommit(
                role,
                levelId,
                levelKey,
                trackedAfterRebuild,
                trackedMobs.Count,
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
            if (syncId < 0 && GameMenu.NetRef?.IsHost != true)
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
                MobSyncNetRoleForTrace(GameMenu.NetRef),
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

            try { GameMenu.NetRef?.ClearMobSyncQueues(); } catch { }
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
            nextRuntimeSyncId = 0;
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
            s_hostDeathTombstonesBySyncId.Clear();
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

        private static void RemoveTrackedMobLocked(Mob mob)
        {
            if (mob == null)
                return;

            s_trackedMobValidationPending = true;
            var index = FindExactTrackedMobIndexLocked(mob);
            if (index >= 0)
            {
                RemoveTrackedMobAtIndexLocked(index);
                return;
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

        private static void RemoveTrackedMobAtIndexLocked(int index)
        {
            if (index < 0 || index >= trackedMobs.Count)
                return;

            s_trackedMobValidationPending = true;
            var mob = trackedMobs[index];
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
            // not alias every enemy of the same class. Reject ambiguous overlapping matches.
            var matchedIndex = -1;
            for (int i = 0; i < trackedMobs.Count; i++)
            {
                var candidate = trackedMobs[i];
                if (!AreLikelySameNativeMobProxy(candidate, mob))
                    continue;

                if (matchedIndex >= 0)
                    return -1;
                matchedIndex = i;
            }

            if (matchedIndex >= 0)
            {
                var canonical = trackedMobs[matchedIndex];
                if (canonical != null)
                    trackedMobIndices[canonical] = matchedIndex;
                return matchedIndex;
            }

            return -1;
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
                return double.IsFinite(dx) && double.IsFinite(dy) && dx * dx + dy * dy <= 0.25;
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
                    IdToMob.Remove(syncId);
                    s_trackedMobValidationPending = true;
                    return false;
                }
            }

            var canonicalMob = trackedMobs[localIndex];
            if (canonicalMob == null)
            {
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
                MobSyncTrace.LogStaleTrackedMapping(
                    syncId,
                    localIndex,
                    mappedSyncId == syncId ? "registry_missing" : $"registry_mismatch:{mappedSyncId}");
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
                if (MobToId.TryGetValue(mappedMob, out var reverseSyncId) && reverseSyncId == syncId)
                    MobToId.Remove(mappedMob);
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
                    RemoveTrackedMobAtIndexLocked(i);
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
                {
                    RemoveTrackedMobAtIndexLocked(i);
                }
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
                if (GameMenu.NetRef?.IsHost != true)
                    return false;

                syncId = nextRuntimeSyncId++;
                MobToId[mob] = syncId;
                IdToMob[syncId] = mob;
                StampHostBossNetIdLocked(mob, syncId);
                // Dynamic/runtime-spawned mobs must be in the canonical tracked list immediately;
                // otherwise the first dirty packet creates an IdToMob entry that is rejected as
                // untracked_mob on the next dequeue.
                if (FindTrackedMobIndexLocked(mob) < 0)
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

            if (TryGetTrackedMobBySyncIdLocked(syncId, out var mappedMob) && mappedMob != null)
                return mappedMob;

            if (!IdToMob.TryGetValue(syncId, out var mob) || mob == null || !IsSyncMob(mob))
                return null;

            try
            {
                if (!DoesLevelMatchCurrentIdentityLocked(mob._level))
                    return null;
            }
            catch
            {
                return null;
            }

            return AddTrackedMobLocked(mob) >= 0 ? mob : null;
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

        /// <summary>Rounds world coordinates to int32 pixels so host/client hit routing agrees despite float drift.</summary>
        private static void QuantizeWorldPositionToPixelsInt32(double x, double y, out int qx, out int qy)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                qx = 0;
                qy = 0;
                return;
            }

            const double lim = int.MaxValue - 8;
            var rx = System.Math.Clamp(System.Math.Round(x, MidpointRounding.AwayFromZero), -lim, lim);
            var ry = System.Math.Clamp(System.Math.Round(y, MidpointRounding.AwayFromZero), -lim, lim);
            qx = (int)rx;
            qy = (int)ry;
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
            if (mob == null || syncId < 0)
                return;

            if (!IsLevelIdentityReadyLocked(mob._level))
                return;

            var hadOldSyncId = MobToId.TryGetValue(mob, out var oldSyncId);
            if (hadOldSyncId && oldSyncId >= 0 && oldSyncId != syncId)
                ClearPerSyncIdCachesLocked(oldSyncId);

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

        private static bool TryResolveSafeBossNemesisTarget(Mob? mob, Entity? requestedTarget, out Entity safeTarget)
        {
            safeTarget = null!;

            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return false;

            if (requestedTarget is Hero heroTarget)
            {
                safeTarget = heroTarget;
                return true;
            }

            try
            {
                var currentHeroTarget = mob.nemesisTarget as Hero;
                if (currentHeroTarget != null &&
                    !currentHeroTarget.destroyed &&
                    currentHeroTarget.life > 0 &&
                    !ModEntry.IsEntityDownedForCombat(currentHeroTarget))
                {
                    safeTarget = currentHeroTarget;
                    return true;
                }
            }
            catch
            {
            }

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null)
            {
                try
                {
                    if (!localHero.destroyed &&
                        localHero.life > 0 &&
                        !ModEntry.IsEntityDownedForCombat(localHero))
                    {
                        safeTarget = localHero;
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySplitStateTypeSignature(string? rawValue, out string typeId, out string runtimeClass)
        {
            typeId = string.Empty;
            runtimeClass = string.Empty;

            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            var value = rawValue.Trim();
            var pipeIndex = value.IndexOf('|');
            if (pipeIndex < 0)
                return false;

            if (pipeIndex > 0)
                typeId = NormalizeMobTypeKey(value[..pipeIndex]);

            if (pipeIndex + 1 < value.Length)
                runtimeClass = NormalizeMobTypeKey(value[(pipeIndex + 1)..]);

            return !string.IsNullOrWhiteSpace(typeId) || !string.IsNullOrWhiteSpace(runtimeClass);
        }

        private static string GetMobTypeIdSafe(Mob? mob)
        {
            if (mob == null)
                return string.Empty;

            try
            {
                return NormalizeMobTypeKey(mob.type?.ToString());
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetMobRuntimeClassKeySafe(Mob? mob)
        {
            if (mob == null)
                return string.Empty;

            try
            {
                var runtimeType = mob.GetType();
                if (runtimeType == null)
                    return string.Empty;

                return NormalizeMobTypeKey(runtimeType.FullName ?? runtimeType.Name);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeMobTypeKey(string? rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType))
                return string.Empty;

            var value = rawType.Trim();

            var slash = value.LastIndexOf('/');
            var dot = value.LastIndexOf('.');
            var colon = value.LastIndexOf(':');
            var separator = System.Math.Max(System.Math.Max(slash, dot), colon);
            if (separator >= 0 && separator + 1 < value.Length)
                value = value[(separator + 1)..];

            return value.Trim();
        }

        private static bool IsClientNetworkAttackActive(Mob? mob)
        {
            if (mob == null)
                return false;

            lock (Sync)
            {
                return clientActiveNetworkAttackMobs.Contains(mob);
            }
        }

        private static void MarkClientNetworkAttackActive(Mob mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientActiveNetworkAttackMobs.Add(mob);
                clientNetworkAttackStartFrame[mob] = GetCurrentFrame(mob);
            }

            TryUnlockClientMobAiAuthority(mob);
        }

        private static void MarkClientBossSkillCallbackLease(Mob mob)
        {
            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return;

            lock (Sync)
            {
                clientBossSkillCallbackLeaseMobs.Add(mob);
            }

            MarkClientNetworkAttackActive(mob);
        }

        private static bool IsClientBossSkillCallbackLeaseActive(Mob? mob)
        {
            if (mob == null)
                return false;

            lock (Sync)
            {
                return clientBossSkillCallbackLeaseMobs.Contains(mob);
            }
        }

        private static double GetCurrentFrame(Mob? mob)
        {
            try
            {
                var level = mob?._level ?? currentLevel;
                if (level != null)
                    return level.ftime;
            }
            catch
            {
            }

            return 0.0;
        }

        private static void RefreshClientNetworkAttackState(Mob mob)
        {
            if (mob == null || !IsClientNetworkAttackActive(mob))
                return;

            var queuedOrCharging = HasLocalQueuedOrChargingSkill(mob);
            var preserveMotion = ShouldPreserveClientAttackMotion(mob);
            var isBoss = BossSyncHelpers.IsBossMob(mob);
            var elapsed = 0.0;
            lock (Sync)
            {
                if (clientNetworkAttackStartFrame.TryGetValue(mob, out var startFrame))
                    elapsed = GetCurrentFrame(mob) - startFrame;
            }

            if (queuedOrCharging && (!isBoss || elapsed < ClientBossVisualAttackMaxActiveFrames))
                return;
            if (preserveMotion && (!isBoss || elapsed < ClientBossVisualAttackMaxActiveFrames))
                return;

            lock (Sync)
            {
                if (clientNetworkAttackStartFrame.TryGetValue(mob, out var startFrame))
                {
                    var lockedElapsed = GetCurrentFrame(mob) - startFrame;
                    if (lockedElapsed < ClientNetworkAttackMinActiveFrames)
                        return;
                }

                clientActiveNetworkAttackMobs.Remove(mob);
                clientBossSkillCallbackLeaseMobs.Remove(mob);
                clientNetworkAttackStartFrame.Remove(mob);
            }

            if (isBoss)
            {
                // Expiring the lease re-locks the brain but does NOT stop an already-running
                // looping action — Conjunctivius kept firing poison orbs long after the host had
                // moved on. Best-effort interrupt of the stale skill/action.
                // ONLY while the boss is alive: the final attack's lease expires seconds after
                // the killing blow, and interrupting then breaks the native death sequence the
                // victory cinematic is waiting on (Concierge froze in letterbox + paused timer).
                bool aliveForInterrupt;
                try { aliveForInterrupt = !mob.destroyed && mob.life > 0; }
                catch { aliveForInterrupt = false; }
                if (aliveForInterrupt)
                    Bosses.BossReflection.TryInterruptMobSkills(mob);
            }
        }

        private static void UpdateClientMobAiAuthority(Mob mob)
        {
            // Let the cine script drive the boss during a locally active boss-intro cinematic;
            // locking resumes automatically on the first update after the cine ends.
            if (ModEntry.IsLocalBossIntroCineActive() && BossSyncHelpers.IsBossMob(mob))
                return;

            if (mob == null)
                return;

            RefreshClientNetworkAttackState(mob);
            var queuedOrCharging = HasLocalQueuedOrChargingSkill(mob);
            var networkAttackActive = IsClientNetworkAttackActive(mob);

            if (queuedOrCharging)
            {
                // A host-selected native skill may need the brain unlocked while it is being
                // queued/charged. Once the action is running, lock the decision-making brain again:
                // the native action/physics can finish, but the replica cannot independently pick a
                // second target/skill and diverge from the host during an online-latency window.
                TryUnlockClientMobAiAuthority(mob);
                TryRepairClientMobAttackTarget(mob);
                return;
            }

            TryLockClientMobAiAuthority(mob);
            if (networkAttackActive)
                TryRepairClientMobAttackTarget(mob);
        }

        /// <summary>
        /// Host HP thresholds can start transformations or scripted dialogue without a separate
        /// attack packet. Give the client boss a short bounded presentation lease so those native
        /// callbacks can run; host transform, HP, targeting and death remain authoritative.
        /// </summary>
        private static void MarkClientBossPresentationLease(Mob mob)
        {
            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return;

            MarkClientNetworkAttackActive(mob);
        }

        private static void TryRepairClientMobAttackTarget(Mob mob)
        {
            if (mob == null)
                return;
            if (!IsMobHostileToPlayers(mob))
                return;

            TryClearClientMobInvalidPlayerTargets(mob);
            if (TryGetCurrentClientAttackTarget(mob, out _))
                return;

            var detected = ResolveDetectedClientTargetEntity(mob);
            if (detected == null)
                return;

            try
            {
                if (!ReferenceEquals(mob.aTarget, detected))
                    mob.setAttackTarget(detected);
            }
            catch { }
        }

        private static bool TryClearClientMobInvalidPlayerTargets(Mob mob)
        {
            if (mob == null)
                return false;

            var cleared = false;

            try
            {
                var at = mob.aTarget;
                if (at != null && IsKnownPlayerEntity(at) && !IsPreservablePlayerCombatTargetForMob(mob, at))
                {
                    mob.setAttackTarget(null);
                    cleared = true;
                }
            }
            catch { }

            try
            {
                var nt = mob.nemesisTarget;
                if (nt != null && IsKnownPlayerEntity(nt) && !IsPreservablePlayerCombatTargetForMob(mob, nt))
                {
                    mob.setNemesisTarget(null);
                    cleared = true;
                }
            }
            catch { }

            return cleared;
        }

        private static void TryLockClientMobAiAuthority(Mob mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                if (!clientAiLockedMobs.Add(mob))
                    return;
            }

            try
            {
                mob.lockAiS(ClientAiAuthorityLockDurationSeconds);
            }
            catch
            {
            }
        }

        private static void TryUnlockClientMobAiAuthority(Mob mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                if (!clientAiLockedMobs.Remove(mob))
                    return;
            }

            try
            {
                mob.unlockAi();
            }
            catch
            {
            }
        }

        private static void TryAssignHostAttackTarget(Mob mob)
        {
            if (mob == null || !IsMobHostileToPlayers(mob))
                return;

            RefreshHostContactAttackState(mob);
            if (TryGetCurrentHostAttackTarget(mob, out var existingTarget))
            {
                // Retention is deliberately MUCH more permissive than acquisition. Testing the
                // current target against the acquire gate re-selected every single frame the target
                // sat outside the facing cone — which is most frames during a real fight — so mobs
                // flipped between players, turned around mid-approach and swung at nothing.
                // Vanilla does not drop aggro because an enemy turned its head; neither do we.
                if (existingTarget == null ||
                    IsPlayerCombatTargetStillRelevant(mob, existingTarget))
                {
                    return;
                }
            }

            // Let vanilla finish elite teleports, charges, stuns and scripted locks. Co-op only fills
            // an actually missing immediate attack target; it never unlocks AI or rewrites nemesis.
            if (HasLocalQueuedOrChargingSkill(mob))
                return;
            try
            {
                if (mob.aiLocked())
                    return;
            }
            catch
            {
                return;
            }

            if (!TryResolveDetectedHostCombatTarget(mob, out var selected) || selected == null)
                return;

            try
            {
                if (ReferenceEquals(mob.aTarget, selected))
                    return;

                // Hard backstop against oscillation. Even if some other path decides a mob should
                // reconsider, it cannot actually switch players more often than this. Without it a
                // single mis-scoped check flips every hostile mob every frame, which reads as
                // enemies running the wrong way and attacking empty air.
                if (!TryBeginHostTargetSwitch(mob))
                    return;

                mob.setAttackTarget(selected);
            }
            catch
            {
            }
        }

        /// <summary>Minimum frames a mob must keep a player target before it may switch again.</summary>
        private const double HostTargetSwitchCooldownFrames = 45.0;

        private static readonly ConditionalWeakTable<Mob, StrongBox<double>> s_hostLastTargetSwitchFrame = new();

        private static bool TryBeginHostTargetSwitch(Mob mob)
        {
            try
            {
                var now = GetCurrentFrame(mob);
                if (!double.IsFinite(now))
                    return true;

                if (s_hostLastTargetSwitchFrame.TryGetValue(mob, out var last) &&
                    last != null &&
                    now - last.Value < HostTargetSwitchCooldownFrames &&
                    now >= last.Value)
                {
                    return false;
                }

                s_hostLastTargetSwitchFrame.Remove(mob);
                s_hostLastTargetSwitchFrame.Add(mob, new StrongBox<double>(now));
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static bool HasValidLivingPlayerCombatTarget(Mob mob)
        {
            if (mob == null)
                return false;

            if (TryGetCurrentHostAttackTarget(mob, out _))
                return true;
            if (TryGetCurrentHostNemesisTarget(mob, out _))
                return true;

            return false;
        }

        private static bool TryGetCurrentHostAttackTarget(Mob mob, out Entity target)
        {
            target = null!;
            if (mob == null)
                return false;

            try
            {
                var attackTarget = mob.aTarget;
                if (attackTarget != null && IsPreservablePlayerCombatTargetForMob(mob, attackTarget))
                {
                    target = attackTarget;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryGetCurrentHostNemesisTarget(Mob mob, out Entity target)
        {
            target = null!;
            if (mob == null)
                return false;

            try
            {
                var nemesisTarget = mob.nemesisTarget;
                if (nemesisTarget != null && IsPreservablePlayerCombatTargetForMob(mob, nemesisTarget))
                {
                    target = nemesisTarget;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryGetAlternateCurrentHostCombatTarget(Mob mob, Entity? excludedTarget, out Entity target)
        {
            target = null!;
            if (mob == null)
                return false;

            if (TryGetCurrentHostAttackTarget(mob, out var attackTarget) &&
                !ReferenceEquals(attackTarget, excludedTarget))
            {
                target = attackTarget;
                return true;
            }

            if (TryGetCurrentHostNemesisTarget(mob, out var nemesisTarget) &&
                !ReferenceEquals(nemesisTarget, excludedTarget))
            {
                target = nemesisTarget;
                return true;
            }

            return false;
        }

        private static bool TryRepairHostAttackTargetFromCurrentState(Mob mob)
        {
            if (mob == null)
                return false;

            if (!TryGetCurrentHostNemesisTarget(mob, out var livingNemesisTarget))
                return false;

            try
            {
                if (!ReferenceEquals(mob.aTarget, livingNemesisTarget))
                    mob.setAttackTarget(livingNemesisTarget);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldSuppressHostRetarget(Mob mob)
        {
            if (mob == null)
                return false;

            if (HasLocalQueuedOrChargingSkill(mob))
                return true;

            try
            {
                return mob.aiLocked();
            }
            catch
            {
                return false;
            }
        }

        private static bool TryClearHostMobInvalidPlayerTargets(Mob mob)
        {
            if (mob == null)
                return false;

            var cleared = false;

            try
            {
                var at = mob.aTarget;
                if (at != null && IsKnownPlayerEntity(at) && !IsPreservablePlayerCombatTargetForMob(mob, at))
                {
                    mob.setAttackTarget(null);
                    cleared = true;
                }
            }
            catch
            {
            }

            try
            {
                var nt = mob.nemesisTarget;
                if (nt != null && IsKnownPlayerEntity(nt) && !IsPreservablePlayerCombatTargetForMob(mob, nt))
                {
                    mob.setNemesisTarget(null);
                    cleared = true;
                }
            }
            catch
            {
            }

            return cleared;
        }

        private static void TryCollectDetectedTarget(Mob mob, Entity? candidate)
        {
            if (candidate == null)
                return;
            if (ReferenceEquals(candidate, mob))
                return;
            if (!IsAcquirablePlayerCombatTargetForMob(mob, candidate, requireDetectArea: true))
                return;

            try
            {
                if (!DoesLevelMatchCurrentIdentityLocked(mob._level))
                    return;
                if (!DoesLevelMatchCurrentIdentityLocked(candidate._level))
                    return;
            }
            catch
            {
                return;
            }

            if (!hostDetectedTargets.Contains(candidate))
                hostDetectedTargets.Add(candidate);
        }

        private static void RefreshHostContactAttackState(Mob mob)
        {
            if (mob == null)
                return;

            var currentTargetUserId = ResolveHostTargetUserId(ResolveCurrentHostPlayerCombatTarget(mob), GameMenu.NetRef?.id ?? 0);
            lock (Sync)
            {
                if (currentTargetUserId <= 0)
                {
                    hostLastSentContactTargetUserIdByMob.Remove(mob);
                    return;
                }

                if (!hostLastSentContactTargetUserIdByMob.TryGetValue(mob, out var sentTargetUserId))
                    return;

                if (sentTargetUserId != currentTargetUserId)
                    hostLastSentContactTargetUserIdByMob.Remove(mob);
            }
        }

        private static Entity? ResolveCurrentHostPlayerCombatTarget(Mob mob)
        {
            if (mob == null)
                return null;

            if (TryGetCurrentHostAttackTarget(mob, out var attackTarget))
                return attackTarget;
            if (TryGetCurrentHostNemesisTarget(mob, out var nemesisTarget))
                return nemesisTarget;

            return null;
        }

        private static bool IsMobHostileToPlayers(Mob? mob)
        {
            if (mob == null)
                return false;

            try
            {
                var level = mob._level;
                var mobTeam = mob._team;
                if (level == null || mobTeam == null)
                    return false;

                return ReferenceEquals(mobTeam, level.teamMob);
            }
            catch
            {
                return false;
            }
        }

        private static int ResolveHostTargetUserId(Entity? target, int localUserId)
        {
            if (target == null || localUserId <= 0)
                return 0;
            if (ModEntry.IsEntityDownedForCombat(target))
                return 0;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(target, localHero))
                return localUserId;

            var gameHero = ModCore.Modules.Game.Instance?.HeroInstance;
            if (gameHero != null && ReferenceEquals(target, gameHero))
                return localUserId;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var clientId = ModEntry.clientIds[i];
                var client = ModEntry.clients[i];
                if (clientId <= 0 || client == null)
                    continue;

                if (ReferenceEquals(target, client))
                    return clientId;
            }

            return 0;
        }

        private static Entity? ResolveHostPlayerCombatEntity(int userId)
        {
            var net = GameMenu.NetRef;
            if (!IsHost(net) || userId <= 0)
                return null;

            var localId = net!.id;
            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (userId == localId)
                return localHero != null && IsPreservablePlayerCombatTargetEntity(localHero) ? localHero : null;

            if (!ModEntry.TryGetClientIndex(localId, userId, out var index))
                return null;

            var client = ModEntry.clients[index];
            return client != null && IsPreservablePlayerCombatTargetEntity(client) ? client : null;
        }

        private static void TryApplyHostMobHitCombatRefresh(Mob mob, int attackerUserId, int previousLife, int currentLife, bool replaySpecialHit)
        {
            if (mob == null || attackerUserId <= 0 || currentLife <= 0)
                return;

            // Threat refresh can interruptSkills mid-charge when aTarget is invalid, stranding the
            // host mob with no attack/move until a full reset. Skip while a skill is in flight.
            if (HasLocalQueuedOrChargingSkill(mob))
                return;

            var attacker = ResolveHostPlayerCombatEntity(attackerUserId);
            if (attacker == null || !IsPreservablePlayerCombatTargetForMob(mob, attacker))
                return;

            // A detached remote KingSkin is safe as an attack target but is not a safe key for all
            // vanilla threat/elite state containers. More importantly, no hit callback may rewrite
            // targets while either player is downed: that exact transition made mobs stop after the
            // survivor's first hit. The normal host update repairs a missing aTarget afterward.
            if (attacker is KingSkin || ModEntry.HasAnyPlayerDownedForCombat())
                return;

            var threatDelta = System.Math.Max(0, previousLife - currentLife);
            try
            {
                if (threatDelta > 0)
                    mob.addThreat(attacker, threatDelta, HaxeProxy.Runtime.Ref<double>.Null);
                else if (!replaySpecialHit)
                    return;

                mob.updateThreat();
            }
            catch
            {
                // Threat refresh is optional. Never fall back to force-setting attack/nemesis state
                // from inside damage application; vanilla will reacquire on its next update.
            }
        }

        private static bool TryResolveDetectedHostCombatTarget(Mob mob, out Entity selected)
        {
            selected = null!;
            if (mob == null)
                return false;

            lock (Sync)
            {
                hostDetectedTargets.Clear();
                try
                {
                    TryCollectDetectedTarget(mob, ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance);

                    for (int i = 0; i < ModEntry.clients.Length; i++)
                    {
                        if (ModEntry.clientIds[i] <= 0)
                            continue;

                        TryCollectDetectedTarget(mob, ModEntry.clients[i]);
                    }

                    if (hostDetectedTargets.Count == 0)
                        return false;

                    try
                    {
                        var currentNemesis = mob.nemesisTarget;
                        if (currentNemesis != null && hostDetectedTargets.Contains(currentNemesis))
                        {
                            selected = currentNemesis;
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        var currentTarget = mob.aTarget;
                        if (currentTarget != null && hostDetectedTargets.Contains(currentTarget))
                        {
                            selected = currentTarget;
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    var mx = GetWorldX(mob);
                    var my = GetWorldY(mob);
                    var bestDistSq = double.MaxValue;

                    for (int i = 0; i < hostDetectedTargets.Count; i++)
                    {
                        var candidate = hostDetectedTargets[i];
                        if (candidate == null)
                            continue;

                        var dx = GetWorldX(candidate) - mx;
                        var dy = GetWorldY(candidate) - my;
                        var distSq = dx * dx + dy * dy;
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            selected = candidate;
                        }
                    }

                    return selected != null;
                }
                finally
                {
                    hostDetectedTargets.Clear();
                }
            }
        }

        private static Entity? ResolveMobAttackTargetEntity(Mob mob, Entity? explicitTarget)
        {
            if (explicitTarget != null && IsPreservablePlayerCombatTargetForMob(mob, explicitTarget))
                return explicitTarget;

            try
            {
                if (mob.aTarget != null && IsPreservablePlayerCombatTargetForMob(mob, mob.aTarget))
                    return mob.aTarget;
            }
            catch
            {
            }

            try
            {
                if (mob.nemesisTarget != null && IsPreservablePlayerCombatTargetForMob(mob, mob.nemesisTarget))
                    return mob.nemesisTarget;
            }
            catch
            {
            }

            if (TryResolveDetectedHostCombatTarget(mob, out var detectedTarget))
                return detectedTarget;

            return null;
        }

        private static bool IsEntityOnCurrentCombatIdentity(Entity? entity)
        {
            if (entity == null)
                return false;

            try
            {
                lock (Sync)
                {
                    return DoesLevelMatchCurrentIdentityLocked(entity._level);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPreservablePlayerCombatTargetEntity(Entity entity)
        {
            if (entity == null)
                return false;
            if (IsCorpseLikeCombatTargetEntity(entity))
                return false;
            if (!IsKnownPlayerEntity(entity))
                return false;
            if (IsHardInvalidPlayerTargetEntity(entity))
                return false;

            return true;
        }

        private static bool IsInvalidPlayerTargetEntity(Entity? entity)
        {
            return IsHardInvalidPlayerTargetEntity(entity);
        }

        private static bool IsPreservablePlayerCombatTargetForMob(Mob mob, Entity entity)
        {
            if (mob == null || entity == null)
                return false;
            if (!IsPreservablePlayerCombatTargetEntity(entity))
                return false;

            try
            {
                if (!mob.isOpponent(entity))
                    return false;
            }
            catch
            {
                return false;
            }

            // Preserve a living opponent even when it is temporarily unhittable (roll i-frames,
            // shield/parry windows, revive protection, etc.). canBeHitBy is an acquisition/attack
            // check, not a reason to erase the mob's long-lived target; clearing it here is what made
            // mobs lose the surviving player immediately after that player attacked while a teammate
            // was downed.
            return true;
        }

        private static void TraceTargetAcquireRejected(Mob mob, Entity entity, string reason)
        {
            if (!MobSyncTrace.Enabled)
                return;

            MobSyncTrace.LogTargetAcquire(
                GetMobRuntimeClassKeySafe(mob),
                IsRemotePlayerCombatShell(entity),
                reason);
        }

        /// <summary>
        /// True when this entity is a remote player's networked shell rather than the local Hero.
        /// </summary>
        private static bool IsRemotePlayerCombatShell(Entity? entity)
        {
            if (entity == null)
                return false;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(entity, localHero))
                return false;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null && ReferenceEquals(entity, client))
                    return true;
            }

            return false;
        }

        private static bool IsAcquirablePlayerCombatTargetForMob(Mob mob, Entity entity, bool requireDetectArea = false)
        {
            if (!IsPreservablePlayerCombatTargetForMob(mob, entity))
                return false;

            // The remote player is a GhostKing (KingSkin), not a Hero. canBeDetected/canBeHitBy are
            // Hero-shaped vanilla checks — canBeHitBy is hooked for Hero only, and canBeDetected is
            // not hooked at all — so asking them about a KingSkin can reject a perfectly valid,
            // living target and leave the second player permanently un-aggroed. Use the mod's own
            // liveness rules for the shell instead; the detect-area test below still applies.
            var remoteShell = IsRemotePlayerCombatShell(entity);
            if (remoteShell)
            {
                if (IsHardInvalidPlayerTargetEntity(entity))
                {
                    TraceTargetAcquireRejected(mob, entity, "remote_shell_invalid");
                    return false;
                }
            }
            else
            {
                try
                {
                    if (!entity.canBeDetected())
                    {
                        TraceTargetAcquireRejected(mob, entity, "canBeDetected");
                        return false;
                    }
                    if (!entity.canBeHitBy(mob))
                    {
                        TraceTargetAcquireRejected(mob, entity, "canBeHitBy");
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            if (!requireDetectArea)
                return true;

            try
            {
                if (mob.inDetectArea(entity))
                    return true;
            }
            catch
            {
                return false;
            }

            // inDetectArea is a facing-dependent cone, so it alone can never see a player standing
            // behind a mob. Vanilla still aggros the LOCAL hero from behind because the game has
            // other acquisition routes — noise, threat, ambient wake — that only ever consider
            // game.hero. The remote player has no such routes: this gate is its only way in, so a
            // cone-only test made the second player permanently sneak-proof.
            //
            // Close proximity stands in for those missing routes. Kept deliberately tight, and
            // tighter vertically than horizontally, so it approximates "same platform, right next
            // to me" rather than aggro through floors or across a room.
            if (remoteShell && IsRemotePlayerWithinProximityAggro(mob, entity))
                return true;

            TraceTargetAcquireRejected(mob, entity, "inDetectArea");
            return false;
        }

        /// <summary>Horizontal reach of the remote-player proximity fallback (~6 tiles).</summary>
        private const double RemotePlayerProximityAggroRangeXPx = 24.0 * 6.0;

        /// <summary>Vertical reach, kept short so mobs do not notice players through floors.</summary>
        private const double RemotePlayerProximityAggroRangeYPx = 24.0 * 2.5;

        /// <summary>
        /// Retention envelope. Wider than the acquire ranges on purpose: the gap between acquiring
        /// and losing a target is what stops a mob oscillating between two players standing at
        /// similar distances.
        /// </summary>
        private const double PlayerTargetRetentionRangeXPx = 24.0 * 14.0;

        private const double PlayerTargetRetentionRangeYPx = 24.0 * 6.0;

        private static bool IsWithinRangeBox(Mob mob, Entity entity, double rangeX, double rangeY)
        {
            try
            {
                var dx = GetWorldX(entity) - GetWorldX(mob);
                var dy = GetWorldY(entity) - GetWorldY(mob);
                if (!double.IsFinite(dx) || !double.IsFinite(dy))
                    return false;

                return System.Math.Abs(dx) <= rangeX && System.Math.Abs(dy) <= rangeY;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether a mob should keep fighting its current player target. Only a target that is
        /// genuinely gone — dead, downed, off-level, or well outside the retention box — releases
        /// the mob to re-select.
        /// </summary>
        private static bool IsPlayerCombatTargetStillRelevant(Mob mob, Entity entity)
        {
            if (IsHardInvalidPlayerTargetEntity(entity))
                return false;

            try
            {
                if (mob.inDetectArea(entity))
                    return true;
            }
            catch
            {
                // If the game cannot answer, keep the existing target rather than thrash.
                return true;
            }

            return IsWithinRangeBox(mob, entity, PlayerTargetRetentionRangeXPx, PlayerTargetRetentionRangeYPx);
        }

        private static bool IsRemotePlayerWithinProximityAggro(Mob mob, Entity entity)
        {
            return IsWithinRangeBox(mob, entity, RemotePlayerProximityAggroRangeXPx, RemotePlayerProximityAggroRangeYPx);
        }

        private static bool IsHardInvalidPlayerTargetEntity(Entity? entity)
        {
            var safeEntity = entity;
            if (safeEntity == null)
                return false;
            if (IsCorpseLikeCombatTargetEntity(safeEntity))
                return true;
            if (!IsKnownPlayerEntity(safeEntity))
                return false;
            if (ModEntry.IsEntityDownedForCombat(safeEntity))
                return true;
            if (!IsEntityOnCurrentCombatIdentity(safeEntity))
                return true;

            try
            {
                return safeEntity.destroyed || safeEntity.life <= 0 || !safeEntity._targetable;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsCorpseLikeCombatTargetEntity(Entity? entity)
        {
            return entity is HeroDeadCorpse || entity is dc.en.deco.DeadCorpse;
        }


        private static bool IsKnownPlayerEntity(Entity? entity)
        {
            if (entity == null)
                return false;

            if (entity is Hero || entity is KingSkin)
                return true;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(entity, localHero))
                return true;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null && ReferenceEquals(entity, client))
                    return true;
            }

            return false;
        }

    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
            var attackEvent = $"attack|{encodedSkill}|0|0|{reqTarget}|{dataVal}|{targetUserId}|{dir}";
            var mobType = BuildMobStateTypeSignature(mob);
            var update = new NetNode.MobEventUpdate(mobSyncId, x, y, dir, SingleEvent(attackEvent), mobType, identityToken);
            MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(update));
            net.SendMobEvents(SingleUpdate(update));
        }

        private void Hook_Mob_setAttackTarget(Hook_Mob.orig_setAttackTarget orig, Mob self, Entity e)
        {
            var net = GameMenu.NetRef;
            if (IsHost(net) && ShouldSuppressHostRetarget(self))
            {
                orig(self, e);
                return;
            }

            if (TryResolveFallbackPlayerCombatTarget(self, e, out var fallbackTarget))
            {
                orig(self, fallbackTarget);
                return;
            }

            orig(self, e);
        }

        private void Hook_Mob_setNemesisTarget(Hook_Mob.orig_setNemesisTarget orig, Mob self, Entity e)
        {
            var net = GameMenu.NetRef;
            if (System.Threading.Volatile.Read(ref forceExactNemesisTargetDepth) > 0)
            {
                orig(self, e);
                return;
            }

            if (!IsMobHostileToPlayers(self))
            {
                orig(self, e);
                return;
            }

            if (IsHost(net) && ShouldSuppressHostRetarget(self))
            {
                orig(self, e);
                return;
            }

            if (TryResolveFallbackPlayerCombatTarget(self, e, out var fallbackTarget))
            {
                orig(self, fallbackTarget);
                return;
            }

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
                    for (int i = 0; i < candidateTrackedMobs.Count; i++)
                    {
                        var mob = candidateTrackedMobs[i];
                        MobToId[mob] = i;
                        IdToMob[i] = mob;
                        trackedMobs.Add(mob);
                        trackedMobIndices[mob] = i;
                    }
                    nextRuntimeSyncId = candidateTrackedMobs.Count;

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

            ClearSyncQuiesceAfterRebuild();

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

            if (sameIdentity && trackedMobs.Count > 0)
            {
                baselineTrackedCount = trackedMobs.Count;
                baselineSource = "live";
            }
            else if (sameLastCommittedIdentity && s_lastCommittedTrackedCount > 0)
            {
                baselineTrackedCount = s_lastCommittedTrackedCount;
                baselineSource = "last_commit";
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
                else if (lastCommittedMatchesAuthoritative && s_lastCommittedTrackedCount > 0)
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
                existingIndex = FindTrackedMobIndexLocked(existingMob);
                if (existingIndex < 0)
                {
                    IdToMob.Remove(syncId);
                }
                else
                {
                if (!ReferenceEquals(existingMob, mob) && existingMob != null)
                    trackedMobIndices.Remove(existingMob);

                if (!ReferenceEquals(existingMob, mob))
                    trackedMobs[existingIndex] = mob;

                trackedMobIndices[mob] = existingIndex;
                    IdToMob[syncId] = mob;
                ValidateTrackedIntegrityLocked("track_existing");
                return existingIndex;
                }
            }

            trackedMobs.Add(mob);
            var addedIndex = trackedMobs.Count - 1;
            trackedMobIndices[mob] = addedIndex;
            if (syncId >= 0)
                IdToMob[syncId] = mob;
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

        private static void ResetMobTrackingStateLocked()
        {
            trackedMobs.Clear();
            trackedMobIndices.Clear();
            IdToMob.Clear();
            MobToId.Clear();
            nextRuntimeSyncId = 0;
            s_pendingCulledMobDeaths.Clear();
            clientMobTargets.Clear();
            clientCachedAttackTargetByMob.Clear();
            clientQueuedOldSkillMarkers.Clear();
            hostLastSentContactTargetUserIdByMob.Clear();
            clientLastReportedMobLife.Clear();
            clientLastSentAffectPayloadBySyncId.Clear();
            clientLastSentDrawStateBySyncId.Clear();
            clientLastAppliedHostAffectPayloadBySyncId.Clear();
            hostLastAppliedClientAffectPayloadBySyncId.Clear();
            clientLastAppliedAnimPayloadByMob.Clear();
            clientLastAnimationApplyFrameByMob.Clear();
            clientActiveNetworkAttackMobs.Clear();
            clientNetworkAttackStartFrame.Clear();
            clientAiLockedMobs.Clear();
            clientAuthoritativeStateSeenSyncIds.Clear();
            parsedAnimPayloadCache.Clear();
            hostMobTypeBySyncId.Clear();
            ClearHostClientInterestLocked();
            hostLastSentMobStatesBySyncId.Clear();
            hostDetectedTargets.Clear();
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
            s_trackedMobValidationPending = true;
            var index = FindTrackedMobIndexLocked(mob);
            if (index < 0 &&
                MobToId.TryGetValue(mob, out var syncId) &&
                TryGetTrackedMobBySyncIdLocked(syncId, out var syncMob) &&
                syncMob != null)
            {
                index = FindTrackedMobIndexLocked(syncMob);
            }

            if (index < 0)
            {
                CleanupTrackedMobCachesLocked(mob);
                if (mob != null && MobToId.Remove(mob, out var _sid))
                    IdToMob.Remove(_sid);
                return;
            }

            RemoveTrackedMobAtIndexLocked(index);
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
            trackedMobIndices.Remove(mob);
            clientMobTargets.Remove(mob);
            clientCachedAttackTargetByMob.Remove(mob);
            clientQueuedOldSkillMarkers.Remove(mob);
            hostLastSentContactTargetUserIdByMob.Remove(mob);
            clientLastReportedMobLife.Remove(mob);
            clientLastAppliedAnimPayloadByMob.Remove(mob);
            clientLastAnimationApplyFrameByMob.Remove(mob);
            clientActiveNetworkAttackMobs.Remove(mob);
            clientNetworkAttackStartFrame.Remove(mob);
            clientAiLockedMobs.Remove(mob);

            if (!MobToId.TryGetValue(mob, out var syncId))
                return;

            ClearPerSyncIdCachesLocked(syncId);
        }

        private static void ClearPerSyncIdCachesLocked(int syncId)
        {
            if (syncId < 0)
                return;

            IdToMob.Remove(syncId);
            clientLastSentAffectPayloadBySyncId.Remove(syncId);
            clientLastSentDrawStateBySyncId.Remove(syncId);
            clientLastAppliedHostAffectPayloadBySyncId.Remove(syncId);
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

            return -1;
        }

        private static bool TryGetTrackedMobBySyncIdLocked(int syncId, out Mob? mob)
        {
            mob = null;
            if (syncId < 0 || trackedMobs.Count == 0)
                return false;

            if (!IdToMob.TryGetValue(syncId, out var mappedMob))
                return false;

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
                MobSyncTrace.LogStaleTrackedMapping(syncId, localIndex, "untracked_mob");
                IdToMob.Remove(syncId);
                s_trackedMobValidationPending = true;
                return false;
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
                MobSyncTrace.LogStaleTrackedMapping(syncId, FindTrackedMobIndexLocked(mappedMob), reason);

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
                if (!IsLevelIdentityReadyLocked(mob._level))
                    return MobToId.TryGetValue(mob, out syncId);

                if (GameMenu.NetRef?.IsHost != true)
                    return MobToId.TryGetValue(mob, out syncId);

                if (MobToId.TryGetValue(mob, out syncId))
                    return true;

                syncId = nextRuntimeSyncId++;
                MobToId[mob] = syncId;
                IdToMob[syncId] = mob;
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
            var mappedMob = ResolveTrackedMobBySyncIdLocked(state.Index);
            if (mappedMob != null)
            {
                var reserved = reservedMobs != null && reservedMobs.Contains(mappedMob);
                if (!reserved && DoesMobMatchStateType(mappedMob, state.Type))
                    return mappedMob;

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

            if (TryResolveSingleUnboundTrackedMobForFirstStateLocked(state, reservedMobs, out var unresolvedMob, out var candidateCount) &&
                unresolvedMob != null)
            {
                TryRebindTrackedMobSyncIdLocked(unresolvedMob, state.Index);
                MobSyncTrace.LogBindSyncId("state_first_snapshot", state.Index, state.Type ?? string.Empty, state.X, state.Y);
                return unresolvedMob;
            }

            if (candidateCount > 1)
            {
                MobSyncTrace.LogAmbiguousMatchRejected(
                    "state",
                    state.Index,
                    state.Type ?? string.Empty,
                    state.X,
                    state.Y,
                    candidateCount);
            }

            return null;
        }

        private static bool TryResolveSingleUnboundTrackedMobForFirstStateLocked(
            NetNode.MobStateSnapshot state,
            HashSet<Mob>? reservedMobs,
            out Mob? uniqueMob,
            out int candidateCount)
        {
            uniqueMob = null;
            candidateCount = 0;
            if (trackedMobs.Count == 0)
                return false;

            if (clientAuthoritativeStateSeenSyncIds.Contains(state.Index))
                return false;

            if (string.IsNullOrWhiteSpace(state.Type))
                return false;

            if (!TryGetCurrentLevelIdentityTokenLocked(out _))
                return false;

            QuantizeWorldPositionToPixelsInt32(state.X, state.Y, out var qRefX, out var qRefY);
            var preferredStateSignature = ExtractAffectPresenceSignature(state.StatePayload);

            for (int i = 0; i < trackedMobs.Count; i++)
            {
                var mob = trackedMobs[i];
                if (mob == null)
                    continue;
                if (reservedMobs != null && reservedMobs.Contains(mob))
                    continue;
                if (!IsStateRebindCandidateLocked(mob))
                    continue;
                if (MobToId.TryGetValue(mob, out _))
                    continue;
                if (!DoesMobMatchStateType(mob, state.Type))
                    continue;

                QuantizeWorldPositionToPixelsInt32(GetWorldX(mob!), GetWorldY(mob), out var qMobX, out var qMobY);
                if (qMobX != qRefX || qMobY != qRefY)
                    continue;

                var normalizedPreferredDir = NormalizeDir(state.Dir);
                if (normalizedPreferredDir != 0)
                {
                    try
                    {
                        if (NormalizeDir(mob.dir) != normalizedPreferredDir)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (state.Life != int.MinValue || state.MaxLife != int.MinValue)
                {
                    try
                    {
                        if (state.Life != int.MinValue && mob.life != state.Life)
                            continue;
                        if (state.MaxLife != int.MinValue && mob.maxLife != state.MaxLife)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(preferredStateSignature))
                {
                    try
                    {
                        var stateSignature = BuildMobAffectPresencePayload(mob);
                        if (!string.Equals(stateSignature, preferredStateSignature, StringComparison.Ordinal))
                            continue;
                    }
                    catch
                    {
                        continue;
                    }
                }

                candidateCount++;
                uniqueMob = mob;
            }

            if (candidateCount != 1)
            {
                uniqueMob = null;
                return false;
            }

            return uniqueMob != null;
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
            return null;
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

            ClearPerSyncIdCachesLocked(syncId);

            if (mob != null)
            {
                if (MobToId.TryGetValue(mob, out var _oldId))
                    IdToMob.Remove(_oldId);
                IdToMob.Remove(syncId);
                MobToId[mob] = syncId;
                IdToMob[syncId] = mob;
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

            if (HasLocalQueuedOrChargingSkill(mob) || ShouldPreserveClientAttackMotion(mob))
                return;

            lock (Sync)
            {
                if (clientNetworkAttackStartFrame.TryGetValue(mob, out var startFrame))
                {
                    var elapsed = GetCurrentFrame(mob) - startFrame;
                    if (elapsed < ClientNetworkAttackMinActiveFrames)
                        return;
                }

                clientActiveNetworkAttackMobs.Remove(mob);
                clientNetworkAttackStartFrame.Remove(mob);
            }
        }

        private static void UpdateClientMobAiAuthority(Mob mob)
        {
            if (mob == null)
                return;

            RefreshClientNetworkAttackState(mob);
            if (HasLocalQueuedOrChargingSkill(mob) || IsClientNetworkAttackActive(mob))
            {
                TryUnlockClientMobAiAuthority(mob);
                TryRepairClientMobAttackTarget(mob);
                return;
            }

            TryLockClientMobAiAuthority(mob);
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
                if (IsHardInvalidPlayerTargetEntity(at))
                {
                    mob.setAttackTarget(null);
                    cleared = true;
                }
            }
            catch { }

            try
            {
                var nt = mob.nemesisTarget;
                if (IsHardInvalidPlayerTargetEntity(nt))
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
            if (mob == null)
                return;
            if (!IsMobHostileToPlayers(mob))
                return;

            RefreshHostContactAttackState(mob);
            var clearedInvalidTargets = TryClearHostMobInvalidPlayerTargets(mob);

            if (TryGetCurrentHostAttackTarget(mob, out _))
                return;

            if (ShouldSuppressHostRetarget(mob) && !clearedInvalidTargets)
                return;

            if (TryRepairHostAttackTargetFromCurrentState(mob))
            {
                return;
            }

            if (!TryResolveDetectedHostCombatTarget(mob, out var selected))
            {
                if (clearedInvalidTargets && !HasValidLivingPlayerCombatTarget(mob))
                    TryClearHostMobInvalidPlayerTargets(mob);
                return;
            }

            try
            {
                if (!ReferenceEquals(mob.aTarget, selected))
                    mob.setAttackTarget(selected);
            }
            catch
            {
            }

            if (!TryGetCurrentHostNemesisTarget(mob, out var currentNemesis) ||
                !ReferenceEquals(currentNemesis, selected))
            {
                TrySetNemesisTargetExact(mob, selected);
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
                if (IsInvalidPlayerTargetEntity(at))
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
                if (IsInvalidPlayerTargetEntity(nt))
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

            var attacker = ResolveHostPlayerCombatEntity(attackerUserId);
            if (attacker == null)
            {
                if (replaySpecialHit)
                    TryAssignHostAttackTarget(mob);
                return;
            }

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
            }

            if (!TryGetCurrentHostAttackTarget(mob, out _))
                TryAssignHostAttackTarget(mob);
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

            try
            {
                if (!entity.canBeHitBy(mob))
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private static bool IsAcquirablePlayerCombatTargetForMob(Mob mob, Entity entity, bool requireDetectArea = false)
        {
            if (!IsPreservablePlayerCombatTargetForMob(mob, entity))
                return false;

            try
            {
                if (!entity.canBeDetected())
                    return false;
                if (!entity.canBeHitBy(mob))
                    return false;
            }
            catch
            {
                return false;
            }

            if (!requireDetectArea)
                return true;

            try
            {
                return mob.inDetectArea(entity);
            }
            catch
            {
                return false;
            }
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

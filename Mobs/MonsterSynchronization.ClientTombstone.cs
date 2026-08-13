using System;
using System.Collections.Generic;
using dc.en;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        /// <summary>
        /// Records a client-side dormant tombstone for a host-owned sync id whose local replica is
        /// being removed WITHOUT a host-confirmed death (native unregister, pruning of a destroyed /
        /// level-mismatched mob, renderer culling, etc.). Host-confirmed deaths must NOT tombstone:
        /// the authoritative MOBDIE already owns that cleanup, and resurrecting a confirmed-dead mob
        /// is the regression this whole mechanism exists to avoid.
        /// </summary>
        /// <remarks>
        /// Metadata-only: never stores a Mob reference. Type/x/y are best-effort snapshots taken
        /// while the mob is still readable; failures leave them empty and recovery uses whatever the
        /// incoming host packet carries instead.
        /// </remarks>
        private static void TryCreateClientMobTombstoneLocked(Mob? mob, int syncId, string reason = "client_unregister")
        {
            if (mob == null || syncId <= 0)
            {
                LogTombstoneCreateSkippedLocked(syncId, "null_or_invalid_id");
                return;
            }
            if (!IsClient(LobbySession.NetRef))
            {
                LogTombstoneCreateSkippedLocked(syncId, "not_client");
                return;
            }
            if (authoritativeClientMobDieDepth > 0)
            {
                LogTombstoneCreateSkippedLocked(syncId, "authoritative_mobdie");
                return;
            }
            if (s_levelIdentityToken <= 0)
            {
                LogTombstoneCreateSkippedLocked(syncId, "identity_not_ready");
                return;
            }

            if (!MobToId.TryGetValue(mob, out var ownedSyncId) || ownedSyncId != syncId)
            {
                LogTombstoneCreateSkippedLocked(syncId, "mobtoid_mismatch");
                return;
            }

            if (s_clientMobTombstonesBySyncId.Count >= ClientMobTombstoneMaxCount)
            {
                LogTombstoneCreateSkippedLocked(syncId, "tombstone_cap");
                return;
            }

            var type = string.Empty;
            try { type = BuildMobStateTypeSignature(mob); } catch { }
            if (string.IsNullOrWhiteSpace(type))
                hostMobTypeBySyncId.TryGetValue(syncId, out type);

            double x = 0.0;
            double y = 0.0;
            try
            {
                if (!mob.destroyed)
                {
                    x = GetWorldX(mob);
                    y = GetWorldY(mob);
                }
            }
            catch
            {
            }

            var frame = GetCurrentFrame(mob);
            var tomb = new ClientMobTombstone
            {
                SyncId = syncId,
                Generation = s_levelIdentityToken,
                Type = type ?? string.Empty,
                X = double.IsFinite(x) ? x : 0.0,
                Y = double.IsFinite(y) ? y : 0.0,
                CreatedFrame = frame,
                LastSeenFrame = frame,
                LastRecreateAttemptFrame = -99999.0,
                RecreateFailCount = 0
            };

            s_clientMobTombstonesBySyncId[syncId] = tomb;
            MobSyncTrace.LogClientTombstoneCreated(syncId, tomb.Type, reason ?? "client_unregister");
        }

        /// <summary>
        /// Shared client mapping-removal boundary. Snapshot a Design-A tombstone while
        /// <see cref="MobToId"/> still owns the id, immediately before that mapping is wiped.
        /// Host, authoritative death, identity-not-ready, and mismatched owners are no-ops.
        /// </summary>
        private static void TryCaptureClientTombstoneForMappedMobLocked(Mob? mob, string reason)
        {
            if (mob == null)
                return;
            if (!MobToId.TryGetValue(mob, out var syncId) || syncId <= 0)
                return;
            TryCreateClientMobTombstoneLocked(mob, syncId, reason);
        }

        private static bool MappingOwnsSyncIdLocked(Mob mob, int syncId)
        {
            if (mob == null || syncId <= 0)
                return false;
            if (MobToId.TryGetValue(mob, out var owned) && owned == syncId)
                return true;
            return IdToMob.TryGetValue(syncId, out var forward) && ReferenceEquals(forward, mob);
        }

        private static bool IsInvalidMappedReplicaLocked(Mob? mob)
        {
            if (mob == null)
                return true;
            try
            {
                if (mob.destroyed || mob._level == null)
                    return true;
                return !DoesLevelMatchCurrentIdentityLocked(mob._level);
            }
            catch
            {
                return true;
            }
        }

        private static bool HasClientMobTombstoneLocked(int syncId)
        {
            return syncId > 0 && IsClient(LobbySession.NetRef) && s_clientMobTombstonesBySyncId.ContainsKey(syncId);
        }

        /// <summary>
        /// Last-resort resolver for a host packet that missed every existing mapping and fallback.
        /// When a dormant tombstone exists for this sync id, recreate a replica from the host's own
        /// pushed identity and rebind it. No new replica logic: reuses the proven MOBREG primitive.
        /// Enforces safety bounds (generation, TTL, recreate cooldown, failure cap) and is only ever
        /// invoked on miss paths, never on the hot resolve path.
        /// </summary>
        private static bool TryRecoverTombstonedSyncIdLocked(
            int syncId,
            string? incomingType,
            double x,
            double y,
            out Mob? recovered)
        {
            recovered = null;
            if (syncId <= 0 || !IsClient(LobbySession.NetRef))
                return false;

            s_diagTombstoneLookups++;
            if (!s_clientMobTombstonesBySyncId.TryGetValue(syncId, out var tomb) || tomb == null)
                return false;
            s_diagTombstoneHits++;

            var frame = GetCurrentFrame(null);

            // Generation change (level reset/rebuild) invalidates every tombstone outright.
            if (tomb.Generation != s_levelIdentityToken)
            {
                RemoveClientMobTombstoneLocked(syncId, "generation_changed");
                return false;
            }

            // TTL is measured from LAST host activity so a mob the host keeps pushing for a long
            // fight stays recoverable, while an id the host went silent on is forgotten.
            if (frame - tomb.LastSeenFrame >= ClientMobTombstoneRetainFrames)
            {
                RemoveClientMobTombstoneLocked(syncId, "ttl_expired");
                return false;
            }

            tomb.LastSeenFrame = frame;

            // Recreate cooldown: rate-limits duplicate attempts without starving recovery.
            if (frame - tomb.LastRecreateAttemptFrame < ClientMobTombstoneRecreateCooldownFrames)
                return false;
            if (tomb.RecreateFailCount >= ClientMobTombstoneMaxRecreateFails)
            {
                RemoveClientMobTombstoneLocked(syncId, "recreate_fail_cap");
                return false;
            }

            // The host now claims a different runtime mob type under this id (in-place replacement,
            // elite transform, boss phase swap). The tombstone is stale against that newer identity.
            if (!string.IsNullOrWhiteSpace(incomingType) &&
                !string.IsNullOrWhiteSpace(tomb.Type) &&
                !TypesReferToSameMobClass(incomingType, tomb.Type))
            {
                RemoveClientMobTombstoneLocked(syncId, "identity_replaced");
                return false;
            }

            var effectiveType = string.IsNullOrWhiteSpace(incomingType) ? tomb.Type ?? string.Empty : incomingType;
            var useX = double.IsFinite(x) ? x : tomb.X;
            var useY = double.IsFinite(y) ? y : tomb.Y;

            tomb.LastRecreateAttemptFrame = frame;

            var replica = TryCreateClientMobReplica(effectiveType, useX, useY);
            if (replica == null)
            {
                tomb.RecreateFailCount++;
                MobSyncTrace.LogClientTombstoneRecovery(syncId, effectiveType, false, "replica_create_failed");
                return false;
            }

            TryRebindTrackedMobSyncIdLocked(replica, syncId);

            // TryRebindTrackedMobSyncIdLocked can early-return without binding when the level
            // identity is not ready yet. Only a confirmed IdToMob binding is a successful recovery;
            // otherwise keep the tombstone and let the next miss retry after the cooldown.
            if (!IdToMob.TryGetValue(syncId, out var boundMob) || !ReferenceEquals(boundMob, replica))
            {
                tomb.RecreateFailCount++;
                MobSyncTrace.LogClientTombstoneRecovery(syncId, effectiveType, false, "rebind_not_ready");
                return false;
            }

            // A successful bind of ANY mob to this sync id supersedes the dormant tombstone. The
            // explicit Remove here (rather than relying on TryRebindTrackedMobSyncIdLocked) keeps
            // the recovery log entry specific; both paths are idempotent.
            s_clientMobTombstonesBySyncId.Remove(syncId);
            MobSyncTrace.LogClientTombstoneRecovery(syncId, effectiveType, true, "replica_bound");
            recovered = replica;
            return true;
        }

        private static void RemoveClientMobTombstoneLocked(int syncId, string reason)
        {
            if (s_clientMobTombstonesBySyncId.Remove(syncId, out var tomb))
                MobSyncTrace.LogClientTombstoneCleared(syncId, reason);
        }

        private static void LogTombstoneCreateSkippedLocked(int syncId, string reason)
        {
            LogFocusSyncLifecycleLocked(syncId, "TOMBSTONE_SKIP", reason);
        }

        private static void LogFocusUnregisterWithoutMappingLocked(Mob? mob)
        {
            var syncId = ClientFocusDesyncSyncId;
            if (mob != null && MobToId.TryGetValue(mob, out var mapped) && mapped > 0)
                syncId = mapped;
            LogFocusSyncLifecycleLocked(syncId, "UNREGISTER_NO_MAPPING", "mobtoid_missing");
        }

        private static void LogFocusSyncLifecycleLocked(int syncId, string evt, string detail)
        {
            if (syncId != ClientFocusDesyncSyncId)
                return;
            MobSyncTrace.LogFocusSyncLifecycle(evt, syncId, detail);
        }

        private static string GetMobSyncRoleLabelLocked()
        {
            var net = LobbySession.NetRef;
            if (net == null || !net.IsAlive)
                return "none";
            return net.IsHost ? "host" : "client";
        }

        private static bool ReadMobDestroyedSafe(Mob? mob)
        {
            if (mob == null)
                return true;
            try { return mob.destroyed; }
            catch { return true; }
        }

        private static void LogRemoveAttemptLocked(Mob? mob, int syncId, string reason)
        {
            MobSyncTrace.LogRemoveAttempt(
                syncId,
                reason,
                ReadMobDestroyedSafe(mob),
                authoritativeClientMobDieDepth,
                GetMobSyncRoleLabelLocked());
            LogFocusSyncLifecycleLocked(syncId, "REMOVE", reason);
        }

        private static void LogResolveFailLocked(
            int syncId,
            bool hasIdToMob,
            bool hasMobToId,
            bool destroyed,
            string reason)
        {
            s_diagResolveFails++;
            var frame = GetCurrentFrame(null);
            var shouldLog = syncId == ClientFocusDesyncSyncId ||
                            frame - s_diagLastResolveFailLogFrame >= 15.0 ||
                            s_diagLastResolveFailSyncId != syncId;
            if (shouldLog)
            {
                s_diagLastResolveFailLogFrame = frame;
                s_diagLastResolveFailSyncId = syncId;
                MobSyncTrace.LogResolveFail(
                    syncId,
                    hasIdToMob,
                    hasMobToId,
                    destroyed,
                    s_levelIdentityToken,
                    reason);
            }

            LogFocusSyncLifecycleLocked(syncId, "RESOLVE_FAIL", reason);
        }

        private static void FlushClientDiagPerfLocked()
        {
            if (!IsClient(LobbySession.NetRef))
                return;

            var frame = GetCurrentFrame(null);
            if (frame - s_diagLastPerfFlushFrame < 30.0)
                return;

            s_diagLastPerfFlushFrame = frame;
            MobSyncTrace.LogClientDiagPerf(
                s_diagTombstoneLookups,
                s_diagTombstoneHits,
                s_diagResolveFails,
                s_diagHitResolveFails,
                s_diagMissingSyncPackets);
            s_diagTombstoneLookups = 0;
            s_diagTombstoneHits = 0;
            s_diagResolveFails = 0;
            s_diagHitResolveFails = 0;
            s_diagMissingSyncPackets = 0;
        }

        /// <summary>Compares two type signatures by their runtime class key so "Rampager|Rampager" equals "Rampager".</summary>
        private static bool TypesReferToSameMobClass(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return true;
            var classA = ExtractRuntimeClassKey(a);
            var classB = ExtractRuntimeClassKey(b);
            if (string.IsNullOrWhiteSpace(classA) || string.IsNullOrWhiteSpace(classB))
                return true;
            return string.Equals(classA, classB, StringComparison.Ordinal);
        }
    }
}
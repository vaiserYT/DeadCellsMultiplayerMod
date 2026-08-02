using System;
using System.Collections.Generic;
using dc.en;
using DeadCellsMultiplayerMod.Mobs.Bosses;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    /// <summary>
    /// Host-owned NetId registry. Native game entity ids and client entity-list indexes are never
    /// identities. The host assigns monotonic NetIds per level generation; clients only bind local
    /// <see cref="Mob"/> references to those NetIds via MOBREG or a one-shot type+spawn state bind.
    /// Conceptual portable form: <see cref="DeadCellsMultiplayerMod.PortableCore.NetEntityId"/>.
    /// </summary>
    public partial class MobsSynchronization
    {
        /// <summary>One-shot bind radius for unbound locals matching a host registry/state entry.</summary>
        private const double ClientRegistryBindMaxDistancePx = 24.0 * 8.0;

        private static readonly List<NetNode.MobRegistryEntry> s_mobRegistryScratch = new();
        private static int s_lastHostMobRegistryToken;
        private static double s_lastHostMobRegistrySendFrame = -99999.0;
        private static int s_hostMobRegistryResendsRemaining;

        private static bool IsHostAuthorityForNetIds() =>
            GameMenu.NetRef?.IsHost == true;

        /// <summary>
        /// Host assigns NetIds in walk order (assignment order only). Clients track unbound mobs
        /// and wait for MOBREG / state packets — they never invent NetIds from list index.
        /// </summary>
        private static void AssignHostNetIdsForRebuildLocked(IReadOnlyList<Mob> candidateTrackedMobs)
        {
            // Continue the session-monotonic allocator instead of restarting at 0. See the note in
            // ResetMobTrackingStateLocked: an id must never be reused for a different enemy, because
            // a same-level rebuild produces an identical wire generation and cannot fence the reuse.
            for (var i = 0; i < candidateTrackedMobs.Count; i++)
            {
                var mob = candidateTrackedMobs[i];
                if (mob == null)
                    continue;

                trackedMobs.Add(mob);
                trackedMobIndices[mob] = trackedMobs.Count - 1;

                if (!IsHostAuthorityForNetIds())
                    continue;

                var netId = nextRuntimeSyncId++;
                MobToId[mob] = netId;
                IdToMob[netId] = mob;
                StampHostBossNetIdLocked(mob, netId);
            }
        }

        private static void StampHostBossNetIdLocked(Mob mob, int netId)
        {
            if (mob == null || netId < 0)
                return;

            try
            {
                if (!BossSyncHelpers.IsBossMob(mob))
                    return;
            }
            catch
            {
                return;
            }

            // Fold boss bid into the same NetId space for this level generation.
            // EntityId is 1-based on the wire (0 = none). NetId remains 0-based in maps.
            var entityId = netId + 1;
            s_hostBossIdentities.Remove(mob);
            s_hostBossIdentities.Add(mob, new HostBossIdentity { EntityId = entityId });
            TrackHostPrimaryBossLocked(mob, entityId);
        }

        private static void QueueHostMobRegistryAfterRebuild()
        {
            if (!IsHostAuthorityForNetIds())
                return;

            lock (Sync)
            {
                s_hostMobRegistryResendsRemaining = HostAuthoritativeBootstrapResyncCount;
                s_lastHostMobRegistryToken = s_levelIdentityToken;
                s_lastHostMobRegistrySendFrame = -99999.0;
            }
        }

        /// <summary>
        /// Re-arms the MOBREG broadcast after a runtime mob registration, without disturbing a
        /// bootstrap burst that is still in flight.
        /// </summary>
        private static void MarkHostMobRegistryDirtyForRuntimeSpawn()
        {
            if (!IsHostAuthorityForNetIds())
                return;

            lock (Sync)
            {
                if (s_hostMobRegistryResendsRemaining < 1)
                    s_hostMobRegistryResendsRemaining = 1;
            }
        }

        private static void FlushHostMobRegistry(NetNode net)
        {
            if (!IsHost(net) || IsSyncQuiescedForTransition())
                return;
            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return;

            var frame = GetCurrentFrame(null);
            lock (Sync)
            {
                if (s_lastHostMobRegistryToken != identityToken)
                {
                    s_lastHostMobRegistryToken = identityToken;
                    s_hostMobRegistryResendsRemaining = HostAuthoritativeBootstrapResyncCount;
                    s_lastHostMobRegistrySendFrame = -99999.0;
                }

                if (s_hostMobRegistryResendsRemaining <= 0)
                    return;

                if (frame - s_lastHostMobRegistrySendFrame < HostAuthoritativeBootstrapResyncIntervalFrames)
                    return;

                s_lastHostMobRegistrySendFrame = frame;
                s_hostMobRegistryResendsRemaining--;
                BuildHostMobRegistryEntriesLocked(identityToken, s_mobRegistryScratch);
            }

            if (s_mobRegistryScratch.Count == 0)
                return;

            net.SendMobRegistry(identityToken, s_mobRegistryScratch);
            s_mobRegistryScratch.Clear();
        }

        private static void BuildHostMobRegistryEntriesLocked(int generation, List<NetNode.MobRegistryEntry> dst)
        {
            dst.Clear();
            for (var i = 0; i < trackedMobs.Count; i++)
            {
                var mob = trackedMobs[i];
                if (mob == null || !MobToId.TryGetValue(mob, out var netId) || netId < 0)
                    continue;

                double x;
                double y;
                try
                {
                    x = GetWorldX(mob);
                    y = GetWorldY(mob);
                }
                catch
                {
                    continue;
                }

                var type = BuildMobStateTypeSignature(mob);
                dst.Add(new NetNode.MobRegistryEntry(netId, generation, type, x, y));
            }
        }

        private static void ConsumeIncomingMobRegistry(NetNode net)
        {
            if (!net.TryConsumeMobRegistry(out var entries) || entries == null || entries.Count == 0)
                return;

            try
            {
                var rejectedCount = 0;
                var rejectedGeneration = 0;
                lock (Sync)
                {
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        if (!ShouldAcceptPacketGenerationLocked(entry.Generation, ref rejectedCount, ref rejectedGeneration))
                            continue;

                        if (TryGetTrackedMobBySyncIdLocked(entry.NetId, out var already) &&
                            already != null &&
                            DoesMobMatchStateType(already, entry.Type))
                        {
                            continue;
                        }

                        if (TryBindUnboundMobByTypeAndSpawnLocked(
                                entry.NetId,
                                entry.Type,
                                entry.X,
                                entry.Y,
                                reservedMobs: null,
                                out var bound) &&
                            bound != null)
                        {
                            MobSyncTrace.LogBindSyncId(
                                "mobreg_bind",
                                entry.NetId,
                                entry.Type ?? string.Empty,
                                entry.X,
                                entry.Y);
                            continue;
                        }

                        // No local mob to bind. For level-bootstrap mobs this means the packet was
                        // early and a later resync repairs it, but for a host runtime spawn (malaise
                        // wave, summon, elite replacement) the mob simply does not exist here and
                        // never will. Build the replica so the second player can see and fight it.
                        var spawned = TryCreateClientMobReplica(entry.Type, entry.X, entry.Y);
                        if (spawned != null)
                        {
                            MobToId[spawned] = entry.NetId;
                            IdToMob[entry.NetId] = spawned;
                            if (FindTrackedMobIndexLocked(spawned) < 0)
                            {
                                trackedMobs.Add(spawned);
                                trackedMobIndices[spawned] = trackedMobs.Count - 1;
                            }

                            MobSyncTrace.LogBindSyncId(
                                "mobreg_spawn",
                                entry.NetId,
                                entry.Type ?? string.Empty,
                                entry.X,
                                entry.Y);
                        }
                    }
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(entries);
            }
        }

        /// <summary>
        /// One-shot bind for an unbound local mob: matching type + nearest spawn within distance.
        /// Never steals a healthy NetId mapping from another living mob.
        /// </summary>
        private static bool TryBindUnboundMobByTypeAndSpawnLocked(
            int netId,
            string? type,
            double x,
            double y,
            HashSet<Mob>? reservedMobs,
            out Mob? bound)
        {
            bound = null;
            if (netId < 0 || string.IsNullOrWhiteSpace(type))
                return false;
            if (!double.IsFinite(x) || !double.IsFinite(y))
                return false;

            var maxDistanceSq = ClientRegistryBindMaxDistancePx * ClientRegistryBindMaxDistancePx;
            var bestDistanceSq = double.MaxValue;
            var secondBestDistanceSq = double.MaxValue;
            Mob? best = null;
            var candidateCount = 0;

            for (var i = 0; i < trackedMobs.Count; i++)
            {
                var mob = trackedMobs[i];
                if (mob == null || (reservedMobs != null && reservedMobs.Contains(mob)))
                    continue;
                if (!IsStateRebindCandidateLocked(mob))
                    continue;
                if (MobToId.TryGetValue(mob, out _))
                    continue;
                if (!DoesMobMatchStateType(mob, type))
                    continue;

                double dx;
                double dy;
                try
                {
                    dx = GetWorldX(mob) - x;
                    dy = GetWorldY(mob) - y;
                }
                catch
                {
                    continue;
                }

                if (!double.IsFinite(dx) || !double.IsFinite(dy))
                    continue;

                var distanceSq = dx * dx + dy * dy;
                if (distanceSq > maxDistanceSq)
                    continue;

                candidateCount++;
                if (distanceSq < bestDistanceSq)
                {
                    secondBestDistanceSq = bestDistanceSq;
                    bestDistanceSq = distanceSq;
                    best = mob;
                }
                else if (distanceSq < secondBestDistanceSq)
                {
                    secondBestDistanceSq = distanceSq;
                }
            }

            if (best == null)
                return false;

            if (candidateCount > 1 && secondBestDistanceSq < double.MaxValue)
            {
                var gap = Math.Sqrt(secondBestDistanceSq) - Math.Sqrt(bestDistanceSq);
                if (!IsBossRelatedEntity(type) && gap < ClientStateRebindMinimumGapPx)
                    return false;
            }

            TryRebindTrackedMobSyncIdLocked(best, netId);
            bound = best;
            return true;
        }
    }
}

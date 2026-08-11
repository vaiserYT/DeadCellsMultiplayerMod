using System.Globalization;
using dc;
using dc.en;
using dc.hl.types;
using dc.tool.atk;
using dc.tool.skill;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using DeadCellsMultiplayerMod.Mobs.Levelinit;
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

        private static void ConsumeIncomingHostMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            try
            {
                MobSyncTrace.LogRecvStates("hostStatesFromHost", states);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingHostMobStates(states);
            }
            finally
            {
                NetNode.ReleaseConsumedList(states);
            }
        }

        private static void ConsumeIncomingHostMobMoves(NetNode net)
        {
            if (!net.TryConsumeMobMoves(out var moves))
                return;

            try
            {
                MobSyncTrace.LogRecvMoves("hostMovesFromHost", moves);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingHostMobMoves(moves);
            }
            finally
            {
                NetNode.ReleaseConsumedList(moves);
            }
        }

        private static void ConsumeIncomingClientMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            try
            {
                MobSyncTrace.LogRecvStates("clientAffectFromClient", states);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingClientMobStatesOnHost(states);
            }
            finally
            {
                NetNode.ReleaseConsumedList(states);
            }
        }

        private static void ApplyIncomingClientMobStatesOnHost(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            s_clientAffectAppliesScratch.Clear();
            s_latestPacketSyncIdsScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                // Queues append chunked packets. Process newest-to-oldest and accept one state
                // per sync id so a delayed older chunk cannot overwrite the latest affect state.
                for (int i = states.Count - 1; i >= 0; i--)
                {
                    var state = states[i];
                    if (!ShouldAcceptPacketGenerationLocked(state.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;
                    if (!s_latestPacketSyncIdsScratch.Add(state.Index))
                        continue;

                    var mob = ResolveMobBySyncIdLocked(state.Index);
                    if (mob == null)
                        continue;
                    if (string.IsNullOrEmpty(state.StatePayload))
                        continue;

                    s_clientAffectAppliesScratch.Add(new PendingClientAffectApply(state.Index, mob, state.StatePayload));
                }
            }

            LogRejectedPacketGeneration("clientStateOnHost", rejectedCount, rejectedGeneration);
            s_latestPacketSyncIdsScratch.Clear();

            for (int i = 0; i < s_clientAffectAppliesScratch.Count; i++)
            {
                var entry = s_clientAffectAppliesScratch[i];
                ApplyClientReportedAffectStateOnHost(entry.SyncId, entry.Mob, entry.StatePayload);
            }

            s_clientAffectAppliesScratch.Clear();
        }

        private static void ApplyClientReportedAffectStateOnHost(int mobSyncId, Mob mob, string? wirePayload)
        {
            if (mob == null || mob.destroyed)
                return;
            if (BossSyncHelpers.IsBossMob(mob))
                return;

            if (!TryDecodeStatePayloadFromWire(wirePayload, out var payload))
                return;

            lock (Sync)
            {
                if (hostLastAppliedClientAffectPayloadBySyncId.TryGetValue(mobSyncId, out var lastApplied) &&
                    string.Equals(lastApplied, payload, StringComparison.Ordinal))
                {
                    return;
                }

                hostLastAppliedClientAffectPayloadBySyncId[mobSyncId] = payload;
            }

            // Client status reports are not authoritative over the host's complete affect list.
            // Track only affects that this client path actually created, so an empty/different client
            // payload cannot prune native elite phase, invulnerability, stun or mutation state.
            ApplyClientOwnedAffectPayloadOnHost(mobSyncId, mob, payload);
        }

        private static void ApplyClientOwnedAffectPayloadOnHost(int mobSyncId, Mob mob, string payload)
        {
            if (mob == null || mob.destroyed || mobSyncId < 0)
                return;

            var desired = ParseAffectStatePayload(payload);
            HashSet<int> previousOwned;
            lock (Sync)
            {
                previousOwned = hostClientOwnedAffectIdsByMob.TryGetValue(mob, out var existing)
                    ? new HashSet<int>(existing)
                    : new HashSet<int>();
            }

            var nextOwned = new HashSet<int>();

            foreach (var affectId in previousOwned)
            {
                if (desired.Contains(affectId))
                {
                    nextOwned.Add(affectId);
                    continue;
                }

                try
                {
                    mob.removeAllAffects(affectId);
                }
                catch
                {
                    // Keep ownership if removal failed so a later state can retry safely.
                    nextOwned.Add(affectId);
                }
            }

            // Do not create new host affects from client presence reports. Client combat prediction
            // previously called setAffectS(..., 99999) here and permanently froze mobs after hits
            // during charge/attack (both peers). Host already applies damage via MOBHIT.

            lock (Sync)
            {
                if (nextOwned.Count > 0)
                    hostClientOwnedAffectIdsByMob[mob] = nextOwned;
                else
                    hostClientOwnedAffectIdsByMob.Remove(mob);
            }
        }

        private static bool ShouldAcceptHostPositionFrameLocked(int syncId, double incomingFrame, bool forceWhenUntimed)
        {
            if (syncId < 0)
                return false;

            if (!double.IsFinite(incomingFrame) || incomingFrame <= 0.0)
            {
                if (forceWhenUntimed || !clientLastAcceptedHostPositionFrameBySyncId.ContainsKey(syncId))
                    return true;
                return false;
            }

            if (clientLastAcceptedHostPositionFrameBySyncId.TryGetValue(syncId, out var lastFrame) &&
                incomingFrame + 0.001 < lastFrame)
            {
                return false;
            }

            if (!clientLastAcceptedHostPositionFrameBySyncId.TryGetValue(syncId, out lastFrame) || incomingFrame > lastFrame)
                clientLastAcceptedHostPositionFrameBySyncId[syncId] = incomingFrame;
            return true;
        }

        private static void ApplyIncomingHostMobStates(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            s_hostStateAppliesScratch.Clear();
            s_usedTrackedMobsScratch.Clear();
            s_latestPacketSyncIdsScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();

                // A newest delta snapshot may omit the type while an older full snapshot in the
                // same accumulated batch contains it. Cache current-generation type metadata first
                // so newest-wins processing can still bind a previously-unmapped mob safely.
                for (int i = 0; i < states.Count; i++)
                {
                    var metadataState = states[i];
                    if (metadataState.Generation == s_levelIdentityToken &&
                        !string.IsNullOrWhiteSpace(metadataState.Type))
                    {
                        hostMobTypeBySyncId[metadataState.Index] = metadataState.Type;
                    }
                }

                // Incoming state packets are accumulated across wire chunks. Newest snapshot for
                // each sync id wins; this prevents stale HP/position data and 0-HP ghost regressions.
                for (int i = states.Count - 1; i >= 0; i--)
                {
                    var state = states[i];
                    if (!ShouldAcceptPacketGenerationLocked(state.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;
                    if (!s_latestPacketSyncIdsScratch.Add(state.Index))
                        continue;

                    if (!string.IsNullOrWhiteSpace(state.Type))
                        hostMobTypeBySyncId[state.Index] = state.Type;

                    var effectiveState = state;
                    if (string.IsNullOrWhiteSpace(effectiveState.Type) &&
                        hostMobTypeBySyncId.TryGetValue(state.Index, out var cachedType) &&
                        !string.IsNullOrWhiteSpace(cachedType))
                    {
                        effectiveState = new NetNode.MobStateSnapshot(
                            state.Index, state.X, state.Y, state.Dir, state.Life, state.MaxLife,
                            state.AnimPayload, cachedType, state.StatePayload, state.Generation,
                            state.Time, state.Dx, state.Dy);
                    }

                    var mob = ResolveTrackedMobForIncomingStateLocked(effectiveState, s_usedTrackedMobsScratch);
                    if (mob == null)
                        continue;

                    s_usedTrackedMobsScratch.Add(mob);
                    var incomingDir = NormalizeDir(state.Dir);
                    clientLastReportedMobLife[mob] = state.Life;
                    clientAuthoritativeStateSeenSyncIds.Add(state.Index);

                    var hasPreviousTarget = clientMobTargets.TryGetValue(mob, out var previousTarget);
                    var positionFresh = ShouldAcceptHostPositionFrameLocked(
                        state.Index,
                        state.Time,
                        forceWhenUntimed: !hasPreviousTarget || state.Life <= 0);
                    s_hostStateAppliesScratch.Add(new PendingHostStateApply(
                        state.Index,
                        mob,
                        state.Life,
                        state.MaxLife,
                        positionFresh ? incomingDir : 0,
                        positionFresh ? state.StatePayload ?? string.Empty : string.Empty));
                    if (!positionFresh && hasPreviousTarget)
                        continue;

                    var mergedAnimPayload = state.AnimPayload ?? string.Empty;
                    var hasExplicitStatePayload = TryDecodeStatePayloadFromWire(state.StatePayload, out var mergedStatePayload);
                    if (hasPreviousTarget)
                    {
                        if (string.IsNullOrEmpty(mergedAnimPayload))
                            mergedAnimPayload = previousTarget.AnimPayload;
                        if (!hasExplicitStatePayload)
                            mergedStatePayload = previousTarget.StatePayload;
                    }

                    // See ApplyIncomingHostMobMoves: the horizontal snap flag must not persist
                    // across packets or interpolation dies after the first teleport. Vertical
                    // snap stays latched only while no horizontal teleport is also in flight.
                    var forcePositionSnap =
                        ShouldForceClientMobPositionSnap(
                            mob,
                            hasPreviousTarget,
                            previousTarget,
                            state.X,
                            state.Y,
                            state.AnimPayload);
                    var forceVerticalPositionSnap =
                        (hasPreviousTarget && previousTarget.ForceVerticalPositionSnap && !previousTarget.ForcePositionSnap) ||
                        ShouldForceClientMobVerticalPositionSnap(
                            mob,
                            hasPreviousTarget,
                            previousTarget,
                            state.X,
                            state.Y,
                            state.AnimPayload,
                            state.Dy);

                    clientMobTargets[mob] = new ClientMobState(
                        state.X,
                        state.Y,
                        incomingDir,
                        state.Life,
                        state.MaxLife,
                        mergedAnimPayload,
                        mergedStatePayload,
                        state.Time,
                        state.Dx,
                        state.Dy,
                        GetCurrentFrame(mob),
                        forcePositionSnap,
                        forceVerticalPositionSnap);
                }
            }

            LogRejectedPacketGeneration("hostStateOnClient", rejectedCount, rejectedGeneration);

            s_usedTrackedMobsScratch.Clear();
            s_latestPacketSyncIdsScratch.Clear();

            for (int i = 0; i < s_hostStateAppliesScratch.Count; i++)
            {
                var entry = s_hostStateAppliesScratch[i];
                if (entry.Dir != 0)
                {
                    try { entry.Mob.dir = entry.Dir; } catch { }
                }
                ApplyAuthoritativeLifeState(entry.Mob, entry.Life, entry.MaxLife);
                ApplyAuthoritativeAffectState(entry.SyncId, entry.Mob, entry.StatePayload);
            }

            s_hostStateAppliesScratch.Clear();
        }

        private static void ApplyIncomingHostMobMoves(IReadOnlyList<NetNode.MobMoveSnapshot> moves)
        {
            if (moves == null || moves.Count == 0)
                return;

            s_latestPacketSyncIdsScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();

                for (int i = moves.Count - 1; i >= 0; i--)
                {
                    var move = moves[i];
                    if (!ShouldAcceptPacketGenerationLocked(move.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;
                    if (!s_latestPacketSyncIdsScratch.Add(move.Index))
                        continue;

                    var mob = ResolveTrackedMobBySyncIdLocked(move.Index);
                    if (mob == null)
                        continue;
                    if (!ShouldAcceptHostPositionFrameLocked(move.Index, move.Time, forceWhenUntimed: false))
                        continue;

                    var mergedAnimPayload = move.AnimPayload ?? string.Empty;
                    if (clientMobTargets.TryGetValue(mob, out var previousTarget))
                    {
                        if (string.IsNullOrEmpty(mergedAnimPayload))
                            mergedAnimPayload = previousTarget.AnimPayload;

                        // ForcePositionSnap must describe THIS packet's discontinuity only.
                        // Carrying the previous flag forward (|=) pinned the mob to hard-snap on
                        // every subsequent frame after one teleport — permanently killing
                        // interpolation and making high-BC blink enemies stutter for the rest of
                        // the fight. The vertical flag is still latched (a fall-through recovery
                        // must persist until the mob is grounded), but the horizontal snap is
                        // recomputed fresh each packet.
                        var forcePositionSnap =
                            ShouldForceClientMobPositionSnap(
                                mob,
                                true,
                                previousTarget,
                                move.X,
                                move.Y,
                                move.AnimPayload);
                        var forceVerticalPositionSnap =
                            (previousTarget.ForceVerticalPositionSnap && !previousTarget.ForcePositionSnap) ||
                            ShouldForceClientMobVerticalPositionSnap(
                                mob,
                                true,
                                previousTarget,
                                move.X,
                                move.Y,
                                move.AnimPayload,
                                move.Dy);

                        clientMobTargets[mob] = new ClientMobState(
                            move.X,
                            move.Y,
                            NormalizeDir(move.Dir),
                            previousTarget.Life,
                            previousTarget.MaxLife,
                            mergedAnimPayload,
                            previousTarget.StatePayload,
                            move.Time,
                            move.Dx,
                            move.Dy,
                            GetCurrentFrame(mob),
                            forcePositionSnap,
                            forceVerticalPositionSnap);
                    }
                }
            }

            LogRejectedPacketGeneration("hostMoveOnClient", rejectedCount, rejectedGeneration);
            s_latestPacketSyncIdsScratch.Clear();
        }

        private static bool ShouldForceClientMobPositionSnap(
            Mob mob,
            bool hasPreviousTarget,
            ClientMobState previousTarget,
            double targetX,
            double targetY,
            string? animPayload)
        {
            if (!TryGetClientMobSnapSource(
                    mob,
                    hasPreviousTarget,
                    previousTarget,
                    targetX,
                    targetY,
                    out var sourceX,
                    out var sourceY))
            {
                return false;
            }

            return ShouldTreatMobMoveAsTeleport(sourceX, sourceY, targetX, targetY, animPayload);
        }

        private static bool ShouldForceClientMobVerticalPositionSnap(
            Mob mob,
            bool hasPreviousTarget,
            ClientMobState previousTarget,
            double targetX,
            double targetY,
            string? animPayload,
            double hostDy)
        {
            // Never vertically relocate a mob on the same packet that created/repaired its mapping.
            // Wait for an established target first so a distant same-type fallback cannot be placed
            // below the wrong floor.
            if (!hasPreviousTarget || !IsTeleportLikeAnimPayload(animPayload))
                return false;

            var hostVelocityYPx = ToPredictionPixelsPerFrame(hostDy);
            if (!double.IsFinite(hostVelocityYPx) ||
                System.Math.Abs(hostVelocityYPx) > ClientTeleportVerticalSnapMaxHostVerticalSpeedPx)
            {
                return false;
            }

            if (!TryGetClientMobSnapSource(
                    mob,
                    hasPreviousTarget,
                    previousTarget,
                    targetX,
                    targetY,
                    out var sourceX,
                    out var sourceY))
            {
                return false;
            }

            // Every confirmed gravity-mob teleport receives the safe-above-floor landing path,
            // including mostly horizontal teleports. Keeping the client's old Y while moving to a
            // different floor profile is what allowed the entity to start inside solid tiles.
            var horizontalDelta = System.Math.Abs(targetX - sourceX);
            var verticalDelta = System.Math.Abs(targetY - sourceY);
            return verticalDelta >= ClientTeleportVerticalSnapMinimumDeltaPx ||
                   horizontalDelta >= ClientTeleportAnimSnapDistancePx;
        }

        private static bool TryGetClientMobSnapSource(
            Mob mob,
            bool hasPreviousTarget,
            ClientMobState previousTarget,
            double targetX,
            double targetY,
            out double sourceX,
            out double sourceY)
        {
            sourceX = 0.0;
            sourceY = 0.0;
            if (mob == null || !double.IsFinite(targetX) || !double.IsFinite(targetY))
                return false;

            if (hasPreviousTarget)
            {
                // Prefer the mob's ACTUAL rendered position as the teleport source. The last
                // target may not have been reached yet (grounded mobs converge over several
                // frames), so measuring from the target under-reports the real visual jump and
                // can miss a blink that happens mid-correction. Fall back to the previous target
                // only when the live position is unreadable.
                double liveX, liveY;
                try
                {
                    liveX = GetWorldX(mob);
                    liveY = GetWorldY(mob);
                }
                catch
                {
                    liveX = double.NaN;
                    liveY = double.NaN;
                }

                if (double.IsFinite(liveX) && double.IsFinite(liveY))
                {
                    // Use whichever source is FARTHER from the target, so neither a lagging
                    // render position nor a stale target can hide a genuine discontinuity.
                    var targetToPrevSq =
                        (previousTarget.X - targetX) * (previousTarget.X - targetX) +
                        (previousTarget.Y - targetY) * (previousTarget.Y - targetY);
                    var targetToLiveSq =
                        (liveX - targetX) * (liveX - targetX) +
                        (liveY - targetY) * (liveY - targetY);

                    if (targetToLiveSq >= targetToPrevSq)
                    {
                        sourceX = liveX;
                        sourceY = liveY;
                    }
                    else
                    {
                        sourceX = previousTarget.X;
                        sourceY = previousTarget.Y;
                    }
                }
                else
                {
                    sourceX = previousTarget.X;
                    sourceY = previousTarget.Y;
                }
            }
            else
            {
                try
                {
                    sourceX = GetWorldX(mob);
                    sourceY = GetWorldY(mob);
                }
                catch
                {
                    return false;
                }
            }

            return double.IsFinite(sourceX) && double.IsFinite(sourceY);
        }

        private static bool ShouldTreatMobMoveAsTeleport(
            double sourceX,
            double sourceY,
            double targetX,
            double targetY,
            string? animPayload)
        {
            if (!double.IsFinite(sourceX) || !double.IsFinite(sourceY) ||
                !double.IsFinite(targetX) || !double.IsFinite(targetY))
            {
                return false;
            }

            var dx = targetX - sourceX;
            var dy = targetY - sourceY;
            var absDx = System.Math.Abs(dx);
            var absDy = System.Math.Abs(dy);
            var distanceSq = dx * dx + dy * dy;

            if (IsTeleportLikeAnimPayload(animPayload))
            {
                return distanceSq >=
                       ClientTeleportAnimSnapDistancePx * ClientTeleportAnimSnapDistancePx;
            }

            // A large vertical delta by itself is usually a normal fall, drop attack, elevator
            // movement, or knock-back. Only classify an unlabelled discontinuity as a teleport when
            // it is strongly horizontal. This keeps gravity/collision vanilla and prevents the
            // client from being placed through a floor by a false teleport snap.
            if (absDx >= ClientTeleportHorizontalSnapDistancePx &&
                absDy <= ClientTeleportUnlabelledMaxVerticalDeltaPx &&
                absDx >= absDy * ClientTeleportHorizontalDominanceRatio)
            {
                return true;
            }

            // Magnitude backstop: an unlabelled jump this large in either axis cannot be ordinary
            // per-frame motion, so treat it as a teleport even without horizontal dominance. This
            // is what catches high-BC blink enemies that warp diagonally or straight up/down and
            // carry no teleport anim token. Flyers get the whole delta; gravity mobs keep their
            // safe-landing path (this only decides that a discontinuity occurred).
            return distanceSq >=
                   ClientTeleportUnlabelledMagnitudeSnapDistancePx * ClientTeleportUnlabelledMagnitudeSnapDistancePx;
        }

        private static bool IsTeleportLikeAnimPayload(string? animPayload)
        {
            if (string.IsNullOrWhiteSpace(animPayload))
                return false;

            return animPayload.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   animPayload.IndexOf("warp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   animPayload.IndexOf("blink", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsJumpLikeAnimPayload(string? animPayload)
        {
            if (string.IsNullOrWhiteSpace(animPayload))
                return false;

            return animPayload.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   animPayload.IndexOf("leap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   animPayload.IndexOf("pounce", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   animPayload.IndexOf("hop", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ApplyAuthoritativeAffectState(int mobSyncId, Mob mob, string? wirePayload)
        {
            if (mob == null || mob.destroyed)
                return;

            if (!TryDecodeStatePayloadFromWire(wirePayload, out var safePayload))
                return;

            // Affects run vanilla calls (setAffectS etc.) on the mob; never do that on a mob
            // culled locally on a client (same .cx hazard class as culled deaths/attacks).
            // Checked BEFORE the dedupe cache so the payload re-applies once the mob wakes.
            if (!IsHost(LobbySession.NetRef) && IsMobCulledLocally(mob))
                return;

            lock (Sync)
            {
                var sameMob = clientLastAppliedHostAffectMobBySyncId.TryGetValue(mobSyncId, out var lastMob) &&
                              ReferenceEquals(lastMob, mob);
                if (sameMob &&
                    clientLastAppliedHostAffectPayloadBySyncId.TryGetValue(mobSyncId, out var lastApplied) &&
                    string.Equals(lastApplied, safePayload, StringComparison.Ordinal))
                {
                    return;
                }

                clientLastAppliedHostAffectPayloadBySyncId[mobSyncId] = safePayload;
                clientLastAppliedHostAffectMobBySyncId[mobSyncId] = mob;
            }

            RunWithSuppressedClientAffectDirty(() =>
            {
                if (BossSyncHelpers.IsBossMob(mob))
                {
                    BossStateSync.ApplyBossStateFromPayload(mob, safePayload);
                    MarkClientBossPresentationLease(mob);
                    return;
                }

                ApplyExplicitAffectPayload(mob, safePayload);
                BossStateSync.ApplyBossStateFromPayload(mob, safePayload);
            });
        }

        private static void RunWithSuppressedClientAffectDirty(Action action)
        {
            if (action == null)
                return;

            suppressClientAffectDirtyDepth++;
            try
            {
                action();
            }
            finally
            {
                suppressClientAffectDirtyDepth--;
            }
        }

        private static void ApplyExplicitAffectPayload(Mob mob, string payload)
        {
            if (mob == null || mob.destroyed)
                return;

            var desired = ParseAffectStatePayload(payload);
            PruneMissingSyncedAffects(mob, desired);

            foreach (var affectId in desired)
                ApplySyncedAffectPresence(mob, affectId);
        }

        private static void PruneMissingSyncedAffects(Mob mob, HashSet<int> desired)
        {
            if (mob == null || mob.destroyed)
                return;

            List<int>? staleAffectIds = null;
            try
            {
                var affects = mob.getAllAffects();
                if (affects == null || affects.length <= 0)
                    return;

                for (int i = 0; i < affects.length; i++)
                {
                    if (desired.Contains(i))
                        continue;
                    if (TryGetDynLength(affects.getDyn(i)) <= 0)
                        continue;

                    staleAffectIds ??= new List<int>();
                    staleAffectIds.Add(i);
                }
            }
            catch
            {
                return;
            }

            if (staleAffectIds == null)
                return;

            for (int i = 0; i < staleAffectIds.Count; i++)
            {
                try
                {
                    mob.removeAllAffects(staleAffectIds[i]);
                }
                catch
                {
                }
            }
        }

        private static void ApplySyncedAffectPresence(Mob mob, int affectId)
        {
            if (mob == null || mob.destroyed || affectId < 0)
                return;

            var hadAffect = false;

            try
            {
                hadAffect = mob.hasAffect(affectId);
            }
            catch
            {
            }

            if (!hadAffect)
            {
                try
                {
                    mob.setAffectS(affectId, AuthoritativeAffectPresenceSeconds, HaxeProxy.Runtime.Ref<double>.Null, null);
                }
                catch
                {
                }
            }
        }

        private static HashSet<int> ParseAffectStatePayload(string? payload)
        {
            var affects = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(payload))
                return affects;

            var decoded = payload!;
            try { decoded = Uri.UnescapeDataString(decoded); } catch { }
            if (string.IsNullOrWhiteSpace(decoded))
                return affects;

            var parts = decoded.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var token = parts[i]?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                var idPart = token;
                var separator = token.IndexOf(':');
                if (separator > 0)
                    idPart = token[..separator];

                if (!int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    continue;
                if (id < 0)
                    continue;

                affects.Add(id);
            }

            return affects;
        }

        private static int TryGetDynLength(object? dynArray)
        {
            if (dynArray is not ArrayObj ao)
                return 0;

            try
            {
                return ao.length;
            }
            catch
            {
                return 0;
            }
        }

        private static void ConsumeIncomingHostMobAttacks(NetNode net)
        {
            if (!net.TryConsumeMobAttacks(out var attacks))
                return;

            try
            {
                MobSyncTrace.LogRecvAttacks("hostAttacksFromHost", attacks);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingHostMobAttacks(attacks);
            }
            finally
            {
                NetNode.ReleaseConsumedList(attacks);
            }
        }

        private static void ApplyIncomingHostMobAttacks(IReadOnlyList<NetNode.MobAttack> attacks)
        {
            if (attacks == null || attacks.Count == 0)
                return;

            var rejectedGeneration = 0;
            var rejectedCount = 0;
            for (int i = 0; i < attacks.Count; i++)
            {
                var attack = attacks[i];
                Mob? mob = null;
                lock (Sync)
                {
                    if (!ShouldAcceptPacketGenerationLocked(attack.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;
                    mob = ResolveTrackedMobForIncomingAttackLocked(attack);
                    // Phase 3: deterministically drop a replayed / out-of-order boss attack whose
                    // sequence was already applied for this stable identity. Pure check only — the
                    // high-water mark is advanced *after* the attack is actually queued below.
                    if (mob != null && IsReplayedBossAttackLocked(mob, attack.AttackSeq))
                        continue;
                }

                if (mob == null)
                {
                    QueuePendingClientBossAttack(attack);
                    continue;
                }

                // Native boss attacks are not atomic: their projectiles, phase transitions, landing
                // motion, cleanup and dialogue often run for many later updates.  The client queue
                // guard still prevents the replica from choosing unrelated attacks, while the
                // presentation lease below lets the host-selected skill finish its vanilla lifecycle.
                TryQueueClientMobAttack(mob, attack.SkillId, attack.RequiresTargetInArea, attack.Data, attack.TargetUserId, attack.Dir);
                // Phase 3 fix: only now that the attack was dispatched to the client attack
                // processor is the sequence considered applied. An attack that was buffered (mob
                // unresolved) never reaches here, so a legitimate retry is not blocked.
                lock (Sync)
                {
                    MarkBossAttackAppliedLocked(mob, attack.AttackSeq);
                }
            }

            LogRejectedPacketGeneration("hostAttackOnClient", rejectedCount, rejectedGeneration);
        }

        private static void QueuePendingClientBossAttack(NetNode.MobAttack attack)
        {
            lock (Sync)
            {
                var expectedType = attack.Type;
                if (string.IsNullOrWhiteSpace(expectedType))
                    hostMobTypeBySyncId.TryGetValue(attack.Index, out expectedType);
                if (!BossSyncHelpers.IsBossTypeSignature(expectedType))
                    return;

                if (clientPendingBossAttacks.Count >= ClientPendingBossAttackLimit)
                    clientPendingBossAttacks.RemoveAt(0);

                clientPendingBossAttacks.Add(new PendingClientBossAttack(
                    attack,
                    GetCurrentFrame(null) + ClientPendingBossAttackRetryFrames));
            }
        }

        private static void RetryPendingClientBossAttacks()
        {
            s_resolvedClientBossAttacksScratch.Clear();
            var frame = GetCurrentFrame(null);

            lock (Sync)
            {
                for (var i = clientPendingBossAttacks.Count - 1; i >= 0; i--)
                {
                    var pending = clientPendingBossAttacks[i];
                    if (pending.Attack.Generation != s_levelIdentityToken ||
                        frame > pending.ExpiresAtFrame)
                    {
                        clientPendingBossAttacks.RemoveAt(i);
                        continue;
                    }

                    var mob = ResolveTrackedMobForIncomingAttackLocked(pending.Attack);
                    if (mob == null)
                        continue;

                    clientPendingBossAttacks.RemoveAt(i);
                    // Phase 3: a fresher packet may already have applied this (or a newer) sequence
                    // while it was buffered; drop the stale replay instead of double-firing. Pure
                    // check — the mark is advanced only after the attack is queued below.
                    if (IsReplayedBossAttackLocked(mob, pending.Attack.AttackSeq))
                        continue;
                    s_resolvedClientBossAttacksScratch.Add(new ResolvedClientBossAttack(mob, pending.Attack));
                }
            }

            // Reverse the scratch list because pending entries were removed newest-to-oldest.
            for (var i = s_resolvedClientBossAttacksScratch.Count - 1; i >= 0; i--)
            {
                var resolved = s_resolvedClientBossAttacksScratch[i];
                TryQueueClientMobAttack(
                    resolved.Mob,
                    resolved.Attack.SkillId,
                    resolved.Attack.RequiresTargetInArea,
                    resolved.Attack.Data,
                    resolved.Attack.TargetUserId,
                    resolved.Attack.Dir);
                // Phase 3 fix: sequence is applied only after the buffered attack was dispatched.
                lock (Sync)
                {
                    MarkBossAttackAppliedLocked(resolved.Mob, resolved.Attack.AttackSeq);
                }
            }

            s_resolvedClientBossAttacksScratch.Clear();
        }

        private static void ConsumeIncomingMobDraws(NetNode net)
        {
            if (!net.TryConsumeMobDraws(out var draws))
                return;

            try
            {
                MobSyncTrace.LogRecvDraws("clientDrawsFromClient", draws);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingMobDraws(draws);
            }
            finally
            {
                NetNode.ReleaseConsumedList(draws);
            }
        }

        private static void ApplyIncomingMobDraws(IReadOnlyList<NetNode.MobDraw> draws)
        {
            if (draws == null || draws.Count == 0)
                return;

            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < draws.Count; i++)
                {
                    var draw = draws[i];
                    if (!ShouldAcceptPacketGenerationLocked(draw.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    var mob = ResolveTrackedMobBySyncIdLocked(draw.MobIndex);
                    if (mob == null)
                        continue;

                    if (!IsSyncMob(mob))
                        continue;

                    TryApplyHostDrawRequestLocked(mob, draw);
                }
            }

            LogRejectedPacketGeneration("clientDrawOnHost", rejectedCount, rejectedGeneration);
        }

        private static void TryApplyHostDrawRequestLocked(Mob mob, NetNode.MobDraw draw)
        {
            if (mob == null)
                return;

            if (TryGetMobSyncId(mob, out var drawSyncId) && drawSyncId >= 0)
            {
                SetHostClientInterestLocked(drawSyncId, draw.UserId, !draw.IsOutOfGame);
                if (!draw.IsOutOfGame)
                {
                    EnqueueHostMobDirtyLocked(drawSyncId, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                    if (draw.IsOnScreen)
                        TryWakeMobForForcedSimulation(mob);
                }
            }
        }

        private static void TryQueueClientMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, int targetUserId, int attackDir)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            var intent = new ClientMobAttackIntent(skillId, requiresTargetInArea, data, targetUserId, attackDir);
            if (BossSyncHelpers.IsBossMob(mob))
            {
                MarkClientBossSkillCallbackLease(mob);
                // Force boss entity sync when attack starts to ensure projectiles/tentacles are in sync
                if (TryGetMobSyncId(mob, out var mobSyncId))
                    EnqueueHostMobDirtyLocked(mobSyncId, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
            }
            WithClientNetworkAttackReplayContext(mob, () => ProcessClientMobAttackIntent(mob, intent));
        }

        private static void ProcessClientMobAttackIntent(Mob mob, ClientMobAttackIntent intent)
        {
            if (mob == null || string.IsNullOrWhiteSpace(intent.SkillId))
                return;

            var skillId = intent.SkillId;
            var traceRoute = ResolveClientAttackRouteForTrace(skillId);
            _ = TryGetMobSyncId(mob, out var traceSyncId);

            // Never replay remote mob attacks on mobs that are culled locally: contact/skill
            // replays run vanilla combat code on a never-initialized mob (same hazard class as
            // the culled-death .cx fatal). A locally-culled mob is far from the local hero, so
            // the replay is off-screen and its target is out of reach here anyway; position/life
            // still sync via state snapshots.
            if (!IsHost(LobbySession.NetRef) && IsMobCulledLocally(mob))
            {
                MobSyncTrace.LogClientAttackRoute("skipped_culled_" + traceRoute, traceSyncId, skillId);
                return;
            }

            // Never replay teleport-class skills (e.g. Mage360 aggrTeleport). Their vanilla
            // implementation defers post-arrival logic that reads the target's grid coords
            // (.cx) frames after prepare() returns - outside any try/catch we can place - and
            // on replayed copies the target reference is not guaranteed across the wind-up.
            // Confirmed via trace: route=oldSkillPrepare skillId=@oldprep:aggrTeleport logged
            // immediately before the Null access .cx fatal. The teleport's position change
            // still arrives via move snapshots; only the local wind-up VFX is dropped.
            if (skillId.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MobSyncTrace.LogClientAttackRoute("skipped_teleport_" + traceRoute, traceSyncId, skillId);
                return;
            }

            MobSyncTrace.LogClientAttackRoute(traceRoute, traceSyncId, skillId);

            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
            {
                ProcessClientContactAttack(mob, intent);
                return;
            }

            if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillExecute(mob, skillId[OldSkillExecutePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(OldSkillPreparePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillPrepare(mob, skillId[OldSkillPreparePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(OldSkillChargeCompletePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillExecute(mob, skillId[OldSkillChargeCompletePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientNewSkillExecute(mob, skillId[NewSkillExecutePacketPrefix.Length..], intent);
                return;
            }

            ProcessClientOldSkillQueue(mob, intent);
        }

        private static string ResolveClientAttackRouteForTrace(string skillId)
        {
            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
                return "contact";

            if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal))
                return "oldSkillExecute";

            if (skillId.StartsWith(OldSkillPreparePacketPrefix, StringComparison.Ordinal))
                return "oldSkillPrepare";

            if (skillId.StartsWith(OldSkillChargeCompletePacketPrefix, StringComparison.Ordinal))
                return "oldSkillChargeComplete";

            if (skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
                return "newSkillExecute";

            return "oldSkillQueue";
        }

        private static void ProcessClientContactAttack(Mob mob, ClientMobAttackIntent intent)
        {
            TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
            TryWakeMobForForcedSimulation(mob);

            var target = ResolveClientAttackTargetEntity(mob, intent.TargetUserId);
            if (target == null)
                target = ResolveClientAttackTargetEntity(mob, 0);
            if (target == null)
                return;

            RegisterClientNetworkAttackExecuted(mob);

            try
            {
                mob.contactAttack(target);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client contactAttack failed for mob");
            }
        }

        private static void ProcessClientOldSkillExecute(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            if (ShouldSkipClientOldSkillExecuteFromMarker(mob, normalizedSkillId))
                return;

            // Context validation: skill bodies read level state directly (the Giant hand's
            // shootGrid reads level .wid), and executing one against a detached or mid-transition
            // mob hard-throws inside native code. A Hashlink exception unwinding out of a
            // half-executed skill leaves VM state inconsistent - prime suspect for the stackless
            // 0xCFFFFFFF exits. Never replay into that state.
            if (mob == null || mob.destroyed || mob._level == null ||
                (currentLevel != null && !ReferenceEquals(mob._level, currentLevel)))
                return;

            var replayKey = BuildMobStateTypeSignature(mob) + "|" + normalizedSkillId;
            lock (Sync)
            {
                // Self-healing blocklist: a (boss type, skill) pair that ever threw is never
                // replayed again this session. The anim payload still shows the attack visually;
                // only the crashing native re-execution stops.
                if (s_poisonedOldSkillReplays.Contains(replayKey))
                    return;
            }

            try
            {
                var skillId = normalizedSkillId.AsHaxeString();
                if (!mob.hasOldSkill(skillId))
                    return;

                var oldSkill = mob.getOldSkill(skillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                if (TryGetChargingOldSkillId(mob, out var chargingOldSkillId))
                {
                    if (!string.Equals(chargingOldSkillId, normalizedSkillId, StringComparison.Ordinal))
                        return;
                }

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                    return;

                if (!TryGetChargingOldSkillId(mob, out _))
                {
                    if (oldSkill is OldMobSkill oldMobSkill && TryExecuteClientOldSkillNativeLike(oldMobSkill, intent.Data))
                    { }
                    else
                    {
                        oldSkill.prepare(intent.Data);
                    }
                }

                TryInvokeOldSkillChargeComplete(oldSkill);
                oldSkill.execute(null);
                lock (Sync)
                {
                    clientQueuedOldSkillMarkers.Remove(mob);
                }
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    s_poisonedOldSkillReplays.Add(replayKey);
                }
                Log.Warning(ex, "[MobsSync] Client oldSkill execute failed (replay poisoned for this session): {SkillId}", normalizedSkillId);
            }
        }

        private static readonly HashSet<string> s_poisonedOldSkillReplays = new(StringComparer.Ordinal);

        private static void ProcessClientOldSkillPrepare(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            try
            {
                if (!TryGetMobOldSkill(mob, normalizedSkillId, out var oldSkill))
                    return;

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                    return;

                if (oldSkill is OldMobSkill oldMobSkill && TryExecuteClientOldSkillNativeLike(oldMobSkill, intent.Data))
                    return;

                if (!oldSkill.prepare(intent.Data))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client oldSkill prepare failed: {SkillId}", normalizedSkillId);
            }
        }

        private static void ProcessClientNewSkillExecute(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            try
            {
                if (TryGetChargingNewSkillId(mob, out var chargingNewSkillId))
                {
                    if (!string.Equals(chargingNewSkillId, normalizedSkillId, StringComparison.Ordinal))
                        return;

                    var chargingSkill = mob.getChargingNewSkill() as MobSkill;
                    if (chargingSkill == null)
                        return;

                    RegisterClientNetworkAttackExecuted(mob);
                    TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                    TryWakeMobForForcedSimulation(mob);
                    if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                        return;
                    chargingSkill.execute(null);
                    return;
                }

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);

                if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                    return;

                var skillId = normalizedSkillId.AsHaxeString();
                var skill = mob.getSkill(skillId) as MobSkill;
                if (skill == null)
                    return;

                skill.prepare(intent.Data);
                skill.execute(null);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client newSkill execute failed: {SkillId}", normalizedSkillId);
            }
        }

        private static void ProcessClientOldSkillQueue(Mob mob, ClientMobAttackIntent intent)
        {
            try
            {
                if (IsQueuedOrChargingOldSkillId(mob, intent.SkillId))
                    return;

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                    return;

                var haxeSkillId = intent.SkillId.AsHaxeString();
                if (!mob.hasOldSkill(haxeSkillId))
                    return;

                var oldSkill = mob.getOldSkill(haxeSkillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                WithClientNetworkQueuedAttackContext(mob, () =>
                {
                    mob.queueAttack(oldSkill, intent.RequiresTargetInArea, intent.Data);
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client oldSkill queue failed: {SkillId}", intent.SkillId);
            }
        }

        private static void TryInvokeOldSkillChargeComplete(OldSkill oldSkill)
        {
            if (oldSkill == null)
                return;

            try
            {
                var cb = oldSkill.dynOnChargeComplete;
                if (cb != null)
                {
                    cb.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] OldSkill dynOnChargeComplete invoke failed");
            }
        }

        private static bool TryExecuteClientOldSkillNativeLike(OldMobSkill oldSkill, int? data)
        {
            if (oldSkill == null)
                return false;

            if (TryPrepareClientOldSkillOnOwnerTarget(oldSkill, null, data))
                return true;

            if (!data.HasValue && TryPrepareClientOldSkillOnOwnerTarget(oldSkill, null, null))
                return true;

            if (TryPrepareClientOldSkillOnOwnerTarget(oldSkill, true, data))
                return true;

            return !data.HasValue && TryPrepareClientOldSkillOnOwnerTarget(oldSkill, true, null);
        }

        private static bool TryPrepareClientOldSkillOnOwnerTarget(OldMobSkill oldSkill, bool? useTargetData, int? data)
        {
            try
            {
                return oldSkill.prepareOnOwnerTarget(useTargetData, data);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetMobOldSkill(Mob mob, string normalizedSkillId, out OldSkill oldSkill)
        {
            oldSkill = null!;
            if (mob == null || string.IsNullOrWhiteSpace(normalizedSkillId))
                return false;

            try
            {
                var skillId = normalizedSkillId.AsHaxeString();
                if (!mob.hasOldSkill(skillId))
                    return false;

                oldSkill = mob.getOldSkill(skillId) as OldSkill;
                return oldSkill != null;
            }
            catch
            {
                oldSkill = null!;
                return false;
            }
        }

        private static bool IsQueuedOrChargingOldSkillId(Mob mob, string expectedSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(expectedSkillId))
                return false;

            if (IsQueuedOldSkillId(mob, expectedSkillId))
                return true;

            if (TryGetChargingOldSkillId(mob, out var chargingOldSkillId) &&
                string.Equals(chargingOldSkillId, expectedSkillId, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool IsQueuedOldSkillId(Mob mob, string expectedSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(expectedSkillId))
                return false;

            if (!TryGetQueuedOldSkillId(mob, out var queuedOldSkillId))
                return false;

            return string.Equals(queuedOldSkillId, expectedSkillId, StringComparison.Ordinal);
        }

        private static bool TryGetQueuedOldSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var queued = mob.queuedOldSkill;
                var queuedSkill = queued?.a;
                skillId = queuedSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static bool TryGetChargingOldSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var chargingOldSkill = mob.getChargingOldSkill() as OldSkill;
                skillId = chargingOldSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static bool TryGetChargingNewSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var chargingNewSkill = mob.getChargingNewSkill();
                skillId = chargingNewSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static void RegisterClientQueuedOldSkillMarker(Mob mob, string skillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            lock (Sync)
            {
                clientQueuedOldSkillMarkers[mob] = skillId;
            }
        }

        private static bool ShouldSkipClientOldSkillExecuteFromMarker(Mob mob, string incomingSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(incomingSkillId))
                return false;

            lock (Sync)
            {
                if (!clientQueuedOldSkillMarkers.TryGetValue(mob, out var markerSkillId))
                    return false;

                if (string.Equals(markerSkillId, incomingSkillId, StringComparison.Ordinal))
                {
                    clientQueuedOldSkillMarkers.Remove(mob);
                    // Marker is only meaningful if the mob is still actively queued/charging this skill
                    // (e.g. client-side behavior fired it and the host event would be a duplicate).
                    // If the skill is not queued/charging, the marker is stale from our own replay
                    // and the incoming host event is a fresh attack — do not skip.
                    if (IsQueuedOrChargingOldSkillId(mob, incomingSkillId))
                        return true;
                    return false;
                }

                if (!IsQueuedOrChargingOldSkillId(mob, markerSkillId))
                    clientQueuedOldSkillMarkers.Remove(mob);
            }

            return false;
        }

        private static bool TryGetCurrentClientAttackTarget(Mob mob, out Entity target)
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

        private static bool TryGetCurrentClientNemesisTarget(Mob mob, out Entity target)
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

        private static bool TryResolveClientExecuteTargetEntity(Mob mob, int targetUserId, out Entity target)
        {
            target = null!;
            if (mob == null || !IsMobHostileToPlayers(mob))
                return false;

            if (targetUserId > 0 && TryResolveClientDirectPlayerCombatTarget(mob, targetUserId, out target))
                return true;
            if (TryGetCurrentClientAttackTarget(mob, out target))
                return true;
            if (TryGetCurrentClientNemesisTarget(mob, out target))
                return true;

            var detected = ResolveDetectedClientTargetEntity(mob);
            if (detected != null)
            {
                target = detected;
                return true;
            }

            return false;
        }

        private static void TrySetClientMobAttackTarget(Mob mob, int targetUserId, int attackDir, bool forceRetarget = false)
        {
            Entity? target = null;

            lock (Sync)
            {
                if (!forceRetarget &&
                    clientCachedAttackTargetByMob.TryGetValue(mob, out var cached) &&
                    cached != null &&
                    IsPreservablePlayerCombatTargetForMob(mob, cached))
                {
                    target = cached;
                }
            }

            if (target == null)
            {
                target = ResolveClientAttackTargetEntity(mob, targetUserId);

                // Only fall back to "whoever is nearby" when the host named NO target. If the host
                // did name one and we cannot resolve it locally, substituting the local player
                // re-aims an attack that was swung at someone else. That is why dodging behind a
                // boss still connected: the host's boss struck forward at the other player, the
                // replica retargeted the local hero standing behind it, and vanilla attack
                // resolution hit them.
                if (target == null && targetUserId <= 0 && IsMobHostileToPlayers(mob))
                    target = ResolveDetectedClientTargetEntity(mob);

                if (target == null)
                {
                    // Keep the authoritative facing so the swing still looks right, but leave the
                    // replica's attack target alone.
                    try
                    {
                        var hostDir = NormalizeDir(attackDir);
                        if (hostDir != 0)
                            mob.dir = hostDir;
                    }
                    catch
                    {
                    }

                    return;
                }

                lock (Sync)
                {
                    clientCachedAttackTargetByMob[mob] = target;
                }
            }

            var normalizedAttackDir = NormalizeDir(attackDir);
            if (!forceRetarget)
            {
                try
                {
                    if (targetUserId <= 0)
                    {
                        if (mob.aTarget != null && IsPreservablePlayerCombatTargetForMob(mob, mob.aTarget))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }

                        if (mob.nemesisTarget != null && IsPreservablePlayerCombatTargetForMob(mob, mob.nemesisTarget))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }
                    }
                    else
                    {
                        if (ReferenceEquals(mob.aTarget, target) || ReferenceEquals(mob.nemesisTarget, target))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            TrySetMobAttackTargetsExact(mob, target, attackDir, forceAttackDir: true);
        }

        private static Entity? ResolveClientAttackTargetEntity(Mob mob, int targetUserId)
        {
            if (!IsMobHostileToPlayers(mob))
                return null;

            if (targetUserId > 0 && TryResolveClientDirectPlayerCombatTarget(mob, targetUserId, out var directTarget))
                return directTarget;
            if (TryGetCurrentClientAttackTarget(mob, out var attackTarget))
                return attackTarget;
            if (TryGetCurrentClientNemesisTarget(mob, out var nemesisTarget))
                return nemesisTarget;

            var detected = ResolveDetectedClientTargetEntity(mob);
            if (detected != null)
                return detected;

            return null;
        }

        private static bool TryResolveClientDirectPlayerCombatTarget(Mob mob, int targetUserId, out Entity target)
        {
            target = null!;
            if (mob == null || targetUserId <= 0 || !IsMobHostileToPlayers(mob))
                return false;

            var net = LobbySession.NetRef;
            var localId = net?.id ?? 0;
            if (localId <= 0)
                return false;

            if (targetUserId == localId)
            {
                var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                if (localHero != null && IsPreservablePlayerCombatTargetForMob(mob, localHero))
                {
                    target = localHero;
                    return true;
                }

                return false;
            }

            if (!ModEntry.TryGetClientIndex(localId, targetUserId, out var index))
                return false;

            var client = ModEntry.clients[index];
            if (client == null || !IsPreservablePlayerCombatTargetForMob(mob, client))
                return false;

            target = client;
            return true;
        }

        private static Entity? ResolveDetectedClientTargetEntity(Mob mob)
        {
            if (mob == null)
                return null;
            if (!IsMobHostileToPlayers(mob))
                return null;

            s_clientDetectedTargetsScratch.Clear();
            var hero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (hero != null)
                s_clientDetectedTargetsScratch.Add(hero);

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null)
                    s_clientDetectedTargetsScratch.Add(client);
            }

            Entity? best = null;
            var bestDistSq = double.MaxValue;
            var mx = GetWorldX(mob);
            var my = GetWorldY(mob);

            for (int i = 0; i < s_clientDetectedTargetsScratch.Count; i++)
            {
                var candidate = s_clientDetectedTargetsScratch[i];
                if (candidate == null || ReferenceEquals(candidate, mob))
                    continue;
                if (!IsAcquirablePlayerCombatTargetForMob(mob, candidate, requireDetectArea: true))
                    continue;

                var dx = GetWorldX(candidate) - mx;
                var dy = GetWorldY(candidate) - my;
                var distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = candidate;
                }
            }

            s_clientDetectedTargetsScratch.Clear();
            return best;
        }

        private static bool HasLocalQueuedOrChargingSkill(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                if (mob.queuedSkill != null)
                    return true;
            }
            catch
            {
            }

            try
            {
                if (mob.queuedOldSkill?.a != null)
                    return true;
            }
            catch
            {
            }

            return TryGetChargingOldSkillId(mob, out _) || TryGetChargingNewSkillId(mob, out _);
        }

        private static void RegisterClientNetworkAttackExecuted(Mob mob)
        {
            MarkClientNetworkAttackActive(mob);
        }

        private static void ConsumeIncomingMobHits(NetNode net)
        {
            s_mobHitMergeScratch.Clear();
            if (net.TryConsumeMobHits(out var incoming) && incoming != null && incoming.Count > 0)
            {
                try
                {
                    s_mobHitMergeScratch.AddRange(incoming);
                }
                finally
                {
                    NetNode.ReleaseConsumedList(incoming);
                }
            }

            if (s_mobHitMergeScratch.Count == 0)
                return;

            MobSyncTrace.LogRecvHits(net.IsHost ? "hitsOnHost" : "hitsOnClient", s_mobHitMergeScratch);

            if (IsSyncQuiescedForTransition())
            {
                s_mobHitMergeScratch.Clear();
                return;
            }

            ApplyIncomingMobHits(s_mobHitMergeScratch, 0, s_mobHitMergeScratch.Count, false);
        }

        private static void ConsumeIncomingMobDies(NetNode net)
        {
            if (!net.TryConsumeMobDies(out var dies))
                return;

            try
            {
                MobSyncTrace.LogRecvDies(net.IsHost ? "diesOnHost" : "diesOnClient", dies);

                // Host is authoritative for mob death. Ignore remote client die packets.
                if (net.IsHost)
                    return;

                if (IsSyncQuiescedForTransition())
                    return;

                ApplyIncomingMobDies(dies);
            }
            finally
            {
                NetNode.ReleaseConsumedList(dies);
            }
        }

        private static void ApplyIncomingMobDies(IReadOnlyList<NetNode.MobDie> dies)
        {
            if (dies == null || dies.Count == 0)
                return;

            s_dieVictimsScratch.Clear();
            s_dieVictimDedupScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < dies.Count; i++)
                {
                    var die = dies[i];
                    if (!ShouldAcceptPacketGenerationLocked(die.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    var mob = ResolveMobFromDieLocked(die);
                    if (mob == null)
                    {
                        // A dropped boss death is what strands the client boss at 0/1 HP with a
                        // locked camera and no rewards. Buffer boss-typed packets for retry and
                        // bounded escalation instead of losing them.
                        RememberUnresolvedBossDieLocked(die);
                        continue;
                    }

                    var isBoss = BossSyncHelpers.IsBossMob(mob);
                    if (isBoss && clientCompletedAuthoritativeBossDeaths.Contains(mob))
                        continue;
                    var life = 0;
                    try
                    {
                        life = mob.life;
                        if (mob.destroyed)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    // Hardening: do not ignore dead-but-not-destroyed mobs. This was a common
                    // source of client-side ghost elites: life already reached 0, HP bar disappeared,
                    // but onDie/despawn never ran locally. Let the authoritative MOBDIE packet finish cleanup.
                    if (!isBoss && life <= 0 && mob.destroyed)
                        continue;

                    if (s_dieVictimDedupScratch.Add(mob))
                        s_dieVictimsScratch.Add(mob);
                }
            }

            LogRejectedPacketGeneration("mobDie", rejectedCount, rejectedGeneration);

            s_dieVictimDedupScratch.Clear();

            for (int i = 0; i < s_dieVictimsScratch.Count; i++)
            {
                var mob = s_dieVictimsScratch[i];
                if (mob == null)
                    continue;

                // Non-boss (client): DEFER to the mob's own update cycle instead of re-running
                // onDie synchronously. The hardening that processed dead-but-not-destroyed mobs
                // here re-killed mobs that had already died cleanly (host MOBDIE arriving after a
                // local kill), corrupting their death state - the source of the level-transition
                // render fatal. The deferred flush skips mobs that finish destroying themselves
                // and only completes genuinely stuck ghosts.
                if (!IsHost(LobbySession.NetRef) && !BossSyncHelpers.IsBossMob(mob) && TryDeferCulledClientMobDeath(mob))
                    continue;

                TryWakeMobForForcedSimulation(mob);
                try
                {
                    RunWithAuthoritativeClientMobDie(mob, () =>
                    {
                        RunWithSuppressedMobDieSend(() =>
                        {
                            mob.life = 0;
                            mob.onDie();
                        });
                    });

                    if (BossSyncHelpers.IsBossMob(mob) && IsCompletedAuthoritativeBossDeath(mob))
                    {
                        lock (Sync)
                        {
                            clientCompletedAuthoritativeBossDeaths.Add(mob);
                            s_clientBossAuthoritativeZeroLifeFrame.Remove(mob);
                        }
                    }

                }
                catch
                {
                }

            }

            s_dieVictimsScratch.Clear();
        }

        private static bool IsCompletedAuthoritativeBossDeath(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                return mob.destroyed || mob.life <= 0;
            }
            catch
            {
                return true;
            }
        }

        private static void ApplyIncomingMobHits(IReadOnlyList<NetNode.MobHit> hits, bool reResolveMobBySyncIdOnApply)
        {
            if (hits == null || hits.Count == 0)
                return;
            ApplyIncomingMobHits(hits, 0, hits.Count, reResolveMobBySyncIdOnApply);
        }

        private static void ApplyIncomingMobHits(IReadOnlyList<NetNode.MobHit> hits, int start, int count, bool reResolveMobBySyncIdOnApply)
        {
            if (hits == null || count <= 0)
                return;

            var end = start + count;
            if (start < 0 || end > hits.Count)
                return;

            var net = LobbySession.NetRef;
            var isHost = IsHost(net);
            s_pendingMobHitAppliesScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;

            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = start; i < end; i++)
                {
                    var hit = hits[i];
                    if (!ShouldAcceptPacketGenerationLocked(hit.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    if (isHost && !IsKnownRemoteHitSenderOnHost(net, hit.UserId))
                        continue;

                    var mob = ResolveMobFromHitLocked(hit);
                    if (mob == null)
                    {
                        // This is where a client's damage is lost. The host already publishes an
                        // immediate reliable keyframe after every hit it DOES apply (below), so a
                        // client needing many times more hits than the host is this drop, not a
                        // missing HP broadcast. Count it so the rate is visible without per-packet spam.
                        MobSyncTrace.LogDamageDropped(isHost, hit.MobIndex, hit.UserId, hit.DamageHint);
                        continue;
                    }

                    if (!TryGetMobLifeAndMaxSafe(mob, out var prevLife, out var maxLife))
                        continue;

                    var targetLife = System.Math.Clamp(hit.Hp, 0, maxLife);
                    var isBoss = BossSyncHelpers.IsBossMob(mob);
                    // A remote player's hit is an INPUT/intent, never authoritative HP. Replaying the
                    // reported damage through the host's native hit path preserves elite invulnerability,
                    // phase transitions, onDamage hooks and final death logic. The absolute HP field is
                    // retained only as a compatibility fallback for packets that carry no damage hint.
                    var replaySpecialHit = isHost && hit.DamageHint > 0.0;
                    if (replaySpecialHit)
                        targetLife = prevLife;

                    // On a client, every hit in this queue came from the authoritative host, so its
                    // absolute result is applied even when it raises a speculative local value. On the
                    // host, an old/no-hint client packet can only lower HP and can never heal a mob.
                    if (isHost && !replaySpecialHit && targetLife >= prevLife)
                    {
                        replaySpecialHit = hit.DamageHint > 0.0 || ShouldReplayIncomingHitWithoutLifeDelta(mob);
                        if (!replaySpecialHit)
                            continue;

                        targetLife = prevLife;
                    }

                    var forceDie = targetLife <= 0 && prevLife > 0;
                    var syncId = -1;
                    TryGetMobSyncId(mob, out syncId);
                    MobSyncTrace.LogIncomingHitApply(syncId, hit.Hp, hit.UserId, replaySpecialHit, forceDie);
                    MobSyncTrace.LogDamageApplied(isHost, syncId, prevLife, targetLife, hit.DamageHint, replaySpecialHit);
                    s_pendingMobHitAppliesScratch.Add(new PendingMobHitApply(
                        mob,
                        hit.UserId,
                        prevLife,
                        targetLife,
                        maxLife,
                        forceDie,
                        syncId,
                        isBoss,
                        replaySpecialHit,
                        hit.DamageHint));
                }
            }

            LogRejectedPacketGeneration(isHost ? "mobHitOnHost" : "mobHitOnClient", rejectedCount, rejectedGeneration);

            FlushGhostDespawnEchoes(net, isHost);

            for (int i = 0; i < s_pendingMobHitAppliesScratch.Count; i++)
            {
                var update = s_pendingMobHitAppliesScratch[i];
                Mob? mob;
                if (reResolveMobBySyncIdOnApply && update.SyncId >= 0)
                {
                    lock (Sync)
                    {
                        mob = ResolveMobBySyncIdLocked(update.SyncId);
                    }
                }
                else
                {
                    mob = update.Mob;
                }

                if (mob == null)
                    continue;

                if (isHost)
                    TryWakeMobForForcedSimulation(mob);

                var appliedLife = update.TargetLife;
                if (update.ReplaySpecialHit)
                {
                    // Boss phase scripts remain the one conservative exception: replaying a
                    // reconstructed hit in the middle of a queued boss skill can strand that script.
                    // Normal mobs and elites always use native host damage, even while attacking, so
                    // their armor/invulnerability/elite callbacks stay vanilla-authoritative.
                    if (isHost && update.IsBoss && HasLocalQueuedOrChargingSkill(mob))
                    {
                        ApplyAuthoritativeLifeState(mob, update.TargetLife, update.TargetMaxLife);
                        appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                    }
                    else
                    {
                        TryWakeMobForForcedSimulation(mob);
                        TryReplayIncomingSpecialHitReaction(mob, update.DamageHint);
                        appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                    }
                }
                else if (update.ForceDie)
                {
                    TryWakeMobForForcedSimulation(mob);
                    if (isHost)
                    {
                        if (update.IsBoss)
                        {
                            TryApplyHostBossFinishingHit(mob, update.TargetMaxLife);
                        }
                        else
                        {
                            try
                            {
                                if (!mob.destroyed)
                                {
                                    mob.life = 0;
                                    mob.onDie();
                                }
                                else
                                {
                                    mob.life = 0;
                                }
                            }
                            catch
                            {
                            }
                        }

                        appliedLife = GetMobLifeOrFallback(mob, 0);
                    }
                    else
                    {
                        ApplyAuthoritativeLifeState(mob, 0, update.TargetMaxLife);
                        appliedLife = 0;
                    }
                }
                else
                {
                    ApplyAuthoritativeLifeState(mob, update.TargetLife, update.TargetMaxLife);
                    appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                }

                if (isHost)
                    TryApplyHostMobHitCombatRefresh(mob, update.SourceUserId, update.PreviousLife, appliedLife, update.ReplaySpecialHit);

                if (isHost)
                {
                    var mobStillPresent = false;
                    try { mobStillPresent = !mob.destroyed; } catch { }
                    if (mobStillPresent)
                    {
                        // Publish a reliable fully-typed keyframe immediately after any remote hit.
                        // Besides HP, this carries authoritative affects/elite phase metadata and
                        // repairs a stale binding without waiting for the periodic recovery pass.
                        QueueHostMobDirty(mob, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                    }
                }

                if (isHost && net != null && update.SyncId >= 0)
                {
                    var destroyed = false;
                    try { destroyed = mob.destroyed; } catch { }
                    // A lethal native replay already emitted the authoritative death/tombstone.
                    // Do not read coordinates from a disposed proxy just to send a redundant HP=0.
                    if (!destroyed && TryGetCurrentLevelIdentityToken(out var identityToken))
                    {
                        var sx = GetWorldX(mob);
                        var sy = GetWorldY(mob);
                        var dir = NormalizeDir(mob.dir);
                        var hitEv = $"hit|{appliedLife.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                        var mobType = BuildMobStateTypeSignature(mob);
                        var evUpdate = new NetNode.MobEventUpdate(update.SyncId, sx, sy, dir, SingleEvent(hitEv), mobType, identityToken);
                        MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(evUpdate));
                        net.SendMobEvents(SingleUpdate(evUpdate));
                    }
                }
            }

            s_pendingMobHitAppliesScratch.Clear();
        }

        private static void TryApplyHostBossFinishingHit(Mob mob, int targetMaxLife)
        {
            if (mob == null)
                return;

            var replayAttempted = false;
            try
            {
                var damage = System.Math.Max(1.0, targetMaxLife * 8.0);
                Hero? sourceHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                try
                {
                    if (sourceHero != null && sourceHero.destroyed)
                        sourceHero = null;
                }
                catch
                {
                    sourceHero = null;
                }

                var attackUtils = AttackUtils.Class;
                var createFromHeroAndHit = attackUtils?.createFromHeroAndHit;
                if (createFromHeroAndHit != null)
                {
                    replayAttempted = true;
                    _ = createFromHeroAndHit(sourceHero, damage, null, mob);
                    if (TryFinalizeHostMobDeath(mob))
                        return;

                    // A boss that is alive after one valid finishing hit may have entered its next
                    // phase or a temporary invulnerability window. Never immediately hit it again
                    // or force life to zero; that skipped multi-phase vanilla boss logic.
                    if (GetMobLifeOrFallback(mob, 1) > 0)
                        return;
                }

                var createFromHero = attackUtils?.createFromHero;
                var hit = attackUtils?.hit;
                if (!replayAttempted && createFromHero != null && hit != null)
                {
                    var attack = createFromHero(sourceHero, damage, null);
                    if (attack != null)
                    {
                        replayAttempted = true;
                        hit(attack, mob);
                        if (TryFinalizeHostMobDeath(mob))
                            return;

                        if (GetMobLifeOrFallback(mob, 1) > 0)
                            return;
                    }
                }

                if (replayAttempted || TryFinalizeHostMobDeath(mob))
                    return;

                // Last resort only when this game/proxy revision exposes no valid attack helper.
                mob.life = 0;
                TryFinalizeHostMobDeath(mob);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Host boss finishing hit replay failed");
            }
        }

        private static bool TryFinalizeHostMobDeath(Mob mob)
        {
            if (mob == null)
                return true;

            try
            {
                if (mob.destroyed)
                    return true;
            }
            catch
            {
            }

            var life = GetMobLifeOrFallback(mob, 1);
            if (life > 0)
                return false;

            try
            {
                mob.life = 0;
                mob.onDie();
            }
            catch
            {
            }

            try
            {
                return mob.destroyed || GetMobLifeOrFallback(mob, 1) <= 0;
            }
            catch
            {
                return true;
            }
        }

        private static bool ShouldReplayIncomingHitWithoutLifeDelta(Mob mob)
        {
            if (mob == null)
                return false;

            var typeId = GetMobTypeIdSafe(mob);
            if (string.Equals(typeId, "mushroom", StringComparison.OrdinalIgnoreCase))
                return true;

            var runtimeClass = GetMobRuntimeClassKeySafe(mob);
            return string.Equals(runtimeClass, "Mushroom", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when this mob is culled on THIS machine (far from the local hero, vanilla is not
        /// simulating it). Client-side, such mobs must not be pushed into vanilla simulation:
        /// their behavior state was never proximity-initialized, and forcing them awake makes vanilla
        /// update null-deref a few frames later (Null access .cx while fighting far from the
        /// host). Returns false on any read failure so callers fall back to existing behavior.
        /// </summary>
        private static bool IsMobCulledLocally(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                return mob.isOutOfGame && !mob.isOnScreen;
            }
            catch
            {
                return false;
            }
        }

        private static void TryReplayIncomingSpecialHitReaction(Mob mob, double damageHint = 1.0)
        {
            if (mob == null)
                return;

            // Cosmetic replay only: skip it on clients for mobs culled locally. Running vanilla
            // hit resolution on a sleeping, never-initialized mob is hazardous, and the reaction
            // is off-screen anyway. The authoritative life still arrives via state snapshots.
            if (!IsHost(LobbySession.NetRef) && IsMobCulledLocally(mob))
                return;

            try
            {
                RunWithSuppressedMobHitSend(() =>
                {
                    var safeDamage = double.IsFinite(damageHint) && damageHint > 0.0
                        ? System.Math.Clamp(damageHint, 1.0, System.Math.Max(1.0, GetMobLifeOrFallback(mob, 1) * 8.0))
                        : 1.0;
                    Hero? replaySourceHero = null;
                    if (IsHost(LobbySession.NetRef))
                    {
                        replaySourceHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                        try
                        {
                            if (replaySourceHero != null && replaySourceHero.destroyed)
                                replaySourceHero = null;
                        }
                        catch
                        {
                            replaySourceHero = null;
                        }
                    }

                    var attackUtils = AttackUtils.Class;
                    var createFromHeroAndHit = attackUtils?.createFromHeroAndHit;
                    if (createFromHeroAndHit != null)
                    {
                        _ = createFromHeroAndHit(replaySourceHero, safeDamage, null, mob);
                        return;
                    }

                    var createFromHero = attackUtils?.createFromHero;
                    var hit = attackUtils?.hit;
                    if (createFromHero == null || hit == null)
                        return;

                    var attack = createFromHero(replaySourceHero, safeDamage, null);
                    if (attack != null)
                        hit(attack, mob);
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Special incoming mob hit replay failed");
            }
        }

        private static void TryWakeMobForForcedSimulation(Mob mob)
        {
            if (mob == null)
                return;

            // The wake is required on the authoritative HOST (it must simulate mobs that remote
            // players are fighting). On clients it only served cosmetic hit/death replays; waking
            // a locally culled mob there runs vanilla behavior logic on uninitialized state and crashes.
            if (!IsHost(LobbySession.NetRef) && IsMobCulledLocally(mob))
                return;

            PromoteMobToSyncVisibleState(mob);
        }

        private static bool ShouldSendHostContactPacket(Mob mob, Entity? target)
        {
            if (mob == null)
                return false;

            var userId = ResolveHostTargetUserId(target ?? ResolveCurrentHostPlayerCombatTarget(mob), LobbySession.NetRef?.id ?? 0);
            if (userId <= 0)
                return false;

            lock (Sync)
            {
                if (hostLastSentContactTargetUserIdByMob.TryGetValue(mob, out var lastTargetUserId) &&
                    lastTargetUserId == userId)
                    return false;

                hostLastSentContactTargetUserIdByMob[mob] = userId;
                return true;
            }
        }

        private static Mob? ResolveMobFromHitLocked(NetNode.MobHit hit)
        {
            var registryMob = ResolveMobBySyncIdLocked(hit.MobIndex);
            var typeMatchesRegistry = MobHitRegistryTypeMatchesLocked(registryMob, hit);

            if (registryMob != null && typeMatchesRegistry)
            {
                if (!MobHitRegistryStillTrustworthyLocked(registryMob, hit))
                {
                    MobSyncTrace.LogIncomingMappingMismatch(
                        "hit",
                        hit.MobIndex,
                        hit.Type ?? string.Empty,
                        BuildMobStateTypeSignature(registryMob),
                        "position_mismatch");
                    // The host DOES own this identity, so the client is not hitting a ghost - the
                    // two simulations have drifted apart far enough that the position no longer
                    // corroborates. Dropping and saying nothing left the client hitting a mob that
                    // could never take damage. Force an authoritative keyframe so the client snaps
                    // back onto the host's position and the NEXT hit corroborates.
                    RequestAuthoritativeHitReconcileLocked(registryMob, hit.MobIndex, "position_mismatch");
                    return null;
                }

                return registryMob;
            }

            // A type-signature disagreement on a sync id the host still owns is NOT proof that the
            // client is hitting the wrong thing. The sync id is the authoritative identity; the type
            // string is only a bind hint, and it legitimately changes under the host's feet when an
            // elite transforms, a boss swaps in a phase proxy, or a mob is replaced in place. Vetoing
            // the damage there produced exactly the reported symptom: an enemy the client can hit
            // forever and never kill, with the host seeing full HP.
            //
            // Position is what actually disambiguates, so require that instead and let the host's
            // native damage path decide the rest.
            if (registryMob != null && MobHitRegistryStillTrustworthyLocked(registryMob, hit))
            {
                MobSyncTrace.LogFallbackMatchResolved(
                    "hit_type_mismatch_accepted_by_position",
                    hit.MobIndex,
                    hit.Type ?? string.Empty,
                    hit.X,
                    hit.Y,
                    candidateCount: 1,
                    rebound: false);
                // The client's bind hint is stale; a fresh authoritative state carries the new
                // signature so later packets stop mismatching.
                RequestAuthoritativeHitReconcileLocked(registryMob, hit.MobIndex, "type_mismatch_accepted");
                return registryMob;
            }

            var missReason = registryMob == null ? "missing_sync_id" : "type_mismatch";
            MobSyncTrace.LogIncomingMappingMismatch(
                "hit",
                hit.MobIndex,
                hit.Type ?? string.Empty,
                registryMob != null ? BuildMobStateTypeSignature(registryMob) : string.Empty,
                missReason);

            // Host-side recovery: if the sync-id dictionary was pruned but the actual mob is
            // still alive in the current level, rebind by type + nearby position. This keeps
            // client kills authoritative without reviving a host-only ghost mob later.
            if (registryMob == null &&
                TryResolveHostMissingHitMobLocked(hit, out var fallbackMob, out var candidateCount) &&
                fallbackMob != null)
            {
                TryRebindTrackedMobSyncIdLocked(fallbackMob, hit.MobIndex);
                MobSyncTrace.LogFallbackMatchResolved(
                    "hit_missing_sync_id",
                    hit.MobIndex,
                    hit.Type ?? string.Empty,
                    hit.X,
                    hit.Y,
                    candidateCount,
                    rebound: true);
                return fallbackMob;
            }

            // Only unresolved missing_sync_id feeds the ghost despawn echo: the host tracks NO mob
            // and cannot find a live local candidate, so the client's same-generation mob at this
            // syncId can only be stale. (type_mismatch means the host DOES have a mob there —
            // echoing a death then could kill a legitimate mob.)
            if (registryMob == null)
                RecordGhostHitMissLocked(hit);
            else
                RequestAuthoritativeHitReconcileLocked(registryMob, hit.MobIndex, "type_and_position_mismatch");

            return null;
        }

        /// <summary>
        /// Host side: a client reported damage against a sync id we own but could not corroborate.
        /// Republish that mob's authoritative state so the client's mapping (position, type
        /// signature, HP) is repaired and its next hit resolves.
        /// </summary>
        /// <remarks>
        /// This is the missing half of the ghost-despawn echo. That echo covers "the host has
        /// nothing at this id, kill the client's ghost"; this covers "the host DOES have something,
        /// but the client's picture of it is stale". Without it, an unresolvable hit was a pure
        /// dead end and the mismatch persisted for the lifetime of the mob, which is what turns a
        /// transient desync into a permanently unkillable enemy. Rate-limited per sync id so a
        /// client spamming hits on a genuinely wrong id cannot amplify into a packet storm.
        /// </remarks>
        private static void RequestAuthoritativeHitReconcileLocked(Mob? mob, int syncId, string reason)
        {
            if (mob == null || syncId < 0)
                return;
            if (!IsHost(LobbySession.NetRef))
                return;

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var minTicks = (long)(System.Diagnostics.Stopwatch.Frequency * HitReconcileMinIntervalSeconds);
            if (s_lastHitReconcileTicksBySyncId.TryGetValue(syncId, out var last) &&
                last != 0 &&
                now - last < minTicks)
            {
                return;
            }

            s_lastHitReconcileTicksBySyncId[syncId] = now;
            EnqueueHostMobDirtyLocked(syncId, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
            Log.Information(
                "[MobSync] host hit reconcile syncId={SyncId} reason={Reason}: republishing authoritative state",
                syncId,
                reason);
        }

        private static bool TryResolveHostMissingHitMobLocked(NetNode.MobHit hit, out Mob? uniqueMob, out int candidateCount)
        {
            uniqueMob = null;
            candidateCount = 0;

            if (!IsHost(LobbySession.NetRef))
                return false;
            if (hit.MobIndex < 0)
                return false;
            if (string.IsNullOrWhiteSpace(hit.Type))
                return false;
            if (!double.IsFinite(hit.X) || !double.IsFinite(hit.Y))
                return false;

            var level = currentLevel;
            var entities = level?.entities;
            if (entities == null || entities.length <= 0)
                return false;

            var maxDistanceSq = MobHitMissingSyncIdRebindDistancePx * MobHitMissingSyncIdRebindDistancePx;
            var bestDistanceSq = double.MaxValue;
            var secondBestDistanceSq = double.MaxValue;
            var bestCellExact = false;
            var secondBestCellExact = false;
            Mob? bestMob = null;
            QuantizeWorldPositionToCells(hit.X, hit.Y, out var hitCx, out var hitCy);

            for (int i = 0; i < entities.length; i++)
            {
                if (entities.getDyn(i) is not Mob mob)
                    continue;
                if (!IsStateRebindCandidateLocked(mob))
                    continue;
                if (!DoesMobMatchStateType(mob, hit.Type))
                    continue;

                // Do not steal a HEALTHY sync id bound to another living mob: that is a host
                // wrapper duplicate of this enemy, still alive under its own id. This fallback
                // only reclaims mobs that are alive locally but lost/unbound in the host registry —
                // including a mob whose reverse mapping points at an id with no (or a stale)
                // forward entry, which is orphaned garbage and safe to rebind.
                if (MobToId.TryGetValue(mob, out var existingSyncId) && existingSyncId != hit.MobIndex)
                {
                    if (IdToMob.TryGetValue(existingSyncId, out var forwardMob) &&
                        ReferenceEquals(forwardMob, mob))
                        continue;
                }

                double dx;
                double dy;
                try
                {
                    dx = GetWorldX(mob) - hit.X;
                    dy = GetWorldY(mob) - hit.Y;
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

                GetMobWorldCells(mob, out var mobCx, out var mobCy);
                var cellExact = mobCx == hitCx && mobCy == hitCy;

                candidateCount++;
                var prefer = bestMob == null ||
                             (cellExact && !bestCellExact) ||
                             (cellExact == bestCellExact && distanceSq < bestDistanceSq);
                if (prefer)
                {
                    if (bestMob != null)
                    {
                        secondBestDistanceSq = bestDistanceSq;
                        secondBestCellExact = bestCellExact;
                    }
                    bestDistanceSq = distanceSq;
                    bestCellExact = cellExact;
                    bestMob = mob;
                }
                else if (cellExact == bestCellExact && distanceSq < secondBestDistanceSq)
                {
                    secondBestDistanceSq = distanceSq;
                    secondBestCellExact = cellExact;
                }
            }

            if (bestMob == null || candidateCount <= 0)
                return false;

            if (candidateCount > 1 &&
                secondBestDistanceSq < double.MaxValue &&
                secondBestCellExact == bestCellExact &&
                System.Math.Sqrt(secondBestDistanceSq) - System.Math.Sqrt(bestDistanceSq) < MobFallbackMinimumScoreGap)
            {
                lock (Sync)
                {
                    var currentFrame = (int)GetCurrentFrame(null);
                    if (currentFrame - s_lastAmbiguousFallbackLogFrame >= AmbiguousFallbackLogCooldownFrames)
                    {
                        MobSyncTrace.LogAmbiguousMatchRejected(
                            "hit_missing_sync_id",
                            hit.MobIndex,
                            hit.Type ?? string.Empty,
                            hit.X,
                            hit.Y,
                            candidateCount);
                        s_lastAmbiguousFallbackLogFrame = currentFrame;
                    }
                }
                return false;
            }

            uniqueMob = bestMob;
            return true;
        }

        /// <summary>
        /// Called under <c>Sync</c>. Counts missing_sync_id hits per syncId; once the same syncId
        /// has missed at least <see cref="GhostHitMissMinCount"/> times spanning at least
        /// <see cref="GhostHitMissMinSeconds"/>, queues a life=0 state echo (rate-limited per
        /// syncId). Late hits on freshly killed mobs produce only 1-2 misses and never trigger.
        /// </summary>
        private static void RecordGhostHitMissLocked(NetNode.MobHit hit)
        {
            if (s_ghostHitMissGeneration != hit.Generation)
            {
                s_ghostHitMissBySyncId.Clear();
                s_ghostHitMissGeneration = hit.Generation;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!s_ghostHitMissBySyncId.TryGetValue(hit.MobIndex, out var record))
            {
                record = new GhostHitMissRecord { FirstMissTicks = now };
                s_ghostHitMissBySyncId[hit.MobIndex] = record;
            }

            record.Count++;
            if (record.Count < GhostHitMissMinCount)
                return;
            if (now - record.FirstMissTicks < (long)(System.Diagnostics.Stopwatch.Frequency * GhostHitMissMinSeconds))
                return;
            if (record.LastEchoTicks != 0 &&
                now - record.LastEchoTicks < (long)(System.Diagnostics.Stopwatch.Frequency * GhostHitEchoMinIntervalSeconds))
                return;

            record.LastEchoTicks = now;
            s_ghostDespawnEchoScratch.Add(new NetNode.MobStateSnapshot(
                hit.MobIndex,
                hit.X,
                hit.Y,
                0,
                0,
                0,
                string.Empty,
                hit.Type ?? string.Empty,
                string.Empty,
                hit.Generation));
        }

        /// <summary>
        /// Sends queued ghost despawn echoes. Host only; discards silently on client role.
        /// </summary>
        private static void FlushGhostDespawnEchoes(NetNode? net, bool isHost)
        {
            List<NetNode.MobStateSnapshot>? toSend = null;
            lock (Sync)
            {
                if (s_ghostDespawnEchoScratch.Count == 0)
                    return;

                if (!isHost || net == null || !net.IsAlive)
                {
                    s_ghostDespawnEchoScratch.Clear();
                    return;
                }

                toSend = new List<NetNode.MobStateSnapshot>(s_ghostDespawnEchoScratch);
                s_ghostDespawnEchoScratch.Clear();
            }

            for (int i = 0; i < toSend.Count; i++)
            {
                Log.Warning(
                    "[MobSync] ghost mob despawn echo syncId={SyncId} type={Type}",
                    toSend[i].Index,
                    toSend[i].Type);
            }

            try
            {
                net!.SendMobStates(toSend);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Ghost despawn echo send failed");
            }
        }

        private static bool MobHitRegistryTypeMatchesLocked(Mob? registryMob, NetNode.MobHit hit)
        {
            if (registryMob == null)
                return false;
            if (string.IsNullOrWhiteSpace(hit.Type))
                return true;
            return DoesMobMatchStateType(registryMob, hit.Type);
        }

        private static bool MobHitQuantizedPositionCloseEnoughLocked(Mob mob, NetNode.MobHit hit)
        {
            QuantizeWorldPositionToCells(hit.X, hit.Y, out var hx, out var hy);
            GetMobWorldCells(mob, out var mx, out var my);
            return mx == hx && my == hy;
        }

        private static bool MobHitQuantizedFallbackPositionMatchesLocked(Mob mob, NetNode.MobHit hit)
        {
            if (mob == null)
                return false;

            QuantizeWorldPositionToCells(hit.X, hit.Y, out var hx, out var hy);
            GetMobWorldCells(mob, out var mx, out var my);

            var grounded = true;
            try
            {
                grounded = mob.hasGravity;
            }
            catch
            {
            }

            return grounded ? mx == hx : (mx == hx && my == hy);
        }

        private static bool MobHitRegistryStillTrustworthyLocked(Mob mob, NetNode.MobHit hit)
        {
            if (mob == null)
                return false;

            if (MobHitQuantizedPositionCloseEnoughLocked(mob, hit) ||
                MobHitQuantizedFallbackPositionMatchesLocked(mob, hit))
                return true;

            // Old code required exact quantized coordinates for client damage reports. That caused
            // valid hits to be rejected as position_mismatch once the same mob drifted even slightly
            // between host/client. If the sync id and type already match, accept the hit within a
            // generous same-room distance and let the normal host mob-state packets correct the drift.
            try
            {
                var dx = GetWorldX(mob) - hit.X;
                var dy = GetWorldY(mob) - hit.Y;
                if (double.IsFinite(dx) && double.IsFinite(dy))
                    return (dx * dx + dy * dy) <= (MobHitTrustedSyncIdDistancePx * MobHitTrustedSyncIdDistancePx);
            }
            catch
            {
            }

            return false;
        }

        private static Mob? ResolveMobFromDieLocked(NetNode.MobDie die)
        {
            lock (Sync)
            {
                var mapped = ResolveMobBySyncIdLocked(die.MobIndex);
                if (mapped != null)
                {
                    if (string.IsNullOrWhiteSpace(die.Type) ||
                        DoesMobMatchStateType(mapped, die.Type) ||
                        (BossSyncHelpers.IsBossMob(mapped) && DoesBossMatchAuthoritativeType(mapped, die.Type)))
                    {
                        return mapped;
                    }

                    InvalidateTrackedSyncCacheLocked(die.MobIndex, "death_type_mismatch");
                    MobSyncTrace.LogIncomingMappingMismatch(
                        "death",
                        die.MobIndex,
                        die.Type,
                        BuildMobStateTypeSignature(mapped),
                        "type_mismatch");
                }

                // An untyped legacy MOBDIE packet must never let an
                // unrelated missing normal-mob death fall through and select the only boss.
                if (string.IsNullOrWhiteSpace(die.Type))
                    return null;

                // MOBEVENT deaths carry the host type signature.  This is the last-resort path for
                // a boss whose native phase rebuilt the local wrapper between its final HP state
                // and death packet.  Require one unique compatible boss; never guess among Boss
                // Rush encounters.
                Mob? uniqueBoss = null;
                var candidateCount = 0;
                for (var i = 0; i < trackedMobs.Count; i++)
                {
                    var candidate = trackedMobs[i];
                    if (candidate == null || !IsStateRebindCandidateLocked(candidate) ||
                        !BossSyncHelpers.IsBossMob(candidate) ||
                        !DoesBossMatchAuthoritativeType(candidate, die.Type))
                    {
                        continue;
                    }

                    candidateCount++;
                    uniqueBoss = candidate;
                    if (candidateCount > 1)
                        return null;
                }

                if (uniqueBoss == null)
                    return null;

                TryRebindTrackedMobSyncIdLocked(uniqueBoss, die.MobIndex);
                return uniqueBoss;
            }
        }

        private static Mob? ResolveMobBySyncIdLocked(int mobIndex)
        {
            var mob = ResolveTrackedMobBySyncIdLocked(mobIndex);
            if (mob == null || !IsSyncMob(mob))
                return null;

            try
            {
                if (mob.destroyed || mob._level == null)
                {
                    s_trackedMobValidationPending = true;
                    return null;
                }

                if (!DoesLevelMatchCurrentIdentityLocked(mob._level))
                {
                    s_trackedMobValidationPending = true;
                    return null;
                }
            }
            catch
            {
                s_trackedMobValidationPending = true;
                return null;
            }

            return mob;
        }

        private static bool IsKnownRemoteHitSenderOnHost(NetNode? net, int senderId)
        {
            if (!IsHost(net))
                return true;

            var localId = net?.id ?? 0;
            if (senderId <= 0 || senderId == localId)
                return false;

            if (!ModEntry.TryGetClientIndex(localId, senderId, out var index))
                return false;

            if (index < 0 || index >= ModEntry.clientIds.Length)
                return false;

            return ModEntry.clientIds[index] == senderId;
        }

        private static bool TryGetMobLifeAndMaxSafe(Mob mob, out int life, out int maxLife)
        {
            life = 0;
            maxLife = 1;
            if (mob == null)
                return false;

            try
            {
                life = mob.life;
                maxLife = System.Math.Max(1, mob.maxLife);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int GetMobLifeOrFallback(Mob mob, int fallback)
        {
            if (mob == null)
                return fallback;

            try
            {
                return mob.life < 0 ? 0 : mob.life;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Scale mob HP for multiplayer: +0.5 per player for regular mobs, +2 per player for bosses.</summary>
        private static void ScaleMobHpForMultiplayer(Mob mob)
        {
            BossHpScaling.ScaleForMultiplayer(mob);
        }
    }
}

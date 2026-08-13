using System;
using System.Collections.Generic;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using dc;
using dc.en;
using dc.hl.types;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        [Flags]
        private enum HostMobDirtyFlags
        {
            None = 0,
            Move = 1 << 0,
            State = 1 << 1,
            ForceState = 1 << 2
        }

        [Flags]
        private enum ClientMobDirtyFlags
        {
            None = 0,
            Draw = 1 << 0,
            Affect = 1 << 1,
            ForceDraw = 1 << 2,
            ForceAffect = 1 << 3
        }

        private readonly struct HostMobObservedState
        {
            public readonly double X;
            public readonly double Y;
            public readonly int Dir;
            public readonly int Life;
            public readonly int MaxLife;
            public readonly string AnimPayload;
            public readonly string MobType;
            public readonly string StatePayload;
            public readonly bool VisibleForSync;
            // The managed registration the state was observed from. Mob.type and the runtime class
            // are immutable for a mob instance, so while the same registration keeps the syncId we
            // reuse the previous MobType instead of rebuilding the signature every frame.
            public readonly Mob MobRef;

            public HostMobObservedState(
                double x,
                double y,
                int dir,
                int life,
                int maxLife,
                string animPayload,
                string mobType,
                string statePayload,
                bool visibleForSync,
                Mob mobRef)
            {
                X = x;
                Y = y;
                Dir = dir;
                Life = life;
                MaxLife = maxLife;
                AnimPayload = animPayload ?? string.Empty;
                MobType = mobType ?? string.Empty;
                StatePayload = statePayload ?? string.Empty;
                VisibleForSync = visibleForSync;
                MobRef = mobRef;
            }
        }

        private readonly struct ClientDrawObservedState
        {
            public readonly bool IsOutOfGame;
            public readonly bool IsOnScreen;

            public ClientDrawObservedState(bool isOutOfGame, bool isOnScreen)
            {
                IsOutOfGame = isOutOfGame;
                IsOnScreen = isOnScreen;
            }
        }

        private static readonly Dictionary<int, HostMobObservedState> hostObservedMobStatesBySyncId = new();
        private static readonly Dictionary<int, HostMobDirtyFlags> hostDirtyFlagsBySyncId = new();
        private static readonly Queue<int> hostDirtyMobQueue = new();
        private static readonly HashSet<int> hostDirtyQueuedSyncIds = new();
        private static readonly Dictionary<int, double> hostLastSendFrameBySyncId = new();
        private static readonly Dictionary<int, ClientDrawObservedState> clientObservedDrawStateBySyncId = new();
        private static readonly Dictionary<int, ClientMobDirtyFlags> clientDirtyFlagsBySyncId = new();
        private static readonly Queue<int> clientDirtyMobQueue = new();
        private static readonly HashSet<int> clientDirtyQueuedSyncIds = new();

        private static void ObserveHostMobForDirtyQueue(Mob mob)
        {
            if (mob == null || !IsSyncMob(mob))
                return;
            if (!TryGetMobSyncId(mob, out var syncId) || syncId <= 0)
                return;

            // Phase 16 relevance-before-observation gate. When no connected client has registered
            // interest in this mob (MOBDRAW IsOutOfGame=false), skip the snapshot build and dirty
            // detection entirely: O(1) lock + TryGetValue + Count, no allocations, no per-client
            // enumeration. Interest is re-established by TryApplyHostDrawRequestLocked, which
            // re-opens the set AND enqueues State|ForceState on the same frame, so the client still
            // receives a current authoritative state (the existing re-interest mechanism - no second
            // resync added). SetHostClientInterestLocked invalidates the observed/last-sent caches
            // when the last interested client leaves, so that ForceState rebuild is guaranteed fresh.
            //
            // Lifecycle and repair traffic deliberately bypasses this gate: MOBREG/MOBUNREG go
            // through the registry flush, MOBDIE goes through Hook_Mob_onDie/SendMobDie, and every
            // forced repair (boss phase, stall recovery, remote activation, rebind) enqueues dirty
            // directly via QueueHostMobDirty/EnqueueHostMobDirtyLocked. The periodic resync
            // scheduler (FlushHostPriorityResync) also iterates all tracked mobs unconditionally,
            // which is what bootstraps a freshly connecting client before it can send any MOBDRAW.
            if (!IsMobClientVisibleForSync(syncId))
                return;

            double x;
            double y;
            int dir;
            int life;
            int maxLife;
            try
            {
                x = GetWorldX(mob);
                y = GetWorldY(mob);
                dir = NormalizeDir(mob.dir);
                life = mob.life;
                maxLife = mob.maxLife;
            }
            catch
            {
                return;
            }

            var visibleForSync = IsMobOnScreenForSync(mob);

            // Skip heavy state-payload builds for idle off-screen mobs. Anim still rebuilds whenever
            // the mob is visible so in-place attack cycles keep syncing. PrisonCourtyard (~100
            // tracked) previously built BOTH payloads every postUpdate — main-thread stall, idle CPU.
            HostMobObservedState previous;
            var hasPrevious = false;
            lock (Sync)
            {
                hasPrevious = hostObservedMobStatesBySyncId.TryGetValue(syncId, out previous);
            }

            var needsAnim = visibleForSync;
            var needsStatePayload = true;
            if (hasPrevious)
            {
                var lifeChanged = life != previous.Life || maxLife != previous.MaxLife;
                var visibilityChanged = visibleForSync != previous.VisibleForSync;
                var moveChanged = visibleForSync && (
                    !previous.VisibleForSync ||
                    !IsApproximatelyEqual(previous.X, x, MobStatePositionEpsilon) ||
                    !IsApproximatelyEqual(previous.Y, y, MobStatePositionEpsilon) ||
                    previous.Dir != dir);

                // Off-screen and unchanged: reuse last state payload (keyframes still refresh).
                if (!visibleForSync && !lifeChanged && !visibilityChanged && !moveChanged)
                    needsStatePayload = false;
            }

            var animPayload = needsAnim ? BuildAnimPayload(mob) : string.Empty;
            // BuildMobStateTypeSignature allocates (type string + runtime-class FullName + joined
            // string) and runs per tracked host mob per frame. The previous signature can only be
            // stale when a different mob took over the syncId, so reuse it while the same managed
            // registration owns the slot and only rebuild for first observation / rebind.
            var mobType = hasPrevious &&
                          !string.IsNullOrEmpty(previous.MobType) &&
                          ReferenceEquals(previous.MobRef, mob)
                ? previous.MobType
                : BuildMobStateTypeSignature(mob);
            var statePayload = needsStatePayload
                ? BuildHostMobStatePayload(mob)
                : (hasPrevious ? previous.StatePayload : string.Empty);

            lock (Sync)
            {
                var flags = HostMobDirtyFlags.None;
                if (!hostObservedMobStatesBySyncId.TryGetValue(syncId, out previous))
                {
                    if (!needsStatePayload)
                        statePayload = BuildHostMobStatePayload(mob);
                    flags = HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState;
                }
                else
                {
                    if (life != previous.Life || maxLife != previous.MaxLife)
                        flags |= HostMobDirtyFlags.State;

                    if (!string.Equals(previous.MobType, mobType, StringComparison.Ordinal) ||
                        (needsStatePayload &&
                         !string.Equals(previous.StatePayload, statePayload, StringComparison.Ordinal)))
                        flags |= HostMobDirtyFlags.State;

                    if (visibleForSync)
                    {
                        var moveChanged =
                            !previous.VisibleForSync ||
                            !IsApproximatelyEqual(previous.X, x, MobStatePositionEpsilon) ||
                            !IsApproximatelyEqual(previous.Y, y, MobStatePositionEpsilon) ||
                            previous.Dir != dir ||
                            !string.Equals(previous.AnimPayload, animPayload, StringComparison.Ordinal);

                        if (moveChanged)
                            flags |= previous.VisibleForSync ? HostMobDirtyFlags.Move : HostMobDirtyFlags.ForceState;
                    }
                    else if (previous.VisibleForSync)
                    {
                        flags |= HostMobDirtyFlags.State;
                    }
                }

                hostObservedMobStatesBySyncId[syncId] = new HostMobObservedState(
                    x,
                    y,
                    dir,
                    life,
                    maxLife,
                    animPayload,
                    mobType,
                    statePayload,
                    visibleForSync,
                    mob);

                if (flags != HostMobDirtyFlags.None)
                    EnqueueHostMobDirtyLocked(syncId, flags);
            }
        }

        private static void ObserveClientMobForDirtyQueue(Mob mob)
        {
            if (mob == null || !IsSyncMob(mob))
                return;
            if (!TryGetMobSyncId(mob, out var syncId) || syncId <= 0)
                return;

            bool isOutOfGame;
            bool isOnScreen;
            try
            {
                isOutOfGame = mob.isOutOfGame;
                isOnScreen = mob.isOnScreen;
            }
            catch
            {
                return;
            }

            lock (Sync)
            {
                var current = new ClientDrawObservedState(isOutOfGame, isOnScreen);
                if (!clientObservedDrawStateBySyncId.TryGetValue(syncId, out var previous))
                {
                    clientObservedDrawStateBySyncId[syncId] = current;
                    EnqueueClientMobDirtyLocked(syncId, ClientMobDirtyFlags.Draw | ClientMobDirtyFlags.ForceDraw);
                    return;
                }

                clientObservedDrawStateBySyncId[syncId] = current;
                if (previous.IsOutOfGame != current.IsOutOfGame || previous.IsOnScreen != current.IsOnScreen)
                    EnqueueClientMobDirtyLocked(syncId, ClientMobDirtyFlags.Draw);
            }
        }

        private static void QueueInitialMobSync(Mob mob)
        {
            if (mob == null || !IsSyncMob(mob))
                return;

            var net = LobbySession.NetRef;
            if (IsHost(net))
            {
                QueueHostMobDirty(mob, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);

                // The MOBREG broadcast is a burst that only refills when the level identity token
                // changes, so once the post-rebuild bootstrap is spent the client is never told
                // about anything spawned afterwards — burrowing Worms, summons, malaise waves,
                // elite replacements. The host allocates a NetId, the client never learns it, and
                // every hit on that mob fails as missing_sync_id. Re-arm the broadcast here so a
                // runtime registration reaches the client.
                //
                // MOBREG is idempotent (the client skips ids it has already bound), so re-sending
                // the whole registry is safe; FlushHostMobRegistry's interval gate keeps a burst of
                // spawns from turning into a packet per mob.
                MarkHostMobRegistryDirtyForRuntimeSpawn();
                return;
            }

            if (IsClient(net))
                QueueClientMobDirty(mob, ClientMobDirtyFlags.Draw | ClientMobDirtyFlags.Affect | ClientMobDirtyFlags.ForceDraw | ClientMobDirtyFlags.ForceAffect);
        }

        private static void QueueHostMobDirty(Mob mob, HostMobDirtyFlags flags)
        {
            if (mob == null || flags == HostMobDirtyFlags.None)
                return;
            if (!TryGetMobSyncId(mob, out var syncId) || syncId <= 0)
                return;

            lock (Sync)
            {
                EnqueueHostMobDirtyLocked(syncId, flags);
            }
        }

        private static void QueueClientMobDirty(Mob mob, ClientMobDirtyFlags flags)
        {
            if (mob == null || flags == ClientMobDirtyFlags.None)
                return;
            if (!TryGetMobSyncId(mob, out var syncId) || syncId <= 0)
                return;

            lock (Sync)
            {
                EnqueueClientMobDirtyLocked(syncId, flags);
            }
        }

        private static void EnqueueHostMobDirtyLocked(int syncId, HostMobDirtyFlags flags)
        {
            if (syncId < 0 || flags == HostMobDirtyFlags.None)
                return;

            if (hostDirtyFlagsBySyncId.TryGetValue(syncId, out var existing))
                hostDirtyFlagsBySyncId[syncId] = existing | flags;
            else
                hostDirtyFlagsBySyncId[syncId] = flags;

            if (hostDirtyQueuedSyncIds.Add(syncId))
                hostDirtyMobQueue.Enqueue(syncId);
        }

        private static void EnqueueClientMobDirtyLocked(int syncId, ClientMobDirtyFlags flags)
        {
            if (syncId < 0 || flags == ClientMobDirtyFlags.None)
                return;

            if (clientDirtyFlagsBySyncId.TryGetValue(syncId, out var existing))
                clientDirtyFlagsBySyncId[syncId] = existing | flags;
            else
                clientDirtyFlagsBySyncId[syncId] = flags;

            if (clientDirtyQueuedSyncIds.Add(syncId))
                clientDirtyMobQueue.Enqueue(syncId);
        }

        private static bool TryDequeuePendingHostDirtyMob(out Mob? mob, out int syncId, out HostMobDirtyFlags flags)
        {
            while (true)
            {
                syncId = -1;
                flags = HostMobDirtyFlags.None;
                mob = null;

                lock (Sync)
                {
                    if (hostDirtyMobQueue.Count <= 0)
                        return false;

                    syncId = hostDirtyMobQueue.Dequeue();
                    hostDirtyQueuedSyncIds.Remove(syncId);
                    if (!hostDirtyFlagsBySyncId.TryGetValue(syncId, out flags))
                        continue;

                    hostDirtyFlagsBySyncId.Remove(syncId);
                }

                lock (Sync)
                {
                    mob = ResolveMobBySyncIdLocked(syncId);
                }

                if (mob != null)
                    return true;
            }
        }

        private static bool TryDequeuePendingClientDirtyMob(out Mob? mob, out int syncId, out ClientMobDirtyFlags flags)
        {
            while (true)
            {
                syncId = -1;
                flags = ClientMobDirtyFlags.None;
                mob = null;

                lock (Sync)
                {
                    if (clientDirtyMobQueue.Count <= 0)
                        return false;

                    syncId = clientDirtyMobQueue.Dequeue();
                    clientDirtyQueuedSyncIds.Remove(syncId);
                    if (!clientDirtyFlagsBySyncId.TryGetValue(syncId, out flags))
                        continue;

                    clientDirtyFlagsBySyncId.Remove(syncId);
                }

                lock (Sync)
                {
                    mob = ResolveMobBySyncIdLocked(syncId);
                }

                if (mob != null)
                    return true;
            }
        }


        private static void FlushHostDirtyMobQueue(NetNode net)
        {
            if (IsSyncQuiescedForTransition())
                return;

            if (!IsHost(net))
                return;

            s_batchSnapshotsScratch.Clear();
            s_moveSnapshotsScratch.Clear();
            var stateBytes = GetWireLineBaseBytes("MOBSTATE|");
            var moveBytes = GetWireLineBaseBytes("MOBMOVE|");

            // Process only the entries that existed when this frame's flush began. A move that is
            // not due yet is intentionally re-queued for a later frame. The old unbounded while
            // loop immediately dequeued that same re-queued id again, which could spin on the game
            // thread for mid-range/dormant mobs and make vanilla AI look frozen under real play.
            int entriesToProcess;
            lock (Sync)
                entriesToProcess = hostDirtyMobQueue.Count;

            for (var processed = 0; processed < entriesToProcess; processed++)
            {
                if (!TryDequeuePendingHostDirtyMob(out var mob, out var syncId, out var flags))
                    break;
                if (mob == null)
                    continue;
                if (!TryBuildHostDirtySnapshotForQueue(mob, syncId, flags, out var sendState, out var stateSnapshot, out var moveSnapshot))
                    continue;

                if (sendState)
                {
                    var entryBytes = EstimateMobStateWireBytes(stateSnapshot, s_batchSnapshotsScratch.Count);
                    if (s_batchSnapshotsScratch.Count > 0 && stateBytes + entryBytes > MobWirePacketByteBudget)
                    {
                        TrySendHostStatesBatchAsync(net, s_batchSnapshotsScratch);
                        s_batchSnapshotsScratch.Clear();
                        stateBytes = GetWireLineBaseBytes("MOBSTATE|");
                    }

                    RecordHostMobSendFrame(syncId);
                    s_batchSnapshotsScratch.Add(stateSnapshot);
                    stateBytes += entryBytes;
                    continue;
                }

                // Move-only update: apply per-tier rate limiting (skip if animation changed)
                if (string.IsNullOrEmpty(moveSnapshot.AnimPayload) && !IsHostMobMoveDue(mob, syncId))
                {
                    lock (Sync)
                    {
                        EnqueueHostMobDirtyLocked(syncId, flags);
                    }
                    continue;
                }

                var moveEntryBytes = EstimateMobMoveWireBytes(moveSnapshot, s_moveSnapshotsScratch.Count);
                if (s_moveSnapshotsScratch.Count > 0 && moveBytes + moveEntryBytes > MobWirePacketByteBudget)
                {
                    TrySendHostMovesBatchAsync(net, s_moveSnapshotsScratch);
                    s_moveSnapshotsScratch.Clear();
                    moveBytes = GetWireLineBaseBytes("MOBMOVE|");
                }

                RecordHostMobSendFrame(syncId);
                s_moveSnapshotsScratch.Add(moveSnapshot);
                moveBytes += moveEntryBytes;
            }

            if (s_batchSnapshotsScratch.Count > 0)
            {
                TrySendHostStatesBatchAsync(net, s_batchSnapshotsScratch);
                s_batchSnapshotsScratch.Clear();
            }

            if (s_moveSnapshotsScratch.Count > 0)
            {
                TrySendHostMovesBatchAsync(net, s_moveSnapshotsScratch);
                s_moveSnapshotsScratch.Clear();
            }
        }

        private static void TrySendHostStatesBatchAsync(NetNode net, List<NetNode.MobStateSnapshot> batch)
        {
            MobSyncTrace.LogSendStatesBatch("host", batch);
            try
            {
                // Test build: keep host authority packets on the game/network frame path instead
                // of scheduling background sends. It removes one source of stale old-level packets
                // racing with level disposal/rebuild while keeping the existing wire protocol.
                net.SendMobStates(batch);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobSync] host state batch send failed");
            }
        }

        private static void TrySendHostMovesBatchAsync(NetNode net, List<NetNode.MobMoveSnapshot> batch)
        {
            MobSyncTrace.LogSendMovesBatch("host", batch);
            try
            {
                net.SendMobMoves(batch);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobSync] host move batch send failed");
            }
        }

        private static bool IsHostMobMoveDue(Mob mob, int syncId)
        {
            var interval = GetHostMobMoveInterval(mob);
            if (interval <= 0)
                return true;

            lock (Sync)
            {
                if (hostLastSendFrameBySyncId.TryGetValue(syncId, out var lastFrame))
                {
                    var frame = GetCurrentFrame(mob);
                    return frame - lastFrame >= interval;
                }

                return true;
            }
        }

        private static void RecordHostMobSendFrame(int syncId)
        {
            lock (Sync)
            {
                hostLastSendFrameBySyncId[syncId] = GetCurrentFrame(null);
            }
        }

        private static double GetHostMobMoveInterval(Mob mob)
        {
            if (mob == null)
                return 1.0;

            var priority = GetHostMobSyncPriority(mob);
            return priority switch
            {
                HostMobSyncPriority.Active => 1.0,
                HostMobSyncPriority.MidRange => 3.0,
                HostMobSyncPriority.Dormant => 10.0,
                _ => 1.0
            };
        }

        private static bool TryBuildHostDirtySnapshotForQueue(
            Mob mob,
            int syncId,
            HostMobDirtyFlags flags,
            out bool sendStateSnapshot,
            out NetNode.MobStateSnapshot stateSnapshot,
            out NetNode.MobMoveSnapshot moveSnapshot)
        {
            sendStateSnapshot = true;
            stateSnapshot = default;
            moveSnapshot = default;
            if (mob == null || syncId < 0 || flags == HostMobDirtyFlags.None)
                return false;

            var forceState = (flags & HostMobDirtyFlags.ForceState) != 0;
            var wantsState = forceState || (flags & HostMobDirtyFlags.State) != 0;
            var wantsMove = (flags & HostMobDirtyFlags.Move) != 0;
            if (!wantsState && !wantsMove)
                return false;

            if (!wantsState && !IsMobOnScreenForSync(mob))
                return false;

            return TryBuildHostMobDeltaSnapshot(
                mob,
                syncId,
                forceFullState: forceState,
                out sendStateSnapshot,
                out stateSnapshot,
                out moveSnapshot);
        }


        /// <summary>
        /// Single priority resync scheduler: bosses first, then active/visible mobs, then budgeted
        /// catch-up for the rest. Replaces the old boss-2f / active-6f / full-30f triple flush.
        /// </summary>
        private static void FlushHostPriorityResync(NetNode net)
        {
            if (!IsHost(net) || IsSyncQuiescedForTransition())
                return;
            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return;

            var frame = GetCurrentFrame(null);
            var sendBoss = false;
            var sendActive = false;
            var sendCatchUp = false;
            lock (Sync)
            {
                if (s_lastHostBossReliableKeyframeToken != identityToken)
                {
                    s_lastHostBossReliableKeyframeToken = identityToken;
                    s_lastHostBossReliableKeyframeFrame = -99999.0;
                }

                if (s_lastHostActiveReliableKeyframeToken != identityToken)
                {
                    s_lastHostActiveReliableKeyframeToken = identityToken;
                    s_lastHostActiveReliableKeyframeFrame = -99999.0;
                }

                if (s_lastHostAuthoritativeFullResyncToken != identityToken)
                {
                    s_lastHostAuthoritativeFullResyncToken = identityToken;
                    s_lastHostAuthoritativeFullResyncFrame = -99999.0;
                    s_hostAuthoritativeBootstrapResyncsRemaining = trackedMobs.Count > 0
                        ? HostAuthoritativeBootstrapResyncCount
                        : 0;
                }

                if (frame - s_lastHostBossReliableKeyframeFrame >= HostBossReliableKeyframeIntervalFrames)
                {
                    s_lastHostBossReliableKeyframeFrame = frame;
                    sendBoss = true;
                }

                if (frame - s_lastHostActiveReliableKeyframeFrame >= HostActiveReliableKeyframeIntervalFrames)
                {
                    s_lastHostActiveReliableKeyframeFrame = frame;
                    sendActive = true;
                }

                var catchUpInterval = s_hostAuthoritativeBootstrapResyncsRemaining > 0
                    ? HostAuthoritativeBootstrapResyncIntervalFrames
                    : HostAuthoritativeFullResyncIntervalFrames;
                if (frame - s_lastHostAuthoritativeFullResyncFrame >= catchUpInterval)
                {
                    s_lastHostAuthoritativeFullResyncFrame = frame;
                    if (s_hostAuthoritativeBootstrapResyncsRemaining > 0)
                        s_hostAuthoritativeBootstrapResyncsRemaining--;
                    sendCatchUp = true;
                }

                if (!sendBoss && !sendActive && !sendCatchUp)
                    return;

                s_batchMobsScratch.Clear();
                for (var i = 0; i < trackedMobs.Count; i++)
                {
                    var mob = trackedMobs[i];
                    if (mob != null)
                        s_batchMobsScratch.Add(mob);
                }
            }

            if (s_batchMobsScratch.Count == 0)
                return;

            // Pass 1: bosses
            if (sendBoss)
                FlushHostPriorityResyncPass(net, preferBoss: true, preferActive: false, includeAll: false);

            // Pass 2: active/visible non-boss
            if (sendActive)
                FlushHostPriorityResyncPass(net, preferBoss: false, preferActive: true, includeAll: false);

            // Pass 3: budgeted catch-up for remaining tracked mobs
            if (sendCatchUp)
                FlushHostPriorityResyncPass(net, preferBoss: false, preferActive: false, includeAll: true);

            s_batchMobsScratch.Clear();
        }

        private static void FlushHostPriorityResyncPass(
            NetNode net,
            bool preferBoss,
            bool preferActive,
            bool includeAll)
        {
            s_batchSnapshotsScratch.Clear();
            var stateBytes = GetWireLineBaseBytes("MOBSTATE|");
            var sent = 0;

            for (var i = 0; i < s_batchMobsScratch.Count; i++)
            {
                var mob = s_batchMobsScratch[i];
                if (mob == null)
                    continue;

                var isBoss = BossSyncHelpers.IsBossMob(mob);
                if (preferBoss && !isBoss)
                    continue;
                if (preferActive)
                {
                    if (isBoss)
                        continue;
                    if (GetHostMobSyncPriority(mob) != HostMobSyncPriority.Active)
                        continue;
                }
                else if (!preferBoss && !includeAll)
                {
                    continue;
                }
                else if (includeAll && !preferBoss && !preferActive)
                {
                    // Catch-up pass includes everyone not already covered this flush by dirty queue.
                }

                if (!TryGetMobSyncId(mob, out var syncId) || syncId < 0)
                    continue;
                if (!TryBuildHostMobDeltaSnapshot(
                        mob,
                        syncId,
                        forceFullState: true,
                        out var sendState,
                        out var stateSnapshot,
                        out _,
                        priorityHint: GetHostMobSyncPriority(mob)) || !sendState)
                {
                    continue;
                }

                var entryBytes = EstimateMobStateWireBytes(stateSnapshot, s_batchSnapshotsScratch.Count);
                if (s_batchSnapshotsScratch.Count > 0 && stateBytes + entryBytes > MobWirePacketByteBudget)
                {
                    TrySendHostStatesBatchAsync(net, s_batchSnapshotsScratch);
                    s_batchSnapshotsScratch.Clear();
                    stateBytes = GetWireLineBaseBytes("MOBSTATE|");
                }

                if (includeAll && sent >= HostPriorityResyncCatchUpBudgetPerFlush)
                    break;

                RecordHostMobSendFrame(syncId);
                s_batchSnapshotsScratch.Add(stateSnapshot);
                stateBytes += entryBytes;
                sent++;
            }

            if (s_batchSnapshotsScratch.Count > 0)
            {
                TrySendHostStatesBatchAsync(net, s_batchSnapshotsScratch);
                s_batchSnapshotsScratch.Clear();
            }
        }

        private static void FlushClientDirtyMobQueue(NetNode net)
        {
            if (IsSyncQuiescedForTransition())
                return;

            if (!IsClient(net))
                return;

            s_drawsScratch.Clear();
            s_batchSnapshotsScratch.Clear();
            var drawBytes = GetWireLineBaseBytes("MOBDRAW|");
            var stateBytes = GetWireLineBaseBytes("MOBSTATE|");
            while (TryDequeuePendingClientDirtyMob(out var mob, out var syncId, out var flags))
            {
                if (mob == null)
                    continue;

                if ((flags & (ClientMobDirtyFlags.Draw | ClientMobDirtyFlags.ForceDraw)) != 0 &&
                    TryBuildClientDrawUpdate(net, mob, syncId, flags, out var draw))
                {
                    var drawEntryBytes = EstimateMobDrawWireBytes(draw, s_drawsScratch.Count);
                    if (s_drawsScratch.Count > 0 && drawBytes + drawEntryBytes > MobWirePacketByteBudget)
                    {
                        MobSyncTrace.LogSendDrawBatch("client", s_drawsScratch);
                        net.SendMobDrawBatch(s_drawsScratch);
                        s_drawsScratch.Clear();
                        drawBytes = GetWireLineBaseBytes("MOBDRAW|");
                    }

                    s_drawsScratch.Add(draw);
                    drawBytes += drawEntryBytes;
                }

                if ((flags & (ClientMobDirtyFlags.Affect | ClientMobDirtyFlags.ForceAffect)) != 0 &&
                    TryBuildClientAffectStateUpdate(mob, syncId, flags, out var affectSnapshot))
                {
                    var affectEntryBytes = EstimateMobStateWireBytes(affectSnapshot, s_batchSnapshotsScratch.Count);
                    if (s_batchSnapshotsScratch.Count > 0 && stateBytes + affectEntryBytes > MobWirePacketByteBudget)
                    {
                        MobSyncTrace.LogSendStatesBatch("client", s_batchSnapshotsScratch);
                        net.SendMobStates(s_batchSnapshotsScratch);
                        s_batchSnapshotsScratch.Clear();
                        stateBytes = GetWireLineBaseBytes("MOBSTATE|");
                    }

                    s_batchSnapshotsScratch.Add(affectSnapshot);
                    stateBytes += affectEntryBytes;
                }
            }

            if (s_drawsScratch.Count > 0)
            {
                MobSyncTrace.LogSendDrawBatch("client", s_drawsScratch);
                net.SendMobDrawBatch(s_drawsScratch);
                s_drawsScratch.Clear();
            }

            if (s_batchSnapshotsScratch.Count > 0)
            {
                MobSyncTrace.LogSendStatesBatch("client", s_batchSnapshotsScratch);
                net.SendMobStates(s_batchSnapshotsScratch);
                s_batchSnapshotsScratch.Clear();
            }
        }

        private static bool TryBuildClientDrawUpdate(
            NetNode net,
            Mob mob,
            int syncId,
            ClientMobDirtyFlags flags,
            out NetNode.MobDraw draw)
        {
            draw = default;
            if (mob == null || syncId < 0 || net == null || net.id <= 0)
                return false;
            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return false;

            bool isOutOfGame;
            bool isOnScreen;
            try
            {
                isOutOfGame = mob.isOutOfGame;
                isOnScreen = mob.isOnScreen;
            }
            catch
            {
                return false;
            }

            var forceDraw = (flags & ClientMobDirtyFlags.ForceDraw) != 0;
            lock (Sync)
            {
                if (!forceDraw &&
                    clientLastSentDrawStateBySyncId.TryGetValue(syncId, out var lastDraw) &&
                    lastDraw.IsOutOfGame == isOutOfGame &&
                    lastDraw.IsOnScreen == isOnScreen)
                {
                    return false;
                }

                clientLastSentDrawStateBySyncId[syncId] = new ClientDrawSentState(isOutOfGame, isOnScreen);
            }

            draw = new NetNode.MobDraw(net.id, syncId, isOutOfGame, isOnScreen, identityToken);
            return true;
        }

        private static bool TryBuildClientAffectStateUpdate(
            Mob mob,
            int syncId,
            ClientMobDirtyFlags flags,
            out NetNode.MobStateSnapshot snapshot)
        {
            snapshot = default;
            if (mob == null || syncId < 0)
                return false;
            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return false;

            var payload = BuildMobAffectPresencePayload(mob);
            var forceAffect = (flags & ClientMobDirtyFlags.ForceAffect) != 0;
            lock (Sync)
            {
                if (!forceAffect &&
                    clientLastSentAffectPayloadBySyncId.TryGetValue(syncId, out var lastPayload) &&
                    string.Equals(lastPayload, payload, StringComparison.Ordinal))
                {
                    return false;
                }

                clientLastSentAffectPayloadBySyncId[syncId] = payload;
            }

            snapshot = new NetNode.MobStateSnapshot(
                syncId,
                0.0,
                0.0,
                0,
                0,
                0,
                string.Empty,
                string.Empty,
                EncodeStatePayloadForWire(payload),
                identityToken);
            return true;
        }

        private void Hook_Entity_setAffectS_MobSync(
            Hook_Entity.orig_setAffectS orig,
            Entity self,
            int id,
            double sec,
            Ref<double> ignoreResist,
            bool? allowResist)
        {
            orig(self, id, sec, ignoreResist, allowResist);
            TryMarkMobAffectDirty(self);
        }

        private void Hook_Entity_addTimeToAffect_MobSync(
            Hook_Entity.orig_addTimeToAffect orig,
            Entity self,
            virtual_a_t_uniqId_val_ affect,
            double frames)
        {
            orig(self, affect, frames);
            TryMarkMobAffectDirty(self);
        }

        private void Hook_Entity_removeAffects_MobSync(
            Hook_Entity.orig_removeAffects orig,
            Entity self,
            virtual_a_t_uniqId_val_ list)
        {
            orig(self, list);
            TryMarkMobAffectDirty(self);
        }

        private void Hook_Entity_removeAllAffects_MobSync(
            Hook_Entity.orig_removeAllAffects orig,
            Entity self,
            int list)
        {
            orig(self, list);
            TryMarkMobAffectDirty(self);
        }

        private static void TryMarkMobAffectDirty(Entity? entity)
        {
            if (entity is not Mob mob || !IsSyncMob(mob))
                return;

            var net = LobbySession.NetRef;
            if (IsHost(net))
            {
                QueueHostMobDirty(mob, HostMobDirtyFlags.State);
                return;
            }

            if (IsClient(net))
            {
                if (System.Threading.Volatile.Read(ref suppressClientAffectDirtyDepth) > 0)
                    return;

                QueueClientMobDirty(mob, ClientMobDirtyFlags.Affect);
            }
        }

        private static void ClearQueuedDirtyStateLocked()
        {
            hostObservedMobStatesBySyncId.Clear();
            s_hostBossPartWatch.Clear();
            hostDirtyFlagsBySyncId.Clear();
            hostDirtyQueuedSyncIds.Clear();
            hostLastSendFrameBySyncId.Clear();
            while (hostDirtyMobQueue.Count > 0)
                hostDirtyMobQueue.Dequeue();

            clientObservedDrawStateBySyncId.Clear();
            clientDirtyFlagsBySyncId.Clear();
            clientDirtyQueuedSyncIds.Clear();
            while (clientDirtyMobQueue.Count > 0)
                clientDirtyMobQueue.Dequeue();
        }

        private static int GetWireLineBaseBytes(string prefix)
        {
            return prefix.Length + 1;
        }

        private static int EstimateMobStateWireBytes(NetNode.MobStateSnapshot snapshot, int currentCount)
        {
            return (currentCount > 0 ? 1 : 0) +
                   GetInvariantWireLength(snapshot.Index) + 1 +
                   GetInvariantWireLength(snapshot.X) + 1 +
                   GetInvariantWireLength(snapshot.Y) + 1 +
                   GetInvariantWireLength(snapshot.Dir) + 1 +
                   GetInvariantWireLength(snapshot.Life) + 1 +
                   GetInvariantWireLength(snapshot.MaxLife) + 1 +
                   GetInvariantWireLength(snapshot.Generation) + 1 +
                   GetUtf8WireLength(snapshot.AnimPayload) + 1 +
                   GetUtf8WireLength(snapshot.Type) + 1 +
                   GetUtf8WireLength(snapshot.StatePayload);
        }

        private static int EstimateMobMoveWireBytes(NetNode.MobMoveSnapshot snapshot, int currentCount)
        {
            return (currentCount > 0 ? 1 : 0) +
                   GetInvariantWireLength(snapshot.Index) + 1 +
                   GetInvariantWireLength(snapshot.X) + 1 +
                   GetInvariantWireLength(snapshot.Y) + 1 +
                   GetInvariantWireLength(snapshot.Dir) + 1 +
                   GetInvariantWireLength(snapshot.Generation) + 1 +
                   GetUtf8WireLength(snapshot.AnimPayload);
        }

        private static int EstimateMobDrawWireBytes(NetNode.MobDraw draw, int currentCount)
        {
            return (currentCount > 0 ? 1 : 0) +
                   GetInvariantWireLength(draw.UserId) + 1 +
                   GetInvariantWireLength(draw.MobIndex) + 1 +
                   1 + 1 +
                   1 +
                   GetInvariantWireLength(draw.Generation);
        }

        private static int GetUtf8WireLength(string? value)
        {
            return string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
        }

        private static int GetInvariantWireLength(int value)
        {
            Span<byte> buffer = stackalloc byte[16];
            return Utf8Formatter.TryFormat(value, buffer, out var written) ? written : value.ToString(CultureInfo.InvariantCulture).Length;
        }

        private static int GetInvariantWireLength(double value)
        {
            Span<byte> buffer = stackalloc byte[32];
            return Utf8Formatter.TryFormat(value, buffer, out var written) ? written : value.ToString(CultureInfo.InvariantCulture).Length;
        }
    }
}

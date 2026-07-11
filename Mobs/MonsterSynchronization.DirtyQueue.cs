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

            public HostMobObservedState(
                double x,
                double y,
                int dir,
                int life,
                int maxLife,
                string animPayload,
                string mobType,
                string statePayload,
                bool visibleForSync)
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
            if (!TryGetMobSyncId(mob, out var syncId) || syncId < 0)
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
            var animPayload = visibleForSync ? BuildAnimPayload(mob) : string.Empty;
            var mobType = BuildMobStateTypeSignature(mob);
            var statePayload = BuildHostMobStatePayload(mob);

            lock (Sync)
            {
                var flags = HostMobDirtyFlags.None;
                if (!hostObservedMobStatesBySyncId.TryGetValue(syncId, out var previous))
                {
                    flags = HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState;
                }
                else
                {
                    if (life != previous.Life || maxLife != previous.MaxLife)
                        flags |= HostMobDirtyFlags.State;

                    if (!string.Equals(previous.MobType, mobType, StringComparison.Ordinal) ||
                        !string.Equals(previous.StatePayload, statePayload, StringComparison.Ordinal))
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
                    visibleForSync);

                if (flags != HostMobDirtyFlags.None)
                    EnqueueHostMobDirtyLocked(syncId, flags);
            }
        }

        private static void ObserveClientMobForDirtyQueue(Mob mob)
        {
            if (mob == null || !IsSyncMob(mob))
                return;
            if (!TryGetMobSyncId(mob, out var syncId) || syncId < 0)
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

            var net = GameMenu.NetRef;
            if (IsHost(net))
            {
                QueueHostMobDirty(mob, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                return;
            }

            if (IsClient(net))
                QueueClientMobDirty(mob, ClientMobDirtyFlags.Draw | ClientMobDirtyFlags.Affect | ClientMobDirtyFlags.ForceDraw | ClientMobDirtyFlags.ForceAffect);
        }

        private static void QueueHostMobDirty(Mob mob, HostMobDirtyFlags flags)
        {
            if (mob == null || flags == HostMobDirtyFlags.None)
                return;
            if (!TryGetMobSyncId(mob, out var syncId) || syncId < 0)
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
            if (!TryGetMobSyncId(mob, out var syncId) || syncId < 0)
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

                mob = ResolveMobBySyncIdLocked(syncId);
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

                mob = ResolveMobBySyncIdLocked(syncId);
                if (mob != null)
                    return true;
            }
        }

        private static int s_pendingHostBatchCount;

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
            while (TryDequeuePendingHostDirtyMob(out var mob, out var syncId, out var flags))
            {
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
            if (Volatile.Read(ref s_pendingHostBatchCount) >= 2)
                return;

            var copy = new List<NetNode.MobStateSnapshot>(batch);
            Interlocked.Increment(ref s_pendingHostBatchCount);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { net.SendMobStates(copy); }
                finally { Interlocked.Decrement(ref s_pendingHostBatchCount); }
            });
        }

        private static void TrySendHostMovesBatchAsync(NetNode net, List<NetNode.MobMoveSnapshot> batch)
        {
            MobSyncTrace.LogSendMovesBatch("host", batch);
            if (Volatile.Read(ref s_pendingHostBatchCount) >= 2)
                return;

            var copy = new List<NetNode.MobMoveSnapshot>(batch);
            Interlocked.Increment(ref s_pendingHostBatchCount);
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { net.SendMobMoves(copy); }
                finally { Interlocked.Decrement(ref s_pendingHostBatchCount); }
            });
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
                HostMobSyncPriority.Active => 2.0,
                HostMobSyncPriority.MidRange => 4.0,
                HostMobSyncPriority.Dormant => 12.0,
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

            var net = GameMenu.NetRef;
            if (IsHost(net))
            {
                QueueHostMobDirty(mob, HostMobDirtyFlags.State);
                return;
            }

            if (IsClient(net))
                QueueClientMobDirty(mob, ClientMobDirtyFlags.Affect);
        }

        private static void ClearQueuedDirtyStateLocked()
        {
            hostObservedMobStatesBySyncId.Clear();
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

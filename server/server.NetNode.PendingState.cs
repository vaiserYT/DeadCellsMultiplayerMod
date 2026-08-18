using System;
using System.Collections.Generic;
using DeadCellsMultiplayerMod.AdvancedCoop;
using DeadCellsMultiplayerMod.Interaction;

public sealed partial class NetNode
{
    private sealed class PendingQueue<T>
    {
        private readonly int _maxCount;
        private List<T> _items = new();
        internal long DroppedCount { get; private set; }

        internal PendingQueue(int maxCount)
        {
            _maxCount = Math.Max(1, maxCount);
        }

        internal void Clear() => _items.Clear();

        internal int RemoveAll(Predicate<T> match) => _items.RemoveAll(match);

        internal void AddBounded(T item, int maxCount)
        {
            if (_items.Count >= _maxCount)
            {
                DroppedCount += _items.Count - _maxCount + 1;
                _items.RemoveRange(0, _items.Count - _maxCount + 1);
            }
            _items.Add(item);
        }

        internal void AppendBounded(IReadOnlyList<T> items, int maxCount)
        {
            if (items == null || items.Count == 0)
                return;

            var firstIncoming = Math.Max(0, items.Count - _maxCount);
            var incomingCount = items.Count - firstIncoming;
            var overflow = _items.Count + incomingCount - _maxCount;
            if (overflow > 0)
            {
                DroppedCount += overflow;
                if (overflow >= _items.Count)
                    _items.Clear();
                else
                    _items.RemoveRange(0, overflow);
            }

            for (var i = firstIncoming; i < items.Count; i++)
                _items.Add(items[i]);
        }

        internal bool TryConsume(out List<T> snapshot)
        {
            if (_items.Count == 0)
            {
                snapshot = EmptyListCache<T>.Instance;
                return false;
            }

            snapshot = _items;
            _items = RentConsumedList<T>(snapshot.Count);
            return true;
        }
    }

    private sealed class LatestValueQueue<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCount;
        private List<TValue> _items = new();
        private readonly Dictionary<TKey, int> _slots = new();
        internal long DroppedCount { get; private set; }

        internal LatestValueQueue(int maxCount)
        {
            _maxCount = Math.Max(1, maxCount);
        }

        internal void Clear()
        {
            _items.Clear();
            _slots.Clear();
        }

        internal void Upsert(TKey key, TValue value)
        {
            if (_slots.TryGetValue(key, out var slot))
            {
                _items[slot] = value;
                return;
            }

            if (_items.Count >= _maxCount)
            {
                DroppedCount += _items.Count;
                _items.Clear();
                _slots.Clear();
            }

            _slots[key] = _items.Count;
            _items.Add(value);
        }

        internal bool TryConsume(out List<TValue> snapshot)
        {
            if (_items.Count == 0)
            {
                snapshot = EmptyListCache<TValue>.Instance;
                return false;
            }

            snapshot = _items;
            _items = RentConsumedList<TValue>(snapshot.Count);
            _slots.Clear();
            return true;
        }
    }

    private readonly PendingQueue<RemoteAttack> _pendingAttacks = new(PendingAttackLimit);
    private readonly PendingQueue<MobStateSnapshot> _pendingMobStates = new(PendingMobStateLimit);
    private readonly LatestValueQueue<(int Generation, int SyncId), MobMoveSnapshot> _pendingMobMoves = new(PendingMobMoveLimit);
    private readonly PendingQueue<MobHit> _pendingMobHits = new(PendingMobHitLimit);
    private readonly PendingQueue<MobDie> _pendingMobDies = new(PendingMobDieLimit);
    private readonly PendingQueue<MobAttack> _pendingMobAttacks = new(PendingMobAttackLimit);
    private readonly PendingQueue<MobDraw> _pendingMobDraws = new(PendingMobDrawLimit);
    private readonly PendingQueue<MobRegistryEntry> _pendingMobRegistry = new(PendingMobStateLimit);
    private readonly PendingQueue<ExitReadyState> _pendingExitReadyStates = new(PendingControlStateLimit);
    private readonly PendingQueue<ExitTransitionCommit> _pendingExitTransitionCommits = new(PendingControlStateLimit);
    private HostSpawnAnchor? _latestHostSpawnAnchor;
    private readonly PendingQueue<PlayerDownState> _pendingPlayerDownStates = new(PendingControlStateLimit);
    private readonly PendingQueue<PlayerReviveRequest> _pendingPlayerReviveRequests = new(PendingControlStateLimit);
    private readonly PendingQueue<string> _pendingBossCineLevelIds = new(PendingBossCineLimit);
    private readonly PendingQueue<BossHeroTeleportEvent> _pendingBossHeroTeleports = new(PendingInteractionLimit);
    private readonly PendingQueue<InterDoorEvent> _pendingInterDoorEvents = new(PendingInteractionLimit);
    private readonly PendingQueue<InterElevatorEvent> _pendingInterElevatorEvents = new(PendingInteractionLimit);
    private readonly PendingQueue<InterElevatorStateEvent> _pendingInterElevatorStateEvents = new(PendingInteractionLimit);
    private readonly PendingQueue<InterPressurePlateEvent> _pendingInterPressurePlateEvents = new(PendingInteractionLimit);
    private readonly PendingQueue<InterTreasureChestEvent> _pendingInterTreasureChestEvents = new(PendingInteractionLimit);
    private readonly PendingQueue<InterVineLadderEvent> _pendingInterVineLadderEvents = new(PendingInteractionLimit);
    private readonly PendingQueue<InterTeleportEvent> _pendingInterTeleportEvents = new(PendingInteractionLimit);
    private readonly PendingQueue<InterBreakableGroundEvent> _pendingInterBreakableGroundEvents = new(PendingInteractionLimit);
    private readonly PendingQueue<InterBossRuneUpdateCellsEvent> _pendingBossRuneUpdateCells = new(PendingInteractionLimit);
    private readonly PendingQueue<InterPortalEvent> _pendingInterPortalEvents = new(PendingInteractionLimit);
    private int _primaryRemoteId;
}

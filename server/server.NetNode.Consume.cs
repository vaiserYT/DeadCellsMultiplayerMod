using System.Collections.Concurrent;
using DeadCellsMultiplayerMod.Interaction;

public sealed partial class NetNode
{
    private static class EmptyListCache<T>
    {
        internal static readonly List<T> Instance = new(0);
    }

    private static class ConsumedListPool<T>
    {
        internal static readonly ConcurrentBag<List<T>> Pool = new();
    }

    private const int RetainedConsumedListCapacity = 1024;

    private static List<T> RentConsumedList<T>(int minCapacity = 0)
    {
        if (!ConsumedListPool<T>.Pool.TryTake(out var list))
            return minCapacity > 0 ? new List<T>(minCapacity) : new List<T>();

        if (list.Capacity < minCapacity)
            list.Capacity = minCapacity;

        return list;
    }

    public static void ReleaseConsumedList<T>(List<T>? list)
    {
        if (list == null || ReferenceEquals(list, EmptyListCache<T>.Instance))
            return;

        list.Clear();
        if (list.Capacity > RetainedConsumedListCapacity)
            list = new List<T>(RetainedConsumedListCapacity);

        ConsumedListPool<T>.Pool.Add(list);
    }

    public bool TryGetRemote(out int remoteId, out double rx, out double ry)
    {
        lock (_sync)
        {
            if (_primaryRemoteId != 0 && _remotes.TryGetValue(_primaryRemoteId, out var state) && state.HasRemote && state.HasPosition)
            {
                remoteId = state.Id;
                rx = state.X;
                ry = state.Y;
                return true;
            }
            remoteId = 0;
            rx = 0;
            ry = 0;
            return false;
        }
    }

    public bool TryGetRemotePosition(int userId, out double x, out double y, out string? levelId)
    {
        lock (_sync)
        {
            if (userId > 0 && _remotes.TryGetValue(userId, out var state) && state.HasRemote && state.HasPosition)
            {
                x = state.X;
                y = state.Y;
                levelId = state.LevelId;
                return true;
            }

            x = 0;
            y = 0;
            levelId = null;
            return false;
        }
    }

    public bool TryConsumeRemoteSnapshot(out List<RemoteSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_remotes.Count == 0)
            {
                snapshot = EmptyListCache<RemoteSnapshot>.Instance;
                return false;
            }

            snapshot = RentConsumedList<RemoteSnapshot>(_remotes.Count);
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote || !state.HasPosition)
                    continue;

                var hasAnim = state.HasAnim;
                var hasHeadAnim = state.HasHeadAnim;
                var hasRoom = state.HasRoom;
                var anim = hasAnim ? state.Anim : null;
                var animQueue = hasAnim ? state.AnimQueue : null;
                var animG = hasAnim ? state.AnimG : null;
                var headAnim = hasHeadAnim ? state.HeadAnim : null;
                var roomLevelId = hasRoom ? state.RoomLevelId : null;
                var roomId = hasRoom ? state.RoomId : null;

                snapshot.Add(new RemoteSnapshot(
                    state.Id,
                    state.X,
                    state.Y,
                    state.Dir,
                    state.LevelId,
                    roomLevelId,
                    roomId,
                    hasRoom,
                    anim,
                    animQueue,
                    animG,
                    hasAnim,
                    state.Username,
                    headAnim,
                    hasHeadAnim));

                if (hasAnim)
                    state.HasAnim = false;
                if (hasHeadAnim)
                    state.HasHeadAnim = false;
            }

            return snapshot.Count > 0;
        }
    }

    public bool TryConsumeRemoteWeaponSnapshots(out List<RemoteWeaponSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_remotes.Count == 0)
            {
                snapshot = EmptyListCache<RemoteWeaponSnapshot>.Instance;
                return false;
            }

            snapshot = RentConsumedList<RemoteWeaponSnapshot>(_remotes.Count);
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote || !state.HasPosition || !state.HasWeaponUpdate)
                    continue;

                int? ammo = state.WeaponAmmo != int.MinValue ? state.WeaponAmmo : (int?)null;
                snapshot.Add(new RemoteWeaponSnapshot(state.Id, state.WeaponKind, state.WeaponSlot, state.WeaponPermanentId, ammo));
                state.HasWeaponUpdate = false;
            }

            return snapshot.Count > 0;
        }
    }

    public bool TryConsumeRemoteAttacks(out List<RemoteAttack> attacks)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingAttacks, out attacks);
        }
    }

    public void ClearMobSyncQueues()
    {
        lock (_sync)
        {
            _pendingMobStates.Clear();
            _pendingMobMoves.Clear();
            _pendingMobHits.Clear();
            _pendingMobDies.Clear();
            _pendingMobAttacks.Clear();
            _pendingMobDraws.Clear();
            _pendingMobRegistry.Clear();
        }
    }

    /// <summary>
    /// After Continue/LoadSave the session stays alive, but cached peer level/room markers still
    /// describe the previous world. Clear them so ghost visibility uses fresh LEVEL/ROOM packets
    /// instead of disposing the new GhostKing as "wrong room/level".
    /// </summary>
    public void ClearRemoteRoomMarkers()
    {
        lock (_sync)
        {
            foreach (var state in _remotes.Values)
            {
                // The cached coordinates belong to the world being left. Keeping HasPosition=true
                // let the ghost bootstrap immediately recreate a remote player at an old-world
                // coordinate before the first fresh position packet arrived (especially visible
                // after restart/Continue as a teammate inside a wall). Hide the remote until a new
                // packet re-establishes both position and level identity.
                state.HasPosition = false;
                state.LevelId = null;
                state.RoomLevelId = null;
                state.RoomId = null;
                state.HasRoom = false;
            }
        }
    }

    public bool TryConsumeMobStates(out List<MobStateSnapshot> snapshot)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingMobStates, out snapshot);
        }
    }

    public bool TryConsumeMobMoves(out List<MobMoveSnapshot> moves)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingMobMoves, out moves);
        }
    }

    public bool TryConsumeMobHits(out List<MobHit> hits)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingMobHits, out hits);
        }
    }

    public bool TryConsumeMobDies(out List<MobDie> dies)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingMobDies, out dies);
        }
    }

    public bool TryConsumeMobAttacks(out List<MobAttack> attacks)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingMobAttacks, out attacks);
        }
    }

    public bool TryConsumeMobDraws(out List<MobDraw> draws)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingMobDraws, out draws);
        }
    }

    public bool TryConsumeMobRegistry(out List<MobRegistryEntry> entries)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingMobRegistry, out entries);
        }
    }

    private static bool TryConsumePendingListLocked<T>(ref List<T> pending, out List<T> snapshot)
    {
        if (pending.Count == 0)
        {
            snapshot = EmptyListCache<T>.Instance;
            return false;
        }

        snapshot = pending;
        pending = RentConsumedList<T>(snapshot.Count);
        return true;
    }

    public bool TryConsumeExitReadyStates(out List<ExitReadyState> states)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingExitReadyStates, out states);
        }
    }

    public bool TryConsumeExitTransitionCommits(out List<ExitTransitionCommit> commits)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingExitTransitionCommits, out commits);
        }
    }

    public bool TryConsumeBossCineLevelIds(out List<string> levelIds)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingBossCineLevelIds, out levelIds);
        }
    }

    public bool TryConsumeBossHeroTeleportEvents(out List<BossHeroTeleportEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingBossHeroTeleports, out events);
        }
    }

    public bool TryConsumePlayerDownStates(out List<PlayerDownState> states)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingPlayerDownStates, out states);
        }
    }

    public bool TryConsumePlayerReviveRequests(out List<PlayerReviveRequest> requests)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingPlayerReviveRequests, out requests);
        }
    }

    public bool TryConsumeInterDoorEvents(out List<InterDoorEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterDoorEvents, out events);
        }
    }

    public bool TryConsumeInterElevatorEvents(out List<InterElevatorEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterElevatorEvents, out events);
        }
    }

    public bool TryConsumeInterElevatorStateEvents(out List<InterElevatorStateEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterElevatorStateEvents, out events);
        }
    }

    public bool TryConsumeInterPressurePlateEvents(out List<InterPressurePlateEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterPressurePlateEvents, out events);
        }
    }

    public bool TryConsumeInterTreasureChestEvents(out List<InterTreasureChestEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterTreasureChestEvents, out events);
        }
    }

    public bool TryConsumeInterVineLadderEvents(out List<InterVineLadderEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterVineLadderEvents, out events);
        }
    }

    public bool TryConsumeInterTeleportEvents(out List<InterTeleportEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterTeleportEvents, out events);
        }
    }

    public bool TryConsumeInterBreakableGroundEvents(out List<InterBreakableGroundEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterBreakableGroundEvents, out events);
        }
    }

    public bool TryConsumeBossRuneUpdateCells(out List<InterBossRuneUpdateCellsEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingBossRuneUpdateCells, out events);
        }
    }

    public bool TryConsumeInterPortalEvents(out List<InterPortalEvent> events)
    {
        lock (_sync)
        {
            return TryConsumePendingListLocked(ref _pendingInterPortalEvents, out events);
        }
    }

    public bool TryGetRemoteHpSnapshots(out List<RemoteHpSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_remotes.Count == 0)
            {
                snapshot = EmptyListCache<RemoteHpSnapshot>.Instance;
                return false;
            }

            snapshot = RentConsumedList<RemoteHpSnapshot>(_remotes.Count);
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote)
                    continue;

                snapshot.Add(new RemoteHpSnapshot(state.Id, state.Life, state.MaxLife, state.Lif, state.BonusLife, state.Recover, state.Username));
            }

            return snapshot.Count > 0;
        }
    }

    public bool TryGetRemoteUserSnapshots(out List<RemoteUserSnapshot> snapshot)
    {
        lock (_sync)
        {
            if (_remotes.Count == 0)
            {
                snapshot = EmptyListCache<RemoteUserSnapshot>.Instance;
                return false;
            }

            snapshot = RentConsumedList<RemoteUserSnapshot>(_remotes.Count);
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote)
                    continue;

                snapshot.Add(new RemoteUserSnapshot(state.Id, state.Username));
            }

            return snapshot.Count > 0;
        }
    }

    public bool TryGetRemoteReady(int userId, out bool ready)
    {
        lock (_sync)
        {
            if (userId > 0 && _remotes.TryGetValue(userId, out var state) && state.HasRemote)
            {
                ready = state.Ready;
                return true;
            }

            ready = false;
            return false;
        }
    }

    public bool TryGetRemoteLevelId(out string? levelId)
    {
        lock (_sync)
        {
            if (_primaryRemoteId != 0 && _remotes.TryGetValue(_primaryRemoteId, out var state))
            {
                levelId = state.LevelId;
                return state.HasRemote && !string.IsNullOrEmpty(levelId);
            }
            levelId = null;
            return false;
        }
    }

    public bool TryGetRemoteHP(out int life, out int maxLife, out int lif, out int bonusLife, out int recover)
    {
        lock (_sync)
        {
            if (_primaryRemoteId != 0 && _remotes.TryGetValue(_primaryRemoteId, out var state))
            {
                life = state.Life;
                maxLife = state.MaxLife;
                lif = state.Lif;
                bonusLife = state.BonusLife;
                recover = state.Recover;
                return state.HasRemote;
            }
            life = 0;
            maxLife = 0;
            lif = 0;
            bonusLife = 0;
            recover = 0;
            return false;
        }
    }

    public bool TryGetRemoteAnim(out string? anim, out int? queueAnim, out bool? g)
    {
        lock (_sync)
        {
            if (_primaryRemoteId != 0 && _remotes.TryGetValue(_primaryRemoteId, out var state))
            {
                if (!state.HasAnim)
                {
                    anim = null;
                    queueAnim = null;
                    g = null;
                    return false;
                }
                anim = state.Anim;
                queueAnim = state.AnimQueue;
                g = state.AnimG;
                state.HasAnim = false;
                return state.HasRemote && anim != null;
            }
            anim = null;
            queueAnim = null;
            g = null;
            return false;
        }
    }

    public bool TryGetRemoteUsername(int userId, out string? username)
    {
        lock (_sync)
        {
            if (userId > 0 && _remotes.TryGetValue(userId, out var state) && state.HasRemote)
            {
                username = state.Username;
                return !string.IsNullOrWhiteSpace(username);
            }

            username = null;
            return false;
        }
    }

    public bool TryGetRemoteSkin(int userId, out string? skin)
    {
        lock (_sync)
        {
            if (userId > 0 && _remotes.TryGetValue(userId, out var state) && state.HasRemote)
            {
                skin = state.Skin;
                return !string.IsNullOrWhiteSpace(skin);
            }

            skin = null;
            return false;
        }
    }

    public void CopyRemoteUserIdsTo(HashSet<int> target, bool includePrimary = true)
    {
        if (target == null)
            return;

        lock (_sync)
        {
            foreach (var state in _remotes.Values)
            {
                if (!state.HasRemote)
                    continue;
                if (!includePrimary && state.Id == _primaryRemoteId)
                    continue;
                if (state.Id > 0)
                    target.Add(state.Id);
            }
        }
    }

    public void CopyConnectedClientIdsTo(HashSet<int> target)
    {
        if (target == null)
            return;

        lock (_clientsLock)
        {
            foreach (var pair in _clients)
            {
                if (pair.Key > 0 && pair.Value.HandshakeComplete)
                    target.Add(pair.Key);
            }
            foreach (var pair in _steamClients)
            {
                if (pair.Key > 0 && pair.Value.HandshakeComplete)
                    target.Add(pair.Key);
            }
        }
    }

    private bool IsHostClientHandshakeComplete(int userId)
    {
        if (userId <= 0)
            return false;

        lock (_clientsLock)
        {
            return (_clients.TryGetValue(userId, out var tcpClient) && tcpClient.HandshakeComplete) ||
                (_steamClients.TryGetValue(userId, out var steamClient) && steamClient.HandshakeComplete);
        }
    }
}

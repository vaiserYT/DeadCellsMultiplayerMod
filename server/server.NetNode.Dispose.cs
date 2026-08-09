using DeadCellsMultiplayerMod;

public sealed partial class NetNode
{
    private static void WaitForTaskShutdown(Task? task, int timeoutMs)
    {
        if (task == null || task.IsCompleted || task.Id == Task.CurrentId)
            return;
        try { task.Wait(timeoutMs); } catch { }
    }

    /// <summary>
    /// Idempotent, ordered teardown. Calling it twice (menu disconnect racing a receive-loop
    /// cleanup, or a process-exit hook racing either) must be a no-op the second time: on Linux the
    /// second pass used to re-enter Steam/socket disposal while background tasks were still
    /// unwinding, which is what surfaced as a crash report on a normal game exit.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        // 1. Stop accepting new work first so nothing re-enters the structures torn down below.
        try { _cts?.Cancel(); } catch { }
        List<ClientConnection> clients;
        List<SteamClientConnection> steamClients;
        lock (_clientsLock)
        {
            clients = new List<ClientConnection>(_clients.Count);
            foreach (var c in _clients.Values)
                clients.Add(c);
            steamClients = new List<SteamClientConnection>(_steamClients.Count);
            foreach (var c in _steamClients.Values)
                steamClients.Add(c);
            _clients.Clear();
            _steamClients.Clear();
            _steamClientIdsBySteam.Clear();
            _connectedClientCount = 0;
        }
        foreach (var client in clients)
        {
            try { client.Dispose(); } catch { }
            if (client.AssignedId >= 2)
            {
                lock (_usedClientIds)
                {
                    _usedClientIds.Remove(client.AssignedId);
                }
            }
        }
        foreach (var steamClient in steamClients)
        {
            _steamBridge?.TryClosePeer(steamClient.SteamId.m_SteamID);
            try { steamClient.Dispose(); } catch { }
            if (steamClient.AssignedId >= 2)
            {
                lock (_usedClientIds)
                {
                    _usedClientIds.Remove(steamClient.AssignedId);
                }
            }
        }

        if (_useSteamTransport && _steamHostId.m_SteamID != 0UL)
        {
            _steamBridge?.TryClosePeer(_steamHostId.m_SteamID);
        }

        // 2. Close the sockets, then let the background loops observe cancellation and exit before
        //    anything they touch is released.
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        WaitForTaskShutdown(_acceptTask, 400);
        WaitForTaskShutdown(_recvTask, 400);
        WaitForTaskShutdown(_steamTransportTask, 400);
        WaitForTaskShutdown(_steamKeepAliveTask, 400);
        Volatile.Write(ref _steamMainThreadDispatchBacklog, 0);
        GameDataSync.Seed = 0;
        lock (_hostCacheSync)
        {
            _cachedHostSeed = null;
            _cachedHostRunSeedSequence = null;
            _cachedHostLaunchKind = null;
            _cachedHostRunCommitPayload = null;
            _cachedHostRunExecutePayload = null;
            _cachedHostRunReadyPayload = null;
            _cachedHostRunLaunchSequence = null;
            _cachedHostBossRune = null;
            _cachedHostSerializerSeq = null;
            _cachedHostSerializerUid = null;
            _cachedHostLevelDescPayload = null;
            _cachedHostLevelSeedsByLevelId.Clear();
            _cachedHostHeroSkin = null;
            _cachedHostHeroHeadSkin = null;
            _cachedHostLevelGraphsByLevelId.Clear();
            _cachedHostGeneratePayload = null;
            _cachedHostCustomGameDataPayload = null;
            _cachedHostCoopId = null;
            _cachedHostHasContinueSave = false;
            _cachedHostMobsHpMult = null;
            _cachedHostBossesHpMult = null;
        }
        lock (_sync)
        {
            _remotes.Clear();
            _primaryRemoteId = 0;
            _hasRemote = false;
            _connectedClientCount = 0;
            _pendingAttacks.Clear();
            _pendingMobStates.Clear();
            _pendingMobMoves.Clear();
            _pendingMobHits.Clear();
            _pendingMobDies.Clear();
            _pendingMobAttacks.Clear();
            _pendingMobDraws.Clear();
            _pendingMobRegistry.Clear();
            _pendingExitReadyStates.Clear();
            _pendingExitTransitionCommits.Clear();
            _latestHostSpawnAnchor = null;
            _pendingBossCineLevelIds.Clear();
            _pendingBossHeroTeleports.Clear();
            _pendingPlayerDownStates.Clear();
            _pendingPlayerReviveRequests.Clear();
            _pendingInterDoorEvents.Clear();
            _pendingInterElevatorEvents.Clear();
            _pendingInterElevatorStateEvents.Clear();
            _pendingInterPressurePlateEvents.Clear();
            _pendingInterTreasureChestEvents.Clear();
            _pendingInterVineLadderEvents.Clear();
            _pendingInterTeleportEvents.Clear();
            _pendingInterBreakableGroundEvents.Clear();
            _pendingBossRuneUpdateCells.Clear();
            _pendingInterPortalEvents.Clear();
            _hasLocalHpSnapshot = false;
        }
        lock (_usedClientIds)
        {
            _usedClientIds.Clear();
        }
        _stream = null; _client = null; _listener = null;
        _acceptTask = null;
        _recvTask = null;
        _steamTransportTask = null;
        _steamKeepAliveTask = null;

        // 3. Release the Steam bridge (and its native callbacks) only after every task that could
        //    call into it has been joined, and detach it before disposal so a late caller sees null
        //    instead of a half-disposed native handle.
        try
        {
            var bridge = _steamBridge;
            _steamBridge = null;
            if (bridge != null)
            {
                if (_useSteamTransport && _role == NetRole.Host)
                    bridge.TryClearRichPresence(out _);
                bridge.Dispose();
            }
        }
        catch { }

        // 4. Cancellation source last: the loops above read _cts?.Token while unwinding.
        try { _cts?.Dispose(); } catch { }
        _cts = null;

        // _sendLock is deliberately NOT disposed. Its AsyncWaitHandle is never used, so disposal
        // buys nothing, while an in-flight SendLineToStreamSafe on another thread would observe an
        // ObjectDisposedException from WaitAsync during shutdown - noise on Windows, and one more
        // way to fault a background task while the Linux runtime is already unwinding.
    }
}

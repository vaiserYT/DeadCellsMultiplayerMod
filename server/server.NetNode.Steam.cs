using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeadCellsMultiplayerMod;
using Steamworks;

public sealed partial class NetNode
{
    internal bool TrySetSteamHostRichPresence(ulong lobbyId)
    {
        if (!_useSteamTransport || _role != NetRole.Host || _steamBridge == null)
            return false;

        var connect = lobbyId == 0UL ? string.Empty : $"+connect_lobby {lobbyId}";
        if (!_steamBridge.TrySetRichPresence("connect", connect, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                _log.Warning("[NetNode] Steam worker set rich presence failed: {Error}", error);
            return false;
        }

        return true;
    }

    internal bool TryClearSteamRichPresence()
    {
        if (!_useSteamTransport || _role != NetRole.Host || _steamBridge == null)
            return false;

        if (!_steamBridge.TryClearRichPresence(out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                _log.Warning("[NetNode] Steam worker clear rich presence failed: {Error}", error);
            return false;
        }

        return true;
    }
    private void StartSteamHost()
    {
        _cts = new CancellationTokenSource();
        ISteamP2PBridge? bridge = null;
        var error = string.Empty;
        var hostIp = SteamConnect.ResolveBestHostIp();

        // The game process already owns a working SteamAPI instance. Prefer that path instead of
        // spawning a second process that frequently cannot initialize Steam and returns an empty
        // startup event. Keep the isolated worker as a fallback for installations where in-process
        // lobby/P2P access is unavailable.
        if (SteamP2PInProcessBridge.TryStartHost(
                _steamHostPort,
                hostIp,
                out var inProcessBridge,
                out var inProcessError) &&
            inProcessBridge?.HostLobbyResult?.Success == true)
        {
            bridge = inProcessBridge;
        }
        else
        {
            error = string.IsNullOrWhiteSpace(inProcessError)
                ? "In-process Steam P2P host failed"
                : inProcessError;
            try { inProcessBridge?.Dispose(); } catch { }
            _log.Warning("[NetNode] In-process Steam P2P host start failed: {Error}", error);
        }

        if (bridge == null)
        {
            var inProcessFailure = error;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (SteamP2PWorkerBridge.TryStart(
                        NetRole.Host,
                        new CSteamID(0),
                        _steamHostPort,
                        hostIp,
                        out var workerBridge,
                        out var workerError))
                {
                    if (workerBridge?.HostLobbyResult?.Success == true)
                    {
                        bridge = workerBridge;
                        error = string.Empty;
                        break;
                    }

                    workerError = workerBridge?.HostLobbyResult?.Error ?? "Steam worker became ready without a lobby";
                    try { workerBridge?.Dispose(); } catch { }
                }

                error = string.IsNullOrWhiteSpace(workerError)
                    ? inProcessFailure
                    : $"in-process: {inProcessFailure}; worker: {workerError}";
                _log.Warning(
                    "[NetNode] Steam P2P host worker start attempt {Attempt}/2 failed: {Error}",
                    attempt,
                    workerError);
                if (attempt < 2)
                    Thread.Sleep(350);
            }
        }

        if (bridge == null)
        {
            _steamHostStartupResult = new SteamConnect.HostLobbyResult
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(error)
                    ? "Steam P2P transport failed to start"
                    : error
            };
            try { _cts.Cancel(); } catch { }
            _log.Warning("[NetNode] Steam P2P host failed to start: {Error}", error);
            return;
        }

        _steamBridge = bridge;
        _steamHostStartupResult = bridge.HostLobbyResult;
        _log.Information(
            "[NetNode] Host started with Steam P2P transport ({Transport}), lobbyId={LobbyId}",
            bridge is SteamP2PInProcessBridge ? "in-process" : "worker fallback",
            bridge.HostLobbyResult?.LobbyId ?? 0UL);
        _steamTransportTask = Task.Run(() => SteamBridgeLoop(_cts.Token));
    }

    private void StartSteamClient()
    {
        _cts = new CancellationTokenSource();
        ISteamP2PBridge? bridge = null;
        var error = string.Empty;

        if (SteamP2PInProcessBridge.TryStartClient(_steamHostId, out var inProcessBridge, out var inProcessError))
        {
            bridge = inProcessBridge;
        }
        else
        {
            error = string.IsNullOrWhiteSpace(inProcessError)
                ? "In-process Steam P2P client failed"
                : inProcessError;
            try { inProcessBridge?.Dispose(); } catch { }
            _log.Warning("[NetNode] In-process Steam P2P client start failed: {Error}", error);
        }

        if (bridge == null)
        {
            var inProcessFailure = error;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (SteamP2PWorkerBridge.TryStart(NetRole.Client, _steamHostId, 0, null, out var workerBridge, out var workerError))
                {
                    bridge = workerBridge;
                    error = string.Empty;
                    break;
                }

                error = string.IsNullOrWhiteSpace(workerError)
                    ? inProcessFailure
                    : $"in-process: {inProcessFailure}; worker: {workerError}";
                _log.Warning(
                    "[NetNode] Steam P2P client worker start attempt {Attempt}/2 failed: {Error}",
                    attempt,
                    workerError);
                if (attempt < 2)
                    Thread.Sleep(350);
            }
        }

        if (bridge == null)
        {
            try { _cts.Cancel(); } catch { }
            _log.Warning("[NetNode] Steam P2P client failed to start: {Error}", error);
            GameMenu.EnqueueMainThreadCoalesced("net:client-connect-failed", () =>
            {
                if (IsCurrentNetworkSession())
                    GameMenu.NotifyClientConnectFailed();
            });
            return;
        }

        _steamBridge = bridge;
        _log.Information(
            "[NetNode] Client started with Steam P2P transport ({Transport})",
            bridge is SteamP2PInProcessBridge ? "in-process" : "worker fallback");
        _steamTransportTask = Task.Run(() => SteamBridgeLoop(_cts.Token));
        _ = Task.Run(() => ConnectWithRetrySteamBridgeAsync(_cts.Token));
    }

    private async Task ConnectWithRetrySteamBridgeAsync(CancellationToken ct)
    {
        var maxAttempts = GameMenu.ClientConnectMaxAttempts;
        var attempt = 0;
        var bridge = _steamBridge;

        if (_steamHostId.m_SteamID == 0UL || bridge == null)
        {
            _log.Warning("[NetNode] Steam client host id or bridge is missing");
            GameMenu.EnqueueMainThreadCoalesced("net:client-connect-failed", () =>
            {
                if (IsCurrentNetworkSession())
                    GameMenu.NotifyClientConnectFailed();
            });
            return;
        }

        if (bridge.LocalSteamId != 0UL && bridge.LocalSteamId == _steamHostId.m_SteamID)
        {
            _log.Warning(
                "[NetNode] Steam P2P requires two different Steam accounts. Host and client both use SteamId={SteamId}. " +
                "Use a second Steam account (e.g. family sharing or another PC) to test multiplayer.",
                _steamHostId.m_SteamID);
            GameMenu.EnqueueMainThreadCoalesced("net:client-connect-failed", () =>
            {
                if (IsCurrentNetworkSession())
                    GameMenu.NotifyClientConnectFailed();
            });
            return;
        }

        while (!ct.IsCancellationRequested && attempt < maxAttempts)
        {
            attempt++;
            GameMenu.EnqueueMainThreadCoalesced("net:client-connect-attempt", () =>
            {
                if (IsCurrentNetworkSession())
                    GameMenu.NotifyClientConnectAttempt(attempt);
            });
            _log.Information("[NetNode] Steam client connecting to hostSteamId={HostSteamId}", _steamHostId.m_SteamID);

            var helloBytes = Encoding.UTF8.GetBytes(BuildHelloLine());
            if (!bridge.TrySend(_steamHostId.m_SteamID, EP2PSend.k_EP2PSendReliable, SteamP2PChannelClientToHost, helloBytes, out var sendError))
            {
                _log.Warning("[NetNode] Steam HELLO send failed: {Error}", sendError);
            }

            var connected = false;
            var startedAt = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(6))
            {
                lock (_sync)
                {
                    connected = _hasRemote && ID > 0;
                }
                if (connected)
                    break;

                await Task.Delay(150, ct).ConfigureAwait(false);
            }

            if (connected)
            {
                GameMenu.EnqueueCriticalMainThreadCoalesced("net:remote-connected", () =>
                {
                    if (!IsCurrentNetworkSession())
                        return;
                    GameMenu.SetRole(_role);
                    GameMenu.NotifyRemoteConnected(_role);
                });
                return;
            }

            _log.Warning(
                "[NetNode] Steam client attempt {Attempt}/{Max}: no WELCOME/ID received within 6s",
                attempt,
                maxAttempts);

            if (attempt >= maxAttempts)
            {
                _log.Warning(
                    "[NetNode] Steam client connection failed: no WELCOME/ID received within 6s after HELLO (attempt {Attempt}/{Max})",
                    attempt,
                    maxAttempts);
                GameMenu.EnqueueMainThreadCoalesced("net:client-connect-failed", () =>
                {
                    if (IsCurrentNetworkSession())
                        GameMenu.NotifyClientConnectFailed();
                });
                break;
            }

            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
    }

    private async Task SteamBridgeLoop(CancellationToken ct)
    {
        var bridge = _steamBridge;
        if (bridge == null)
            return;

        _lastSteamPacketReceivedTicks = Stopwatch.GetTimestamp();
        var expectedChannel = _role == NetRole.Host ? SteamP2PChannelClientToHost : SteamP2PChannelHostToClient;

        while (!ct.IsCancellationRequested && !_disposed)
        {
            var hasPacket = false;

            while (bridge.TryReadPacket(out var packet))
            {
                hasPacket = true;
                if (packet.Channel != expectedChannel)
                    continue;

                if (_role == NetRole.Client)
                {
                    if (_steamHostId.m_SteamID != 0UL && packet.RemoteSteamId != _steamHostId.m_SteamID)
                        continue;
                    _lastSteamPacketReceivedTicks = Stopwatch.GetTimestamp();
                    await ProcessIncomingSteamPayloadAsync(packet.Payload, 1, null, ct).ConfigureAwait(false);
                }
                else
                {
                    var remoteSteamId = new CSteamID(packet.RemoteSteamId);
                    if (!TryGetOrRegisterSteamClient(remoteSteamId, out var connection) || connection == null)
                        continue;
                    await ProcessIncomingSteamPayloadAsync(packet.Payload, connection.AssignedId, connection, ct).ConfigureAwait(false);
                }
            }

            while (bridge.TryReadWarning(out var warning))
            {
                _log.Warning("[NetNode] Steam P2P worker: {Warning}", warning);
            }

            while (bridge.TryReadSessionFail(out var failedSteamId))
            {
                hasPacket = true;
                _log.Warning("[NetNode] P2P session failed: remote={RemoteId}", failedSteamId);
                if (_role == NetRole.Client && failedSteamId == _steamHostId.m_SteamID)
                {
                    GameMenu.EnqueueMainThreadCoalesced("net:cleanup-client", () =>
                    {
                        if (IsCurrentNetworkSession())
                            CleanupClient();
                    });
                    return;
                }
                if (_role == NetRole.Host)
                {
                    SteamClientConnection? connection = null;
                    lock (_clientsLock)
                    {
                        if (_steamClientIdsBySteam.TryGetValue(failedSteamId, out var assignedId) &&
                            _steamClients.TryGetValue(assignedId, out var conn))
                        {
                            connection = conn;
                        }
                    }
                    if (connection != null)
                    {
                        var connToCleanup = connection;
                        GameMenu.EnqueueMainThreadCoalesced(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"net:cleanup-host-client:{connToCleanup.AssignedId}"), () =>
                        {
                            if (IsCurrentNetworkSession())
                                CleanupHostSteamClient(connToCleanup);
                        });
                    }
                }
            }

            if (!hasPacket)
                TrySendSteamKeepAlive();

            if (_role == NetRole.Client && !hasPacket && _hasRemote)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (double)(now - _lastSteamPacketReceivedTicks) / Stopwatch.Frequency;
                if (elapsed >= SteamReceiveTimeoutSeconds)
                {
                    _log.Warning("[NetNode] Steam client receive timeout ({Elapsed:F1}s)", elapsed);
                    GameMenu.EnqueueMainThreadCoalesced("net:cleanup-client", () =>
                    {
                        if (IsCurrentNetworkSession())
                            CleanupClient();
                    });
                    return;
                }
            }

            if (!hasPacket)
                await Task.Delay(8, ct).ConfigureAwait(false);
        }
    }

    private void TrySendSteamKeepAlive()
    {
        var bridge = _steamBridge;
        if (bridge == null)
            return;

        var now = Stopwatch.GetTimestamp();
        var minTicks = (long)(Stopwatch.Frequency * SteamKeepAliveSeconds);
        if (_lastSteamKeepAliveSentTicks != 0 && now - _lastSteamKeepAliveSentTicks < minTicks)
            return;

        if (_role == NetRole.Client)
        {
            bool connected;
            lock (_sync)
                connected = _hasRemote && ID > 0;

            if (!connected || _steamHostId.m_SteamID == 0UL)
                return;

            _lastSteamKeepAliveSentTicks = now;
            bridge.TrySend(
                _steamHostId.m_SteamID,
                EP2PSend.k_EP2PSendReliable,
                SteamP2PChannelClientToHost,
                SteamKeepAliveBytes,
                out _);
            return;
        }

        List<SteamClientConnection> clients;
        lock (_clientsLock)
        {
            if (_steamClients.Count == 0)
                return;

            clients = new List<SteamClientConnection>(_steamClients.Values);
        }

        _lastSteamKeepAliveSentTicks = now;
        foreach (var client in clients)
        {
            bridge.TrySend(
                client.SteamId.m_SteamID,
                EP2PSend.k_EP2PSendReliable,
                SteamP2PChannelHostToClient,
                SteamKeepAliveBytes,
                out _);
        }
    }

    private bool TryGetOrRegisterSteamClient(CSteamID remoteSteamId, out SteamClientConnection? connection)
    {
        connection = null;
        if (!IsCurrentNetworkSession())
            return false;
        var steamKey = remoteSteamId.m_SteamID;

        int existingId;
        lock (_clientsLock)
        {
            if (_steamClientIdsBySteam.TryGetValue(steamKey, out existingId) &&
                _steamClients.TryGetValue(existingId, out var existingConnection))
            {
                connection = existingConnection;
                return true;
            }
        }

        if (!TryTakeNextUnusedClientId(out var assignedId))
        {
            _log.Warning("[NetNode] Max players reached, ignoring Steam client {SteamId}", steamKey);
            return false;
        }

        if (_steamBridge != null && _steamBridge.LocalSteamId != 0UL && _steamBridge.LocalSteamId == steamKey)
        {
            _log.Warning(
                "[NetNode] Steam P2P requires two different Steam accounts. Client SteamId={SteamId} matches host. " +
                "Connection will not work correctly. Use a second Steam account to join.",
                steamKey);
        }

        var newConnection = new SteamClientConnection(remoteSteamId, assignedId);
        lock (_clientsLock)
        {
            _steamClients[assignedId] = newConnection;
            _steamClientIdsBySteam[steamKey] = assignedId;
            _connectedClientCount = CountCompletedHostClientsLocked();
        }

        connection = newConnection;
        _log.Information("[NetNode] Steam client registered: SteamId={SteamId} assignedId={AssignedId}", steamKey, assignedId);
        _ = Task.Run(() => SendInitialStateToSteamClient(newConnection));
        return true;
    }

    private bool IsCurrentSteamClientConnection(SteamClientConnection connection)
    {
        if (!IsCurrentNetworkSession())
            return false;
        lock (_clientsLock)
        {
            return _steamClients.TryGetValue(connection.AssignedId, out var current) &&
                   ReferenceEquals(current, connection);
        }
    }

    private async Task SendInitialStateToSteamClient(SteamClientConnection connection, bool forceSend = false)
    {
        if (!IsCurrentSteamClientConnection(connection) ||
            !connection.TryReserveInitialStateSend(TimeSpan.FromMilliseconds(750), forceSend))
            return;

        await SendSteamHandshakeToSteamClient(connection).ConfigureAwait(false);
        if (!IsCurrentSteamClientConnection(connection))
            return;

        int? cachedBossRune;
        int? cachedSeed;
        int? cachedRunSeedSequence;
        string? cachedLaunchKind;
        string? cachedRunCommitPayload;
        string? cachedRunExecutePayload;
        string? cachedRunReadyPayload;
        int? cachedSerializerSeq;
        int? cachedSerializerUid;
        string? cachedLevelDescPayload;
        string? cachedLevelSeedPayload;
        string? cachedLevelGraphPayload;
        string? cachedCustomGameDataPayload;
        string? cachedHeroSkin;
        string? cachedHeroHeadSkin;
        string? cachedCoopId;
        bool cachedHasContinueSave;
        double? cachedMobsHpMult;
        double? cachedBossesHpMult;
        lock (_hostCacheSync)
        {
            cachedBossRune = _cachedHostBossRune;
            cachedSeed = _cachedHostSeed;
            cachedRunSeedSequence = _cachedHostRunSeedSequence;
            cachedLaunchKind = _cachedHostLaunchKind;
            cachedRunCommitPayload = _cachedHostRunCommitPayload;
            cachedRunExecutePayload = _cachedHostRunExecutePayload;
            cachedRunReadyPayload = _cachedHostRunReadyPayload;
            cachedSerializerSeq = _cachedHostSerializerSeq;
            cachedSerializerUid = _cachedHostSerializerUid;
            cachedLevelDescPayload = _cachedHostLevelDescPayload;
            cachedLevelSeedPayload = _cachedHostLevelSeedPayload;
            cachedLevelGraphPayload = _cachedHostLevelGraphPayload;
            cachedCustomGameDataPayload = _cachedHostCustomGameDataPayload;
            cachedHeroSkin = _cachedHostHeroSkin;
            cachedHeroHeadSkin = _cachedHostHeroHeadSkin;
            cachedCoopId = _cachedHostCoopId;
            cachedHasContinueSave = _cachedHostHasContinueSave;
            cachedMobsHpMult = _cachedHostMobsHpMult;
            cachedBossesHpMult = _cachedHostBossesHpMult;
        }

        if (cachedSerializerSeq.HasValue && cachedSerializerUid.HasValue)
            await SendLineToSteamClientSafe(connection, $"HXSYNC|{cachedSerializerSeq.Value}|{cachedSerializerUid.Value}\n").ConfigureAwait(false);
        if (cachedBossRune.HasValue)
            await SendLineToSteamClientSafe(connection, $"BOSSRUNE|{cachedBossRune.Value}\n").ConfigureAwait(false);
        if (cachedCoopId != null)
            await SendLineToSteamClientSafe(connection, BuildCoopStateLine(1, cachedCoopId, cachedHasContinueSave)).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedCustomGameDataPayload))
            await SendLineToSteamClientSafe(connection, $"CGDATA|{cachedCustomGameDataPayload}\n").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedRunCommitPayload))
            await SendLineToSteamClientSafe(connection, $"{RunLaunchWireCodec.CommitTag}|{cachedRunCommitPayload}\n").ConfigureAwait(false);
        if (cachedSeed.HasValue && cachedRunSeedSequence.HasValue)
            await SendLineToSteamClientSafe(
                connection,
                $"SEED|{cachedRunSeedSequence.Value}|{cachedSeed.Value}|{cachedLaunchKind ?? string.Empty}\n").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedRunExecutePayload))
            await SendLineToSteamClientSafe(connection, $"{RunLaunchWireCodec.ExecuteTag}|{cachedRunExecutePayload}\n").ConfigureAwait(false);
        if (cachedLevelDescPayload != null)
            await SendLineToSteamClientSafe(connection, $"LDESC|{cachedLevelDescPayload}\n").ConfigureAwait(false);
        if (cachedLevelSeedPayload != null)
            await SendLineToSteamClientSafe(connection, $"LSEED|{cachedLevelSeedPayload}\n").ConfigureAwait(false);
        if (cachedLevelGraphPayload != null)
            await SendLineToSteamClientSafe(connection, $"LGRAPH|{cachedLevelGraphPayload}\n").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedHeroSkin))
            await SendLineToSteamClientSafe(connection, BuildTaggedLine("SKIN", 1, cachedHeroSkin)).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedHeroHeadSkin))
            await SendLineToSteamClientSafe(connection, BuildTaggedLine("HEAD", 1, cachedHeroHeadSkin)).ConfigureAwait(false);
        await SendKnownUsersToSteamClientSafe(connection).ConfigureAwait(false);
        if (_role == NetRole.Host && TryBuildLocalHpLine(out var localHpLine))
            await SendLineToSteamClientSafe(connection, localHpLine).ConfigureAwait(false);
        if (cachedMobsHpMult.HasValue && cachedBossesHpMult.HasValue)
            await SendLineToSteamClientSafe(connection, $"HPMULT|{cachedMobsHpMult.Value.ToString(CultureInfo.InvariantCulture)}|{cachedBossesHpMult.Value.ToString(CultureInfo.InvariantCulture)}\n").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedRunReadyPayload))
            await SendLineToSteamClientSafe(connection, $"{RunLaunchWireCodec.ReadyTag}|{cachedRunReadyPayload}\n").ConfigureAwait(false);
    }

    private async Task SendSteamHandshakeToSteamClient(SteamClientConnection connection)
    {
        await SendLineToSteamClientSafe(connection, BuildWelcomeLine()).ConfigureAwait(false);
        await SendLineToSteamClientSafe(connection, $"ID|{connection.AssignedId}\n").ConfigureAwait(false);
    }

    private async Task ProcessIncomingSteamPayloadAsync(
        string payload,
        int senderId,
        SteamClientConnection? senderConnection,
        CancellationToken cancellationToken)
    {
        if (IsSupersededNetworkSession() || string.IsNullOrEmpty(payload))
            return;
        if (payload.Length > MaxProtocolLineChars)
        {
            _log.Warning("[NetNode] Ignored oversized Steam protocol payload ({Length} chars)", payload.Length);
            return;
        }

        var lines = payload.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            var lineCopy = line;
            if (TryHandleFastPathLine(lineCopy, senderId))
                continue;

            await GameMenu.EnqueueNetworkMainThreadAsync(() =>
            {
                if (!IsCurrentNetworkSession())
                    return;

                try
                {
                    if (!HandleLine(lineCopy, senderId, out var forwardLine))
                    {
                        if (_role == NetRole.Host && senderConnection != null)
                            CleanupHostSteamClient(senderConnection);
                        else
                            CleanupClient();
                        return;
                    }

                    if (_role == NetRole.Host && senderConnection != null && forwardLine != null)
                        ForwardLineToOtherSteamClients(senderConnection, forwardLine);
                }
                catch (Exception ex)
                {
                    _log.Warning("[NetNode] Steam HandleLine(main-thread) failed: {msg}", ex.Message);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
    private void ForwardLineToOtherSteamClients(SteamClientConnection sender, string line)
    {
        List<SteamClientConnection> snapshot;
        lock (_clientsLock)
        {
            snapshot = new List<SteamClientConnection>(_steamClients.Count);
            foreach (var c in _steamClients.Values)
            {
                if (c.AssignedId != sender.AssignedId)
                    snapshot.Add(c);
            }
        }

        foreach (var client in snapshot)
        {
            _ = SendLineToSteamClientSafe(client, line);
        }
    }

    private void CleanupHostSteamClient(SteamClientConnection sender)
    {
        if (!IsCurrentNetworkSession())
            return;

        var wasConnected = sender.HandshakeComplete;
        bool hasClients;
        lock (_clientsLock)
        {
            _steamClients.Remove(sender.AssignedId);
            _steamClientIdsBySteam.Remove(sender.SteamId.m_SteamID);
            _connectedClientCount = CountCompletedHostClientsLocked();
            hasClients = _connectedClientCount > 0;
        }

        _steamBridge?.TryClosePeer(sender.SteamId.m_SteamID);
        sender.Dispose();

        if (sender.AssignedId >= 2)
        {
            lock (_usedClientIds)
            {
                _usedClientIds.Remove(sender.AssignedId);
            }
        }

        lock (_sync)
        {
            RemoveRemoteLocked(sender.AssignedId);
            _pendingAttacks.RemoveAll(a => a.Id == sender.AssignedId);
            _pendingMobHits.RemoveAll(h => h.UserId == sender.AssignedId);
            _pendingMobDies.RemoveAll(d => d.UserId == sender.AssignedId);
            _pendingExitReadyStates.RemoveAll(s => s.UserId == sender.AssignedId);
            _pendingPlayerDownStates.RemoveAll(s => s.UserId == sender.AssignedId);
            _pendingPlayerReviveRequests.RemoveAll(s => s.ReviverId == sender.AssignedId || s.TargetId == sender.AssignedId);
            _hasRemote = hasClients;
        }

        if (wasConnected && !hasClients)
            GameMenu.EnqueueCriticalMainThreadCoalesced("net:remote-disconnected", () =>
            {
                if (IsCurrentNetworkSession())
                    GameMenu.NotifyRemoteDisconnected(_role);
            });
    }
}

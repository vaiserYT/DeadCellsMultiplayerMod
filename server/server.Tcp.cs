using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DeadCellsMultiplayerMod;
using Serilog;

public sealed partial class NetNode
{
    // ================= TCP HOST =================
    private void StartHost()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(_bindEp);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();

        var lep = (IPEndPoint)_listener.LocalEndpoint;
        _log.Information("[NetNode][Net] Host started OK. Bound to {0}:{1}", lep.Address, lep.Port);

        _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
        _steamKeepAliveTask = Task.Run(() => SteamKeepAliveLoop(_cts.Token));
    }

    // ================= TCP CLIENT =================
    private void StartClient()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectWithRetryAsync(_cts.Token));
        _steamKeepAliveTask = Task.Run(() => SteamKeepAliveLoop(_cts.Token));
    }

    /// <summary>
    /// Enables OS-level TCP keep-alive probes. Complements the application PING: this one also
    /// keeps the mapping alive inside routers/VPN adapters that only watch the transport layer, and
    /// makes a half-open connection fail fast instead of hanging in a read forever.
    /// </summary>
    private void ConfigureTcpSocketKeepAlive(TcpClient tcp)
    {
        try
        {
            var socket = tcp.Client;
            if (socket == null)
                return;

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch (Exception ex)
        {
            // Per-option support varies by OS/runtime; the application PING is the portable path.
            _log.Debug("[NetNode][Net] TCP keep-alive options unavailable: {Message}", ex.Message);
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        var maxAttempts = GameMenu.ClientConnectMaxAttempts;
        var attempt = 0;

        while (!ct.IsCancellationRequested && attempt < maxAttempts)
        {
            attempt++;
            GameMenu.EnqueueMainThreadCoalesced("net:client-connect-attempt", () =>
            {
                if (IsCurrentNetworkSession())
                    GameMenu.NotifyClientConnectAttempt(attempt);
            });
            try
            {
                _log.Information("[NetNode] Client connecting to {dest}", _destEp);

                var tcp = new TcpClient(AddressFamily.InterNetwork);
                tcp.NoDelay = true;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                await tcp.ConnectAsync(_destEp.Address, _destEp.Port, timeoutCts.Token).ConfigureAwait(false);
                ConfigureTcpSocketKeepAlive(tcp);
                _client = tcp;
                _stream = tcp.GetStream();

                _log.Information("[NetNode] Client connected to {dest}", _destEp);

                await SendLineSafe(BuildHelloLine()).ConfigureAwait(false);

                // Do not report a live room until WELCOME and ID pass the compatibility
                // handshake. The old path showed "connected" for arbitrary TCP listeners.
                _recvTask = Task.Run(() => RecvLoop(_stream!, ct, 1, null));
                return;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                CloseClientConnection();
                _log.Warning("[NetNode] Client connect error: {msg}", ex.Message);
                if (attempt >= maxAttempts)
                {
                    GameMenu.EnqueueMainThreadCoalesced("net:client-connect-failed", () =>
                    {
                        if (IsCurrentNetworkSession())
                            GameMenu.NotifyClientConnectFailed();
                    });
                    break;
                }
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
    }

    // ================= TCP ACCEPT & RECV =================
    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _listener != null)
            {
                var tcp = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    try { tcp.Close(); } catch { }
                    break;
                }

                if (!TryTakeNextUnusedClientId(out var assignedId))
                {
                    _log.Warning("[NetNode] Max players reached, kicking client");
                    try { tcp.Close(); } catch { }
                    continue;
                }
                tcp.NoDelay = true;
                ConfigureTcpSocketKeepAlive(tcp);
                var connection = new ClientConnection(tcp, assignedId);
                lock (_clientsLock)
                {
                    _clients[assignedId] = connection;
                    _connectedClientCount = CountCompletedHostClientsLocked();
                }

                _log.Information("[NetNode] Host accepted {ep}", connection.RemoteEndPoint);

                await SendLineToClientSafe(connection, BuildWelcomeLine()).ConfigureAwait(false);

                // Identity BEFORE state. Handling "ID|" is what makes the joining peer adopt the
                // Client role, and the launch coordinator refuses a RUNCOMMIT from a peer whose role
                // it has not yet observed. Sending the cached launch first therefore raced: the
                // client rejected a perfectly valid launch it would never be offered again, and sat
                // in the lobby. (The Steam path already sent WELCOME+ID before its cached state,
                // which is why this reproduced far more readily over direct IP.)
                await SendLineToClientSafe(connection, $"ID|{assignedId}\n").ConfigureAwait(false);

                if (_role == NetRole.Host)
                {
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
                    List<string> cachedLevelSeedPayloads;
                    List<string> cachedLevelGraphPayloads;
                    string? cachedGeneratePayload;
                    string? cachedCustomGameDataPayload;
                    string? cachedRuneProgressPayload;
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
                        // Replay EVERY generated level, not just the most recent: the joiner needs
                        // the one for the level it is about to load, which is rarely the last one
                        // the host generated.
                        cachedLevelSeedPayloads = new List<string>(_cachedHostLevelSeedsByLevelId.Values);
                        cachedLevelGraphPayloads = new List<string>(_cachedHostLevelGraphsByLevelId.Values);
                        cachedGeneratePayload = _cachedHostGeneratePayload;
                        cachedCustomGameDataPayload = _cachedHostCustomGameDataPayload;
                        cachedRuneProgressPayload = _cachedHostRuneProgressPayload;
                        cachedCoopId = _cachedHostCoopId;
                        cachedHasContinueSave = _cachedHostHasContinueSave;
                        cachedMobsHpMult = _cachedHostMobsHpMult;
                        cachedBossesHpMult = _cachedHostBossesHpMult;
                    }

                    if (cachedSerializerSeq.HasValue && cachedSerializerUid.HasValue)
                        await SendLineToClientSafe(connection, $"HXSYNC|{cachedSerializerSeq.Value}|{cachedSerializerUid.Value}\n").ConfigureAwait(false);

                    if (cachedBossRune.HasValue)
                        await SendLineToClientSafe(connection, $"BOSSRUNE|{cachedBossRune.Value}\n").ConfigureAwait(false);

                    if (cachedCoopId != null)
                        await SendLineToClientSafe(connection, BuildCoopStateLine(1, cachedCoopId, cachedHasContinueSave)).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cachedCustomGameDataPayload))
                        await SendLineToClientSafe(connection, $"CGDATA|{cachedCustomGameDataPayload}\n").ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cachedRuneProgressPayload))
                        await SendLineToClientSafe(connection, $"RUNEPROG|{cachedRuneProgressPayload}\n").ConfigureAwait(false);

                    // GEN carries the pending launch action the client's auto-start gate waits on.
                    // It must precede RUNCOMMIT so the client already knows which launch kind the
                    // committed run belongs to when the commit lands.
                    if (!string.IsNullOrWhiteSpace(cachedGeneratePayload))
                        await SendLineToClientSafe(connection, $"GEN|{cachedGeneratePayload}\n").ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cachedRunCommitPayload))
                        await SendLineToClientSafe(connection, $"{RunLaunchWireCodec.CommitTag}|{cachedRunCommitPayload}\n").ConfigureAwait(false);

                    if (cachedSeed.HasValue && cachedRunSeedSequence.HasValue)
                        await SendLineToClientSafe(
                            connection,
                            $"SEED|{cachedRunSeedSequence.Value}|{cachedSeed.Value}|{cachedLaunchKind ?? string.Empty}\n").ConfigureAwait(false);

                    // A late joiner must receive the authoritative current level before RUNEXEC
                    // can auto-start it. Keeping execute ahead of LSEED/LGRAPH recreated the old
                    // race where a fast client entered LevelGen while the large graph packet was
                    // still behind the execute packet on a real network. Client launch state is
                    // order-independent, so prerequisites can safely be replayed first.
                    if (cachedLevelDescPayload != null)
                        await SendLineToClientSafe(connection, $"LDESC|{cachedLevelDescPayload}\n").ConfigureAwait(false);

                    for (var i = 0; i < cachedLevelSeedPayloads.Count; i++)
                        await SendLineToClientSafe(connection, $"LSEED|{cachedLevelSeedPayloads[i]}\n").ConfigureAwait(false);

                    for (var i = 0; i < cachedLevelGraphPayloads.Count; i++)
                        await SendLineToClientSafe(connection, $"LGRAPH|{cachedLevelGraphPayloads[i]}\n").ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cachedRunExecutePayload))
                        await SendLineToClientSafe(connection, $"{RunLaunchWireCodec.ExecuteTag}|{cachedRunExecutePayload}\n").ConfigureAwait(false);

                    if (cachedMobsHpMult.HasValue && cachedBossesHpMult.HasValue)
                        await SendLineToClientSafe(connection, $"HPMULT|{cachedMobsHpMult.Value.ToString(CultureInfo.InvariantCulture)}|{cachedBossesHpMult.Value.ToString(CultureInfo.InvariantCulture)}\n").ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cachedRunReadyPayload))
                        await SendLineToClientSafe(connection, $"{RunLaunchWireCodec.ReadyTag}|{cachedRunReadyPayload}\n").ConfigureAwait(false);
                }

                await SendKnownUsersToClientSafe(connection).ConfigureAwait(false);
                if (_role == NetRole.Host && TryBuildLocalHpLine(out var localHpLine))
                    await SendLineToClientSafe(connection, localHpLine).ConfigureAwait(false);

                _ = Task.Run(() => RecvLoop(connection.Stream, ct, assignedId, connection));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] AcceptLoop error: {msg}", ex.Message);
        }
    }

    private async Task RecvLoop(NetworkStream stream, CancellationToken ct, int? senderId, ClientConnection? sender)
    {
        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var recvCt = recvCts.Token;
        var incomingLines = Channel.CreateBounded<string>(new BoundedChannelOptions(PendingNetworkLineLimit)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        try
        {
            var readTask = ReadIncomingLinesLoop(stream, incomingLines.Writer, senderId, recvCt);
            var processTask = ProcessIncomingLinesLoop(incomingLines.Reader, senderId, sender, recvCts, recvCt);
            var handshakeTask = sender == null
                ? Task.CompletedTask
                : EnforceTcpHandshakeTimeoutAsync(sender, recvCts, recvCt);
            await Task.WhenAll(readTask, processTask, handshakeTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (recvCt.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] RecvLoop error: {msg}", ex.Message);
        }
        finally
        {
            try { recvCts.Cancel(); } catch { }
            if (_role == NetRole.Host && sender != null)
            {
                CleanupHostClient(sender);
            }
            else
            {
                CleanupClient();
            }
        }
    }

    private async Task ReadIncomingLinesLoop(NetworkStream stream, ChannelWriter<string> writer, int? senderId, CancellationToken ct)
    {
        var buf = new byte[4096];
        var sb = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n <= 0)
                    break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.Length > MaxProtocolLineChars)
                    throw new InvalidDataException($"Incoming protocol line exceeded {MaxProtocolLineChars} characters.");

                while (TryReadBufferedLine(sb, out var line))
                {
                    if (line.Length == 0)
                        continue;

                    // Resolve fast-path lines here rather than after the ordered channel. Mob
                    // traffic needs no game thread, and a control line waiting on a full
                    // main-thread queue would otherwise head-of-line block every movement
                    // snapshot behind it — and then the socket read itself.
                    if (TryHandleFastPathLine(line, senderId))
                        continue;

                    await writer.WriteAsync(line, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Recv read error: {msg}", ex.Message);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task ProcessIncomingLinesLoop(
        ChannelReader<string> reader,
        int? senderId,
        ClientConnection? sender,
        CancellationTokenSource recvCts,
        CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    var lineCopy = line;

                    await GameMenu.EnqueueNetworkMainThreadAsync(() =>
                    {
                        if (!IsCurrentNetworkSession())
                            return;

                        try
                        {
                            if (!HandleLine(lineCopy, senderId, out var forwardLine))
                            {
                                try { recvCts.Cancel(); } catch { }
                                return;
                            }

                            if (_role == NetRole.Host && sender != null && forwardLine != null)
                                ForwardLineToOtherClients(sender, forwardLine);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning("[NetNode] HandleLine(main-thread) failed: {msg}", ex.Message);
                        }
                    }, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Recv process error: {msg}", ex.Message);
        }
    }

    private void ForwardLineToOtherClients(ClientConnection sender, string line)
    {
        List<ClientConnection> snapshot;
        lock (_clientsLock)
        {
            snapshot = new List<ClientConnection>(_clients.Count);
            foreach (var c in _clients.Values)
            {
                if (c.AssignedId != sender.AssignedId)
                    snapshot.Add(c);
            }
        }

        foreach (var client in snapshot)
        {
            _ = SendLineToClientSafe(client, line);
        }
    }

    private void CleanupHostClient(ClientConnection sender)
    {
        var wasConnected = sender.HandshakeComplete;
        bool hasClients;
        lock (_clientsLock)
        {
            _clients.Remove(sender.AssignedId);
            _connectedClientCount = CountCompletedHostClientsLocked();
            hasClients = _connectedClientCount > 0;
        }

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
        {
            bool stillNoCompletedClients;
            lock (_clientsLock)
            {
                stillNoCompletedClients = CountCompletedHostClientsLocked() == 0;
            }
            if (stillNoCompletedClients)
                GameMenu.EnqueueCriticalMainThreadCoalesced("net:remote-disconnected", () =>
                {
                    if (IsCurrentNetworkSession())
                        GameMenu.NotifyRemoteDisconnected(_role);
                });
        }
    }

    private Task SendLineToClientSafe(ClientConnection client, string line)
    {
        return SendLineToStreamSafe(client.Stream, client.SendLock, line);
    }

    private async Task SendKnownUsersToClientSafe(ClientConnection connection)
    {
        List<RemoteState> snapshot;
        lock (_sync)
        {
            if (_remotes.Count == 0)
                return;
            snapshot = new List<RemoteState>(_remotes.Values);
        }

        foreach (var state in snapshot)
        {
            var username = state.Username;
            if (string.IsNullOrWhiteSpace(username))
                continue;
            var line = BuildTaggedLine("USER", state.Id, username);
            await SendLineToClientSafe(connection, line).ConfigureAwait(false);
            await SendLineToClientSafe(connection, BuildReadyLine(state.Id, state.Ready)).ConfigureAwait(false);
            await SendLineToClientSafe(connection, BuildCoopStateLine(state.Id, state.CoopId, state.HasContinueSave)).ConfigureAwait(false);
        }
    }
}
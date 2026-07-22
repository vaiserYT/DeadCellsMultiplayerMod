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
        _log.Information("[NetNode] Host started OK. Bound to {0}:{1}", lep.Address, lep.Port);

        _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
    }

    // ================= TCP CLIENT =================
    private void StartClient()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectWithRetryAsync(_cts.Token));
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
                var connection = new ClientConnection(tcp, assignedId);
                lock (_clientsLock)
                {
                    _clients[assignedId] = connection;
                    _connectedClientCount = CountCompletedHostClientsLocked();
                }

                _log.Information("[NetNode] Host accepted {ep}", connection.RemoteEndPoint);

                await SendLineToClientSafe(connection, BuildWelcomeLine()).ConfigureAwait(false);
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
                    string? cachedLevelSeedPayload;
                    string? cachedLevelGraphPayload;
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
                        cachedMobsHpMult = _cachedHostMobsHpMult;
                        cachedBossesHpMult = _cachedHostBossesHpMult;
                    }

                    if (cachedSerializerSeq.HasValue && cachedSerializerUid.HasValue)
                        await SendLineToClientSafe(connection, $"HXSYNC|{cachedSerializerSeq.Value}|{cachedSerializerUid.Value}\n").ConfigureAwait(false);

                    if (cachedBossRune.HasValue)
                        await SendLineToClientSafe(connection, $"BOSSRUNE|{cachedBossRune.Value}\n").ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cachedRunCommitPayload))
                        await SendLineToClientSafe(connection, $"{RunLaunchWireCodec.CommitTag}|{cachedRunCommitPayload}\n").ConfigureAwait(false);

                    if (cachedSeed.HasValue && cachedRunSeedSequence.HasValue)
                        await SendLineToClientSafe(
                            connection,
                            $"SEED|{cachedRunSeedSequence.Value}|{cachedSeed.Value}|{cachedLaunchKind ?? string.Empty}\n").ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cachedRunExecutePayload))
                        await SendLineToClientSafe(connection, $"{RunLaunchWireCodec.ExecuteTag}|{cachedRunExecutePayload}\n").ConfigureAwait(false);

                    if (cachedLevelDescPayload != null)
                        await SendLineToClientSafe(connection, $"LDESC|{cachedLevelDescPayload}\n").ConfigureAwait(false);

                    if (cachedLevelSeedPayload != null)
                        await SendLineToClientSafe(connection, $"LSEED|{cachedLevelSeedPayload}\n").ConfigureAwait(false);

                    if (cachedLevelGraphPayload != null)
                        await SendLineToClientSafe(connection, $"LGRAPH|{cachedLevelGraphPayload}\n").ConfigureAwait(false);

                    if (cachedMobsHpMult.HasValue && cachedBossesHpMult.HasValue)
                        await SendLineToClientSafe(connection, $"HPMULT|{cachedMobsHpMult.Value.ToString(CultureInfo.InvariantCulture)}|{cachedBossesHpMult.Value.ToString(CultureInfo.InvariantCulture)}\n").ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cachedRunReadyPayload))
                        await SendLineToClientSafe(connection, $"{RunLaunchWireCodec.ReadyTag}|{cachedRunReadyPayload}\n").ConfigureAwait(false);
                }

                await SendLineToClientSafe(connection, $"ID|{assignedId}\n").ConfigureAwait(false);
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
            var readTask = ReadIncomingLinesLoop(stream, incomingLines.Writer, recvCt);
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

    private async Task ReadIncomingLinesLoop(NetworkStream stream, ChannelWriter<string> writer, CancellationToken ct)
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

                    if (_role == NetRole.Client && TryHandleClientFastPathLine(lineCopy))
                        continue;

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
            _pendingChatMessages.RemoveAll(m => m.Id == sender.AssignedId);
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
        }
    }
}

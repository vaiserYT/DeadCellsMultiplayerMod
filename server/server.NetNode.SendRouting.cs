using System.Net.Sockets;
using System.Text;
using Steamworks;

public sealed partial class NetNode
{
    private static byte[] Utf8ProtocolBytes(string line) => Encoding.UTF8.GetBytes(line);

    private void CloseClientConnection()
    {
        try { _stream?.Close(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }

    private static bool IsRealtimeSteamLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
            return false;

        if (IsPositionLine(trimmed))
            return true;

        return trimmed.StartsWith("ANIM|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("HEADANIM|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("HP|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("MOBSTATE|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("MOBSTATE2|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("MOBMOVE|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("MOBCHARGE|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("MOBDRAW|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("INTERELEVSTATE|", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDroppableTcpRealtimeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
            return false;

        if (IsPositionLine(trimmed))
            return true;

        // Only packets that are superseded by a newer frame may be dropped when TCP is
        // congested. Full MOBSTATE bootstrap/resync chunks, HP, hits, deaths and control
        // messages must remain reliable and ordered.
        return trimmed.StartsWith("ANIM|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("HEADANIM|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("MOBMOVE|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("MOBCHARGE|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("MOBDRAW|", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("INTERELEVSTATE|", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPositionLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var separatorIndex = line.IndexOf('|');
        if (separatorIndex <= 0)
            return false;

        for (var i = 0; i < separatorIndex; i++)
        {
            if (!char.IsDigit(line[i]))
                return false;
        }

        return true;
    }

    private static EP2PSend ResolveSteamSendType(string line)
    {
        // Full mob tables are split into multiple chunks and form the authoritative bootstrap for
        // a level. Sending those chunks unreliably can leave the client permanently missing a
        // subset of enemies even though ordinary movement packets continue to arrive.
        if (line.StartsWith("MOBEVENT|", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("MOBSTATE|", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("MOBSTATE2|", StringComparison.OrdinalIgnoreCase))
        {
            return EP2PSend.k_EP2PSendReliable;
        }

        return IsRealtimeSteamLine(line)
            ? EP2PSend.k_EP2PSendUnreliable
            : EP2PSend.k_EP2PSendReliable;
    }

    private int GetSteamOutgoingChannel()
    {
        return _role == NetRole.Host
            ? SteamP2PChannelHostToClient
            : SteamP2PChannelClientToHost;
    }

    private bool HasAnyConnection()
    {
        if (_role == NetRole.Host)
        {
            lock (_clientsLock)
            {
                return _useSteamTransport ? _steamClients.Count > 0 : _clients.Count > 0;
            }
        }
        if (_useSteamTransport)
        {
            lock (_sync) return _hasRemote;
        }

        return _stream != null && _client != null && _client.Connected;
    }

    /// <summary>Sends a pre-encoded mob protocol line (used by MobSyncWorker). Line must start with MOB.</summary>
    public Task SendMobWireLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return Task.CompletedTask;

        if (!line.StartsWith("MOB", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        if (_role != NetRole.Host && _role != NetRole.Client)
            return Task.CompletedTask;

        if (!HasAnyConnection())
            return Task.CompletedTask;

        return SendLineSafe(line);
    }

    private Task SendLineSafe(string line)
    {
        if (_role == NetRole.Host)
            return BroadcastLineSafe(line);

        if (_useSteamTransport && _steamBridge != null)
            return SendLineToSteamBridgeSafe(_steamHostId.m_SteamID, line, ResolveSteamSendType(line), GetSteamOutgoingChannel());

        return SendLineToStreamSafe(_stream, _sendLock, line);
    }

    private async Task BroadcastLineSafe(string line)
    {
        if (_useSteamTransport && _steamBridge != null)
        {
            List<SteamClientConnection> steamSnapshot;
            lock (_clientsLock)
            {
                steamSnapshot = new List<SteamClientConnection>(_steamClients.Count);
                foreach (var c in _steamClients.Values)
                    steamSnapshot.Add(c);
            }
            if (steamSnapshot.Count == 0) return;
            var sendType = ResolveSteamSendType(line);
            var channel = SteamP2PChannelHostToClient;
            var bytes = Utf8ProtocolBytes(line);
            if (bytes.Length > MaxProtocolLineChars)
            {
                _log.Warning("[NetNode] Rejected oversized Steam broadcast ({Length} bytes)", bytes.Length);
                return;
            }
            foreach (var client in steamSnapshot)
            {
                _steamBridge.TrySend(client.SteamId.m_SteamID, sendType, channel, bytes, out _);
            }
            return;
        }

        List<ClientConnection> snapshot;
        lock (_clientsLock)
        {
            snapshot = new List<ClientConnection>(_clients.Count);
            foreach (var c in _clients.Values)
                snapshot.Add(c);
        }
        if (snapshot.Count == 0) return;
        var tasks = new Task[snapshot.Count];
        for (var i = 0; i < snapshot.Count; i++)
            tasks[i] = SendLineToClientSafe(snapshot[i], line);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SendKnownUsersToSteamClientSafe(SteamClientConnection connection)
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
            await SendLineToSteamClientSafe(connection, line).ConfigureAwait(false);
            await SendLineToSteamClientSafe(connection, BuildReadyLine(state.Id, state.Ready)).ConfigureAwait(false);
            await SendLineToSteamClientSafe(connection, BuildCoopStateLine(state.Id, state.CoopId, state.HasContinueSave)).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(state.Skin))
            {
                var skinLine = BuildTaggedLine("SKIN", state.Id, state.Skin);
                await SendLineToSteamClientSafe(connection, skinLine).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(state.Head))
            {
                var headLine = BuildTaggedLine("HEAD", state.Id, state.Head);
                await SendLineToSteamClientSafe(connection, headLine).ConfigureAwait(false);
            }
        }
    }

    private async Task SendLineToStreamSafe(NetworkStream? stream, SemaphoreSlim? sendLock, string line)
    {
        if (stream == null || sendLock == null || _disposed || string.IsNullOrEmpty(line))
            return;

        var bytes = Utf8ProtocolBytes(line);
        if (bytes.Length > MaxProtocolLineChars)
        {
            _log.Warning("[NetNode] Rejected oversized TCP protocol line ({Length} bytes)", bytes.Length);
            return;
        }

        var realtime = IsDroppableTcpRealtimeLine(line);
        var token = _cts?.Token ?? CancellationToken.None;
        var locked = false;
        try
        {
            // Position and visual animation packets become obsolete immediately. If a previous TCP
            // write is still in flight, drop only those stale visual packets instead of creating an
            // unbounded chain of Tasks waiting on the send semaphore. Full mob snapshots, HP,
            // control, death and hit lines remain reliable and ordered.
            if (realtime)
            {
                locked = await sendLock.WaitAsync(0).ConfigureAwait(false);
                if (!locked)
                    return;
            }
            else
            {
                await sendLock.WaitAsync(token).ConfigureAwait(false);
                locked = true;
            }

            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] send error: {msg}", ex.Message);
        }
        finally
        {
            if (locked)
            {
                try { sendLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private Task SendLineToSteamClientSafe(SteamClientConnection client, string line, EP2PSend? sendType = null)
    {
        if (_steamBridge == null)
            return Task.CompletedTask;
        var bytes = Utf8ProtocolBytes(line);
        if (bytes.Length > MaxProtocolLineChars)
        {
            _log.Warning("[NetNode] Steam payload too large for {SteamId}: {PayloadSize} bytes", client.SteamId.m_SteamID, bytes.Length);
            return Task.CompletedTask;
        }
        var st = sendType ?? ResolveSteamSendType(line);
        if (!_steamBridge.TrySend(client.SteamId.m_SteamID, st, SteamP2PChannelHostToClient, bytes, out var err))
            _log.Warning("[NetNode] Steam send failed to {SteamId}: {Error}", client.SteamId.m_SteamID, err);
        return Task.CompletedTask;
    }

    private Task SendLineToSteamBridgeSafe(ulong steamId, string line, EP2PSend sendType, int channel)
    {
        if (_steamBridge == null || steamId == 0UL)
            return Task.CompletedTask;

        var bytes = Utf8ProtocolBytes(line);
        if (bytes.Length > MaxProtocolLineChars || bytes.Length > SteamMaxPacketSizeBytes)
        {
            _log.Warning(
                "[NetNode] Steam payload too large for {SteamId}: {PayloadSize} bytes (protocol limit {Limit} bytes)",
                steamId,
                bytes.Length,
                MaxProtocolLineChars);
            return Task.CompletedTask;
        }

        if (!_steamBridge.TrySend(steamId, sendType, channel, bytes, out var err))
        {
            var ctx = line.StartsWith("HELLO", StringComparison.Ordinal) ? " HELLO" : string.Empty;
            _log.Warning("[NetNode] Steam send failed to {SteamId}: {Error}{Context}", steamId, err, ctx);
        }
        return Task.CompletedTask;
    }
}

using System.Globalization;
using DeadCellsMultiplayerMod;

public sealed partial class NetNode
{
    private const double HandshakeTimeoutSeconds = 8.0;

    private static string BuildHelloLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"HELLO|{BuildInfo.NetworkProtocolVersion}|{BuildInfo.Version}\n");

    private static string BuildWelcomeLine() => string.Create(
        CultureInfo.InvariantCulture,
        $"WELCOME|{BuildInfo.NetworkProtocolVersion}|{BuildInfo.Version}\n");

    private static bool TryValidateHandshakeLine(
        string line,
        string expectedTag,
        out int remoteProtocol,
        out string remoteBuild,
        out string failure)
    {
        remoteProtocol = 0;
        remoteBuild = "unknown";
        failure = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            failure = "empty handshake";
            return false;
        }

        var parts = line.Split('|');
        if (parts.Length != 3 || !string.Equals(parts[0], expectedTag, StringComparison.OrdinalIgnoreCase))
        {
            failure = "legacy or malformed handshake";
            return false;
        }

        remoteBuild = string.IsNullOrWhiteSpace(parts[2]) ? "unknown" : parts[2].Trim();
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out remoteProtocol))
        {
            failure = "invalid protocol number";
            return false;
        }

        if (remoteProtocol != BuildInfo.NetworkProtocolVersion)
        {
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"protocol {remoteProtocol} is incompatible with {BuildInfo.NetworkProtocolVersion}");
            return false;
        }

        if (!string.Equals(remoteBuild, BuildInfo.Version, StringComparison.OrdinalIgnoreCase))
        {
            failure = string.Create(
                CultureInfo.InvariantCulture,
                $"build {remoteBuild} does not match {BuildInfo.Version}");
            return false;
        }

        return true;
    }

    private bool RejectIncompatiblePeer(string line, string expectedTag)
    {
        if (TryValidateHandshakeLine(line, expectedTag, out var remoteProtocol, out var remoteBuild, out var failure))
            return false;

        _log.Warning(
            "[NetNode] Rejected incompatible co-op peer: {Failure}; remoteBuild={RemoteBuild} remoteProtocol={RemoteProtocol} localBuild={LocalBuild} localProtocol={LocalProtocol}",
            failure,
            remoteBuild,
            remoteProtocol,
            BuildInfo.Version,
            BuildInfo.NetworkProtocolVersion);
        GameMenu.NotifyProtocolMismatch(remoteBuild, remoteProtocol, BuildInfo.Version, BuildInfo.NetworkProtocolVersion, _role);
        return true;
    }

    private void CompleteHostHandshake(int senderId)
    {
        if (_role != NetRole.Host || senderId <= 0)
            return;

        var newlyCompleted = false;
        lock (_clientsLock)
        {
            if (_useSteamTransport)
            {
                if (_steamClients.TryGetValue(senderId, out var steamClient))
                    newlyCompleted = steamClient.TryCompleteHandshake();
            }
            else if (_clients.TryGetValue(senderId, out var client))
            {
                newlyCompleted = client.TryCompleteHandshake();
            }

            _connectedClientCount = CountCompletedHostClientsLocked();
        }

        if (!newlyCompleted)
            return;

        lock (_sync)
        {
            _hasRemote = true;
            if (_primaryRemoteId == 0)
                _primaryRemoteId = senderId;
        }

        GameMenu.EnqueueCriticalMainThreadCoalesced("net:remote-connected", () =>
        {
            if (!IsCurrentNetworkSession())
                return;
            GameMenu.SetRole(_role);
            GameMenu.NotifyRemoteConnected(_role);
        });
    }

    private int CountCompletedHostClientsLocked()
    {
        var count = 0;
        if (_useSteamTransport)
        {
            foreach (var connection in _steamClients.Values)
            {
                if (connection.HandshakeComplete)
                    count++;
            }
        }
        else
        {
            foreach (var connection in _clients.Values)
            {
                if (connection.HandshakeComplete)
                    count++;
            }
        }

        return count;
    }

    private async Task EnforceTcpHandshakeTimeoutAsync(
        ClientConnection connection,
        CancellationTokenSource receiveCancellation,
        CancellationToken cancellationToken)
    {
        if (connection == null || _role != NetRole.Host)
            return;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested && !connection.HandshakeComplete)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds >= HandshakeTimeoutSeconds)
            {
                _log.Warning(
                    "[NetNode] Closing TCP peer {RemoteEndPoint}: compatible HELLO not received within {TimeoutSeconds:F0}s",
                    connection.RemoteEndPoint,
                    HandshakeTimeoutSeconds);
                try { receiveCancellation.Cancel(); } catch { }
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }
}

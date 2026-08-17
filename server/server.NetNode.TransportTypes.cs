using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Steamworks;

public sealed partial class NetNode
{
    private sealed class ClientConnection : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int AssignedId { get; }
        public EndPoint? RemoteEndPoint => Client.Client?.RemoteEndPoint;
        private int _handshakeComplete;
        public bool HandshakeComplete => Volatile.Read(ref _handshakeComplete) != 0;

        public ClientConnection(TcpClient client, int assignedId)
        {
            Client = client;
            Stream = client.GetStream();
            AssignedId = assignedId;
        }

        public bool TryCompleteHandshake() => Interlocked.Exchange(ref _handshakeComplete, 1) == 0;

        public void Dispose()
        {
            try { Stream.Close(); } catch { }
            try { Client.Close(); } catch { }
            try { SendLock.Dispose(); } catch { }
        }
    }

    private sealed class SteamClientConnection : IDisposable
    {
        public CSteamID SteamId { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int AssignedId { get; }
        private int _handshakeComplete;
        public bool HandshakeComplete => Volatile.Read(ref _handshakeComplete) != 0;
        private readonly object _initialStateSync = new();
        private DateTime _lastInitialStateSentUtc = DateTime.MinValue;
        private long _lastPacketReceivedTicks;

        public SteamClientConnection(CSteamID steamId, int assignedId)
        {
            SteamId = steamId;
            AssignedId = assignedId;
            _lastPacketReceivedTicks = Stopwatch.GetTimestamp();
        }

        public long LastPacketReceivedTicks => Interlocked.Read(ref _lastPacketReceivedTicks);
        public void MarkPacketReceived() => Interlocked.Exchange(ref _lastPacketReceivedTicks, Stopwatch.GetTimestamp());

        public bool TryCompleteHandshake() => Interlocked.Exchange(ref _handshakeComplete, 1) == 0;

        public bool TryReserveInitialStateSend(TimeSpan minInterval, bool force = false)
        {
            var now = DateTime.UtcNow;
            lock (_initialStateSync)
            {
                if (!force &&
                    _lastInitialStateSentUtc != DateTime.MinValue &&
                    now - _lastInitialStateSentUtc < minInterval)
                {
                    return false;
                }

                _lastInitialStateSentUtc = now;
                return true;
            }
        }

        public void Dispose()
        {
            try { SendLock.Dispose(); } catch { }
        }
    }
}

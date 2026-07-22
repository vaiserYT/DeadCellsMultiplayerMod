using System;
using System.Collections.Concurrent;
using System.Text;
using Steamworks;

namespace DeadCellsMultiplayerMod
{
    /// <summary>
    /// Fallback transport when the isolated Steam worker cannot initialize SteamAPI on a user's
    /// installation. It uses the SteamAPI already owned and callback-pumped by the modded game.
    /// </summary>
    internal sealed class SteamP2PInProcessBridge : ISteamP2PBridge
    {
        private const uint SteamMaxPacketSizeBytes = 16u * 1024u * 1024u;
        private const int SteamMinReceiveBufferBytes = 64 * 1024;

        private readonly NetRole _role;
        private readonly ulong _hostSteamId;
        private readonly Callback<P2PSessionRequest_t> _sessionRequestCallback;
        private readonly Callback<P2PSessionConnectFail_t> _sessionFailCallback;
        private readonly ConcurrentQueue<string> _warnings = new();
        private readonly ConcurrentQueue<ulong> _sessionFails = new();
        private byte[] _receiveBuffer = new byte[SteamMinReceiveBufferBytes];
        private int _nextReadChannel;
        private bool _disposed;

        public SteamConnect.HostLobbyResult? HostLobbyResult { get; }
        public ulong LocalSteamId { get; }
        public bool IsRunning => !_disposed;

        private SteamP2PInProcessBridge(
            NetRole role,
            ulong hostSteamId,
            SteamConnect.HostLobbyResult? hostLobbyResult)
        {
            _role = role;
            _hostSteamId = hostSteamId;
            HostLobbyResult = hostLobbyResult;
            LocalSteamId = ModEntry.RunSteamNetworkingSerialized(() => SteamUser.GetSteamID().m_SteamID);

            _sessionRequestCallback = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            _sessionFailCallback = Callback<P2PSessionConnectFail_t>.Create(OnSessionFail);
        }

        internal static bool TryStartHost(
            int hostPort,
            string hostIp,
            out ISteamP2PBridge? bridge,
            out string error)
        {
            bridge = null;
            error = string.Empty;
            if (!ModEntry.EnsureSteamApiForNetworking("in-process P2P host fallback"))
            {
                error = "SteamAPI is unavailable in the game process";
                return false;
            }

            SteamConnect.HostLobbyResult? createdLobby = null;
            try
            {
                var lobby = new SteamConnect.HostLobbyResult
                {
                    Success = false,
                    Error = "Steam lobby creation failed"
                };
                var created = ModEntry.RunSteamNetworkingSerialized(() =>
                    SteamConnect.TryCreateLobbyForP2PHost(hostPort, hostIp, out lobby));
                if (!created || !lobby.Success)
                {
                    error = string.IsNullOrWhiteSpace(lobby.Error) ? "Steam lobby creation failed" : lobby.Error;
                    return false;
                }

                createdLobby = lobby;
                bridge = new SteamP2PInProcessBridge(NetRole.Host, 0UL, lobby);
                return true;
            }
            catch (Exception ex)
            {
                if (createdLobby?.LobbyId > 0UL)
                {
                    try { SteamConnect.LeaveLobby(createdLobby.LobbyId); } catch { }
                }
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryStartClient(
            CSteamID hostSteamId,
            out ISteamP2PBridge? bridge,
            out string error)
        {
            bridge = null;
            error = string.Empty;
            if (hostSteamId.m_SteamID == 0UL)
            {
                error = "Steam host id is missing";
                return false;
            }
            if (!ModEntry.EnsureSteamApiForNetworking("in-process P2P client fallback"))
            {
                error = "SteamAPI is unavailable in the game process";
                return false;
            }

            try
            {
                bridge = new SteamP2PInProcessBridge(NetRole.Client, hostSteamId.m_SteamID, null);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TrySend(ulong steamId, EP2PSend sendType, int channel, byte[] payload, out string error)
        {
            error = string.Empty;
            if (_disposed || steamId == 0UL || payload == null || payload.Length == 0)
            {
                error = "Steam P2P fallback is not available";
                return false;
            }
            if (_role == NetRole.Client && steamId != _hostSteamId)
            {
                error = "Steam client attempted to send to an unexpected peer";
                return false;
            }

            try
            {
                var sent = ModEntry.RunSteamNetworkingSerialized(() =>
                    SteamNetworking.SendP2PPacket(
                        new CSteamID(steamId),
                        payload,
                        (uint)payload.Length,
                        sendType,
                        channel));
                if (!sent)
                    error = $"Steam send failed to {steamId} ({sendType}, ch={channel})";
                return sent;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TryClosePeer(ulong steamId)
        {
            if (_disposed || steamId == 0UL)
                return false;
            try
            {
                return ModEntry.RunSteamNetworkingSerialized(() =>
                    SteamNetworking.CloseP2PSessionWithUser(new CSteamID(steamId)));
            }
            catch
            {
                return false;
            }
        }

        public bool TrySetRichPresence(string key, string value, out string error)
        {
            error = string.Empty;
            if (_disposed || string.IsNullOrWhiteSpace(key))
                return false;
            try
            {
                var ok = ModEntry.RunSteamNetworkingSerialized(() =>
                    SteamFriends.SetRichPresence(key, value ?? string.Empty));
                if (!ok)
                    error = "Steam rich presence update failed";
                return ok;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TryClearRichPresence(out string error)
        {
            error = string.Empty;
            if (_disposed)
                return false;
            try
            {
                ModEntry.RunSteamNetworkingSerialized(SteamFriends.ClearRichPresence);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TryReadPacket(out SteamP2PWorkerPacket packet)
        {
            packet = default;
            if (_disposed)
                return false;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var channel = (_nextReadChannel + attempt) & 1;
                try
                {
                    uint packetSize = 0;
                    var available = ModEntry.RunSteamNetworkingSerialized(() =>
                        SteamNetworking.IsP2PPacketAvailable(out packetSize, channel));
                    if (!available)
                        continue;
                    if (packetSize == 0 || packetSize > SteamMaxPacketSizeBytes || packetSize > int.MaxValue)
                    {
                        _warnings.Enqueue($"Rejected invalid Steam P2P packet size {packetSize} on channel {channel}");
                        continue;
                    }

                    var required = (int)Math.Max(packetSize, SteamMinReceiveBufferBytes);
                    if (_receiveBuffer.Length < required)
                        Array.Resize(ref _receiveBuffer, required);

                    uint bytesRead = 0;
                    CSteamID remote = default;
                    var read = ModEntry.RunSteamNetworkingSerialized(() =>
                        SteamNetworking.ReadP2PPacket(
                            _receiveBuffer,
                            (uint)_receiveBuffer.Length,
                            out bytesRead,
                            out remote,
                            channel));
                    if (!read || bytesRead == 0 || bytesRead > _receiveBuffer.Length)
                        continue;
                    if (_role == NetRole.Client && remote.m_SteamID != _hostSteamId)
                        continue;

                    _nextReadChannel = (channel + 1) & 1;
                    packet = new SteamP2PWorkerPacket(
                        remote.m_SteamID,
                        channel,
                        Encoding.UTF8.GetString(_receiveBuffer, 0, (int)bytesRead));
                    return true;
                }
                catch (Exception ex)
                {
                    _warnings.Enqueue($"Steam receive error: {ex.Message}");
                }
            }

            _nextReadChannel = (_nextReadChannel + 1) & 1;
            return false;
        }

        public bool TryReadWarning(out string warning) => _warnings.TryDequeue(out warning!);
        public bool TryReadSessionFail(out ulong steamId) => _sessionFails.TryDequeue(out steamId);

        private void OnSessionRequest(P2PSessionRequest_t data)
        {
            if (_disposed)
                return;
            var remote = data.m_steamIDRemote;
            if (_role == NetRole.Client && remote.m_SteamID != _hostSteamId)
                return;

            try
            {
                ModEntry.RunSteamNetworkingSerialized(() => SteamNetworking.AcceptP2PSessionWithUser(remote));
            }
            catch (Exception ex)
            {
                _warnings.Enqueue($"Steam session accept failed: {ex.Message}");
            }
        }

        private void OnSessionFail(P2PSessionConnectFail_t data)
        {
            if (!_disposed && data.m_steamIDRemote.m_SteamID != 0UL)
                _sessionFails.Enqueue(data.m_steamIDRemote.m_SteamID);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_role == NetRole.Client && _hostSteamId != 0UL)
            {
                try
                {
                    ModEntry.RunSteamNetworkingSerialized(() =>
                        SteamNetworking.CloseP2PSessionWithUser(new CSteamID(_hostSteamId)));
                }
                catch { }
            }
            else if (_role == NetRole.Host && HostLobbyResult?.LobbyId > 0UL)
            {
                try { SteamConnect.LeaveLobby(HostLobbyResult.LobbyId); } catch { }
            }

            _disposed = true;
            try { _sessionRequestCallback.Dispose(); } catch { }
            try { _sessionFailCallback.Dispose(); } catch { }
        }
    }
}

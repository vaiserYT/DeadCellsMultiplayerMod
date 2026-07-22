using System.Net;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private IPEndPoint BuildEndpoint(string ipText, int port)
        {
            if (port <= 0 || port > 65535) port = 1234;
            if (!IPAddress.TryParse(ipText, out var ip))
            {
                ip = IPAddress.Loopback;
            }
            return new IPEndPoint(ip, port);
        }

        /// <summary>
        /// Hosting binds EVERY interface. Binding the address typed in the menu meant the default
        /// 127.0.0.1 produced a loopback-only listener, so no second machine could ever connect
        /// over LAN, VPN or a forwarded port — the IP field is what a joining CLIENT dials, not
        /// what the host binds. IPAddress.Any covers loopback (same-PC testing), LAN, VPN and WAN
        /// from a single listener.
        /// </summary>
        private static IPEndPoint BuildHostBindEndpoint(int port)
        {
            if (port <= 0 || port > 65535) port = 1234;
            return new IPEndPoint(IPAddress.Any, port);
        }

        public void StartHostFromMenu(string ipText, int port)
        {
            var ep = BuildHostBindEndpoint(port);
            StartHostWithEndpoint(ep);
        }

        public void StartClientFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartClientWithEndpoint(ep);
        }

        public void StartSteamHostFromMenu(int hostPort)
        {
            StartHostWithSteamTransport(hostPort);
        }

        public void StartSteamClientFromMenu(ulong hostSteamId)
        {
            StartClientWithSteamTransport(hostSteamId);
        }

        private void StartHostCore(Action createHost)
        {
            if (_netRole == NetRole.Client)
            {
                var localUser = dc.Main.Class.ME?.user ?? game?.user;
                if (localUser != null)
                    GameDataSync.RestoreOriginalUserState(localUser, clearRemote: true);
                GameDataSync.SwapToLocalSerializerSync();
            }

            _net?.Dispose();
            GameMenu.NetRef = null;
            ResetNetworkState();
            createHost();
            // Publish the newly-created node immediately. Its accept/connect loops start inside the
            // factory, so delaying NetRef until after other setup left a small window where valid
            // early callbacks were treated as belonging to no/currently-superseded session.
            GameMenu.NetRef = _net;
            _netRole = NetRole.Host;
            _net?.SendHpMultipliers();
            GameMenu.SetRole(_netRole);
            ConnectionUI.NotifyConnectionsChanged();
        }

        private void StartHostWithEndpoint(IPEndPoint ep)
        {
            StartHostCore(() => _net = NetNode.CreateHost(Logger, ep));
            var lep = _net?.ListenerEndpoint;
            if (lep != null)
                Logger.Information($"[NetMod] Host listening at {lep.Address}:{lep.Port}");
            LogHostJoinAddresses(ep.Port);
        }

        /// <summary>
        /// Logs exactly what a joining player should type. Without this, a LAN/port-forward host
        /// has no in-game way to discover which address to share or which port to forward.
        /// </summary>
        private void LogHostJoinAddresses(int port)
        {
            try
            {
                var addresses = new List<string>();
                foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr))
                        continue;
                    addresses.Add(addr.ToString());
                }

                if (addresses.Count > 0)
                {
                    Logger.Information(
                        "[NetMod] Same-network players join at: {Addresses} (port {Port})",
                        string.Join(" or ", addresses),
                        port);
                }
                else
                {
                    Logger.Information(
                        "[NetMod] No LAN IPv4 address detected; only same-PC joins (127.0.0.1:{Port}) will work",
                        port);
                }

                Logger.Information(
                    "[NetMod] Over the internet: forward TCP port {Port} to this PC, then share your public IP",
                    port);
            }
            catch (Exception ex)
            {
                Logger.Debug("[NetMod] Could not enumerate local addresses: {Message}", ex.Message);
            }
        }

        private void StartClientCore(Action createClient)
        {
            _net?.Dispose();
            GameMenu.NetRef = null;
            var main = dc.Main.Class.ME;
            if (main?.user != null)
                GameDataSync.RestoreOriginalUserState(main.user, true);
            ResetNetworkState();
            createClient();
            // Publish before UI setup for the same reason as the host path: the background
            // connection loop is already active when the factory returns.
            GameMenu.NetRef = _net;
            _netRole = NetRole.Client;
            GameMenu.SetRole(_netRole);
            ConnectionUI.NotifyConnectionsChanged();
        }

        private void StartClientWithEndpoint(IPEndPoint ep)
        {
            StartClientCore(() => _net = NetNode.CreateClient(Logger, ep));
            Logger.Information($"[NetMod] Client connecting to {ep.Address}:{ep.Port}");
        }

        private void StartHostWithSteamTransport(int hostPort)
        {
            if (!_ready)
            {
                Logger.Warning("[NetMod] Steam host start rejected: OnGameEndInit not yet run");
                return;
            }
            StartHostCore(() => _net = NetNode.CreateSteamHost(Logger, hostPort));
            var lobby = _net?.HostLobbyResult;
            if (lobby?.Success == true)
            {
                Logger.Information(
                    "[NetMod] Host started with Steam P2P transport lobbyId={LobbyId}",
                    lobby.LobbyId);
            }
            else
            {
                Logger.Warning(
                    "[NetMod] Steam host worker/lobby did not start: {Error}",
                    lobby?.Error ?? "No worker startup result");
            }
        }

        private void StartClientWithSteamTransport(ulong hostSteamId)
        {
            if (!_ready)
            {
                Logger.Warning("[NetMod] Steam client start rejected: OnGameEndInit not yet run");
                return;
            }
            StartClientCore(() => _net = NetNode.CreateSteamClient(Logger, hostSteamId));
            Logger.Information("[NetMod] Client connecting via Steam P2P to hostSteamId={HostSteamId}", hostSteamId);
        }

        public void StopNetworkFromMenu()
        {
            var roleBeforeStop = _netRole;
            if (roleBeforeStop == NetRole.Client)
            {
                Logger.Information("[NetMod] Disconnecting client from host...");
                _net?.SendControlAndFlush("BYE", 500);
                var localUser = dc.Main.Class.ME?.user ?? game?.user;
                if (localUser != null)
                    GameDataSync.RestoreOriginalUserState(localUser, clearRemote: true);
                GameDataSync.SwapToLocalSerializerSync();
            }
            else if (roleBeforeStop == NetRole.Host)
            {
                Logger.Information("[NetMod] Disposing host server...");
                _net?.SendControlAndFlush("KICK", 500);
            }

            _net?.Dispose();
            // Invalidate the public session reference before draining/resetting queues. Otherwise
            // a late callback from the disposed node can still pass IsCurrentNetworkSession and
            // repopulate the just-cleared state.
            GameMenu.NetRef = null;
            ResetNetworkState();
            _net = null;
            _netRole = NetRole.None;
            GameMenu.SetRole(_netRole);
            ConnectionUI.NotifyConnectionsChanged();
        }
    }
}

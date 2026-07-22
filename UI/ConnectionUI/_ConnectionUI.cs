using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using ModCore.Utilities;
using System.Collections.Generic;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    public static class _ConnectionUI
    {
        /// <summary>Sentinel for <see cref="GameMenu.IsSteamJoinLobbyResolvePending"/>; displayed in ConnectionUI only.</summary>
        internal const string SteamLobbyConnectingMarker = "_steamLobbyConnecting";

        public static List<string> GetAllPlayerNames()
        {
            var playerNames = new List<string>();

            var net = ModEntry._net;
            if (net == null)
            {
                if (GameMenu.IsSteamJoinLobbyResolvePending())
                    playerNames.Add(_ConnectionUI.SteamLobbyConnectingMarker);
                return playerNames;
            }

            var localName = GameMenu.Username;
            if (string.IsNullOrWhiteSpace(localName))
                localName = "Guest";

            var hasSnapshots = net.TryGetRemoteUserSnapshots(out var snapshots);
            try
            {
                var isHost = net.IsHost;
                var localId = net.id;
                const int hostId = 1;

                if (!net.HasRemote && !isHost)
                {
                    playerNames.Add("connecting...");
                    return playerNames;
                }

                if (isHost)
                {
                    playerNames.Add(localName + " (Host) (you)");
                }
                else
                {
                    string? hostName = null;
                    if (hasSnapshots)
                    {
                        for (int i = 0; i < snapshots.Count; i++)
                        {
                            var remote = snapshots[i];
                            if (remote.Id != hostId)
                                continue;

                            hostName = GetPlayerName(localId, remote.Id, remote.Username ?? string.Empty);
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(hostName))
                    {
                        var fallbackHost = GameMenu.RemoteUsername;
                        hostName = string.IsNullOrWhiteSpace(fallbackHost) ? "Host" : fallbackHost.Trim();
                    }
                    playerNames.Add(hostName + " (Host)");
                    playerNames.Add(localName + " (you)");
                }

                if (hasSnapshots)
                {
                    for (int i = 0; i < snapshots.Count; i++)
                    {
                        var remote = snapshots[i];
                        if (remote.Id == hostId)
                            continue;
                        if (!isHost && remote.Id == localId)
                            continue;
                        if (isHost && localId > 0 && remote.Id == localId)
                            continue;

                        string displayName = GetPlayerName(localId, remote.Id, remote.Username ?? string.Empty);
                        playerNames.Add(displayName);
                    }
                }

                return playerNames;
            }
            finally
            {
                if (hasSnapshots)
                    NetNode.ReleaseConsumedList(snapshots);
            }
        }


        public static string GetPlayerName(int localId, int remoteId, string remoteUsername)
        {
            if (ModEntry.TryGetClientIndex(localId, remoteId, out var slotIndex))
            {
                var displayName = ModEntry.GetClientLabel(slotIndex);

                if (string.IsNullOrWhiteSpace(displayName) ||
                    string.Equals(displayName, "Guest", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(remoteUsername))
                        displayName = remoteUsername.Trim();
                }

                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = "Guest";

                return displayName;
            }
            return string.IsNullOrWhiteSpace(remoteUsername) ? "Guest" : remoteUsername.Trim();
        }
        public static bool ShouldAutoHideConnectionUI(this TitleScreen titleScreen, bool visible)
        {
            ConnectionUI.set_visible = visible;
            return visible;
        }

    }
}

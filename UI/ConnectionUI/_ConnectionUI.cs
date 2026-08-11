using dc;
using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using ModCore.Utilities;
using System.Collections.Generic;
using System.Text;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.Connection
{
    public static class _ConnectionUI
    {
        /// <summary>Sentinel for <see cref="LobbySession.IsSteamJoinLobbyResolvePending"/>; displayed in ConnectionUI only.</summary>
        internal const string SteamLobbyConnectingMarker = "_steamLobbyConnecting";

        /// <summary>Fixed lobby preview capacity: 1 host + <see cref="NetNode.MaxClientSlots"/> clients.</summary>
        internal static int LobbySlotCount => NetNode.MaxClientSlots + 1;

        internal readonly struct LobbyPlayerSlot
        {
            public readonly bool Occupied;
            public readonly string Nick;
            public readonly string Skin;
            public readonly bool IsHost;
            public readonly bool IsYou;
            public readonly bool IsConnecting;

            public LobbyPlayerSlot(bool occupied, string nick, string skin, bool isHost, bool isYou, bool isConnecting)
            {
                Occupied = occupied;
                Nick = nick ?? string.Empty;
                Skin = string.IsNullOrWhiteSpace(skin) ? "PrisonerDefault" : skin.Trim();
                IsHost = isHost;
                IsYou = isYou;
                IsConnecting = isConnecting;
            }

            public static LobbyPlayerSlot Empty => new(false, string.Empty, "PrisonerDefault", false, false, false);
        }

        public static List<string> GetAllPlayerNames()
        {
            var slots = GetLobbyPlayerSlots();
            var playerNames = new List<string>(slots.Count);
            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (!slot.Occupied)
                    continue;

                if (slot.IsConnecting)
                {
                    playerNames.Add(slot.Nick);
                    continue;
                }

                var label = slot.Nick;
                if (slot.IsHost && slot.IsYou)
                    label += " (Host) (you)";
                else if (slot.IsHost)
                    label += " (Host)";
                else if (slot.IsYou)
                    label += " (you)";
                playerNames.Add(label);
            }

            return playerNames;
        }

        /// <summary>
        /// Four fixed lobby seats used by the beheaded players row.
        /// Slot 0 is always the host seat; remaining seats fill with connected peers.
        /// </summary>
        internal static List<LobbyPlayerSlot> GetLobbyPlayerSlots()
        {
            int capacity = LobbySlotCount;
            var slots = new List<LobbyPlayerSlot>(capacity);
            for (int i = 0; i < capacity; i++)
                slots.Add(LobbyPlayerSlot.Empty);

            var net = ModEntry._net;
            if (net == null)
            {
                if (LobbySession.IsSteamJoinLobbyResolvePending())
                {
                    slots[0] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: SteamLobbyConnectingMarker,
                        skin: "PrisonerDefault",
                        isHost: false,
                        isYou: true,
                        isConnecting: true);
                }
                return slots;
            }

            var localName = LobbySession.Username;
            if (string.IsNullOrWhiteSpace(localName))
                localName = "Guest";

            var localSkin = ResolveLocalHeroSkin();
            var hasSnapshots = net.TryGetRemoteUserSnapshots(out var snapshots);
            try
            {
                var isHost = net.IsHost;
                var localId = net.id;
                const int hostId = 1;

                if (!net.HasRemote && !isHost)
                {
                    slots[0] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: "connecting...",
                        skin: "PrisonerDefault",
                        isHost: false,
                        isYou: true,
                        isConnecting: true);
                    return slots;
                }

                if (isHost)
                {
                    slots[0] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: localName,
                        skin: localSkin,
                        isHost: true,
                        isYou: true,
                        isConnecting: false);

                    int write = 1;
                    if (hasSnapshots)
                    {
                        for (int i = 0; i < snapshots.Count && write < capacity; i++)
                        {
                            var remote = snapshots[i];
                            if (remote.Id == hostId)
                                continue;
                            if (localId > 0 && remote.Id == localId)
                                continue;

                            string displayName = GetPlayerName(localId, remote.Id, remote.Username ?? string.Empty);
                            string skin = ResolveRemoteSkin(localId, remote.Id);
                            slots[write++] = new LobbyPlayerSlot(
                                occupied: true,
                                nick: displayName,
                                skin: skin,
                                isHost: false,
                                isYou: false,
                                isConnecting: false);
                        }
                    }
                }
                else
                {
                    string hostName = "Host";
                    string hostSkin = "PrisonerDefault";
                    if (hasSnapshots)
                    {
                        for (int i = 0; i < snapshots.Count; i++)
                        {
                            var remote = snapshots[i];
                            if (remote.Id != hostId)
                                continue;

                            hostName = GetPlayerName(localId, remote.Id, remote.Username ?? string.Empty);
                            hostSkin = ResolveRemoteSkin(localId, remote.Id);
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(hostName) ||
                        string.Equals(hostName, "Guest", StringComparison.OrdinalIgnoreCase))
                    {
                        var fallbackHost = LobbySession.RemoteUsername;
                        if (!string.IsNullOrWhiteSpace(fallbackHost))
                            hostName = fallbackHost.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(hostSkin) ||
                        string.Equals(hostSkin, "PrisonerDefault", StringComparison.Ordinal))
                    {
                        var cachedHost = ModEntry.Instance?.remoteSkin;
                        if (!string.IsNullOrWhiteSpace(cachedHost))
                            hostSkin = cachedHost.Trim();
                    }

                    slots[0] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: hostName,
                        skin: hostSkin,
                        isHost: true,
                        isYou: false,
                        isConnecting: false);

                    slots[1] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: localName,
                        skin: localSkin,
                        isHost: false,
                        isYou: true,
                        isConnecting: false);

                    int write = 2;
                    if (hasSnapshots)
                    {
                        for (int i = 0; i < snapshots.Count && write < capacity; i++)
                        {
                            var remote = snapshots[i];
                            if (remote.Id == hostId || remote.Id == localId)
                                continue;

                            string displayName = GetPlayerName(localId, remote.Id, remote.Username ?? string.Empty);
                            string skin = ResolveRemoteSkin(localId, remote.Id);
                            slots[write++] = new LobbyPlayerSlot(
                                occupied: true,
                                nick: displayName,
                                skin: skin,
                                isHost: false,
                                isYou: false,
                                isConnecting: false);
                        }
                    }
                }

                return slots;
            }
            finally
            {
                if (hasSnapshots)
                    NetNode.ReleaseConsumedList(snapshots);
            }
        }

        /// <summary>Compact signature so lobby UI refreshes when nick OR skin changes.</summary>
        internal static string BuildLobbySlotsSignature(List<LobbyPlayerSlot> slots)
        {
            var sb = new StringBuilder(slots.Count * 24);
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (i > 0)
                    sb.Append('\u001f');
                sb.Append(s.Occupied ? '1' : '0');
                sb.Append('|');
                sb.Append(s.Nick);
                sb.Append('|');
                sb.Append(s.Skin);
                sb.Append('|');
                sb.Append(s.IsHost ? 'H' : '-');
                sb.Append(s.IsYou ? 'Y' : '-');
                sb.Append(s.IsConnecting ? 'C' : '-');
            }
            return sb.ToString();
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

        internal static string ResolveLocalHeroSkin()
        {
            try
            {
                User? user = null;
                try { user = Main.Class.ME?.user; } catch { }
                if (user == null)
                {
                    try { user = Game.Class.ME?.user; } catch { }
                }

                string? raw = null;
                try { raw = user?.heroSkin?.ToString(); } catch { }

                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cleaned = raw.Replace("|", "/").Trim();
                    try
                    {
                        var info = Cdb.Class.getSkinInfo(cleaned.AsHaxeString());
                        var cmd = info?.consoleCmdId?.ToString();
                        if (!string.IsNullOrWhiteSpace(cmd))
                            return cmd.Replace("|", "/").Trim();
                    }
                    catch
                    {
                    }

                    return cleaned;
                }
            }
            catch
            {
            }

            return "PrisonerDefault";
        }

        private static string ResolveRemoteSkin(int localId, int remoteId)
        {
            if (ModEntry.TryGetClientIndex(localId, remoteId, out var slotIndex))
            {
                if ((uint)slotIndex < (uint)ModEntry.clientSkins.Length)
                {
                    var known = ModEntry.clientSkins[slotIndex];
                    if (!string.IsNullOrWhiteSpace(known))
                        return known.Replace("|", "/").Trim();
                }
            }

            if (remoteId == 1)
            {
                var hostSkin = ModEntry.Instance?.remoteSkin;
                if (!string.IsNullOrWhiteSpace(hostSkin))
                    return hostSkin.Replace("|", "/").Trim();
            }

            if (ModEntry._net != null &&
                ModEntry._net.TryGetRemoteSkin(remoteId, out var netSkin) &&
                !string.IsNullOrWhiteSpace(netSkin))
            {
                return netSkin.Replace("|", "/").Trim();
            }

            return "PrisonerDefault";
        }
    }
}

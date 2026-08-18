using dc;
using dc.pr;
using dc.tool;
using dc.ui;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using Hashlink.Virtuals;
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
            public readonly string Head;
            public readonly bool IsHost;
            public readonly bool IsYou;
            public readonly bool IsConnecting;

            public LobbyPlayerSlot(bool occupied, string nick, string skin, string head, bool isHost, bool isYou, bool isConnecting)
            {
                Occupied = occupied;
                Nick = nick ?? string.Empty;
                Skin = string.IsNullOrWhiteSpace(skin) ? "PrisonerDefault" : skin.Trim();
                Head = string.IsNullOrWhiteSpace(head) ? "BaseFlame" : head.Trim();
                IsHost = isHost;
                IsYou = isYou;
                IsConnecting = isConnecting;
            }

            public static LobbyPlayerSlot Empty => new(false, string.Empty, "PrisonerDefault", "BaseFlame", false, false, false);
        }

        private static List<LobbyPlayerSlot>? _cachedLobbyPlayerSlots;
        private static string _cachedLobbyPlayerSlotsSignature = string.Empty;

        internal static void InvalidateLobbyPlayerSlots()
        {
            _cachedLobbyPlayerSlots = null;
            _cachedLobbyPlayerSlotsSignature = string.Empty;
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
            if (_cachedLobbyPlayerSlots != null)
                return _cachedLobbyPlayerSlots;

            int capacity = LobbySlotCount;
            var slots = new List<LobbyPlayerSlot>(capacity);
            for (int i = 0; i < capacity; i++)
                slots.Add(LobbyPlayerSlot.Empty);

            var net = ModEntry._net;
            var session = LobbySession.ReadSessionSnapshot();
            if (net == null)
            {
                if (session.SteamJoinLobbyResolvePending)
                {
                    slots[0] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: SteamLobbyConnectingMarker,
                        skin: "PrisonerDefault",
                        head: "BaseFlame",
                        isHost: false,
                        isYou: true,
                        isConnecting: true);
                }
                return CacheLobbyPlayerSlots(slots);
            }

            var localName = session.Username;
            if (string.IsNullOrWhiteSpace(localName))
                localName = "Guest";

            var localSkin = ResolveLocalHeroSkin();
            var localHead = ResolveLocalHeroHeadSkin();
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
                        head: "BaseFlame",
                        isHost: false,
                        isYou: true,
                        isConnecting: true);
                    return CacheLobbyPlayerSlots(slots);
                }

                if (isHost)
                {
                    slots[0] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: localName,
                        skin: localSkin,
                        head: localHead,
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
                            string head = ResolveRemoteHeadSkin(localId, remote.Id);
                            slots[write++] = new LobbyPlayerSlot(
                                occupied: true,
                                nick: displayName,
                                skin: skin,
                                head: head,
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
                    string hostHead = "BaseFlame";
                    if (hasSnapshots)
                    {
                        for (int i = 0; i < snapshots.Count; i++)
                        {
                            var remote = snapshots[i];
                            if (remote.Id != hostId)
                                continue;

                            hostName = GetPlayerName(localId, remote.Id, remote.Username ?? string.Empty);
                            hostSkin = ResolveRemoteSkin(localId, remote.Id);
                            hostHead = ResolveRemoteHeadSkin(localId, remote.Id);
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

                    if (string.IsNullOrWhiteSpace(hostHead) ||
                        string.Equals(hostHead, "BaseFlame", StringComparison.Ordinal))
                    {
                        var cachedHostHead = ModEntry.Instance?.remoteHeadSkin;
                        if (!string.IsNullOrWhiteSpace(cachedHostHead))
                            hostHead = cachedHostHead.Trim();
                    }

                    slots[0] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: hostName,
                        skin: hostSkin,
                        head: hostHead,
                        isHost: true,
                        isYou: false,
                        isConnecting: false);

                    slots[1] = new LobbyPlayerSlot(
                        occupied: true,
                        nick: localName,
                        skin: localSkin,
                        head: localHead,
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
                            string head = ResolveRemoteHeadSkin(localId, remote.Id);
                            slots[write++] = new LobbyPlayerSlot(
                                occupied: true,
                                nick: displayName,
                                skin: skin,
                                head: head,
                                isHost: false,
                                isYou: false,
                                isConnecting: false);
                        }
                    }
                }

                return CacheLobbyPlayerSlots(slots);
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
            if (ReferenceEquals(slots, _cachedLobbyPlayerSlots) &&
                !string.IsNullOrEmpty(_cachedLobbyPlayerSlotsSignature))
            {
                return _cachedLobbyPlayerSlotsSignature;
            }

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
                sb.Append(s.Head);
                sb.Append('|');
                sb.Append(s.IsHost ? 'H' : '-');
                sb.Append(s.IsYou ? 'Y' : '-');
                sb.Append(s.IsConnecting ? 'C' : '-');
            }
            return sb.ToString();
        }

        private static List<LobbyPlayerSlot> CacheLobbyPlayerSlots(List<LobbyPlayerSlot> slots)
        {
            _cachedLobbyPlayerSlots = slots;
            _cachedLobbyPlayerSlotsSignature = BuildLobbySlotsSignature(slots);
            return slots;
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

        private static string? _cachedLocalHeroSkin;
        private static string _cachedLocalHeroSkinSource = string.Empty;
        private static int _cachedLocalHeroSkinSlot = int.MinValue;
        private static int _tryLoadLocalHeroSkinSlot = int.MinValue;
        private static bool _preferCachedLocalHeroSkinForSlot;
        private static virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_? _cachedLocalSkinInfo;
        private static string? _cachedLocalHeroHeadSkin;

        internal static void RememberLocalHeroSkinFromUser(User? user, string source)
        {
            if (user == null)
                return;

            CacheLocalSkinInfoFromUser(user);
            RememberLocalHeroHeadSkin(ReadUserHeroHeadSkinId(user));

            if (!TryReadUserHeroSkinId(user, out var skin, out var via))
                return;

            RememberLocalHeroSkin(skin, source + "/" + via);
        }

        internal static void RememberLocalHeroSkin(string? skin, string source)
        {
            // Same identifier GameDataSync.SendHeroSkin sends: user.heroSkin, not consoleCmdId.
            var normalized = CleanHeroSkinId(skin);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            int slot = ResolveCurrentSaveSlot();
            bool changed = !string.Equals(_cachedLocalHeroSkin, normalized, StringComparison.Ordinal)
                || _cachedLocalHeroSkinSlot != slot;
            _cachedLocalHeroSkin = normalized;
            _cachedLocalHeroSkinSource = source ?? string.Empty;
            _cachedLocalHeroSkinSlot = slot;
            _ = changed;
        }

        internal static void RememberLocalHeroHeadSkin(string? head)
        {
            var normalized = CleanHeroSkinId(head);
            if (string.IsNullOrWhiteSpace(normalized))
                return;
            _cachedLocalHeroHeadSkin = normalized;
        }

        internal static void RefreshLocalHeroCosmeticsForSaveSlot()
        {
            InvalidateLobbyPlayerSlots();
            InvalidateLocalHeroSkinCacheIfSlotChanged();
            _tryLoadLocalHeroSkinSlot = int.MinValue;
            _preferCachedLocalHeroSkinForSlot = false;

            try
            {
                // TitleScreen.user can remain bound to the previous slot until the save-menu
                // transition completes. Save.tryLoad follows options.curSlot and is authoritative
                // immediately after a slot selection.
                var loaded = TryLoadLocalUserSkinOnce(forceReload: true);
                if (loaded != null)
                {
                    _preferCachedLocalHeroSkinForSlot = true;
                    return;
                }

                var user = TryResolveLocalUser(out _);
                if (user != null)
                    RememberLocalHeroSkinFromUser(user, "saveSlotChanged");
            }
            catch
            {
            }
        }

        internal static string ResolveLocalHeroSkin()
        {
            try
            {
                InvalidateLocalHeroSkinCacheIfSlotChanged();

                if (_preferCachedLocalHeroSkinForSlot &&
                    _cachedLocalHeroSkinSlot == ResolveCurrentSaveSlot() &&
                    !string.IsNullOrWhiteSpace(_cachedLocalHeroSkin))
                {
                    return _cachedLocalHeroSkin;
                }

                var user = TryResolveLocalUser(out var userSource);
                if (user == null)
                    TryLoadLocalUserSkinOnce();

                string? rawHeroSkin = null;
                string? infosCmd = null;
                string? infosRaw = null;
                if (user != null)
                    TryReadUserHeroSkinFields(user, out rawHeroSkin, out infosCmd, out infosRaw);

                string? chosen = null;
                string chosenSource = "fallback";
                if (TryPickPreferredSkin(infosCmd, infosRaw, rawHeroSkin, out chosen, out chosenSource))
                {
                    RememberLocalHeroSkin(chosen, userSource + "/" + chosenSource);
                }
                else if (!string.IsNullOrWhiteSpace(_cachedLocalHeroSkin))
                {
                    chosen = _cachedLocalHeroSkin;
                    chosenSource = "cache:" + _cachedLocalHeroSkinSource;
                }
                else if (_cachedLocalSkinInfo != null)
                {
                    chosen = "local-save";
                    chosenSource = "cachedSkinInfo";
                }

                if (!string.IsNullOrWhiteSpace(chosen))
                    return chosen;
            }
            catch
            {
            }

            return "PrisonerDefault";
        }

        private static User? TryResolveLocalUser(out string source)
        {
            source = "none";
            try
            {
                var screen = LobbySession.GetTitleScreen();
                if (screen?.user != null)
                {
                    source = "titleScreen";
                    return screen.user;
                }
            }
            catch
            {
            }

            try
            {
                var user = Main.Class.ME?.user;
                if (user != null)
                {
                    source = "main";
                    return user;
                }
            }
            catch
            {
            }

            try
            {
                var user = Game.Class.ME?.user;
                if (user != null)
                {
                    source = "game";
                    return user;
                }
            }
            catch
            {
            }

            return null;
        }

        private static User? TryLoadLocalUserSkinOnce(bool forceReload = false)
        {
            int slot = ResolveCurrentSaveSlot();
            if (!forceReload && !string.IsNullOrWhiteSpace(_cachedLocalHeroSkin) && _cachedLocalHeroSkinSlot == slot)
                return null;
            if (_tryLoadLocalHeroSkinSlot == slot)
                return null;

            _tryLoadLocalHeroSkinSlot = slot;
            try
            {
                var loaded = Save.Class.tryLoad.Invoke();
                if (loaded == null)
                    return null;

                RememberLocalHeroSkinFromUser(loaded, "tryLoad");
                return loaded;
            }
            catch
            {
                return null;
            }
        }

        private static void InvalidateLocalHeroSkinCacheIfSlotChanged()
        {
            int slot = ResolveCurrentSaveSlot();
            if (_cachedLocalHeroSkinSlot == int.MinValue || _cachedLocalHeroSkinSlot == slot)
                return;

            _cachedLocalHeroSkin = null;
            _cachedLocalHeroSkinSource = string.Empty;
            _cachedLocalHeroSkinSlot = int.MinValue;
            _cachedLocalSkinInfo = null;
            _cachedLocalHeroHeadSkin = null;
        }

        private static int ResolveCurrentSaveSlot()
        {
            try
            {
                var current = Main.Class.ME?.options?.curSlot;
                if (current.HasValue && current.Value >= 0)
                    return current.Value;
            }
            catch
            {
            }

            return 0;
        }

        private static bool TryReadUserHeroSkinId(User user, out string skin, out string via)
        {
            skin = string.Empty;
            via = "none";
            TryReadUserHeroSkinFields(user, out var rawHeroSkin, out var infosCmd, out var infosRaw);
            if (!TryPickPreferredSkin(infosCmd, infosRaw, rawHeroSkin, out var chosen, out via) ||
                string.IsNullOrWhiteSpace(chosen))
            {
                return false;
            }

            skin = chosen;
            return true;
        }

        private static void TryReadUserHeroSkinFields(
            User user,
            out string? rawHeroSkin,
            out string? infosCmd,
            out string? infosRaw)
        {
            rawHeroSkin = null;
            infosCmd = null;
            infosRaw = null;
            try { rawHeroSkin = user.heroSkin?.ToString(); } catch { }
            try
            {
                var infos = user.getHeroSkinInfos();
                if (infos == null)
                    return;
                try { infosRaw = infos.ToString(); } catch { }
                try { infosCmd = infos.consoleCmdId?.ToString(); } catch { }
            }
            catch
            {
            }
        }

        private static bool TryPickPreferredSkin(
            string? infosCmd,
            string? infosRaw,
            string? rawHeroSkin,
            out string chosen,
            out string via)
        {
            chosen = string.Empty;
            via = "fallback";

            // GameDataSync.SendHeroSkin / GhostKing both use user.heroSkin, not consoleCmdId.
            var heroSkin = CleanHeroSkinId(rawHeroSkin);
            if (!string.IsNullOrWhiteSpace(heroSkin))
            {
                chosen = heroSkin;
                via = "heroSkin";
                return true;
            }

            var rawInfos = CleanHeroSkinId(infosRaw);
            if (!string.IsNullOrWhiteSpace(rawInfos) && !IsConsoleCmdSkinId(rawInfos, infosCmd))
            {
                chosen = rawInfos;
                via = "getHeroSkinInfos";
                return true;
            }

            return false;
        }

        private static void CacheLocalSkinInfoFromUser(User user)
        {
            try
            {
                var infos = user.getHeroSkinInfos();
                if (infos != null)
                    _cachedLocalSkinInfo = infos;
            }
            catch
            {
            }
        }

        internal static bool TryGetCachedLocalSkinInfo(
            out virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_? skinInfo)
        {
            skinInfo = _cachedLocalSkinInfo;
            return skinInfo != null;
        }

        private static string CleanHeroSkinId(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            return raw.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        }

        private static bool IsConsoleCmdSkinId(string candidate, string? infosCmd)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(infosCmd))
                return false;
            return string.Equals(candidate.Trim(), infosCmd.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        internal static string ResolveLocalHeroHeadSkin()
        {
            try
            {
                InvalidateLocalHeroSkinCacheIfSlotChanged();

                if (_preferCachedLocalHeroSkinForSlot &&
                    _cachedLocalHeroSkinSlot == ResolveCurrentSaveSlot() &&
                    !string.IsNullOrWhiteSpace(_cachedLocalHeroHeadSkin))
                {
                    return _cachedLocalHeroHeadSkin;
                }

                var user = TryResolveLocalUser(out _);
                if (user == null)
                    TryLoadLocalUserSkinOnce();

                if (user != null)
                {
                    var fromUser = ReadUserHeroHeadSkinId(user);
                    if (!string.IsNullOrWhiteSpace(fromUser))
                    {
                        RememberLocalHeroHeadSkin(fromUser);
                        return fromUser;
                    }
                }

                if (!string.IsNullOrWhiteSpace(_cachedLocalHeroHeadSkin))
                    return _cachedLocalHeroHeadSkin;
            }
            catch
            {
            }

            return "BaseFlame";
        }

        private static string ReadUserHeroHeadSkinId(User user)
        {
            try
            {
                var raw = CleanHeroSkinId(user.heroHeadSkin?.ToString());
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw;
            }
            catch
            {
            }

            try
            {
                var infos = user.getHeroHeadSkinInfos();
                var item = CleanHeroSkinId(infos?.item?.ToString());
                if (!string.IsNullOrWhiteSpace(item))
                    return item;
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ResolveRemoteHeadSkin(int localId, int remoteId)
        {
            if (ModEntry.TryGetClientIndex(localId, remoteId, out var slotIndex))
            {
                if ((uint)slotIndex < (uint)ModEntry.clientHeadSkins.Length)
                {
                    var known = ModEntry.clientHeadSkins[slotIndex];
                    if (!string.IsNullOrWhiteSpace(known))
                        return known.Replace("|", "/").Trim();
                }
            }

            if (remoteId == 1)
            {
                var hostHead = ModEntry.Instance?.remoteHeadSkin;
                if (!string.IsNullOrWhiteSpace(hostHead))
                    return hostHead.Replace("|", "/").Trim();
            }

            return "BaseFlame";
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

using dc;
using dc.pr;
using dc.tool;
using dc.ui;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using Hashlink.Virtuals;
using ModCore.Events;
using ModCore.Utilities;
using Serilog;
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

        private static string? _cachedLocalHeroSkin;
        private static string _cachedLocalHeroSkinSource = string.Empty;
        private static int _cachedLocalHeroSkinSlot = int.MinValue;
        private static int _tryLoadLocalHeroSkinSlot = int.MinValue;
        private static virtual_colorMap_consoleCmdId_glowData_group_head_incompatibleHeads_item_model_onlyDefaultHead_scarfBlendMode_scarfs_? _cachedLocalSkinInfo;
        private static string _lastResolveLocalHeroSkinLog = string.Empty;

        internal static void RememberLocalHeroSkinFromUser(User? user, string source)
        {
            if (user == null)
                return;

            CacheLocalSkinInfoFromUser(user);

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

            if (changed)
            {
                Log.Information(
                    "[ConnectionUI] cached local hero skin={Skin} slot={Slot} via={Via}",
                    normalized,
                    slot,
                    _cachedLocalHeroSkinSource);
            }
        }

        internal static string ResolveLocalHeroSkin()
        {
            try
            {
                InvalidateLocalHeroSkinCacheIfSlotChanged();

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

                var logLine = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{userSource}|{user != null}|{rawHeroSkin}|{infosCmd}|{chosen}|{chosenSource}");
                if (!string.Equals(_lastResolveLocalHeroSkinLog, logLine, StringComparison.Ordinal))
                {
                    _lastResolveLocalHeroSkinLog = logLine;
                    Log.Information(
                        "[ConnectionUI] ResolveLocalHeroSkin user={UserSource} present={Present} heroSkin={HeroSkin} infosCmd={InfosCmd} infosRaw={InfosRaw} cache={Cache} chosen={Chosen} via={Via}",
                        userSource,
                        user != null,
                        rawHeroSkin ?? string.Empty,
                        infosCmd ?? string.Empty,
                        infosRaw ?? string.Empty,
                        _cachedLocalHeroSkin ?? string.Empty,
                        chosen ?? "PrisonerDefault",
                        chosenSource);
                }

                if (!string.IsNullOrWhiteSpace(chosen))
                    return chosen;
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] ResolveLocalHeroSkin failed: {Message}", ex.Message);
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

        private static void TryLoadLocalUserSkinOnce()
        {
            int slot = ResolveCurrentSaveSlot();
            if (!string.IsNullOrWhiteSpace(_cachedLocalHeroSkin) && _cachedLocalHeroSkinSlot == slot)
                return;
            if (_tryLoadLocalHeroSkinSlot == slot)
                return;

            _tryLoadLocalHeroSkinSlot = slot;
            try
            {
                var loaded = Save.Class.tryLoad.Invoke();
                if (loaded == null)
                {
                    Log.Information("[ConnectionUI] Save.tryLoad returned null slot={Slot}", slot);
                    return;
                }

                RememberLocalHeroSkinFromUser(loaded, "tryLoad");
            }
            catch (Exception ex)
            {
                Log.Warning("[ConnectionUI] Save.tryLoad for lobby skin failed slot={Slot}: {Message}", slot, ex.Message);
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
            catch (Exception ex)
            {
                Log.Information("[ConnectionUI] getHeroSkinInfos failed: {Message}", ex.Message);
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

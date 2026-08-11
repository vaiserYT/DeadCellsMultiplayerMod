using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using Newtonsoft.Json;
using ModCore.Utilities;
using Microsoft.Win32;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using ModCore.Modules;

namespace DeadCellsMultiplayerMod
{
    internal static partial class LobbySession
    {
        internal static void AbortClientWorldSync(string reason)
        {
            if (CurrentRole != NetRole.Client)
                return;

            var safeReason = string.IsNullOrWhiteSpace(reason) ? "authoritative world sync failed" : reason.Trim();
            MainThreadPump.EnqueueCriticalMainThreadCoalesced("game:abort-world-desync", () =>
            {
                if (CurrentRole != NetRole.Client)
                    return;

                _log?.Error("[NetMod][LevelSync] Aborting client session instead of continuing a divergent world: {Reason}", safeReason);
                try
                {
                    MultiplayerUI.PushSystemMessage(
                        Localize("Level failed to synchronize with the host. Return to the menu and rejoin."),
                        8.0,
                        1.0);
                }
                catch
                {
                }

                // Never keep playing after seed/graph authority has failed. Returning through the
                // normal world-exit path is safer than throwing from a Hashlink generation hook
                // and also guarantees the transport/save/serializer cleanup runs.
                HandleWorldExit();
            });
        }

        internal static void ForceExitToMainMenu()
        {
            try
            {
                var boot = dc.Boot.Class?.ME;
                if (boot != null)
                {
                    boot.returnToMainMenu();
                    return;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Boot.returnToMainMenu failed: {Message}", ex.Message);
            }

            try
            {
                var titleScreen = GetTitleScreen();
                if (titleScreen != null)
                {
                    titleScreen.mainMenu();
                    return;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] TitleScreen.mainMenu failed: {Message}", ex.Message);
            }

            try
            {
                var main = dc.Main.Class?.ME;
                if (main != null)
                    _ = main.onExit();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Main.onExit fallback failed: {Message}", ex.Message);
            }
        }

        internal static void ShowHostStatusMenu(TitleScreen screen)
        {
            if (_menuRebuildDepth > 0)
                return;
            _menuRebuildDepth++;
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                UiBegin();
                if (_menuTransport == ConnectionTransport.Steam)
                    UiInfo(Localize("Steam lobby — invite friends, then launch when everyone is Ready."), 0x9098A8);
                else
                    UiInfo(Localize("LAN lobby — waiting for players. Launch unlocks when everyone is Ready."), 0x9098A8);
                var multiplayerSaveLabel = GetMultiplayerSaveButtonLabel();
                var continueLabel = GetContinueButtonLabel(screen);
                var startLabel = GetStartNormalModeButtonLabel();
                var canLaunch = AllPlayersReady();
                var continueCompatible = IsHostContinueCompatible(out var continueBlockReason);
                var canContinue = canLaunch && continueCompatible;
                var disabledContinueReason = canLaunch ? continueBlockReason : "Not all players ready";
                UiButton(continueLabel, () => ContinueHostRun(screen), canContinue ? Localize("Continue the selected multiplayer save") : Localize(disabledContinueReason), canContinue);
                UiButton(startLabel, () => StartHostRunNormalMode(screen), GetText.Instance.GetString("Launch game"), canLaunch, 0x59D5FF);
                UiButton("Custom Mode", () => OpenHostCustomMode(screen), Localize("Configure and launch multiplayer custom mode"), canLaunch);
                UiButton(GetReadyButtonLabel(), () => ToggleLocalReadyFromMenu(screen), Localize("Toggle your ready state"));
                UiButton(multiplayerSaveLabel, () => OpenMultiplayerSlotMenu(screen), Localize("Choose multiplayer save slot"));
                if (_menuTransport == ConnectionTransport.Steam && _steamLobbyId != 0UL)
                {
                    UiButton(
                        Localize("Invite Steam friends"),
                        () => OpenSteamHostInviteOverlay(screen),
                        Localize("Open the Steam friends invite list"));
                }
                UiButton(GetText.Instance.GetString("Back"), () =>
                {
                    var transport = _menuTransport;
                    StopNetworkFromMenu();
                    SetRole(NetRole.None);
                    _menuSelection = NetRole.None;
                    // Return to the setup screen that led here — never hide ConnectionUI after Show*.
                    if (transport == ConnectionTransport.Lan)
                        ShowConnectionMenu(screen, NetRole.Host);
                    else
                        ShowHostTransportMenu(screen);
                    screen.ShouldAutoHideConnectionUI(true);
                }, GetText.Instance.GetString("Back to host setup"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                _inHostStatusMenu = true;
                _inClientWaitingMenu = false;
                UiCommit(showLobby: true);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open host status menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
                _menuRebuildDepth--;
            }
        }

        internal static void OpenSteamHostInviteOverlay(TitleScreen screen)
        {
            if (SteamConnect.TryOpenInviteOverlay(_steamLobbyId, out var error))
                return;

            _log?.Warning("[NetMod][Steam] Could not open lobby invite overlay: {Error}", error);
            ShowConnectionErrorPopup(
                screen,
                Localize("Steam invite failed"),
                Localize("Could not open the Steam invite list. Check console logs."),
                () => ShowHostStatusMenu(screen));
        }

        internal static void ShowClientWaitingMenu(TitleScreen screen)
        {
            if (_menuRebuildDepth > 0)
                return;
            _menuRebuildDepth++;
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                UiBegin();
                UiInfo(Localize("Connected — ready up and wait for the host to launch."), 0x9098A8);
                UiInfo($"Selected mode: {GetPendingLaunchSummaryLabel(screen)}", 0xE0E0E0);
                if (_menuTransport == ConnectionTransport.Steam)
                    UiInfo(Localize("Transport: Steam"), 0x8A93A6);
                else
                    UiInfo(Localize("Transport: LAN"), 0x8A93A6);
                UiButton(GetReadyButtonLabel(), () => ToggleLocalReadyFromMenu(screen), Localize("Toggle your ready state"));
                var multiplayerSaveLabel = GetMultiplayerSaveButtonLabel();
                UiButton(multiplayerSaveLabel, () => OpenMultiplayerSlotMenu(screen), Localize("Choose multiplayer save slot"));
                UiButton(
                    GetText.Instance.GetString("Disconnect"),
                    () =>
                    {
                        var transport = _menuTransport;
                        DisconnectFromMenu(screen, restoreTitle: false);
                        if (transport == ConnectionTransport.Lan)
                            ShowConnectionMenu(screen, NetRole.Client);
                        else
                            ShowJoinTransportMenu(screen);
                        screen.ShouldAutoHideConnectionUI(true);
                    },
                    GetText.Instance.GetString("Disconnect and return to join options"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                _inClientWaitingMenu = true;
                _inHostStatusMenu = false;
                UiCommit(showLobby: true);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open client waiting menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
                _menuRebuildDepth--;
            }
        }

        internal static void ShowLobbyNotFoundPopup(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                UiBegin();
                UiInfo(GetText.Instance.GetString("Can't find lobby"), 0xFF9090);
                UiButton(
                    GetText.Instance.GetString("OK"),
                    () => ShowConnectionMenu(screen, NetRole.Client),
                    GetText.Instance.GetString("Return to join menu"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                _inClientWaitingMenu = false;
                _inHostStatusMenu = false;
                UiCommit();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open lobby not found popup: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        internal static void DisconnectFromMenu(TitleScreen screen, bool restoreTitle = true)
        {
            StopNetworkFromMenu();
            ResetClientConnectState();
            _menuSelection = NetRole.None;
            ResetSteamState();
            _inHostStatusMenu = false;
            _inClientWaitingMenu = false;
            if (restoreTitle)
            {
                try
                {
                    ConnectionUI.DismissAndHide();
                    SetIsMainMenu(screen, false);
                    try { screen.clearMenu(); } catch { }
                    screen.mainMenu();
                }
                catch
                {
                    try { screen.mainMenu(); } catch { }
                }
            }
        }

        internal static void StopNetworkFromMenu()
        {
            ResetHostDisconnectCountdown();
            try
            {
                ModEntry.Instance?.StopNetworkFromMenu();
            }
            catch { }
            lock (Sync)
            {
                ResetLobbyLaunchStateLocked();
                ResetRemoteCoopStateLocked();
            }
            ResetLobbyReadyState();
            ResetSteamState();
        }

        internal static void EditUsername(TitleScreen screen)
        {
            OpenTextInput(screen, GetText.Instance.GetString("Username"), _username, value =>
            {
                var cleaned = CleanUsername(value);
                _username = cleaned;
                SaveConfig();
                SendUsernameToRemote();
                ShowConnectionMenu(screen, _menuSelection == NetRole.None ? NetRole.Host : _menuSelection);
            }, noSpaces: true);
        }

        public static void NotifyRemoteConnected(NetRole role)
        {
            ResetHostDisconnectCountdown();
            SendUsernameToRemote();
            SendLocalReadyState();
            SendCoopStateToRemote();

            if (role == NetRole.Host)
            {
                SendCachedDataToRemote();
                lock (Sync)
                {
                    if (_inActualRun)
                        SendCachedGeneratePayload();
                }
                ConnectionUI.NotifyConnectionsChanged();

                if (_menuSelection == NetRole.Host)
                {
                    var ts = GetTitleScreen();
                    if (ts != null) ShowHostStatusMenu(ts);
                }
            }
            else if (role == NetRole.Client)
            {
                ConnectionUI.NotifyConnectionsChanged();
                if (_menuSelection == NetRole.Client)
                {
                    var ts = GetTitleScreen();
                    if (ts != null) ShowClientWaitingMenu(ts);
                }
            }

            RequestLobbyMenuRefresh();
        }

        internal static void NotifyClientConnectAttempt(int attempt)
        {
            if (_menuSelection == NetRole.Client)
            {
                var ts = GetTitleScreen();
                if (ts != null) ShowClientWaitingMenu(ts);
            }
        }

        internal static void NotifyClientConnectFailed()
        {
            StopNetworkFromMenu();
            ResetClientConnectState();
            _menuSelection = NetRole.Client;

            var ts = GetTitleScreen();
            if (ts != null) ShowLobbyNotFoundPopup(ts);
        }

        public static void NotifyRemoteDisconnected(NetRole role)
        {
            // Clear launch ACK/queued/ready state before any reconnect can reuse this NetNode.
            // This matters when one peer restarts its process while the other peer stays alive.
            try { RunLaunchCoordinator.OnRemoteDisconnected(role); } catch { }
            try { ModEntry.Instance?.HandleNetworkDisconnectGhostCleanup(role); } catch { }

            if (role == NetRole.Host)
            {
                var disconnectedName = string.IsNullOrWhiteSpace(_remoteUsername) ? Localize("Guest") : _remoteUsername.Trim();
                MultiplayerUI.PushSystemMessage(FormatLocalized("{0} disconnected from the server.", disconnectedName));
                _remoteUsername = "guest";
                ResetLobbyReadyState();
                lock (Sync)
                {
                    ResetRemoteCoopStateLocked();
                }
                _genArrived = false;
                _seedArrived = false;
                if (_menuSelection == NetRole.Host)
                {
                    var ts = GetTitleScreen();
                    if (ts != null) ShowHostStatusMenu(ts);
                }

                MainThreadPump.EnqueueMainThreadCoalesced("ui:refresh-layout-after-disconnect", () => ConnectionUI.RefreshLayoutAfterDisconnect());
                RequestLobbyMenuRefresh();
                return;
            }

            bool wasInRun;
            lock (Sync)
            {
                wasInRun = _inActualRun;
                ResetLobbyLaunchStateLocked();
            }

            var saved = true;
            if (wasInRun)
                saved = TrySaveClientWorldBeforeHostAutoExit("host_disconnect");

            SetRole(NetRole.None);
            NetRef = null;
            _menuSelection = NetRole.None;
            ResetSteamState();
            ClearNetworkCaches();
            _inHostStatusMenu = false;
            _inClientWaitingMenu = false;
            _remoteUsername = "guest";
            ResetLobbyReadyState();
            _genArrived = false;
            MultiplayerUI.PushSystemMessage(Localize("Host disconnected from server."));
            if (wasInRun)
                StartHostDisconnectCountdown(savePending: !saved);

            MainThreadPump.EnqueueMainThreadCoalesced("ui:refresh-layout-after-disconnect", () => ConnectionUI.RefreshLayoutAfterDisconnect());
            RequestLobbyMenuRefresh();
        }

        internal static void SendUsernameToRemote()
        {
            var net = NetRef;
            if (net == null || !net.HasRemote) return;

            try
            {
                net.SendUsername(_username);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send username: {Message}", ex.Message);
            }
        }

        internal static void SendCachedDataToRemote()
        {
            var net = NetRef;
            if (net == null) return;

            try
            {
                var ld = GetCachedLevelDescSync();
                if (ld != null)
                    net.SendLevelDesc(JsonConvert.SerializeObject(ld));
                SendCoopStateToRemote();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to re-send LevelDesc: {Message}", ex.Message);
            }
        }

        internal static bool AllPlayersReady()
        {
            RefreshPlayersDisplayFromNetwork();
            if (_playersDisplay.Count == 0)
                return false;
            return _playersDisplay.All(p => p.Ready);
        }

        internal static void ClearNetworkCaches()
        {
            lock (Sync)
            {
                _cachedLevelDescSync = null;
                ResetLobbyLaunchStateLocked();
                ResetRemoteCoopStateLocked();
            }
        }

        internal static void ResetSteamState()
        {
            var lobbyId = _steamLobbyId;
            if (lobbyId != 0UL)
            {
                try { SteamConnect.LeaveLobby(lobbyId); } catch { }
            }
            try { SteamConnect.StopHostLobbyWorker(); } catch { }
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ConnectionUI.NotifyConnectionsChanged();
            if (_menuTransport == ConnectionTransport.Steam)
                _menuTransport = ConnectionTransport.Lan;
        }

        internal static void StartHostDisconnectCountdown(bool savePending = false)
        {
            var now = DateTime.UtcNow;
            _hostDisconnectCountdownActive = true;
            CaptureHostDisconnectCountdownGame();
            _hostDisconnectCountdownUntil = now.AddSeconds(HostDisconnectCountdownSeconds);
            _lastHostDisconnectCountdown = HostDisconnectCountdownSeconds;
            _hostDisconnectSavePending = savePending;
            _forceMultiplayerSaveStore = savePending;
            if (savePending)
            {
                _hostDisconnectSaveRetryAt = now.AddMilliseconds(HostDisconnectSaveRetryMs);
                _hostDisconnectSaveDeadline = now.AddSeconds(HostDisconnectSaveMaxSeconds);
            }
            else
            {
                _hostDisconnectSaveRetryAt = DateTime.MinValue;
                _hostDisconnectSaveDeadline = DateTime.MinValue;
            }
            MultiplayerUI.PushSystemMessage(FormatLocalized("Back to menu in {0}...", HostDisconnectCountdownSeconds));
        }

        internal static void ResetHostDisconnectCountdown()
        {
            _hostDisconnectCountdownActive = false;
            _hostDisconnectCountdownGameRef = null;
            _hostDisconnectCountdownUntil = DateTime.MinValue;
            _lastHostDisconnectCountdown = -1;
            _hostDisconnectSavePending = false;
            _forceMultiplayerSaveStore = false;
            _hostDisconnectSaveRetryAt = DateTime.MinValue;
            _hostDisconnectSaveDeadline = DateTime.MinValue;
        }

        internal static void UpdateHostDisconnectCountdown()
        {
            if (!_hostDisconnectCountdownActive)
                return;

            if (!IsHostDisconnectCountdownGameStillActive())
            {
                _log?.Information("[NetMod] Host-disconnect auto-exit canceled because client already left the run");
                ResetHostDisconnectCountdown();
                return;
            }

            var now = DateTime.UtcNow;
            if (_hostDisconnectSavePending && now >= _hostDisconnectSaveRetryAt)
            {
                if (TrySaveClientWorldBeforeHostAutoExit("host_disconnect_retry"))
                {
                    _hostDisconnectSavePending = false;
                    _forceMultiplayerSaveStore = false;
                }
                else
                {
                    _forceMultiplayerSaveStore = true;
                    _hostDisconnectSaveRetryAt = now.AddMilliseconds(HostDisconnectSaveRetryMs);
                }
            }

            var remaining = (int)Math.Ceiling((_hostDisconnectCountdownUntil - now).TotalSeconds);
            if (remaining < 0)
                remaining = 0;

            if (remaining != _lastHostDisconnectCountdown)
            {
                _lastHostDisconnectCountdown = remaining;
                MultiplayerUI.PushSystemMessage(FormatLocalized("Back to menu in {0}...", remaining));
            }

            if (remaining > 0)
                return;

            if (_hostDisconnectSavePending && now < _hostDisconnectSaveDeadline)
            {
                _hostDisconnectCountdownUntil = now.AddSeconds(1);
                return;
            }

            _hostDisconnectCountdownActive = false;
            _hostDisconnectCountdownGameRef = null;
            _hostDisconnectSavePending = false;
            _forceMultiplayerSaveStore = false;
            ForceExitToMainMenu();
        }

        internal static void CaptureHostDisconnectCountdownGame()
        {
            try
            {
                var game = dc.pr.Game.Class.ME ?? ModEntry.Instance?.game;
                _hostDisconnectCountdownGameRef = game == null ? null : new WeakReference<dc.pr.Game>(game);
            }
            catch
            {
                _hostDisconnectCountdownGameRef = null;
            }
        }

        internal static bool IsHostDisconnectCountdownGameStillActive()
        {
            var gameRef = _hostDisconnectCountdownGameRef;
            if (gameRef == null)
                return true;

            if (!gameRef.TryGetTarget(out var scheduledGame) || scheduledGame == null)
                return false;

            try
            {
                if (scheduledGame.destroyed)
                    return false;
            }
            catch
            {
                return false;
            }

            try
            {
                var currentGame = dc.pr.Game.Class.ME ?? ModEntry.Instance?.game;
                return ReferenceEquals(currentGame, scheduledGame);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TrySaveClientWorldBeforeHostAutoExit(string reason)
        {
            try
            {
                var main = dc.Main.Class.ME;
                var user = main?.user;
                if (main == null || user == null)
                    return true;

                // The client world is a transient reconstruction of host-owned graph/entity state.
                // Writing it during a disconnect was the most dangerous possible time: packets may
                // be incomplete and the host serializer identity may still be installed globally.
                // Restore the client's real user/serializer state and let the host-owned MSave stay
                // as the last known-good generation instead of overwriting it with partial state.
                GameDataSync.SwapToOriginalUserData(user);
                GameDataSync.ClearRemoteSerializerState(
                    restoreLocal: true,
                    reason: "client_host_disconnect_no_persist");
                _forceMultiplayerSaveStore = false;
                _log?.Information(
                    "[NetMod][SaveGuard] skipped non-authoritative client world write before host-disconnect auto-exit ({Reason})",
                    reason);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to restore local client state before host-disconnect auto-exit ({Reason}): {Message}", reason, ex.Message);
                return false;
            }
        }

        internal static bool TryValidateClientMultiplayerSave(out string error)
        {
            error = string.Empty;
            try
            {
                var savePath = GetMultiplayerSaveRelativeFilePath(null);
                if (!dc.tool.File.Class.exists.Invoke(MakeHLString(savePath)))
                {
                    error = "no multiplayer save";
                    return false;
                }

                // Same reasoning as TryResolveContinueUser: a readSave probe on an incompatible
                // save throws a HashlinkError that poisons the Haxe VM even though this catch
                // reports it, and the game fatals on a later tick. Presence is all this validation
                // needs; vanilla owns the real load when the player actually continues.
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static string Localize(string message)
        {
            return GetText.Instance.GetString(message);
        }

        internal static string FormatLocalized(string format, params object[] args)
        {
            var localizedFormat = Localize(format);
            try
            {
                return string.Format(CultureInfo.InvariantCulture, localizedFormat, args);
            }
            catch
            {
                return string.Format(CultureInfo.InvariantCulture, format, args);
            }
        }

        public static void ReceiveGeneratePayload(string json)
        {
            try
            {
                var payload = JsonConvert.DeserializeAnonymousType(json, new
                {
                    levelDesc = new LevelDescSync(),
                    rawDesc = string.Empty,
                    launchAction = string.Empty,
                    launchCustom = false,
                    launchStreamEnabled = false,
                    newCoopWorldPrepared = false,
                    coopId = string.Empty,
                    hostHasContinueSave = false
                });
                if (payload == null) return;

                if (payload.levelDesc != null && !IsChallengeLevel(payload.levelDesc.LevelId))
                {
                    CacheLevelDescSync(payload.levelDesc);
                    _log?.Information("[NetMod] Client cached LevelDescSync from generate payload");
                }

                if (!string.IsNullOrWhiteSpace(payload.rawDesc))
                {
                    _log?.Information("[NetMod] Client received raw LevelDesc: {Json}", payload.rawDesc);
                }

                ApplyReceivedPendingLaunch(payload.launchAction, payload.launchCustom, payload.launchStreamEnabled);
                if (!string.IsNullOrWhiteSpace(payload.coopId))
                    ReceiveRemoteCoopState(1, payload.coopId, payload.hostHasContinueSave);
                lock (Sync)
                {
                    _receivedLaunchPayload = true;
                    _receivedNewCoopWorldPrepared = payload.newCoopWorldPrepared;
                }
                TryStoreRemoteCoopIdForPendingNewGame();

                lock (Sync)
                {
                    if (_role == NetRole.Client && !_inActualRun)
                    {
                        _genArrived = true;
                        SignalClientLaunchProgressLocked();
                    }
                }

                RequestLobbyMenuRefresh();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to receive generate payload: {Message}", ex.Message);
            }
        }

        internal static bool IsChallengeLevel(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId)) return false;
            return levelId.IndexOf("challenge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal sealed class LevelDescSync
        {
            public string LevelId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int MapDepth { get; set; }
            public double MobDensity { get; set; }
            public int MinGold { get; set; }
            public double EliteRoomChance { get; set; }
            public double EliteWanderChance { get; set; }
            public int DoubleUps { get; set; }
            public int TripleUps { get; set; }
            public int QuarterUpsBC3 { get; set; }
            public int QuarterUpsBC4 { get; set; }
            public int WorldDepth { get; set; }
            public int BaseLootLevel { get; set; }
            public double BonusTripleScrollAfterBC { get; set; }
            public double CellBonus { get; set; }
            public int Group { get; set; }
        }

        internal sealed class MenuConfig
        {
            public string user { get; set; } = "guest";
            public string last_ip { get; set; } = "127.0.0.1";
            public int last_port { get; set; } = 1234;
            public string player_id { get; set; } = Guid.NewGuid().ToString("N");
        }

        internal sealed class PlayerInfo
        {
            public int UserId { get; set; }
            public string Name { get; set; } = "guest";
            public bool Ready { get; set; }
            public bool IsHost { get; set; }
        }

        internal static void LoadConfig()
        {
            try
            {
                var path = GetConfigPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var cfg = JsonConvert.DeserializeObject<MenuConfig>(json);
                    if (cfg != null)
                    {
                        _username = CleanUsername(string.IsNullOrWhiteSpace(cfg.user) ? GetDefaultUsername() : cfg.user);
                        _mpIp = string.IsNullOrWhiteSpace(cfg.last_ip) ? "127.0.0.1" : cfg.last_ip.Trim();
                        _mpPort = cfg.last_port <= 0 || cfg.last_port > 65535 ? 1234 : cfg.last_port;
                        _playerId = string.IsNullOrWhiteSpace(cfg.player_id) ? Guid.NewGuid().ToString("N") : cfg.player_id.Trim();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to load config: {Message}", ex.Message);
            }

            _username = CleanUsername(GetDefaultUsername());
            _mpIp = "127.0.0.1";
            _mpPort = 1234;
            _playerId = Guid.NewGuid().ToString("N");
            SaveConfig();
        }

        internal static void SaveConfig()
        {
            try
            {
                var path = GetConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var cfg = new MenuConfig
                {
                    user = _username,
                    last_ip = _mpIp,
                    last_port = _mpPort,
                    player_id = _playerId
                };
                var json = JsonConvert.SerializeObject(cfg, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to save config: {Message}", ex.Message);
            }
        }

        internal static string GetConfigPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var root = Directory.GetParent(baseDir)?.Parent?.Parent?.Parent?.FullName ?? baseDir;
            var dir = Path.Combine(root, "mods", "DeadCellsMultiplayerMod");
            return Path.Combine(dir, "config.json");
        }

        internal static string CleanUsername(string? value)
        {
            var cleaned = string.IsNullOrWhiteSpace(value) ? "guest" : value.Trim();
            cleaned = cleaned.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
            return cleaned.Length == 0 ? "guest" : cleaned;
        }

        internal static string GetDefaultUsername()
        {
            var steamName = TryGetSteamPersonaName();
            if (!string.IsNullOrWhiteSpace(steamName))
                return CleanUsername(steamName);
            try
            {
                var env = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(env))
                    return CleanUsername(env);
            }
            catch { }
            return "guest";
        }

        internal static string? TryGetSteamPersonaName()
        {
            string? steamPath = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key != null)
                {
                    steamPath = key.GetValue("SteamPath") as string;
                    if (string.IsNullOrWhiteSpace(steamPath))
                        steamPath = key.GetValue("InstallPath") as string;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(steamPath))
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                    if (key != null)
                        steamPath = key.GetValue("InstallPath") as string;
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(steamPath))
                return null;

            string loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersPath))
                return null;

            return TryParseMostRecentPersonaName(loginUsersPath);
        }

        internal static string? TryParseMostRecentPersonaName(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                int depth = 0;
                bool pendingUserBlock = false;
                bool inUserBlock = false;
                int userBlockDepth = 0;
                bool isMostRecent = false;
                string? personaCandidate = null;

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0)
                        continue;

                    if (!inUserBlock && depth == 1 && IsQuotedKeyOnly(line))
                    {
                        pendingUserBlock = true;
                        isMostRecent = false;
                        personaCandidate = null;
                    }

                    if (line.StartsWith("{", StringComparison.Ordinal))
                    {
                        depth++;
                        if (pendingUserBlock && depth == 2)
                        {
                            inUserBlock = true;
                            userBlockDepth = depth;
                            pendingUserBlock = false;
                        }
                        continue;
                    }

                    if (line.StartsWith("}", StringComparison.Ordinal))
                    {
                        if (inUserBlock && depth == userBlockDepth)
                        {
                            if (isMostRecent && !string.IsNullOrWhiteSpace(personaCandidate))
                                return personaCandidate;
                            inUserBlock = false;
                            personaCandidate = null;
                            isMostRecent = false;
                        }
                        depth = Math.Max(0, depth - 1);
                        continue;
                    }

                    if (!inUserBlock)
                        continue;

                    if (TryParseVdfPair(line, out var key, out var value))
                    {
                        if (key.Equals("PersonaName", StringComparison.OrdinalIgnoreCase))
                            personaCandidate = value;
                        else if (key.Equals("MostRecent", StringComparison.OrdinalIgnoreCase))
                            isMostRecent = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }

            return null;
        }

        internal static bool IsQuotedKeyOnly(string line)
        {
            if (!line.StartsWith("\"", StringComparison.Ordinal))
                return false;
            int secondQuote = line.IndexOf('"', 1);
            if (secondQuote < 0)
                return false;
            int thirdQuote = line.IndexOf('"', secondQuote + 1);
            return thirdQuote < 0;
        }

        internal static bool TryParseVdfPair(string line, out string key, out string value)
        {
            key = string.Empty;
            value = string.Empty;
            if (!line.StartsWith("\"", StringComparison.Ordinal))
                return false;
            int keyEnd = line.IndexOf('"', 1);
            if (keyEnd < 0)
                return false;
            int valueStart = line.IndexOf('"', keyEnd + 1);
            if (valueStart < 0)
                return false;
            int valueEnd = line.IndexOf('"', valueStart + 1);
            if (valueEnd < 0)
                return false;
            key = line.Substring(1, keyEnd - 1);
            value = line.Substring(valueStart + 1, valueEnd - valueStart - 1);
            return true;
        }

        internal static void ResetClientConnectState()
        {
            lock (Sync)
            {
                _pendingClientRestartSeed = null;
                _pendingClientRestartReason = string.Empty;
            }
        }

        internal static void HandleTextInputClipboardShortcuts()
        {
            var textInput = GetActiveTextInput();
            if (textInput == null)
                return;

            if (!IsTextInputActive(textInput))
            {
                ClearActiveTextInput();
                return;
            }

            if (_activeTextInputNoSpaces)
                RemoveSpacesFromTextInput(textInput);

            if (dc.hxd.Key.Class.isPressed(KeyEsc))
            {
                try
                {
                    textInput.cancel();
                }
                finally
                {
                    ClearActiveTextInput();
                }
                return;
            }

            if (dc.hxd.Key.Class.isPressed(KeySpace))
            {
                try
                {
                    textInput.validate();
                }
                finally
                {
                    ClearActiveTextInput();
                }
                return;
            }

            if (!IsCtrlDown())
                return;

            if (dc.hxd.Key.Class.isPressed(KeyC))
            {
                if (TryGetTextInputValue(textInput, out var text))
                    TrySetClipboardText(text);
                return;
            }

            if (dc.hxd.Key.Class.isPressed(KeyV))
            {
                var clip = TryGetClipboardText();
                if (!string.IsNullOrEmpty(clip))
                {
                    if (_activeTextInputNoSpaces)
                        clip = RemoveSpaces(clip);
                    TrySetTextInputValue(textInput, clip);
                }
            }
        }

        internal static bool IsCtrlDown()
        {
            return dc.hxd.Key.Class.isDown(KeyCtrl) || dc.hxd.Key.Class.isDown(KeyLCtrl) || dc.hxd.Key.Class.isDown(KeyRCtrl);
        }

        internal static void RegisterActiveTextInput(TextInput input, bool noSpaces)
        {
            lock (TextInputSync)
            {
                _activeTextInputRef = new WeakReference<TextInput?>(input);
                _activeTextInputNoSpaces = noSpaces;
            }
        }

        internal static void ClearActiveTextInput()
        {
            lock (TextInputSync)
            {
                _activeTextInputRef = null;
                _activeTextInputNoSpaces = false;
            }
        }

        internal static TextInput? GetActiveTextInput()
        {
            lock (TextInputSync)
            {
                if (_activeTextInputRef != null && _activeTextInputRef.TryGetTarget(out var input))
                    return input;
            }

            return null;
        }

        internal static bool IsTextInputActive(TextInput input)
        {
            var active = GetMemberValue(input, "isActive", true) ?? GetMemberValue(input, "active", true);
            if (active is bool activeBool)
                return activeBool;

            var visible = GetMemberValue(input, "visible", true) ?? GetMemberValue(input, "isVisible", true);
            if (visible is bool visibleBool)
                return visibleBool;

            var target = GetTextInputTarget(input);
            var focused = GetMemberValue(target, "hasFocus", true) ?? GetMemberValue(target, "focused", true);
            if (focused is bool focusedBool)
                return focusedBool;

            return true;
        }

        internal static object? GetTextInputTarget(TextInput input)
        {
            return GetMemberValue(input, "input", true)
                ?? GetMemberValue(input, "textInput", true)
                ?? GetMemberValue(input, "textField", true)
                ?? input;
        }

        internal static bool TryGetTextInputValue(TextInput input, out string text)
        {
            text = string.Empty;
            var target = GetTextInputTarget(input);
            if (target == null)
                return false;

            var value = GetMemberValue(target, "text", true)
                ?? GetMemberValue(target, "value", true)
                ?? GetMemberValue(target, "str", true);
            if (value == null)
                return false;

            if (value is dc.String ds)
            {
                text = ds.ToString() ?? string.Empty;
                return true;
            }

            text = value.ToString() ?? string.Empty;
            return true;
        }

        internal static bool TrySetTextInputValue(TextInput input, string text)
        {
            var target = GetTextInputTarget(input);
            if (target == null)
                return false;

            if (TryInvokeTextInputSetter(target, MakeHLString(text))
                || TryInvokeTextInputSetter(target, text))
                return true;

            return TrySetMember(target, "text", MakeHLString(text))
                || TrySetMember(target, "value", MakeHLString(text))
                || TrySetMember(target, "str", MakeHLString(text))
                || TrySetMember(target, "text", text)
                || TrySetMember(target, "value", text)
                || TrySetMember(target, "str", text);
        }

        internal static bool TryInvokeTextInputSetter(object target, object value)
        {
            var type = target.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            foreach (var name in new[] { "setText", "set_text", "setValue", "set_value" })
            {
                var method = type.GetMethod(name, flags);
                if (method == null)
                    continue;

                try
                {
                    method.Invoke(target, new[] { value });
                    return true;
                }
                catch
                {
                    // Try next setter.
                }
            }

            return false;
        }

        internal static void RemoveSpacesFromTextInput(TextInput input)
        {
            if (!TryGetTextInputValue(input, out var text))
                return;

            if (!text.Contains(' ', StringComparison.Ordinal))
                return;

            TrySetTextInputValue(input, RemoveSpaces(text));
        }

        internal static string RemoveSpaces(string value)
        {
            return value.Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        internal static string? TryGetClipboardText()
        {
            try
            {
                if (!IsClipboardFormatAvailable(CfUnicodeText))
                    return null;
                if (!OpenClipboard(IntPtr.Zero))
                    return null;

                try
                {
                    var handle = GetClipboardData(CfUnicodeText);
                    if (handle == IntPtr.Zero)
                        return null;

                    var ptr = GlobalLock(handle);
                    if (ptr == IntPtr.Zero)
                        return null;

                    try
                    {
                        return Marshal.PtrToStringUni(ptr);
                    }
                    finally
                    {
                        GlobalUnlock(handle);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return null;
            }
        }

        internal static bool TrySetClipboardText(string text)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero))
                    return false;

                try
                {
                    if (!EmptyClipboard())
                        return false;

                    var bytes = (text.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
                    if (hGlobal == IntPtr.Zero)
                        return false;

                    var target = GlobalLock(hGlobal);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(hGlobal);
                    }

                    if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
                    {
                        GlobalFree(hGlobal);
                        return false;
                    }

                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            catch
            {
                return false;
            }
        }

        internal static void OpenTextInput(TitleScreen screen, string title, string initial, Action<string> onValidate, bool noSpaces = false)
        {
            try
            {
                ClearActiveTextInput();
                if (noSpaces && initial.Contains(' ', StringComparison.Ordinal))
                    initial = RemoveSpaces(initial);
                var initialText = initial ?? string.Empty;
                // Custom ConnectionUI prompt (stock TextInput dialog is too ugly for the hub).
                ConnectionUI.ShowTextPrompt(
                    title,
                    initialText,
                    value =>
                    {
                        var text = value ?? string.Empty;
                        if (noSpaces)
                            text = RemoveSpaces(text);
                        onValidate(text);
                    },
                    onCancel: null,
                    noSpaces: noSpaces);
            }
            catch (Exception ex)
            {
                ClearActiveTextInput();
                _log?.Warning("[NetMod] Failed to open text input: {Message}", ex.Message);
            }
        }

        internal static void TryAddMenuButton(TitleScreen screen, string label, Action onClick, string? help = null, int? textColor = null)
        {
            try
            {
                AddMenuButton(screen, label, onClick, help, isEnabled: null, textColor: textColor);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Menu add failed for {Label}: {Message}", label, ex.Message);
            }
        }

        /// <summary>
        /// Shared menu-button helper. <paramref name="textColor"/> is opt-in and defaults to the
        /// vanilla white: this method builds Host game, Join game, Ready, Disconnect, OK, Back and
        /// most other entries, so a hardcoded accent here would recolour the entire mod UI.
        /// </summary>
        internal static void AddMenuButton(TitleScreen screen, string label, Action onClick, string? help = null, bool? isEnabled = null, int? textColor = null)
        {
            var cb = new HlAction(onClick);
            var labelStr = MakeHLString(label);
            var helpStr = MakeHLString(help ?? string.Empty);
            int colorVal = textColor ?? 0xFFFFFF;
            var color = Ref<int>.From(ref colorVal);
            screen.addMenu(labelStr, cb, helpStr, isEnabled, color);
        }

        internal static void AddInfoLine(TitleScreen screen, string text, int? infoColor = null)
        {
            int colorVal = infoColor ?? 0xFFFFFF;
            var labelStr = MakeHLString(text);
            var helpStr = MakeHLString(string.Empty);
            var color = Ref<int>.From(ref colorVal);
            var cb = new HlAction(() => { });
            screen.addMenu(labelStr, cb, helpStr, false, color);
        }

        // ---------------------------------------------------------------- ConnectionUI routing

        /// <summary>Starts a ConnectionUI screen (clears the pending model, makes the hub visible).</summary>
        internal static void UiBegin()
        {
            ConnectionUI.BeginMenu();
        }

        /// <summary>Adds a pretty button to the current ConnectionUI screen.</summary>
        internal static void UiButton(string label, Action onClick, string? help = null, bool? isEnabled = null, int? textColor = null, bool fieldStyle = false)
        {
            ConnectionUI.AddPendingButton(label, help ?? string.Empty, isEnabled ?? true, textColor ?? 0xFFFFFF, onClick, fieldStyle);
        }

        /// <summary>Adds an informational line to the current ConnectionUI screen.</summary>
        internal static void UiInfo(string text, int? infoColor = null)
        {
            ConnectionUI.AddPendingInfo(text, infoColor ?? 0xFFFFFF);
        }

        /// <summary>Renders the current ConnectionUI screen.</summary>
        internal static void UiCommit(bool showLobby = false, bool hubLayout = false)
        {
            ConnectionUI.CommitMenu(showLobby, hubLayout);
        }

        /// <summary>
        /// Leaves the multiplayer hub and forces a real TitleScreen main-menu rebuild.
        /// ShowMultiplayerMenu clears menu items then restores isMainMenu=true, so a bare
        /// <c>mainMenu()</c> can no-op and leave an empty title under a leftover hub.
        /// </summary>
        internal static void ReturnFromMultiplayerHubToTitle(TitleScreen screen)
        {
            try
            {
                StopNetworkFromMenu();
                ConnectionUI.DismissAndHide();
                // Force TitleScreen to treat this as a fresh main-menu build.
                SetIsMainMenu(screen, false);
                try { screen.clearMenu(); } catch { }
                screen.mainMenu();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Return to title failed: {Message}", ex.Message);
                try
                {
                    ConnectionUI.DismissAndHide();
                    SetIsMainMenu(screen, false);
                    screen.mainMenu();
                }
                catch { }
            }
        }

        /// <summary>Shows the ConnectionUI lobby display (player list + lobby code).</summary>
        internal static void UiLobby()
        {
            ConnectionUI.ShowLobbyMode();
        }

        internal static object? GetMemberValue(object? obj, string name, bool ignoreCase)
        {
            if (obj == null || string.IsNullOrWhiteSpace(name)) return null;

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = obj.GetType();
            var flags = ignoreCase ? Flags | BindingFlags.IgnoreCase : Flags;
            try
            {
                var prop = type.GetProperty(name, flags);
                if (prop != null) return prop.GetValue(obj);

                var field = type.GetField(name, flags);
                if (field != null) return field.GetValue(obj);
            }
            catch { }

            return null;
        }

        internal static bool TrySetMember(object? obj, string name, object? value)
        {
            if (obj == null || string.IsNullOrWhiteSpace(name)) return false;

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = obj.GetType();
            try
            {
                var prop = type.GetProperty(name, Flags);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(obj, value);
                    return true;
                }

                var field = type.GetField(name, Flags);
                if (field != null)
                {
                    field.SetValue(obj, value);
                    return true;
                }
            }
            catch { }

            return false;
        }

        internal static dc.String MakeHLString(string value)
        {
            return value.AsHaxeString();
        }

        internal static bool GetIsMainMenu(TitleScreen screen)
        {
            try
            {
                var val = GetMemberValue(screen, "isMainMenu", true);
                if (val is bool b) return b;
            }
            catch { }
            return false;
        }

        internal static void SetIsMainMenu(TitleScreen screen, bool value)
        {
            try
            {
                TrySetMember(screen, "isMainMenu", value);
            }
            catch { }
        }

        internal static int GetArrayLength(object arrObj)
        {
            try
            {
                var lenObj = GetMemberValue(arrObj, "length", true);
                if (lenObj is IConvertible conv)
                    return conv.ToInt32(null);
            }
            catch { }
            return 0;
        }

        internal static int FindMenuIndexByLabel(object? arrObj, string label)
        {
            if (arrObj == null) return -1;
            try
            {
                var type = arrObj.GetType();
                var getDyn = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDyn == null) return -1;

                int len = GetArrayLength(arrObj);
                for (int i = 0; i < len; i++)
                {
                    var item = getDyn.Invoke(arrObj, new object[] { i });
                    var text = GetMenuLabel(item);
                    if (text.Equals(label, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch { }
            return -1;
        }

        internal static string GetMenuLabel(object? menuItem)
        {
            if (menuItem == null) return string.Empty;

            try
            {
                var t = GetMemberValue(menuItem, "t", true);
                if (t is dc.String ds)
                    return ds.ToString() ?? string.Empty;

                var textValue = GetMemberValue(t ?? menuItem, "text", true)
                             ?? GetMemberValue(t ?? menuItem, "str", true);
                if (textValue != null)
                    return textValue.ToString() ?? string.Empty;

                return t?.ToString() ?? menuItem.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static void RemoveMenuItems(TitleScreen screen, params string[] labels)
        {
            if (labels.Length == 0) return;
            var arrObj = GetMemberValue(screen, "menuItems", true);
            if (arrObj == null) return;

            try
            {
                var type = arrObj.GetType();
                var getDyn = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var removeDyn = type.GetMethod("removeDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? type.GetMethod("remove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDyn == null || removeDyn == null) return;

                var targets = new System.Collections.Generic.List<object>();
                int len = GetArrayLength(arrObj);
                for (int i = 0; i < len; i++)
                {
                    var item = getDyn.Invoke(arrObj, new object[] { i });
                    if (item == null)
                        continue;
                    var label = GetMenuLabel(item);
                    foreach (var l in labels)
                    {
                        if (label.Equals(l, StringComparison.OrdinalIgnoreCase))
                        {
                            targets.Add(item);
                            break;
                        }
                    }
                }

                foreach (var it in targets)
                {
                    removeDyn.Invoke(arrObj, new object[] { it });
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to clean menu items: {Message}", ex.Message);
            }
        }

        internal static void RemoveDuplicatesKeepFirst(TitleScreen screen, params string[] labels)
        {
            if (labels.Length == 0) return;
            var arrObj = GetMemberValue(screen, "menuItems", true);
            if (arrObj == null) return;

            try
            {
                var type = arrObj.GetType();
                var getDyn = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var removeDyn = type.GetMethod("removeDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? type.GetMethod("remove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDyn == null || removeDyn == null) return;

                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var toRemove = new System.Collections.Generic.List<object>();

                int len = GetArrayLength(arrObj);
                for (int i = 0; i < len; i++)
                {
                    var item = getDyn.Invoke(arrObj, new object[] { i });
                    if (item == null)
                        continue;
                    var label = GetMenuLabel(item);
                    foreach (var l in labels)
                    {
                        if (label.Equals(l, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!seen.Add(label))
                                toRemove.Add(item);
                            break;
                        }
                    }
                }

                foreach (var it in toRemove)
                {
                    removeDyn.Invoke(arrObj, new object[] { it });
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to clean duplicate menu items: {Message}", ex.Message);
            }
        }


        internal static void StoreTitleScreen(TitleScreen ts)
        {
            _titleScreenRef = new WeakReference<TitleScreen?>(ts);
        }

        internal static TitleScreen? GetTitleScreen()
        {
            if (_titleScreenRef != null && _titleScreenRef.TryGetTarget(out var ts))
                return ts;
            return null;
        }
    }
}

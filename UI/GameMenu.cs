using System.Runtime.InteropServices;
using System.Globalization;
using System.Reflection;
using dc.pr;
using dc.ui;
using Newtonsoft.Json;
using Serilog;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.PortableCore;
using DeadCellsMultiplayerMod.Tools;
using ModCore.Modules;


namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private static readonly object Sync = new();
        private static ILogger? _log;
        private static NetRole _role = NetRole.None;
        private static bool _inActualRun;
        private static int? _serverSeed;
        private static int? _remoteSeed;
        private static int? _pendingClientRestartSeed;
        private static string _pendingClientRestartReason = string.Empty;
        private const int MaxSeed = 999_999;
        public static NetNode? NetRef { get; set; }

        private static bool _menuHooksAttached;
        private static bool _addMenuHookRegistered;
        private static WeakReference<TitleScreen?>? _titleScreenRef;
        private static string _mpIp = "127.0.0.1";
        private static int _mpPort = 1234;
        private static NetRole _menuSelection = NetRole.None;
        private enum ConnectionTransport
        {
            Lan,
            Steam
        }
        private static ConnectionTransport _menuTransport = ConnectionTransport.Lan;
        private static ulong _steamLobbyId;
        private static string _steamLobbyCode = string.Empty;
        private static ulong _steamHostSteamId;
        private static bool _steamJoinLobbyResolvePending;
        private static ulong? _pendingOverlayJoinLobbyId;
        internal const int ClientConnectMaxAttempts = 3;
        private static bool _pendingAutoStart;
        private static bool _autoStartTriggered;
        private static bool _continueLaunchInProgress;
        private static DateTime _continueLaunchStartedAt = DateTime.MinValue;
        private const int ContinueLaunchGuardMs = 6000;
        private static DateTime _autoStartRetryAt = DateTime.MinValue;
        private const int DeathRestartCooldownMs = 1000;
        private static DateTime _deathRestartCooldownUntil = DateTime.MinValue;
        private const string AutoStartMutexName = "DeadCellsMultiplayerMod.AutoStart";
        private static bool _mainMenuButtonAdded;
        private static bool _suppressAutoButton;
        private static bool _worldExitHandled;
        private static bool _hostDisconnectCountdownActive;
        private static WeakReference<dc.pr.Game>? _hostDisconnectCountdownGameRef;
        private static DateTime _hostDisconnectCountdownUntil = DateTime.MinValue;
        private static int _lastHostDisconnectCountdown = -1;
        private const int HostDisconnectCountdownSeconds = 5;
        private static bool _hostDisconnectSavePending;
        private static DateTime _hostDisconnectSaveRetryAt = DateTime.MinValue;
        private static DateTime _hostDisconnectSaveDeadline = DateTime.MinValue;
        private const int HostDisconnectSaveRetryMs = 500;
        private const int HostDisconnectSaveMaxSeconds = 10;
        private static bool _seedArrived;
        private static string _username = "guest";
        private static string _remoteUsername = "guest";
        private static string _playerId = Guid.NewGuid().ToString("N");
        public static string Username => _username;
        public static string RemoteUsername => _remoteUsername;

        internal static string GetSteamLobbyCodeForUi()
        {
            if (!string.IsNullOrWhiteSpace(_steamLobbyCode))
                return _steamLobbyCode;

            if (_steamLobbyId > 0)
                return SteamConnect.BuildLobbyCodeFromLobbyId(_steamLobbyId);

            return string.Empty;
        }

        internal static bool TryCopySteamLobbyCodeFromUi()
        {
            var code = GetSteamLobbyCodeForUi();
            if (string.IsNullOrWhiteSpace(code))
                return false;

            return SteamConnect.TryCopyLobbyCodeToClipboard(code);
        }

        /// <summary>True while clipboard/overlay join is resolving the Steam lobby (before <see cref="ApplySteamJoinResult"/>).</summary>
        internal static bool IsSteamJoinLobbyResolvePending() => _steamJoinLobbyResolvePending;
        private static bool _localReady;
        private static List<PlayerInfo> _playersDisplay = new();
        private static bool _inHostStatusMenu;
        private static bool _inClientWaitingMenu;
        /// <summary>Prevents nested host/client status menu rebuilds when addMenu hook runs ProcessMainThreadQueue before orig.</summary>
        private static int _menuRebuildDepth;
        private static bool _genArrived;
        private static LevelDescSync? _cachedLevelDescSync;
        private static readonly object TextInputSync = new();
        private static WeakReference<TextInput?>? _activeTextInputRef;
        private static bool _activeTextInputNoSpaces;
        private const int KeyCtrl = 17;
        private const int KeyLCtrl = 162;
        private const int KeyRCtrl = 163;
        private const int KeyC = 67;
        private const int KeyV = 86;
        private const int KeySpace = 32;
        private const int KeyEsc = 27;
        // Win32 clipboard helpers for text input shortcuts.
        private const uint CfUnicodeText = 13;
        private const uint GmemMoveable = 0x0002;
        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        public static void Initialize(ILogger logger)
        {
            logger.Information("\x1b[32m[[ModEntry.GameMenu] Initializing GameMenu...]\x1b[0m ");
            InitializeRunLaunchHandshake(logger);
            lock (Sync)
            {
                _log = logger;
                _role = NetRole.None;
                _inActualRun = false;
                _serverSeed = null;
                _remoteSeed = null;
                _pendingClientRestartSeed = null;
                _pendingClientRestartReason = string.Empty;
                _pendingAutoStart = false;
                _autoStartTriggered = false;
                _continueLaunchInProgress = false;
                _continueLaunchStartedAt = DateTime.MinValue;
                _genArrived = false;
                _seedArrived = false;
                _deathRestartCooldownUntil = DateTime.MinValue;
                _cachedLevelDescSync = null;
                _hostDisconnectCountdownActive = false;
                _hostDisconnectCountdownGameRef = null;
                _hostDisconnectCountdownUntil = DateTime.MinValue;
                _lastHostDisconnectCountdown = -1;
                _hostDisconnectSavePending = false;
                _hostDisconnectSaveRetryAt = DateTime.MinValue;
                _hostDisconnectSaveDeadline = DateTime.MinValue;
                _menuTransport = ConnectionTransport.Lan;
                _steamLobbyId = 0;
                _steamLobbyCode = string.Empty;
                _steamHostSteamId = 0UL;
                _pendingLaunchAction = PendingLaunchAction.NewGame;
                _pendingLaunchCustom = false;
                _pendingLaunchStreamEnabled = false;
                _hasAuthoritativePendingNewGameLaunch = false;
                _authoritativePendingNewGameCustom = false;
                _authoritativePendingNewGameStreamEnabled = false;
                ResetRemoteCoopStateLocked();
                _receivedNewCoopWorldPrepared = false;
                ResetLobbyReadyStateLocked();
                InvalidateGeneratePayloadCacheLocked();
                ResetRunLaunchCompatStateLocked();
                ResetMainThreadQueuesLocked();
                ResetClientLaunchSessionLocked();
            }

            InitializeMenuUiHooks();
        }

        public static void MarkInRun()
        {
            lock (Sync)
            {
                _inActualRun = true;
                _continueLaunchInProgress = false;
                _continueLaunchStartedAt = DateTime.MinValue;
                MarkClientLaunchInRunLocked();
            }
            ClearClientRestartPending();
        }

        internal static bool IsClientInActualRun()
        {
            lock (Sync)
            {
                return _role == NetRole.Client && _inActualRun;
            }
        }

        internal static bool IsInActualRun()
        {
            lock (Sync)
            {
                return _inActualRun;
            }
        }

        public static void SetRole(NetRole role)
        {
            var previous = _role;
            lock (Sync)
            {
                _role = role;
                if (role == NetRole.None)
                    ClearStructuredLaunchFlagsLocked();
            }
            RunLaunchCoordinator.OnRoleChanged(previous, role);
            if (previous == NetRole.Client && role != NetRole.Client)
            {
                GameDataSync.SwapToLocalSerializerSync();
                EnqueueCriticalMainThreadCoalesced("game:restore-original-user", () =>
                {
                    try
                    {
                        var main = dc.Main.Class.ME;
                        if (main?.user != null)
                            GameDataSync.RestoreOriginalUserState(main.user, true);
                    }
                    catch
                    {
                    }
                });
            }
        }

        public static int ForceGenerateServerSeed(string reason)
        {
            var seed = Random.Shared.Next(1, MaxSeed + 1);
            lock (Sync)
            {
                _serverSeed = seed;
            }
            if (_role == NetRole.Host)
                MUser.UpdateCoopRunSeed(seed, _playerId);
            _log?.Information("[NetMod] Generated host seed {Seed} ({Reason})", seed, reason);
            return seed;
        }

        public static bool TryGetHostRunSeed(out int seed)
        {
            lock (Sync)
            {
                if (_serverSeed.HasValue)
                {
                    seed = _serverSeed.Value;
                    return true;
                }
            }

            seed = 0;
            return false;
        }

        public static void ReceiveHostRunRestart(int seed)
        {
            lock (Sync)
            {
                _remoteSeed = seed;
                _seedArrived = true;
                if (_role == NetRole.Client)
                {
                    _autoStartTriggered = false;
                    if (_pendingClientRestartSeed.HasValue)
                    {
                        _pendingClientRestartSeed = seed;
                        _pendingClientRestartReason = "host_same_run_restart";
                        _pendingAutoStart = false;
                    }
                    else if (_inActualRun)
                    {
                        _inActualRun = false;
                        _pendingAutoStart = false;
                        _pendingClientRestartSeed = seed;
                        _pendingClientRestartReason = "host_same_run_restart";
                    }
                    else
                    {
                        SignalClientLaunchProgressLocked();
                    }
                }
            }

            _log?.Information("[NetMod] Client received same-run restart {Seed}", seed);
        }

        internal static void QueueHostRestartFromDeath(string reason)
        {
            var now = DateTime.UtcNow;
            lock (Sync)
            {
                if (_role != NetRole.Host)
                    return;
                if (now < _deathRestartCooldownUntil)
                    return;
                _deathRestartCooldownUntil = now.AddMilliseconds(DeathRestartCooldownMs);
            }

            EnqueueCriticalMainThreadCoalesced("game:host-restart", () =>
            {
                ModEntry.ResetDownedPlayersForRestart();

                var game = ModEntry.Instance?.game;
                if (game?.user == null)
                {
                    _log?.Warning("[NetMod] Skipping host restart ({Reason}): game not ready", reason);
                    return;
                }

                _log?.Information("[NetMod] Host restarting run ({Reason})", reason);
                GameDataSync.ClearPendingBossRuneReloadState();
                GameDataSync.SendBossRune(game.user, NetRef);
                GameDataSync.BeginSameRunRestart(GameDataSync.Seed);
                try { NetRef?.SendRunRestart(GameDataSync.Seed); } catch { }
                var restartLaunch = GameDataSync.BuildSameRunRestartLaunchMode();
                var restartIsCustom = GameDataSync.ResolveCurrentRunIsCustom();
                var restartStreamEnabled = GameDataSync.ResolveCurrentRunStreamEnabled();
                try
                {
                    RestartCurrentWorldDirect(game, GameDataSync.Seed, restartStreamEnabled, restartIsCustom, restartLaunch);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Host direct restart failed: {Message}", ex.Message);
                }
            });
        }

        private static void RestartCurrentWorldDirect(
            dc.pr.Game game,
            int seed,
            bool streamEnabled,
            bool customMode,
            dc.LaunchMode launchMode)
        {
            var user = game.user;
            if (user == null)
                return;

            PrepareCurrentWorldForRestartTransition(game);
            try { game.destroy(); } catch { }
            try { game.disposeImmediately(); } catch { }
            user.newGame(seed, GameDataSync._isTwitch, streamEnabled, customMode, launchMode);
        }

        private static void RestartCurrentWorldWithLoading(dc.pr.Game game, dc.LaunchMode launchMode)
        {
            var main = dc.Main.Class.ME;
            if (main == null)
                throw new InvalidOperationException("Main is unavailable for restart launch.");

            PrepareCurrentWorldForRestartTransition(game);
            main.launchGame(launchMode, null, null);
        }

        private static void PrepareCurrentWorldForRestartTransition(dc.pr.Game game)
        {
            try { ModEntry.Instance?.DisposeCoopGhostRuntimeForWorldTeardown(game); } catch { }

            try
            {
                var cine = game.curCine;
                if (cine != null)
                {
                    try { cine.destroyed = true; } catch { }
                    try { cine.disposeImmediately(); } catch { }
                    if (ReferenceEquals(game.curCine, cine))
                        game.curCine = null;
                }
            }
            catch
            {
            }

            try
            {
                if (game.controller != null)
                    game.controller.manualLock = false;
            }
            catch
            {
            }
        }

        private static void QueueClientRestartFromHostSeed(int seed, string reason)
        {
            MarkClientRestartPending();
            EnqueueCriticalMainThreadCoalesced("game:client-restart", () =>
            {
                ModEntry.ResetDownedPlayersForRestart();

                var game = ModEntry.Instance?.game;
                if (game?.user == null)
                {
                    _log?.Warning("[NetMod] Skipping client restart ({Reason}): game not ready", reason);
                    lock (Sync)
                    {
                        _seedArrived = true;
                        _autoStartTriggered = false;
                        SignalClientLaunchProgressLocked();
                    }
                    return;
                }

                _log?.Information("[NetMod] Client restarting run from host seed {Seed} ({Reason})", seed, reason);
                GameDataSync.ClearPendingBossRuneReloadState();
                GameDataSync.RestoreRemoteUserData(game.user);
                GameDataSync.BeginSameRunRestart(seed);
                var restartLaunch = GameDataSync.BuildSameRunRestartLaunchMode();
                try
                {
                    RestartCurrentWorldWithLoading(game, restartLaunch);
                }
                catch (Exception ex)
                {
                    GameDataSync.CancelSameRunRestart();
                    _log?.Warning("[NetMod] Client loading restart failed: {Message}", ex.Message);
                }
            });
        }

        private static void TryProcessPendingClientRestart()
        {
            int seed;
            string reason;
            lock (Sync)
            {
                if (_role != NetRole.Client || !_pendingClientRestartSeed.HasValue)
                    return;

                if (!IsRemoteRunSyncReadyForLaunchLocked())
                    return;

                seed = _pendingClientRestartSeed.Value;
                reason = string.IsNullOrWhiteSpace(_pendingClientRestartReason)
                    ? "host_restart"
                    : _pendingClientRestartReason;
                _pendingClientRestartSeed = null;
                _pendingClientRestartReason = string.Empty;
                _pendingAutoStart = false;
                _autoStartTriggered = false;
            }

            QueueClientRestartFromHostSeed(seed, reason);
        }

        internal static bool HasPendingClientRestart()
        {
            lock (Sync)
            {
                return _role == NetRole.Client && _pendingClientRestartSeed.HasValue;
            }
        }

        public static bool TryGetRemoteSeed(out int seed)
        {
            lock (Sync)
            {
                if (_remoteSeed.HasValue)
                {
                    seed = _remoteSeed.Value;
                    return true;
                }
            }

            seed = 0;
            return false;
        }

        public static void ReceiveLevelDesc(string json)
        {
            try
            {
                var sync = JsonConvert.DeserializeObject<LevelDescSync>(json);
                if (sync == null) return;

                CacheLevelDescSync(sync);
                NotifyLevelDescReceived();
                _log?.Information("[NetMod] Client received LevelDesc");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to parse LevelDesc: {Message}", ex.Message);
            }
        }

        public static void ReceiveRemoteUsername(string username)
        {
            var cleaned = CleanUsername(username);
            string previous;
            lock (Sync)
            {
                previous = _remoteUsername;
                if (string.Equals(previous, cleaned, StringComparison.Ordinal))
                    return;

                _remoteUsername = cleaned;
            }

            // Lobby heartbeats re-send the same name ~2/sec; only react on real changes.
            _log?.Information("[NetMod] Received remote username {Username}", cleaned);
            if (_role == NetRole.Host)
            {
                var userForMsg = cleaned;
                EnqueueMainThread(() =>
                    MultiplayerUI.PushSystemMessage(FormatLocalized("{0} connected to the server.", userForMsg)));
            }

            RequestLobbyMenuRefresh();
        }

        private static void SendCachedGeneratePayload()
        {
            var net = NetRef;
            if (net == null) return;

            LevelDescSync? levelDesc;
            lock (Sync)
            {
                levelDesc = _cachedLevelDescSync;
            }

            var json = BuildGeneratePayloadJson(levelDesc);
            SendCoopStateToRemote();
            net.SendGeneratePayload(json);
        }

        private static void CacheLevelDescSync(LevelDescSync? sync)
        {
            lock (Sync)
            {
                _cachedLevelDescSync = sync;
            }
        }

        private static LevelDescSync? GetCachedLevelDescSync()
        {
            lock (Sync)
            {
                return _cachedLevelDescSync;
            }
        }

        public static void TickMenu(double dt)
        {
            UpdateHostDisconnectCountdown();
            TryProcessPendingClientRestart();
            if (DateTime.UtcNow < _autoStartRetryAt)
                return;

            bool shouldStart = false;

            lock (Sync)
            {
                if (_role == NetRole.Client &&
                    !_inActualRun &&
                    !_pendingClientRestartSeed.HasValue)
                {
                    // Re-arm when late prereqs (LGRAPH / BOSSRUNE) arrive after seed/exec.
                    // Arming is sole-writer via Reevaluate; claim still requires full readiness.
                    ReevaluateClientLaunchArmLocked();
                    if (TryClaimClientAutoStartLocked())
                        shouldStart = true;
                }
            }

            if (!shouldStart)
                return;

            var ts = GetTitleScreen();
            if (ts != null)
            {
                try
                {
                    Mutex? mutex = null;
                    bool hasHandle = false;
                    try
                    {
                        mutex = new Mutex(false, AutoStartMutexName);
                        try
                        {
                            hasHandle = mutex.WaitOne(0);
                        }
                        catch (AbandonedMutexException)
                        {
                            hasHandle = true;
                        }

                        if (!hasHandle)
                        {
                            lock (Sync)
                            {
                                ReleaseClientAutoStartClaimLocked();
                            }
                            _autoStartRetryAt = DateTime.UtcNow.AddMilliseconds(250);
                            return;
                        }

                        TryAutoStartPendingLaunch(ts);
                    }
                    finally
                    {
                        if (hasHandle)
                            mutex?.ReleaseMutex();
                        mutex?.Dispose();
                    }
                    _log?.Information("[NetMod] Auto-started new game after seed");
                }
                catch (IOException ioEx)
                {
                    _log?.Warning("[NetMod] Auto-start blocked by config lock: {Message}", ioEx.Message);
                    lock (Sync)
                    {
                        ReleaseClientAutoStartClaimLocked();
                    }
                    _autoStartRetryAt = DateTime.UtcNow.AddSeconds(1.5);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to auto-start new game: {Message}", ex.Message);
                    lock (Sync)
                    {
                        ReleaseClientAutoStartClaimLocked();
                    }
                }
            }
            else
            {
                lock (Sync)
                {
                    ReleaseClientAutoStartClaimLocked();
                }
            }
        }

        private static void NotifyLevelDescReceived()
        {
            lock (Sync)
            {
                if (_role == NetRole.Client && !_inActualRun)
                    SignalClientLaunchProgressLocked();
            }
        }

        private static void ShowMultiplayerMenu(TitleScreen screen)
        {
            


            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();
                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Host game"),
                    () => ShowHostTransportMenu(screen),
                    GetText.Instance.GetString("Create a multiplayer session"));
                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Join game"),
                    () => ShowJoinTransportMenu(screen),
                    GetText.Instance.GetString("Connect to an existing host"));
                AddMenuButton(screen, GetText.Instance.GetString("Back"), () =>
                {
                    StopNetworkFromMenu();
                    screen.mainMenu();
                }, GetText.Instance.GetString("Return to main menu"));
                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(screen, GetText.Instance.GetString("Host game"), GetText.Instance.GetString("Join game"));
                _inHostStatusMenu = false;
                _inClientWaitingMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open multiplayer menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowHostTransportMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Lan host"),
                    () => ShowConnectionMenu(screen, NetRole.Host),
                    GetText.Instance.GetString("Use direct IP/port hosting"));

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Steam host"),
                    () => StartSteamHost(screen),
                    GetText.Instance.GetString("Create Steam lobby and start immediately"));

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Back"),
                    () => ShowMultiplayerMenu(screen),
                    GetText.Instance.GetString("Back to multiplayer menu"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(
                    screen,
                    GetText.Instance.GetString("Lan host"),
                    GetText.Instance.GetString("Steam host"),
                    GetText.Instance.GetString("Back"));
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open host transport menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowJoinTransportMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Lan join"),
                    () => ShowConnectionMenu(screen, NetRole.Client),
                    GetText.Instance.GetString("Connect by IP/port"));

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Steam join"),
                    () => StartSteamJoin(screen),
                    GetText.Instance.GetString("Connect by Steam lobby id/code from clipboard"));

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Back"),
                    () => ShowMultiplayerMenu(screen),
                    GetText.Instance.GetString("Back to multiplayer menu"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(
                    screen,
                    GetText.Instance.GetString("Lan join"),
                    GetText.Instance.GetString("Steam join"),
                    GetText.Instance.GetString("Back"));
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open join transport menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowConnectionMenu(TitleScreen screen, NetRole role)
        {
            _menuSelection = role;
            _menuTransport = ConnectionTransport.Lan;

            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddMenuButton(
                    screen,
                    $"{GetText.Instance.GetString("Username: ")}{_username}",
                    () => EditUsername(screen),
                    GetText.Instance.GetString("Edit display name"));

                AddMenuButton(screen, $"{GetText.Instance.GetString("IP: ")}{_mpIp}", () =>
                {
                    OpenTextInput(screen, GetText.Instance.GetString("IP address"), _mpIp, value =>
                    {
                        _mpIp = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value;
                        SaveConfig();
                        ShowConnectionMenu(screen, role);
                    }, noSpaces: true);
                }, GetText.Instance.GetString("Edit IP"));

                AddMenuButton(screen, $"{GetText.Instance.GetString("Port: ")}{_mpPort}", () =>
                {
                    OpenTextInput(screen, GetText.Instance.GetString("Port"), _mpPort.ToString(), value =>
                    {
                        if (!int.TryParse(value, out var parsed) || parsed <= 0 || parsed > 65535)
                            parsed = 1234;
                        _mpPort = parsed;
                        SaveConfig();
                        ShowConnectionMenu(screen, role);
                    }, noSpaces: true);
                }, GetText.Instance.GetString("Edit port"));

                var actionLabel = role == NetRole.Host
                    ? GetText.Instance.GetString("Host")
                    : GetText.Instance.GetString("Join");
                if (role == NetRole.Host)
                {
                    AddMenuButton(screen, actionLabel, () =>
                    {
                        StartHostServerOnly();
                        ShowHostStatusMenu(screen);
                        screen.ShouldAutoHideConnectionUI(true);
                    }, GetText.Instance.GetString("Start hosting"));
                }
                else
                {
                    AddMenuButton(screen, actionLabel, () =>
                    {
                        StartNetwork(role, screen);
                        ShowClientWaitingMenu(screen);
                        screen.ShouldAutoHideConnectionUI(true);
                    }, GetText.Instance.GetString("Connect to host"));
                }

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("Back"),
                    () =>
                    {
                        if (role == NetRole.Host)
                            ShowHostTransportMenu(screen);
                        else
                            ShowJoinTransportMenu(screen);
                        screen.ShouldAutoHideConnectionUI(false);
                    },
                    GetText.Instance.GetString("Back to multiplayer menu"));
                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(
                    screen,
                    GetText.Instance.GetString("Host game"),
                    GetText.Instance.GetString("Join game"),
                    "About Core Modding");
                _inHostStatusMenu = false;
                _inClientWaitingMenu = false;
                if (role == NetRole.Host)
                {
                    SetRole(NetRole.None);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to show connection menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void StartSteamHost(TitleScreen screen)
        {
            _menuSelection = NetRole.Host;
            _menuTransport = ConnectionTransport.Steam;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ConnectionUI.NotifyConnectionsChanged();
            ApplySteamPersonaUsername();

            StartHostServerOnly(bindAnyAddress: true);
            if (NetRef == null || !NetRef.IsAlive || !NetRef.IsHost)
            {
                _log?.Warning("[NetMod][Steam] Host start failed: host server was not created");
                ShowConnectionErrorPopup(
                    screen,
                    GetText.Instance.GetString("Steam host failed"),
                    GetText.Instance.GetString("Could not start Steam host. Check console logs."),
                    () => ShowHostTransportMenu(screen));
                return;
            }

            var lobby = NetRef.HostLobbyResult;
            if (lobby == null || !lobby.Success)
            {
                StopNetworkFromMenu();
                _log?.Warning("[NetMod][SteamWorkerError] {Error}", lobby?.Error ?? "Lobby creation failed");
                ShowConnectionErrorPopup(
                    screen,
                    GetText.Instance.GetString("Steam host failed"),
                    GetText.Instance.GetString("Steam lobby creation failed. Check console logs."),
                    () => ShowHostTransportMenu(screen));
                return;
            }

            if (!string.IsNullOrWhiteSpace(lobby.PersonaName))
                ApplySteamPersonaUsername(lobby.PersonaName);

            _steamLobbyId = lobby.LobbyId;
            _steamLobbyCode = SteamConnect.BuildLobbyCodeFromLobbyId(_steamLobbyId);
            ConnectionUI.NotifyConnectionsChanged();
            _log?.Information("[NetMod][Steam] Host lobby ready: id={LobbyId} code={LobbyCode}", _steamLobbyId, _steamLobbyCode);


            var copied = SteamConnect.TryCopyLobbyCodeToClipboard(_steamLobbyCode)
                         || SteamConnect.TryCopyLobbyIdToClipboard(lobby.LobbyId);
            if (copied)
                MultiplayerUI.PushSystemMessage("Lobby id copied to clipboard");

            ShowHostStatusMenu(screen);
            screen.ShouldAutoHideConnectionUI(true);
        }

        private static void StartSteamJoin(TitleScreen screen)
        {
            _menuSelection = NetRole.Client;
            _menuTransport = ConnectionTransport.Steam;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ApplySteamPersonaUsername();

            _steamJoinLobbyResolvePending = true;
            PrepareSteamJoinConnectionUiOnly(screen);
            _ = Task.Run(() =>
            {
                var ok = SteamConnect.TryResolveJoinEndpointFromClipboard(out var join);
                EnqueueMainThread(() => ApplySteamJoinResult(screen, ok, join, fromOverlay: false));
            });
        }

        internal static void HandleSteamOverlayJoinRequest(ulong lobbyId)
        {
            var screen = GetTitleScreen();
            if (screen == null)
            {
                _pendingOverlayJoinLobbyId = lobbyId;
                _log?.Information("[NetMod][Steam] Overlay join request queued: not at main menu (lobbyId={LobbyId})", lobbyId);
                return;
            }

            _log?.Information("[NetMod][Steam] Overlay join starting: lobbyId={LobbyId} screen=ok", lobbyId);

            _menuSelection = NetRole.Client;
            _menuTransport = ConnectionTransport.Steam;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ApplySteamPersonaUsername();

            _steamJoinLobbyResolvePending = true;
            PrepareSteamJoinConnectionUiOnly(screen);
            _ = Task.Run(() =>
            {
                _log?.Information("[NetMod][Steam] Overlay join resolving lobby (lobbyId={LobbyId})", lobbyId);
                var ok = SteamConnect.TryResolveJoinEndpointFromLobbyId(lobbyId, out var join);
                EnqueueMainThread(() => ApplySteamJoinResult(screen, ok, join, fromOverlay: true));
            });
        }

        private static void ApplySteamPersonaUsername(string? preferredPersona = null)
        {
            var candidate = string.IsNullOrWhiteSpace(preferredPersona)
                ? GetDefaultUsername()
                : preferredPersona;

            var cleaned = CleanUsername(candidate);
            if (string.IsNullOrWhiteSpace(cleaned))
                return;

            _username = cleaned;
            SaveConfig();
            SendUsernameToRemote();
        }

        /// <summary>Clears title menu and shows ConnectionUI while the Steam lobby is resolved off-thread.</summary>
        private static void PrepareSteamJoinConnectionUiOnly(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();
                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                _inClientWaitingMenu = false;
                _inHostStatusMenu = false;
                screen.ShouldAutoHideConnectionUI(true);
                ConnectionUI.NotifyConnectionsChanged();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to prepare Steam join UI: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ApplySteamJoinResult(TitleScreen screen, bool ok, SteamConnect.JoinLobbyResult join, bool fromOverlay)
        {
            _steamJoinLobbyResolvePending = false;

            if (fromOverlay)
                _log?.Information("[NetMod][Steam] Overlay join result: ok={Ok} error={Error}", ok, join.Error ?? "(none)");

            if (!ok)
            {
                _log?.Warning("[NetMod][SteamWorkerError] {Error}", join.Error);
                ShowConnectionErrorPopup(
                    screen,
                    GetText.Instance.GetString("Steam join failed"),
                    GetText.Instance.GetString("Steam join failed. Check console logs."),
                    () => ShowJoinTransportMenu(screen));
                return;
            }

            if (!string.IsNullOrWhiteSpace(join.PersonaName))
                ApplySteamPersonaUsername(join.PersonaName);

            if (join.HostSteamId == 0UL && join.Endpoint == null)
            {
                _log?.Warning("[NetMod][Steam] Join failed: lobby endpoint and host Steam id are missing");
                ShowConnectionErrorPopup(
                    screen,
                    GetText.Instance.GetString("Steam join failed"),
                    GetText.Instance.GetString("Steam lobby endpoint is invalid. Check console logs."),
                    () => ShowJoinTransportMenu(screen));
                return;
            }

            if (join.Endpoint != null)
            {
                _mpIp = join.Endpoint.Address.ToString();
                _mpPort = join.Endpoint.Port;
                SaveConfig();
            }
            else if (join.HostSteamId != 0UL)
            {
                _log?.Information("[NetMod][Steam] {Source} join: P2P-only (hostSteamId={HostSteamId})", fromOverlay ? "Overlay" : "Clipboard", join.HostSteamId);
            }
            _steamLobbyId = join.LobbyId;
            _steamLobbyCode = SteamConnect.BuildLobbyCodeFromLobbyId(_steamLobbyId);
            _steamHostSteamId = join.HostSteamId;
            ConnectionUI.NotifyConnectionsChanged();
            _log?.Information("[NetMod][Steam] Joined lobby: id={LobbyId} code={LobbyCode} hostSteamId={HostSteamId}", _steamLobbyId, _steamLobbyCode, _steamHostSteamId);

            StartNetwork(NetRole.Client, screen);
            ShowClientWaitingMenu(screen);
            screen.ShouldAutoHideConnectionUI(true);
        }

        private static void ShowConnectionErrorPopup(TitleScreen screen, string title, string details, Action onOk)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddInfoLine(screen, title, infoColor: 0xFF9090);
                if (!string.IsNullOrWhiteSpace(details))
                    AddInfoLine(screen, details, infoColor: 0xE0E0E0);

                AddMenuButton(
                    screen,
                    GetText.Instance.GetString("OK"),
                    onOk,
                    GetText.Instance.GetString("Return to previous menu"));

                RemoveMenuItems(screen, "About Core Modding", GetText.Instance.GetString("Play multiplayer"));
                RemoveDuplicatesKeepFirst(screen, GetText.Instance.GetString("OK"));
                _inClientWaitingMenu = false;
                _inHostStatusMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open connection error popup: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void StartNetwork(NetRole role, TitleScreen screen)
        {
            try
            {
                if (ModEntry.Instance == null)
                {
                    _log?.Warning("[NetMod] ModEntry instance unavailable for network start");
                    return;
                }

                if (role == NetRole.Host)
                {
                    PrepareLobbyForNewNetworkSession(clearRemoteCoopState: true);
                    var streamEnabled = TryGetStreamEnabled(screen);
                    if (_menuTransport == ConnectionTransport.Steam)
                        ModEntry.Instance.StartSteamHostFromMenu(_mpPort);
                    else
                        ModEntry.Instance.StartHostFromMenu(_mpIp, _mpPort);
                    SetAuthoritativePendingNewGameLaunch(custom: false, streamEnabled);
                    RememberPendingLaunch(PendingLaunchAction.NewGame, custom: false, streamEnabled, sendToRemote: true);
                    TryLaunchNewGame(screen, custom: false, streamEnabled);
                }
                else if (role == NetRole.Client)
                {
                    PrepareLobbyForNewNetworkSession(clearRemoteCoopState: true);
                    if (_menuTransport == ConnectionTransport.Steam)
                    {
                        if (_steamHostSteamId == 0UL)
                        {
                            _log?.Warning("[NetMod][Steam] Client start aborted: host Steam id is missing");
                            ShowConnectionErrorPopup(
                                screen,
                                GetText.Instance.GetString("Steam join failed"),
                                GetText.Instance.GetString("Steam host id is missing. Check console logs."),
                                () => ShowJoinTransportMenu(screen));
                            return;
                        }
                    }

                    if (_menuTransport == ConnectionTransport.Steam)
                        ModEntry.Instance.StartSteamClientFromMenu(_steamHostSteamId);
                    else
                        ModEntry.Instance.StartClientFromMenu(_mpIp, _mpPort);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to start network: {Message}", ex.Message);
            }
        }

        private static void StartHostServerOnly(bool bindAnyAddress = false)
        {
            try
            {
                if (ModEntry.Instance == null)
                {
                    _log?.Warning("[NetMod] ModEntry instance unavailable for host start");
                    return;
                }

                if (NetRef != null && NetRef.IsAlive && NetRef.IsHost)
                {
                    PrepareLobbyForNewNetworkSession();
                    return;
                }

                PrepareLobbyForNewNetworkSession(clearRemoteCoopState: true);
                if (_menuTransport == ConnectionTransport.Steam)
                {
                    ModEntry.Instance.StartSteamHostFromMenu(_mpPort);
                }
                else
                {
                    var hostIp = bindAnyAddress ? "0.0.0.0" : _mpIp;
                    ModEntry.Instance.StartHostFromMenu(hostIp, _mpPort);
                }

            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Host start failed: {Message}", ex.Message);
            }
        }

        // private static void GameDisposeHook(Hook_Game.orig_onDispose orig, Game self)
        // {
        //     try
        //     {
        //         HandleWorldExit(isDisposeHook: true);
        //     }
        //     catch (Exception ex)
        //     {
        //         _log?.Warning("[NetMod] onDispose hook error: {Message}", ex.Message);
        //     }

        //     orig(self);
        // }

        private static void HandleWorldExit(bool isDisposeHook = false)
        {
            ResetHostDisconnectCountdown();
            lock (Sync)
            {
                if (_worldExitHandled) return;
                _worldExitHandled = true;
            }

            var roleBefore = _role;
            if (roleBefore == NetRole.Host)
            {
                try { NetRef?.SendControlAndFlush("KICK", 320); } catch { }
            }

            try
            {
                NetRef?.Dispose();
            }
            catch { }

            SetRole(NetRole.None);
            NetRef = null;
            ResetClientConnectState();
            ResetLobbyReadyState();
            _menuSelection = NetRole.None;
            ResetSteamState();

            if (roleBefore == NetRole.Client)
            {
                ForceExitToMainMenu();
            }

            lock (Sync)
            {
                _worldExitHandled = false;
            }
        }
    }
}

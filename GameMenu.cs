using System;
using System.Net;
using System.Reflection;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using Newtonsoft.Json;
using Hashlink.Virtuals;
using Serilog;
using ModCore.Utitities;

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
        private const int MaxSeed = 999_999;
        public static NetNode? NetRef { get; set; }

        private static bool _menuHooksAttached;
        private static WeakReference<TitleScreen?>? _titleScreenRef;
        private static string _mpIp = "127.0.0.1";
        private static int _mpPort = 1234;
        private static NetRole _menuSelection = NetRole.None;
        private static bool _waitingForHost;
        private static bool _pendingAutoStart;
        private static bool _levelDescArrived;
        private static bool _autoStartTriggered;
        private static bool _mainMenuButtonAdded;
        private static bool _suppressAutoButton;
        private static bool _worldExitHandled;
        private static bool _seedArrived;
        private static string _username = "guest";
        private static string _playerId = Guid.NewGuid().ToString("N");
        public static string Username => _username;
        public static string PlayerId => _playerId;
        private static bool _localReady;
        private static readonly Dictionary<string, PlayerInfo> _clientPlayers = new();
        private static List<PlayerInfo> _playersDisplay = new();
        private static bool _inHostStatusMenu;
        private static bool _inClientWaitingMenu;
        private static bool _genArrived;
        private static LevelDescSync? _cachedLevelDescSync;
        private static RunParamsResolved? _latestResolvedRunParams;

        private static void InitializeMenuUiHooks()
        {
            if (_menuHooksAttached) return;

            try
            {
                LoadConfig();
                Hook_TitleScreen.addMenu += AddMenuHook;
                Hook_TitleScreen.mainMenu += MainMenuHook;
                Hook_Game.onDispose += GameDisposeHook;
                _menuHooksAttached = true;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] TitleScreen hooks failed: {Message}", ex.Message);
            }
        }

        public static void Initialize(ILogger logger)
        {
            lock (Sync)
            {
                _log = logger;
                _role = NetRole.None;
                _inActualRun = false;
                _serverSeed = null;
                _remoteSeed = null;
                _levelDescArrived = false;
                _pendingAutoStart = false;
                _autoStartTriggered = false;
                _genArrived = false;
                _seedArrived = false;
                _cachedLevelDescSync = null;
                _latestResolvedRunParams = null;
            }

            InitializeMenuUiHooks();
        }

        public static void MarkInRun()
        {
            lock (Sync)
            {
                _inActualRun = true;
            }
        }

        public static void SetRole(NetRole role)
        {
            lock (Sync)
            {
                _role = role;
            }
        }

        public static int ForceGenerateServerSeed(string reason)
        {
            var seed = Random.Shared.Next(1, MaxSeed + 1);
            lock (Sync)
            {
                _serverSeed = seed;
            }
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

        public static void ReceiveHostRunSeed(int seed)
        {
            lock (Sync)
            {
                _remoteSeed = seed;
                if (_role == NetRole.Client && !_inActualRun)
                {
                    _seedArrived = true;
                    _pendingAutoStart = true;
                }
            }
            _log?.Information("[NetMod] Client received host seed {Seed}", seed);
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

        public static void ReceiveRunParams(string json)
        {
            try
            {
                var rp = JsonConvert.DeserializeObject<RunParams>(json);
                if (rp == null) return;

                UpdateResolvedRunParams(rp, fromNetwork: true);
                _log?.Information("[NetMod] Client received run params");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to parse run params: {Message}", ex.Message);
            }
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

        private static void SendCachedGeneratePayload()
        {
            var net = NetRef;
            if (net == null) return;

            LevelDescSync? levelDesc;
            RunParams? runParams;
            lock (Sync)
            {
                levelDesc = _cachedLevelDescSync;
                runParams = _latestResolvedRunParams?.Data;
            }

            if (levelDesc == null && runParams == null)
                return;

            var payload = new
            {
                levelDesc = levelDesc ?? new LevelDescSync(),
                runParams = runParams ?? new RunParams(),
                rawDesc = string.Empty
            };
            var json = JsonConvert.SerializeObject(payload);
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

        private static void UpdateResolvedRunParams(RunParams rp, bool fromNetwork)
        {
            lock (Sync)
            {
                _latestResolvedRunParams = new RunParamsResolved { Data = rp };
            }
        }

        private static bool TryGetResolvedRunParams(out RunParamsResolved? rp)
        {
            lock (Sync)
            {
                rp = _latestResolvedRunParams;
                return rp != null;
            }
        }

        public static void TickMenu(double dt)
        {
            bool shouldStart = false;

            lock (Sync)
            {
                if (_role == NetRole.Client &&
                    !_inActualRun &&
                    _pendingAutoStart &&
                    _seedArrived &&
                    !_autoStartTriggered)
                {
                    _autoStartTriggered = true;
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
                    ts.startNewGame(custom: true);
                    _log?.Information("[NetMod] Auto-started new game after seed");
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to auto-start new game: {Message}", ex.Message);
                    lock (Sync)
                    {
                        _autoStartTriggered = false;
                        _pendingAutoStart = true;
                    }
                }
            }
            else
            {
                lock (Sync)
                {
                    _autoStartTriggered = false;
                    _pendingAutoStart = true;
                }
            }
        }

        private static void NotifyLevelDescReceived()
        {
            lock (Sync)
            {
                if (_role == NetRole.Client && !_inActualRun)
                {
                    _levelDescArrived = true;
                    _pendingAutoStart = true;
                }
            }
        }

        private static void MainMenuHook(Hook_TitleScreen.orig_mainMenu orig, TitleScreen self)
        {
            StoreTitleScreen(self);
            _mainMenuButtonAdded = false;
            orig(self);

            EnsureMainMenuMultiplayerButton(self);
        }

        private static virtual_cb_help_inter_isEnable_t_<bool> AddMenuHook(
            Hook_TitleScreen.orig_addMenu orig,
            TitleScreen self,
            dc.String str,
            HlAction cb,
            dc.String help,
            bool? isEnable,
            Ref<int> color)
        {
            var ret = orig(self, str, cb, help, isEnable, color);

            try
            {
                if (_suppressAutoButton) return ret;
                if (_mainMenuButtonAdded) return ret;
                if (!self.isMainMenu) return ret;

                var items = GetMemberValue(self, "menuItems", true);
                var count = GetArrayLength(items);
                // Default main menu: after the first item (Play) length becomes 1
                if (count == 1)
                {
                    int white = 0xFFFFFF;
                    var label = MakeHLString("Play multiplayer");
                    var helpStr = MakeHLString("Host or join a multiplayer session");
                    var colorHl = Ref<int>.From(ref white);
                    var cbHl = new HlAction(() => ShowMultiplayerMenu(self));
                    orig(self, label, cbHl, helpStr, null, colorHl);
                    _mainMenuButtonAdded = true;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] addMenu hook failed: {Message}", ex.Message);
            }

            return ret;
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
                AddMenuButton(screen, "Host game", () => ShowConnectionMenu(screen, NetRole.Host), "Create a multiplayer session");
                AddMenuButton(screen, "Join game", () => ShowConnectionMenu(screen, NetRole.Client), "Connect to an existing host");
                AddMenuButton(screen, "Back", () => screen.mainMenu(), "Return to main menu");
                RemoveMenuItems(screen, "About Core Modding", "Play multiplayer");
                RemoveDuplicatesKeepFirst(screen, "Host game", "Join game");
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

        private static void ShowConnectionMenu(TitleScreen screen, NetRole role)
        {
            _menuSelection = role;
            if (role == NetRole.Client)
                _waitingForHost = true;

            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddMenuButton(screen, $"Username: {_username}", () => EditUsername(screen), "Edit display name");

                AddMenuButton(screen, $"IP: {_mpIp}", () =>
                {
                    OpenTextInput(screen, "IP address", _mpIp, value =>
                    {
                        _mpIp = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value;
                        SaveConfig();
                        ShowConnectionMenu(screen, role);
                    });
                }, "Edit IP");

                AddMenuButton(screen, $"Port: {_mpPort}", () =>
                {
                    OpenTextInput(screen, "Port", _mpPort.ToString(), value =>
                    {
                        if (!int.TryParse(value, out var parsed) || parsed <= 0 || parsed > 65535)
                            parsed = 1234;
                        _mpPort = parsed;
                        SaveConfig();
                        ShowConnectionMenu(screen, role);
                    });
                }, "Edit port");

                var actionLabel = role == NetRole.Host ? "Host" : "Join";
                if (role == NetRole.Host)
                {
                    AddMenuButton(screen, actionLabel, () =>
                    {
                        StartHostServerOnly();
                        ShowHostStatusMenu(screen);
                    }, "Start hosting");
                }
                else
                {
                    AddMenuButton(screen, actionLabel, () =>
                    {
                        StartNetwork(role, screen);
                        ShowClientWaitingMenu(screen);
                    }, "Connect to host");
                }

                AddMenuButton(screen, "Back", () => ShowMultiplayerMenu(screen), "Back to multiplayer menu");
                RemoveMenuItems(screen, "About Core Modding", "Play multiplayer");
                RemoveDuplicatesKeepFirst(screen, "Host game", "Join game", "About Core Modding");
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
                    ModEntry.Instance.StartHostFromMenu(_mpIp, _mpPort);
                    _waitingForHost = false;
                    try
                    {
                        screen.startNewGame(custom: false);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warning("[NetMod] Failed to start host run: {Message}", ex.Message);
                    }
                }
                else if (role == NetRole.Client)
                {
                    ModEntry.Instance.StartClientFromMenu(_mpIp, _mpPort);
                    lock (Sync)
                    {
                        _levelDescArrived = false;
                        _pendingAutoStart = false;
                        _autoStartTriggered = false;
                        _seedArrived = false;
                    }
                    _waitingForHost = true;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to start network: {Message}", ex.Message);
            }
        }

        private static void StartHostServerOnly()
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
                    _waitingForHost = false;
                    return;
                }

                ModEntry.Instance.StartHostFromMenu(_mpIp, _mpPort);
                _waitingForHost = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Host start failed: {Message}", ex.Message);
            }
        }

        private static void StartHostRun(TitleScreen screen)
        {
            StartHostServerOnly();
            try
            {
                screen.startNewGame(custom: false);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to start host run: {Message}", ex.Message);
            }
        }

        private static void GameDisposeHook(Hook_Game.orig_onDispose orig, Game self)
        {
            try
            {
                HandleWorldExit(isDisposeHook: true);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] onDispose hook error: {Message}", ex.Message);
            }

            orig(self);
        }

        private static void HandleWorldExit(bool isDisposeHook = false)
        {
            lock (Sync)
            {
                if (_worldExitHandled) return;
                _worldExitHandled = true;
            }

            var roleBefore = _role;
            if (roleBefore == NetRole.Host)
            {
                try { NetRef?.SendKick(); } catch { }
            }

            try
            {
                NetRef?.Dispose();
            }
            catch { }

            SetRole(NetRole.None);
            NetRef = null;
            _waitingForHost = false;
            _menuSelection = NetRole.None;

            if (roleBefore == NetRole.Client)
            {
                ForceExitToMainMenu();
            }

            lock (Sync)
            {
                _worldExitHandled = false;
            }
        }

        private static void ForceExitToMainMenu()
        {
            try
            {
                GetTitleScreen()?.mainMenu();
            }
            catch { }
        }

        private static void ShowHostStatusMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddInfoLine(screen, $"Status: {BuildStatus(NetRole.Host)}", infoColor: 0xA0C0FF);
                AddInfoLine(screen, $"Players: {BuildPlayerList(NetRole.Host)}", infoColor: 0xA0C0FF);

                AddMenuButton(screen, "Play", () => StartHostRun(screen), "Launch game");
                AddMenuButton(screen, "Back", () =>
                {
                    SetRole(NetRole.None);
                    _menuSelection = NetRole.None;
                    ShowMultiplayerMenu(screen);
                }, "Back to host setup");

                RemoveMenuItems(screen, "About Core Modding", "Play multiplayer");
                RemoveDuplicatesKeepFirst(screen, "Play", "Back");
                _inHostStatusMenu = true;
                _inClientWaitingMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open host status menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void ShowClientWaitingMenu(TitleScreen screen)
        {
            var prevSuppress = _suppressAutoButton;
            _suppressAutoButton = true;
            var prevIsMain = GetIsMainMenu(screen);
            try
            {
                SetIsMainMenu(screen, false);
                screen.clearMenu();

                AddInfoLine(screen, "Waiting for the host", infoColor: 0xA0C0FF);
                AddMenuButton(screen, "Disconnect", () => DisconnectFromMenu(screen), "Disconnect and return to main menu");

                RemoveMenuItems(screen, "About Core Modding", "Play multiplayer");
                RemoveDuplicatesKeepFirst(screen, "Disconnect");
                _inClientWaitingMenu = true;
                _inHostStatusMenu = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open client waiting menu: {Message}", ex.Message);
            }
            finally
            {
                SetIsMainMenu(screen, prevIsMain);
                _suppressAutoButton = prevSuppress;
            }
        }

        private static void DisconnectFromMenu(TitleScreen screen)
        {
            try
            {
                ModEntry.Instance?.StopNetworkFromMenu();
            }
            catch { }
            _waitingForHost = false;
            _menuSelection = NetRole.None;
            _inHostStatusMenu = false;
            _inClientWaitingMenu = false;
            screen.mainMenu();
        }

        private static void EditUsername(TitleScreen screen)
        {
            OpenTextInput(screen, "Username", _username, value =>
            {
                var cleaned = string.IsNullOrWhiteSpace(value) ? "guest" : value.Trim();
                _username = cleaned;
                SaveConfig();
                ShowConnectionMenu(screen, _menuSelection == NetRole.None ? NetRole.Host : _menuSelection);
            });
        }

        public static void NotifyRemoteConnected(NetRole role)
        {
            if (role == NetRole.Host)
            {
                _waitingForHost = false;
                SendCachedDataToRemote();
                SendCachedGeneratePayload();

                if (_menuSelection == NetRole.Host)
                {
                    var ts = GetTitleScreen();
                    if (ts != null) ShowHostStatusMenu(ts);
                }
            }
            else if (role == NetRole.Client)
            {
                _waitingForHost = false;
                if (_menuSelection == NetRole.Client)
                {
                    var ts = GetTitleScreen();
                    if (ts != null) ShowClientWaitingMenu(ts);
                }
            }
        }

        public static void NotifyRemoteDisconnected(NetRole role)
        {
            if (role == NetRole.Host)
            {
                ForceExitToMainMenu();
            }

            SetRole(NetRole.None);
            NetRef = null;
            _waitingForHost = false;
            _menuSelection = NetRole.None;
            ClearNetworkCaches();
            _inHostStatusMenu = false;
            _inClientWaitingMenu = false;
            _localReady = false;
            _genArrived = false;
        }

        private static void SendCachedDataToRemote()
        {
            var net = NetRef;
            if (net == null) return;

            try
            {
                var ld = GetCachedLevelDescSync();
                if (ld != null)
                    net.SendLevelDesc(JsonConvert.SerializeObject(ld));
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to re-send LevelDesc: {Message}", ex.Message);
            }

            try
            {
                if (TryGetResolvedRunParams(out var rp) && rp != null)
                    net.SendRunParams(JsonConvert.SerializeObject(rp.Data));
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to re-send run params: {Message}", ex.Message);
            }

        }

        private static bool AllPlayersReady()
        {
            if (!_localReady) return false;
            if (_playersDisplay.Count == 0) return true;
            return _playersDisplay.All(p => p.Ready);
        }

        private static void ClearNetworkCaches()
        {
            CacheLevelDescSync(null);
            _latestResolvedRunParams = null;
            _genArrived = false;
            _seedArrived = false;
        }

        public static void ReceiveGeneratePayload(string json)
        {
            try
            {
                var payload = JsonConvert.DeserializeAnonymousType(json, new
                {
                    levelDesc = new LevelDescSync(),
                    runParams = new RunParams(),
                    rawDesc = string.Empty
                });
                if (payload == null) return;

                if (payload.levelDesc != null && !IsChallengeLevel(payload.levelDesc.LevelId))
                {
                    CacheLevelDescSync(payload.levelDesc);
                    _log?.Information("[NetMod] Client cached LevelDescSync from generate payload");
                }

                if (payload.runParams != null)
                {
                    UpdateResolvedRunParams(payload.runParams, fromNetwork: true);
                    _log?.Information("[NetMod] Client cached RunParams from generate payload");
                }

                if (!string.IsNullOrWhiteSpace(payload.rawDesc))
                {
                    _log?.Information("[NetMod] Client received raw LevelDesc: {Json}", payload.rawDesc);
                }

                lock (Sync)
                {
                    if (_role == NetRole.Client && !_inActualRun)
                    {
                        _genArrived = true;
                        _pendingAutoStart = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to receive generate payload: {Message}", ex.Message);
            }
        }

        private static bool IsChallengeLevel(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId)) return false;
            return levelId.IndexOf("challenge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class RunParams
        {
            public int lvl;
            public bool isCustom;
            public bool mode;
            public int bossRune;
            public List<double>? forge;
            public List<HistoryEntry>? history;
            public List<string>? meta;
            public int? runNum;
            public string? endKind;
            public bool? hasMods;
        }

        private sealed class RunParamsResolved
        {
            public RunParams Data = null!;
        }

        private sealed class HistoryEntry
        {
            public int brut;
            public int cellsEarned;
            public string? level;
            public int surv;
            public int tact;
            public double time;
        }

        private sealed class LevelDescSync
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

        private sealed class MenuConfig
        {
            public string user { get; set; } = "guest";
            public string last_ip { get; set; } = "127.0.0.1";
            public int last_port { get; set; } = 1234;
            public string player_id { get; set; } = Guid.NewGuid().ToString("N");
        }

        private sealed class PlayerInfo
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Name { get; set; } = "guest";
            public bool Ready { get; set; }
            public bool IsHost { get; set; }
        }

        private static void LoadConfig()
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
                        _username = string.IsNullOrWhiteSpace(cfg.user) ? GetDefaultUsername() : cfg.user.Trim();
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

            _username = GetDefaultUsername();
            _mpIp = "127.0.0.1";
            _mpPort = 1234;
            _playerId = Guid.NewGuid().ToString("N");
            SaveConfig();
        }

        private static void SaveConfig()
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

        private static string GetConfigPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var root = Directory.GetParent(baseDir)?.Parent?.Parent?.Parent?.FullName ?? baseDir;
            var dir = Path.Combine(root, "mods", "DeadCellsMultiplayerMod");
            return Path.Combine(dir, "config.json");
        }

        private static string GetDefaultUsername()
        {
            try
            {
                var env = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(env))
                    return env;
            }
            catch { }
            return "guest";
        }

        private static string BuildStatus(NetRole role)
        {
            var net = NetRef;
            if (net != null && net.HasRemote)
                return role == NetRole.Host ? "client connected" : "connected to host";

            if (role == NetRole.Client)
                return _waitingForHost ? "waiting for the host" : "not connected";

            return "waiting for client";
        }

        private static string BuildPlayerList(NetRole role)
        {
            var parts = new System.Collections.Generic.List<string>();
            parts.Add(role == NetRole.Host ? "Host (you)" : "Client (you)");

            var net = NetRef;
            if (net != null && net.HasRemote)
            {
                parts.Add(role == NetRole.Host ? "Client joined" : "Host online");
            }
            else
            {
                parts.Add(role == NetRole.Host ? "No client connected" : "Waiting for host");
            }

            return string.Join(", ", parts);
        }

        private static void OpenTextInput(TitleScreen screen, string title, string initial, Action<string> onValidate)
        {
            try
            {
                _ = new TextInput(
                    screen,
                    MakeHLString(title),
                    MakeHLString(initial ?? string.Empty),
                    MakeHLString("OK"),
                    new HlAction<dc.String>(s =>
                    {
                        var text = s?.ToString() ?? string.Empty;
                        onValidate(text);
                    }),
                    MakeHLString("Cancel"),
                    MakeHLString(string.Empty),
                    (dc.hxd.res.Sound?)null);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open text input: {Message}", ex.Message);
            }
        }

        private static void TryAddMenuButton(TitleScreen screen, string label, Action onClick, string? help = null)
        {
            try
            {
                AddMenuButton(screen, label, onClick, help);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Menu add failed for {Label}: {Message}", label, ex.Message);
            }
        }

        private static void AddMenuButton(TitleScreen screen, string label, Action onClick, string? help = null)
        {
            var cb = new HlAction(onClick);
            var labelStr = MakeHLString(label);
            var helpStr = MakeHLString(help ?? string.Empty);
            int colorVal = 0xFFFFFF;
            var color = Ref<int>.From(ref colorVal);
            screen.addMenu(labelStr, cb, helpStr, null, color);
        }

        private static void AddInfoLine(TitleScreen screen, string text, int? infoColor = null)
        {
            int colorVal = infoColor ?? 0xFFFFFF;
            var labelStr = MakeHLString(text);
            var helpStr = MakeHLString(string.Empty);
            var color = Ref<int>.From(ref colorVal);
            var cb = new HlAction(() => { });
            screen.addMenu(labelStr, cb, helpStr, false, color);
        }

        private static object? GetMemberValue(object? obj, string name, bool ignoreCase)
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

        private static bool TrySetMember(object? obj, string name, object? value)
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

        private static dc.String MakeHLString(string value)
        {
            return value.AsHaxeString();
        }

        private static bool GetIsMainMenu(TitleScreen screen)
        {
            try
            {
                var val = GetMemberValue(screen, "isMainMenu", true);
                if (val is bool b) return b;
            }
            catch { }
            return false;
        }

        private static void SetIsMainMenu(TitleScreen screen, bool value)
        {
            try
            {
                TrySetMember(screen, "isMainMenu", value);
            }
            catch { }
        }

        private static void EnsureMainMenuMultiplayerButton(TitleScreen screen)
        {
            try
            {
                var arr = GetMemberValue(screen, "menuItems", true);
                var existingIdx = FindMenuIndexByLabel(arr, "Play multiplayer");
                if (existingIdx < 0)
                {
                    TryAddMenuButton(screen, "Play multiplayer", () => ShowMultiplayerMenu(screen), "Host or join a multiplayer session");
                    arr = GetMemberValue(screen, "menuItems", true);
                }

                _mainMenuButtonAdded = true;
                MoveButtonAfterPlay(arr, "Play multiplayer", "Play");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to ensure main menu button order: {Message}", ex.Message);
            }
        }

        private static void MoveButtonAfterPlay(object? arrObj, string targetLabel, string anchorLabel)
        {
            if (arrObj == null) return;

            try
            {
                var type = arrObj.GetType();
                var getDyn = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var removeDyn = type.GetMethod("removeDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var insertDyn = type.GetMethod("insertDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDyn == null || removeDyn == null || insertDyn == null) return;

                int len = GetArrayLength(arrObj);
                int targetIdx = -1;
                int anchorIdx = -1;
                object? targetObj = null;

                for (int i = 0; i < len; i++)
                {
                    var item = getDyn.Invoke(arrObj, new object[] { i });
                    var label = GetMenuLabel(item);
                    if (targetIdx < 0 && label.Equals(targetLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIdx = i;
                        targetObj = item;
                    }
                    if (anchorIdx < 0 && label.Equals(anchorLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        anchorIdx = i;
                    }
                }

                if (targetIdx < 0 || anchorIdx < 0 || targetObj == null) return;
                var desired = anchorIdx + 1;
                if (targetIdx == desired) return;

                removeDyn.Invoke(arrObj, new[] { targetObj });
                insertDyn.Invoke(arrObj, new object[] { desired, targetObj });
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to reposition menu button: {Message}", ex.Message);
            }
        }

        private static int GetArrayLength(object arrObj)
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

        private static int FindMenuIndexByLabel(object? arrObj, string label)
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

        private static string GetMenuLabel(object? menuItem)
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

        private static void RemoveMenuItems(TitleScreen screen, params string[] labels)
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

        private static void RemoveDuplicatesKeepFirst(TitleScreen screen, params string[] labels)
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


        private static void StoreTitleScreen(TitleScreen ts)
        {
            _titleScreenRef = new WeakReference<TitleScreen?>(ts);
        }

        private static TitleScreen? GetTitleScreen()
        {
            if (_titleScreenRef != null && _titleScreenRef.TryGetTarget(out var ts))
                return ts;
            return null;
        }
    }
}

using System;
using System.Net;
using System.Reflection;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using Newtonsoft.Json;
using Hashlink.Virtuals;

namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private static bool _menuHooksAttached;
        private static WeakReference<TitleScreen?>? _titleScreenRef;
        private static string _mpIp = "127.0.0.1";
        private static int _mpPort = 1234;
        private static NetRole _menuSelection = NetRole.None;
        private static bool _waitingForHost;
        private static bool _pendingAutoStart;
        private static bool _levelDescArrived;
        private static bool _gameDataArrived;
        private static bool _autoStartTriggered;
        private static bool _mainMenuButtonAdded;
        private static bool _suppressAutoButton;
        private static bool _worldExitHandled;

        private static void InitializeMenuUiHooks()
        {
            if (_menuHooksAttached) return;

            try
            {
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

        public static void TickMenu(double dt)
        {
            bool shouldStart = false;

            lock (Sync)
            {
                if (_role == NetRole.Client &&
                    _pendingAutoStart &&
                    _levelDescArrived &&
                    _gameDataArrived &&
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
                    _log?.Information("[NetMod] Auto-started new game after LDESC+GDATA");
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

        public static void NotifyGameDataReceived()
        {
            lock (Sync)
            {
                _gameDataArrived = true;
                _pendingAutoStart = true;
            }
        }

        private static void NotifyLevelDescReceived()
        {
            lock (Sync)
            {
                _levelDescArrived = true;
                _pendingAutoStart = true;
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

                AddMenuButton(screen, $"IP: {_mpIp}", () =>
                {
                    OpenTextInput(screen, "IP address", _mpIp, value =>
                    {
                        _mpIp = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value;
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
                        _gameDataArrived = false;
                        _pendingAutoStart = false;
                        _autoStartTriggered = false;
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
                AddMenuButton(screen, "Back", () => ShowConnectionMenu(screen, NetRole.Host), "Back to host setup");

                RemoveMenuItems(screen, "About Core Modding", "Play multiplayer");
                RemoveDuplicatesKeepFirst(screen, "Play", "Back");
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
            screen.mainMenu();
        }

        public static void NotifyRemoteConnected(NetRole role)
        {
            if (role == NetRole.Host)
            {
                _waitingForHost = false;
                SendCachedDataToRemote();

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

            try
            {
                var gd = GetCachedGameDataSync();
                if (gd != null)
                {
                    net.SendGameData(JsonConvert.SerializeObject(gd));
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to re-send GameDataSync: {Message}", ex.Message);
            }
        }

        private static void ClearNetworkCaches()
        {
            CacheLevelDescSync(null);
            CacheGameDataSync(null);
            _latestResolvedRunParams = null;
            _lastAppliedSync = null;
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

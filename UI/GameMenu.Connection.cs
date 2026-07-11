using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using dc.pr;
using dc.ui;
using HaxeProxy.Runtime;
using Newtonsoft.Json;
using ModCore.Utilities;
using Microsoft.Win32;
using DeadCellsMultiplayerMod.UI;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using ModCore.Modules;

namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private static void ForceExitToMainMenu()
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

        private static void StopNetworkFromMenu()
        {
            ResetHostDisconnectCountdown();
            try
            {
                ModEntry.Instance?.StopNetworkFromMenu();
            }
            catch { }
            lock (Sync)
            {
                _inActualRun = false;
            }
            ResetSteamState();
        }

        public static void NotifyRemoteConnected(NetRole role)
        {
            ResetHostDisconnectCountdown();
            SendUsernameToRemote();

            if (role == NetRole.Host)
            {
                _waitingForHost = false;
                SendCachedDataToRemote();
                SendCachedGeneratePayload();
                ConnectionUI.NotifyConnectionsChanged();
            }
            else if (role == NetRole.Client)
            {
                _waitingForHost = false;
                _clientConnecting = false;
                _clientConnectAttempt = 0;
                ConnectionUI.NotifyConnectionsChanged();
            }
        }

        internal static void NotifyClientConnectAttempt(int attempt)
        {
            lock (Sync)
            {
                _clientConnectAttempt = attempt;
                _clientConnecting = true;
                _waitingForHost = true;
            }

            ConnectionUI.NotifyConnectionsChanged();
        }

        internal static void NotifyClientConnectFailed()
        {
            StopNetworkFromMenu();
            ResetClientConnectState();
            _waitingForHost = false;
            _menuSelection = NetRole.Client;

            EnqueueMainThread(() =>
            {
                var screen = GetTitleScreen();
                if (screen != null)
                {
                    screen.clearMenu();
                    AddInfoLine(screen, Localize("Can't find lobby"), 0xFF9090);
                    AddInfoLine(screen, Localize("Check the address or Steam lobby code."), 0xE0E0E0);
                    AddMenuButton(screen, GetText.Instance.GetString("OK"), () =>
                    {
                        screen.clearMenu();
                        ShowJoinTransportMenu(screen);
                    }, Localize("Return to join menu"));
                    screen.ShouldAutoHideConnectionUI(false);
                }
            });
        }

        public static void NotifyRemoteDisconnected(NetRole role)
        {
            if (role == NetRole.Host)
            {
                var disconnectedName = string.IsNullOrWhiteSpace(_remoteUsername) ? Localize("Guest") : _remoteUsername.Trim();
                MultiplayerUI.PushSystemMessage(FormatLocalized("{0} disconnected from the server.", disconnectedName));
                _remoteUsername = "guest";
                _seedArrived = false;

                ConnectionUI.NotifyConnectionsChanged();
                EnqueueMainThreadCoalesced("ui:refresh-layout-after-disconnect", () => ConnectionUI.RefreshLayoutAfterDisconnect());
                return;
            }

            var wasInRun = _inActualRun;
            SetRole(NetRole.None);
            NetRef = null;
            _waitingForHost = false;
            _menuSelection = NetRole.None;
            ResetSteamState();
            ClearNetworkCaches();
            _remoteUsername = "guest";
            MultiplayerUI.PushSystemMessage(Localize("Host disconnected from server."));
            if (wasInRun)
                StartHostDisconnectCountdown();

            EnqueueMainThreadCoalesced("ui:refresh-layout-after-disconnect", () => ConnectionUI.RefreshLayoutAfterDisconnect());
        }

        private static void SendUsernameToRemote()
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
        }

        private static void ClearNetworkCaches()
        {
            CacheLevelDescSync(null);
            _seedArrived = false;
        }

        private static void ResetSteamState()
        {
            var lobbyId = _steamLobbyId;
            if (lobbyId != 0UL)
            {
                try { SteamConnect.LeaveLobby(lobbyId); } catch { }
            }
            try { SteamConnect.StopHostLobbyWorker(); } catch { }
            _steamLobbyActive = false;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ConnectionUI.NotifyConnectionsChanged();
            if (_menuTransport == ConnectionTransport.Steam)
                _menuTransport = ConnectionTransport.Lan;
        }

        private static void StartHostDisconnectCountdown()
        {
            _hostDisconnectCountdownActive = true;
            _hostDisconnectCountdownUntil = DateTime.UtcNow.AddSeconds(HostDisconnectCountdownSeconds);
            _lastHostDisconnectCountdown = HostDisconnectCountdownSeconds;
            MultiplayerUI.PushSystemMessage(FormatLocalized("Back to menu in {0}...", HostDisconnectCountdownSeconds));
        }

        private static void ResetHostDisconnectCountdown()
        {
            _hostDisconnectCountdownActive = false;
            _hostDisconnectCountdownUntil = DateTime.MinValue;
            _lastHostDisconnectCountdown = -1;
        }

        private static void UpdateHostDisconnectCountdown()
        {
            if (!_hostDisconnectCountdownActive)
                return;

            var remaining = (int)Math.Ceiling((_hostDisconnectCountdownUntil - DateTime.UtcNow).TotalSeconds);
            if (remaining < 0)
                remaining = 0;

            if (remaining != _lastHostDisconnectCountdown)
            {
                _lastHostDisconnectCountdown = remaining;
                MultiplayerUI.PushSystemMessage(FormatLocalized("Back to menu in {0}...", remaining));
            }

            if (remaining > 0)
                return;

            _hostDisconnectCountdownActive = false;
            ForceExitToMainMenu();
        }

        internal static string Localize(string message)
        {
            return GetText.Instance.GetString(message);
        }

        private static string FormatLocalized(string format, params object[] args)
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
                    rawDesc = string.Empty
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

                lock (Sync)
                {
                    if (_role == NetRole.Client && !_inActualRun)
                        _pendingAutoStart = true;
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

        private static string CleanUsername(string? value)
        {
            var cleaned = string.IsNullOrWhiteSpace(value) ? "guest" : value.Trim();
            cleaned = cleaned.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
            return cleaned.Length == 0 ? "guest" : cleaned;
        }

        private static string GetDefaultUsername()
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

        private static string? TryGetSteamPersonaName()
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

        private static string? TryParseMostRecentPersonaName(string path)
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

        private static bool IsQuotedKeyOnly(string line)
        {
            if (!line.StartsWith("\"", StringComparison.Ordinal))
                return false;
            int secondQuote = line.IndexOf('"', 1);
            if (secondQuote < 0)
                return false;
            int thirdQuote = line.IndexOf('"', secondQuote + 1);
            return thirdQuote < 0;
        }

        private static bool TryParseVdfPair(string line, out string key, out string value)
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

        private static string BuildStatus(NetRole role)
        {
            var net = NetRef;
            if (role == NetRole.Client && _clientConnecting)
            {
                if (_clientConnectAttempt > 0)
                    return $"{GetText.Instance.GetString("connecting...")} ({_clientConnectAttempt}/{ClientConnectMaxAttempts})";
                return GetText.Instance.GetString("connecting...");
            }

            if (net != null && net.HasRemote)
                return role == NetRole.Host
                    ? GetText.Instance.GetString("client connected")
                    : GetText.Instance.GetString("connected to host");

            if (role == NetRole.Client)
                return _waitingForHost
                    ? GetText.Instance.GetString("waiting for the host")
                    : GetText.Instance.GetString("not connected");

            return GetText.Instance.GetString("waiting for client");
        }

        private static void ResetClientConnectState()
        {
            lock (Sync)
            {
                _clientConnectAttempt = 0;
                _clientConnecting = false;
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

        private static bool IsCtrlDown()
        {
            return dc.hxd.Key.Class.isDown(KeyCtrl) || dc.hxd.Key.Class.isDown(KeyLCtrl) || dc.hxd.Key.Class.isDown(KeyRCtrl);
        }

        private static void RegisterActiveTextInput(TextInput input, bool noSpaces)
        {
            lock (TextInputSync)
            {
                _activeTextInputRef = new WeakReference<TextInput?>(input);
                _activeTextInputNoSpaces = noSpaces;
            }
        }

        private static void ClearActiveTextInput()
        {
            lock (TextInputSync)
            {
                _activeTextInputRef = null;
                _activeTextInputNoSpaces = false;
            }
        }

        private static TextInput? GetActiveTextInput()
        {
            lock (TextInputSync)
            {
                if (_activeTextInputRef != null && _activeTextInputRef.TryGetTarget(out var input))
                    return input;
            }

            return null;
        }

        private static bool IsTextInputActive(TextInput input)
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

        private static object? GetTextInputTarget(TextInput input)
        {
            return GetMemberValue(input, "input", true)
                ?? GetMemberValue(input, "textInput", true)
                ?? GetMemberValue(input, "textField", true)
                ?? input;
        }

        private static bool TryGetTextInputValue(TextInput input, out string text)
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

        private static bool TrySetTextInputValue(TextInput input, string text)
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

        private static bool TryInvokeTextInputSetter(object target, object value)
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

        private static void RemoveSpacesFromTextInput(TextInput input)
        {
            if (!TryGetTextInputValue(input, out var text))
                return;

            if (!text.Contains(' ', StringComparison.Ordinal))
                return;

            TrySetTextInputValue(input, RemoveSpaces(text));
        }

        private static string RemoveSpaces(string value)
        {
            return value.Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        private static string? TryGetClipboardText()
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

        private static bool TrySetClipboardText(string text)
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

        private static void OpenTextInput(TitleScreen screen, string title, string initial, Action<string> onValidate, bool noSpaces = false)
        {
            try
            {
                ClearActiveTextInput();
                if (noSpaces && initial.Contains(' ', StringComparison.Ordinal))
                    initial = RemoveSpaces(initial);
                var initialText = initial ?? string.Empty;
                var input = new TextInput(
                    screen,
                    MakeHLString(title),
                    MakeHLString(initialText),
                    MakeHLString(initialText),
                    new HlAction<dc.String>(s =>
                    {
                        var text = s?.ToString() ?? string.Empty;
                        if (noSpaces)
                            text = RemoveSpaces(text);
                        try
                        {
                            onValidate(text);
                        }
                        finally
                        {
                            ClearActiveTextInput();
                        }
                    }),
                    MakeHLString(GetText.Instance.GetString("OK")),
                    MakeHLString(GetText.Instance.GetString("Cancel")),
                    (dc.hxd.res.Sound?)null);
                RegisterActiveTextInput(input, noSpaces);
            }
            catch (Exception ex)
            {
                ClearActiveTextInput();
                _log?.Warning("[NetMod] Failed to open text input: {Message}", ex.Message);
            }
        }

        private static object? GetMemberValue(object? obj, string name, bool ignoreCase)
            => TitleScreenReflection.GetMemberValue(obj, name, ignoreCase);

        private static bool TrySetMember(object? obj, string name, object? value)
            => TitleScreenReflection.TrySetMember(obj, name, value);

        private static dc.String MakeHLString(string value)
        {
            return value.AsHaxeString();
        }

        private static void AddMenuButton(
            TitleScreen screen,
            string label,
            Action onClick,
            string? help = null,
            int textColor = 0xFFFFFF)
        {
            var cb = new HlAction(onClick);
            var labelStr = MakeHLString(label);
            var helpStr = MakeHLString(help ?? string.Empty);
            int colorVal = textColor;
            var color = Ref<int>.From(ref colorVal);
            screen.addMenu(labelStr, cb, helpStr, null, color);
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

using System;
using dc;
using dc.pr;
using dc.tool;
using dc.ui;

namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private enum PendingLaunchAction
        {
            None,
            LoadSave,
            NewGame
        }

        private static bool _launchHooksAttached;
        private static PendingLaunchAction _pendingLaunchAction = PendingLaunchAction.NewGame;
        private static bool _pendingLaunchCustom;
        private static bool _pendingLaunchStreamEnabled;
        private static bool _hasAuthoritativePendingNewGameLaunch;
        private static bool _authoritativePendingNewGameCustom;
        private static bool _authoritativePendingNewGameStreamEnabled;
        private static string _cachedGeneratePayloadSignature = string.Empty;
        private static string? _cachedGeneratePayloadJson;
        // Host Custom Mode rules live in save/customGameData_{slot}.json. The client must receive
        // that file before startNewGame(true); otherwise GameData.load/checkIntegrity null-derefs.
        private static bool _remoteCustomGameDataReady;
        private static string? _pendingRemoteCustomGameDataJson;

        private static void InitializeMultiplayerLaunchHooks()
        {
            if (_launchHooksAttached)
                return;

            Hook_TitleScreen.startNewGame += Hook_TitleScreen_startNewGame;
            Hook_TitleScreen.confirmNewGame += Hook_TitleScreen_confirmNewGame;
            _launchHooksAttached = true;
        }

        private static void Hook_TitleScreen_startNewGame(Hook_TitleScreen.orig_startNewGame orig, TitleScreen self, bool custom)
        {
            var streamEnabled = TryGetStreamEnabled(self);
            NormalizePendingNewGameLaunch(ref custom, ref streamEnabled);
            RememberPendingLaunch(PendingLaunchAction.NewGame, custom, streamEnabled, sendToRemote: _role == NetRole.Host);
            // CustomGame.close() saves customGameData_{slot}.json immediately before this hook.
            // Push that authoritative file to the client before the RunLaunch barrier / native load.
            if (custom && _role == NetRole.Host)
                SendCustomGameDataToRemote();
            orig(self, custom);
        }

        private static void Hook_TitleScreen_confirmNewGame(Hook_TitleScreen.orig_confirmNewGame orig, TitleScreen self, bool custom)
        {
            var streamEnabled = TryGetStreamEnabled(self);
            NormalizePendingNewGameLaunch(ref custom, ref streamEnabled);
            RememberPendingLaunch(PendingLaunchAction.NewGame, custom, streamEnabled, sendToRemote: _role == NetRole.Host);
            if (custom && _role == NetRole.Host)
                SendCustomGameDataToRemote();
            orig(self, custom);
        }

        private static void RememberPendingLaunch(PendingLaunchAction action, bool custom, bool streamEnabled, bool sendToRemote, bool assignNewCoopWorld = true)
        {
            if (sendToRemote && (action != PendingLaunchAction.NewGame || assignNewCoopWorld))
                PrepareCoopIdentityForPendingLaunch(action);
            else if (sendToRemote)
                SendCoopStateToRemote();

            lock (Sync)
            {
                _pendingLaunchAction = action;
                _pendingLaunchCustom = custom;
                _pendingLaunchStreamEnabled = streamEnabled;
                if (action == PendingLaunchAction.NewGame && sendToRemote && !assignNewCoopWorld)
                    _pendingNewCoopWorldIdAssigned = false;
                InvalidateGeneratePayloadCacheLocked();
            }

            if (sendToRemote)
            {
                SendLaunchModeToRemote();
                SendCachedGeneratePayload();
            }
        }

        private static void NormalizePendingNewGameLaunch(ref bool custom, ref bool streamEnabled)
        {
            lock (Sync)
            {
                if (_hasAuthoritativePendingNewGameLaunch)
                {
                    custom = _authoritativePendingNewGameCustom;
                    streamEnabled = _authoritativePendingNewGameStreamEnabled;
                }
            }
        }

        private static void SetAuthoritativePendingNewGameLaunch(bool custom, bool streamEnabled)
        {
            lock (Sync)
            {
                _hasAuthoritativePendingNewGameLaunch = true;
                _authoritativePendingNewGameCustom = custom;
                _authoritativePendingNewGameStreamEnabled = streamEnabled;
            }
        }

        internal static void ClearAuthoritativePendingNewGameLaunch()
        {
            lock (Sync)
            {
                _hasAuthoritativePendingNewGameLaunch = false;
                _authoritativePendingNewGameCustom = false;
                _authoritativePendingNewGameStreamEnabled = false;
            }
        }

        internal static bool TryGetAuthoritativePendingNewGameLaunch(out bool custom, out bool streamEnabled)
        {
            lock (Sync)
            {
                if (_hasAuthoritativePendingNewGameLaunch)
                {
                    custom = _authoritativePendingNewGameCustom;
                    streamEnabled = _authoritativePendingNewGameStreamEnabled;
                    return true;
                }
            }

            custom = false;
            streamEnabled = false;
            return false;
        }

        private static void InvalidateGeneratePayloadCacheLocked()
        {
            _cachedGeneratePayloadSignature = string.Empty;
            _cachedGeneratePayloadJson = null;
        }

        private static bool TryGetStreamEnabled(TitleScreen? screen)
        {
            try
            {
                return screen != null && screen.isStreamEnable;
            }
            catch
            {
                return false;
            }
        }

        private static string GetModeLabel(bool isCustom)
        {
            return isCustom ? "Custom Mode" : "Normal Mode";
        }

        private static bool ResolveCurrentSaveIsCustom(TitleScreen? screen)
        {
            try
            {
                var mainGameData = screen?.user?.mainGameData;
                if (mainGameData != null)
                    return mainGameData.isCustom;
            }
            catch
            {
            }

            lock (Sync)
            {
                return _pendingLaunchCustom;
            }
        }

        private static string GetContinueButtonLabel(TitleScreen? screen)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Continue ({GetModeLabel(ResolveCurrentSaveIsCustom(screen))})");
        }

        private static string GetStartNormalModeButtonLabel()
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{GetModeLabel(isCustom: false)}");
        }

        private static void ContinueHostRun(TitleScreen screen)
        {
            if (!CanHostStartContinue(out var reason))
            {
                _log?.Warning("[NetMod] Continue Coop blocked on host: {Reason}", reason);
                return;
            }

            StartHostServerOnly();
            ClearAuthoritativePendingNewGameLaunch();
            TrySendContinueLaunchPrerequisites(screen);
            RememberPendingLaunch(
                PendingLaunchAction.LoadSave,
                ResolveCurrentSaveIsCustom(screen),
                TryGetStreamEnabled(screen),
                sendToRemote: true);
            TryLaunchContinue(screen);
        }

        private static void OpenHostCustomMode(TitleScreen screen)
        {
            if (!AllPlayersReady())
                return;

            if (!EnsureCustomModeScreenUser(screen))
            {
                _log?.Warning("[NetMod] Failed to prepare custom mode user for selected multiplayer save slot");
                return;
            }

            SetAuthoritativePendingNewGameLaunch(
                custom: true,
                streamEnabled: TryGetStreamEnabled(screen));
            RememberPendingLaunch(
                PendingLaunchAction.NewGame,
                custom: true,
                TryGetStreamEnabled(screen),
                sendToRemote: true,
                assignNewCoopWorld: false);

            try
            {
                // Vanilla CustomGame's "for mod" constructor path clears CustomGame.user and
                // exits through _Boot.exit() on close. The preset widgets then dereference
                // user.itemMeta, which crashes on default preset clicks. Use the normal
                // title-screen custom mode flow and keep multiplayer launch state in our
                // pending-launch hooks instead of opting into the native mod-only branch.
                screen.customModeMenu(isForMod: false);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to open custom mode menu: {Message}", ex.Message);
            }
        }

        private static bool EnsureCustomModeScreenUser(TitleScreen? screen)
        {
            if (screen == null)
                return false;

            if (TryPrepareCustomModeUser(screen.user, out var currentUser))
            {
                screen.user = currentUser;
                return true;
            }

            try
            {
                var loadedUser = Save.Class.tryLoad.Invoke();
                if (TryPrepareCustomModeUser(loadedUser, out var preparedLoadedUser))
                {
                    screen.user = preparedLoadedUser;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log?.Debug("[NetMod] Save.tryLoad() failed while opening custom mode: {Message}", ex.Message);
            }

            try
            {
                var freshUser = new User();
                if (TryPrepareCustomModeUser(freshUser, out var preparedFreshUser))
                {
                    screen.user = preparedFreshUser;
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log?.Debug("[NetMod] Fresh User() failed while opening custom mode: {Message}", ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Main.getGame(NewGame) loads User via Save.tryLoad independently of TitleScreen.user.
        /// Prepare the live User that User.newGame will use so CustomGameData.checkIntegrity
        /// does not null-deref itemMeta on the client.
        /// </summary>
        internal static bool PrepareUserForCustomModeLaunch(User? user)
        {
            return TryPrepareCustomModeUser(user, out _);
        }

        private static bool TryPrepareCustomModeUser(User? candidate, out User preparedUser)
        {
            preparedUser = null!;
            if (candidate == null)
                return false;

            try
            {
                candidate.onReload();
            }
            catch
            {
            }

            try
            {
                var itemMeta = candidate.itemMeta ?? new ItemMetaManager(candidate);
                itemMeta._user = candidate;
                try
                {
                    itemMeta.onReload();
                }
                catch
                {
                }

                candidate.itemMeta = itemMeta;
            }
            catch
            {
            }

            try
            {
                var mainGameData = candidate.mainGameData;
                if (mainGameData != null && mainGameData.sUser == null)
                    mainGameData.sUser = candidate;
            }
            catch
            {
            }

            if (candidate.itemMeta == null)
                return false;

            preparedUser = candidate;
            return true;
        }

        private static string GetCustomGameDataRelativePath(int? slot = null)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"customGameData_{ResolveSaveSlotNumber(slot)}.json");
        }

        private static bool TryReadLocalCustomGameDataJson(out string json)
        {
            json = string.Empty;
            try
            {
                var relativePath = GetCustomGameDataRelativePath();
                if (!dc.tool.File.Class.exists.Invoke(MakeHLString(relativePath)))
                {
                    _log?.Warning(
                        "[NetMod] Host customGameData file missing at {Path}",
                        GetAbsoluteSavePath(relativePath));
                    return false;
                }

                var bytes = dc.tool.File.Class.getBytes.Invoke(MakeHLString(relativePath));
                var text = bytes?.toString()?.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    _log?.Warning("[NetMod] Host customGameData file is empty: {Path}", relativePath);
                    return false;
                }

                json = text;
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to read host customGameData: {Message}", ex.Message);
                return false;
            }
        }

        private static bool TryWriteLocalCustomGameDataJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                var relativePath = GetCustomGameDataRelativePath();
                var absolutePath = GetAbsoluteSavePath(relativePath);
                var directory = System.IO.Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    System.IO.Directory.CreateDirectory(directory);

                // Prefer the game File API so PATH / relative resolution matches CustomGameData.load.
                var hlBytes = dc.haxe.io.Bytes.Class.ofString.Invoke(MakeHLString(json), null);
                dc.tool.File.Class.saveBytes.Invoke(MakeHLString(relativePath), hlBytes);
                _log?.Information(
                    "[NetMod] Wrote host customGameData ({Length} chars) to {Path}",
                    json.Length,
                    relativePath);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to write customGameData via File API: {Message}", ex.Message);
            }

            try
            {
                var absolutePath = GetAbsoluteSavePath(GetCustomGameDataRelativePath());
                var directory = System.IO.Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(absolutePath, json);
                _log?.Information(
                    "[NetMod] Wrote host customGameData ({Length} chars) via absolute path",
                    json.Length);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to write customGameData: {Message}", ex.Message);
                return false;
            }
        }

        private static void SendCustomGameDataToRemote()
        {
            var net = NetRef;
            if (net == null || !net.IsAlive || !net.IsHost)
                return;

            if (!TryReadLocalCustomGameDataJson(out var json))
            {
                _log?.Warning("[NetMod] Skipping CGDATA send: local customGameData unavailable");
                return;
            }

            net.SendCustomGameData(json);
            lock (Sync)
            {
                // Host already has the file; mark ready so local state stays consistent.
                _remoteCustomGameDataReady = true;
                _pendingRemoteCustomGameDataJson = json;
            }
        }

        public static void ReceiveCustomGameData(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                _log?.Warning("[NetMod] Ignoring empty CGDATA payload");
                return;
            }

            string json;
            try
            {
                // Wire format is base64 so indented customGameData JSON cannot split the line protocol.
                var raw = Convert.FromBase64String(payload.Trim());
                json = System.Text.Encoding.UTF8.GetString(raw);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to decode CGDATA payload: {Message}", ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _log?.Warning("[NetMod] Decoded CGDATA payload was empty");
                return;
            }

            lock (Sync)
            {
                _pendingRemoteCustomGameDataJson = json;
            }

            // Disk write must happen on the game thread-safe path before auto-start consumes it.
            EnqueueCriticalMainThreadCoalesced(
                "game:apply-custom-game-data",
                () =>
                {
                    string? pending;
                    lock (Sync)
                        pending = _pendingRemoteCustomGameDataJson;

                    if (string.IsNullOrWhiteSpace(pending))
                        return;

                    if (!TryWriteLocalCustomGameDataJson(pending))
                    {
                        _log?.Warning("[NetMod] Client failed to materialize host customGameData");
                        return;
                    }

                    lock (Sync)
                    {
                        _remoteCustomGameDataReady = true;
                        SignalClientLaunchProgressLocked();
                    }

                    _log?.Information("[NetMod] Client applied host customGameData ({Length} chars)", pending.Length);
                });
        }

        internal static void ClearRemoteCustomGameDataState()
        {
            lock (Sync)
            {
                _remoteCustomGameDataReady = false;
                _pendingRemoteCustomGameDataJson = null;
            }
        }

        private static void StartHostRunNormalMode(TitleScreen screen)
        {
            if (!AllPlayersReady())
                return;

            StartHostServerOnly();
            SetAuthoritativePendingNewGameLaunch(
                custom: false,
                streamEnabled: TryGetStreamEnabled(screen));
            RememberPendingLaunch(
                PendingLaunchAction.NewGame,
                custom: false,
                TryGetStreamEnabled(screen),
                sendToRemote: true);
            TryLaunchNewGame(screen, custom: false, TryGetStreamEnabled(screen));
        }

        private static void TryLaunchContinue(TitleScreen? screen)
        {
            if (!TryBeginContinueLaunch())
                return;

            try
            {
                ModEntry.Instance?.PrepareForContinueLaunch();

                var main = dc.Main.Class.ME;
                if (main != null)
                {
                    main.launchGame(new LaunchMode.LoadSave(), null, 0.8);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Continue launch failed: {Message}", ex.Message);
                ClearContinueLaunchGuard();
            }

            try
            {
                screen?.saveMenu();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Continue fallback failed: {Message}", ex.Message);
                ClearContinueLaunchGuard();
            }
        }

        private static bool TryBeginContinueLaunch()
        {
            var now = DateTime.UtcNow;
            lock (Sync)
            {
                if (_continueLaunchInProgress &&
                    (now - _continueLaunchStartedAt).TotalMilliseconds < ContinueLaunchGuardMs)
                {
                    return false;
                }

                _continueLaunchInProgress = true;
                _continueLaunchStartedAt = now;
                return true;
            }
        }

        private static void ClearContinueLaunchGuard()
        {
            lock (Sync)
            {
                _continueLaunchInProgress = false;
                _continueLaunchStartedAt = DateTime.MinValue;
            }
        }

        private static void TrySendContinueLaunchPrerequisites(TitleScreen? screen)
        {
            var net = NetRef;
            if (net == null || !net.IsAlive || !net.IsHost)
                return;

            var user = TryResolveContinueUser(screen);
            if (user == null)
                return;

            GameDataSync.SendBossRune(user, net);
            GameDataSync.SendCurrentHeroCosmetics(user, net, force: true);
        }

        private static User? TryResolveContinueUser(TitleScreen? screen)
        {
            try
            {
                if (screen?.user != null)
                    return screen.user;
            }
            catch
            {
            }

            try
            {
                if (dc.Main.Class.ME?.user != null)
                    return dc.Main.Class.ME.user;
            }
            catch
            {
            }

<<<<<<< HEAD
            // Deliberately NOT falling back to Save.tryLoad() here. This resolver only feeds
            // boss-rune and cosmetics packets, but hxbit deserialization of an incompatible or
            // damaged save throws a HashlinkError that the C# catch below cannot actually contain:
            // the Haxe VM is left poisoned and the next mainLoop tick fatals with an unrelated
            // cast ("Can't cast level.SpotFlags to tool._Cooldown.CdInst"). A missing cosmetic
            // packet is a cosmetic problem; a poisoned VM takes down the host and force-drops the
            // client. If neither live user reference exists, do nothing.
            return null;
=======
            try
            {
                return Save.Class.tryLoad.Invoke();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to load selected save for Continue prerequisites: {Message}", ex.Message);
                return null;
            }
>>>>>>> 885c4abae3de9bd3315c71e50e248e42f6014fac
        }

        private static void TryLaunchNewGame(TitleScreen? screen, bool custom, bool streamEnabled)
        {
            SetAuthoritativePendingNewGameLaunch(custom, streamEnabled);

            if (custom && screen != null)
            {
                // Best-effort TitleScreen prep. Main.getGame reloads User via Save.tryLoad;
                // GameDataSync.user_hook_new_game prepares that live instance before GameData ctor.
                if (!EnsureCustomModeScreenUser(screen) && _role == NetRole.Host)
                {
                    _log?.Warning(
                        "[NetMod] Custom Mode launch aborted: TitleScreen user/itemMeta is not ready on host");
                    return;
                }
            }

            // Client Custom Mode: go straight to Main.launchGame. TitleScreen.startNewGame can
            // divert into the mods options UI when showModsUI is enabled, which never starts a run.
            if (custom && _role == NetRole.Client)
            {
                var main = dc.Main.Class.ME;
                if (main == null)
                {
                    _log?.Warning("[NetMod] Custom Mode client launch aborted: Main is unavailable");
                    return;
                }

                try
                {
                    if (_structuredLaunchExecuteSequence > 0)
                        NotifyClientLaunchQueued(_structuredLaunchExecuteSequence);

                    _log?.Information(
                        "[NetMod] Launching NewGame custom={Custom} stream={Stream} role={Role}",
                        custom,
                        streamEnabled,
                        _role);
                    main.launchGame(new LaunchMode.NewGame(custom, streamEnabled), null, 0.8);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Direct custom-mode client launch failed: {Message}", ex.Message);
                }

                return;
            }

            if (screen != null)
            {
                try
                {
                    // Structured RunLaunch barrier: tell the host we are invoking the native loader now.
                    if (_role == NetRole.Client && _structuredLaunchExecuteSequence > 0)
                        NotifyClientLaunchQueued(_structuredLaunchExecuteSequence);

                    _log?.Information(
                        "[NetMod] Launching NewGame custom={Custom} stream={Stream} role={Role}",
                        custom,
                        streamEnabled,
                        _role);
                    screen.startNewGame(custom);
                    return;
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] startNewGame failed, falling back to direct launch: {Message}", ex.Message);
                }
            }

            var fallbackMain = dc.Main.Class.ME;
            if (fallbackMain == null)
                return;

            try
            {
                if (_role == NetRole.Client && _structuredLaunchExecuteSequence > 0)
                    NotifyClientLaunchQueued(_structuredLaunchExecuteSequence);

                fallbackMain.launchGame(new LaunchMode.NewGame(custom, streamEnabled), null, 0.8);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Direct new-game launch failed: {Message}", ex.Message);
            }
        }

        private static string BuildGeneratePayloadJson(LevelDescSync? levelDesc)
        {
            PendingLaunchAction action;
            bool custom;
            bool streamEnabled;
            bool newCoopWorldPrepared;
            var coopId = MUser.GetCurrentCoopId() ?? string.Empty;
            var hostHasContinueSave = HasLocalContinueSaveState(out _);
            lock (Sync)
            {
                action = _pendingLaunchAction;
                custom = _pendingLaunchCustom;
                streamEnabled = _pendingLaunchStreamEnabled;
                newCoopWorldPrepared = _pendingNewCoopWorldIdAssigned;
            }

            var signature = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{levelDesc?.LevelId}|{levelDesc?.MapDepth}|{levelDesc?.Group}|{(int)action}|{(custom ? 1 : 0)}|{(streamEnabled ? 1 : 0)}|{(newCoopWorldPrepared ? 1 : 0)}|{coopId}|{(hostHasContinueSave ? 1 : 0)}");

            lock (Sync)
            {
                if (string.Equals(_cachedGeneratePayloadSignature, signature, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(_cachedGeneratePayloadJson))
                {
                    return _cachedGeneratePayloadJson!;
                }
            }

            var payload = new
            {
                levelDesc = levelDesc ?? new LevelDescSync(),
                rawDesc = string.Empty,
                launchAction = action.ToString(),
                launchCustom = custom,
                launchStreamEnabled = streamEnabled,
                newCoopWorldPrepared,
                coopId,
                hostHasContinueSave
            };
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);

            lock (Sync)
            {
                _cachedGeneratePayloadSignature = signature;
                _cachedGeneratePayloadJson = json;
            }

            return json;
        }

        private static void ApplyReceivedPendingLaunch(string? actionText, bool launchCustom, bool launchStreamEnabled)
        {
            PendingLaunchAction action;
            if (!Enum.TryParse(actionText, ignoreCase: true, out action))
                action = PendingLaunchAction.NewGame;

            lock (Sync)
            {
                _hasAuthoritativePendingNewGameLaunch = action == PendingLaunchAction.NewGame;
                _authoritativePendingNewGameCustom = action == PendingLaunchAction.NewGame && launchCustom;
                _authoritativePendingNewGameStreamEnabled = action == PendingLaunchAction.NewGame && launchStreamEnabled;
                _pendingLaunchAction = action;
                _pendingLaunchCustom = launchCustom;
                _pendingLaunchStreamEnabled = launchStreamEnabled;
                // Opening Custom Mode announces custom=true before CGDATA exists. Clear readiness
                // until the host's saved customGameData file arrives.
                if (action == PendingLaunchAction.NewGame && launchCustom)
                    _remoteCustomGameDataReady = false;
                else
                {
                    _remoteCustomGameDataReady = true;
                    _pendingRemoteCustomGameDataJson = null;
                }
            }
        }

        public static void ReceiveLaunchMode(
            int actionValue,
            bool launchCustom,
            bool launchStreamEnabled,
            bool newCoopWorldPrepared,
            string? coopId,
            bool hostHasContinueSave)
        {
            var action = Enum.IsDefined(typeof(PendingLaunchAction), actionValue)
                ? (PendingLaunchAction)actionValue
                : PendingLaunchAction.NewGame;

            ApplyReceivedPendingLaunch(action.ToString(), launchCustom, launchStreamEnabled);

            if (!string.IsNullOrWhiteSpace(coopId))
                ReceiveRemoteCoopState(1, coopId, hostHasContinueSave);

            lock (Sync)
            {
                _receivedLaunchPayload = true;
                _receivedNewCoopWorldPrepared = newCoopWorldPrepared;
            }

            TryStoreRemoteCoopIdForPendingNewGame();
            RequestLobbyMenuRefresh();
        }

        private static void SendLaunchModeToRemote()
        {
            var net = NetRef;
            if (net == null || !net.IsAlive || !net.IsHost)
                return;

            PendingLaunchAction action;
            bool custom;
            bool streamEnabled;
            bool newCoopWorldPrepared;
            lock (Sync)
            {
                action = _pendingLaunchAction;
                custom = _pendingLaunchCustom;
                streamEnabled = _pendingLaunchStreamEnabled;
                newCoopWorldPrepared = _pendingNewCoopWorldIdAssigned;
            }

            var coopId = MUser.GetCurrentCoopId() ?? string.Empty;
            var hostHasContinueSave = HasLocalContinueSaveState(out _);
            net.SendLaunchMode((int)action, custom, streamEnabled, newCoopWorldPrepared, coopId, hostHasContinueSave);
        }

        private static bool IsPendingLaunchReadyForAutoStartLocked()
        {
            if (_pendingLaunchAction == PendingLaunchAction.LoadSave)
            {
                if (!CanClientAcceptContinueLaunchLocked(out var reason))
                {
                    LogClientContinueBlockReasonLocked(reason);
                    return false;
                }

                return _genArrived && GameDataSync.HasRemoteBossRune();
            }

            if (!_genArrived || !_seedArrived)
                return false;

            if (_pendingLaunchCustom && !_remoteCustomGameDataReady)
                return false;

            return IsRemoteRunSyncReadyForLaunchLocked();
        }

        private static bool IsRemoteRunSyncReadyForLaunchLocked()
        {
            if (!GameDataSync.HasRemoteBossRune())
                return false;

            var levelId = GetCachedLevelDescSync()?.LevelId;
            if (!string.IsNullOrWhiteSpace(levelId) && GameDataSync.HasPendingRemoteLevelGraph(levelId))
                return true;

            return GameDataSync.HasPendingRemoteLevelGraph("PrisonStart");
        }

        private static void TryAutoStartPendingLaunch(TitleScreen screen)
        {
            PendingLaunchAction action;
            bool custom;
            bool streamEnabled;
            lock (Sync)
            {
                action = _pendingLaunchAction;
                custom = _pendingLaunchCustom;
                streamEnabled = _pendingLaunchStreamEnabled;
            }

            if (action == PendingLaunchAction.LoadSave)
            {
                TryLaunchContinue(screen);
                return;
            }

            TryLaunchNewGame(screen, custom, streamEnabled);
        }
    }
}

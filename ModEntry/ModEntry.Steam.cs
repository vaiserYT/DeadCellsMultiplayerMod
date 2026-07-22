using Steamworks;
using DeadCellsMultiplayerMod.Tools;
using System.Threading;


namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private static readonly object s_steamCallbackPumpLock = new();
        private static int s_steamCallbackShutdown;
        private static bool s_steamProcessExitHookInstalled;
        private static bool s_steamMainThreadPumpLogged;

        private static bool TryParseConnectLobbyFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(args[i + 1], out var lobbyId) && lobbyId > 0)
                {
                    Instance?.Logger.Information("[NetMod][Steam] Launch parameter +connect_lobby detected lobbyId={LobbyId}", lobbyId);
                    GameMenu.EnqueueMainThreadCoalesced("steam:overlay-join", () => GameMenu.HandleSteamOverlayJoinRequest(lobbyId));
                    return true;
                }
            }
            return false;
        }

        private static void TryDeferredSteamOverlayCallbackRegistration()
        {
            if (!s_steamFeaturesRequested || s_steamUnavailable ||
                !s_steamOverlayCallbackPending ||
                (s_steamOverlayJoinCallback != null && s_steamRichPresenceJoinCallback != null))
                return;
            if (s_steamOverlayCallbackRetryCount >= SteamOverlayCallbackMaxRetries)
            {
                s_steamOverlayCallbackPending = false;
                Instance?.Logger.Warning("[NetMod] Steam overlay join callback registration gave up after {Count} retries", SteamOverlayCallbackMaxRetries);
                MarkSteamUnavailable($"overlay callbacks never registered after {SteamOverlayCallbackMaxRetries} retries");
                return;
            }
            s_steamOverlayCallbackRetryCount++;
            var shouldLogFailure = s_steamOverlayCallbackRetryCount == 1 || s_steamOverlayCallbackRetryCount % 60 == 0;
            var initialized = TryEnsureSteamApiInitialized(
                $"callback registration attempt {s_steamOverlayCallbackRetryCount}",
                shouldLogFailure);

            if (!initialized)
            {
                // Never create Steamworks callbacks before SteamAPI.Init succeeds. On non-Steam
                // runtimes Callback<T>.Create would leave a dispatcher that cannot be pumped.
                if (s_steamUnavailable)
                    s_steamOverlayCallbackPending = false;
                return;
            }

            try
            {
                // Callback<T>.Create throws when Steamworks was never initialized, which is the
                // normal state on a machine with no Steam client. Treat that as "Steam features
                // off" rather than letting it escape into the per-frame update path.
                s_steamOverlayJoinCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
                s_steamRichPresenceJoinCallback = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);
            }
            catch (Exception ex)
            {
                s_steamOverlayJoinCallback = null;
                s_steamRichPresenceJoinCallback = null;
                s_steamOverlayCallbackPending = false;
                MarkSteamUnavailable($"overlay callback registration threw: {ex.GetType().Name}");
                return;
            }

            s_steamOverlayCallbackPending = false;
            StartSteamCallbackPumpTimer();
            Instance?.Logger.Information(
                "[NetMod] Steam overlay join callbacks registered (attempt {Attempt}, steamInitialized={Initialized})",
                s_steamOverlayCallbackRetryCount,
                initialized);
        }

        private static void WriteOverlayJoinDiagnostic(string callbackType, string data)
        {
            try
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dccm_overlay_join_fired.txt");
                System.IO.File.WriteAllText(path, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z | {callbackType} | {data}");
            }
            catch
            {
                // Diagnostics must never abort a Steam callback or join request.
            }
        }

        private static void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t data)
        {
            WriteOverlayJoinDiagnostic("GameLobbyJoinRequested_t", data.m_steamIDLobby.m_SteamID.ToString());
            Instance?.Logger.Information("[NetMod][Steam] GameLobbyJoinRequested_t callback fired");
            var lobbyId = data.m_steamIDLobby.m_SteamID;
            if (lobbyId == 0UL)
                return;
            Instance?.Logger.Information("[NetMod][Steam] Overlay lobby join requested lobbyId={LobbyId}", lobbyId);
            EnqueueAndProcessOverlayJoin(lobbyId, "GameLobbyJoinRequested_t");
        }

        private static void OnGameRichPresenceJoinRequested(GameRichPresenceJoinRequested_t data)
        {
            var connect = data.m_rgchConnect ?? string.Empty;
            WriteOverlayJoinDiagnostic("GameRichPresenceJoinRequested_t", connect);
            Instance?.Logger.Information("[NetMod][Steam] GameRichPresenceJoinRequested_t callback fired");
            if (string.IsNullOrWhiteSpace(connect))
            {
                Instance?.Logger.Information("[NetMod][Steam] Rich Presence join requested but connect string is empty (host may not have set Rich Presence)");
                return;
            }
            Instance?.Logger.Information("[NetMod][Steam] Overlay Rich Presence join requested connect={Connect}", connect);
            var lobbyId = TryParseLobbyIdFromConnectString(connect);
            if (lobbyId == 0UL)
            {
                Instance?.Logger.Warning("[NetMod][Steam] Could not parse lobby ID from connect string: {Connect}", connect);
                return;
            }
            EnqueueAndProcessOverlayJoin(lobbyId, "GameRichPresenceJoinRequested_t");
        }

        private static void EnqueueAndProcessOverlayJoin(ulong lobbyId, string source)
        {
            var nowTicks = Environment.TickCount64;
            if (lobbyId == s_lastOverlayJoinLobbyId &&
                nowTicks - s_lastOverlayJoinTicks < SteamOverlayJoinDedupMs)
            {
                Instance?.Logger.Debug("[NetMod][Steam] Ignoring duplicate overlay join request lobbyId={LobbyId} source={Source}", lobbyId, source);
                return;
            }

            s_lastOverlayJoinLobbyId = lobbyId;
            s_lastOverlayJoinTicks = nowTicks;
            Instance?.Logger.Information("[NetMod][Steam] Queueing overlay join request lobbyId={LobbyId} source={Source}", lobbyId, source);
            GameMenu.EnqueueMainThreadCoalesced("steam:overlay-join", () => GameMenu.HandleSteamOverlayJoinRequest(lobbyId));
        }

        private static ulong TryParseLobbyIdFromConnectString(string connect)
        {
            if (string.IsNullOrWhiteSpace(connect))
                return 0UL;
            var parts = connect.Split((char[]?)[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(parts[i + 1], out var lobbyId) && lobbyId > 0)
                    return lobbyId;
            }
            if (ulong.TryParse(connect.Trim(), out var direct) && direct > 0)
                return direct;
            return 0UL;
        }

        internal static bool TryRunSteamCallbacksSerialized()
        {
            if (!s_steamFeaturesRequested || !s_steamApiReady || s_steamUnavailable)
                return false;

            if (Volatile.Read(ref s_steamCallbackShutdown) != 0 ||
                Environment.HasShutdownStarted ||
                AppDomain.CurrentDomain.IsFinalizingForUnload())
            {
                return false;
            }
            if (!Monitor.TryEnter(s_steamCallbackPumpLock))
                return false;

            try
            {
                SteamAPI.RunCallbacks();
                return true;
            }
            catch (Exception ex)
            {
                Instance?.Logger.Debug("[NetMod][Steam] Steam callback pump skipped after error: {Message}", ex.Message);
                return false;
            }
            finally
            {
                Monitor.Exit(s_steamCallbackPumpLock);
            }
        }

        /// <summary>
        /// Call from GameMenu when at main menu so Steam overlay join callbacks are pumped even if frame update is throttled.
        /// </summary>
        internal static void PumpSteamCallbacksForOverlay()
        {
            // LAN/direct-IP sessions never initialize or pump Steam. This keeps GOG and other
            // non-Steam runtimes completely independent from the Steam callback dispatcher.
            if (!s_steamFeaturesRequested || s_steamUnavailable)
                return;

            var callbacksStart = RuntimeHitchWatch.Start();
            TryRunSteamCallbacksSerialized();
            var callbacksMs = RuntimeHitchWatch.GetElapsedMilliseconds(callbacksStart);
            if (callbacksMs >= RuntimeHitchWatch.InteractionSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Instance?.Logger,
                    "ModEntry.TryRunSteamCallbacks",
                    callbacksMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"steamReady={(s_steamApiReady ? 1 : 0)} pendingOverlay={(s_steamOverlayCallbackPending ? 1 : 0)}"));
            }

            var auxStart = RuntimeHitchWatch.Start();
            TryDeferredSteamOverlayCallbackRegistration();
            TryPollSteamOverlayJoinFromLaunchData();
            var auxMs = RuntimeHitchWatch.GetElapsedMilliseconds(auxStart);
            if (auxMs >= RuntimeHitchWatch.InteractionSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Instance?.Logger,
                    "ModEntry.PumpSteamCallbacksForOverlay",
                    auxMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"steamReady={(s_steamApiReady ? 1 : 0)} pendingOverlay={(s_steamOverlayCallbackPending ? 1 : 0)}"));
            }
        }

        internal static bool EnsureSteamApiForNetworking(string source)
        {
            return RequestSteamFeatures(source);
        }

        internal static bool RequestSteamFeatures(string source)
        {
            if (s_steamUnavailable)
                return false;

            s_steamFeaturesRequested = true;
            s_steamOverlayCallbackPending = true;

            if (TryEnsureSteamApiInitialized(source, logFailure: true))
                return true;

            // This was an explicit Steam request, not background probing. Fail once and cleanly
            // leave LAN/direct IP available instead of repeatedly touching an unavailable API.
            MarkSteamUnavailable($"SteamAPI.Init returned false ({source})");
            return false;
        }

        internal static T RunSteamNetworkingSerialized<T>(Func<T> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            lock (s_steamCallbackPumpLock)
                return action();
        }

        internal static void RunSteamNetworkingSerialized(Action action)
        {
            if (action == null)
                return;

            lock (s_steamCallbackPumpLock)
                action();
        }

        private static bool TryEnsureSteamApiInitialized(string source, bool logFailure)
        {
            if (s_steamApiReady)
                return true;

            if (s_steamUnavailable)
                return false;

            try
            {
                SteamConnect.PrepareSteamNativePathForRuntime();
                lock (s_steamCallbackPumpLock)
                {
                    if (s_steamApiReady)
                        return true;
                    if (SteamAPI.Init())
                    {
                        s_steamFeaturesRequested = true;
                        s_steamApiReady = true;
                        Instance?.Logger.Information("[NetMod][Steam] SteamAPI.Init succeeded ({Source})", source);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // No steam_api64.dll, no running Steam client, or a non-Steam install: Init can
                // THROW here rather than return false. That must never escape into the game loop.
                MarkSteamUnavailable($"SteamAPI.Init threw: {ex.GetType().Name}");
                return false;
            }

            if (logFailure)
                Instance?.Logger.Debug("[NetMod][Steam] SteamAPI.Init returned false ({Source})", source);

            return false;
        }

        private static void TryPollSteamOverlayJoinFromLaunchData()
        {
            if (!s_steamApiReady)
                return;

            var nowTicks = Environment.TickCount64;
            if (nowTicks < s_nextSteamLaunchPollTicks)
                return;

            s_nextSteamLaunchPollTicks = nowTicks + SteamOverlayLaunchPollIntervalMs;

            string steamLaunchCommand = string.Empty;
            var launchCommandLength = SteamApps.GetLaunchCommandLine(out steamLaunchCommand, 2048);
            steamLaunchCommand = (steamLaunchCommand ?? string.Empty).Trim();
            if (launchCommandLength > 0 &&
                !string.IsNullOrWhiteSpace(steamLaunchCommand) &&
                !string.Equals(steamLaunchCommand, s_lastSteamLaunchCommand, StringComparison.Ordinal))
            {
                s_lastSteamLaunchCommand = steamLaunchCommand;
                var lobbyId = TryParseLobbyIdFromConnectString(steamLaunchCommand);
                if (lobbyId > 0UL)
                {
                    Instance?.Logger.Information("[NetMod][Steam] Detected overlay join from Steam launch command: {Command}", steamLaunchCommand);
                    EnqueueAndProcessOverlayJoin(lobbyId, "SteamApps.GetLaunchCommandLine");
                    return;
                }
            }

            var connectLobby = (SteamApps.GetLaunchQueryParam("connect_lobby") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(connectLobby) ||
                string.Equals(connectLobby, s_lastSteamLaunchConnectLobbyParam, StringComparison.Ordinal))
                return;

            s_lastSteamLaunchConnectLobbyParam = connectLobby;
            if (ulong.TryParse(connectLobby, out var lobbyId2) && lobbyId2 > 0UL)
            {
                Instance?.Logger.Information("[NetMod][Steam] Detected overlay join from Steam launch query param connect_lobby={LobbyId}", lobbyId2);
                EnqueueAndProcessOverlayJoin(lobbyId2, "SteamApps.GetLaunchQueryParam");
            }
        }

        /// <summary>
        /// Background timer pumps Steam callbacks so overlay Join works when game loop is paused (overlay open).
        /// Callbacks run on timer thread; we EnqueueMainThread for game ops.
        /// </summary>
        private static void StartSteamCallbackPumpTimer()
        {
            if (!s_steamApiReady || s_steamUnavailable ||
                Volatile.Read(ref s_steamCallbackShutdown) != 0)
                return;

            EnsureSteamProcessExitHook();

            // Never call SteamAPI.RunCallbacks from a ThreadPool timer. Dead Cells can invalidate its
            // Steam pipe during level/main-menu teardown while that timer is still alive; the native
            // ManualDispatch call then raises an uncatchable 0xC0000005. Boot/Hero/menu hooks already
            // pump callbacks every game frame on the main thread, which is both sufficient and safe.
            var staleTimer = Interlocked.Exchange(ref s_steamCallbackPumpTimer, null);
            try { staleTimer?.Dispose(); } catch { }

            if (!s_steamMainThreadPumpLogged)
            {
                s_steamMainThreadPumpLogged = true;
                Instance?.Logger.Information("[NetMod][Steam] Callback pump uses main-thread hooks only");
            }
        }

        private static void EnsureSteamProcessExitHook()
        {
            if (s_steamProcessExitHookInstalled)
                return;
            lock (s_steamCallbackPumpLock)
            {
                if (s_steamProcessExitHookInstalled)
                    return;
                AppDomain.CurrentDomain.ProcessExit += OnSteamProcessExit;
                s_steamProcessExitHookInstalled = true;
            }
        }

        private static void OnSteamProcessExit(object? sender, EventArgs e)
        {
            Interlocked.Exchange(ref s_steamCallbackShutdown, 1);
            var timer = Interlocked.Exchange(ref s_steamCallbackPumpTimer, null);
            try { timer?.Dispose(); } catch { }

            // Do not dispose callback wrappers while another timer/game-thread pump is inside
            // SteamAPI.RunCallbacks. That shutdown race can execute managed callback code after
            // the runtime has started tearing down.
            lock (s_steamCallbackPumpLock)
            {
                try { s_steamOverlayJoinCallback?.Dispose(); } catch { }
                try { s_steamRichPresenceJoinCallback?.Dispose(); } catch { }
                s_steamOverlayJoinCallback = null;
                s_steamRichPresenceJoinCallback = null;
            }
        }
    }
}

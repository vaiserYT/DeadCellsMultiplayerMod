using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using dc.pr;
using dc.ui;
using Newtonsoft.Json;
using Serilog;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.PortableCore;
using ModCore.Modules;
using HaxeProxy.Runtime;


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
        private static int _serverSeedSequence;
        private static int _remoteSeedSequence;
        private static int _consumedRemoteSeedSequence;
        private static string _remoteLaunchKind = string.Empty;
        // Protocol 17: the launch is gated so the authoritative seed is present before newGame runs.
        // This wait is now only a short scheduling tolerance, not a 10s main-thread barrier.
        private const int RemoteRunSeedWaitMs = 2000;
        private const int RunSeedTransitionGraceMs = 2000;
        private const int MaxSeed = 999_999;
        public static NetNode? NetRef { get; set; }
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        // Network protocol work must never be silently dropped: losing a death, level, revive, or
        // interaction message creates permanent host/client divergence. A bounded channel applies
        // back-pressure to the receive loop while the game thread is loading or paused.
        private static readonly Channel<Action> _networkMainThreadQueue = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(2048)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        private static readonly object MainThreadCoalesceSync = new();
        private static readonly Dictionary<string, Action> _coalescedActions = new(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<string> _coalescedKeys = new();
        // Death/revive/restart/session transitions must not wait behind a continuous stream of
        // visual/network work. Critical actions are coalesced by fixed keys and get first chance
        // during each pump, while still remaining bounded.
        private static readonly object CriticalMainThreadCoalesceSync = new();
        private static readonly Dictionary<string, Action> _criticalCoalescedActions = new(StringComparer.Ordinal);
        private static readonly ConcurrentQueue<string> _criticalCoalescedKeys = new();
        private const int MainThreadQueueMaxActionsPerPump = 128;
        private const int MainThreadQueueMaxPendingDirect = 512;
        private const int MainThreadQueueMaxPendingCoalesced = 512;
        private const int MainThreadQueueMaxPendingCritical = 64;
        private static int _mainThreadDirectQueueCount;
        private static long _lastMainThreadQueueDropLogTicks;
        private static long _lastMainThreadCoalescedDropLogTicks;

        private static bool _menuHooksAttached;
        private static bool _addMenuHookRegistered;
        private static bool _mainMenuButtonAdded;
        private static bool _addingMultiplayerButton;
        private const int MultiplayerMainMenuTextColor = 0x7FD4FF; // soft blue
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
        private static bool _steamLobbyActive;
        private static ulong _steamLobbyId;
        private static string _steamLobbyCode = string.Empty;
        private static ulong _steamHostSteamId;
        private static bool _steamJoinLobbyResolvePending;
        private static int _steamJoinResolveGeneration;
        private static ulong? _pendingOverlayJoinLobbyId;
        private static bool _waitingForHost;
        private static int _roomStatusMenuKind; // 0 none, 1 host, 2 client
        private static DateTime _lastRoomStatusAutoRefresh = DateTime.MinValue;
        internal const int ClientConnectMaxAttempts = 3;
        private static int _clientConnectAttempt;
        private static bool _clientConnecting;
        private static bool _pendingAutoStart;
        private static bool _levelDescArrived;
        private static bool _autoStartTriggered;
        private static DateTime _autoStartRetryAt = DateTime.MinValue;
        private const int DeathRestartCooldownMs = 1000;
        private static DateTime _deathRestartCooldownUntil = DateTime.MinValue;
        // While a client full-run restart (from host seed) is pending, the host's freshly broadcast level
        // graph for the restart level must NOT trigger an in-place reloadAfterBossRuneModif on the client:
        // that collides with the queued launchGame restart and leaves the old downed hero / Game Over stuck.
        private static long _clientRestartPendingUntilTicks;
        private const int ClientRestartPendingTtlMs = 12000;
        private const string AutoStartMutexName = "DeadCellsMultiplayerMod.AutoStart";
        private static bool _worldExitHandled;
        private static bool _hostDisconnectCountdownActive;
        private static DateTime _hostDisconnectCountdownUntil = DateTime.MinValue;
        private static int _lastHostDisconnectCountdown = -1;
        private const int HostDisconnectCountdownSeconds = 5;
        private static bool _seedArrived;
        // The title-screen Start button can enter the opening cinematic before User.newGame is
        // invoked. Precommit and broadcast the initial seed here so connected clients can leave
        // the lobby immediately instead of waiting for the host cinematic to finish.
        private static int? _precommittedHostSeed;
        private static int _precommittedHostSeedSequence;
        private static string _precommittedHostLaunchKind = string.Empty;
        private static long _precommittedHostSeedExpiresAtTicks;
        private const int PrecommittedHostSeedTtlMs = 300000;
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

        /// <summary>True while clipboard/overlay join is resolving the Steam lobby.</summary>
        internal static bool IsSteamJoinLobbyResolvePending() => _steamJoinLobbyResolvePending;
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
            RunMultiplayerSaveStartupRecovery(logger);
            lock (Sync)
            {
                _log = logger;
                _role = NetRole.None;
                _inActualRun = false;
                _serverSeed = null;
                _remoteSeed = null;
                _serverSeedSequence = 0;
                _remoteSeedSequence = 0;
                _consumedRemoteSeedSequence = 0;
                _remoteLaunchKind = string.Empty;
                _levelDescArrived = false;
                _pendingAutoStart = false;
                _autoStartTriggered = false;
                _seedArrived = false;
                ClearStructuredLaunchFlagsLocked();
                _precommittedHostSeed = null;
                _precommittedHostSeedSequence = 0;
                _precommittedHostLaunchKind = string.Empty;
                _precommittedHostSeedExpiresAtTicks = 0;
                _clientConnectAttempt = 0;
                _clientConnecting = false;
                _deathRestartCooldownUntil = DateTime.MinValue;
                _cachedLevelDescSync = null;
                _hostDisconnectCountdownActive = false;
                _hostDisconnectCountdownUntil = DateTime.MinValue;
                _lastHostDisconnectCountdown = -1;
                _menuTransport = ConnectionTransport.Lan;
                _steamLobbyActive = false;
                _steamLobbyId = 0;
                _steamLobbyCode = string.Empty;
                _steamHostSteamId = 0UL;
                _steamJoinLobbyResolvePending = false;
                Interlocked.Increment(ref _steamJoinResolveGeneration);
                while (_mainThreadQueue.TryDequeue(out _)) { }
                while (_networkMainThreadQueue.Reader.TryRead(out _)) { }
                Interlocked.Exchange(ref _mainThreadDirectQueueCount, 0);
                while (_coalescedKeys.TryDequeue(out _)) { }
                lock (MainThreadCoalesceSync)
                    _coalescedActions.Clear();
                while (_criticalCoalescedKeys.TryDequeue(out _)) { }
                lock (CriticalMainThreadCoalesceSync)
                    _criticalCoalescedActions.Clear();
            }

            InitializeMenuUiHooks();
        }

        internal static void EnqueueMainThread(Action action)
        {
            if (action == null) return;

            var pending = Interlocked.Increment(ref _mainThreadDirectQueueCount);
            if (pending > MainThreadQueueMaxPendingDirect)
            {
                Interlocked.Decrement(ref _mainThreadDirectQueueCount);
                LogMainThreadQueueDropRateLimited(pending);
                return;
            }

            _mainThreadQueue.Enqueue(action);
        }


        internal static ValueTask EnqueueNetworkMainThreadAsync(Action action, CancellationToken cancellationToken)
        {
            if (action == null)
                return ValueTask.CompletedTask;

            return _networkMainThreadQueue.Writer.WriteAsync(action, cancellationToken);
        }

        internal static void ClearPendingNetworkMainThreadActions()
        {
            while (_networkMainThreadQueue.Reader.TryRead(out _)) { }
        }

        private static void LogMainThreadQueueDropRateLimited(int pending)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var minTicks = System.Diagnostics.Stopwatch.Frequency * 5L;
            var previous = Interlocked.Read(ref _lastMainThreadQueueDropLogTicks);
            if (previous != 0 && now - previous < minTicks)
                return;
            if (Interlocked.CompareExchange(ref _lastMainThreadQueueDropLogTicks, now, previous) != previous)
                return;

            _log?.Warning(
                "[NetMod] Dropped main-thread work because the direct queue exceeded {MaxPending} actions (observed={Pending})",
                MainThreadQueueMaxPendingDirect,
                pending);
        }

        internal static void EnqueueMainThreadCoalesced(string coalesceKey, Action action)
        {
            if (action == null)
                return;

            if (string.IsNullOrWhiteSpace(coalesceKey))
            {
                EnqueueMainThread(action);
                return;
            }

            bool isNewKey;
            lock (MainThreadCoalesceSync)
            {
                isNewKey = !_coalescedActions.ContainsKey(coalesceKey);
                if (isNewKey && _coalescedActions.Count >= MainThreadQueueMaxPendingCoalesced)
                {
                    LogMainThreadCoalescedDropRateLimited(coalesceKey, critical: false);
                    return;
                }
                _coalescedActions[coalesceKey] = action;
            }

            if (isNewKey)
                _coalescedKeys.Enqueue(coalesceKey);
        }

        internal static void EnqueueCriticalMainThreadCoalesced(string coalesceKey, Action action)
        {
            if (action == null || string.IsNullOrWhiteSpace(coalesceKey))
                return;

            bool isNewKey;
            lock (CriticalMainThreadCoalesceSync)
            {
                isNewKey = !_criticalCoalescedActions.ContainsKey(coalesceKey);
                if (isNewKey && _criticalCoalescedActions.Count >= MainThreadQueueMaxPendingCritical)
                {
                    LogMainThreadCoalescedDropRateLimited(coalesceKey, critical: true);
                    return;
                }
                _criticalCoalescedActions[coalesceKey] = action;
            }

            if (isNewKey)
                _criticalCoalescedKeys.Enqueue(coalesceKey);
        }

        private static void LogMainThreadCoalescedDropRateLimited(string key, bool critical)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var minTicks = System.Diagnostics.Stopwatch.Frequency * 5L;
            var previous = Interlocked.Read(ref _lastMainThreadCoalescedDropLogTicks);
            if (previous != 0 && now - previous < minTicks)
                return;
            if (Interlocked.CompareExchange(ref _lastMainThreadCoalescedDropLogTicks, now, previous) != previous)
                return;

            _log?.Warning(
                "[NetMod] Rejected {Kind} coalesced main-thread work because its queue is full (key={Key})",
                critical ? "critical" : "normal",
                key);
        }

        internal static void ProcessMainThreadQueue()
        {
            var processed = 0;
            while (processed < MainThreadQueueMaxActionsPerPump)
            {
                Action? action = null;

                if (_criticalCoalescedKeys.TryDequeue(out var criticalKey))
                {
                    lock (CriticalMainThreadCoalesceSync)
                    {
                        _criticalCoalescedActions.TryGetValue(criticalKey, out action);
                        _criticalCoalescedActions.Remove(criticalKey);
                    }
                }
                else
                {
                    // Three fifths of the regular budget goes to protocol traffic. Direct and
                    // coalesced work each receive a reserved turn so neither can starve forever.
                    var phase = processed % 5;
                    Action? networkAction;
                    if (phase <= 2 && _networkMainThreadQueue.Reader.TryRead(out networkAction))
                    {
                        action = networkAction;
                    }
                    else if (phase == 3 && _mainThreadQueue.TryDequeue(out var directPreferred))
                    {
                        Interlocked.Decrement(ref _mainThreadDirectQueueCount);
                        action = directPreferred;
                    }
                    else if (phase == 4 && _coalescedKeys.TryDequeue(out var preferredKey))
                    {
                        lock (MainThreadCoalesceSync)
                        {
                            _coalescedActions.TryGetValue(preferredKey, out action);
                            _coalescedActions.Remove(preferredKey);
                        }
                    }
                    else if (_networkMainThreadQueue.Reader.TryRead(out networkAction))
                    {
                        action = networkAction;
                    }
                    else if (_mainThreadQueue.TryDequeue(out var direct))
                    {
                        Interlocked.Decrement(ref _mainThreadDirectQueueCount);
                        action = direct;
                    }
                    else if (_coalescedKeys.TryDequeue(out var key))
                    {
                        lock (MainThreadCoalesceSync)
                        {
                            _coalescedActions.TryGetValue(key, out action);
                            _coalescedActions.Remove(key);
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                if (action == null)
                    continue;

                processed++;
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Main thread task failed: {Message}", ex.Message);
                }
            }
        }

        public static void MarkInRun()
        {
            lock (Sync)
            {
                _inActualRun = true;
            }
            // The (re)started run's hero is up — the restart completed, so stop suppressing level reloads.
            ClearClientRestartPending();
            SendRunReadyFromHero();
        }

        internal static void MarkClientRestartPending()
        {
            Volatile.Write(ref _clientRestartPendingUntilTicks, Environment.TickCount64 + ClientRestartPendingTtlMs);
        }

        internal static void ClearClientRestartPending()
        {
            Volatile.Write(ref _clientRestartPendingUntilTicks, 0);
        }

        internal static bool IsClientRestartPending()
        {
            var until = Volatile.Read(ref _clientRestartPendingUntilTicks);
            return until != 0 && Environment.TickCount64 < until;
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
            _log?.Information("[NetMod] Generated host seed {Seed} ({Reason})", seed, reason);
            return seed;
        }

        /// <summary>
        /// Commits one host launch to the wire. The monotonic sequence prevents a client entering
        /// Boss Rush (or another nested launch mode) from accidentally reusing the previous run's
        /// cached seed while the new SEED packet is still in flight.
        /// </summary>
        public static int RegisterHostRunSeed(int seed, string launchKind, string reason)
        {
            int sequence;
            lock (Sync)
            {
                _serverSeed = seed;
                sequence = _serverSeedSequence == int.MaxValue ? 1 : _serverSeedSequence + 1;
                _serverSeedSequence = sequence;
            }

            _log?.Information(
                "[NetMod] Registered host run seed seq={Sequence} seed={Seed} launch={LaunchKind} ({Reason})",
                sequence,
                seed,
                launchKind ?? string.Empty,
                reason);
            return sequence;
        }

        internal static bool PrecommitInitialHostRunSeed(out int seed, out int sequence, out RunLaunchDescriptor? descriptor)
        {
            seed = 0;
            sequence = 0;
            descriptor = null;

            var net = NetRef;
            if (net == null || !net.IsAlive || !net.IsHost)
                return false;

            const string launchKind = "dc.LaunchMode+NewGame";

            seed = ForceGenerateServerSeed("title.startNewGame_precommit");
            sequence = RegisterHostRunSeed(seed, launchKind, "title.startNewGame_precommit");

            lock (Sync)
            {
                _precommittedHostSeed = seed;
                _precommittedHostSeedSequence = sequence;
                _precommittedHostLaunchKind = launchKind;
                _precommittedHostSeedExpiresAtTicks = Environment.TickCount64 + PrecommittedHostSeedTtlMs;
            }

            descriptor = BuildHostRunLaunchDescriptor(seed, sequence, launchKind);
            net.SendRunLaunchCommit(descriptor, flush: true);

            // The legacy seed packet remains during protocol migration, but clients no longer
            // execute from it until the matching structured RUNEXEC has arrived.
            // Send it before the host enters any
            // first-run cinematic; User.newGame will reuse and resend this same sequence later.
            net.SendSeed(sequence, seed, launchKind);
            // The normal send is cached for late joiners. The bounded flush makes sure the
            // connected client receives the launch packet before the title screen changes state.
            net.SendControlAndFlush($"SEED|{sequence}|{seed}|{launchKind}", 500);
            _log?.Information(
                "[NetMod] Precommitted initial host run seq={Sequence} seed={Seed} launch={LaunchKind}",
                sequence,
                seed,
                launchKind);
            return true;
        }

        /// <summary>
        /// Stages the Boss Rush seed before either game enters the native Boss Rush loader. The
        /// BossRushDoor transition is already coordinated by LevelExitSync, so sending the
        /// structured commit/execute before the door-ready state guarantees the client has the
        /// authoritative seed waiting when its own User.newGame hook runs.
        /// </summary>
        internal static bool PrecommitHostBossRushRunSeed(
            string bossRushType,
            int doorCx,
            int doorCy,
            out int seed,
            out int sequence)
        {
            const string launchKind = "dc.LaunchMode+BossRush";
            seed = 0;
            sequence = 0;

            var net = NetRef;
            if (net == null || !net.IsAlive || !net.IsHost)
                return false;

            lock (Sync)
            {
                var expired = _precommittedHostSeedExpiresAtTicks != 0 &&
                              Environment.TickCount64 >= _precommittedHostSeedExpiresAtTicks;
                if (expired)
                    ClearPrecommittedHostRunSeedLocked();

                if (_precommittedHostSeed.HasValue &&
                    _precommittedHostSeedSequence > 0 &&
                    GameDataSync.IsBossRushLaunchKind(_precommittedHostLaunchKind))
                {
                    seed = _precommittedHostSeed.Value;
                    sequence = _precommittedHostSeedSequence;
                }
            }

            if (sequence <= 0)
            {
                seed = ForceGenerateServerSeed("bossrush_door_precommit");
                sequence = RegisterHostRunSeed(seed, launchKind, "bossrush_door_precommit");

                lock (Sync)
                {
                    _precommittedHostSeed = seed;
                    _precommittedHostSeedSequence = sequence;
                    _precommittedHostLaunchKind = launchKind;
                    _precommittedHostSeedExpiresAtTicks = Environment.TickCount64 + PrecommittedHostSeedTtlMs;
                }
            }

            try
            {
                // Commit and execute before the synchronized door-ready packet. Steam/TCP preserve
                // ordering, so the client receives this launch before it is told to activate the
                // matching local BossRushDoor. The native Boss Rush variant read from the door
                // (bossRushType) is carried so the client can validate it against its own door.
                CommitHostRunLaunchFromHook(seed, sequence, launchKind, bossRushType);
                net.SendSeed(sequence, seed, launchKind);
                net.SendControlAndFlush($"SEED|{sequence}|{seed}|{launchKind}", 500);
                _log?.Information(
                    "[NetMod][BossRushSeed] Precommitted seq={Sequence} seed={Seed} type={BossRushType} door={DoorCx}:{DoorCy}",
                    sequence,
                    seed,
                    string.IsNullOrWhiteSpace(bossRushType) ? "unknown" : bossRushType,
                    doorCx,
                    doorCy);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warning(
                    "[NetMod][BossRushSeed] Failed to precommit Boss Rush seed at door={DoorCx}:{DoorCy}: {Message}",
                    doorCx,
                    doorCy,
                    ex.Message);
                return false;
            }
        }

        internal static bool HasPrecommittedHostBossRushLaunch()
        {
            lock (Sync)
            {
                var expired = _precommittedHostSeedExpiresAtTicks != 0 &&
                              Environment.TickCount64 >= _precommittedHostSeedExpiresAtTicks;
                if (expired)
                    ClearPrecommittedHostRunSeedLocked();

                return _precommittedHostSeed.HasValue &&
                       _precommittedHostSeedSequence > 0 &&
                       GameDataSync.IsBossRushLaunchKind(_precommittedHostLaunchKind);
            }
        }

        internal static bool HasPendingRemoteBossRushLaunch()
        {
            var descriptor = RunLaunchCoordinator.GetCurrentRemoteDescriptor();
            if (descriptor == null || !descriptor.BossRush)
                return false;

            lock (Sync)
            {
                return descriptor.Sequence > _consumedRemoteSeedSequence &&
                       _remoteSeedSequence == descriptor.Sequence &&
                       RunLaunchCoordinator.HasExecutableRemoteLaunch(descriptor.Sequence);
            }
        }

        internal static bool TryGetPendingRemoteBossRushSeed(out int seed)
        {
            lock (Sync)
            {
                if (_remoteSeed.HasValue &&
                    _remoteSeedSequence > _consumedRemoteSeedSequence &&
                    GameDataSync.IsBossRushLaunchKind(_remoteLaunchKind))
                {
                    seed = _remoteSeed.Value;
                    return true;
                }
            }

            seed = 0;
            return false;
        }

        internal static bool TryConsumePrecommittedHostRunSeed(
            string launchKind,
            out int seed,
            out int sequence)
        {
            lock (Sync)
            {
                var expired = _precommittedHostSeedExpiresAtTicks != 0 &&
                              Environment.TickCount64 >= _precommittedHostSeedExpiresAtTicks;
                if (expired)
                    ClearPrecommittedHostRunSeedLocked();

                if (!_precommittedHostSeed.HasValue || _precommittedHostSeedSequence <= 0)
                {
                    seed = 0;
                    sequence = 0;
                    return false;
                }

                var requestedNewGame = !string.IsNullOrWhiteSpace(launchKind) &&
                                       launchKind.Contains("NewGame", StringComparison.OrdinalIgnoreCase);
                var stagedNewGame = !string.IsNullOrWhiteSpace(_precommittedHostLaunchKind) &&
                                    _precommittedHostLaunchKind.Contains("NewGame", StringComparison.OrdinalIgnoreCase);
                var requestedBossRush = GameDataSync.IsBossRushLaunchKind(launchKind);
                var stagedBossRush = GameDataSync.IsBossRushLaunchKind(_precommittedHostLaunchKind);
                if (!string.Equals(launchKind, _precommittedHostLaunchKind, StringComparison.Ordinal) &&
                    !(requestedNewGame && stagedNewGame) &&
                    !(requestedBossRush && stagedBossRush))
                {
                    seed = 0;
                    sequence = 0;
                    return false;
                }

                seed = _precommittedHostSeed.Value;
                sequence = _precommittedHostSeedSequence;
                ClearPrecommittedHostRunSeedLocked();
                return true;
            }
        }

        internal static void CancelPrecommittedHostRunSeed(string reason = "precommitted_launch_cancelled")
        {
            int sequence;
            lock (Sync)
            {
                sequence = _precommittedHostSeedSequence;
                ClearPrecommittedHostRunSeedLocked();
            }

            if (sequence > 0)
                CancelHostStructuredLaunch(sequence, reason);
        }

        private static void ClearPrecommittedHostRunSeedLocked()
        {
            _precommittedHostSeed = null;
            _precommittedHostSeedSequence = 0;
            _precommittedHostLaunchKind = string.Empty;
            _precommittedHostSeedExpiresAtTicks = 0;
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

        public static bool TryGetKnownSeed(out int seed)
        {
            lock (Sync)
            {
                if (_serverSeed.HasValue)
                {
                    seed = _serverSeed.Value;
                    return true;
                }
                if (_remoteSeed.HasValue)
                {
                    seed = _remoteSeed.Value;
                    return true;
                }
            }

            seed = 0;
            return false;
        }

        public static void ReceiveHostRunSeed(int sequence, int seed, string launchKind)
        {
            var scheduleInRunReconcile = false;
            lock (Sync)
            {
                if (sequence <= 0)
                    return;

                if (sequence < _remoteSeedSequence)
                    return;

                if (sequence == _remoteSeedSequence)
                {
                    if (_remoteSeed == seed)
                        Monitor.PulseAll(Sync);
                    return;
                }

                _remoteSeed = seed;
                _remoteSeedSequence = sequence;
                _remoteLaunchKind = launchKind ?? string.Empty;
                if (_role == NetRole.Client)
                {
                    // A Boss Rush seed must only be consumed by the client's own Boss Rush launch
                    // hook. Force-restarting the run on it (the reconcile path) was the historical
                    // double-load race, and auto-starting a fresh full run from it would launch
                    // the wrong mode entirely.
                    var isBossRushSeed = GameDataSync.IsBossRushLaunchKind(launchKind);
                    if (_inActualRun)
                    {
                        // A nested launch hook (Boss Rush, challenge, daily, etc.) consumes this
                        // sequence directly. If no hook consumes it within a short grace window,
                        // treat it as a host restart/late-join recovery and rebuild from the seed.
                        scheduleInRunReconcile = sequence > _consumedRemoteSeedSequence && !isBossRushSeed;
                    }
                    else
                    {
                        _seedArrived = true;
                        if (!isBossRushSeed && CanAutoStartStructuredClientLaunchLocked())
                            _pendingAutoStart = true;
                    }
                }
                Monitor.PulseAll(Sync);
            }
            _log?.Information(
                "[NetMod] Client received host run seed seq={Sequence} seed={Seed} launch={LaunchKind}",
                sequence,
                seed,
                launchKind ?? string.Empty);

            if (scheduleInRunReconcile)
                ScheduleClientRunSeedReconcile(sequence, seed);
        }

        private static void ScheduleClientRunSeedReconcile(int sequence, int seed)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(RunSeedTransitionGraceMs).ConfigureAwait(false);
                EnqueueCriticalMainThreadCoalesced("game:run-seed-reconcile", () =>
                {
                    var shouldRestart = false;
                    lock (Sync)
                    {
                        if (_role == NetRole.Client &&
                            _inActualRun &&
                            _remoteSeedSequence == sequence &&
                            _consumedRemoteSeedSequence < sequence)
                        {
                            _inActualRun = false;
                            _pendingAutoStart = false;
                            _autoStartTriggered = false;
                            shouldRestart = true;
                        }
                    }

                    if (shouldRestart)
                        QueueClientRestartFromHostSeed(seed, $"unconsumed_host_launch_seq_{sequence}");
                });
            });
        }

        /// <summary>
        /// Waits for and consumes exactly one not-yet-used host launch seed. Network receive runs on
        /// a background thread, so the game launch hook can safely form a short deterministic barrier.
        /// </summary>
        public static bool TryConsumeNextRemoteRunSeed(out int seed, out int sequence, out string launchKind)
        {
            if (RunLaunchCoordinator.TryConsumeRemoteLaunch(
                    RemoteRunSeedWaitMs,
                    out var descriptor,
                    out var error) &&
                descriptor != null)
            {
                seed = descriptor.RunSeed;
                sequence = descriptor.Sequence;
                launchKind = descriptor.LaunchKind;
                lock (Sync)
                {
                    _remoteSeed = seed;
                    _remoteSeedSequence = sequence;
                    _remoteLaunchKind = launchKind;
                    _consumedRemoteSeedSequence = sequence;
                    _seedArrived = true;
                    Monitor.PulseAll(Sync);
                }
                return true;
            }

            _log?.Error("[NetMod][RunLaunch] {Error}", error);
            seed = 0;
            sequence = 0;
            launchKind = string.Empty;
            return false;
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
                try
                {
                    var main = dc.Main.Class.ME;
                    if (main != null)
                    {
                        main.launchGame(GameDataSync._launch, null, 0.8);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Host launchGame restart failed, fallback to direct newGame: {Message}", ex.Message);
                }

                game.destroy();
                game.disposeImmediately();
                game.user.newGame(GameDataSync.Seed, GameDataSync._isTwitch, GameDataSync._isCustom, GameDataSync._mode, GameDataSync._launch);
            });
        }

        private static void QueueClientRestartFromHostSeed(int seed, string reason)
        {
            // Set synchronously (before the queued action runs) so any level graph that arrives in the
            // meantime is prevented from firing an in-place reload that would pre-empt this full restart.
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
                        _pendingAutoStart = true;
                        _autoStartTriggered = false;
                    }
                    return;
                }

                _log?.Information("[NetMod] Client restarting run from host seed {Seed} ({Reason})", seed, reason);
                try
                {
                    var main = dc.Main.Class.ME;
                    if (main != null)
                    {
                        main.launchGame(GameDataSync._launch, null, 0.8);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Client launchGame restart failed, fallback to direct newGame: {Message}", ex.Message);
                }

                game.destroy();
                game.disposeImmediately();
                game.user.newGame(seed, GameDataSync._isTwitch, GameDataSync._isCustom, GameDataSync._mode, GameDataSync._launch);
            });
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
                _remoteUsername = cleaned;
            }
            var changed = !string.Equals(previous, cleaned, StringComparison.Ordinal);
            if (changed)
                _log?.Information("[NetMod] Received remote username {Username}", cleaned);
            if (_role == NetRole.Host && changed)
            {
                var userForMsg = cleaned;
                EnqueueMainThread(() =>
                    MultiplayerUI.PushSystemMessage(FormatLocalized("{0} connected to the server.", userForMsg)));
            }
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

            if (levelDesc == null)
                return;

            var payload = new
            {
                levelDesc = levelDesc ?? new LevelDescSync(),
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

        public static void TickMenu(double dt)
        {
            UpdateHostDisconnectCountdown();
            if (DateTime.UtcNow < _autoStartRetryAt)
                return;

            bool shouldStart = false;
            int autoStartQueuedSequence = 0;

            lock (Sync)
            {
                if (_role == NetRole.Client &&
                    !_inActualRun &&
                    _pendingAutoStart &&
                    _seedArrived &&
                    CanAutoStartStructuredClientLaunchLocked() &&
                    !_autoStartTriggered)
                {
                    _autoStartTriggered = true;
                    shouldStart = true;
                    autoStartQueuedSequence = _structuredLaunchExecuteSequence;
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
                                _autoStartTriggered = false;
                                _pendingAutoStart = true;
                            }
                            _autoStartRetryAt = DateTime.UtcNow.AddMilliseconds(250);
                            return;
                        }

                        ts.startNewGame(custom: true);
                    }
                    finally
                    {
                        if (hasHandle)
                            mutex?.ReleaseMutex();
                        mutex?.Dispose();
                    }
                    _log?.Information("[NetMod] Auto-started new game after seed");
                    // Protocol 17 correction: only now that the client has actually invoked the native
                    // new game do we confirm RUNQUEUED to the host (which is held until this arrives).
                    NotifyClientLaunchQueued(autoStartQueuedSequence);
                }
                catch (IOException ioEx)
                {
                    _log?.Warning("[NetMod] Auto-start blocked by config lock: {Message}", ioEx.Message);
                    lock (Sync)
                    {
                        _autoStartTriggered = false;
                        _pendingAutoStart = true;
                    }
                    _autoStartRetryAt = DateTime.UtcNow.AddSeconds(1.5);
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

        private static void ShowMultiplayerMenu(TitleScreen screen)
        {
            _roomStatusMenuKind = 0;
            screen.clearMenu();
            AddInfoLine(screen, GetText.Instance.GetString("Co-op"), 0xFFE48A);
            AddMenuButton(screen, GetText.Instance.GetString("Host room"), () => ShowHostTransportMenu(screen), GetText.Instance.GetString("Create a Steam or IP/VPN room"));
            AddMenuButton(screen, GetText.Instance.GetString("Join room"), () => ShowJoinTransportMenu(screen), GetText.Instance.GetString("Join with Steam invite/lobby code or IP"));
            AddMenuButton(screen, GetMultiplayerSaveButtonLabel(), () => OpenMultiplayerSlotMenu(screen), Localize("Choose multiplayer save slot"));
            AddMenuButton(screen, GetText.Instance.GetString("Back"), () => screen.mainMenu(), GetText.Instance.GetString("Return to main menu"));
        }

        private static void ShowHostTransportMenu(TitleScreen screen)
        {
            if (!ModEntry.IsSteamAvailable)
            {
                ShowLanConnectionMenu(screen, NetRole.Host);
                return;
            }

            _roomStatusMenuKind = 0;
            screen.clearMenu();
            AddInfoLine(screen, GetText.Instance.GetString("Host room"), 0xFFE48A);
            AddMenuButton(screen, GetText.Instance.GetString("Steam friends lobby"), () => NativeStartSteamHost(screen), GetText.Instance.GetString("Create Steam lobby and invite friends"));
            AddMenuButton(screen, GetText.Instance.GetString("IP / VPN lobby"), () => ShowLanConnectionMenu(screen, NetRole.Host), GetText.Instance.GetString("Hamachi, Radmin, ZeroTier, LAN or port forward"));
            AddMenuButton(screen, GetText.Instance.GetString("Back"), () => ShowMultiplayerMenu(screen), GetText.Instance.GetString("Back to multiplayer menu"));
        }

        private static void ShowJoinTransportMenu(TitleScreen screen)
        {
            if (!ModEntry.IsSteamAvailable)
            {
                ShowLanConnectionMenu(screen, NetRole.Client);
                return;
            }

            _roomStatusMenuKind = 0;
            screen.clearMenu();
            AddInfoLine(screen, GetText.Instance.GetString("Join room"), 0xFFE48A);
            AddMenuButton(screen, GetText.Instance.GetString("Join Steam invite/code"), () => NativeStartSteamJoin(screen), GetText.Instance.GetString("Use lobby code from clipboard or accepted Steam invite"));
            AddMenuButton(screen, GetText.Instance.GetString("Join IP / VPN"), () => ShowLanConnectionMenu(screen, NetRole.Client), GetText.Instance.GetString("Connect by Hamachi/Radmin/ZeroTier/IP"));
            AddMenuButton(screen, GetText.Instance.GetString("Back"), () => ShowMultiplayerMenu(screen), GetText.Instance.GetString("Back to multiplayer menu"));
        }

        private static void ShowLanConnectionMenu(TitleScreen screen, NetRole role)
        {
            _roomStatusMenuKind = 0;
            _menuSelection = role;
            _menuTransport = ConnectionTransport.Lan;
            if (role == NetRole.Client)
                _waitingForHost = true;

            screen.clearMenu();

            AddMenuButton(screen, $"{GetText.Instance.GetString("Username: ")}{_username}", () =>
                OpenTextInput(screen, GetText.Instance.GetString("Username"), _username, value =>
                {
                    _username = CleanUsername(value);
                    SaveConfig();
                    SendUsernameToRemote();
                    ShowLanConnectionMenu(screen, role);
                }, noSpaces: true), GetText.Instance.GetString("Edit display name"));

            AddMenuButton(screen, $"{GetText.Instance.GetString("IP: ")}{_mpIp}", () =>
                OpenTextInput(screen, GetText.Instance.GetString("IP address"), _mpIp, value =>
                {
                    _mpIp = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value;
                    SaveConfig();
                    ShowLanConnectionMenu(screen, role);
                }, noSpaces: true), GetText.Instance.GetString("Edit IP"));

            AddMenuButton(screen, $"{GetText.Instance.GetString("Port: ")}{_mpPort}", () =>
                OpenTextInput(screen, GetText.Instance.GetString("Port"), _mpPort.ToString(), value =>
                {
                    if (!int.TryParse(value, out var parsed) || parsed <= 0 || parsed > 65535)
                        parsed = 1234;
                    _mpPort = parsed;
                    SaveConfig();
                    ShowLanConnectionMenu(screen, role);
                }, noSpaces: true), GetText.Instance.GetString("Edit port"));

            var actionLabel = role == NetRole.Host ? GetText.Instance.GetString("Host") : GetText.Instance.GetString("Join");
            AddMenuButton(screen, actionLabel, () =>
            {
                if (role == NetRole.Host)
                {
                    StartHostServerOnly();
                    ShowHostStatusMenu(screen);
                    screen.ShouldAutoHideConnectionUI(true);
                }
                else
                {
                    StartNetwork(role, screen);
                    ShowClientWaitingMenu(screen);
                    screen.ShouldAutoHideConnectionUI(true);
                }
            }, role == NetRole.Host ? GetText.Instance.GetString("Start hosting") : GetText.Instance.GetString("Connect to host"));

            AddMenuButton(screen, GetText.Instance.GetString("Back"), () =>
            {
                screen.ShouldAutoHideConnectionUI(false);
                if (role == NetRole.Host)
                    ShowHostTransportMenu(screen);
                else
                    ShowJoinTransportMenu(screen);
            }, GetText.Instance.GetString("Back to multiplayer menu"));

            if (role == NetRole.Host)
                SetRole(NetRole.None);
        }

        private static void ShowHostStatusMenu(TitleScreen screen)
        {
            _roomStatusMenuKind = 1;
            screen.clearMenu();
            AddInfoLine(screen, BuildRoomSummaryLine(), 0xFFE48A);
            AddInfoLine(screen, BuildFriendSummaryLine(), NetRef != null && NetRef.HasRemote ? 0xA6FF8A : 0xE0E0E0);
            AddMenuButton(screen, GetText.Instance.GetString("Start run for everyone"), () => StartHostRun(screen), GetText.Instance.GetString("Launch the synced co-op run"));
            AddMenuButton(screen, GetText.Instance.GetString("Refresh room"), () => ShowHostStatusMenu(screen), GetText.Instance.GetString("Refresh lobby status"));
            AddMenuButton(screen, GetMultiplayerSaveButtonLabel(), () => OpenMultiplayerSlotMenu(screen), Localize("Choose multiplayer save slot"));
            if (_menuTransport == ConnectionTransport.Steam)
            {
                AddMenuButton(screen, GetText.Instance.GetString("Invite Steam friends"), () => OpenSteamInviteOverlayFromMenu(screen), GetText.Instance.GetString("Open Steam friend invite overlay"));
                AddMenuButton(screen, GetText.Instance.GetString("Copy Steam room code"), () => { TryCopySteamLobbyCodeFromUi(); ShowHostStatusMenu(screen); }, GetText.Instance.GetString("Copy lobby code for friend"));
            }
            AddMenuButton(screen, GetText.Instance.GetString("Stop hosting"), () =>
            {
                StopNetworkFromMenu();
                SetRole(NetRole.None);
                _menuSelection = NetRole.None;
                ShowMultiplayerMenu(screen);
                screen.ShouldAutoHideConnectionUI(false);
            }, GetText.Instance.GetString("Close room and go back"));
        }

        private static void ShowClientWaitingMenu(TitleScreen screen)
        {
            _roomStatusMenuKind = 2;
            screen.clearMenu();
            AddInfoLine(screen, BuildRoomSummaryLine(), 0xFFE48A);
            AddInfoLine(screen, BuildFriendSummaryLine(), NetRef != null && NetRef.HasRemote ? 0xA6FF8A : 0xE0E0E0);
            AddInfoLine(screen, GetText.Instance.GetString("Waiting for host to start..."), 0xE0E0E0);
            AddMenuButton(screen, GetText.Instance.GetString("Refresh room"), () => ShowClientWaitingMenu(screen), GetText.Instance.GetString("Refresh lobby status"));
            AddMenuButton(screen, GetText.Instance.GetString("Disconnect"), () =>
            {
                StopNetworkFromMenu();
                _waitingForHost = false;
                ResetClientConnectState();
                _menuSelection = NetRole.None;
                ResetSteamState();
                screen.mainMenu();
                screen.ShouldAutoHideConnectionUI(false);
            }, GetText.Instance.GetString("Disconnect and return to main menu"));
            AddMenuButton(screen, GetMultiplayerSaveButtonLabel(), () => OpenMultiplayerSlotMenu(screen), Localize("Choose multiplayer save slot"));
        }



        public static void RefreshRoomStatusMenuIfVisible()
        {
            if (_roomStatusMenuKind == 0)
                return;
            if ((DateTime.UtcNow - _lastRoomStatusAutoRefresh).TotalSeconds < 1.0)
                return;
            _lastRoomStatusAutoRefresh = DateTime.UtcNow;

            EnqueueMainThreadCoalesced("ui:auto-refresh-room-status", () =>
            {
                var screen = GetTitleScreen();
                if (screen == null)
                    return;
                if (_roomStatusMenuKind == 1)
                    ShowHostStatusMenu(screen);
                else if (_roomStatusMenuKind == 2)
                    ShowClientWaitingMenu(screen);
            });
        }


        private static void OpenSteamInviteOverlayFromMenu(TitleScreen screen)
        {
            if (!ModEntry.IsSteamAvailable ||
                !ModEntry.EnsureSteamApiForNetworking("Steam invite overlay"))
            {
                SwitchToLanTransport(NetRole.Host);
                NotifySteamUnavailableFallback();
                ShowLanConnectionMenu(screen, NetRole.Host);
                return;
            }

            if (_steamLobbyId == 0UL)
            {
                AddInfoLine(screen, GetText.Instance.GetString("No Steam room yet."), 0xFF9090);
                return;
            }
            if (!SteamConnect.TryOpenInviteOverlay(_steamLobbyId, out var error))
                _log?.Warning("[NetMod][Steam] Invite overlay failed: {Error}", error);
            ShowHostStatusMenu(screen);
        }

        private static string BuildRoomSummaryLine()
        {
            var transport = _menuTransport == ConnectionTransport.Steam ? "Steam" : "IP/VPN";
            var role = _role == NetRole.Host ? "Host" : _role == NetRole.Client ? "Client" : _menuSelection == NetRole.Host ? "Host" : _menuSelection == NetRole.Client ? "Client" : "Room";
            var code = _menuTransport == ConnectionTransport.Steam ? GetSteamLobbyCodeForUi() : $"{_mpIp}:{_mpPort}";
            if (string.IsNullOrWhiteSpace(code))
                code = _menuTransport == ConnectionTransport.Steam ? "creating..." : $"{_mpIp}:{_mpPort}";
            return $"{transport} {role}  |  {code}";
        }

        private static string BuildFriendSummaryLine()
        {
            var net = NetRef;
            if (net == null || !net.IsAlive)
                return "Not connected";
            if (!net.HasRemote)
                return net.IsHost ? "Waiting for friend..." : "Connecting to host...";
            var name = string.IsNullOrWhiteSpace(_remoteUsername) || string.Equals(_remoteUsername, "guest", StringComparison.OrdinalIgnoreCase)
                ? "friend"
                : _remoteUsername.Trim();
            if (net.IsHost)
                return $"Same lobby: yes  |  Friend: {name}";
            return $"Same lobby: yes  |  Host: {name}";
        }

        private static void ShowConnectionErrorPopup(TitleScreen screen, string title, string details, Action onOk)
        {
            screen.clearMenu();
            AddInfoLine(screen, title, 0xFF9090);
            if (!string.IsNullOrWhiteSpace(details))
                AddInfoLine(screen, details, 0xE0E0E0);
            AddMenuButton(screen, GetText.Instance.GetString("OK"), onOk, GetText.Instance.GetString("Return to previous menu"));
        }

        private static void AddInfoLine(TitleScreen screen, string text, int? infoColor = null)
        {
            int colorVal = infoColor ?? 0xFFFFFF;
            var cb = new HlAction(() => { });
            screen.addMenu(MakeHLString(text), cb, MakeHLString(string.Empty), false, Ref<int>.From(ref colorVal));
        }

        private static void SharedStartSteamHost(Action<string, string, Action> showError, Action showStatus, Action showTransport)
        {
            _menuSelection = NetRole.Host;
            _menuTransport = ConnectionTransport.Steam;
            _steamLobbyActive = false;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ConnectionUI.NotifyConnectionsChanged();
            ApplySteamPersonaUsername();

            StartHostServerOnly(bindAnyAddress: true);
            if (NetRef == null || !NetRef.IsAlive || !NetRef.IsHost)
            {
                _log?.Warning("[NetMod][Steam] Host start failed: host server was not created");
                showError(GetText.Instance.GetString("Steam host failed"),
                    GetText.Instance.GetString("Could not start Steam host. Check console logs."),
                    showTransport);
                return;
            }

            var lobby = NetRef.HostLobbyResult;
            if (lobby == null || !lobby.Success)
            {
                var error = lobby?.Error ?? "Lobby creation failed";
                StopNetworkFromMenu();
                _log?.Warning("[NetMod][SteamWorkerError] {Error}", error);

                if (IsSteamUnavailableError(error))
                {
                    ModEntry.MarkSteamUnavailable(error);
                    SwitchToLanTransport(NetRole.Host);
                    NotifySteamUnavailableFallback();
                    showTransport();
                    return;
                }

                showError(GetText.Instance.GetString("Steam host failed"),
                    GetText.Instance.GetString("Steam lobby creation failed. Check console logs."),
                    showTransport);
                return;
            }

            if (!string.IsNullOrWhiteSpace(lobby.PersonaName))
                ApplySteamPersonaUsername(lobby.PersonaName);

            _steamLobbyActive = true;
            _steamLobbyId = lobby.LobbyId;
            _steamLobbyCode = SteamConnect.BuildLobbyCodeFromLobbyId(_steamLobbyId);
            ConnectionUI.NotifyConnectionsChanged();
            _log?.Information("[NetMod][Steam] Host lobby ready: id={LobbyId} code={LobbyCode}", _steamLobbyId, _steamLobbyCode);

            var copied = SteamConnect.TryCopyLobbyCodeToClipboard(_steamLobbyCode)
                         || SteamConnect.TryCopyLobbyIdToClipboard(lobby.LobbyId);
            if (copied)
                MultiplayerUI.PushSystemMessage("Lobby id copied to clipboard");

            showStatus();
        }

        private static void NativeStartSteamHost(TitleScreen screen)
        {
            if (!TrySelectSteamTransportOrShowLan(
                    screen,
                    NetRole.Host,
                    "Steam host selected in multiplayer menu"))
            {
                return;
            }

            SharedStartSteamHost(
                showError: (title, details, onOk) => ShowConnectionErrorPopup(screen, title, details, onOk),
                showStatus: () => { ShowHostStatusMenu(screen); screen.ShouldAutoHideConnectionUI(true); },
                showTransport: () => ShowHostTransportMenu(screen)
            );
        }

        private static void NativeStartSteamJoin(TitleScreen screen)
        {
            if (!TrySelectSteamTransportOrShowLan(
                    screen,
                    NetRole.Client,
                    "Steam join selected in multiplayer menu"))
            {
                return;
            }

            _steamJoinLobbyResolvePending = true;
            var joinGeneration = Interlocked.Increment(ref _steamJoinResolveGeneration);
            _waitingForHost = true;
            _clientConnecting = true;
            ShowClientWaitingMenu(screen);
            screen.ShouldAutoHideConnectionUI(true);
            ConnectionUI.NotifyConnectionsChanged();

            _ = Task.Run(() =>
            {
                var ok = SteamConnect.TryResolveJoinEndpointFromClipboard(out var join);
                EnqueueMainThreadCoalesced("steam:join-result", () =>
                {
                    if (joinGeneration != Volatile.Read(ref _steamJoinResolveGeneration) || !_steamJoinLobbyResolvePending)
                        return;
                    ApplySteamJoinResult(screen, ok, join, fromOverlay: false);
                });
            });
        }

        private static void ApplySteamJoinResult(TitleScreen screen, bool ok, SteamConnect.JoinLobbyResult join, bool fromOverlay)
        {
            SharedApplySteamJoinResult(ok, join, fromOverlay,
                showError: (title, details, onBack) => ShowConnectionErrorPopup(screen, title, details, onBack),
                showStatus: () => { ShowClientWaitingMenu(screen); screen.ShouldAutoHideConnectionUI(true); },
                showTransport: () => ShowJoinTransportMenu(screen)
            );
        }

        private static void SharedApplySteamJoinResult(bool ok, SteamConnect.JoinLobbyResult join, bool fromOverlay,
            Action<string, string, Action> showError, Action showStatus, Action showTransport)
        {
            _steamJoinLobbyResolvePending = false;

            if (fromOverlay)
                _log?.Information("[NetMod][Steam] Overlay join result: ok={Ok} error={Error}", ok, join.Error ?? "(none)");

            if (!ok)
            {
                StopNetworkFromMenu();
                _log?.Warning("[NetMod][SteamWorkerError] {Error}", join.Error);

                if (IsSteamUnavailableError(join.Error))
                {
                    ModEntry.MarkSteamUnavailable(join.Error);
                    SwitchToLanTransport(NetRole.Client);
                    NotifySteamUnavailableFallback();
                    showTransport();
                    return;
                }

                showError(GetText.Instance.GetString("Steam join failed"),
                    GetText.Instance.GetString("Steam join failed. Check console logs."),
                    showTransport);
                return;
            }

            if (!string.IsNullOrWhiteSpace(join.PersonaName))
                ApplySteamPersonaUsername(join.PersonaName);

            if (join.HostSteamId == 0UL && join.Endpoint == null)
            {
                showError(GetText.Instance.GetString("Steam join failed"),
                    GetText.Instance.GetString("Steam lobby endpoint is invalid. Check console logs."),
                    showTransport);
                return;
            }

            if (join.Endpoint != null)
            {
                _mpIp = join.Endpoint.Address.ToString();
                _mpPort = join.Endpoint.Port;
                SaveConfig();
            }

            _steamLobbyId = join.LobbyId;
            _steamLobbyCode = SteamConnect.BuildLobbyCodeFromLobbyId(_steamLobbyId);
            _steamHostSteamId = join.HostSteamId;
            ConnectionUI.NotifyConnectionsChanged();
            _log?.Information("[NetMod][Steam] Joined lobby: id={LobbyId} code={LobbyCode} hostSteamId={HostSteamId}", _steamLobbyId, _steamLobbyCode, _steamHostSteamId);

            var ts = GetTitleScreen();
            if (ts == null)
            {
                showError(GetText.Instance.GetString("Steam join failed"),
                    GetText.Instance.GetString("Main menu is not available."),
                    showTransport);
                return;
            }

            StartNetwork(NetRole.Client, ts);
            showStatus();
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

            if (!TrySelectSteamTransportOrShowLan(
                    screen,
                    NetRole.Client,
                    "Steam overlay/connect_lobby join request"))
            {
                return;
            }

            _log?.Information("[NetMod][Steam] Overlay join starting: lobbyId={LobbyId} screen=ok", lobbyId);

            _menuSelection = NetRole.Client;
            _menuTransport = ConnectionTransport.Steam;
            _steamLobbyActive = false;
            _steamLobbyId = 0;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            ApplySteamPersonaUsername();

            _steamJoinLobbyResolvePending = true;
            var joinGeneration = Interlocked.Increment(ref _steamJoinResolveGeneration);
            _waitingForHost = true;
            _clientConnecting = true;
            ShowClientWaitingMenu(screen);
            screen.ShouldAutoHideConnectionUI(true);
            ConnectionUI.NotifyConnectionsChanged();
            _ = Task.Run(() =>
            {
                _log?.Information("[NetMod][Steam] Overlay join resolving lobby (lobbyId={LobbyId})", lobbyId);
                var ok = SteamConnect.TryResolveJoinEndpointFromLobbyId(lobbyId, out var join);
                EnqueueMainThreadCoalesced("steam:join-result", () =>
                {
                    if (joinGeneration != Volatile.Read(ref _steamJoinResolveGeneration) || !_steamJoinLobbyResolvePending)
                        return;
                    ApplySteamJoinResult(screen, ok, join, fromOverlay: true);
                });
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

        private static bool _steamUnavailableNotified;

        private static void SwitchToLanTransport(NetRole role)
        {
            _menuSelection = role;
            _menuTransport = ConnectionTransport.Lan;
            _steamLobbyActive = false;
            _steamLobbyId = 0UL;
            _steamLobbyCode = string.Empty;
            _steamHostSteamId = 0UL;
            _steamJoinLobbyResolvePending = false;
            Interlocked.Increment(ref _steamJoinResolveGeneration);
            _waitingForHost = role == NetRole.Client;
            _clientConnecting = false;
            ConnectionUI.NotifyConnectionsChanged();
        }

        private static void NotifySteamUnavailableFallback()
        {
            if (_steamUnavailableNotified)
                return;

            _steamUnavailableNotified = true;
            _log?.Warning("[NetMod] Steam transport unavailable; using direct IP/LAN transport instead");
            MultiplayerUI.PushSystemMessage(Localize("Steam unavailable - using direct IP/LAN instead."));
        }

        private static bool IsSteamUnavailableError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return error.IndexOf("Steam API unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("Steam API init failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("steam_api", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("Steam client", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("callback dispatcher is not initialized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("worker dependency missing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("GameProxy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("FileNotFoundException", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   error.IndexOf("Could not load file or assembly", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TrySelectSteamTransportOrShowLan(TitleScreen screen, NetRole role, string source)
        {
            _menuSelection = role;
            _menuTransport = ConnectionTransport.Steam;

            if (ModEntry.IsSteamAvailable &&
                ModEntry.EnsureSteamApiForNetworking(source))
            {
                return true;
            }

            SwitchToLanTransport(role);
            NotifySteamUnavailableFallback();
            screen.ShouldAutoHideConnectionUI(false);
            ShowLanConnectionMenu(screen, role);
            return false;
        }

        /// <summary>
        /// True only when the Steam transport is both selected AND usable. Without a working
        /// Steam client the lobby path can never connect, so the menu falls back to the
        /// direct IP/LAN transport instead of failing with an obscure lobby error.
        /// </summary>
        private static bool ShouldUseSteamTransport()
        {
            if (_menuTransport != ConnectionTransport.Steam)
                return false;

            if (ModEntry.IsSteamAvailable &&
                ModEntry.EnsureSteamApiForNetworking("Steam transport selected in multiplayer menu"))
                return true;

            SwitchToLanTransport(_menuSelection);
            NotifySteamUnavailableFallback();
            return false;
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
                    if (ShouldUseSteamTransport())
                        ModEntry.Instance.StartSteamHostFromMenu(_mpPort);
                    else
                        ModEntry.Instance.StartHostFromMenu(_mpIp, _mpPort);
                    _waitingForHost = false;
                    StartHostRun(screen);
                }
                else if (role == NetRole.Client)
                {
                    if (ShouldUseSteamTransport())
                    {
                        if (_steamHostSteamId == 0UL)
                        {
                            _log?.Warning("[NetMod][Steam] Client start aborted: host Steam id is missing");
                            var ts = GetTitleScreen();
                            if (ts != null)
                                ShowConnectionErrorPopup(ts,
                                    GetText.Instance.GetString("Steam join failed"),
                                    GetText.Instance.GetString("Steam host id is missing. Check console logs."),
                                    () => ShowJoinTransportMenu(ts));
                            return;
                        }
                    }

                    lock (Sync)
                    {
                        _levelDescArrived = false;
                        _pendingAutoStart = false;
                        _autoStartTriggered = false;
                        _seedArrived = false;
                        ClearStructuredLaunchFlagsLocked();
                        _clientConnectAttempt = 0;
                        _clientConnecting = true;
                        _waitingForHost = true;
                    }

                    if (ShouldUseSteamTransport())
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
                    _waitingForHost = false;
                    return;
                }

                if (ShouldUseSteamTransport())
                {
                    ModEntry.Instance.StartSteamHostFromMenu(_mpPort);
                }
                else
                {
                    var hostIp = bindAnyAddress ? "0.0.0.0" : _mpIp;
                    ModEntry.Instance.StartHostFromMenu(hostIp, _mpPort);
                }

                _waitingForHost = false;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Host start failed: {Message}", ex.Message);
            }
        }

        private static void StartHostRun(TitleScreen screen)
        {
            lock (Sync)
            {
                if (_initialHostLaunchPendingSequence > 0)
                {
                    MultiplayerUI.PushSystemMessage(Localize("The co-op run is already starting."));
                    return;
                }
            }

            StartHostServerOnly();
            var precommitted = PrecommitInitialHostRunSeed(out _, out var sequence, out var descriptor);
            if (!precommitted || descriptor == null)
            {
                _log?.Warning("[NetMod][RunLaunch] Could not prepare the host launch descriptor");
                MultiplayerUI.PushSystemMessage(Localize("Could not prepare the co-op run launch."));
                return;
            }

            if (!TryBeginInitialHostLaunch(screen, descriptor, out var beginError))
            {
                CancelPrecommittedHostRunSeed("initial_launch_begin_failed");
                _log?.Warning(
                    "[NetMod][RunLaunch] Could not begin initial launch seq={Sequence}: {Error}",
                    sequence,
                    beginError);
                MultiplayerUI.PushSystemMessage(Localize("Could not prepare the co-op run launch."));
            }
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
                try { NetRef?.SendControlAndFlush("KICK", 320); } catch { }
            }

            try
            {
                NetRef?.Dispose();
            }
            catch { }

            SetRole(NetRole.None);
            NetRef = null;
            _waitingForHost = false;
            ResetClientConnectState();
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

using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.PortableCore;
using ModCore.Modules;

namespace DeadCellsMultiplayerMod;

/// <summary>
/// Restores features-continue RunLaunch / main-thread network APIs on top of the
/// checked-out <c>dev</c> GameMenu lobby/UI base.
/// </summary>
internal static partial class GameMenu
{
    private static int _serverSeedSequence;
    private static int _remoteSeedSequence;
    private static int _consumedRemoteSeedSequence;
    private static string _remoteLaunchKind = string.Empty;

    private const int RemoteRunSeedWaitMs = 2000;
    private const int RunSeedTransitionGraceMs = 2000;

    private static readonly Channel<Action> _networkMainThreadQueue = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    private static readonly object CriticalMainThreadCoalesceSync = new();
    private static readonly Dictionary<string, Action> _criticalCoalescedActions = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<string> _criticalCoalescedKeys = new();
    private const int MainThreadQueueMaxPendingCritical = 64;
    private const int MainThreadQueueBurstActionsPerPump = 768;
    private const int MainThreadQueueBurstBacklogThreshold = 96;
    private static long _lastMainThreadCoalescedDropLogTicks;

    private static long _clientRestartPendingUntilTicks;
    private const int ClientRestartPendingTtlMs = 12000;

    private static int? _precommittedHostSeed;
    private static int _precommittedHostSeedSequence;
    private static string _precommittedHostLaunchKind = string.Empty;
    private static long _precommittedHostSeedExpiresAtTicks;
    private const int PrecommittedHostSeedTtlMs = 300000;

    private static DateTime _lastRoomStatusAutoRefresh = DateTime.MinValue;
    private static bool _protocolMismatchPending;

    private static void ResetRunLaunchCompatStateLocked()
    {
        _serverSeedSequence = 0;
        _remoteSeedSequence = 0;
        _consumedRemoteSeedSequence = 0;
        _remoteLaunchKind = string.Empty;
        ClearStructuredLaunchFlagsLocked();
        ClearPrecommittedHostRunSeedLocked();
        _clientRestartPendingUntilTicks = 0;
        _protocolMismatchPending = false;
        while (_networkMainThreadQueue.Reader.TryRead(out _)) { }
        while (_criticalCoalescedKeys.TryDequeue(out _)) { }
        lock (CriticalMainThreadCoalesceSync)
            _criticalCoalescedActions.Clear();
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
                LogCriticalMainThreadCoalescedDropRateLimited(coalesceKey);
                return;
            }

            _criticalCoalescedActions[coalesceKey] = action;
        }

        if (isNewKey)
            _criticalCoalescedKeys.Enqueue(coalesceKey);
    }

    private static void LogCriticalMainThreadCoalescedDropRateLimited(string key)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var minTicks = System.Diagnostics.Stopwatch.Frequency * 5L;
        var previous = Interlocked.Read(ref _lastMainThreadCoalescedDropLogTicks);
        if (previous != 0 && now - previous < minTicks)
            return;
        if (Interlocked.CompareExchange(ref _lastMainThreadCoalescedDropLogTicks, now, previous) != previous)
            return;

        _log?.Warning(
            "[NetMod] Rejected critical coalesced main-thread work because its queue is full (key={Key})",
            key);
    }

    /// <summary>
    /// Drain critical coalesced work first, then reliable network protocol actions.
    /// Called from <see cref="ProcessMainThreadQueue"/> so receive-loop back-pressure stays healthy.
    /// </summary>
    private static int DrainCriticalAndNetworkMainThreadQueues(int budget)
    {
        if (budget <= 0)
            return 0;

        var processed = 0;
        var networkBacklog = 0;
        if (_networkMainThreadQueue.Reader.CanCount)
            networkBacklog = _networkMainThreadQueue.Reader.Count;

        var effectiveBudget = networkBacklog >= MainThreadQueueBurstBacklogThreshold
            ? Math.Max(budget, MainThreadQueueBurstActionsPerPump)
            : budget;

        while (processed < effectiveBudget)
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
            else if (_networkMainThreadQueue.Reader.TryRead(out var networkAction))
            {
                action = networkAction;
            }
            else
            {
                break;
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

        return processed;
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
        net.SendSeed(sequence, seed, launchKind);
        net.SendControlAndFlush($"SEED|{sequence}|{seed}|{launchKind}", 500);
        _log?.Information(
            "[NetMod] Precommitted initial host run seq={Sequence} seed={Seed} launch={LaunchKind}",
            sequence,
            seed,
            launchKind);
        return true;
    }

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

    /// <summary>
    /// Protocol 17 seed receive path used by the text NetNode. Keeps the legacy 1-arg
    /// <see cref="ReceiveHostRunSeed(int)"/> for older same-run restart flows.
    /// </summary>
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
                var isBossRushSeed = GameDataSync.IsBossRushLaunchKind(launchKind);
                if (_inActualRun)
                {
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

    public static void RefreshRoomStatusMenuIfVisible()
    {
        if (!_inHostStatusMenu && !_inClientWaitingMenu)
            return;
        if ((DateTime.UtcNow - _lastRoomStatusAutoRefresh).TotalSeconds < 1.0)
            return;
        _lastRoomStatusAutoRefresh = DateTime.UtcNow;

        EnqueueMainThreadCoalesced("ui:auto-refresh-room-status", () =>
        {
            var screen = GetTitleScreen();
            if (screen == null)
                return;
            if (_inHostStatusMenu)
                ShowHostStatusMenu(screen);
            else if (_inClientWaitingMenu)
                ShowClientWaitingMenu(screen);
        });
    }

    internal static void NotifyProtocolMismatch(
        string remoteBuild,
        int remoteProtocol,
        string localBuild,
        int localProtocol,
        NetRole localRole)
    {
        var remoteLabel = string.IsNullOrWhiteSpace(remoteBuild) ? "unknown" : remoteBuild.Trim();
        var detail = string.Create(
            CultureInfo.InvariantCulture,
            $"Other player: {remoteLabel} (protocol {remoteProtocol}). You: {localBuild} (protocol {localProtocol}).");

        MultiplayerUI.PushSystemMessage(
            "Co-op version mismatch. Both players need the exact same mod build.",
            8.0,
            1.5);
        ConnectionUI.NotifyConnectionsChanged();

        if (localRole != NetRole.Client)
            return;

        lock (Sync)
        {
            _protocolMismatchPending = true;
            _clientConnecting = false;
            _waitingForHost = false;
        }

        EnqueueMainThreadCoalesced("ui:protocol-mismatch", () =>
        {
            var screen = GetTitleScreen();
            if (screen == null)
                return;

            screen.clearMenu();
            AddInfoLine(screen, Localize("Co-op version mismatch"), 0xFF9090);
            AddInfoLine(screen, detail, 0xE0E0E0);
            AddInfoLine(
                screen,
                Localize("Install the exact same DeadCellsMultiplayerMod build on both computers."),
                0xE0E0E0);
            AddMenuButton(screen, GetText.Instance.GetString("OK"), () =>
            {
                screen.clearMenu();
                ShowJoinTransportMenu(screen);
            }, Localize("Return to join menu"));
            screen.ShouldAutoHideConnectionUI(false);
        });
    }
}

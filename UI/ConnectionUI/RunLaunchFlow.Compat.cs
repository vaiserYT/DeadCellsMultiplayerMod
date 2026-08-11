using System.Globalization;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.PortableCore;
using ModCore.Modules;

namespace DeadCellsMultiplayerMod;

/// <summary>
/// Sequenced seed / precommit / protocol-mismatch helpers for run launch.
/// </summary>
internal static partial class LobbySession
{
    internal static int _serverSeedSequence;
    internal static int _remoteSeedSequence;
    internal static int _consumedRemoteSeedSequence;
    internal static string _remoteLaunchKind = string.Empty;

    internal const int RemoteRunSeedWaitMs = 2000;
    internal const int RunSeedTransitionGraceMs = 2000;

    internal static long _clientRestartPendingUntilTicks;
    internal const int ClientRestartPendingTtlMs = 12000;

    internal static int? _precommittedHostSeed;
    internal static int _precommittedHostSeedSequence;
    internal static string _precommittedHostLaunchKind = string.Empty;
    internal static long _precommittedHostSeedExpiresAtTicks;
    internal const int PrecommittedHostSeedTtlMs = 300000;

    internal static DateTime _lastRoomStatusAutoRefresh = DateTime.MinValue;

    internal static void ResetRunLaunchCompatStateLocked()
    {
        _serverSeedSequence = 0;
        _remoteSeedSequence = 0;
        _consumedRemoteSeedSequence = 0;
        _remoteLaunchKind = string.Empty;
        _clientLevelGraphWaitStartedTicks = 0;
        _clientLevelGraphWaitExpired = false;
        _nextClientLaunchBlockLogTicks = 0;
        ClearStructuredLaunchFlagsLocked();
        ClearPrecommittedHostRunSeedLocked();
        _clientRestartPendingUntilTicks = 0;
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

    internal static void ClearPrecommittedHostRunSeedLocked()
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
    /// Protocol seed receive path used by the text NetNode (SEED|seq|seed|kind).
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
                    SignalClientLaunchProgressLocked();
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

    internal static void ScheduleClientRunSeedReconcile(int sequence, int seed)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(RunSeedTransitionGraceMs).ConfigureAwait(false);
            MainThreadPump.EnqueueCriticalMainThreadCoalesced("game:run-seed-reconcile", () =>
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
                MarkRemoteLaunchSequenceConsumedLocked(sequence);
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

    /// <summary>
    /// Marks a remote launch sequence as consumed so host rebroadcasts of the same
    /// RUNEXEC/SEED cannot schedule a false in-run restart after the client already
    /// started that launch.
    /// </summary>
    internal static void MarkRemoteLaunchSequenceConsumed(int sequence, string reason)
    {
        if (sequence <= 0)
            return;

        lock (Sync)
        {
            if (sequence <= _consumedRemoteSeedSequence)
                return;

            MarkRemoteLaunchSequenceConsumedLocked(sequence);
        }

        _log?.Information(
            "[NetMod][RunLaunch] Marked remote launch consumed seq={Sequence} ({Reason})",
            sequence,
            reason);
    }

    internal static void MarkRemoteLaunchSequenceConsumedLocked(int sequence)
    {
        if (sequence > _consumedRemoteSeedSequence)
            _consumedRemoteSeedSequence = sequence;
    }

    public static void RefreshRoomStatusMenuIfVisible()
    {
        if (!_inHostStatusMenu && !_inClientWaitingMenu)
            return;
        if ((DateTime.UtcNow - _lastRoomStatusAutoRefresh).TotalSeconds < 1.0)
            return;
        _lastRoomStatusAutoRefresh = DateTime.UtcNow;

        MainThreadPump.EnqueueMainThreadCoalesced("ui:auto-refresh-room-status", () =>
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

        MainThreadPump.EnqueueMainThreadCoalesced("ui:protocol-mismatch", () =>
        {
            var screen = GetTitleScreen();
            if (screen == null)
                return;

            screen.clearMenu();
            UiBegin();
            UiInfo(Localize("Co-op version mismatch"), 0xFF9090);
            UiInfo(detail, 0xE0E0E0);
            UiInfo(
                Localize("Install the exact same DeadCellsMultiplayerMod build on both computers."),
                0xE0E0E0);
            UiButton(GetText.Instance.GetString("OK"), () =>
            {
                screen.clearMenu();
                ShowJoinTransportMenu(screen);
            }, Localize("Return to join menu"));
            screen.ShouldAutoHideConnectionUI(true);
            UiCommit();
        });
    }
}

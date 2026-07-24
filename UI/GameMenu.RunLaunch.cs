using System.Globalization;
using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.PortableCore;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using Serilog;

namespace DeadCellsMultiplayerMod;

internal static partial class GameMenu
{
    private const int InitialLaunchAckWaitMs = 2600;
    private const int InitialLaunchAckRetryWaitMs = 1800;
    // Protocol 17: after granting execute, the host waits (on a worker) for the client's RUNQUEUED
    // confirmation before it starts its own native loader, so both consume the same seed/config.
    private const int InitialLaunchQueuedWaitMs = 4000;
    private const int InitialLaunchQueuedRetryWaitMs = 3000;

    private static bool _structuredLaunchCommitArrived;
    private static int _structuredLaunchExecuteSequence;
    private static int _initialHostLaunchPendingSequence;

    private static void InitializeRunLaunchHandshake(ILogger logger)
    {
        RunLaunchCoordinator.Initialize(logger);
        lock (Sync)
            ClearStructuredLaunchFlagsLocked();
    }

    private static void ClearStructuredLaunchFlagsLocked()
    {
        _structuredLaunchCommitArrived = false;
        _structuredLaunchExecuteSequence = 0;
        _initialHostLaunchPendingSequence = 0;
    }

    private static RunLaunchDescriptor BuildHostRunLaunchDescriptor(
        int seed,
        int sequence,
        string launchKind,
        string? bossRushType = null)
    {
        var bossCells = ReadBossCellsForLaunch();
        var initialLevelId = GameDataSync.IsBossRushLaunchKind(launchKind)
            ? "BossRushZone"
            : "PrisonStart";

        // Real, runtime-sourced values only. The run seed is the authoritative generation input;
        // boss sequence / arena order / modifiers derive from it deterministically (see
        // RunLaunchDescriptor) and the per-level generation seed uses the dedicated SEED sync, so
        // those are not re-invented here. Route carries the native Boss Rush variant read from the
        // door (BossRushDoor.bossRushType); TargetArena carries the concrete entry arena id.
        var route = string.IsNullOrWhiteSpace(bossRushType) ? string.Empty : bossRushType.Trim();

        return RunLaunchCoordinator.CreateOrReuseHostDescriptor(
            seed,
            sequence,
            launchKind,
            initialLevelId,
            initialLevelSeed: null,
            difficulty: bossCells,
            bossCells: bossCells,
            dlcFlags: 0,
            saveSlot: ResolveSaveSlotNumber(null),
            route: route,
            targetArena: initialLevelId);
    }

    private static int ReadBossCellsForLaunch()
    {
        try
        {
            var value = dc.Main.Class.ME?.user?.bossRuneActivated;
            return Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryBeginInitialHostLaunch(
        TitleScreen screen,
        RunLaunchDescriptor descriptor,
        out string error)
    {
        var net = NetRef;
        if (net == null || !net.IsAlive || !net.IsHost)
        {
            error = "host network is not available";
            return false;
        }

        lock (Sync)
        {
            if (_initialHostLaunchPendingSequence > 0)
            {
                error = $"launch sequence {_initialHostLaunchPendingSequence} is already waiting for the client";
                return false;
            }

            _initialHostLaunchPendingSequence = descriptor.Sequence;
        }

        net.SendRunLaunchCommit(descriptor, flush: true);

        // Steam P2P packet polling happens from the game/update side. Waiting synchronously in the
        // Start button callback prevents the host from polling the client's RUNACK, so the old
        // barrier could always time out and make the button appear to do nothing. Wait on a worker
        // and return control to Dead Cells immediately; all game/UI work is queued back to the main
        // thread after the acknowledgement arrives.
        if (!net.HasRemote)
        {
            QueueInitialHostLaunchExecution(screen, descriptor);
            error = string.Empty;
            return true;
        }

        _ = Task.Run(() =>
        {
            var acknowledged = RunLaunchCoordinator.WaitForHostAck(
                descriptor,
                InitialLaunchAckWaitMs,
                out var ackError);

            if (!acknowledged)
            {
                // One reliable resend covers a packet queued at the exact handshake/menu boundary.
                net.SendRunLaunchCommit(descriptor, flush: true);
                acknowledged = RunLaunchCoordinator.WaitForHostAck(
                    descriptor,
                    InitialLaunchAckRetryWaitMs,
                    out ackError);
            }

            if (!acknowledged)
            {
                QueueInitialHostLaunchFailure(screen, descriptor.Sequence, ackError);
                return;
            }

            // Grant execute now so the client can queue the identical native launch. This worker never
            // touches Dead Cells objects; it only sends control packets and waits on Monitor, so the
            // game main thread keeps polling and processing the client's RUNQUEUED.
            RunLaunchExecute execute;
            try
            {
                execute = RunLaunchCoordinator.MarkHostExecute(descriptor);
            }
            catch (Exception ex)
            {
                QueueInitialHostLaunchFailure(screen, descriptor.Sequence, "host execute failed: " + ex.Message);
                return;
            }

            net.SendRunLaunchExecute(execute, flush: true);

            // Host starts only after the client confirms it queued the same launch (required order).
            var queued = RunLaunchCoordinator.WaitForClientQueued(
                descriptor,
                InitialLaunchQueuedWaitMs,
                out var queuedError);
            if (!queued)
            {
                net.SendRunLaunchExecute(execute, flush: true);
                queued = RunLaunchCoordinator.WaitForClientQueued(
                    descriptor,
                    InitialLaunchQueuedRetryWaitMs,
                    out queuedError);
            }

            if (queued)
                QueueInitialHostLaunchExecution(screen, descriptor);
            else
                QueueInitialHostLaunchFailure(screen, descriptor.Sequence, queuedError);
        });

        error = string.Empty;
        return true;
    }

    private static void QueueInitialHostLaunchExecution(
        TitleScreen screen,
        RunLaunchDescriptor descriptor)
    {
        EnqueueCriticalMainThreadCoalesced(
            $"game:initial-run-launch:{descriptor.Sequence}",
            () =>
            {
                lock (Sync)
                {
                    if (_initialHostLaunchPendingSequence != descriptor.Sequence)
                        return;
                }

                var net = NetRef;
                var current = RunLaunchCoordinator.GetCurrentHostDescriptor();
                if (net == null || !net.IsAlive || !net.IsHost ||
                    current == null || !current.HasSameIdentity(descriptor))
                {
                    QueueInitialHostLaunchFailure(
                        screen,
                        descriptor.Sequence,
                        "host launch state changed before execution");
                    return;
                }

                try
                {
                    var execute = RunLaunchCoordinator.MarkHostExecute(descriptor);
                    net.SendRunLaunchExecute(execute, flush: true);

                    lock (Sync)
                    {
                        if (_initialHostLaunchPendingSequence == descriptor.Sequence)
                            _initialHostLaunchPendingSequence = 0;
                    }

                    bool custom;
                    bool streamEnabled;
                    lock (Sync)
                    {
                        custom = _pendingLaunchCustom;
                        streamEnabled = _pendingLaunchStreamEnabled;
                    }

                    SetAuthoritativePendingNewGameLaunch(custom, streamEnabled);
                    if (custom && !EnsureCustomModeScreenUser(screen))
                    {
                        CancelPrecommittedHostRunSeed("custom_mode_user_unready");
                        CancelHostStructuredLaunch(descriptor.Sequence, "custom_mode_user_unready");
                        MultiplayerUI.PushSystemMessage(Localize("Custom Mode could not prepare the save user."));
                        ShowHostStatusMenu(screen);
                        return;
                    }

                    screen.startNewGame(custom);
                }
                catch (Exception ex)
                {
                    lock (Sync)
                    {
                        if (_initialHostLaunchPendingSequence == descriptor.Sequence)
                            _initialHostLaunchPendingSequence = 0;
                    }

                    CancelPrecommittedHostRunSeed("native_startNewGame_failed");
                    CancelHostStructuredLaunch(descriptor.Sequence, "native_startNewGame_failed_after_consume");
                    _log?.Warning("[NetMod] Failed to start host run: {Message}", ex.Message);
                    MultiplayerUI.PushSystemMessage(Localize("Dead Cells could not start the co-op run."));
                }
            });
    }

    private static void QueueInitialHostLaunchFailure(
        TitleScreen screen,
        int sequence,
        string reason)
    {
        EnqueueCriticalMainThreadCoalesced(
            $"game:initial-run-launch-failed:{sequence}",
            () =>
            {
                lock (Sync)
                {
                    if (_initialHostLaunchPendingSequence != sequence)
                        return;
                    _initialHostLaunchPendingSequence = 0;
                }

                CancelPrecommittedHostRunSeed("initial_ack_barrier_failed");
                _log?.Warning(
                    "[NetMod][RunLaunch] Initial launch aborted seq={Sequence}: {Error}",
                    sequence,
                    reason);
                MultiplayerUI.PushSystemMessage(
                    Localize("Friend did not acknowledge the run start. Both players must use the same build."),
                    7.0,
                    1.0);
                ShowHostStatusMenu(screen);
            });
    }

    internal static RunLaunchDescriptor CommitHostRunLaunchFromHook(
        int seed,
        int sequence,
        string launchKind,
        string? bossRushType = null)
    {
        var descriptor = BuildHostRunLaunchDescriptor(seed, sequence, launchKind, bossRushType);
        var net = NetRef;
        if (net == null || !net.IsAlive || !net.IsHost)
            return descriptor;

        net.SendRunLaunchCommit(descriptor, flush: true);

        // Hooks run inside Dead Cells' native launch path. Never block that main-thread path while
        // waiting for a network acknowledgement (Steam receives are polled by the same frame loop).
        // RUNCOMMIT and RUNEXEC are reliable/ordered, and the host cache replays both to late clients.
        var execute = RunLaunchCoordinator.MarkHostExecute(descriptor);
        net.SendRunLaunchExecute(execute, flush: true);
        return descriptor;
    }

    internal static void MarkRunLaunchLoading(int sequence, string reason)
    {
        RunLaunchCoordinator.MarkLocalLoading(sequence, reason);
    }


    internal static void PublishInitialLevelSeed(
        string levelId,
        double levelSeed,
        NetNode? net)
    {
        if (net == null || !net.IsAlive || !net.IsHost)
            return;

        var enriched = RunLaunchCoordinator.TryEnrichHostInitialLevel(levelId, levelSeed);
        if (enriched != null)
        {
            // A same-identity RUNCOMMIT is a metadata enrichment, not a second launch. The
            // execute/ready cache is intentionally retained for late-joining clients.
            net.SendRunLaunchCommit(enriched, flush: false);
        }
    }

    private static void SendRunReadyFromHero()
    {
        string levelId = string.Empty;
        try
        {
            levelId = ModEntry.me?._level?.map?.id?.ToString() ?? string.Empty;
        }
        catch
        {
        }

        var ready = RunLaunchCoordinator.MarkLocalPlaying(levelId);
        if (ready != null)
            NetRef?.SendRunLevelReady(ready);
    }

    internal static void ReceiveRunLaunchCommitPayload(string payload)
    {
        if (!RunLaunchWireCodec.TryDecodeCommit(payload, out var descriptor, out var decodeError) ||
            descriptor == null)
        {
            _log?.Warning("[NetMod][RunLaunch] Malformed RUNCOMMIT: {Error}", decodeError);
            return;
        }

        var accepted = RunLaunchCoordinator.TryAcceptRemoteDescriptor(
            descriptor,
            out var ack,
            out var isNewCommit);

        // ACK immediately from the receive path. The host waits on a worker so Steam packet polling stays alive.
        NetRef?.SendRunLaunchAck(ack, flush: true);

        if (!accepted)
        {
            _log?.Warning(
                "[NetMod][RunLaunch] Rejected host commit seq={Sequence}: {Error}",
                descriptor.Sequence,
                ack.Error);
            return;
        }

        lock (Sync)
        {
            _remoteSeed = descriptor.RunSeed;
            _remoteSeedSequence = descriptor.Sequence;
            _remoteLaunchKind = descriptor.LaunchKind ?? string.Empty;
            _seedArrived = true;
            _structuredLaunchCommitArrived = true;
            if (isNewCommit)
            {
                _structuredLaunchExecuteSequence = 0;
                _pendingAutoStart = false;
                _autoStartTriggered = false;
            }
            Monitor.PulseAll(Sync);
        }
    }

    internal static void ReceiveRunLaunchAckPayload(string payload)
    {
        if (!RunLaunchWireCodec.TryDecodeAck(payload, out var ack, out var decodeError) || ack == null)
        {
            _log?.Warning("[NetMod][RunLaunch] Malformed RUNACK: {Error}", decodeError);
            return;
        }

        RunLaunchCoordinator.ReceiveHostAck(ack);
    }

    internal static void ReceiveRunLaunchExecutePayload(string payload)
    {
        if (!RunLaunchWireCodec.TryDecodeExecute(payload, out var execute, out var decodeError) || execute == null)
        {
            _log?.Warning("[NetMod][RunLaunch] Malformed RUNEXEC: {Error}", decodeError);
            return;
        }

        if (!RunLaunchCoordinator.TryAcceptRemoteExecute(execute, out var executeError))
        {
            _log?.Warning(
                "[NetMod][RunLaunch] Rejected RUNEXEC seq={Sequence}: {Error}",
                execute.Sequence,
                executeError);
            return;
        }

        var descriptor = RunLaunchCoordinator.GetCurrentRemoteDescriptor();
        var scheduleInRunReconcile = false;
        lock (Sync)
        {
            _structuredLaunchExecuteSequence = execute.Sequence;
            if (_role == NetRole.Client && descriptor != null && !descriptor.BossRush)
            {
                if (_inActualRun)
                    scheduleInRunReconcile = execute.Sequence > _consumedRemoteSeedSequence;
                else
                    _pendingAutoStart = true;
            }
            Monitor.PulseAll(Sync);
        }

        // Protocol 17 correction: RUNQUEUED must mean "the client is now invoking the authoritative
        // native launch", not merely "the execute message was received". The host is held until then.
        //   * Boss Rush: sent from the door gate (TryBeginLocalBossRushLoad) when the client is at its
        //     synchronized Boss Rush door and about to invoke the native loader.
        //   * Fresh normal new game: sent from the auto-start when startNewGame is actually invoked.
        //   * In-run reconcile below: the live run adopts the authoritative seed immediately, so the
        //     launch is genuinely being invoked now and RUNQUEUED is sent here.
        if (scheduleInRunReconcile && descriptor != null)
        {
            ScheduleClientRunSeedReconcile(execute.Sequence, descriptor.RunSeed);
            NotifyClientLaunchQueued(execute.Sequence);
        }
    }

    internal static void ReceiveRunLaunchQueuedPayload(string payload)
    {
        if (!RunLaunchWireCodec.TryDecodeQueued(payload, out var queued, out var decodeError) || queued == null)
        {
            _log?.Warning("[NetMod][RunLaunch] Malformed RUNQUEUED: {Error}", decodeError);
            return;
        }

        RunLaunchCoordinator.ReceiveClientQueued(queued);
    }

    /// <summary>
    /// Client: tell the host we have the authoritative launch and have committed to loading it.
    /// Sent once per sequence; requires that the launch is both committed and executed locally.
    /// </summary>
    internal static void NotifyClientLaunchQueued(int sequence)
    {
        var net = NetRef;
        if (net == null || !net.IsAlive || net.IsHost || sequence <= 0)
            return;
        if (RunLaunchCoordinator.HasLocalQueued(sequence))
            return;
        if (!RunLaunchCoordinator.HasExecutableRemoteLaunch(sequence))
            return;

        var queued = RunLaunchCoordinator.BuildClientQueued(sequence, true, string.Empty);
        if (queued == null)
            return;

        net.SendRunLaunchQueued(queued, flush: true);
        _log?.Information("[NetMod][RunLaunch] Client sent RUNQUEUED seq={Sequence}", sequence);
        BossSyncDiag.Trace("launch queued role=client seq={Sequence}", sequence);
    }

    /// <summary>
    /// Gate used by the door coordinator before a peer begins a fresh Boss Rush native load. The
    /// client requires the executable authoritative launch (and confirms RUNQUEUED); the host requires
    /// the client's RUNQUEUED. Neither side ever generates its own Boss Rush.
    /// </summary>
    internal static bool TryBeginLocalBossRushLoad(out string reason)
    {
        reason = string.Empty;
        var net = NetRef;
        if (net == null || !net.IsAlive)
            return true; // offline / solo: nothing to synchronize

        if (net.IsHost)
        {
            int sequence;
            lock (Sync)
                sequence = _precommittedHostSeedSequence;

            if (sequence <= 0 || !HasPrecommittedHostBossRushLaunch())
            {
                reason = "host Boss Rush launch not committed yet";
                return false;
            }
            if (!net.HasRemote)
                return true;
            if (RunLaunchCoordinator.HasClientQueued(sequence))
                return true;

            reason = "waiting for friend to queue the same Boss Rush launch";
            return false;
        }

        var descriptor = RunLaunchCoordinator.GetCurrentRemoteDescriptor();
        if (descriptor == null || !descriptor.BossRush)
        {
            reason = "authoritative Boss Rush launch not received yet";
            return false;
        }
        if (!HasPendingRemoteBossRushLaunch())
        {
            reason = "authoritative Boss Rush launch not executable yet";
            return false;
        }

        // Ready: confirm to the host (idempotent) before we invoke newGame.
        NotifyClientLaunchQueued(descriptor.Sequence);
        return true;
    }

    /// <summary>
    /// Client-side validation of the real Boss Rush variant. Compares this peer's own door
    /// (BossRushDoor.bossRushType) against the authoritative host <see cref="RunLaunchDescriptor.Route"/>
    /// and surfaces a mismatch (a genuine divergence into a different Boss Rush entry) in the logs.
    /// Non-fatal: the seed is still the authoritative generation input, so we warn rather than block.
    /// </summary>
    internal static void ValidateClientBossRushVariant(string localBossRushType)
    {
        var net = NetRef;
        if (net == null || !net.IsAlive || net.IsHost)
            return;

        var descriptor = RunLaunchCoordinator.GetCurrentRemoteDescriptor();
        if (descriptor == null || !descriptor.BossRush)
            return;

        var expected = (descriptor.Route ?? string.Empty).Trim();
        var actual = (localBossRushType ?? string.Empty).Trim();

        // If neither side has a variant string there is nothing to compare (older host, or a door
        // that exposes no type). Only a genuine value-vs-value difference is a divergence.
        if (expected.Length == 0 || actual.Length == 0)
            return;

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Warning(
                "[NetMod][BossRushSeed] Boss Rush variant mismatch: host route='{HostRoute}' client door='{ClientVariant}' seq={Sequence}",
                expected,
                actual,
                descriptor.Sequence);
            BossSyncDiag.Warn(
                "boss rush variant mismatch host='{HostRoute}' client='{ClientVariant}' seq={Sequence}",
                expected,
                actual,
                descriptor.Sequence);
        }
    }

    /// <summary>Cancel a Boss Rush launch cleanly when the gate cannot synchronize (never fall back to a local run).</summary>
    internal static void CancelBossRushLaunchGate(string reason)
    {
        var net = NetRef;
        if (net != null && net.IsAlive && net.IsHost)
        {
            int sequence;
            lock (Sync)
                sequence = _precommittedHostSeedSequence;

            CancelPrecommittedHostRunSeed(reason);
            if (sequence > 0)
                CancelHostStructuredLaunch(sequence, reason);
        }

        _log?.Warning("[NetMod][BossRushSeed] Boss Rush launch gate cancelled: {Reason}", reason);
        BossSyncDiag.Warn("launch gate cancelled role={Role} reason={Reason}", BossSyncDiag.Role(net), reason);
    }

    internal static void ReceiveRunLevelReadyPayload(string payload)
    {
        if (!RunLaunchWireCodec.TryDecodeReady(payload, out var ready, out var decodeError) || ready == null)
        {
            _log?.Warning("[NetMod][RunLaunch] Malformed RUNREADY: {Error}", decodeError);
            return;
        }

        RunLaunchCoordinator.ReceiveRemoteReady(ready);
    }

    internal static void ReceiveRunLaunchCancelPayload(string payload)
    {
        if (!RunLaunchWireCodec.TryDecodeCancel(payload, out var cancel, out var decodeError) || cancel == null)
        {
            _log?.Warning("[NetMod][RunLaunch] Malformed RUNCANCEL: {Error}", decodeError);
            return;
        }

        if (!RunLaunchCoordinator.TryAcceptRemoteCancel(cancel))
            return;

        lock (Sync)
        {
            _remoteSeed = null;
            _remoteLaunchKind = string.Empty;
            _seedArrived = false;
            _pendingAutoStart = false;
            _autoStartTriggered = false;
            ClearStructuredLaunchFlagsLocked();
            Monitor.PulseAll(Sync);
        }

        MultiplayerUI.PushSystemMessage(Localize("Host cancelled the co-op run launch."));
    }

    private static void CancelHostStructuredLaunch(int sequence, string reason)
    {
        var cancel = RunLaunchCoordinator.CancelHostLaunch(sequence, reason);
        if (cancel != null)
            NetRef?.SendRunLaunchCancel(cancel, flush: true);
        NetRef?.ClearCachedHostRunLaunch(sequence);
    }

    private static bool CanAutoStartStructuredClientLaunchLocked()
    {
        return _structuredLaunchCommitArrived &&
               _structuredLaunchExecuteSequence > 0 &&
               _remoteSeedSequence == _structuredLaunchExecuteSequence &&
               RunLaunchCoordinator.HasExecutableRemoteLaunch(_remoteSeedSequence);
    }
}

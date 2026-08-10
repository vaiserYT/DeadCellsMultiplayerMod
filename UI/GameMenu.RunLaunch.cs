using System.Globalization;
using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.PortableCore;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using Serilog;

namespace DeadCellsMultiplayerMod;

internal static partial class GameMenu
{
    private static bool _structuredLaunchCommitArrived;
    private static int _structuredLaunchExecuteSequence;

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

        // Hooks run inside Dead Cells' native launch path. Never block that main-thread path on a
        // network write: flush:true waits on the send task, which on a congested TCP link stalls the
        // frame that is starting the run. RUNCOMMIT and RUNEXEC are reliable and ordered, the host
        // cache replays both to late clients, and the beacon below re-publishes them until the
        // friend confirms, so a synchronous flush bought nothing but a main-thread hitch.
        net.SendRunLaunchCommit(descriptor, flush: false);
        var execute = RunLaunchCoordinator.MarkHostExecute(descriptor);
        net.SendRunLaunchExecute(execute, flush: false);

        // Converge instead of hoping: keep re-publishing the whole prerequisite set until the
        // client positively confirms it is launching.
        StartHostRunLaunchBeacon(descriptor.Sequence);
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

    /// <summary>
    /// Publishes RUNREADY ("this peer is now playing the committed run in this level"). Called once
    /// per launch sequence from <see cref="MarkInRun"/>, i.e. when the hero has actually
    /// initialized. This is the terminal state of the launch state machine: it advances the session
    /// phase to Playing, and on the host it is what lets a late joiner learn that the run is live.
    /// It was previously written but never invoked, so the phase never left LoadingLevel and the
    /// launch had no completion signal at all.
    /// </summary>
    internal static void SendRunReadyFromHero()
    {
        string levelId = string.Empty;
        try
        {
            levelId = ModEntry.me?._level?.map?.id?.ToString() ?? string.Empty;
        }
        catch
        {
        }

        RunLevelReady? ready;
        try
        {
            ready = RunLaunchCoordinator.MarkLocalPlaying(levelId);
        }
        catch (Exception ex)
        {
            _log?.Warning("[NetMod][RunLaunch] Failed to mark local run ready: {Message}", ex.Message);
            return;
        }

        if (ready == null)
            return;

        try
        {
            NetRef?.SendRunLevelReady(ready);
        }
        catch (Exception ex)
        {
            _log?.Warning("[NetMod][RunLaunch] Failed to send RUNREADY: {Message}", ex.Message);
        }
    }

    internal static void ReceiveRunLaunchCommitPayload(string payload)
    {
        if (!RunLaunchWireCodec.TryDecodeCommit(payload, out var descriptor, out var decodeError) ||
            descriptor == null)
        {
            _log?.Warning("[NetMod][RunLaunch] Malformed RUNCOMMIT: {Error}", decodeError);
            return;
        }

        // Receiving a host commit on a live non-host node IS the proof that we are the client. If
        // the coordinator has not observed the role transition yet, adopt it here rather than
        // rejecting a valid launch that nothing would ever re-offer.
        var localNet = NetRef;
        if (localNet != null && localNet.IsAlive && !localNet.IsHost)
            RunLaunchCoordinator.EnsureClientRoleForIncomingLaunch();

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
                // A genuinely new launch gets a fresh level-graph preference window; carrying the
                // previous launch's expiry over would skip the wait for a graph that is about to
                // arrive for this run.
                _clientLevelGraphWaitStartedTicks = 0;
                _clientLevelGraphWaitExpired = false;
            }
            SignalClientLaunchProgressLocked();
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
                    SignalClientLaunchProgressLocked();
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

        // The client is invoking the authoritative native loader for this sequence now.
        // Consume it immediately so later host SEED/RUNEXEC rebroadcasts cannot schedule
        // unconsumed_host_launch_seq_* after the world is already built.
        MarkRemoteLaunchSequenceConsumed(sequence, "client_launch_queued");

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

        // RUNREADY means "the sender is already playing this run". Receiving it as a client that is
        // NOT yet in a run is the protocol-level definition of joining mid-run — no timing guess
        // required. The flag is consumed once, by the first level this client enters.
        lock (Sync)
        {
            if (_role == NetRole.Client && !_inActualRun && !_midRunJoinSpawnPending)
            {
                _midRunJoinSpawnPending = true;
                _log?.Information(
                    "[NetMod][Session] Mid-run join detected (host already playing level={Level}); " +
                    "a host-approved spawn will be applied once the level is built",
                    ready.LevelId);
            }
        }
    }

    private static bool _midRunJoinSpawnPending;

    /// <summary>Consumed once by the joining client's first level; false for every normal launch.</summary>
    internal static bool TryConsumeMidRunJoinSpawn()
    {
        lock (Sync)
        {
            if (!_midRunJoinSpawnPending)
                return false;
            _midRunJoinSpawnPending = false;
            return true;
        }
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
        StopHostRunLaunchBeacon("host_launch_cancelled");
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

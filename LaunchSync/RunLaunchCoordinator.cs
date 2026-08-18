using DeadCellsMultiplayerMod.PortableCore;
using Serilog;

namespace DeadCellsMultiplayerMod;

internal enum PendingLaunchAction
{
    None,
    LoadSave,
    NewGame
}

/// <summary>
/// DCCM integration adapter for the portable run-launch contracts. It owns no Dead Cells
/// objects: the menu and User.newGame hooks call it before performing game-specific work.
/// </summary>
internal static class RunLaunchCoordinator
{
    internal readonly record struct PendingLaunchIntent(
        PendingLaunchAction Action,
        bool Custom,
        bool StreamEnabled);

    private static readonly object Sync = new();

    private static ILogger? _log;
    private static NetRole _role = NetRole.None;
    private static CoopSessionStateMachine _state = new();
    private static long _stateSequence;
    private static Guid _hostSessionId;
    private static long _nextRunId;
    private static PendingLaunchIntent _pendingLaunchIntent =
        new(PendingLaunchAction.NewGame, false, false);

    private static RunLaunchDescriptor? _hostDescriptor;
    private static RunLaunchDescriptor? _remoteDescriptor;
    private static int _hostExecutedSequence;
    private static int _remoteExecutedSequence;
    private static int _consumedRemoteSequence;

    private static Guid _ackSessionId;
    private static long _ackRunId;
    private static int _ackSequence;
    private static bool _ackAccepted;
    private static string _ackError = string.Empty;

    // Client-queued gate (protocol 17): the host waits for this before starting its native loader.
    private static Guid _queuedSessionId;
    private static long _queuedRunId;
    private static int _queuedSequence;
    private static bool _queuedAccepted;
    private static string _queuedError = string.Empty;
    private static int _localQueuedSequence;

    private static int _localReadySequence;
    private static int _remoteReadySequence;

    internal static void Initialize(ILogger logger)
    {
        lock (Sync)
        {
            _log = logger;
            ResetLocked(NetRole.None, "initialize");
        }
    }

    internal static CoopSessionSnapshot GetSessionSnapshot()
    {
        lock (Sync)
        {
            return _state.Snapshot;
        }
    }

    internal static NetRole CurrentRole
    {
        get
        {
            lock (Sync)
                return _role;
        }
    }

    internal static PortableCore.CoopSessionPhase MapClientLaunchPhaseToSessionPhase(
        LobbySession.ClientLaunchPhase phase)
    {
        return phase switch
        {
            LobbySession.ClientLaunchPhase.Lobby => PortableCore.CoopSessionPhase.Lobby,
            LobbySession.ClientLaunchPhase.IntentReceived => PortableCore.CoopSessionPhase.LaunchCommitted,
            LobbySession.ClientLaunchPhase.AwaitingPrereqs => PortableCore.CoopSessionPhase.LaunchCommitted,
            LobbySession.ClientLaunchPhase.Armed => PortableCore.CoopSessionPhase.LaunchCommitted,
            LobbySession.ClientLaunchPhase.Starting => PortableCore.CoopSessionPhase.LoadingLevel,
            LobbySession.ClientLaunchPhase.InRun => PortableCore.CoopSessionPhase.Playing,
            LobbySession.ClientLaunchPhase.RestartPending => PortableCore.CoopSessionPhase.TransitionCommitted,
            _ => PortableCore.CoopSessionPhase.Faulted
        };
    }

    internal static PendingLaunchIntent GetPendingLaunchIntent()
    {
        lock (Sync)
        {
            return _pendingLaunchIntent;
        }
    }

    internal static void SetPendingLaunchIntent(
        PendingLaunchAction action,
        bool custom,
        bool streamEnabled)
    {
        lock (Sync)
        {
            _pendingLaunchIntent = new PendingLaunchIntent(action, custom, streamEnabled);
        }
    }

    internal static void OnRoleChanged(NetRole previous, NetRole next)
    {
        if (previous == next)
            return;

        lock (Sync)
        {
            ResetLocked(next, $"role_{previous}_to_{next}");
        }
    }

    internal static void OnRemoteConnected(NetRole role)
    {
        lock (Sync)
        {
            if (_role != role)
                ResetLocked(role, "remote_connected_role_repair");
            EnsureLobbyLocked("remote_connected");
        }
    }

    internal static void OnRemoteDisconnected(NetRole role)
    {
        lock (Sync)
        {
            if (_role == NetRole.Host && role == NetRole.Host)
            {
                ClearAckLocked();
                ClearQueuedLocked();
                _remoteReadySequence = 0;
                Monitor.PulseAll(Sync);
            }
        }
    }

    internal static RunLaunchDescriptor CreateOrReuseHostDescriptor(
        int seed,
        int sequence,
        string launchKind,
        string initialLevelId,
        double? initialLevelSeed,
        int difficulty,
        int bossCells,
        ulong dlcFlags,
        int saveSlot,
        int bossRushSeed = 0,
        int levelGenSeed = 0,
        int bossRushTier = 0,
        string? route = null,
        string? bossSequence = null,
        ulong modifiers = 0UL,
        string? targetArena = null)
    {
        lock (Sync)
        {
            // Same reasoning as EnsureClientRoleForIncomingLaunch: the caller has already verified a
            // live host NetNode. Throwing here would escape into Dead Cells' native User.newGame
            // hook, which has no handler - the host process dies mid-launch and force-drops the
            // client. Adopt the role instead; only a genuine host/client contradiction still throws.
            if (_role == NetRole.None && LobbySession.NetRef is { IsAlive: true, IsHost: true })
            {
                ResetLocked(NetRole.Host, "adopt_host_role_for_local_launch");
                _log?.Information("[NetMod][RunLaunch] Adopted host role for a local launch commit");
            }

            if (_role != NetRole.Host)
                throw new InvalidOperationException("Only the host can create a run launch descriptor.");

            if (_hostDescriptor != null &&
                _hostDescriptor.Sequence == sequence &&
                _hostDescriptor.RunSeed == seed)
            {
                return _hostDescriptor;
            }

            EnsureLobbyLocked("host_create_launch");
            if (!CommitLaunchStateLocked("host_launch_commit"))
            {
                throw new InvalidOperationException(
                    $"Cannot commit a host launch while session phase is {_state.Phase}.");
            }

            if (_hostSessionId == Guid.Empty)
                _hostSessionId = Guid.NewGuid();

            var isBossRush = GameDataSync.IsBossRushLaunchKind(launchKind);
            // Boss Rush generation derives from the run seed today, so the authoritative Boss Rush
            // seed defaults to the run seed. Keeping it as a distinct field lets a future build pin a
            // separate value without another protocol change, and lets both peers validate it matches.
            var effectiveBossRushSeed = isBossRush
                ? (bossRushSeed > 0 ? bossRushSeed : seed)
                : Math.Max(0, bossRushSeed);

            var descriptor = new RunLaunchDescriptor(
                _hostSessionId,
                ++_nextRunId,
                sequence,
                BuildInfo.NetworkProtocolVersion,
                seed,
                string.IsNullOrWhiteSpace(launchKind) ? "unknown" : launchKind,
                string.IsNullOrWhiteSpace(initialLevelId) ? "PrisonStart" : initialLevelId,
                initialLevelSeed,
                Math.Max(0, difficulty),
                Math.Max(0, bossCells),
                isBossRush,
                dlcFlags,
                Math.Max(0, saveSlot),
                effectiveBossRushSeed,
                Math.Max(0, levelGenSeed),
                Math.Max(0, bossRushTier),
                route ?? string.Empty,
                bossSequence ?? string.Empty,
                modifiers,
                targetArena ?? string.Empty);

            if (!descriptor.TryValidate(out var validationError))
                throw new InvalidOperationException("Invalid host run descriptor: " + validationError);

            _hostDescriptor = descriptor;
            _hostExecutedSequence = 0;
            _localReadySequence = 0;
            _remoteReadySequence = 0;
            _localQueuedSequence = 0;
            ClearAckLocked();
            ClearQueuedLocked();

            _log?.Information(
                "[NetMod][RunLaunch] Host committed session={Session} run={RunId} seq={Sequence} seed={Seed} kind={Kind} level={Level} save={SaveSlot}",
                descriptor.SessionId,
                descriptor.RunId,
                descriptor.Sequence,
                descriptor.RunSeed,
                descriptor.LaunchKind,
                descriptor.InitialLevelId,
                descriptor.SaveSlot + 1);
            BossSyncDiag.Trace("launch committed role=host {Desc}", descriptor.ToDiagnosticString());

            return descriptor;
        }
    }

    internal static bool TryAcceptRemoteDescriptor(
        RunLaunchDescriptor descriptor,
        out RunLaunchAck ack,
        out bool isNewCommit)
    {
        lock (Sync)
        {
            isNewCommit = false;
            var error = ValidateRemoteDescriptorLocked(descriptor);
            if (!string.IsNullOrEmpty(error))
            {
                ack = new RunLaunchAck(
                    descriptor.SessionId,
                    descriptor.RunId,
                    descriptor.Sequence,
                    false,
                    error);
                return false;
            }

            if (_remoteDescriptor != null && _remoteDescriptor.HasSameIdentity(descriptor))
            {
                var updateError = ValidateDescriptorUpdateLocked(_remoteDescriptor, descriptor);
                if (!string.IsNullOrEmpty(updateError))
                {
                    ack = new RunLaunchAck(
                        descriptor.SessionId,
                        descriptor.RunId,
                        descriptor.Sequence,
                        false,
                        updateError);
                    return false;
                }

                var wasEnriched = !_remoteDescriptor.InitialLevelSeed.HasValue &&
                                  descriptor.InitialLevelSeed.HasValue;
                _remoteDescriptor = descriptor;
                Monitor.PulseAll(Sync);

                ack = new RunLaunchAck(
                    descriptor.SessionId,
                    descriptor.RunId,
                    descriptor.Sequence,
                    true,
                    string.Empty);

                if (wasEnriched)
                {
                    _log?.Information(
                        "[NetMod][RunLaunch] Client enriched seq={Sequence} with initial level={Level} levelSeed={LevelSeed}",
                        descriptor.Sequence,
                        descriptor.InitialLevelId,
                        descriptor.InitialLevelSeed);
                }
                return true;
            }

            if (_remoteDescriptor != null && descriptor.Sequence < _remoteDescriptor.Sequence)
            {
                var staleError = $"stale run sequence {descriptor.Sequence}; current is {_remoteDescriptor.Sequence}";
                ack = new RunLaunchAck(
                    descriptor.SessionId,
                    descriptor.RunId,
                    descriptor.Sequence,
                    false,
                    staleError);
                return false;
            }

            EnsureLobbyLocked("client_receive_launch");
            if (!CommitLaunchStateLocked("client_launch_commit"))
            {
                var phaseError = $"cannot commit launch while session phase is {_state.Phase}";
                ack = new RunLaunchAck(
                    descriptor.SessionId,
                    descriptor.RunId,
                    descriptor.Sequence,
                    false,
                    phaseError);
                return false;
            }

            _remoteDescriptor = descriptor;
            _remoteExecutedSequence = 0;
            _localReadySequence = 0;
            _remoteReadySequence = 0;
            isNewCommit = true;
            Monitor.PulseAll(Sync);

            ack = new RunLaunchAck(
                descriptor.SessionId,
                descriptor.RunId,
                descriptor.Sequence,
                true,
                string.Empty);

            _log?.Information(
                "[NetMod][RunLaunch] Client accepted session={Session} run={RunId} seq={Sequence} seed={Seed} kind={Kind} level={Level} save={SaveSlot}",
                descriptor.SessionId,
                descriptor.RunId,
                descriptor.Sequence,
                descriptor.RunSeed,
                descriptor.LaunchKind,
                descriptor.InitialLevelId,
                descriptor.SaveSlot + 1);
            BossSyncDiag.Trace("launch accepted role=client {Desc}", descriptor.ToDiagnosticString());
            return true;
        }
    }

    internal static void ReceiveHostAck(RunLaunchAck ack)
    {
        lock (Sync)
        {
            if (_role != NetRole.Host || _hostDescriptor == null)
                return;
            if (ack.SessionId != _hostDescriptor.SessionId ||
                ack.RunId != _hostDescriptor.RunId ||
                ack.Sequence != _hostDescriptor.Sequence)
            {
                _log?.Warning(
                    "[NetMod][RunLaunch] Ignored ACK for another launch session={Session} run={RunId} seq={Sequence}",
                    ack.SessionId,
                    ack.RunId,
                    ack.Sequence);
                return;
            }

            _ackSessionId = ack.SessionId;
            _ackRunId = ack.RunId;
            _ackSequence = ack.Sequence;
            _ackAccepted = ack.Accepted;
            _ackError = ack.Error ?? string.Empty;
            Monitor.PulseAll(Sync);

            if (ack.Accepted)
            {
                _log?.Information(
                    "[NetMod][RunLaunch] Client acknowledged session={Session} run={RunId} seq={Sequence}",
                    ack.SessionId,
                    ack.RunId,
                    ack.Sequence);
            }
            else
            {
                _log?.Warning(
                    "[NetMod][RunLaunch] Client rejected session={Session} run={RunId} seq={Sequence}: {Error}",
                    ack.SessionId,
                    ack.RunId,
                    ack.Sequence,
                    _ackError);
            }
        }
    }

    // Removed: WaitForHostAck / WaitForClientQueued.
    //
    // Both were blocking rendezvous helpers with no call sites. Launch synchronization is now
    // non-blocking end to end: the host publishes and keeps re-publishing (LobbySession's launch
    // beacon) while the client arms itself from received state, so nothing needs to park a thread
    // waiting for a peer. Keeping unused blocking waits around invited exactly the main-thread
    // stall this design exists to avoid.

    /// <summary>
    /// Client side: build the RUNQUEUED message once the client holds the authoritative seed and has
    /// enqueued the identical native launch. <paramref name="queued"/> is false to report a clean
    /// failure so the host can cancel instead of starting alone.
    /// </summary>
    internal static RunLaunchQueued? BuildClientQueued(int sequence, bool queued, string error)
    {
        lock (Sync)
        {
            if (_role != NetRole.Client || _remoteDescriptor == null || _remoteDescriptor.Sequence != sequence)
                return null;

            if (queued)
                _localQueuedSequence = sequence;

            return new RunLaunchQueued(
                _remoteDescriptor.SessionId,
                _remoteDescriptor.RunId,
                _remoteDescriptor.Sequence,
                queued,
                error ?? string.Empty);
        }
    }

    internal static bool HasLocalQueued(int sequence)
    {
        lock (Sync)
            return sequence > 0 && _localQueuedSequence == sequence;
    }

    /// <summary>Host side: record a client RUNQUEUED confirmation (or failure) for the current commit.</summary>
    internal static void ReceiveClientQueued(RunLaunchQueued queued)
    {
        lock (Sync)
        {
            if (_role != NetRole.Host || _hostDescriptor == null)
                return;
            if (queued.SessionId != _hostDescriptor.SessionId ||
                queued.RunId != _hostDescriptor.RunId ||
                queued.Sequence != _hostDescriptor.Sequence)
            {
                _log?.Warning(
                    "[NetMod][RunLaunch] Ignored RUNQUEUED for another launch session={Session} run={RunId} seq={Sequence}",
                    queued.SessionId,
                    queued.RunId,
                    queued.Sequence);
                return;
            }

            _queuedSessionId = queued.SessionId;
            _queuedRunId = queued.RunId;
            _queuedSequence = queued.Sequence;
            _queuedAccepted = queued.Queued;
            _queuedError = queued.Error ?? string.Empty;
            Monitor.PulseAll(Sync);

            if (queued.Queued)
            {
                _log?.Information(
                    "[NetMod][RunLaunch] Client queued launch session={Session} run={RunId} seq={Sequence}",
                    queued.SessionId,
                    queued.RunId,
                    queued.Sequence);
                BossSyncDiag.Trace("launch queued-ack received role=host seq={Sequence}", queued.Sequence);
            }
            else
            {
                _log?.Warning(
                    "[NetMod][RunLaunch] Client failed to queue launch seq={Sequence}: {Error}",
                    queued.Sequence,
                    _queuedError);
                BossSyncDiag.Warn("launch queue REJECTED by client seq={Sequence} reason={Error}", queued.Sequence, _queuedError);
            }
        }
    }

    /// <summary>Non-blocking host check used by the in-world door coordinator (polled per frame).</summary>
    internal static bool HasClientQueued(int sequence)
    {
        lock (Sync)
            return sequence > 0 && _queuedSequence == sequence && _queuedAccepted;
    }

    /// <summary>
    /// Host side: has the remote peer positively committed to this launch? Only RUNQUEUED ("I am
    /// invoking the native loader now") and RUNREADY ("I am in the level") count. A bare RUNACK is
    /// deliberately excluded: the client can accept a commit and still never arm its auto-start,
    /// which is precisely the failure the launch beacon exists to repair.
    /// </summary>
    internal static bool IsRemoteLaunchConfirmed(int sequence)
    {
        if (sequence <= 0)
            return false;

        lock (Sync)
        {
            if (_queuedSequence == sequence && _queuedAccepted)
                return true;
            return _remoteReadySequence >= sequence;
        }
    }

    /// <summary>Single-line host launch state for beacon diagnostics; no per-frame use.</summary>
    internal static string DescribeHostLaunchState()
    {
        lock (Sync)
        {
            var descriptor = _hostDescriptor;
            return descriptor == null
                ? "none"
                : $"seq={descriptor.Sequence} seed={descriptor.RunSeed} kind={descriptor.LaunchKind} " +
                  $"phase={_state.Phase} ack={(_ackSequence == descriptor.Sequence ? (_ackAccepted ? "ok" : "rejected") : "-")} " +
                  $"queued={(_queuedSequence == descriptor.Sequence && _queuedAccepted ? "yes" : "no")} " +
                  $"remoteReady={_remoteReadySequence}";
        }
    }

    /// <summary>
    /// Adopts the Client role when a live client NetNode receives host launch traffic before the
    /// role transition has propagated here.
    /// </summary>
    /// <remarks>
    /// The coordinator's role is only ever set from <see cref="OnRoleChanged"/>, which is driven by
    /// the transport handshake on the game main thread. Launch messages are parsed on that same
    /// queue but can be dispatched by a different path, so a RUNCOMMIT could legitimately arrive
    /// while the coordinator still believed it was role None. Every validation then failed with
    /// "local peer is not a client", the host got a negative ACK, and nothing ever retried: the
    /// client sat in the lobby permanently. Adopting the role here is safe because the caller has
    /// already proven a live non-host NetNode exists.
    /// </remarks>
    internal static bool EnsureClientRoleForIncomingLaunch()
    {
        lock (Sync)
        {
            if (_role == NetRole.Client)
                return true;
            if (_role == NetRole.Host)
                return false;

            ResetLocked(NetRole.Client, "adopt_client_role_for_incoming_launch");
            _log?.Information("[NetMod][RunLaunch] Adopted client role for an incoming host launch");
            return true;
        }
    }

    internal static RunLaunchExecute MarkHostExecute(RunLaunchDescriptor descriptor)
    {
        lock (Sync)
        {
            if (_role != NetRole.Host || _hostDescriptor == null || !_hostDescriptor.HasSameIdentity(descriptor))
                throw new InvalidOperationException("Cannot execute a run that is not the current host commit.");

            _hostExecutedSequence = descriptor.Sequence;
            Monitor.PulseAll(Sync);
            _log?.Information(
                "[NetMod][RunLaunch] Host execute session={Session} run={RunId} seq={Sequence}",
                descriptor.SessionId,
                descriptor.RunId,
                descriptor.Sequence);
            return new RunLaunchExecute(descriptor.SessionId, descriptor.RunId, descriptor.Sequence);
        }
    }

    internal static bool TryAcceptRemoteExecute(RunLaunchExecute execute, out string error)
    {
        lock (Sync)
        {
            if (_role != NetRole.Client)
            {
                error = "local peer is not a client";
                return false;
            }
            if (_remoteDescriptor == null)
            {
                error = "execute arrived before a run commit";
                return false;
            }
            if (execute.SessionId != _remoteDescriptor.SessionId ||
                execute.RunId != _remoteDescriptor.RunId ||
                execute.Sequence != _remoteDescriptor.Sequence)
            {
                error = "execute identity does not match the committed run";
                return false;
            }

            _remoteExecutedSequence = execute.Sequence;
            Monitor.PulseAll(Sync);
            _log?.Information(
                "[NetMod][RunLaunch] Client received execute session={Session} run={RunId} seq={Sequence}",
                execute.SessionId,
                execute.RunId,
                execute.Sequence);
            error = string.Empty;
            return true;
        }
    }

    internal static bool TryConsumeRemoteLaunch(
        int timeoutMs,
        out RunLaunchDescriptor? descriptor,
        out string error)
    {
        var deadline = Environment.TickCount64 + Math.Max(1, timeoutMs);
        lock (Sync)
        {
            while (_remoteDescriptor == null ||
                   _remoteExecutedSequence < _remoteDescriptor.Sequence ||
                   _remoteDescriptor.Sequence <= _consumedRemoteSequence)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    descriptor = null;
                    error = "timed out waiting for a committed and executed host launch";
                    return false;
                }

                Monitor.Wait(Sync, (int)Math.Min(remaining, 100));
            }

            descriptor = _remoteDescriptor;
            _consumedRemoteSequence = descriptor.Sequence;
            error = string.Empty;
            return true;
        }
    }

    internal static bool HasExecutableRemoteLaunch(int sequence)
    {
        lock (Sync)
        {
            return _remoteDescriptor != null &&
                   _remoteDescriptor.Sequence == sequence &&
                   _remoteExecutedSequence >= sequence;
        }
    }

    internal static RunLaunchDescriptor? GetCurrentHostDescriptor()
    {
        lock (Sync)
            return _hostDescriptor;
    }

    internal static RunLaunchDescriptor? GetCurrentRemoteDescriptor()
    {
        lock (Sync)
            return _remoteDescriptor;
    }

    /// <summary>
    /// Returns the already-committed run seed only while the coordinator is still
    /// inside an active run/load.  This is used to distinguish Dead Cells' native
    /// same-run restart re-entry into User.newGame from a genuinely new lobby launch.
    /// A restart must reuse the committed seed and must never try to commit a second
    /// RUNCOMMIT while the original session is LoadingLevel/Playing.
    ///
    /// LaunchCommitted must NOT count here: that is the pre-first-load client state
    /// after RUNCOMMIT/RUNEXEC. Treating it as an active run made the client's first
    /// User.newGame look like a native restart, skip seed consumption, and later
    /// force a false unconsumed_host_launch restart that regenerated the world.
    /// </summary>
    internal static bool TryGetActiveRunSeedForNativeRestart(out int seed, out CoopSessionPhase phase)
    {
        lock (Sync)
        {
            phase = _state.Phase;
            var descriptor = _role == NetRole.Host ? _hostDescriptor : _remoteDescriptor;
            if (descriptor != null &&
                _state.Phase is (CoopSessionPhase.LoadingLevel or
                                 CoopSessionPhase.Playing))
            {
                seed = descriptor.RunSeed;
                return seed > 0;
            }

            seed = 0;
            return false;
        }
    }


    internal static RunLaunchDescriptor? TryEnrichHostInitialLevel(
        string levelId,
        double levelSeed)
    {
        lock (Sync)
        {
            if (_role != NetRole.Host || _hostDescriptor == null)
                return null;
            if (string.IsNullOrWhiteSpace(levelId) ||
                double.IsNaN(levelSeed) ||
                double.IsInfinity(levelSeed))
            {
                return null;
            }

            // The first graph RNG observed after RUNEXEC is the only value that belongs in the
            // launch descriptor. Later biome seeds continue through the existing LSEED channel.
            if (_hostDescriptor.InitialLevelSeed.HasValue)
                return null;

            var enriched = _hostDescriptor with
            {
                InitialLevelId = levelId,
                InitialLevelSeed = levelSeed
            };

            if (!enriched.TryValidate(out var validationError))
            {
                _log?.Warning(
                    "[NetMod][RunLaunch] Refused initial-level enrichment seq={Sequence}: {Error}",
                    _hostDescriptor.Sequence,
                    validationError);
                return null;
            }

            _hostDescriptor = enriched;
            Monitor.PulseAll(Sync);
            _log?.Information(
                "[NetMod][RunLaunch] Host enriched seq={Sequence} with initial level={Level} levelSeed={LevelSeed}",
                enriched.Sequence,
                enriched.InitialLevelId,
                enriched.InitialLevelSeed);
            return enriched;
        }
    }

    internal static void MarkLocalLoading(int sequence, string reason)
    {
        lock (Sync)
        {
            var current = _role == NetRole.Host ? _hostDescriptor : _remoteDescriptor;
            if (current == null || current.Sequence != sequence)
                return;
            if (_state.Phase == CoopSessionPhase.LoadingLevel || _state.Phase == CoopSessionPhase.Playing)
                return;

            if (_state.Phase is not (CoopSessionPhase.LaunchCommitted or CoopSessionPhase.TransitionCommitted))
            {
                _log?.Warning(
                    "[NetMod][RunLaunch] Cannot mark loading from phase={Phase} seq={Sequence} ({Reason})",
                    _state.Phase,
                    sequence,
                    reason);
                return;
            }

            TryTransitionLocked(CoopSessionPhase.LoadingLevel, reason);
        }
    }

    internal static RunLevelReady? MarkLocalPlaying(string levelId)
    {
        lock (Sync)
        {
            var current = _role == NetRole.Host ? _hostDescriptor : _remoteDescriptor;
            if (current == null)
                return null;

            if (_state.Phase == CoopSessionPhase.LoadingLevel)
                TryTransitionLocked(CoopSessionPhase.Playing, "hero_initialized");
            else if (_state.Phase != CoopSessionPhase.Playing)
                return null;

            if (_localReadySequence == current.Sequence)
                return null;

            _localReadySequence = current.Sequence;
            var ready = new RunLevelReady(
                current.SessionId,
                current.RunId,
                current.Sequence,
                string.IsNullOrWhiteSpace(levelId) ? current.InitialLevelId : levelId);

            _log?.Information(
                "[NetMod][RunLaunch] Local level ready role={Role} session={Session} run={RunId} seq={Sequence} level={Level}",
                _role,
                ready.SessionId,
                ready.RunId,
                ready.Sequence,
                ready.LevelId);
            return ready;
        }
    }

    internal static void ReceiveRemoteReady(RunLevelReady ready)
    {
        lock (Sync)
        {
            var current = _role == NetRole.Host ? _hostDescriptor : _remoteDescriptor;
            if (current == null ||
                ready.SessionId != current.SessionId ||
                ready.RunId != current.RunId ||
                ready.Sequence != current.Sequence)
            {
                return;
            }

            if (ready.Sequence <= _remoteReadySequence)
                return;

            _remoteReadySequence = ready.Sequence;
            _log?.Information(
                "[NetMod][RunLaunch] Remote level ready role={Role} session={Session} run={RunId} seq={Sequence} level={Level}",
                _role,
                ready.SessionId,
                ready.RunId,
                ready.Sequence,
                ready.LevelId);
        }
    }

    internal static RunLaunchCancel? CancelHostLaunch(int sequence, string reason)
    {
        lock (Sync)
        {
            if (_role != NetRole.Host || _hostDescriptor == null || _hostDescriptor.Sequence != sequence)
                return null;

            var cancel = new RunLaunchCancel(
                _hostDescriptor.SessionId,
                _hostDescriptor.RunId,
                _hostDescriptor.Sequence,
                reason ?? string.Empty);

            _hostDescriptor = null;
            _hostExecutedSequence = 0;
            ClearAckLocked();
            ReturnToLobbyLocked("host_launch_cancelled");
            Monitor.PulseAll(Sync);
            return cancel;
        }
    }

    internal static bool TryAcceptRemoteCancel(RunLaunchCancel cancel)
    {
        lock (Sync)
        {
            if (_role != NetRole.Client || _remoteDescriptor == null)
                return false;
            if (cancel.SessionId != _remoteDescriptor.SessionId ||
                cancel.RunId != _remoteDescriptor.RunId ||
                cancel.Sequence != _remoteDescriptor.Sequence)
            {
                return false;
            }

            _remoteDescriptor = null;
            _remoteExecutedSequence = 0;
            _consumedRemoteSequence = Math.Min(_consumedRemoteSequence, cancel.Sequence - 1);
            ReturnToLobbyLocked("remote_launch_cancelled");
            Monitor.PulseAll(Sync);
            _log?.Warning(
                "[NetMod][RunLaunch] Host cancelled seq={Sequence}: {Reason}",
                cancel.Sequence,
                cancel.Reason ?? string.Empty);
            return true;
        }
    }

    private static string ValidateRemoteDescriptorLocked(RunLaunchDescriptor descriptor)
    {
        if (_role != NetRole.Client)
            return "local peer is not a client";
        if (!descriptor.TryValidate(out var validationError))
            return validationError;
        if (descriptor.ProtocolVersion != BuildInfo.NetworkProtocolVersion)
        {
            return $"launch protocol {descriptor.ProtocolVersion} does not match {BuildInfo.NetworkProtocolVersion}";
        }
        if (_remoteDescriptor != null &&
            descriptor.Sequence == _remoteDescriptor.Sequence &&
            !_remoteDescriptor.HasSameIdentity(descriptor))
        {
            return "run sequence was reused with a different identity";
        }

        return string.Empty;
    }

    private static string ValidateDescriptorUpdateLocked(
        RunLaunchDescriptor current,
        RunLaunchDescriptor incoming)
    {
        if (current.ProtocolVersion != incoming.ProtocolVersion ||
            current.RunSeed != incoming.RunSeed ||
            !string.Equals(current.LaunchKind, incoming.LaunchKind, StringComparison.Ordinal) ||
            current.Difficulty != incoming.Difficulty ||
            current.BossCells != incoming.BossCells ||
            current.BossRush != incoming.BossRush ||
            current.DlcFlags != incoming.DlcFlags ||
            current.SaveSlot != incoming.SaveSlot ||
            current.BossRushSeed != incoming.BossRushSeed ||
            current.LevelGenSeed != incoming.LevelGenSeed ||
            current.BossRushTier != incoming.BossRushTier ||
            current.Modifiers != incoming.Modifiers ||
            !string.Equals(current.Route, incoming.Route, StringComparison.Ordinal) ||
            !string.Equals(current.BossSequence, incoming.BossSequence, StringComparison.Ordinal) ||
            !string.Equals(current.TargetArena, incoming.TargetArena, StringComparison.Ordinal))
        {
            return "a committed run attempted to change immutable launch settings";
        }

        if (current.InitialLevelSeed.HasValue)
        {
            if (!incoming.InitialLevelSeed.HasValue ||
                current.InitialLevelSeed.Value != incoming.InitialLevelSeed.Value ||
                !string.Equals(current.InitialLevelId, incoming.InitialLevelId, StringComparison.Ordinal))
            {
                return "initial level metadata changed after it was committed";
            }
        }
        else if (!incoming.InitialLevelSeed.HasValue &&
                 !string.Equals(current.InitialLevelId, incoming.InitialLevelId, StringComparison.Ordinal))
        {
            return "initial level id changed without an authoritative level seed";
        }

        return string.Empty;
    }

    private static void ResetLocked(NetRole role, string reason)
    {
        _role = role;
        _state = new CoopSessionStateMachine();
        _stateSequence = 0;
        _hostSessionId = role == NetRole.Host ? Guid.NewGuid() : Guid.Empty;
        _nextRunId = 0;
        _pendingLaunchIntent = new PendingLaunchIntent(PendingLaunchAction.NewGame, false, false);
        _hostDescriptor = null;
        _remoteDescriptor = null;
        _hostExecutedSequence = 0;
        _remoteExecutedSequence = 0;
        _consumedRemoteSequence = 0;
        _localReadySequence = 0;
        _remoteReadySequence = 0;
        _localQueuedSequence = 0;
        ClearAckLocked();
        ClearQueuedLocked();

        if (role != NetRole.None)
            TryTransitionLocked(CoopSessionPhase.Lobby, reason);

        Monitor.PulseAll(Sync);
    }

    private static void EnsureLobbyLocked(string reason)
    {
        if (_role == NetRole.None)
            return;
        if (_state.Phase == CoopSessionPhase.Disconnected)
            TryTransitionLocked(CoopSessionPhase.Lobby, reason);
    }

    private static bool CommitLaunchStateLocked(string reason)
    {
        switch (_state.Phase)
        {
            case CoopSessionPhase.Disconnected:
                if (!TryTransitionLocked(CoopSessionPhase.Lobby, reason + "_enter_lobby"))
                    return false;
                return TryTransitionLocked(CoopSessionPhase.LaunchCommitted, reason);

            case CoopSessionPhase.Lobby:
                return TryTransitionLocked(CoopSessionPhase.LaunchCommitted, reason);

            case CoopSessionPhase.Playing:
            case CoopSessionPhase.LoadingLevel:
                // LoadingLevel was the one live phase with no commit path, so it fell to `default`
                // and threw. In practice the session spends most of its life here — MarkLocalPlaying
                // does not reliably advance it to Playing — which made two separate failures:
                // the native-restart crash ("Cannot commit a host launch while session phase is
                // LoadingLevel"), and the Boss Rush door precommit silently returning false because
                // its catch swallowed that same exception, so the client was never sent the
                // authoritative launch and both peers waited on each other until the gate timed out.
                //
                // A commit raised from a live run is a transition, not a fresh lobby launch, so it
                // resolves the same way Playing does.
                return TryTransitionLocked(CoopSessionPhase.TransitionCommitted, reason);

            case CoopSessionPhase.RunEnded:
            case CoopSessionPhase.Faulted:
                if (!TryTransitionLocked(CoopSessionPhase.Lobby, reason + "_return_lobby"))
                    return false;
                return TryTransitionLocked(CoopSessionPhase.LaunchCommitted, reason);

            case CoopSessionPhase.LaunchCommitted:
            case CoopSessionPhase.TransitionCommitted:
                return true;

            default:
                return false;
        }
    }

    private static void ReturnToLobbyLocked(string reason)
    {
        if (_state.Phase == CoopSessionPhase.Lobby || _state.Phase == CoopSessionPhase.Disconnected)
            return;

        if (!TryTransitionLocked(CoopSessionPhase.Lobby, reason))
        {
            if (TryTransitionLocked(CoopSessionPhase.Faulted, reason + "_fault"))
                TryTransitionLocked(CoopSessionPhase.Lobby, reason + "_recover");
        }
    }

    private static bool TryTransitionLocked(CoopSessionPhase next, string reason)
    {
        var sequence = ++_stateSequence;
        if (_state.TryTransition(next, sequence, reason, out var error))
        {
            _log?.Debug(
                "[NetMod][RunLaunch] Session phase={Phase} stateSeq={StateSequence} reason={Reason}",
                _state.Phase,
                sequence,
                reason);
            DeadCellsMultiplayerMod.SaveGuard.NotifyRunLaunchPhaseForSaveGuard(_state.Phase.ToString());
            return true;
        }

        _log?.Warning(
            "[NetMod][RunLaunch] State transition rejected current={Current} next={Next} stateSeq={StateSequence}: {Error}",
            _state.Phase,
            next,
            sequence,
            error);
        return false;
    }

    private static void ClearAckLocked()
    {
        _ackSessionId = Guid.Empty;
        _ackRunId = 0;
        _ackSequence = 0;
        _ackAccepted = false;
        _ackError = string.Empty;
    }

    private static void ClearQueuedLocked()
    {
        _queuedSessionId = Guid.Empty;
        _queuedRunId = 0;
        _queuedSequence = 0;
        _queuedAccepted = false;
        _queuedError = string.Empty;
    }
}

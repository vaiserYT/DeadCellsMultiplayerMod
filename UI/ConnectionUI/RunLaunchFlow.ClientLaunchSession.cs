namespace DeadCellsMultiplayerMod;

/// <summary>
/// Single client auto-start arming path. Call sites signal progress; only
/// <see cref="ReevaluateClientLaunchArmLocked"/> sets lobby <c>_pendingAutoStart</c>.
/// </summary>
internal static partial class LobbySession
{
    internal enum ClientLaunchPhase
    {
        Lobby,
        IntentReceived,
        AwaitingPrereqs,
        Armed,
        Starting,
        InRun,
        RestartPending
    }

    internal static ClientLaunchPhase _clientLaunchPhase = ClientLaunchPhase.Lobby;

    private static void SetClientLaunchPhaseLocked(ClientLaunchPhase phase)
    {
        _clientLaunchPhase = phase;
        _pendingAutoStart = phase == ClientLaunchPhase.Armed;

        // Starting is the only phase that owns an active auto-start claim. All other transitions
        // release an old claim so a later prerequisite update can arm the client again safely.
        if (phase != ClientLaunchPhase.Starting)
            _autoStartTriggered = false;
    }

    internal static void ResetClientLaunchSessionLocked()
    {
        SetClientLaunchPhaseLocked(ClientLaunchPhase.Lobby);
    }

    internal static void MarkClientLaunchInRunLocked()
    {
        SetClientLaunchPhaseLocked(ClientLaunchPhase.InRun);
    }

    internal static void MarkClientLaunchRestartPendingLocked()
    {
        SetClientLaunchPhaseLocked(ClientLaunchPhase.RestartPending);
    }

    /// <summary>
    /// After gen/seed/commit/exec/custom-data/level-desc progress, recompute whether
    /// the client lobby auto-start may arm.
    /// </summary>
    internal static void SignalClientLaunchProgressLocked()
    {
        ReevaluateClientLaunchArmLocked();
    }

    /// <summary>
    /// Network/main-thread entry for launch prereqs that arrive outside LobbySession
    /// (remote level graph, boss rune). Safe to call from receive paths.
    /// </summary>
    internal static void NotifyClientLaunchPrerequisiteProgress()
    {
        lock (Sync)
        {
            if (_role == NetRole.Client && !_inActualRun)
                SignalClientLaunchProgressLocked();
        }
    }

    internal static void ReevaluateClientLaunchArmLocked()
    {
        if (_role != NetRole.Client)
        {
            SetClientLaunchPhaseLocked(ClientLaunchPhase.Lobby);
            return;
        }

        if (_pendingClientRestartSeed.HasValue)
        {
            SetClientLaunchPhaseLocked(ClientLaunchPhase.RestartPending);
            return;
        }

        if (_inActualRun)
        {
            SetClientLaunchPhaseLocked(ClientLaunchPhase.InRun);
            return;
        }

        if (_autoStartTriggered || _clientLaunchPhase == ClientLaunchPhase.Starting)
            return;

        var hasIntent = _genArrived ||
                        _seedArrived ||
                        _structuredLaunchCommitArrived ||
                        _structuredLaunchExecuteSequence > 0 ||
                        _remoteCustomGameDataReady;
        if (!hasIntent)
        {
            SetClientLaunchPhaseLocked(ClientLaunchPhase.Lobby);
            return;
        }

        SetClientLaunchPhaseLocked(ClientLaunchPhase.IntentReceived);

        if (!IsPendingLaunchReadyForAutoStartLocked())
        {
            SetClientLaunchPhaseLocked(ClientLaunchPhase.AwaitingPrereqs);
            return;
        }

        // Fresh NewGame still requires the structured commit/execute barrier.
        if (RunLaunchCoordinator.GetPendingLaunchIntent().Action != PendingLaunchAction.LoadSave &&
            !CanAutoStartStructuredClientLaunchLocked())
        {
            SetClientLaunchPhaseLocked(ClientLaunchPhase.AwaitingPrereqs);
            return;
        }

        SetClientLaunchPhaseLocked(ClientLaunchPhase.Armed);
    }

    /// <summary>
    /// TickMenu claim: Armed → Starting. Returns false if another pump already claimed.
    /// </summary>
    internal static bool TryClaimClientAutoStartLocked()
    {
        if (_role != NetRole.Client ||
            _inActualRun ||
            _pendingClientRestartSeed.HasValue ||
            !_pendingAutoStart ||
            _autoStartTriggered ||
            !IsPendingLaunchReadyForAutoStartLocked())
        {
            return false;
        }

        if (RunLaunchCoordinator.GetPendingLaunchIntent().Action != PendingLaunchAction.LoadSave &&
            !CanAutoStartStructuredClientLaunchLocked())
        {
            return false;
        }

        _autoStartTriggered = true;
        SetClientLaunchPhaseLocked(ClientLaunchPhase.Starting);
        return true;
    }

    internal static void ReleaseClientAutoStartClaimLocked()
    {
        _autoStartTriggered = false;
        SetClientLaunchPhaseLocked(ClientLaunchPhase.Armed);
    }
}

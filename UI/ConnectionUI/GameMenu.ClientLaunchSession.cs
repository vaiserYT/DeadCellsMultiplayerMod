namespace DeadCellsMultiplayerMod;

/// <summary>
/// Single client auto-start arming path. Call sites signal progress; only
/// <see cref="ReevaluateClientLaunchArmLocked"/> sets lobby <c>_pendingAutoStart</c>.
/// </summary>
internal static partial class GameMenu
{
    private enum ClientLaunchPhase
    {
        Lobby,
        IntentReceived,
        AwaitingPrereqs,
        Armed,
        Starting,
        InRun,
        RestartPending
    }

    private static ClientLaunchPhase _clientLaunchPhase = ClientLaunchPhase.Lobby;

    private static void ResetClientLaunchSessionLocked()
    {
        _clientLaunchPhase = ClientLaunchPhase.Lobby;
    }

    private static void MarkClientLaunchInRunLocked()
    {
        _clientLaunchPhase = ClientLaunchPhase.InRun;
    }

    private static void MarkClientLaunchRestartPendingLocked()
    {
        _clientLaunchPhase = ClientLaunchPhase.RestartPending;
        _pendingAutoStart = false;
    }

    /// <summary>
    /// After gen/seed/commit/exec/custom-data/level-desc progress, recompute whether
    /// the client lobby auto-start may arm.
    /// </summary>
    private static void SignalClientLaunchProgressLocked()
    {
        ReevaluateClientLaunchArmLocked();
    }

    /// <summary>
    /// Network/main-thread entry for launch prereqs that arrive outside GameMenu
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

    private static void ReevaluateClientLaunchArmLocked()
    {
        if (_role != NetRole.Client)
        {
            _clientLaunchPhase = ClientLaunchPhase.Lobby;
            return;
        }

        if (_pendingClientRestartSeed.HasValue)
        {
            _clientLaunchPhase = ClientLaunchPhase.RestartPending;
            return;
        }

        if (_inActualRun)
        {
            _clientLaunchPhase = ClientLaunchPhase.InRun;
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
            _clientLaunchPhase = ClientLaunchPhase.Lobby;
            return;
        }

        _clientLaunchPhase = ClientLaunchPhase.IntentReceived;

        if (!IsPendingLaunchReadyForAutoStartLocked())
        {
            _clientLaunchPhase = ClientLaunchPhase.AwaitingPrereqs;
            _pendingAutoStart = false;
            return;
        }

        // Fresh NewGame still requires the structured commit/execute barrier.
        if (_pendingLaunchAction != PendingLaunchAction.LoadSave &&
            !CanAutoStartStructuredClientLaunchLocked())
        {
            _clientLaunchPhase = ClientLaunchPhase.AwaitingPrereqs;
            _pendingAutoStart = false;
            return;
        }

        _pendingAutoStart = true;
        _clientLaunchPhase = ClientLaunchPhase.Armed;
    }

    /// <summary>
    /// TickMenu claim: Armed → Starting. Returns false if another pump already claimed.
    /// </summary>
    private static bool TryClaimClientAutoStartLocked()
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

        if (_pendingLaunchAction != PendingLaunchAction.LoadSave &&
            !CanAutoStartStructuredClientLaunchLocked())
        {
            return false;
        }

        _autoStartTriggered = true;
        _clientLaunchPhase = ClientLaunchPhase.Starting;
        return true;
    }

    private static void ReleaseClientAutoStartClaimLocked()
    {
        _autoStartTriggered = false;
        _pendingAutoStart = true;
        _clientLaunchPhase = ClientLaunchPhase.Armed;
    }
}

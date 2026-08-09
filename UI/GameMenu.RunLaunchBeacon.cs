using System.Threading;

namespace DeadCellsMultiplayerMod;

/// <summary>
/// Host-side retransmission of the authoritative run launch, and the client-side stall report that
/// makes a failed launch diagnosable.
/// </summary>
/// <remarks>
/// The launch handshake used to be strictly fire-and-forget: RUNCOMMIT and RUNEXEC were sent once,
/// from inside the native launch path, and nothing ever checked whether the client acted on them.
/// Any single failure — a message that arrived before the receiver had adopted its role, a client
/// whose handshake completed a moment after Start was pressed (so it never got GEN), a prerequisite
/// that landed out of order — left the client parked in the lobby with no error on either machine
/// and no way to recover short of restarting both games.
///
/// The beacon replaces that with convergence: while the host holds a committed launch that the
/// client has not positively confirmed, it re-publishes the complete prerequisite set on a fixed
/// cadence. Re-publishing is idempotent (a same-identity RUNCOMMIT is treated as metadata
/// enrichment, RUNEXEC is level-triggered on a sequence, SEED is sequence-guarded), so extra beats
/// cost nothing and any one of them can be the one that repairs the client.
///
/// It deliberately runs on its own task rather than from TickMenu: the window where this matters
/// most is exactly when the host's main thread is busy generating the first level.
/// </remarks>
internal static partial class GameMenu
{
    private const int RunLaunchBeaconIntervalMs = 1200;
    /// <summary>
    /// Upper bound on how long the host keeps trying. Long enough to cover a slow client's whole
    /// load, short enough that an abandoned launch stops producing traffic and log noise.
    /// </summary>
    private const int RunLaunchBeaconMaxDurationMs = 120_000;
    private const int RunLaunchBeaconLogIntervalMs = 6000;

    private static readonly object RunLaunchBeaconSync = new();
    private static CancellationTokenSource? _runLaunchBeaconCts;
    private static Task? _runLaunchBeaconTask;

    /// <summary>
    /// Starts (or restarts) the beacon for the sequence the host just committed. Safe to call from
    /// the native launch path: it never blocks and never touches Dead Cells objects.
    /// </summary>
    internal static void StartHostRunLaunchBeacon(int sequence)
    {
        if (sequence <= 0)
            return;

        var net = NetRef;
        if (net == null || !net.IsAlive || !net.IsHost)
            return;

        CancellationTokenSource cts;
        lock (RunLaunchBeaconSync)
        {
            StopHostRunLaunchBeaconLocked("superseded_by_new_commit");
            cts = new CancellationTokenSource();
            _runLaunchBeaconCts = cts;
            _runLaunchBeaconTask = Task.Run(() => RunHostLaunchBeaconAsync(sequence, cts.Token));
        }

        _log?.Information("[NetMod][RunLaunch] Launch beacon armed seq={Sequence}", sequence);
    }

    internal static void StopHostRunLaunchBeacon(string reason)
    {
        lock (RunLaunchBeaconSync)
            StopHostRunLaunchBeaconLocked(reason);
    }

    private static void StopHostRunLaunchBeaconLocked(string reason)
    {
        var cts = _runLaunchBeaconCts;
        _runLaunchBeaconCts = null;
        _runLaunchBeaconTask = null;
        if (cts == null)
            return;

        try { cts.Cancel(); } catch { }
        try { cts.Dispose(); } catch { }
        _log?.Debug("[NetMod][RunLaunch] Launch beacon stopped ({Reason})", reason);
    }

    private static async Task RunHostLaunchBeaconAsync(int sequence, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + RunLaunchBeaconMaxDurationMs;
        var nextLogTick = 0L;

        try
        {
            while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                await Task.Delay(RunLaunchBeaconIntervalMs, ct).ConfigureAwait(false);

                var net = NetRef;
                if (net == null || !net.IsAlive || !net.IsHost)
                {
                    _log?.Debug("[NetMod][RunLaunch] Launch beacon stopping: no live host session");
                    return;
                }

                // Confirmed means the client told us it is invoking the loader (RUNQUEUED) or is
                // already in the level (RUNREADY). Anything less is not proof of progress.
                if (RunLaunchCoordinator.IsRemoteLaunchConfirmed(sequence))
                {
                    _log?.Information(
                        "[NetMod][RunLaunch] Launch beacon complete seq={Sequence}: friend confirmed the launch",
                        sequence);
                    return;
                }

                // Nothing to do until somebody is actually connected; keep looping so a player who
                // joins mid-load still gets the full prerequisite set from the next beat.
                if (!net.HasRemote)
                    continue;

                if (!net.TryResendCachedHostRunLaunch(out var cachedSequence))
                {
                    _log?.Debug("[NetMod][RunLaunch] Launch beacon stopping: no cached launch to replay");
                    return;
                }

                if (cachedSequence != 0 && cachedSequence != sequence)
                {
                    _log?.Debug(
                        "[NetMod][RunLaunch] Launch beacon stopping: cache moved to seq={Cached} (was {Sequence})",
                        cachedSequence,
                        sequence);
                    return;
                }

                var now = Environment.TickCount64;
                if (now >= nextLogTick)
                {
                    nextLogTick = now + RunLaunchBeaconLogIntervalMs;
                    _log?.Information(
                        "[NetMod][RunLaunch] Launch beacon re-published seq={Sequence} state={State}",
                        sequence,
                        RunLaunchCoordinator.DescribeHostLaunchState());
                }
            }

            if (!ct.IsCancellationRequested)
            {
                _log?.Warning(
                    "[NetMod][RunLaunch] Launch beacon gave up after {Seconds}s seq={Sequence} state={State}; " +
                    "the friend never confirmed the launch",
                    RunLaunchBeaconMaxDurationMs / 1000,
                    sequence,
                    RunLaunchCoordinator.DescribeHostLaunchState());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log?.Warning("[NetMod][RunLaunch] Launch beacon failed: {Message}", ex.Message);
        }
    }
}

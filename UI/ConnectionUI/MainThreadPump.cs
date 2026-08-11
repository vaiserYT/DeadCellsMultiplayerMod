using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using DeadCellsMultiplayerMod.Tools;

namespace DeadCellsMultiplayerMod;

/// <summary>
/// Unified main-thread pump: Critical coalesce, then Network Channel, then Normal UI queue.
/// </summary>
internal static class MainThreadPump
{
    private readonly struct MainThreadWorkItem
    {
        public readonly Action? Action;
        public readonly string? CoalesceKey;

        public MainThreadWorkItem(Action action)
        {
            Action = action;
            CoalesceKey = null;
        }

        public MainThreadWorkItem(string coalesceKey)
        {
            Action = null;
            CoalesceKey = coalesceKey;
        }
    }

    private static readonly ConcurrentQueue<MainThreadWorkItem> _mainThreadQueue = new();
    private static readonly ConcurrentDictionary<MethodInfo, string> _mainThreadActionLabelCache = new();
    private static readonly object MainThreadCoalesceSync = new();
    private static readonly Dictionary<string, Action> _coalescedMainThreadActions = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _pendingCoalescedMainThreadKeys = new(StringComparer.Ordinal);
    private static int _mainThreadQueueDepth;
    private const int MainThreadQueueMaxActionsPerPump = 64;
    private const double MainThreadQueueBudgetMs = 4.0;

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

    internal static void ResetMainThreadQueuesLocked()
    {
        while (_networkMainThreadQueue.Reader.TryRead(out _)) { }
        while (_criticalCoalescedKeys.TryDequeue(out _)) { }
        lock (CriticalMainThreadCoalesceSync)
            _criticalCoalescedActions.Clear();

        _mainThreadQueueDepth = _mainThreadQueue.Count;
        lock (MainThreadCoalesceSync)
        {
            _coalescedMainThreadActions.Clear();
            _pendingCoalescedMainThreadKeys.Clear();
        }
    }

    internal static void EnqueueMainThread(Action action)
    {
        if (action == null)
            return;

        _mainThreadQueue.Enqueue(new MainThreadWorkItem(action));
        Interlocked.Increment(ref _mainThreadQueueDepth);
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

        var shouldEnqueue = false;
        lock (MainThreadCoalesceSync)
        {
            _coalescedMainThreadActions[coalesceKey] = action;
            if (_pendingCoalescedMainThreadKeys.Add(coalesceKey))
                shouldEnqueue = true;
        }

        if (!shouldEnqueue)
            return;

        _mainThreadQueue.Enqueue(new MainThreadWorkItem(coalesceKey));
        Interlocked.Increment(ref _mainThreadQueueDepth);
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

        LobbySession.Log?.Warning(
            "[NetMod] Rejected critical coalesced main-thread work because its queue is full (key={Key})",
            key);
    }

    /// <summary>
    /// Drain critical coalesced work first, then reliable network protocol actions.
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
                LobbySession.Log?.Warning("[NetMod] Main thread task failed: {Message}", ex.Message);
            }
        }

        return processed;
    }

    internal static void ProcessMainThreadQueue()
    {
        var hitchStart = RuntimeHitchWatch.Start();
        var perfEnabled = RuntimeHitchWatch.Enabled;
        var startDepth = Volatile.Read(ref _mainThreadQueueDepth);
        var processed = 0;
        var slowActions = 0;
        var maxActionMs = 0.0;
        var maxActionLabel = string.Empty;
        var actionsStart = RuntimeHitchWatch.Start();

        // Keep the Steam friends join page live without requiring the player to back out and
        // reopen it. The discovery pass is rate-limited to 2.5 seconds and stays on the game
        // thread so no Steam API call can outlive the title screen or process shutdown.
        try
        {
            LobbySession.TickSteamFriendLobbyRefresh();
        }
        catch (Exception ex)
        {
            LobbySession.Log?.Debug("[NetMod][Steam] Friend lobby refresh tick failed: {Message}", ex.Message);
        }

        // Critical + reliable network protocol work must drain even when the UI queue is busy.
        processed += DrainCriticalAndNetworkMainThreadQueues(MainThreadQueueMaxActionsPerPump);

        while (_mainThreadQueue.TryDequeue(out var workItem))
        {
            Interlocked.Decrement(ref _mainThreadQueueDepth);
            Action? action = workItem.Action;
            var actionLabel = workItem.CoalesceKey;
            if (actionLabel != null)
            {
                lock (MainThreadCoalesceSync)
                {
                    _pendingCoalescedMainThreadKeys.Remove(actionLabel);
                    _coalescedMainThreadActions.TryGetValue(actionLabel, out action);
                    _coalescedMainThreadActions.Remove(actionLabel);
                }
            }

            if (action == null)
                continue;

            processed++;
            var actionStart = perfEnabled ? RuntimeHitchWatch.Start() : 0;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LobbySession.Log?.Warning("[NetMod] Main thread task failed: {Message}", ex.Message);
            }
            finally
            {
                if (perfEnabled)
                {
                    var actionMs = RuntimeHitchWatch.GetElapsedMilliseconds(actionStart);
                    if (actionMs > maxActionMs)
                    {
                        actionLabel ??= DescribeMainThreadAction(action);
                        maxActionMs = actionMs;
                        maxActionLabel = actionLabel;
                    }

                    if (actionMs >= RuntimeHitchWatch.MainThreadQueueActionSlowThresholdMs)
                    {
                        slowActions++;
                        actionLabel ??= DescribeMainThreadAction(action);
                        RuntimeHitchWatch.LogSlow(
                            LobbySession.Log,
                            $"LobbySession.MainThreadQueueAction:{actionLabel}",
                            actionMs,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"action={actionLabel} processed={processed} startDepth={startDepth}"));
                    }
                }
            }

            if (processed >= MainThreadQueueMaxActionsPerPump)
                break;
            if (RuntimeHitchWatch.GetElapsedMilliseconds(actionsStart) >= MainThreadQueueBudgetMs)
                break;
        }

        var actionsMs = RuntimeHitchWatch.GetElapsedMilliseconds(actionsStart);
        var remainingDepth = Volatile.Read(ref _mainThreadQueueDepth);
        var observedDepth = System.Math.Max(startDepth, remainingDepth);
        if (perfEnabled && observedDepth >= RuntimeHitchWatch.MainThreadQueueDepthThreshold)
        {
            RuntimeHitchWatch.LogCount(
                LobbySession.Log,
                "LobbySession.MainThreadQueueDepth",
                observedDepth,
                RuntimeHitchWatch.MainThreadQueueDepthThreshold,
                string.Create(CultureInfo.InvariantCulture, $"processed={processed} remaining={remainingDepth}"));
        }

        if (actionsMs >= RuntimeHitchWatch.MainThreadQueueActionsSlowThresholdMs)
        {
            RuntimeHitchWatch.LogSlow(
                LobbySession.Log,
                "LobbySession.ExecuteMainThreadActions",
                actionsMs,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"processed={processed} slowActions={slowActions} maxAction={maxActionLabel} maxMs={maxActionMs:0.00} remaining={remainingDepth}"));
        }

        var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
        if (hitchMs >= RuntimeHitchWatch.MainThreadQueueSlowThresholdMs)
        {
            RuntimeHitchWatch.LogSlow(
                LobbySession.Log,
                "MainThreadPump.ProcessMainThreadQueue",
                hitchMs,
                string.Create(CultureInfo.InvariantCulture, $"processed={processed} startDepth={startDepth} remaining={remainingDepth}"));
        }
    }

    private static string DescribeMainThreadAction(Action? action)
    {
        if (action == null)
            return "null";

        var method = action.Method;
        return _mainThreadActionLabelCache.GetOrAdd(method, static m =>
        {
            var declaringType = m.DeclaringType?.FullName;
            if (!string.IsNullOrWhiteSpace(declaringType))
                return $"{declaringType}.{m.Name}";

            return m.Name;
        });
    }
}

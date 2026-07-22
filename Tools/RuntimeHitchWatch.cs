using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;

namespace DeadCellsMultiplayerMod.Tools;

internal static class RuntimeHitchWatch
{
    private const double LogCooldownSeconds = 2.0;
    private static readonly long LogCooldownTicks = (long)(Stopwatch.Frequency * LogCooldownSeconds);
    private static readonly ConcurrentDictionary<string, long> LastLogTicksByKey = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> SuppressedCountsByKey = new(StringComparer.Ordinal);

    internal const double MainThreadQueueSlowThresholdMs = 8.0;
    internal const double MainThreadQueueActionsSlowThresholdMs = 6.0;
    internal const double MainThreadQueueActionSlowThresholdMs = 4.0;
    internal const int MainThreadQueueDepthThreshold = 128;
    internal const double ModFrameSlowThresholdMs = 8.0;
    internal const double ModHeroSlowThresholdMs = 8.0;
    internal const double ModHeroStepSlowThresholdMs = 2.0;
    internal const double MobSyncConsumeSlowThresholdMs = 6.0;
    internal const double MobSyncFlushSlowThresholdMs = 6.0;
    internal const double LevelExitSlowThresholdMs = 4.0;
    internal const double LevelExitStepSlowThresholdMs = 2.0;
    internal const double InteractionSlowThresholdMs = 4.0;
    internal const double GhostRuntimeSlowThresholdMs = 4.0;
    internal const double GhostRuntimeStepSlowThresholdMs = 2.0;
    internal static bool Enabled => MultiplayerSettingsStorage.ShowPerfLogs;

    internal static long Start() => Stopwatch.GetTimestamp();

    internal static double GetElapsedMilliseconds(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    internal static void LogSlow(ILogger? log, string key, double elapsedMs, string? details = null)
    {
        if (log == null || !MultiplayerSettingsStorage.ShowPerfLogs)
            return;

        if (!TryEnterLogWindow(key, out var suppressed))
            return;

        if (suppressed > 0)
        {
            if (string.IsNullOrWhiteSpace(details))
            {
                log.Warning("[Perf] Slow {Key}: {ElapsedMs:0.00} ms ({Suppressed} similar events suppressed)", key, elapsedMs, suppressed);
            }
            else
            {
                log.Warning("[Perf] Slow {Key}: {ElapsedMs:0.00} ms ({Details}) ({Suppressed} similar events suppressed)", key, elapsedMs, details, suppressed);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(details))
            log.Warning("[Perf] Slow {Key}: {ElapsedMs:0.00} ms", key, elapsedMs);
        else
            log.Warning("[Perf] Slow {Key}: {ElapsedMs:0.00} ms ({Details})", key, elapsedMs, details);
    }

    internal static void LogCount(ILogger? log, string key, int count, int threshold, string? details = null)
    {
        if (log == null || count < threshold || !MultiplayerSettingsStorage.ShowPerfLogs)
            return;

        if (!TryEnterLogWindow(key, out var suppressed))
            return;

        if (suppressed > 0)
        {
            if (string.IsNullOrWhiteSpace(details))
            {
                log.Warning("[Perf] High {Key}: {Count} ({Suppressed} similar events suppressed)", key, count, suppressed);
            }
            else
            {
                log.Warning("[Perf] High {Key}: {Count} ({Details}) ({Suppressed} similar events suppressed)", key, count, details, suppressed);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(details))
            log.Warning("[Perf] High {Key}: {Count}", key, count);
        else
            log.Warning("[Perf] High {Key}: {Count} ({Details})", key, count, details);
    }

    private static bool TryEnterLogWindow(string key, out int suppressed)
    {
        suppressed = 0;
        var now = Stopwatch.GetTimestamp();
        if (LastLogTicksByKey.TryGetValue(key, out var lastTicks) &&
            now - lastTicks < LogCooldownTicks)
        {
            SuppressedCountsByKey.AddOrUpdate(key, 1, static (_, current) => current + 1);
            return false;
        }

        LastLogTicksByKey[key] = now;
        if (SuppressedCountsByKey.TryRemove(key, out var suppressedCount))
            suppressed = suppressedCount;

        return true;
    }
}

using System;
using Serilog;

namespace DeadCellsMultiplayerMod;

/// <summary>
/// Structured, low-noise diagnostics for the boss multiplayer lifecycle. Verbose lines are gated
/// behind <see cref="Enabled"/> (env <c>DCCM_BOSS_SYNC_TRACE=1</c> or the in-game "Boss sync trace
/// logging" setting) so normal users never see them. State changes, mismatches, repairs, timeouts
/// and failures are logged regardless so a broken co-op session is always diagnosable. Never call
/// these from a per-frame hot path with <see cref="Enabled"/> false-gated content already; the
/// verbose overloads short-circuit, but keep call sites on state transitions only.
/// </summary>
internal static class BossSyncDiag
{
    private static readonly bool EnvTraceEnabled = string.Equals(
        Environment.GetEnvironmentVariable("DCCM_BOSS_SYNC_TRACE"),
        "1",
        StringComparison.Ordinal);

    public static bool Enabled
    {
        get
        {
            if (EnvTraceEnabled)
                return true;
            try { return MultiplayerSettingsStorage.DebugBossSyncTrace; }
            catch { return false; }
        }
    }

    /// <summary>Canonical role token for log correlation.</summary>
    public static string Role(NetNode? net)
    {
        if (net == null || !net.IsAlive)
            return "solo";
        return net.IsHost ? "host" : "client";
    }

    /// <summary>Verbose state-change line (only emitted when boss tracing is enabled).</summary>
    public static void Trace(string template, params object?[] args)
    {
        if (!Enabled)
            return;
        Log.Information("[BossSync] " + template, args);
    }

    /// <summary>Always-emitted line for mismatches, repairs, timeouts and failures.</summary>
    public static void Warn(string template, params object?[] args)
    {
        Log.Warning("[BossSync] " + template, args);
    }

    /// <summary>Always-emitted line for hard desyncs / unrecoverable launch failures.</summary>
    public static void Error(string template, params object?[] args)
    {
        Log.Error("[BossSync] " + template, args);
    }
}

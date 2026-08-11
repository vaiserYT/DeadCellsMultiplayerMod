using System;
using System.Collections.Generic;
using dc.en;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    /// <summary>
    /// Client-side boss death completion watchdog.
    ///
    /// The client never runs a boss death on its own authority: <c>Hook_Mob_onDie</c> suppresses
    /// the local die and waits for the host's MOBDIE/MOBEVENT confirmation, and
    /// <c>ApplyAuthoritativeLifeState</c> intentionally does not kill a boss on a bare life=0
    /// state. When the confirming death packet could not be resolved back to the local boss
    /// (type-signature mismatch on a rebuilt phase proxy, a Boss Rush wrapper with a different
    /// generated name, or a lost/rebound sync id), the client boss was stranded alive at 0/1 HP
    /// with a dead-looking host anim: no loot, no boss-cell reward, arena doors closed and the
    /// camera locked on the arena.
    ///
    /// This partial closes that hole with two cooperating mechanisms, both client-only:
    ///  1. Every authoritative zero-life observation for a boss is remembered. If the host does
    ///     not rebuild the phase with positive HP within a short grace window, the vanilla death
    ///     is force-completed locally exactly once (full onDie: loot, reward, doors, camera).
    ///  2. Boss-typed MOBDIE packets that failed to resolve are buffered and retried each frame;
    ///     after an escalation window they may fall back to the unique living boss combatant or
    ///     to a proximity-unique match, so a rebuilt/rewrapped boss still receives its death.
    ///
    /// Host phase rebuilds are safe: the host publishes a ForceState with positive HP in the same
    /// frame it rebuilds, which clears the zero-life marker well inside the grace window, and the
    /// host never sends MOBDIE for a continuing boss.
    /// </summary>
    public partial class MobsSynchronization
    {
        /// <summary>Fixed frames (level.ftime, 30fps) of authoritative zero life before the client
        /// force-completes the boss death (~3 s). Long enough for a multi-phase host rebuild's
        /// positive-HP ForceState to arrive and cancel; short enough that the arena never feels
        /// stuck.</summary>
        private const double ClientBossZeroLifeForceDeathGraceFrames = 30.0 * 3.0;

        /// <summary>Minimum fixed frames between resolution retries for one buffered boss death packet (~0.5 s).</summary>
        private const double ClientUnresolvedBossDieRetryIntervalFrames = 15.0;

        /// <summary>Fixed frames after which a buffered boss death may use the unique/proximity fallback (~4 s,
        /// after the host tombstone resend burst has had every chance to resolve strictly).</summary>
        private const double ClientUnresolvedBossDieEscalateFrames = 30.0 * 4.0;

        /// <summary>Fixed frames after which a buffered boss death is dropped (~30 s).</summary>
        private const double ClientUnresolvedBossDieExpireFrames = 30.0 * 30.0;

        /// <summary>Proximity fallback radius. Must be generous (death anim drift) yet small enough to
        /// never bridge two simultaneous Boss Rush duo arenas.</summary>
        private const double ClientBossDieProximityFallbackRadiusPx = 480.0;

        private sealed class UnresolvedBossDie
        {
            public int SyncId;
            public int Generation;
            public double X;
            public double Y;
            public string Type = string.Empty;
            public double FirstFrame;
            public double LastAttemptFrame;
        }

        /// <summary>Guarded by <see cref="Sync"/>.</summary>
        private static readonly List<UnresolvedBossDie> s_unresolvedBossDies = new();

        /// <summary>Guarded by <see cref="Sync"/>. First frame the client saw authoritative host life &lt;= 0 for a boss.</summary>
        private static readonly Dictionary<Mob, double> s_clientBossAuthoritativeZeroLifeFrame =
            new(ReferenceEqualityComparer.Instance);

        private static readonly List<Mob> s_bossWatchdogMobScratch = new();
        private static readonly List<UnresolvedBossDie> s_bossWatchdogDieScratch = new();

        /// <summary>
        /// Records that the host reported life &lt;= 0 for this boss. Called from the authoritative
        /// life/hit apply paths. The marker is cleared by any positive authoritative life (host
        /// phase rebuild) or by a completed death.
        /// </summary>
        private static void MarkClientBossAuthoritativeZeroLife(Mob? mob)
        {
            if (mob == null || !IsClient(LobbySession.NetRef))
                return;
            if (!BossSyncHelpers.IsBossEncounterCombatant(mob))
                return;

            try
            {
                if (mob.destroyed)
                    return;
            }
            catch
            {
                return;
            }

            lock (Sync)
            {
                if (clientCompletedAuthoritativeBossDeaths.Contains(mob))
                    return;
                if (!s_clientBossAuthoritativeZeroLifeFrame.ContainsKey(mob))
                    s_clientBossAuthoritativeZeroLifeFrame[mob] = GetCurrentFrame(mob);
            }
        }

        private static void ClearClientBossAuthoritativeZeroLife(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                s_clientBossAuthoritativeZeroLifeFrame.Remove(mob);
            }
        }

        /// <summary>
        /// Buffers a boss-typed MOBDIE packet whose target could not be resolved so it can be
        /// retried once the boss registers/rebinds. Caller already holds <see cref="Sync"/>.
        /// </summary>
        private static void RememberUnresolvedBossDieLocked(NetNode.MobDie die)
        {
            if (!BossSyncHelpers.IsBossTypeSignature(die.Type))
                return;

            for (var i = 0; i < s_unresolvedBossDies.Count; i++)
            {
                var existing = s_unresolvedBossDies[i];
                if (existing.SyncId == die.MobIndex && existing.Generation == die.Generation)
                {
                    // Refresh the coordinates from the newest resend but keep FirstFrame so
                    // escalation/expiry timing is measured from the first miss.
                    existing.X = die.X;
                    existing.Y = die.Y;
                    if (!string.IsNullOrWhiteSpace(die.Type))
                        existing.Type = die.Type;
                    return;
                }
            }

            var frame = GetCurrentFrame(null);
            s_unresolvedBossDies.Add(new UnresolvedBossDie
            {
                SyncId = die.MobIndex,
                Generation = die.Generation,
                X = die.X,
                Y = die.Y,
                Type = die.Type ?? string.Empty,
                FirstFrame = frame,
                LastAttemptFrame = -99999.0
            });

            MobSyncTrace.LogIncomingMappingMismatch(
                "death",
                die.MobIndex,
                die.Type ?? string.Empty,
                string.Empty,
                "boss_die_buffered_for_retry");
        }

        /// <summary>
        /// Per-frame client pass. Retries buffered boss death packets and force-completes bosses
        /// stranded at authoritative zero life.
        /// </summary>
        private static void ProcessClientBossDeathWatchdog()
        {
            if (!IsClient(LobbySession.NetRef))
                return;
            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return;

            var frame = GetCurrentFrame(null);

            RetryUnresolvedBossDies(frame, identityToken);
            ForceCompleteZeroLifeBosses(frame);
        }

        private static void RetryUnresolvedBossDies(double frame, int identityToken)
        {
            s_bossWatchdogDieScratch.Clear();
            lock (Sync)
            {
                if (s_unresolvedBossDies.Count == 0)
                    return;

                for (var i = s_unresolvedBossDies.Count - 1; i >= 0; i--)
                {
                    var entry = s_unresolvedBossDies[i];
                    if (entry == null ||
                        entry.Generation != identityToken ||
                        frame - entry.FirstFrame >= ClientUnresolvedBossDieExpireFrames)
                    {
                        s_unresolvedBossDies.RemoveAt(i);
                        continue;
                    }

                    if (frame - entry.LastAttemptFrame < ClientUnresolvedBossDieRetryIntervalFrames)
                        continue;

                    entry.LastAttemptFrame = frame;
                    s_bossWatchdogDieScratch.Add(entry);
                }
            }

            for (var i = 0; i < s_bossWatchdogDieScratch.Count; i++)
            {
                var entry = s_bossWatchdogDieScratch[i];
                if (entry == null)
                    continue;

                // First choice: the normal strict resolver (exact mapping / unique typed boss).
                var die = new NetNode.MobDie(0, entry.SyncId, entry.X, entry.Y, entry.Generation, entry.Type);
                var mob = ResolveMobFromDieLocked(die);

                // Escalation: after the strict resolver has had a fair window (covering phase
                // rebuilds, late registration and rebinds), fall back to the unique living boss
                // combatant, or to a proximity-unique one. The host only ever sends a boss death
                // for a finished encounter, so once escalated this cannot kill a continuing boss.
                if (mob == null && frame - entry.FirstFrame >= ClientUnresolvedBossDieEscalateFrames)
                    mob = TryResolveBossDieByUniqueOrProximityLocked(entry.SyncId, entry.X, entry.Y);

                if (mob == null)
                    continue;

                lock (Sync)
                {
                    s_unresolvedBossDies.Remove(entry);
                    if (clientCompletedAuthoritativeBossDeaths.Contains(mob))
                        continue;
                }

                RunClientAuthoritativeBossDeath(mob, $"unresolved_boss_die_retry syncId={entry.SyncId}");
            }

            s_bossWatchdogDieScratch.Clear();
        }

        private static void ForceCompleteZeroLifeBosses(double frame)
        {
            s_bossWatchdogMobScratch.Clear();
            lock (Sync)
            {
                if (s_clientBossAuthoritativeZeroLifeFrame.Count == 0)
                    return;

                List<Mob>? removeScratch = null;
                foreach (var pair in s_clientBossAuthoritativeZeroLifeFrame)
                {
                    var mob = pair.Key;
                    var destroyed = false;
                    var life = 0;
                    try
                    {
                        destroyed = mob.destroyed;
                        life = mob.life;
                    }
                    catch
                    {
                        destroyed = true;
                    }

                    // Completed, destroyed, or host-rebuilt (positive HP applied elsewhere clears
                    // the marker too, but a locally healed value above the suppressed-die clamp is
                    // also treated as "the encounter continues").
                    if (destroyed || clientCompletedAuthoritativeBossDeaths.Contains(mob) || life > 1)
                    {
                        (removeScratch ??= new List<Mob>()).Add(mob);
                        continue;
                    }

                    if (frame - pair.Value < ClientBossZeroLifeForceDeathGraceFrames)
                        continue;

                    s_bossWatchdogMobScratch.Add(mob);
                }

                if (removeScratch != null)
                {
                    for (var i = 0; i < removeScratch.Count; i++)
                        s_clientBossAuthoritativeZeroLifeFrame.Remove(removeScratch[i]);
                }
            }

            for (var i = 0; i < s_bossWatchdogMobScratch.Count; i++)
                RunClientAuthoritativeBossDeath(s_bossWatchdogMobScratch[i], "authoritative_zero_life_watchdog");

            s_bossWatchdogMobScratch.Clear();
        }

        /// <summary>
        /// Last-resort resolver for an escalated boss death packet: the single living boss
        /// combatant in the level, or the only living boss combatant within the fallback radius
        /// of the packet position. Boss Rush duos/trios remain safe because a second living boss
        /// inside the radius aborts the proximity match, and the whole-level fallback requires
        /// exactly one living boss.
        /// </summary>
        private static Mob? TryResolveBossDieByUniqueOrProximityLocked(int syncId, double x, double y)
        {
            lock (Sync)
            {
                Mob? uniqueLiving = null;
                var livingCount = 0;

                Mob? nearestInRadius = null;
                var nearestDistSq = double.MaxValue;
                var insideRadiusCount = 0;
                var radiusSq = ClientBossDieProximityFallbackRadiusPx * ClientBossDieProximityFallbackRadiusPx;

                for (var i = 0; i < trackedMobs.Count; i++)
                {
                    var candidate = trackedMobs[i];
                    if (candidate == null || !BossSyncHelpers.IsBossEncounterCombatant(candidate))
                        continue;
                    if (clientCompletedAuthoritativeBossDeaths.Contains(candidate))
                        continue;

                    try
                    {
                        if (candidate.destroyed)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    livingCount++;
                    uniqueLiving = candidate;

                    var dx = GetWorldX(candidate) - x;
                    var dy = GetWorldY(candidate) - y;
                    var distSq = dx * dx + dy * dy;
                    if (distSq <= radiusSq)
                    {
                        insideRadiusCount++;
                        if (distSq < nearestDistSq)
                        {
                            nearestDistSq = distSq;
                            nearestInRadius = candidate;
                        }
                    }
                }

                Mob? resolved = null;
                if (livingCount == 1)
                    resolved = uniqueLiving;
                else if (insideRadiusCount == 1)
                    resolved = nearestInRadius;

                if (resolved == null)
                    return null;

                TryRebindTrackedMobSyncIdLocked(resolved, syncId);
                return resolved;
            }
        }

        /// <summary>
        /// Runs the full vanilla boss death on the client under authoritative depth so loot,
        /// boss-cell rewards, arena doors, outro scripting and camera release all execute exactly
        /// as in a solo run. Idempotent per boss via <see cref="clientCompletedAuthoritativeBossDeaths"/>.
        /// </summary>
        private static void RunClientAuthoritativeBossDeath(Mob? mob, string reason)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                if (clientCompletedAuthoritativeBossDeaths.Contains(mob))
                    return;
            }

            var destroyed = false;
            try { destroyed = mob.destroyed; } catch { destroyed = true; }

            if (!destroyed)
            {
                TryWakeMobForForcedSimulation(mob);
                try
                {
                    RunWithAuthoritativeClientMobDie(mob, () =>
                    {
                        RunWithSuppressedMobDieSend(() =>
                        {
                            mob.life = 0;
                            mob.onDie();
                        });
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[MobSync] client forced boss death failed ({Reason})", reason);
                }
            }

            lock (Sync)
            {
                if (IsCompletedAuthoritativeBossDeath(mob))
                    clientCompletedAuthoritativeBossDeaths.Add(mob);
                clientPendingSuppressedBossDies.Remove(mob);
                clientPendingSuppressedMobDies.Remove(mob);
                s_clientBossAuthoritativeZeroLifeFrame.Remove(mob);
            }

            Log.Information("[MobSync] client completed authoritative boss death ({Reason})", reason);
        }

        /// <summary>Called from <see cref="ResetMobTrackingStateLocked"/> (caller holds <see cref="Sync"/>).</summary>
        private static void ResetBossDeathWatchdogStateLocked()
        {
            s_unresolvedBossDies.Clear();
            s_clientBossAuthoritativeZeroLifeFrame.Clear();
            s_bossWatchdogMobScratch.Clear();
            s_bossWatchdogDieScratch.Clear();
        }
    }
}

using System;
using System.Collections.Generic;
using dc;
using dc.en;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    /// <summary>
    /// Host-side activation pass for enemies that are near a REMOTE player but far from the host's
    /// own hero.
    /// </summary>
    /// <remarks>
    /// Dead Cells culls entities against <c>game.hero</c>. On the host that is the host's character,
    /// so an enemy standing next to the second player - but off the host's screen - is marked
    /// <c>isOutOfGame</c> and vanilla stops driving its behaviour tree.
    ///
    /// Every host-side targeting and wake routine the mod had was invoked from
    /// <c>Hook_Mob_preUpdate</c> (<c>TryApplyHostClientVisibilityInterest</c>,
    /// <c>TryAssignHostAttackTarget</c>, <c>TryMaintainHostBossSurvivorTarget</c>), and
    /// <c>TryRepairHostMobAfterPlayerCombatStateChange</c> additionally skips any mob matching
    /// <c>isOutOfGame &amp;&amp; !isOnScreen</c>. In other words the remedy for "this mob is culled"
    /// only ran for mobs that were not culled. That is the shared root cause behind three reported
    /// symptoms at once:
    ///   - malaise/dynamic mobs that appear on the client but stand still and do nothing,
    ///   - enemies that never aggro the second player when the host is elsewhere,
    ///   - bosses that stop fighting a survivor because the host's character walked away or died.
    /// A dormant mob never acquires a target, so <c>HasValidLivingPlayerCombatTarget</c> stays false,
    /// <c>ResolveHostMobSyncPriority</c> reports Dormant, and almost no state is streamed for it.
    ///
    /// This pass gives those routines a driver that does not depend on vanilla choosing to update
    /// the mob. It only ever runs on the host, only while a living remote player exists, and only
    /// promotes mobs inside a bounded box around that player - so ordinary distance culling is left
    /// intact everywhere else. The host stays the sole authority: the mob is handed to the same
    /// shared <see cref="TryAssignHostAttackTarget"/> path used by every other enemy, with all of its
    /// guards (AI lock, queued/charging skill, retention envelope, switch cooldown) unchanged.
    /// </remarks>
    public partial class MobsSynchronization
    {
        /// <summary>
        /// Roughly one screen around the remote player. Wide enough that a mob is simulated before
        /// the second player is in weapon range, narrow enough that the host is not forced to run
        /// the whole level.
        /// </summary>
        private const double HostRemoteActivationRangeXPx = 24.0 * 32.0;

        private const double HostRemoteActivationRangeYPx = 24.0 * 18.0;

        /// <summary>Tracked mobs examined per frame. The cursor wraps, so every mob is revisited.</summary>
        private const int HostRemoteActivationScanBudgetPerFrame = 96;

        private const long HostRemoteActivationLogIntervalMs = 5000;

        private static int s_hostRemoteActivationCursor;
        private static long s_hostRemoteActivationLastLogMs;
        private static readonly List<Entity> s_hostRemoteActivationPlayers = new();
        private static readonly List<Mob> s_hostRemoteActivationMobs = new();

        /// <summary>
        /// Collects remote players that are alive, not downed and on this host's current level.
        /// Returns false when there is nobody to activate for, which is the common single-player and
        /// "client is on another floor" case and costs nothing.
        /// </summary>
        private static bool TryCollectLivingRemotePlayers(List<Entity> destination)
        {
            destination.Clear();

            var clients = ModEntry.clients;
            var clientIds = ModEntry.clientIds;
            if (clients == null || clientIds == null)
                return false;

            var count = System.Math.Min(clients.Length, clientIds.Length);
            for (var i = 0; i < count; i++)
            {
                if (clientIds[i] <= 0)
                    continue;

                var client = clients[i];
                if (client == null)
                    continue;

                // IsHardInvalidPlayerTargetEntity already folds together destroyed/dead/untargetable,
                // the downed state and the "is this entity even on our level" identity check.
                try
                {
                    if (IsHardInvalidPlayerTargetEntity(client))
                        continue;
                }
                catch
                {
                    continue;
                }

                destination.Add(client);
            }

            return destination.Count > 0;
        }

        private static bool IsMobNearAnyRemotePlayer(Mob mob, List<Entity> remotePlayers)
        {
            for (var i = 0; i < remotePlayers.Count; i++)
            {
                if (IsWithinRangeBox(mob, remotePlayers[i], HostRemoteActivationRangeXPx, HostRemoteActivationRangeYPx))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Runs once per host frame from the mob-sync frame loop, before the dirty queue is flushed
        /// so anything activated here streams out on the same frame.
        /// </summary>
        private static void RunHostRemotePlayerActivationPass(NetNode net)
        {
            if (!IsHost(net) || IsSyncQuiescedForTransition())
                return;
            if (!IsIncomingMobIdentityReady())
                return;
            if (!TryCollectLivingRemotePlayers(s_hostRemoteActivationPlayers))
                return;

            lock (Sync)
            {
                var total = trackedMobs.Count;
                if (total <= 0)
                {
                    s_hostRemoteActivationCursor = 0;
                    s_hostRemoteActivationMobs.Clear();
                    return;
                }

                if (s_hostRemoteActivationCursor >= total)
                    s_hostRemoteActivationCursor = 0;

                s_hostRemoteActivationMobs.Clear();
                var budget = System.Math.Min(HostRemoteActivationScanBudgetPerFrame, total);
                for (var scanned = 0; scanned < budget; scanned++)
                {
                    var mob = trackedMobs[s_hostRemoteActivationCursor];
                    s_hostRemoteActivationCursor++;
                    if (s_hostRemoteActivationCursor >= total)
                        s_hostRemoteActivationCursor = 0;

                    if (mob != null)
                        s_hostRemoteActivationMobs.Add(mob);
                }
            }

            // Target selection reads game state and takes Sync internally; never call it under lock.
            var activated = 0;
            for (var i = 0; i < s_hostRemoteActivationMobs.Count; i++)
                activated += TryActivateHostMobForRemotePlayer(s_hostRemoteActivationMobs[i]) ? 1 : 0;

            s_hostRemoteActivationMobs.Clear();

            if (activated <= 0)
                return;

            var now = Environment.TickCount64;
            if (now - s_hostRemoteActivationLastLogMs < HostRemoteActivationLogIntervalMs)
                return;

            s_hostRemoteActivationLastLogMs = now;
            Log.Information(
                "[MobAI] host activated mobs near remote player count={Count} remotePlayers={Players}",
                activated,
                s_hostRemoteActivationPlayers.Count);
        }

        /// <summary>
        /// Un-culls a single mob that the host would otherwise not simulate and lets the shared
        /// target-selection path consider both players for it. Returns true when the mob actually
        /// needed waking, so the caller can report a meaningful rate rather than a per-frame count.
        /// </summary>
        private static bool TryActivateHostMobForRemotePlayer(Mob mob)
        {
            if (mob == null || !IsSyncMob(mob) || !IsMobHostileToPlayers(mob))
                return false;

            bool wasDormant;
            try
            {
                if (mob.destroyed || mob.life <= 0)
                    return false;

                wasDormant = mob.isOutOfGame || !mob.isOnScreen;
            }
            catch
            {
                return false;
            }

            if (!IsMobNearAnyRemotePlayer(mob, s_hostRemoteActivationPlayers))
                return false;

            if (wasDormant)
            {
                // Bring the mob back into vanilla simulation so its own behaviour tree runs. The
                // host owns AI either way - this only restores the simulation that proximity culling
                // took away because the measurement was made against the host's character.
                PromoteMobToSyncVisibleState(mob);

                // A mob that was dormant has streamed nothing for a while; make sure the replica
                // receives a full authoritative state rather than waiting for the next keyframe.
                QueueHostMobDirty(mob, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
            }

            // Shared path, same guards as every other enemy: it no-ops when the mob already holds a
            // relevant target, has a queued/charging skill, or has its AI locked by a script.
            TryAssignHostAttackTarget(mob);

            return wasDormant;
        }
    }
}

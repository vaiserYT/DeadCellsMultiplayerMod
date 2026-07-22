using System;
using System.Collections.Generic;
using System.Threading;
using dc;
using dc.en;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private static int s_playerCombatStateRevision;
        private static int s_lastProcessedPlayerCombatStateRevision;
        private static string s_lastPlayerCombatStateReason = string.Empty;
        private static readonly List<Mob> s_playerCombatRepairMobsScratch = new();
        private const double HostDownedRetargetMaxDistancePx = 24.0 * 45.0;
        private const double HostBossSurvivorRetargetMaxDistancePx = 24.0 * 96.0;

        /// <summary>
        /// Called only when a player actually enters/leaves the downed state. It deliberately does
        /// not run for the periodic down-state heartbeat, so a long revive wait cannot repeatedly
        /// disturb legitimate elite/boss phases.
        /// </summary>
        internal static void NotifyPlayerCombatStateChanged(string reason)
        {
            // Do not sweep, wake, unlock or rewrite every enemy when a player goes down/revives.
            // Vanilla and the conservative post-update aTarget repair handle reacquisition safely.
            lock (Sync)
            {
                s_lastPlayerCombatStateReason = reason ?? string.Empty;
                unchecked
                {
                    s_playerCombatStateRevision++;
                }
                clientCachedAttackTargetByMob.Clear();
                hostLastSentContactTargetUserIdByMob.Clear();
            }
        }

        private static void ResetPlayerCombatStateRepairLocked()
        {
            s_playerCombatRepairMobsScratch.Clear();
            s_lastProcessedPlayerCombatStateRevision = Volatile.Read(ref s_playerCombatStateRevision);
            s_lastPlayerCombatStateReason = string.Empty;
        }

        private static void RunHostPlayerCombatStateRepairIfPending()
        {
            if (!IsHost(GameMenu.NetRef))
                return;

            var revision = Volatile.Read(ref s_playerCombatStateRevision);
            if (revision == Volatile.Read(ref s_lastProcessedPlayerCombatStateRevision))
                return;

            string reason;
            lock (Sync)
            {
                revision = Volatile.Read(ref s_playerCombatStateRevision);
                if (revision == s_lastProcessedPlayerCombatStateRevision)
                    return;

                s_lastProcessedPlayerCombatStateRevision = revision;
                reason = s_lastPlayerCombatStateReason;
                s_playerCombatRepairMobsScratch.Clear();
                for (var i = 0; i < trackedMobs.Count; i++)
                {
                    var mob = trackedMobs[i];
                    if (mob != null)
                        s_playerCombatRepairMobsScratch.Add(mob);
                }
            }

            var repaired = 0;
            for (var i = 0; i < s_playerCombatRepairMobsScratch.Count; i++)
            {
                if (TryRepairHostMobAfterPlayerCombatStateChange(s_playerCombatRepairMobsScratch[i]))
                    repaired++;
            }
            s_playerCombatRepairMobsScratch.Clear();

            if (repaired > 0)
            {
                Log.Information(
                    "[MobSync] repaired host mob targets after player state change reason={Reason} count={Count}",
                    reason,
                    repaired);
            }
        }

        private static bool TryRepairHostMobAfterPlayerCombatStateChange(Mob mob)
        {
            if (mob == null || !IsSyncMob(mob) || !IsMobHostileToPlayers(mob))
                return false;

            try
            {
                if (mob.destroyed || mob.life <= 0 || (mob.isOutOfGame && !mob.isOnScreen))
                    return false;
            }
            catch
            {
                return false;
            }

            var cleared = TryClearHostMobInvalidPlayerTargets(mob);
            if (TryGetCurrentHostAttackTarget(mob, out var current))
            {
                ResetHostMobStallRecoveryMotion(mob);
                return cleared;
            }

            Entity? selected = null;
            if (TryGetCurrentHostNemesisTarget(mob, out var nemesis))
                selected = nemesis;
            else if (TryResolveDetectedHostCombatTarget(mob, out var detected))
                selected = detected;
            else
                selected = ResolveClosestLivingHostCombatTarget(mob, HostDownedRetargetMaxDistancePx);

            if (selected == null)
                return cleared;

            var allowUnlock = !BossSyncHelpers.IsBossMob(mob) &&
                              (cleared || ModEntry.HasAnyPlayerDownedForCombat());
            return TrySetHostCombatTargetExact(mob, selected, allowUnlockStaleAi: allowUnlock);
        }

        /// <summary>
        /// A boss can keep a scripted AI lock after its current player becomes downed.  Native
        /// boss code may then clear the one-shot repaired aTarget again while retaining the stale
        /// phase lock.  While (and only while) a teammate is down, keep the surviving player as
        /// the immediate attack target before vanilla updates.  We intentionally do not rewrite
        /// boss threat/nemesis containers and never unlock or cancel a real boss skill/phase.
        /// </summary>
        private static void TryMaintainHostBossSurvivorTarget(Mob mob)
        {
            if (mob == null || !IsHost(GameMenu.NetRef) || !BossSyncHelpers.IsBossMob(mob) ||
                !ModEntry.HasAnyPlayerDownedForCombat() || !IsMobHostileToPlayers(mob))
            {
                return;
            }

            try
            {
                if (mob.destroyed || mob.life <= 0 || (mob.isOutOfGame && !mob.isOnScreen))
                    return;
            }
            catch
            {
                return;
            }

            TryClearHostMobInvalidPlayerTargets(mob);
            if (TryGetCurrentHostAttackTarget(mob, out _))
                return;
            if (HasLocalQueuedOrChargingSkill(mob))
                return;

            Entity? survivor = null;
            if (TryResolveDetectedHostCombatTarget(mob, out var detected))
                survivor = detected;
            else
                survivor = ResolveClosestLivingHostCombatTarget(mob, HostBossSurvivorRetargetMaxDistancePx);

            if (survivor == null)
                return;

            try
            {
                mob.setAttackTarget(survivor);
                TryWakeMobForForcedSimulation(mob);
            }
            catch
            {
            }
        }

        private static Entity? ResolveClosestLivingHostCombatTarget(Mob mob, double maxDistancePx)
        {
            if (mob == null)
                return null;

            var mx = GetWorldX(mob);
            var my = GetWorldY(mob);
            var maxDistSq = maxDistancePx * maxDistancePx;
            var bestDistSq = maxDistSq;
            Entity? best = null;

            void Consider(Entity? candidate)
            {
                if (candidate == null || !IsAcquirablePlayerCombatTargetForMob(mob, candidate, requireDetectArea: false))
                    return;

                try
                {
                    if (!IsEntityOnCurrentCombatIdentity(candidate))
                        return;
                }
                catch
                {
                    return;
                }

                var dx = GetWorldX(candidate) - mx;
                var dy = GetWorldY(candidate) - my;
                var distSq = dx * dx + dy * dy;
                if (!double.IsFinite(distSq) || distSq > bestDistSq)
                    return;

                bestDistSq = distSq;
                best = candidate;
            }

            Consider(ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance);
            for (var i = 0; i < ModEntry.clients.Length; i++)
            {
                if (ModEntry.clientIds[i] > 0)
                    Consider(ModEntry.clients[i]);
            }

            return best;
        }


        private static bool TrySetHostCombatTargetExact(Mob mob, Entity target, bool allowUnlockStaleAi)
        {
            if (mob == null || target == null || !IsPreservablePlayerCombatTargetForMob(mob, target))
                return false;

            var changed = false;
            try
            {
                if (!ReferenceEquals(mob.aTarget, target))
                {
                    mob.setAttackTarget(target);
                    changed = true;
                }
            }
            catch { }

            try
            {
                if (!ReferenceEquals(mob.nemesisTarget, target))
                {
                    TrySetNemesisTargetExact(mob, target);
                    changed = true;
                }
            }
            catch { }

            if (allowUnlockStaleAi && !HasLocalQueuedOrChargingSkill(mob))
            {
                try
                {
                    if (mob.aiLocked())
                    {
                        mob.unlockAi();
                        changed = true;
                    }
                }
                catch { }
            }

            // Only force visibility when an actual repair was made. Re-waking every tracked mob on
            // every down/revive transition can pull sleeping enemies into simulation prematurely.
            if (changed)
                TryWakeMobForForcedSimulation(mob);
            ResetHostMobStallRecoveryMotion(mob);
            return changed;
        }
    }
}

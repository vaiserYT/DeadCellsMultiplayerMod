using System;
using System.Collections.Generic;
using dc.en;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private sealed class HostMobStallWatch
        {
            public double LastX;
            public double LastY;
            public double LastMotionFrame;
            public double LastSoftRepairFrame;
            public double LastHardRepairFrame;
        }

        private static readonly Dictionary<int, HostMobStallWatch> s_hostMobStallWatchBySyncId = new();
        private const double HostMobSoftStallFrames = 30.0 * 3.0;
        private const double HostMobHardStallFrames = 30.0 * 6.0;
        private const double HostMobRepairCooldownFrames = 30.0 * 3.0;
        private const double HostMobMotionDistancePx = 2.0;
        private const double HostMobMotionVelocityThreshold = 0.04;

        private static void ClearHostMobStallRecoveryLocked()
        {
            s_hostMobStallWatchBySyncId.Clear();
        }

        private static void RemoveHostMobStallRecoveryLocked(int syncId)
        {
            if (syncId >= 0)
                s_hostMobStallWatchBySyncId.Remove(syncId);
        }

        private static void ResetHostMobStallRecoveryMotion(Mob mob)
        {
            if (mob == null || !TryGetMobSyncId(mob, out var syncId) || syncId < 0)
                return;

            var frame = GetCurrentFrame(mob);
            var x = GetWorldX(mob);
            var y = GetWorldY(mob);
            lock (Sync)
            {
                if (!s_hostMobStallWatchBySyncId.TryGetValue(syncId, out var watch))
                {
                    watch = new HostMobStallWatch
                    {
                        LastSoftRepairFrame = -99999.0,
                        LastHardRepairFrame = -99999.0
                    };
                    s_hostMobStallWatchBySyncId[syncId] = watch;
                }

                watch.LastX = x;
                watch.LastY = y;
                watch.LastMotionFrame = frame;
            }
        }

        /// <summary>
        /// Conservative host-only watchdog for mobs whose AI target/lock survives an elite phase,
        /// teleport or stun after the action itself has ended. It never runs for bosses, dead mobs,
        /// sleeping/off-screen mobs or mobs with no nearby living player target.
        /// </summary>
        private static void TryRecoverHostStalledMob(Mob mob)
        {
            if (mob == null || !IsHost(LobbySession.NetRef) || !IsSyncMob(mob))
                return;
            if (BossSyncHelpers.IsBossMob(mob) || !IsMobHostileToPlayers(mob))
                return;

            try
            {
                if (mob.destroyed || mob.life <= 0 || (mob.isOutOfGame && !mob.isOnScreen))
                    return;
            }
            catch
            {
                return;
            }

            if (!TryGetMobSyncId(mob, out var syncId) || syncId < 0)
                return;

            var frame = GetCurrentFrame(mob);
            if (frame <= 0.0)
                return;

            double x;
            double y;
            double motion;
            try
            {
                x = GetWorldX(mob);
                y = GetWorldY(mob);
                motion = Math.Abs(mob.dx) + Math.Abs(mob.bdx) + Math.Abs(mob.dy) + Math.Abs(mob.bdy);
            }
            catch
            {
                return;
            }

            HostMobStallWatch watch;
            lock (Sync)
            {
                if (!s_hostMobStallWatchBySyncId.TryGetValue(syncId, out var existingWatch))
                {
                    watch = new HostMobStallWatch
                    {
                        LastX = x,
                        LastY = y,
                        LastMotionFrame = frame,
                        LastSoftRepairFrame = -99999.0,
                        LastHardRepairFrame = -99999.0
                    };
                    s_hostMobStallWatchBySyncId[syncId] = watch;
                    return;
                }

                watch = existingWatch;
            }

            var dx = x - watch.LastX;
            var dy = y - watch.LastY;
            watch.LastX = x;
            watch.LastY = y;
            if (dx * dx + dy * dy >= HostMobMotionDistancePx * HostMobMotionDistancePx ||
                motion >= HostMobMotionVelocityThreshold)
            {
                watch.LastMotionFrame = frame;
                return;
            }

            // Do not wake or unlock an enemy merely because all players are outside its detection
            // area. A real living target must be available on this host.
            if (!TryResolveDetectedHostCombatTarget(mob, out var target) || target == null)
            {
                if (!TryGetCurrentHostAttackTarget(mob, out target) &&
                    !TryGetCurrentHostNemesisTarget(mob, out target))
                {
                    watch.LastMotionFrame = frame;
                    return;
                }
            }

            var stalledFrames = frame - watch.LastMotionFrame;
            var anyPlayerDowned = ModEntry.HasAnyPlayerDownedForCombat();
            var softStallFrames = anyPlayerDowned ? 30.0 * 1.5 : HostMobSoftStallFrames;
            var hardStallFrames = anyPlayerDowned ? 30.0 * 3.0 : HostMobHardStallFrames;
            if (stalledFrames < softStallFrames)
                return;

            if (frame - watch.LastSoftRepairFrame >= HostMobRepairCooldownFrames)
            {
                watch.LastSoftRepairFrame = frame;
                try
                {
                    if (!ReferenceEquals(mob.aTarget, target))
                        mob.setAttackTarget(target);
                }
                catch { }
                TrySetNemesisTargetExact(mob, target);
            }

            if (stalledFrames < hardStallFrames ||
                frame - watch.LastHardRepairFrame < HostMobRepairCooldownFrames)
            {
                return;
            }

            // Never break a real elite attack/teleport/phase merely because it is stationary.
            // Wait for the queued/charging skill to finish and only recover a stale lock afterward.
            if (HasLocalQueuedOrChargingSkill(mob))
            {
                watch.LastMotionFrame = frame;
                return;
            }

            watch.LastHardRepairFrame = frame;
            var wasAiLocked = false;
            try { wasAiLocked = mob.aiLocked(); } catch { }

            try { mob.unlockAi(); } catch { }
            TryWakeMobForForcedSimulation(mob);
            try
            {
                if (!ReferenceEquals(mob.aTarget, target))
                    mob.setAttackTarget(target);
            }
            catch { }
            TrySetNemesisTargetExact(mob, target);
            watch.LastMotionFrame = frame;

            Log.Warning(
                "[MobSync] repaired stalled host mob syncId={SyncId} type={Type} aiLocked={AiLocked}",
                syncId,
                GetMobRuntimeClassKeySafe(mob),
                wasAiLocked);
        }
    }
}

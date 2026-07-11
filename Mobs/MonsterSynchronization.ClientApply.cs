using System;
using System.Diagnostics;
using dc;
using dc.en;
using dc.libs.heaps.slib._AnimManager;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private static void ApplyInterpolatedState(Mob self)
        {
            ClientMobState target;
            lock (Sync)
            {
                if (!clientMobTargets.TryGetValue(self, out target))
                    return;
            }

            // Life/death must sync even when the mob is far/off-screen (visual interpolation stays gated below).
            ApplyAuthoritativeLifeState(self, target.Life, target.MaxLife);

            if (!ShouldProcessClientVisualState(self))
                return;

            var preserveLocalMotion = HasLocalQueuedOrChargingSkill(self) || IsClientNetworkAttackActive(self);

            if (!preserveLocalMotion)
            {
                // Equivalent to: vertical setting OR flying mob (!hasGravity). Flyers always follow host Y.
                bool syncY;
                try
                {
                    syncY = !self.hasGravity || IsClientVerticalSyncEnabled();
                }
                catch
                {
                    syncY = IsClientVerticalSyncEnabled();
                }

                var currentX = GetWorldX(self);
                var currentY = GetWorldY(self);
                var interpolationAlpha = GetClientInterpolationAlpha();

                // Dead reckoning: extrapolate from last authoritative state using host-reported velocity.
                // This allows mobs to move smoothly between reduced-rate snapshots instead of lagging behind.
                double predictedX, predictedY;
                if (target.Time > 0.0 && (System.Math.Abs(target.Dx) > 0.001 || System.Math.Abs(target.Dy) > 0.001))
                {
                    var elapsed = GetCurrentFrame(self) - target.Time;
                    if (elapsed > 0.0 && elapsed < 60.0)
                    {
                        predictedX = target.X + target.Dx * elapsed;
                        predictedY = target.Y + target.Dy * elapsed;
                    }
                    else
                    {
                        predictedX = target.X;
                        predictedY = target.Y;
                    }
                }
                else
                {
                    predictedX = target.X;
                    predictedY = target.Y;
                }

                var lerpedX = currentX + (predictedX - currentX) * interpolationAlpha;
                var lerpedY = syncY
                    ? currentY + (predictedY - currentY) * interpolationAlpha
                    : currentY;

                try
                {
                    if (syncY)
                        self.setPosPixel(lerpedX, lerpedY);
                    else
                        SetWorldXKeepingY(self, lerpedX);
                }
                catch
                {
                    if (self.spr != null)
                    {
                        self.spr.x = lerpedX;
                        if (syncY)
                            self.spr.y = lerpedY;
                    }
                }

                try
                {
                    self.dx = 0;
                    self.bdx = 0;
                    if (syncY)
                    {
                        self.dy = 0;
                        self.bdy = 0;
                        self.fallStartY = lerpedY;
                    }
                }
                catch
                {
                }
            }

            var responsiveDir = ComputeResponsiveFacingDir(self, target);
            if (responsiveDir != 0)
                self.dir = responsiveDir;
        }

        private static bool ShouldPreserveClientAttackMotion(Mob mob)
        {
            if (mob == null)
                return false;

            if (HasLocalQueuedOrChargingSkill(mob))
                return true;

            try
            {
                var motion =
                    System.Math.Abs(mob.dx) +
                    System.Math.Abs(mob.bdx) +
                    System.Math.Abs(mob.dy) +
                    System.Math.Abs(mob.bdy);
                return motion > 0.02;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldProcessClientVisualState(Mob mob)
        {
            if (mob == null)
                return false;
            if (BossSyncHelpers.IsBossMob(mob))
                return true;
            if (HasValidLivingPlayerCombatTarget(mob))
                return true;
            if (IsClientNetworkAttackActive(mob))
                return true;

            if (TryGetMobVisibilityState(mob, out var isOnScreen, out var isOutOfGame, out var onScreenRecent))
            {
                if (isOnScreen || !isOutOfGame || onScreenRecent > 0.0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Client-side: mark the mob dead and defer the vanilla death to the mob's OWN update
        /// cycle (<see cref="TryRunPendingCulledMobDeath"/> in postUpdate). The original mod never
        /// calls onDie() from a network apply; a death half-executed outside the mob's update is
        /// corrupted state that the level-transition render trips on (Null access .groupName with
        /// every mod scene object provably removed). Deferral keeps the ghost-mob cleanup while
        /// restoring vanilla death timing. Returns true when deferred (always, for valid mobs).
        /// </summary>
        private static bool TryDeferCulledClientMobDeath(Mob mob)
        {
            if (mob == null)
                return false;
            if (IsHost(GameMenu.NetRef))
                return false;

            try { mob.life = 0; } catch { }
            lock (Sync)
            {
                s_pendingCulledMobDeaths.Add(mob);
            }
            return true;
        }

        /// <summary>
        /// Called from Hook_Mob_postUpdate (client branch). A mob reaching postUpdate is being
        /// simulated by vanilla, so its state is initialized and the deferred death is now safe.
        /// Returns true when this mob's deferred death was executed this frame.
        /// </summary>
        private static bool TryRunPendingCulledMobDeath(Mob mob)
        {
            if (mob == null)
                return false;

            lock (Sync)
            {
                if (s_pendingCulledMobDeaths.Count == 0 || !s_pendingCulledMobDeaths.Contains(mob))
                    return false;
            }

            // Reaching postUpdate is not proof of initialization if vanilla also ticks culled
            // mobs; require the mob to actually be awake before running the vanilla death.
            if (IsMobCulledLocally(mob))
                return false;

            lock (Sync)
            {
                s_pendingCulledMobDeaths.Remove(mob);
            }

            try
            {
                if (mob.destroyed)
                    return true;

                RunWithSuppressedMobDieSend(() =>
                {
                    mob.life = 0;
                    mob.onDie();
                });

                var animManager = GetMobAnimManager(mob);
                if (animManager?.stack != null)
                {
                    while (animManager.stack.length > 0)
                        animManager.stack.pop();
                }
            }
            catch
            {
                try
                {
                    mob.isOutOfGame = true;
                    mob.isOnScreen = false;
                }
                catch
                {
                }
            }

            return true;
        }

        private static void ApplyAuthoritativeLifeState(Mob mob, int targetLife, int targetMaxLife)
        {
            if (mob == null)
                return;

            if (targetMaxLife > 0 && mob.maxLife != targetMaxLife)
                mob.maxLife = targetMaxLife;

            var clampedLife = targetLife;
            if (mob.maxLife > 0)
                clampedLife = System.Math.Clamp(clampedLife, 0, mob.maxLife);
            else if (clampedLife < 0)
                clampedLife = 0;

            if (mob.life == clampedLife)
            {
                // Host may report life=0 after the local client already lost the HP bar but never ran
                // the death/despawn branch. Force that branch once for non-boss mobs so rune elites and
                // normal mobs do not stay as invisible/unkillable ghosts.
                if (clampedLife <= 0 && !BossSyncHelpers.IsBossMob(mob))
                    ForceNonBossAuthoritativeDeath(mob);
                return;
            }

            var wasAlive = mob.life > 0;
            mob.life = clampedLife;

            if (mob.life <= 0 && wasAlive)
            {
                if (BossSyncHelpers.IsBossMob(mob))
                    return;

                if (TryDeferCulledClientMobDeath(mob))
                    return;

                try
                {
                    if (!mob.destroyed)
                    {
                        RunWithSuppressedMobDieSend(() =>
                        {
                            mob.life = 0;
                            mob.onDie();
                        });
                    }

                    var animManager = GetMobAnimManager(mob);
                    if (animManager?.stack != null)
                    {
                        while (animManager.stack.length > 0)
                            animManager.stack.pop();
                    }
                }
                catch
                {
                }
            }
        }


        private static void ForceNonBossAuthoritativeDeath(Mob mob)
        {
            if (mob == null)
                return;

            try
            {
                if (mob.destroyed)
                    return;
            }
            catch
            {
            }

            if (TryDeferCulledClientMobDeath(mob))
                return;

            try
            {
                TryWakeMobForForcedSimulation(mob);
                RunWithSuppressedMobDieSend(() =>
                {
                    mob.life = 0;
                    mob.onDie();
                });
            }
            catch
            {
                try
                {
                    mob.isOutOfGame = true;
                    mob.isOnScreen = false;
                }
                catch
                {
                }
            }
        }

        private static void ApplyClientAnimationStateBeforeUpdate(Mob self)
        {
            ClientMobState target;
            var shouldApplyAnimThisFrame = true;
            lock (Sync)
            {
                if (!clientMobTargets.TryGetValue(self, out target))
                    return;

                shouldApplyAnimThisFrame = ShouldApplyClientAnimationForFrameLocked(self);
            }

            if (!ShouldProcessClientVisualState(self))
                return;

            var responsiveDir = ComputeResponsiveFacingDir(self, target);
            if (responsiveDir != 0)
                self.dir = responsiveDir;

            if (HasLocalQueuedOrChargingSkill(self) || IsClientNetworkAttackActive(self))
                return;

            if (!shouldApplyAnimThisFrame)
                return;

            ApplyAnimPayload(self, target.AnimPayload);
        }

        private static bool ShouldApplyClientAnimationForFrameLocked(Mob mob)
        {
            if (mob == null)
                return true;

            try
            {
                var level = mob._level ?? currentLevel;
                if (level == null)
                    return true;

                var frame = level.ftime;
                if (clientLastAnimationApplyFrameByMob.TryGetValue(mob, out var lastFrame) &&
                    lastFrame == frame)
                {
                    return false;
                }

                clientLastAnimationApplyFrameByMob[mob] = frame;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void ApplyAnimPayload(Mob mob, string? payload)
        {
            if (mob == null || mob.life <= 0 || mob.destroyed)
                return;

            var safePayload = payload ?? string.Empty;
            lock (Sync)
            {
                if (clientLastAppliedAnimPayloadByMob.TryGetValue(mob, out var lastApplied) &&
                    string.Equals(lastApplied, safePayload, StringComparison.Ordinal))
                {
                    return;
                }

                clientLastAppliedAnimPayloadByMob[mob] = safePayload;
            }

            if (!TryGetParsedAnimPayloadCached(safePayload, out var parsed))
                return;

            var spr = mob.spr;
            if (spr == null)
                return;

            var animManager = GetMobAnimManager(mob);
            if (animManager == null)
                return;

            try
            {
                var top = GetTopAnimInstance(animManager);
                var currentGroup = top?.group?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(currentGroup))
                    currentGroup = spr.groupName?.ToString() ?? string.Empty;

                if (!string.Equals(currentGroup, parsed.Group, StringComparison.Ordinal))
                {
                    animManager.play(parsed.Group.AsHaxeString(), null, null).loop(null);
                    top = GetTopAnimInstance(animManager);
                }

                if (top != null)
                {
                    if (top.reverse != parsed.Reverse)
                        top.reverse = parsed.Reverse;
                    if (System.Math.Abs(top.speed - parsed.Speed) > ClientAnimSpeedEpsilon)
                        top.speed = parsed.Speed;
                }
            }
            catch
            {
            }
        }
    }
}

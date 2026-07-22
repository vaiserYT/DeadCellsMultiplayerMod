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

            if (BossSyncHelpers.IsBossMob(self))
            {
                ApplyAuthoritativeBossTransform(self, target);
                return;
            }

            var forcePositionSnap = target.ForcePositionSnap;
            var forceVerticalPositionSnap = target.ForceVerticalPositionSnap;

            bool hasGravity;
            try
            {
                hasGravity = self.hasGravity;
            }
            catch
            {
                hasGravity = true;
            }

            var networkAttackActive = IsClientNetworkAttackActive(self);
            var jumpLikeMotion =
                hasGravity &&
                (IsJumpLikeAnimPayload(target.AnimPayload) ||
                 (networkAttackActive &&
                  double.IsFinite(target.Dy) &&
                  System.Math.Abs(target.Dy) > ClientJumpVelocityEpsilon));
            var preserveLocalMotion = HasLocalQueuedOrChargingSkill(self) || networkAttackActive;

            // Network attacks normally keep their local vanilla motion. Jump/leap attacks are the one
            // exception: they still need bounded host phase alignment or the client can land on a
            // different platform and remain permanently offset.
            if (!preserveLocalMotion || forcePositionSnap || jumpLikeMotion)
            {
                var currentX = GetWorldX(self);
                var currentY = GetWorldY(self);
                var interpolationAlpha = GetClientInterpolationAlpha();

                // Predict from the LOCAL receive frame, not by subtracting host/client ftime values.
                double predictedX = target.X;
                double predictedY = target.Y;
                var velocityXPx = ToPredictionPixelsPerFrame(target.Dx);
                var velocityYPx = ToPredictionPixelsPerFrame(target.Dy);
                var receiveFrame = target.ReceivedFrame > 0.0 ? target.ReceivedFrame : GetCurrentFrame(self);
                var elapsed = GetCurrentFrame(self) - receiveFrame;
                if (!forcePositionSnap &&
                    elapsed > 0.0 &&
                    (System.Math.Abs(velocityXPx) > 0.001 || System.Math.Abs(velocityYPx) > 0.001))
                {
                    var maxPrediction = hasGravity
                        ? ClientGroundedMaxPredictionFrames
                        : ClientMaxPredictionFrames;
                    elapsed = System.Math.Min(elapsed, maxPrediction);
                    predictedX = target.X + velocityXPx * elapsed;

                    // Flyers always predict Y. Gravity mobs predict Y only during a real jump/leap;
                    // ordinary grounded movement remains controlled by local vanilla collision.
                    if (!hasGravity || jumpLikeMotion)
                        predictedY = target.Y + velocityYPx * elapsed;
                }

                var deltaX = predictedX - currentX;
                var deltaY = predictedY - currentY;
                var distSq = deltaX * deltaX + deltaY * deltaY;
                var hardSnapX = forcePositionSnap ||
                    (!hasGravity &&
                     distSq >= ClientAuthoritativeHardSnapDistancePx * ClientAuthoritativeHardSnapDistancePx);

                var recoverGroundedMobFromBelowFloor =
                    hasGravity &&
                    !forceVerticalPositionSnap &&
                    !IsClientVerticalSyncEnabled() &&
                    ShouldRecoverGroundedMobFromBelowHost(
                        target,
                        currentX,
                        currentY,
                        predictedX,
                        predictedY,
                        velocityYPx);

                // Gravity mobs are never placed directly on the host's floor coordinate. Teleports
                // and fall-through recovery place them a little above it, then vanilla gravity/collision
                // performs the final landing. This avoids embedding the client mob in solid tiles.
                var safeGroundedLanding =
                    hasGravity &&
                    (forceVerticalPositionSnap || recoverGroundedMobFromBelowFloor);

                var syncY = !hasGravity ||
                            IsClientVerticalSyncEnabled() ||
                            safeGroundedLanding;
                var hardSnapY = (!hasGravity && hardSnapX) || safeGroundedLanding;

                double lerpedX;
                if (hasGravity && forcePositionSnap)
                {
                    // Teleports are discontinuous in X. Y uses the safe landing path below.
                    lerpedX = predictedX;
                }
                else if (hasGravity)
                {
                    var desiredStep = 0.0;
                    var absDeltaXForGate = System.Math.Abs(deltaX);
                    var canCorrectHorizontally =
                        System.Math.Abs(deltaY) <= ClientGroundedMaxVerticalMismatchPx ||
                        jumpLikeMotion ||
                        // Escape hatch: a grounded mob whose vertical mismatch normally blocks
                        // horizontal correction would otherwise FREEZE in X forever when it lands
                        // on a differently-elevated spot (high-BC blink onto another platform).
                        // Once the horizontal error is large, allow X convergence regardless of
                        // the vertical gap — the safe-landing/vertical paths handle Y separately.
                        absDeltaXForGate >= ClientGroundedMaxDirectCorrectionDistancePx;

                    if (canCorrectHorizontally)
                    {
                        var absDeltaX = System.Math.Abs(deltaX);
                        if (absDeltaX <= ClientGroundedMaxDirectCorrectionDistancePx)
                        {
                            var groundedAlphaCeiling = System.Math.Max(0.35, interpolationAlpha);
                            var distanceAlpha = System.Math.Min(
                                0.35 + absDeltaX / (PixelsPerCase * 8.0),
                                groundedAlphaCeiling);
                            var maxStep = System.Math.Clamp(
                                ClientGroundedBaseCorrectionPxPerFrame + System.Math.Abs(velocityXPx) * 0.35,
                                ClientGroundedBaseCorrectionPxPerFrame,
                                jumpLikeMotion
                                    ? ClientGroundedLargeDriftCorrectionPxPerFrame
                                    : ClientGroundedMaxCorrectionPxPerFrame);
                            desiredStep = absDeltaX < 0.20
                                ? 0.0
                                : System.Math.Clamp(deltaX * distanceAlpha, -maxStep, maxStep);
                        }
                        else
                        {
                            var maxStep = absDeltaX >= ClientGroundedEmergencyDriftDistancePx
                                ? ClientGroundedEmergencyDriftCorrectionPxPerFrame
                                : ClientGroundedLargeDriftCorrectionPxPerFrame;
                            desiredStep = System.Math.Clamp(deltaX, -maxStep, maxStep);
                        }
                    }

                    lerpedX = currentX + desiredStep;
                }
                else
                {
                    lerpedX = hardSnapX
                        ? predictedX
                        : currentX + deltaX * interpolationAlpha;
                }

                var lerpedY = currentY;
                if (syncY)
                {
                    lerpedY = safeGroundedLanding
                        ? predictedY - ClientGroundedSafeLandingLiftPx
                        : (hardSnapY ? predictedY : currentY + deltaY * interpolationAlpha);
                }

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

                if (jumpLikeMotion)
                {
                    TryAlignClientJumpMotion(self, target, predictedY);
                }
                else
                {
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
            }

            if (forcePositionSnap || forceVerticalPositionSnap)
                ClearClientForcePositionSnapAfterApply(self, target);

            var responsiveDir = ComputeResponsiveFacingDir(self, target);
            if (responsiveDir != 0)
                self.dir = responsiveDir;
        }

        private static void ApplyAuthoritativeBossTransform(Mob boss, ClientMobState target)
        {
            if (boss == null)
                return;

            var hasGravity = true;
            try { hasGravity = boss.hasGravity; } catch { }
            var networkAttackActive = IsClientNetworkAttackActive(boss);
            var jumpLikeMotion =
                hasGravity &&
                (IsJumpLikeAnimPayload(target.AnimPayload) ||
                 (networkAttackActive &&
                  double.IsFinite(target.Dy) &&
                  System.Math.Abs(target.Dy) > ClientJumpVelocityEpsilon));
            var safeGroundedLanding =
                hasGravity &&
                (target.ForcePositionSnap || target.ForceVerticalPositionSnap);
            var syncVerticalPosition = !hasGravity || jumpLikeMotion || safeGroundedLanding;

            var currentX = GetWorldX(boss);
            var currentY = GetWorldY(boss);
            var predictedX = target.X;
            var predictedY = target.Y;
            var velocityXPx = ToPredictionPixelsPerFrame(target.Dx);
            var velocityYPx = ToPredictionPixelsPerFrame(target.Dy);
            var receiveFrame = target.ReceivedFrame > 0.0 ? target.ReceivedFrame : GetCurrentFrame(boss);
            var elapsed = System.Math.Clamp(
                GetCurrentFrame(boss) - receiveFrame,
                0.0,
                ClientBossMaxPredictionFrames);

            if (!target.ForcePositionSnap && elapsed > 0.0)
            {
                predictedX += velocityXPx * elapsed;
                if (syncVerticalPosition)
                    predictedY += velocityYPx * elapsed;
            }

            // The boss path intentionally keeps ordinary grounded Y movement under vanilla
            // collision. Unlike regular mobs, however, it previously had no recovery when that
            // local replica lost floor contact. Concierge could therefore continue falling forever
            // while the host remained safely landed. Recover upward only after the host is stable;
            // never push a boss down or interrupt a real host jump/fall.
            var recoverGroundedBossFromBelowFloor =
                hasGravity &&
                !jumpLikeMotion &&
                !target.ForceVerticalPositionSnap &&
                ShouldRecoverGroundedMobFromBelowHost(
                    target,
                    currentX,
                    currentY,
                    predictedX,
                    predictedY,
                    velocityYPx);
            safeGroundedLanding =
                hasGravity &&
                (target.ForcePositionSnap ||
                 target.ForceVerticalPositionSnap ||
                 recoverGroundedBossFromBelowFloor);
            syncVerticalPosition = !hasGravity || jumpLikeMotion || safeGroundedLanding;

            var deltaX = predictedX - currentX;
            var deltaY = predictedY - currentY;
            var hardSnap = target.ForcePositionSnap ||
                           target.ForceVerticalPositionSnap ||
                           deltaX * deltaX + (syncVerticalPosition ? deltaY * deltaY : 0.0) >=
                           ClientBossHardSnapDistancePx * ClientBossHardSnapDistancePx;
            var alpha = System.Math.Max(ClientBossMinimumInterpolationAlpha, GetClientInterpolationAlpha());
            var nextX = hardSnap ? predictedX : currentX + deltaX * alpha;
            var nextY = currentY;
            if (safeGroundedLanding)
                nextY = predictedY - ClientGroundedSafeLandingLiftPx;
            else if (syncVerticalPosition)
                nextY = hardSnap ? predictedY : currentY + deltaY * alpha;

            try
            {
                if (syncVerticalPosition)
                    boss.setPosPixel(nextX, nextY);
                else
                    SetWorldXKeepingY(boss, nextX);
            }
            catch
            {
                try
                {
                    if (boss.spr != null)
                    {
                        boss.spr.x = nextX;
                        if (syncVerticalPosition)
                            boss.spr.y = nextY;
                    }
                }
                catch
                {
                }
            }

            // While a host-selected native attack is active, keep the host velocity phase on the
            // replica. Boss scripts such as Hand of the King's leaps use velocity/collision to
            // transition into landing and cleanup states. Grounded vertical physics remain local;
            // repeatedly zeroing gravity was what left walking bosses hovering above the floor.
            try
            {
                if (IsClientNetworkAttackActive(boss))
                {
                    boss.dx = double.IsFinite(target.Dx)
                        ? System.Math.Clamp(target.Dx, -ClientBossVisualVelocityMaxRawMagnitude, ClientBossVisualVelocityMaxRawMagnitude)
                        : 0.0;
                    if (syncVerticalPosition)
                    {
                        boss.dy = double.IsFinite(target.Dy)
                            ? System.Math.Clamp(target.Dy, -ClientBossVisualVelocityMaxRawMagnitude, ClientBossVisualVelocityMaxRawMagnitude)
                            : 0.0;
                        boss.bdy = 0;
                    }
                }
                else
                {
                    boss.dx = 0;
                    if (!hasGravity)
                    {
                        boss.dy = 0;
                        boss.bdy = 0;
                        boss.fallStartY = nextY;
                    }
                    else if (safeGroundedLanding)
                    {
                        // Start slightly above the authoritative floor and let vanilla collision
                        // perform the landing. Re-zeroing gravity every client frame was the cause
                        // of grounded bosses hovering instead of walking on the arena floor.
                        boss.dy = 0;
                        boss.bdy = 0;
                        boss.fallStartY = nextY;
                    }
                }

                boss.bdx = 0;
            }
            catch
            {
            }

            if (target.ForcePositionSnap || target.ForceVerticalPositionSnap)
                ClearClientForcePositionSnapAfterApply(boss, target);

            var responsiveDir = ComputeResponsiveFacingDir(boss, target);
            if (responsiveDir != 0)
                boss.dir = responsiveDir;
        }

        private static void TryAlignClientJumpMotion(
            Mob mob,
            ClientMobState target,
            double predictedHostY)
        {
            if (mob == null || !double.IsFinite(predictedHostY))
                return;

            try
            {
                var currentX = GetWorldX(mob);
                var currentY = GetWorldY(mob);
                var belowHost = currentY - predictedHostY;

                // Upward-only positional phase repair. Never move a jumping mob down through a floor.
                if (belowHost >= ClientJumpPhaseBelowHostRecoveryPx &&
                    belowHost <= PixelsPerCase * 5.0)
                {
                    var lift = System.Math.Min(
                        belowHost - ClientGroundedSafeLandingLiftPx,
                        ClientJumpPhaseUpwardCorrectionPxPerFrame);
                    if (lift > 0.0)
                        mob.setPosPixel(currentX, currentY - lift);
                }
            }
            catch
            {
                // Velocity alignment below can still repair the next jump phase.
            }

            try
            {
                var hostDx = double.IsFinite(target.Dx)
                    ? System.Math.Clamp(target.Dx, -ClientJumpVelocityMaxRawMagnitude, ClientJumpVelocityMaxRawMagnitude)
                    : 0.0;
                var hostDy = double.IsFinite(target.Dy)
                    ? System.Math.Clamp(target.Dy, -ClientJumpVelocityMaxRawMagnitude, ClientJumpVelocityMaxRawMagnitude)
                    : 0.0;

                var localDx = mob.dx + mob.bdx;
                var localDy = mob.dy + mob.bdy;

                if (System.Math.Abs(hostDx) > ClientJumpVelocityEpsilon &&
                    (System.Math.Sign(localDx) != System.Math.Sign(hostDx) ||
                     System.Math.Abs(localDx - hostDx) > 0.15))
                {
                    mob.dx = hostDx;
                    mob.bdx = 0;
                }

                if (System.Math.Abs(hostDy) > ClientJumpVelocityEpsilon &&
                    (System.Math.Sign(localDy) != System.Math.Sign(hostDy) ||
                     System.Math.Abs(localDy - hostDy) > 0.12))
                {
                    mob.dy = hostDy;
                    mob.bdy = 0;
                }
            }
            catch
            {
                // Keep the local vanilla jump if optional velocity fields are unavailable.
            }
        }

        private static void ClearClientForcePositionSnapAfterApply(Mob mob, ClientMobState applied)
        {
            lock (Sync)
            {
                if (!clientMobTargets.TryGetValue(mob, out var current) ||
                    (!current.ForcePositionSnap && !current.ForceVerticalPositionSnap) ||
                    current.ReceivedFrame != applied.ReceivedFrame)
                {
                    return;
                }

                clientMobTargets[mob] = new ClientMobState(
                    current.X,
                    current.Y,
                    current.Dir,
                    current.Life,
                    current.MaxLife,
                    current.AnimPayload,
                    current.StatePayload,
                    current.Time,
                    current.Dx,
                    current.Dy,
                    current.ReceivedFrame,
                    forcePositionSnap: false,
                    forceVerticalPositionSnap: false);
            }
        }



        private static bool ShouldRecoverGroundedMobFromBelowHost(
            ClientMobState target,
            double currentX,
            double currentY,
            double predictedX,
            double predictedY,
            double hostVelocityYPx)
        {
            if (target.Life <= 0 ||
                !double.IsFinite(currentX) || !double.IsFinite(currentY) ||
                !double.IsFinite(predictedX) || !double.IsFinite(predictedY) ||
                !double.IsFinite(hostVelocityYPx))
            {
                return false;
            }

            // Dead Cells Y grows downward. Recover only when the client mob has dropped well below
            // the host's authoritative resting position. Never use this path to push a mob downward.
            var belowHostPx = currentY - predictedY;
            if (belowHostPx < ClientGroundedBelowHostRecoveryDistancePx)
                return false;

            if (System.Math.Abs(predictedX - currentX) > ClientGroundedBelowHostRecoveryMaxHorizontalDriftPx)
                return false;

            // Wait until the host has effectively landed. This avoids fighting legitimate vanilla
            // falls, jumps, drop attacks, and knock-backs.
            return System.Math.Abs(hostVelocityYPx) <= ClientGroundedBelowHostRecoveryMaxHostVerticalSpeedPx;
        }

        private static double ToPredictionPixelsPerFrame(double velocity)
        {
            if (!double.IsFinite(velocity))
                return 0.0;

            // Dead Cells entity velocity is normally expressed in case fractions per frame. A few
            // proxy/game revisions expose already-scaled values, so avoid multiplying obvious pixel
            // velocities a second time.
            return System.Math.Abs(velocity) <= 1.5
                ? velocity * PixelsPerCase
                : velocity;
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
            {
                // A dead or dying boss belongs entirely to its native death sequence; forcing
                // host visuals onto it can hold the victory cinematic's end conditions hostage.
                bool dyingBoss;
                try { dyingBoss = mob.destroyed || mob.life <= 0; }
                catch { dyingBoss = true; }
                if (dyingBoss)
                    return false;

                // While a boss-intro cinematic runs locally, the cine script owns the boss.
                // Interpolating/snapping it to the host's live fight position (the Hand of the
                // King WALKS IN during his intro) or forcing host anims makes the cine's end
                // conditions unreachable — letterboxed camera and control lock for the whole
                // fight. Life sync is applied before this gate, so HP truth still flows during
                // the cinematic; full visual sync resumes the frame the cine ends.
                return !ModEntry.IsLocalBossIntroCineActive();
            }
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

            // Only defer truly culled/sleeping mobs. Visible or locally awake mobs can run the
            // authoritative death immediately; deferring those is what created 0-HP frozen ghosts
            // on the client after the host had already removed the enemy.
            if (!IsMobCulledLocally(mob))
                return false;

            try { mob.life = 0; } catch { }
            lock (Sync)
            {
                s_pendingCulledMobDeaths.Add(mob);
                if (!s_pendingCulledMobDeathFirstFrame.ContainsKey(mob))
                    s_pendingCulledMobDeathFirstFrame[mob] = GetCurrentFrame(mob);
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
                s_pendingCulledMobDeathFirstFrame.Remove(mob);
            }

            try
            {
                if (mob.destroyed)
                    return true;

                RunWithAuthoritativeClientMobDie(mob, () =>
                {
                    RunWithSuppressedMobDieSend(() =>
                    {
                        mob.life = 0;
                        mob.onDie();
                    });
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
                else if (clampedLife <= 0)
                    MarkClientBossAuthoritativeZeroLife(mob);
                return;
            }

            var wasAlive = mob.life > 0;
            mob.life = clampedLife;

            if (mob.life > 0 && BossSyncHelpers.IsBossMob(mob))
            {
                // Positive authoritative HP means the encounter continues (multi-phase rebuild):
                // cancel any pending zero-life force-death before granting the presentation lease.
                ClearClientBossAuthoritativeZeroLife(mob);
                MarkClientBossPresentationLease(mob);
            }

            if (mob.life <= 0 && wasAlive)
            {
                if (BossSyncHelpers.IsBossMob(mob))
                {
                    // The MOBDIE packet remains the preferred completion path; the watchdog only
                    // steps in if that packet never resolves within the grace window.
                    MarkClientBossAuthoritativeZeroLife(mob);
                    return;
                }

                if (TryDeferCulledClientMobDeath(mob))
                    return;

                try
                {
                    if (!mob.destroyed)
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
                RunWithAuthoritativeClientMobDie(mob, () =>
                {
                    RunWithSuppressedMobDieSend(() =>
                    {
                        mob.life = 0;
                        mob.onDie();
                    });
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

            // While a boss-intro cinematic is running locally, the cine script owns the boss.
            // Forcing the host's (already-fighting) animation payload onto it here kept the
            // intro from ever reaching its end conditions — letterbox and control lock held
            // forever for the second player into the arena (reported on Death). Sync resumes
            // on the first frame after the cine ends.
            if (BossSyncHelpers.IsBossMob(self) && ModEntry.IsLocalBossIntroCineActive())
                return;

            if (!ShouldProcessClientVisualState(self))
                return;

            var responsiveDir = ComputeResponsiveFacingDir(self, target);
            if (responsiveDir != 0)
                self.dir = responsiveDir;

            if (HasLocalQueuedOrChargingSkill(self))
                return;

            // Ordinary mobs keep their local queued attack animation until it completes. Bosses
            // continue accepting host animation transitions during the presentation lease so
            // multi-stage attacks and phase-specific telegraphs do not freeze on an earlier frame.
            if (IsClientNetworkAttackActive(self) && !BossSyncHelpers.IsBossMob(self))
                return;

            if (!shouldApplyAnimThisFrame)
                return;

            if (ApplyAnimPayload(self, target.AnimPayload) && BossSyncHelpers.IsBossMob(self))
                MarkClientBossSkillCallbackLease(self);
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

        private static bool ApplyAnimPayload(Mob mob, string? payload)
        {
            if (mob == null || mob.life <= 0 || mob.destroyed)
                return false;

            var safePayload = payload ?? string.Empty;
            
            // For bosses, force animation sync even if the payload is the same
            // This ensures boss transformations and phase changes are visually synced
            if (BossSyncHelpers.IsBossMob(mob) && !string.IsNullOrWhiteSpace(safePayload))
            {
                return ApplyBossAnimPayloadForce(mob, safePayload);
            }
            lock (Sync)
            {
                if (clientLastAppliedAnimPayloadByMob.TryGetValue(mob, out var lastApplied) &&
                    string.Equals(lastApplied, safePayload, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!TryGetParsedAnimPayloadCached(safePayload, out var parsed))
                return false;

            var spr = mob.spr;
            if (spr == null)
                return false;

            var animManager = GetMobAnimManager(mob);
            if (animManager == null)
                return false;

            try
            {
                var top = GetTopAnimInstance(animManager);
                var currentGroup = top?.group?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(currentGroup))
                    currentGroup = spr.groupName?.ToString() ?? string.Empty;

                if (!string.Equals(currentGroup, parsed.Group, StringComparison.Ordinal))
                {
                    // A host anim name the local lib does not contain (lib swapped mid-transition,
                    // different asset variant) must be skipped, not handed to slib to throw on.
                    if (!SpriteHasAnimGroup(spr, parsed.Group))
                        return false;

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
                return false;
            }

            // Cache only after the sprite and animation manager accepted the payload. Boss and
            // helper proxies can receive their first host snapshot before native graphics finish
            // initialization; caching that failed attempt permanently hid later phase animations.
            lock (Sync)
                clientLastAppliedAnimPayloadByMob[mob] = safePayload;

            return true;
        }
    }
}

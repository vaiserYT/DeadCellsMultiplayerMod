using dc.en;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using System.Diagnostics;
using System.Reflection;

namespace DeadCellsMultiplayerMod
{
    public class DeadBase : dc.GameCinematic
    {
        private readonly Hero _hero;
        private HeroDeadCorpse? _corpse;
        private Homunculus? _homunculus;
        private bool _lethalFallStarted;
        private bool _anchoredGroundedPoseApplied;
        private long _lethalFallStartedTicks;
        private bool _cineSuppressed;
        private bool _hadHeroVisibleState;
        private bool _heroWasVisible;
        private bool _hadHeroHeadBlackState;
        private int _heroHeadBlackValue;
        private bool _hasBossArenaCorpseAnchor;
        private double _bossArenaCorpseAnchorX;
        private double _bossArenaCorpseAnchorY;
        private bool _bossArenaCorpsePushApplied;
        private long _bossArenaCorpsePushStartedTicks;
        private const double BossArenaCorpsePushSettleSeconds = 0.35;
        private const double BossArenaCorpsePushVelocityThreshold = 0.08;
        private const double AnchoredCorpseInitialDropPx = 48.0;
        private const double AnchoredCorpseLandingTimeoutSeconds = 0.70;
        private const double AnchoredCorpseMaxHorizontalDriftPx = 48.0;
        private const double AnchoredCorpseMaxDownwardDriftPx = 96.0;

        public DeadBase(Hero hero, GhostKing? king)
        {
            _DeadBase.EnterGhostDead(this, hero, king);
            _hero = hero;

            CaptureHeroVisibility();
            HideHero();
            CreateCorpse();
            SuppressCineEffects();
            EnsureViewportTracksHero(immediate: true);
        }

        public override void update()
        {
            base.update();

            if (_hero == null || _hero.destroyed)
            {
                destroy();
                return;
            }

            SuppressCineEffects();

            var hasLiveHomunculus = HasLiveHomunculus();

            try { _hero.cancelVelocities(); } catch { }
            if (!hasLiveHomunculus)
            {
                try { _hero.lockControlsS(0.25); } catch { }
            }
            try { _hero.cancelSkillControlLock(); } catch { }

            HideHero();
            EnsureCorpse();
            EnsureHomunculus();
            MaintainLocalHomunculusControl();
            EnsureCorpseFalling();
            EnsureViewportTracksHero(immediate: false);
        }

        public override void onDispose()
        {
            base.onDispose();
            RestoreCineState();
            DisposeCorpse();
            DisposeHomunculus();
            RestoreHeroVisibility();
            EnsureViewportTracksHero(immediate: true);
        }

        private void EnsureCorpse()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                CreateCorpse();
        }

        private void EnsureHomunculus()
        {
            // Fake-death flow no longer uses Homunculus.
            DisposeHomunculus();
        }

        private void CreateCorpse()
        {
            DisposeCorpse();
            DisposeHomunculus();

            if (!IsSafeToCreateCorpse())
                return;

            try
            {
                var corpse = CreateCorpseWithoutDrops();
                if (corpse == null)
                    return;

                _corpse = corpse;
                _lethalFallStarted = false;
                _anchoredGroundedPoseApplied = false;
                _lethalFallStartedTicks = 0;
                _hasBossArenaCorpseAnchor = false;
                _bossArenaCorpseAnchorX = 0;
                _bossArenaCorpseAnchorY = 0;
                _bossArenaCorpsePushApplied = false;
                _bossArenaCorpsePushStartedTicks = 0;
                if (ModEntry.ShouldAnchorLocalDownedCorpse())
                    PlaceCorpseAtHeroAnchorForLanding(corpse);
                else
                    PlaceCorpseAtHeroVisualPosition(corpse);
                if (!ModEntry.ShouldAnchorLocalDownedCorpse())
                    TryApplyBossArenaCorpsePush(corpse);
                EnsureLethalFallStarted();
            }
            catch
            {
                _corpse = null;
            }
        }


        private HeroDeadCorpse? CreateCorpseWithoutDrops()
        {
            // Fake death is recoverable, so the player must keep their real cells and blueprints.
            // HeroDeadCorpse normally spawns the vanilla death drops during construction/init. If
            // those drops are allowed while the hero inventory is also preserved, collecting them
            // duplicates part of the player's cells (the recording showed 80 becoming 110).
            // Temporarily present an empty inventory only while the visual corpse is created, then
            // restore the authoritative hero inventory immediately.
            var hero = _hero;
            if (hero == null || hero.destroyed)
                return null;

            var originalCells = 0;
            var capturedCells = false;
            var originalBlueprints = hero.blueprints;
            try
            {
                originalCells = hero.cells;
                capturedCells = true;
                hero.cells = 0;
                hero.blueprints = (dc.hl.types.ArrayObj)ArrayUtils.CreateDyn().array;
            }
            catch
            {
            }

            try
            {
                var corpse = new HeroDeadCorpse(this, hero);
                corpse.init();
                try { corpse.cells = 0; } catch { }
                return corpse;
            }
            finally
            {
                try
                {
                    if (capturedCells)
                        hero.cells = originalCells;
                }
                catch
                {
                }

                try
                {
                    hero.blueprints = originalBlueprints;
                }
                catch
                {
                }
            }
        }

        private bool IsSafeToCreateCorpse()
        {
            if (_hero == null || _hero.destroyed)
                return false;

            try
            {
                var level = _hero._level;
                if (level == null || level.destroyed)
                    return false;
                if (level.game == null || level.game.destroyed)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void EnsureCorpseFalling()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                return;

            KeepCorpseActive(corpse);
            if (ModEntry.ShouldAnchorLocalDownedCorpse())
            {
                MaintainAnchoredCorpseLanding(corpse);
                return;
            }

            KeepBossArenaCorpseAnchored(corpse);
            EnsureLethalFallStarted();
        }

        private void EnsureLethalFallStarted()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed || _lethalFallStarted)
                return;

            var anchored = ModEntry.ShouldAnchorLocalDownedCorpse();
            var levelId = _hero?._level?.map?.id?.ToString();
            if (!anchored && ModEntry.IsBossLevel(levelId))
            {
                TryClampCorpseToGround(corpse);
                return;
            }

            _lethalFallStarted = true;

            if (anchored)
            {
                // Run the real vanilla lethal-fall sequence at the already-safe logical anchor.
                // We no longer freeze the corpse before collision, because doing so is exactly what
                // left the body hovering and rotating forever. A timeout below is only a fallback.
                _lethalFallStartedTicks = Stopwatch.GetTimestamp();
                PlaceCorpseAtHeroAnchorForLanding(corpse);
                try { corpse.hasGravity = true; } catch { }
                try { corpse.startLethalFall(); } catch { ForceAnchoredGroundedPose(corpse); }
                return;
            }

            try { corpse.startLethalFall(); } catch { }
        }

        private void MaintainAnchoredCorpseLanding(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return;

            EnsureLethalFallStarted();
            if (!_lethalFallStarted)
                return;

            if (_anchoredGroundedPoseApplied)
            {
                PinAnchoredGroundedCorpse(corpse);
                return;
            }

            if (!TryGetHeroLogicalAnchor(out var anchorX, out var anchorY))
                return;

            // Vanilla lethal-fall is allowed to handle gravity and the landing transition, but the
            // corpse must not inherit horizontal death impulse and drift back into a pit or spikes.
            KeepCorpseOverAnchorWhileFalling(corpse, anchorX);

            if (IsCorpseStabilized(corpse))
            {
                _anchoredGroundedPoseApplied = true;
                PinAnchoredGroundedCorpse(corpse);
                return;
            }

            var shouldForceGroundedPose = false;
            if (TryGetCorpseLogicalPosition(corpse, out var corpseX, out var corpseY))
            {
                if (Math.Abs(corpseX - anchorX) > AnchoredCorpseMaxHorizontalDriftPx ||
                    corpseY > anchorY + AnchoredCorpseMaxDownwardDriftPx)
                {
                    shouldForceGroundedPose = true;
                }
            }

            if (!shouldForceGroundedPose && _lethalFallStartedTicks > 0)
            {
                var elapsed = (Stopwatch.GetTimestamp() - _lethalFallStartedTicks) /
                              (double)Stopwatch.Frequency;
                shouldForceGroundedPose = elapsed >= AnchoredCorpseLandingTimeoutSeconds;
            }

            if (shouldForceGroundedPose)
                ForceAnchoredGroundedPose(corpse);
        }


        private static void KeepCorpseOverAnchorWhileFalling(HeroDeadCorpse corpse, double anchorX)
        {
            if (corpse == null || corpse.destroyed || !double.IsFinite(anchorX))
                return;

            try
            {
                var currentX = (corpse.cx + corpse.xr) * 24.0;
                var deltaX = anchorX - currentX;
                var tileX = anchorX / 24.0;
                var cx = (int)Math.Floor(tileX);
                corpse.cx = cx;
                corpse.xr = tileX - cx;
                if (corpse.spr != null && double.IsFinite(deltaX))
                    corpse.spr.x += deltaX;
            }
            catch
            {
            }

            try { corpse.dx = 0; } catch { }
            try { corpse.bdx = 0; } catch { }
        }

        private void ForceAnchoredGroundedPose(HeroDeadCorpse corpse)
        {
            _anchoredGroundedPoseApplied = TryApplyAnchoredGroundedPose(corpse);
            if (!_anchoredGroundedPoseApplied)
            {
                // Even if the named pose is unavailable on an unusual skin, stop the current
                // animation and freeze at the safe floor anchor rather than allowing endless spin.
                TryStopCorpseAnimationOnLastFrame(corpse);
                _anchoredGroundedPoseApplied = true;
            }

            PinAnchoredGroundedCorpse(corpse);
        }

        private void PinAnchoredGroundedCorpse(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return;

            if (TryGetHeroLogicalAnchor(out var anchorX, out var anchorY))
            {
                FreezeCorpsePhysics(corpse);
                try { corpse.setPosPixel(anchorX, anchorY); } catch { }
            }

            // HeroDeadCorpse's own update can occasionally restore lethalFall. Reassert the landed
            // pose while downed so the visual cannot return to a floating spin.
            if (!IsCorpseStabilized(corpse))
                TryApplyAnchoredGroundedPose(corpse);
            TryStopCorpseAnimationOnLastFrame(corpse);
            FreezeCorpsePhysics(corpse);
        }

        private void PlaceCorpseAtHeroAnchorForLanding(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return;

            if (!TryGetHeroLogicalAnchor(out var anchorX, out var anchorY))
                return;

            try { corpse.cancelVelocities(); } catch { }
            try { corpse.setPosPixel(anchorX, anchorY - AnchoredCorpseInitialDropPx); } catch { }
        }

        private void PlaceCorpseAtHeroVisualPosition(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed || _hero == null)
                return;

            try
            {
                corpse.setPosPixel(_hero.get_targetSprPosX(), _hero.get_targetSprPosY());
                return;
            }
            catch
            {
            }

            try
            {
                if (_hero.spr != null)
                    corpse.setPosPixel(_hero.spr.x, _hero.spr.y);
            }
            catch
            {
            }
        }

        private bool TryGetHeroLogicalAnchor(out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            if (_hero == null || _hero.destroyed)
                return false;

            try
            {
                x = (_hero.cx + _hero.xr) * 24.0;
                y = (_hero.cy + _hero.yr) * 24.0;
                return double.IsFinite(x) && double.IsFinite(y);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetCorpseLogicalPosition(HeroDeadCorpse corpse, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            if (corpse == null || corpse.destroyed)
                return false;

            try
            {
                x = (corpse.cx + corpse.xr) * 24.0;
                y = (corpse.cy + corpse.yr) * 24.0;
                return double.IsFinite(x) && double.IsFinite(y);
            }
            catch
            {
                return false;
            }
        }


        private static bool TryApplyAnchoredGroundedPose(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return false;

            try
            {
                var animManager = corpse.spr?._animManager;
                if (animManager == null)
                    return false;

                animManager
                    .play("lethalSlam".AsHaxeString(), null, null)
                    .stopOnLastFrame(Ref<bool>.Null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryStopCorpseAnimationOnLastFrame(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return;

            try { corpse.spr?._animManager?.stopOnLastFrame(Ref<bool>.Null); } catch { }
        }

        private static void FreezeCorpsePhysics(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return;

            try { corpse.hasGravity = false; } catch { }
            try { corpse.cancelVelocities(); } catch { }
            try { corpse.dx = 0; } catch { }
            try { corpse.dy = 0; } catch { }
            try { corpse.bdx = 0; } catch { }
            try { corpse.bdy = 0; } catch { }
        }

        private static void TryClampCorpseToGround(HeroDeadCorpse corpse)
        {
            // Corpse coordinates come from the authoritative revive anchor. Do not call
            // LevelMap.getGroundYr here; that native bridge can crash the current runtime with a
            // CPoint-to-LevelMap cast failure. Freezing physics keeps the body at the safe anchor.
        }

        private void CaptureBossArenaCorpseAnchor(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed || !ModEntry.IsBossLevel(_hero?._level?.map?.id?.ToString()))
                return;

            try
            {
                _bossArenaCorpseAnchorX = corpse.get_targetSprPosX();
                _bossArenaCorpseAnchorY = corpse.get_targetSprPosY();
                _hasBossArenaCorpseAnchor = true;
                return;
            }
            catch
            {
            }

            try
            {
                if (corpse.spr != null)
                {
                    _bossArenaCorpseAnchorX = corpse.spr.x;
                    _bossArenaCorpseAnchorY = corpse.spr.y;
                    _hasBossArenaCorpseAnchor = true;
                }
            }
            catch
            {
            }
        }

        private void KeepBossArenaCorpseAnchored(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed || !ModEntry.IsBossLevel(_hero?._level?.map?.id?.ToString()))
                return;

            if (!_bossArenaCorpsePushApplied)
                TryApplyBossArenaCorpsePush(corpse);

            TryClampCorpseToGround(corpse);

            if (!_hasBossArenaCorpseAnchor)
            {
                if (!IsBossArenaCorpsePushSettled(corpse))
                    return;

                CaptureBossArenaCorpseAnchor(corpse);
            }

            if (!_hasBossArenaCorpseAnchor)
                return;

            try { corpse.setPosPixel(_bossArenaCorpseAnchorX, _bossArenaCorpseAnchorY); } catch { }
            TryClampCorpseToGround(corpse);
            CaptureBossArenaCorpseAnchor(corpse);
        }

        private void TryApplyBossArenaCorpsePush(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed || _hero == null)
                return;
            if (_bossArenaCorpsePushApplied || ModEntry.ShouldAnchorLocalDownedCorpse())
                return;
            if (!ModEntry.IsBossLevel(_hero._level?.map?.id?.ToString()))
                return;

            var dir = 1;
            try { dir = _hero.dir < 0 ? -1 : 1; } catch { }

            double pushX = dir * 0.18;
            double pushY = -0.12;
            var hasMomentum = false;
            try
            {
                var momentumX = _hero.dx + _hero.bdx;
                var momentumY = _hero.dy + _hero.bdy;
                if (double.IsFinite(momentumX) && double.IsFinite(momentumY))
                {
                    pushX = momentumX;
                    pushY = momentumY;
                    hasMomentum = true;
                }
            }
            catch
            {
            }

            if (!hasMomentum ||
                (System.Math.Abs(pushX) < 0.01 && System.Math.Abs(pushY) < 0.01))
            {
                pushX = dir * 0.18;
                pushY = -0.12;
            }
            else
            {
                if (System.Math.Abs(pushX) < 0.08)
                    pushX = dir * 0.12;
                if (pushY > -0.08)
                    pushY = -0.12;
            }

            try { corpse.hasGravity = true; } catch { }
            try { corpse.bump(pushX, pushY, null); } catch { }
            _bossArenaCorpsePushApplied = true;
            _bossArenaCorpsePushStartedTicks = Stopwatch.GetTimestamp();
            _hasBossArenaCorpseAnchor = false;
        }

        private bool IsBossArenaCorpsePushSettled(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return false;
            if (!_bossArenaCorpsePushApplied)
                return true;

            if (_bossArenaCorpsePushStartedTicks != 0 &&
                Stopwatch.GetElapsedTime(_bossArenaCorpsePushStartedTicks).TotalSeconds >= BossArenaCorpsePushSettleSeconds)
            {
                return true;
            }

            try
            {
                var totalVelocity =
                    System.Math.Abs(corpse.dx) +
                    System.Math.Abs(corpse.dy) +
                    System.Math.Abs(corpse.bdx) +
                    System.Math.Abs(corpse.bdy);
                if (totalVelocity <= BossArenaCorpsePushVelocityThreshold)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private void CreateHomunculus(HeroDeadCorpse corpse)
        {
            _homunculus = null;
        }

        private bool HasLiveHomunculus()
        {
            return false;
        }

        private void MaintainLocalHomunculusControl()
        {
            // No-op: local fake-death should not create/control Homunculus.
        }

        private static dc.tool.mainSkills.Homunculus? GetHomunculusSkill(Hero? hero)
        {
            if (hero == null)
                return null;

            try
            {
                var manager = hero.mainSkillsManager;
                if (manager == null)
                    return null;

                return manager.getMainSkill(dc.tool.mainSkills.Homunculus.Class) as dc.tool.mainSkills.Homunculus;
            }
            catch
            {
                return null;
            }
        }

        private static void KeepCorpseActive(HeroDeadCorpse corpse)
        {
            if (corpse == null || corpse.destroyed)
                return;

            var wasOutOfGame = false;
            try { wasOutOfGame = corpse.isOutOfGame; } catch { }

            try { corpse.isOnScreen = true; } catch { }
            try
            {
                if (corpse.onScreenRecent < 1200.0)
                    corpse.onScreenRecent = 1200.0;
            }
            catch { }

            try { corpse.lastOutOfGame = false; } catch { }
            try { corpse.isOutOfGame = false; } catch { }

            if (!wasOutOfGame)
                return;

            try { corpse.onOutOfGameChange(); } catch { }
        }

        public bool TryGetCorpsePixelPosition(out double x, out double y)
        {
            x = 0;
            y = 0;

            var corpse = _corpse;
            if (corpse == null || corpse.destroyed)
                return false;

            try
            {
                // Use physics-driven target coordinates so hero follows corpse reliably
                // even when sprite position is temporarily unavailable or delayed.
                x = corpse.get_targetSprPosX();
                y = corpse.get_targetSprPosY();
                return true;
            }
            catch
            {
            }

            var sprite = corpse.spr;
            if (sprite != null)
            {
                x = sprite.x;
                y = sprite.y;
                return true;
            }

            try
            {
                x = (corpse.cx + corpse.xr) * 24.0;
                y = (corpse.cy + corpse.yr) * 24.0;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetHomunculusPixelPosition(out double x, out double y)
        {
            // Keep network/revive logic compatible by mirroring corpse position
            // when fake-death head is disabled.
            if (TryGetCorpsePixelPosition(out x, out y))
                return true;

            x = 0;
            y = 0;
            return false;
        }

        public bool TryGetHomunculusAnim(out string? anim)
        {
            anim = null;
            return false;
        }

        public bool IsHomunculusNearCorpse(double maxDistancePx)
        {
            if (maxDistancePx <= 0)
                return false;

            if (!TryGetCorpsePixelPosition(out var corpseX, out var corpseY))
                return false;
            if (!TryGetHomunculusPixelPosition(out var headX, out var headY))
                return false;

            var dx = headX - corpseX;
            var dy = headY - corpseY;
            return dx * dx + dy * dy <= maxDistancePx * maxDistancePx;
        }

        public bool IsCorpseInLethalFall()
        {
            var corpse = _corpse;
            if (corpse == null || corpse.destroyed || !_lethalFallStarted)
                return false;

            if (IsCorpseStabilized(corpse))
                return false;

            try
            {
                var group = corpse.spr?.groupName?.ToString();
                if (!string.IsNullOrEmpty(group) &&
                    group.IndexOf("lethalFall", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
            }

            return true;
        }

        private static bool IsCorpseStabilized(HeroDeadCorpse corpse)
        {
            try
            {
                var group = corpse.spr?.groupName?.ToString();
                if (!string.IsNullOrEmpty(group) &&
                    group.IndexOf("lethalSlam", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private void HideHero()
        {
            try { _hero.visible = false; } catch { }
            SetHeroHeadVisible(false);
        }

        private void SuppressCineEffects()
        {
            RestoreCineState();

            if (_cineSuppressed)
            {
                TryKeepHudVisibleWhenAllowed();
                return;
            }

            try { disableBars(); } catch { }
            try { bars = 0.0; } catch { }

            try
            {
                var top = topBar;
                if (top != null)
                    top.set_visible(false);
            }
            catch
            {
            }

            try
            {
                var bottom = bottomBar;
                if (bottom != null)
                    bottom.set_visible(false);
            }
            catch
            {
            }

            // Dead player should keep normal HUD visible during fake-death state,
            // but do not override pause/full-map/UI-hidden states.
            TryKeepHudVisibleWhenAllowed();
            _cineSuppressed = true;
        }

        private static void TryKeepHudVisibleWhenAllowed()
        {
            try
            {
                var game = dc.pr.Game.Class.ME;
                if (game == null)
                    return;

                try
                {
                    if (game.paused)
                        return;
                }
                catch
                {
                }

                if (ShouldRespectMenuHiddenHud(game))
                    return;

                try
                {
                    var console = dc.ui.Console.Class.ME;
                    if (console != null && console.flags.exists(dc.ui.Console.Class.HIDE_UI))
                        return;
                }
                catch
                {
                }

                try
                {
                    var hud = game.hud;
                    if (hud != null)
                    {
                        var mini = hud.minimap;
                        if (mini != null)
                        {
                            try
                            {
                                if (mini.isFullscreen)
                                    return;
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }

                try { game.hud?.show(null); } catch { }
            }
            catch
            {
            }
        }

        /// <summary>Optional menu fields exist on the Haxe <see cref="dc.pr.Game"/> shape but are not always exposed on the C# projection; read via reflection.</summary>
        private static bool TryAnyNonDestroyedOptionalMenu(dc.pr.Game game)
        {
            try
            {
                var gt = game.GetType();
                string[] names = { "pauseMenu", "menu", "curMenu", "inventoryMenu", "modal" };
                for (var ni = 0; ni < names.Length; ni++)
                {
                    var name = names[ni];
                    PropertyInfo? p;
                    try
                    {
                        p = gt.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch
                    {
                        continue;
                    }

                    object? v;
                    try
                    {
                        v = p?.GetValue(game);
                    }
                    catch
                    {
                        continue;
                    }

                    if (v == null)
                        continue;

                    try
                    {
                        var pd = v.GetType().GetProperty("destroyed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (pd == null)
                            return true;
                        if (pd.GetValue(v) is bool d && !d)
                            return true;
                    }
                    catch
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool ShouldRespectMenuHiddenHud(dc.pr.Game game)
        {
            if (game == null)
                return false;

            try
            {
                if (game._pauseAfterFrames > 0)
                    return true;
            }
            catch
            {
            }

            try
            {
                var cine = game.curCine;
                if (cine != null && !cine.destroyed)
                {
                    var t = cine.GetType().Name;
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (t.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            t.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch
            {
            }

            if (TryAnyNonDestroyedOptionalMenu(game))
                return true;

            return false;
        }

        private void RestoreCineState()
        {
            var game = dc.pr.Game.Class.ME;
            if (game == null)
                return;

            try
            {
                if (ReferenceEquals(game.curCine, this))
                    game.curCine = null;
            }
            catch
            {
            }
        }

        private void EnsureViewportTracksHero(bool immediate)
        {
            if (_hero == null || _hero.destroyed)
                return;

            try
            {
                var viewport = _hero._level?.viewport;
                if (viewport == null)
                    return;

                if (!ReferenceEquals(viewport.tracked, _hero))
                    viewport.track(_hero, immediate);
            }
            catch
            {
            }
        }

        private void CaptureHeroVisibility()
        {
            if (_hadHeroVisibleState)
                return;

            try { _heroWasVisible = _hero.visible; }
            catch { _heroWasVisible = true; }
            _hadHeroVisibleState = true;

            try
            {
                var head = _hero?.heroHead;
                if (head != null)
                {
                    _heroHeadBlackValue = head.headBlack;
                    _hadHeroHeadBlackState = true;
                }
            }
            catch
            {
            }
        }

        private void RestoreHeroVisibility()
        {
            if (!_hadHeroVisibleState || _hero == null)
                return;

            try { _hero.visible = _heroWasVisible; } catch { }
            SetHeroHeadVisible(_heroWasVisible);
        }

        private void SetHeroHeadVisible(bool visible)
        {
            try
            {
                var head = _hero?.heroHead;
                if (head == null)
                    return;

                try { head.customHeadSpr?.set_visible(visible); } catch { }
                try { head.customBackSpr?.set_visible(visible); } catch { }
                try { head.headNormalSb?.set_visible(visible); } catch { }
                try { head.headAddSb?.set_visible(visible); } catch { }
                if (visible && _hadHeroHeadBlackState)
                {
                    try { head.headBlack = _heroHeadBlackValue; } catch { }
                }
                else
                {
                    try { head.headBlack = 0; } catch { }
                }
                try { head.eye?.set_visible(visible); } catch { }
            }
            catch
            {
            }
        }

        private void DisposeCorpse()
        {
            var corpse = _corpse;
            _corpse = null;
            _lethalFallStarted = false;
            if (corpse == null)
                return;

            try
            {
                if (!corpse.destroyed)
                    corpse.destroy();
            }
            catch { }

            try { corpse.dispose(); } catch { }
        }

        private void DisposeHomunculus()
        {
            var hom = _homunculus;
            _homunculus = null;
            if (hom == null)
                return;

            RemoveFromHomunculusSkillEntityList(hom);
            try
            {
                if (!hom.destroyed)
                    hom.destroy();
            }
            catch
            {
            }

            try { hom.dispose(); } catch { }
        }

        private static void RemoveFromHomunculusSkillEntityList(Homunculus hom)
        {
            if (hom == null)
                return;

            try
            {
                var bucketObj = hom._level?.entitiesByClass?.get(17969);
                if (bucketObj is dc.hl.types.ArrayObj bucket)
                    bucket.remove(hom);
            }
            catch
            {
            }
        }
    }
}

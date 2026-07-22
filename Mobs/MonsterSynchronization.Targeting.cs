using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using dc;
using dc.en;
using dc.h2d;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.pr;
using dc.tool.atk;
using dc.tool.skill;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using DeadCellsMultiplayerMod.Mobs.Levelinit;
using Hashlink.Virtuals;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization :
    IOnAdvancedModuleInitializing,
    IOnFrameUpdate,
    IEventReceiver
    {
        private static void TrySetNemesisTargetExact(Mob mob, Entity target)
        {
            if (mob == null || target == null)
                return;

            try
            {
                if (ReferenceEquals(mob.nemesisTarget, target))
                    return;
            }
            catch
            {
            }

            if (TryResolveSafeBossNemesisTarget(mob, target, out var safeBossTarget))
                target = safeBossTarget;
            else if (BossSyncHelpers.IsBossMob(mob))
                return;

            System.Threading.Interlocked.Increment(ref forceExactNemesisTargetDepth);
            try
            {
                mob.setNemesisTarget(target);
            }
            catch
            {
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref forceExactNemesisTargetDepth);
            }
        }

        private static void TrySetMobAttackTargetsExact(Mob mob, Entity target, int attackDir = 0, bool forceAttackDir = false)
        {
            if (mob == null || target == null)
                return;

            try
            {
                var mobX = GetWorldX(mob);
                var targetX = GetWorldX(target);
                var targetDir = targetX < mobX ? -1 : targetX > mobX ? 1 : mob.dir;

                if (forceAttackDir)
                {
                    var normalized = NormalizeDir(attackDir);
                    if (normalized != 0)
                        mob.dir = normalized;
                }
                else
                {
                    if (targetDir != 0)
                        mob.dir = targetDir;
                }
            }
            catch
            {
            }

            try
            {
                mob.setAttackTarget(target);
            }
            catch
            {
            }

            TrySetNemesisTargetExact(mob, target);

            if (forceAttackDir)
            {
                try
                {
                    var normalized = NormalizeDir(attackDir);
                    if (normalized != 0)
                        mob.dir = normalized;
                }
                catch
                {
                }
            }
        }

        private static int NormalizeDir(int dir)
        {
            if (dir < 0) return -1;
            if (dir > 0) return 1;
            return 0;
        }

        private static int ComputeResponsiveFacingDir(Mob mob, ClientMobState state)
        {
            if (mob == null)
                return NormalizeDir(state.Dir);

            var netDir = NormalizeDir(state.Dir);
            if (netDir != 0)
                return netDir;

            var currentX = GetWorldX(mob);
            var deltaX = state.X - currentX;
            if (deltaX >= ClientTurnSnapDeltaPx)
                return 1;
            if (deltaX <= -ClientTurnSnapDeltaPx)
                return -1;

            return NormalizeDir(mob.dir);
        }

        private static double GetWorldX(Entity entity)
        {
            return (entity.cx + entity.xr) * PixelsPerCase;
        }

        private static double GetWorldY(Entity entity)
        {
            return (entity.cy + entity.yr) * PixelsPerCase;
        }

        private static double GetSyncX(Entity entity)
        {
            try
            {
                var spr = entity.spr;
                if (spr != null)
                    return spr.x;
            }
            catch
            {
            }

            return GetWorldX(entity);
        }

        private static double GetSyncY(Entity entity)
        {
            try
            {
                var spr = entity.spr;
                if (spr != null)
                    return spr.y;
            }
            catch
            {
            }

            return GetWorldY(entity);
        }

        private static void SetWorldXKeepingY(Mob mob, double worldX)
        {
            if (mob == null)
                return;

            // Update X only and preserve the entity's current Y cell/fraction to avoid
            // disturbing native vertical physics (oldSkill/mid-air states).
            var xCellFloat = worldX / PixelsPerCase;
            var xCase = (int)System.Math.Floor(xCellFloat);
            var xFrac = xCellFloat - xCase;

            if (xFrac < 0.0)
                xFrac = 0.0;
            else if (xFrac > 1.0)
                xFrac = 1.0;

            mob.setPosCase(xCase, mob.cy, xFrac, mob.yr);
        }

        private readonly struct ParsedAnimPayload
        {
            public readonly string Group;
            public readonly bool Reverse;
            public readonly double Speed;

            public ParsedAnimPayload(string group, bool reverse, double speed)
            {
                Group = group ?? string.Empty;
                Reverse = reverse;
                Speed = speed;
            }
        }

        private static AnimManager? GetMobAnimManager(Mob mob)
        {
            var spr = mob.spr;
            if (spr == null)
                return null;

            try
            {
                return spr._animManager ?? spr.get_anim();
            }
            catch
            {
                return spr._animManager;
            }
        }

        private static AnimInstance? GetTopAnimInstance(AnimManager? animManager)
        {
            var stack = animManager?.stack;
            if (stack == null || stack.length <= 0)
                return null;

            try
            {
                return stack.getDyn(0) as AnimInstance;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildAnimPayload(Mob mob)
        {
            var spr = mob.spr;
            if (spr == null)
                return string.Empty;

            var group = spr.groupName?.ToString() ?? string.Empty;
            var reverse = false;
            var speed = 1.0;

            try
            {
                var animManager = GetMobAnimManager(mob);
                var top = GetTopAnimInstance(animManager);
                if (top != null)
                {
                    if (!string.IsNullOrWhiteSpace(top.group?.ToString()))
                        group = top.group.ToString();
                    reverse = top.reverse;
                    if (top.speed > 0.0)
                        speed = top.speed;
                }
            }
            catch
            {
            }

            if (string.IsNullOrWhiteSpace(group))
                return string.Empty;

            string encodedGroup;
            try
            {
                encodedGroup = Uri.EscapeDataString(group);
            }
            catch
            {
                encodedGroup = group;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{encodedGroup}~{(reverse ? 1 : 0)}~{speed:R}");
        }

        private static bool TryParseAnimPayload(string? payload, out ParsedAnimPayload parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            var parts = payload.Split('~', StringSplitOptions.None);
            if (parts.Length < 3)
                return false;

            var encodedGroup = parts[0];
            string group;
            try
            {
                group = Uri.UnescapeDataString(encodedGroup);
            }
            catch
            {
                group = encodedGroup;
            }

            if (string.IsNullOrWhiteSpace(group))
                return false;

            var hasLegacyFrame = parts.Length >= 4 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            var reversePart = hasLegacyFrame ? parts[2] : parts[1];
            var speedPart = hasLegacyFrame ? parts[3] : parts[2];

            var reverse = reversePart == "1";
            if (!double.TryParse(speedPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                speed = 1.0;

            parsed = new ParsedAnimPayload(group, reverse, System.Math.Clamp(speed, 0.01, MaxClientAnimPayloadSpeed));
            return true;
        }

        private static bool TryGetParsedAnimPayloadCached(string payload, out ParsedAnimPayload parsed)
        {
            parsed = default;
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            lock (Sync)
            {
                if (parsedAnimPayloadCache.TryGetValue(payload, out parsed))
                    return true;
            }

            if (!TryParseAnimPayload(payload, out parsed))
                return false;

            lock (Sync)
            {
                if (parsedAnimPayloadCache.Count >= ParsedAnimPayloadCacheLimit)
                    parsedAnimPayloadCache.Clear();

                parsedAnimPayloadCache[payload] = parsed;
            }

            return true;
        }
        
        private static bool ApplyBossAnimPayloadForce(Mob mob, string payload)
        {
            if (mob == null || string.IsNullOrWhiteSpace(payload))
                return false;

            if (!TryGetParsedAnimPayloadCached(payload, out var parsed))
                return false;

            try
            {
                var spr = mob.spr;
                if (spr == null)
                    return false;

                var animManager = GetMobAnimManager(mob);
                var top = GetTopAnimInstance(animManager);

                var currentGroup = top?.group?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(currentGroup))
                    currentGroup = spr.groupName?.ToString() ?? string.Empty;

                // Randomized anim variants (walkIdleA / walkIdleB / ...) are rolled independently
                // per peer. They are the same logical state, so the client's locally chosen
                // variant must not be restarted (or, worse, swapped in place) every frame just
                // because the host rolled another letter.
                var groupsMatch = string.Equals(currentGroup, parsed.Group, StringComparison.Ordinal) ||
                                  IsSameAnimVariantFamily(currentGroup, parsed.Group);

                if (!groupsMatch)
                {
                    // CRASH FIX (bossanim-framesafe): never assign top.group / spr.groupName in place.
                    // Swapping the group string while the anim instance keeps its old frame index
                    // makes the next AnimManager._update fetch an out-of-range frame
                    // ("Unknown frame: behemothWalkIdleB(60)") and hard-crashes the render loop.
                    // Group changes must go through play(), which resets the frame counter, and
                    // only to groups this sprite's lib actually contains.
                    if (animManager == null || !SpriteHasAnimGroup(spr, parsed.Group))
                        return false;

                    if (!TryBeginForcedBossAnimGroup(mob, parsed.Group))
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

                lock (Sync)
                {
                    clientLastAppliedAnimPayloadByMob[mob] = payload;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True when the sprite's currently attached lib actually contains <paramref name="group"/>.
        /// Boss transformations swap sprite libs natively; a host anim name from the other side of
        /// such a swap must be ignored instead of handed to slib (which throws).
        /// </summary>
        private static bool SpriteHasAnimGroup(HSprite? spr, string group)
        {
            if (spr == null || string.IsNullOrWhiteSpace(group))
                return false;

            try
            {
                var groups = spr.lib?.groups;
                return groups != null && groups.exists(group.AsHaxeString());
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSameAnimVariantFamily(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;

            return string.Equals(StripAnimVariantSuffix(a), StripAnimVariantSuffix(b), StringComparison.Ordinal);
        }

        private static string StripAnimVariantSuffix(string group)
        {
            // Dead Cells names randomized variants with a single trailing capital letter after a
            // lowercase/digit character (behemothWalkIdleA, behemothWalkIdleB, ...).
            if (group.Length >= 2)
            {
                var last = group[^1];
                if (last >= 'A' && last <= 'Z')
                {
                    var prev = group[^2];
                    if ((prev >= 'a' && prev <= 'z') || (prev >= '0' && prev <= '9'))
                        return group[..^1];
                }
            }

            return group;
        }

        /// <summary>
        /// Rate-limits forced boss anim group restarts. If native client code re-picks a different
        /// group right after a forced play(), re-forcing the same target every frame would pin the
        /// boss to frame 0 forever. One forced restart per target group per short window keeps the
        /// visual converging without fighting the local animation state machine at frame rate.
        /// </summary>
        private static bool TryBeginForcedBossAnimGroup(Mob mob, string group)
        {
            double frame = 0.0;
            try
            {
                var level = mob._level ?? currentLevel;
                if (level != null)
                    frame = level.ftime;
            }
            catch
            {
            }

            lock (Sync)
            {
                if (clientLastForcedBossAnimByMob.TryGetValue(mob, out var last) &&
                    string.Equals(last.Group, group, StringComparison.Ordinal))
                {
                    var delta = frame - last.Frame;
                    if (delta >= 0.0 && delta < ClientBossAnimForceReplayCooldownFrames)
                        return false;
                }

                clientLastForcedBossAnimByMob[mob] = (group, frame);
                return true;
            }
        }

        private static long s_livingTrackedBossCacheTickMs;
        private static bool s_livingTrackedBossCacheValue;

        /// <summary>
        /// True while any tracked, non-destroyed boss encounter combatant is still alive in the
        /// current level. Unlike a level-id check this goes false the moment the fight is actually
        /// over, and it covers Boss Rush duo clones that can outlive the primary Level.boss.
        /// Cached for 250ms — callers poll it per frame from door/interaction code.
        /// </summary>
        internal static bool HasLivingTrackedBoss()
        {
            var now = System.Environment.TickCount64;
            if (s_livingTrackedBossCacheTickMs != 0 && now - s_livingTrackedBossCacheTickMs < 250)
                return s_livingTrackedBossCacheValue;

            var alive = false;
            lock (Sync)
            {
                for (var i = 0; i < trackedMobs.Count; i++)
                {
                    var mob = trackedMobs[i];
                    if (mob == null)
                        continue;
                    try
                    {
                        if (mob.destroyed || mob.life <= 0)
                            continue;
                        if (Bosses.BossSyncHelpers.IsBossMob(mob))
                        {
                            alive = true;
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            s_livingTrackedBossCacheValue = alive;
            s_livingTrackedBossCacheTickMs = now;
            return alive;
        }
    }
}

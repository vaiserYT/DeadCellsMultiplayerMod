using System;
using System.Diagnostics;
using dc;
using dc.en;
using dc.hl.types;
using dc.libs.heaps.slib;
using dc.pr;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private static bool s_remoteKingRenderDetachedForTransition;
        private static bool s_subLevelRenderGuardArmed;
        private static long s_subLevelRenderGuardStartedTicks;
        private static string s_subLevelRenderGuardReason = string.Empty;
        private static long s_lastKingSkinGuardLogTicks;
        private const double KingSkinGuardLogIntervalSeconds = 1.0;
        private const double SubLevelRenderGuardTimeoutSeconds = 8.0;

        internal static bool IsRemoteKingTransitionActive => s_remoteKingRenderDetachedForTransition;

        internal static void CheckRemoteKingRenderSafety(string reason)
        {
            if (s_remoteKingRenderDetachedForTransition)
                return;

            GuardRemoteKingSprites(reason, detachForTransition: false);
        }

        internal static void PrepareRemoteKingsForLevelTransition(string reason)
        {
            if (s_remoteKingRenderDetachedForTransition)
                return;

            s_remoteKingRenderDetachedForTransition = true;
            GuardRemoteKingSprites(reason, detachForTransition: true);
        }

        internal static void FinishRemoteKingLevelTransition()
        {
            s_remoteKingRenderDetachedForTransition = false;
        }

        internal static void PrepareRemoteKingsForSubLevelTransition(string reason)
        {
            s_subLevelRenderGuardArmed = true;
            s_subLevelRenderGuardStartedTicks = Stopwatch.GetTimestamp();
            s_subLevelRenderGuardReason = string.IsNullOrWhiteSpace(reason)
                ? "sublevel-transition"
                : reason;

            var instance = Instance;
            instance?.DrainRemoteCombatQueuesAfterLevelChange();
            instance?.MarkDiveNetGuardAfterSpawnOrRoomChange();
            PrepareRemoteKingsForLevelTransition(s_subLevelRenderGuardReason);
            instance?.Logger.Information(
                "[NetMod][SubLevelGuard] armed reason={Reason}",
                s_subLevelRenderGuardReason);
        }

        internal static void CancelRemoteKingSubLevelTransition(string reason)
        {
            var instance = Instance;
            s_subLevelRenderGuardArmed = false;
            s_subLevelRenderGuardStartedTicks = 0;
            s_subLevelRenderGuardReason = string.Empty;
            FinishRemoteKingLevelTransition();
            instance?.Logger.Warning(
                "[NetMod][SubLevelGuard] cancelled reason={Reason}",
                reason);
        }

        private void Hook_Level_onActivation_SubLevelRenderGuard(
            Hook_Level.orig_onActivation orig,
            Level self)
        {
            var wasArmed = s_subLevelRenderGuardArmed;
            if (!wasArmed)
            {
                orig(self);
                return;
            }

            var targetLevelId = "<unknown>";
            try
            {
                targetLevelId = self?.map?.id?.ToString() ?? "<unknown>";
            }
            catch
            {
            }

            Logger.Information(
                "[NetMod][SubLevelGuard] activating target={Target} reason={Reason}",
                targetLevelId,
                s_subLevelRenderGuardReason);

            // The remote KingSkin must stay detached for the entire native
            // Level.resume -> Level.onActivation -> Level.initRender sequence.
            // Calling the original first is intentional: LevelDisp.render invokes
            // Boot.tryRender inside this call. Re-attaching before it returns brings
            // back the null groupName crash on timed/no-hit reward rooms.
            orig(self);

            CompleteRemoteKingSubLevelTransitionGuard(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"level-onActivation:{targetLevelId}"));
        }

        private void TickRemoteKingSubLevelTransitionGuard()
        {
            if (!s_subLevelRenderGuardArmed || s_subLevelRenderGuardStartedTicks == 0)
                return;

            if (Stopwatch.GetElapsedTime(s_subLevelRenderGuardStartedTicks).TotalSeconds <
                SubLevelRenderGuardTimeoutSeconds)
            {
                return;
            }

            CompleteRemoteKingSubLevelTransitionGuard("timeout");
        }

        private void CompleteRemoteKingSubLevelTransitionGuard(string completionReason)
        {
            if (!s_subLevelRenderGuardArmed)
                return;

            var armedReason = s_subLevelRenderGuardReason;
            s_subLevelRenderGuardArmed = false;
            s_subLevelRenderGuardStartedTicks = 0;
            s_subLevelRenderGuardReason = string.Empty;

            // The detached HSprite nodes cannot safely be re-parented because the
            // active level display changed. Dispose the old ghost shell only after
            // native rendering completed; the next remote snapshot recreates it in
            // the currently active parent/sublevel.
            for (var slot = 0; slot < clients.Length; slot++)
                DisposeClientSlot(slot, clearIdentity: false);

            FinishRemoteKingLevelTransition();
            DrainRemoteCombatQueuesAfterLevelChange();
            MarkDiveNetGuardAfterSpawnOrRoomChange();
            SendCurrentRoomTarget(force: true);
            GameMenu.EnqueueMainThreadCoalesced("ghost:receive-coords", ReceiveGhostCoords);

            Logger.Information(
                "[NetMod][SubLevelGuard] completed reason={CompletionReason} armedBy={ArmedReason}",
                completionReason,
                armedReason);
        }

        internal static bool EnsureGhostKingRenderSafe(GhostKing? king, string reason, bool detachForTransition)
        {
            return GuardSingleKingSprite(king, -1, reason, detachForTransition);
        }

        private static void GuardRemoteKingSprites(string reason, bool detachForTransition)
        {
            for (var slot = 0; slot < clients.Length; slot++)
            {
                var king = clients[slot];
                if (king == null)
                    continue;

                GuardSingleKingSprite(king, slot, reason, detachForTransition);
            }
        }

        private static bool GuardSingleKingSprite(GhostKing? king, int slot, string reason, bool detachForTransition)
        {
            if (king == null)
                return true;

            var bodyOk = EnsureSpriteAnimationGroup(king.spr, "idle", out var bodyBefore, out var bodyAfter);
            var invalidCloneCount = 0;
            var repairedCloneCount = 0;

            var clones = king.spriteClones;
            if (clones != null)
            {
                for (var i = 0; i < clones.length; i++)
                {
                    virtual_e_followHead_notActualClone_offX_offY_scaleBonus_? cloneInfo = null;
                    try { cloneInfo = clones.array[i] as virtual_e_followHead_notActualClone_offX_offY_scaleBonus_; } catch { }
                    var clone = cloneInfo?.e;
                    if (clone == null)
                        continue;

                    if (!HasValidAnimationGroup(clone))
                        invalidCloneCount++;

                    if (EnsureSpriteAnimationGroup(clone, "idle", out _, out _))
                        repairedCloneCount++;
                    else
                        HideAndDetachSprite(clone, detach: true);
                }
            }

            var head = king.head;
            var headFrontOk = EnsureSpriteAnimationGroup(head?.customHeadSpr, "idle", out _, out _);
            var headBackOk = EnsureSpriteAnimationGroup(head?.customBackSpr, "idle", out _, out _);

            if (!bodyOk)
                HideAndDetachSprite(king.spr, detach: true);

            if (detachForTransition)
            {
                try { king.visible = false; } catch { }
                HideAndDetachSprite(king.spr, detach: true);
                HideAndDetachSprite(head?.customHeadSpr, detach: true);
                HideAndDetachSprite(head?.customBackSpr, detach: true);

                if (clones != null)
                {
                    for (var i = 0; i < clones.length; i++)
                    {
                        virtual_e_followHead_notActualClone_offX_offY_scaleBonus_? cloneInfo = null;
                        try { cloneInfo = clones.array[i] as virtual_e_followHead_notActualClone_offX_offY_scaleBonus_; } catch { }
                        HideAndDetachSprite(cloneInfo?.e, detach: true);
                    }
                }
            }

            if (!bodyOk || !headFrontOk || !headBackOk || invalidCloneCount > 0 || detachForTransition)
            {
                LogKingSkinGuard(
                    slot,
                    reason,
                    detachForTransition,
                    bodyOk,
                    bodyBefore,
                    bodyAfter,
                    invalidCloneCount,
                    repairedCloneCount,
                    headFrontOk,
                    headBackOk);
            }

            return bodyOk && headFrontOk && headBackOk && invalidCloneCount == 0;
        }

        private static bool EnsureSpriteAnimationGroup(HSprite? sprite, string fallbackGroup, out string before, out string after)
        {
            before = ReadGroupName(sprite);
            after = before;
            if (sprite == null)
                return true;

            if (HasValidAnimationGroup(sprite))
                return true;

            try
            {
                var anim = sprite._animManager;
                if (anim != null)
                    anim.play(fallbackGroup.AsHaxeString(), null, null).loop(null);
            }
            catch
            {
            }

            after = ReadGroupName(sprite);
            if (HasValidAnimationGroup(sprite))
                return true;

            try
            {
                var lib = sprite.lib;
                if (lib != null)
                {
                    var startFrame = 0;
                    var stopAllAnimations = true;
                    sprite.set(
                        lib,
                        fallbackGroup.AsHaxeString(),
                        Ref<int>.From(ref startFrame),
                        Ref<bool>.From(ref stopAllAnimations));
                }
            }
            catch
            {
            }

            after = ReadGroupName(sprite);
            return HasValidAnimationGroup(sprite);
        }

        private static bool HasValidAnimationGroup(HSprite? sprite)
        {
            if (sprite == null)
                return true;

            try
            {
                var group = sprite.groupName;
                return group != null && !string.IsNullOrWhiteSpace(group.ToString());
            }
            catch
            {
                return false;
            }
        }

        private static string ReadGroupName(HSprite? sprite)
        {
            if (sprite == null)
                return "<no-sprite>";

            try
            {
                return sprite.groupName?.ToString() ?? "<null>";
            }
            catch
            {
                return "<read-failed>";
            }
        }

        private static void HideAndDetachSprite(HSprite? sprite, bool detach)
        {
            if (sprite == null)
                return;

            try { sprite.set_visible(false); } catch { }
            if (!detach)
                return;

            try
            {
                var parent = sprite.parent;
                if (parent != null)
                    parent.removeChild(sprite);
            }
            catch
            {
            }
        }

        private static void LogKingSkinGuard(
            int slot,
            string reason,
            bool detached,
            bool bodyOk,
            string bodyBefore,
            string bodyAfter,
            int invalidCloneCount,
            int repairedCloneCount,
            bool headFrontOk,
            bool headBackOk)
        {
            var instance = Instance;
            if (instance == null)
                return;

            var now = Stopwatch.GetTimestamp();
            if (!detached && s_lastKingSkinGuardLogTicks != 0 &&
                Stopwatch.GetElapsedTime(s_lastKingSkinGuardLogTicks, now).TotalSeconds < KingSkinGuardLogIntervalSeconds)
                return;

            s_lastKingSkinGuardLogTicks = now;
            instance.Logger.Warning(
                "[NetMod][KingSkinGuard] reason={Reason} slot={Slot} detached={Detached} bodyOk={BodyOk} bodyGroupBefore={Before} bodyGroupAfter={After} invalidClones={InvalidClones} repairedClones={RepairedClones} headFrontOk={HeadFrontOk} headBackOk={HeadBackOk}",
                reason,
                slot,
                detached,
                bodyOk,
                bodyBefore,
                bodyAfter,
                invalidCloneCount,
                repairedCloneCount,
                headFrontOk,
                headBackOk);
        }
    }
}

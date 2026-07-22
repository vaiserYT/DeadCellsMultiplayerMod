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
        private static int s_activateSubLevelRuntimeHookDepth;
        private static long s_subLevelRenderGuardStartedTicks;
        private static string s_subLevelRenderGuardReason = string.Empty;
        private static long s_lastKingSkinGuardLogTicks;
        private static long s_remoteKingCreationBlockedUntilTicks;
        private const double KingSkinGuardLogIntervalSeconds = 1.0;
        private const double SubLevelRenderGuardTimeoutSeconds = 8.0;
        private const double PostNativeRemoteKingCreationDelaySeconds = 0.35;

        internal static bool IsRemoteKingTransitionActive => s_remoteKingRenderDetachedForTransition;
        internal static bool IsRemoteKingTransitionGuardArmed => s_subLevelRenderGuardArmed;
        internal static bool IsRemoteKingSubLevelTransitionGuardArmed => s_subLevelRenderGuardArmed;
        internal static bool IsRemoteKingCreationBlocked =>
            s_remoteKingCreationBlockedUntilTicks != 0 &&
            Stopwatch.GetTimestamp() < s_remoteKingCreationBlockedUntilTicks;

        private void Hook_Game_activateSubLevel(
            Hook_Game.orig_activateSubLevel orig,
            Game self,
            dc.level.LevelMap levelMap,
            int? linkId,
            HaxeProxy.Runtime.Ref<bool> shouldSave,
            HaxeProxy.Runtime.Ref<bool> outAnim)
        {
            var outermost = ++s_activateSubLevelRuntimeHookDepth == 1;
            var guardActiveForCall = false;
            var callFailed = false;

            try
            {
                if (outermost &&
                    _netRole != NetRole.None &&
                    _net != null &&
                    _net.IsAlive &&
                    (HasAnyRemoteKingRenderShell() || s_subLevelRenderGuardArmed))
                {
                    PrepareRemoteKingsForSubLevelTransition(
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"activateSubLevel-native:link={linkId?.ToString() ?? "null"}"));
                    guardActiveForCall = true;

                    Logger.Information(
                        "[NetMod][ActivateSubLevelGuard] pre-native hard teardown linkId={LinkId} depth={Depth}",
                        linkId,
                        s_activateSubLevelRuntimeHookDepth);
                }

                orig(self, levelMap, linkId, shouldSave, outAnim);
            }
            catch
            {
                callFailed = true;
                if (outermost && s_subLevelRenderGuardArmed)
                    CancelRemoteKingSubLevelTransition("activateSubLevel-native-threw");
                throw;
            }
            finally
            {
                s_activateSubLevelRuntimeHookDepth =
                    global::System.Math.Max(0, s_activateSubLevelRuntimeHookDepth - 1);

                if (outermost &&
                    !callFailed &&
                    guardActiveForCall &&
                    s_subLevelRenderGuardArmed)
                {
                    CompleteRemoteKingSubLevelTransitionGuard(
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"activateSubLevel-native-returned:link={linkId?.ToString() ?? "null"}"));
                }
            }
        }

        internal static void CheckRemoteKingRenderSafety(string reason)
        {
            if (s_remoteKingRenderDetachedForTransition)
                return;

            GuardRemoteKingSprites(reason, detachForTransition: false);
        }

        internal static void PrepareRemoteKingsForLevelTransition(string reason)
        {
            var instance = Instance;
            var retiredCombatRuntimes = 0;
            for (var slot = 0; slot < clients.Length; slot++)
            {
                var client = clients[slot];
                if (client == null)
                    continue;

                try
                {
                    client.PrepareForNetworkTransition();
                    retiredCombatRuntimes++;
                }
                catch
                {
                }
            }

            if (retiredCombatRuntimes > 0)
            {
                instance?.Logger.Information(
                    "[NetMod][RemoteCombatTeardown] retired={Count} reason={Reason}",
                    retiredCombatRuntimes,
                    reason);
            }

            if (s_remoteKingRenderDetachedForTransition)
                return;

            s_remoteKingRenderDetachedForTransition = true;
            GuardRemoteKingSprites(reason, detachForTransition: true);
        }

        internal static void FinishRemoteKingLevelTransition()
        {
            s_remoteKingRenderDetachedForTransition = false;
        }

        private static void DelayRemoteKingCreationAfterNativeRender()
        {
            s_remoteKingCreationBlockedUntilTicks = Stopwatch.GetTimestamp() +
                (long)(Stopwatch.Frequency * PostNativeRemoteKingCreationDelaySeconds);
        }

        /// <summary>
        /// Fully disposes remote render shells only for an actual boss-cell main-level reload.
        /// Generic level disposal and sublevel/exit paths deliberately do not call this helper,
        /// preserving the v0.8.68i Cavern Key, boss-cell-door and exit-teleporter lifecycle.
        /// </summary>
        internal static void PrepareAndDisposeRemoteKingsForBossCellReload(string reason)
        {
            PrepareRemoteKingsForLevelTransition(reason);

            var instance = Instance;
            if (instance == null)
                return;

            // Detaching an HSprite is not enough for main-level reloads such as boss-cell
            // changes. The old GhostKing process can still be visited by Boot.tryRender while
            // Game.loadMainLevel is replacing the display tree, causing Null access .groupName.
            // Fully dispose the old remote shells before native level disposal/reload begins.
            for (var slot = 0; slot < clients.Length; slot++)
                instance.DisposeClientSlot(slot, clearIdentity: false);

            instance.DrainRemoteCombatQueuesAfterLevelChange();
            instance.Logger.Information(
                "[NetMod][MainLevelRenderGuard] disposed remote shells reason={Reason}",
                reason);
        }

        internal static void PrepareRemoteKingsForSubLevelTransition(string reason)
        {
            var wasAlreadyArmed = s_subLevelRenderGuardArmed;
            s_subLevelRenderGuardArmed = true;

            if (!wasAlreadyArmed)
            {
                s_subLevelRenderGuardStartedTicks = Stopwatch.GetTimestamp();
                s_subLevelRenderGuardReason = string.IsNullOrWhiteSpace(reason)
                    ? "sublevel-resume"
                    : reason;
            }

            var instance = Instance;
            instance?.DrainRemoteCombatQueuesAfterLevelChange();
            instance?.MarkDiveNetGuardAfterSpawnOrRoomChange();
            PrepareRemoteKingsForLevelTransition(s_subLevelRenderGuardReason);

            // Remove each remote process through Process.disposeImmediately rather than
            // destroy()+dispose()+disposeGfx(). The old triple-dispose path could leave a
            // destroyed process in the level tree and later crash Game disposal/restart in
            // Process._dispose with a null controller.manualLock.
            if (instance != null)
            {
                for (var slot = 0; slot < clients.Length; slot++)
                    instance.DisposeClientSlotForSubLevelTransition(slot, clearIdentity: false);
            }

            instance?.Logger.Information(
                "[NetMod][SubLevelGuard] armed transition-window hard-dispose={HardDispose} reason={Reason}",
                instance != null,
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
            var targetLevelId = "<unknown>";
            var activationKind = "unknown";

            try
            {
                targetLevelId = self?.map?.id?.ToString() ?? "<unknown>";
                if (self != null)
                    activationKind = self.isSubLevel ? "sublevel" : "parent-or-main";
            }
            catch
            {
            }

            // The strongly typed Game.activateSubLevel hook owns the complete native
            // transition window. Never auto-arm here; doing so is too late and can leave
            // the remote shell attached during Level.resume/Boot.tryRender.
            if (!s_subLevelRenderGuardArmed)
            {
                orig(self);
                return;
            }

            Logger.Information(
                "[NetMod][SubLevelGuard] activation inside native guard target={Target} kind={Kind} depth={Depth} reason={Reason}",
                targetLevelId,
                activationKind,
                s_activateSubLevelRuntimeHookDepth,
                s_subLevelRenderGuardReason);

            try
            {
                orig(self);
            }
            catch
            {
                CancelRemoteKingSubLevelTransition(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"level-onActivation-orig-threw:{activationKind}:{targetLevelId}"));
                throw;
            }

            if (s_activateSubLevelRuntimeHookDepth > 0)
                return;

            CompleteRemoteKingSubLevelTransitionGuard(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"level-onActivation-outside-native:{activationKind}:{targetLevelId}"));
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

            // The shell was already removed before native activation. Do not dispose a
            // second time here. Clear the transition gate and rebuild from the latest
            // remote snapshot in the now-active level.
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

        private static bool HasAnyRemoteKingRenderShell()
        {
            for (var slot = 0; slot < clients.Length; slot++)
            {
                if (clients[slot] != null)
                    return true;
            }

            return false;
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

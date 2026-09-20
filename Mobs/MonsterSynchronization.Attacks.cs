using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
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
        private static void TrySendHostMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, Entity? explicitTarget = null)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            var net = LobbySession.NetRef;
            if (!IsHost(net))
                return;

            if (!IsSyncMob(mob))
                return;

            if (!TryGetMobSyncId(mob, out var mobSyncId))
                return;
            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return;

            var targetEntity = ResolveMobAttackTargetEntity(mob, explicitTarget);

            var targetUserId = ResolveHostTargetUserId(targetEntity, net!.id);

            var x = GetWorldX(mob);
            var y = GetWorldY(mob);
            var dir = NormalizeDir(mob.dir);
            var encodedSkill = Uri.EscapeDataString(skillId);
            var reqTarget = requiresTargetInArea ? 1 : 0;
            var dataVal = data ?? 0;
            // Phase 3: per-boss monotonic attack sequence (0 for non-bosses) so the client can drop
            // replayed / out-of-order boss attacks deterministically via a high-water mark.
            var attackSeq = NextHostBossAttackSeq(mob);
            var attackEvent = $"attack|{encodedSkill}|0|0|{reqTarget}|{dataVal}|{targetUserId}|{dir}|{attackSeq}";
            var mobType = BuildMobStateTypeSignature(mob);
            var update = new NetNode.MobEventUpdate(mobSyncId, x, y, dir, SingleEvent(attackEvent), mobType, identityToken);
            MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(update));
            net.SendMobEvents(SingleUpdate(update));
            
            // For bosses, force a state sync immediately after attack to sync spawned entities
            if (BossSyncHelpers.IsBossMob(mob))
            {
                lock (Sync)
                {
                    EnqueueHostMobDirtyLocked(mobSyncId, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                }
            }
        }

        private void Hook_Mob_setAttackTarget(Hook_Mob.orig_setAttackTarget orig, Mob self, Entity e)
        {
            // Never substitute another target from inside vanilla's target setter. Elite skills and
            // normal AI deliberately clear/swap targets while changing state; replacing null or an
            // invalid target here can leave the behavior tree waiting forever on the old phase.
            orig(self, e);
        }

        private void Hook_Mob_setNemesisTarget(Hook_Mob.orig_setNemesisTarget orig, Mob self, Entity e)
        {
            // Keep vanilla target-container transitions intact. Co-op may repair only the immediate
            // attack target later, after the mob's own update has completed.
            orig(self, e);
        }

        private static bool TryResolveFallbackPlayerCombatTarget(Mob? mob, Entity? currentTarget, out Entity fallbackTarget)
        {
            fallbackTarget = null!;
            if (mob == null)
                return false;
            if (!IsMobHostileToPlayers(mob))
                return false;
            if (currentTarget == null)
            {
                if (TryGetCurrentHostAttackTarget(mob, out _))
                    return false;

                if (TryGetCurrentHostNemesisTarget(mob, out var livingNemesisTarget))
                {
                    fallbackTarget = livingNemesisTarget;
                    return true;
                }

                return false;
            }
            else if (!IsInvalidPlayerTargetEntity(currentTarget))
                return false;

            if (TryGetAlternateCurrentHostCombatTarget(mob, currentTarget, out var existingTarget))
            {
                fallbackTarget = existingTarget;
                return true;
            }

            if (TryResolveDetectedHostCombatTarget(mob, out var detectedTarget))
            {
                fallbackTarget = detectedTarget;
                return true;
            }

            return false;
        }

        private static bool TryResolveSafeBossNemesisTarget(Mob? mob, Entity? requestedTarget, out Entity safeTarget)
        {
            safeTarget = null!;

            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return false;

            if (requestedTarget is Hero heroTarget)
            {
                safeTarget = heroTarget;
                return true;
            }

            try
            {
                var currentHeroTarget = mob.nemesisTarget as Hero;
                if (currentHeroTarget != null &&
                    !currentHeroTarget.destroyed &&
                    currentHeroTarget.life > 0 &&
                    !ModEntry.IsEntityDownedForCombat(currentHeroTarget))
                {
                    safeTarget = currentHeroTarget;
                    return true;
                }
            }
            catch
            {
            }

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null)
            {
                try
                {
                    if (!localHero.destroyed &&
                        localHero.life > 0 &&
                        !ModEntry.IsEntityDownedForCombat(localHero))
                    {
                        safeTarget = localHero;
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TrySplitStateTypeSignature(string? rawValue, out string typeId, out string runtimeClass)
        {
            typeId = string.Empty;
            runtimeClass = string.Empty;

            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            var value = rawValue.Trim();
            var pipeIndex = value.IndexOf('|');
            if (pipeIndex < 0)
                return false;

            if (pipeIndex > 0)
                typeId = NormalizeMobTypeKey(value[..pipeIndex]);

            if (pipeIndex + 1 < value.Length)
                runtimeClass = NormalizeMobTypeKey(value[(pipeIndex + 1)..]);

            return !string.IsNullOrWhiteSpace(typeId) || !string.IsNullOrWhiteSpace(runtimeClass);
        }

        private static string GetMobTypeIdSafe(Mob? mob)
        {
            if (mob == null)
                return string.Empty;

            try
            {
                return NormalizeMobTypeKey(mob.type?.ToString());
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetMobRuntimeClassKeySafe(Mob? mob)
        {
            if (mob == null)
                return string.Empty;

            try
            {
                var runtimeType = mob.GetType();
                if (runtimeType == null)
                    return string.Empty;

                return NormalizeMobTypeKey(runtimeType.FullName ?? runtimeType.Name);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeMobTypeKey(string? rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType))
                return string.Empty;

            var value = rawType.Trim();

            var slash = value.LastIndexOf('/');
            var dot = value.LastIndexOf('.');
            var colon = value.LastIndexOf(':');
            var separator = System.Math.Max(System.Math.Max(slash, dot), colon);
            if (separator >= 0 && separator + 1 < value.Length)
                value = value[(separator + 1)..];

            return value.Trim();
        }

        private static bool IsClientNetworkAttackActive(Mob? mob)
        {
            if (mob == null)
                return false;

            lock (Sync)
            {
                return clientActiveNetworkAttackMobs.Contains(mob);
            }
        }

        private static void MarkClientNetworkAttackActive(Mob mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientActiveNetworkAttackMobs.Add(mob);
                clientNetworkAttackStartFrame[mob] = GetCurrentFrame(mob);
            }

            TryUnlockClientMobAiAuthority(mob);
        }

        private static void MarkClientBossSkillCallbackLease(Mob mob)
        {
            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return;

            lock (Sync)
            {
                clientBossSkillCallbackLeaseMobs.Add(mob);
            }

            MarkClientNetworkAttackActive(mob);
        }

        private static bool IsClientBossSkillCallbackLeaseActive(Mob? mob)
        {
            if (mob == null)
                return false;

            lock (Sync)
            {
                return clientBossSkillCallbackLeaseMobs.Contains(mob);
            }
        }

        private static double GetCurrentFrame(Mob? mob)
        {
            try
            {
                var level = mob?._level ?? currentLevel;
                if (level != null)
                    return level.ftime;
            }
            catch
            {
            }

            return 0.0;
        }

        private static void RefreshClientNetworkAttackState(Mob mob)
        {
            if (mob == null || !IsClientNetworkAttackActive(mob))
                return;

            var queuedOrCharging = HasLocalQueuedOrChargingSkill(mob);
            var preserveMotion = ShouldPreserveClientAttackMotion(mob);
            var isBoss = BossSyncHelpers.IsBossMob(mob);
            var elapsed = 0.0;
            lock (Sync)
            {
                if (clientNetworkAttackStartFrame.TryGetValue(mob, out var startFrame))
                    elapsed = GetCurrentFrame(mob) - startFrame;
            }

            if (queuedOrCharging && (!isBoss || elapsed < ClientBossVisualAttackMaxActiveFrames))
                return;
            if (preserveMotion && (!isBoss || elapsed < ClientBossVisualAttackMaxActiveFrames))
                return;

            lock (Sync)
            {
                if (clientNetworkAttackStartFrame.TryGetValue(mob, out var startFrame))
                {
                    var lockedElapsed = GetCurrentFrame(mob) - startFrame;
                    if (lockedElapsed < ClientNetworkAttackMinActiveFrames)
                        return;
                }

                clientActiveNetworkAttackMobs.Remove(mob);
                clientBossSkillCallbackLeaseMobs.Remove(mob);
                clientNetworkAttackStartFrame.Remove(mob);
            }

            if (isBoss)
            {
                // Expiring the lease re-locks the brain but does NOT stop an already-running
                // looping action — Conjunctivius kept firing poison orbs long after the host had
                // moved on. Best-effort interrupt of the stale skill/action.
                // ONLY while the boss is alive: the final attack's lease expires seconds after
                // the killing blow, and interrupting then breaks the native death sequence the
                // victory cinematic is waiting on (Concierge froze in letterbox + paused timer).
                bool aliveForInterrupt;
                try { aliveForInterrupt = !mob.destroyed && mob.life > 0; }
                catch { aliveForInterrupt = false; }
                if (aliveForInterrupt)
                    Bosses.BossReflection.TryInterruptMobSkills(mob);
            }
        }

        private static void UpdateClientMobAiAuthority(Mob mob)
        {
            // Let the cine script drive the boss during a locally active boss-intro cinematic;
            // locking resumes automatically on the first update after the cine ends.
            if (ModEntry.IsLocalBossIntroCineActive() && BossSyncHelpers.IsBossMob(mob))
                return;

            if (mob == null)
                return;

            RefreshClientNetworkAttackState(mob);
            var queuedOrCharging = HasLocalQueuedOrChargingSkill(mob);
            var networkAttackActive = IsClientNetworkAttackActive(mob);

            if (queuedOrCharging)
            {
                // A host-selected native skill may need the brain unlocked while it is being
                // queued/charged. Once the action is running, lock the decision-making brain again:
                // the native action/physics can finish, but the replica cannot independently pick a
                // second target/skill and diverge from the host during an online-latency window.
                TryUnlockClientMobAiAuthority(mob);
                TryRepairClientMobAttackTarget(mob);
                return;
            }

            TryLockClientMobAiAuthority(mob);
            if (networkAttackActive)
                TryRepairClientMobAttackTarget(mob);
        }

        /// <summary>
        /// Host HP thresholds can start transformations or scripted dialogue without a separate
        /// attack packet. Give the client boss a short bounded presentation lease so those native
        /// callbacks can run; host transform, HP, targeting and death remain authoritative.
        /// </summary>
        private static void MarkClientBossPresentationLease(Mob mob)
        {
            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return;

            MarkClientNetworkAttackActive(mob);
        }

        private static void TryRepairClientMobAttackTarget(Mob mob)
        {
            if (mob == null)
                return;
            if (!IsMobHostileToPlayers(mob))
                return;

            TryClearClientMobInvalidPlayerTargets(mob);
            if (TryGetCurrentClientAttackTarget(mob, out _))
                return;

            var detected = ResolveDetectedClientTargetEntity(mob);
            if (detected == null)
                return;

            try
            {
                if (!ReferenceEquals(mob.aTarget, detected))
                    mob.setAttackTarget(detected);
            }
            catch { }
        }

        private static bool TryClearClientMobInvalidPlayerTargets(Mob mob)
        {
            if (mob == null)
                return false;

            var cleared = false;

            try
            {
                var at = mob.aTarget;
                if (at != null && IsKnownPlayerEntity(at) && !IsPreservablePlayerCombatTargetForMob(mob, at))
                {
                    mob.setAttackTarget(null);
                    cleared = true;
                }
            }
            catch { }

            try
            {
                var nt = mob.nemesisTarget;
                if (nt != null && IsKnownPlayerEntity(nt) && !IsPreservablePlayerCombatTargetForMob(mob, nt))
                {
                    mob.setNemesisTarget(null);
                    cleared = true;
                }
            }
            catch { }

            return cleared;
        }

        private static void TryLockClientMobAiAuthority(Mob mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                if (!clientAiLockedMobs.Add(mob))
                    return;
            }

            try
            {
                mob.lockAiS(ClientAiAuthorityLockDurationSeconds);
            }
            catch
            {
            }
        }

        private static void TryUnlockClientMobAiAuthority(Mob mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                if (!clientAiLockedMobs.Remove(mob))
                    return;
            }

            try
            {
                mob.unlockAi();
            }
            catch
            {
            }
        }

        private static void TryAssignHostAttackTarget(Mob mob)
        {
            if (mob == null || !IsMobHostileToPlayers(mob))
                return;

            RefreshHostContactAttackState(mob);
            if (TryGetCurrentHostAttackTarget(mob, out var existingTarget))
            {
                // Retention is deliberately MUCH more permissive than acquisition. Testing the
                // current target against the acquire gate re-selected every single frame the target
                // sat outside the facing cone — which is most frames during a real fight — so mobs
                // flipped between players, turned around mid-approach and swung at nothing.
                // Vanilla does not drop aggro because an enemy turned its head; neither do we.
                if (existingTarget == null ||
                    IsPlayerCombatTargetStillRelevant(mob, existingTarget))
                {
                    return;
                }
            }

            // Let vanilla finish elite teleports, charges, stuns and scripted locks. Co-op only fills
            // an actually missing immediate attack target; it never unlocks AI or rewrites nemesis.
            if (HasLocalQueuedOrChargingSkill(mob))
                return;
            try
            {
                if (mob.aiLocked())
                    return;
            }
            catch
            {
                return;
            }

            if (!TryResolveDetectedHostCombatTarget(mob, out var selected) || selected == null)
                return;

            try
            {
                if (ReferenceEquals(mob.aTarget, selected))
                    return;

                // Hard backstop against oscillation. Even if some other path decides a mob should
                // reconsider, it cannot actually switch players more often than this. Without it a
                // single mis-scoped check flips every hostile mob every frame, which reads as
                // enemies running the wrong way and attacking empty air.
                if (!TryBeginHostTargetSwitch(mob))
                    return;

                mob.setAttackTarget(selected);
            }
            catch
            {
            }
        }

        /// <summary>Minimum frames a mob must keep a player target before it may switch again.</summary>
        private const double HostTargetSwitchCooldownFrames = 45.0;

        private static readonly ConditionalWeakTable<Mob, StrongBox<double>> s_hostLastTargetSwitchFrame = new();

        private static bool TryBeginHostTargetSwitch(Mob mob)
        {
            try
            {
                var now = GetCurrentFrame(mob);
                if (!double.IsFinite(now))
                    return true;

                if (s_hostLastTargetSwitchFrame.TryGetValue(mob, out var last) &&
                    last != null &&
                    now - last.Value < HostTargetSwitchCooldownFrames &&
                    now >= last.Value)
                {
                    return false;
                }

                s_hostLastTargetSwitchFrame.Remove(mob);
                s_hostLastTargetSwitchFrame.Add(mob, new StrongBox<double>(now));
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static bool HasValidLivingPlayerCombatTarget(Mob mob)
        {
            if (mob == null)
                return false;

            if (TryGetCurrentHostAttackTarget(mob, out _))
                return true;
            if (TryGetCurrentHostNemesisTarget(mob, out _))
                return true;

            return false;
        }

        private static bool TryGetCurrentHostAttackTarget(Mob mob, out Entity target)
        {
            target = null!;
            if (mob == null)
                return false;

            try
            {
                var attackTarget = mob.aTarget;
                if (attackTarget != null && IsPreservablePlayerCombatTargetForMob(mob, attackTarget))
                {
                    target = attackTarget;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryGetCurrentHostNemesisTarget(Mob mob, out Entity target)
        {
            target = null!;
            if (mob == null)
                return false;

            try
            {
                var nemesisTarget = mob.nemesisTarget;
                if (nemesisTarget != null && IsPreservablePlayerCombatTargetForMob(mob, nemesisTarget))
                {
                    target = nemesisTarget;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryGetAlternateCurrentHostCombatTarget(Mob mob, Entity? excludedTarget, out Entity target)
        {
            target = null!;
            if (mob == null)
                return false;

            if (TryGetCurrentHostAttackTarget(mob, out var attackTarget) &&
                !ReferenceEquals(attackTarget, excludedTarget))
            {
                target = attackTarget;
                return true;
            }

            if (TryGetCurrentHostNemesisTarget(mob, out var nemesisTarget) &&
                !ReferenceEquals(nemesisTarget, excludedTarget))
            {
                target = nemesisTarget;
                return true;
            }

            return false;
        }

        private static bool TryRepairHostAttackTargetFromCurrentState(Mob mob)
        {
            if (mob == null)
                return false;

            if (!TryGetCurrentHostNemesisTarget(mob, out var livingNemesisTarget))
                return false;

            try
            {
                if (!ReferenceEquals(mob.aTarget, livingNemesisTarget))
                    mob.setAttackTarget(livingNemesisTarget);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldSuppressHostRetarget(Mob mob)
        {
            if (mob == null)
                return false;

            if (HasLocalQueuedOrChargingSkill(mob))
                return true;

            try
            {
                return mob.aiLocked();
            }
            catch
            {
                return false;
            }
        }

        private static bool TryClearHostMobInvalidPlayerTargets(Mob mob)
        {
            if (mob == null)
                return false;

            var cleared = false;

            try
            {
                var at = mob.aTarget;
                if (at != null && IsKnownPlayerEntity(at) && !IsPreservablePlayerCombatTargetForMob(mob, at))
                {
                    mob.setAttackTarget(null);
                    cleared = true;
                }
            }
            catch
            {
            }

            try
            {
                var nt = mob.nemesisTarget;
                if (nt != null && IsKnownPlayerEntity(nt) && !IsPreservablePlayerCombatTargetForMob(mob, nt))
                {
                    mob.setNemesisTarget(null);
                    cleared = true;
                }
            }
            catch
            {
            }

            return cleared;
        }

        private static void TryCollectDetectedTarget(Mob mob, Entity? candidate)
        {
            if (candidate == null)
                return;
            if (ReferenceEquals(candidate, mob))
                return;
            if (!IsAcquirablePlayerCombatTargetForMob(mob, candidate, requireDetectArea: true))
                return;

            try
            {
                if (!DoesLevelMatchCurrentIdentityLocked(mob._level))
                    return;
                if (!DoesLevelMatchCurrentIdentityLocked(candidate._level))
                    return;
            }
            catch
            {
                return;
            }

            if (!hostDetectedTargets.Contains(candidate))
                hostDetectedTargets.Add(candidate);
        }

        private static void RefreshHostContactAttackState(Mob mob)
        {
            if (mob == null)
                return;

            var currentTargetUserId = ResolveHostTargetUserId(ResolveCurrentHostPlayerCombatTarget(mob), LobbySession.NetRef?.id ?? 0);
            lock (Sync)
            {
                if (currentTargetUserId <= 0)
                {
                    hostLastSentContactTargetUserIdByMob.Remove(mob);
                    return;
                }

                if (!hostLastSentContactTargetUserIdByMob.TryGetValue(mob, out var sentTargetUserId))
                    return;

                if (sentTargetUserId != currentTargetUserId)
                    hostLastSentContactTargetUserIdByMob.Remove(mob);
            }
        }

        private static Entity? ResolveCurrentHostPlayerCombatTarget(Mob mob)
        {
            if (mob == null)
                return null;

            if (TryGetCurrentHostAttackTarget(mob, out var attackTarget))
                return attackTarget;
            if (TryGetCurrentHostNemesisTarget(mob, out var nemesisTarget))
                return nemesisTarget;

            return null;
        }

        private static bool IsMobHostileToPlayers(Mob? mob)
        {
            if (mob == null)
                return false;

            try
            {
                var level = mob._level;
                var mobTeam = mob._team;
                if (level == null || mobTeam == null)
                    return false;

                return ReferenceEquals(mobTeam, level.teamMob);
            }
            catch
            {
                return false;
            }
        }

        private static int ResolveHostTargetUserId(Entity? target, int localUserId)
        {
            if (target == null || localUserId <= 0)
                return 0;
            if (ModEntry.IsEntityDownedForCombat(target))
                return 0;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(target, localHero))
                return localUserId;

            var gameHero = ModCore.Modules.Game.Instance?.HeroInstance;
            if (gameHero != null && ReferenceEquals(target, gameHero))
                return localUserId;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var clientId = ModEntry.clientIds[i];
                var client = ModEntry.clients[i];
                if (clientId <= 0 || client == null)
                    continue;

                if (ReferenceEquals(target, client))
                    return clientId;
            }

            return 0;
        }

        private static Entity? ResolveHostPlayerCombatEntity(int userId)
        {
            var net = LobbySession.NetRef;
            if (!IsHost(net) || userId <= 0)
                return null;

            var localId = net!.id;
            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (userId == localId)
                return localHero != null && IsPreservablePlayerCombatTargetEntity(localHero) ? localHero : null;

            if (!ModEntry.TryGetClientIndex(localId, userId, out var index))
                return null;

            var client = ModEntry.clients[index];
            return client != null && IsPreservablePlayerCombatTargetEntity(client) ? client : null;
        }

        private static void TryApplyHostMobHitCombatRefresh(Mob mob, int attackerUserId, int previousLife, int currentLife, bool replaySpecialHit)
        {
            if (mob == null || attackerUserId <= 0 || currentLife <= 0)
                return;

            // Threat refresh can interruptSkills mid-charge when aTarget is invalid, stranding the
            // host mob with no attack/move until a full reset. Skip while a skill is in flight.
            if (HasLocalQueuedOrChargingSkill(mob))
                return;

            var attacker = ResolveHostPlayerCombatEntity(attackerUserId);
            if (attacker == null || !IsPreservablePlayerCombatTargetForMob(mob, attacker))
                return;

            // A detached remote KingSkin is safe as an attack target but is not a safe key for all
            // vanilla threat/elite state containers. More importantly, no hit callback may rewrite
            // targets while either player is downed: that exact transition made mobs stop after the
            // survivor's first hit. The normal host update repairs a missing aTarget afterward.
            if (attacker is KingSkin || ModEntry.HasAnyPlayerDownedForCombat())
                return;

            var threatDelta = System.Math.Max(0, previousLife - currentLife);
            try
            {
                if (threatDelta > 0)
                    mob.addThreat(attacker, threatDelta, HaxeProxy.Runtime.Ref<double>.Null);
                else if (!replaySpecialHit)
                    return;

                mob.updateThreat();
            }
            catch
            {
                // Threat refresh is optional. Never fall back to force-setting attack/nemesis state
                // from inside damage application; vanilla will reacquire on its next update.
            }
        }

        private static bool TryResolveDetectedHostCombatTarget(Mob mob, out Entity selected)
        {
            selected = null!;
            if (mob == null)
                return false;

            lock (Sync)
            {
                hostDetectedTargets.Clear();
                try
                {
                    TryCollectDetectedTarget(mob, ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance);

                    for (int i = 0; i < ModEntry.clients.Length; i++)
                    {
                        if (ModEntry.clientIds[i] <= 0)
                            continue;

                        TryCollectDetectedTarget(mob, ModEntry.clients[i]);
                    }

                    if (hostDetectedTargets.Count == 0)
                        return false;

                    try
                    {
                        var currentNemesis = mob.nemesisTarget;
                        if (currentNemesis != null && hostDetectedTargets.Contains(currentNemesis))
                        {
                            selected = currentNemesis;
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        var currentTarget = mob.aTarget;
                        if (currentTarget != null && hostDetectedTargets.Contains(currentTarget))
                        {
                            selected = currentTarget;
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    var mx = GetWorldX(mob);
                    var my = GetWorldY(mob);
                    var bestDistSq = double.MaxValue;

                    for (int i = 0; i < hostDetectedTargets.Count; i++)
                    {
                        var candidate = hostDetectedTargets[i];
                        if (candidate == null)
                            continue;

                        var dx = GetWorldX(candidate) - mx;
                        var dy = GetWorldY(candidate) - my;
                        var distSq = dx * dx + dy * dy;
                        if (distSq < bestDistSq)
                        {
                            bestDistSq = distSq;
                            selected = candidate;
                        }
                    }

                    return selected != null;
                }
                finally
                {
                    hostDetectedTargets.Clear();
                }
            }
        }

        private static Entity? ResolveMobAttackTargetEntity(Mob mob, Entity? explicitTarget)
        {
            if (explicitTarget != null && IsPreservablePlayerCombatTargetForMob(mob, explicitTarget))
                return explicitTarget;

            try
            {
                if (mob.aTarget != null && IsPreservablePlayerCombatTargetForMob(mob, mob.aTarget))
                    return mob.aTarget;
            }
            catch
            {
            }

            try
            {
                if (mob.nemesisTarget != null && IsPreservablePlayerCombatTargetForMob(mob, mob.nemesisTarget))
                    return mob.nemesisTarget;
            }
            catch
            {
            }

            if (TryResolveDetectedHostCombatTarget(mob, out var detectedTarget))
                return detectedTarget;

            return null;
        }

        private static bool IsEntityOnCurrentCombatIdentity(Entity? entity)
        {
            if (entity == null)
                return false;

            try
            {
                lock (Sync)
                {
                    return DoesLevelMatchCurrentIdentityLocked(entity._level);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPreservablePlayerCombatTargetEntity(Entity entity)
        {
            if (entity == null)
                return false;
            if (IsCorpseLikeCombatTargetEntity(entity))
                return false;
            if (!IsKnownPlayerEntity(entity))
                return false;
            if (IsHardInvalidPlayerTargetEntity(entity))
                return false;

            return true;
        }

        private static bool IsInvalidPlayerTargetEntity(Entity? entity)
        {
            return IsHardInvalidPlayerTargetEntity(entity);
        }

        private static bool IsPreservablePlayerCombatTargetForMob(Mob mob, Entity entity)
        {
            if (mob == null || entity == null)
                return false;
            if (!IsPreservablePlayerCombatTargetEntity(entity))
                return false;

            try
            {
                if (!mob.isOpponent(entity))
                    return false;
            }
            catch
            {
                return false;
            }

            // Preserve a living opponent even when it is temporarily unhittable (roll i-frames,
            // shield/parry windows, revive protection, etc.). canBeHitBy is an acquisition/attack
            // check, not a reason to erase the mob's long-lived target; clearing it here is what made
            // mobs lose the surviving player immediately after that player attacked while a teammate
            // was downed.
            return true;
        }

        private static void TraceTargetAcquireRejected(Mob mob, Entity entity, string reason)
        {
            if (!MobSyncTrace.Enabled)
                return;

            MobSyncTrace.LogTargetAcquire(
                GetMobRuntimeClassKeySafe(mob),
                IsRemotePlayerCombatShell(entity),
                reason);
        }

        /// <summary>
        /// True when this entity is a remote player's networked shell rather than the local Hero.
        /// </summary>
        private static bool IsRemotePlayerCombatShell(Entity? entity)
        {
            if (entity == null)
                return false;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(entity, localHero))
                return false;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null && ReferenceEquals(entity, client))
                    return true;
            }

            return false;
        }

        private static bool IsAcquirablePlayerCombatTargetForMob(Mob mob, Entity entity, bool requireDetectArea = false)
        {
            if (!IsPreservablePlayerCombatTargetForMob(mob, entity))
                return false;

            // The remote player is a GhostKing (KingSkin), not a Hero. canBeDetected/canBeHitBy are
            // Hero-shaped vanilla checks — canBeHitBy is hooked for Hero only, and canBeDetected is
            // not hooked at all — so asking them about a KingSkin can reject a perfectly valid,
            // living target and leave the second player permanently un-aggroed. Use the mod's own
            // liveness rules for the shell instead; the detect-area test below still applies.
            var remoteShell = IsRemotePlayerCombatShell(entity);
            if (remoteShell)
            {
                if (IsHardInvalidPlayerTargetEntity(entity))
                {
                    TraceTargetAcquireRejected(mob, entity, "remote_shell_invalid");
                    return false;
                }
            }
            else
            {
                try
                {
                    if (!entity.canBeDetected())
                    {
                        TraceTargetAcquireRejected(mob, entity, "canBeDetected");
                        return false;
                    }
                    if (!entity.canBeHitBy(mob))
                    {
                        TraceTargetAcquireRejected(mob, entity, "canBeHitBy");
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }

            if (!requireDetectArea)
                return true;

            try
            {
                if (mob.inDetectArea(entity))
                    return true;
            }
            catch
            {
                return false;
            }

            // inDetectArea is a facing-dependent cone, so it alone can never see a player standing
            // behind a mob. Vanilla still aggros the LOCAL hero from behind because the game has
            // other acquisition routes — noise, threat, ambient wake — that only ever consider
            // game.hero. The remote player has no such routes: this gate is its only way in, so a
            // cone-only test made the second player permanently sneak-proof.
            //
            // Close proximity stands in for those missing routes. Kept deliberately tight, and
            // tighter vertically than horizontally, so it approximates "same platform, right next
            // to me" rather than aggro through floors or across a room.
            if (remoteShell && IsRemotePlayerWithinProximityAggro(mob, entity))
                return true;

            TraceTargetAcquireRejected(mob, entity, "inDetectArea");
            return false;
        }

        /// <summary>Horizontal reach of the remote-player proximity fallback (~6 tiles).</summary>
        private const double RemotePlayerProximityAggroRangeXPx = 24.0 * 6.0;

        /// <summary>Vertical reach, kept short so mobs do not notice players through floors.</summary>
        private const double RemotePlayerProximityAggroRangeYPx = 24.0 * 2.5;

        /// <summary>
        /// Retention envelope. Wider than the acquire ranges on purpose: the gap between acquiring
        /// and losing a target is what stops a mob oscillating between two players standing at
        /// similar distances.
        /// </summary>
        private const double PlayerTargetRetentionRangeXPx = 24.0 * 14.0;

        private const double PlayerTargetRetentionRangeYPx = 24.0 * 6.0;

        private static bool IsWithinRangeBox(Mob mob, Entity entity, double rangeX, double rangeY)
        {
            try
            {
                var dx = GetWorldX(entity) - GetWorldX(mob);
                var dy = GetWorldY(entity) - GetWorldY(mob);
                if (!double.IsFinite(dx) || !double.IsFinite(dy))
                    return false;

                return System.Math.Abs(dx) <= rangeX && System.Math.Abs(dy) <= rangeY;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether a mob should keep fighting its current player target. Only a target that is
        /// genuinely gone — dead, downed, off-level, or well outside the retention box — releases
        /// the mob to re-select.
        /// </summary>
        private static bool IsPlayerCombatTargetStillRelevant(Mob mob, Entity entity)
        {
            if (IsHardInvalidPlayerTargetEntity(entity))
                return false;

            try
            {
                if (mob.inDetectArea(entity))
                    return true;
            }
            catch
            {
                // If the game cannot answer, keep the existing target rather than thrash.
                return true;
            }

            return IsWithinRangeBox(mob, entity, PlayerTargetRetentionRangeXPx, PlayerTargetRetentionRangeYPx);
        }

        private static bool IsRemotePlayerWithinProximityAggro(Mob mob, Entity entity)
        {
            return IsWithinRangeBox(mob, entity, RemotePlayerProximityAggroRangeXPx, RemotePlayerProximityAggroRangeYPx);
        }

        private static bool IsHardInvalidPlayerTargetEntity(Entity? entity)
        {
            var safeEntity = entity;
            if (safeEntity == null)
                return false;
            if (IsCorpseLikeCombatTargetEntity(safeEntity))
                return true;
            if (!IsKnownPlayerEntity(safeEntity))
                return false;
            if (ModEntry.IsEntityDownedForCombat(safeEntity))
                return true;
            if (!IsEntityOnCurrentCombatIdentity(safeEntity))
                return true;

            try
            {
                return safeEntity.destroyed || safeEntity.life <= 0 || !safeEntity._targetable;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsCorpseLikeCombatTargetEntity(Entity? entity)
        {
            return entity is HeroDeadCorpse || entity is dc.en.deco.DeadCorpse;
        }


        private static bool IsKnownPlayerEntity(Entity? entity)
        {
            if (entity == null)
                return false;

            if (entity is Hero || entity is KingSkin)
                return true;

            var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(entity, localHero))
                return true;

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null && ReferenceEquals(entity, client))
                    return true;
            }

            return false;
        }

    }
}

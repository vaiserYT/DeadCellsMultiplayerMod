using System.Globalization;
using dc;
using dc.en;
using dc.hl.types;
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

        private static void ConsumeIncomingHostMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            try
            {
                MobSyncTrace.LogRecvStates("hostStatesFromHost", states);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingHostMobStates(states);
            }
            finally
            {
                NetNode.ReleaseConsumedList(states);
            }
        }

        private static void ConsumeIncomingHostMobMoves(NetNode net)
        {
            if (!net.TryConsumeMobMoves(out var moves))
                return;

            try
            {
                MobSyncTrace.LogRecvMoves("hostMovesFromHost", moves);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingHostMobMoves(moves);
            }
            finally
            {
                NetNode.ReleaseConsumedList(moves);
            }
        }

        private static void ConsumeIncomingClientMobStates(NetNode net)
        {
            if (!net.TryConsumeMobStates(out var states))
                return;

            try
            {
                MobSyncTrace.LogRecvStates("clientAffectFromClient", states);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingClientMobStatesOnHost(states);
            }
            finally
            {
                NetNode.ReleaseConsumedList(states);
            }
        }

        private static void ApplyIncomingClientMobStatesOnHost(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            s_clientAffectAppliesScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    if (!ShouldAcceptPacketGenerationLocked(state.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    var mob = ResolveMobBySyncIdLocked(state.Index);
                    if (mob == null)
                        continue;
                    if (string.IsNullOrEmpty(state.StatePayload))
                        continue;

                    s_clientAffectAppliesScratch.Add(new PendingClientAffectApply(state.Index, mob, state.StatePayload));
                }
            }

            LogRejectedPacketGeneration("clientStateOnHost", rejectedCount, rejectedGeneration);

            for (int i = 0; i < s_clientAffectAppliesScratch.Count; i++)
            {
                var entry = s_clientAffectAppliesScratch[i];
                ApplyClientReportedAffectStateOnHost(entry.SyncId, entry.Mob, entry.StatePayload);
            }

            s_clientAffectAppliesScratch.Clear();
        }

        private static void ApplyClientReportedAffectStateOnHost(int mobSyncId, Mob mob, string? wirePayload)
        {
            if (mob == null || mob.destroyed)
                return;
            if (BossSyncHelpers.IsBossMob(mob))
                return;

            if (!TryDecodeStatePayloadFromWire(wirePayload, out var payload))
                return;

            lock (Sync)
            {
                if (hostLastAppliedClientAffectPayloadBySyncId.TryGetValue(mobSyncId, out var lastApplied) &&
                    string.Equals(lastApplied, payload, StringComparison.Ordinal))
                {
                    return;
                }

                hostLastAppliedClientAffectPayloadBySyncId[mobSyncId] = payload;
            }

            ApplyExplicitAffectPayload(mob, payload);
        }

        private static void ApplyIncomingHostMobStates(IReadOnlyList<NetNode.MobStateSnapshot> states)
        {
            if (states == null || states.Count == 0)
                return;

            s_hostStateAppliesScratch.Clear();
            s_usedTrackedMobsScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();

                for (int i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    if (!ShouldAcceptPacketGenerationLocked(state.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    if (!string.IsNullOrWhiteSpace(state.Type))
                        hostMobTypeBySyncId[state.Index] = state.Type;

                    var mob = ResolveTrackedMobForIncomingStateLocked(state, s_usedTrackedMobsScratch);
                    if (mob == null)
                        continue;

                    s_usedTrackedMobsScratch.Add(mob);
                    var incomingDir = NormalizeDir(state.Dir);
                    clientLastReportedMobLife[mob] = state.Life;
                    clientAuthoritativeStateSeenSyncIds.Add(state.Index);
                    s_hostStateAppliesScratch.Add(new PendingHostStateApply(
                        state.Index,
                        mob,
                        state.Life,
                        state.MaxLife,
                        incomingDir,
                        state.StatePayload ?? string.Empty));

                    var mergedAnimPayload = state.AnimPayload ?? string.Empty;
                    var hasExplicitStatePayload = TryDecodeStatePayloadFromWire(state.StatePayload, out var mergedStatePayload);
                    if (clientMobTargets.TryGetValue(mob, out var previousTarget))
                    {
                        if (string.IsNullOrEmpty(mergedAnimPayload))
                            mergedAnimPayload = previousTarget.AnimPayload;
                        if (!hasExplicitStatePayload)
                            mergedStatePayload = previousTarget.StatePayload;
                    }

                    clientMobTargets[mob] = new ClientMobState(
                        state.X,
                        state.Y,
                        incomingDir,
                        state.Life,
                        state.MaxLife,
                        mergedAnimPayload,
                        mergedStatePayload,
                        state.Time,
                        state.Dx,
                        state.Dy);
                }
            }

            LogRejectedPacketGeneration("hostStateOnClient", rejectedCount, rejectedGeneration);

            s_usedTrackedMobsScratch.Clear();

            for (int i = 0; i < s_hostStateAppliesScratch.Count; i++)
            {
                var entry = s_hostStateAppliesScratch[i];
                if (entry.Dir != 0)
                {
                    try { entry.Mob.dir = entry.Dir; } catch { }
                }
                ApplyAuthoritativeLifeState(entry.Mob, entry.Life, entry.MaxLife);
                ApplyAuthoritativeAffectState(entry.SyncId, entry.Mob, entry.StatePayload);
            }

            s_hostStateAppliesScratch.Clear();
        }

        private static void ApplyIncomingHostMobMoves(IReadOnlyList<NetNode.MobMoveSnapshot> moves)
        {
            if (moves == null || moves.Count == 0)
                return;

            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();

                for (int i = 0; i < moves.Count; i++)
                {
                    var move = moves[i];
                    if (!ShouldAcceptPacketGenerationLocked(move.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    var mob = ResolveTrackedMobBySyncIdLocked(move.Index);
                    if (mob == null)
                        continue;

                    var mergedAnimPayload = move.AnimPayload ?? string.Empty;
                    if (clientMobTargets.TryGetValue(mob, out var previousTarget))
                    {
                        if (string.IsNullOrEmpty(mergedAnimPayload))
                            mergedAnimPayload = previousTarget.AnimPayload;

                        clientMobTargets[mob] = new ClientMobState(
                            move.X,
                            move.Y,
                            NormalizeDir(move.Dir),
                            previousTarget.Life,
                            previousTarget.MaxLife,
                            mergedAnimPayload,
                            previousTarget.StatePayload,
                            move.Time,
                            move.Dx,
                            move.Dy);
                    }
                }
            }

            LogRejectedPacketGeneration("hostMoveOnClient", rejectedCount, rejectedGeneration);
        }

        private static void ApplyAuthoritativeAffectState(int mobSyncId, Mob mob, string? wirePayload)
        {
            if (mob == null || mob.destroyed)
                return;

            if (!TryDecodeStatePayloadFromWire(wirePayload, out var safePayload))
                return;

            // Affects run vanilla calls (setAffectS etc.) on the mob; never do that on a mob
            // culled locally on a client (same .cx hazard class as culled deaths/attacks).
            // Checked BEFORE the dedupe cache so the payload re-applies once the mob wakes.
            if (!IsHost(GameMenu.NetRef) && IsMobCulledLocally(mob))
                return;

            lock (Sync)
            {
                if (clientLastAppliedHostAffectPayloadBySyncId.TryGetValue(mobSyncId, out var lastApplied) &&
                    string.Equals(lastApplied, safePayload, StringComparison.Ordinal))
                {
                    return;
                }

                clientLastAppliedHostAffectPayloadBySyncId[mobSyncId] = safePayload;
            }

            if (BossSyncHelpers.IsBossMob(mob))
            {
                BossStateSync.ApplyBossStateFromPayload(mob, safePayload);
                return;
            }

            ApplyExplicitAffectPayload(mob, safePayload);
            BossStateSync.ApplyBossStateFromPayload(mob, safePayload);
        }

        private static void ApplyExplicitAffectPayload(Mob mob, string payload)
        {
            if (mob == null || mob.destroyed)
                return;

            var desired = ParseAffectStatePayload(payload);
            PruneMissingSyncedAffects(mob, desired);

            foreach (var affectId in desired)
                ApplySyncedAffectPresence(mob, affectId);
        }

        private static void PruneMissingSyncedAffects(Mob mob, HashSet<int> desired)
        {
            if (mob == null || mob.destroyed)
                return;

            List<int>? staleAffectIds = null;
            try
            {
                var affects = mob.getAllAffects();
                if (affects == null || affects.length <= 0)
                    return;

                for (int i = 0; i < affects.length; i++)
                {
                    if (desired.Contains(i))
                        continue;
                    if (TryGetDynLength(affects.getDyn(i)) <= 0)
                        continue;

                    staleAffectIds ??= new List<int>();
                    staleAffectIds.Add(i);
                }
            }
            catch
            {
                return;
            }

            if (staleAffectIds == null)
                return;

            for (int i = 0; i < staleAffectIds.Count; i++)
            {
                try
                {
                    mob.removeAllAffects(staleAffectIds[i]);
                }
                catch
                {
                }
            }
        }

        private static void ApplySyncedAffectPresence(Mob mob, int affectId)
        {
            if (mob == null || mob.destroyed || affectId < 0)
                return;

            var hadAffect = false;

            try
            {
                hadAffect = mob.hasAffect(affectId);
            }
            catch
            {
            }

            if (!hadAffect)
            {
                try
                {
                    mob.setAffectS(affectId, AuthoritativeAffectPresenceSeconds, HaxeProxy.Runtime.Ref<double>.Null, null);
                }
                catch
                {
                }
            }
        }

        private static HashSet<int> ParseAffectStatePayload(string? payload)
        {
            var affects = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(payload))
                return affects;

            var decoded = payload!;
            try { decoded = Uri.UnescapeDataString(decoded); } catch { }
            if (string.IsNullOrWhiteSpace(decoded))
                return affects;

            var parts = decoded.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var token = parts[i]?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                var idPart = token;
                var separator = token.IndexOf(':');
                if (separator > 0)
                    idPart = token[..separator];

                if (!int.TryParse(idPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    continue;
                if (id < 0)
                    continue;

                affects.Add(id);
            }

            return affects;
        }

        private static int TryGetDynLength(object? dynArray)
        {
            if (dynArray is not ArrayObj ao)
                return 0;

            try
            {
                return ao.length;
            }
            catch
            {
                return 0;
            }
        }

        private static void ConsumeIncomingHostMobAttacks(NetNode net)
        {
            if (!net.TryConsumeMobAttacks(out var attacks))
                return;

            try
            {
                MobSyncTrace.LogRecvAttacks("hostAttacksFromHost", attacks);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingHostMobAttacks(attacks);
            }
            finally
            {
                NetNode.ReleaseConsumedList(attacks);
            }
        }

        private static void ApplyIncomingHostMobAttacks(IReadOnlyList<NetNode.MobAttack> attacks)
        {
            if (attacks == null || attacks.Count == 0)
                return;

            var rejectedGeneration = 0;
            var rejectedCount = 0;
            for (int i = 0; i < attacks.Count; i++)
            {
                var attack = attacks[i];
                Mob? mob = null;
                lock (Sync)
                {
                    if (!ShouldAcceptPacketGenerationLocked(attack.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;
                    mob = ResolveTrackedMobForIncomingAttackLocked(attack);
                }

                if (mob == null)
                    continue;

                TryQueueClientMobAttack(mob, attack.SkillId, attack.RequiresTargetInArea, attack.Data, attack.TargetUserId, attack.Dir);
            }

            LogRejectedPacketGeneration("hostAttackOnClient", rejectedCount, rejectedGeneration);
        }

        private static void ConsumeIncomingMobDraws(NetNode net)
        {
            if (!net.TryConsumeMobDraws(out var draws))
                return;

            try
            {
                MobSyncTrace.LogRecvDraws("clientDrawsFromClient", draws);
                if (IsSyncQuiescedForTransition())
                    return;
                ApplyIncomingMobDraws(draws);
            }
            finally
            {
                NetNode.ReleaseConsumedList(draws);
            }
        }

        private static void ApplyIncomingMobDraws(IReadOnlyList<NetNode.MobDraw> draws)
        {
            if (draws == null || draws.Count == 0)
                return;

            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < draws.Count; i++)
                {
                    var draw = draws[i];
                    if (!ShouldAcceptPacketGenerationLocked(draw.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    var mob = ResolveTrackedMobBySyncIdLocked(draw.MobIndex);
                    if (mob == null)
                        continue;

                    if (!IsSyncMob(mob))
                        continue;

                    TryApplyHostDrawRequestLocked(mob, draw);
                }
            }

            LogRejectedPacketGeneration("clientDrawOnHost", rejectedCount, rejectedGeneration);
        }

        private static void TryApplyHostDrawRequestLocked(Mob mob, NetNode.MobDraw draw)
        {
            if (mob == null)
                return;

            if (TryGetMobSyncId(mob, out var drawSyncId) && drawSyncId >= 0)
            {
                SetHostClientInterestLocked(drawSyncId, draw.UserId, !draw.IsOutOfGame);
                if (!draw.IsOutOfGame)
                {
                    EnqueueHostMobDirtyLocked(drawSyncId, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                    if (draw.IsOnScreen)
                        TryWakeMobForForcedSimulation(mob);
                }
            }
        }

        private static void TryQueueClientMobAttack(Mob mob, string skillId, bool requiresTargetInArea, int? data, int targetUserId, int attackDir)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            var intent = new ClientMobAttackIntent(skillId, requiresTargetInArea, data, targetUserId, attackDir);
            ProcessClientMobAttackIntent(mob, intent);
        }

        private static void ProcessClientMobAttackIntent(Mob mob, ClientMobAttackIntent intent)
        {
            if (mob == null || string.IsNullOrWhiteSpace(intent.SkillId))
                return;

            var skillId = intent.SkillId;
            var traceRoute = ResolveClientAttackRouteForTrace(skillId);
            _ = TryGetMobSyncId(mob, out var traceSyncId);

            // Never replay remote mob attacks on mobs that are culled locally: contact/skill
            // replays run vanilla combat code on a never-initialized mob (same hazard class as
            // the culled-death .cx fatal). A locally-culled mob is far from the local hero, so
            // the replay is off-screen and its target is out of reach here anyway; position/life
            // still sync via state snapshots.
            if (!IsHost(GameMenu.NetRef) && IsMobCulledLocally(mob))
            {
                MobSyncTrace.LogClientAttackRoute("skipped_culled_" + traceRoute, traceSyncId, skillId);
                return;
            }

            // Never replay teleport-class skills (e.g. Mage360 aggrTeleport). Their vanilla
            // implementation defers post-arrival logic that reads the target's grid coords
            // (.cx) frames after prepare() returns - outside any try/catch we can place - and
            // on replayed copies the target reference is not guaranteed across the wind-up.
            // Confirmed via trace: route=oldSkillPrepare skillId=@oldprep:aggrTeleport logged
            // immediately before the Null access .cx fatal. The teleport's position change
            // still arrives via move snapshots; only the local wind-up VFX is dropped.
            if (skillId.IndexOf("teleport", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MobSyncTrace.LogClientAttackRoute("skipped_teleport_" + traceRoute, traceSyncId, skillId);
                return;
            }

            MobSyncTrace.LogClientAttackRoute(traceRoute, traceSyncId, skillId);

            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
            {
                ProcessClientContactAttack(mob, intent);
                return;
            }

            if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillExecute(mob, skillId[OldSkillExecutePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(OldSkillPreparePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillPrepare(mob, skillId[OldSkillPreparePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(OldSkillChargeCompletePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientOldSkillExecute(mob, skillId[OldSkillChargeCompletePacketPrefix.Length..], intent);
                return;
            }

            if (skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
            {
                ProcessClientNewSkillExecute(mob, skillId[NewSkillExecutePacketPrefix.Length..], intent);
                return;
            }

            ProcessClientOldSkillQueue(mob, intent);
        }

        private static string ResolveClientAttackRouteForTrace(string skillId)
        {
            if (string.Equals(skillId, ContactAttackPacketSkillId, StringComparison.Ordinal))
                return "contact";

            if (skillId.StartsWith(OldSkillExecutePacketPrefix, StringComparison.Ordinal))
                return "oldSkillExecute";

            if (skillId.StartsWith(OldSkillPreparePacketPrefix, StringComparison.Ordinal))
                return "oldSkillPrepare";

            if (skillId.StartsWith(OldSkillChargeCompletePacketPrefix, StringComparison.Ordinal))
                return "oldSkillChargeComplete";

            if (skillId.StartsWith(NewSkillExecutePacketPrefix, StringComparison.Ordinal))
                return "newSkillExecute";

            return "oldSkillQueue";
        }

        private static void ProcessClientContactAttack(Mob mob, ClientMobAttackIntent intent)
        {
            TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
            TryWakeMobForForcedSimulation(mob);

            var target = ResolveClientAttackTargetEntity(mob, intent.TargetUserId);
            if (target == null)
                target = ResolveClientAttackTargetEntity(mob, 0);
            if (target == null)
                return;

            RegisterClientNetworkAttackExecuted(mob);

            try
            {
                mob.contactAttack(target);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client contactAttack failed for mob");
            }
        }

        private static void ProcessClientOldSkillExecute(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            if (ShouldSkipClientOldSkillExecuteFromMarker(mob, normalizedSkillId))
                return;

            try
            {
                var skillId = normalizedSkillId.AsHaxeString();
                if (!mob.hasOldSkill(skillId))
                    return;

                var oldSkill = mob.getOldSkill(skillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                if (TryGetChargingOldSkillId(mob, out var chargingOldSkillId))
                {
                    if (!string.Equals(chargingOldSkillId, normalizedSkillId, StringComparison.Ordinal))
                        return;
                }

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                    return;

                if (!TryGetChargingOldSkillId(mob, out _))
                {
                    if (oldSkill is OldMobSkill oldMobSkill && TryExecuteClientOldSkillNativeLike(oldMobSkill, intent.Data))
                    { }
                    else
                    {
                        oldSkill.prepare(intent.Data);
                    }
                }

                TryInvokeOldSkillChargeComplete(oldSkill);
                oldSkill.execute(null);
                lock (Sync)
                {
                    clientQueuedOldSkillMarkers.Remove(mob);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client oldSkill execute failed: {SkillId}", normalizedSkillId);
            }
        }

        private static void ProcessClientOldSkillPrepare(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            try
            {
                if (!TryGetMobOldSkill(mob, normalizedSkillId, out var oldSkill))
                    return;

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                    return;

                if (oldSkill is OldMobSkill oldMobSkill && TryExecuteClientOldSkillNativeLike(oldMobSkill, intent.Data))
                    return;

                if (!oldSkill.prepare(intent.Data))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client oldSkill prepare failed: {SkillId}", normalizedSkillId);
            }
        }

        private static void ProcessClientNewSkillExecute(Mob mob, string rawSkillId, ClientMobAttackIntent intent)
        {
            if (string.IsNullOrWhiteSpace(rawSkillId))
                return;

            var normalizedSkillId = rawSkillId.Trim();
            if (string.IsNullOrWhiteSpace(normalizedSkillId))
                return;

            try
            {
                if (TryGetChargingNewSkillId(mob, out var chargingNewSkillId))
                {
                    if (!string.Equals(chargingNewSkillId, normalizedSkillId, StringComparison.Ordinal))
                        return;

                    var chargingSkill = mob.getChargingNewSkill() as MobSkill;
                    if (chargingSkill == null)
                        return;

                    RegisterClientNetworkAttackExecuted(mob);
                    TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                    TryWakeMobForForcedSimulation(mob);
                    if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                        return;
                    chargingSkill.execute(null);
                    return;
                }

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);

                if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                    return;

                var skillId = normalizedSkillId.AsHaxeString();
                var skill = mob.getSkill(skillId) as MobSkill;
                if (skill == null)
                    return;

                skill.prepare(intent.Data);
                skill.execute(null);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client newSkill execute failed: {SkillId}", normalizedSkillId);
            }
        }

        private static void ProcessClientOldSkillQueue(Mob mob, ClientMobAttackIntent intent)
        {
            try
            {
                if (IsQueuedOrChargingOldSkillId(mob, intent.SkillId))
                    return;

                RegisterClientNetworkAttackExecuted(mob);
                TrySetClientMobAttackTarget(mob, intent.TargetUserId, intent.AttackDir, forceRetarget: true);
                TryWakeMobForForcedSimulation(mob);
                if (ResolveClientAttackTargetEntity(mob, intent.TargetUserId) == null)
                    TrySetClientMobAttackTarget(mob, 0, intent.AttackDir, forceRetarget: true);

                if (!TryResolveClientExecuteTargetEntity(mob, intent.TargetUserId, out _))
                    return;

                var haxeSkillId = intent.SkillId.AsHaxeString();
                if (!mob.hasOldSkill(haxeSkillId))
                    return;

                var oldSkill = mob.getOldSkill(haxeSkillId) as OldMobSkill;
                if (oldSkill == null)
                    return;

                WithClientNetworkQueuedAttackContext(mob, () =>
                {
                    mob.queueAttack(oldSkill, intent.RequiresTargetInArea, intent.Data);
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Client oldSkill queue failed: {SkillId}", intent.SkillId);
            }
        }

        private static void TryInvokeOldSkillChargeComplete(OldSkill oldSkill)
        {
            if (oldSkill == null)
                return;

            try
            {
                var cb = oldSkill.dynOnChargeComplete;
                if (cb != null)
                {
                    cb.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] OldSkill dynOnChargeComplete invoke failed");
            }
        }

        private static bool TryExecuteClientOldSkillNativeLike(OldMobSkill oldSkill, int? data)
        {
            if (oldSkill == null)
                return false;

            if (TryPrepareClientOldSkillOnOwnerTarget(oldSkill, null, data))
                return true;

            if (!data.HasValue && TryPrepareClientOldSkillOnOwnerTarget(oldSkill, null, null))
                return true;

            if (TryPrepareClientOldSkillOnOwnerTarget(oldSkill, true, data))
                return true;

            return !data.HasValue && TryPrepareClientOldSkillOnOwnerTarget(oldSkill, true, null);
        }

        private static bool TryPrepareClientOldSkillOnOwnerTarget(OldMobSkill oldSkill, bool? useTargetData, int? data)
        {
            try
            {
                return oldSkill.prepareOnOwnerTarget(useTargetData, data);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetMobOldSkill(Mob mob, string normalizedSkillId, out OldSkill oldSkill)
        {
            oldSkill = null!;
            if (mob == null || string.IsNullOrWhiteSpace(normalizedSkillId))
                return false;

            try
            {
                var skillId = normalizedSkillId.AsHaxeString();
                if (!mob.hasOldSkill(skillId))
                    return false;

                oldSkill = mob.getOldSkill(skillId) as OldSkill;
                return oldSkill != null;
            }
            catch
            {
                oldSkill = null!;
                return false;
            }
        }

        private static bool IsQueuedOrChargingOldSkillId(Mob mob, string expectedSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(expectedSkillId))
                return false;

            if (IsQueuedOldSkillId(mob, expectedSkillId))
                return true;

            if (TryGetChargingOldSkillId(mob, out var chargingOldSkillId) &&
                string.Equals(chargingOldSkillId, expectedSkillId, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool IsQueuedOldSkillId(Mob mob, string expectedSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(expectedSkillId))
                return false;

            if (!TryGetQueuedOldSkillId(mob, out var queuedOldSkillId))
                return false;

            return string.Equals(queuedOldSkillId, expectedSkillId, StringComparison.Ordinal);
        }

        private static bool TryGetQueuedOldSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var queued = mob.queuedOldSkill;
                var queuedSkill = queued?.a;
                skillId = queuedSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static bool TryGetChargingOldSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var chargingOldSkill = mob.getChargingOldSkill() as OldSkill;
                skillId = chargingOldSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static bool TryGetChargingNewSkillId(Mob mob, out string skillId)
        {
            skillId = string.Empty;
            if (mob == null)
                return false;

            try
            {
                var chargingNewSkill = mob.getChargingNewSkill();
                skillId = chargingNewSkill?.id?.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(skillId);
            }
            catch
            {
                skillId = string.Empty;
                return false;
            }
        }

        private static void RegisterClientQueuedOldSkillMarker(Mob mob, string skillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(skillId))
                return;

            lock (Sync)
            {
                clientQueuedOldSkillMarkers[mob] = skillId;
            }
        }

        private static bool ShouldSkipClientOldSkillExecuteFromMarker(Mob mob, string incomingSkillId)
        {
            if (mob == null || string.IsNullOrWhiteSpace(incomingSkillId))
                return false;

            lock (Sync)
            {
                if (!clientQueuedOldSkillMarkers.TryGetValue(mob, out var markerSkillId))
                    return false;

                if (string.Equals(markerSkillId, incomingSkillId, StringComparison.Ordinal))
                {
                    clientQueuedOldSkillMarkers.Remove(mob);
                    // Marker is only meaningful if the mob is still actively queued/charging this skill
                    // (e.g. client-side behavior fired it and the host event would be a duplicate).
                    // If the skill is not queued/charging, the marker is stale from our own replay
                    // and the incoming host event is a fresh attack — do not skip.
                    if (IsQueuedOrChargingOldSkillId(mob, incomingSkillId))
                        return true;
                    return false;
                }

                if (!IsQueuedOrChargingOldSkillId(mob, markerSkillId))
                    clientQueuedOldSkillMarkers.Remove(mob);
            }

            return false;
        }

        private static bool TryGetCurrentClientAttackTarget(Mob mob, out Entity target)
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

        private static bool TryGetCurrentClientNemesisTarget(Mob mob, out Entity target)
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

        private static bool TryResolveClientExecuteTargetEntity(Mob mob, int targetUserId, out Entity target)
        {
            target = null!;
            if (mob == null || !IsMobHostileToPlayers(mob))
                return false;

            if (targetUserId > 0 && TryResolveClientDirectPlayerCombatTarget(mob, targetUserId, out target))
                return true;
            if (TryGetCurrentClientAttackTarget(mob, out target))
                return true;
            if (TryGetCurrentClientNemesisTarget(mob, out target))
                return true;

            var detected = ResolveDetectedClientTargetEntity(mob);
            if (detected != null)
            {
                target = detected;
                return true;
            }

            return false;
        }

        private static void TrySetClientMobAttackTarget(Mob mob, int targetUserId, int attackDir, bool forceRetarget = false)
        {
            Entity? target = null;

            lock (Sync)
            {
                if (!forceRetarget &&
                    clientCachedAttackTargetByMob.TryGetValue(mob, out var cached) &&
                    cached != null &&
                    IsPreservablePlayerCombatTargetForMob(mob, cached))
                {
                    target = cached;
                }
            }

            if (target == null)
            {
                target = ResolveClientAttackTargetEntity(mob, targetUserId);
                if (target == null && IsMobHostileToPlayers(mob))
                    target = ResolveDetectedClientTargetEntity(mob);
                if (target == null)
                    return;

                lock (Sync)
                {
                    clientCachedAttackTargetByMob[mob] = target;
                }
            }

            var normalizedAttackDir = NormalizeDir(attackDir);
            if (!forceRetarget)
            {
                try
                {
                    if (targetUserId <= 0)
                    {
                        if (mob.aTarget != null && IsPreservablePlayerCombatTargetForMob(mob, mob.aTarget))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }

                        if (mob.nemesisTarget != null && IsPreservablePlayerCombatTargetForMob(mob, mob.nemesisTarget))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }
                    }
                    else
                    {
                        if (ReferenceEquals(mob.aTarget, target) || ReferenceEquals(mob.nemesisTarget, target))
                        {
                            if (normalizedAttackDir != 0)
                                mob.dir = normalizedAttackDir;
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            TrySetMobAttackTargetsExact(mob, target, attackDir, forceAttackDir: true);
        }

        private static Entity? ResolveClientAttackTargetEntity(Mob mob, int targetUserId)
        {
            if (!IsMobHostileToPlayers(mob))
                return null;

            if (targetUserId > 0 && TryResolveClientDirectPlayerCombatTarget(mob, targetUserId, out var directTarget))
                return directTarget;
            if (TryGetCurrentClientAttackTarget(mob, out var attackTarget))
                return attackTarget;
            if (TryGetCurrentClientNemesisTarget(mob, out var nemesisTarget))
                return nemesisTarget;

            var detected = ResolveDetectedClientTargetEntity(mob);
            if (detected != null)
                return detected;

            return null;
        }

        private static bool TryResolveClientDirectPlayerCombatTarget(Mob mob, int targetUserId, out Entity target)
        {
            target = null!;
            if (mob == null || targetUserId <= 0 || !IsMobHostileToPlayers(mob))
                return false;

            var net = GameMenu.NetRef;
            var localId = net?.id ?? 0;
            if (localId <= 0)
                return false;

            if (targetUserId == localId)
            {
                var localHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                if (localHero != null && IsPreservablePlayerCombatTargetForMob(mob, localHero))
                {
                    target = localHero;
                    return true;
                }

                return false;
            }

            if (!ModEntry.TryGetClientIndex(localId, targetUserId, out var index))
                return false;

            var client = ModEntry.clients[index];
            if (client == null || !IsPreservablePlayerCombatTargetForMob(mob, client))
                return false;

            target = client;
            return true;
        }

        private static Entity? ResolveDetectedClientTargetEntity(Mob mob)
        {
            if (mob == null)
                return null;
            if (!IsMobHostileToPlayers(mob))
                return null;

            s_clientDetectedTargetsScratch.Clear();
            var hero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (hero != null)
                s_clientDetectedTargetsScratch.Add(hero);

            for (int i = 0; i < ModEntry.clients.Length; i++)
            {
                var client = ModEntry.clients[i];
                if (client != null)
                    s_clientDetectedTargetsScratch.Add(client);
            }

            Entity? best = null;
            var bestDistSq = double.MaxValue;
            var mx = GetWorldX(mob);
            var my = GetWorldY(mob);

            for (int i = 0; i < s_clientDetectedTargetsScratch.Count; i++)
            {
                var candidate = s_clientDetectedTargetsScratch[i];
                if (candidate == null || ReferenceEquals(candidate, mob))
                    continue;
                if (!IsAcquirablePlayerCombatTargetForMob(mob, candidate, requireDetectArea: true))
                    continue;

                var dx = GetWorldX(candidate) - mx;
                var dy = GetWorldY(candidate) - my;
                var distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = candidate;
                }
            }

            s_clientDetectedTargetsScratch.Clear();
            return best;
        }

        private static bool HasLocalQueuedOrChargingSkill(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                if (mob.queuedSkill != null)
                    return true;
            }
            catch
            {
            }

            try
            {
                if (mob.queuedOldSkill?.a != null)
                    return true;
            }
            catch
            {
            }

            return TryGetChargingOldSkillId(mob, out _) || TryGetChargingNewSkillId(mob, out _);
        }

        private static void RegisterClientNetworkAttackExecuted(Mob mob)
        {
            MarkClientNetworkAttackActive(mob);
        }

        private static void ConsumeIncomingMobHits(NetNode net)
        {
            s_mobHitMergeScratch.Clear();
            if (net.TryConsumeMobHits(out var incoming) && incoming != null && incoming.Count > 0)
            {
                try
                {
                    s_mobHitMergeScratch.AddRange(incoming);
                }
                finally
                {
                    NetNode.ReleaseConsumedList(incoming);
                }
            }

            if (s_mobHitMergeScratch.Count == 0)
                return;

            MobSyncTrace.LogRecvHits(net.IsHost ? "hitsOnHost" : "hitsOnClient", s_mobHitMergeScratch);

            if (IsSyncQuiescedForTransition())
            {
                s_mobHitMergeScratch.Clear();
                return;
            }

            ApplyIncomingMobHits(s_mobHitMergeScratch, 0, s_mobHitMergeScratch.Count, false);
        }

        private static void ConsumeIncomingMobDies(NetNode net)
        {
            if (!net.TryConsumeMobDies(out var dies))
                return;

            try
            {
                MobSyncTrace.LogRecvDies(net.IsHost ? "diesOnHost" : "diesOnClient", dies);

                // Host is authoritative for mob death. Ignore remote client die packets.
                if (net.IsHost)
                    return;

                if (IsSyncQuiescedForTransition())
                    return;

                ApplyIncomingMobDies(dies);
            }
            finally
            {
                NetNode.ReleaseConsumedList(dies);
            }
        }

        private static void ApplyIncomingMobDies(IReadOnlyList<NetNode.MobDie> dies)
        {
            if (dies == null || dies.Count == 0)
                return;

            s_dieVictimsScratch.Clear();
            s_dieVictimDedupScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;
            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = 0; i < dies.Count; i++)
                {
                    var die = dies[i];
                    if (!ShouldAcceptPacketGenerationLocked(die.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    var mob = ResolveMobFromDieLocked(die);
                    if (mob == null)
                        continue;

                    var isBoss = BossSyncHelpers.IsBossMob(mob);
                    var life = 0;
                    try
                    {
                        life = mob.life;
                        if (mob.destroyed)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }

                    // Hardening: do not ignore dead-but-not-destroyed mobs. This was a common
                    // source of client-side ghost elites: life already reached 0, HP bar disappeared,
                    // but onDie/despawn never ran locally. Let the authoritative MOBDIE packet finish cleanup.
                    if (!isBoss && life <= 0 && mob.destroyed)
                        continue;

                    if (s_dieVictimDedupScratch.Add(mob))
                        s_dieVictimsScratch.Add(mob);
                }
            }

            LogRejectedPacketGeneration("mobDie", rejectedCount, rejectedGeneration);

            s_dieVictimDedupScratch.Clear();

            for (int i = 0; i < s_dieVictimsScratch.Count; i++)
            {
                var mob = s_dieVictimsScratch[i];
                if (mob == null)
                    continue;

                // Non-boss (client): DEFER to the mob's own update cycle instead of re-running
                // onDie synchronously. The hardening that processed dead-but-not-destroyed mobs
                // here re-killed mobs that had already died cleanly (host MOBDIE arriving after a
                // local kill), corrupting their death state - the source of the level-transition
                // render fatal. The deferred flush skips mobs that finish destroying themselves
                // and only completes genuinely stuck ghosts.
                if (!IsHost(GameMenu.NetRef) && !BossSyncHelpers.IsBossMob(mob))
                {
                    TryDeferCulledClientMobDeath(mob);
                    continue;
                }

                TryWakeMobForForcedSimulation(mob);
                try
                {
                    RunWithAuthoritativeClientBossDie(mob, () =>
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
                }
            }

            s_dieVictimsScratch.Clear();
        }

        private static void ApplyIncomingMobHits(IReadOnlyList<NetNode.MobHit> hits, bool reResolveMobBySyncIdOnApply)
        {
            if (hits == null || hits.Count == 0)
                return;
            ApplyIncomingMobHits(hits, 0, hits.Count, reResolveMobBySyncIdOnApply);
        }

        private static void ApplyIncomingMobHits(IReadOnlyList<NetNode.MobHit> hits, int start, int count, bool reResolveMobBySyncIdOnApply)
        {
            if (hits == null || count <= 0)
                return;

            var end = start + count;
            if (start < 0 || end > hits.Count)
                return;

            var net = GameMenu.NetRef;
            var isHost = IsHost(net);
            s_pendingMobHitAppliesScratch.Clear();
            var rejectedGeneration = 0;
            var rejectedCount = 0;

            lock (Sync)
            {
                PruneInvalidTrackedMobsLocked();
                for (int i = start; i < end; i++)
                {
                    var hit = hits[i];
                    if (!ShouldAcceptPacketGenerationLocked(hit.Generation, ref rejectedCount, ref rejectedGeneration))
                        continue;

                    if (isHost && !IsKnownRemoteHitSenderOnHost(net, hit.UserId))
                        continue;

                    var mob = ResolveMobFromHitLocked(hit);
                    if (mob == null)
                        continue;

                    if (!TryGetMobLifeAndMaxSafe(mob, out var prevLife, out var maxLife))
                        continue;

                    var targetLife = System.Math.Clamp(hit.Hp, 0, maxLife);
                    var replaySpecialHit = false;

                    if (targetLife >= prevLife)
                    {
                        replaySpecialHit = ShouldReplayIncomingHitWithoutLifeDelta(mob);
                        if (!replaySpecialHit)
                            continue;

                        targetLife = prevLife;
                    }

                    var forceDie = targetLife <= 0 && prevLife > 0;
                    var syncId = -1;
                    TryGetMobSyncId(mob, out syncId);
                    MobSyncTrace.LogIncomingHitApply(syncId, hit.Hp, hit.UserId, replaySpecialHit, forceDie);
                    s_pendingMobHitAppliesScratch.Add(new PendingMobHitApply(
                        mob,
                        hit.UserId,
                        prevLife,
                        targetLife,
                        maxLife,
                        forceDie,
                        syncId,
                        BossSyncHelpers.IsBossMob(mob),
                        replaySpecialHit));
                }
            }

            LogRejectedPacketGeneration(isHost ? "mobHitOnHost" : "mobHitOnClient", rejectedCount, rejectedGeneration);

            FlushGhostDespawnEchoes(net, isHost);

            for (int i = 0; i < s_pendingMobHitAppliesScratch.Count; i++)
            {
                var update = s_pendingMobHitAppliesScratch[i];
                Mob? mob;
                if (reResolveMobBySyncIdOnApply && update.SyncId >= 0)
                {
                    lock (Sync)
                    {
                        mob = ResolveMobBySyncIdLocked(update.SyncId);
                    }
                }
                else
                {
                    mob = update.Mob;
                }

                if (mob == null)
                    continue;

                if (isHost)
                    TryWakeMobForForcedSimulation(mob);

                var appliedLife = update.TargetLife;
                if (update.ReplaySpecialHit)
                {
                    TryWakeMobForForcedSimulation(mob);
                    TryReplayIncomingSpecialHitReaction(mob);
                    appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                }
                else if (update.ForceDie)
                {
                    TryWakeMobForForcedSimulation(mob);
                    if (isHost)
                    {
                        if (update.IsBoss)
                        {
                            TryApplyHostBossFinishingHit(mob, update.TargetMaxLife);
                        }
                        else
                        {
                            try
                            {
                                if (!mob.destroyed)
                                {
                                    mob.life = 0;
                                    mob.onDie();
                                }
                                else
                                {
                                    mob.life = 0;
                                }
                            }
                            catch
                            {
                            }
                        }

                        appliedLife = GetMobLifeOrFallback(mob, 0);
                    }
                    else
                    {
                        ApplyAuthoritativeLifeState(mob, 0, update.TargetMaxLife);
                        appliedLife = 0;
                    }
                }
                else
                {
                    ApplyAuthoritativeLifeState(mob, update.TargetLife, update.TargetMaxLife);
                    appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                }

                if (isHost)
                    TryApplyHostMobHitCombatRefresh(mob, update.SourceUserId, update.PreviousLife, appliedLife, update.ReplaySpecialHit);

                if (isHost && net != null && update.SyncId >= 0)
                {
                    var sx = GetWorldX(mob);
                    var sy = GetWorldY(mob);
                    var dir = NormalizeDir(mob.dir);
                    var hitEv = $"hit|{appliedLife.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    if (TryGetCurrentLevelIdentityToken(out var identityToken))
                    {
                        var evUpdate = new NetNode.MobEventUpdate(update.SyncId, sx, sy, dir, SingleEvent(hitEv), generation: identityToken);
                        MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(evUpdate));
                        net.SendMobEvents(SingleUpdate(evUpdate));
                    }
                }
            }

            s_pendingMobHitAppliesScratch.Clear();
        }

        private static void TryApplyHostBossFinishingHit(Mob mob, int targetMaxLife)
        {
            if (mob == null)
                return;

            try
            {
                var damage = System.Math.Max(1.0, targetMaxLife * 8.0);
                var attackUtils = AttackUtils.Class;
                var createFromHeroAndHit = attackUtils?.createFromHeroAndHit;
                if (createFromHeroAndHit != null)
                {
                    _ = createFromHeroAndHit(null, damage, null, mob);
                    if (TryFinalizeHostMobDeath(mob))
                        return;
                }

                var createFromHero = attackUtils?.createFromHero;
                var hit = attackUtils?.hit;
                if (createFromHero != null && hit != null)
                {
                    var attack = createFromHero(null, damage, null);
                    if (attack != null)
                    {
                        hit(attack, mob);
                        if (TryFinalizeHostMobDeath(mob))
                            return;
                    }
                }

                if (TryFinalizeHostMobDeath(mob))
                    return;

                // Last-resort force; some bosses need explicit life zero before onDie branching.
                mob.life = 0;
                TryFinalizeHostMobDeath(mob);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Host boss finishing hit replay failed");
            }
        }

        private static bool TryFinalizeHostMobDeath(Mob mob)
        {
            if (mob == null)
                return true;

            try
            {
                if (mob.destroyed)
                    return true;
            }
            catch
            {
            }

            var life = GetMobLifeOrFallback(mob, 1);
            if (life > 0)
                return false;

            try
            {
                mob.life = 0;
                mob.onDie();
            }
            catch
            {
            }

            try
            {
                return mob.destroyed || GetMobLifeOrFallback(mob, 1) <= 0;
            }
            catch
            {
                return true;
            }
        }

        private static bool ShouldReplayIncomingHitWithoutLifeDelta(Mob mob)
        {
            if (mob == null)
                return false;

            var typeId = GetMobTypeIdSafe(mob);
            if (string.Equals(typeId, "mushroom", StringComparison.OrdinalIgnoreCase))
                return true;

            var runtimeClass = GetMobRuntimeClassKeySafe(mob);
            return string.Equals(runtimeClass, "Mushroom", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when this mob is culled on THIS machine (far from the local hero, vanilla is not
        /// simulating it). Client-side, such mobs must not be pushed into vanilla simulation:
        /// their behavior state was never proximity-initialized, and forcing them awake makes vanilla
        /// update null-deref a few frames later (Null access .cx while fighting far from the
        /// host). Returns false on any read failure so callers fall back to existing behavior.
        /// </summary>
        private static bool IsMobCulledLocally(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                return mob.isOutOfGame && !mob.isOnScreen;
            }
            catch
            {
                return false;
            }
        }

        private static void TryReplayIncomingSpecialHitReaction(Mob mob)
        {
            if (mob == null)
                return;

            // Cosmetic replay only: skip it on clients for mobs culled locally. Running vanilla
            // hit resolution on a sleeping, never-initialized mob is hazardous, and the reaction
            // is off-screen anyway. The authoritative life still arrives via state snapshots.
            if (!IsHost(GameMenu.NetRef) && IsMobCulledLocally(mob))
                return;

            try
            {
                RunWithSuppressedMobHitSend(() =>
                {
                    var attackUtils = AttackUtils.Class;
                    var createFromHeroAndHit = attackUtils?.createFromHeroAndHit;
                    if (createFromHeroAndHit != null)
                    {
                        _ = createFromHeroAndHit(null, 1.0, null, mob);
                        return;
                    }

                    var createFromHero = attackUtils?.createFromHero;
                    var hit = attackUtils?.hit;
                    if (createFromHero == null || hit == null)
                        return;

                    var attack = createFromHero(null, 1.0, null);
                    if (attack != null)
                        hit(attack, mob);
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Special incoming mob hit replay failed");
            }
        }

        private static void TryWakeMobForForcedSimulation(Mob mob)
        {
            if (mob == null)
                return;

            // The wake is required on the authoritative HOST (it must simulate mobs that remote
            // players are fighting). On clients it only served cosmetic hit/death replays; waking
            // a locally culled mob there runs vanilla behavior logic on uninitialized state and crashes.
            if (!IsHost(GameMenu.NetRef) && IsMobCulledLocally(mob))
                return;

            PromoteMobToSyncVisibleState(mob);
        }

        private static bool ShouldSendHostContactPacket(Mob mob, Entity? target)
        {
            if (mob == null)
                return false;

            var userId = ResolveHostTargetUserId(target ?? ResolveCurrentHostPlayerCombatTarget(mob), GameMenu.NetRef?.id ?? 0);
            if (userId <= 0)
                return false;

            lock (Sync)
            {
                if (hostLastSentContactTargetUserIdByMob.TryGetValue(mob, out var lastTargetUserId) &&
                    lastTargetUserId == userId)
                    return false;

                hostLastSentContactTargetUserIdByMob[mob] = userId;
                return true;
            }
        }

        private static Mob? ResolveMobFromHitLocked(NetNode.MobHit hit)
        {
            var registryMob = ResolveMobBySyncIdLocked(hit.MobIndex);
            var typeMatchesRegistry = MobHitRegistryTypeMatchesLocked(registryMob, hit);

            if (registryMob != null && typeMatchesRegistry)
            {
                if (!MobHitRegistryStillTrustworthyLocked(registryMob, hit))
                {
                    MobSyncTrace.LogIncomingMappingMismatch(
                        "hit",
                        hit.MobIndex,
                        hit.Type ?? string.Empty,
                        BuildMobStateTypeSignature(registryMob),
                        "position_mismatch");
                    return null;
                }

                return registryMob;
            }

            MobSyncTrace.LogIncomingMappingMismatch(
                "hit",
                hit.MobIndex,
                hit.Type ?? string.Empty,
                registryMob != null ? BuildMobStateTypeSignature(registryMob) : string.Empty,
                registryMob == null ? "missing_sync_id" : "type_mismatch");

            // Only the missing_sync_id case feeds the ghost despawn echo: the host tracks NO mob
            // at this syncId in the current generation, so the client's same-generation mob at
            // this syncId can only be a stale ghost. (type_mismatch means the host DOES have a
            // mob there — echoing a death then could kill a legitimate mob.)
            if (registryMob == null)
                RecordGhostHitMissLocked(hit);

            return null;
        }

        /// <summary>
        /// Called under <c>Sync</c>. Counts missing_sync_id hits per syncId; once the same syncId
        /// has missed at least <see cref="GhostHitMissMinCount"/> times spanning at least
        /// <see cref="GhostHitMissMinSeconds"/>, queues a life=0 state echo (rate-limited per
        /// syncId). Late hits on freshly killed mobs produce only 1-2 misses and never trigger.
        /// </summary>
        private static void RecordGhostHitMissLocked(NetNode.MobHit hit)
        {
            if (s_ghostHitMissGeneration != hit.Generation)
            {
                s_ghostHitMissBySyncId.Clear();
                s_ghostHitMissGeneration = hit.Generation;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!s_ghostHitMissBySyncId.TryGetValue(hit.MobIndex, out var record))
            {
                record = new GhostHitMissRecord { FirstMissTicks = now };
                s_ghostHitMissBySyncId[hit.MobIndex] = record;
            }

            record.Count++;
            if (record.Count < GhostHitMissMinCount)
                return;
            if (now - record.FirstMissTicks < (long)(System.Diagnostics.Stopwatch.Frequency * GhostHitMissMinSeconds))
                return;
            if (record.LastEchoTicks != 0 &&
                now - record.LastEchoTicks < (long)(System.Diagnostics.Stopwatch.Frequency * GhostHitEchoMinIntervalSeconds))
                return;

            record.LastEchoTicks = now;
            s_ghostDespawnEchoScratch.Add(new NetNode.MobStateSnapshot(
                hit.MobIndex,
                hit.X,
                hit.Y,
                0,
                0,
                0,
                string.Empty,
                hit.Type ?? string.Empty,
                string.Empty,
                hit.Generation));
        }

        /// <summary>
        /// Sends queued ghost despawn echoes. Host only; discards silently on client role.
        /// </summary>
        private static void FlushGhostDespawnEchoes(NetNode? net, bool isHost)
        {
            List<NetNode.MobStateSnapshot>? toSend = null;
            lock (Sync)
            {
                if (s_ghostDespawnEchoScratch.Count == 0)
                    return;

                if (!isHost || net == null || !net.IsAlive)
                {
                    s_ghostDespawnEchoScratch.Clear();
                    return;
                }

                toSend = new List<NetNode.MobStateSnapshot>(s_ghostDespawnEchoScratch);
                s_ghostDespawnEchoScratch.Clear();
            }

            for (int i = 0; i < toSend.Count; i++)
            {
                Log.Warning(
                    "[MobSync] ghost mob despawn echo syncId={SyncId} type={Type}",
                    toSend[i].Index,
                    toSend[i].Type);
            }

            try
            {
                net!.SendMobStates(toSend);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Ghost despawn echo send failed");
            }
        }

        private static bool MobHitRegistryTypeMatchesLocked(Mob? registryMob, NetNode.MobHit hit)
        {
            if (registryMob == null)
                return false;
            if (string.IsNullOrWhiteSpace(hit.Type))
                return true;
            return DoesMobMatchStateType(registryMob, hit.Type);
        }

        private static bool MobHitQuantizedPositionCloseEnoughLocked(Mob mob, NetNode.MobHit hit)
        {
            QuantizeWorldPositionToPixelsInt32(hit.X, hit.Y, out var hx, out var hy);
            QuantizeWorldPositionToPixelsInt32(GetWorldX(mob), GetWorldY(mob), out var mx, out var my);
            return mx == hx && my == hy;
        }

        private static bool MobHitQuantizedFallbackPositionMatchesLocked(Mob mob, NetNode.MobHit hit)
        {
            if (mob == null)
                return false;

            QuantizeWorldPositionToPixelsInt32(hit.X, hit.Y, out var hx, out var hy);
            QuantizeWorldPositionToPixelsInt32(GetWorldX(mob), GetWorldY(mob), out var mx, out var my);

            var grounded = true;
            try
            {
                grounded = mob.hasGravity;
            }
            catch
            {
            }

            return grounded ? mx == hx : (mx == hx && my == hy);
        }

        private static bool MobHitRegistryStillTrustworthyLocked(Mob mob, NetNode.MobHit hit)
        {
            if (mob == null)
                return false;

            if (MobHitQuantizedPositionCloseEnoughLocked(mob, hit) ||
                MobHitQuantizedFallbackPositionMatchesLocked(mob, hit))
                return true;

            // Old code required exact quantized coordinates for client damage reports. That caused
            // valid hits to be rejected as position_mismatch once the same mob drifted even slightly
            // between host/client. If the sync id and type already match, accept the hit within a
            // generous same-room distance and let the normal host mob-state packets correct the drift.
            try
            {
                var dx = GetWorldX(mob) - hit.X;
                var dy = GetWorldY(mob) - hit.Y;
                if (double.IsFinite(dx) && double.IsFinite(dy))
                    return (dx * dx + dy * dy) <= (MobHitTrustedSyncIdDistancePx * MobHitTrustedSyncIdDistancePx);
            }
            catch
            {
            }

            return false;
        }

        private static Mob? ResolveMobFromDieLocked(NetNode.MobDie die)
        {
            lock (Sync)
            {
                return ResolveMobBySyncIdLocked(die.MobIndex);
            }
        }

        private static Mob? ResolveMobBySyncIdLocked(int mobIndex)
        {
            var mob = ResolveTrackedMobBySyncIdLocked(mobIndex);
            if (mob == null || !IsSyncMob(mob))
                return null;

            try
            {
                if (mob.destroyed || mob._level == null)
                {
                    s_trackedMobValidationPending = true;
                    return null;
                }

                if (!DoesLevelMatchCurrentIdentityLocked(mob._level))
                {
                    s_trackedMobValidationPending = true;
                    return null;
                }
            }
            catch
            {
                s_trackedMobValidationPending = true;
                return null;
            }

            return mob;
        }

        private static bool IsKnownRemoteHitSenderOnHost(NetNode? net, int senderId)
        {
            if (!IsHost(net))
                return true;

            var localId = net?.id ?? 0;
            if (senderId <= 0 || senderId == localId)
                return false;

            if (!ModEntry.TryGetClientIndex(localId, senderId, out var index))
                return false;

            if (index < 0 || index >= ModEntry.clientIds.Length)
                return false;

            return ModEntry.clientIds[index] == senderId;
        }

        private static bool TryGetMobLifeAndMaxSafe(Mob mob, out int life, out int maxLife)
        {
            life = 0;
            maxLife = 1;
            if (mob == null)
                return false;

            try
            {
                life = mob.life;
                maxLife = System.Math.Max(1, mob.maxLife);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int GetMobLifeOrFallback(Mob mob, int fallback)
        {
            if (mob == null)
                return fallback;

            try
            {
                return mob.life < 0 ? 0 : mob.life;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Scale mob HP for multiplayer: +0.5 per player for regular mobs, +2 per player for bosses.</summary>
        private static void ScaleMobHpForMultiplayer(Mob mob)
        {
            BossHpScaling.ScaleForMultiplayer(mob);
        }
    }
}

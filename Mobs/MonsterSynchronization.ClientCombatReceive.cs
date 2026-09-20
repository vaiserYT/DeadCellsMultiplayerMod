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
    public partial class MobsSynchronization
    {
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

                    // A host-confirmed death always wins over a dormant client tombstone: the
                    // identity is dead, never resurrected. Clear the tombstone so no later state/hit
                    // can recreate a replica for a mob the host has confirmed dead.
                    if (IsClient(LobbySession.NetRef) && HasClientMobTombstoneLocked(die.MobIndex))
                        RemoveClientMobTombstoneLocked(die.MobIndex, "authoritative_mobdie");

                    var mob = ResolveMobFromDieLocked(die);
                    if (mob == null)
                    {
                        // A dropped boss death is what strands the client boss at 0/1 HP with a
                        // locked camera and no rewards. Buffer boss-typed packets for retry and
                        // bounded escalation instead of losing them.
                        RememberUnresolvedBossDieLocked(die);
                        continue;
                    }

                    var isBoss = BossSyncHelpers.IsBossMob(mob);
                    if (isBoss && clientCompletedAuthoritativeBossDeaths.Contains(mob))
                        continue;
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
                if (!IsHost(LobbySession.NetRef) && !BossSyncHelpers.IsBossMob(mob) && TryDeferCulledClientMobDeath(mob))
                    continue;

                TryWakeMobForForcedSimulation(mob);
                try
                {
                    RunWithAuthoritativeClientMobDie(mob, () =>
                    {
                        RunWithSuppressedMobDieSend(() =>
                        {
                            mob.life = 0;
                            mob.onDie();
                        });
                    });

                    if (BossSyncHelpers.IsBossMob(mob) && IsCompletedAuthoritativeBossDeath(mob))
                    {
                        lock (Sync)
                        {
                            clientCompletedAuthoritativeBossDeaths.Add(mob);
                            s_clientBossAuthoritativeZeroLifeFrame.Remove(mob);
                        }
                    }

                }
                catch
                {
                }

            }

            s_dieVictimsScratch.Clear();
        }

        private static bool IsCompletedAuthoritativeBossDeath(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                return mob.destroyed || mob.life <= 0;
            }
            catch
            {
                return true;
            }
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

            var net = LobbySession.NetRef;
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
                    {
                        // This is where a client's damage is lost. The host already publishes an
                        // immediate reliable keyframe after every hit it DOES apply (below), so a
                        // client needing many times more hits than the host is this drop, not a
                        // missing HP broadcast. Count it so the rate is visible without per-packet spam.
                        MobSyncTrace.LogDamageDropped(isHost, hit.MobIndex, hit.UserId, hit.DamageHint);
                        continue;
                    }

                    if (!TryGetMobLifeAndMaxSafe(mob, out var prevLife, out var maxLife))
                        continue;

                    var targetLife = System.Math.Clamp(hit.Hp, 0, maxLife);
                    var isBoss = BossSyncHelpers.IsBossMob(mob);
                    // A remote player's hit is an INPUT/intent, never authoritative HP. Replaying the
                    // reported damage through the host's native hit path preserves elite invulnerability,
                    // phase transitions, onDamage hooks and final death logic. The absolute HP field is
                    // retained only as a compatibility fallback for packets that carry no damage hint.
                    var replaySpecialHit = isHost && hit.DamageHint > 0.0;
                    if (replaySpecialHit)
                        targetLife = prevLife;

                    // On a client, every hit in this queue came from the authoritative host, so its
                    // absolute result is applied even when it raises a speculative local value. On the
                    // host, an old/no-hint client packet can only lower HP and can never heal a mob.
                    if (isHost && !replaySpecialHit && targetLife >= prevLife)
                    {
                        replaySpecialHit = hit.DamageHint > 0.0 || ShouldReplayIncomingHitWithoutLifeDelta(mob);
                        if (!replaySpecialHit)
                            continue;

                        targetLife = prevLife;
                    }

                    var forceDie = targetLife <= 0 && prevLife > 0;
                    var syncId = -1;
                    TryGetMobSyncId(mob, out syncId);
                    MobSyncTrace.LogIncomingHitApply(syncId, hit.Hp, hit.UserId, replaySpecialHit, forceDie);
                    MobSyncTrace.LogDamageApplied(isHost, syncId, prevLife, targetLife, hit.DamageHint, replaySpecialHit);
                    s_pendingMobHitAppliesScratch.Add(new PendingMobHitApply(
                        mob,
                        hit.UserId,
                        prevLife,
                        targetLife,
                        maxLife,
                        forceDie,
                        syncId,
                        isBoss,
                        replaySpecialHit,
                        hit.DamageHint));
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
                    // Boss phase scripts remain the one conservative exception: replaying a
                    // reconstructed hit in the middle of a queued boss skill can strand that script.
                    // Normal mobs and elites always use native host damage, even while attacking, so
                    // their armor/invulnerability/elite callbacks stay vanilla-authoritative.
                    if (isHost && update.IsBoss && HasLocalQueuedOrChargingSkill(mob))
                    {
                        ApplyAuthoritativeLifeState(mob, update.TargetLife, update.TargetMaxLife);
                        appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                    }
                    else
                    {
                        TryWakeMobForForcedSimulation(mob);
                        TryReplayIncomingSpecialHitReaction(mob, update.DamageHint);
                        appliedLife = GetMobLifeOrFallback(mob, update.TargetLife);
                    }
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

                if (isHost)
                {
                    var mobStillPresent = false;
                    try { mobStillPresent = !mob.destroyed; } catch { }
                    if (mobStillPresent)
                    {
                        // Publish a reliable fully-typed keyframe immediately after any remote hit.
                        // Besides HP, this carries authoritative affects/elite phase metadata and
                        // repairs a stale binding without waiting for the periodic recovery pass.
                        QueueHostMobDirty(mob, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                    }
                }

                if (isHost && net != null && update.SyncId >= 0)
                {
                    var destroyed = false;
                    try { destroyed = mob.destroyed; } catch { }
                    // A lethal native replay already emitted the authoritative death/tombstone.
                    // Do not read coordinates from a disposed proxy just to send a redundant HP=0.
                    if (!destroyed && TryGetCurrentLevelIdentityToken(out var identityToken))
                    {
                        var sx = GetWorldX(mob);
                        var sy = GetWorldY(mob);
                        var dir = NormalizeDir(mob.dir);
                        var hitEv = $"hit|{appliedLife.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                        var mobType = BuildMobStateTypeSignature(mob);
                        var evUpdate = new NetNode.MobEventUpdate(update.SyncId, sx, sy, dir, SingleEvent(hitEv), mobType, identityToken);
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

            var replayAttempted = false;
            try
            {
                var damage = System.Math.Max(1.0, targetMaxLife * 8.0);
                Hero? sourceHero = ModEntry.me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                try
                {
                    if (sourceHero != null && sourceHero.destroyed)
                        sourceHero = null;
                }
                catch
                {
                    sourceHero = null;
                }

                var attackUtils = AttackUtils.Class;
                var createFromHeroAndHit = attackUtils?.createFromHeroAndHit;
                if (createFromHeroAndHit != null)
                {
                    replayAttempted = true;
                    _ = createFromHeroAndHit(sourceHero, damage, null, mob);
                    if (TryFinalizeHostMobDeath(mob))
                        return;

                    // A boss that is alive after one valid finishing hit may have entered its next
                    // phase or a temporary invulnerability window. Never immediately hit it again
                    // or force life to zero; that skipped multi-phase vanilla boss logic.
                    if (GetMobLifeOrFallback(mob, 1) > 0)
                        return;
                }

                var createFromHero = attackUtils?.createFromHero;
                var hit = attackUtils?.hit;
                if (!replayAttempted && createFromHero != null && hit != null)
                {
                    var attack = createFromHero(sourceHero, damage, null);
                    if (attack != null)
                    {
                        replayAttempted = true;
                        hit(attack, mob);
                        if (TryFinalizeHostMobDeath(mob))
                            return;

                        if (GetMobLifeOrFallback(mob, 1) > 0)
                            return;
                    }
                }

                if (replayAttempted || TryFinalizeHostMobDeath(mob))
                    return;

                // Last resort only when this game/proxy revision exposes no valid attack helper.
                mob.life = 0;
                TryFinalizeHostMobDeath(mob);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MobsSync] Host boss finishing hit replay failed");
            }
        }

    }
}

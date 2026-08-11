using System.Collections.Generic;
using System.Runtime.CompilerServices;
using dc.en;
using DeadCellsMultiplayerMod.Mobs.Bosses;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    // Phase 2 (bosses-only, additive): a stable per-boss identity that survives native
    // phase/proxy rebuilds and sync-id churn.
    //
    // The host assigns every boss a monotonic EntityId and stamps it into the boss state
    // payload (the "bid:" token, alongside "bb:1"). Boss MOBSTATE is sent on a tight cadence,
    // so once the client has learned "EntityId -> local boss", every later snapshot re-points
    // the incoming (possibly changed) sync id back to the same local boss by identity instead
    // of by proximity. Because the sync-id map is continuously kept correct, all downstream
    // packets that key off the sync id (attacks, hits, MOBDIE) resolve normally with no format
    // changes.
    //
    // Everything here is inert when the identity is absent (EntityId == 0): a peer that never
    // stamps a bid behaves exactly as before. Only bosses (BossSyncHelpers.IsBossMob) ever get
    // an id; ordinary mobs are untouched.
    public partial class MobsSynchronization
    {
        // ============================ HOST ============================

        private sealed class HostBossIdentity
        {
            public int EntityId;
        }

        // Keyed on the (weak) native boss reference so a boss whose object is stable across a
        // phase change keeps its id for free.
        private static ConditionalWeakTable<Mob, HostBossIdentity> s_hostBossIdentities = new();

        // Session-monotonic: ids never repeat across arenas, so a stale client binding from a
        // previous level can never be mistaken for a new boss. Client bindings reset per level.
        private static int s_nextHostBossEntityId = 1;

        // The arena's primary boss (Level.boss) is frequently rebuilt behind a new proxy on a
        // phase transition. The replacement inherits the prior id so the main encounter keeps a
        // single stable identity even when its native object is swapped.
        private static int s_hostPrimaryBossEntityId;

        // Phase 3: per-boss monotonic attack sequence, keyed by stable EntityId so it stays
        // monotonic across native rebuilds (the primary boss inherits its EntityId). Reset per arena.
        private static readonly Dictionary<int, int> s_hostNextAttackSeqByEntityId = new();

        /// <summary>
        /// Host-only. Returns the stable EntityId for a boss (NetId+1 when mapped, else assign), or 0
        /// for non-bosses / when not hosting. Safe to call while holding <see cref="Sync"/>
        /// (Monitor is re-entrant).
        /// </summary>
        internal static int GetOrAssignHostBossEntityId(Mob mob)
        {
            if (mob == null || !IsHost(LobbySession.NetRef))
                return 0;

            try
            {
                if (!BossSyncHelpers.IsBossMob(mob))
                    return 0;
            }
            catch
            {
                return 0;
            }

            lock (Sync)
            {
                // Prefer the host NetId space so bid: and sync Index stay aligned.
                if (MobToId.TryGetValue(mob, out var netId) && netId >= 0)
                {
                    var fromNetId = netId + 1;
                    if (s_hostBossIdentities.TryGetValue(mob, out var existingFromNet) &&
                        existingFromNet.EntityId == fromNetId)
                    {
                        TrackHostPrimaryBossLocked(mob, fromNetId);
                        return fromNetId;
                    }

                    s_hostBossIdentities.Remove(mob);
                    s_hostBossIdentities.Add(mob, new HostBossIdentity { EntityId = fromNetId });
                    TrackHostPrimaryBossLocked(mob, fromNetId);
                    return fromNetId;
                }

                if (s_hostBossIdentities.TryGetValue(mob, out var existing) && existing.EntityId > 0)
                {
                    TrackHostPrimaryBossLocked(mob, existing.EntityId);
                    return existing.EntityId;
                }

                var inherited = TryInheritHostPrimaryBossEntityIdLocked(mob);
                var id = inherited > 0 ? inherited : s_nextHostBossEntityId++;
                s_hostBossIdentities.Remove(mob);
                s_hostBossIdentities.Add(mob, new HostBossIdentity { EntityId = id });
                TrackHostPrimaryBossLocked(mob, id);

                if (inherited <= 0)
                    BossSyncDiag.Trace("host boss identity assigned entityId={EntityId} type={Type}", id, SafeBossType(mob));
                else
                    BossSyncDiag.Trace("host boss identity inherited (primary rebuild) entityId={EntityId} type={Type}", id, SafeBossType(mob));

                return id;
            }
        }

        private static int TryInheritHostPrimaryBossEntityIdLocked(Mob mob)
        {
            if (s_hostPrimaryBossEntityId <= 0)
                return 0;

            try
            {
                var level = mob._level;
                if (level != null && ReferenceEquals(level.boss, mob))
                    return s_hostPrimaryBossEntityId;
            }
            catch
            {
                // ignore
            }

            return 0;
        }

        private static void TrackHostPrimaryBossLocked(Mob mob, int entityId)
        {
            try
            {
                var level = mob._level;
                if (level != null && ReferenceEquals(level.boss, mob))
                    s_hostPrimaryBossEntityId = entityId;
            }
            catch
            {
                // ignore
            }
        }

        private static string SafeBossType(Mob mob)
        {
            try
            {
                return BuildMobStateTypeSignature(mob);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Host-only. Detects the multi-phase pattern where a depleted boss object is destroyed by
        /// its own onDie() and the encounter continues behind a rebuilt native object: if the
        /// arena's current <c>Level.boss</c> is a different living object carrying (or pending
        /// inheritance of) the same stable EntityId, the death is a phase hand-off, not a victory.
        /// Identity-gated on purpose — without a matching EntityId this never suppresses a death,
        /// so duo arenas with two living bosses of one type keep real deaths intact.
        /// </summary>
        internal static bool TryGetHostBossPhaseSuccessor(Mob mob, out Mob successor)
        {
            successor = null!;
            if (mob == null || !IsHost(LobbySession.NetRef))
                return false;

            try
            {
                var level = mob._level;
                var candidate = level?.boss as Mob;
                if (candidate == null || ReferenceEquals(candidate, mob))
                    return false;
                if (candidate.destroyed || candidate.life <= 0)
                    return false;

                var handoffEntityId = 0;
                lock (Sync)
                {
                    if (!s_hostBossIdentities.TryGetValue(mob, out var own) || own.EntityId <= 0)
                        return false;

                    if (s_hostBossIdentities.TryGetValue(candidate, out var next) && next.EntityId > 0)
                    {
                        if (next.EntityId != own.EntityId)
                            return false;
                    }
                    else if (s_hostPrimaryBossEntityId != own.EntityId)
                    {
                        // The rebuilt object has not been seen by the sync layer yet. It only
                        // counts as the same encounter if it would inherit this boss's id via the
                        // primary-boss (Level.boss) inheritance rule on first sight.
                        return false;
                    }

                    handoffEntityId = own.EntityId;
                }

                BossSyncDiag.Trace(
                    "host boss death treated as phase hand-off entityId={EntityId} type={Type}",
                    handoffEntityId,
                    SafeBossType(mob));

                successor = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Host-only. Returns the next per-boss monotonic attack sequence (>= 1) for a boss, or 0 for
        /// a non-boss / when not hosting. The client uses it as a high-water mark to drop replayed or
        /// out-of-order boss attacks.
        /// </summary>
        internal static int NextHostBossAttackSeq(Mob mob)
        {
            var entityId = GetOrAssignHostBossEntityId(mob);
            if (entityId <= 0)
                return 0;

            lock (Sync)
            {
                s_hostNextAttackSeqByEntityId.TryGetValue(entityId, out var current);
                var next = current + 1;
                s_hostNextAttackSeqByEntityId[entityId] = next;
                return next;
            }
        }

        // ============================ CLIENT ============================

        // Learned "stable boss identity -> local boss" bindings for the current arena. Cleared on
        // every mob-registry rebuild (level change) via ResetBossIdentityStateLocked.
        private static readonly Dictionary<int, Mob> s_clientBossByEntityId = new();
        private static readonly Dictionary<Mob, int> s_clientEntityIdByBoss = new(ReferenceEqualityComparer.Instance);

        // Phase 3: highest boss attack sequence already applied, per stable EntityId. An incoming
        // boss attack whose seq is <= this is a replay / out-of-order duplicate and is dropped.
        private static readonly Dictionary<int, int> s_clientLastAppliedAttackSeqByEntityId = new();

        /// <summary>Record (or refresh) the local boss bound to a stable identity. Caller holds <see cref="Sync"/>.</summary>
        private static void RememberClientBossEntityIdLocked(Mob? mob, int entityId)
        {
            if (mob == null || entityId <= 0)
                return;

            // Evict a stale reference that previously claimed this id (its native object rebuilt).
            if (s_clientBossByEntityId.TryGetValue(entityId, out var prior) && !ReferenceEquals(prior, mob))
            {
                if (prior != null && s_clientEntityIdByBoss.TryGetValue(prior, out var priorId) && priorId == entityId)
                    s_clientEntityIdByBoss.Remove(prior);
            }

            // Evict any different id this exact reference used to hold (should not happen, keep clean).
            if (s_clientEntityIdByBoss.TryGetValue(mob, out var existingId) && existingId != entityId)
                s_clientBossByEntityId.Remove(existingId);

            s_clientBossByEntityId[entityId] = mob;
            s_clientEntityIdByBoss[mob] = entityId;
        }

        /// <summary>
        /// Identity-primary resolution. If a live local boss was already learned for this id, rebind
        /// the incoming sync id onto it and return it. Never uses proximity, and disambiguates any
        /// number of same-type bosses. Caller holds <see cref="Sync"/>.
        /// </summary>
        private static bool TryResolveClientBossByEntityIdLocked(int entityId, int incomingSyncId, HashSet<Mob>? reservedMobs, out Mob? mob)
        {
            mob = null;
            if (entityId <= 0)
                return false;

            if (!s_clientBossByEntityId.TryGetValue(entityId, out var bound) || bound == null)
                return false;

            if (reservedMobs != null && reservedMobs.Contains(bound))
                return false;

            if (!IsStateRebindCandidateLocked(bound))
            {
                // The learned boss is gone (local phase rebuild / destroy). Forget it so the
                // fallback path can relearn the identity against the surviving/new boss.
                ForgetClientBossEntityIdLocked(entityId);
                return false;
            }

            if (incomingSyncId >= 0 && (!MobToId.TryGetValue(bound, out var currentId) || currentId != incomingSyncId))
                TryRebindTrackedMobSyncIdLocked(bound, incomingSyncId);

            mob = bound;
            return true;
        }

        private static void ForgetClientBossEntityIdLocked(int entityId)
        {
            if (entityId <= 0)
                return;

            if (s_clientBossByEntityId.TryGetValue(entityId, out var prior) && prior != null &&
                s_clientEntityIdByBoss.TryGetValue(prior, out var priorId) && priorId == entityId)
            {
                s_clientEntityIdByBoss.Remove(prior);
            }

            s_clientBossByEntityId.Remove(entityId);
        }

        /// <summary>
        /// True when a candidate boss is already claimed by a different, still-living identity. The
        /// unique-of-type fallback skips such candidates so that, during a rebuild in a multi-boss
        /// arena, only the unbound (newly rebuilt) boss remains a candidate for a not-yet-learned id.
        /// Caller holds <see cref="Sync"/>.
        /// </summary>
        private static bool IsBossClaimedByOtherLivingEntityLocked(Mob candidate, int currentEntityId)
        {
            if (candidate == null)
                return false;
            if (!s_clientEntityIdByBoss.TryGetValue(candidate, out var boundId) || boundId == currentEntityId)
                return false;

            return IsStateRebindCandidateLocked(candidate);
        }

        /// <summary>
        /// Phase 3 replay/reorder guard — <b>pure check</b>. Returns true when an incoming boss attack
        /// should be dropped because a newer-or-equal sequence was already applied for the same stable
        /// identity. Has NO side effects: the high-water mark is only advanced by
        /// <see cref="MarkBossAttackAppliedLocked"/> after the attack is actually queued, so an attack
        /// that could not be dispatched (unresolved mob, buffered for retry) never wrongly blocks a
        /// later legitimate delivery. Caller holds <see cref="Sync"/>.
        /// </summary>
        /// <remarks>
        /// No-ops (returns false, applies the attack) when there is no sequence yet (seq &lt;= 0) or the
        /// boss identity has not been learned, so pre-Phase-3 peers and the very first attack are never
        /// dropped.
        /// </remarks>
        private static bool IsReplayedBossAttackLocked(Mob? mob, int attackSeq)
        {
            if (mob == null || attackSeq <= 0)
                return false;

            if (!s_clientEntityIdByBoss.TryGetValue(mob, out var entityId) || entityId <= 0)
                return false;

            if (s_clientLastAppliedAttackSeqByEntityId.TryGetValue(entityId, out var lastApplied) && attackSeq <= lastApplied)
            {
                BossSyncDiag.Trace(
                    "client boss attack replay dropped entityId={EntityId} seq={Seq} lastApplied={LastApplied}",
                    entityId,
                    attackSeq,
                    lastApplied);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Advances the per-identity attack high-water mark to <paramref name="attackSeq"/> once the
        /// attack has actually been dispatched to the client attack processor. Monotonic (never lowers
        /// the mark). Caller holds <see cref="Sync"/>.
        /// </summary>
        private static void MarkBossAttackAppliedLocked(Mob? mob, int attackSeq)
        {
            if (mob == null || attackSeq <= 0)
                return;

            if (!s_clientEntityIdByBoss.TryGetValue(mob, out var entityId) || entityId <= 0)
                return;

            if (!s_clientLastAppliedAttackSeqByEntityId.TryGetValue(entityId, out var lastApplied) || attackSeq > lastApplied)
                s_clientLastAppliedAttackSeqByEntityId[entityId] = attackSeq;
        }

        /// <summary>Called from <see cref="ResetMobTrackingStateLocked"/> (caller holds <see cref="Sync"/>).</summary>
        private static void ResetBossIdentityStateLocked()
        {
            s_clientBossByEntityId.Clear();
            s_clientEntityIdByBoss.Clear();
            s_clientLastAppliedAttackSeqByEntityId.Clear();

            // A registry rebuild means a fresh arena. Host boss identities are keyed to the old
            // arena's (weak) references and its Level.boss, so drop them; the monotonic counter is
            // intentionally NOT reset so ids stay globally unique across arenas.
            s_hostBossIdentities = new ConditionalWeakTable<Mob, HostBossIdentity>();
            s_hostPrimaryBossEntityId = 0;
            s_hostNextAttackSeqByEntityId.Clear();
        }
    }
}

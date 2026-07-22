using System;
using System.Collections.Generic;
using dc.en;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        // Host-side despawn detection for boss-OWNED transient parts (Death's thrown scythe,
        // giant projectiles, spawner shots, etc.). The sync layer streams state for entities that
        // appear and never sends an explicit per-entity removal, so when such a part vanishes on
        // the host the client keeps rendering its copy until a NEW attack recycles the sync id.
        // That is exactly the "Death scythe spins on the arena until Death repeats the attack"
        // report. We watch the parts we were tracking and, when one is gone on the host, send the
        // authoritative MOBDIE the client already knows how to apply (it retires the ghost via the
        // mob's own update cycle).
        //
        // Scope guards keep this conservative:
        //   * Boss-owned PARTS only (IsBossRelatedEntity by type), never the encounter boss itself
        //     — real boss death keeps its dedicated authoritative path.
        //   * Only parts we positively saw alive last tick, so nothing is invented.
        //   * Absence is confirmed for two consecutive host ticks before emitting, so a one-frame
        //     registry gap during a rebuild cannot produce a spurious kill.

        private static readonly Dictionary<int, BossPartWatchEntry> s_hostBossPartWatch =
            new();

        private readonly struct BossPartWatchEntry
        {
            public BossPartWatchEntry(double x, double y, int generation, string type, int missingTicks)
            {
                X = x;
                Y = y;
                Generation = generation;
                Type = type;
                MissingTicks = missingTicks;
            }

            public double X { get; }
            public double Y { get; }
            public int Generation { get; }
            public string Type { get; }
            public int MissingTicks { get; }
        }

        private const int BossPartDespawnConfirmTicks = 2;

        private static void ScanHostBossPartDespawns(NetNode net)
        {
            if (net == null || !IsHost(net))
                return;
            if (IsSyncQuiescedForTransition())
                return;

            s_bossPartSeenScratch.Clear();

            int identityToken;
            if (!TryGetCurrentLevelIdentityToken(out identityToken))
                identityToken = 0;

            lock (Sync)
            {
                // 1. Record every currently-live tracked boss-owned part by sync id.
                for (var i = 0; i < trackedMobs.Count; i++)
                {
                    var mob = trackedMobs[i];
                    if (mob == null)
                        continue;

                    bool alive;
                    try { alive = !mob.destroyed && mob.life > 0; }
                    catch { continue; }
                    if (!alive)
                        continue;

                    // Owned parts only — never the encounter boss (its death is authoritative).
                    if (!IsBossOwnedTransientPart(mob))
                        continue;

                    if (!MobToId.TryGetValue(mob, out var syncId) || syncId < 0)
                        continue;

                    double x, y;
                    try { x = GetWorldX(mob); y = GetWorldY(mob); }
                    catch { x = 0.0; y = 0.0; }

                    var type = SafePartType(mob);
                    s_bossPartSeenScratch.Add(syncId);
                    s_hostBossPartWatch[syncId] = new BossPartWatchEntry(x, y, identityToken, type, 0);
                }

                // 2. Any watched part not seen this tick is a despawn candidate. Confirm across
                //    BossPartDespawnConfirmTicks ticks, then emit MOBDIE and drop the watch entry.
                s_bossPartDespawnScratch.Clear();
                foreach (var kvp in s_hostBossPartWatch)
                {
                    if (s_bossPartSeenScratch.Contains(kvp.Key))
                        continue;

                    var entry = kvp.Value;
                    var missing = entry.MissingTicks + 1;
                    if (missing >= BossPartDespawnConfirmTicks)
                        s_bossPartDespawnScratch.Add(kvp.Key);
                    else
                        s_hostBossPartWatch[kvp.Key] = new BossPartWatchEntry(
                            entry.X, entry.Y, entry.Generation, entry.Type, missing);
                }

                for (var i = 0; i < s_bossPartDespawnScratch.Count; i++)
                {
                    var syncId = s_bossPartDespawnScratch[i];
                    if (s_hostBossPartWatch.TryGetValue(syncId, out var entry))
                    {
                        s_hostBossPartDespawnSendScratch.Add(entry);
                        s_hostBossPartDespawnSendIdScratch.Add(syncId);
                        s_hostBossPartWatch.Remove(syncId);
                    }
                }
            }

            // 3. Send outside the lock — SendMobDie touches the network layer.
            for (var i = 0; i < s_hostBossPartDespawnSendIdScratch.Count; i++)
            {
                var syncId = s_hostBossPartDespawnSendIdScratch[i];
                var entry = s_hostBossPartDespawnSendScratch[i];
                try
                {
                    net.SendMobDie(syncId, entry.X, entry.Y, entry.Generation, entry.Type);
                    if (BossSyncDiag.Enabled)
                        Log.Information(
                            "[MobSync] boss-owned part despawn -> MOBDIE syncId={SyncId} type={Type}",
                            syncId,
                            entry.Type);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[MobSync] boss part despawn send failed syncId={SyncId}", syncId);
                }
            }

            s_hostBossPartDespawnSendScratch.Clear();
            s_hostBossPartDespawnSendIdScratch.Clear();
        }

        private static bool IsBossOwnedTransientPart(Mob mob)
        {
            if (mob == null)
                return false;

            try
            {
                // Never the encounter boss itself.
                if (BossSyncHelpers.IsBossEncounterCombatant(mob))
                    return false;

                var level = mob._level;
                if (level != null && ReferenceEquals(level.boss, mob))
                    return false;

                return IsBossRelatedEntity(SafePartType(mob));
            }
            catch
            {
                return false;
            }
        }

        private static string SafePartType(Mob mob)
        {
            try { return BuildMobStateTypeSignature(mob); }
            catch { return string.Empty; }
        }

        private static void ResetHostBossPartWatch()
        {
            lock (Sync)
            {
                s_hostBossPartWatch.Clear();
            }
        }

        private static readonly HashSet<int> s_bossPartSeenScratch = new();
        private static readonly List<int> s_bossPartDespawnScratch = new();
        private static readonly List<BossPartWatchEntry> s_hostBossPartDespawnSendScratch = new();
        private static readonly List<int> s_hostBossPartDespawnSendIdScratch = new();
    }
}

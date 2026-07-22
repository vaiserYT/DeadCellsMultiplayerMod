using System;
using System.Collections.Generic;
using dc.en;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private static void RememberHostDeathTombstoneLocked(Mob mob, int syncId, double x, double y, int generation)
        {
            if (mob == null || syncId < 0 || generation <= 0)
                return;

            var dir = 0;
            try { dir = NormalizeDir(mob.dir); } catch { }

            var type = string.Empty;
            try { type = BuildMobStateTypeSignature(mob); } catch { }

            var statePayload = string.Empty;
            try { statePayload = BuildHostMobStatePayload(mob); } catch { }

            var tombstone = new HostDeathTombstone
            {
                SyncId = syncId,
                Generation = generation,
                X = x,
                Y = y,
                Dir = dir,
                MaxLife = Math.Max(1, SafeMobMaxLife(mob)),
                Type = type,
                StatePayload = statePayload,
                CreatedFrame = GetCurrentFrame(mob),
                LastSentFrame = -99999.0,
                SendsRemaining = HostAuthoritativeDeathTombstoneResendCount
            };

            s_hostDeathTombstonesBySyncId[syncId] = tombstone;
        }

        private static int SafeMobMaxLife(Mob mob)
        {
            try
            {
                return mob.maxLife > 0 ? mob.maxLife : 1;
            }
            catch
            {
                return 1;
            }
        }

        private static void FlushHostDeathTombstoneResends(NetNode net)
        {
            if (!IsHost(net))
                return;
            if (!TryGetCurrentLevelIdentityToken(out var identityToken))
                return;

            s_hostDeathTombstoneScratch.Clear();
            s_hostDeathTombstoneRemoveScratch.Clear();
            var frame = GetCurrentFrame(null);
            lock (Sync)
            {
                if (s_hostDeathTombstonesBySyncId.Count == 0)
                    return;

                foreach (var pair in s_hostDeathTombstonesBySyncId)
                {
                    var t = pair.Value;
                    if (t == null)
                        continue;

                    var expired = t.Generation != identityToken ||
                                  frame - t.CreatedFrame >= HostAuthoritativeDeathTombstoneRetainFrames ||
                                  t.SendsRemaining <= 0;
                    if (expired)
                    {
                        s_hostDeathTombstoneRemoveScratch.Add(pair.Key);
                        continue;
                    }

                    if (frame - t.LastSentFrame >= HostAuthoritativeDeathTombstoneResendIntervalFrames)
                    {
                        t.LastSentFrame = frame;
                        t.SendsRemaining--;
                        s_hostDeathTombstoneScratch.Add(t);
                    }
                }

                if (s_hostDeathTombstoneRemoveScratch.Count > 0)
                {
                    for (int i = 0; i < s_hostDeathTombstoneRemoveScratch.Count; i++)
                        s_hostDeathTombstonesBySyncId.Remove(s_hostDeathTombstoneRemoveScratch[i]);
                    s_hostDeathTombstoneRemoveScratch.Clear();
                }
            }

            if (s_hostDeathTombstoneScratch.Count == 0)
                return;

            s_hostDeathTombstoneStateScratch.Clear();
            for (int i = 0; i < s_hostDeathTombstoneScratch.Count; i++)
            {
                var t = s_hostDeathTombstoneScratch[i];
                if (t == null)
                    continue;

                try
                {
                    net.SendMobDie(t.SyncId, t.X, t.Y, t.Generation, t.Type);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[MobSync] host tombstone MOBDIE resend failed syncId={SyncId}", t.SyncId);
                }

                s_hostDeathTombstoneStateScratch.Add(new NetNode.MobStateSnapshot(
                    t.SyncId,
                    t.X,
                    t.Y,
                    t.Dir,
                    0,
                    t.MaxLife,
                    string.Empty,
                    t.Type,
                    EncodeStatePayloadForWire(t.StatePayload),
                    t.Generation,
                    frame,
                    0.0,
                    0.0));

                if (s_hostDeathTombstoneStateScratch.Count >= 32)
                {
                    TrySendHostStatesBatchAsync(net, s_hostDeathTombstoneStateScratch);
                    s_hostDeathTombstoneStateScratch.Clear();
                }
            }

            if (s_hostDeathTombstoneStateScratch.Count > 0)
            {
                TrySendHostStatesBatchAsync(net, s_hostDeathTombstoneStateScratch);
                s_hostDeathTombstoneStateScratch.Clear();
            }

            s_hostDeathTombstoneScratch.Clear();
        }

        private static void FlushPendingClientAuthoritativeDeaths()
        {
            if (!IsClient(GameMenu.NetRef))
                return;

            var pending = new List<Mob>();
            var frame = GetCurrentFrame(null);
            lock (Sync)
            {
                if (s_pendingCulledMobDeaths.Count == 0)
                    return;

                pending.AddRange(s_pendingCulledMobDeaths);
            }

            for (int i = 0; i < pending.Count; i++)
            {
                var mob = pending[i];
                if (mob == null)
                    continue;

                try
                {
                    if (mob.destroyed)
                    {
                        ClearPendingClientAuthoritativeDeath(mob);
                        continue;
                    }
                }
                catch
                {
                    ClearPendingClientAuthoritativeDeath(mob);
                    continue;
                }

                var firstFrame = frame;
                lock (Sync)
                {
                    if (s_pendingCulledMobDeathFirstFrame.TryGetValue(mob, out var storedFrame))
                        firstFrame = storedFrame;
                }

                var age = frame - firstFrame;
                var culled = IsMobCulledLocally(mob);

                if (culled && age < ClientPendingCulledDeathHideFrames)
                    continue;

                if (culled)
                {
                    HideClientDeadGhost(mob);
                    ClearPendingClientAuthoritativeDeath(mob);
                    continue;
                }

                if (RunAuthoritativeClientDeathNow(mob))
                    ClearPendingClientAuthoritativeDeath(mob);
            }
        }

        private static void ClearPendingClientAuthoritativeDeath(Mob mob)
        {
            lock (Sync)
            {
                s_pendingCulledMobDeaths.Remove(mob);
                s_pendingCulledMobDeathFirstFrame.Remove(mob);
            }
        }

        private static bool RunAuthoritativeClientDeathNow(Mob mob)
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
                return true;
            }

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

                var animManager = GetMobAnimManager(mob);
                if (animManager?.stack != null)
                {
                    while (animManager.stack.length > 0)
                        animManager.stack.pop();
                }

                return true;
            }
            catch
            {
                HideClientDeadGhost(mob);
                return true;
            }
        }

        private static void HideClientDeadGhost(Mob mob)
        {
            if (mob == null)
                return;

            try { mob.life = 0; } catch { }
            try { mob.isOnScreen = false; } catch { }
            try { mob.isOutOfGame = true; } catch { }
            try { mob.lastOutOfGame = true; } catch { }
        }
    }
}

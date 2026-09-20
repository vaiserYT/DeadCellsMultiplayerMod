using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using dc;
using dc.en;
using dc.h2d;
using dc.libs.heaps.slib;
using dc.libs.heaps.slib._AnimManager;
using dc.pr;
using dc.tool;
using dc.tool.atk;
using dc.tool.skill;
using DeadCellsMultiplayerMod.Ghost;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.Mobs.Bosses;
using DeadCellsMultiplayerMod.PortableCore;
using DeadCellsMultiplayerMod.Tools;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization
{
    public partial class MobsSynchronization
    {
        private static void Hook_Mob_onDie(Hook_Mob.orig_onDie orig, Mob self)
        {
            var lifecycleGeneration = 0;
            var lifecycleNetId = -1;
            var authoritativeClientDeath = false;
            if (self != null && IsClient(LobbySession.NetRef) &&
                System.Threading.Volatile.Read(ref authoritativeClientMobDieDepth) > 0 &&
                TryGetMobSyncId(self, out lifecycleNetId) &&
                TryGetCurrentLevelIdentityToken(out lifecycleGeneration))
            {
                lock (Sync)
                {
                    if (s_clientMobLifecycle.IsDead(lifecycleGeneration, lifecycleNetId))
                        return;
                }

                authoritativeClientDeath = true;
            }

            if (ShouldSuppressClientBossDie(self))
            {
                MarkSuppressedClientBossDie(self);
                MarkSuppressedClientMobDie(self);
                return;
            }

            var shouldSendDie = false;
            var dieSyncId = -1;
            var dieX = 0.0;
            var dieY = 0.0;
            var dieType = string.Empty;
            NetNode? dieNet = null;
            var isClient = false;
            var isBossDeathCandidate = false;
            if (self != null && suppressMobDieSendDepth <= 0)
            {
                dieNet = LobbySession.NetRef;
                isClient = IsClient(dieNet);
                isBossDeathCandidate = BossSyncHelpers.IsBossMob(self);

                // Client is not authoritative for mob death; wait for host confirmation. The
                // authoritative depth is set only while applying a host-confirmed death, allowing
                // vanilla onDie to run exactly once for normal mobs, elites and bosses.
                if (isClient && IsSyncMob(self) &&
                    System.Threading.Volatile.Read(ref authoritativeClientMobDieDepth) <= 0)
                {
                    MarkSuppressedClientMobDie(self);
                    try
                    {
                        if (self.life <= 0)
                            self.life = GetClientAuthoritativeLifeFallback(self, 1);
                    }
                    catch
                    {
                    }

                    return;
                }

                if (dieNet != null &&
                    dieNet.IsAlive &&
                    dieNet.IsHost &&
                    TryGetMobSyncId(self, out dieSyncId))
                {
                    shouldSendDie = true;
                    dieX = GetSyncX(self);
                    dieY = GetSyncY(self);
                    dieType = BuildMobStateTypeSignature(self);
                }
            }

            orig(self);

            if (self == null)
                return;

            ClearSuppressedClientBossDie(self);

            if (authoritativeClientDeath)
            {
                var stillAlive = false;
                try { stillAlive = !self.destroyed && self.life > 0; } catch { }
                if (!stillAlive)
                {
                    lock (Sync)
                        s_clientMobLifecycle.MarkDead(lifecycleGeneration, lifecycleNetId);
                }
            }

            // Some multi-phase bosses route a depleted phase through onDie(), then rebuild the
            // same encounter with positive life.  That is not a victory.  Keep its authoritative
            // mapping and immediately publish the rebuilt phase instead of sending a death packet,
            // removing tracking, or reviving players early.
            if (shouldSendDie && isBossDeathCandidate)
            {
                var bossContinues = false;
                try { bossContinues = !self.destroyed && self.life > 0; } catch { }
                if (bossContinues)
                {
                    QueueHostMobDirty(self, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                    return;
                }

                // Some phase transitions destroy the depleted native object outright and rebuild
                // the encounter behind a new one, so "!destroyed" alone misreads them as a real
                // death. If a different living Level.boss carries the same stable identity, this
                // is a hand-off: publish the rebuilt phase immediately instead of a death packet.
                if (TryGetHostBossPhaseSuccessor(self, out var phaseSuccessor))
                {
                    QueueHostMobDirty(phaseSuccessor, HostMobDirtyFlags.State | HostMobDirtyFlags.ForceState);
                    return;
                }
            }

            if (shouldSendDie && dieNet != null && dieNet.IsAlive && dieSyncId >= 0)
            {
                if (TryGetCurrentLevelIdentityToken(out var identityToken))
                {
                    lock (Sync)
                    {
                        RememberHostDeathTombstoneLocked(self, dieSyncId, dieX, dieY, identityToken);
                    }

                    // Authoritative death: one reliable MOBDIE path (no dual MOBEVENT|die).
                    dieNet.SendMobDie(dieSyncId, dieX, dieY, identityToken, dieType);
                }
            }

            lock (Sync)
            {
                RemoveTrackedMobLocked(self, "on_die");
            }
        }

        private static void RunWithSuppressedMobDieSend(Action action)
        {
            if (action == null)
                return;

            suppressMobDieSendDepth++;
            try
            {
                action();
            }
            finally
            {
                suppressMobDieSendDepth--;
            }
        }

    }
}

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
        private static void MarkSuppressedClientMobDie(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientPendingSuppressedMobDies.Add(mob);
            }
        }

        private static bool WasClientMobDeathSuppressed(Mob? mob)
        {
            if (mob == null)
                return false;

            lock (Sync)
            {
                return clientPendingSuppressedMobDies.Contains(mob) ||
                       clientPendingSuppressedBossDies.Contains(mob);
            }
        }

        private static int GetClientAuthoritativeLifeFallback(Mob? mob, int fallbackLife)
        {
            var recovered = System.Math.Max(1, fallbackLife);
            if (mob == null)
                return recovered;

            lock (Sync)
            {
                if (clientMobTargets.TryGetValue(mob, out var target) && target.Life > 0)
                    recovered = System.Math.Max(recovered, target.Life);
            }

            return recovered;
        }

        private static void TryRecoverSuppressedClientMobDie(Mob? mob, int fallbackLife)
        {
            if (mob == null)
                return;

            bool hadSuppressedDie;
            lock (Sync)
            {
                hadSuppressedDie = clientPendingSuppressedMobDies.Remove(mob);
            }

            if (!hadSuppressedDie)
                return;

            try
            {
                if (!mob.destroyed && mob.life <= 0)
                    mob.life = GetClientAuthoritativeLifeFallback(mob, fallbackLife);
            }
            catch
            {
            }

            // Restore the older native handoff after a locally suppressed lethal callback. The
            // regular client authority update will relock the replica when no native attack/phase
            // callback remains active.
            TryUnlockClientMobAiAuthority(mob);
        }

        private static bool ShouldSuppressClientBossDie(Mob? mob)
        {
            if (mob == null || !BossSyncHelpers.IsBossMob(mob))
                return false;

            var net = LobbySession.NetRef;
            if (!IsClient(net))
                return false;
            if (!IsSyncMob(mob))
                return false;

            return System.Threading.Volatile.Read(ref authoritativeClientBossDieDepth) <= 0;
        }

        private static void MarkSuppressedClientBossDie(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientPendingSuppressedBossDies.Add(mob);
            }
        }

        private static void ClearSuppressedClientBossDie(Mob? mob)
        {
            if (mob == null)
                return;

            lock (Sync)
            {
                clientPendingSuppressedBossDies.Remove(mob);
            }
        }

        private static void TryRecoverSuppressedClientBossDie(Mob? mob, int fallbackLife)
        {
            if (mob == null || mob.destroyed)
                return;

            var net = LobbySession.NetRef;
            if (!IsClient(net))
            {
                ClearSuppressedClientBossDie(mob);
                return;
            }

            bool hadSuppressedDie;
            lock (Sync)
            {
                hadSuppressedDie = clientPendingSuppressedBossDies.Remove(mob);
            }

            if (!hadSuppressedDie)
                return;

            try
            {
                if (mob.life <= 0)
                    mob.life = System.Math.Max(1, fallbackLife);
            }
            catch
            {
            }
        }

        private static void RunWithAuthoritativeClientMobDie(Mob? mob, Action action)
        {
            if (action == null)
                return;

            var net = LobbySession.NetRef;
            if (!IsClient(net) || mob == null || !IsSyncMob(mob))
            {
                action();
                return;
            }

            var isBoss = BossSyncHelpers.IsBossMob(mob);
            authoritativeClientMobDieDepth++;
            if (isBoss)
                authoritativeClientBossDieDepth++;
            try
            {
                action();
            }
            finally
            {
                if (isBoss)
                    authoritativeClientBossDieDepth--;
                authoritativeClientMobDieDepth--;
            }
        }

        /// <summary>
        /// Reads the pre-target-resolution damage the attack was built from.
        /// <c>AttackUtils.createFromHero(source, baseDmg, tier)</c> takes exactly this value, so it
        /// is the only scalar the host can feed back into the native hit path without re-deriving a
        /// number that was already derived once. <c>finalDmg</c>/<c>inflictedDmg</c> are produced by
        /// <c>updateDamages(attack, target)</c> and <c>applyHitResult</c>: they are per-target and
        /// already contain this replica's armour, resistances and invulnerability, so transmitting
        /// them would let a client-side outcome dictate the authoritative result.
        /// </summary>
    }
}

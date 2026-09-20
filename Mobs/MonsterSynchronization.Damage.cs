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
        private static void RunWithSuppressedMobHitSend(Action action)
        {
            if (action == null)
                return;

            suppressMobHitSendDepth++;
            try
            {
                action();
            }
            finally
            {
                suppressMobHitSendDepth--;
            }
        }

        private void Hook_Mob_onDamage(Hook_Mob.orig_onDamage orig, Mob self, AttackData i)
        {
            var preDamageLife = GetMobLifeOrFallback(self, 0);
            // Lethal damage runs onDie inside orig, which removes the mob from tracking before this hook resumes.
            // Cache ids before orig so hit|life still sends when the mob is already untracked/destroyed.
            var preSyncOk = false;
            var cachedMobSyncId = -1;
            if (self != null && i != null && LobbySession.NetRef != null && IsSyncMob(self))
            {
                preSyncOk = TryGetMobSyncId(self, out cachedMobSyncId);
            }

            // Read the attack's construction input before vanilla resolves the hit: resolution
            // rewrites the per-target damage fields, and AttackData instances are recycled.
            var attackIntentDamage = ReadAttackIntentDamage(i);

            orig(self, i);

            try
            {
                if (self == null || i == null)
                    return;

                var net = LobbySession.NetRef;
                if (net == null)
                    return;

                if (!IsSyncMob(self) && !preSyncOk)
                    return;

                var isClient = IsClient(net);
                var suppressedClientLethal = isClient && WasClientMobDeathSuppressed(self);
                var tookLifeDelta = self.life < preDamageLife || suppressedClientLethal;
                var becameDead = self.life <= 0 || suppressedClientLethal;
                bool shouldReport = false;
                if (IsHost(net))
                {
                    shouldReport = true;
                }
                else if (isClient)
                {
                    shouldReport = IsDamageFromLocalPlayer(i);
                    // Fallback for damage-source edge cases: never drop a lethal hit report.
                    if (!shouldReport && tookLifeDelta && becameDead)
                        shouldReport = true;
                }

                if (!shouldReport)
                    return;

                if (System.Threading.Volatile.Read(ref suppressMobHitSendDepth) > 0)
                    return;

                if (!TryGetMobSyncId(self, out var mobSyncId))
                {
                    if (!preSyncOk || !shouldReport)
                        return;
                    mobSyncId = cachedMobSyncId;
                }

                // NetId 0 is reserved. Never report damage against it — that is the courtyard
                // syncId=0 thrash vector once the real owner was gone.
                if (mobSyncId <= 0)
                    return;

                // A locally lethal client hit temporarily restores the mob so the client does not
                // run an unsanctioned death. Still report life=0 to the host; reporting the restored
                // value (usually 1) was the source of elites becoming permanently unkillable.
                var life = suppressedClientLethal ? 0 : GetMobLifeOrFallback(self, 0);
                var damageHint = isClient && shouldReport
                    ? EstimateClientAttackDamageHint(attackIntentDamage, preDamageLife, life, suppressedClientLethal)
                    : 0.0;
                var x = GetSyncX(self);
                var y = GetSyncY(self);
                var mobType = BuildMobStateTypeSignature(self);

                if (IsHost(net))
                {
                    var hx = GetWorldX(self);
                    var hy = GetWorldY(self);
                    var hitEvent = $"hit|{life.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    if (TryGetCurrentLevelIdentityToken(out var identityToken))
                    {
                        var update = new NetNode.MobEventUpdate(mobSyncId, hx, hy, NormalizeDir(self.dir), SingleEvent(hitEvent), mobType, identityToken);
                        MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(update));
                        net.SendMobEvents(SingleUpdate(update));
                    }
                }

                if (IsClient(net))
                {
                    lock (Sync)
                    {
                        if (!clientLastReportedMobLife.TryGetValue(self, out var lastLife))
                        {
                            // First locally-confirmed hit for this tracked mob: establish baseline and
                            // propagate immediately when damage actually reduced life.
                            clientLastReportedMobLife[self] = life;
                            var maxLife = self.maxLife;
                            if (life >= maxLife && life > 0 && damageHint <= 0.0)
                                return;
                        }
                        else
                        {
                            // Never drop a killing blow: stale lastLife or host sync can make life look non-decreasing.
                            if (life >= lastLife)
                            {
                                var lethalReport = shouldReport && life <= 0 && lastLife > 0 && preDamageLife > 0;
                                var authoritativeProbe = shouldReport && damageHint > 0.0;
                                if (!lethalReport && !authoritativeProbe)
                                    return;
                            }

                            clientLastReportedMobLife[self] = life;
                        }
                    }

                    var clientHitEvent = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"hit|{life}|{damageHint:R}");
                    if (TryGetCurrentLevelIdentityToken(out var identityToken))
                    {
                        var clientUpdate = new NetNode.MobEventUpdate(mobSyncId, x, y, 0, SingleEvent(clientHitEvent), mobType, identityToken);
                        MobSyncTrace.LogSendMobEvents(MobSyncNetRoleForTrace(net), SingleUpdate(clientUpdate));
                        net.SendMobEvents(SingleUpdate(clientUpdate));
                    }
                }
            }
            finally
            {
                TryRecoverClientSyncMobLifeAfterLocalDamage(self, preDamageLife);
                TryRecoverSuppressedClientMobDie(self, preDamageLife);
                TryRecoverSuppressedClientBossDie(self, preDamageLife);
            }
        }

    }
}

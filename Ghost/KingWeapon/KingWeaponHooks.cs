using dc;
using dc.en;
using dc.en.loot;
using dc.hl.types;
using dc.tool;
using dc.tool.atk;
using dc.tool.weap;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.Ghost;

internal static class KingWeaponHooks
{
    private static bool _installed;
    private const double SuppressLocalHeroSkillLockMaxSeconds = 0.35;

#if DEBUG
    private static void LogKingWeaponHookEx(Exception ex, string hookName)
    {
        try
        {
            ModEntry.Instance?.Logger.Warning(ex, "[KingWeapon] {Hook}", hookName);
        }
        catch
        {
        }
    }
#else
    private static void LogKingWeaponHookEx(Exception ex, string hookName)
    {
    }
#endif

    internal static void Install()
    {
        if(_installed)
            return;
        _installed = true;

        Hook_Inventory.add += Hook_Inventory_add;
        Hook_Inventory.equip += Hook_Inventory_equip;
        Hook_Inventory.swapWeapons += Hook_Inventory_swapWeapons;
        Hook_Inventory.replace += Hook_Inventory_replace;

        Hook_Hero.lockControlFromSkill += Hook_Hero_lockControlFromSkill;
        Hook_Hero.unlockControls += Hook_Hero_unlockControls;
        Hook_Hero.addKillCount += Hook_Hero_addKillCount;
        Hook_Hero.onMobDeath += Hook_Hero_onMobDeath;
        Hook_Hero.onOwnAttackDealt += Hook_Hero_onOwnAttackDealt;
        Hook_Hero.setAffectS += Hook_Hero_setAffectS;
        Hook_Viewport.bumpDir += Hook_Viewport_bumpDir;

        Hook_Entity.recoil += Hook_Entity_recoil;
        Hook_Entity.bump += Hook_Entity_bump;
        Hook_Entity.bumpAwayFrom += Hook_Entity_bumpAwayFrom;
        Hook_Entity.cancelVelocities += Hook_Entity_cancelVelocities;
        Hook_Entity.onDamage += Hook_Entity_onDamage;
        Hook_Entity.addTimeToAffect += Hook_Entity_addTimeToAffect;
        Hook_Entity.removeAffects += Hook_Entity_removeAffects;
        Hook_Entity.removeAllAffects += Hook_Entity_removeAllAffects;
        Hook_Entity.resetAllAffectToTime += Hook_Entity_resetAllAffectToTime;
        Hook_Entity.multiplyAffect += Hook_Entity_multiplyAffect;
        Hook_Entity.minTimeAffect += Hook_Entity_minTimeAffect;
        Hook_Entity.addAllAffixesFrom += Hook_Entity_addAllAffixesFrom;
        Hook_Entity.addReceivedAffix += Hook_Entity_addReceivedAffix;
        Hook_Entity.removeAllReceivedAffix += Hook_Entity_removeAllReceivedAffix;

        Hook__AttackUtils.createFromHero += Hook__AttackUtils_createFromHero;
        Hook__AttackUtils.createFromHeroAndHit += Hook__AttackUtils_createFromHeroAndHit;

        Hook_Weapon.prepare += Hook_Weapon_prepare;
        Hook_Weapon.get_shootX += Hook_Weapon_get_shootX;
        Hook_Weapon.get_shootY += Hook_Weapon_get_shootY;
        Hook_Weapon.interrupt += Hook_Weapon_interrupt;
        Hook_Weapon.dynOnInterrupt += Hook_Weapon_dynOnInterrupt;
        Hook_Weapon.fixedUpdate += Hook_Weapon_fixedUpdate;
        Hook_Weapon.postUpdate += Hook_Weapon_postUpdate;
        Hook_Weapon.dynOnAttackAnim += Hook_Weapon_dynOnAttackAnim;
        Hook_Weapon.dynOnFxFrame += Hook_Weapon_dynOnFxFrame;
        Hook_Weapon.updateAmmoHud += Hook_Weapon_updateAmmoHud;

        Hook_BaseBow.dynOnAttackAnim += Hook_BaseBow_dynOnAttackAnim;
        Hook_BaseBow.fixedUpdate += Hook_BaseBow_fixedUpdate;
        Hook_BaseBow.get_shootY += Hook_BaseBow_get_shootY;
        Hook_BaseBow.playShootAnim += Hook_BaseBow_playShootAnim;
        Hook_BaseBow.shoot += Hook_BaseBow_shoot;
        Hook_BaseBow.dynamicChargeExecute += Hook_BaseBow_dynamicChargeExecute;
        Hook_BaseBow.onBowChargeStart += Hook_BaseBow_onBowChargeStart;
        Hook_BaseBow.onBowCharging += Hook_BaseBow_onBowCharging;
        Hook_BaseBow.onExecute += Hook_BaseBow_onExecute;
        Hook_BaseBow.interrupt += Hook_BaseBow_interrupt;

        Hook_BaseShield.tryToCancel += Hook_BaseShield_tryToCancel;
        Hook_BaseShield.onShieldChargeStart += Hook_BaseShield_onShieldChargeStart;
        Hook_BaseShield.onShieldReleased += Hook_BaseShield_onShieldReleased;
        Hook_BaseShield.startParry += Hook_BaseShield_startParry;
        Hook_BaseShield.onShieldStartParry += Hook_BaseShield_onShieldStartParry;
        Hook_BaseShield.onShieldEndParry += Hook_BaseShield_onShieldEndParry;
        Hook_BaseShield.onShieldHolding += Hook_BaseShield_onShieldHolding;
        Hook_BaseShield.onShieldBlock += Hook_BaseShield_onShieldBlock;
        Hook_BaseShield.onShieldCounterSuccessful += Hook_BaseShield_onShieldCounterSuccessful;
        Hook_BaseShield.counterGrenade += Hook_BaseShield_counterGrenade;
        Hook_BaseShield.counterBullet += Hook_BaseShield_counterBullet;

        Hook_Ammo.startMagnet += Hook_Ammo_startMagnet;
        Hook_Ammo.postUpdate += Hook_Ammo_postUpdate;
        Hook_Ammo.retrieve += Hook_Ammo_retrieve;
        Hook_Ammo.pickUp += Hook_Ammo_pickUp;
    }

    private static InventItem Hook_Inventory_add(Hook_Inventory.orig_add orig, Inventory self, InventItem i)
    {
        var instance = ModEntry.Instance;
        if(instance != null)
            return instance.NotifyInventoryAddFromKingWeaponHooks(orig, self, i);
        return orig(self, i);
    }

    private static bool Hook_Inventory_equip(Hook_Inventory.orig_equip orig, Inventory self, InventItem i)
    {
        var instance = ModEntry.Instance;
        if(instance != null)
            return instance.NotifyInventoryEquipFromKingWeaponHooks(orig, self, i);
        return orig(self, i);
    }

    private static void Hook_Inventory_swapWeapons(Hook_Inventory.orig_swapWeapons orig, Inventory self)
    {
        var instance = ModEntry.Instance;
        if(instance != null)
        {
            instance.NotifyInventorySwapWeaponsFromKingWeaponHooks(orig, self);
            return;
        }
        orig(self);
    }

    private static void Hook_Inventory_replace(Hook_Inventory.orig_replace orig, Inventory self, InventItem by, InventItem oldPos)
    {
        var instance = ModEntry.Instance;
        if(instance != null)
        {
            instance.NotifyInventoryReplaceFromKingWeaponHooks(orig, self, by, oldPos);
            return;
        }
        orig(self, by, oldPos);
    }

    private static void Hook_Hero_lockControlFromSkill(Hook_Hero.orig_lockControlFromSkill orig, Hero self, double sec)
    {
        if(KingWeaponSupport.IsInKingContext &&
           sec > 0 &&
           sec <= SuppressLocalHeroSkillLockMaxSeconds &&
           ModEntry.me != null &&
           ReferenceEquals(self, ModEntry.me) &&
           !ShouldBypassKingContextControlSuppression(self))
            return;
        orig(self, sec);
    }

    private static void Hook_Hero_unlockControls(Hook_Hero.orig_unlockControls orig, Hero self)
    {
        orig(self);
    }

    private static bool ShouldBypassKingContextControlSuppression(Hero? hero)
    {
        if(hero == null)
            return false;

        dc.pr.Game game;
        try
        {
            game = hero._level?.game ?? dc.pr.Game.Class.ME;
        }
        catch
        {
            game = null!;
        }

        if(game == null)
            return false;

        try
        {
            if(game._pauseAfterFrames > 0)
                return true;
        }
        catch
        {
        }

        try
        {
            var cine = game.curCine;
            if(cine != null && !cine.destroyed)
                return true;
        }
        catch
        {
        }

        return false;
    }

    private static void Hook_Hero_addKillCount(Hook_Hero.orig_addKillCount orig, Hero self, Mob mob)
    {
        if(ShouldSuppressLocalHeroKillProgress(self))
            return;
        orig(self, mob);
    }

    private static void Hook_Hero_onMobDeath(Hook_Hero.orig_onMobDeath orig, Hero self, Mob old)
    {
        if(ShouldSuppressLocalHeroKillProgress(self))
            return;
        orig(self, old);
    }

    private static void Hook_Hero_onOwnAttackDealt(Hook_Hero.orig_onOwnAttackDealt orig, Hero self, AttackData atk, Entity target)
    {
        var isKingWeaponAttack = IsKingWeaponAttack(atk);

        var localHero = ModEntry.me;
        if(isKingWeaponAttack && localHero != null && IsSameEntity(self, localHero))
            return;

        if(ShouldSuppressLocalHeroKillProgress(self))
            return;

        orig(self, atk, target);
    }

    private static void Hook_Viewport_bumpDir(Hook_Viewport.orig_bumpDir orig, Viewport self, int dir, double? pow)
    {
        if(KingWeaponSupport.IsInKingContext)
            return;
        orig(self, dir, pow);
    }

    private static void Hook_Entity_recoil(Hook_Entity.orig_recoil orig, Entity self, double dx)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, dx);
    }

    private static void Hook_Entity_bump(Hook_Entity.orig_bump orig, Entity self, double dy, double ignoreResist, bool? dx)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, dy, ignoreResist, dx);
    }

    private static void Hook_Entity_bumpAwayFrom(Hook_Entity.orig_bumpAwayFrom orig, Entity self, Entity e, double? pow, bool? ignoreResist)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self, e, pow, ignoreResist);
    }

    private static void Hook_Entity_cancelVelocities(Hook_Entity.orig_cancelVelocities orig, Entity self)
    {
        if(KingWeaponSupport.IsInKingContext && ModEntry.me != null && ReferenceEquals(self, ModEntry.me))
            return;
        orig(self);
    }

    private static void Hook_Entity_onDamage(Hook_Entity.orig_onDamage orig, Entity self, AttackData a)
    {
        var suppress = ShouldSuppressDamageFromKingWeapon(self, a);
        if(suppress)
            return;

        orig(self, a);
    }

    private static void Hook_Hero_setAffectS(
        Hook_Hero.orig_setAffectS orig,
        Hero self,
        int id,
        double sec,
        Ref<double> ignoreResist,
        bool? allowResist)
    {
        if(ShouldSuppressLocalHeroAffectMutation(self))
            return;
        orig(self, id, sec, ignoreResist, allowResist);
    }

    private static void Hook_Entity_addTimeToAffect(Hook_Entity.orig_addTimeToAffect orig, Entity self, virtual_a_t_uniqId_val_ affect, double frames)
    {
        if(ShouldSuppressLocalHeroAffectMutation(self))
            return;
        orig(self, affect, frames);
    }

    private static void Hook_Entity_removeAffects(Hook_Entity.orig_removeAffects orig, Entity self, virtual_a_t_uniqId_val_ list)
    {
        if(ShouldSuppressLocalHeroAffectMutation(self))
            return;
        orig(self, list);
    }

    private static void Hook_Entity_removeAllAffects(Hook_Entity.orig_removeAllAffects orig, Entity self, int list)
    {
        if(ShouldSuppressLocalHeroAffectMutation(self))
            return;
        orig(self, list);
    }

    private static void Hook_Entity_resetAllAffectToTime(Hook_Entity.orig_resetAllAffectToTime orig, Entity self, int id, double t)
    {
        if(ShouldSuppressLocalHeroAffectMutation(self))
            return;
        orig(self, id, t);
    }

    private static void Hook_Entity_multiplyAffect(Hook_Entity.orig_multiplyAffect orig, Entity self, int id, double v)
    {
        if(ShouldSuppressLocalHeroAffectMutation(self))
            return;
        orig(self, id, v);
    }

    private static void Hook_Entity_minTimeAffect(Hook_Entity.orig_minTimeAffect orig, Entity self, int id, double v)
    {
        if(ShouldSuppressLocalHeroAffectMutation(self))
            return;
        orig(self, id, v);
    }

    private static void Hook_Entity_addAllAffixesFrom(
        Hook_Entity.orig_addAllAffixesFrom orig,
        Entity self,
        InventItem sourceItem,
        Ref<double> durationS)
    {
        if(ShouldSuppressPlayerAffixesInKingContext(self))
            return;
        orig(self, sourceItem, durationS);
    }

    private static void Hook_Entity_addReceivedAffix(
        Hook_Entity.orig_addReceivedAffix orig,
        Entity self,
        dc.String affixId,
        Ref<double> durationS)
    {
        if(ShouldSuppressPlayerAffixesInKingContext(self))
            return;
        orig(self, affixId, durationS);
    }

    private static void Hook_Entity_removeAllReceivedAffix(
        Hook_Entity.orig_removeAllReceivedAffix orig,
        Entity self,
        dc.String affixId)
    {
        if(ShouldSuppressPlayerAffixesInKingContext(self))
            return;
        orig(self, affixId);
    }

    private static AttackData Hook__AttackUtils_createFromHero(
        Hook__AttackUtils.orig_createFromHero orig,
        Entity source,
        object baseDmg,
        int? tier)
    {
        if(ShouldRedirectHeroAttackSourceToKingSkin(source, out var redirect))
            source = redirect;
        return orig(source, baseDmg, tier);
    }

    private static AttackData Hook__AttackUtils_createFromHeroAndHit(
        Hook__AttackUtils.orig_createFromHeroAndHit orig,
        Entity source,
        object baseDmg,
        int? tier,
        Entity target)
    {
        if(ShouldRedirectHeroAttackSourceToKingSkin(source, out var redirect))
            source = redirect;
        return orig(source, baseDmg, tier, target);
    }

    private static bool ShouldRedirectHeroAttackSourceToKingSkin(Entity? source, out Entity redirectedSource)
    {
        redirectedSource = source!;
        if(source == null)
            return false;
        if(!KingWeaponSupport.IsInKingContext || !KingWeaponSupport.IsLocalHeroDamageAllowedInKingContext)
            return false;

        var localHero = ModEntry.me;
        if(localHero == null || !IsSameEntity(source, localHero))
            return false;

        if(!KingWeaponSupport.TryGetCurrentContextSource(out var kingSource) || kingSource.destroyed)
            return false;

        redirectedSource = (Entity)kingSource;
        return true;
    }

    private static void Hook_Weapon_prepare(Hook_Weapon.orig_prepare orig, Weapon self, double attackSpeed)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () => orig(self, attackSpeed));
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.SyncSource(self);
            return;
        }

        ModEntry.Instance?.NotifyLocalWeaponPrepareFromKingWeaponHooks(self);
        orig(self, attackSpeed);
    }

    private static double Hook_Weapon_get_shootX(Hook_Weapon.orig_get_shootX orig, Weapon self)
    {
        if(KingWeaponSupport.TryGetSource(self, out var source) && source != null)
            return source.get_shootX();
        if(KingWeaponSupport.IsRetiredRemoteWeapon(self))
            return 0.0;
        return orig(self);
    }

    private static double Hook_Weapon_get_shootY(Hook_Weapon.orig_get_shootY orig, Weapon self)
    {
        if(KingWeaponSupport.TryGetSource(self, out var source) && source != null)
            return source.get_shootY();
        if(KingWeaponSupport.IsRetiredRemoteWeapon(self))
            return 0.0;
        return orig(self);
    }

    private static void Hook_Weapon_interrupt(Hook_Weapon.orig_interrupt orig, Weapon self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            KingWeaponSupport.SyncSource(self);
            return;
        }

        var wasCharging = false;
        try { wasCharging = self != null && self.isCharging(); } catch { }
        orig(self);
        if(wasCharging && self != null)
            ModEntry.Instance?.NotifyLocalWeaponInterruptFromKingWeaponHooks(self);
    }

    private static void Hook_Weapon_dynOnInterrupt(Hook_Weapon.orig_dynOnInterrupt orig, Weapon self, WeaponSkill s, double r)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, s, r); } catch { }
            });
            return;
        }

        orig(self, s, r);
    }

    private static void Hook_Weapon_fixedUpdate(Hook_Weapon.orig_fixedUpdate orig, Weapon self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () => orig(self));
            KingWeaponSupport.SyncSource(self);
            return;
        }

        orig(self);
    }

    private static void Hook_Weapon_postUpdate(Hook_Weapon.orig_postUpdate orig, Weapon self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () => orig(self));
            KingWeaponSupport.SyncSource(self);
            return;
        }

        orig(self);
    }

    private static void Hook_Weapon_dynOnAttackAnim(
        Hook_Weapon.orig_dynOnAttackAnim orig,
        Weapon self,
        WeaponSkill s,
        virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ a)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, s, a); } catch { }
            });
            return;
        }

        orig(self, s, a);
    }

    private static void Hook_Weapon_dynOnFxFrame(
        Hook_Weapon.orig_dynOnFxFrame orig,
        Weapon self,
        WeaponSkill s,
        virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ cinf)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, s, cinf); } catch { }
            });
            return;
        }

        orig(self, s, cinf);
    }

    private static void Hook_Weapon_updateAmmoHud(Hook_Weapon.orig_updateAmmoHud orig, Weapon self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
            return;
        orig(self);
    }

    private static void Hook_BaseBow_dynOnAttackAnim(
        Hook_BaseBow.orig_dynOnAttackAnim orig,
        BaseBow self,
        WeaponSkill s,
        virtual_animId_animSpd_area_breachBonus_canCrit_charge_coolDown_critMul_dynamicCharge_earlyCombo_fxId_fxProps_glowColor_hitFrame_lockCtrlAfter_onionSkinFrame_onionSkinOffX_power_props_sfxCharge_sfxHit_sfxProps_sfxRelease_ cinf)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, s, cinf); } catch { }
            });
            return;
        }

        orig(self, s, cinf);
    }

    private static void Hook_BaseBow_fixedUpdate(Hook_BaseBow.orig_fixedUpdate orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.PatchCurrentSkill(self);
            KingWeaponSupport.WithKingContext(self, () => orig(self));
            KingWeaponSupport.SyncSource(self);
            return;
        }
        orig(self);
    }

    private static double Hook_BaseBow_get_shootY(Hook_BaseBow.orig_get_shootY orig, BaseBow self)
    {
        if(KingWeaponSupport.TryGetSource(self, out var source) && source != null)
            return source.get_shootY();
        if(KingWeaponSupport.IsRetiredRemoteWeapon(self))
            return 0.0;
        return orig(self);
    }

    private static void Hook_BaseBow_playShootAnim(Hook_BaseBow.orig_playShootAnim orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseBow_shoot(Hook_BaseBow.orig_shoot orig, BaseBow self, ArrayObj entity)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, entity); } catch { }
            });
            return;
        }
        orig(self, entity);
        ModEntry.Instance?.NotifyLocalBowShotFromKingWeaponHooks(self);
    }

    private static void Hook_BaseBow_dynamicChargeExecute(Hook_BaseBow.orig_dynamicChargeExecute orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseBow_onBowChargeStart(Hook_BaseBow.orig_onBowChargeStart orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static void Hook_BaseBow_onBowCharging(Hook_BaseBow.orig_onBowCharging orig, BaseBow self, double r)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self, r); } catch { }
            });
            return;
        }
        orig(self, r);
    }

    private static bool Hook_BaseBow_onExecute(Hook_BaseBow.orig_onExecute orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
            return KingWeaponSupport.WithKingContext(self, () =>
            {
                try { return orig(self); } catch { return false; }
            });
        return orig(self);
    }

    private static void Hook_BaseBow_interrupt(Hook_BaseBow.orig_interrupt orig, BaseBow self)
    {
        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try { orig(self); } catch { }
            });
            return;
        }
        orig(self);
    }

    private static bool Hook_BaseShield_tryToCancel(Hook_BaseShield.orig_tryToCancel orig, BaseShield self, bool byWeapon)
    {
        return RunBaseShieldHookBool(self, () => orig(self, byWeapon));
    }

    private static void Hook_BaseShield_onShieldChargeStart(Hook_BaseShield.orig_onShieldChargeStart orig, BaseShield self)
    {
        RunBaseShieldHookVoid(self, () => orig(self));
    }

    private static void Hook_BaseShield_onShieldReleased(Hook_BaseShield.orig_onShieldReleased orig, BaseShield self)
    {
        RunBaseShieldHookVoid(self, () => orig(self));
    }

    private static void Hook_BaseShield_startParry(Hook_BaseShield.orig_startParry orig, BaseShield self)
    {
        RunBaseShieldHookVoid(self, () => orig(self));
    }

    private static void Hook_BaseShield_onShieldStartParry(Hook_BaseShield.orig_onShieldStartParry orig, BaseShield self)
    {
        RunBaseShieldHookVoid(self, () => orig(self));
    }

    private static void Hook_BaseShield_onShieldEndParry(Hook_BaseShield.orig_onShieldEndParry orig, BaseShield self)
    {
        RunBaseShieldHookVoid(self, () => orig(self));
    }

    private static void Hook_BaseShield_onShieldHolding(Hook_BaseShield.orig_onShieldHolding orig, BaseShield self, double ratio)
    {
        if(ShouldSuppressLocalHeroShieldInKingContext(self))
            return;

        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try
                {
                    orig(self, ratio);
                }
                catch(Exception ex)
                {
                    LogKingWeaponHookEx(ex, "BaseShield.onShieldHolding");
                }
            });
            return;
        }

        ModEntry.Instance?.NotifyLocalShieldHoldingPulseFromKingWeaponHooks(self, ratio);
        orig(self, ratio);
    }

    private static void Hook_BaseShield_onShieldBlock(Hook_BaseShield.orig_onShieldBlock orig, BaseShield self, AttackData sourceAtk, bool fullParry)
    {
        RunBaseShieldHookVoid(self, () => orig(self, sourceAtk, fullParry));
    }

    private static void Hook_BaseShield_onShieldCounterSuccessful(Hook_BaseShield.orig_onShieldCounterSuccessful orig, BaseShield self, AttackData sourceAtk, bool fullParry)
    {
        RunBaseShieldHookVoid(self, () => orig(self, sourceAtk, fullParry));
    }

    private static void Hook_BaseShield_counterGrenade(Hook_BaseShield.orig_counterGrenade orig, BaseShield self, Grenade repelled)
    {
        RunBaseShieldHookVoid(self, () => orig(self, repelled));
    }

    private static Bullet Hook_BaseShield_counterBullet(Hook_BaseShield.orig_counterBullet orig, BaseShield self, AttackData sourceAtk, Bullet cBullet, bool fullParry)
    {
        return RunBaseShieldHookBullet(self, cBullet, () => orig(self, sourceAtk, cBullet, fullParry));
    }

    private static void RunBaseShieldHookVoid(BaseShield self, Action origCall)
    {
        if(ShouldSuppressLocalHeroShieldInKingContext(self))
            return;

        if(KingWeaponSupport.IsKingWeapon(self))
        {
            KingWeaponSupport.WithKingContext(self, () =>
            {
                try
                {
                    origCall();
                }
                catch(Exception ex)
                {
                    LogKingWeaponHookEx(ex, "BaseShield");
                }
            });
            return;
        }

        origCall();
    }

    private static bool RunBaseShieldHookBool(BaseShield self, Func<bool> origCall)
    {
        if(ShouldSuppressLocalHeroShieldInKingContext(self))
            return false;

        if(KingWeaponSupport.IsKingWeapon(self))
            return KingWeaponSupport.WithKingContext(self, () =>
            {
                try
                {
                    return origCall();
                }
                catch(Exception ex)
                {
                    LogKingWeaponHookEx(ex, "BaseShield");
                    return false;
                }
            });
        return origCall();
    }

    private static Bullet RunBaseShieldHookBullet(BaseShield self, Bullet fallback, Func<Bullet> origCall)
    {
        if(ShouldSuppressLocalHeroShieldInKingContext(self))
            return fallback;

        if(KingWeaponSupport.IsKingWeapon(self))
            return KingWeaponSupport.WithKingContext(self, () =>
            {
                try
                {
                    return origCall();
                }
                catch(Exception ex)
                {
                    LogKingWeaponHookEx(ex, "BaseShield.counterBullet");
                    return fallback;
                }
            });
        return origCall();
    }

    private static void Hook_Ammo_startMagnet(Hook_Ammo.orig_startMagnet orig, Ammo self, Entity e)
    {
        if(TryGetAmmoSource(self, out var source) && source != null && ModEntry.me != null && ReferenceEquals(e, ModEntry.me))
        {
            orig(self, source);
            return;
        }

        orig(self, e);
    }

    private static void Hook_Ammo_postUpdate(Hook_Ammo.orig_postUpdate orig, Ammo self)
    {
        orig(self);
        TryCleanupReturnedAmmo(self);
    }

    private static void Hook_Ammo_retrieve(Hook_Ammo.orig_retrieve orig, Ammo self, Hero h)
    {
        orig(self, h);
        if(ModEntry.me != null && ReferenceEquals(h, ModEntry.me))
            ModEntry.Instance?.NotifyLocalAmmoChangedFromKingWeaponHooks(self?.item);
    }

    private static void Hook_Ammo_pickUp(Hook_Ammo.orig_pickUp orig, Ammo self, Hero h)
    {
        orig(self, h);
        if(ModEntry.me != null && ReferenceEquals(h, ModEntry.me))
            ModEntry.Instance?.NotifyLocalAmmoChangedFromKingWeaponHooks(self?.item);
    }

    private static bool TryGetAmmoSource(Ammo? ammo, out KingSkin source)
    {
        source = null!;
        if(ammo == null)
            return false;

        InventItem item;
        try
        {
            item = ammo.item;
        }
        catch
        {
            return false;
        }

        if(item == null)
            return false;

        if(!KingWeaponSupport.TryGetSourceByItem(item, out source) || source == null)
            return false;

        if(source.destroyed || source.life <= 0)
            return false;

        return true;
    }

    private static bool ShouldSuppressLocalHeroShieldInKingContext(BaseShield? self)
    {
        if(self == null)
            return false;
        if(!KingWeaponSupport.IsInKingContext)
            return false;
        if(KingWeaponSupport.IsKingWeapon(self))
            return false;

        var localHero = ModEntry.me;
        if(localHero == null)
            return false;

        try
        {
            return IsSameEntity(self.owner, localHero);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldSuppressLocalHeroKillProgress(Hero? self)
    {
        if(self == null)
            return false;

        var localHero = ModEntry.me;
        if(localHero == null || !IsSameEntity(self, localHero))
            return false;

        if(KingWeaponSupport.IsInKingContext)
            return true;

        if(ModEntry.IsLocalPlayerDowned())
            return true;

        return false;
    }

    private static bool ShouldSuppressPlayerAffixesInKingContext(Entity? self)
    {
        if(self == null)
            return false;
        if(!KingWeaponSupport.IsInKingContext)
            return false;
        if(self is Hero)
            return true;
        if(self is KingSkin)
            return true;
        return false;
    }

    private static bool ShouldSuppressLocalHeroAffectMutation(Entity? self)
    {
        if(self == null)
            return false;
        if(self is not Hero)
            return false;

        var localHero = ModEntry.me;
        if(localHero == null || !IsSameEntity(self, localHero))
            return false;

        if(KingWeaponSupport.IsInKingContext)
            return true;

        if(ModEntry.IsLocalPlayerDowned())
            return true;

        return false;
    }

    private static bool ShouldSuppressDamageFromKingWeapon(Entity? target, AttackData? attack)
    {
        if(attack == null)
            return false;

        // Remote KingWeapon objects are presentation-only. Both players already report their
        // real local hits through MobSync and the host sends authoritative HP/death back. Letting
        // the detached ghost weapon call Entity.onDamage duplicates hit side effects, can damage
        // a teammate/world object or a different local mob, and is the main path complex weapons
        // use to reach unsafe engine state. Suppress all gameplay damage even while downed.
        if(KingWeaponSupport.IsInKingContext)
            return true;

        Weapon sourceWeapon;
        try
        {
            sourceWeapon = attack.sourceWeapon;
        }
        catch
        {
            sourceWeapon = null!;
        }

        Entity sourceEntity;
        try
        {
            sourceEntity = attack.source;
        }
        catch
        {
            sourceEntity = null!;
        }

        if(sourceWeapon != null && KingWeaponSupport.IsKingWeapon(sourceWeapon))
            return true;

        if(sourceWeapon is KingWeapon)
            return true;

        if(sourceEntity is KingSkin)
            return true;

        // sourceItem can remain bound to a stale King source after inventory churn.
        // Never block trusted local hero hits based on item binding alone.
        if(!KingWeaponSupport.IsInKingContext && IsAttackFromLocalHero(attack, sourceWeapon, sourceEntity))
            return false;

        InventItem sourceItem;
        try
        {
            sourceItem = attack.sourceItem;
        }
        catch
        {
            sourceItem = null!;
        }

        if(sourceItem != null &&
           (KingWeaponSupport.IsRetiredRemoteItem(sourceItem) ||
            (KingWeaponSupport.TryGetSourceByItem(sourceItem, out var source) && source != null)))
            return true;

        return false;
    }

    private static bool IsKingWeaponAttack(AttackData? attack)
    {
        if(attack == null)
            return false;

        if(KingWeaponSupport.IsInKingContext)
            return true;

        Weapon sourceWeapon;
        try
        {
            sourceWeapon = attack.sourceWeapon;
        }
        catch
        {
            sourceWeapon = null!;
        }

        if(sourceWeapon != null && KingWeaponSupport.IsKingWeapon(sourceWeapon))
            return true;

        if(sourceWeapon is KingWeapon)
            return true;

        Entity sourceEntity;
        try
        {
            sourceEntity = attack.source;
        }
        catch
        {
            sourceEntity = null!;
        }

        if(sourceEntity is KingSkin)
            return true;

        if(IsAttackFromLocalHero(attack, sourceWeapon, sourceEntity))
            return false;

        InventItem sourceItem;
        try
        {
            sourceItem = attack.sourceItem;
        }
        catch
        {
            sourceItem = null!;
        }

        if(sourceItem != null &&
           (KingWeaponSupport.IsRetiredRemoteItem(sourceItem) ||
            (KingWeaponSupport.TryGetSourceByItem(sourceItem, out var sourceByItem) && sourceByItem != null)))
            return true;

        return false;
    }

    private static bool IsAttackFromLocalHero(AttackData? attack, Weapon? sourceWeapon = null, Entity? sourceEntity = null)
    {
        if(attack == null)
            return false;

        var localHero = ModEntry.me;
        if(localHero == null)
            return false;

        if(sourceEntity != null && IsSameEntity(sourceEntity, localHero))
            return true;

        Entity sourceCarrier;
        try
        {
            sourceCarrier = attack.carrier;
        }
        catch
        {
            sourceCarrier = null!;
        }

        if(sourceCarrier != null && IsSameEntity(sourceCarrier, localHero))
            return true;

        if(sourceWeapon == null)
        {
            try
            {
                sourceWeapon = attack.sourceWeapon;
            }
            catch
            {
                sourceWeapon = null;
            }
        }

        if(sourceWeapon != null && IsSameEntity(sourceWeapon.owner, localHero))
            return true;

        return false;
    }

    private static int GetEntityUid(Entity? entity)
    {
        if(entity == null)
            return -1;

        try
        {
            return entity.__uid;
        }
        catch
        {
            return -1;
        }
    }

    private static bool IsSameEntity(Entity? a, Entity? b)
    {
        if(a == null || b == null)
            return false;

        if(ReferenceEquals(a, b))
            return true;

        var uidA = GetEntityUid(a);
        var uidB = GetEntityUid(b);
        if(uidA > 0 && uidB > 0)
            return uidA == uidB;

        return false;
    }

    private static void TryCleanupReturnedAmmo(Ammo? ammo)
    {
        if(ammo == null)
            return;
        if(!TryGetAmmoSource(ammo, out var source) || source == null)
            return;

        try
        {
            if(ammo.destroyed || ammo.life <= 0)
                return;
        }
        catch
        {
            return;
        }

        bool magneting;
        Entity magnetTarget;
        try
        {
            magneting = ammo.magneting;
            magnetTarget = ammo.magnetedTo;
        }
        catch
        {
            return;
        }

        if(!magneting || magnetTarget == null || !ReferenceEquals(magnetTarget, source))
            return;
        if(source.destroyed || source.life <= 0)
            return;

        double ammoX;
        double ammoY;
        double srcX;
        double srcY;
        try
        {
            ammoX = ((double)ammo.cx + ammo.xr) * 24.0;
            ammoY = ((double)ammo.cy + ammo.yr) * 24.0 - ammo.hei * 0.5;
            srcX = ((double)source.cx + source.xr) * 24.0;
            srcY = ((double)source.cy + source.yr) * 24.0 - source.hei * 0.5;
        }
        catch
        {
            return;
        }

        var dx = srcX - ammoX;
        var dy = srcY - ammoY;

        double pickDist;
        try
        {
            pickDist = ammo.pickDist;
        }
        catch
        {
            pickDist = 12.0;
        }

        if(pickDist < 8.0)
            pickDist = 8.0;

        var reach = pickDist + 8.0;
        if(dx * dx + dy * dy > reach * reach)
            return;

        try { ammo.pickUpByEntity(source); } catch { }

        bool stillAlive;
        try
        {
            stillAlive = !ammo.destroyed && ammo.life > 0;
        }
        catch
        {
            stillAlive = false;
        }

        if(stillAlive)
        {
            try { ammo.vanish(); } catch { }
            try { ammo.destroy(); } catch { }
        }
    }
}

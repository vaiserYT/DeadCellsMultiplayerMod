using dc.en;
using dc.tool;
using dc.tool.hero;
using dc.tool.weap;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using DeadCellsMultiplayerMod.Tools;
using ModCore.Utilities;
using System.Collections.Generic;
using System.Diagnostics;

namespace DeadCellsMultiplayerMod.Ghost
{
    public class KingWeaponsManager : HeroWeaponsManager
    {
        // Vanilla affect ids used by shield block/hold/parry; remote clear must stay aligned with game data.
        private const int ShieldAffectClearBlockOrHold0 = 96;
        private const int ShieldAffectClearBlockOrHold1 = 98;
        private const int ShieldAffectClearBlockOrHold2 = 99;

        private const double ShieldReleaseAfterLastPulseSeconds = 0.22;
        private const double ShieldPulseIgnoreAfterReleaseSeconds = 0.25;

        private readonly GhostKing king;
        private Inventory inventory = null!;
        private Weapon weapon = null!;
        private InventItem weaponItem = null!;
        private int pendingAttacks;
        private int pendingInterrupts;
        private int pendingSlot = -1;
        private long _shieldLastPulseTicks;
        private bool _shieldActive;
        private long _lastShieldReleaseTimestamp;
        private string _activeKindId = string.Empty;
        private bool _meleeSwingActive;
        private readonly HashSet<string> _quarantinedKinds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _visualOnlyNoticeKinds = new(StringComparer.OrdinalIgnoreCase);

        public bool IsShieldActive => _shieldActive;

        public KingWeaponsManager(Hero hero, GhostKing king) : base(hero)
        {
            this.king = king;
        }

        public override void init()
        {
            var inv = king.inventory;
            if(inv != null)
                inventory = inv;
        }

        /// <summary>
        /// Runs remote weapon visuals defensively. A remote weapon is never important enough to let a
        /// Hashlink/weapon-specific failure terminate the co-op run: known-dangerous kinds are visual-only,
        /// and any unexpected runtime failure quarantines that kind for this ghost until it is recreated.
        /// Real enemy HP/death remains authoritative through MobSync.
        /// </summary>
        public void update()
        {
            try
            {
                UpdateCore();
            }
            catch(Exception ex)
            {
                QuarantineCurrentWeapon("update", ex);
            }
        }

        private void UpdateCore()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            if(hero == null) return;
            var inv = king.inventory;
            if(inventory == null && inv != null)
                inventory = inv;

            var item = GetWeaponItem(pendingSlot);
            if(item == null || item.kind?.Index == InventItemKind.Indexes.Meta) return;

            var kindId = GetWeaponKindId(item) ?? string.Empty;
            if(KingWeaponSupport.RequiresVisualOnlyRemoteReplay(kindId))
            {
                EnterVisualOnlyMode(item, kindId, "known-unsafe");
                return;
            }

            if(kindId.Length > 0 && _quarantinedKinds.Contains(kindId))
            {
                EnterVisualOnlyMode(item, kindId, "runtime-quarantine");
                return;
            }

            if(NeedsWeaponRebuild(item))
            {
                var rebuildStart = RuntimeHitchWatch.Start();
                DisposeCurrentWeaponSafely();

                weaponItem = item;
                _activeKindId = kindId;
                _shieldActive = false;
                _shieldLastPulseTicks = 0;
                _lastShieldReleaseTimestamp = 0;
                _meleeSwingActive = false;
                ClearShieldAffects();

                // Do not construct an idle detached weapon merely because the remote player equipped it.
                // A runtime is needed only when an actual remote attack arrives. Flint's sender never
                // requests that duplicated execution, so its powered-feedback runtime is never created.
                if(pendingAttacks <= 0)
                {
                    pendingInterrupts = 0;
                    return;
                }

                // Create only the raw vanilla runtime first. Flint/powered-feedback weapons must be
                // recognized before binding the detached ghost or patching/advancing any skill.
                var candidate = KingWeaponSupport.CreateWeaponCandidate(hero, item);
                weapon = candidate;
                if(KingWeaponSupport.RequiresVisualOnlyRemoteReplay(kindId, candidate, out var unsafeReason))
                {
                    if(kindId.Length > 0)
                        _quarantinedKinds.Add(kindId);

                    // Do not call dispose on this raw unsafe candidate: a Flint dispose/interrupt path
                    // can itself enter stopPoweredFeedback. It was never bound or advanced and is left
                    // for the Hashlink runtime to reclaim.
                    EnterVisualOnlyMode(item, kindId, unsafeReason, disposeCurrent: false);
                    return;
                }

                KingWeaponSupport.ActivateRemoteWeapon(candidate, king);
                pendingInterrupts = 0;
                LogKingWeaponsStepIfSlow(
                    "KingWeaponsManager.Rebuild",
                    rebuildStart,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"pendingSlot={pendingSlot} permanentId={item.permanentId} kind={kindId} weapon={weapon?.GetType().Name ?? "null"}"));
            }

            var activeWeapon = weapon;
            if(activeWeapon == null)
                return;

            var game = dc.pr.Game.Class.ME;
            if(game != null) activeWeapon.cd.update(game.tmod);
            var now = Stopwatch.GetTimestamp();

            if(activeWeapon is BaseShield)
            {
                var shieldStart = RuntimeHitchWatch.Start();
                if(pendingInterrupts > 0)
                {
                    pendingInterrupts = 0;
                    pendingAttacks = 0;
                    if(_shieldActive || activeWeapon.isCharging())
                        ReleaseShield(now);
                }

                if(pendingAttacks > 0)
                {
                    // Treat incoming ATK as "button still held" pulses. Don't stack them.
                    pendingAttacks = 0;

                    // When the remote releases the shield, a few late ATK packets can arrive and would re-trigger hold,
                    // causing the animation/state to flicker (release -> hold -> release ...). Ignore pulses briefly after release.
                    var ignorePulses = _lastShieldReleaseTimestamp != 0 &&
                        Stopwatch.GetElapsedTime(_lastShieldReleaseTimestamp, now).TotalSeconds < ShieldPulseIgnoreAfterReleaseSeconds;
                    if(!ignorePulses)
                    {
                        _shieldLastPulseTicks = now;

                        if(!_shieldActive && activeWeapon.isReady())
                        {
                            ClearShieldAffects();
                            KingWeaponSupport.SyncSource(activeWeapon);
                            activeWeapon.prepare(getWeaponAttackSpeed(activeWeapon));
                            _shieldActive = true;
                        }
                    }
                }

                if(_shieldActive && !activeWeapon.destroyed)
                {
                    if(activeWeapon is BaseShield shield)
                    {
                        try { shield.onShieldHolding(1.0); } catch { }
                    }

                    activeWeapon.fixedUpdate();
                    activeWeapon.postUpdate();

                    var sincePulseS = _shieldLastPulseTicks != 0
                        ? Stopwatch.GetElapsedTime(_shieldLastPulseTicks, now).TotalSeconds
                        : 0.0;
                    if(_shieldLastPulseTicks != 0 && sincePulseS > ShieldReleaseAfterLastPulseSeconds)
                        ReleaseShield(now);
                }

                LogKingWeaponsStepIfSlow(
                    "KingWeaponsManager.ShieldUpdate",
                    shieldStart,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"shieldActive={(_shieldActive ? 1 : 0)} pendingAttacks={pendingAttacks} pendingInterrupts={pendingInterrupts} weapon={activeWeapon.GetType().Name}"));

                var shieldTotalMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
                if(shieldTotalMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
                {
                    RuntimeHitchWatch.LogSlow(
                        ModEntry.Instance?.Logger,
                        "KingWeaponsManager.Update",
                        shieldTotalMs,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"pendingAttacks={pendingAttacks} pendingInterrupts={pendingInterrupts} shieldActive={(_shieldActive ? 1 : 0)} weapon={activeWeapon.GetType().Name}"));
                }

                return;
            }

            if(pendingAttacks > 0 && activeWeapon.isReady())
            {
                KingWeaponSupport.SyncSource(activeWeapon);
                activeWeapon.prepare(getWeaponAttackSpeed(activeWeapon));
                pendingAttacks--;
                _meleeSwingActive = true;
            }

            if(pendingAttacks > 1)
                pendingAttacks = 1;

            if(!activeWeapon.destroyed)
            {
                if(activeWeapon is BaseBow)
                {
                    // Keep ranged recoveries (mini-arrows/boomerangs) bound to KingSkin context
                    // without re-triggering full bow fixed logic each tick.
                    activeWeapon.postUpdate();
                }
                else
                {
                    activeWeapon.fixedUpdate();
                    activeWeapon.postUpdate();
                }
            }

            if(pendingInterrupts > 0)
            {
                pendingInterrupts = 0;
                if(!activeWeapon.destroyed && activeWeapon.isCharging())
                {
                    try { activeWeapon.interrupt(); } catch { }
                    try { activeWeapon.fixedUpdate(); } catch { }
                    try { activeWeapon.postUpdate(); } catch { }
                }

                _meleeSwingActive = false;
                RestoreRemoteIdlePose();
            }
            else if(_meleeSwingActive &&
                     !activeWeapon.destroyed &&
                     !activeWeapon.isCharging() &&
                     activeWeapon.isReady())
            {
                // Melee/ranged ATK uses stopOnLastFrame; without a locomotion ANIM change the
                // ghost stays frozen on the last attack frame while the player stands still.
                _meleeSwingActive = false;
                RestoreRemoteIdlePose();
            }

            var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
            if(hitchMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    ModEntry.Instance?.Logger,
                    "KingWeaponsManager.Update",
                    hitchMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"pendingAttacks={pendingAttacks} pendingInterrupts={pendingInterrupts} shieldActive={(_shieldActive ? 1 : 0)} weapon={activeWeapon.GetType().Name}"));
            }
        }

        public void queueAttack(int slot = -1)
        {
            if(slot >= 0) pendingSlot = slot;
            if(pendingAttacks < 3)
                pendingAttacks++;
        }

        public void queueInterrupt(int slot = -1)
        {
            if(slot >= 0) pendingSlot = slot;
            if(pendingInterrupts < 3)
                pendingInterrupts++;
        }

        /// <summary>Disposes the managed weapon and clears shield state; call when GhostKing is torn down to avoid use-after-dispose.</summary>
        internal void DisposeManagedWeapon()
        {
            ClearShieldAffects();
            DisposeCurrentWeaponSafely();
            weaponItem = null!;
            _activeKindId = string.Empty;
            pendingAttacks = 0;
            pendingInterrupts = 0;
            pendingSlot = -1;
            _shieldActive = false;
            _shieldLastPulseTicks = 0;
            _lastShieldReleaseTimestamp = 0;
            _meleeSwingActive = false;
            _quarantinedKinds.Clear();
            _visualOnlyNoticeKinds.Clear();
        }

        private void EnterVisualOnlyMode(InventItem item, string kindId, string reason, bool disposeCurrent = true)
        {
            if(disposeCurrent)
            {
                DisposeCurrentWeaponSafely();
            }
            else
            {
                var unsafeCandidate = weapon;
                weapon = null!;
                try
                {
                    if(unsafeCandidate != null)
                        KingWeaponSupport.Unbind(unsafeCandidate);
                }
                catch
                {
                }
            }

            weaponItem = item;
            _activeKindId = kindId;
            pendingAttacks = 0;
            pendingInterrupts = 0;
            _shieldActive = false;
            _shieldLastPulseTicks = 0;
            _lastShieldReleaseTimestamp = 0;
            ClearShieldAffects();

            var logKey = string.IsNullOrWhiteSpace(kindId) ? "<unknown>" : kindId;
            if(_visualOnlyNoticeKinds.Add(logKey))
            {
                ModEntry.Instance?.Logger.Information(
                    "[NetMod][RemoteWeaponGuard] visual-only remote replay kind={Kind} reason={Reason}; MobSync still carries authoritative damage",
                    logKey,
                    reason);
            }
        }

        private void QuarantineCurrentWeapon(string step, Exception ex)
        {
            var kindId = !string.IsNullOrWhiteSpace(_activeKindId)
                ? _activeKindId
                : GetWeaponKindId(weaponItem) ?? "<unknown>";

            if(!string.Equals(kindId, "<unknown>", StringComparison.Ordinal))
                _quarantinedKinds.Add(kindId);

            DisposeCurrentWeaponSafely();
            pendingAttacks = 0;
            pendingInterrupts = 0;
            pendingSlot = -1;
            _shieldActive = false;
            _shieldLastPulseTicks = 0;
            _lastShieldReleaseTimestamp = 0;
            ClearShieldAffects();

            var logger = ModEntry.Instance?.Logger;
            if(logger != null)
            {
                logger.Warning(
                    ex,
                    "[NetMod][RemoteWeaponGuard] quarantined unsafe remote weapon kind={Kind} step={Step}; continuing with visual-only replay",
                    kindId,
                    step);
            }
        }

        private void DisposeCurrentWeaponSafely()
        {
            var current = weapon;
            weapon = null!;
            if(current == null)
                return;

            try { KingWeaponSupport.RetireRemoteWeapon(current); } catch { }
        }

        private bool NeedsWeaponRebuild(InventItem item)
        {
            if(item == null)
                return false;
            if(weapon == null || weapon.destroyed || weaponItem == null)
                return true;
            if(ReferenceEquals(weaponItem, item))
                return false;

            var oldPermanentId = weaponItem.permanentId;
            var newPermanentId = item.permanentId;
            if(oldPermanentId != 0 && newPermanentId != 0 && oldPermanentId != newPermanentId)
                return true;

            var oldKind = GetWeaponKindId(weaponItem);
            var newKind = GetWeaponKindId(item);
            if(!string.Equals(oldKind, newKind, StringComparison.Ordinal))
                return true;

            if(weaponItem.posID != item.posID)
                return true;

            return false;
        }

        private static string? GetWeaponKindId(InventItem? item)
        {
            if(item?.kind is InventItemKind.Weapon w)
                return w.Param0?.ToString();
            return null;
        }

        private void ClearShieldAffects()
        {
            try { king.removeAllAffects(ShieldAffectClearBlockOrHold0); } catch { }
            try { king.removeAllAffects(ShieldAffectClearBlockOrHold1); } catch { }
            try { king.removeAllAffects(ShieldAffectClearBlockOrHold2); } catch { }
        }

        private void ReleaseShield(long now)
        {
            var hitchStart = RuntimeHitchWatch.Start();
            if(weapon is BaseShield shieldToRelease)
            {
                try { shieldToRelease.tryToCancel(false); } catch { }
                try { shieldToRelease.onShieldReleased(); } catch { }
            }

            try { weapon.interrupt(); } catch { }
            try { weapon.fixedUpdate(); } catch { }
            try { weapon.postUpdate(); } catch { }
            _shieldActive = false;
            _shieldLastPulseTicks = 0;
            _lastShieldReleaseTimestamp = now;
            ClearShieldAffects();
            RestoreRemoteIdlePose();
            LogKingWeaponsStepIfSlow(
                "KingWeaponsManager.ReleaseShield",
                hitchStart,
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"weapon={weapon?.GetType().Name ?? "null"}"));
        }

        private void RestoreRemoteIdlePose()
        {
            try { king.spr?._animManager?.play("idle".AsHaxeString(), null, null)?.loop(null); } catch { }
        }

        private static void LogKingWeaponsStepIfSlow(string key, long stepStart, string? details)
        {
            var stepMs = RuntimeHitchWatch.GetElapsedMilliseconds(stepStart);
            if(stepMs < RuntimeHitchWatch.GhostRuntimeStepSlowThresholdMs)
                return;

            RuntimeHitchWatch.LogSlow(ModEntry.Instance?.Logger, key, stepMs, details);
        }

        private InventItem? GetWeaponItem(int slot)
        {
            var inv = inventory;
            if(inv != null)
            {
                if(slot >= 0)
                {
                    var prefer = inv.getEquippedWeaponOn(slot);
                    if(prefer != null) return prefer;
                }
                var w0 = inv.getEquippedWeaponOn(0);
                if(w0 != null) return w0;
                var w1 = inv.getEquippedWeaponOn(1);
                if(w1 != null) return w1;
            }

            if(ModEntry._net == null)
                return ModEntry.Instance?.inventItem;
            return null;
        }
    }
}

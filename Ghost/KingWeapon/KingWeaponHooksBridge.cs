using System.Diagnostics;
using dc.tool;
using dc.tool.weap;

namespace DeadCellsMultiplayerMod;

public partial class ModEntry
{
    private const double LocalAttackRepeatBlockSeconds = 0.06;
    private const double LocalInterruptRepeatBlockSeconds = 0.06;
    private const double LocalShieldPulseSeconds = 0.08;
    private long _lastLocalAttackSendTicks;
    private string _lastLocalAttackKey = string.Empty;
    private long _lastLocalInterruptSendTicks;
    private string _lastLocalInterruptKey = string.Empty;
    private long _lastLocalShieldPulseTicks;
    private readonly HashSet<string> _visualOnlyLocalWeaponNotices = new(StringComparer.OrdinalIgnoreCase);

    internal InventItem NotifyInventoryAddFromKingWeaponHooks(Hook_Inventory.orig_add orig, Inventory self, InventItem i)
    {
        if(_inventorySyncGuard)
            return orig(self, i);

        if(me != null && ReferenceEquals(self, me.inventory))
            inventItem = i;

        var result = orig(self, i);

        if(_netRole != NetRole.None && IsLocalInventory(self))
            SendEquippedWeapons(self);

        return result;
    }

    internal bool NotifyInventoryEquipFromKingWeaponHooks(Hook_Inventory.orig_equip orig, Inventory self, InventItem i)
    {
        var result = orig(self, i);
        if(_inventorySyncGuard)
            return result;
        if(!IsLocalInventory(self))
            return result;
        SendEquippedWeapons(self);
        return result;
    }

    internal void NotifyInventorySwapWeaponsFromKingWeaponHooks(Hook_Inventory.orig_swapWeapons orig, Inventory self)
    {
        orig(self);
        if(_inventorySyncGuard)
            return;
        if(!IsLocalInventory(self))
            return;
        SendEquippedWeapons(self);
    }

    internal void NotifyInventoryReplaceFromKingWeaponHooks(Hook_Inventory.orig_replace orig, Inventory self, InventItem by, InventItem oldPos)
    {
        orig(self, by, oldPos);
        if(_inventorySyncGuard)
            return;
        if(!IsLocalInventory(self))
            return;
        SendEquippedWeapons(self);
    }

    internal void NotifyLocalWeaponPrepareFromKingWeaponHooks(Weapon self)
    {
        if(_netRole == NetRole.None || self == null || me == null)
            return;

        if(!ReferenceEquals(self.owner, me))
            return;

        var item = self.item;
        if(item == null || !TryGetWeaponKindId(item, out var kindId) || string.IsNullOrWhiteSpace(kindId))
            return;

        // Flint's local attack remains completely vanilla. Do not request a duplicated remote
        // weapon runtime for powered-feedback weapons; remote damage is already carried by MobSync
        // and the normal player animation stream still provides the visible swing.
        if(DeadCellsMultiplayerMod.Ghost.KingWeaponSupport.RequiresVisualOnlyRemoteReplay(kindId, self, out var visualOnlyReason))
        {
            LogVisualOnlyLocalWeaponOnce(kindId!, self, visualOnlyReason);
            _visualOnlyWeaponAnimUntilTicks = Stopwatch.GetTimestamp() +
                                              (long)(Stopwatch.Frequency * 1.75);
            // Flint does not send ATK because that would create the unsafe remote weapon runtime.
            // Clear the body-animation dedupe so the first vanilla charge/swing frame is sent even
            // when it happens to reuse the last animation name.
            _lastAnimSent = null;
            _lastAnimQueueSent = null;
            _lastAnimGSent = null;
            return;
        }

        var isShield = IsShieldWeaponKind(kindId!);

        var slot = ResolveWeaponSlotForSend(me.inventory, item, kindId!);
        if(slot < 0)
            slot = 0;

        var now = Stopwatch.GetTimestamp();
        var attackKey = $"{kindId}|{slot}|{item.permanentId}";
        if(!isShield)
        {
            var minRepeatTicks = (long)(Stopwatch.Frequency * LocalAttackRepeatBlockSeconds);
            if(_lastLocalAttackSendTicks != 0 &&
               now - _lastLocalAttackSendTicks < minRepeatTicks &&
               string.Equals(_lastLocalAttackKey, attackKey, StringComparison.Ordinal))
                return;

            _lastLocalAttackSendTicks = now;
            _lastLocalAttackKey = attackKey;
        }
        else
        {
            _lastLocalShieldPulseTicks = now;
        }

        var ammo = GetWeaponAmmoForSync(item);
        _net?.SendAttack(kindId!, slot, item.permanentId, ammo);
        _suppressHeroAnimUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.18);
        // Attack poses travel via ATK, not ANIM. Invalidate the idle dedupe cache so a standing
        // re-idle after the swing is actually sent to peers.
        _lastAnimSent = null;
        _lastAnimQueueSent = null;
        _lastAnimGSent = null;
    }

    internal void NotifyLocalShieldHoldingPulseFromKingWeaponHooks(BaseShield self, double ratio)
    {
        if(_netRole == NetRole.None || self == null || me == null)
            return;

        if(!ReferenceEquals(self.owner, me))
            return;

        var item = self.item;
        if(item == null || !TryGetWeaponKindId(item, out var kindId) || string.IsNullOrWhiteSpace(kindId))
            return;
        if(!IsShieldWeaponKind(kindId!))
            return;

        var now = Stopwatch.GetTimestamp();
        var pulseTicks = (long)(Stopwatch.Frequency * LocalShieldPulseSeconds);
        if(_lastLocalShieldPulseTicks != 0 && now - _lastLocalShieldPulseTicks < pulseTicks)
            return;

        var slot = ResolveWeaponSlotForSend(me.inventory, item, kindId!);
        if(slot < 0)
            slot = 0;

        var ammo = GetWeaponAmmoForSync(item);
        _net?.SendAttack(kindId!, slot, item.permanentId, ammo);
        _lastLocalShieldPulseTicks = now;
    }

    internal void NotifyLocalWeaponInterruptFromKingWeaponHooks(Weapon self)
    {
        if(_netRole == NetRole.None || self == null || me == null)
            return;

        if(!ReferenceEquals(self.owner, me))
            return;

        var item = self.item;
        if(item == null || !TryGetWeaponKindId(item, out var kindId) || string.IsNullOrWhiteSpace(kindId))
            return;

        if(DeadCellsMultiplayerMod.Ghost.KingWeaponSupport.RequiresVisualOnlyRemoteReplay(kindId, self, out var visualOnlyReason))
        {
            LogVisualOnlyLocalWeaponOnce(kindId!, self, visualOnlyReason);
            return;
        }

        var slot = ResolveWeaponSlotForSend(me.inventory, item, kindId!);
        if(slot < 0)
            slot = 0;

        var now = Stopwatch.GetTimestamp();
        var interruptKey = $"{kindId}|{slot}|{item.permanentId}";
        var minRepeatTicks = (long)(Stopwatch.Frequency * LocalInterruptRepeatBlockSeconds);
        if(_lastLocalInterruptSendTicks != 0 &&
           now - _lastLocalInterruptSendTicks < minRepeatTicks &&
           string.Equals(_lastLocalInterruptKey, interruptKey, StringComparison.Ordinal))
            return;

        _lastLocalInterruptSendTicks = now;
        _lastLocalInterruptKey = interruptKey;

        var ammo = GetWeaponAmmoForSync(item);
        _net?.SendAttack(kindId!, slot, item.permanentId, ammo, RemoteAttackAction.Interrupt);
    }

    internal void NotifyLocalBowShotFromKingWeaponHooks(BaseBow self)
    {
        if(_netRole == NetRole.None || self == null || me == null)
            return;
        if(!ReferenceEquals(self.owner, me))
            return;

        NotifyLocalAmmoChangedFromKingWeaponHooks(self.item);
    }

    internal void NotifyLocalAmmoChangedFromKingWeaponHooks(InventItem? item)
    {
        if(_netRole == NetRole.None || item == null || me == null)
            return;

        if(!TryGetWeaponKindId(item, out var kindId) || string.IsNullOrWhiteSpace(kindId))
            return;

        var inv = me.inventory;
        if(inv == null)
            return;

        var slot = ResolveWeaponSlotForSend(inv, item, kindId!);
        if(slot < 0)
        {
            SendEquippedWeapons(inv);
            return;
        }

        var ammo = GetWeaponAmmoForSync(item);
        _net?.SendInventoryWeapon(kindId!, slot, item.permanentId, ammo);
    }

    private void LogVisualOnlyLocalWeaponOnce(string kindId, Weapon weapon, string reason)
    {
        var runtimeName = string.Empty;
        try
        {
            runtimeName = weapon?.GetType().FullName ?? weapon?.GetType().Name ?? string.Empty;
        }
        catch
        {
        }

        var key = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{kindId}|{runtimeName}|{reason}");

        if(!_visualOnlyLocalWeaponNotices.Add(key))
            return;

        Logger.Information(
            "[NetMod][FlintGuard] local vanilla weapon kept remote-visual-only kind={Kind} runtime={Runtime} reason={Reason}",
            kindId,
            runtimeName,
            reason);
    }

    private static bool IsShieldWeaponKind(string kindId)
    {
        return kindId.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0 ||
               kindId.IndexOf("parry", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private int ResolveWeaponSlotForSend(Inventory? inv, InventItem item, string kindId)
    {
        if(inv == null)
            return -1;

        var slot = GetWeaponSlot(inv, item);
        if(slot >= 0)
            return slot;

        var w0 = inv.getEquippedWeaponOn(0);
        if(IsSameWeaponIdentity(w0, item, kindId))
            return 0;

        var w1 = inv.getEquippedWeaponOn(1);
        if(IsSameWeaponIdentity(w1, item, kindId))
            return 1;

        return slot;
    }

    private static bool IsSameWeaponIdentity(InventItem? candidate, InventItem item, string kindId)
    {
        if(candidate == null || item == null)
            return false;

        if(ReferenceEquals(candidate, item))
            return true;

        if(item.permanentId != 0 && candidate.permanentId == item.permanentId)
            return true;

        if(TryGetWeaponKindId(candidate, out var candidateKind) &&
           !string.IsNullOrWhiteSpace(candidateKind) &&
           string.Equals(candidateKind, kindId, StringComparison.Ordinal))
            return true;

        return false;
    }
}

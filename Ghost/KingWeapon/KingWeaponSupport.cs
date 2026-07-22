using System.Runtime.CompilerServices;
using dc.en;
using dc.libs.heaps.slib;
using dc.pr;
using dc.tool;
using dc.tool.skill;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.Ghost;

// King weapons are created with Weapon.owner = the local Hero because engine APIs (Weapon.create, skills, areas)
// require a Hero. Logical attribution uses ConditionalWeakTable binds to KingSkin. WithKingContextCore temporarily
// copies KingSkin pose/level/team/sprite onto that Hero so vanilla weapon code reads the king; callers must not
// assume Hero global state matches KingSkin outside WithKingContext. Main-thread-only: context uses [ThreadStatic].
internal static class KingWeaponSupport
{
    private static readonly ConditionalWeakTable<Weapon, KingSkin> WeaponToSource = new();
    private static readonly ConditionalWeakTable<InventItem, KingSkin> ItemToSource = new();
    private static readonly ConditionalWeakTable<OldSkill, SkillHooks> WrappedSkills = new();
    private static readonly ConditionalWeakTable<Weapon, object> RetiredRemoteWeapons = new();
    private static readonly ConditionalWeakTable<InventItem, object> RetiredRemoteItems = new();
    private static readonly object RetiredMarker = new();

    [ThreadStatic]
    private static int _contextDepth;
    [ThreadStatic]
    private static int _allowLocalHeroDamageDepth;
    [ThreadStatic]
    private static KingSkin? _currentContextSource;

    internal static bool IsInKingContext => _contextDepth > 0;
    internal static bool IsLocalHeroDamageAllowedInKingContext => _allowLocalHeroDamageDepth > 0;

    /// <summary>
    /// Some Dead Cells weapons are not safe to instantiate or advance against the detached remote
    /// ghost. Flint is the confirmed case: its powered feedback/timing path can dereference local
    /// runtime state that does not exist for the ghost. These weapons stay visual-only; MobSync
    /// remains responsible for authoritative enemy damage and death.
    /// </summary>
    internal static bool RequiresVisualOnlyRemoteReplay(string? kindId)
    {
        return RequiresVisualOnlyRemoteReplay(kindId, null, out _);
    }

    /// <summary>
    /// Detects Flint and any powered-feedback weapon before a detached ghost is allowed to bind,
    /// patch or advance its runtime. The kind-id check covers known data aliases while the runtime
    /// reflection check catches generated proxy names/members such as stopPoweredFeedback even when
    /// the item id itself does not contain the display name "Flint".
    /// </summary>
    internal static bool RequiresVisualOnlyRemoteReplay(string? kindId, Weapon? runtimeWeapon, out string reason)
    {
        var kind = kindId?.Trim() ?? string.Empty;
        var compactKind = CompactWeaponId(kind);

        if(compactKind.Contains("flint", StringComparison.OrdinalIgnoreCase))
        {
            reason = "kind-flint";
            return true;
        }

        // Flint has appeared under a non-display internal id in some game/proxy revisions.
        if(compactKind.Contains("powerfulmelee", StringComparison.OrdinalIgnoreCase) ||
           compactKind.Contains("poweredmelee", StringComparison.OrdinalIgnoreCase))
        {
            reason = "kind-powered-melee";
            return true;
        }

        if(runtimeWeapon != null && TryFindPoweredFeedbackRuntimeSignal(runtimeWeapon, out var signal))
        {
            reason = signal;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string CompactWeaponId(string value)
    {
        if(string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
    }

    private static bool TryFindPoweredFeedbackRuntimeSignal(Weapon weapon, out string signal)
    {
        signal = string.Empty;
        if(weapon == null)
            return false;

        System.Type? runtimeType;
        try
        {
            runtimeType = weapon.GetType();
        }
        catch
        {
            return false;
        }

        while(runtimeType != null)
        {
            var typeName = runtimeType.FullName ?? runtimeType.Name ?? string.Empty;
            var compactTypeName = CompactWeaponId(typeName);
            if(compactTypeName.Contains("flint", StringComparison.OrdinalIgnoreCase) ||
               compactTypeName.Contains("powerfulmelee", StringComparison.OrdinalIgnoreCase) ||
               compactTypeName.Contains("poweredmelee", StringComparison.OrdinalIgnoreCase))
            {
                signal = "runtime-type:" + typeName;
                return true;
            }

            try
            {
                var members = runtimeType.GetMembers(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly);

                for(int i = 0; i < members.Length; i++)
                {
                    var memberName = members[i]?.Name ?? string.Empty;
                    if(memberName.Contains("poweredFeedback", StringComparison.OrdinalIgnoreCase) ||
                       memberName.Contains("stopPoweredFeedback", StringComparison.OrdinalIgnoreCase))
                    {
                        signal = "runtime-member:" + memberName;
                        return true;
                    }
                }
            }
            catch
            {
                // Generated Hashlink proxy reflection is best-effort only.
            }

            runtimeType = runtimeType.BaseType;
        }

        return false;
    }

    /// <summary>Rejects remote-only powered feedback animations that can reference a missing weapon controller.</summary>
    internal static bool IsUnsafeRemoteGhostAnimation(string? anim)
    {
        if(string.IsNullOrWhiteSpace(anim))
            return false;

        var value = anim.Trim();
        if(value.IndexOf("stopPoweredFeedback", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if(value.IndexOf("poweredFeedback", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if(value.IndexOf("flint", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
    internal static bool TryGetCurrentContextSource(out KingSkin source)
    {
        if(!IsInKingContext || _currentContextSource == null)
        {
            source = null!;
            return false;
        }

        source = _currentContextSource;
        return true;
    }

    /// <summary>Saved Hero fields for WithKingContextCore; keeps save/restore in one place when extending the swap.</summary>
    internal readonly struct KingWeaponRuntimeFrame
    {
        public readonly HSprite? spr;
        public readonly Level? _level;
        public readonly Team? _team;
        public readonly int cx;
        public readonly int cy;
        public readonly double xr;
        public readonly double yr;
        public readonly int dir;
        public readonly double dx;
        public readonly double dy;

        public KingWeaponRuntimeFrame(Hero hero)
        {
            spr = hero.spr;
            _level = hero._level;
            _team = hero._team;
            cx = hero.cx;
            cy = hero.cy;
            xr = hero.xr;
            yr = hero.yr;
            dir = hero.dir;
            dx = hero.dx;
            dy = hero.dy;
        }

        public void ApplyKingSkin(KingSkin? src, Hero hero)
        {
            if(src == null)
                return;
            if(src.spr != null)
                hero.spr = src.spr;
            if(src._level != null)
                hero._level = src._level;
            if(src._team != null)
                hero._team = src._team;
            hero.cx = src.cx;
            hero.cy = src.cy;
            hero.xr = src.xr;
            hero.yr = src.yr;
            hero.dir = src.dir;
            hero.dx = src.dx;
            hero.dy = src.dy;
        }

        public void Restore(Hero hero)
        {
            hero.spr = spr;
            hero._level = _level;
            hero._team = _team;
            hero.cx = cx;
            hero.cy = cy;
            hero.xr = xr;
            hero.yr = yr;
            hero.dir = dir;
            hero.dx = dx;
            hero.dy = dy;
        }
    }

    private sealed class SkillHooks
    {
        public HlAction? DynOnChargeStart;
        public HlAction<double>? DynOnCharging;
        public HlAction? DynOnChargeComplete;
        public HlAction<double>? DynOnExecute;
        public HlAction? DynOnAttackAnim;
        public HlAction? DynOnFxFrame;
        public HlAction<double>? DynOnInterrupt;
    }

    public static Weapon CreateWeaponCandidate(Hero owner, InventItem item)
    {
        Weapon weapon;
        try
        {
            weapon = Weapon.Class.create(owner, item);
        }
        catch
        {
            weapon = new Weapon(owner, item);
        }

        if(weapon == null)
            weapon = new Weapon(owner, item);

        return weapon;
    }

    public static void ActivateRemoteWeapon(Weapon weapon, KingSkin source)
    {
        if(weapon == null || source == null)
            return;

        Bind(weapon, source);
        SyncSource(weapon);
        PatchSkills(weapon);
    }

    public static Weapon CreateWeapon(Hero owner, InventItem item, KingSkin source)
    {
        var weapon = CreateWeaponCandidate(owner, item);
        ActivateRemoteWeapon(weapon, source);
        return weapon;
    }

    public static void Bind(Weapon weapon, KingSkin source)
    {
        if(weapon == null || source == null)
            return;

        WeaponToSource.Remove(weapon);
        WeaponToSource.Add(weapon, source);
        RetiredRemoteWeapons.Remove(weapon);

        var item = weapon.item;
        if(item != null)
        {
            ItemToSource.Remove(item);
            ItemToSource.Add(item, source);
            RetiredRemoteItems.Remove(item);
        }
    }

    public static void Unbind(Weapon weapon)
    {
        if(weapon == null)
            return;

        InventItem? item = null;
        try { item = weapon.item; } catch { }
        if(item != null)
        {
            try { ItemToSource.Remove(item); } catch { }
        }
        try { WeaponToSource.Remove(weapon); } catch { }
    }

    /// <summary>
    /// Retires a detached remote weapon while its owner is still bound to the GhostKing.
    /// Native weapon disposal can touch the owner's cooldowns and schedule delayed skill callbacks;
    /// running it after Unbind would make those paths operate on the real local Hero instead.
    /// </summary>
    public static void RetireRemoteWeapon(Weapon weapon)
    {
        if(weapon == null)
            return;

        InventItem? item = null;
        try { item = weapon.item; } catch { }
        try
        {
            RetiredRemoteWeapons.Remove(weapon);
            RetiredRemoteWeapons.Add(weapon, RetiredMarker);
        }
        catch { }
        if(item != null)
        {
            try
            {
                RetiredRemoteItems.Remove(item);
                RetiredRemoteItems.Add(item, RetiredMarker);
            }
            catch { }
        }

        // Disarm while the generated proxy is still valid. Disposal may invalidate skill access.
        try { DisarmSkillCallbacks(weapon); } catch { }

        Hero? owner = null;
        try { owner = weapon.owner; } catch { }
        if(owner != null && TryGetSource(weapon, out var source) && source != null)
        {
            try
            {
                WithKingContextCore(owner, source, () =>
                {
                    try
                    {
                        if(!weapon.destroyed)
                            weapon.dispose();
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }
        }

        // A Haxe Timer may still hold one of our wrapper delegates after the weapon has been
        // disposed. Removing the binding makes an already-captured wrapper a no-op instead of
        // falling back to the local Hero.
        Unbind(weapon);
    }

    public static bool TryGetSource(Weapon weapon, out KingSkin source)
    {
        if(weapon == null)
        {
            source = null!;
            return false;
        }
        return WeaponToSource.TryGetValue(weapon, out source!);
    }

    public static bool TryGetSourceByItem(InventItem? item, out KingSkin source)
    {
        if(item == null)
        {
            source = null!;
            return false;
        }
        return ItemToSource.TryGetValue(item, out source!);
    }

    public static bool IsKingWeapon(Weapon weapon)
    {
        return weapon != null &&
            (WeaponToSource.TryGetValue(weapon, out _) || RetiredRemoteWeapons.TryGetValue(weapon, out _));
    }

    public static bool IsRetiredRemoteWeapon(Weapon weapon)
    {
        return weapon != null && RetiredRemoteWeapons.TryGetValue(weapon, out _);
    }

    public static bool IsRetiredRemoteItem(InventItem? item)
    {
        return item != null && RetiredRemoteItems.TryGetValue(item, out _);
    }

    public static void WithKingContext(Weapon weapon, Action action)
    {
        if(action == null)
            return;

        if(!TryGetSource(weapon, out var src))
        {
            // This overload exists only for detached remote weapons. Once a weapon is unbound it
            // is retired; delayed Haxe Timer callbacks must not execute against the local Hero.
            return;
        }

        if(_contextDepth > 0)
        {
            action();
            return;
        }

        WithKingContextCore(weapon?.owner, src, action);
    }

    public static void WithKingContext(Hero hero, KingSkin source, Action action)
    {
        if(action == null)
            return;

        if(_contextDepth > 0)
        {
            action();
            return;
        }

        WithKingContextCore(hero, source, action);
    }

    public static T WithKingContext<T>(Hero hero, KingSkin source, Func<T> func)
    {
        T result = default!;
        WithKingContext(hero, source, () => { result = func(); });
        return result;
    }

    public static void WithLocalHeroDamageAllowed(Action action)
    {
        if(action == null)
            return;

        _allowLocalHeroDamageDepth++;
        try
        {
            action();
        }
        finally
        {
            _allowLocalHeroDamageDepth--;
        }
    }

    private static void WithKingContextCore(Hero? hero, KingSkin? src, Action action)
    {
        if(hero == null || src == null)
        {
            action();
            return;
        }

        _contextDepth++;
        var previousSource = _currentContextSource;
        _currentContextSource = src;

        var frame = new KingWeaponRuntimeFrame(hero);
        try
        {
            frame.ApplyKingSkin(src, hero);
            action();
        }
        finally
        {
            frame.Restore(hero);
            _currentContextSource = previousSource;
            _contextDepth--;
        }
    }

    public static T WithKingContext<T>(Weapon weapon, Func<T> func)
    {
        T result = default!;
        WithKingContext(weapon, () => { result = func(); });
        return result;
    }

    public static void SyncSource(Weapon weapon)
    {
        if(weapon == null || IsRetiredRemoteWeapon(weapon))
            return;

        if(!TryGetSource(weapon, out var source))
            return;

        var arr = weapon.areas;
        if(source == null || arr == null)
            return;

        for(int i = 0; i < arr.length; i++)
        {
            var a = arr.array[i] as Area;
            if(a != null)
                a.setRelativePos(source, a.x, a.y);
        }
    }

    public static void PatchSkills(Weapon weapon)
    {
        if(weapon == null || IsRetiredRemoteWeapon(weapon))
            return;

        var arr = weapon.skills;
        if(arr == null)
            return;

        for(int i = 0; i < arr.length; i++)
        {
            var s = arr.array[i] as WeaponSkill;
            if(s == null)
                continue;

            WrapSkillCallbacks(weapon, s);

            s.lockControlsAfterUseS = 0.0;
            s.canMoveDuringCharge = true;
        }
    }

    public static void PatchCurrentSkill(Weapon weapon)
    {
        if(weapon == null || IsRetiredRemoteWeapon(weapon))
            return;

        WeaponSkill s;
        try
        {
            s = weapon.get_curSkill();
        }
        catch
        {
            return;
        }

        if(s == null)
            return;

        WrapSkillCallbacks(weapon, s);

        s.lockControlsAfterUseS = 0.0;
        s.canMoveDuringCharge = true;
    }

    private static void WrapSkillCallbacks(Weapon weapon, OldSkill skill)
    {
        if(weapon == null || skill == null)
            return;

        if(WrappedSkills.TryGetValue(skill, out _))
            return;

        var hooks = new SkillHooks
        {
            DynOnChargeStart = skill.dynOnChargeStart,
            DynOnCharging = skill.dynOnCharging,
            DynOnChargeComplete = skill.dynOnChargeComplete,
            DynOnExecute = skill.dynOnExecute,
            DynOnAttackAnim = skill.dynOnAttackAnim,
            DynOnFxFrame = skill.dynOnFxFrame,
            DynOnInterrupt = skill.dynOnInterrupt
        };
        WrappedSkills.Add(skill, hooks);

        if(hooks.DynOnChargeStart != null)
            skill.dynOnChargeStart = () => WithKingContext(weapon, () => hooks.DynOnChargeStart?.Invoke());

        if(hooks.DynOnCharging != null)
            skill.dynOnCharging = r => WithKingContext(weapon, () => hooks.DynOnCharging?.Invoke(r));

        if(hooks.DynOnChargeComplete != null)
            skill.dynOnChargeComplete = () => WithKingContext(weapon, () => hooks.DynOnChargeComplete?.Invoke());

        if(hooks.DynOnExecute != null)
            skill.dynOnExecute = ratio => WithKingContext(weapon, () => hooks.DynOnExecute?.Invoke(ratio));

        if(hooks.DynOnAttackAnim != null)
            skill.dynOnAttackAnim = () => WithKingContext(weapon, () => hooks.DynOnAttackAnim?.Invoke());

        if(hooks.DynOnFxFrame != null)
            skill.dynOnFxFrame = () => WithKingContext(weapon, () => hooks.DynOnFxFrame?.Invoke());

        if(hooks.DynOnInterrupt != null)
            skill.dynOnInterrupt = r => WithKingContext(weapon, () => hooks.DynOnInterrupt?.Invoke(r));
    }

    private static void DisarmSkillCallbacks(Weapon weapon)
    {
        if(weapon == null)
            return;

        var arr = weapon.skills;
        if(arr != null)
        {
            for(int i = 0; i < arr.length; i++)
            {
                if(arr.array[i] is OldSkill skill)
                    DisarmSkillCallbacks(skill);
            }
        }

        try
        {
            var current = weapon.get_curSkill();
            if(current != null)
                DisarmSkillCallbacks(current);
        }
        catch
        {
        }
    }

    private static void DisarmSkillCallbacks(OldSkill skill)
    {
        if(skill == null)
            return;

        try { skill.dynOnChargeStart = () => { }; } catch { }
        try { skill.dynOnCharging = _ => { }; } catch { }
        try { skill.dynOnChargeComplete = () => { }; } catch { }
        try { skill.dynOnExecute = _ => { }; } catch { }
        try { skill.dynOnAttackAnim = () => { }; } catch { }
        try { skill.dynOnFxFrame = () => { }; } catch { }
        try { skill.dynOnInterrupt = _ => { }; } catch { }
        WrappedSkills.Remove(skill);
    }
}

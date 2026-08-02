using dc;
using dc.en;
using dc.en.inter;
using dc.hl.types;
using dc.pr;
using dc.tool.atk;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Events.Interfaces.Game.Hero;
using Serilog;
using System.Reflection;

namespace DeadCellsMultiplayerMod.Interaction;

public partial class InteractionSync
{
    private static T? FindNearestByPos<T>(Level level, double x, double y, double maxDistSq) where T : Entity
    {
        var candidates = GetInteractionCandidates<T>(level);
        if (candidates == null || candidates.Count == 0)
            return null;

        T? nearest = null;
        double nearestSq = maxDistSq;
        for (var i = 0; i < candidates.Count; i++)
        {
            var e = candidates[i];
            if (e?.spr == null) continue;
            try
            {
                var dx = e.spr.x - x;
                var dy = e.spr.y - y;
                var dSq = dx * dx + dy * dy;
                if (dSq < nearestSq)
                {
                    nearestSq = dSq;
                    nearest = e;
                }
            }
            catch { }
        }
        return nearest;
    }

    private static object? TryGetLevelTriggers(Level level)
    {
        try
        {
            var fromProperty = LevelTriggersProperty?.GetValue(level);
            if (fromProperty != null)
                return fromProperty;
            return LevelTriggersField?.GetValue(level);
        }
        catch
        {
            return null;
        }
    }

    private static int GetTriggerArrayLength(object? triggers)
    {
        if (triggers is ArrayObj ao)
            return ao.length;
        if (triggers is ArrayDyn ad)
            return ad.get_length();
        return 0;
    }

    private static T? GetTriggerAt<T>(object? triggers, int i) where T : class
    {
        if (triggers is ArrayObj ao)
            return ao.getDyn(i) as T;
        if (triggers is ArrayDyn ad)
            return ad.getDyn(i) as T;
        return null;
    }

    private static T? FindNearestTriggerByPos<T>(Level level, double x, double y, double maxDistSq) where T : Entity
    {
        try
        {
            var triggers = GetInteractionTriggerCandidates<T>(level);
            if (triggers == null || triggers.Count == 0)
                return null;

            T? nearest = null;
            var nearestSq = maxDistSq;
            for (var i = 0; i < triggers.Count; i++)
            {
                var t = triggers[i];
                if (t?.spr == null) continue;
                var dx = t.spr.x - x;
                var dy = t.spr.y - y;
                var dSq = dx * dx + dy * dy;
                if (dSq < nearestSq)
                {
                    nearestSq = dSq;
                    nearest = t;
                }
            }
            return nearest;
        }
        catch
        {
            return null;
        }
    }

    private static LevelInteractionCache GetInteractionCache(Level level)
    {
        var entityCount = level.entities?.length ?? 0;
        var triggerCount = GetTriggerArrayLength(TryGetLevelTriggers(level));
        if (!ReferenceEquals(_cachedInteractionLevel, level) ||
            entityCount != _cachedInteractionEntityCount ||
            triggerCount != _cachedInteractionTriggerCount)
        {
            RebuildInteractionCache(level);
        }

        return CachedInteractionLevelData;
    }

    private static void RebuildInteractionCache(Level? level)
    {
        CachedInteractionLevelData.Clear();
        _cachedInteractionLevel = level;
        _cachedInteractionEntityCount = -1;
        _cachedInteractionTriggerCount = -1;

        if (level == null)
            return;

        var entities = level.entities;
        _cachedInteractionEntityCount = entities?.length ?? 0;
        if (entities != null)
        {
            for (var i = 0; i < entities.length; i++)
            {
                switch (entities.getDyn(i))
                {
                    case Door door:
                        CachedInteractionLevelData.Doors.Add(door);
                        break;
                    case Elevator elevator:
                        CachedInteractionLevelData.Elevators.Add(elevator);
                        break;
                    case VineLadder vineLadder:
                        CachedInteractionLevelData.VineLadders.Add(vineLadder);
                        break;
                    case Teleport teleport:
                        CachedInteractionLevelData.Teleports.Add(teleport);
                        break;
                    case Portal portal:
                        CachedInteractionLevelData.Portals.Add(portal);
                        break;
                    case PressurePlate pressurePlate:
                        CachedInteractionLevelData.PressurePlates.Add(pressurePlate);
                        break;
                    case dc.en.inter.button.Button button:
                        CachedInteractionLevelData.Buttons.Add(button);
                        break;
                    case TreasureChest treasureChest:
                        CachedInteractionLevelData.TreasureChests.Add(treasureChest);
                        break;
                    case SwitchBossRune switchBossRune:
                        CachedInteractionLevelData.SwitchBossRunes.Add(switchBossRune);
                        break;
                }
            }
        }

        var triggers = TryGetLevelTriggers(level);
        var triggerCount = GetTriggerArrayLength(triggers);
        _cachedInteractionTriggerCount = triggerCount;
        for (var i = 0; i < triggerCount; i++)
        {
            switch (GetTriggerAt<Entity>(triggers, i))
            {
                case Elevator elevator:
                    CachedInteractionLevelData.TriggerElevators.Add(elevator);
                    break;
                case Teleport teleport:
                    CachedInteractionLevelData.TriggerTeleports.Add(teleport);
                    break;
                case Portal portal:
                    CachedInteractionLevelData.TriggerPortals.Add(portal);
                    break;
                case dc.en.inter.button.Button button:
                    CachedInteractionLevelData.TriggerButtons.Add(button);
                    break;
            }
        }
    }

    private static IReadOnlyList<T>? GetInteractionCandidates<T>(Level level) where T : Entity
    {
        var cache = GetInteractionCache(level);
        if (typeof(T) == typeof(Door))
            return (IReadOnlyList<T>)(object)cache.Doors;
        if (typeof(T) == typeof(Elevator))
            return (IReadOnlyList<T>)(object)cache.Elevators;
        if (typeof(T) == typeof(VineLadder))
            return (IReadOnlyList<T>)(object)cache.VineLadders;
        if (typeof(T) == typeof(Teleport))
            return (IReadOnlyList<T>)(object)cache.Teleports;
        if (typeof(T) == typeof(Portal))
            return (IReadOnlyList<T>)(object)cache.Portals;
        if (typeof(T) == typeof(PressurePlate))
            return (IReadOnlyList<T>)(object)cache.PressurePlates;
        if (typeof(T) == typeof(dc.en.inter.button.Button))
            return (IReadOnlyList<T>)(object)cache.Buttons;
        if (typeof(T) == typeof(TreasureChest))
            return (IReadOnlyList<T>)(object)cache.TreasureChests;
        if (typeof(T) == typeof(SwitchBossRune))
            return (IReadOnlyList<T>)(object)cache.SwitchBossRunes;
        return null;
    }

    private static IReadOnlyList<T>? GetInteractionTriggerCandidates<T>(Level level) where T : Entity
    {
        var cache = GetInteractionCache(level);
        if (typeof(T) == typeof(Elevator))
            return (IReadOnlyList<T>)(object)cache.TriggerElevators;
        if (typeof(T) == typeof(Teleport))
            return (IReadOnlyList<T>)(object)cache.TriggerTeleports;
        if (typeof(T) == typeof(Portal))
            return (IReadOnlyList<T>)(object)cache.TriggerPortals;
        if (typeof(T) == typeof(dc.en.inter.button.Button))
            return (IReadOnlyList<T>)(object)cache.TriggerButtons;
        return null;
    }

}

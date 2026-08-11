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
    private void Hook_Door_init(Hook_Door.orig_init orig, Door self)
    {
        orig(self);
        if (self == null)
            return;

        _doorStableAnchors[self] = ComputeDoorStableAnchor(self);
        var net = LobbySession.NetRef;
        if (net != null && net.IsAlive)
        {
            _doorHadAutoClose[self] = SafeRead(() => self.autoClose, false);
            self.autoClose = false;
        }
    }

    private void Hook_Door_open(Hook_Door.orig_open orig, Door self, int durationMs, int? finalRatio, double? _tween)
    {
        orig(self, durationMs, finalRatio, _tween);
        _openedDoors.Add(self);
        TrySendDoorEvent(self, "open");
    }

    private void Hook_Door_close(Hook_Door.orig_close orig, Door self, Ref<int> delayMs)
    {
        orig(self, delayMs);
        _openedDoors.Remove(self);
        TrySendDoorEvent(self, "close");
    }

    private void Hook_Door_onDamage(Hook_Door.orig_onDamage orig, Door self, AttackData a)
    {
        orig(self, a);
        TrySendDoorEvent(self, "damage");
    }

    private void Hook_Door_onDie(Hook_Door.orig_onDie orig, Door self)
    {
        orig(self);
        _openedDoors.Remove(self);
        TrySendDoorEvent(self, "die");
    }

    private void TrySendDoorEvent(Door self, string action)
    {
        if (_applyingRemoteDoorEvents)
            return;
        var net = LobbySession.NetRef;
        if (!IsNetReadyForSend(net))
            return;
        try
        {
            var (x, y) = GetDoorStableAnchor(self);
            var broken = action == "die" || SafeRead(() => self.broken, false);
            net!.SendInterDoor(net.id, x, y, action, broken, GetCurrentInteractionLevelId());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Door send failed");
        }
    }

    private (double X, double Y) GetDoorStableAnchor(Door door)
    {
        if (door == null)
            return (0, 0);

        if (_doorStableAnchors.TryGetValue(door, out var anchor))
            return anchor;

        anchor = ComputeDoorStableAnchor(door);
        _doorStableAnchors[door] = anchor;
        return anchor;
    }

    private static bool IsAnyPlayerNearby(Level level, double doorX, double doorY)
    {
        var hero = ModEntry.me;
        if (hero != null && ReferenceEquals(hero._level, level))
        {
            if (!SafeRead(() => hero.destroyed, true) && SafeRead(() => hero.life, 0) > 0)
            {
                var (hx, hy) = GetEntityPixelPos(hero);
                var dx = hx - doorX;
                var dy = hy - doorY;
                if (dx * dx + dy * dy <= DoorProximityRadiusSq)
                    return true;
            }
        }

        for (var i = 0; i < ModEntry.clients.Length; i++)
        {
            var client = ModEntry.clients[i];
            if (client == null)
                continue;
            if (!ReferenceEquals(client._level, level))
                continue;
            if (SafeRead(() => client.destroyed, true) || SafeRead(() => client.life, 0) <= 0)
                continue;

            var (cx, cy) = GetEntityPixelPos(client);
            var dx = cx - doorX;
            var dy = cy - doorY;
            if (dx * dx + dy * dy <= DoorProximityRadiusSq)
                return true;
        }

        return false;
    }

    private void ApplyDoorDie(Door door)
    {
        _openedDoors.Remove(door);
        if (!SafeRead(() => door.broken, false))
        {
            door.life = 0;
            door.onDie();
        }
    }

    private void BroadcastAuthoritativeDoorStates(NetNode net)
    {
        var level = ModEntry.me?._level;
        if (level == null || !net.IsHost || !IsNetReadyForSend(net))
            return;

        var now = System.Environment.TickCount64;
        var cache = GetInteractionCache(level);

        for (var i = 0; i < cache.Doors.Count; i++)
        {
            var door = cache.Doors[i];
            if (door == null)
                continue;

            var state = GetAuthoritativeDoorState(door);
            var changed = !_lastAuthoritativeDoorState.TryGetValue(door, out var previous) ||
                          !string.Equals(previous, state, StringComparison.Ordinal);
            var activeHeartbeat = state != "state_closed" &&
                                  (!_lastDoorStateSentTickMs.TryGetValue(door, out var lastSent) ||
                                   now - lastSent >= DoorStateHeartbeatMs);
            if (!changed && !activeHeartbeat)
                continue;

            _lastAuthoritativeDoorState[door] = state;
            _lastDoorStateSentTickMs[door] = now;
            var (x, y) = GetDoorStableAnchor(door);
            net.SendInterDoor(net.id, x, y, state, state == "state_broken", GetCurrentInteractionLevelId());
        }
    }

    private string GetAuthoritativeDoorState(Door door)
    {
        if (SafeRead(() => door.broken, false))
            return "state_broken";

        // A locked door is script-controlled (boss arena seals). Never advertise it as open:
        // the stale _openedDoors entry from walking through it earlier otherwise made the 2s
        // heartbeat force the client's sealed door back open for the whole fight.
        if (TryReadBooleanMember(door, "locked", "isLocked"))
            return "state_closed";

        if (TryReadBooleanMember(door, "opened", "isOpen", "open"))
            return "state_open";

        var ratio = TryReadNumericMember(door, "ratio", "openRatio", "openingRatio", "curRatio");
        if (ratio.HasValue)
            return ratio.Value > 0.45 ? "state_open" : "state_closed";

        // Only when no native state is readable at all, fall back to the open-hook cache.
        return _openedDoors.Contains(door) ? "state_open" : "state_closed";
    }

    private bool ShouldRejectRemoteDoorOpen(Door door)
    {
        // Script-sealed doors (boss arena locks) must never be reopened by replayed remote
        // events or heartbeats; the local fight script owns them until the encounter ends.
        if (TryReadBooleanMember(door, "locked", "isLocked"))
            return true;

        return DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.HasLivingTrackedBoss();
    }

    private static double? TryReadNumericMember(object instance, params string[] names)
    {
        if (instance == null)
            return null;

        var type = instance.GetType();
        foreach (var name in names)
        {
            try
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead == true)
                {
                    var value = property.GetValue(instance);
                    if (value != null)
                        return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                }

                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fieldValue = field?.GetValue(instance);
                if (fieldValue != null)
                    return Convert.ToDouble(fieldValue, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                // Ignore optional/generated members that cannot be read in this game build.
            }
        }

        return null;
    }

    private void ApplyRemoteDoorEvents(List<InterDoorEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        _applyingRemoteDoorEvents = true;
        try
        {
            var localId = LobbySession.NetRef?.id ?? 0;
            foreach (var ev in events)
            {
                if (ev.UserId == localId)
                    continue;
                if (!IsInteractionEventForCurrentLevel(ev.LevelId))
                    continue;

                var door = FindDoorByPos(level, ev.X, ev.Y);
                if (door == null)
                    continue;

                try
                {
                    switch (ev.Action)
                    {
                        case "open":
                            if (ShouldRejectRemoteDoorOpen(door))
                                break;
                            door.open(300, null, null);
                            break;
                        case "close":
                            if (SafeRead(() => door.broken, false))
                                break;
                            _openedDoors.Remove(door);
                            try
                            {
                                int delayMs = DoorCloseDelayMs;
                                door.close(Ref<int>.From(ref delayMs));
                            }
                            catch (Exception ex)
                            {
                                _log.Warning(ex, "[InteractionSync] close failed (door may be broken)");
                            }
                            break;
                        case "damage":
                            if (ev.Broken)
                                ApplyDoorDie(door);
                            break;
                        case "die":
                        case "state_broken":
                            ApplyDoorDie(door);
                            break;
                        case "state_open":
                            if (!SafeRead(() => door.broken, false) && !ShouldRejectRemoteDoorOpen(door))
                            {
                                door.open(180, null, null);
                                _openedDoors.Add(door);
                            }
                            break;
                        case "state_closed":
                            if (!SafeRead(() => door.broken, false))
                            {
                                _openedDoors.Remove(door);
                                int stateDelayMs = 0;
                                door.close(Ref<int>.From(ref stateDelayMs));
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply door event failed x={X} y={Y} action={Action}", ev.X, ev.Y, ev.Action);
                }
            }
        }
        finally
        {
            _applyingRemoteDoorEvents = false;
        }
    }

    private Door? FindDoorByPos(Level level, double x, double y)
    {
        var byAnchor = FindDoorByStableAnchor(level, x, y);
        if (byAnchor != null)
            return byAnchor;

        // Doors can be attached or proxy-wrapped after the early level cache is created. Refresh
        // once on a miss so elevator cache changes cannot leave button doors permanently invisible
        // to the interaction synchronizer.
        RebuildInteractionCache(level);
        byAnchor = FindDoorByStableAnchor(level, x, y);
        if (byAnchor != null)
            return byAnchor;

        var byPos = FindInteractByPos<Door>(level, x, y, DoorPosTolerance);
        if (byPos != null)
            return byPos;
        return FindNearestDoor(level, x, y);
    }

    private Door? FindDoorByStableAnchor(Level level, double x, double y)
    {
        var candidates = GetInteractionCandidates<Door>(level);
        if (candidates == null || candidates.Count == 0)
            return null;

        Door? nearest = null;
        var nearestSq = DoorPosTolerance * DoorPosTolerance * 4.0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var door = candidates[i];
            if (door == null)
                continue;

            try
            {
                if (!ReferenceEquals(door._level, level) || SafeRead(() => door.destroyed, true))
                    continue;
                var anchor = GetDoorStableAnchor(door);
                var dx = anchor.X - x;
                var dy = anchor.Y - y;
                var distanceSq = dx * dx + dy * dy;
                if (distanceSq < nearestSq)
                {
                    nearestSq = distanceSq;
                    nearest = door;
                }
            }
            catch
            {
                // Keep searching other doors.
            }
        }

        return nearest;
    }

    private static Door? FindNearestDoor(Level level, double x, double y) =>
        FindNearestByPos<Door>(level, x, y, DoorPosTolerance * DoorPosTolerance * 4);




    private void CheckAndCloseDoorsWhenNoOneNearby()
    {
        var level = ModEntry.me?._level;
        if (level == null)
            return;

        _scratchDoorsToRemove.Clear();
        _scratchDoorsToClose.Clear();
        foreach (var door in _openedDoors)
        {
            try
            {
                if (door == null || SafeRead(() => door.destroyed, true) || SafeRead(() => door.broken, false))
                {
                    _scratchDoorsToRemove.Add(door!);
                    continue;
                }
                if (!_doorHadAutoClose.TryGetValue(door, out var hadAutoClose) || !hadAutoClose)
                    continue;
                if (!ReferenceEquals(door._level, level))
                    continue;

                var (doorX, doorY) = GetDoorStableAnchor(door);
                if (IsAnyPlayerNearby(level, doorX, doorY))
                    continue;
                if (SafeRead(() => door.broken, false))
                    continue;

                _scratchDoorsToClose.Add(door);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[InteractionSync] Door auto-close check failed");
            }
        }

        if (_scratchDoorsToClose.Count > 0)
        {
            for (var i = 0; i < _scratchDoorsToClose.Count; i++)
            {
                var door = _scratchDoorsToClose[i];
                _openedDoors.Remove(door);
                try
                {
                    int delayMs = DoorCloseDelayMs;
                    door.close(Ref<int>.From(ref delayMs));
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] closeFast failed (door may be broken)");
                }
            }
        }

        if (_scratchDoorsToRemove.Count > 0)
        {
            for (var i = 0; i < _scratchDoorsToRemove.Count; i++)
                _openedDoors.Remove(_scratchDoorsToRemove[i]);
        }
    }

    private static (double X, double Y) ComputeDoorStableAnchor(Door door)
    {
        if (door == null)
            return (0, 0);

        // Door sprites can shift, tween, disappear, or be replaced during opening. The logical
        // entity coordinates remain fixed at the doorway and are therefore safe to use as the
        // cross-machine identity for button-controlled and pressure-plate doors.
        try
        {
            var x = (door.cx + door.xr) * TileSizePx;
            var y = (door.cy + door.yr) * TileSizePx;
            if (IsFinitePosition(x, y))
                return (x, y);
        }
        catch
        {
            // Fall back to the render anchor for unusual scripted door variants.
        }

        return GetEntityPixelPos(door);
    }
    private static bool TryReadBooleanMember(object instance, params string[] names)
    {
        if (instance == null)
            return false;

        var type = instance.GetType();
        foreach (var name in names)
        {
            try
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.CanRead == true && property.GetValue(instance) is bool propertyValue)
                    return propertyValue;

                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(instance) is bool fieldValue)
                    return fieldValue;
            }
            catch
            {
                // Ignore optional/generated members that cannot be read in this game build.
            }
        }

        return false;
    }
}

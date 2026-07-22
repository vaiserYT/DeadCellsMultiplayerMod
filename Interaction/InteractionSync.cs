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

public class InteractionSync :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnHeroUpdate
{
    private sealed class LevelInteractionCache
    {
        public readonly List<Door> Doors = new();
        public readonly List<Elevator> Elevators = new();
        public readonly List<VineLadder> VineLadders = new();
        public readonly List<Teleport> Teleports = new();
        public readonly List<Portal> Portals = new();
        public readonly List<PressurePlate> PressurePlates = new();
        public readonly List<TreasureChest> TreasureChests = new();
        public readonly List<SwitchBossRune> SwitchBossRunes = new();
        public readonly List<Elevator> TriggerElevators = new();
        public readonly List<Teleport> TriggerTeleports = new();
        public readonly List<Portal> TriggerPortals = new();

        public void Clear()
        {
            Doors.Clear();
            Elevators.Clear();
            VineLadders.Clear();
            Teleports.Clear();
            Portals.Clear();
            PressurePlates.Clear();
            TreasureChests.Clear();
            SwitchBossRunes.Clear();
            TriggerElevators.Clear();
            TriggerTeleports.Clear();
            TriggerPortals.Clear();
        }
    }

    private const double PosTolerance = 1.0;
    private const double PlatePosTolerance = 8.0;
    private const double ChestPosTolerance = 16.0;
    private const double DoorPosTolerance = 16.0;
    private const double TeleportPosTolerance = 48.0;
    private const double BreakableGroundPosTolerance = 24.0;
    private const double SwitchBossRunePosTolerance = 32.0;
    private const double ElevatorPosTolerance = 48.0;
    private const double PortalPosTolerance = 48.0;
    private const double TileSizePx = 24.0;
    private const double DoorProximityRadiusPx = 100.0;
    private static readonly double DoorProximityRadiusSq = DoorProximityRadiusPx * DoorProximityRadiusPx;
    private const int DoorCloseDelayMs = 250;
    private const int DoorStateHeartbeatMs = 2000;

    private readonly ILogger _log;
    private readonly HashSet<Door> _openedDoors = new();
    private readonly Dictionary<Door, bool> _doorHadAutoClose = new();
    private readonly Dictionary<Door, (double X, double Y)> _doorStableAnchors = new();
    private readonly List<Door> _scratchDoorsToRemove = new();
    private readonly List<Door> _scratchDoorsToClose = new();
    private readonly HashSet<Elevator> _scratchAppliedElevators = new();
    private readonly List<(double X, double Y)> _scratchAppliedBreakableGround = new();
    private static readonly PropertyInfo? LevelTriggersProperty = typeof(Level).GetProperty("triggers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? LevelTriggersField = typeof(Level).GetField("triggers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly LevelInteractionCache CachedInteractionLevelData = new();
    private static Level? _cachedInteractionLevel;
    private static int _cachedInteractionEntityCount = -1;
    private static int _cachedInteractionTriggerCount = -1;
    private bool _applyingRemoteDoorEvents;
    private bool _applyingRemoteChestEvents;
    private bool _applyingRemotePressurePlateEvents;
    private bool _applyingRemoteVineLadderEvents;
    private bool _applyingRemoteTeleportEvents;
    private bool _applyingRemoteBreakableGroundEvents;
    private bool _applyingRemotePortalEvents;
    private bool _applyingRemoteElevatorEvents;
    /// <summary>Throttle elevator activation pulses — onStep can fire every frame while riding.</summary>
    private readonly Dictionary<Elevator, long> _elevatorLastInterSendTickMs = new();
    private readonly Dictionary<(PressurePlate Plate, int UserId), long> _pressurePlateLastAppliedSequence = new();
    private readonly Dictionary<Door, string> _lastAuthoritativeDoorState = new();
    private readonly Dictionary<Door, long> _lastDoorStateSentTickMs = new();
    private Level? _interactionRuntimeLevel;
    private long _nextPressurePlateSequence;

    public InteractionSync(ModEntry entry)
    {
        _log = entry.Logger;
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        entry.Logger.Information("\x1b[32m[[InteractionSync] Initializing InteractionSync...]\x1b[0m ");

        Hook_Door.init += Hook_Door_init;
        Hook_Door.open += Hook_Door_open;
        Hook_Door.close += Hook_Door_close;
        Hook_Door.onDamage += Hook_Door_onDamage;
        Hook_Door.onDie += Hook_Door_onDie;
        Hook_Elevator.onStep += Hook_Elevator_onStep;
        Hook_PressurePlate.trigger += Hook_PressurePlate_trigger;
        // Don't hook executeOn - it fires every frame when standing, causing infinite event flood
        Hook_TreasureChest.open += Hook_TreasureChest_open;
        Hook_VineLadder.activate += Hook_VineLadder_activate;
        Hook_Teleport.open += Hook_Teleport_open;
        Hook_Portal.show += Hook_Portal_show;
        Hook_Portal.close += Hook_Portal_close;
        Hook_Hero.breakBreakableGround += Hook_Hero_breakBreakableGround;
        Hook_SwitchBossRune.canBeActivated += Hook_SwitchBossRune_canBeActivated;
        Hook_SwitchBossRune.close += Hook_SwitchBossRune_close;
        Hook_SwitchBossRune.updateCells += Hook_SwitchBossRune_updateCells;
    }


    private bool Hook_SwitchBossRune_canBeActivated(Hook_SwitchBossRune.orig_canBeActivated orig, SwitchBossRune self, Hero by)
    {
        var net = GameMenu.NetRef;
        if(net != null && !net.IsHost)
            return false;
        return orig(self, by);
    }

    private void Hook_SwitchBossRune_close(Hook_SwitchBossRune.orig_close orig, SwitchBossRune self)
    {
        orig(self);

        var net = GameMenu.NetRef;
        if (!IsNetReadyForSend(net) || !net!.IsHost)
            return;

        try
        {
            // updateCells already sends the visual +/- edge. close publishes only the final
            // authoritative value so the peer cannot process duplicate boss-cell changes or
            // schedule overlapping reloads.
            var user = self?._level?.game?.user ?? dc.Main.Class.ME?.user;
            if (user != null)
                GameDataSync.SendBossRune(user, net);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Failed to send boss rune after SwitchBossRune.close");
        }
    }

    private void Hook_SwitchBossRune_updateCells(Hook_SwitchBossRune.orig_updateCells orig, SwitchBossRune self, bool add)
    {
        var net = GameMenu.NetRef;

        // updateCells performs a native main-level rebuild. Dispose the old remote render shells
        // before that rebuild starts; otherwise Boot.tryRender can visit a GhostKing whose sprite
        // group has already been destroyed and crash Game.loadMainLevel with Null access .groupName.
        if (IsNetReadyForSend(net) && net!.IsHost)
        {
            ModEntry.PrepareAndDisposeRemoteKingsForBossCellReload(
                add ? "boss-rune-update:add" : "boss-rune-update:remove");
        }

        orig(self, add);

        if (!IsNetReadyForSend(net) || !net!.IsHost)
            return;

        try
        {
            var (x, y) = GetEntityPixelPos(self);
            net.SendInterBossRuneUpdateCells(x, y, add);
            var user = self?._level?.game?.user ?? dc.Main.Class.ME?.user;
            if (user != null)
                GameDataSync.SendBossRune(user, net);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Failed to send boss rune updateCells");
        }
    }

    private void Hook_Door_init(Hook_Door.orig_init orig, Door self)
    {
        orig(self);
        if (self == null)
            return;

        _doorStableAnchors[self] = ComputeDoorStableAnchor(self);
        var net = GameMenu.NetRef;
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
        var net = GameMenu.NetRef;
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

    private void Hook_Elevator_onStep(Hook_Elevator.orig_onStep orig, Elevator self)
    {
        orig(self);
        if (_applyingRemoteElevatorEvents)
            return;
        if (!IsNetReadyForSend(GameMenu.NetRef))
            return;
        try
        {
            var now = System.Environment.TickCount64;
            if (_elevatorLastInterSendTickMs.TryGetValue(self, out var last) && now - last < ElevatorInterSendMinIntervalMs)
                return;
            _elevatorLastInterSendTickMs[self] = now;

            var (x, y) = GetElevatorStableAnchor(self);
            GameMenu.NetRef!.SendInterElevator(x, y);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Elevator send failed");
        }
    }

    private const int ElevatorInterSendMinIntervalMs = 100;
    private static void TryApplyElevatorRemoteActivation(Elevator elevator)
    {
        if (elevator == null)
            return;

        elevator.onStep();
    }

    private static (double x, double y) GetElevatorStableAnchor(Elevator e)
    {
        if (e == null)
            return (0, 0);
        try
        {
            return ((e.cx + e.xr) * TileSizePx, (e.cy + e.yr) * TileSizePx);
        }
        catch
        {
            return GetEntityPixelPos(e);
        }
    }

    private void Hook_PressurePlate_trigger(Hook_PressurePlate.orig_trigger orig, PressurePlate self, Entity by)
    {
        orig(self, by);
        TrySendPressurePlateEvent(self);
    }

    private void TrySendPressurePlateEvent(PressurePlate self)
    {
        if (_applyingRemotePressurePlateEvents)
            return;

        var net = GameMenu.NetRef;
        if (!IsNetReadyForSend(net))
            return;

        TrySendInteractEvent(
            self,
            (x, y) => net!.SendInterPressurePlate(
                net.id,
                x,
                y,
                ++_nextPressurePlateSequence,
                GetCurrentInteractionLevelId()),
            "PressurePlate");
    }

    private void Hook_TreasureChest_open(Hook_TreasureChest.orig_open orig, TreasureChest self, Hero by)
    {
        orig(self, by);
        if (!_applyingRemoteChestEvents)
            TrySendTreasureChestEvent(self);
    }

    private void TrySendTreasureChestEvent(TreasureChest self)
    {
        TrySendInteractEvent(self, (x, y) => GameMenu.NetRef!.SendInterTreasureChest(x, y), "TreasureChest");
    }

    private void Hook_VineLadder_activate(Hook_VineLadder.orig_activate orig, VineLadder self)
    {
        orig(self);
        TrySendVineLadderEvent(self);
    }

    private void TrySendVineLadderEvent(VineLadder self)
    {
        if (_applyingRemoteVineLadderEvents)
            return;
        TrySendInteractEvent(self, (x, y) => GameMenu.NetRef!.SendInterVineLadder(x, y), "VineLadder");
    }

    private void Hook_Teleport_open(Hook_Teleport.orig_open orig, Teleport self)
    {
        orig(self);
        TrySendTeleportEvent(self);
    }

    private void Hook_Hero_breakBreakableGround(Hook_Hero.orig_breakBreakableGround orig, Hero self, int x, int y)
    {
        orig(self, x, y);
        if (_applyingRemoteBreakableGroundEvents)
            return;
        var net = GameMenu.NetRef;
        if (!IsNetReadyForSend(net) || ModEntry.me == null || !ReferenceEquals(self, ModEntry.me))
            return;
        try
        {
            net!.SendInterBreakableGround(x, y);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] BreakableGround send failed");
        }
    }

    private void TrySendTeleportEvent(Teleport self)
    {
        if (_applyingRemoteTeleportEvents)
            return;
        TrySendInteractEvent(self, (x, y) => GameMenu.NetRef!.SendInterTeleport(x, y), "Teleport");
    }

    private void Hook_Portal_show(Hook_Portal.orig_show orig, Portal self)
    {
        orig(self);
        TrySendPortalEvent(self, "show");
    }

    private void Hook_Portal_close(Hook_Portal.orig_close orig, Portal self)
    {
        orig(self);
        TrySendPortalEvent(self, "close");
    }

    private void TrySendPortalEvent(Portal self, string action)
    {
        if (_applyingRemotePortalEvents)
            return;
        if (!IsNetReadyForSend(GameMenu.NetRef))
            return;
        try
        {
            var (x, y) = GetEntityPixelPos(self);
            GameMenu.NetRef!.SendInterPortal(x, y, action);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Portal send failed action={Action}", action);
        }
    }

    private static (double x, double y) GetEntityPixelPos(Entity e)
    {
        if (e?.spr == null)
            return (0, 0);
        try
        {
            return (e.spr.x, e.spr.y);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool IsFinitePosition(double x, double y)
    {
        return double.IsFinite(x) && double.IsFinite(y);
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

    private static string GetCurrentInteractionLevelId()
    {
        try
        {
            return ModEntry.me?._level?.map?.id?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsInteractionEventForCurrentLevel(string? eventLevelId)
    {
        if (string.IsNullOrWhiteSpace(eventLevelId))
            return true; // Backwards compatibility with older interaction packets.

        var currentLevelId = GetCurrentInteractionLevelId();
        return !string.IsNullOrWhiteSpace(currentLevelId) &&
               string.Equals(currentLevelId, eventLevelId.Trim(), StringComparison.Ordinal);
    }

    private static T SafeRead<T>(Func<T> fn, T fallback)
    {
        try { return fn(); }
        catch { return fallback; }
    }

    private static void ApplyAndRelease<T>(List<T> events, Action<List<T>> apply)
    {
        try
        {
            apply(events);
        }
        finally
        {
            NetNode.ReleaseConsumedList(events);
        }
    }

    private static bool IsNetReadyForSend(NetNode? net) =>
        net != null && net.IsAlive && net.id > 0;

    private bool TrySendInteractEvent(Entity entity, Action<double, double> send, string logContext)
    {
        if (!IsNetReadyForSend(GameMenu.NetRef))
            return false;
        try
        {
            var (x, y) = GetEntityPixelPos(entity);
            send(x, y);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] {LogContext} send failed", logContext);
            return false;
        }
    }

    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive)
            return;

        GameDataSync.PumpBossRuneHudRefresh();

        if (net.TryConsumeInterDoorEvents(out var doorEvents))
            ApplyAndRelease(doorEvents, ApplyRemoteDoorEvents);

        if (net.TryConsumeInterElevatorEvents(out var elevEvents))
            ApplyAndRelease(elevEvents, ApplyRemoteElevatorEvents);

        // Legacy elevator mode deliberately ignores authoritative position snapshots. Consume and
        // release any stale packet from a mismatched peer, but never move elevator/entity coordinates.
        if (net.TryConsumeInterElevatorStateEvents(out var elevatorStateEvents))
            NetNode.ReleaseConsumedList(elevatorStateEvents);

        if (net.TryConsumeInterPressurePlateEvents(out var plateEvents))
            ApplyAndRelease(plateEvents, ApplyRemotePressurePlateEvents);

        if (net.TryConsumeInterTreasureChestEvents(out var chestEvents))
            ApplyAndRelease(chestEvents, ApplyRemoteTreasureChestEvents);

        if (net.TryConsumeInterVineLadderEvents(out var vineLadderEvents))
            ApplyAndRelease(vineLadderEvents, ApplyRemoteVineLadderEvents);

        if (net.TryConsumeInterTeleportEvents(out var teleportEvents))
            ApplyAndRelease(teleportEvents, ApplyRemoteTeleportEvents);

        if (net.TryConsumeInterPortalEvents(out var portalEvents))
            ApplyAndRelease(portalEvents, ApplyRemotePortalEvents);

        EnsureInteractionRuntimeLevel(ModEntry.me?._level);
        if (net.IsHost)
        {
            CheckAndCloseDoorsWhenNoOneNearby();
            BroadcastAuthoritativeDoorStates(net);
        }
        if (net.TryConsumeInterBreakableGroundEvents(out var breakableGroundEvents))
            ApplyAndRelease(breakableGroundEvents, ApplyRemoteBreakableGroundEvents);

        if (net.TryConsumeBossRuneUpdateCells(out var updateCellsEvents))
            ApplyAndRelease(updateCellsEvents, ApplyRemoteBossRuneUpdateCells);
    }

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

    private void EnsureInteractionRuntimeLevel(Level? level)
    {
        if (ReferenceEquals(_interactionRuntimeLevel, level))
            return;

        _interactionRuntimeLevel = level;

        // Door.init runs before the first hero update in a new biome. Preserve entries that already
        // belong to the new level, otherwise we would erase the original autoClose value immediately
        // after the hook captured it. Only stale doors from the disposed level are removed.
        _scratchDoorsToRemove.Clear();
        foreach (var door in _openedDoors)
        {
            if (door == null || level == null || !SafeRead(() => ReferenceEquals(door._level, level), false))
                _scratchDoorsToRemove.Add(door!);
        }
        for (var i = 0; i < _scratchDoorsToRemove.Count; i++)
            _openedDoors.Remove(_scratchDoorsToRemove[i]);

        var staleAutoCloseDoors = new List<Door>();
        foreach (var door in _doorHadAutoClose.Keys)
        {
            if (door == null || level == null || !SafeRead(() => ReferenceEquals(door._level, level), false))
                staleAutoCloseDoors.Add(door!);
        }
        for (var i = 0; i < staleAutoCloseDoors.Count; i++)
            _doorHadAutoClose.Remove(staleAutoCloseDoors[i]);

        var staleDoorAnchors = new List<Door>();
        foreach (var door in _doorStableAnchors.Keys)
        {
            if (door == null || level == null || !SafeRead(() => ReferenceEquals(door._level, level), false))
                staleDoorAnchors.Add(door!);
        }
        for (var i = 0; i < staleDoorAnchors.Count; i++)
            _doorStableAnchors.Remove(staleDoorAnchors[i]);

        _lastAuthoritativeDoorState.Clear();
        _lastDoorStateSentTickMs.Clear();
        _elevatorLastInterSendTickMs.Clear();
        _pressurePlateLastAppliedSequence.Clear();
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

    private void ApplyRemoteBossRuneUpdateCells(List<InterBossRuneUpdateCellsEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        foreach (var ev in events)
        {
            var altar = FindSwitchBossRuneByPos(level, ev.X, ev.Y);
            if (altar == null)
            {
                _log.Warning("[InteractionSync] No SwitchBossRune found at x={X} y={Y}", ev.X, ev.Y);
                continue;
            }
            try
            {
                altar.updateCells(ev.Add);
                GameDataSync.RequestBossRuneHudRefreshFromRemoteState();
                GameDataSync.PumpBossRuneHudRefresh();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "[InteractionSync] updateCells(add={Add}) failed", ev.Add);
            }
        }
    }

    private static SwitchBossRune? FindSwitchBossRuneByPos(Level level, double x, double y)
    {
        var found = FindInteractByPos<SwitchBossRune>(level, x, y, SwitchBossRunePosTolerance);
        if (found != null)
            return found;

        RebuildInteractionCache(level);
        found = FindInteractByPos<SwitchBossRune>(level, x, y, SwitchBossRunePosTolerance * 2.0);
        if (found != null)
            return found;

        var candidates = GetInteractionCandidates<SwitchBossRune>(level);
        if (candidates != null && candidates.Count == 1)
            return candidates[0];

        return FindNearestByPos<SwitchBossRune>(level, x, y, 256.0 * 256.0);
    }

    private void ApplyRemoteDoorEvents(List<InterDoorEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        _applyingRemoteDoorEvents = true;
        try
        {
            var localId = GameMenu.NetRef?.id ?? 0;
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

    private void ApplyRemoteElevatorEvents(List<InterElevatorEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level == null || events == null || events.Count == 0)
            return;

        _applyingRemoteElevatorEvents = true;
        try
        {
            _scratchAppliedElevators.Clear();
            foreach (var ev in events)
            {
                var elevator = FindElevatorByPos(level, ev.X, ev.Y);
                if (elevator == null)
                {
                    _log.Warning("[InteractionSync] No Elevator found at x={X} y={Y}", ev.X, ev.Y);
                    continue;
                }

                if (!_scratchAppliedElevators.Add(elevator))
                    continue;

                try
                {
                    TryApplyElevatorRemoteActivation(elevator);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply elevator event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteElevatorEvents = false;
        }
    }

    private void ApplyRemoteVineLadderEvents(List<InterVineLadderEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        _applyingRemoteVineLadderEvents = true;
        try
        {
            foreach (var ev in events)
            {
                var vineLadder = FindVineLadderByPos(level, ev.X, ev.Y);
                if (vineLadder == null)
                    continue;

                try
                {
                    vineLadder.activate();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply vine ladder event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteVineLadderEvents = false;
        }
    }

    private void ApplyRemotePortalEvents(List<InterPortalEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level == null || events == null || events.Count == 0)
            return;

        _applyingRemotePortalEvents = true;
        try
        {
            foreach (var ev in events)
            {
                var portal = FindPortalByPos(level, ev.X, ev.Y);
                if (portal == null)
                    continue;

                try
                {
                    if (ev.Action == "show")
                        portal.show();
                    else if (ev.Action == "close")
                        portal.close();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply portal event failed x={X} y={Y} action={Action}", ev.X, ev.Y, ev.Action);
                }
            }
        }
        finally
        {
            _applyingRemotePortalEvents = false;
        }
    }

    private void ApplyRemoteTeleportEvents(List<InterTeleportEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        _applyingRemoteTeleportEvents = true;
        try
        {
            foreach (var ev in events)
            {
                var teleport = FindTeleportByPos(level, ev.X, ev.Y);
                if (teleport == null)
                {
                    continue;
                }

                try
                {
                    teleport.open();
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply teleport event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteTeleportEvents = false;
        }
    }

    private void ApplyRemoteBreakableGroundEvents(List<InterBreakableGroundEvent> events)
    {
        var hero = ModEntry.me;
        if (hero == null || events == null || events.Count == 0)
            return;

        _applyingRemoteBreakableGroundEvents = true;
        try
        {
            _scratchAppliedBreakableGround.Clear();
            foreach (var ev in events)
            {
                var alreadyNearby = false;
                for (var i = 0; i < _scratchAppliedBreakableGround.Count; i++)
                {
                    var (ax, ay) = _scratchAppliedBreakableGround[i];
                    if (System.Math.Abs(ax - ev.X) <= BreakableGroundPosTolerance && System.Math.Abs(ay - ev.Y) <= BreakableGroundPosTolerance)
                    {
                        alreadyNearby = true;
                        break;
                    }
                }
                if (alreadyNearby)
                    continue;

                var cx = (int)System.Math.Round(ev.X);
                var cy = (int)System.Math.Round(ev.Y);
                _scratchAppliedBreakableGround.Add((ev.X, ev.Y));

                try
                {
                    hero.breakBreakableGround(cx, cy);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply breakable ground failed x={X} y={Y}", cx, cy);
                }
            }
        }
        finally
        {
            _applyingRemoteBreakableGroundEvents = false;
        }
    }

    private void ApplyRemotePressurePlateEvents(List<InterPressurePlateEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        var localHero = ModEntry.me as Entity;
        var localId = GameMenu.NetRef?.id ?? 0;
        if (localHero == null)
            return;

        _applyingRemotePressurePlateEvents = true;
        try
        {
            foreach (var ev in events)
            {
                if (ev.UserId > 0 && ev.UserId == localId)
                    continue;
                if (!IsInteractionEventForCurrentLevel(ev.LevelId))
                    continue;

                var plate = FindPressurePlateByPos(level, ev.X, ev.Y);
                if (plate == null)
                    continue;

                if (ev.Sequence > 0 && ev.UserId > 0)
                {
                    var key = (plate, ev.UserId);
                    if (_pressurePlateLastAppliedSequence.TryGetValue(key, out var lastSequence) &&
                        ev.Sequence <= lastSequence)
                    {
                        continue;
                    }
                    _pressurePlateLastAppliedSequence[key] = ev.Sequence;
                }

                try
                {
                    plate.trigger(localHero);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply pressure plate event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemotePressurePlateEvents = false;
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

    private static Door? FindNearestDoor(Level level, double x, double y) =>
        FindNearestByPos<Door>(level, x, y, DoorPosTolerance * DoorPosTolerance * 4);

    private static Elevator? FindElevatorByPos(Level level, double x, double y)
    {
        var byAnchor = FindElevatorByStableAnchor(level, x, y);
        if (byAnchor != null)
            return byAnchor;

        var byPos = FindInteractByPos<Elevator>(level, x, y, ElevatorPosTolerance);
        if (byPos != null)
            return byPos;

        var byTrack = FindElevatorByTrackBounds(level, x, y);
        if (byTrack != null)
            return byTrack;

        var nearest = FindNearestByPos<Elevator>(level, x, y, ElevatorPosTolerance * ElevatorPosTolerance * 4);
        if (nearest != null)
            return nearest;
        return FindElevatorInTriggers(level, x, y);
    }

    private static Elevator? FindElevatorByStableAnchor(Level level, double anchorX, double anchorY)
    {
        var elevators = GetInteractionCandidates<Elevator>(level);
        if (elevators == null || elevators.Count == 0)
            return null;

        for (var i = 0; i < elevators.Count; i++)
        {
            var e = elevators[i];
            if (e == null)
                continue;
            try
            {
                var (ax, ay) = GetElevatorStableAnchor(e);
                if (System.Math.Abs(ax - anchorX) < ElevatorPosTolerance &&
                    System.Math.Abs(ay - anchorY) < ElevatorPosTolerance)
                    return e;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static Elevator? FindElevatorByTrackBounds(Level level, double x, double y)
    {
        var elevators = GetInteractionCandidates<Elevator>(level);
        if (elevators == null || elevators.Count == 0)
            return null;

        Elevator? nearest = null;
        double nearestSq = double.MaxValue;
        for (var i = 0; i < elevators.Count; i++)
        {
            var elevator = elevators[i];
            if (elevator == null)
                continue;

            try
            {
                var leftPx = elevator.xLeft * TileSizePx - ElevatorPosTolerance;
                var rightPx = (elevator.xRight + 1) * TileSizePx + ElevatorPosTolerance;
                var topPx = elevator.yTop * TileSizePx - ElevatorPosTolerance;
                var bottomPx = (elevator.yBottom + 1) * TileSizePx + ElevatorPosTolerance;

                if (x < leftPx || x > rightPx || y < topPx || y > bottomPx)
                    continue;

                var anchorX = elevator.spr?.x ?? ((elevator.cx + elevator.xr) * TileSizePx);
                var anchorY = elevator.spr?.y ?? ((elevator.cy + elevator.yr) * TileSizePx);
                var dx = anchorX - x;
                var dy = anchorY - y;
                var dSq = dx * dx + dy * dy;
                if (dSq < nearestSq)
                {
                    nearestSq = dSq;
                    nearest = elevator;
                }
            }
            catch
            {
                // ignore bad elevator state
            }
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

    private static Elevator? FindElevatorInTriggers(Level level, double x, double y) =>
        FindNearestTriggerByPos<Elevator>(level, x, y, ElevatorPosTolerance * ElevatorPosTolerance * 4);

    private static VineLadder? FindVineLadderByPos(Level level, double x, double y)
    {
        return FindInteractByPos<VineLadder>(level, x, y, PlatePosTolerance);
    }

    private Teleport? FindTeleportByPos(Level level, double x, double y)
    {
        var byPos = FindInteractByPos<Teleport>(level, x, y, TeleportPosTolerance);
        if (byPos != null)
            return byPos;
        var nearest = FindNearestByPos<Teleport>(level, x, y, 200.0 * 200.0);
        if (nearest != null)
            return nearest;
        return FindTeleportInTriggers(level, x, y);
    }

    private static Portal? FindPortalByPos(Level level, double x, double y)
    {
        var byPos = FindInteractByPos<Portal>(level, x, y, PortalPosTolerance);
        if (byPos != null)
            return byPos;
        var nearest = FindNearestByPos<Portal>(level, x, y, PortalPosTolerance * PortalPosTolerance * 4);
        if (nearest != null)
            return nearest;
        return FindPortalInTriggers(level, x, y);
    }

    private static Portal? FindPortalInTriggers(Level level, double x, double y) =>
        FindNearestTriggerByPos<Portal>(level, x, y, PortalPosTolerance * PortalPosTolerance * 4);

    private static Teleport? FindTeleportInTriggers(Level level, double x, double y) =>
        FindNearestTriggerByPos<Teleport>(level, x, y, TeleportPosTolerance * TeleportPosTolerance * 4);

    private static PressurePlate? FindPressurePlateByPos(Level level, double x, double y)
    {
        return FindInteractByPos<PressurePlate>(level, x, y, PlatePosTolerance);
    }

    private void ApplyRemoteTreasureChestEvents(List<InterTreasureChestEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        var localHero = ModEntry.me;
        if (localHero == null)
            return;

        _applyingRemoteChestEvents = true;
        try
        {
            foreach (var ev in events)
            {
                var chest = FindTreasureChestByPos(level, ev.X, ev.Y);
                if (chest == null)
                    continue;

                try
                {
                    chest.open(localHero);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply treasure chest event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemoteChestEvents = false;
        }
    }

    private static TreasureChest? FindTreasureChestByPos(Level level, double x, double y)
    {
        var byPos = FindInteractByPos<TreasureChest>(level, x, y, ChestPosTolerance);
        if (byPos != null)
            return byPos;
        return FindNearestTreasureChest(level, x, y);
    }

    private static TreasureChest? FindNearestTreasureChest(Level level, double x, double y) =>
        FindNearestByPos<TreasureChest>(level, x, y, ChestPosTolerance * ChestPosTolerance * 4);

    private static T? FindInteractByPos<T>(Level level, double x, double y, double tolerance = PosTolerance) where T : Entity
    {
        var candidates = GetInteractionCandidates<T>(level);
        if (candidates == null || candidates.Count == 0)
            return null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var e = candidates[i];
            if (e == null)
                continue;
            try
            {
                if (e.spr != null &&
                    System.Math.Abs(e.spr.x - x) < tolerance &&
                    System.Math.Abs(e.spr.y - y) < tolerance)
                {
                    return e;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
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
        return null;
    }
}

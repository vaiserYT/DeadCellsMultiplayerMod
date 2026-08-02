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

public partial class InteractionSync :
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
        public readonly List<dc.en.inter.button.Button> Buttons = new();
        public readonly List<TreasureChest> TreasureChests = new();
        public readonly List<SwitchBossRune> SwitchBossRunes = new();
        public readonly List<Elevator> TriggerElevators = new();
        public readonly List<Teleport> TriggerTeleports = new();
        public readonly List<Portal> TriggerPortals = new();
        public readonly List<dc.en.inter.button.Button> TriggerButtons = new();

        public void Clear()
        {
            Doors.Clear();
            Elevators.Clear();
            VineLadders.Clear();
            Teleports.Clear();
            Portals.Clear();
            PressurePlates.Clear();
            Buttons.Clear();
            TreasureChests.Clear();
            SwitchBossRunes.Clear();
            TriggerElevators.Clear();
            TriggerTeleports.Clear();
            TriggerPortals.Clear();
            TriggerButtons.Clear();
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

    /// <summary>
    /// Generated GameProxy builds do not expose Hook_Button/Hook_ATSwitch consistently. Track each
    /// button's activation edge from the hero update instead, which keeps this compatible with the
    /// proxy shipped by the current DCCM/game build.
    /// </summary>
    private readonly Dictionary<dc.en.inter.button.Button, bool> _buttonActivationState = new();

    private bool _applyingRemoteVineLadderEvents;

    private bool _applyingRemoteTeleportEvents;

    private bool _applyingRemoteBreakableGroundEvents;

    private bool _applyingRemotePortalEvents;

    private bool _applyingRemoteElevatorEvents;

    private bool _applyingRemoteElevatorStateEvents;

    /// <summary>Throttle elevator activation pulses — onStep can fire every frame while riding.</summary>
    private readonly Dictionary<Elevator, long> _elevatorLastInterSendTickMs = new();

    private readonly Dictionary<(Elevator Elevator, int UserId), long> _elevatorLastAppliedSequence = new();

    /// <summary>
    /// Keyed on Entity rather than PressurePlate: the same INTERPLATE pulse now also carries button
    /// activations, and a button is a different Interactive subclass.
    /// </summary>
    private readonly Dictionary<(Entity Activator, int UserId), long> _pressurePlateLastAppliedSequence = new();

    private readonly Dictionary<Door, string> _lastAuthoritativeDoorState = new();

    private readonly Dictionary<Door, long> _lastDoorStateSentTickMs = new();

    private Level? _interactionRuntimeLevel;

    private long _nextElevatorSequence;

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
        // Button/ATSwitch generated hook classes are not present in every GameProxy build. Their
        // activation edges are detected in PollLocalButtonActivations from IOnHeroUpdate instead.
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

        EnsureInteractionRuntimeLevel(ModEntry.me?._level);
        PollLocalButtonActivations(ModEntry.me?._level);

        if (net.TryConsumeInterDoorEvents(out var doorEvents))
            ApplyAndRelease(doorEvents, ApplyRemoteDoorEvents);

        if (net.TryConsumeInterElevatorEvents(out var elevEvents))
            ApplyAndRelease(elevEvents, ApplyRemoteElevatorEvents);

        if (net.TryConsumeInterElevatorStateEvents(out var elevatorStateEvents))
            ApplyAndRelease(elevatorStateEvents, ApplyRemoteElevatorStateEvents);

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
        _elevatorLastAppliedSequence.Clear();
        _pressurePlateLastAppliedSequence.Clear();
        _buttonActivationState.Clear();
    }

}

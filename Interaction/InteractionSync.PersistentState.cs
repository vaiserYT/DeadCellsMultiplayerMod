using dc;
using dc.en;
using dc.en.inter;
using dc.pr;

namespace DeadCellsMultiplayerMod.Interaction;

/// <summary>
/// Host-authoritative convergence for latching world mechanisms.
/// </summary>
/// <remarks>
/// Buttons, vine ladders and teleports were synchronized as ONE-SHOT EVENTS: the activating peer
/// sent a single packet and the other side applied it if — and only if — that packet happened to
/// arrive while the receiver was already in the same level with its entities built. Anything that
/// broke that single delivery lost the state permanently:
///   * the receiver was still loading, so <c>IsInteractionEventForCurrentLevel</c> rejected it;
///   * the entity lookup ran before the level cache contained the fixture;
///   * the player joined afterwards and was never told at all.
/// The visible result is the reported regression: one player opens a switch door and it stays shut
/// for the other, forever, with no way to retry.
///
/// Doors already solved this with <see cref="BroadcastAuthoritativeDoorStates"/> — a host-owned
/// periodic re-publish of the RESULTING state rather than the triggering event. This file extends
/// that same proven mechanism to the mechanisms that drive those doors, instead of adding a second
/// interaction protocol: it reuses the existing INTERPLATE / INTERTELEPORT / INTERVINELADDER
/// channels and the existing apply paths, and simply keeps re-asserting the latched state until
/// every peer agrees. Convergence therefore also covers late join for free — a client that loads
/// two minutes into a level receives the current state on the next heartbeat.
///
/// Only LATCHING mechanisms are re-asserted. Momentary ones (a pressure plate held down by weight)
/// are deliberately excluded: re-triggering those every heartbeat would re-run their effect, and
/// their durable consequence is the door, which the door broadcast already owns.
/// </remarks>
public partial class InteractionSync
{
    // Identity for every collection here is the live game object, so reference equality is used
    // explicitly rather than relying on whatever Equals/GetHashCode the generated Haxe proxy
    // happens to expose. This matches how the mob registry keys its own per-entity tables.

    /// <summary>Latched mechanisms the host has itself observed activating, by native hook.</summary>
    private readonly HashSet<VineLadder> _hostActivatedVineLadders =
        new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<Teleport> _hostOpenedTeleports =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Idempotency window for latched mechanisms.
    /// </summary>
    /// <remarks>
    /// VineLadder.activate() and Teleport.open() are not guarded natively, so a heartbeat would
    /// re-run the activation and its FX every beat. This is a TIME window rather than a
    /// once-per-level claim on purpose: nothing on the wire distinguishes a heartbeat repeat from a
    /// genuine second activation, and a permanent claim would silently swallow the latter. The
    /// window only has to exceed the heartbeat period to suppress the repeats.
    /// </remarks>
    private readonly Dictionary<Entity, long> _lastPersistentInteractionApplyTickMs =
        new(ReferenceEqualityComparer.Instance);

    private const int PersistentInteractionRetriggerGuardMs = DoorStateHeartbeatMs + 1000;

    private long _lastPersistentInteractionHeartbeatMs;

    /// <summary>Guards momentary pressure plates against heartbeat-induced repeat triggering.</summary>
    private readonly Dictionary<PressurePlate, long> _lastRemotePlateTriggerTickMs =
        new(ReferenceEqualityComparer.Instance);

    private const int RemotePlateRetriggerGuardMs = 1000;

    private void ClearPersistentInteractionStateForLevel()
    {
        _hostActivatedVineLadders.Clear();
        _hostOpenedTeleports.Clear();
        _lastPersistentInteractionApplyTickMs.Clear();
        _lastRemotePlateTriggerTickMs.Clear();
        _lastPersistentInteractionHeartbeatMs = 0;
    }

    private void RememberHostLatchedVineLadder(VineLadder? vineLadder)
    {
        if (vineLadder == null)
            return;
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive || !net.IsHost)
            return;
        _hostActivatedVineLadders.Add(vineLadder);
    }

    private void RememberHostLatchedTeleport(Teleport? teleport)
    {
        if (teleport == null)
            return;
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive || !net.IsHost)
            return;
        _hostOpenedTeleports.Add(teleport);
    }

    /// <summary>
    /// True when a latched mechanism may be applied now; false while inside the repeat-suppression
    /// window that filters out the host's state heartbeat.
    /// </summary>
    private bool TryClaimPersistentInteractionApply(Entity? entity)
    {
        if (entity == null)
            return false;

        var now = System.Environment.TickCount64;
        if (_lastPersistentInteractionApplyTickMs.TryGetValue(entity, out var last) &&
            now - last < PersistentInteractionRetriggerGuardMs)
        {
            return false;
        }

        _lastPersistentInteractionApplyTickMs[entity] = now;
        return true;
    }

    /// <summary>
    /// Host: re-assert every latched mechanism's current state on the existing interaction channels.
    /// Runs on the same cadence as the door state broadcast.
    /// </summary>
    private void BroadcastAuthoritativePersistentInteractions(NetNode net)
    {
        if (!net.IsHost || !IsNetReadyForSend(net))
            return;

        var level = ModEntry.me?._level;
        if (level == null)
            return;

        var now = System.Environment.TickCount64;
        if (_lastPersistentInteractionHeartbeatMs != 0 &&
            now - _lastPersistentInteractionHeartbeatMs < DoorStateHeartbeatMs)
        {
            return;
        }
        _lastPersistentInteractionHeartbeatMs = now;

        var levelId = GetCurrentInteractionLevelId();
        if (string.IsNullOrWhiteSpace(levelId))
            return;

        var cache = GetInteractionCache(level);
        BroadcastLatchedButtons(net, level, cache.Buttons, levelId);
        BroadcastLatchedButtons(net, level, cache.TriggerButtons, levelId);
        BroadcastLatchedVineLadders(net, level, levelId);
        BroadcastLatchedTeleports(net, level, levelId);
    }

    /// <summary>
    /// True only when the entity exposes a usable sprite anchor.
    /// </summary>
    /// <remarks>
    /// Every interaction packet identifies its fixture purely by position, and
    /// <see cref="GetEntityPixelPos"/> silently returns (0,0) when the sprite is missing. A one-shot
    /// event built that way is harmless noise, but a HEARTBEAT built that way is a lie repeated
    /// forever: the receiver resolves (0,0) with an 8px tolerance and can bind it to an unrelated
    /// fixture near the level origin. Never heartbeat a fixture we cannot address.
    /// </remarks>
    private static bool HasAddressableAnchor(Entity entity)
    {
        if (entity == null)
            return false;

        return SafeRead(
            () =>
            {
                if (entity.spr == null)
                    return false;
                var x = entity.spr.x;
                var y = entity.spr.y;
                return IsFinitePosition(x, y) && (x != 0.0 || y != 0.0);
            },
            false);
    }

    private void BroadcastLatchedButtons(
        NetNode net,
        Level level,
        IReadOnlyList<dc.en.inter.button.Button> buttons,
        string levelId)
    {
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            if (button == null)
                continue;
            if (!SafeRead(() => ReferenceEquals(button._level, level), false))
                continue;
            if (!SafeRead(() => button.isActivated(), false))
                continue;
            if (!HasAddressableAnchor(button))
                continue;

            // The receiver skips a button that is already activated, so this is inert once the
            // peers agree and only does work while they genuinely disagree.
            TrySendInteractEvent(
                button,
                (x, y) => net.SendInterPressurePlate(net.id, x, y, ++_nextPressurePlateSequence, levelId),
                "ButtonStateHeartbeat");
        }
    }

    private void BroadcastLatchedVineLadders(NetNode net, Level level, string levelId)
    {
        if (_hostActivatedVineLadders.Count == 0)
            return;

        foreach (var vineLadder in _hostActivatedVineLadders)
        {
            if (vineLadder == null)
                continue;
            if (!SafeRead(() => ReferenceEquals(vineLadder._level, level), false))
                continue;
            if (!HasAddressableAnchor(vineLadder))
                continue;

            TrySendInteractEvent(
                vineLadder,
                (x, y) => net.SendInterVineLadder(x, y, levelId),
                "VineLadderStateHeartbeat");
        }
    }

    private void BroadcastLatchedTeleports(NetNode net, Level level, string levelId)
    {
        if (_hostOpenedTeleports.Count == 0)
            return;

        foreach (var teleport in _hostOpenedTeleports)
        {
            if (teleport == null)
                continue;
            if (!SafeRead(() => ReferenceEquals(teleport._level, level), false))
                continue;
            if (!HasAddressableAnchor(teleport))
                continue;

            TrySendInteractEvent(
                teleport,
                (x, y) => net.SendInterTeleport(x, y, levelId),
                "TeleportStateHeartbeat");
        }
    }
}

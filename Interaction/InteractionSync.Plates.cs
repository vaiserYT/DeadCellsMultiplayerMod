using dc;
using dc.en;
using dc.en.inter;
using dc.en.inter.button;
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
    private void Hook_PressurePlate_trigger(Hook_PressurePlate.orig_trigger orig, PressurePlate self, Entity by)
    {
        orig(self, by);
        TrySendPressurePlateEvent(self);
    }

    /// <summary>
    /// Detects Button/ATSwitch activation without relying on generated Hook_Button or Hook_ATSwitch
    /// classes, which are absent from some GameProxy versions. Only rising activation edges are
    /// transmitted, so initial already-open fixtures and steady one-shot buttons do not spam events.
    /// </summary>
    private void PollLocalButtonActivations(Level? level)
    {
        if (level == null || _applyingRemotePressurePlateEvents)
            return;

        var cache = GetInteractionCache(level);
        PollLocalButtonCandidates(cache.Buttons);
        PollLocalButtonCandidates(cache.TriggerButtons);
    }

    private void PollLocalButtonCandidates(IReadOnlyList<Button> buttons)
    {
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            if (button == null)
                continue;

            var isActivated = SafeRead(() => button.isActivated(), false);
            if (!_buttonActivationState.TryGetValue(button, out var wasActivated))
            {
                // Establish a baseline. A button that was already active when the level became ready
                // must not be replayed as a fresh local press.
                _buttonActivationState[button] = isActivated;
                continue;
            }

            _buttonActivationState[button] = isActivated;
            if (!wasActivated && isActivated)
                TrySendActivatorEvent(button, button.GetType().Name);
        }
    }

    private void TrySendPressurePlateEvent(PressurePlate self) => TrySendActivatorEvent(self, "PressurePlate");

    private void TrySendActivatorEvent(Entity self, string logContext)
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
            logContext);
    }

    private void ApplyRemotePressurePlateEvents(List<InterPressurePlateEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level?.entities == null || events == null || events.Count == 0)
            return;

        var localHeroTyped = ModEntry.me;
        var localHero = localHeroTyped as Entity;
        var localId = GameMenu.NetRef?.id ?? 0;
        if (localHero == null || localHeroTyped == null)
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

                // Resolve the concrete activator kind at this position. Plate first: it is the more
                // specific match and the pre-existing behaviour.
                var plate = FindPressurePlateByPos(level, ev.X, ev.Y);
                var button = plate == null ? FindButtonByPos(level, ev.X, ev.Y) : null;
                Entity? activator = plate ?? (Entity?)button;
                if (activator == null)
                    continue;

                // A button that is already activated here has nothing to do. Buttons are one-shot, so
                // this both keeps the replay idempotent and stops a late/duplicated packet from
                // re-running the activation FX on an already-open door.
                if (button != null && SafeRead(() => button.isActivated(), false))
                    continue;

                if (ev.Sequence > 0 && ev.UserId > 0)
                {
                    var key = (activator, ev.UserId);
                    if (_pressurePlateLastAppliedSequence.TryGetValue(key, out var lastSequence) &&
                        ev.Sequence <= lastSequence)
                    {
                        continue;
                    }
                    _pressurePlateLastAppliedSequence[key] = ev.Sequence;
                }

                try
                {
                    if (plate != null)
                    {
                        // Pressure plates are momentary, so unlike buttons they are NOT idempotent:
                        // re-running trigger() re-runs its effect. The host re-asserts latched
                        // button state on a heartbeat, and a button that happens to sit within
                        // plate tolerance would otherwise resolve to this plate and pulse it every
                        // beat. A real plate cannot meaningfully re-fire this fast, so suppressing
                        // repeats inside a short window costs nothing and removes the hazard.
                        var nowTicks = System.Environment.TickCount64;
                        if (_lastRemotePlateTriggerTickMs.TryGetValue(plate, out var lastTrigger) &&
                            nowTicks - lastTrigger < RemotePlateRetriggerGuardMs)
                        {
                            continue;
                        }

                        _lastRemotePlateTriggerTickMs[plate] = nowTicks;
                        plate.trigger(localHero);
                    }
                    else
                    {
                        // onActivationSuccess is the post-validation entry point, so the replay is not
                        // rejected for the local hero standing somewhere else, while vanilla still
                        // runs the button's own FX/sound and whatever its triggerId is linked to.
                        button!.onActivationSuccess(localHeroTyped);
                        // Record the remotely applied state immediately so the next hero-update poll
                        // does not mistake it for a new local activation and echo it back.
                        _buttonActivationState[button] = SafeRead(() => button.isActivated(), true);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "[InteractionSync] Apply activator event failed x={X} y={Y}", ev.X, ev.Y);
                }
            }
        }
        finally
        {
            _applyingRemotePressurePlateEvents = false;
        }
    }

}

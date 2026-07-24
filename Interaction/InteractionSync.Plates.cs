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

}

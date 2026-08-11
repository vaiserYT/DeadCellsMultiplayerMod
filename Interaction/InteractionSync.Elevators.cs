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
    private const int ElevatorInterSendMinIntervalMs = 100;

    private void Hook_Elevator_onStep(Hook_Elevator.orig_onStep orig, Elevator self)
    {
        orig(self);
        if (_applyingRemoteElevatorEvents || _applyingRemoteElevatorStateEvents)
            return;
        if (!IsNetReadyForSend(LobbySession.NetRef))
            return;
        try
        {
            var now = System.Environment.TickCount64;
            if (_elevatorLastInterSendTickMs.TryGetValue(self, out var last) && now - last < ElevatorInterSendMinIntervalMs)
                return;
            _elevatorLastInterSendTickMs[self] = now;

            var net = LobbySession.NetRef!;
            var (x, y) = GetElevatorStableAnchor(self);
            var sequence = ++_nextElevatorSequence;
            var levelId = GetCurrentInteractionLevelId();
            net.SendInterElevator(net.id, x, y, sequence, levelId);

            // Host publishes platform state so clients stay aligned while the car moves.
            if (net.IsHost)
            {
                var (px, py) = GetEntityPixelPos(self);
                var moving = false;
                try { moving = System.Math.Abs(self.speed) > 0.01; } catch { }
                net.SendInterElevatorState(net.id, x, y, sequence, px, py, moving, levelId);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[InteractionSync] Elevator send failed");
        }
    }

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

    private void ApplyRemoteElevatorEvents(List<InterElevatorEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level == null || events == null || events.Count == 0)
            return;

        var localId = LobbySession.NetRef?.id ?? 0;
        _applyingRemoteElevatorEvents = true;
        try
        {
            _scratchAppliedElevators.Clear();
            foreach (var ev in events)
            {
                if (ev.UserId > 0 && ev.UserId == localId)
                    continue;
                if (!IsInteractionEventForCurrentLevel(ev.LevelId))
                    continue;

                var elevator = FindElevatorByPos(level, ev.X, ev.Y);
                if (elevator == null)
                {
                    _log.Warning("[InteractionSync] No Elevator found at x={X} y={Y}", ev.X, ev.Y);
                    continue;
                }

                if (!_scratchAppliedElevators.Add(elevator))
                    continue;

                if (ev.Sequence > 0 && ev.UserId > 0)
                {
                    var key = (elevator, ev.UserId);
                    if (_elevatorLastAppliedSequence.TryGetValue(key, out var lastSequence) &&
                        ev.Sequence <= lastSequence)
                    {
                        continue;
                    }
                    _elevatorLastAppliedSequence[key] = ev.Sequence;
                }

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

    private void ApplyRemoteElevatorStateEvents(List<InterElevatorStateEvent> events)
    {
        var level = ModEntry.me?._level;
        if (level == null || events == null || events.Count == 0)
            return;

        var localId = LobbySession.NetRef?.id ?? 0;
        _applyingRemoteElevatorStateEvents = true;
        try
        {
            foreach (var ev in events)
            {
                if (ev.UserId > 0 && ev.UserId == localId)
                    continue;
                if (!IsInteractionEventForCurrentLevel(ev.LevelId))
                    continue;

                var elevator = FindElevatorByPos(level, ev.AnchorX, ev.AnchorY);
                if (elevator == null)
                    continue;

                try
                {
                    TryApplyElevatorRemoteState(elevator, ev);
                }
                catch (Exception ex)
                {
                    _log.Warning(
                        ex,
                        "[InteractionSync] Apply elevator state failed anchor=({X},{Y}) platform=({PX},{PY})",
                        ev.AnchorX,
                        ev.AnchorY,
                        ev.PlatformX,
                        ev.PlatformY);
                }
            }
        }
        finally
        {
            _applyingRemoteElevatorStateEvents = false;
        }
    }

    private static void TryApplyElevatorRemoteState(Elevator elevator, InterElevatorStateEvent ev)
    {
        if (elevator == null)
            return;

        var tileX = ev.PlatformX / TileSizePx;
        var tileY = ev.PlatformY / TileSizePx;
        var cx = (int)System.Math.Floor(tileX);
        var cy = (int)System.Math.Floor(tileY);
        elevator.cx = cx;
        elevator.cy = cy;
        elevator.xr = tileX - cx;
        elevator.yr = tileY - cy;
        if (!ev.Moving)
        {
            try { elevator.speed = 0; } catch { }
        }
    }

    private static Elevator? FindElevatorByStableAnchor(Level level, double anchorX, double anchorY)
    {
        var elevators = GetInteractionCandidates<Elevator>(level);
        if (elevators == null || elevators.Count == 0)
            return null;

        Elevator? nearest = null;
        var nearestSq = ElevatorPosTolerance * ElevatorPosTolerance;
        for (var i = 0; i < elevators.Count; i++)
        {
            var e = elevators[i];
            if (e == null)
                continue;
            try
            {
                var (ax, ay) = GetElevatorStableAnchor(e);
                var dx = ax - anchorX;
                var dy = ay - anchorY;
                if (System.Math.Abs(dx) >= ElevatorPosTolerance ||
                    System.Math.Abs(dy) >= ElevatorPosTolerance)
                    continue;

                var distanceSq = dx * dx + dy * dy;
                if (distanceSq < nearestSq)
                {
                    nearestSq = distanceSq;
                    nearest = e;
                }
            }
            catch
            {
                // Keep searching other elevators.
            }
        }

        return nearest;
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

    private static Elevator? FindElevatorInTriggers(Level level, double x, double y) =>
        FindNearestTriggerByPos<Elevator>(level, x, y, ElevatorPosTolerance * ElevatorPosTolerance * 4);

    private static VineLadder? FindVineLadderByPos(Level level, double x, double y)
    {
        return FindInteractByPos<VineLadder>(level, x, y, PlatePosTolerance);
    }


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
}

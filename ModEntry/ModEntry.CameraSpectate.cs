using System;
using System.Diagnostics;
using dc;
using dc.en;
using dc.hxd;
using dc.tool;
using DeadCellsMultiplayerMod.UI;
using HaxeProxy.Runtime;
using System.Runtime.InteropServices;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private const int KeyOemComma = 188;
        private const int KeyOemPeriod = 190;
        private const int KeyAsciiComma = 44;
        private const int KeyAsciiPeriod = 46;
        private const double CameraRefollowHoldSeconds = 0.75;
        private const string PadUiPrevBindingName = "ui_prev";
        private const string PadUiNextBindingName = "ui_next";

        private int _spectatedRemoteCameraId;
        private int _spectatedCameraOrderIndex;
        private long _cameraRefollowUntilTicks;
        private bool _cameraCycleCommaTextPressed;
        private bool _cameraCyclePeriodTextPressed;
        private bool _cameraSpectateTextInputHookInstalled;
        private bool _automaticDownedSpectateActive;
        private HlAction<Event>? _cameraSpectateWindowEventHandler;

        private void ProcessCameraSpectateInput()
        {
            EnsureCameraSpectateTextInputHook();
            MaintainAutomaticDownedSpectate();
            EnsureSpectatedRemoteCameraStillValid();
            MaintainLocalCameraRefollow();

            if (_spectatedRemoteCameraId > 0)
                TrackPreferredCameraTarget(immediate: false);

            if (_netRole == NetRole.None || me == null)
                return;

            var direction = 0;
            if (ConsumeCameraCyclePress(KeyOemComma, KeyAsciiComma, ref _cameraCycleCommaTextPressed))
                direction = -1;
            else if (ConsumeCameraCyclePress(KeyOemPeriod, KeyAsciiPeriod, ref _cameraCyclePeriodTextPressed))
                direction = 1;
            else if (IsGamepadCameraCyclePressed(PadUiPrevBindingName))
                direction = -1;
            else if (IsGamepadCameraCyclePressed(PadUiNextBindingName))
                direction = 1;

            if (direction == 0)
                return;

            CycleSpectatedCameraTarget(direction);
        }

        private void EnsureCameraSpectateTextInputHook()
        {
            if (_cameraSpectateTextInputHookInstalled)
                return;

            try
            {
                var window = Window.Class.getInstance();
                if (window == null)
                    return;

                _cameraSpectateWindowEventHandler ??= new HlAction<Event>(OnCameraSpectateWindowEvent);
                window.addEventTarget(_cameraSpectateWindowEventHandler);
                _cameraSpectateTextInputHookInstalled = true;
            }
            catch
            {
            }
        }

        private void OnCameraSpectateWindowEvent(Event e)
        {
            if (e == null || e.kind == null)
                return;
            if (e.kind.Index != EventKind.Indexes.ETextInput)
                return;
            if (IsCameraCycleTextInputBlocked())
                return;

            switch (e.charCode)
            {
                case KeyAsciiComma:
                    _cameraCycleCommaTextPressed = true;
                    break;
                case KeyAsciiPeriod:
                    _cameraCyclePeriodTextPressed = true;
                    break;
            }
        }

        private static bool IsCameraCycleTextInputBlocked()
        {
            if (DeadCellsMultiplayerMod.MultiplayerModUI.Connection.ConnectionUI.IsTextPromptOpen())
                return true;
            var activeTextInput = TextInputHandler.GetActiveTextInput();
            return activeTextInput != null && TextInputHandler.IsTextInputActive(activeTextInput);
        }

        private bool ConsumeCameraCyclePress(int primaryKeyCode, int fallbackKeyCode, ref bool textPressed)
        {
            if (textPressed)
            {
                textPressed = false;
                return true;
            }

            return Key.Class.isPressed(primaryKeyCode) || Key.Class.isPressed(fallbackKeyCode);
        }

        private bool IsGamepadCameraCyclePressed(string bindingName)
        {
            if (string.IsNullOrWhiteSpace(bindingName))
                return false;
            if (IsCameraCycleTextInputBlocked())
                return false;

            try
            {
                if (me?.controller is not ControllerAccess access)
                    return false;
                if (access.manualLock)
                    return false;

                var controller = access.parent;
                if (controller == null || controller.isLocked)
                    return false;
                if (controller.exclusiveId != null && controller.exclusiveId != access.id)
                    return false;
                if (!(GetCurrentUnixTimeSeconds() >= controller.suspendTimer))
                    return false;

                var options = dc.Main.Class.ME?.options;
                var gamepadBindings = options?.get_gamepad();
                var bindingObject = TitleScreenReflection.GetMemberValue(gamepadBindings, bindingName, true);
                return IsAnyGamepadBindingPressed(controller, bindingObject);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAnyGamepadBindingPressed(Controller controller, object? bindingObject)
        {
            if (controller == null || bindingObject == null)
                return false;

            if (bindingObject is dc.hl.types.ArrayObj arrayObj)
            {
                try
                {
                    for (var i = 0; i < arrayObj.length; i++)
                    {
                        var raw = arrayObj.getDyn(i);
                        if (raw is not int code || code < 0)
                            continue;
                        if (controller.padIsPressed(code))
                            return true;
                    }
                }
                catch
                {
                }

                return false;
            }

            if (bindingObject is dc.hl.types.ArrayBytes_Int bytes)
            {
                try
                {
                    for (var i = 0; i < bytes.length; i++)
                    {
                        var code = Marshal.ReadInt32(bytes.bytes, i << 2);
                        if (code < 0)
                            continue;
                        if (controller.padIsPressed(code))
                            return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static double GetCurrentUnixTimeSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        }

        internal static Entity? ResolvePreferredCameraTarget(Entity? localFallback)
        {
            var instance = Instance;
            return instance?.ResolvePreferredCameraTargetCore(localFallback) ?? localFallback;
        }

        internal static bool IsSpectatingRemoteCamera()
        {
            return Instance != null && Instance._spectatedRemoteCameraId > 0;
        }

        private Entity? ResolvePreferredCameraTargetCore(Entity? localFallback)
        {
            if (_spectatedRemoteCameraId > 0 &&
                TryResolveRemoteCameraTarget(_spectatedRemoteCameraId, out var remoteTarget))
            {
                return remoteTarget;
            }

            if (_spectatedRemoteCameraId > 0)
            {
                _spectatedRemoteCameraId = 0;
                _spectatedCameraOrderIndex = 0;
            }

            return localFallback ?? ResolveDefaultLocalCameraTarget();
        }

        private Entity? ResolveDefaultLocalCameraTarget()
        {
            return me;
        }

        private void EnsureSpectatedRemoteCameraStillValid()
        {
            if (_spectatedRemoteCameraId <= 0)
                return;

            if (TryResolveRemoteCameraTarget(_spectatedRemoteCameraId, out _))
                return;

            var previousTargetId = _spectatedRemoteCameraId;
            _spectatedRemoteCameraId = 0;
            _spectatedCameraOrderIndex = 0;
            RequestLocalCameraRefollow($"spectate-invalid:{previousTargetId}");
        }

        private void CycleSpectatedCameraTarget(int direction)
        {
            Span<int> orderedTargets = stackalloc int[clients.Length + 1];
            var count = 0;

            // While downed, the local hidden/frozen hero is never a useful camera target. Keep
            // manual cycling available for sessions with multiple living teammates, but do not let
            // the player accidentally cycle back to their own corpse. Revive restores local view.
            if (!_localFakeDead)
                orderedTargets[count++] = 0;

            for (var i = 0; i < clientIds.Length; i++)
            {
                var remoteId = clientIds[i];
                if (remoteId <= 0)
                    continue;
                if (!TryResolveRemoteCameraTarget(remoteId, out _))
                    continue;

                orderedTargets[count++] = remoteId;
            }

            if (count == 0)
            {
                _spectatedRemoteCameraId = 0;
                _spectatedCameraOrderIndex = 0;
                RequestLocalCameraRefollow("spectate-no-valid-target");
                return;
            }

            if (count == 1)
            {
                var onlyTargetId = orderedTargets[0];
                _spectatedRemoteCameraId = onlyTargetId;
                _spectatedCameraOrderIndex = 0;
                if (onlyTargetId == 0)
                {
                    RequestLocalCameraRefollow("spectate-local-only");
                }
                else
                {
                    _cameraRefollowUntilTicks = 0;
                    TrackPreferredCameraTarget(immediate: true, forceTrack: true);
                }
                return;
            }

            var currentIndex = _spectatedCameraOrderIndex;
            if (currentIndex < 0 || currentIndex >= count || orderedTargets[currentIndex] != _spectatedRemoteCameraId)
                currentIndex = 0;
            for (var i = 0; i < count; i++)
            {
                if (orderedTargets[i] != _spectatedRemoteCameraId)
                    continue;

                currentIndex = i;
                break;
            }

            var nextIndex = currentIndex + direction;
            if (nextIndex < 0)
                nextIndex = count - 1;
            else if (nextIndex >= count)
                nextIndex = 0;

            var nextTargetId = orderedTargets[nextIndex];
            if (nextTargetId == _spectatedRemoteCameraId)
                return;

            var previousTargetId = _spectatedRemoteCameraId;
            _spectatedRemoteCameraId = nextTargetId;
            _spectatedCameraOrderIndex = nextIndex;
            if (nextTargetId == 0)
            {
                RequestLocalCameraRefollow($"cycle-local:{previousTargetId}");
                return;
            }

            _cameraRefollowUntilTicks = 0;
            TrackPreferredCameraTarget(immediate: true, forceTrack: true);
        }

        private void MaintainAutomaticDownedSpectate()
        {
            var shouldAutoSpectate = _netRole != NetRole.None && _localFakeDead && me != null;
            if (!shouldAutoSpectate)
            {
                if (!_automaticDownedSpectateActive)
                    return;

                var previousTargetId = _spectatedRemoteCameraId;
                _automaticDownedSpectateActive = false;
                _spectatedRemoteCameraId = 0;
                _spectatedCameraOrderIndex = 0;
                RequestLocalCameraRefollow($"auto-spectate-ended:{previousTargetId}");
                Logger.Information("[NetMod][Spectate] automatic downed spectate ended; camera restored to local player");
                return;
            }

            if (!_automaticDownedSpectateActive)
            {
                _automaticDownedSpectateActive = true;
                _cameraRefollowUntilTicks = 0;
            }

            // Preserve a valid manually selected living teammate. This keeps camera cycling useful
            // in sessions with more than two players without the automatic selector fighting it.
            if (_spectatedRemoteCameraId > 0 &&
                TryResolveRemoteCameraTarget(_spectatedRemoteCameraId, out _))
            {
                return;
            }

            var previousInvalidTargetId = _spectatedRemoteCameraId;
            _spectatedRemoteCameraId = 0;
            _spectatedCameraOrderIndex = 0;

            if (!TryFindFirstLivingRemoteCameraTarget(out var targetId, out var targetOrderIndex))
            {
                // When everyone is down, keep the local corpse view until the synchronized restart
                // instead of issuing a viewport.track call every frame.
                if (previousInvalidTargetId > 0)
                    RequestLocalCameraRefollow($"auto-spectate-no-living-target:{previousInvalidTargetId}");
                return;
            }

            _spectatedRemoteCameraId = targetId;
            _spectatedCameraOrderIndex = targetOrderIndex;
            _cameraRefollowUntilTicks = 0;
            TrackPreferredCameraTarget(immediate: true, forceTrack: true);
            Logger.Information("[NetMod][Spectate] automatic downed spectate following remote player {RemoteId}", targetId);
        }

        private bool TryFindFirstLivingRemoteCameraTarget(out int targetId, out int targetOrderIndex)
        {
            targetId = 0;
            targetOrderIndex = 0;
            var orderIndex = 0;

            for (var i = 0; i < clientIds.Length; i++)
            {
                var remoteId = clientIds[i];
                if (remoteId <= 0)
                    continue;
                if (!TryResolveRemoteCameraTarget(remoteId, out _))
                    continue;

                targetId = remoteId;
                targetOrderIndex = orderIndex;
                return true;
            }

            return false;
        }

        private void MaintainLocalCameraRefollow()
        {
            if (_cameraRefollowUntilTicks == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (now >= _cameraRefollowUntilTicks)
            {
                _cameraRefollowUntilTicks = 0;
                return;
            }

            TrackLocalCameraTarget(immediate: false, forceTrack: true);
        }

        private void RequestLocalCameraRefollow(string _)
        {
            _cameraRefollowUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * CameraRefollowHoldSeconds);
            TrackLocalCameraTarget(immediate: true, forceTrack: true);
        }

        private void TrackPreferredCameraTarget(bool immediate, bool forceTrack = false)
        {
            var target = ResolvePreferredCameraTargetCore(localFallback: null);
            TryTrackCameraTarget(target, immediate, forceTrack);
        }

        private void TrackLocalCameraTarget(bool immediate, bool forceTrack = false)
        {
            var target = ResolveDefaultLocalCameraTarget();
            TryTrackCameraTarget(target, immediate, forceTrack);
        }

        private void TryTrackCameraTarget(Entity? target, bool immediate, bool forceTrack)
        {
            var viewport = me?._level?.viewport ?? game?.curLevel?.viewport;
            if (viewport == null)
                return;

            if (!IsUsableCameraEntity(target))
                return;

            try
            {
                if (forceTrack || immediate || !ReferenceEquals(viewport.tracked, target))
                    viewport.track(target, immediate);
            }
            catch
            {
            }
        }

        private bool TryResolveRemoteCameraTarget(int remoteId, out Entity? target)
        {
            target = null;
            if (remoteId <= 0)
                return false;
            if (IsRemotePlayerDowned(remoteId))
                return false;

            var localId = _net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return false;
            if (index < 0 || index >= clients.Length)
                return false;
            if (_pendingClientDisposeTicks.ContainsKey(index))
                return false;

            var client = clients[index];
            if (!IsUsableCameraEntity(client))
                return false;

            var localLevelId = GetCurrentLevelId();
            var remoteLevelId = client?._level?.map?.id?.ToString();
            if (!string.IsNullOrWhiteSpace(localLevelId) &&
                !string.IsNullOrWhiteSpace(remoteLevelId) &&
                !string.Equals(localLevelId, remoteLevelId, StringComparison.Ordinal))
            {
                return false;
            }

            target = client;
            return true;
        }

        private static bool IsUsableCameraEntity(Entity? entity)
        {
            if (entity == null)
                return false;

            try
            {
                return !entity.destroyed && entity._level != null;
            }
            catch
            {
                return false;
            }
        }

    }
}

using System.Reflection;
using System.Runtime.InteropServices;
using dc.en;
using dc.hl.types;
using dc.pr;
using dc.tool;

namespace DeadCellsMultiplayerMod;

internal static partial class GameMenu
{
    private const int ReviveInteractKeyCode = 82; // R (legacy keyboard revive key)

    // SDL_GameControllerButton values used by Dead Cells' pad binding table. This is only a
    // last-resort Windows/XInput fallback; the normal path asks the game's own ControllerAccess
    // whether the bound interaction action is currently held.
    private const ushort XInputDpadUp = 0x0001;
    private const ushort XInputDpadDown = 0x0002;
    private const ushort XInputDpadLeft = 0x0004;
    private const ushort XInputDpadRight = 0x0008;
    private const ushort XInputStart = 0x0010;
    private const ushort XInputBack = 0x0020;
    private const ushort XInputLeftThumb = 0x0040;
    private const ushort XInputRightThumb = 0x0080;
    private const ushort XInputLeftShoulder = 0x0100;
    private const ushort XInputRightShoulder = 0x0200;
    private const ushort XInputA = 0x1000;
    private const ushort XInputB = 0x2000;
    private const ushort XInputX = 0x4000;
    private const ushort XInputY = 0x8000;

    private static bool _reviveInputResolutionLogged;
    private static bool _reviveInputFallbackLogged;
    private static int _reviveXInputBackend; // 0 unknown, 14 xinput1_4, 910 xinput9_1_0, -1 unavailable

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint userIndex, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState910(uint userIndex, out XInputState state);

    /// <summary>
    /// Hold-to-revive input. Keyboard R remains supported. Controller input follows the same
    /// gameplay action slot that is bound to R, so controller remapping is respected instead of
    /// hard-coding Xbox/PlayStation button numbers.
    /// </summary>
    internal static bool IsReviveHoldInputDown(Hero? hero)
    {
        if (hero == null)
            return false;

        try
        {
            if (dc.hxd.Key.Class.isDown(ReviveInteractKeyCode))
                return true;
        }
        catch
        {
        }

#pragma warning disable CS8602
        try
        {
            if (hero.controller is not ControllerAccess access)
                return false;
            if (access.manualLock)
                return false;

            var controller = access.parent;
            if (controller == null || controller.isLocked)
                return false;
            if (controller.exclusiveId != null && controller.exclusiveId != access.id)
                return false;
            if (!(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 >= controller.suspendTimer))
                return false;

            var bindings = controller.get_bindings();
            if (bindings == null)
                return false;

            var actionCode = ResolveReviveActionCode(
                bindings.primary,
                bindings.secondary,
                bindings.third);
            if (actionCode < 0)
            {
                LogReviveInputResolutionOnce(
                    "[NetMod][ReviveInput] Could not resolve the gameplay action bound to keyboard R; " +
                    "controller revive is unavailable until the interaction binding can be resolved.");
                return false;
            }

            // Preferred path: ControllerAccess understands action state, remapping, Steam Input,
            // DirectInput and the active controller. Different game/proxy revisions expose slightly
            // different names, so resolve the safe boolean member dynamically.
            if (TryReadBooleanActionState(access, actionCode, out var actionHeld))
                return actionHeld;

            // Fallback path: read the physical pad bindings for that same action. Try the game's
            // own held-state methods first, then XInput only when exactly one XInput pad is present.
            var padA = GetReviveBinding(bindings.padA, actionCode);
            var padB = GetReviveBinding(bindings.padB, actionCode);
            var padC = GetReviveBinding(bindings.padC, actionCode);

            return IsBoundPadHeld(controller, padA) ||
                   IsBoundPadHeld(controller, padB) ||
                   IsBoundPadHeld(controller, padC);
        }
        catch (Exception ex)
        {
            if (!_reviveInputFallbackLogged)
            {
                _reviveInputFallbackLogged = true;
                _log?.Warning("[NetMod][ReviveInput] Controller revive input failed safely: {Message}", ex.Message);
            }
        }
#pragma warning restore CS8602

        return false;
    }

    private static int ResolveReviveActionCode(
        ArrayBytes_Int? primary,
        ArrayBytes_Int? secondary,
        ArrayBytes_Int? third)
    {
        var maxLength = Math.Max(
            primary?.length ?? 0,
            Math.Max(secondary?.length ?? 0, third?.length ?? 0));

        for (var actionCode = 0; actionCode < maxLength; actionCode++)
        {
            if (GetReviveBinding(primary, actionCode) != ReviveInteractKeyCode &&
                GetReviveBinding(secondary, actionCode) != ReviveInteractKeyCode &&
                GetReviveBinding(third, actionCode) != ReviveInteractKeyCode)
            {
                continue;
            }

            if (!_reviveInputResolutionLogged)
            {
                _reviveInputResolutionLogged = true;
                _log?.Information(
                    "[NetMod][ReviveInput] Controller revive mapped to gameplay action {ActionCode}",
                    actionCode);
            }

            return actionCode;
        }

        return -1;
    }

    private static int GetReviveBinding(ArrayBytes_Int? bindings, int actionCode)
    {
        if (bindings == null || actionCode < 0 || actionCode >= bindings.length)
            return -1;

        try
        {
            return Marshal.ReadInt32(bindings.bytes, actionCode << 2);
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryReadBooleanActionState(object access, int actionCode, out bool value)
    {
        // isDown is the expected ControllerAccess API. Other names keep this compatible with
        // proxy/API revisions without taking a compile-time dependency on an optional member.
        return TryInvokeBooleanMember(access, "isDown", actionCode, out value) ||
               TryInvokeBooleanMember(access, "isHeld", actionCode, out value) ||
               TryInvokeBooleanMember(access, "down", actionCode, out value) ||
               TryInvokeBooleanMember(access, "held", actionCode, out value);
    }

    private static bool IsBoundPadHeld(Controller controller, int padCode)
    {
        if (padCode < 0)
            return false;

        if (TryInvokeBooleanMember(controller, "padIsDown", padCode, out var held))
            return held;
        if (TryInvokeBooleanMember(controller, "padIsHeld", padCode, out held))
            return held;
        if (TryInvokeBooleanMember(controller, "isPadDown", padCode, out held))
            return held;
        if (TryInvokeBooleanMember(controller, "isPadHeld", padCode, out held))
            return held;

        if (TryReadSingleXInputController(out var state) &&
            TryMapSdlButtonToXInputMask(padCode, out var buttonMask))
        {
            if (!_reviveInputFallbackLogged)
            {
                _reviveInputFallbackLogged = true;
                _log?.Information("[NetMod][ReviveInput] Using safe XInput held-state fallback");
            }

            return (state.Gamepad.Buttons & buttonMask) != 0;
        }

        // Compatibility fallback for game revisions where padIsPressed reports current state rather
        // than an edge. If it is edge-only, it returns false on subsequent frames and therefore
        // cannot accidentally complete a hold-to-revive by itself.
        try
        {
            return controller.padIsPressed(padCode);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeBooleanMember(object target, string memberName, int argument, out bool value)
    {
        value = false;
        if (target == null || string.IsNullOrEmpty(memberName))
            return false;

        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = target.GetType();

            foreach (var method in type.GetMethods(flags))
            {
                if (!string.Equals(method.Name, memberName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;

                var result = method.Invoke(target, new[] { ConvertReviveInputArgument(argument, parameters[0].ParameterType) });
                if (TryConvertReviveInputBoolean(result, out value))
                    return true;
            }

            object? callable = type.GetField(memberName, flags)?.GetValue(target)
                               ?? type.GetProperty(memberName, flags)?.GetValue(target);
            if (callable == null)
                return false;

            if (callable is Delegate del)
            {
                var result = del.DynamicInvoke(argument);
                return TryConvertReviveInputBoolean(result, out value);
            }

            foreach (var invoke in callable.GetType().GetMethods(flags))
            {
                if (!string.Equals(invoke.Name, "Invoke", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parameters = invoke.GetParameters();
                if (parameters.Length != 1)
                    continue;

                var result = invoke.Invoke(
                    callable,
                    new[] { ConvertReviveInputArgument(argument, parameters[0].ParameterType) });
                if (TryConvertReviveInputBoolean(result, out value))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static object ConvertReviveInputArgument(int value, System.Type targetType)
    {
        if (targetType == typeof(int) || targetType == typeof(object))
            return value;
        if (targetType == typeof(uint))
            return unchecked((uint)value);
        if (targetType == typeof(short))
            return unchecked((short)value);
        if (targetType == typeof(ushort))
            return unchecked((ushort)value);
        if (targetType.IsEnum)
            return Enum.ToObject(targetType, value);

        return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryConvertReviveInputBoolean(object? result, out bool value)
    {
        if (result is bool boolean)
        {
            value = boolean;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryReadSingleXInputController(out XInputState state)
    {
        state = default;
        var connectedCount = 0;
        XInputState candidate = default;

        for (uint index = 0; index < 4; index++)
        {
            if (!TryXInputGetState(index, out var current))
                continue;

            connectedCount++;
            candidate = current;
            if (connectedCount > 1)
                return false;
        }

        if (connectedCount != 1)
            return false;

        state = candidate;
        return true;
    }

    private static bool TryXInputGetState(uint userIndex, out XInputState state)
    {
        state = default;

        if (_reviveXInputBackend == -1)
            return false;

        if (_reviveXInputBackend == 14)
        {
            try { return XInputGetState14(userIndex, out state) == 0; }
            catch { _reviveXInputBackend = 0; }
        }
        else if (_reviveXInputBackend == 910)
        {
            try { return XInputGetState910(userIndex, out state) == 0; }
            catch { _reviveXInputBackend = 0; }
        }

        try
        {
            var result = XInputGetState14(userIndex, out state);
            _reviveXInputBackend = 14;
            return result == 0;
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch
        {
        }

        try
        {
            var result = XInputGetState910(userIndex, out state);
            _reviveXInputBackend = 910;
            return result == 0;
        }
        catch
        {
            _reviveXInputBackend = -1;
            return false;
        }
    }

    private static bool TryMapSdlButtonToXInputMask(int buttonCode, out ushort mask)
    {
        mask = buttonCode switch
        {
            0 => XInputA,
            1 => XInputB,
            2 => XInputX,
            3 => XInputY,
            4 => XInputBack,
            6 => XInputStart,
            7 => XInputLeftThumb,
            8 => XInputRightThumb,
            9 => XInputLeftShoulder,
            10 => XInputRightShoulder,
            11 => XInputDpadUp,
            12 => XInputDpadDown,
            13 => XInputDpadLeft,
            14 => XInputDpadRight,
            _ => 0
        };

        return mask != 0;
    }

    private static void LogReviveInputResolutionOnce(string message)
    {
        if (_reviveInputResolutionLogged)
            return;

        _reviveInputResolutionLogged = true;
        _log?.Warning(message);
    }
}

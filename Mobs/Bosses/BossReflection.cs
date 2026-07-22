using System.Reflection;

namespace DeadCellsMultiplayerMod.Mobs.Bosses;

/// <summary>
/// Reflection helpers for Haxe proxy objects. Proxy classes surface Haxe fields as C#
/// PROPERTIES, so plain GetField() lookups return null on them — which silently disabled the
/// generic boss phase/action sync for every boss without a dedicated typed branch
/// (Conjunctivius among them). These helpers try properties first, then fields, walking the
/// type hierarchy, and never throw.
/// </summary>
internal static class BossReflection
{
    private const BindingFlags Flags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    internal static object? TryReadMember(object? target, string name)
    {
        if (target == null || string.IsNullOrEmpty(name))
            return null;

        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            try
            {
                var p = t.GetProperty(name, Flags);
                if (p != null && p.CanRead)
                    return p.GetValue(target);
            }
            catch
            {
            }

            try
            {
                var f = t.GetField(name, Flags);
                if (f != null)
                    return f.GetValue(target);
            }
            catch
            {
            }
        }

        return null;
    }

    internal static bool TryWriteMember(object? target, string name, object? value)
    {
        if (target == null || string.IsNullOrEmpty(name))
            return false;

        for (var t = target.GetType(); t != null; t = t.BaseType)
        {
            try
            {
                var p = t.GetProperty(name, Flags);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(target, CoerceValue(value, p.PropertyType));
                    return true;
                }
            }
            catch
            {
            }

            try
            {
                var f = t.GetField(name, Flags);
                if (f != null)
                {
                    f.SetValue(target, CoerceValue(value, f.FieldType));
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    internal static int? TryReadInt(object? target, string name)
    {
        var value = TryReadMember(target, name);
        if (value == null)
            return null;

        try
        {
            if (value is bool b)
                return b ? 1 : 0;
            return System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static object? CoerceValue(object? value, System.Type memberType)
    {
        if (value == null)
            return null;

        try
        {
            if (memberType.IsInstanceOfType(value))
                return value;
            if (memberType == typeof(bool))
                return value is bool bb ? bb : System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
            return System.Convert.ChangeType(value, memberType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return value;
        }
    }

    /// <summary>
    /// Best-effort interrupt of a mob's currently running/queued skill. Used when the host's
    /// boss switched behaviour (phase change, expired attack lease) but the client's AI-locked
    /// boss would otherwise keep looping its stale action — e.g. Conjunctivius firing poison
    /// orbs long after the host entered the shield/tentacle stage. Every step is a silent
    /// no-op where the proxy exposes none of the known members.
    /// </summary>
    internal static void TryInterruptMobSkills(object? mob)
    {
        if (mob == null)
            return;

        try
        {
            for (var t = mob.GetType(); t != null; t = t.BaseType)
            {
                MethodInfo? m = null;
                try { m = t.GetMethod("interruptSkills", Flags, null, System.Type.EmptyTypes, null); } catch { }
                if (m != null)
                {
                    try { m.Invoke(mob, null); return; } catch { }
                }

                try { m = t.GetMethod("interruptSkills", Flags, null, new[] { typeof(bool) }, null); } catch { m = null; }
                if (m != null)
                {
                    try { m.Invoke(mob, new object[] { false }); return; } catch { }
                }
            }
        }
        catch
        {
        }

        TryWriteMember(mob, "queuedSkill", null);
        TryWriteMember(mob, "action", null);
    }
}

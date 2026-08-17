namespace DeadCellsMultiplayerMod.PortableCore;

/// <summary>Small boundary for HashLink/native calls that may throw during teardown or reload.</summary>
internal static class NativeCall
{
    public static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }
}

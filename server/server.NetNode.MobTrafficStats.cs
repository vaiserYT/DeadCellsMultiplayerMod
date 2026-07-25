using System.Threading;

/// <summary>
/// Minimal, allocation-free counters for the mob receive pipeline.
/// </summary>
/// <remarks>
/// These exist to answer one question during live testing without per-packet logging: are mob
/// lines actually arriving off the socket, and is the game thread consuming them? A stalled
/// receive pipeline shows up as a rising fast-path count with a flat consume count; a stalled
/// host shows up as both counts flat.
/// </remarks>
internal static class NetNodeMobTrafficStats
{
    private static long _fastPathLines;

    /// <summary>Total MOB* lines handled directly on the network thread since process start.</summary>
    public static long FastPathLines => Interlocked.Read(ref _fastPathLines);

    public static void RecordFastPathLine()
    {
        Interlocked.Increment(ref _fastPathLines);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _fastPathLines, 0L);
    }
}

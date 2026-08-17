using System.Diagnostics;
using System.Threading;
using Serilog;

namespace DeadCellsMultiplayerMod.Network;

/// <summary>Allocation-free aggregate transport diagnostics. Individual packets are never logged.</summary>
internal static class NetTrafficDiagnostics
{
    private static long _sentLines;
    private static long _sentBytes;
    private static long _receivedLines;
    private static long _receivedBytes;
    private static long _sendErrors;
    private static long _lastFlushTicks;

    public static void RecordSent(int bytes)
    {
        Interlocked.Increment(ref _sentLines);
        Interlocked.Add(ref _sentBytes, Math.Max(0, bytes));
    }

    public static void RecordReceived(int bytes)
    {
        Interlocked.Increment(ref _receivedLines);
        Interlocked.Add(ref _receivedBytes, Math.Max(0, bytes));
    }

    public static void RecordSendError()
    {
        Interlocked.Increment(ref _sendErrors);
    }

    public static void TryFlush(ILogger log, string role)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastFlushTicks);
        if (previous != 0 && now - previous < Stopwatch.Frequency * 5L)
            return;
        Interlocked.Exchange(ref _lastFlushTicks, now);

        var sentLines = Interlocked.Exchange(ref _sentLines, 0);
        var sentBytes = Interlocked.Exchange(ref _sentBytes, 0);
        var receivedLines = Interlocked.Exchange(ref _receivedLines, 0);
        var receivedBytes = Interlocked.Exchange(ref _receivedBytes, 0);
        var sendErrors = Interlocked.Exchange(ref _sendErrors, 0);

        log.Information(
            "[NetTraffic] PERF role={Role} sentLines={SentLines} sentBytes={SentBytes} receivedLines={ReceivedLines} receivedBytes={ReceivedBytes} sendErrors={SendErrors}",
            role ?? string.Empty,
            sentLines,
            sentBytes,
            receivedLines,
            receivedBytes,
            sendErrors);
    }
}

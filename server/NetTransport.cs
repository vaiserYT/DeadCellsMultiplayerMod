using System.Diagnostics;
using DeadCellsMultiplayerMod;

namespace DeadCellsMultiplayerMod.Network;

internal enum NetTransportKind
{
    Tcp,
    Steam
}

internal readonly record struct NetTransportSnapshot(
    NetTransportKind Kind,
    NetRole Role,
    bool HasConnection,
    bool IsDisposed);

internal sealed class NetPacketBudget
{
    private readonly long _windowTicks;
    private readonly int _maxBytes;
    private readonly object _sync = new();
    private long _windowStartedTicks;
    private int _usedBytes;
    internal long DroppedPackets { get; private set; }
    internal long DroppedBytes { get; private set; }

    internal NetPacketBudget(int maxBytes, double windowMilliseconds)
    {
        _maxBytes = Math.Max(1, maxBytes);
        _windowTicks = Math.Max(1L, (long)(Stopwatch.Frequency * windowMilliseconds / 1000.0));
    }

    internal bool TryConsume(int bytes)
    {
        var count = Math.Max(0, bytes);
        var now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (_windowStartedTicks == 0 || now - _windowStartedTicks >= _windowTicks)
            {
                _windowStartedTicks = now;
                _usedBytes = 0;
            }

            if (count > _maxBytes - _usedBytes)
            {
                DroppedPackets++;
                DroppedBytes += count;
                return false;
            }

            _usedBytes += count;
            return true;
        }
    }
}

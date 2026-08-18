using System.Threading;

namespace DeadCellsMultiplayerMod.Tools;

internal enum LifecycleState
{
    Created,
    Running,
    Stopping,
    Disposed
}

internal readonly record struct LifecycleSnapshot(
    string Owner,
    LifecycleState State,
    long Generation,
    int OwnedResources);

/// <summary>Small ownership guard for async game/network resources.</summary>
internal sealed class LifecycleTracker
{
    private readonly string _owner;
    private int _state = (int)LifecycleState.Created;
    private long _generation;
    private int _ownedResources;

    internal LifecycleTracker(string owner)
    {
        _owner = owner;
    }

    internal LifecycleSnapshot Snapshot => new(
        _owner,
        (LifecycleState)Volatile.Read(ref _state),
        Interlocked.Read(ref _generation),
        Volatile.Read(ref _ownedResources));

    internal long Start()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _state, (int)LifecycleState.Running);
        return Interlocked.Read(ref _generation);
    }

    internal bool TryBeginStop()
    {
        return Interlocked.CompareExchange(
                   ref _state,
                   (int)LifecycleState.Stopping,
                   (int)LifecycleState.Running) == (int)LifecycleState.Running;
    }

    internal void MarkDisposed()
    {
        Interlocked.Exchange(ref _state, (int)LifecycleState.Disposed);
        Interlocked.Exchange(ref _ownedResources, 0);
    }

    internal bool IsCurrent(long generation) =>
        generation == Interlocked.Read(ref _generation) &&
        (LifecycleState)Volatile.Read(ref _state) == LifecycleState.Running;

    internal void OwnResource() => Interlocked.Increment(ref _ownedResources);

    internal void ReleaseResource()
    {
        var current = Volatile.Read(ref _ownedResources);
        while (current > 0 &&
               Interlocked.CompareExchange(ref _ownedResources, current - 1, current) != current)
        {
            current = Volatile.Read(ref _ownedResources);
        }
    }
}

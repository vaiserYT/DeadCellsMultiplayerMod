using DeadCellsMultiplayerMod.PortableCore;
using DeadCellsMultiplayerMod.Network;
using DeadCellsMultiplayerMod.Tools;
using Xunit;

namespace DeadCellsMultiplayerMod.ProtocolReplay.Tests;

public sealed class LaunchProtocolReplayTests
{
    [Fact]
    public void CommitLine_RoundTripsDescriptor()
    {
        var descriptor = CreateDescriptor();

        var line = RunLaunchWireCodec.BuildCommitLine(descriptor);
        Assert.StartsWith("RUNCOMMIT|", line, StringComparison.Ordinal);
        Assert.True(
            RunLaunchWireCodec.TryDecodeCommit(line["RUNCOMMIT|".Length..], out var decoded, out var error),
            error);
        Assert.NotNull(decoded);
        Assert.Equal(descriptor, decoded);
    }

    [Fact]
    public void ReplayRejectsStaleAndIllegalTransitions()
    {
        var state = new CoopSessionStateMachine();

        Assert.True(state.TryTransition(CoopSessionPhase.Lobby, 1, "connected", out var error), error);
        Assert.True(state.TryTransition(CoopSessionPhase.LaunchCommitted, 2, "commit", out error), error);
        Assert.False(state.TryTransition(CoopSessionPhase.Playing, 2, "stale", out error));
        Assert.Contains("Stale transition", error, StringComparison.Ordinal);
        Assert.False(state.TryTransition(CoopSessionPhase.Disconnected, 3, "illegal", out error));
        Assert.Contains("Illegal co-op session transition", error, StringComparison.Ordinal);
        Assert.Equal(CoopSessionPhase.LaunchCommitted, state.Phase);
    }

    [Fact]
    public void MalformedAndOversizedCommitPayloadsAreRejected()
    {
        Assert.False(RunLaunchWireCodec.TryDecodeCommit("not-base64", out _, out var malformedError));
        Assert.False(string.IsNullOrWhiteSpace(malformedError));

        var oversized = new string('A', 64 * 1024 + 1);
        Assert.False(RunLaunchWireCodec.TryDecodeCommit(oversized, out _, out var oversizedError));
        Assert.Contains("exceeds", oversizedError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientLaunchGateMapsToPortableSessionPhases()
    {
        Assert.Equal(
            CoopSessionPhase.LaunchCommitted,
            RunLaunchCoordinator.MapClientLaunchPhaseToSessionPhase(LobbySession.ClientLaunchPhase.Armed));
        Assert.Equal(
            CoopSessionPhase.LoadingLevel,
            RunLaunchCoordinator.MapClientLaunchPhaseToSessionPhase(LobbySession.ClientLaunchPhase.Starting));
        Assert.Equal(
            CoopSessionPhase.Playing,
            RunLaunchCoordinator.MapClientLaunchPhaseToSessionPhase(LobbySession.ClientLaunchPhase.InRun));
    }

    [Fact]
    public void RealtimeBudgetDropsOnlyWhenWindowIsFull()
    {
        var budget = new NetPacketBudget(maxBytes: 10, windowMilliseconds: 1000);

        Assert.True(budget.TryConsume(6));
        Assert.False(budget.TryConsume(5));
        Assert.Equal(1, budget.DroppedPackets);
        Assert.Equal(5, budget.DroppedBytes);
    }

    [Fact]
    public void LifecycleTrackerInvalidatesOldGenerationOnStop()
    {
        var tracker = new LifecycleTracker("replay");
        var generation = tracker.Start();

        Assert.True(tracker.IsCurrent(generation));
        Assert.True(tracker.TryBeginStop());
        Assert.False(tracker.IsCurrent(generation));

        tracker.MarkDisposed();
        Assert.Equal(LifecycleState.Disposed, tracker.Snapshot.State);
    }

    private static RunLaunchDescriptor CreateDescriptor()
    {
        return new RunLaunchDescriptor(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RunId: 7,
            Sequence: 3,
            ProtocolVersion: 17,
            RunSeed: 12345,
            LaunchKind: "normal",
            InitialLevelId: "PrisonStart",
            InitialLevelSeed: 42.5,
            Difficulty: 2,
            BossCells: 2,
            BossRush: false,
            DlcFlags: 0,
            SaveSlot: 1,
            BossRushSeed: 0,
            LevelGenSeed: 0,
            BossRushTier: 0,
            Route: string.Empty,
            BossSequence: string.Empty,
            Modifiers: 0,
            TargetArena: "PrisonStart");
    }
}

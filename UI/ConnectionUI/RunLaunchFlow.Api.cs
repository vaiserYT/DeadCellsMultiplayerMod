namespace DeadCellsMultiplayerMod;

/// <summary>Run-launch handshake API surface (implementation lives in LobbySession RunLaunchFlow partials).</summary>
internal static class RunLaunchFlow
{
    internal static void ReceiveRunLaunchCommitPayload(string payload)
        => LobbySession.ReceiveRunLaunchCommitPayload(payload);

    internal static void ReceiveRunLaunchAckPayload(string payload)
        => LobbySession.ReceiveRunLaunchAckPayload(payload);

    internal static void ReceiveRunLaunchExecutePayload(string payload)
        => LobbySession.ReceiveRunLaunchExecutePayload(payload);

    internal static void ReceiveRunLaunchQueuedPayload(string payload)
        => LobbySession.ReceiveRunLaunchQueuedPayload(payload);

    internal static void ReceiveRunLevelReadyPayload(string payload)
        => LobbySession.ReceiveRunLevelReadyPayload(payload);

    internal static void ReceiveRunLaunchCancelPayload(string payload)
        => LobbySession.ReceiveRunLaunchCancelPayload(payload);

    public static void ReceiveHostRunSeed(int sequence, int seed, string launchKind)
        => LobbySession.ReceiveHostRunSeed(sequence, seed, launchKind);

    public static void ReceiveHostRunRestart(int seed)
        => LobbySession.ReceiveHostRunRestart(seed);

    public static void ReceiveLaunchMode(
        int actionValue,
        bool launchCustom,
        bool launchStreamEnabled,
        bool newCoopWorldPrepared,
        string? coopId,
        bool hostHasContinueSave)
        => LobbySession.ReceiveLaunchMode(
            actionValue,
            launchCustom,
            launchStreamEnabled,
            newCoopWorldPrepared,
            coopId,
            hostHasContinueSave);

    public static void ReceiveCustomGameData(string? payload)
        => LobbySession.ReceiveCustomGameData(payload);

    internal static bool TryConsumeMidRunJoinSpawn()
        => LobbySession.TryConsumeMidRunJoinSpawn();
}

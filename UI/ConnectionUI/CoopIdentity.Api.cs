namespace DeadCellsMultiplayerMod;

/// <summary>Co-op identity API surface (implementation lives in LobbySession CoopIdentity partial).</summary>
internal static class CoopIdentity
{
    public static void ReceiveRemoteCoopState(int userId, string? coopId, bool hasContinueSave)
        => LobbySession.ReceiveRemoteCoopState(userId, coopId, hasContinueSave);
}

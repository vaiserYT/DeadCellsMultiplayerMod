namespace DeadCellsMultiplayerMod;

/// <summary>Lobby ready-state API surface (implementation lives in LobbySession ReadySync partial).</summary>
internal static class ReadySync
{
    internal static void ReceiveRemoteReady(int userId, bool ready)
        => LobbySession.ReceiveRemoteReady(userId, ready);

    internal static void ResetLobbyReadyState()
        => LobbySession.ResetLobbyReadyState();
}

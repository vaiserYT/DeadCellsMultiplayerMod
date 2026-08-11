namespace DeadCellsMultiplayerMod;

/// <summary>Multiplayer save-slot API surface (implementation lives in LobbySession MultiplayerSaves partial).</summary>
internal static class MultiplayerSaves
{
    internal static void InitializeMultiplayerSaveHooks()
        => LobbySession.InitializeMultiplayerSaveHooks();
}

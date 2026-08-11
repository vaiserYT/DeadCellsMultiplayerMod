namespace DeadCellsMultiplayerMod;

/// <summary>Title-screen multiplayer button hooks (implementation lives in LobbySession TitleMenuHooks partial).</summary>
internal static class TitleMenuHooks
{
    internal static void InitializeMenuUiHooks()
        => LobbySession.InitializeMenuUiHooks();
}

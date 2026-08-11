namespace DeadCellsMultiplayerMod;

/// <summary>Multiplayer save-guard API surface (implementation lives in LobbySession SaveGuard partial).</summary>
internal static class SaveGuard
{
    internal static void NotifyRunLaunchPhaseForSaveGuard(string phase)
        => LobbySession.NotifyRunLaunchPhaseForSaveGuard(phase);
}

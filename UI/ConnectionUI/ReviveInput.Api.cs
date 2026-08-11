using dc.en;

namespace DeadCellsMultiplayerMod;

/// <summary>Revive-input API surface (implementation lives in LobbySession ReviveInput partial).</summary>
internal static class ReviveInput
{
    internal static bool IsReviveHoldInputDown(Hero? hero)
        => LobbySession.IsReviveHoldInputDown(hero);
}

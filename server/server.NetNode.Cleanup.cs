using System.Globalization;
using DeadCellsMultiplayerMod;

public sealed partial class NetNode
{
    private void CleanupClient()
    {
        lock (_sync)
        {
            _hasRemote = false;
            _connectedClientCount = 0;
            _remotes.Clear();
            _primaryRemoteId = 0;
            _pendingAttacks.Clear();
            _pendingChatMessages.Clear();
            _pendingMobStates.Clear();
            _pendingMobMoves.Clear();
            _pendingMobCharges.Clear();
            _pendingMobHits.Clear();
            _pendingMobDies.Clear();
            _pendingBossVictories.Clear();
            _pendingMobAttacks.Clear();
            _pendingMobDraws.Clear();
            _pendingMobRegistry.Clear();
            _pendingExitReadyStates.Clear();
            _pendingBossCineLevelIds.Clear();
            _pendingBossIntroEnds.Clear();
            _pendingBossIntroReadyStates.Clear();
            _pendingBossHeroTeleports.Clear();
            _pendingPlayerDownStates.Clear();
            _pendingPlayerReviveRequests.Clear();
            _pendingInterDoorEvents.Clear();
            _pendingInterElevatorEvents.Clear();
            _pendingInterElevatorStateEvents.Clear();
            _pendingInterPressurePlateEvents.Clear();
            _pendingInterTreasureChestEvents.Clear();
            _pendingInterVineLadderEvents.Clear();
            _pendingInterTeleportEvents.Clear();
            _pendingInterBreakableGroundEvents.Clear();
            _pendingBossRuneUpdateCells.Clear();
            _pendingInterPortalEvents.Clear();
        }
        if (_useSteamTransport)
        {
            if (_steamHostId.m_SteamID != 0UL)
                _steamBridge?.TryClosePeer(_steamHostId.m_SteamID);
        }
        else
        {
            CloseClientConnection();
        }
        GameMenu.EnqueueCriticalMainThreadCoalesced("net:remote-disconnected", () =>
        {
            if (IsCurrentNetworkSession())
                GameMenu.NotifyRemoteDisconnected(_role);
        });
    }

    private RemoteState GetOrCreateRemoteLocked(int id)
    {
        if (!_remotes.TryGetValue(id, out var state))
        {
            state = new RemoteState(id);
            _remotes[id] = state;
        }
        return state;
    }

    private void RemoveRemoteLocked(int id)
    {
        _remotes.Remove(id);
        if (_primaryRemoteId == id)
        {
            _primaryRemoteId = 0;
            foreach (var key in _remotes.Keys)
            {
                if (_primaryRemoteId == 0 || key < _primaryRemoteId)
                    _primaryRemoteId = key;
            }
        }
    }

    private static int? ResolvePayloadId(string payload, int? senderId, out string cleanedPayload)
    {
        cleanedPayload = payload;
        var parts = payload.Split(new[] { '|' }, 2);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
        {
            cleanedPayload = parts[1];
            return parsedId;
        }
        return senderId;
    }
}

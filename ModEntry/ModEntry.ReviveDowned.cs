using dc;


namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        /// <summary>Assigned id for the listen-server host (<see cref="NetNode"/>).</summary>
        internal const int MultiplayerHostAssignedId = 1;

        internal static bool IsLocalPlayerDowned()
        {
            return Instance != null && Instance._localFakeDead;
        }

        internal static bool ShouldAnchorLocalDownedCorpse()
        {
            return Instance != null && Instance._localFakeDead;
        }

        /// <summary>
        /// True when the session host is fake-dead (on host) or their down state was received (on client).
        /// </summary>
        internal static bool IsSessionHostDowned(NetNode? net)
        {
            if (net == null || !net.IsAlive)
                return false;
            if (net.IsHost)
                return IsLocalPlayerDowned();
            return IsRemotePlayerDowned(MultiplayerHostAssignedId);
        }

        internal static void ApplyLocalDownedExitPenaltyIfNeeded()
        {
            Instance?.ApplyLocalDownedExitPenaltyIfNeededCore();
        }

        internal static bool IsRemotePlayerDowned(int userId)
        {
            var instance = Instance;
            if (instance == null || userId <= 0)
                return false;

            if (!instance._remoteDowned.TryGetValue(userId, out var state) || state == null)
                return false;

            var localLevelId = instance.GetCurrentLevelId();
            if (!string.IsNullOrEmpty(localLevelId) &&
                !string.IsNullOrEmpty(state.LevelId) &&
                !string.Equals(localLevelId, state.LevelId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        internal static bool HasAnyPlayerDownedForCombat()
        {
            var instance = Instance;
            if (instance == null)
                return false;

            if (instance._localFakeDead)
                return true;

            if (instance._remoteDowned.Count == 0)
                return false;

            var localLevelId = instance.GetCurrentLevelId();
            foreach (var state in instance._remoteDowned.Values)
            {
                if (state == null)
                    continue;
                if (string.IsNullOrEmpty(localLevelId) ||
                    string.IsNullOrEmpty(state.LevelId) ||
                    string.Equals(localLevelId, state.LevelId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsEntityDownedForCombat(Entity? entity)
        {
            if (entity == null)
                return false;

            var localHero = me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localHero != null && ReferenceEquals(entity, localHero))
                return IsLocalPlayerDowned();

            var net = _net;
            var localId = net?.id ?? 0;
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client == null || !ReferenceEquals(entity, client))
                    continue;

                var remoteId = clientIds[i];
                if (remoteId <= 0)
                    return false;
                if (localId > 0 && remoteId == localId)
                    return IsLocalPlayerDowned();
                return IsRemotePlayerDowned(remoteId);
            }

            return false;
        }
    }
}

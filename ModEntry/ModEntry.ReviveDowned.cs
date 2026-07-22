using dc;
using System.Diagnostics;


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

        /// <summary>
        /// Boss victory is already host-authoritative through the mob death pipeline.  A downed
        /// local player is restored as part of that same confirmed event, before reward pickup.
        /// Duplicate death tombstones are harmless because revive is idempotent.
        /// </summary>
        internal static void ReviveLocalPlayerAfterBossVictory()
        {
            var instance = Instance;
            if (instance == null || !instance._localFakeDead)
                return;

            var net = _net;
            if (net == null || !net.IsAlive)
                return;

            instance.ReviveLocalPlayer(net);
        }

        /// <summary>
        /// A host-confirmed victory is also the client's presentation barrier: no stale local boss
        /// death cinematic or spectator target may retain the camera after rewards become available.
        /// </summary>
        internal static void RecoverLocalPresentationAfterBossVictory()
        {
            var instance = Instance;
            var net = _net;
            if (instance == null || net == null || !net.IsAlive || net.IsHost)
                return;

            instance._clientBossVictoryRecoveryPending = true;
            instance._clientBossVictoryRecoveryStartedTick = Stopwatch.GetTimestamp();
            instance._clientBossVictoryRecoveryLevelId = instance.GetCurrentLevelId();

            GameMenu.EnqueueCriticalMainThreadCoalesced("game:boss-victory-presentation", () =>
            {
                if (_net == null || !_net.IsAlive || _net.IsHost)
                    return;

                instance.ApplyClientBossVictoryPresentationRecovery(releaseUnknownCinematic: false);
            });
        }

        private void MaintainClientBossVictoryPresentationRecovery()
        {
            if (!_clientBossVictoryRecoveryPending)
                return;
            if (_netRole != NetRole.Client || _net == null || !_net.IsAlive)
            {
                ResetClientBossVictoryPresentationRecovery();
                return;
            }

            var currentLevelId = GetCurrentLevelId();
            if (!string.IsNullOrWhiteSpace(_clientBossVictoryRecoveryLevelId) &&
                !string.Equals(currentLevelId, _clientBossVictoryRecoveryLevelId, StringComparison.OrdinalIgnoreCase))
            {
                ResetClientBossVictoryPresentationRecovery();
                return;
            }

            var elapsedSeconds = _clientBossVictoryRecoveryStartedTick > 0
                ? (Stopwatch.GetTimestamp() - _clientBossVictoryRecoveryStartedTick) / (double)Stopwatch.Frequency
                : 0.0;

            var hasRetainedCinematic = false;
            try
            {
                var cine = dc.pr.Game.Class.ME?.curCine;
                hasRetainedCinematic = cine != null;
            }
            catch
            {
            }

            ApplyClientBossVictoryPresentationRecovery(
                releaseUnknownCinematic: elapsedSeconds >= ClientBossVictoryUnknownCineReleaseSeconds);

            if (elapsedSeconds >= ClientBossVictoryRecoveryMaxSeconds ||
                (!hasRetainedCinematic && elapsedSeconds >= ClientBossVictoryNoCineGraceSeconds))
            {
                ResetClientBossVictoryPresentationRecovery();
            }
        }

        private void ApplyClientBossVictoryPresentationRecovery(bool releaseUnknownCinematic)
        {
            try
            {
                var game = dc.pr.Game.Class.ME;
                var cine = game?.curCine;
                if (cine != null && cine is not DeadBase && cine is not RemoteDownedCorpse)
                {
                    var typeName = cine.GetType().Name ?? string.Empty;
                    var isKnownBossDeath = BossDeathCineTypeNames.Contains(typeName);
                    var isHeroDeath = typeName.Contains("HeroDeath", StringComparison.OrdinalIgnoreCase);
                    var isConfirmedBossLevel = IsBossLevel(GetCurrentLevelId());
                    var isDestroyed = false;
                    try { isDestroyed = cine.destroyed; } catch { }
                    if (isKnownBossDeath ||
                        (isDestroyed && isConfirmedBossLevel && !isHeroDeath) ||
                        (releaseUnknownCinematic && isConfirmedBossLevel && !isHeroDeath))
                    {
                        // Unknown DLC/Boss Rush death cinematics are allowed to finish naturally.
                        // If one still owns Game.curCine eight seconds after host-confirmed victory,
                        // it is stale and must not strand only the client behind a letterbox/camera lock.
                        SuppressRemoteBossDeathCineState(cine);
                    }
                }
            }
            catch
            {
            }

            SuppressRemoteBossDeathCineIfNeeded();
            _automaticDownedSpectateActive = false;
            _spectatedRemoteCameraId = 0;
            _spectatedCameraOrderIndex = 0;
            try { me?.cancelSkillControlLock(); } catch { }
            try { me?.unlockControls(); } catch { }
            EnsureHeroVisibilityAfterRoomChange(me);
            RequestLocalCameraRefollow("host-boss-victory");
        }

        private void ResetClientBossVictoryPresentationRecovery()
        {
            _clientBossVictoryRecoveryPending = false;
            _clientBossVictoryRecoveryStartedTick = 0;
            _clientBossVictoryRecoveryLevelId = null;
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

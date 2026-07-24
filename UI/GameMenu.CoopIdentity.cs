using System.Diagnostics;
using System.Globalization;

namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private const string ContinueReasonOk = "OK";
        private static readonly Dictionary<int, RemoteCoopState> _remoteCoopStates = new();
        private static bool _receivedLaunchPayload;
        private static bool _receivedNewCoopWorldPrepared;
        private static bool _pendingNewCoopWorldIdAssigned;
        private static string? _storedPendingNewCoopWorldCoopId;
        private static int? _storedPendingNewCoopWorldSeed;
        private static int _continueSaveCacheSlot = -1;
        private static long _continueSaveCacheTicks;
        private static bool _continueSaveCacheValid;
        private static bool _continueSaveCacheHasSave;
        private static string _continueSaveCacheReason = ContinueReasonOk;
        private static string _lastLoggedClientContinueBlockReason = string.Empty;
        private const double ContinueSaveCacheSeconds = 1.0;

        public static void ReceiveRemoteCoopState(int userId, string? coopId, bool hasContinueSave)
        {
            if (userId <= 0)
                return;

            var normalized = MUser.NormalizeCoopId(coopId);
            lock (Sync)
            {
                _remoteCoopStates[userId] = new RemoteCoopState(normalized, hasContinueSave);
            }

            TryStoreRemoteCoopIdForPendingNewGame();
            RequestLobbyMenuRefresh();
        }

        private static void ResetRemoteCoopStateLocked()
        {
            _remoteCoopStates.Clear();
            _receivedLaunchPayload = false;
            _receivedNewCoopWorldPrepared = false;
            _pendingNewCoopWorldIdAssigned = false;
            _storedPendingNewCoopWorldCoopId = null;
            _storedPendingNewCoopWorldSeed = null;
            _lastLoggedClientContinueBlockReason = string.Empty;
        }

        private static void SendCoopStateToRemote()
        {
            var net = NetRef;
            if (net == null || !net.IsAlive)
                return;

            var localCoopId = MUser.GetCurrentCoopId() ?? string.Empty;
            var hasContinueSave = HasLocalContinueSaveState(out _);

            try
            {
                net.SendCoopState(localCoopId, hasContinueSave);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send coop id: {Message}", ex.Message);
            }
        }

        private static void NotifyMultiplayerSaveSlotChanged()
        {
            InvalidateLocalContinueSaveStateCache();
            SendCoopStateToRemote();
            RequestLobbyMenuRefresh();
        }

        private static void PrepareCoopIdentityForPendingLaunch(PendingLaunchAction action)
        {
            if (_role != NetRole.Host)
                return;

            if (action != PendingLaunchAction.NewGame)
            {
                lock (Sync)
                {
                    _pendingNewCoopWorldIdAssigned = false;
                }

                SendCoopStateToRemote();
                return;
            }

            var shouldCreate = false;
            lock (Sync)
            {
                if (!_pendingNewCoopWorldIdAssigned || _pendingLaunchAction != PendingLaunchAction.NewGame)
                {
                    _pendingNewCoopWorldIdAssigned = true;
                    shouldCreate = true;
                }
            }

            if (!shouldCreate)
            {
                SendCoopStateToRemote();
                return;
            }

            int? seed;
            lock (Sync)
            {
                seed = _serverSeed;
            }

            var coopId = MUser.EnsureCoopIdForNewCoopWorld(_playerId, seed);
            _log?.Information("[NetMod] Created coop id {CoopId} for new coop world", coopId);
            SendCoopStateToRemote();
        }

        private static void TryStoreRemoteCoopIdForPendingNewGame()
        {
            string? remoteCoopId;
            int? seed;
            lock (Sync)
            {
                if (_role != NetRole.Client ||
                    !_receivedLaunchPayload ||
                    !_receivedNewCoopWorldPrepared ||
                    _pendingLaunchAction != PendingLaunchAction.NewGame)
                {
                    return;
                }

                if (!_remoteCoopStates.TryGetValue(1, out var hostState) ||
                    string.IsNullOrWhiteSpace(hostState.CoopId))
                {
                    return;
                }

                remoteCoopId = hostState.CoopId;
                seed = _remoteSeed;
                if (string.Equals(_storedPendingNewCoopWorldCoopId, remoteCoopId, StringComparison.Ordinal) &&
                    _storedPendingNewCoopWorldSeed == seed)
                {
                    return;
                }
            }

            if (!MUser.SetCoopId(remoteCoopId, GetRemoteHostIdentity(), seed))
            {
                _log?.Warning("[NetMod] Failed to store host coop id for new coop world");
                return;
            }

            lock (Sync)
            {
                _storedPendingNewCoopWorldCoopId = remoteCoopId;
                _storedPendingNewCoopWorldSeed = seed;
            }

            _log?.Information("[NetMod] Stored host coop id {CoopId} for new coop world", remoteCoopId);
            SendCoopStateToRemote();
        }

        private static bool CanHostStartContinue(out string reason)
        {
            if (!AllPlayersReady())
            {
                reason = "Not all players ready";
                return false;
            }

            return IsHostContinueCompatible(out reason);
        }

        private static bool IsHostContinueCompatible(out string reason)
        {
            if (!TryGetLocalContinueReadiness(out var localCoopId, out reason))
                return false;

            var net = NetRef;
            if (net == null || !net.IsAlive)
            {
                reason = ContinueReasonOk;
                return true;
            }

            if (!net.TryGetRemoteUserSnapshots(out var snapshots))
            {
                reason = ContinueReasonOk;
                return true;
            }

            try
            {
                if (snapshots.Count == 0)
                {
                    reason = ContinueReasonOk;
                    return true;
                }

                lock (Sync)
                {
                    for (var i = 0; i < snapshots.Count; i++)
                    {
                        var remoteId = snapshots[i].Id;
                        if (remoteId <= 0)
                            continue;

                        if (!_remoteCoopStates.TryGetValue(remoteId, out var remoteState))
                        {
                            reason = "Client coop id not received";
                            return false;
                        }

                        if (!remoteState.HasContinueSave)
                        {
                            reason = "Client has no continue save";
                            return false;
                        }

                        if (string.IsNullOrWhiteSpace(remoteState.CoopId))
                        {
                            reason = "Client has no local coop id";
                            return false;
                        }

                        if (!string.Equals(localCoopId, remoteState.CoopId, StringComparison.Ordinal))
                        {
                            reason = "Coop world mismatch";
                            return false;
                        }
                    }
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(snapshots);
            }

            reason = ContinueReasonOk;
            return true;
        }

        private static bool CanClientAcceptContinueLaunchLocked(out string reason)
        {
            if (!TryGetLocalContinueReadiness(out var localCoopId, out reason))
                return false;

            if (!_remoteCoopStates.TryGetValue(1, out var hostState))
            {
                reason = "Host coop id not received";
                return false;
            }

            if (!hostState.HasContinueSave)
            {
                reason = "Host has no continue save";
                return false;
            }

            if (string.IsNullOrWhiteSpace(hostState.CoopId))
            {
                reason = "Host has no coop id";
                return false;
            }

            if (!string.Equals(localCoopId, hostState.CoopId, StringComparison.Ordinal))
            {
                reason = "Coop world mismatch";
                return false;
            }

            reason = ContinueReasonOk;
            return true;
        }

        private static bool TryGetLocalContinueReadiness(out string localCoopId, out string reason)
        {
            localCoopId = string.Empty;

            if (!HasLocalContinueSaveState(out reason))
                return false;

            var coopId = MUser.GetCurrentCoopId();
            if (string.IsNullOrWhiteSpace(coopId))
            {
                reason = "No local coop id";
                return false;
            }

            localCoopId = coopId;
            reason = ContinueReasonOk;
            return true;
        }

        private static bool HasLocalContinueSaveState(out string reason)
        {
            var slot = ResolveCurrentSaveSlotForCache();
            var now = Stopwatch.GetTimestamp();
            lock (Sync)
            {
                if (_continueSaveCacheValid &&
                    _continueSaveCacheSlot == slot &&
                    Stopwatch.GetElapsedTime(_continueSaveCacheTicks, now).TotalSeconds < ContinueSaveCacheSeconds)
                {
                    reason = _continueSaveCacheReason;
                    return _continueSaveCacheHasSave;
                }
            }

            var hasSave = ReadLocalContinueSaveState(out reason);
            lock (Sync)
            {
                _continueSaveCacheSlot = slot;
                _continueSaveCacheTicks = now;
                _continueSaveCacheValid = true;
                _continueSaveCacheHasSave = hasSave;
                _continueSaveCacheReason = reason;
            }

            return hasSave;
        }

        private static bool ReadLocalContinueSaveState(out string reason)
        {
            try
            {
                var relativePath = GetMultiplayerSaveRelativeFilePath(null);
                if (!dc.tool.File.Class.exists.Invoke(MakeHLString(relativePath)))
                {
                    reason = "No continue save";
                    return false;
                }

                var bytes = dc.tool.File.Class.getBytes.Invoke(MakeHLString(relativePath));
                if (bytes == null)
                {
                    reason = "No continue save";
                    return false;
                }

                var user = dc.tool.Save.Class.readSave.Invoke(bytes);
                if (user?.mainGameData == null)
                {
                    reason = "No continue save";
                    return false;
                }

                reason = ContinueReasonOk;
                return true;
            }
            catch (Exception ex)
            {
                reason = "No continue save";
                _log?.Warning("[NetMod] Failed to validate multiplayer continue save: {Message}", ex.Message);
                return false;
            }
        }

        private static void InvalidateLocalContinueSaveStateCache()
        {
            lock (Sync)
            {
                _continueSaveCacheValid = false;
                _continueSaveCacheSlot = -1;
                _continueSaveCacheTicks = 0;
                _continueSaveCacheHasSave = false;
                _continueSaveCacheReason = ContinueReasonOk;
            }
        }

        private static int ResolveCurrentSaveSlotForCache()
        {
            try
            {
                var current = dc.Main.Class.ME?.options?.curSlot;
                if (current.HasValue && current.Value >= 0)
                    return current.Value;
            }
            catch
            {
            }

            return 0;
        }

        private static void LogClientContinueBlockReasonLocked(string reason)
        {
            if (string.Equals(_lastLoggedClientContinueBlockReason, reason, StringComparison.Ordinal))
                return;

            _lastLoggedClientContinueBlockReason = reason;
            _log?.Warning("[NetMod] Continue Coop blocked on client: {Reason}", reason);
        }

        private static string GetRemoteHostIdentity()
        {
            if (_steamHostSteamId != 0UL)
                return _steamHostSteamId.ToString(CultureInfo.InvariantCulture);

            return string.IsNullOrWhiteSpace(_remoteUsername)
                ? "host"
                : _remoteUsername.Trim();
        }

        private readonly struct RemoteCoopState
        {
            public readonly string? CoopId;
            public readonly bool HasContinueSave;

            public RemoteCoopState(string? coopId, bool hasContinueSave)
            {
                CoopId = coopId;
                HasContinueSave = hasContinueSave;
            }
        }
    }
}

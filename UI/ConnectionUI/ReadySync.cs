using System.Globalization;
using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;

namespace DeadCellsMultiplayerMod
{
    internal static partial class LobbySession
    {
        internal static void ResetLobbyReadyState()
        {
            lock (Sync)
            {
                ResetLobbyReadyStateLocked();
            }
        }

        internal static void ResetLobbyReadyStateLocked()
        {
            _localReady = false;
            _playersDisplay.Clear();
        }

        internal static void ResetLobbyLaunchStateLocked()
        {
            StopHostRunLaunchBeacon("lobby_launch_state_reset");
            _clientLevelGraphWaitStartedTicks = 0;
            _clientLevelGraphWaitExpired = false;
            // A pending join spawn belongs to the session that observed the host playing; it must
            // not survive into a fresh lobby/launch.
            _midRunJoinSpawnPending = false;
            _inActualRun = false;
            _pendingAutoStart = false;
            _autoStartTriggered = false;
            _pendingClientRestartSeed = null;
            _pendingClientRestartReason = string.Empty;
            _continueLaunchInProgress = false;
            _continueLaunchStartedAt = DateTime.MinValue;
            _autoStartRetryAt = DateTime.MinValue;
            _genArrived = false;
            _seedArrived = false;
            _receivedLaunchPayload = false;
            _receivedNewCoopWorldPrepared = false;
            _remoteCustomGameDataReady = false;
            _pendingRemoteCustomGameDataJson = null;
            ResetClientLaunchSessionLocked();
        }

        internal static void PrepareLobbyForNewNetworkSession(bool clearRemoteCoopState = false)
        {
            lock (Sync)
            {
                ResetLobbyLaunchStateLocked();
                ResetLobbyReadyStateLocked();
                _pendingNewCoopWorldIdAssigned = false;
                if (clearRemoteCoopState)
                    ResetRemoteCoopStateLocked();
            }
        }

        internal static void ToggleLocalReadyFromMenu(TitleScreen screen)
        {
            SetLocalReady(!ReadSessionSnapshot().LocalReady, sendToRemote: true, refreshMenu: true);
            screen.ShouldAutoHideConnectionUI(true);
        }

        internal static void SetLocalReady(bool ready, bool sendToRemote, bool refreshMenu)
        {
            bool changed;
            lock (Sync)
            {
                changed = _localReady != ready;
                if (changed)
                    _localReady = ready;
            }

            if (!changed && !refreshMenu)
                return;

            if (sendToRemote)
                SendLocalReadyState();
            if (refreshMenu)
                RequestLobbyMenuRefresh();
        }

        internal static void SendLocalReadyState()
        {
            var net = NetRef;
            if (net == null || !net.IsAlive || net.id <= 0)
                return;

            var ready = ReadSessionSnapshot().LocalReady;

            try
            {
                net.SendReady(ready);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send ready state: {Message}", ex.Message);
            }
        }

        internal static void ReceiveRemoteReady(int userId, bool ready)
        {
            if (userId <= 0)
                return;

            RequestLobbyMenuRefresh();
        }

        internal static void RequestLobbyMenuRefresh()
        {
            MainThreadPump.EnqueueMainThreadCoalesced("ui:lobby-ready-refresh", () =>
            {
                var state = ReadSessionSnapshot();
                if (state.InActualRun || state.AutoStartTriggered)
                    return;

                var screen = GetTitleScreen();
                if (screen == null)
                    return;

                if (_inHostStatusMenu)
                {
                    ShowHostStatusMenu(screen);
                    return;
                }

                if (_inClientWaitingMenu)
                    ShowClientWaitingMenu(screen);
            });
        }

        internal static void RefreshPlayersDisplayFromNetwork()
        {
            _playersDisplay.Clear();

            var net = NetRef;
            var state = ReadSessionSnapshot();
            var localId = net?.id ?? (state.Role == NetRole.Host ? 1 : 0);
            var localName = string.IsNullOrWhiteSpace(state.Username) ? "Guest" : state.Username.Trim();
            if (state.Role != NetRole.None)
            {
                _playersDisplay.Add(new PlayerInfo
                {
                    UserId = localId,
                    Name = localName,
                    Ready = state.LocalReady,
                    IsHost = state.Role == NetRole.Host
                });
            }

            if (net == null || !net.IsAlive)
                return;

            if (!net.TryGetRemoteUserSnapshots(out var snapshots))
                return;

            try
            {
                for (var i = 0; i < snapshots.Count; i++)
                {
                    var remote = snapshots[i];
                    if (remote.Id <= 0)
                        continue;

                    var ready = false;
                    net.TryGetRemoteReady(remote.Id, out ready);

                    var name = _ConnectionUI.GetPlayerName(localId, remote.Id, remote.Username ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(name) &&
                        remote.Id == 1 &&
                        !string.IsNullOrWhiteSpace(state.RemoteUsername))
                    {
                        name = state.RemoteUsername.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(name))
                        name = $"Player {remote.Id}";

                    _playersDisplay.Add(new PlayerInfo
                    {
                        UserId = remote.Id,
                        Name = name,
                        Ready = ready,
                        IsHost = remote.Id == 1
                    });
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(snapshots);
            }

            _playersDisplay.Sort(static (left, right) =>
            {
                if (left.IsHost != right.IsHost)
                    return left.IsHost ? -1 : 1;
                return left.UserId.CompareTo(right.UserId);
            });
        }

        internal static string GetReadyButtonLabel()
        {
            return ReadSessionSnapshot().LocalReady ? "Ready: On" : "Ready: Off";
        }

        internal static string GetPendingLaunchSummaryLabel(TitleScreen? screen)
        {
            var state = ReadSessionSnapshot();
            var action = state.PendingLaunchAction;
            var custom = state.PendingLaunchCustom;

            if (action == PendingLaunchAction.LoadSave)
            {
                var continueCustom = ResolveCurrentSaveIsCustom(screen);
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Continue ({GetModeLabel(continueCustom)})");
            }

            return GetModeLabel(custom);
        }
    }
}

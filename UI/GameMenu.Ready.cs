using System.Globalization;
using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;

namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private static void ResetLobbyReadyState()
        {
            lock (Sync)
            {
                ResetLobbyReadyStateLocked();
            }
        }

        private static void ResetLobbyReadyStateLocked()
        {
            _localReady = false;
            _playersDisplay.Clear();
        }

        private static void ResetLobbyLaunchStateLocked()
        {
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

        private static void PrepareLobbyForNewNetworkSession(bool clearRemoteCoopState = false)
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

        private static void ToggleLocalReadyFromMenu(TitleScreen screen)
        {
            SetLocalReady(!_localReady, sendToRemote: true, refreshMenu: true);
            screen.ShouldAutoHideConnectionUI(true);
        }

        private static void SetLocalReady(bool ready, bool sendToRemote, bool refreshMenu)
        {
            if (_localReady == ready && !refreshMenu)
                return;

            _localReady = ready;
            if (sendToRemote)
                SendLocalReadyState();
            if (refreshMenu)
                RequestLobbyMenuRefresh();
        }

        private static void SendLocalReadyState()
        {
            var net = NetRef;
            if (net == null || !net.IsAlive || net.id <= 0)
                return;

            try
            {
                net.SendReady(_localReady);
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

        private static void RequestLobbyMenuRefresh()
        {
            EnqueueMainThreadCoalesced("ui:lobby-ready-refresh", () =>
            {
                lock (Sync)
                {
                    if (_inActualRun || _autoStartTriggered)
                        return;
                }

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

        private static void RefreshPlayersDisplayFromNetwork()
        {
            _playersDisplay.Clear();

            var net = NetRef;
            var localId = net?.id ?? (_role == NetRole.Host ? 1 : 0);
            var localName = string.IsNullOrWhiteSpace(_username) ? "Guest" : _username.Trim();
            if (_role != NetRole.None)
            {
                _playersDisplay.Add(new PlayerInfo
                {
                    UserId = localId,
                    Name = localName,
                    Ready = _localReady,
                    IsHost = _role == NetRole.Host
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
                        !string.IsNullOrWhiteSpace(_remoteUsername))
                    {
                        name = _remoteUsername.Trim();
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

        private static string GetReadyButtonLabel()
        {
            return _localReady ? "Ready: On" : "Ready: Off";
        }

        private static string GetPendingLaunchSummaryLabel(TitleScreen? screen)
        {
            PendingLaunchAction action;
            bool custom;
            lock (Sync)
            {
                action = _pendingLaunchAction;
                custom = _pendingLaunchCustom;
            }

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

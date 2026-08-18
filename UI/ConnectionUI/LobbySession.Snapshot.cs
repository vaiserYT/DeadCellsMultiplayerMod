namespace DeadCellsMultiplayerMod
{
    internal static partial class LobbySession
    {
        internal readonly record struct LobbySessionSnapshot(
            NetRole Role,
            bool InActualRun,
            bool AutoStartTriggered,
            bool LocalReady,
            NetRole MenuSelection,
            bool InHostStatusMenu,
            bool InClientWaitingMenu,
            PendingLaunchAction PendingLaunchAction,
            bool PendingLaunchCustom,
            bool PendingLaunchStreamEnabled,
            bool SteamJoinLobbyResolvePending,
            string Username,
            string RemoteUsername);

        internal static LobbySessionSnapshot ReadSessionSnapshot()
        {
            lock (Sync)
            {
                var launchIntent = RunLaunchCoordinator.GetPendingLaunchIntent();
                return new LobbySessionSnapshot(
                    RunLaunchCoordinator.CurrentRole,
                    _inActualRun,
                    _autoStartTriggered,
                    _localReady,
                    _menuSelection,
                    _inHostStatusMenu,
                    _inClientWaitingMenu,
                    launchIntent.Action,
                    launchIntent.Custom,
                    launchIntent.StreamEnabled,
                    _steamJoinLobbyResolvePending,
                    _username ?? string.Empty,
                    _remoteUsername ?? string.Empty);
            }
        }
    }
}

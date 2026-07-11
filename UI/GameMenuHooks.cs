using dc.pr;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using ModCore.Modules;
using DeadCellsMultiplayerMod.UI;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;

namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private static void InitializeMenuUiHooks()
        {
            if (_menuHooksAttached) return;

            try
            {
                LoadConfig();
                InitializeMultiplayerSaveHooks();
                Hook_TitleScreen.mainMenu += MainMenuHook;
                _menuHooksAttached = true;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] TitleScreen hooks failed: {Message}", ex.Message);
            }
        }

        private static void MainMenuHook(Hook_TitleScreen.orig_mainMenu orig, TitleScreen self)
        {
            ModEntry.PumpSteamCallbacksForOverlay();
            if (!_addMenuHookRegistered)
            {
                Hook_TitleScreen.addMenu += AddMenuHook;
                _addMenuHookRegistered = true;
            }
            TryDisconnectWhenReturningToMainMenu();
            StoreTitleScreen(self);
            ConnectionUI.EnsureCreated(self);
            _mainMenuButtonAdded = false;
            orig(self);

            if (!_mainMenuButtonAdded)
            {
                var label = GetText.Instance.GetString("Play multiplayer");
                var help = GetText.Instance.GetString("Host or join a multiplayer session");
                AddMenuButton(self, label, () => ShowMultiplayerMenu(self), help);
                _mainMenuButtonAdded = true;
            }
            ProcessPendingOverlayJoinRequest(self);
        }

        private static void ProcessPendingOverlayJoinRequest(TitleScreen screen)
        {
            if (_pendingOverlayJoinLobbyId is not { } lobbyId)
                return;
            _pendingOverlayJoinLobbyId = null;
            _log?.Information("[NetMod][Steam] Processing queued overlay join request (lobbyId={LobbyId})", lobbyId);
            HandleSteamOverlayJoinRequest(lobbyId);
        }

        private static void TryDisconnectWhenReturningToMainMenu()
        {
            if (_role == NetRole.None)
                return;
            if (!_inActualRun)
                return;
            _log?.Information("[NetMod] Main menu opened during run; stopping network");
            StopNetworkFromMenu();
        }

        private static virtual_cb_help_inter_isEnable_t_<bool> AddMenuHook(
            Hook_TitleScreen.orig_addMenu orig,
            TitleScreen self,
            dc.String str,
            HlAction cb,
            dc.String help,
            bool? isEnable,
            Ref<int> color)
        {
            ModEntry.PumpSteamCallbacksForOverlay();
            GameMenu.ProcessMainThreadQueue();

            if (!_addingMultiplayerButton && !_mainMenuButtonAdded)
            {
                var label = str?.ToString() ?? string.Empty;
                var playLabel = GetText.Instance.GetString("Play");
                if (label.Equals(playLabel, StringComparison.OrdinalIgnoreCase))
                {
                    var wrappedCb = WrapQuitCallbackIfNeeded(str, cb);
                    var result = orig(self, str, wrappedCb ?? cb, help, isEnable, color);

                    _addingMultiplayerButton = true;
                    try
                    {
                        var mpLabel = GetText.Instance.GetString("Play multiplayer");
                        var mpHelp = GetText.Instance.GetString("Host or join a multiplayer session");
                        AddMenuButton(self, mpLabel, () => ShowMultiplayerMenu(self), mpHelp);
                        _mainMenuButtonAdded = true;
                    }
                    finally { _addingMultiplayerButton = false; }

                    return result;
                }
            }

            var wrapped = WrapQuitCallbackIfNeeded(str, cb);
            return orig(self, str, wrapped ?? cb, help, isEnable, color);
        }

        private static HlAction? WrapQuitCallbackIfNeeded(dc.String? label, HlAction? callback)
        {
            if (callback == null)
                return null;
            if (_role == NetRole.None)
                return callback;

            var text = label?.ToString() ?? string.Empty;
            if (!IsQuitMenuLabel(text))
                return callback;

            return new HlAction(() =>
            {
                try { StopNetworkFromMenu(); }
                catch (Exception ex) { _log?.Warning("[NetMod] Quit cleanup failed: {Message}", ex.Message); }
                try { callback(); }
                catch (Exception ex) { _log?.Warning("[NetMod] Quit callback failed: {Message}", ex.Message); }
            });
        }

        private static bool IsQuitMenuLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;
            var text = label.Trim();
            if (text.IndexOf("quit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("выйт", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("выход", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            try
            {
                var localizedQuit = GetText.Instance.GetString("Quitter le jeu");
                if (!string.IsNullOrWhiteSpace(localizedQuit) &&
                    string.Equals(text, localizedQuit, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            return false;
        }

    }
}

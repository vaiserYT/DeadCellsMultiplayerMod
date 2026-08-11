using System.Reflection;
using dc.pr;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using ModCore.Modules;
using DeadCellsMultiplayerMod.UI;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;

namespace DeadCellsMultiplayerMod
{
    internal static partial class LobbySession
    {
        /// <summary>
        /// Accent colour for the main-menu "Play multiplayer" entry, and for that entry only.
        /// </summary>
        /// <remarks>
        /// 0x59D5FF is already used by this mod's in-game UI (LevelExitSync's circle marker), so it
        /// sits in the Dead Cells palette and stays legible against the dark title background in both
        /// the dimmed unselected state and the brightened selected state. It is applied through
        /// <see cref="ApplyMultiplayerMenuAccent"/> and the opt-in colour argument of AddMenuButton
        /// rather than by changing that helper's default, because the helper also builds Host game,
        /// Join game, Ready, Disconnect, OK and Back.
        /// </remarks>
        internal const int MultiplayerMenuAccentColor = 0x59D5FF;

        internal static void InitializeMenuUiHooks()
        {
            if (_menuHooksAttached) return;

            try
            {
                LoadConfig();
                MultiplayerSaves.InitializeMultiplayerSaveHooks();
                InitializeMultiplayerLaunchHooks();
                Hook_TitleScreen.mainMenu += MainMenuHook;
                _menuHooksAttached = true;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] TitleScreen hooks failed: {Message}", ex.Message);
            }
        }

        internal static void MainMenuHook(Hook_TitleScreen.orig_mainMenu orig, TitleScreen self)
        {
            ModEntry.PumpSteamCallbacksForOverlay();
            if (!_addMenuHookRegistered)
            {
                Hook_TitleScreen.addMenu += AddMenuHook;
                _addMenuHookRegistered = true;
            }
            TryDisconnectWhenReturningToMainMenu();
            StoreTitleScreen(self);
            _mainMenuButtonAdded = false;
            // Ensure a live ConnectionUI before any visibility toggle: returning mid-run
            // destroys the previous TitleScreen tree and leaves a stale Instance.root.
            ConnectionUI.EnsureCreated(self);
            ResetOriginalMainMenuUiState();
            ConnectionUI.set_visible = false;
            orig(self);

            EnsureMainMenuMultiplayerButton(self);
            ProcessPendingOverlayJoinRequest(self);
        }

        internal static void ResetOriginalMainMenuUiState()
        {
            ResetHostDisconnectCountdown();
            _inHostStatusMenu = false;
            _inClientWaitingMenu = false;
            _menuSelection = NetRole.None;
            ConnectionUI.set_visible = false;
        }

        internal static void ProcessPendingOverlayJoinRequest(TitleScreen screen)
        {
            if (_pendingOverlayJoinLobbyId is not { } lobbyId)
                return;
            _pendingOverlayJoinLobbyId = null;
            _log?.Information("[NetMod][Steam] Processing queued overlay join request (lobbyId={LobbyId})", lobbyId);
            HandleSteamOverlayJoinRequest(lobbyId);
        }

        internal static void TryDisconnectWhenReturningToMainMenu()
        {
            if (_role == NetRole.None)
                return;
            if (!_inActualRun)
                return;
            _log?.Information("[NetMod] Main menu opened during run; stopping network");
            StopNetworkFromMenu();
        }

        internal static virtual_cb_help_inter_isEnable_t_<bool> AddMenuHook(
            Hook_TitleScreen.orig_addMenu orig,
            TitleScreen self,
            dc.String str,
            HlAction cb,
            dc.String help,
            bool? isEnable,
            Ref<int> color)
        {
            ModEntry.PumpSteamCallbacksForOverlay();
            MainThreadPump.ProcessMainThreadQueue();
            var wrappedCb = WrapQuitCallbackIfNeeded(str, cb);
            var ret = orig(self, str, wrappedCb ?? cb, help, isEnable, color);

            try
            {
                if (_suppressAutoButton) return ret;
                if (_mainMenuButtonAdded) return ret;
                if (!self.isMainMenu) return ret;

                var items = TitleScreenReflection.GetMemberValue(self, "menuItems", true);
                if (items == null)
                    return ret;
                var count = TitleScreenReflection.GetArrayLength(items);
                if (count == 1)
                {
                    int accent = MultiplayerMenuAccentColor;
                    var label = GetText.Instance.GetString("Play multiplayer").AsHaxeString();
                    var helpStr = GetText.Instance.GetString("Host or join a multiplayer session").AsHaxeString();
                    var colorHl = Ref<int>.From(ref accent);
                    var cbHl = new HlAction(() => ShowMultiplayerMenu(self));
                    orig(self, label, cbHl, helpStr, null, colorHl);
                    _mainMenuButtonAdded = true;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] addMenu hook failed: {Message}", ex.Message);
            }

            return ret;
        }

        internal static HlAction? WrapQuitCallbackIfNeeded(dc.String label, HlAction? callback)
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

        internal static bool IsQuitMenuLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;
            var text = label.Trim();
            if (text.IndexOf("quit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("exit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("РІС‹Р№С‚", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("РІС‹С…РѕРґ", StringComparison.OrdinalIgnoreCase) >= 0) return true;
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

        internal static void EnsureMainMenuMultiplayerButton(TitleScreen screen)
        {
            try
            {
                var arr = TitleScreenReflection.GetMemberValue(screen, "menuItems", true);
                var playMultiplayer = GetText.Instance.GetString("Play multiplayer");
                var playHelp = GetText.Instance.GetString("Host or join a multiplayer session");
                var playLabel = GetText.Instance.GetString("Play");
                var existingIdx = TitleScreenReflection.FindMenuIndexByLabel(arr, playMultiplayer);
                if (existingIdx < 0)
                {
                    TryAddMenuButton(
                        screen,
                        playMultiplayer,
                        () => ShowMultiplayerMenu(screen),
                        playHelp,
                        MultiplayerMenuAccentColor);
                    arr = TitleScreenReflection.GetMemberValue(screen, "menuItems", true);
                }
                _mainMenuButtonAdded = true;
                MoveButtonAfterPlay(arr, playMultiplayer, playLabel);

                // Re-assert last, after any reordering, and regardless of which path inserted the
                // entry. This is what makes the accent survive a menu rebuild, a return from
                // gameplay and a language change, without needing every insertion site to remember.
                ApplyMultiplayerMenuAccent(arr, playMultiplayer);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to ensure main menu button order: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Applies <see cref="MultiplayerMenuAccentColor"/> to the live "Play multiplayer" menu item.
        /// </summary>
        /// <remarks>
        /// The entry can be created by either <c>AddMenuHook</c> or the fallback in
        /// <see cref="EnsureMainMenuMultiplayerButton"/>, and the TitleScreen menu is rebuilt every
        /// time the main menu is shown. Colouring the existing item makes the result independent of
        /// which path won the race. Lookup is by the localized label, so exactly one entry is
        /// touched and no unrelated menu item changes colour.
        /// </remarks>
        internal static void ApplyMultiplayerMenuAccent(object? menuItemsArray, string playMultiplayerLabel)
        {
            try
            {
                var index = TitleScreenReflection.FindMenuIndexByLabel(menuItemsArray, playMultiplayerLabel);
                if (index < 0)
                    return;

                var item = TitleScreenReflection.GetMenuItemAt(menuItemsArray, index);
                // menuItem.t is dc.ui.Text, which inherits dc.h2d.Text.textColor (settable Int32).
                var text = TitleScreenReflection.GetMemberValue(item, "t", true);
                if (text == null)
                    return;

                TitleScreenReflection.TrySetMember(text, "textColor", MultiplayerMenuAccentColor);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply multiplayer menu accent: {Message}", ex.Message);
            }
        }

        internal static void MoveButtonAfterPlay(object? arrObj, string targetLabel, string anchorLabel)
        {
            if (arrObj == null) return;
            try
            {
                var type = arrObj.GetType();
                var getDyn = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var removeDyn = type.GetMethod("removeDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var insertDyn = type.GetMethod("insertDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getDyn == null || removeDyn == null || insertDyn == null) return;

                int len = TitleScreenReflection.GetArrayLength(arrObj);
                int targetIdx = -1;
                int anchorIdx = -1;
                object? targetObj = null;

                for (int i = 0; i < len; i++)
                {
                    var item = getDyn.Invoke(arrObj, new object[] { i });
                    var label = TitleScreenReflection.GetMenuLabel(item);
                    if (targetIdx < 0 && label.Equals(targetLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIdx = i;
                        targetObj = item;
                    }
                    if (anchorIdx < 0 && label.Equals(anchorLabel, StringComparison.OrdinalIgnoreCase))
                        anchorIdx = i;
                }

                if (targetIdx < 0 || anchorIdx < 0 || targetObj == null) return;
                var desired = anchorIdx + 1;
                if (targetIdx == desired) return;

                removeDyn.Invoke(arrObj, new[] { targetObj });
                insertDyn.Invoke(arrObj, new object[] { desired, targetObj });
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to reposition menu button: {Message}", ex.Message);
            }
        }
    }
}

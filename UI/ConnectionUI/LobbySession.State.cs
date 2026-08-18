using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using ModCore.Modules;
using Serilog;

namespace DeadCellsMultiplayerMod
{
    internal static partial class LobbySession
    {
        internal static readonly object Sync = new();
        internal static ILogger? _log;
        internal static ILogger? Log => _log;
        internal static NetRole _role = NetRole.None;
        internal static bool _inActualRun;
        internal static int? _serverSeed;
        internal static int? _remoteSeed;
        internal static int? _pendingClientRestartSeed;
        internal static string _pendingClientRestartReason = string.Empty;
        internal const int MaxSeed = 999_999;
        public static NetNode? NetRef { get; set; }

        internal static bool _menuHooksAttached;
        internal static bool _addMenuHookRegistered;
        internal static WeakReference<TitleScreen?>? _titleScreenRef;
        internal static string _mpIp = "127.0.0.1";
        internal static int _mpPort = 1234;
        internal static NetRole _menuSelection = NetRole.None;
        internal enum ConnectionTransport
        {
            Lan,
            Steam
        }
        internal static ConnectionTransport _menuTransport = ConnectionTransport.Lan;
        internal static SteamConnect.SteamLobbyVisibility _steamLobbyVisibility =
            SteamConnect.SteamLobbyVisibility.FriendsOnly;
        internal static ulong _steamLobbyId;
        internal static string _steamLobbyCode = string.Empty;
        internal static ulong _steamHostSteamId;
        internal static bool _steamJoinLobbyResolvePending;
        internal static ulong? _pendingOverlayJoinLobbyId;
        internal static bool _steamFriendJoinPageActive;
        internal static bool _steamFriendLobbyRefreshInFlight;
        internal static long _nextSteamFriendLobbyRefreshTicks;
        internal static string _steamFriendLobbySignature = string.Empty;
        internal static List<SteamConnect.FriendLobbyInfo> _steamFriendLobbies = new();
        internal const int SteamFriendLobbyRefreshMs = 2500;
        internal const int ClientConnectMaxAttempts = 3;
        internal static bool _pendingAutoStart;
        internal static bool _autoStartTriggered;
        internal static bool _continueLaunchInProgress;
        internal static DateTime _continueLaunchStartedAt = DateTime.MinValue;
        internal const int ContinueLaunchGuardMs = 6000;
        internal static DateTime _autoStartRetryAt = DateTime.MinValue;
        internal const int DeathRestartCooldownMs = 1000;
        internal static DateTime _deathRestartCooldownUntil = DateTime.MinValue;
        internal const string AutoStartMutexName = "DeadCellsMultiplayerMod.AutoStart";
        internal static bool _mainMenuButtonAdded;
        internal static bool _suppressAutoButton;
        internal static bool _worldExitHandled;
        internal static bool _hostDisconnectCountdownActive;
        internal static WeakReference<dc.pr.Game>? _hostDisconnectCountdownGameRef;
        internal static DateTime _hostDisconnectCountdownUntil = DateTime.MinValue;
        internal static int _lastHostDisconnectCountdown = -1;
        internal const int HostDisconnectCountdownSeconds = 5;
        internal static bool _hostDisconnectSavePending;
        internal static DateTime _hostDisconnectSaveRetryAt = DateTime.MinValue;
        internal static DateTime _hostDisconnectSaveDeadline = DateTime.MinValue;
        internal const int HostDisconnectSaveRetryMs = 500;
        internal const int HostDisconnectSaveMaxSeconds = 10;
        internal static bool _seedArrived;
        internal static string _username = "guest";
        internal static string _remoteUsername = "guest";
        internal static string _playerId = Guid.NewGuid().ToString("N");
        public static string Username => ReadSessionSnapshot().Username;
        public static string RemoteUsername => ReadSessionSnapshot().RemoteUsername;

        internal static bool IsSteamJoinLobbyResolvePending() => ReadSessionSnapshot().SteamJoinLobbyResolvePending;
        internal static bool _localReady;
        internal static List<PlayerInfo> _playersDisplay = new();
        internal static bool _inHostStatusMenu;
        internal static bool _inClientWaitingMenu;
        internal static int _menuRebuildDepth;
        internal static bool _genArrived;
        internal static LevelDescSync? _cachedLevelDescSync;
        internal static readonly object TextInputSync = new();
        internal static WeakReference<TextInput?>? _activeTextInputRef;
        internal static bool _activeTextInputNoSpaces;
        internal const int KeyCtrl = 17;
        internal const int KeyLCtrl = 162;
        internal const int KeyRCtrl = 163;
        internal const int KeyC = 67;
        internal const int KeyV = 86;
        internal const int KeySpace = 32;
        internal const int KeyEsc = 27;
        internal const uint CfUnicodeText = 13;
        internal const uint GmemMoveable = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);
    }
}

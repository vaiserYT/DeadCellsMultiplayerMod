using System.Diagnostics;
using System.Globalization;
using dc;
using dc.en;
using dc.en.inter;
using dc.en.inter.door;
using dc.h2d;
using dc.pr;
using dc.ui;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.MultiplayerModUI.LevelExit;

public class LevelExitSync :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnHeroUpdate
{
    private sealed class DoorVisual
    {
        public Entity? Door;
        public string DoorKey = string.Empty;
        public Graphics? Circle;
        public dc.ui.Text? Counter;
        public bool? LastActive;
        public int LastTextWidth = -1;
        public int LastReadyCount = -1;
        public int LastExpectedCount = -1;
    }

    private sealed class PlayerExitState
    {
        public int UserId;
        public string DoorKey = string.Empty;
        public int DoorCx;
        public int DoorCy;
        public bool Pressed;
        public bool InsideCircle;
        public bool IsOutOfGame;
        public bool IsOnScreen;
        public long LastTick;
    }

    private const double ExitCircleRadiusPx = 84.0;
    private const double ExitCounterYOffsetPx = 100.0;
    private const double ExitStateResendSeconds = 0.20;
    private const double CounterScale = 1.10;
    private const int CounterColor = 0xFFFFFF;
    private const int MarkerColor = 0x68AD3D;
    private const int CircleColor = 0x59D5FF;
    private const double CircleAlphaIdle = 0.10;
    private const double CircleAlphaActive = 0.22;
    private const int PointerFxSuppressionKey = 188743680;

    private readonly ILogger _log;

    private readonly Dictionary<string, DoorVisual> _doorVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<int, PlayerExitState> _playerStates = new();
    private readonly HashSet<int> _activePlayerIds = new();
    private readonly HashSet<int> _activePlayerScratchIds = new();
    private readonly Dictionary<string, int> _readyPlayerCounts = new(StringComparer.Ordinal);
    private readonly List<int> _stalePlayerIds = new();
    private readonly List<string> _staleDoorVisualKeys = new();
    private readonly Dictionary<string, Entity> _exitTargetsByDoorKey = new(StringComparer.Ordinal);
    private Pointer? _exitPointer;
    private string _exitPointerDoorKey = string.Empty;

    private Level? _lastLevel;
    private string _localDoorKey = string.Empty;
    private int _localDoorCx;
    private int _localDoorCy;
    private bool _localPressed;
    private bool _localInsideCircle;
    private bool _localDoorOutOfGame;
    private bool _localDoorOnScreen;
    private bool _hasLastSentState;
    private int _lastSentDoorCx;
    private int _lastSentDoorCy;
    private byte _lastSentStateFlags;
    private long _lastLocalStateSendTick;
    private bool _suppressDoorActivateHook;
    private string _transitionDoorKey = string.Empty;
    private bool _timerPausedByExit;
    private string _exitFailsafeDoorKey = string.Empty;
    private long _exitFailsafeStartedTicks;
    private const double ExitFailsafeTeleportSeconds = 8.0;

    /// <summary>Exit/portal/boss-door entities only — avoids scanning <c>level.entities</c> every hero frame.</summary>
    private readonly List<Entity?> _exitTargetCandidates = new();

    private Level? _exitCandidatesLevel;
    private int _exitTargetCandidatesVersion;
    private Level? _nearestExitCacheLevel;
    private bool _nearestExitCacheHasValue;
    private double _nearestExitCacheHeroX = double.NaN;
    private double _nearestExitCacheHeroY = double.NaN;
    private bool _nearestExitCacheInsideCircle;
    private int _nearestExitCacheCandidatesVersion = -1;
    private Entity? _nearestExitCacheTarget;
    private bool _readyStateCacheDirty = true;
    private bool _watchedDoorCacheDirty = true;
    private bool _doorVisualRefreshDirty = true;
    private bool _exitPointerDirty = true;
    private int _cachedExpectedPlayerCount = 1;
    private string _cachedWatchedDoorKey = string.Empty;
    private int _cachedDownedSignature;
    private bool _hasCachedDownedSignature;

    private const double NearestExitCacheReuseDistancePx = 18.0;
    private const double NearestExitCacheReuseDistanceSq = NearestExitCacheReuseDistancePx * NearestExitCacheReuseDistancePx;

    public LevelExitSync(ModEntry entry)
    {
        _log = entry.Logger;
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        entry.Logger.Information("\x1b[32m[[ModEntry.LevelExitSync] Initializing LevelExitSync...]\x1b[0m ");
        Hook_Exit.postUpdate += Hook_Exit_postUpdate;
        Hook_Exit.onActivate += Hook_Exit_onActivate;
        Hook_Portal.onActivate += Hook_Portal_onActivate;
        Hook_BossRushDoor.onActivate += Hook_BossRushDoor_onActivate;
        Hook_Level.registerEntity += Hook_Level_registerEntity;
        Hook_Level.unregisterEntity += Hook_Level_unregisterEntity;
        Hook_Level.onDispose += Hook_Level_onDispose;
    }

    private void Hook_Exit_postUpdate(Hook_Exit.orig_postUpdate orig, Exit self)
    {
        orig(self);
        EnsureDoorVisual(self);
    }

    private void Hook_Exit_onActivate(Hook_Exit.orig_onActivate orig, Exit self, Hero by, bool inf)
    {
        HandleExitTargetActivate(self, by, () => orig(self, by, inf), null);
    }

    private void Hook_Portal_onActivate(Hook_Portal.orig_onActivate orig, Portal self, Hero by, bool lp)
    {
        HandleExitTargetActivate(
            self,
            by,
            () => orig(self, by, lp),
            target => SafeRead(() => target.visible, false));
    }

    private void Hook_BossRushDoor_onActivate(Hook_BossRushDoor.orig_onActivate orig, BossRushDoor self, Hero by, bool cine)
    {
        HandleExitTargetActivate(
            self,
            by,
            () => orig(self, by, cine),
            target => !SafeRead(() => target.locked, true));
    }

    private void Hook_Level_registerEntity(Hook_Level.orig_registerEntity orig, Level self, Entity clid)
    {
        orig(self, clid);
        TryTrackExitTargetCandidate(self, clid);
    }

    private void Hook_Level_unregisterEntity(Hook_Level.orig_unregisterEntity orig, Level self, Entity clid)
    {
        TryUntrackExitTargetCandidate(self, clid);
        orig(self, clid);
    }

    private void Hook_Level_onDispose(Hook_Level.orig_onDispose orig, Level self)
    {
        var localLevel = ModEntry.me?._level;
        if (localLevel != null && ReferenceEquals(localLevel, self))
            ModEntry.PrepareRemoteKingsForLevelTransition("level-onDispose-current");

        if (ReferenceEquals(_exitCandidatesLevel, self))
        {
            _exitCandidatesLevel = null;
            _exitTargetCandidates.Clear();
            _exitTargetsByDoorKey.Clear();
            InvalidateNearestExitCache();
            _exitPointerDirty = true;
        }

        orig(self);
    }

    private void HandleExitTargetActivate<T>(T target, Hero by, Action origActivate, Func<T, bool>? canCoordinate)
        where T : Entity
    {
        if (_suppressDoorActivateHook)
        {
            origActivate();
            return;
        }

        if (target == null)
        {
            origActivate();
            return;
        }

        if (canCoordinate != null && !canCoordinate(target))
        {
            origActivate();
            return;
        }

        var localHero = ModEntry.me;
        if (by == null || localHero == null || !ReferenceEquals(by, localHero))
        {
            origActivate();
            return;
        }

        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive || net.id <= 0)
        {
            origActivate();
            return;
        }

        if (!IsEntityInsideExitCircle(localHero, target))
        {
            origActivate();
            return;
        }

        var targetDoorKey = BuildDoorKey(target.cx, target.cy);
        _localDoorKey = targetDoorKey;
        _localDoorCx = target.cx;
        _localDoorCy = target.cy;
        _localDoorOutOfGame = SafeRead(() => target.isOutOfGame, false);
        _localDoorOnScreen = SafeRead(() => target.isOnScreen, false);
        _localInsideCircle = true;
        var wasReadyHere = _localPressed &&
                           string.Equals(_localDoorKey, targetDoorKey, StringComparison.Ordinal) &&
                           _localInsideCircle;
        _localPressed = true;

        UpdateLocalPlayerState(net, forceSend: true);
        if (!wasReadyHere)
            PushReachedExitMessage(net.id, target, net);

        if (AreAllPlayersReadyForDoor(_localDoorKey, net))
        {
            ApplyLocalTimerPause(false);
            TriggerExitTransition(target, localHero, origActivate);
            return;
        }

        ApplyLocalTimerPause(true);
    }

    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        var hero = ModEntry.me;
        var net = GameMenu.NetRef;
        if (hero == null || net == null || !net.IsAlive || net.id <= 0)
        {
            if (_lastLevel != null || _doorVisuals.Count > 0 || _playerStates.Count > 0 || _exitPointer != null)
                ResetLevelState(null);
            ApplyLocalTimerPause(false);
            return;
        }

        var currentLevel = hero._level;
        if (!ReferenceEquals(_lastLevel, currentLevel))
            ResetLevelState(currentLevel);

        if (ConsumeIncomingExitReadyStates(net))
            MarkExitUiStateDirty();

        if (RefreshActivePlayers(net))
            MarkExitUiStateDirty();

        if (PrunePlayerStates(net.id))
            MarkExitUiStateDirty();

        PruneDoorVisuals(currentLevel);

        if (RefreshDownedSignature(net.id))
            MarkExitUiStateDirty();

        var nearestTarget = FindNearestExitTarget(hero, currentLevel, out var insideCircle);
        if (ApplyNearestExitSelection(nearestTarget, insideCircle))
            MarkExitUiStateDirty();
        if (nearestTarget != null)
            EnsureDoorVisual(nearestTarget);

        if (_localPressed && (!_localInsideCircle || string.IsNullOrEmpty(_localDoorKey)))
        {
            _localPressed = false;
            _transitionDoorKey = string.Empty;
            MarkExitUiStateDirty();
        }

        UpdateLocalPlayerState(net, forceSend: false);
        ApplyLocalTimerPause(_localPressed && _localInsideCircle);
        RefreshDoorVisuals(net);
        TryExitTeleportFailsafe(hero, currentLevel, net);

        if (_localPressed &&
            _localInsideCircle &&
            nearestTarget != null &&
            !string.IsNullOrEmpty(_localDoorKey) &&
            !string.Equals(_transitionDoorKey, _localDoorKey, StringComparison.Ordinal) &&
            AreAllPlayersReadyForDoor(_localDoorKey, net))
        {
            TriggerExitTransition(nearestTarget, hero, null);
        }

        if (ModEntry.IsLocalPlayerDowned())
        {
            var autoDoorKey = ResolveAutoFollowDoorKey(net);
            if (!string.IsNullOrWhiteSpace(autoDoorKey) &&
                !string.Equals(_transitionDoorKey, autoDoorKey, StringComparison.Ordinal))
            {
                var autoTarget = FindExitTargetByDoorKey(currentLevel, autoDoorKey);
                if (autoTarget != null)
                    TriggerExitTransition(autoTarget, hero, null);
            }
        }

        UpdateExitPointer(net);
    }


    private void TryExitTeleportFailsafe(Hero hero, Level? level, NetNode net)
    {
        if (hero == null || level == null || net == null || !net.IsAlive)
            return;
        if (_localPressed && _localInsideCircle)
        {
            _exitFailsafeDoorKey = string.Empty;
            _exitFailsafeStartedTicks = 0;
            return;
        }

        string candidateKey = string.Empty;
        foreach (var state in _playerStates.Values)
        {
            if (state == null || state.UserId <= 0)
                continue;
            if (!state.Pressed || !state.InsideCircle || string.IsNullOrWhiteSpace(state.DoorKey))
                continue;
            if (IsPlayerDownedForExit(state.UserId, net.id))
                continue;
            candidateKey = state.DoorKey;
            break;
        }

        if (string.IsNullOrWhiteSpace(candidateKey))
        {
            _exitFailsafeDoorKey = string.Empty;
            _exitFailsafeStartedTicks = 0;
            return;
        }

        var target = FindExitTargetByDoorKey(level, candidateKey);
        if (target == null)
            return;

        if (!string.Equals(_exitFailsafeDoorKey, candidateKey, StringComparison.Ordinal))
        {
            _exitFailsafeDoorKey = candidateKey;
            _exitFailsafeStartedTicks = Stopwatch.GetTimestamp();
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_exitFailsafeStartedTicks).TotalSeconds;
        if (elapsed < ExitFailsafeTeleportSeconds)
            return;

        try
        {
            var x = GetEntityX(target);
            var y = GetEntityY(target);
            hero.cancelVelocities();
            hero.setPosPixel(x, y);
            _localDoorKey = candidateKey;
            _localDoorCx = target.cx;
            _localDoorCy = target.cy;
            _localInsideCircle = true;
            _localPressed = true;
            UpdateLocalPlayerState(net, forceSend: true);
            MultiplayerUI.PushSystemMessage(FormatLocalized("Exit failsafe: pulled you to {0}", ResolveExitDestinationName(target)), 6.0, 1.5);
            _exitFailsafeStartedTicks = Stopwatch.GetTimestamp();
            MarkExitUiStateDirty();
        }
        catch (Exception ex)
        {
            _log.Warning("[ExitSync] Exit teleport failsafe failed: {Message}", ex.Message);
        }
    }

    private void TriggerExitTransition(Entity target, Hero hero, Action? origActivate)
    {
        if (target == null || hero == null)
            return;

        if (ModEntry.IsLocalPlayerDowned())
            ModEntry.ApplyLocalDownedExitPenaltyIfNeeded();

        _transitionDoorKey = BuildDoorKey(target.cx, target.cy);
        ModEntry.PrepareRemoteKingsForLevelTransition(
            string.Create(
                CultureInfo.InvariantCulture,
                $"exit-activate:{target.GetType().Name}:{_transitionDoorKey}"));
        _suppressDoorActivateHook = true;
        try
        {
            if (origActivate != null)
                origActivate();
            else
                InvokeExitTargetActivate(target, hero);
        }
        catch (Exception ex)
        {
            _log.Warning("[ExitSync] Failed to trigger level exit transition: {Message}", ex.Message);
            _transitionDoorKey = string.Empty;
        }
        finally
        {
            _suppressDoorActivateHook = false;
        }
    }

    private static void InvokeExitTargetActivate(Entity target, Hero hero)
    {
        if (target is dc.en.Interactive interactive)
            interactive.onActivate(hero, false);
    }

    private bool ConsumeIncomingExitReadyStates(NetNode net)
    {
        if (!net.TryConsumeExitReadyStates(out var states))
            return false;

        try
        {
            var currentLevel = ModEntry.me?._level ?? _lastLevel;
            var localId = net.id;
            var anyChanged = false;
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (state.UserId <= 0 || state.UserId == localId)
                    continue;

                var wasReady = _playerStates.TryGetValue(state.UserId, out var prev) &&
                               prev.Pressed &&
                               prev.InsideCircle &&
                               prev.DoorCx == state.DoorCx &&
                               prev.DoorCy == state.DoorCy;
                var isReady = state.Pressed && state.InsideCircle;

                var trackedState = GetOrCreatePlayerState(state.UserId);
                anyChanged |= ApplyPlayerState(
                    trackedState,
                    BuildDoorKey(state.DoorCx, state.DoorCy),
                    state.DoorCx,
                    state.DoorCy,
                    state.Pressed,
                    state.InsideCircle,
                    state.IsOutOfGame,
                    state.IsOnScreen);
                trackedState.LastTick = Stopwatch.GetTimestamp();

                if (!wasReady && isReady)
                {
                    var target = FindExitTargetByCoordinates(currentLevel, state.DoorCx, state.DoorCy);
                    PushReachedExitMessage(state.UserId, target, net);
                }
            }

            return anyChanged;
        }
        finally
        {
            NetNode.ReleaseConsumedList(states);
        }
    }

    private bool RefreshActivePlayers(NetNode net)
    {
        _activePlayerScratchIds.Clear();
        if (net.id > 0)
            _activePlayerScratchIds.Add(net.id);

        net.CopyRemoteUserIdsTo(_activePlayerScratchIds);

        for (int i = 0; i < ModEntry.clientIds.Length; i++)
        {
            var id = ModEntry.clientIds[i];
            if (id > 0)
                _activePlayerScratchIds.Add(id);
        }

        if (_activePlayerIds.SetEquals(_activePlayerScratchIds))
            return false;

        _activePlayerIds.Clear();
        _activePlayerIds.UnionWith(_activePlayerScratchIds);
        return true;
    }

    private bool PrunePlayerStates(int localId)
    {
        if (_playerStates.Count == 0)
            return false;

        _stalePlayerIds.Clear();
        foreach (var pair in _playerStates)
        {
            if (pair.Key == localId)
                continue;
            if (_activePlayerIds.Contains(pair.Key))
                continue;
            _stalePlayerIds.Add(pair.Key);
        }

        if (_stalePlayerIds.Count == 0)
            return false;

        for (int i = 0; i < _stalePlayerIds.Count; i++)
            _playerStates.Remove(_stalePlayerIds[i]);

        return true;
    }

    private void UpdateLocalPlayerState(NetNode net, bool forceSend)
    {
        var localId = net.id;
        if (localId <= 0)
            return;

        var trackedState = GetOrCreatePlayerState(localId);
        var localStateChanged = ApplyPlayerState(
            trackedState,
            _localDoorKey,
            _localDoorCx,
            _localDoorCy,
            _localPressed,
            _localInsideCircle,
            _localDoorOutOfGame,
            _localDoorOnScreen);
        trackedState.LastTick = Stopwatch.GetTimestamp();
        if (localStateChanged)
            MarkExitUiStateDirty();

        var now = Stopwatch.GetTimestamp();
        var resendTicks = (long)(Stopwatch.Frequency * ExitStateResendSeconds);
        var stateFlags = BuildLocalStateFlags();
        var changed = !_hasLastSentState ||
                      _lastSentDoorCx != _localDoorCx ||
                      _lastSentDoorCy != _localDoorCy ||
                      _lastSentStateFlags != stateFlags;
        var timedOut = _lastLocalStateSendTick == 0 || now - _lastLocalStateSendTick >= resendTicks;
        if (!forceSend && !changed && !timedOut)
            return;

        net.SendExitReady(_localDoorCx, _localDoorCy, _localPressed, _localInsideCircle, _localDoorOutOfGame, _localDoorOnScreen);
        _hasLastSentState = true;
        _lastSentDoorCx = _localDoorCx;
        _lastSentDoorCy = _localDoorCy;
        _lastSentStateFlags = stateFlags;
        _lastLocalStateSendTick = now;
    }

    private bool AreAllPlayersReadyForDoor(string doorKey, NetNode net)
    {
        if (string.IsNullOrWhiteSpace(doorKey))
            return false;

        EnsureReadyStateCache(net);
        var expected = _cachedExpectedPlayerCount;
        if (expected <= 1)
            return true;

        _readyPlayerCounts.TryGetValue(doorKey, out var ready);
        return ready >= expected;
    }

    private int ComputeExpectedPlayerCount(NetNode net)
    {
        var localId = net.id;
        var expected = 0;

        foreach (var userId in _activePlayerIds)
        {
            if (userId <= 0)
                continue;
            if (IsPlayerDownedForExit(userId, localId))
                continue;
            expected++;
        }

        var aliveStates = 0;
        foreach (var state in _playerStates.Values)
        {
            if (state.UserId <= 0)
                continue;
            if (IsPlayerDownedForExit(state.UserId, localId))
                continue;
            aliveStates++;
        }
        if (aliveStates > expected)
            expected = aliveStates;

        if (net.IsHost)
        {
            var hostExpected = 1 + NetNode.ConnectedClientCount;
            if (localId > 0 && IsPlayerDownedForExit(localId, localId))
                hostExpected--;

            foreach (var userId in _activePlayerIds)
            {
                if (userId <= 0 || userId == localId)
                    continue;
                if (IsPlayerDownedForExit(userId, localId))
                    hostExpected--;
            }

            if (hostExpected < 0)
                hostExpected = 0;
            if (hostExpected > expected)
                expected = hostExpected;
        }

        if (expected <= 0 && localId > 0 && !IsPlayerDownedForExit(localId, localId))
            expected = 1;

        return System.Math.Max(1, expected);
    }

    private string ResolveAutoFollowDoorKey(NetNode net)
    {
        EnsureReadyStateCache(net);
        foreach (var state in _playerStates.Values)
        {
            if (state.UserId <= 0)
                continue;
            if (IsPlayerDownedForExit(state.UserId, net.id))
                continue;
            if (!state.Pressed || !state.InsideCircle)
                continue;
            if (string.IsNullOrWhiteSpace(state.DoorKey))
                continue;
            if (AreAllPlayersReadyForDoor(state.DoorKey, net))
                return state.DoorKey;
        }

        return string.Empty;
    }

    private static bool IsPlayerDownedForExit(int userId, int localId)
    {
        if (userId <= 0)
            return false;

        if (localId > 0 && userId == localId)
            return ModEntry.IsLocalPlayerDowned();

        return ModEntry.IsRemotePlayerDowned(userId);
    }

    private void PushReachedExitMessage(int userId, Entity? target, NetNode net)
    {
        var playerName = ResolveUserDisplayName(userId, net);
        var destination = ResolveExitDestinationName(target);
        MultiplayerUI.PushSystemMessage(FormatLocalized("{0} reached the exit to {1}", playerName, destination));
    }

    private static string ResolveExitDestinationName(Entity? target)
    {
        if (target == null)
            return Localize("next area");

        if (target is Exit exit)
        {
            var byFunc = SafeRead(() => exit.getDestName()?.ToString() ?? string.Empty, string.Empty);
            if (!string.IsNullOrWhiteSpace(byFunc))
                return byFunc.Trim();

            var byField = SafeRead(() => exit.destName?.ToString() ?? string.Empty, string.Empty);
            if (!string.IsNullOrWhiteSpace(byField))
                return byField.Trim();

            var byLevel = SafeRead(() => exit.destLevel?.ToString() ?? string.Empty, string.Empty);
            if (!string.IsNullOrWhiteSpace(byLevel))
                return byLevel.Trim();
        }

        if (target is Portal portal)
        {
            var mapId = SafeRead(() => portal.destMap?.id?.ToString() ?? string.Empty, string.Empty);
            if (!string.IsNullOrWhiteSpace(mapId))
                return mapId.Trim();
        }

        if (target is BossRushDoor bossDoor)
        {
            var type = SafeRead(() => bossDoor.bossRushType?.ToString() ?? string.Empty, string.Empty);
            if (!string.IsNullOrWhiteSpace(type))
                return type.Trim();
            return Localize("Boss Rush");
        }

        return Localize("next area");
    }

    private static string ResolveUserDisplayName(int userId, NetNode net)
    {
        if (userId <= 0)
            return Localize("Guest");

        if (net.id > 0 && userId == net.id)
            return string.IsNullOrWhiteSpace(GameMenu.Username) ? Localize("Guest") : GameMenu.Username.Trim();

        if (net.TryGetRemoteUsername(userId, out var username))
        {
            var name = username?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        if (userId == 1 && !string.IsNullOrWhiteSpace(GameMenu.RemoteUsername))
            return GameMenu.RemoteUsername.Trim();

        return FormatLocalized("Player {0}", userId);
    }

    private static string Localize(string value)
    {
        try
        {
            var localized = Lang.Class.t.get(value.AsHaxeString(), null)?.ToString();
            if (!string.IsNullOrWhiteSpace(localized))
                return localized;
        }
        catch
        {
        }

        return value;
    }

    private static string FormatLocalized(string format, params object[] args)
    {
        var localizedFormat = Localize(format);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, localizedFormat, args);
        }
        catch
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }

    private static string BuildDoorKey(int cx, int cy)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{cx}:{cy}");
    }

    private static bool TryParseDoorKey(string key, out int cx, out int cy)
    {
        cx = 0;
        cy = 0;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var sep = key.IndexOf(':');
        if (sep <= 0 || sep >= key.Length - 1)
            return false;

        return int.TryParse(key.AsSpan(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out cx) &&
               int.TryParse(key.AsSpan(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out cy);
    }

    private Entity? FindExitTargetByDoorKey(Level? level, string key)
    {
        if (level == null || string.IsNullOrWhiteSpace(key))
            return null;

        EnsureExitTargetCandidates(level);
        if (_exitTargetsByDoorKey.TryGetValue(key, out var direct))
        {
            if (IsTrackedExitTargetCandidate(level, direct))
                return direct;

            _exitTargetsByDoorKey.Remove(key);
        }

        if (!TryParseDoorKey(key, out var cx, out var cy))
            return null;
        return FindExitTargetByCoordinates(level, cx, cy);
    }

    private Entity? FindExitTargetByCoordinates(Level? level, int cx, int cy)
    {
        if (level == null)
            return null;

        EnsureExitTargetCandidates(level);
        var key = BuildDoorKey(cx, cy);
        if (_exitTargetsByDoorKey.TryGetValue(key, out var direct))
        {
            if (IsTrackedExitTargetCandidate(level, direct))
                return direct;

            _exitTargetsByDoorKey.Remove(key);
        }

        var removedCandidates = 0;
        for (int i = _exitTargetCandidates.Count - 1; i >= 0; i--)
        {
            var entity = _exitTargetCandidates[i];
            if (!IsTrackedExitTargetCandidate(level, entity))
            {
                UnindexExitTargetCandidate(entity);
                _exitTargetCandidates.RemoveAt(i);
                removedCandidates++;
                continue;
            }

            if (entity!.cx == cx && entity.cy == cy)
            {
                _exitTargetsByDoorKey[key] = entity;
                if (removedCandidates > 0)
                    HandleRemovedExitTargetCandidates();
                return entity;
            }
        }

        if (removedCandidates > 0)
            HandleRemovedExitTargetCandidates();

        return null;
    }

    private static bool IsSupportedExitTarget(Entity? entity)
    {
        return entity is Exit || entity is Portal || entity is BossRushDoor;
    }

    private static bool IsAvailableExitTarget(Entity? entity)
    {
        if (!IsSupportedExitTarget(entity))
            return false;
        if (!SafeRead(() => entity!.visible, true))
            return false;
        if (entity is BossRushDoor bossDoor && SafeRead(() => bossDoor.locked, false))
            return false;
        return true;
    }

    private DoorVisual EnsureDoorVisual(Entity target)
    {
        var key = BuildDoorKey(target.cx, target.cy);
        if (_doorVisuals.TryGetValue(key, out var existing))
        {
            existing.Door = target;
            existing.DoorKey = key;
            return existing;
        }

        var visual = new DoorVisual
        {
            Door = target,
            DoorKey = key
        };

        var parent = target.spr;
        if (parent != null)
        {
            try
            {
                visual.Circle = new Graphics(parent);
                DrawDoorCircle(visual.Circle, false);
                visual.Circle.visible = true;
            }
            catch
            {
                visual.Circle = null;
            }

            try
            {
                var net = GameMenu.NetRef;
                if (net != null && net.IsAlive)
                    EnsureReadyStateCache(net);

                var expected = net != null && net.IsAlive ? _cachedExpectedPlayerCount : System.Math.Max(1, _activePlayerIds.Count);
                var initialLabel = BuildCounterLabel(0, expected);
                visual.Counter = Assets.Class.makeText(initialLabel.AsHaxeString(), dc.ui.Text.Class.COLORS.get("WO".AsHaxeString()), false, parent);
                visual.Counter.customScale = CounterScale;
                visual.Counter.onResize();
                visual.Counter.textColor = CounterColor;
                visual.Counter.alpha = 1.0;
                visual.Counter.y = -ExitCounterYOffsetPx;
                visual.Counter.visible = true;
            }
            catch
            {
                visual.Counter = null;
            }
        }

        _doorVisuals[key] = visual;
        _doorVisualRefreshDirty = true;
        return visual;
    }

    private void UpdateDoorVisual(DoorVisual visual)
    {
        if (visual.Door == null)
            return;

        if (visual.Circle != null)
        {
            var isActive = !string.IsNullOrEmpty(_localDoorKey) &&
                           string.Equals(_localDoorKey, visual.DoorKey, StringComparison.Ordinal) &&
                           _localInsideCircle;
            if (!visual.LastActive.HasValue || visual.LastActive.Value != isActive)
            {
                DrawDoorCircle(visual.Circle, isActive);
                visual.LastActive = isActive;
            }
            visual.Circle.visible = true;
        }
    }

    private void RefreshDoorVisuals(NetNode? net)
    {
        if (_doorVisuals.Count == 0)
        {
            _doorVisualRefreshDirty = false;
            return;
        }

        if (!_doorVisualRefreshDirty)
            return;

        if (net != null && net.IsAlive)
            EnsureReadyStateCache(net);

        var expected = net != null && net.IsAlive ? _cachedExpectedPlayerCount : System.Math.Max(1, _activePlayerIds.Count);

        foreach (var visual in _doorVisuals.Values)
        {
            UpdateDoorVisual(visual);
            UpdateDoorCounterVisual(visual, expected);
        }

        _doorVisualRefreshDirty = false;
    }

    private void UpdateDoorCounterVisual(DoorVisual visual, int expected)
    {
        var counter = visual.Counter;
        if (counter == null)
            return;

        _readyPlayerCounts.TryGetValue(visual.DoorKey, out var ready);
        if (visual.LastReadyCount != ready || visual.LastExpectedCount != expected)
        {
            var label = BuildCounterLabel(ready, expected);
            counter.set_text(label.AsHaxeString());
            visual.LastReadyCount = ready;
            visual.LastExpectedCount = expected;
            visual.LastTextWidth = SafeRead(() => counter.textWidth, label.Length * 10);
            var textWidth = visual.LastTextWidth > 0 ? visual.LastTextWidth : label.Length * 10;
            counter.x = -(textWidth * counter.scaleX) * 0.5;
        }

        counter.visible = true;
    }

    private static string BuildCounterLabel(int ready, int expected)
    {
        if (ready < 0)
            ready = 0;
        if (expected < 1)
            expected = 1;
        if (ready > expected)
            ready = expected;
        return string.Create(CultureInfo.InvariantCulture, $"{ready}/{expected}");
    }

    private static void DrawDoorCircle(Graphics circle, bool active)
    {
        if (circle == null)
            return;

        try
        {
            Graphics g = circle;
            g.clear();
            int color = CircleColor;
            double alpha = active ? CircleAlphaActive : CircleAlphaIdle;
            g.beginFill(Ref<int>.From(ref color), Ref<double>.From(ref alpha));
            g.drawCircle(0.0, 0.0, ExitCircleRadiusPx, Ref<int>.Null);
            g.endFill();
        }
        catch
        {
        }
    }

    private void EnsureExitTargetCandidates(Level? level)
    {
        if (level == null || level.entities == null)
        {
            _exitTargetCandidates.Clear();
            _exitTargetsByDoorKey.Clear();
            _exitCandidatesLevel = null;
            InvalidateNearestExitCache();
            return;
        }

        if (ReferenceEquals(_exitCandidatesLevel, level))
            return;

        _exitCandidatesLevel = level;
        _exitTargetCandidates.Clear();
        _exitTargetsByDoorKey.Clear();
        var entities = level.entities;
        for (int i = 0; i < entities.length; i++)
        {
            var e = entities.getDyn(i) as Entity;
            if (IsSupportedExitTarget(e))
            {
                _exitTargetCandidates.Add(e);
                IndexExitTargetCandidate(e);
            }
        }
        _exitTargetCandidatesVersion++;
        InvalidateNearestExitCache();
        _exitPointerDirty = true;
    }

    private void TryTrackExitTargetCandidate(Level? level, Entity? entity)
    {
        if (!ReferenceEquals(_exitCandidatesLevel, level) || !IsSupportedExitTarget(entity))
            return;

        if (_exitTargetCandidates.Contains(entity))
            return;

        _exitTargetCandidates.Add(entity);
        IndexExitTargetCandidate(entity);
        _exitTargetCandidatesVersion++;
        InvalidateNearestExitCache();
        _exitPointerDirty = true;
    }

    private void TryUntrackExitTargetCandidate(Level? level, Entity? entity)
    {
        if (!ReferenceEquals(_exitCandidatesLevel, level) || entity == null)
            return;

        var removed = false;
        for (int i = _exitTargetCandidates.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_exitTargetCandidates[i], entity))
            {
                UnindexExitTargetCandidate(entity);
                _exitTargetCandidates.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
        {
            _exitTargetCandidatesVersion++;
            InvalidateNearestExitCache();
            _exitPointerDirty = true;
        }
    }

    private static bool IsTrackedExitTargetCandidate(Level level, Entity? entity)
    {
        if (!IsSupportedExitTarget(entity))
            return false;
        if (!ReferenceEquals(entity!._level, level))
            return false;
        if (SafeRead(() => entity.destroyed, false))
            return false;
        return true;
    }

    private Entity? FindNearestExitTarget(Hero hero, Level? level, out bool insideCircle)
    {
        insideCircle = false;
        if (hero == null || level == null)
            return null;

        EnsureExitTargetCandidates(level);
        if (TryGetCachedNearestExitTarget(hero, level, out var cachedTarget, out insideCircle))
            return cachedTarget;

        var heroX = GetEntityX(hero);
        var heroY = GetEntityY(hero);

        Entity? best = null;
        var bestDistSq = double.MaxValue;
        var removedCandidates = 0;
        for (int i = _exitTargetCandidates.Count - 1; i >= 0; i--)
        {
            var target = _exitTargetCandidates[i];
            if (!IsTrackedExitTargetCandidate(level, target))
            {
                UnindexExitTargetCandidate(target);
                _exitTargetCandidates.RemoveAt(i);
                removedCandidates++;
                continue;
            }

            if (!IsAvailableExitTarget(target))
                continue;

            var dx = GetEntityX(target!) - heroX;
            var dy = GetEntityY(target!) - heroY;
            var distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = target;
            }
        }

        if (best == null)
        {
            if (removedCandidates > 0)
                HandleRemovedExitTargetCandidates();
            CacheNearestExitTarget(level, hero, null, false);
            return null;
        }

        if (removedCandidates > 0)
            HandleRemovedExitTargetCandidates();
        insideCircle = bestDistSq <= ExitCircleRadiusPx * ExitCircleRadiusPx;
        CacheNearestExitTarget(level, hero, best, insideCircle);
        return best;
    }

    private static bool IsEntityInsideExitCircle(Entity entity, Entity target)
    {
        if (entity == null || target == null)
            return false;

        var dx = GetEntityX(entity) - GetEntityX(target);
        var dy = GetEntityY(entity) - GetEntityY(target);
        var distSq = dx * dx + dy * dy;
        return distSq <= ExitCircleRadiusPx * ExitCircleRadiusPx;
    }

    private void UpdateExitPointer(NetNode net)
    {
        if (_exitPointer != null && SafeRead(() => _exitPointer.destroyed, true))
        {
            _exitPointer = null;
            _exitPointerDoorKey = string.Empty;
            _exitPointerDirty = true;
        }

        if (!_exitPointerDirty && _exitPointer != null)
            return;

        var watchedDoor = ResolveWatchedDoorKey(net);
        if (string.IsNullOrWhiteSpace(watchedDoor))
        {
            ClearExitPointer();
            _exitPointerDirty = false;
            return;
        }

        if (net == null || !net.IsAlive || AreAllPlayersReadyForDoor(watchedDoor, net))
        {
            ClearExitPointer();
            _exitPointerDirty = false;
            return;
        }

        if (_exitPointer != null &&
            !SafeRead(() => _exitPointer.destroyed, true) &&
            string.Equals(_exitPointerDoorKey, watchedDoor, StringComparison.Ordinal))
        {
            _exitPointerDirty = false;
            return;
        }

        var level = ModEntry.me?._level ?? _lastLevel;
        var door = FindExitTargetByDoorKey(level, watchedDoor);
        if (door == null)
        {
            ClearExitPointer();
            _exitPointerDirty = false;
            return;
        }

        if (_exitPointer != null && !SafeRead(() => _exitPointer.destroyed, true))
        {
            _exitPointer.e = door;
            _exitPointerDoorKey = watchedDoor;
            _exitPointerDirty = false;
            return;
        }

        try
        {
            _exitPointer = new Pointer(door, "".AsHaxeString(), 99999.0, MarkerColor);
            _exitPointerDoorKey = watchedDoor;
            PointerFxHelper.SuppressPointerFx(_exitPointer, PointerFxSuppressionKey);
        }
        catch
        {
            _exitPointer = null;
            _exitPointerDoorKey = string.Empty;
        }
        finally
        {
            _exitPointerDirty = false;
        }
    }

    private string ResolveWatchedDoorKey(NetNode? net)
    {
        if (!_watchedDoorCacheDirty)
            return _cachedWatchedDoorKey;

        if (net != null && net.IsAlive)
            EnsureReadyStateCache(net);

        var watchedDoor = string.Empty;
        if (_localPressed && _localInsideCircle && !string.IsNullOrWhiteSpace(_localDoorKey))
        {
            watchedDoor = _localDoorKey;
        }
        else
        {
            var localId = net?.id ?? 0;
            foreach (var pair in _playerStates)
            {
                var state = pair.Value;
                if (!state.Pressed || !state.InsideCircle)
                    continue;
                if (string.IsNullOrWhiteSpace(state.DoorKey))
                    continue;
                if (state.UserId > 0 && IsPlayerDownedForExit(state.UserId, localId))
                    continue;

                watchedDoor = state.DoorKey;
                break;
            }
        }

        _cachedWatchedDoorKey = watchedDoor;
        _watchedDoorCacheDirty = false;
        return watchedDoor;
    }

    private void ClearExitPointer()
    {
        if (_exitPointer == null)
            return;

        try
        {
            _exitPointer.destroy();
        }
        catch
        {
        }
        finally
        {
            _exitPointer = null;
            _exitPointerDoorKey = string.Empty;
        }
    }

    private void PruneDoorVisuals(Level? currentLevel)
    {
        if (_doorVisuals.Count == 0)
            return;

        _staleDoorVisualKeys.Clear();
        foreach (var pair in _doorVisuals)
        {
            var door = pair.Value.Door;
            var remove = door == null || currentLevel == null;
            if (!remove && door != null)
            {
                remove = !ReferenceEquals(door._level, currentLevel) || SafeRead(() => door.destroyed, true);
            }

            if (!remove)
                continue;

            _staleDoorVisualKeys.Add(pair.Key);
        }

        if (_staleDoorVisualKeys.Count == 0)
            return;

        for (int i = 0; i < _staleDoorVisualKeys.Count; i++)
            RemoveDoorVisual(_staleDoorVisualKeys[i]);

        _doorVisualRefreshDirty = true;
        _exitPointerDirty = true;
    }

    private void RemoveDoorVisual(string key)
    {
        if (!_doorVisuals.TryGetValue(key, out var visual))
            return;

        try { visual.Circle?.remove(); } catch { }
        try { visual.Counter?.remove(); } catch { }
        _doorVisuals.Remove(key);
        _doorVisualRefreshDirty = true;
    }

    private void ResetLevelState(Level? newLevel)
    {
        _lastLevel = newLevel;
        _exitCandidatesLevel = null;
        _exitTargetCandidates.Clear();
        _exitTargetsByDoorKey.Clear();
        _exitTargetCandidatesVersion = 0;
        InvalidateNearestExitCache();
        _localDoorKey = string.Empty;
        _localDoorCx = 0;
        _localDoorCy = 0;
        _localPressed = false;
        _localInsideCircle = false;
        _localDoorOutOfGame = true;
        _localDoorOnScreen = false;
        _lastLocalStateSendTick = 0;
        _hasLastSentState = false;
        _lastSentDoorCx = 0;
        _lastSentDoorCy = 0;
        _lastSentStateFlags = 0;
        _transitionDoorKey = string.Empty;
        _readyStateCacheDirty = true;
        _watchedDoorCacheDirty = true;
        _doorVisualRefreshDirty = true;
        _exitPointerDirty = true;
        _cachedExpectedPlayerCount = 1;
        _cachedWatchedDoorKey = string.Empty;
        _hasCachedDownedSignature = false;
        _cachedDownedSignature = 0;
        _exitPointerDoorKey = string.Empty;

        _staleDoorVisualKeys.Clear();
        foreach (var key in _doorVisuals.Keys)
            _staleDoorVisualKeys.Add(key);
        for (int i = 0; i < _staleDoorVisualKeys.Count; i++)
            RemoveDoorVisual(_staleDoorVisualKeys[i]);

        _playerStates.Clear();
        _activePlayerIds.Clear();
        _activePlayerScratchIds.Clear();
        _readyPlayerCounts.Clear();
        ClearExitPointer();
        ApplyLocalTimerPause(false);
    }

    private void InvalidateNearestExitCache()
    {
        _nearestExitCacheLevel = null;
        _nearestExitCacheHasValue = false;
        _nearestExitCacheHeroX = double.NaN;
        _nearestExitCacheHeroY = double.NaN;
        _nearestExitCacheInsideCircle = false;
        _nearestExitCacheCandidatesVersion = -1;
        _nearestExitCacheTarget = null;
    }

    private bool TryGetCachedNearestExitTarget(Hero hero, Level level, out Entity? target, out bool insideCircle)
    {
        target = null;
        insideCircle = false;
        if (!_nearestExitCacheHasValue ||
            !ReferenceEquals(_nearestExitCacheLevel, level) ||
            _nearestExitCacheCandidatesVersion != _exitTargetCandidatesVersion)
        {
            return false;
        }

        var heroX = GetEntityX(hero);
        var heroY = GetEntityY(hero);
        var dx = heroX - _nearestExitCacheHeroX;
        var dy = heroY - _nearestExitCacheHeroY;
        if (dx * dx + dy * dy > NearestExitCacheReuseDistanceSq)
            return false;

        target = _nearestExitCacheTarget;
        if (target == null)
        {
            insideCircle = _nearestExitCacheInsideCircle;
            return true;
        }
        if (!IsTrackedExitTargetCandidate(level, target) || !IsAvailableExitTarget(target))
        {
            InvalidateNearestExitCache();
            target = null;
            return false;
        }

        insideCircle = IsEntityInsideExitCircle(hero, target);
        _nearestExitCacheInsideCircle = insideCircle;
        return true;
    }

    private void CacheNearestExitTarget(Level level, Hero hero, Entity? target, bool insideCircle)
    {
        _nearestExitCacheLevel = level;
        _nearestExitCacheHasValue = true;
        _nearestExitCacheHeroX = GetEntityX(hero);
        _nearestExitCacheHeroY = GetEntityY(hero);
        _nearestExitCacheInsideCircle = insideCircle;
        _nearestExitCacheCandidatesVersion = _exitTargetCandidatesVersion;
        _nearestExitCacheTarget = target;
    }

    private void ApplyLocalTimerPause(bool paused)
    {
        if (_timerPausedByExit == paused)
            return;

        var game = ModEntry.Instance?.game;
        if (game?.data == null)
            return;

        try
        {
            game.data.stopGameTime = paused;
            _timerPausedByExit = paused;
        }
        catch
        {
        }
    }

    private static double GetEntityX(Entity e)
    {
        if (e == null)
            return 0.0;
        if (e.spr != null)
            return e.spr.x;
        return e.cx * 24.0;
    }

    private static double GetEntityY(Entity e)
    {
        if (e == null)
            return 0.0;
        if (e.spr != null)
            return e.spr.y;
        return e.cy * 24.0;
    }

    private static T SafeRead<T>(Func<T> getter, T fallback)
    {
        try { return getter(); } catch { return fallback; }
    }

    private void MarkExitUiStateDirty()
    {
        _readyStateCacheDirty = true;
        _watchedDoorCacheDirty = true;
        _doorVisualRefreshDirty = true;
        _exitPointerDirty = true;
    }

    private bool RefreshDownedSignature(int localId)
    {
        unchecked
        {
            var signature = 17;
            foreach (var userId in _activePlayerIds)
            {
                var downed = IsPlayerDownedForExit(userId, localId) ? 1 : 0;
                var combined = (userId * 397) ^ downed;
                signature += combined;
                signature ^= combined;
            }

            if (_hasCachedDownedSignature && signature == _cachedDownedSignature)
                return false;

            _cachedDownedSignature = signature;
            _hasCachedDownedSignature = true;
            return true;
        }
    }

    private void EnsureReadyStateCache(NetNode net)
    {
        if (!_readyStateCacheDirty)
            return;

        _readyPlayerCounts.Clear();
        foreach (var state in _playerStates.Values)
        {
            if (state.UserId <= 0)
                continue;
            if (IsPlayerDownedForExit(state.UserId, net.id))
                continue;
            if (!state.Pressed || !state.InsideCircle)
                continue;
            if (string.IsNullOrWhiteSpace(state.DoorKey))
                continue;

            if (_readyPlayerCounts.TryGetValue(state.DoorKey, out var count))
                _readyPlayerCounts[state.DoorKey] = count + 1;
            else
                _readyPlayerCounts[state.DoorKey] = 1;
        }

        _cachedExpectedPlayerCount = ComputeExpectedPlayerCount(net);
        _readyStateCacheDirty = false;
    }

    private bool ApplyNearestExitSelection(Entity? nearestTarget, bool insideCircle)
    {
        var newDoorKey = nearestTarget != null ? BuildDoorKey(nearestTarget.cx, nearestTarget.cy) : string.Empty;
        var newDoorCx = nearestTarget?.cx ?? 0;
        var newDoorCy = nearestTarget?.cy ?? 0;
        var newDoorOutOfGame = nearestTarget == null || SafeRead(() => nearestTarget.isOutOfGame, false);
        var newDoorOnScreen = nearestTarget != null && SafeRead(() => nearestTarget.isOnScreen, false);
        var newInsideCircle = nearestTarget != null && insideCircle;

        if (string.Equals(_localDoorKey, newDoorKey, StringComparison.Ordinal) &&
            _localDoorCx == newDoorCx &&
            _localDoorCy == newDoorCy &&
            _localDoorOutOfGame == newDoorOutOfGame &&
            _localDoorOnScreen == newDoorOnScreen &&
            _localInsideCircle == newInsideCircle)
        {
            return false;
        }

        _localDoorKey = newDoorKey;
        _localDoorCx = newDoorCx;
        _localDoorCy = newDoorCy;
        _localDoorOutOfGame = newDoorOutOfGame;
        _localDoorOnScreen = newDoorOnScreen;
        _localInsideCircle = newInsideCircle;
        return true;
    }

    private static bool ApplyPlayerState(
        PlayerExitState trackedState,
        string doorKey,
        int doorCx,
        int doorCy,
        bool pressed,
        bool insideCircle,
        bool isOutOfGame,
        bool isOnScreen)
    {
        if (trackedState == null)
            return false;

        var changed =
            !string.Equals(trackedState.DoorKey, doorKey, StringComparison.Ordinal) ||
            trackedState.DoorCx != doorCx ||
            trackedState.DoorCy != doorCy ||
            trackedState.Pressed != pressed ||
            trackedState.InsideCircle != insideCircle ||
            trackedState.IsOutOfGame != isOutOfGame ||
            trackedState.IsOnScreen != isOnScreen;

        trackedState.DoorKey = doorKey;
        trackedState.DoorCx = doorCx;
        trackedState.DoorCy = doorCy;
        trackedState.Pressed = pressed;
        trackedState.InsideCircle = insideCircle;
        trackedState.IsOutOfGame = isOutOfGame;
        trackedState.IsOnScreen = isOnScreen;
        return changed;
    }

    private PlayerExitState GetOrCreatePlayerState(int userId)
    {
        if (!_playerStates.TryGetValue(userId, out var state))
        {
            state = new PlayerExitState
            {
                UserId = userId
            };
            _playerStates[userId] = state;
        }

        return state;
    }

    private byte BuildLocalStateFlags()
    {
        byte flags = 0;
        if (_localPressed)
            flags |= 1;
        if (_localInsideCircle)
            flags |= 2;
        if (_localDoorOutOfGame)
            flags |= 4;
        if (_localDoorOnScreen)
            flags |= 8;
        return flags;
    }

    private void IndexExitTargetCandidate(Entity? entity)
    {
        if (!IsSupportedExitTarget(entity))
            return;

        var key = BuildDoorKey(entity!.cx, entity.cy);
        _exitTargetsByDoorKey[key] = entity;
    }

    private void UnindexExitTargetCandidate(Entity? entity)
    {
        if (!IsSupportedExitTarget(entity))
            return;

        var key = BuildDoorKey(entity!.cx, entity.cy);
        if (_exitTargetsByDoorKey.TryGetValue(key, out var indexed) && ReferenceEquals(indexed, entity))
            _exitTargetsByDoorKey.Remove(key);
    }

    private void HandleRemovedExitTargetCandidates()
    {
        _exitTargetCandidatesVersion++;
        InvalidateNearestExitCache();
        _watchedDoorCacheDirty = true;
        _doorVisualRefreshDirty = true;
        _exitPointerDirty = true;
    }

}

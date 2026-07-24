using System;
using System.Collections.Generic;
using dc.cine;
using dc.en;
using dc.en.inter;
using dc.hl.types;
using dc.level;
using dc.pr;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod
{
    internal partial class GameDataSync
    {
        [Flags]
        private enum LevelReloadReason
        {
            None = 0,
            GraphUpdated = 1,
            BossRuneChanged = 2,
        }

        private static readonly object _levelReloadLock = new();
        private static readonly object _pendingBossRuneReloadLock = new();
        private static readonly Dictionary<string, LevelReloadReason> _pendingLevelReloadReasons =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string?> _pendingLevelReloadPayloads =
            new(StringComparer.Ordinal);

        private static string? _lastLevelReloadLevelId;
        private static string? _lastLevelReloadPayload;
        private static LevelReloadReason _lastLevelReloadReason;
        private static long _lastLevelReloadTick;

        private static string? _pendingBossRuneReloadLevelId;
        private static int _pendingBossRuneReloadValue;
        private static long _pendingBossRuneReloadTick;
        private static bool _hasPendingBossRuneReload;

        private const int LevelReloadThrottleMs = 3000;
        private const int PendingBossRuneReloadTtlMs = 15000;

        internal static void TryScheduleBossRuneReloadForCurrentLevel()
        {
            var currentLevelId = TryGetCurrentLevelId();
            if (string.IsNullOrWhiteSpace(currentLevelId))
                return;

            if (!HasRemoteLevelGraphCached(currentLevelId))
                return;

            ScheduleLevelReload(currentLevelId, LevelReloadReason.BossRuneChanged, payload: null);
        }

        internal static void MarkPendingBossRuneReload(int bossRune)
        {
            var levelId = TryGetCurrentLevelId();
            lock (_pendingBossRuneReloadLock)
            {
                _pendingBossRuneReloadValue = bossRune;
                _pendingBossRuneReloadLevelId = levelId;
                _pendingBossRuneReloadTick = Environment.TickCount64;
                _hasPendingBossRuneReload = true;
            }
        }

        internal static void ClearPendingBossRuneReloadState()
        {
            lock (_pendingBossRuneReloadLock)
            {
                _hasPendingBossRuneReload = false;
                _pendingBossRuneReloadLevelId = null;
            }
        }

        private static bool HasPendingBossRuneReloadForLevel(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            lock (_pendingBossRuneReloadLock)
            {
                if (!_hasPendingBossRuneReload)
                    return false;

                if (Environment.TickCount64 - _pendingBossRuneReloadTick > PendingBossRuneReloadTtlMs)
                {
                    _hasPendingBossRuneReload = false;
                    _pendingBossRuneReloadLevelId = null;
                    return false;
                }

                if (string.IsNullOrWhiteSpace(_pendingBossRuneReloadLevelId))
                    return true;

                return string.Equals(_pendingBossRuneReloadLevelId, levelId, StringComparison.Ordinal);
            }
        }

        private static void ScheduleLevelReload(string levelId, LevelReloadReason reason, string? payload)
        {
            if (string.IsNullOrWhiteSpace(levelId) || reason == LevelReloadReason.None)
                return;

            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive || net.IsHost)
                return;

            lock (_levelReloadLock)
            {
                if (_pendingLevelReloadReasons.TryGetValue(levelId, out var existing))
                    reason |= existing;
                _pendingLevelReloadReasons[levelId] = reason;

                if (!string.IsNullOrWhiteSpace(payload))
                    _pendingLevelReloadPayloads[levelId] = payload;
                else if (!_pendingLevelReloadPayloads.ContainsKey(levelId))
                    _pendingLevelReloadPayloads[levelId] = null;
            }

            GameMenu.EnqueueMainThreadCoalesced("level:reload:" + levelId, () =>
            {
                try
                {
                    TryTriggerLevelReload(levelId);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to process level reload for {LevelId}: {Message}", levelId, ex.Message);
                }
            });
        }

        /// <summary>
        /// In-place reloadAfterBossRuneModif keeps the current hero and only regenerates the level. It must be
        /// suppressed while the local player is downed/Game Over or a full-run restart is pending, otherwise the
        /// host's restart-level graph reloads the old downed run in place (no heal, Game Over stuck) or crashes
        /// with a Null access .curCine — instead of letting the queued launchGame restart take over.
        /// </summary>
        private static bool ShouldSuppressClientLevelReload()
        {
            try
            {
                if (ModEntry.IsLocalPlayerDowned())
                    return true;
                if (GameMenu.IsClientRestartPending())
                    return true;
            }
            catch
            {
            }

            return false;
        }

        private static void TryTriggerLevelReload(string graphLevelId)
        {
            if (string.IsNullOrWhiteSpace(graphLevelId))
                return;

            LevelReloadReason reason;
            string? payload;
            lock (_levelReloadLock)
            {
                if (!_pendingLevelReloadReasons.TryGetValue(graphLevelId, out reason) ||
                    reason == LevelReloadReason.None)
                {
                    _pendingLevelReloadReasons.Remove(graphLevelId);
                    _pendingLevelReloadPayloads.Remove(graphLevelId);
                    return;
                }

                _pendingLevelReloadReasons.Remove(graphLevelId);
                _pendingLevelReloadPayloads.TryGetValue(graphLevelId, out payload);
                _pendingLevelReloadPayloads.Remove(graphLevelId);
            }

            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive || net.IsHost)
                return;

            if (ShouldSuppressClientLevelReload())
            {
                _log?.Information("[NetMod] Skipping level reload for {LevelId}: client downed/restart pending", graphLevelId);
                return;
            }

            var hero = ModEntry.me;
            var level = hero?._level;
            if (hero == null || level == null || level.map == null)
                return;

            var currentLevelId = level.map.id?.ToString();
            if (!string.Equals(currentLevelId, graphLevelId, StringComparison.Ordinal))
                return;

            var applyBoss = (reason & LevelReloadReason.BossRuneChanged) != 0;
            var applyGraph = (reason & LevelReloadReason.GraphUpdated) != 0;

            dc.User? user = null;
            var remoteBossRune = 0;
            var bossApplies = false;
            if (applyBoss)
            {
                user = dc.Main.Class.ME?.user ?? level.game?.user;
                if (user != null &&
                    TryGetRemoteBossRune(out remoteBossRune))
                {
                    var localBossRune = GetEffectiveBossRune(user);
                    var forceByPending = ConsumePendingBossRuneReloadIfMatch(graphLevelId, remoteBossRune);
                    bossApplies = forceByPending || localBossRune != remoteBossRune;
                    if (bossApplies)
                    {
                        _log?.Information(
                            "[NetMod] Boss-rune graph reload candidate level={LevelId} local={LocalBossRune} remote={RemoteBossRune} pending={Pending}",
                            graphLevelId,
                            localBossRune,
                            remoteBossRune,
                            forceByPending);
                    }
                }
            }

            if (!bossApplies)
                applyBoss = false;

            if (!applyBoss && !applyGraph)
                return;

            // Mid-session reload regenerates via reloadAfterBossRuneModif → generateGraph apply.
            // Require a cached remote graph so Path A can bind the host layout.
            if (!HasRemoteLevelGraphCached(graphLevelId))
                return;

            if (!TryBeginLevelReload(graphLevelId, payload, applyBoss ? LevelReloadReason.BossRuneChanged : LevelReloadReason.GraphUpdated))
                return;

            var targetLevelId = ResolveReloadTargetLevelId(level, graphLevelId);
            int offsetCx;
            int offsetCy;
            if (applyBoss)
            {
                ApplyRemoteBossRune(user!, remoteBossRune);
                (offsetCx, offsetCy) = ComputeBossRuneReloadOffsets(hero, level);
                ModEntry.PrepareAndDisposeRemoteKingsForBossCellReload(
                    "client-boss-rune-reload:" + targetLevelId);
            }
            else
            {
                (offsetCx, offsetCy) = ComputeCurrentLevelReloadOffsets(hero, level);
            }

            var reload = LevelTransition.Class.reloadAfterBossRuneModif;
            if (reload == null)
            {
                _log?.Warning("[NetMod] Missing LevelTransition.reloadAfterBossRuneModif for {LevelId}", targetLevelId);
                return;
            }

            _ = reload(targetLevelId.AsHaxeString(), offsetCx, offsetCy);
            if (applyBoss)
            {
                _log?.Information(
                    "[NetMod] Triggered level reload for {LevelId} reason=BossRune offset=({OffsetCx},{OffsetCy}) bossRune={BossRune}",
                    targetLevelId,
                    offsetCx,
                    offsetCy,
                    remoteBossRune);
            }
            else
            {
                _log?.Information(
                    "[NetMod] Triggered level reload for {LevelId} reason=Graph offset=({OffsetCx},{OffsetCy})",
                    targetLevelId,
                    offsetCx,
                    offsetCy);
            }
        }

        private static bool TryBeginLevelReload(string levelId, string? payload, LevelReloadReason effectiveReason)
        {
            var now = Environment.TickCount64;
            lock (_levelReloadLock)
            {
                var sameLevel = string.Equals(_lastLevelReloadLevelId, levelId, StringComparison.Ordinal);
                if (sameLevel && now - _lastLevelReloadTick < LevelReloadThrottleMs)
                {
                    // Boss-rune path throttles by level only; pure graph also matches payload.
                    if ((effectiveReason & LevelReloadReason.BossRuneChanged) != 0)
                        return false;

                    if (string.Equals(_lastLevelReloadPayload, payload, StringComparison.Ordinal) &&
                        (_lastLevelReloadReason & LevelReloadReason.GraphUpdated) != 0)
                        return false;
                }

                _lastLevelReloadLevelId = levelId;
                _lastLevelReloadPayload = payload;
                _lastLevelReloadReason = effectiveReason;
                _lastLevelReloadTick = now;
                return true;
            }
        }

        private static (int OffsetCx, int OffsetCy) ComputeBossRuneReloadOffsets(Hero hero, Level level)
        {
            var heroCx = 0;
            var heroCy = 0;
            try { heroCx = hero.cx; } catch { }
            try { heroCy = hero.cy; } catch { }

            var anchorRoom = TryFindBossRuneAnchorRoom(level) ?? TryGetRoomAt(level, heroCx, heroCy);
            if (anchorRoom == null)
                return (0, 0);

            var roomX = 0;
            var roomY = 0;
            try { roomX = anchorRoom.x; } catch { }
            try { roomY = anchorRoom.y; } catch { }

            return (heroCx - roomX, heroCy - roomY);
        }

        private static (int OffsetCx, int OffsetCy) ComputeCurrentLevelReloadOffsets(Hero hero, Level level)
        {
            var heroCx = 0;
            var heroCy = 0;
            try { heroCx = hero.cx; } catch { }
            try { heroCy = hero.cy; } catch { }

            var room = TryGetRoomAt(level, heroCx, heroCy);
            if (room == null)
                return (0, 0);

            var roomX = 0;
            var roomY = 0;
            try { roomX = room.x; } catch { }
            try { roomY = room.y; } catch { }

            return (heroCx - roomX, heroCy - roomY);
        }

        private static Room? TryFindBossRuneAnchorRoom(Level level)
        {
            try
            {
                var entitiesByClass = level.entitiesByClass;
                if (entitiesByClass == null)
                    return null;

                var switchClassId = SwitchBossRune.Class.__clid;
                var entries = entitiesByClass.get(switchClassId) as ArrayObj;
                if (entries == null)
                    return null;

                for (int i = 0; i < entries.length; i++)
                {
                    if (entries.getDyn(i) is not SwitchBossRune altar)
                        continue;

                    var room = TryGetRoomAt(level, altar.cx, altar.cy);
                    if (room != null)
                        return room;
                }
            }
            catch
            {
            }

            return null;
        }

        private static Room? TryGetRoomAt(Level level, int cx, int cy)
        {
            try
            {
                return level.map?.getRoomAt(cx, cy);
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveReloadTargetLevelId(Level level, string fallbackLevelId)
        {
            try
            {
                var levelId = level.map?.id?.ToString();
                if (!string.IsNullOrWhiteSpace(levelId))
                    return levelId;
            }
            catch
            {
            }

            return string.IsNullOrWhiteSpace(fallbackLevelId) ? "PrisonStart" : fallbackLevelId;
        }

        private static string? TryGetCurrentLevelId()
        {
            try
            {
                var levelId = ModEntry.me?._level?.map?.id?.ToString();
                if (!string.IsNullOrWhiteSpace(levelId))
                    return levelId;
            }
            catch
            {
            }

            return null;
        }

        private static bool ConsumePendingBossRuneReloadIfMatch(string graphLevelId, int remoteBossRune)
        {
            lock (_pendingBossRuneReloadLock)
            {
                if (!_hasPendingBossRuneReload)
                    return false;

                if (Environment.TickCount64 - _pendingBossRuneReloadTick > PendingBossRuneReloadTtlMs)
                {
                    _hasPendingBossRuneReload = false;
                    _pendingBossRuneReloadLevelId = null;
                    return false;
                }

                if (_pendingBossRuneReloadValue != remoteBossRune)
                    return false;

                if (!string.IsNullOrWhiteSpace(_pendingBossRuneReloadLevelId) &&
                    !string.Equals(_pendingBossRuneReloadLevelId, graphLevelId, StringComparison.Ordinal))
                {
                    return false;
                }

                _hasPendingBossRuneReload = false;
                _pendingBossRuneReloadLevelId = null;
                return true;
            }
        }

        private static void ResetLevelReloadState()
        {
            lock (_levelReloadLock)
            {
                _pendingLevelReloadReasons.Clear();
                _pendingLevelReloadPayloads.Clear();
                _lastLevelReloadLevelId = null;
                _lastLevelReloadPayload = null;
                _lastLevelReloadReason = LevelReloadReason.None;
                _lastLevelReloadTick = 0;
            }
        }
    }
}

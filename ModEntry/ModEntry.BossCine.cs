using System.Diagnostics;
using dc.en;
using dc.en.inter;
using dc.pr;
using ModCore.Utilities;
using dc;
using HaxeProxy.Runtime;
using dc.cine;
using dc.cine.coll;
using dc.cine.dlcp;
using dc.cine.kf;
using dc.cine.queen;
using dc.en.mob.boss;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private void DetectAndSendBossCine()
        {
            if (_netRole == NetRole.None || _net == null || !_net.IsAlive)
                return;

            var currentLevelId = string.IsNullOrWhiteSpace(levelId) ? null : levelId.Trim();
            if (string.IsNullOrEmpty(currentLevelId) || !BossLevelIds.Contains(currentLevelId))
                return;
            if (IsBossCineCompleted(currentLevelId))
                return;

            var game = dc.pr.Game.Class.ME;
            var cine = game?.curCine;
            if (cine == null || cine.destroyed)
                return;

            if (cine is DeadBase || cine is RemoteDownedCorpse)
                return;

            if (cine is HeroDeath || cine is HeroDeathBase || cine is HeroDeathContinue ||
                cine is HeroDeathRespawn || cine is HeroDeathDLCP)
                return;
            var deathTypeName = cine.GetType().FullName ?? string.Empty;
            if (deathTypeName.IndexOf("HeroDeath", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            if (TrySendBossCinePayload(BuildBossCinePayload(currentLevelId, null)))
                MarkBossCineCompleted(currentLevelId);
        }

        private bool TrySendBossCinePayload(string payload)
        {
            if (_netRole == NetRole.None || _net == null || !_net.IsAlive)
                return false;

            if (!TryParseBossCinePayload(payload, out var levelId, out var _, out var _, out var _, out var _, out var _, out var _, out var _))
                return false;

            if (IsBossCineCompleted(levelId))
                return false;

            if (IsBossCineSendSuppressed(levelId))
                return false;

            var now = Stopwatch.GetTimestamp();
            var cooldownTicks = (long)(Stopwatch.Frequency * BossCineSendCooldownSeconds);
            if (_lastBossCineSentLevelId == levelId && now - _lastBossCineSentTick < cooldownTicks)
                return false;

            _lastBossCineSentLevelId = levelId;
            _lastBossCineSentTick = now;
            _net.SendBossCine(payload);
            return true;
        }

        private string BuildBossCinePayload(string levelId, string? genericEventId)
        {
            double? x = null;
            double? y = null;
            int? dir = null;
            if (me != null)
            {
                TryCaptureBossCineHeroPosition(me, out x, out y, out dir);
            }

            return BuildBossCinePayload(levelId, genericEventId, x, y, dir, null, null, null);
        }

        private string BuildBossCinePayload(string levelId, string? genericEventId, double? x, double? y, int? dir)
        {
            return BuildBossCinePayload(levelId, genericEventId, x, y, dir, null, null, null);
        }

        private string BuildBossCinePayload(
            string levelId,
            string? genericEventId,
            double? x,
            double? y,
            int? dir,
            double? finalX,
            double? finalY,
            int? finalDir)
        {
            var safeLevelId = string.IsNullOrWhiteSpace(levelId)
                ? string.Empty
                : levelId.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal)
                    .Trim();
            if (string.IsNullOrEmpty(safeLevelId))
                return string.Empty;

            var safeEventId = string.IsNullOrWhiteSpace(genericEventId)
                ? string.Empty
                : genericEventId.Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", string.Empty, StringComparison.Ordinal)
                    .Trim();

            if (!x.HasValue || !y.HasValue)
                return string.IsNullOrEmpty(safeEventId) ? safeLevelId : $"{safeLevelId}|{safeEventId}";

            var resolvedFinalX = finalX ?? x.Value;
            var resolvedFinalY = finalY ?? y.Value;
            var resolvedFinalDir = finalDir ?? dir ?? 0;

            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{safeLevelId}|{safeEventId}|{x.Value}|{y.Value}|{dir ?? 0}|{resolvedFinalX}|{resolvedFinalY}|{resolvedFinalDir}");
        }

        private static bool TryParseBossCinePayload(
            string? payload,
            out string levelId,
            out string? genericEventId,
            out double? snapX,
            out double? snapY,
            out int? snapDir,
            out double? finalX,
            out double? finalY,
            out int? finalDir)
        {
            levelId = string.Empty;
            genericEventId = null;
            snapX = null;
            snapY = null;
            snapDir = null;
            finalX = null;
            finalY = null;
            finalDir = null;

            if (string.IsNullOrWhiteSpace(payload))
                return false;

            var normalized = payload
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Trim();
            if (normalized.Length == 0)
                return false;

            var parts = normalized.Split('|');
            if (parts.Length == 0)
                return false;

            levelId = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            if (parts.Length >= 2)
            {
                var eventId = parts[1].Trim();
                if (eventId.Length > 0)
                    genericEventId = eventId;
            }

            if (parts.Length >= 4)
            {
                if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedX))
                    snapX = parsedX;
                if (double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedY))
                    snapY = parsedY;
            }

            if (parts.Length >= 5 &&
                int.TryParse(parts[4], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedDir))
            {
                snapDir = parsedDir;
            }

            if (parts.Length >= 7)
            {
                if (double.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedFinalX))
                    finalX = parsedFinalX;
                if (double.TryParse(parts[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedFinalY))
                    finalY = parsedFinalY;
            }

            if (parts.Length >= 8 &&
                int.TryParse(parts[7], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedFinalDir))
            {
                finalDir = parsedFinalDir;
            }

            return true;
        }

        private static void TryCaptureBossCineHeroPosition(Hero? hero, out double? x, out double? y, out int? dir)
        {
            x = null;
            y = null;
            dir = null;

            if (hero == null)
                return;

            if (hero.spr != null)
            {
                x = hero.spr.x;
                y = hero.spr.y;
            }
            else
            {
                x = hero.get_targetSprPosX();
                y = hero.get_targetSprPosY();
            }

            dir = hero.dir;
        }

        private void ApplyReceivedBossCine()
        {
            var net = _net;
            if (net == null || !net.IsAlive)
            {
                _pendingBossCineApplyByLevel.Clear();
                _suppressBossCineEchoByLevel.Clear();
                _completedBossCineLevels.Clear();
                _appliedBossHeroTeleportLevels.Clear();
                return;
            }

            if (net.TryConsumeBossCineLevelIds(out var levelIds) && levelIds.Count > 0)
            {
                try
                {
                    var defaultExpiry = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * BossCineApplyPendingTtlSeconds);
                    for (int i = 0; i < levelIds.Count; i++)
                    {
                        var receivedPayload = levelIds[i];
                        if (!TryParseBossCinePayload(receivedPayload, out var receivedLevelId, out var receivedGenericEventId, out var receivedSnapX, out var receivedSnapY, out var _, out var _, out var _, out var _))
                            continue;

                        ClearBossCineCompleted(receivedLevelId);
                        if (!string.IsNullOrWhiteSpace(receivedGenericEventId) && receivedSnapX.HasValue && receivedSnapY.HasValue)
                            SuppressBossCineSend(receivedLevelId);

                        var normalized = receivedPayload
                            .Replace("\r", string.Empty, StringComparison.Ordinal)
                            .Replace("\n", string.Empty, StringComparison.Ordinal)
                            .Trim();

                        var expiry = defaultExpiry;
                        if (_pendingBossCineApplyByLevel.TryGetValue(normalized, out var oldExpiry) && oldExpiry > expiry)
                            expiry = oldExpiry;

                        _pendingBossCineApplyByLevel[normalized] = expiry;
                    }
                }
                finally
                {
                    NetNode.ReleaseConsumedList(levelIds);
                }
            }

            if (_pendingBossCineApplyByLevel.Count == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            var currentLevelId = string.IsNullOrWhiteSpace(levelId) ? string.Empty : levelId.Trim();
            List<string>? remove = null;
            foreach (var kv in _pendingBossCineApplyByLevel)
            {
                var pendingPayload = kv.Key;
                var expiryTicks = kv.Value;
                if (now >= expiryTicks)
                {
                    (remove ??= new List<string>()).Add(pendingPayload);
                    continue;
                }

                if (!TryParseBossCinePayload(pendingPayload, out var pendingLevelId, out var genericEventId, out var snapX, out var snapY, out var snapDir, out var finalX, out var finalY, out var finalDir))
                {
                    (remove ??= new List<string>()).Add(pendingPayload);
                    continue;
                }

                if (string.IsNullOrEmpty(currentLevelId) ||
                    !string.Equals(pendingLevelId, currentLevelId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var triggered =
                    !string.IsNullOrWhiteSpace(genericEventId) &&
                    snapX.HasValue &&
                    snapY.HasValue
                        ? TrySyncBossCinematicFromLocalTrigger(pendingLevelId, genericEventId)
                        : TryTriggerBossCinematic(pendingLevelId, genericEventId, snapX, snapY, snapDir, finalX, finalY, finalDir);

                if (triggered)
                {
                    MarkBossCineCompleted(pendingLevelId);
                    SuppressBossCineSend(pendingLevelId);
                    (remove ??= new List<string>()).Add(pendingPayload);
                }
            }

            if (remove == null || remove.Count == 0)
                return;

            for (int i = 0; i < remove.Count; i++)
                _pendingBossCineApplyByLevel.Remove(remove[i]);
        }

        private void ApplyReceivedBossHeroTeleport()
        {
            var net = _net;
            if (net == null || !net.IsAlive)
                return;

            if (!net.TryConsumeBossHeroTeleportEvents(out var teleports) || teleports.Count == 0)
                return;

            try
            {
                var localHero = me ?? ModCore.Modules.Game.Instance?.HeroInstance;
                if (localHero == null)
                    return;

                var localId = net.id;
                var currentLevelId = GetCurrentLevelId();
                foreach (var teleport in teleports)
                {
                    if (teleport.UserId > 0 && teleport.UserId == localId)
                        continue;
                    if (HasAppliedBossHeroTeleport(currentLevelId))
                        continue;

                    MarkBossHeroTeleportApplied(currentLevelId);
                    SuppressBossCineSend(currentLevelId);
                    _suppressBossTriggerNetSendUntilTick =
                        Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * BossHeroTeleportEchoSuppressSeconds);
                    localHero.cancelVelocities();
                    localHero.setPosPixel(teleport.X, teleport.Y - BossHeroTeleportYOffsetPx);
                    localHero.dir = teleport.Dir;
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(teleports);
            }
        }

        private bool TrySyncBossCinematicFromLocalTrigger(
            string levelId,
            string? genericEventId)
        {
            if (string.IsNullOrWhiteSpace(levelId) || !BossLevelIds.Contains(levelId))
                return false;

            var game = dc.pr.Game.Class.ME;
            var hero = game?.hero ?? me;
            var level = hero?._level;
            if (game == null || hero == null || level == null)
                return false;

            var currentCine = game.curCine;
            if (currentCine != null && !currentCine.destroyed)
                return IsBossIntroCinematic(currentCine);

            if (GameHasAnyCinematic(game))
                return false;

            if (!DidBossHiddenTriggerStart(level, genericEventId))
                return false;

            _lastBossCineSentLevelId = levelId;
            _lastBossCineSentTick = Stopwatch.GetTimestamp();
            return true;
        }

        private static bool DidBossHiddenTriggerStart(Level level, string? genericEventId)
        {
            if (level == null || string.IsNullOrWhiteSpace(genericEventId))
                return false;

            var entitiesByClass = level.entitiesByClass;
            var triggerClid = HiddenTrigger.Class.__clid;
            var entries = entitiesByClass?.get(triggerClid) as dc.hl.types.ArrayObj;
            if (entries == null)
                return false;

            for (var i = 0; i < entries.length; i++)
            {
                if (entries.getDyn(i) is not HiddenTrigger ht)
                    continue;

                var evId = ht.genericEventId?.ToString();
                if (!string.Equals(evId, genericEventId, StringComparison.Ordinal))
                    continue;

                if (ht.used)
                    return true;
            }

            return false;
        }

        private bool TryTriggerBossCinematic(
            string levelId,
            string? genericEventId,
            double? snapX,
            double? snapY,
            int? snapDir,
            double? finalX,
            double? finalY,
            int? finalDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(levelId))
                    return false;

                if (!BossLevelIds.Contains(levelId))
                    return false;

                var game = dc.pr.Game.Class.ME;
                var hero = game?.hero ?? me;
                var level = hero?._level;
                if (game == null || level == null || hero == null)
                    return false;

                var currentCine = game.curCine;
                if (currentCine != null && !currentCine.destroyed)
                    return IsBossIntroCinematic(currentCine);

                var entitiesByClass = level.entitiesByClass;
                if (entitiesByClass != null)
                {
                    var triggerClid = HiddenTrigger.Class.__clid;
                    var entries = entitiesByClass.get(triggerClid) as dc.hl.types.ArrayObj;
                    if (entries != null)
                    {
                        HiddenTrigger? usedReplayCandidate = null;
                        for (var i = 0; i < entries.length; i++)
                        {
                            if (entries.getDyn(i) is not HiddenTrigger ht)
                                continue;

                            var evId = ht.genericEventId?.ToString();
                            if (string.IsNullOrEmpty(evId))
                                continue;
                            if (!string.IsNullOrWhiteSpace(genericEventId))
                            {
                                if (!string.Equals(evId, genericEventId, StringComparison.Ordinal))
                                    continue;
                            }
                            else if (!BossRoomGenericEventIds.Contains(evId))
                            {
                                continue;
                            }

                            if (ht.used)
                            {
                                usedReplayCandidate ??= ht;
                                continue;
                            }

                            if (GameHasAnyCinematic(game))
                                return false;

                            TrySnapHeroToBossCinePosition(hero, snapX, snapY, snapDir);
                            RunWithSuppressedBossCineSend(() => ht.trigger(hero));
                            if (DidBossTriggerStart(game, ht))
                            {
                                TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                                _lastBossCineSentLevelId = levelId;
                                _lastBossCineSentTick = Stopwatch.GetTimestamp();
                                return true;
                            }

                            return false;
                        }

                        if (usedReplayCandidate != null && TryReplayBossHiddenTrigger(usedReplayCandidate, hero, snapX, snapY, snapDir, finalX, finalY, finalDir))
                        {
                            _lastBossCineSentLevelId = levelId;
                            _lastBossCineSentTick = Stopwatch.GetTimestamp();
                            return true;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(genericEventId) &&
                    TryCreateBossCinematicDirectly(level, hero, genericEventId, snapX, snapY, snapDir, finalX, finalY, finalDir))
                {
                    MarkBossRoomHiddenTriggersUsed(level, genericEventId);
                    _lastBossCineSentLevelId = levelId;
                    _lastBossCineSentTick = Stopwatch.GetTimestamp();
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private bool TryReplayBossHiddenTrigger(
            HiddenTrigger trigger,
            Hero hero,
            double? snapX,
            double? snapY,
            int? snapDir,
            double? finalX,
            double? finalY,
            int? finalDir)
        {
            if (trigger == null || hero == null)
                return false;

            try
            {
                var wasUsed = trigger.used;
                var game = dc.pr.Game.Class.ME;
                if (GameHasAnyCinematic(game))
                    return false;

                trigger.used = false;
                TrySnapHeroToBossCinePosition(hero, snapX, snapY, snapDir);
                RunWithSuppressedBossCineSend(() => trigger.trigger(hero));

                if (DidBossTriggerStart(game, trigger))
                {
                    TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                    return true;
                }

                trigger.used = wasUsed;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool DidBossTriggerStart(dc.pr.Game? game, HiddenTrigger trigger)
        {
            if (trigger != null)
            {
                try
                {
                    if (trigger.used)
                        return true;
                }
                catch
                {
                }
            }

            var currentCine = game?.curCine;
            return currentCine != null && !currentCine.destroyed && IsBossIntroCinematic(currentCine);
        }

        private static bool GameHasAnyCinematic(dc.pr.Game? game)
        {
            if (game == null)
                return false;

            try
            {
                return game.hasCinematic();
            }
            catch
            {
                var currentCine = game.curCine;
                return currentCine != null && !currentCine.destroyed;
            }
        }

        private void TrySnapHeroToBossCinePosition(Hero hero, double? snapX, double? snapY, int? snapDir)
        {
            if (hero == null || !snapX.HasValue || !snapY.HasValue)
                return;

            try { hero.cancelVelocities(); } catch { }
            SnapHeroToDownedPosition(hero, snapX.Value, snapY.Value, clampToGround: false);
            if (snapDir.HasValue)
            {
                try { hero.dir = snapDir.Value; } catch { }
            }
        }

        private static bool IsBossIntroCinematic(dc.GameCinematic? cine)
        {
            if (cine == null)
                return false;

            try
            {
                var typeName = cine.GetType().Name ?? string.Empty;
                return BossIntroCineTypeNames.Contains(typeName);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsBossPhaseTransitionCinematic(dc.GameCinematic? cine)
        {
            if (cine == null)
                return false;

            try
            {
                return BossPhaseTransitionCineTypeNames.Contains(cine.GetType().Name ?? string.Empty);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Intro OR mid-fight transformation: cines during which the local cine script owns the boss.</summary>
        private static bool IsBossOwnedCinematic(dc.GameCinematic? cine)
            => IsBossIntroCinematic(cine) || IsBossPhaseTransitionCinematic(cine);

        private bool TryCreateBossCinematicDirectly(
            Level level,
            Hero hero,
            string genericEventId,
            double? snapX,
            double? snapY,
            int? snapDir,
            double? finalX,
            double? finalY,
            int? finalDir)
        {
            if (level == null || hero == null || string.IsNullOrWhiteSpace(genericEventId))
                return false;

            try
            {
                TrySnapHeroToBossCinePosition(hero, snapX, snapY, snapDir);
                switch (genericEventId.Trim())
                {
                    case "roomDeath":
                        RunWithSuppressedBossCineSend(() => _ = new EnterRoomDeathBoss(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomBeholder":
                    case "roomBerserk":
                        RunWithSuppressedBossCineSend(() => _ = new EnterRoomBoss(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomCollectorBoss":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            var story = level.game?.user?.story;
                            var counters = story?.counters;
                            var key = "collectorMet".AsHaxeString();
                            var collectorMet = 0;
                            if (counters != null && counters.exists(key))
                            {
                                var rawValue = counters.get(key)?.ToString();
                                int.TryParse(rawValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out collectorMet);
                            }
                            if (collectorMet == 1)
                                _ = new StartCollectorFightAlt(hero);
                            else
                                _ = new MeetCollectorEnd(null);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomDooku":
                        RunWithSuppressedBossCineSend(() => _ = new EnterDookuBossRoom(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomGardenerBoss":
                        RunWithSuppressedBossCineSend(() => _ = new EnterRoomGardenerBoss(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomGiant":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            if (level.boss is Giant giant)
                            {
                                var modifiers = giant.bossRushModifiers;
                                if (modifiers != null && modifiers.enabled)
                                {
                                    _ = new EnterModifiedGiantRoom(hero);
                                    return;
                                }
                            }

                            _ = new EnterGiantRoom(hero);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomKingsHand":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            if (level.game.isBossRush())
                            {
                                _ = new EnterThroneBossRush(hero);
                                return;
                            }

                            if (hero.hasSkin("king".AsHaxeString(), null))
                                return;

                            _ = new EnterThroneRoom(hero);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomKingsHandAsKing":
                        if (!hero.hasSkin("king".AsHaxeString(), null))
                            return false;

                        RunWithSuppressedBossCineSend(() => _ = new EnterThroneRoomAsKing(hero));
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomMamaTick":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            var storyProgress = 0;
                            try
                            {
                                var ug = level.game?.user;
                                if (ug != null)
                                {
                                    var sp = ug.story.getNpcProgress(new NpcId.TickPriest());
                                    storyProgress = sp ?? 0;
                                }
                            }
                            catch { }

                            if (storyProgress > 0 && level.boss is MamaTick mamaTick)
                            {
                                mamaTick.publicEmerge();
                                return;
                            }

                            _ = new EnterRoomBoss(hero);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomQueen":
                        RunWithSuppressedBossCineSend(() => _ = new EnterRoomQueenBoss());
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;

                    case "roomBehemoth":
                        RunWithSuppressedBossCineSend(() =>
                        {
                            if (level.boss is Behemoth behemoth)
                            {
                                var modifiers = behemoth.bossRushModifiers;
                                if (modifiers != null && modifiers.bossRushClone != null)
                                {
                                    _ = new EnterDualBehemoth(hero);
                                    return;
                                }
                            }

                            _ = new EnterRoomBoss(hero);
                        });
                        TrySnapHeroToBossCinePosition(hero, finalX, finalY, finalDir);
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void MarkBossRoomHiddenTriggersUsed(Level level, string genericEventId)
        {
            if (level == null || string.IsNullOrWhiteSpace(genericEventId))
                return;

            try
            {
                var entries = level.entitiesByClass?.get(HiddenTrigger.Class.__clid) as dc.hl.types.ArrayObj;
                if (entries == null)
                    return;

                for (var i = 0; i < entries.length; i++)
                {
                    if (entries.getDyn(i) is not HiddenTrigger ht)
                        continue;

                    var eventId = ht.genericEventId?.ToString();
                    if (!string.Equals(eventId, genericEventId, StringComparison.Ordinal))
                        continue;

                    ht.used = true;
                }
            }
            catch
            {
            }
        }

        private bool IsBossCineSendSuppressed(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            var normalized = levelId.Trim();
            if (normalized.Length == 0)
                return false;

            if (!_suppressBossCineEchoByLevel.TryGetValue(normalized, out var expiry))
                return false;

            var now = Stopwatch.GetTimestamp();
            if (now < expiry)
                return true;

            _suppressBossCineEchoByLevel.Remove(normalized);
            return false;
        }

        private void SuppressBossCineSend(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            var normalized = levelId.Trim();
            if (normalized.Length == 0)
                return;

            _suppressBossCineEchoByLevel[normalized] =
                Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * BossCineEchoSuppressSeconds);
        }

        private bool IsBossCineCompleted(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            return _completedBossCineLevels.Contains(levelId.Trim());
        }

        private void MarkBossCineCompleted(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            _completedBossCineLevels.Add(levelId.Trim());
        }

        private void ClearBossCineCompleted(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            _completedBossCineLevels.Remove(levelId.Trim());
        }

        private void RunWithSuppressedBossCineSend(Action action)
        {
            if (action == null)
                return;

            _suppressBossCineSendDepth++;
            try
            {
                action();
            }
            finally
            {
                _suppressBossCineSendDepth--;
            }
        }

        private bool HasAppliedBossHeroTeleport(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            return _appliedBossHeroTeleportLevels.Contains(levelId.Trim());
        }

        private void MarkBossHeroTeleportApplied(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            _appliedBossHeroTeleportLevels.Add(levelId.Trim());
        }

        private void SuppressRemoteBossDeathCineIfNeeded()
        {
            if (_netRole != NetRole.Client || _net == null || !_net.IsAlive)
                return;

            try
            {
                var game = dc.pr.Game.Class.ME;
                var cine = game?.curCine;
                if (cine == null || cine.destroyed)
                    return;

                if (cine is DeadBase || cine is RemoteDownedCorpse)
                    return;

                var typeName = cine.GetType().Name ?? string.Empty;
                if (!BossDeathCineTypeNames.Contains(typeName))
                    return;

                SuppressRemoteBossDeathCineState(cine);
            }
            catch
            {
            }
        }

        private bool ShouldSuppressRemoteBossDeathCineConstruction()
        {
            return _netRole == NetRole.Client && _net != null && _net.IsAlive;
        }

        private void SuppressRemoteBossDeathCineState(dc.GameCinematic? cine)
        {
            try
            {
                var game = dc.pr.Game.Class.ME;
                if (game != null && cine != null && ReferenceEquals(game.curCine, cine))
                    game.curCine = null;
            }
            catch
            {
            }

            try { cine?.destroy(); } catch { }
            try { cine?.disposeImmediately(); } catch { }
            try { me?.cancelSkillControlLock(); } catch { }
            try { me?.unlockControls(); } catch { }
            EnsureHeroVisibilityAfterRoomChange(me);
        }

        /// <summary>
        /// True while a boss-intro cinematic (letterbox room entrance) is running locally. The
        /// mob sync layer consults this to leave the boss alone while the cine script owns it.
        /// </summary>
        internal static bool IsLocalBossIntroCineActive()
        {
            try
            {
                var game = dc.pr.Game.Class.ME;
                var cine = game?.curCine;
                return cine != null && !cine.destroyed && IsBossOwnedCinematic(cine);
            }
            catch
            {
                return false;
            }
        }

        private dc.GameCinematic? _watchedBossIntroCine;
        private long _watchedBossIntroCineStartTick;
        private const double BossIntroCineHardCapSeconds = 30.0;

        /// <summary>
        /// Control-unlock guarantee: no boss-intro cinematic may hold the letterbox and the
        /// hero's controls past a hard cap. If one stalls (any reason — desynced boss state,
        /// broken script condition), it is force-released with the same routine used for
        /// suppressed remote death cines, so the player is never permanently frozen.
        /// </summary>
        private void EnforceBossIntroCineControlUnlock()
        {
            try
            {
                var game = dc.pr.Game.Class.ME;
                var cine = game?.curCine;
                var isIntro = cine != null && !cine.destroyed && IsBossOwnedCinematic(cine);
                if (!isIntro)
                {
                    _watchedBossIntroCine = null;
                    _watchedBossIntroCineStartTick = 0;
                    return;
                }

                if (!ReferenceEquals(_watchedBossIntroCine, cine))
                {
                    _watchedBossIntroCine = cine;
                    _watchedBossIntroCineStartTick = Stopwatch.GetTimestamp();
                    return;
                }

                if (_watchedBossIntroCineStartTick == 0)
                    return;

                var elapsed = Stopwatch.GetElapsedTime(_watchedBossIntroCineStartTick).TotalSeconds;
                if (elapsed < BossIntroCineHardCapSeconds)
                    return;

                _watchedBossIntroCine = null;
                _watchedBossIntroCineStartTick = 0;
                SuppressRemoteBossDeathCineState(cine);
                RestoreViewportToLocalHero();
                DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI.MultiplayerUI.PushSystemMessage(
                    "Boss intro stalled — skipping cinematic.",
                    4.0,
                    1.0);
            }
            catch
            {
            }
        }

        private string _lastTracedCineTypeName = string.Empty;

        /// <summary>
        /// Diagnostic (boss-trace gated): logs every active-cinematic type change so cines the
        /// mod does not yet classify (e.g. the TimeKeeper phase transition) can be identified
        /// from a session log and then sorted into the intro/transition/death sets.
        /// </summary>
        private void TraceActiveCineTypeChange()
        {
            if (!BossSyncDiag.Enabled)
                return;

            try
            {
                var cine = dc.pr.Game.Class.ME?.curCine;
                var name = cine != null && !cine.destroyed ? (cine.GetType().Name ?? string.Empty) : string.Empty;
                if (string.Equals(name, _lastTracedCineTypeName, StringComparison.Ordinal))
                    return;

                _lastTracedCineTypeName = name;
                if (name.Length > 0)
                    BossSyncDiag.Trace("active cinematic type={Type}", name);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Re-target the level viewport at the local hero. A force-released cinematic can leave
        /// the camera parked on its cine focus for the rest of the fight otherwise.
        /// </summary>
        private void RestoreViewportToLocalHero()
        {
            try
            {
                var hero = me;
                if (hero == null || hero.destroyed)
                    return;

                var viewport = hero._level?.viewport;
                if (viewport != null && !ReferenceEquals(viewport.tracked, hero))
                    viewport.track(hero, true);
            }
            catch
            {
            }
        }

        private void Hook__BeholderDeath__constructor__(Hook__BeholderDeath.orig___constructor__ orig, BeholderDeath e, Beholder boss)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, boss);
        }

        private void Hook__GiantDeath__constructor__(Hook__GiantDeath.orig___constructor__ orig, GiantDeath e, Hero heroTarget)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, heroTarget);
        }

        private void Hook__GiantDeath4__constructor__(Hook__GiantDeath4.orig___constructor__ orig, GiantDeath4 e, Hero heroTarget)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, heroTarget);
        }

        private void Hook__KillKingCinem__constructor__(Hook__KillKingCinem.orig___constructor__ orig, KillKingCinem e, HlAction tween)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, tween);
        }

        private void Hook__KillQueenCinem__constructor__(Hook__KillQueenCinem.orig___constructor__ orig, KillQueenCinem e)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e);
        }

        private void Hook__QueenDefeated__constructor__(Hook__QueenDefeated.orig___constructor__ orig, QueenDefeated e, Queen queen, HlAction dialogEnd)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, queen, dialogEnd);
        }

        private void Hook__KillDookuBeastCinem__constructor__(Hook__KillDookuBeastCinem.orig___constructor__ orig, KillDookuBeastCinem e)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e);
        }

        private void Hook__FakeKillDooku__constructor__(Hook__FakeKillDooku.orig___constructor__ orig, FakeKillDooku e, Hero manager, DookuManager instant, Ref<bool> instantRef)
        {
            // FakeKillDooku is Dracula's PHASE 1 -> 2 TRANSITION, not a death cine. Suppressing it
            // on clients left them locked in phase 1 while the host moved on to DookuBeast — the
            // reported phase split — and the cross-form state that followed hard-crashed the game.
            // Every peer must run it natively; it is classified as an intro-type cine so the boss
            // is quarantined from sync while it plays and the 30s watchdog guarantees release.
            orig(e, manager, instant, instantRef);
        }

        private void Hook__RichterDeath__constructor__(Hook__RichterDeath.orig___constructor__ orig, RichterDeath e, Hero lostBody, bool titleLib)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, lostBody, titleLib);
        }

        private void Hook__EndCollectorPreSmash__constructor__(Hook__EndCollectorPreSmash.orig___constructor__ orig, EndCollectorPreSmash e, dc.en.mob.Boss boss, HlAction lt)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, boss, lt);
        }

        private void Hook__SmashCinem__constructor__(Hook__SmashCinem.orig___constructor__ orig, SmashCinem e, bool hasKingSkin, HlAction endCb)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e, hasKingSkin, endCb);
        }

        private void Hook__EndCollectorPostSmash__constructor__(Hook__EndCollectorPostSmash.orig___constructor__ orig, EndCollectorPostSmash e)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e);
        }

        private void Hook__EndCollectorPostSmashKS__constructor__(Hook__EndCollectorPostSmashKS.orig___constructor__ orig, EndCollectorPostSmashKS e)
        {
            if (ShouldSuppressRemoteBossDeathCineConstruction())
            {
                SuppressRemoteBossDeathCineState(e);
                return;
            }

            orig(e);
        }
    }
}

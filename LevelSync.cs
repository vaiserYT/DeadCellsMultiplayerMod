using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using dc.cine;
using dc.en;
using dc.en.inter;
using dc.hl.types;
using dc.level;
using dc.pr;
using ModCore.Utilities;
using Rand = dc.libs.Rand;

namespace DeadCellsMultiplayerMod
{
    internal partial class GameDataSync
    {
        private static readonly object _levelGraphLock = new();
        private static readonly object _levelGraphReloadLock = new();
        private static readonly object _bossRuneReloadLock = new();
        private static readonly object _pendingBossRuneReloadLock = new();
        private static readonly Dictionary<string, LevelGraphSync> _remoteLevelGraphs = new(StringComparer.Ordinal);
        private static long _nextLevelGraphSequence;
        private static long _lastReceivedLevelGraphSequence;
        private static long _lastAppliedLevelGraphSequence;
        private const int MaxRemoteLevelGraphPayloadChars = 1_000_000;
        private const int MaxRemoteLevelGraphNodes = 4096;
        private const int MaxCachedRemoteLevelGraphs = 8;
        private const int RemoteLevelGraphTtlMs = 60_000;
        private static string? _lastLevelGraphReloadLevelId;
        private static string? _lastLevelGraphReloadPayload;
        private static long _lastLevelGraphReloadTick;
        private static string? _lastBossRuneReloadLevelId;
        private static long _lastBossRuneReloadTick;
        private static string? _pendingBossRuneReloadLevelId;
        private static int _pendingBossRuneReloadValue;
        private static long _pendingBossRuneReloadTick;
        private static bool _hasPendingBossRuneReload;
        private const int LevelGraphReloadThrottleMs = 3000;
        private const int BossRuneReloadThrottleMs = 3000;
        private const int PendingBossRuneReloadTtlMs = 15000;

        private sealed class LevelGraphSync
        {
            public int V { get; set; } = 2;
            public long Seq { get; set; }
            public string LevelId { get; set; } = string.Empty;
            [JsonIgnore]
            public long ReceivedAtTick { get; set; }
            public string? RootUid { get; set; }
            public int ZLinkId { get; set; }
            public double? PostGraphRandSeed { get; set; }
            public List<LevelGraphNodeSync> Nodes { get; set; } = new();
        }

        private sealed class LevelGraphNodeSync
        {
            public string Uid { get; set; } = string.Empty;
            public string? ParentUid { get; set; }
            public string? SubTeleportUid { get; set; }
            public bool IsZRoot { get; set; }
            public string RType { get; set; } = string.Empty;
            public int Group { get; set; }
            public int Id { get; set; }
            public int Flags { get; set; }
            public string? ForcedTemplateId { get; set; }
            public string? ExitLevel { get; set; }
            public string? ExitName { get; set; }
            public int? ExitColor { get; set; }
            public int ChildPriority { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int SpawnDistance { get; set; }
            public double FillerWeight { get; set; }
            public int? ParentLinkConstraint { get; set; }
            public List<string>? ChildrenUids { get; set; }
            public List<string>? ZChildrenUids { get; set; }
            public List<int>? Npcs { get; set; }
            public List<LevelGraphZLinkSync>? ZLinks { get; set; }
            public LevelGraphGenDataSync? GenData { get; set; }
        }

        private sealed class LevelGraphZLinkSync
        {
            public int Id { get; set; }
            public string DestUid { get; set; } = string.Empty;
            public string? DoorId { get; set; }
            public int? ContentClue { get; set; }
        }

        private sealed class LevelGraphGenDataSync
        {
            public string? SpecificBiome { get; set; }
            public bool? ZDoorLock { get; set; }
            public bool? ForcePauseTimer { get; set; }
            public bool? ShouldBeFlipped { get; set; }
            public int? GenSubTeleportTo { get; set; }
            public LevelGraphZDoorTypeSync? ZDoorType { get; set; }
        }

        private sealed class LevelGraphZDoorTypeSync
        {
            public int RawIndex { get; set; }
            public int? IntParam0 { get; set; }
            public double? DoubleParam0 { get; set; }
        }

        public static void ReceiveLevelGraph(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;
            if (payload.Length > MaxRemoteLevelGraphPayloadChars)
            {
                _log?.Warning("[NetMod] Rejected oversized level graph payload ({Length} chars)", payload.Length);
                return;
            }

            try
            {
                var graph = JsonSerializer.Deserialize<LevelGraphSync>(payload);
                if (graph == null || string.IsNullOrWhiteSpace(graph.LevelId))
                    return;
                if (graph.LevelId.Length > 128 || graph.Nodes == null || graph.Nodes.Count == 0 || graph.Nodes.Count > MaxRemoteLevelGraphNodes)
                {
                    _log?.Warning(
                        "[NetMod] Rejected invalid level graph level={LevelId} nodes={Count}",
                        graph.LevelId,
                        graph.Nodes?.Count ?? -1);
                    return;
                }

                graph.ReceivedAtTick = Environment.TickCount64;
                lock (_levelGraphLock)
                {
                    PruneRemoteLevelGraphsLocked(graph.ReceivedAtTick);
                    if (graph.Seq > 0 && graph.Seq <= _lastReceivedLevelGraphSequence)
                    {
                        _log?.Debug(
                            "[NetMod] Ignored stale level graph seq={Seq} last={Last} level={LevelId}",
                            graph.Seq,
                            _lastReceivedLevelGraphSequence,
                            graph.LevelId);
                        return;
                    }

                    if (graph.Seq > 0)
                        _lastReceivedLevelGraphSequence = graph.Seq;
                    _remoteLevelGraphs[graph.LevelId] = graph;
                    TrimRemoteLevelGraphCacheLocked();
                    Monitor.PulseAll(_levelGraphLock);
                }

                _log?.Information(
                    "[NetMod] Received level graph for {LevelId} ({Count} nodes, seq={Seq})",
                    graph.LevelId,
                    graph.Nodes.Count,
                    graph.Seq);
                TryScheduleLevelGraphReloadForLevel(graph.LevelId, payload);
                // Graph and boss-rune packets can arrive in either order. This path is coalesced,
                // throttled, and suppressed during downed/restart transitions by the reload guard.
                TryScheduleBossRuneReloadForLevel(graph.LevelId);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to parse level graph sync: {Message}", ex.Message);
            }
        }

        private static void PruneRemoteLevelGraphsLocked(long now)
        {
            if (_remoteLevelGraphs.Count == 0)
                return;

            List<string>? expired = null;
            foreach (var pair in _remoteLevelGraphs)
            {
                var receivedAt = pair.Value?.ReceivedAtTick ?? 0;
                if (receivedAt > 0 && now - receivedAt > RemoteLevelGraphTtlMs)
                {
                    expired ??= new List<string>();
                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
                return;
            foreach (var key in expired)
                _remoteLevelGraphs.Remove(key);
        }

        private static void TrimRemoteLevelGraphCacheLocked()
        {
            while (_remoteLevelGraphs.Count > MaxCachedRemoteLevelGraphs)
            {
                string? oldestKey = null;
                long oldestTick = long.MaxValue;
                foreach (var pair in _remoteLevelGraphs)
                {
                    var tick = pair.Value?.ReceivedAtTick ?? 0;
                    if (oldestKey == null || tick < oldestTick)
                    {
                        oldestKey = pair.Key;
                        oldestTick = tick;
                    }
                }

                if (oldestKey == null)
                    break;
                _remoteLevelGraphs.Remove(oldestKey);
            }
        }

        internal static void TryScheduleBossRuneReloadForCurrentLevel()
        {
            var currentLevelId = TryGetCurrentLevelId();
            if (string.IsNullOrWhiteSpace(currentLevelId))
                return;

            lock (_levelGraphLock)
            {
                if (!_remoteLevelGraphs.ContainsKey(currentLevelId))
                    return;
            }

            TryScheduleBossRuneReloadForLevel(currentLevelId);
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

        private static void TryScheduleBossRuneReloadForLevel(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive || net.IsHost)
                return;

            GameMenu.EnqueueMainThreadCoalesced("level:boss-rune-reload:" + levelId, () =>
            {
                try
                {
                    TryTriggerBossRuneReload(levelId);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to process boss-rune reload for {LevelId}: {Message}", levelId, ex.Message);
                }
            });
        }

        private static void TryScheduleLevelGraphReloadForLevel(string levelId, string payload)
        {
            if (string.IsNullOrWhiteSpace(levelId) || string.IsNullOrWhiteSpace(payload))
                return;

            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive || net.IsHost)
                return;

            GameMenu.EnqueueMainThreadCoalesced("level:graph-reload:" + levelId, () =>
            {
                try
                {
                    TryTriggerLevelGraphReload(levelId, payload);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to process level-graph reload for {LevelId}: {Message}", levelId, ex.Message);
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

        private static void TryTriggerLevelGraphReload(string graphLevelId, string payload)
        {
            if (string.IsNullOrWhiteSpace(graphLevelId) || string.IsNullOrWhiteSpace(payload))
                return;

            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive || net.IsHost)
                return;

            if (ShouldSuppressClientLevelReload())
            {
                _log?.Information("[NetMod] Skipping level-graph reload for {LevelId}: client downed/restart pending", graphLevelId);
                return;
            }

            var hero = ModEntry.me;
            var level = hero?._level;
            if (hero == null || level == null || level.map == null)
                return;

            var currentLevelId = level.map.id?.ToString();
            if (!string.Equals(currentLevelId, graphLevelId, StringComparison.Ordinal))
                return;

            lock (_levelGraphLock)
            {
                if (!_remoteLevelGraphs.ContainsKey(graphLevelId))
                    return;
            }

            if (!TryBeginLevelGraphReload(graphLevelId, payload))
                return;

            var targetLevelId = ResolveBossRuneReloadTargetLevelId(level, graphLevelId);
            var (offsetCx, offsetCy) = ComputeCurrentLevelReloadOffsets(hero, level);
            var reload = LevelTransition.Class.reloadAfterBossRuneModif;
            if (reload == null)
            {
                _log?.Warning("[NetMod] Missing LevelTransition.reloadAfterBossRuneModif for graph reload {LevelId}", targetLevelId);
                return;
            }

            _ = reload(targetLevelId.AsHaxeString(), offsetCx, offsetCy);
            _log?.Information(
                "[NetMod] Triggered level-graph reload for {LevelId} offset=({OffsetCx},{OffsetCy})",
                targetLevelId,
                offsetCx,
                offsetCy);
        }

        private static bool TryBeginLevelGraphReload(string levelId, string payload)
        {
            var now = Environment.TickCount64;
            lock (_levelGraphReloadLock)
            {
                if (string.Equals(_lastLevelGraphReloadLevelId, levelId, StringComparison.Ordinal) &&
                    string.Equals(_lastLevelGraphReloadPayload, payload, StringComparison.Ordinal) &&
                    now - _lastLevelGraphReloadTick < LevelGraphReloadThrottleMs)
                {
                    return false;
                }

                _lastLevelGraphReloadLevelId = levelId;
                _lastLevelGraphReloadPayload = payload;
                _lastLevelGraphReloadTick = now;
                return true;
            }
        }

        private static void TryTriggerBossRuneReload(string graphLevelId)
        {
            if (string.IsNullOrWhiteSpace(graphLevelId))
                return;

            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive || net.IsHost)
                return;

            if (ShouldSuppressClientLevelReload())
            {
                _log?.Information("[NetMod] Skipping boss-rune reload for {LevelId}: client downed/restart pending", graphLevelId);
                return;
            }

            var hero = ModEntry.me;
            var level = hero?._level;
            var user = dc.Main.Class.ME?.user ?? level?.game?.user;
            if (hero == null || level == null || level.map == null || user == null)
                return;

            var currentLevelId = level.map.id?.ToString();
            if (!string.Equals(currentLevelId, graphLevelId, StringComparison.Ordinal))
                return;

            if (!TryGetRemoteBossRune(out var remoteBossRune))
                return;

            var localBossRune = GetEffectiveBossRune(user);
            var forceByPending = ConsumePendingBossRuneReloadIfMatch(graphLevelId, remoteBossRune);
            if (!forceByPending && localBossRune == remoteBossRune)
                return;

            _log?.Information(
                "[NetMod] Boss-rune graph reload candidate level={LevelId} local={LocalBossRune} remote={RemoteBossRune} pending={Pending}",
                graphLevelId,
                localBossRune,
                remoteBossRune,
                forceByPending);

            if (!TryBeginBossRuneReload(graphLevelId))
                return;

            ApplyRemoteBossRune(user, remoteBossRune);

            var (offsetCx, offsetCy) = ComputeBossRuneReloadOffsets(hero, level);
            var targetLevelId = ResolveBossRuneReloadTargetLevelId(level, graphLevelId);

            var reload = LevelTransition.Class.reloadAfterBossRuneModif;
            if (reload == null)
            {
                _log?.Warning("[NetMod] Missing LevelTransition.reloadAfterBossRuneModif for {LevelId}", targetLevelId);
                return;
            }

            ModEntry.PrepareAndDisposeRemoteKingsForBossCellReload(
                "client-boss-rune-reload:" + targetLevelId);
            _ = reload(targetLevelId.AsHaxeString(), offsetCx, offsetCy);
            _log?.Information(
                "[NetMod] Triggered boss-rune reload for {LevelId} offset=({OffsetCx},{OffsetCy}) bossRune={BossRune}",
                targetLevelId,
                offsetCx,
                offsetCy,
                remoteBossRune);
        }

        private static bool TryBeginBossRuneReload(string levelId)
        {
            var now = Environment.TickCount64;
            lock (_bossRuneReloadLock)
            {
                if (string.Equals(_lastBossRuneReloadLevelId, levelId, StringComparison.Ordinal) &&
                    now - _lastBossRuneReloadTick < BossRuneReloadThrottleMs)
                {
                    return false;
                }

                _lastBossRuneReloadLevelId = levelId;
                _lastBossRuneReloadTick = now;
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

        private static string ResolveBossRuneReloadTargetLevelId(Level level, string fallbackLevelId)
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

        public static void SendLevelGraph(string levelId, RoomNode? root, LevelStruct? graph, Rand? rng, NetNode? net)
        {
            if (net == null || !net.IsAlive)
            {
                _log?.Information("[NetMod] Skip level graph send for {LevelId}: net unavailable", levelId);
                return;
            }

            if (graph == null || string.IsNullOrWhiteSpace(levelId))
            {
                _log?.Warning("[NetMod] Skip level graph send: invalid graph/levelId (level={LevelId})", levelId);
                return;
            }

            try
            {
                var sync = CaptureLevelGraph(levelId, graph);
                if (sync == null)
                {
                    _log?.Warning("[NetMod] CaptureLevelGraph returned null for {LevelId} (allLen={AllLen})", levelId, graph.all?.length ?? -1);
                    return;
                }

                if (sync.Nodes.Count == 0)
                {
                    _log?.Warning("[NetMod] Captured empty level graph for {LevelId} (allLen={AllLen})", levelId, graph.all?.length ?? -1);
                    return;
                }

                try
                {
                    sync.RootUid = root?.uid?.ToString();
                }
                catch
                {
                }

                try
                {
                    if (rng != null)
                        sync.PostGraphRandSeed = rng.seed;
                }
                catch
                {
                }

                sync.Seq = Interlocked.Increment(ref _nextLevelGraphSequence);
                var json = JsonSerializer.Serialize(sync);
                net.SendLevelGraph(levelId, json);
                _log?.Information("[NetNode] Sent level graph for {LevelId} ({Count} nodes, seq={Seq}, postRand={PostRand})",
                    levelId,
                    sync.Nodes.Count,
                    sync.Seq,
                    sync.PostGraphRandSeed.HasValue ? sync.PostGraphRandSeed.Value.ToString(CultureInfo.InvariantCulture) : "n/a");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send level graph for {LevelId}: {Message}", levelId, ex.Message);
            }
        }

        public static bool TryApplyRemoteLevelGraph(string levelId, LevelStruct? graph, Rand? rng, int timeoutMs, out RoomNode? appliedRoot, out string reason)
        {
            reason = string.Empty;
            appliedRoot = null;
            if (graph == null || string.IsNullOrWhiteSpace(levelId))
            {
                reason = "invalid arguments";
                return false;
            }

            if (!TryWaitGetRemoteLevelGraph(levelId, timeoutMs, out var remoteGraph))
            {
                reason = "remote graph not received";
                return false;
            }

            if (remoteGraph == null || remoteGraph.Nodes == null || remoteGraph.Nodes.Count == 0)
            {
                reason = "remote graph payload empty";
                return false;
            }

            var applied = ApplyLevelGraph(graph, remoteGraph, out appliedRoot, out reason);
            if (applied && rng != null && remoteGraph.PostGraphRandSeed.HasValue)
            {
                try
                {
                    rng.seed = remoteGraph.PostGraphRandSeed.Value;
                }
                catch (Exception ex)
                {
                    applied = false;
                    reason = "failed to apply post-graph rand seed: " + ex.Message;
                }
            }

            ConsumeRemoteLevelGraph(levelId);
            if (!applied)
                return false;

            // FTL / HL cast correlation: pair with host "Sent level graph" and client combat/restart logs.
            // Repro surface: client PrisonStart (or any level) with remote graph, host_restart, then dive/combat.
            try
            {
                var rootUid = remoteGraph.RootUid ?? "?";
                try
                {
                    if (appliedRoot != null)
                        rootUid = appliedRoot.uid?.ToString() ?? rootUid;
                }
                catch
                {
                }

                _log?.Information(
                    "[NetMod] Remote level graph applied (FTL correlation) levelId={LevelId} nodes={Count} rootUid={RootUid} postRandSeed={PostRand} postRandApplied={PostRandApplied}",
                    levelId,
                    remoteGraph.Nodes.Count,
                    rootUid,
                    remoteGraph.PostGraphRandSeed.HasValue
                        ? remoteGraph.PostGraphRandSeed.Value.ToString(CultureInfo.InvariantCulture)
                        : "n/a",
                    rng != null && remoteGraph.PostGraphRandSeed.HasValue);
            }
            catch
            {
            }

            return true;
        }

        private static bool TryWaitGetRemoteLevelGraph(string levelId, int timeoutMs, out LevelGraphSync? graph)
        {
            graph = null;
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            lock (_levelGraphLock)
            {
                while (true)
                {
                    var now = Environment.TickCount64;
                    PruneRemoteLevelGraphsLocked(now);
                    if (_remoteLevelGraphs.TryGetValue(levelId, out var found))
                    {
                        graph = found;
                        return true;
                    }

                    var remaining = deadline - now;
                    if (timeoutMs <= 0 || remaining <= 0)
                        return false;

                    // LGRAPH is parsed by the network fast path, so waiting on the graph lock is
                    // enough. Processing arbitrary main-thread actions from inside LevelGen caused
                    // re-entrant level/dispose/ghost work while the graph was only half built.
                    Monitor.Wait(_levelGraphLock, (int)Math.Min(remaining, 250));
                }
            }
        }

        private static bool TryGetRemoteLevelGraph(string levelId, out LevelGraphSync? graph)
        {
            lock (_levelGraphLock)
            {
                PruneRemoteLevelGraphsLocked(Environment.TickCount64);
                if (_remoteLevelGraphs.TryGetValue(levelId, out var found))
                {
                    graph = found;
                    return true;
                }
            }

            graph = null;
            return false;
        }

        private static void ConsumeRemoteLevelGraph(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            lock (_levelGraphLock)
            {
                if (_remoteLevelGraphs.TryGetValue(levelId, out var graph) && graph.Seq > 0)
                    _lastAppliedLevelGraphSequence = Math.Max(_lastAppliedLevelGraphSequence, graph.Seq);
                _remoteLevelGraphs.Remove(levelId);
            }
        }

        internal static void ResetTransientNetworkState()
        {
            lock (_levelSeedLock)
            {
                _remoteLevelId = null;
                _remoteLevelSeed = null;
            }

            lock (_levelGraphLock)
            {
                _remoteLevelGraphs.Clear();
                _lastReceivedLevelGraphSequence = 0;
                _lastAppliedLevelGraphSequence = 0;
                Monitor.PulseAll(_levelGraphLock);
            }
            Interlocked.Exchange(ref _nextLevelGraphSequence, 0);

            lock (_levelGraphReloadLock)
            {
                _lastLevelGraphReloadLevelId = null;
                _lastLevelGraphReloadPayload = null;
                _lastLevelGraphReloadTick = 0;
            }
            lock (_bossRuneReloadLock)
            {
                _lastBossRuneReloadLevelId = null;
                _lastBossRuneReloadTick = 0;
            }
            ClearPendingBossRuneReloadState();
        }
    }
}

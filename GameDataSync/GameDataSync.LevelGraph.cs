using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using dc.level;
using ModCore.Utilities;
using Rand = dc.libs.Rand;

namespace DeadCellsMultiplayerMod
{
    internal partial class GameDataSync
    {
        private static readonly object _levelGraphLock = new();
        private static readonly Dictionary<string, LevelGraphSync> _remoteLevelGraphs = new(StringComparer.Ordinal);
        private static long _nextLevelGraphSequence;
        // Graph ordering is per level, not global. A challenge room/sublevel can legitimately
        // arrive after the next biome's graph (or vice versa) during late-join replay. A single
        // global "last sequence" incorrectly discarded valid graphs for other level ids and left
        // the client generating its own world.
        private static readonly Dictionary<string, long> _lastReceivedLevelGraphSequenceByLevelId = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> _lastAppliedLevelGraphSequenceByLevelId = new(StringComparer.Ordinal);
        private const int MaxRemoteLevelGraphPayloadChars = 1_000_000;
        private const int MaxRemoteLevelGraphNodes = 4096;
        private const int MaxCachedRemoteLevelGraphs = 8;

        private sealed class LevelGraphSync
        {
            public int V { get; set; } = 1;
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
                    if (graph.Seq > 0)
                    {
                        if (_lastAppliedLevelGraphSequenceByLevelId.TryGetValue(graph.LevelId, out var appliedSeq) &&
                            graph.Seq <= appliedSeq)
                        {
                            _log?.Debug(
                                "[NetMod] Ignored already-applied level graph seq={Seq} applied={Applied} level={LevelId}",
                                graph.Seq,
                                appliedSeq,
                                graph.LevelId);
                            return;
                        }

                        if (_lastReceivedLevelGraphSequenceByLevelId.TryGetValue(graph.LevelId, out var lastSeq) &&
                            graph.Seq <= lastSeq)
                        {
                            _log?.Debug(
                                "[NetMod] Ignored stale level graph seq={Seq} last={Last} level={LevelId}",
                                graph.Seq,
                                lastSeq,
                                graph.LevelId);
                            return;
                        }

                        _lastReceivedLevelGraphSequenceByLevelId[graph.LevelId] = graph.Seq;
                    }

                    _remoteLevelGraphs[graph.LevelId] = graph;
                    TrimRemoteLevelGraphCacheLocked();
                    Monitor.PulseAll(_levelGraphLock);
                }

                _log?.Information(
                    "[NetMod] Received level graph for {LevelId} ({Count} nodes, seq={Seq})",
                    graph.LevelId,
                    graph.Nodes.Count,
                    graph.Seq);

                // Lobby auto-start waits on HasPendingRemoteLevelGraph; re-arm if seed/exec
                // already arrived before this LGRAPH.
                GameMenu.NotifyClientLaunchPrerequisiteProgress();

                var reason = LevelReloadReason.GraphUpdated;
                // Graph and boss-rune packets can arrive in either order. Fold a pending boss-rune
                // change into the same coalesced reload so only one reloadAfterBossRuneModif runs.
                if (HasPendingBossRuneReloadForLevel(graph.LevelId))
                    reason |= LevelReloadReason.BossRuneChanged;
                ScheduleLevelReload(graph.LevelId, reason, payload);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to parse level graph sync: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// The received graph is the ONLY way a client can reproduce the host's procedural layout;
        /// the run seed alone does not determine it. Expiring it on a wall-clock TTL (it used to be
        /// 60s, and the expiry also ran from inside the wait loop) silently converted "the client
        /// took a while to reach LevelGen" into "the client generated an unrelated map with
        /// different rooms, passages and exits" - the mid-run-join and slow-client desync.
        ///
        /// Cache pressure is therefore bounded by count (LRU) only, never by age: entries are
        /// consumed on apply, replaced per level generation, and cleared wholesale by
        /// <see cref="ResetTransientNetworkState"/>, so a stale entry cannot outlive its session.
        /// </summary>
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

        internal static bool HasPendingRemoteLevelGraph(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            lock (_levelGraphLock)
            {
                return _remoteLevelGraphs.ContainsKey(levelId);
            }
        }

        private static bool HasRemoteLevelGraphCached(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;

            lock (_levelGraphLock)
            {
                return _remoteLevelGraphs.ContainsKey(levelId);
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

            if (applied && remoteGraph.Seq > 0)
            {
                lock (_levelGraphLock)
                {
                    _lastAppliedLevelGraphSequenceByLevelId[levelId] = remoteGraph.Seq;
                }
            }

            // Only consume a graph after it was successfully bound. If application failed
            // because the local LevelStruct was still being replaced, keep the authoritative
            // payload cached: the already-scheduled in-level reload can retry it and a repeated
            // host cache response with the same sequence does not have to be accepted again.
            if (!applied)
                return false;

            ConsumeRemoteLevelGraph(levelId);

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
            var nextRequestAt = 0L;

            while (true)
            {
                var now = Environment.TickCount64;
                lock (_levelGraphLock)
                {
                    if (_remoteLevelGraphs.TryGetValue(levelId, out var found))
                    {
                        graph = found;
                        return true;
                    }
                }

                var remaining = deadline - now;
                if (timeoutMs <= 0 || remaining <= 0)
                    return false;

                // Reliable delivery can still be interrupted by a reconnect, a transition race or
                // a host send that happened just before this client finished switching levels.
                // Request the cached authoritative graph periodically instead of ever silently
                // committing a locally generated layout.
                if (now >= nextRequestAt)
                {
                    try { GameMenu.NetRef?.RequestLevelGraph(levelId); } catch { }
                    nextRequestAt = now + 1000;
                }

                lock (_levelGraphLock)
                {
                    if (_remoteLevelGraphs.TryGetValue(levelId, out var found))
                    {
                        graph = found;
                        return true;
                    }

                    Monitor.Wait(_levelGraphLock, (int)Math.Min(Math.Max(1, remaining), 250));
                }
            }
        }

        private static void ConsumeRemoteLevelGraph(string levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return;

            lock (_levelGraphLock)
            {
                _remoteLevelGraphs.Remove(levelId);
            }
        }

        internal static void ResetGeneratedLevelSyncForRestart()
        {
            // A same-run restart revisits the same level ids. Never let the client's old cached
            // seed/graph satisfy the new generation before the host has produced the replacement.
            lock (_levelSeedLock)
            {
                _remoteLevelSeedsByLevelId.Clear();
                Monitor.PulseAll(_levelSeedLock);
            }

            lock (_levelGraphLock)
            {
                _remoteLevelGraphs.Clear();
                Monitor.PulseAll(_levelGraphLock);
            }

            ResetLevelReloadState();
            ClearPendingBossRuneReloadState();
        }

        internal static void ResetTransientNetworkState()
        {
            lock (_levelSeedLock)
            {
                _remoteLevelSeedsByLevelId.Clear();
                _lastReceivedLevelSeedSequenceByLevelId.Clear();
                _lastAppliedLevelSeedSequenceByLevelId.Clear();
                Monitor.PulseAll(_levelSeedLock);
            }

            lock (_levelGraphLock)
            {
                _remoteLevelGraphs.Clear();
                _lastReceivedLevelGraphSequenceByLevelId.Clear();
                _lastAppliedLevelGraphSequenceByLevelId.Clear();
                Monitor.PulseAll(_levelGraphLock);
            }
            Interlocked.Exchange(ref _nextLevelGraphSequence, 0);

            ResetLevelReloadState();
            ClearPendingBossRuneReloadState();
            ClearRemoteSerializerState(restoreLocal: true, "transient_network_reset");
        }
    }
}

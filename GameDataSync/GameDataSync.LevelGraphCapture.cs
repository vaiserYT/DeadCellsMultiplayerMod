using dc;
using dc.haxe.ds;
using dc.level;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using dc.haxe;
using dc.hl.types;


namespace DeadCellsMultiplayerMod
{
    internal partial class GameDataSync
    {

        private static LevelGraphSync? CaptureLevelGraph(string levelId, LevelStruct graph)
        {
            var sync = new LevelGraphSync
            {
                V = 1,
                LevelId = levelId,
                ZLinkId = graph.zLinkId
            };

            var seenUids = new HashSet<string>(StringComparer.Ordinal);
            var all = graph.all;
            if (all != null)
            {
                for (int i = 0; i < all.length; i++)
                {
                    TryCaptureLevelGraphNode(all.getDyn(i), sync, seenUids);
                }
            }

            if (sync.Nodes.Count == 0 && graph.nodes != null)
            {
                try
                {
                    var keys = graph.nodes.keys();
                    while (keys.hasNext.Invoke())
                    {
                        var key = keys.next.Invoke();
                        if (key == null)
                            continue;
                        TryCaptureLevelGraphNode(graph.nodes.get(key), sync, seenUids);
                    }
                }
                catch
                {
                }
            }

            return sync;
        }

        private static void TryCaptureLevelGraphNode(object? candidate, LevelGraphSync sync, HashSet<string> seenUids)
        {
            // One unsupported node property (e.g. a generated proxy method missing in this game
            // build) must never abort the whole graph capture. Skip the node, keep the topology.
            try
            {
                if (candidate is not RoomNode node)
                    return;

                var nodeSync = CaptureLevelGraphNode(node);
                if (nodeSync == null || string.IsNullOrWhiteSpace(nodeSync.Uid))
                    return;

                if (!seenUids.Add(nodeSync.Uid))
                    return;

                sync.Nodes.Add(nodeSync);
            }
            catch
            {
            }
        }

        private static LevelGraphNodeSync? CaptureLevelGraphNode(RoomNode node)
        {
            var uid = node.uid?.ToString();
            var rType = node.rType?.ToString();
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(rType))
                return null;

            int? parentLinkConstraint = null;
            try
            {
                if (node.parentLinkConstraint is HaxeEnum plc)
                    parentLinkConstraint = plc.RawIndex;
            }
            catch
            {
            }

            return new LevelGraphNodeSync
            {
                Uid = uid,
                ParentUid = node.parent?.uid?.ToString(),
                SubTeleportUid = node.subTeleportTo?.uid?.ToString(),
                IsZRoot = node.isZRoot,
                RType = rType,
                Group = node.group,
                Id = node.id,
                Flags = node.flags,
                ForcedTemplateId = node.forcedTemplate?.id?.ToString(),
                ExitLevel = node.exitLevel?.ToString(),
                ExitName = node.exitName?.ToString(),
                ExitColor = node.exitColor,
                ChildPriority = node.childPriority,
                X = node.x,
                Y = node.y,
                SpawnDistance = node.spawnDistance,
                FillerWeight = node.fillerWeight,
                ParentLinkConstraint = parentLinkConstraint,
                ChildrenUids = CaptureRoomNodeUids(node.children),
                ZChildrenUids = CaptureRoomNodeUids(node.zChildren),
                Npcs = CaptureNpcIds(node.npcs),
                ZLinks = CaptureZLinks(node.zLinks),
                GenData = TryCaptureLevelGraphGenData(node)
            };
        }

        private static List<string> CaptureRoomNodeUids(ArrayObj? nodes)
        {
            var result = new List<string>();
            if (nodes == null)
                return result;

            for (int i = 0; i < nodes.length; i++)
            {
                try
                {
                    if (nodes.getDyn(i) is not RoomNode node)
                        continue;

                    var uid = node.uid?.ToString();
                    if (!string.IsNullOrEmpty(uid))
                        result.Add(uid);
                }
                catch
                {
                }
            }

            return result;
        }

        private static List<int> CaptureNpcIds(ArrayObj? npcs)
        {
            var result = new List<int>();
            if (npcs == null)
                return result;

            for (int i = 0; i < npcs.length; i++)
            {
                try
                {
                    if (npcs.getDyn(i) is NpcId npcId)
                        result.Add((int)npcId.Index);
                }
                catch
                {
                }
            }

            return result;
        }

        private static List<LevelGraphZLinkSync> CaptureZLinks(ArrayObj? zLinks)
        {
            var result = new List<LevelGraphZLinkSync>();
            if (zLinks == null)
                return result;

            for (int i = 0; i < zLinks.length; i++)
            {
                try
                {
                    var link = zLinks.getDyn(i) as virtual_contentClue_dest_doorId_id_;
                    if (link == null)
                        continue;

                    var destUid = link.dest?.uid?.ToString();
                    if (string.IsNullOrWhiteSpace(destUid))
                        continue;

                    int? clue = null;
                    try
                    {
                        var contentClue = link.contentClue;
                        if (contentClue is HaxeEnum haxeEnum)
                            clue = haxeEnum.RawIndex;
                    }
                    catch
                    {
                    }

                    result.Add(new LevelGraphZLinkSync
                    {
                        Id = link.id,
                        DestUid = destUid,
                        DoorId = link.doorId?.ToString(),
                        ContentClue = clue
                    });
                }
                catch
                {
                }
            }

            return result;
        }

        // RoomNode.genData is exposed through a generated Haxe virtual proxy whose CLR type name
        // can change when the game's proxy metadata changes. Keeping that generated type in this
        // method signature made otherwise-compatible builds fail at compile time (CS0246). Use
        // dynamic here so capture remains tied to the member contract rather than one proxy name;
        // every optional read below is already isolated by try/catch for version tolerance.
        private static LevelGraphGenDataSync? CaptureLevelGraphGenData(dynamic genData)
        {
            if (genData == null)
                return null;

            var result = new LevelGraphGenDataSync();
            var hasAny = false;

            try
            {
                var v = genData.specificBiome?.ToString();
                if (!string.IsNullOrWhiteSpace(v))
                {
                    result.SpecificBiome = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var v = genData.zDoorLock;
                if (v.HasValue)
                {
                    result.ZDoorLock = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var v = genData.forcePauseTimer;
                if (v.HasValue)
                {
                    result.ForcePauseTimer = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var v = genData.shouldBeFlipped;
                if (v.HasValue)
                {
                    result.ShouldBeFlipped = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var v = genData.subTeleportTo;
                if (v.HasValue)
                {
                    result.GenSubTeleportTo = v;
                    hasAny = true;
                }
            }
            catch { }

            try
            {
                var zDoorType = CaptureZDoorType(genData.zDoorType);
                if (zDoorType != null)
                {
                    result.ZDoorType = zDoorType;
                    hasAny = true;
                }
            }
            catch { }

            return hasAny ? result : null;
        }

        /// <summary>
        /// <see cref="RoomNode.genData"/> is a generated proxy virtual; a game build that renamed or
        /// dropped <c>get_genData()</c> makes the plain property access throw "Method not found"
        /// BEFORE <see cref="CaptureLevelGraphGenData"/>'s dynamic reads ever run, which aborted the
        /// entire graph capture and silently starved the client of the authoritative layout (host
        /// logged "Failed to send level graph", client logged WORLD DESYNC + abort). The captured
        /// GenData is informational only (the apply path copies the client's own local genData), so
        /// a failed read must degrade to null, never kill the capture.
        /// </summary>
        private static LevelGraphGenDataSync? TryCaptureLevelGraphGenData(RoomNode node)
        {
            try
            {
                return CaptureLevelGraphGenData(node.genData);
            }
            catch
            {
                return null;
            }
        }

        private static LevelGraphZDoorTypeSync? CaptureZDoorType(ZDoorType? zDoorType)
        {
            if (zDoorType is null)
                return null;

            if (zDoorType is not HaxeEnum haxeEnum)
                return null;

            var result = new LevelGraphZDoorTypeSync
            {
                RawIndex = haxeEnum.RawIndex
            };

            switch (zDoorType)
            {
                case ZDoorType.BossRune bossRune:
                    result.IntParam0 = bossRune.Param0;
                    break;
                case ZDoorType.PerfectKills perfectKills:
                    result.IntParam0 = perfectKills.Param0;
                    break;
                case ZDoorType.Timed timed:
                    result.DoubleParam0 = timed.Param0;
                    break;
            }

            return result;
        }


        private static bool ApplyLevelGraph(LevelStruct target, LevelGraphSync sync, out RoomNode? rebuiltRoot, out string reason)
        {
            rebuiltRoot = null;
            reason = string.Empty;
            if (sync.Nodes == null || sync.Nodes.Count == 0)
            {
                reason = "no nodes";
                return false;
            }

            try
            {
                var localNodesByUid = CaptureExistingRoomNodesByUid(target);

                target.nodes = new StringMap();
                target.all = (ArrayObj)ArrayUtils.CreateDyn().array;
                target.zLinkId = sync.ZLinkId;

                var byUid = new Dictionary<string, RoomNode>(StringComparer.Ordinal);
                var syncByUid = new Dictionary<string, LevelGraphNodeSync>(StringComparer.Ordinal);
                var orderedNodes = new List<RoomNode>(sync.Nodes.Count);

                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid) || string.IsNullOrWhiteSpace(src.RType))
                        continue;

                    if (byUid.ContainsKey(src.Uid))
                        continue;

                    var ctorGroup = src.Group;
                    var node = new RoomNode(src.RType.AsHaxeString(), Ref<int>.From(ref ctorGroup), target, null);
                    node.uid = src.Uid.AsHaxeString();
                    node.rType = src.RType.AsHaxeString();
                    node.group = src.Group;
                    node.id = src.Id;
                    node.flags = src.Flags;
                    node.childPriority = src.ChildPriority;
                    node.x = src.X;
                    node.y = src.Y;
                    node.spawnDistance = src.SpawnDistance;
                    node.fillerWeight = src.FillerWeight;
                    node.exitLevel = string.IsNullOrWhiteSpace(src.ExitLevel) ? null : src.ExitLevel.AsHaxeString();
                    node.exitName = string.IsNullOrWhiteSpace(src.ExitName) ? null : src.ExitName.AsHaxeString();
                    node.exitColor = src.ExitColor;

                    if (!string.IsNullOrWhiteSpace(src.ForcedTemplateId))
                    {
                        try
                        {
                            node.forceTemplate(src.ForcedTemplateId.AsHaxeString());
                        }
                        catch
                        {
                            try
                            {
                                node.forcedTemplate = (virtual_active_flags_group_id_type_)(object)dc.Data.Class.room.byId.get(src.ForcedTemplateId.AsHaxeString());
                            }
                            catch
                            {
                            }
                        }

                        // Keep payload fields authoritative even if native forceTemplate mutates them.
                        node.rType = src.RType.AsHaxeString();
                        node.group = src.Group;
                    }

                    if (src.ParentLinkConstraint.HasValue)
                    {
                        var constraint = CreateLinkConstraintFromIndex(src.ParentLinkConstraint.Value);
                        if (constraint is not null)
                            node.parentLinkConstraint = constraint;
                    }

                    if (src.Npcs != null)
                    {
                        for (int n = 0; n < src.Npcs.Count; n++)
                        {
                            var npc = CreateNpcIdFromIndex(src.Npcs[n]);
                            if (npc is not null)
                                node.npcs.pushDyn(npc);
                        }
                    }

                    if (localNodesByUid.TryGetValue(src.Uid, out var localNode))
                    {
                        try
                        {
                            if (localNode.genData != null)
                                node.genData = localNode.genData;
                        }
                        catch
                        {
                        }
                    }

                    orderedNodes.Add(node);
                    byUid[src.Uid] = node;
                    syncByUid[src.Uid] = src;
                }

                var earlyAll = ArrayUtils.CreateDyn();
                var earlyNodes = new StringMap();
                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;
                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;
                    earlyAll.array.pushDyn(node);
                    earlyNodes.set(src.Uid.AsHaxeString(), node);
                }
                target.all = (ArrayObj)earlyAll.array;
                target.nodes = earlyNodes;

                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;

                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    try { node.set_isZRoot(src.IsZRoot); } catch { }

                    if (!string.IsNullOrWhiteSpace(src.ParentUid) && byUid.TryGetValue(src.ParentUid, out var parent))
                        node.set_parent(parent);
                    else
                        node.set_parent(null);
                }

                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;

                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    if (!src.IsZRoot || string.IsNullOrWhiteSpace(src.ParentUid))
                        continue;
                    if (!byUid.TryGetValue(src.ParentUid, out var parent))
                        continue;

                    ZDoorContentClue? clue = null;
                    string? parentDoorId = null;
                    string? childDoorId = null;
                    if (syncByUid.TryGetValue(src.ParentUid, out var parentSrc))
                    {
                        if (TryFindZLinkSync(parentSrc.ZLinks, src.Uid, out var parentToChild))
                        {
                            parentDoorId = parentToChild.DoorId;
                            if (parentToChild.ContentClue.HasValue)
                                clue = CreateZDoorContentClueFromIndex(parentToChild.ContentClue.Value);
                        }
                    }
                    if (TryFindZLinkSync(src.ZLinks, src.ParentUid, out var childToParent))
                        childDoorId = childToParent.DoorId;

                    try
                    {
                        parent.addZChild(node, clue);
                    }
                    catch
                    {
                        // If native rebuild fails, leave local z-links and continue; reason will surface later in apply/debug logs.
                    }

                    if (parentDoorId != null)
                        TrySetZLinkDoorId(parent, node, parentDoorId);
                    if (childDoorId != null)
                        TrySetZLinkDoorId(node, parent, childDoorId);
                }

                target.zLinkId = sync.ZLinkId;

                // Rebuild child arrays in host order. Parent pointers are already set above.
                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;
                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    node.children = BuildRoomNodeArrayByUid(src.ChildrenUids, byUid);
                    node.zChildren = BuildRoomNodeArrayByUid(src.ZChildrenUids, byUid);
                }

                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;
                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    if (!string.IsNullOrWhiteSpace(src.SubTeleportUid) && byUid.TryGetValue(src.SubTeleportUid, out var subTp))
                        node.subTeleportTo = subTp;
                    else
                        node.subTeleportTo = null;
                }

                var rebuiltAll = ArrayUtils.CreateDyn();
                var rebuiltNodes = new StringMap();
                for (int i = 0; i < sync.Nodes.Count; i++)
                {
                    var src = sync.Nodes[i];
                    if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                        continue;
                    if (!byUid.TryGetValue(src.Uid, out var node))
                        continue;

                    rebuiltAll.array.pushDyn(node);
                    rebuiltNodes.set(src.Uid.AsHaxeString(), node);
                }

                target.all = (ArrayObj)rebuiltAll.array;
                target.nodes = rebuiltNodes;

                if (!string.IsNullOrWhiteSpace(sync.RootUid) && byUid.TryGetValue(sync.RootUid, out var explicitRoot))
                {
                    rebuiltRoot = explicitRoot;
                }
                else
                {
                    for (int i = 0; i < sync.Nodes.Count; i++)
                    {
                        var src = sync.Nodes[i];
                        if (src == null || string.IsNullOrWhiteSpace(src.Uid))
                            continue;
                        if (src.IsZRoot)
                            continue;
                        if (!string.IsNullOrWhiteSpace(src.ParentUid))
                            continue;

                        if (byUid.TryGetValue(src.Uid, out var inferredRoot))
                        {
                            rebuiltRoot = inferredRoot;
                            break;
                        }
                    }
                }

                if (rebuiltRoot == null)
                {
                    reason = "rebuilt root not found";
                    return false;
                }

                try
                {
                    LogGenericZDoorDiagnostics(sync, byUid);
                }
                catch
                {
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static NpcId? CreateNpcIdFromIndex(int index)
        {
            return CreateEnumByIndex<NpcId, NpcId.Indexes>(index);
        }

        private static Dictionary<string, RoomNode> CaptureExistingRoomNodesByUid(LevelStruct target)
        {
            var result = new Dictionary<string, RoomNode>(StringComparer.Ordinal);
            if (target == null)
                return result;

            try
            {
                var all = target.all;
                if (all == null)
                    return result;

                for (int i = 0; i < all.length; i++)
                {
                    if (all.getDyn(i) is not RoomNode node)
                        continue;

                    var uid = node.uid?.ToString();
                    if (string.IsNullOrWhiteSpace(uid))
                        continue;

                    if (!result.ContainsKey(uid))
                        result[uid] = node;
                }
            }
            catch
            {
            }

            return result;
        }

        private static LinkConstraint? CreateLinkConstraintFromIndex(int index)
        {
            return index switch
            {
                0 => new LinkConstraint.All(),
                1 => new LinkConstraint.NeverDown(),
                2 => new LinkConstraint.NeverUp(),
                3 => new LinkConstraint.NeverRight(),
                4 => new LinkConstraint.NeverLeft(),
                5 => new LinkConstraint.HorizontalOnly(),
                6 => new LinkConstraint.VerticalOnly(),
                7 => new LinkConstraint.HorizontalLevelDirOnly(),
                8 => new LinkConstraint.RightOnly(),
                9 => new LinkConstraint.LeftOnly(),
                10 => new LinkConstraint.UpOnly(),
                11 => new LinkConstraint.DownOnly(),
                _ => null
            };
        }

        private static ZDoorContentClue? CreateZDoorContentClueFromIndex(int index)
        {
            return CreateEnumByIndex<ZDoorContentClue, ZDoorContentClue.Indexes>(index);
        }

        private static ZDoorType? CreateZDoorTypeFromSync(LevelGraphZDoorTypeSync? sync)
        {
            if (sync == null)
                return null;

            try
            {
                return sync.RawIndex switch
                {
                    0 => new ZDoorType.BossRune(sync.IntParam0 ?? 0),
                    1 => new ZDoorType.PerfectKills(sync.IntParam0 ?? 0),
                    2 => new ZDoorType.Timed(sync.DoubleParam0 ?? 0d),
                    3 => new ZDoorType.Conditional(),
                    4 => new ZDoorType.TumulusAntichamber(),
                    5 => new ZDoorType.CliffEnigma(),
                    6 => new ZDoorType.TrainingArena(),
                    7 => new ZDoorType.PurpleTeleport(),
                    8 => new ZDoorType.BossRushTeleport(),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static TEnum? CreateEnumByIndex<TEnum, TIndex>(int index)
            where TEnum : class
            where TIndex : struct, Enum
        {
            if (!Enum.IsDefined(typeof(TIndex), index))
                return null;

            var name = Enum.GetName(typeof(TIndex), index);
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var nested = typeof(TEnum).GetNestedType(name, System.Reflection.BindingFlags.Public);
            if (nested == null)
                return null;

            try
            {
                return Activator.CreateInstance(nested) as TEnum;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryFindZLinkSync(List<LevelGraphZLinkSync>? zLinks, string? destUid, out LevelGraphZLinkSync result)
        {
            result = null!;
            if (zLinks == null || string.IsNullOrWhiteSpace(destUid))
                return false;

            for (int i = 0; i < zLinks.Count; i++)
            {
                var item = zLinks[i];
                if (item == null || string.IsNullOrWhiteSpace(item.DestUid))
                    continue;
                if (!string.Equals(item.DestUid, destUid, StringComparison.Ordinal))
                    continue;
                result = item;
                return true;
            }

            return false;
        }

        private static void TrySetZLinkDoorId(RoomNode from, RoomNode dest, string doorId)
        {
            if (from == null || dest == null)
                return;

            try
            {
                var zLinks = from.zLinks;
                if (zLinks == null)
                    return;

                for (int i = 0; i < zLinks.length; i++)
                {
                    var link = zLinks.getDyn(i) as virtual_contentClue_dest_doorId_id_;
                    if (link == null)
                        continue;
                    if (!ReferenceEquals(link.dest, dest))
                        continue;
                    link.doorId = doorId.AsHaxeString();
                    return;
                }
            }
            catch
            {
            }
        }

        private static void LogGenericZDoorDiagnostics(LevelGraphSync sync, Dictionary<string, RoomNode> byUid)
        {
            var nodes = sync.Nodes;
            if (_log == null || nodes == null)
                return;

            for (int i = 0; i < nodes.Count; i++)
            {
                var src = nodes[i];
                if (src == null || !string.Equals(src.RType, "GenericZDoor", StringComparison.Ordinal))
                    continue;
                if (!byUid.TryGetValue(src.Uid, out var node))
                    continue;

                var childInfo = new List<string>();
                try
                {
                    var children = node.children;
                    if (children != null)
                    {
                        for (int c = 0; c < children.length; c++)
                        {
                            if (children.getDyn(c) is not RoomNode child)
                                continue;
                            var plc = "null";
                            var payloadPlc = "null";
                            try
                            {
                                if (child.parentLinkConstraint is HaxeEnum he)
                                    plc = he.RawIndex.ToString(CultureInfo.InvariantCulture);
                            }
                            catch { }
                            try
                            {
                                var childUid = child.uid?.ToString();
                                if (!string.IsNullOrWhiteSpace(childUid) &&
                                    sync.Nodes != null)
                                {
                                    for (int s = 0; s < sync.Nodes.Count; s++)
                                    {
                                        var childSrc = sync.Nodes[s];
                                        if (childSrc == null || !string.Equals(childSrc.Uid, childUid, StringComparison.Ordinal))
                                            continue;
                                        if (childSrc.ParentLinkConstraint.HasValue)
                                            payloadPlc = childSrc.ParentLinkConstraint.Value.ToString(CultureInfo.InvariantCulture);
                                        break;
                                    }
                                }
                            }
                            catch { }
                            childInfo.Add($"{child.uid}:{plc}/p{payloadPlc}");
                        }
                    }
                }
                catch { }

                var zdoorInfo = new List<string>();
                try
                {
                    var zLinks = node.zLinks;
                    if (zLinks != null)
                    {
                        for (int z = 0; z < zLinks.length; z++)
                        {
                            var link = zLinks.getDyn(z) as virtual_contentClue_dest_doorId_id_;
                            if (link == null)
                                continue;
                            zdoorInfo.Add(link.doorId?.ToString() ?? "null");
                        }
                    }
                }
                catch { }

                _log.Information(
                    "[NetMod] GenericZDoor diag {LevelId} uid={Uid} rType={RType} g={Group} forced={Forced} runtimeForced={RuntimeForced} parent={Parent} isZ={IsZ} children={ChildCount}[{ChildInfo}] zLinks={ZCount}[{ZInfo}] payloadChildren={PChild} payloadZ={PZ}",
                    sync.LevelId,
                    src.Uid,
                    src.RType ?? "null",
                    src.Group,
                    src.ForcedTemplateId ?? "null",
                    node.forcedTemplate?.id?.ToString() ?? "null",
                    src.ParentUid ?? "null",
                    src.IsZRoot,
                    node.children?.length ?? -1,
                    string.Join(",", childInfo),
                    node.zLinks?.length ?? -1,
                    string.Join(",", zdoorInfo),
                    src.ChildrenUids?.Count ?? 0,
                    src.ZLinks?.Count ?? 0);
            }
        }

        private static ArrayObj BuildRoomNodeArrayByUid(List<string>? orderedUids, Dictionary<string, RoomNode> byUid)
        {
            var arr = ArrayUtils.CreateDyn();
            if (orderedUids == null)
                return (ArrayObj)arr.array;

            for (int i = 0; i < orderedUids.Count; i++)
            {
                var uid = orderedUids[i];
                if (string.IsNullOrWhiteSpace(uid))
                    continue;
                if (!byUid.TryGetValue(uid, out var node))
                    continue;

                arr.array.pushDyn(node);
            }

            return (ArrayObj)arr.array;
        }
    }
}

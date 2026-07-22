using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using dc;
using dc.haxe.ds;
using dc.level;
using dc.pr;
using dc.tool;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using dc.haxe;
using dc.haxe.io;
using dc.hl.types;
using Rand = dc.libs.Rand;


namespace DeadCellsMultiplayerMod
{
    internal partial class GameDataSync
    {

        private static void CopyStoryVisitedLoreRoomsToSet(dynamic? map, HashSet<string> target)
        {
            target.Clear();
            if (map == null)
                return;

            try
            {
                var keys = map.keys.Invoke();
                while (keys.hasNext.Invoke())
                {
                    var keyObj = keys.next.Invoke();
                    if (keyObj == null)
                        continue;

                    var key = keyObj.ToString();
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    var raw = map.get.Invoke(keyObj);
                    var visited = raw is bool b ? b : ToInt(raw) != 0;
                    if (visited)
                        target.Add(key);
                }
            }
            catch
            {
            }
        }

        private static void CopyStoryPlannedLoresToList(ArrayBytes_Int? source, List<int> target)
        {
            target.Clear();
            if (source == null)
                return;

            var seen = new HashSet<int>();
            for (var i = 0; i < source.length; i++)
            {
                int planned;
                try
                {
                    planned = ToInt(source.getDyn(i));
                }
                catch
                {
                    continue;
                }

                if (seen.Add(planned))
                    target.Add(planned);
            }
        }

        private static StringMap BuildCountersMap(Dictionary<string, int> values)
        {
            var map = new StringMap();
            foreach (var kv in values)
                map.set(kv.Key.AsHaxeString(), kv.Value);
            return map;
        }

        private static EnumValueMap BuildNpcProgressMap(Dictionary<int, int> values)
        {
            var map = new EnumValueMap();
            foreach (var kv in values)
            {
                var npcId = CreateNpcIdFromIndex(kv.Key);
                if (npcId == null)
                    continue;

                map.set(npcId, kv.Value);
            }
            return map;
        }

        private static void ApplyStoryStringIntMap(dynamic? map, Dictionary<string, int> values)
        {
            if (map == null)
                return;

            try
            {
                var keysToRemove = new List<object>();
                var keys = map.keys.Invoke();
                while (keys.hasNext.Invoke())
                {
                    var keyObj = keys.next.Invoke();
                    if (keyObj != null)
                        keysToRemove.Add(keyObj);
                }

                for (var i = 0; i < keysToRemove.Count; i++)
                {
                    try { map.remove.Invoke(keysToRemove[i]); } catch { }
                }
            }
            catch
            {
            }

            foreach (var kv in values)
            {
                try
                {
                    map.set.Invoke(kv.Key.AsHaxeString(), kv.Value);
                }
                catch
                {
                }
            }
        }

        private static void ApplyStoryVisitedLoreRoomsMap(dynamic? map, HashSet<string> values)
        {
            if (map == null)
                return;

            try
            {
                var keysToRemove = new List<object>();
                var keys = map.keys.Invoke();
                while (keys.hasNext.Invoke())
                {
                    var keyObj = keys.next.Invoke();
                    if (keyObj != null)
                        keysToRemove.Add(keyObj);
                }

                for (var i = 0; i < keysToRemove.Count; i++)
                {
                    try { map.remove.Invoke(keysToRemove[i]); } catch { }
                }
            }
            catch
            {
            }

            foreach (var key in values)
            {
                try
                {
                    map.set.Invoke(key.AsHaxeString(), 1);
                }
                catch
                {
                }
            }
        }

        private static ArrayBytes_Int BuildStoryPlannedLoresArray(List<int> values)
        {
            var arr = new ArrayBytes_Int();
            var seen = new HashSet<int>();
            for (var i = 0; i < values.Count; i++)
            {
                var planned = values[i];
                if (!seen.Add(planned))
                    continue;

                try
                {
                    arr.push(planned);
                }
                catch
                {
                    try
                    {
                        arr.pushDyn(planned);
                    }
                    catch
                    {
                    }
                }
            }

            return arr;
        }

        private static void ApplyStoryState(
            User user,
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            int storyDataVersion,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores)
        {
            user.story = BuildStoryManager(
                counters,
                npcProgress,
                storyDataVersion,
                loreRoomRunIds,
                visitedLoreRooms,
                plannedLores);
            user.counters = BuildCountersMap(counters);
            user.npcs = BuildNpcProgressMap(npcProgress);
        }

        private static void ClearUserStoryState(User user)
        {
            user.story = null;
            user.counters = new StringMap();
            user.npcs = new EnumValueMap();
        }

        private static StoryManager BuildStoryManager(
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            int storyDataVersion,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores)
        {
            var story = new StoryManager();
            try
            {
                story.onReload();
            }
            catch
            {
            }

            dynamic dynStory = story;
            dynStory.counters = BuildCountersMap(counters);
            dynStory.npcProgresses = BuildNpcProgressMap(npcProgress);
            ApplyStoryStringIntMap(dynStory.loreRoomRunIds, loreRoomRunIds);
            ApplyStoryVisitedLoreRoomsMap(dynStory.visitedLoreRooms, visitedLoreRooms);
            dynStory.plannedLores = BuildStoryPlannedLoresArray(plannedLores);
            dynStory.storyDataVersion = storyDataVersion;
            return story;
        }

        private static ArrayObj? CloneItemProgress(ArrayObj? source)
        {
            if (source == null)
                return null;

            var arr = ArrayUtils.CreateDyn();
            for (int i = 0; i < source.length; i++)
            {
                var item = source.getDyn(i) as ItemProgress;
                if (item == null)
                    continue;
                var copy = new ItemProgress(item.itemId);
                copy.investedCells = item.investedCells;
                copy.isNew = item.isNew;
                copy.unlocked = item.unlocked;
                copy.__uid = item.__uid;
                arr.array.pushDyn(copy);
            }
            return (ArrayObj)arr.array;
        }

        private static ArrayObj? CloneMetaProgress(ArrayObj? source)
        {
            if (source == null)
                return null;

            var arr = ArrayUtils.CreateDyn();
            for (int i = 0; i < source.length; i++)
            {
                var item = source.getDyn(i) as MetaProgress;
                if (item == null)
                    continue;

                var copy = new MetaProgress(item.itemId);
                copy.investedCells = item.investedCells;
                copy.isNew = item.isNew;
                copy.unlocked = item.unlocked;
                copy.upgradeLevel = item.upgradeLevel;
                copy.n = item.n;
                copy.done = item.done;
                copy.metaLevel = item.metaLevel;
                arr.array.pushDyn(copy);
            }

            return (ArrayObj)arr.array;
        }

        private static ArrayObj? CloneItemList(ArrayObj? source)
        {
            if (source == null)
                return null;

            var arr = ArrayUtils.CreateDyn();
            for (int i = 0; i < source.length; i++)
            {
                object? item = source.getDyn(i);
                arr.array.pushDyn(item);
            }
            return (ArrayObj)arr.array;
        }

        private static IntMap? CloneIntMap(IntMap? source)
        {
            if (source == null)
                return null;

            var map = new IntMap();
            try
            {
                var keys = source.keys();
                while (keys.hasNext.Invoke())
                {
                    var key = keys.next.Invoke();
                    map.set(key, ToInt(source.get(key)));
                }
            }
            catch
            {
            }

            return map;
        }

        internal static bool TryBuildSafeSaveUser(User currentUser, bool onlyGameData, out User? saveUser)
        {
            saveUser = null;
            if (currentUser == null || string.IsNullOrWhiteSpace(_origProgressPayload))
                return false;

            try
            {
                var cloneBytes = Save.Class.genSave.Invoke(currentUser, onlyGameData);
                if (cloneBytes == null)
                    return false;

                var clone = Save.Class.readSave.Invoke(cloneBytes);
                if (clone == null)
                    return false;

                if (!TryApplyProgressPayload(clone, _origProgressPayload))
                    return false;

                saveUser = clone;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryApplyProgressPayload(User target, string? payload)
        {
            if (target == null || string.IsNullOrWhiteSpace(payload))
                return false;

            if (!TryDeserializeProgressUser(payload, out var source) || source == null)
                return false;

            ApplyProgressSnapshot(target, source);
            return true;
        }

        private static void ApplyProgressSnapshot(User target, User source)
        {
            if (target == null || source == null)
                return;

            var preservedHeroSkin = !string.IsNullOrWhiteSpace(_origHeroSkin)
                ? _origHeroSkin
                : CleanSkin(target.heroSkin?.ToString());
            var preservedHeroHeadSkin = !string.IsNullOrWhiteSpace(_origHeroHeadSkin)
                ? _origHeroHeadSkin
                : CleanSkin(target.heroHeadSkin?.ToString());

            var legacyCounters = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyCountersToDictionary(source.counters, legacyCounters);
            var legacyNpcProgress = new Dictionary<int, int>();
            CopyNpcProgressToDictionary(source.npcs, legacyNpcProgress);
            var sourceStory = source.story;
            if (sourceStory != null)
            {
                if (sourceStory.counters == null)
                    sourceStory.counters = BuildCountersMap(legacyCounters);
                if (sourceStory.npcProgresses == null)
                    sourceStory.npcProgresses = BuildNpcProgressMap(legacyNpcProgress);
            }

            target.flags = source.flags;
            target.userId = source.userId;
            target.deathMoney = source.deathMoney;
            target.deathCells = source.deathCells;
            target.bossRuneActivated = GetEffectiveBossRune(source);
            target.tutorial = source.tutorial;
            target.counters = sourceStory?.counters ?? BuildCountersMap(legacyCounters);
            target.npcs = sourceStory?.npcProgresses ?? BuildNpcProgressMap(legacyNpcProgress);
            target.story = sourceStory;
            target.itemMeta = null;
            target.userStats = source.userStats;
            target.activeMods = CloneItemList(source.activeMods);
            target.meta = CloneMetaProgress(source.meta);
            target.metaItems = CloneItemList(source.metaItems);
            target.achievements = CloneItemList(source.achievements);
            target.localAchievements = CloneItemList(source.localAchievements);
            target.deathItem = source.deathItem;
            target.consecutiveCompletedRuns = source.consecutiveCompletedRuns;
            ApplyHeroCosmetics(target, preservedHeroSkin, preservedHeroHeadSkin);
            MirrorStoryStateToSaveUser(target);

            try
            {
                target.userStats?.init();
            }
            catch
            {
            }

            try
            {
                target.onReload();
            }
            catch
            {
            }

            ApplyHeroCosmetics(target, preservedHeroSkin, preservedHeroHeadSkin);
            MirrorStoryStateToSaveUser(target);

            try
            {
                target.story?.onReload();
            }
            catch
            {
            }

            var targetMeta = EnsureItemMeta(target, target.itemMeta);
            targetMeta._user = target;
            if (source.itemMeta != null)
            {
                targetMeta.itemProgress = CloneItemProgress(source.itemMeta.itemProgress) ?? EnsureArray(targetMeta.itemProgress);
                targetMeta.permanentItems = CloneItemList(source.itemMeta.permanentItems) ?? EnsureArray(targetMeta.permanentItems);
                targetMeta.forgeInvestedCells = CloneIntMap(source.itemMeta.forgeInvestedCells) ?? new IntMap();
            }

            try
            {
                targetMeta.onReload();
            }
            catch
            {
            }

            try
            {
                targetMeta.revealAllBaseItems();
            }
            catch
            {
            }

            try
            {
                targetMeta.cleanDuplicatedItemProgress();
            }
            catch
            {
            }

            target.itemMeta = targetMeta;

            try
            {
                target.br_setActivated(GetEffectiveBossRune(source));
            }
            catch
            {
                target.bossRuneActivated = GetEffectiveBossRune(source);
            }
        }

        private static void MirrorStoryStateToSaveUser(User user)
        {
            if (user == null)
                return;

            try
            {
                var saveUser = user.mainGameData?.sUser;
                if (saveUser == null || ReferenceEquals(saveUser, user))
                    return;

                saveUser.story = user.story;
                saveUser.counters = user.counters;
                saveUser.npcs = user.npcs;
            }
            catch
            {
            }
        }

        private static void ApplyHeroCosmetics(User? user, string? heroSkin, string? heroHeadSkin)
        {
            if (user == null)
                return;

            if (!string.IsNullOrWhiteSpace(heroSkin))
                user.heroSkin = heroSkin.AsHaxeString();

            if (!string.IsNullOrWhiteSpace(heroHeadSkin))
                user.heroHeadSkin = heroHeadSkin.AsHaxeString();
        }

        private static string? BuildProgressPayload(User user)
        {
            if (user == null)
                return null;

            try
            {
                var prepared = false;
                try
                {
                    prepared = user.prepareSave();
                }
                catch (Exception ex)
                {
                    _log?.Debug(ex, "[NetMod] user.prepareSave() threw while building progress payload");
                }

                if (!prepared)
                    _log?.Debug("[NetMod] user.prepareSave() returned false; trying packed progress payload anyway");

                var saveBytes = Save.Class.genSave.Invoke(user, true);
                if (saveBytes != null)
                {
                    var saveRaw = CopyBytesToManaged(saveBytes);
                    return saveRaw.Length == 0 ? "P2|" : "P2|" + Convert.ToBase64String(saveRaw);
                }

                _log?.Warning("[NetMod] Failed to build packed progress payload: save bytes were null");
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "[NetMod] Failed to build packed progress payload");
            }

            if (TrySerializeUserBytes(user, out var userBytes) && userBytes != null)
            {
                var userRaw = CopyBytesToManaged(userBytes);
                return userRaw.Length == 0 ? "P1|" : "P1|" + Convert.ToBase64String(userRaw);
            }

            _log?.Warning("[NetMod] Failed to build progress payload from both packed-save and raw-user paths");
            return null;
        }

        private static bool TryDeserializeProgressUser(string payload, out User? user)
        {
            user = null;
            if (string.IsNullOrWhiteSpace(payload))
                return false;

            var isPackedSavePayload = payload.StartsWith("P2|", StringComparison.Ordinal);
            var encoded =
                payload.StartsWith("P2|", StringComparison.Ordinal) ? payload[3..] :
                payload.StartsWith("P1|", StringComparison.Ordinal) ? payload[3..] :
                payload;
            byte[] raw;
            try
            {
                raw = string.IsNullOrEmpty(encoded) ? Array.Empty<byte>() : Convert.FromBase64String(encoded);
            }
            catch
            {
                return false;
            }

            if (!TryCreateHaxeBytes(raw, out var bytes) || bytes == null)
                return false;

            try
            {
                if (isPackedSavePayload)
                {
                    user = Save.Class.readSave.Invoke(bytes);
                    return user != null;
                }

                var serializer = new dc.hxbit.Serializer
                {
                    refs = new IntMap()
                };
                var position = 0;
                serializer.beginLoad(bytes, Ref<int>.From(ref position));
                user = (User)(object)serializer.getRef(User.Class, User.Class.__clid);
                serializer.endLoad();
                user?.onReload();
                return user != null;
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "[NetMod] Failed to deserialize progress payload");
                user = null;
                return false;
            }
        }

        private static bool TrySerializeUserBytes(User user, out Bytes? bytes)
        {
            bytes = null;
            if (user == null)
                return false;

            try
            {
                var serializer = new dc.hxbit.Serializer();
                serializer.beginSave();
                var userRef = user.unnamedField0;
                if (userRef == null)
                    userRef = user.unnamedField0 = (virtual___uid_getCLID_getSerializeSchema_serialize_unserialize_unserializeInit_)(object)user;
                serializer.addKnownRef(userRef);
                var position = 0;
                bytes = serializer.endSave(Ref<int>.From(ref position));
                return bytes != null;
            }
            catch (Exception ex)
            {
                _log?.Warning(ex, "[NetMod] Failed to serialize raw user progress payload");
                bytes = null;
                return false;
            }
        }

        private static byte[] CopyBytesToManaged(Bytes bytes)
        {
            if (bytes == null || bytes.length <= 0 || bytes.b == IntPtr.Zero)
                return Array.Empty<byte>();

            var raw = new byte[bytes.length];
            Marshal.Copy(bytes.b, raw, 0, raw.Length);
            return raw;
        }

        private static bool TryCreateHaxeBytes(byte[] raw, out Bytes? bytes)
        {
            bytes = null;

            try
            {
                bytes = Bytes.Class.alloc.Invoke(raw.Length);
                if (bytes == null)
                    return false;

                if (raw.Length > 0 && bytes.b != IntPtr.Zero)
                    Marshal.Copy(raw, 0, bytes.b, raw.Length);

                return true;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        private static int ToInt(object? value)
        {
            if (value == null)
                return 0;

            if (value is int i)
                return i;

            if (value is bool b)
                return b ? 1 : 0;

            if (value is IConvertible conv)
            {
                try
                {
                    return conv.ToInt32(CultureInfo.InvariantCulture);
                }
                catch { }
            }

            return 0;
        }

        private static ArrayObj EnsureArray(ArrayObj? source)
        {
            if (source != null)
                return source;
            return (ArrayObj)ArrayUtils.CreateDyn().array;
        }

        private static ItemMetaManager EnsureItemMeta(User user, ItemMetaManager? meta)
        {
            var result = meta ?? user.itemMeta ?? new ItemMetaManager(user);
            result.itemProgress = EnsureArray(result.itemProgress);
            result.permanentItems = EnsureArray(result.permanentItems);
            return result;
        }

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
            if (candidate is not RoomNode node)
                return;

            var nodeSync = CaptureLevelGraphNode(node);
            if (nodeSync == null || string.IsNullOrWhiteSpace(nodeSync.Uid))
                return;

            if (!seenUids.Add(nodeSync.Uid))
                return;

            sync.Nodes.Add(nodeSync);
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
                GenData = CaptureLevelGraphGenData(node.genData)
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

        private static LevelGraphGenDataSync? CaptureLevelGraphGenData(virtual_altarItemGroup_brLegendaryMultiTreasure_broken_cells_doorCost_doorCurse_flaskRefill_forcedMerchantType_forcePauseTimer_isCliffPath_itemInWall_itemLevelBonus_killsMultiTreasure_locked_maxPerks_mins_noHealingShop_shouldBeFlipped_specificBiome_subTeleportTo_timedMultiTreasure_zDoorLock_zDoorType_? genData)
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

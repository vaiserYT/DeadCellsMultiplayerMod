using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Hashlink.Virtuals;
using Hashlink.Proxy.DynamicAccess;
using HaxeProxy.Runtime;
using Newtonsoft.Json;
using MonoMod.RuntimeDetour;
using Serilog;
using dc;
using dc.hl.types;
using dc.libs;
using dc.level;
using dc.pr;
using dc.tool;
using Serializer = dc.hxbit.Serializer;
using ModCore.Modules;
using Hook_GameData = dc.tool.Hook_GameData;


namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private static readonly object Sync = new();

        private static ILogger? _log;
        private static Delegate? _generateHookDelegate;
        private static Func<Delegate, LevelGen, User, int, object, Ref<bool>, ArrayObj>? _origInvoker;
        private static Delegate? _newGameHookDelegate;
        private static Action<Delegate, User, int, object, bool, bool, LaunchMode>? _newGameInvoker;
        private static Hook? _delegateHook;
        private static Assembly? _gameProxyAssembly;
        private static bool _gameDataSyncReady;
        private static bool _inActualRun;
        private static bool _hostGameDataSent;
        public static bool AllowGameDataHooks;
        private static GameDataSync? _cachedGameDataSync;
        private static LevelDescSync? _cachedLevelDescSync;

        private static NetRole _role = NetRole.None;
        private static int? _serverSeed;   // host-generated seed
        private static int? _remoteSeed;   // client-received seed
        private static int? _lastHostSeed; // stored for transitions
        public static int HostLvl;
        public static bool HostIsCustom;
        public static bool HostMode;
        public static LaunchMode? HostGData;
        private static RunParamsResolved? _latestResolvedRunParams;

        private const int MaxSeed = 999_999;

        public static NetNode? NetRef { get; set; }
        private static GameDataSync? _lastAppliedSync;

        private const string LevelDescTypeName = "Hashlink.Virtuals.virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_";
        private const string RunDataVirtualTypeName = "Hashlink.Virtuals.virtual_bossRune_endKind_forge_hasMods_history_isCustom_meta_runNum_";
        private static readonly System.Type? LevelDescType = System.Type.GetType(LevelDescTypeName) ?? typeof(Hook_LevelGen).Assembly.GetType(LevelDescTypeName);
        private static readonly bool EnableLevelGenHook = true;

        private static RunParams? LastRunParams;

        private sealed class RunParams
        {
            public int lvl;
            public bool isCustom;
            public bool mode;
            public int bossRune;
            public List<double>? forge;
            public List<HistoryEntry>? history;
            public List<string>? meta;
            public int? runNum;
            public string? endKind;
            public bool? hasMods;
        }

        private sealed class RunParamsResolved
        {
            public RunParams Data = null!;
        }

        private sealed class HistoryEntry
        {
            public int brut;
            public int cellsEarned;
            public string? level;
            public int surv;
            public int tact;
            public double time;
        }

        private sealed class LevelDescSync
        {
            public string LevelId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int MapDepth { get; set; }
            public double MobDensity { get; set; }
            public int MinGold { get; set; }
            public double EliteRoomChance { get; set; }
            public double EliteWanderChance { get; set; }
            public int DoubleUps { get; set; }
            public int TripleUps { get; set; }
            public int QuarterUpsBC3 { get; set; }
            public int QuarterUpsBC4 { get; set; }
            public int WorldDepth { get; set; }
            public int BaseLootLevel { get; set; }
            public double BonusTripleScrollAfterBC { get; set; }
            public double CellBonus { get; set; }
            public int Group { get; set; }
        }

        private static void LogLevelDesc(string title, LevelDescSync sync)
        {
            try
            {
                var json = JsonConvert.SerializeObject(sync);
                _log?.Information("[NetMod] {Title} LevelDescSync: {Json}", title, json);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to log LevelDescSync: {Message}", ex.Message);
            }
        }

        private static string BuildRunDataJson(RunParams rp)
        {
            var dto = new
            {
                bossRune = rp.bossRune,
                endKind = rp.endKind,
                forge = rp.forge?.Select(f => (int)System.Math.Round(f)).ToList() ?? new List<int>(),
                hasMods = rp.hasMods ?? false,
                history = rp.history?.Select(h => (object)new
                {
                    h.brut,
                    h.cellsEarned,
                    h.level,
                    h.surv,
                    h.tact,
                    time = h.time
                }).ToList() ?? new List<object>(),
                isCustom = rp.isCustom,
                meta = rp.meta ?? new List<string>(),
                runNum = rp.runNum
            };

            return JsonConvert.SerializeObject(dto);
        }

        private static void LogRunDataJson(string title, RunParams rp, int seed, object? levelDesc)
        {
            try
            {
                var json = BuildRunDataJson(rp);
                var level = ExtractLevelId(levelDesc) ?? "unknown";
                _log?.Information("[NetMod] {Title}: seed={Seed}, level={Level}, data={Json}", title, seed, level, json);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to log run params JSON: {Message}", ex.Message);
            }
        }

        private static bool SyncHasCoreData(GameDataSync sync)
        {
            return sync.BossRune != 0 ||
                   sync.RunNum != 0 ||
                   sync.HasMods ||
                   !string.IsNullOrWhiteSpace(sync.EndKind) ||
                   (sync.Forge?.Count ?? 0) > 0 ||
                   (sync.Meta?.Count ?? 0) > 0 ||
                   (sync.History?.Count ?? 0) > 0;
        }

        private static bool RunParamsIncomplete(RunParams rp)
        {
            return rp.runNum == null ||
                   rp.hasMods == null ||
                   rp.forge == null || rp.forge.Count == 0 ||
                   rp.history == null || rp.history.Count == 0 ||
                   rp.meta == null || rp.meta.Count == 0;
        }

        private static string? ExtractLevelId(object? levelDesc)
        {
            if (levelDesc == null) return null;

            try
            {
                var id = GetMemberValue(levelDesc, "id", ignoreCase: true)?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) return id;

                var name = GetMemberValue(levelDesc, "name", ignoreCase: true)?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }

            return levelDesc.ToString();
        }

        public static void MarkInRun()
        {
            GameDataSync? syncToApply = null;
            RunParamsResolved? rp = null;
            NetRole roleCopy;

            lock (Sync)
            {
                _inActualRun = true;
                syncToApply = _cachedGameDataSync != null ? CloneSync(_cachedGameDataSync) : null;
                rp = _latestResolvedRunParams;
                roleCopy = _role;
            }

            if (roleCopy != NetRole.Client) return;

            if (syncToApply != null)
            {
                var gd = GetGameDataInstance();
                if (gd != null)
                {
                    ApplyGameDataSyncToInstance(gd, syncToApply, rp?.Data);
                }
            }

            if (rp?.Data != null)
            {
                try
                {
                    var gd = GetGameDataInstance();
                    if (gd != null)
                    {
                        ApplyRunParamsToGameData(gd, rp.Data);
                        ApplyRunParamsToMember(gd, "runParams", rp.Data);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to apply cached run params on enter run: {Message}", ex.Message);
                }
            }
        }


        // ---------------------------------------------------------
        // INITIALIZE
        // ---------------------------------------------------------
        public static void Initialize(ILogger logger)
        {
            lock (Sync)
        {
            _log = logger;
            _gameDataSyncReady = false;
            _inActualRun = false;
            _hostGameDataSent = false;

            _log?.Information("[NetMod] GameMenu.Initialize - attaching RNG hooks");

                InitializeMenuUiHooks();


                TryPatchUtilityDelegates();

                Hook_GameData.serialize += GameDataSerializeHook;
                Hook_GameData.unserialize += GameDataUnserializeHook;
                _log?.Information("[NetMod] Hook_GameData.serialize + unserialize attached");

                // Hook_Rand.initSeed += InitSeedHook;
                TryAttachNewGameHook();
                DisableTwitchApi();

                if (!EnableLevelGenHook)
                {
                    _log?.Information("[NetMod] LevelGen.generate hook disabled to avoid long type-name crash");
                }
                else try
                {
                    var hookType = typeof(Hook_LevelGen).GetNestedType("hook_generate");
                    var origType = typeof(Hook_LevelGen).GetNestedType("orig_generate");
                    if (hookType == null || origType == null || LevelDescType == null)
                    {
                        _log?.Warning("[NetMod] LevelGen.generate hook skipped (hookType={Hook}, origType={Orig}, descType={Desc})",
                            hookType != null, origType != null, LevelDescType != null);
                    }
                    else
                    {
                        var dm = new DynamicMethod(
                            "GenerateHookShim",
                            typeof(ArrayObj),
                            new[] { origType, typeof(LevelGen), typeof(User), typeof(int), LevelDescType, typeof(Ref<bool>) },
                            typeof(GameMenu).Module,
                            skipVisibility: true);

                        var il = dm.GetILGenerator();
                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Castclass, typeof(Delegate));
                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldarg_3);
                        il.Emit(OpCodes.Ldarg_S, 4);
                        if (LevelDescType.IsValueType) il.Emit(OpCodes.Box, LevelDescType);
                        il.Emit(OpCodes.Ldarg_S, 5);
                        il.Emit(OpCodes.Call, typeof(GameMenu).GetMethod(nameof(GenerateHookImpl), BindingFlags.Static | BindingFlags.NonPublic)!);
                        il.Emit(OpCodes.Ret);

                        _generateHookDelegate = dm.CreateDelegate(hookType);
                        Hook_LevelGen.generate += (dynamic)_generateHookDelegate;
                        _log?.Information("[NetMod] Hook_LevelGen.generate attached via reflection");

                        var invokeMethod = origType.GetMethod("Invoke")!;
                        var dmInvoke = new DynamicMethod(
                            "InvokeOrigGenerate",
                            typeof(ArrayObj),
                            new[] { typeof(Delegate), typeof(LevelGen), typeof(User), typeof(int), typeof(object), typeof(Ref<bool>) },
                            typeof(GameMenu).Module,
                            skipVisibility: true);

                        var il2 = dmInvoke.GetILGenerator();
                        il2.Emit(OpCodes.Ldarg_0);
                        il2.Emit(OpCodes.Castclass, origType);
                        il2.Emit(OpCodes.Ldarg_1);
                        il2.Emit(OpCodes.Ldarg_2);
                        il2.Emit(OpCodes.Ldarg_3);
                        il2.Emit(OpCodes.Ldarg_S, 4);
                        if (LevelDescType.IsValueType) il2.Emit(OpCodes.Unbox_Any, LevelDescType); else il2.Emit(OpCodes.Castclass, LevelDescType);
                        il2.Emit(OpCodes.Ldarg_S, 5);
                        il2.Emit(OpCodes.Callvirt, invokeMethod);
                        il2.Emit(OpCodes.Ret);

                        _origInvoker = (Func<Delegate, LevelGen, User, int, object, Ref<bool>, ArrayObj>)dmInvoke.CreateDelegate(typeof(Func<Delegate, LevelGen, User, int, object, Ref<bool>, ArrayObj>));
                    }
                }
                catch (ArgumentException ex) when (ex.ParamName == "name" || ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Warning("[NetMod] LevelGen.generate hook failed: {Message}", ex.Message);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to attach LevelGen.generate hook: {Message}", ex.Message);
                }

                _log?.Information("[NetMod] Hook_Rand.initSeed attached");
            }
        }

        // Disable Twitch integration by forcing static flag to false
        private static void DisableTwitchApi()
        {
            try
            {
                var type = System.Type.GetType("tool.twitch.CustomSocketConnection, GameProxy")
                           ?? System.Type.GetType("tool.twitch.CustomSocketConnection");

                if (type == null) return;

                var field = type.GetField("ENABLE_TWITCH", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) return;

                field.SetValue(null, false);
                _log?.Information("[NetMod] Twitch API disabled (ENABLE_TWITCH=false)");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to disable Twitch API: {Message}", ex.Message);
            }
        }

        // MonoMod detour for UtilityDelegates.CreateDelegate to clamp generated type names
        private static void TryPatchUtilityDelegates()
        {
            try
            {
                if (_delegateHook != null) return;

                var utilType = FindUtilityDelegatesType();
                var target = utilType?.GetMethod("CreateDelegate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var detour = typeof(GameMenu).GetMethod(nameof(CreateDelegateSafe), BindingFlags.Static | BindingFlags.NonPublic);
                if (target == null || detour == null)
                {
                    _log?.Warning("[NetMod] UtilityDelegates.CreateDelegate not found; LevelGen hook may fail");
                    return;
                }

                _delegateHook = new Hook(target, detour);
                _log?.Information("[NetMod] Patched UtilityDelegates.CreateDelegate (short names)");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to patch UtilityDelegates.CreateDelegate: {Message}", ex.Message);
            }
        }

        private delegate System.Type CreateDelegateOrig(string name, System.Type ret, System.Type[] args);

        // Ensures generated delegate type names stay under runtime limit
        private static System.Type CreateDelegateSafe(CreateDelegateOrig orig, string name, System.Type ret, System.Type[] args)
        {
            const int limit = 200; // well below 1024 to be safe
            string shortName = name.Length > limit ? name[..limit] : name;
            return orig(shortName, ret, args);
        }

        private static System.Type? FindUtilityDelegatesType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Hashlink.UnsafeUtilities.UtilityDelegates");
                if (t != null) return t;
            }
            return null;
        }

        private static Assembly? GetGameProxyAssembly()
        {
            try
            {
                _gameProxyAssembly ??= AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "GameProxy", StringComparison.OrdinalIgnoreCase));

                return _gameProxyAssembly;
            }
            catch { return _gameProxyAssembly; }
        }

        private static void GameDataSerializeHook(Hook_GameData.orig_serialize orig, dc.tool.GameData self, Serializer ser)
        {
            orig(self, ser);
        }

        private static void GameDataUnserializeHook(
            Hook_GameData.orig_unserialize orig,
            dc.tool.GameData self,
            Serializer ser)
        {
            orig(self, ser); // after this, GameData is fully loaded

            if (!AllowGameDataHooks)
                return;

            var sync = BuildGameDataSync(self);
            if (TryGetResolvedRunParams(out var rpUnserialize) && rpUnserialize != null)
            {
                ApplyRunParamsToSync(sync, rpUnserialize.Data, ExtractLevelId(null));
            }
            CacheGameDataSync(sync);

            // On client, if we already have a cached network sync, apply it after deserialize
            if (_role == NetRole.Client)
            {
                try
                {
                    var cached = GetCachedGameDataSync() ?? sync;
                    ApplyGameDataSyncToInstance(self, cached, rpUnserialize?.Data);
                    CacheGameDataSync(cached);
                    _log?.Information("[NetMod] Client applied cached GameDataSync after deserialize");
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to apply cached GameDataSync after deserialize: {Message}", ex.Message);
                }
            }

            if (_role == NetRole.Host && NetRef != null && NetRef.IsHost && SyncHasCoreData(sync))
            {
                NetRef.SendGameData(JsonConvert.SerializeObject(sync));
            }
        }

        public static GameDataSync BuildGameDataSync(dc.tool.GameData gd)
        {
            var sync = new GameDataSync();

            PopulateSyncFromObject(gd, sync);

            var runParamsObj = GetMemberValue(gd, "runParams", ignoreCase: true)
                               ?? FindMemberRecursive(gd, "runParams", 3, new HashSet<object>(ReferenceEqualityComparer.Instance))
                               ?? FindMemberRecursiveContains(gd, "runParams", 3, new HashSet<object>(ReferenceEqualityComparer.Instance));
            if (runParamsObj != null)
            {
                PopulateSyncFromObject(runParamsObj, sync);
            }

            if (!SyncHasCoreData(sync))
            {
                PopulateSyncFromRunParams(runParamsObj, sync);
            }

            if (!SyncHasCoreData(sync))
            {
                var runDataObj = FindRunDataObject(new[] { runParamsObj, gd }, new HashSet<object>(ReferenceEqualityComparer.Instance));
                if (runDataObj != null)
                {
                    PopulateSyncFromRunParams(runDataObj, sync);
                }
            }

            if (!SyncHasCoreData(sync))
            {
                PopulateSyncFromJson(gd, sync);
            }

            return sync;
        }

        private static GameDataSync BuildGameDataSyncFromRunParams(RunParams rp)
        {
            var dto = new GameDataSync
            {
                Seed = rp.lvl,
                StartLevel = string.Empty,
                IsCustom = rp.isCustom,
                BossRune = rp.bossRune,
                RunNum = rp.runNum ?? 0,
                EndKind = rp.endKind ?? string.Empty,
                HasMods = rp.hasMods ?? false,
                Forge = rp.forge?.Select(x => (int)System.Math.Round(x)).ToList() ?? new List<int>(),
                Meta = rp.meta?.ToList() ?? new List<string>(),
                History = new List<GameDataSync.HistoryEntry>()
            };

            if (rp.history != null)
            {
                NormalizeRunHistory(rp.history);
                foreach (var h in rp.history)
                {
                    dto.History.Add(new GameDataSync.HistoryEntry
                    {
                        Level = h.level ?? string.Empty,
                        Brut = h.brut,
                        Surv = h.surv,
                        Tact = h.tact,
                        CellsEarned = h.cellsEarned,
                        Time = (float)h.time
                    });
                }
            }

            return dto;
        }

        private static RunParams BuildRunParamsFromSync(GameDataSync sync)
        {
            return new RunParams
            {
                lvl = sync.Seed,
                isCustom = sync.IsCustom,
                mode = false,
                bossRune = sync.BossRune,
                forge = sync.Forge?.Select(f => (double)f).ToList() ?? new List<double>(),
                history = sync.History?.Select(h => new HistoryEntry
                {
                    level = h.Level,
                    brut = h.Brut,
                    surv = h.Surv,
                    tact = h.Tact,
                    cellsEarned = h.CellsEarned,
                    time = h.Time
                }).ToList() ?? new List<HistoryEntry>(),
                meta = sync.Meta?.ToList() ?? new List<string>(),
                runNum = sync.RunNum,
                endKind = sync.EndKind,
                hasMods = sync.HasMods
            };
        }

        private static void ApplyGameDataSyncToInstance(dc.tool.GameData gd, GameDataSync sync, RunParams? rp = null)
        {
            try
            {
                rp ??= BuildRunParamsFromSync(sync);
                UpdateResolvedRunParams(rp, fromNetwork: true);
                _lastAppliedSync = CloneSync(sync);

                TrySetMember(gd, "seed", sync.Seed);
                TrySetMember(gd, "mainLevel", sync.StartLevel);
                TrySetMember(gd, "isCustom", sync.IsCustom);
                TrySetMember(gd, "bossRune", sync.BossRune);
                TrySetMember(gd, "runNum", sync.RunNum);
                TrySetMember(gd, "endKind", sync.EndKind);
                TrySetMember(gd, "hasMods", sync.HasMods);

                var forge = sync.Forge?.Select(x => (double)x).ToList() ?? new List<double>();
                var meta = sync.Meta?.ToList() ?? new List<string>();

                TrySetMember(gd, "forge", forge);
                TrySetMember(gd, "meta", meta);

                var hist = new List<object>();
                if (sync.History != null)
                {
                    foreach (var h in sync.History)
                    {
                        var entry = new
                        {
                            level = h.Level,
                            brut = h.Brut,
                            surv = h.Surv,
                            tact = h.Tact,
                            cellsEarned = h.CellsEarned,
                            time = h.Time
                        };
                        hist.Add(entry);
                    }
                }
                TrySetMember(gd, "history", hist);

                ApplyRunParamsToGameData(gd, rp);
                ApplyRunParamsToMember(gd, "runParams", rp);
                ApplyRunParamsToMember(gd, "runData", rp);

                // Try to update embedded runData objects if present
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var runDataObj = FindRunDataObject(new[]
                {
                    GetMemberValue(gd, "runData", true),
                    GetMemberValue(gd, "runParams", true),
                    gd
                }, visited);
                if (runDataObj != null)
                {
                    ApplyRunParamsToObject(runDataObj, rp);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply GameDataSync to instance: {Message}", ex.Message);
            }
        }

        private static void PopulateSyncFromObject(object source, GameDataSync sync)
        {
            if (source == null) return;

            try
            {
                dynamic d = DynamicAccessUtils.AsDynamic(source);
                sync.Seed = sync.Seed != 0 ? sync.Seed : SafeToInt(d?.seed, sync.Seed);
                sync.StartLevel = !string.IsNullOrWhiteSpace(sync.StartLevel) ? sync.StartLevel : d?.mainLevel as string ?? sync.StartLevel;
                sync.IsCustom = sync.IsCustom || (d?.isCustom ?? false);
                sync.BossRune = sync.BossRune != 0 ? sync.BossRune : SafeToInt(d?.bossRune, sync.BossRune);
                sync.RunNum = sync.RunNum != 0 ? sync.RunNum : SafeToInt(d?.runNum, sync.RunNum);
                sync.EndKind = !string.IsNullOrWhiteSpace(sync.EndKind) ? sync.EndKind : d?.endKind as string ?? sync.EndKind;
                sync.HasMods = sync.HasMods || (d?.hasMods ?? false);

                var forgeDyn = ExtractDoubleList(d?.forge);
                if (forgeDyn != null && forgeDyn.Count > 0)
                {
                    var list = new List<int>();
                    foreach (var v in forgeDyn) list.Add((int)v);
                    sync.Forge = list;
                }

                var metaDyn = ExtractStringList(d?.meta);
                if (metaDyn != null && metaDyn.Count > 0)
                {
                    var list = new List<string>();
                    foreach (var v in metaDyn) list.Add(v);
                    sync.Meta = list;
                }

                var histDyn = ExtractHistoryList(d?.history);
                if (histDyn != null && histDyn.Count > 0)
                {
                    var list = new List<GameDataSync.HistoryEntry>();
                    foreach (var h in histDyn)
                    {
                        list.Add(new GameDataSync.HistoryEntry
                        {
                            Level = h.level ?? string.Empty,
                            Brut = h.brut,
                            Surv = h.surv,
                            Tact = h.tact,
                            CellsEarned = h.cellsEarned,
                            Time = (float)h.time
                        });
                    }
                    sync.History = list;
                }
            }
            catch { }

            sync.Seed = sync.Seed != 0 ? sync.Seed : SafeToInt(GetMemberValue(source, "seed", ignoreCase: true), sync.Seed);
            sync.StartLevel = !string.IsNullOrWhiteSpace(sync.StartLevel) ? sync.StartLevel : GetMemberValue(source, "mainLevel", ignoreCase: true)?.ToString() ?? sync.StartLevel;
            sync.IsCustom = sync.IsCustom || (GetMemberValue(source, "isCustom", ignoreCase: true) as bool? ?? false);
            sync.BossRune = sync.BossRune != 0 ? sync.BossRune : SafeToInt(GetMemberValue(source, "bossRune", ignoreCase: true), sync.BossRune);
            sync.RunNum = sync.RunNum != 0 ? sync.RunNum : SafeToInt(GetMemberValue(source, "runNum", ignoreCase: true), sync.RunNum);
            sync.EndKind = !string.IsNullOrWhiteSpace(sync.EndKind) ? sync.EndKind : GetMemberValue(source, "endKind", ignoreCase: true)?.ToString() ?? sync.EndKind;
            sync.HasMods = sync.HasMods || (GetBoolValue(source, "hasMods") ?? false);

            var forgeList = ExtractDoubleList(GetMemberValue(source, "forge", ignoreCase: true));
            if (forgeList != null && forgeList.Count > 0)
            {
                sync.Forge = forgeList.Select(d => (int)d).ToList();
            }

            var metaList = ExtractStringList(GetMemberValue(source, "meta", ignoreCase: true));
            if (metaList != null && metaList.Count > 0)
            {
                sync.Meta = metaList;
            }

            var historyEnum = EnumerateUnknownCollection(GetMemberValue(source, "history", ignoreCase: true));
            if (historyEnum != null)
            {
                var items = new List<GameDataSync.HistoryEntry>();
                foreach (var h in historyEnum)
                {
                    if (h == null) continue;
                    items.Add(new GameDataSync.HistoryEntry
                    {
                        Level = GetMemberValue(h, "level", ignoreCase: true)?.ToString() ?? string.Empty,
                        Brut = SafeToInt(GetMemberValue(h, "brut", ignoreCase: true), 0),
                        Surv = SafeToInt(GetMemberValue(h, "surv", ignoreCase: true), 0),
                        Tact = SafeToInt(GetMemberValue(h, "tact", ignoreCase: true), 0),
                        CellsEarned = SafeToInt(GetMemberValue(h, "cellsEarned", ignoreCase: true), 0),
                        Time = (float)GetDoubleValue(h, "time", 0)
                    });
                }

                if (items.Count > 0)
                {
                    sync.History = items;
                }
            }

            try
            {
                if (RunParamsIncomplete(new RunParams
                    {
                        bossRune = sync.BossRune,
                        forge = sync.Forge.Select(x => (double)x).ToList(),
                        history = sync.History.Select(h => new HistoryEntry
                        {
                            brut = h.Brut,
                            cellsEarned = h.CellsEarned,
                            level = h.Level,
                            surv = h.Surv,
                            tact = h.Tact,
                            time = h.Time
                        }).ToList(),
                        meta = sync.Meta,
                        runNum = sync.RunNum,
                        endKind = sync.EndKind,
                        hasMods = sync.HasMods
                    }))
                {
                    var jo = JObject.FromObject(source);
                    sync.BossRune = sync.BossRune != 0 ? sync.BossRune : (jo["bossRune"]?.Value<int>() ?? sync.BossRune);
                    sync.RunNum = sync.RunNum != 0 ? sync.RunNum : (jo["runNum"]?.Value<int>() ?? sync.RunNum);
                    sync.EndKind = !string.IsNullOrWhiteSpace(sync.EndKind) ? sync.EndKind : (jo["endKind"]?.ToString() ?? sync.EndKind);
                    sync.HasMods = sync.HasMods || (jo["hasMods"]?.Value<bool>() ?? false);
                    var forgeArr = ExtractDoubleListFromJson(jo["forge"]);
                    if (forgeArr != null && forgeArr.Count > 0) sync.Forge = forgeArr.Select(x => (int)x).ToList();
                    var metaArr = ExtractStringListFromJson(jo["meta"]);
                    if (metaArr != null && metaArr.Count > 0) sync.Meta = metaArr;
                    var histArr = ExtractHistoryListFromJson(jo["history"]);
                    if (histArr != null && histArr.Count > 0)
                    {
                        sync.History = histArr.Select(h => new GameDataSync.HistoryEntry
                        {
                            Level = h.level ?? string.Empty,
                            Brut = h.brut,
                            Surv = h.surv,
                            Tact = h.tact,
                            CellsEarned = h.cellsEarned,
                            Time = (float)h.time
                        }).ToList();
                    }
                }
            }
            catch { }
        }

        private static void PopulateSyncFromRunParams(object? runParamsObj, GameDataSync sync)
        {
            if (runParamsObj == null) return;

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            int bossRune = sync.BossRune;
            List<double>? forge = sync.Forge?.Select(x => (double)x).ToList();
            List<HistoryEntry>? history = sync.History?.Select(h => new HistoryEntry
            {
                brut = h.Brut,
                cellsEarned = h.CellsEarned,
                level = h.Level,
                surv = h.Surv,
                tact = h.Tact,
                time = h.Time
            }).ToList();
            List<string>? meta = sync.Meta;
            int? runNum = sync.RunNum != 0 ? sync.RunNum : null;
            string? endKind = string.IsNullOrWhiteSpace(sync.EndKind) ? null : sync.EndKind;
            bool? hasMods = sync.HasMods ? true : (bool?)null;
            bool isCustom = sync.IsCustom;

            TryExtractRunDataFromObject(runParamsObj, ref bossRune, ref forge, ref history, ref meta, ref runNum, ref endKind, ref hasMods, ref isCustom, visited);

            if (bossRune != 0) sync.BossRune = bossRune;
            if (runNum.HasValue) sync.RunNum = runNum.Value;
            if (!string.IsNullOrWhiteSpace(endKind)) sync.EndKind = endKind;
            if (hasMods.HasValue) sync.HasMods = hasMods.Value;
            sync.IsCustom = sync.IsCustom || isCustom;

            if (forge != null && forge.Count > 0) sync.Forge = forge.Select(x => (int)x).ToList();
            if (meta != null && meta.Count > 0) sync.Meta = meta;
            if (history != null && history.Count > 0)
            {
                NormalizeRunHistory(history);
                sync.History = history.Select(h => new GameDataSync.HistoryEntry
                {
                    Level = h.level ?? string.Empty,
                    Brut = h.brut,
                    Surv = h.surv,
                    Tact = h.tact,
                    CellsEarned = h.cellsEarned,
                    Time = (float)h.time
                }).ToList();
            }
        }

        private static void PopulateSyncFromJson(object source, GameDataSync sync)
        {
            try
            {
                var settings = new JsonSerializerSettings
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    Error = (s, e) => { e.ErrorContext.Handled = true; }
                };
                var json = JsonConvert.SerializeObject(source, settings);
                var jo = JObject.Parse(json);
                sync.BossRune = sync.BossRune != 0 ? sync.BossRune : (jo["bossRune"]?.Value<int>() ?? sync.BossRune);
                sync.RunNum = sync.RunNum != 0 ? sync.RunNum : (jo["runNum"]?.Value<int>() ?? sync.RunNum);
                sync.EndKind = !string.IsNullOrWhiteSpace(sync.EndKind) ? sync.EndKind : (jo["endKind"]?.ToString() ?? sync.EndKind);
                sync.HasMods = sync.HasMods || (jo["hasMods"]?.Value<bool>() ?? false);
                var forgeArr = ExtractDoubleListFromJson(jo["forge"]);
                if (forgeArr != null && forgeArr.Count > 0) sync.Forge = forgeArr.Select(x => (int)x).ToList();
                var metaArr = ExtractStringListFromJson(jo["meta"]);
                if (metaArr != null && metaArr.Count > 0) sync.Meta = metaArr;
                var histArr = ExtractHistoryListFromJson(jo["history"]);
                if (histArr != null && histArr.Count > 0)
                {
                    NormalizeRunHistory(histArr);
                    sync.History = histArr.Select(h => new GameDataSync.HistoryEntry
                    {
                        Level = h.level ?? string.Empty,
                        Brut = h.brut,
                        Surv = h.surv,
                        Tact = h.tact,
                        CellsEarned = h.cellsEarned,
                        Time = (float)h.time
                    }).ToList();
                }
            }
            catch (Exception ex)
            {
                // swallow JSON parsing errors to avoid noisy logs
            }
        }

        private static void NormalizeRunHistory(List<HistoryEntry> history)
        {
            if (history == null) return;
            foreach (var h in history)
            {
                if (h == null) continue;
                if (h.brut < 0) h.brut = 0;
                if (h.surv < 0) h.surv = 0;
                if (h.tact < 0) h.tact = 0;
                if (h.cellsEarned < 0) h.cellsEarned = 0;
                if (h.time < 0) h.time = 0;
                if (string.IsNullOrWhiteSpace(h.level)) h.level = string.Empty;
            }
        }

        public static void ApplyFullGameData(dc.tool.GameData src)
        {
            var game = ModCore.Modules.Game.Instance;
            if (game == null) return;

            var target = GetMemberValue(game, "gameData", ignoreCase: true) as dc.tool.GameData;
            if (target == null) return;

            CopyGameData(target, src);

            _log?.Information("[NetMod] Full GameData replaced (hxbit)");
        }

        public static void CacheGameDataSync(GameDataSync? sync)
        {
            lock (Sync)
            {
                _cachedGameDataSync = sync != null ? CloneSync(sync) : null;
                _hostGameDataSent = false;
            }
        }

        private static void CacheLevelDescSync(LevelDescSync? sync)
        {
            lock (Sync)
            {
                _cachedLevelDescSync = sync == null ? null : JsonConvert.DeserializeObject<LevelDescSync>(JsonConvert.SerializeObject(sync));
            }
        }

        private static LevelDescSync? GetCachedLevelDescSync()
        {
            lock (Sync)
            {
                if (_cachedLevelDescSync == null) return null;
                return JsonConvert.DeserializeObject<LevelDescSync>(JsonConvert.SerializeObject(_cachedLevelDescSync));
            }
        }

        private static GameDataSync? GetCachedGameDataSync()
        {
            lock (Sync)
            {
                return _cachedGameDataSync != null ? CloneSync(_cachedGameDataSync) : null;
            }
        }

        private static GameDataSync CloneSync(GameDataSync src)
        {
            var clone = new GameDataSync
            {
                Seed = src.Seed,
                StartLevel = src.StartLevel,
                IsCustom = src.IsCustom,
                BossRune = src.BossRune,
                RunNum = src.RunNum,
                EndKind = src.EndKind,
                HasMods = src.HasMods,
                Forge = src.Forge != null ? new List<int>(src.Forge) : new List<int>(),
                Meta = src.Meta != null ? new List<string>(src.Meta) : new List<string>(),
                History = new List<GameDataSync.HistoryEntry>()
            };

            if (src.History != null)
            {
                foreach (var h in src.History)
                {
                    clone.History.Add(new GameDataSync.HistoryEntry
                    {
                        Level = h.Level,
                        Brut = h.Brut,
                        Surv = h.Surv,
                        Tact = h.Tact,
                        CellsEarned = h.CellsEarned,
                        Time = h.Time
                    });
                }
            }

            return clone;
        }

        private static dc.tool.GameData? GetGameDataInstance()
        {
            try
            {
                var instProp = typeof(dc.tool.GameData).GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var instField = typeof(dc.tool.GameData).GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var instAlt = typeof(dc.tool.GameData).GetField("inst", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                return (instProp?.GetValue(null) as dc.tool.GameData)
                       ?? (instField?.GetValue(null) as dc.tool.GameData)
                       ?? (instAlt?.GetValue(null) as dc.tool.GameData);
            }
            catch { return null; }
        }

        private static LevelDescSync BuildLevelDescSync(object desc)
        {
            var sync = new LevelDescSync();
            try
            {
                sync.LevelId = ExtractLevelId(desc) ?? string.Empty;
                sync.Name = GetMemberValue(desc, "name", true)?.ToString() ?? string.Empty;
                sync.MapDepth = SafeToInt(GetMemberValue(desc, "mapDepth", true), 0);
                sync.MinGold = SafeToInt(GetMemberValue(desc, "minGold", true), 0);
                sync.WorldDepth = SafeToInt(GetMemberValue(desc, "worldDepth", true), 0);
                sync.BaseLootLevel = SafeToInt(GetMemberValue(desc, "baseLootLevel", true), 0);
                sync.Group = SafeToInt(GetMemberValue(desc, "group", true), 0);
                sync.DoubleUps = SafeToInt(GetMemberValue(desc, "doubleUps", true), 0);
                sync.TripleUps = SafeToInt(GetMemberValue(desc, "tripleUps", true), 0);
                sync.QuarterUpsBC3 = SafeToInt(GetMemberValue(desc, "quarterUpsBC3", true), 0);
                sync.QuarterUpsBC4 = SafeToInt(GetMemberValue(desc, "quarterUpsBC4", true), 0);
                sync.MobDensity = SafeToDouble(GetMemberValue(desc, "mobDensity", true), 0);
                sync.EliteRoomChance = SafeToDouble(GetMemberValue(desc, "eliteRoomChance", true), 0);
                sync.EliteWanderChance = SafeToDouble(GetMemberValue(desc, "eliteWanderChance", true), 0);
                sync.BonusTripleScrollAfterBC = SafeToDouble(GetMemberValue(desc, "bonusTripleScrollAfterBC", true), 0);
                sync.CellBonus = SafeToDouble(GetMemberValue(desc, "cellBonus", true), 0);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to build LevelDescSync: {Message}", ex.Message);
            }
            return sync;
        }

        private static bool IsChallengeLevel(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId)) return false;
            return levelId.Equals("Challenge", StringComparison.OrdinalIgnoreCase)
                   || levelId.Equals("ldesc_challenge", StringComparison.OrdinalIgnoreCase);
        }

        private static dc.tool.GameData? GetGameDataFromContext(User user, LaunchMode gdata)
        {
            var gd = GetGameDataInstance();
            if (gd != null) return gd;

            try
            {
                gd = GetMemberValue(user, "gameData", ignoreCase: true) as dc.tool.GameData
                     ?? FindMemberRecursiveContains(user, "gameData", 3, new HashSet<object>(ReferenceEqualityComparer.Instance)) as dc.tool.GameData;
                if (gd != null) return gd;

                gd = GetMemberValue(gdata, "gameData", ignoreCase: true) as dc.tool.GameData
                     ?? FindMemberRecursiveContains(gdata, "gameData", 3, new HashSet<object>(ReferenceEqualityComparer.Instance)) as dc.tool.GameData;
                if (gd != null) return gd;

                var game = ModCore.Modules.Game.Instance;
                if (game != null)
                {
                    gd = GetMemberValue(game, "gameData", ignoreCase: true) as dc.tool.GameData
                         ?? FindMemberRecursiveContains(game, "gameData", 3, new HashSet<object>(ReferenceEqualityComparer.Instance)) as dc.tool.GameData;
                }
            }
            catch { }

            return gd;
        }

        public static void ApplyGameDataSync(GameDataSync sync)
        {
            try
            {
                // If client is still in menu, defer application but cache for use in newGame.
                bool defer;
                RunParams? rpFromSync = null;
                lock (Sync)
                {
                    defer = _role == NetRole.Client && !_inActualRun && !_gameDataSyncReady;
                }

                try
                {
                    rpFromSync = BuildRunParamsFromSync(sync);
                }
                catch { }

                if (defer)
                {
                    if (rpFromSync != null)
                    {
                        UpdateResolvedRunParams(rpFromSync, fromNetwork: true);
                    }
                    CacheGameDataSync(sync);
                    _log?.Information("[NetMod] Cached GameDataSync (deferred apply until run)");
                    return;
                }

                var game = ModCore.Modules.Game.Instance;
                if (game == null) return;
                var gd = GetMemberValue(game, "gameData", ignoreCase: true) as dc.tool.GameData;
                if (gd == null) return;

                if (rpFromSync == null) rpFromSync = BuildRunParamsFromSync(sync);
                ApplyGameDataSyncToInstance(gd, sync, rpFromSync);

                _log?.Information("[NetMod] Applied GameDataSync from host.");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply GameDataSync: {Message}", ex.Message);
            }
        }

        // Hook User.newGame with long signature via reflection/emit
        private static void TryAttachNewGameHook()
        {
            try
            {
                var hookType = typeof(Hook_User).GetNestedType("hook_newGame");
                var origType = typeof(Hook_User).GetNestedType("orig_newGame");
                if (hookType == null || origType == null || LevelDescType == null)
                {
                    _log?.Warning("[NetMod] User.newGame hook skipped (hookType={Hook}, origType={Orig}, descType={Desc})",
                        hookType != null, origType != null, LevelDescType != null);
                    return;
                }

                var dm = new DynamicMethod(
                    "NewGameHookShim",
                    typeof(void),
                    new[] { origType, typeof(User), typeof(int), LevelDescType, typeof(bool), typeof(bool), typeof(LaunchMode) },
                    typeof(GameMenu).Module,
                    skipVisibility: true);

                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, typeof(Delegate));
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldarg_3);
                if (LevelDescType.IsValueType) il.Emit(OpCodes.Box, LevelDescType);
                il.Emit(OpCodes.Ldarg_S, 4);
                il.Emit(OpCodes.Ldarg_S, 5);
                il.Emit(OpCodes.Ldarg_S, 6);
                il.Emit(OpCodes.Call, typeof(GameMenu).GetMethod(nameof(NewGameHookImpl), BindingFlags.Static | BindingFlags.NonPublic)!);
                il.Emit(OpCodes.Ret);

                _newGameHookDelegate = dm.CreateDelegate(hookType);
                Hook_User.newGame += (dynamic)_newGameHookDelegate;

                var invokeMethod = origType.GetMethod("Invoke")!;
                var dmInvoke = new DynamicMethod(
                    "InvokeOrigNewGame",
                    typeof(void),
                    new[] { typeof(Delegate), typeof(User), typeof(int), typeof(object), typeof(bool), typeof(bool), typeof(LaunchMode) },
                    typeof(GameMenu).Module,
                    skipVisibility: true);

                var il2 = dmInvoke.GetILGenerator();
                il2.Emit(OpCodes.Ldarg_0);
                il2.Emit(OpCodes.Castclass, origType);
                il2.Emit(OpCodes.Ldarg_1);
                il2.Emit(OpCodes.Ldarg_2);
                il2.Emit(OpCodes.Ldarg_3);
                if (LevelDescType.IsValueType) il2.Emit(OpCodes.Unbox_Any, LevelDescType); else il2.Emit(OpCodes.Castclass, LevelDescType);
                il2.Emit(OpCodes.Ldarg_S, 4);
                il2.Emit(OpCodes.Ldarg_S, 5);
                il2.Emit(OpCodes.Ldarg_S, 6);
                il2.Emit(OpCodes.Callvirt, invokeMethod);
                il2.Emit(OpCodes.Ret);

                _newGameInvoker = (Action<Delegate, User, int, object, bool, bool, LaunchMode>)dmInvoke.CreateDelegate(typeof(Action<Delegate, User, int, object, bool, bool, LaunchMode>));

                _log?.Information("[NetMod] Hook_User.newGame attached via reflection");
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to attach User.newGame hook: {Message}", ex.Message);
            }
        }

        private static void NewGameHookImpl(
            Delegate orig,
            User self,
            int lvl,
            object desc,
            bool isCustom,
            bool mode,
            LaunchMode gdata)
        {
            AllowGameDataHooks = true;

            int incomingSeed = lvl;
            int finalSeed = lvl;
            RunParams? runParamsForLog = null;

            lock (Sync)
            {
                _gameDataSyncReady = true;

                if (_role == NetRole.Host && _serverSeed.HasValue)
                {
                    finalSeed = _serverSeed.Value;
                    _lastHostSeed = finalSeed;
                }
                else if (_role == NetRole.Client && _remoteSeed.HasValue)
                {
                    finalSeed = _remoteSeed.Value;
                }
            }

            ForceDisableTwitch(self, gdata);

            if (_role == NetRole.Client && TryGetResolvedRunParams(out var rp) && rp != null)
            {
                finalSeed = rp.Data.lvl;
                lvl = rp.Data.lvl;
                isCustom = rp.Data.isCustom;
                runParamsForLog = rp.Data;

                gdata = ApplyRunParamsEverywhere(self, gdata, rp.Data);

                _log?.Information("[NetMod] Client applied run params in newGame (lvl={Lvl})",
                    rp.Data.lvl);

                LogRunParams("Client world params (newGame)", rp.Data);
            }
            else if (_role == NetRole.Host)
            {
                try
                {
                    HostGData = gdata;

                    var rpHost = CreateRunParams(finalSeed, gdata, isCustom, mode, self, desc);
                    UpdateResolvedRunParams(rpHost, fromNetwork: false);
                    runParamsForLog = rpHost;
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to cache host run params: {Message}", ex.Message);
                }
            }

            // Force twitch/custom flags
            try
            {
                dynamic dynUser = DynamicAccessUtils.AsDynamic(self);
                dynUser.isTwitch = false;
                dynUser.custom = true;
            }
            catch { }
            TrySetMember(self, "isTwitch", false);
            TrySetMember(self, "twitch", false);
            TrySetMember(self, "custom", true);
            TrySetMember(self, "isCustom", true);

            if (_newGameInvoker == null)
                throw new InvalidOperationException("newGame invoker null");

            // For client, inject cached GameDataSync and run params before the game logs newGame params
            if (_role == NetRole.Client)
            {
                try
                {
                    var syncCached = GetCachedGameDataSync();
                    if (syncCached != null)
                    {
                        ApplyGameDataSync(syncCached);
                        _lastAppliedSync = syncCached;
                    }

                    var rpClient = runParamsForLog ?? (_latestResolvedRunParams?.Data);
                    if (rpClient != null)
                    {
                        gdata = ApplyRunParamsEverywhere(self, gdata, rpClient);
                        ApplyRuntimeHeroState(self, rpClient, _lastAppliedSync);
                    }
                }
                catch { }
            }

            //
            // 1) Call game's original newGame()
            //    >>> HERE GameData is FINALLY fully populated
            //
            _newGameInvoker(orig, self, finalSeed, desc, false, isCustom, gdata);
            ForceDisableTwitch(self, gdata);

            // AFTER newGame is invoked and GameData is fully created
            if (_role == NetRole.Client)
            {
                try
                {
                    var rpClient = runParamsForLog ?? (_latestResolvedRunParams?.Data);
                    var gd = GetGameDataInstance();
                    if (gd != null)
                    {
                        if (rpClient != null)
                        {
                            // dbg
                            _log?.Information("[NetMod] (FIX) Applying final run params to GameData");

                            TrySetMember(gd, "seed", rpClient.lvl);

                            ApplyRunParamsToGameData(gd, rpClient);
                            ApplyRunParamsToMember(gd, "runParams", rpClient);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] FIX failed (client apply runparams after newGame): {Message}", ex.Message);
                }
            }



            if (_role == NetRole.Client)
            {
                try
                {
                    var syncCached = GetCachedGameDataSync();
                    if (syncCached != null)
                    {
                        ApplyGameDataSync(syncCached);
                        _lastAppliedSync = syncCached;
                    }

                    var rpClient = runParamsForLog ?? (_latestResolvedRunParams?.Data);
                    if (rpClient != null)
                    {
                        gdata = ApplyRunParamsEverywhere(self, gdata, rpClient);
                        ApplyRuntimeHeroState(self, rpClient, _lastAppliedSync);
                    }
                }
                catch { }
            }

            if (runParamsForLog != null)
            {
                try
                {
                    var gdInst = GetGameDataInstance();
                    if (gdInst != null)
                    {
                        ApplyRunParamsToGameData(gdInst, runParamsForLog);
                        ApplyRunParamsToMember(gdInst, "runParams", runParamsForLog);
                    }
                }
                catch { }
            }

            try
            {
                if (runParamsForLog == null || RunParamsIncomplete(runParamsForLog))
                {
                    var refreshed = CreateRunParams(finalSeed, gdata, isCustom, mode, self, desc);
                    runParamsForLog = refreshed;
                    if (_role == NetRole.Host)
                    {
                        UpdateResolvedRunParams(refreshed, fromNetwork: false);
                    }
                }

                if (runParamsForLog != null)
                {
                    LogRunDataJson("Run params (newGame)", runParamsForLog, finalSeed, desc);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to log refreshed run params: {Message}", ex.Message);
            }

            //
            // 2) ONLY NOW we read GameData and build GameDataSync
            //
            try
            {
                var gd = GetGameDataFromContext(self, gdata);
                if (gd != null)
                {
                    GameDataSync? sync = null;
                    if (_role == NetRole.Client)
                    {
                        sync = GetCachedGameDataSync();
                        if (sync == null && runParamsForLog != null)
                        {
                            sync = BuildGameDataSyncFromRunParams(runParamsForLog);
                        }
                    }
                    else
                    {
                        sync = BuildGameDataSync(gd);
                    }

                    if (sync != null && runParamsForLog != null)
                    {
                        ApplyRunParamsToSync(sync, runParamsForLog, ExtractLevelId(desc));
                    }

                    if (sync != null)
                    {
                        CacheGameDataSync(sync);

                        if (_role == NetRole.Host && NetRef != null && NetRef.IsHost)
                        {
                            var json = JsonConvert.SerializeObject(sync);
                            NetRef.SendGameData(json);
                            _log?.Information("[NetMod] Built GameDataSync after newGame: {Json}", json);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error("[NetMod] newGame Sync error: {Message}", ex.Message);
            }
        }



        // ---------------------------------------------------------
        // ROLE MGMT
        // ---------------------------------------------------------
        public static void SetRole(NetRole role)
        {
            lock (Sync)
            {
                _role = role;
                if (role == NetRole.None)
                {
                    _inActualRun = false;
                    _gameDataSyncReady = false;
                    _hostGameDataSent = false;
                }

                if (role != NetRole.Client)
                    _remoteSeed = null;

                if (role != NetRole.Client)
                {
                    _pendingAutoStart = false;
                    _levelDescArrived = false;
                    _gameDataArrived = false;
                    _autoStartTriggered = false;
                }
                else
                {
                    _pendingAutoStart = false;
                    _levelDescArrived = false;
                    _gameDataArrived = false;
                    _autoStartTriggered = false;
                }

                _log?.Information("[NetMod] SetRole -> {Role}, server={S}, remote={R}",
                    role, _serverSeed ?? -1, _remoteSeed ?? -1);
            }
        }



        public static void ReceiveHostRunSeed(int s)
        {
            lock (Sync)
            {
                _remoteSeed = Normalize(s);
            }

            _log?.Information("[NetMod] Received SEED from host: {Seed}", s);
        }

        public static void ReceiveRunParams(string json)
        {
            try
            {
                var obj = JsonConvert.DeserializeObject<RunParams>(json);
                if (obj == null)
                {
                    _log?.Warning("[NetMod] Failed to deserialize run params (null)");
                    return;
                }

                obj.mode = false;
                UpdateResolvedRunParams(obj, fromNetwork: true);
                _log?.Information("[NetMod] Received run params lvl={Lvl}, custom={Custom}, mode={Mode}", obj.lvl, obj.isCustom, obj.mode);

                if (_role == NetRole.Client)
                {
                    try
                    {
                        var gd = GetGameDataInstance();
                        if (gd != null)
                        {
                            TrySetMember(gd, "seed", obj.lvl);
                            ApplyRunParamsToGameData(gd, obj);
                            ApplyRunParamsToMember(gd, "runParams", obj);
                        }
                    }
                    catch { }
                }

                var dto = BuildGameDataSyncFromRunParams(obj);
                CacheGameDataSync(dto);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to receive run params: {Message}", ex.Message);
            }
        }

        public static void ReceiveLevelDesc(string json)
        {
            try
            {
                var dto = JsonConvert.DeserializeObject<LevelDescSync>(json);
                if (dto == null)
                {
                    _log?.Warning("[NetMod] Failed to deserialize LevelDescSync (null)");
                    return;
                }

                if (IsChallengeLevel(dto.LevelId))
                {
                    _log?.Information("[NetMod] Ignoring LevelDescSync for Challenge");
                    return;
                }

                CacheLevelDescSync(dto);
                NotifyLevelDescReceived();
                _log?.Information("[NetMod] Received LevelDescSync for level {Level}", dto.LevelId);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to receive LevelDescSync: {Message}", ex.Message);
            }
        }

        private static void UpdateResolvedRunParams(RunParams rp, bool fromNetwork)
        {
            rp.mode = false;
            lock (Sync)
            {
                HostLvl = rp.lvl;
                HostIsCustom = rp.isCustom;
                HostMode = rp.mode;
                _latestResolvedRunParams = new RunParamsResolved
                {
                    Data = rp
                };

                if (fromNetwork || _role == NetRole.Client)
                {
                    _remoteSeed = Normalize(rp.lvl);
                }
            }
        }

        private static bool TryGetResolvedRunParams(out RunParamsResolved? runParams)
        {
            lock (Sync)
            {
                runParams = _latestResolvedRunParams;
                return runParams != null;
            }
        }

        private static bool WaitForRunParams(TimeSpan timeout, out RunParamsResolved? runParams)
        {
            runParams = null;

            bool ok = SpinWait.SpinUntil(() =>
            {
                lock (Sync) return _latestResolvedRunParams != null;
            }, timeout);

            if (!ok) return false;

            lock (Sync)
            {
                runParams = _latestResolvedRunParams;
                return runParams != null;
            }
        }

        private static RunParams CreateRunParams(int lvl, LaunchMode gdata, bool isCustom, bool mode, User? user, object? levelDesc)
        {
            int bossRune = 0;
            List<double>? forge = null;
            List<HistoryEntry>? history = null;
            List<string>? meta = null;
            int? runNum = null;
            string? endKind = null;
            bool? hasMods = null;
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            GetGameProxyAssembly();

            object payload = gdata;
            var p0 = GetMemberValue(gdata, "Param0", ignoreCase: true);
            var p1 = GetMemberValue(gdata, "Param1", ignoreCase: true);
            var hxObj = GetMemberValue(gdata, "HashlinkObj", ignoreCase: true) ?? GetMemberValue(gdata, "HashlinkPointer", ignoreCase: true);

            if (p0 != null) payload = p0;

            TryExtractRunParamsFrom(payload, ref bossRune, ref forge, ref history, ref meta, visited);
            TryExtractRunParamsFrom(p1, ref bossRune, ref forge, ref history, ref meta, visited);
            TryExtractRunParamsFrom(gdata, ref bossRune, ref forge, ref history, ref meta, visited);
            TryExtractRunParamsFrom(hxObj, ref bossRune, ref forge, ref history, ref meta, visited);
            TryExtractRunParamsFrom(user, ref bossRune, ref forge, ref history, ref meta, visited);
            TryPopulateFromGameData(ref bossRune, ref forge, ref history, ref meta, ref runNum, ref endKind, ref hasMods, ref isCustom, visited);
            var runDataObj = FindRunDataObject(new[] { payload, p0, p1, gdata, hxObj, user, levelDesc }, new HashSet<object>(ReferenceEqualityComparer.Instance));
            if (runDataObj != null)
            {
                TryExtractRunDataFromObject(runDataObj, ref bossRune, ref forge, ref history, ref meta, ref runNum, ref endKind, ref hasMods, ref isCustom, visited);
            }

            var rp = new RunParams
            {
                lvl = lvl,
                isCustom = isCustom,
                mode = false,
                bossRune = bossRune,
                forge = forge,
                history = history,
                meta = meta,
                runNum = runNum,
                endKind = endKind,
                hasMods = hasMods
            };

            // Try recursive search fallbacks
            if (rp.forge == null)
            {
                rp.forge = ExtractDoubleList(FindMemberRecursive(gdata, "forge"));
            }
            if (rp.history == null)
            {
                rp.history = ExtractHistoryList(FindMemberRecursive(gdata, "history"));
            }
            if (rp.meta == null)
            {
                rp.meta = ExtractStringList(FindMemberRecursive(gdata, "meta"));
            }
            if (rp.forge == null)
            {
                rp.forge = ExtractDoubleList(FindMemberRecursiveContains(gdata, "forge"));
            }
            if (rp.history == null)
            {
                rp.history = ExtractHistoryList(FindMemberRecursiveContains(gdata, "history"));
            }
            if (rp.meta == null)
            {
                rp.meta = ExtractStringList(FindMemberRecursiveContains(gdata, "meta"));
            }

            if (rp.forge == null || rp.history == null || rp.meta == null || rp.bossRune == 0 || rp.runNum == null || rp.endKind == null || rp.hasMods == null)
            {
                try
                {
                    var json = JsonConvert.SerializeObject(gdata);
                    var jo = JObject.Parse(json);
                    if (rp.forge == null)
                    {
                        var forgeArr = jo["forge"] as JArray;
                        if (forgeArr != null) rp.forge = forgeArr.Select(x => SafeToDouble(x)).ToList();
                    }
                    if (rp.history == null)
                    {
                        var histArr = jo["history"] as JArray;
                        if (histArr != null)
                        {
                            rp.history = histArr.Select(h => new HistoryEntry
                            {
                                brut = h["brut"]?.Value<int>() ?? 0,
                                cellsEarned = h["cellsEarned"]?.Value<int>() ?? 0,
                                level = h["level"]?.ToString(),
                                surv = h["surv"]?.Value<int>() ?? 0,
                                tact = h["tact"]?.Value<int>() ?? 0,
                                time = h["time"]?.Value<double>() ?? 0
                            }).ToList();
                        }
                    }
                    if (rp.meta == null)
                    {
                        var metaArr = jo["meta"] as JArray;
                        if (metaArr != null) rp.meta = metaArr.Select(x => x?.ToString() ?? string.Empty).ToList();
                    }
                    if (rp.bossRune == 0)
                    {
                        rp.bossRune = jo["bossRune"]?.Value<int>() ?? rp.bossRune;
                    }
                    rp.runNum ??= jo["runNum"]?.Value<int?>();
                    rp.endKind ??= jo["endKind"]?.ToString();
                    rp.hasMods ??= jo["hasMods"]?.Value<bool?>();
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to build run params from JSON: {Message}", ex.Message);
                }
            }

            if (rp.forge == null) rp.forge = ExtractDoubleListFromJson(GetMemberValue(gdata, "forge", ignoreCase: true));
            if (rp.history == null) rp.history = ExtractHistoryListFromJson(GetMemberValue(gdata, "history", ignoreCase: true));
            if (rp.meta == null) rp.meta = ExtractStringListFromJson(GetMemberValue(gdata, "meta", ignoreCase: true));
            if (rp.bossRune == 0) rp.bossRune = GetFieldValue(gdata, "bossRune", rp.bossRune);
            if (rp.runNum == null) rp.runNum = GetNullableInt(gdata, "runNum");
            if (rp.endKind == null) rp.endKind = GetMemberValue(gdata, "endKind", ignoreCase: true)?.ToString();
            if (rp.hasMods == null) rp.hasMods = GetBoolValue(gdata, "hasMods");

            rp.forge ??= new List<double>();
            rp.history ??= new List<HistoryEntry>();
            rp.meta ??= new List<string>();
            rp.endKind ??= string.Empty;
            rp.hasMods ??= false;

            return rp;
        }

        private static void TryExtractRunParamsFrom(object? source, ref int bossRune, ref List<double>? forge, ref List<HistoryEntry>? history, ref List<string>? meta, HashSet<object> visited)
        {
            if (source == null) return;
            if (!visited.Add(source)) return;

            var type = source.GetType();
            if (type.IsPrimitive || source is string) return;

            // Dynamic
            try
            {
                dynamic d = DynamicAccessUtils.AsDynamic(source);
                if (bossRune == 0)
                    bossRune = (int)(d?.bossRune ?? 0);
                forge ??= ExtractDoubleList(d?.forge);
                history ??= ExtractHistoryList(d?.history);
                meta ??= ExtractStringList(d?.meta);
            }
            catch { }

            // Reflection
            try
            {
                if (bossRune == 0)
                    bossRune = GetFieldValue(source, "bossRune", bossRune);
                forge ??= ExtractDoubleList(GetMemberValue(source, "forge", ignoreCase: true));
                history ??= ExtractHistoryList(GetMemberValue(source, "history", ignoreCase: true));
                meta ??= ExtractStringList(GetMemberValue(source, "meta", ignoreCase: true));
            }
            catch { }

            // IDictionary path
            try
            {
                if (source is IDictionary dict)
                {
                    if (bossRune == 0 && dict.Contains("bossRune")) bossRune = SafeToInt(dict["bossRune"], bossRune);
                    forge ??= ExtractDoubleList(dict["forge"]);
                    history ??= ExtractHistoryList(dict["history"]);
                    meta ??= ExtractStringList(dict["meta"]);
                }
            }
            catch { }

            // JSON fallback
            try
            {
                if (bossRune == 0 || forge == null || history == null || meta == null)
                {
                    var jo = JObject.FromObject(source);
                    ApplyFromJson(jo, ref bossRune, ref forge, ref history, ref meta);
                }
            }
            catch { }

            if (forge == null || history == null || meta == null || bossRune == 0)
            {
                var paramsData = GetMemberValue(source, "ParamsData", ignoreCase: true) ?? GetMemberValue(source, "Item", ignoreCase: true);
                var nested = EnumerateUnknownCollection(paramsData);
                if (nested != null)
                {
                    foreach (var item in nested)
                    {
                        TryExtractRunParamsFrom(item, ref bossRune, ref forge, ref history, ref meta, visited);
                    }
                }
            }
        }

        private static void TryExtractRunDataFromObject(object? source, ref int bossRune, ref List<double>? forge, ref List<HistoryEntry>? history, ref List<string>? meta, ref int? runNum, ref string? endKind, ref bool? hasMods, ref bool isCustom, HashSet<object> visited)
        {
            if (source == null) return;

            TryExtractRunParamsFrom(source, ref bossRune, ref forge, ref history, ref meta, visited);

            runNum ??= GetNullableInt(source, "runNum");
            endKind ??= GetMemberValue(source, "endKind", ignoreCase: true)?.ToString();
            hasMods ??= GetBoolValue(source, "hasMods");
            var isCustomVal = GetBoolValue(source, "isCustom");
            if (isCustomVal.HasValue) isCustom = isCustomVal.Value;

            TryExtractRunDataFromGameProxy(source, ref bossRune, ref forge, ref history, ref meta, ref runNum, ref endKind, ref hasMods, ref isCustom);

            try
            {
                var jo = JObject.FromObject(source);
                if (bossRune == 0) bossRune = jo["bossRune"]?.Value<int>() ?? bossRune;
                forge ??= ExtractDoubleListFromJson(jo["forge"]);
                history ??= ExtractHistoryListFromJson(jo["history"]);
                meta ??= ExtractStringListFromJson(jo["meta"]);
                runNum ??= jo["runNum"]?.Value<int?>();
                endKind ??= jo["endKind"]?.ToString();
                hasMods ??= jo["hasMods"]?.Value<bool?>();
                isCustomVal = jo["isCustom"]?.Value<bool?>();
                if (isCustomVal.HasValue) isCustom = isCustomVal.Value;
            }
            catch { }
        }

        private static void TryExtractRunDataFromGameProxy(object source, ref int bossRune, ref List<double>? forge, ref List<HistoryEntry>? history, ref List<string>? meta, ref int? runNum, ref string? endKind, ref bool? hasMods, ref bool isCustom)
        {
            var typeName = source.GetType().FullName ?? string.Empty;
            if (!typeName.Contains(RunDataVirtualTypeName, StringComparison.OrdinalIgnoreCase))
                return;

            bossRune = bossRune != 0 ? bossRune : GetFieldValue(source, "bossRune", bossRune);
            runNum ??= GetNullableInt(source, "runNum");
            endKind ??= GetMemberValue(source, "endKind", ignoreCase: true)?.ToString();
            hasMods ??= GetBoolValue(source, "hasMods");
            var isCustomVal = GetBoolValue(source, "isCustom");
            if (isCustomVal.HasValue) isCustom = isCustomVal.Value;

            forge ??= ExtractDoubleList(GetMemberValue(source, "forge", ignoreCase: true)) ??
                      ExtractDoubleListFromJson(GetMemberValue(source, "forge", ignoreCase: true));

            history ??= ExtractHistoryList(GetMemberValue(source, "history", ignoreCase: true)) ??
                       ExtractHistoryListFromJson(GetMemberValue(source, "history", ignoreCase: true));

            meta ??= ExtractStringList(GetMemberValue(source, "meta", ignoreCase: true)) ??
                     ExtractStringListFromJson(GetMemberValue(source, "meta", ignoreCase: true));
        }

        private static void TryPopulateFromGameData(ref int bossRune, ref List<double>? forge, ref List<HistoryEntry>? history, ref List<string>? meta, ref int? runNum, ref string? endKind, ref bool? hasMods, ref bool isCustom, HashSet<object> visited)
        {
            try
            {
                var game = ModCore.Modules.Game.Instance;
                if (game == null) return;

                object? gameData = GetMemberValue(game, "gameData", ignoreCase: true)
                                   ?? FindMemberRecursiveContains(game, "gameData", 3, visited)
                                   ?? FindMemberRecursiveContains(game, "GameData", 3, visited);

                if (gameData == null) return;

                object? runParamsObj = GetMemberValue(gameData, "runParams", ignoreCase: true)
                                       ?? FindMemberRecursive(gameData, "runParams", 3, visited)
                                       ?? FindMemberRecursiveContains(gameData, "run", 2, visited);

                if (runParamsObj == null) return;

                TryExtractRunDataFromObject(runParamsObj, ref bossRune, ref forge, ref history, ref meta, ref runNum, ref endKind, ref hasMods, ref isCustom, visited);
                _log?.Information("[NetMod] Run params pulled from GameData ({Type})", runParamsObj.GetType().FullName);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to read run params from GameData: {Message}", ex.Message);
            }
        }

        private static void ApplyGameData(dc.tool.GameData target, dc.tool.GameData src)
        {
            CopyGameData(target, src);
        }

        private static void CopyGameData(dc.tool.GameData target, dc.tool.GameData source)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var f in typeof(dc.tool.GameData).GetFields(flags))
            {
                try
                {
                    var v = f.GetValue(source);
                    f.SetValue(target, v);
                }
                catch { }
            }

            foreach (var p in typeof(dc.tool.GameData).GetProperties(flags))
            {
                if (!p.CanRead || !p.CanWrite) continue;
                try
                {
                    var v = p.GetValue(source);
                    p.SetValue(target, v);
                }
                catch { }
            }
        }

        private static object? FindRunDataObject(IEnumerable<object?> roots, HashSet<object> visited)
        {
            var queue = new Queue<(object obj, int depth)>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                if (!visited.Add(root)) continue;
                queue.Enqueue((root, 0));
            }

            const int maxDepth = 8;
            const int maxNodes = 4096;
            int processed = 0;

            while (queue.Count > 0 && processed < maxNodes)
            {
                var (current, depth) = queue.Dequeue();
                processed++;

                if (LooksLikeRunData(current))
                    return current;

                if (depth >= maxDepth) continue;

                foreach (var child in EnumerateChildObjects(current))
                {
                    if (child == null) continue;
                    if (!visited.Add(child)) continue;
                    queue.Enqueue((child, depth + 1));
                }
            }

            return null;
        }

        private static bool LooksLikeRunData(object obj)
        {
            var type = obj.GetType();
            var fullName = type.FullName ?? string.Empty;
            if (fullName.IndexOf(RunDataVirtualTypeName, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            int matches = 0;

            if (HasMember(type, "bossRune")) matches++;
            if (HasMember(type, "forge")) matches++;
            if (HasMember(type, "history")) matches++;
            if (HasMember(type, "meta")) matches++;
            if (HasMember(type, "runNum")) matches++;
            if (HasMember(type, "endKind")) matches++;
            if (HasMember(type, "hasMods")) matches++;

            return matches >= 4 && HasMember(type, "bossRune");
        }

        private static IEnumerable<object?> EnumerateChildObjects(object obj)
        {
            if (obj == null || IsPrimitiveLike(obj))
                yield break;

            var hxArray = EnumerateHxArray(obj);
            if (hxArray != null)
            {
                foreach (var item in hxArray)
                {
                    if (IsPrimitiveLike(item)) continue;
                    yield return item;
                }
                yield break;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = obj.GetType();

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                object? val = null;
                try { val = prop.GetValue(obj); } catch { }
                if (IsPrimitiveLike(val)) continue;
                yield return val;
            }

            foreach (var field in type.GetFields(flags))
            {
                object? val = null;
                try { val = field.GetValue(obj); } catch { }
                if (IsPrimitiveLike(val)) continue;
                yield return val;
            }

            var items = EnumerateUnknownCollection(obj);
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (IsPrimitiveLike(item)) continue;
                    yield return item;
                }
            }
        }

        private static bool HasMember(System.Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            return type.GetProperty(name, flags) != null || type.GetField(name, flags) != null;
        }

        private static bool IsPrimitiveLike(object? value)
        {
            if (value == null) return true;
            var type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is decimal || value is IntPtr || value is UIntPtr;
        }

        private static int? GetNullableInt(object obj, string name)
        {
            try
            {
                var member = GetMemberValue(obj, name, ignoreCase: true);
                if (member == null) return null;
                if (member is int i) return i;
                if (member is IConvertible) return Convert.ToInt32(member);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to read int field {Field}: {Message}", name, ex.Message);
            }

            return null;
        }

        private static bool? GetBoolValue(object obj, string name)
        {
            try
            {
                var member = GetMemberValue(obj, name, ignoreCase: true);
                if (member == null) return null;
                if (member is bool b) return b;
                if (member is IConvertible) return Convert.ToBoolean(member);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to read bool field {Field}: {Message}", name, ex.Message);
            }

            return null;
        }

        private static void ApplyFromJson(JObject jo, ref int bossRune, ref List<double>? forge, ref List<HistoryEntry>? history, ref List<string>? meta)
        {
            if (jo == null) return;
            if (bossRune == 0) bossRune = jo["bossRune"]?.Value<int>() ?? bossRune;
            forge ??= ExtractDoubleListFromJson(jo["forge"]);
            history ??= ExtractHistoryListFromJson(jo["history"]);
            meta ??= ExtractStringListFromJson(jo["meta"]);
        }

        private static int SafeToInt(object? value, int fallback)
        {
            try
            {
                if (value == null) return fallback;
                if (value is int i) return i;
                if (value is IConvertible) return Convert.ToInt32(value);
                var s = value.ToString();
                if (int.TryParse(s, out var v)) return v;
            }
            catch { }
            return fallback;
        }

        private static LaunchMode ApplyRunParamsToGdata(LaunchMode gdata, RunParams rp)
        {
            try
            {
                object boxed = gdata;

                TrySetMember(boxed, "mode", false);
                TrySetMember(boxed, "isCustom", rp.isCustom);
                TrySetMember(boxed, "custom", rp.isCustom);
                TrySetMember(boxed, "isTwitch", false);
                TrySetMember(boxed, "twitch", false);
                TrySetMember(boxed, "bossRune", rp.bossRune);
                ApplyList(rp.forge, GetMemberValue(boxed, "forge", ignoreCase: true));
                ApplyHistory(rp.history, GetMemberValue(boxed, "history", ignoreCase: true));
                ApplyStringList(rp.meta, GetMemberValue(boxed, "meta", ignoreCase: true));

                ApplyRunParamsToMember(boxed, "runParams", rp);
                ApplyRunParamsToMember(boxed, "HashlinkObj", rp);
                ApplyRunParamsToMember(boxed, "Param0", rp);
                ApplyRunParamsToMember(boxed, "Param1", rp);

                LogRunParams("Client world params (LevelGen.generate)", rp);

                if (boxed is LaunchMode updated)
                {
                    gdata = updated;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply run params to LaunchMode: {Message}", ex.Message);
            }

            return gdata;
        }

        private static void ApplyRunParamsToSync(GameDataSync sync, RunParams rp, string? levelId)
        {
            sync.Seed = sync.Seed != 0 ? sync.Seed : rp.lvl;
            sync.StartLevel = !string.IsNullOrWhiteSpace(sync.StartLevel) ? sync.StartLevel : levelId ?? sync.StartLevel;
            if (rp.bossRune != 0) sync.BossRune = rp.bossRune;
            if (rp.runNum.HasValue && rp.runNum.Value != 0) sync.RunNum = rp.runNum.Value;
            if (!string.IsNullOrWhiteSpace(rp.endKind)) sync.EndKind = rp.endKind;
            if (rp.hasMods.HasValue) sync.HasMods = rp.hasMods.Value;
            sync.IsCustom = sync.IsCustom || rp.isCustom;

            if (rp.forge != null && rp.forge.Count > 0)
                sync.Forge = rp.forge.Select(x => (int)System.Math.Round(x)).ToList();

            if (rp.meta != null && rp.meta.Count > 0)
                sync.Meta = rp.meta.ToList();

            if (rp.history != null && rp.history.Count > 0)
            {
                NormalizeRunHistory(rp.history);
                sync.History = rp.history.Select(h => new GameDataSync.HistoryEntry
                {
                    Level = h.level ?? string.Empty,
                    Brut = h.brut,
                    Surv = h.surv,
                    Tact = h.tact,
                    CellsEarned = h.cellsEarned,
                    Time = (float)h.time
                }).ToList();
            }
        }

        private static dc.String MakeHLString(string? s)
        {
            if (s == null)
                s = "";

            // UTF8 в unmanaged память
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(s + "\0");
            IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(utf8.Length);
            System.Runtime.InteropServices.Marshal.Copy(utf8, 0, ptr, utf8.Length);

            // вызываем фабрику fromUTF8
            var cls = dc.String.Class;
            var f = cls.fromUTF8; // HlFunc<String, IntPtr>

            dc.String result = f.Invoke(ptr);

            // по идее Hashlink копирует строку, память можно освободить
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);

            return result;
        }

        private static object? ApplyRunParamsToObject(object? target, RunParams rp)
        {
            if (target == null)
                return null;

            try
            {
                // -----------------------------------------------------
                // 🔥 CASE 1: HL виртуальный RunData
                // -----------------------------------------------------
                if (target is virtual_bossRune_endKind_forge_hasMods_history_isCustom_meta_runNum_ rd)
                {
                    // Простые поля
                    rd.bossRune = rp.bossRune;
                    rd.runNum  = rp.runNum  ?? 0;
                    rd.hasMods = rp.hasMods ?? false;
                    rd.isCustom = rp.isCustom;    // bool

                    rd.endKind = MakeHLString(rp.endKind ?? "");

                    // ---------------- forge (int) ----------------
                    var forgeTarget = GetMemberValue(rd, "forge", true);
                    if (forgeTarget != null && rp.forge != null)
                    {
                        ClearArray(forgeTarget);
                        ApplyArrayPush(forgeTarget, rp.forge.Select(f => (object)(int)System.Math.Round(f)));
                    }

                    // --------------- meta (HaxeString) -----------------
                    var metaTarget = GetMemberValue(rd, "meta", true);
                    if (metaTarget != null && rp.meta != null)
                    {
                        ClearArray(metaTarget);
                        ApplyArrayPush(metaTarget, rp.meta.Select(m => (object)(m ?? string.Empty)));
                    }

                    // ---------------- history ----------------
                    // Avoid constructing HL history objects here; GameData already populated elsewhere.

                    _log?.Information("[NetMod] Patched REAL RunData (virtual): runNum={0}, meta={1}, forge={2}",
                        rd.runNum, rd.meta?.length ?? -1, rd.forge?.length ?? -1);

                    return target;
                }

                // -----------------------------------------------------
                // 🔥 CASE 2: fallback через TrySetMember
                // -----------------------------------------------------
                TrySetMember(target, "bossRune", rp.bossRune);
                TrySetMember(target, "runNum",   rp.runNum);
                TrySetMember(target, "endKind",  rp.endKind ?? "");

                TrySetMember(target, "hasMods",  rp.hasMods);
                TrySetMember(target, "isCustom", rp.isCustom);
                TrySetMember(target, "custom",   rp.isCustom);

                TrySetMember(target, "isTwitch", false);
                TrySetMember(target, "twitch",   false);

                ApplyList(rp.forge,   GetMemberValue(target, "forge",   true));
                ApplyHistory(rp.history, GetMemberValue(target, "history", true));
                ApplyStringList(rp.meta, GetMemberValue(target, "meta", true));
            }
            catch (Exception ex)
            {
                _log?.Error(ex, "[NetMod] ApplyRunParamsToObject ERROR");
            }

            return target;
        }


        private static void ApplyRunParamsToMember(object? owner, string memberName, RunParams rp, bool ignoreCase = true)
        {
            if (owner == null) return;

            var updated = ApplyRunParamsToObject(GetMemberValue(owner, memberName, ignoreCase: ignoreCase), rp);
            if (updated != null && updated.GetType().IsValueType)
            {
                TrySetMember(owner, memberName, updated);
            }
        }

        private static LaunchMode ApplyRunParamsEverywhere(User self, LaunchMode gdata, RunParams rp)
        {
            // Update LaunchMode (struct) and propagate run params into all reachable contexts.
            gdata = ApplyRunParamsToGdata(gdata, rp);

            try
            {
                var gd = GetGameDataFromContext(self, gdata) ?? GetGameDataInstance();
                if (gd != null)
                {
                    ApplyRunParamsToGameData(gd, rp);
                    ApplyRunParamsToMember(gd, "runParams", rp);
                    ApplyRunParamsToMember(gd, "runData", rp);

                    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    var runDataObj = FindRunDataObject(new[]
                    {
                        GetMemberValue(gd, "runData", ignoreCase: true),
                        GetMemberValue(gd, "runParams", ignoreCase: true),
                        GetMemberValue(self, "runData", ignoreCase: true),
                        GetMemberValue(gdata, "runData", ignoreCase: true),
                        gd,
                        self,
                        gdata
                    }, visited);
                    if (runDataObj != null)
                    {
                        var rdUpdated = ApplyRunParamsToObject(runDataObj, rp);
                        if (rdUpdated != null && rdUpdated.GetType().IsValueType)
                        {
                            TrySetMember(gd, "runParams", rdUpdated);
                        }
                    }
                }

                ApplyRunParamsToMember(self, "runParams", rp);
                ApplyRunParamsToMember(self, "runData", rp);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to propagate run params to contexts: {Message}", ex.Message);
            }

            return gdata;
        }

        private static void ApplyRuntimeHeroState(User user, RunParams? rpOpt, GameDataSync? syncOpt)
        {
            if (rpOpt == null && syncOpt == null) return;
            try
            {
                dynamic dynUser = DynamicAccessUtils.AsDynamic(user);
                object? hero = null;
                try { hero = dynUser.hero; } catch { }
                hero ??= GetMemberValue(user, "hero", true);
                if (hero == null)
                {
                    var gameInst = ModCore.Modules.Game.Instance;
                    hero = GetMemberValue(gameInst, "hero", true)
                           ?? GetMemberValue(gameInst, "heroInstance", true)
                           ?? GetMemberValue(gameInst, "HeroInstance", true);
                }
                if (hero == null)
                {
                    _log?.Error("Hero is null in runtime sync");
                    return;
                }

                var meta = rpOpt?.meta ?? syncOpt?.Meta?.ToList();
                var forge = rpOpt?.forge ?? syncOpt?.Forge?.Select(f => (double)f).ToList();
                var histEntry = rpOpt?.history?.FirstOrDefault();

                int flaskCount = meta?.Count(m => m != null && m.StartsWith("Flask", StringComparison.OrdinalIgnoreCase)) ?? 0;
                TrySetMember(hero, "flaskMax", flaskCount);
                TrySetMember(hero, "flaskCount", flaskCount);

                bool mirrorUnlock = meta?.Any(m => string.Equals(m, "MirrorUnlock", StringComparison.OrdinalIgnoreCase)) == true;
                TrySetMember(hero, "mirrorUnlocked", mirrorUnlock);

                bool backpackUnlock = meta?.Any(m => string.Equals(m, "BackpackUnlock", StringComparison.OrdinalIgnoreCase)) == true;
                TrySetMember(hero, "backpackUnlocked", backpackUnlock);

                int recycling = meta?.Any(m => string.Equals(m, "Recycling2", StringComparison.OrdinalIgnoreCase)) == true ? 2 :
                                meta?.Any(m => string.Equals(m, "Recycling1", StringComparison.OrdinalIgnoreCase)) == true ? 1 : 0;
                TrySetMember(hero, "recyclingLevel", recycling);

                if (forge != null && forge.Count > 0)
                {
                    if (forge.Count > 0) TrySetMember(hero, "forgeLevelWeapon", (int)System.Math.Round(forge[0]));
                    if (forge.Count > 1) TrySetMember(hero, "forgeLevelSkill", (int)System.Math.Round(forge[1]));
                    if (forge.Count > 2) TrySetMember(hero, "forgeLevelAmulet", (int)System.Math.Round(forge[2]));
                }

                if (histEntry != null)
                {
                    TrySetMember(hero, "statBrut", histEntry.brut);
                    TrySetMember(hero, "statSurv", histEntry.surv);
                    TrySetMember(hero, "statTact", histEntry.tact);
                    int scrolls = histEntry.brut + histEntry.surv + histEntry.tact;
                    TrySetMember(hero, "scrolls", scrolls);
                    TrySetMember(hero, "scrollApps", scrolls);
                }

                if (histEntry != null)
                {
                    TrySetMember(hero, "money", histEntry.cellsEarned);
                    TrySetMember(hero, "gold", histEntry.cellsEarned);
                }

                LogHeroState(hero, flaskCount, mirrorUnlock, backpackUnlock, recycling, forge, histEntry);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply runtime hero state: {Message}", ex.Message);
            }
        }

        private static void LogHeroState(object hero, int flaskCount, bool mirrorUnlock, bool backpackUnlock, int recycling, List<double>? forge, HistoryEntry? hist)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("[NetMod] Hero runtime: ");
                sb.Append("flask=");
                sb.Append(flaskCount);
                sb.Append(", mirror=");
                sb.Append(mirrorUnlock);
                sb.Append(", backpack=");
                sb.Append(backpackUnlock);
                sb.Append(", recycling=");
                sb.Append(recycling);
                sb.Append(", forge=[");
                if (forge != null) sb.Append(string.Join(",", forge.Select(f => (int)System.Math.Round(f))));
                sb.Append("]");
                if (hist != null)
                {
                    sb.Append(", stats=");
                    sb.Append($"{hist.brut}/{hist.surv}/{hist.tact}, cells={hist.cellsEarned}");
                }

                double money = SafeToDouble(GetMemberValue(hero, "money", true), -1);
                double gold = SafeToDouble(GetMemberValue(hero, "gold", true), -1);
                int statBrut = SafeToInt(GetMemberValue(hero, "statBrut", true), -1);
                int statSurv = SafeToInt(GetMemberValue(hero, "statSurv", true), -1);
                int statTact = SafeToInt(GetMemberValue(hero, "statTact", true), -1);
                sb.Append($", money={money}, gold={gold}, statsNow={statBrut}/{statSurv}/{statTact}");

                _log?.Information(sb.ToString());
            }
            catch { }
        }

        private static void ForceDisableTwitch(User user, LaunchMode? gdata)
        {
            try
            {
                dynamic dynUser = DynamicAccessUtils.AsDynamic(user);
                dynUser.isTwitch = false;
                dynUser.twitch = false;
                dynUser.custom = true;
                dynUser.isCustom = true;
            }
            catch { }

            TrySetMember(user, "isTwitch", false);
            TrySetMember(user, "twitch", false);
            TrySetMember(user, "custom", true);
            TrySetMember(user, "isCustom", true);

        }

        private static void ApplyRunParamsToGameData(dc.tool.GameData gd, RunParams rp)
        {
            try
            {
                TrySetMember(gd, "seed", rp.lvl);
                TrySetMember(gd, "isCustom", rp.isCustom);
                TrySetMember(gd, "custom", rp.isCustom);
                TrySetMember(gd, "isTwitch", false);
                TrySetMember(gd, "twitch", false);
                TrySetMember(gd, "bossRune", rp.bossRune);
                TrySetMember(gd, "runNum", rp.runNum ?? 0);
                TrySetMember(gd, "endKind", rp.endKind ?? string.Empty);
                TrySetMember(gd, "hasMods", rp.hasMods ?? false);
                ApplyList(rp.forge, GetMemberValue(gd, "forge", ignoreCase: true));
                ApplyHistory(rp.history, GetMemberValue(gd, "history", ignoreCase: true));
                ApplyStringList(rp.meta, GetMemberValue(gd, "meta", ignoreCase: true));
            }
            catch { }
        }

        private static int GetFieldValue(object obj, string name, int fallback)
        {
            try
            {
                var member = GetMemberValue(obj, name, ignoreCase: true);
                if (member == null) return fallback;
                if (member is int i) return i;
                if (member is IConvertible) return Convert.ToInt32(member);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to read int field {Field}: {Message}", name, ex.Message);
            }
            return fallback;
        }

        private static object? GetMemberValue(object obj, string name, bool ignoreCase = false)
        {
            if (obj == null) return null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (ignoreCase) flags |= BindingFlags.IgnoreCase;
            var type = obj.GetType();
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.CanRead)
            {
                try { return prop.GetValue(obj); } catch { }
            }
            var field = type.GetField(name, flags);
            if (field != null)
            {
                try { return field.GetValue(obj); } catch { }
            }
            return null;
        }

        private static void TrySetMember(object obj, string name, object? value)
        {
            if (obj == null) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = obj.GetType();
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    var val = ConvertForTarget(prop.PropertyType, value);
                    prop.SetValue(obj, val);
                    return;
                }
                catch (Exception ex)
                {
                    if (!name.Equals("bonusTripleScrollAfterBC", StringComparison.OrdinalIgnoreCase))
                        _log?.Warning("[NetMod] Failed to set property {Field}: {Message}", name, ex.Message);
                }
            }

            var field = type.GetField(name, flags);
            if (field != null && !field.IsInitOnly)
            {
                try
                {
                    var val = ConvertForTarget(field.FieldType, value);
                    field.SetValue(obj, val);
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Failed to set field {Field}: {Message}", name, ex.Message);
                }
            }
        }

        private static object? ConvertForTarget(System.Type targetType, object? value)
        {
            if (value == null) return null;

            try
            {
                // Handle Hashlink dc.String specifically
                if (targetType == typeof(dc.String))
                {
                    var s = value is string str ? str : value.ToString() ?? string.Empty;
                    return MakeHLString(s);
                }

                if (targetType.IsInstanceOfType(value))
                    return value;

                if (value is IConvertible)
                    return System.Convert.ChangeType(value, targetType);
            }
            catch { }

            return value;
        }

        private static void ClearArray(object arrayObj)
        {
            if (arrayObj == null) return;
            try
            {
                // IList: just clear
                if (arrayObj is IList list)
                {
                    list.Clear();
                    return;
                }

                var type = arrayObj.GetType();

                // Method clear()
                var clear = type.GetMethod("clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (clear != null)
                {
                    clear.Invoke(arrayObj, Array.Empty<object>());
                    return;
                }

                // Method resize(int) or set_length(int)
                var resize = type.GetMethod("resize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? type.GetMethod("set_length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (resize != null)
                {
                    resize.Invoke(arrayObj, new object[] { 0 });
                    return;
                }

                // Property length
                var lenProp = type.GetProperty("length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (lenProp != null && lenProp.CanWrite)
                {
                    lenProp.SetValue(arrayObj, 0);
                    return;
                }
            }
            catch { }
        }

        private static void ApplyArrayPush(object arrayObj, IEnumerable<object?> values)
        {
            if (arrayObj == null) return;
            try
            {
                var push = arrayObj.GetType().GetMethod("push", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var paramType = push?.GetParameters().FirstOrDefault()?.ParameterType;
                if (push == null || paramType == null) return;

                foreach (var v in values)
                {
                    var converted = ConvertForTarget(paramType, v);
                    push.Invoke(arrayObj, new[] { converted });
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to push into HL array: {Message}", ex.Message);
            }
        }

        private static List<double>? ExtractDoubleList(object? value)
        {
            var hxItems = EnumerateHxArray(value);
            if (hxItems != null)
            {
                var listHx = new List<double>();
                foreach (var item in hxItems)
                {
                    if (item is double d) listHx.Add(d);
                    else if (item is IConvertible) listHx.Add(Convert.ToDouble(item));
                }
                if (listHx.Count > 0) return listHx;
            }

            var enumerable = EnumerateUnknownCollection(value);
            if (enumerable == null) return null;

            var list = new List<double>();
            foreach (var item in enumerable)
            {
                if (item is double d) list.Add(d);
                else if (item is IConvertible) list.Add(Convert.ToDouble(item));
            }
            return list.Count > 0 ? list : null;
        }

        private static List<string>? ExtractStringList(object? value)
        {
            var hxItems = EnumerateHxArray(value);
            if (hxItems != null)
            {
                var listHx = new List<string>();
                foreach (var item in hxItems)
                {
                    if (item == null) continue;
                    listHx.Add(item.ToString() ?? string.Empty);
                }
                if (listHx.Count > 0) return listHx;
            }

            var enumerable = EnumerateUnknownCollection(value);
            if (enumerable == null) return null;

            var list = new List<string>();
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                list.Add(item.ToString() ?? string.Empty);
            }
            return list.Count > 0 ? list : null;
        }

        private static List<HistoryEntry>? ExtractHistoryList(object? value)
        {
            var hxItems = EnumerateHxArray(value);
            if (hxItems != null)
            {
                var listHx = new List<HistoryEntry>();
                foreach (var item in hxItems)
                {
                    if (item == null) continue;
                    listHx.Add(new HistoryEntry
                    {
                        brut = SafeToInt(GetMemberValue(item, "brut", ignoreCase: true), 0),
                        cellsEarned = SafeToInt(GetMemberValue(item, "cellsEarned", ignoreCase: true), 0),
                        level = GetMemberValue(item, "level", ignoreCase: true)?.ToString(),
                        surv = SafeToInt(GetMemberValue(item, "surv", ignoreCase: true), 0),
                        tact = SafeToInt(GetMemberValue(item, "tact", ignoreCase: true), 0),
                        time = GetDoubleValue(item, "time", 0)
                    });
                }
                if (listHx.Count > 0) return listHx;
            }

            var enumerable = EnumerateUnknownCollection(value);
            if (enumerable == null) return null;

            var list = new List<HistoryEntry>();
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                var entry = new HistoryEntry
                {
                    brut = GetFieldValue(item!, "brut", 0),
                    cellsEarned = GetFieldValue(item!, "cellsEarned", 0),
                    level = GetMemberValue(item!, "level", ignoreCase: true)?.ToString(),
                    surv = GetFieldValue(item!, "surv", 0),
                    tact = GetFieldValue(item!, "tact", 0),
                    time = GetDoubleValue(item!, "time", 0)
                };
                list.Add(entry);
            }
            return list.Count > 0 ? list : null;
        }

        private static List<double>? ExtractDoubleListFromJson(object? value)
        {
            try
            {
                if (value == null) return null;
                var arr = JArray.FromObject(value);
                var list = arr.Select(x => SafeToDouble(x)).ToList();
                return list.Count > 0 ? list : null;
            }
            catch { return null; }
        }

        private static List<string>? ExtractStringListFromJson(object? value)
        {
            try
            {
                if (value == null) return null;
                var arr = JArray.FromObject(value);
                var list = arr.Select(x => x?.ToString() ?? string.Empty).ToList();
                return list.Count > 0 ? list : null;
            }
            catch { return null; }
        }

        private static List<HistoryEntry>? ExtractHistoryListFromJson(object? value)
        {
            try
            {
                if (value == null) return null;
                var arr = JArray.FromObject(value);
                var list = arr.Select(h => new HistoryEntry
                {
                    brut = h["brut"]?.Value<int>() ?? 0,
                    cellsEarned = h["cellsEarned"]?.Value<int>() ?? 0,
                    level = h["level"]?.ToString(),
                    surv = h["surv"]?.Value<int>() ?? 0,
                    tact = h["tact"]?.Value<int>() ?? 0,
                    time = h["time"]?.Value<double>() ?? 0
                }).ToList();
                return list.Count > 0 ? list : null;
            }
            catch { return null; }
        }

        private static double SafeToDouble(JToken token)
        {
            if (token == null) return 0;
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return token.Value<double>();
            double.TryParse(token.ToString(), out var v);
            return v;
        }

        private static double SafeToDouble(object? value, double fallback = 0.0)
        {
            try
            {
                if (value == null) return fallback;
                if (value is double d) return d;
                if (value is float f) return f;
                if (value is int i) return i;
                if (value is long l) return l;
                if (value is IConvertible) return Convert.ToDouble(value);
                if (double.TryParse(value.ToString(), out var v)) return v;
            }
            catch { }
            return fallback;
        }

        private static void ApplyLevelDescSync(object desc, LevelDescSync sync)
        {
            if (desc == null) return;
            try
            {
                TrySetMember(desc, "mapDepth", sync.MapDepth);
                TrySetMember(desc, "minGold", sync.MinGold);
                TrySetMember(desc, "worldDepth", sync.WorldDepth);
                TrySetMember(desc, "baseLootLevel", sync.BaseLootLevel);
                TrySetMember(desc, "group", sync.Group);
                TrySetMember(desc, "doubleUps", sync.DoubleUps);
                TrySetMember(desc, "tripleUps", sync.TripleUps);
                TrySetMember(desc, "quarterUpsBC3", sync.QuarterUpsBC3);
                TrySetMember(desc, "quarterUpsBC4", sync.QuarterUpsBC4);
                TrySetMember(desc, "mobDensity", sync.MobDensity);
                TrySetMember(desc, "eliteRoomChance", sync.EliteRoomChance);
                TrySetMember(desc, "eliteWanderChance", sync.EliteWanderChance);
                TrySetMember(desc, "bonusTripleScrollAfterBC", sync.BonusTripleScrollAfterBC.ToString(System.Globalization.CultureInfo.InvariantCulture));
                TrySetMember(desc, "cellBonus", sync.CellBonus);
                if (!string.IsNullOrWhiteSpace(sync.LevelId))
                {
                    TrySetMember(desc, "id", sync.LevelId);
                }
                if (!string.IsNullOrWhiteSpace(sync.Name))
                {
                    TrySetMember(desc, "name", sync.Name);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply LevelDescSync: {Message}", ex.Message);
            }
        }

        private static double GetDoubleValue(object obj, string name, double fallback)
        {
            try
            {
                var member = GetMemberValue(obj, name, ignoreCase: true);
                if (member == null) return fallback;
                if (member is double d) return d;
                if (member is IConvertible) return Convert.ToDouble(member);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to read double field {Field}: {Message}", name, ex.Message);
            }
            return fallback;
        }

        private static IEnumerable<object?>? EnumerateUnknownCollection(object? value)
        {
            if (value == null) return null;
            if (value is string) return null;

            var hx = EnumerateHxArray(value);
            if (hx != null) return hx;

            if (value is IEnumerable enumerable)
            {
                return enumerable.Cast<object?>();
            }

            var len = GetLength(value);
            if (len <= 0) return null;

            var type = value.GetType();
            var getter = GetIndexer(type);

            if (getter == null) return null;

            var list = new List<object?>();
            for (int i = 0; i < len; i++)
            {
                try { list.Add(getter.Invoke(value, new object[] { i })); }
                catch { list.Add(null); }
            }

            return list;
        }

        private static List<object?>? EnumerateHxArray(object? value)
        {
            if (value == null) return null;
            var type = value.GetType();
            var fullName = type.FullName ?? string.Empty;
            if (!fullName.Contains("dc.hl.types.Array", StringComparison.OrdinalIgnoreCase)) return null;

            int len = GetLength(value);
            var getter = type.GetMethod("getDyn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var list = new List<object?>();

            if (len > 0 && getter != null)
            {
                for (int i = 0; i < len; i++)
                {
                    try { list.Add(getter.Invoke(value, new object[] { i })); }
                    catch { list.Add(null); }
                }
                return list;
            }

            var arrProp = type.GetProperty("array", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (arrProp != null)
            {
                try
                {
                    if (arrProp.GetValue(value) is Array raw)
                    {
                        foreach (var o in raw) list.Add(o);
                        return list;
                    }
                }
                catch { }
            }

            return null;
        }

        private static int GetLength(object obj)
        {
            try
            {
                var val = GetMemberValue(obj, "length", ignoreCase: true)
                       ?? GetMemberValue(obj, "Count", ignoreCase: true)
                       ?? GetMemberValue(obj, "count", ignoreCase: true)
                       ?? GetMemberValue(obj, "Length", ignoreCase: true);
                if (val is int i) return i;
                if (val is IConvertible) return Convert.ToInt32(val);
            }
            catch { }
            return 0;
        }

        private static object? FindMemberRecursive(object obj, string targetName, int depth = 4, HashSet<object>? visited = null)
        {
            if (obj == null || depth < 0) return null;

            visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (!visited.Add(obj)) return null;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = obj.GetType();

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                object? val = null;
                try { val = prop.GetValue(obj); } catch { }
                if (val == null) continue;

                if (string.Equals(prop.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    return val;
            }

            foreach (var field in type.GetFields(flags))
            {
                object? val = null;
                try { val = field.GetValue(obj); } catch { }
                if (val == null) continue;

                if (string.Equals(field.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    return val;
            }

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                object? val = null;
                try { val = prop.GetValue(obj); } catch { }
                if (val == null) continue;

                var found = FindMemberRecursive(val, targetName, depth - 1, visited);
                if (found != null) return found;

                if (val is IEnumerable enumerable && val is not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        found = FindMemberRecursive(item, targetName, depth - 1, visited);
                        if (found != null) return found;
                    }
                }
            }

            foreach (var field in type.GetFields(flags))
            {
                object? val = null;
                try { val = field.GetValue(obj); } catch { }
                if (val == null) continue;

                var found = FindMemberRecursive(val, targetName, depth - 1, visited);
                if (found != null) return found;

                if (val is IEnumerable enumerable && val is not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        found = FindMemberRecursive(item, targetName, depth - 1, visited);
                        if (found != null) return found;
                    }
                }
            }

            return null;
        }

        private static object? FindMemberRecursiveContains(object obj, string targetName, int depth = 4, HashSet<object>? visited = null)
        {
            if (obj == null || depth < 0) return null;

            visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (!visited.Add(obj)) return null;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = obj.GetType();

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                object? val = null;
                try { val = prop.GetValue(obj); } catch { }
                if (val == null) continue;

                if (prop.Name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return val;
            }

            foreach (var field in type.GetFields(flags))
            {
                object? val = null;
                try { val = field.GetValue(obj); } catch { }
                if (val == null) continue;

                if (field.Name.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return val;
            }

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                object? val = null;
                try { val = prop.GetValue(obj); } catch { }
                if (val == null) continue;

                var found = FindMemberRecursiveContains(val, targetName, depth - 1, visited);
                if (found != null) return found;

                if (val is IEnumerable enumerable && val is not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        found = FindMemberRecursiveContains(item, targetName, depth - 1, visited);
                        if (found != null) return found;
                    }
                }
            }

            foreach (var field in type.GetFields(flags))
            {
                object? val = null;
                try { val = field.GetValue(obj); } catch { }
                if (val == null) continue;

                var found = FindMemberRecursiveContains(val, targetName, depth - 1, visited);
                if (found != null) return found;

                if (val is IEnumerable enumerable && val is not string)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        found = FindMemberRecursiveContains(item, targetName, depth - 1, visited);
                        if (found != null) return found;
                    }
                }
            }

            return null;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);
            int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static MethodInfo? GetIndexer(System.Type type)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var m in methods)
            {
                if ((m.Name.Equals("get_Item", StringComparison.OrdinalIgnoreCase) ||
                     m.Name.Equals("Item", StringComparison.OrdinalIgnoreCase) ||
                     m.Name.Equals("At", StringComparison.OrdinalIgnoreCase) ||
                     m.Name.Equals("get", StringComparison.OrdinalIgnoreCase) ||
                     m.Name.Equals("index", StringComparison.OrdinalIgnoreCase)) &&
                    m.GetParameters().Length == 1 &&
                    (m.GetParameters()[0].ParameterType == typeof(int) || m.GetParameters()[0].ParameterType == typeof(long)))
                {
                    return m;
                }
            }

            var prop = type.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return prop?.GetGetMethod(true);
        }



        private static void ApplyList(IEnumerable<double>? source, object? target)
        {
            if (source == null || target is not IList list) return;
            try
            {
                list.Clear();
                foreach (var v in source)
                {
                    list.Add(v);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply forge list: {Message}", ex.Message);
            }
        }

        private static void ApplyStringList(IEnumerable<string>? source, object? target)
        {
            if (source == null || target is not IList list) return;
            try
            {
                list.Clear();
                foreach (var v in source)
                {
                    list.Add(v);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply meta list: {Message}", ex.Message);
            }
        }

        private static void ApplyHistory(IEnumerable<HistoryEntry>? source, object? target)
        {
            if (source == null || target is not IList list) return;
            try
            {
                list.Clear();
                foreach (var item in source)
                {
                    var newEntry = CreateHistoryObject(list, item);
                    list.Add(newEntry ?? item);
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to apply history list: {Message}", ex.Message);
            }
        }

        private static object? CreateHistoryObject(IList targetList, HistoryEntry src)
        {
            try
            {
                var elementType = targetList.GetType().GetGenericArguments().FirstOrDefault() ??
                                  targetList.Cast<object?>().FirstOrDefault()?.GetType();
                if (elementType == null) return null;

                var obj = Activator.CreateInstance(elementType);
                if (obj == null) return null;

                TrySetMember(obj, "brut", src.brut);
                TrySetMember(obj, "cellsEarned", src.cellsEarned);
                TrySetMember(obj, "level", src.level);
                TrySetMember(obj, "surv", src.surv);
                TrySetMember(obj, "tact", src.tact);
                TrySetMember(obj, "time", src.time);

                return obj;
            }
            catch
            {
                return null;
            }
        }

        private static void LogRunParams(string title, RunParams rp)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(title);
                sb.Append(": lvl=");
                sb.Append(rp.lvl);
                sb.Append(", custom=");
                sb.Append(rp.isCustom);
                sb.Append(", mode=");
                sb.Append(rp.mode);
                sb.Append(", bossRune=");
                sb.Append(rp.bossRune);
                sb.Append(", runNum=");
                sb.Append(rp.runNum?.ToString() ?? "null");
                sb.Append(", endKind=");
                sb.Append(rp.endKind ?? "null");
                sb.Append(", hasMods=");
                sb.Append(rp.hasMods?.ToString() ?? "null");

                sb.Append(", forge=[");
                if (rp.forge != null)
                    sb.Append(string.Join(",", rp.forge));
                sb.Append("]");

                sb.Append(", historyCount=");
                sb.Append(rp.history?.Count ?? 0);
                if (rp.history != null)
                {
                    for (int i = 0; i < rp.history.Count; i++)
                    {
                        var h = rp.history[i];
                        sb.Append($" | hist[{i}]: level={h.level}, brut={h.brut}, surv={h.surv}, tact={h.tact}, time={h.time}, cells={h.cellsEarned}");
                    }
                }

                sb.Append(", meta=[");
                if (rp.meta != null)
                    sb.Append(string.Join(",", rp.meta));
                sb.Append("]");

                _log?.Information(sb.ToString());
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to log run params: {Message}", ex.Message);
            }
        }


        // ---------------------------------------------------------
        // SEED UTILITIES
        // ---------------------------------------------------------
        private static int GenerateSeed()
        {
            int s = Random.Shared.Next(1, MaxSeed);
            return s == 0 ? 1 : s;
        }

        private static int Normalize(int s)
        {
            int v = System.Math.Abs(s % MaxSeed);
            return v == 0 ? 1 : v;
        }

        public static int ForceGenerateServerSeed(string origin)
        {
            lock (Sync)
            {
                _serverSeed = GenerateSeed();
                _lastHostSeed = _serverSeed;
                _log?.Information("[NetMod] Host generated run seed {Seed} ({Origin})", _serverSeed, origin);
                if (_serverSeed.HasValue && NetRef != null)
                    NetRef.SendSeed(_serverSeed.Value);
                return _serverSeed ?? 1;
            }
        }

        public static bool TryGetHostRunSeed(out int seed)
        {
            lock (Sync)
            {
                if (_lastHostSeed.HasValue)
                {
                    seed = _lastHostSeed.Value;
                    return true;
                }
                if (_serverSeed.HasValue)
                {
                    seed = _serverSeed.Value;
                    return true;
                }
            }

            seed = 0;
            return false;
        }


        private static bool WaitForRemote(TimeSpan timeout, out int s)
        {
            s = 0;

            bool ok = SpinWait.SpinUntil(() =>
            {
                lock (Sync) return _remoteSeed.HasValue;
            }, timeout);

            if (!ok) return false;

            lock (Sync)
            {
                s = _remoteSeed!.Value;
                return true;
            }
        }


        private static ArrayObj GenerateHookImpl(
            Delegate orig,
            LevelGen self,
            User user,
            int ldat,
            object desc,
            Ref<bool> resetCount)
        {
            int final = _serverSeed ?? _remoteSeed ?? GenerateSeed();
            bool wait = false;
            int? serverCopy = null;
            int? remoteCopy = null;
            NetRole roleCopy;
            RunParamsResolved? runParams = null;
            

            lock (Sync)
            {
                roleCopy = _role;

                // HOST
                if (_role == NetRole.Host)
                {
                    _serverSeed ??= GenerateSeed();
                    final = _serverSeed.Value;
                    _lastHostSeed = final;

                    serverCopy = final;

                    NetRef?.SendSeed(final);
                    _log?.Information("[NetMod] Host broadcast seed {Seed} (LevelGen.generate)", final);
                }
                // CLIENT
                else if (_role == NetRole.Client)
                {
                    if (_remoteSeed.HasValue)
                    {
                        final = _remoteSeed.Value;
                        remoteCopy = final;
                    }
                    else
                    {
                        wait = true;
                    }
                }
            }

            if (roleCopy == NetRole.Host && !TryGetResolvedRunParams(out runParams))
            {
                _log?.Warning("[NetMod] Host missing cached run params; skipping send this generate");
            }

            // CLIENT WAIT
            if (wait)
            {
                _log?.Information("[NetMod] Client waiting for seed (LevelGen.generate)...");

                if (WaitForRemote(TimeSpan.FromSeconds(2), out var hs))
                {
                    final = hs;
                    remoteCopy = hs;
                    _log?.Information("[NetMod] Client got seed {Seed}", hs);
                }
                else
                {
                    _log?.Warning("[NetMod] Client TIMEOUT -> using fallback {Seed}", final);
                }
            }

            if (roleCopy == NetRole.Client && !TryGetResolvedRunParams(out runParams))
            {
                _log?.Information("[NetMod] Client waiting for run params (LevelGen.generate)...");
                if (!WaitForRunParams(TimeSpan.FromSeconds(2), out runParams))
                {
                    _log?.Warning("[NetMod] Client TIMEOUT waiting for run params; using local values");
                }
                else
                {
                    _log?.Information("[NetMod] Client received run params during generate");
                }
            }

            if (roleCopy == NetRole.Host && runParams != null)
            {
                try
                {
                    runParams.Data.mode = false;
                    if (NetRef != null)
                    {
                    var json = JsonConvert.SerializeObject(runParams.Data);
                    NetRef.SendRunParams(json);
                    LogRunParams("Host world params (generate)", runParams.Data);
                    }
                    else
                    {
                        _log?.Information("[NetMod] Skip sending run params: NetRef is null");
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Host failed to send run params: {Message}", ex.Message);
                }
            }

            if (roleCopy == NetRole.Host)
            {
                try
                {
                    SendLevelDescToClient(desc);

                    var dto = GetCachedGameDataSync();
                    if (dto == null)
                    {
                        var gd = GetMemberValue(user, "gameData", ignoreCase: true) as dc.tool.GameData;
                        if (gd != null)
                        {
                            dto = BuildGameDataSync(gd);
                        }
                    }

                    if (runParams != null && dto != null)
                    {
                        ApplyRunParamsToSync(dto, runParams.Data, ExtractLevelId(desc));
                    }
                    else if (dto != null && TryGetResolvedRunParams(out var fallbackRp) && fallbackRp != null)
                    {
                        ApplyRunParamsToSync(dto, fallbackRp.Data, ExtractLevelId(desc));
                    }

                    if (dto != null && !SyncHasCoreData(dto) && TryGetResolvedRunParams(out var cachedRp) && cachedRp != null)
                    {
                        ApplyRunParamsToSync(dto, cachedRp.Data, ExtractLevelId(desc));
                    }

                    if (dto != null && NetRef != null)
                    {
                        dto.Seed = final;
                        if (!SyncHasCoreData(dto) && runParams != null)
                        {
                            ApplyRunParamsToSync(dto, runParams.Data, ExtractLevelId(desc));
                        }

                        if (!SyncHasCoreData(dto)) return _origInvoker(orig, self, user, ldat, desc, resetCount);

                        CacheGameDataSync(dto);
                        var json = JsonConvert.SerializeObject(dto);
                        NetRef.SendGameData(json);
                        _hostGameDataSent = true;
                        _log?.Information("[NetMod] Host sent GameDataSync (seed={Seed}, bytes={Len})", dto.Seed, json.Length);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Host failed to send GameDataSync: {Message}", ex.Message);
                }
            }

            final = Normalize(final);
            _lastHostSeed ??= final;

            _log?.Information(
                "[NetMod] GenerateHook: role={Role}, ldat={Ldat}, server={Server}, remote={Remote}, final={Final}",
                roleCopy, ldat, serverCopy ?? -1, remoteCopy ?? -1, final);

            if (roleCopy == NetRole.Client)
            {
                try
                {
                    var ldSync = GetCachedLevelDescSync();
                    var currentLevelId = ExtractLevelId(desc);
                    if (ldSync != null && !IsChallengeLevel(ldSync.LevelId) &&
                        (string.IsNullOrWhiteSpace(ldSync.LevelId) || string.Equals(ldSync.LevelId, currentLevelId, StringComparison.OrdinalIgnoreCase)))
                    {
                        ApplyLevelDescSync(desc, ldSync);
                        _log?.Information("[NetMod] Client applied LevelDescSync in LevelGen.generate");
                        LogLevelDesc("Client LevelDesc applied", ldSync);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warning("[NetMod] Client failed to apply LevelDescSync: {Message}", ex.Message);
                }
            }

            // CALL ORIGINAL
            if (_origInvoker == null)
                throw new InvalidOperationException("Hook_LevelGen.generate invoker is not initialized");

            return _origInvoker(orig, self, user, ldat, desc, resetCount);
        }

        private static void SendLevelDescToClient(object desc)
        {
            try
            {
                var sync = BuildLevelDescSync(desc);
                if (IsChallengeLevel(sync.LevelId))
                {
                    _log?.Information("[NetMod] Skip sending LevelDesc (Challenge)");
                    return;
                }
                CacheLevelDescSync(sync);
                if (NetRef != null)
                {
                    NetRef.SendLevelDesc(JsonConvert.SerializeObject(sync));
                }
                LogLevelDesc("Host LevelDesc", sync);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Host failed to send LevelDesc: {Message}", ex.Message);
            }
        }
    }
}



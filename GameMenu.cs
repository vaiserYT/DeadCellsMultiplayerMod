using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using MonoMod.RuntimeDetour;
using Serilog;
using dc;
using dc.hl.types;
using dc.libs;
using dc.level;
using dc.pr;
using ModCore.Modules;

namespace DeadCellsMultiplayerMod
{
    internal static class GameMenu
    {
        private static readonly object Sync = new();

        private static ILogger? _log;
        private static Delegate? _generateHookDelegate;
        private static Func<Delegate, LevelGen, User, int, object, Ref<bool>, ArrayObj>? _origInvoker;
        private static Delegate? _newGameHookDelegate;
        private static Action<Delegate, User, int, object, bool, bool, LaunchMode>? _newGameInvoker;
        private static Hook? _delegateHook;

        private static NetRole _role = NetRole.None;
        private static int? _serverSeed;   // host-generated seed
        private static int? _remoteSeed;   // client-received seed
        private static int? _lastHostSeed; // stored for transitions

        private const int MaxSeed = 999_999;

        public static NetNode? NetRef { get; set; }

        private const string LevelDescTypeName = "Hashlink.Virtuals.virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_";
        private static readonly System.Type? LevelDescType = System.Type.GetType(LevelDescTypeName) ?? typeof(Hook_LevelGen).Assembly.GetType(LevelDescTypeName);
        private static readonly bool EnableLevelGenHook = true;


        // ---------------------------------------------------------
        // INITIALIZE
        // ---------------------------------------------------------
        public static void Initialize(ILogger logger)
        {
            lock (Sync)
            {
                _log = logger;

                _log?.Information("[NetMod] GameMenu.Initialize — attaching RNG hooks");

                // Patch UtilityDelegates.CreateDelegate to shorten generated names (avoids 1024-char limit)
                TryPatchUtilityDelegates();

                // Hook_Rand.initSeed += InitSeedHook;
                TryAttachNewGameHook();

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
            var finalSeed = lvl;
            var incoming = lvl;

            lock (Sync)
            {
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

            _log?.Information("[NetMod] User.newGame hook: incoming={Incoming}, final={Final}, custom={Custom}, mode={Mode}, role={Role}",
                incoming, finalSeed, isCustom, mode, _role);

            if (_newGameInvoker == null)
                throw new InvalidOperationException("Hook_User.newGame invoker is not initialized");

            _newGameInvoker(orig, self, finalSeed, desc, isCustom, mode, gdata);
        }


        // ---------------------------------------------------------
        // ROLE MGMT
        // ---------------------------------------------------------
        public static void SetRole(NetRole role)
        {
            lock (Sync)
            {
                _role = role;

                if (role != NetRole.Client)
                    _remoteSeed = null;

                _log?.Information("[NetMod] SetRole -> {Role}, server={S}, remote={R}",
                    role, _serverSeed ?? -1, _remoteSeed ?? -1);
            }
        }


        // ---------------------------------------------------------
        // RECEIVE SEED FROM HOST
        // ---------------------------------------------------------
        public static void ReceiveHostSeed(int s) => ReceiveHostRunSeed(s);

        public static void ReceiveHostRunSeed(int s)
        {
            lock (Sync)
            {
                _remoteSeed = Normalize(s);
            }

            _log?.Information("[NetMod] Received SEED from host: {Seed}", s);
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


        // ---------------------------------------------------------
        // HOOK: Rand.initSeed — sync RNG seeds
        // ---------------------------------------------------------
        private static void InitSeedHook(
            Hook_Rand.orig_initSeed orig,
            Rand self,
            int incomingSeed,
            int? extra)
        {
            int final = incomingSeed;
            bool wait = false;
            int? serverCopy = null;
            int? remoteCopy = null;
            NetRole roleCopy;

            lock (Sync)
            {
                roleCopy = _role;

                // HOST — always generates new seed
                if (_role == NetRole.Host)
                {
                    _serverSeed = GenerateSeed();
                    final = _serverSeed.Value;
                    _lastHostSeed = final;

                    serverCopy = final;

                    NetRef?.SendSeed(final);
                    _log?.Information("[NetMod] Host broadcast level-seed {Seed}", final);
                }
                // CLIENT — wait for host seed
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

            // WAIT FOR HOST SEED ON CLIENT
            if (wait)
            {
                _log?.Information("[NetMod] Client waiting for seed (Rand.initSeed)...");

                if (WaitForRemote(TimeSpan.FromSeconds(2), out var hs))
                {
                    final = hs;
                    remoteCopy = hs;
                    _log?.Information("[NetMod] Client received seed {Seed}", hs);
                }
                else
                {
                    _log?.Warning("[NetMod] Client TIMEOUT — using incoming {Seed}", incomingSeed);
                }
            }

            final = Normalize(final);

            _log?.Information(
                "[NetMod] initSeedHook: role={Role}, incoming={Incoming}, server={Server}, remote={Remote}, final={Final}",
                roleCopy,
                incomingSeed,
                serverCopy ?? -1,
                remoteCopy ?? -1,
                final);

            // CALL ORIGINAL HASHLINK RNG INIT
            orig(self, final, extra);
        }


        // ---------------------------------------------------------
        // HOOK: LevelGen.generate — mirror seeds across peers
        // ---------------------------------------------------------
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
                    _log?.Warning("[NetMod] Client TIMEOUT — using fallback {Seed}", final);
                }
            }

            final = Normalize(final);
            _lastHostSeed ??= final;

            _log?.Information(
                "[NetMod] GenerateHook: role={Role}, ldat={Ldat}, server={Server}, remote={Remote}, final={Final}",
                roleCopy, ldat, serverCopy ?? -1, remoteCopy ?? -1, final);

            // CALL ORIGINAL
            if (_origInvoker == null)
                throw new InvalidOperationException("Hook_LevelGen.generate invoker is not initialized");

            return _origInvoker(orig, self, user, ldat, desc, resetCount);
        }


        // ---------------------------------------------------------
        // OPTIONAL DEBUG
        // ---------------------------------------------------------
        public static void DebugSeeds()
        {
            _log?.Information("[NetMod] DEBUG: serverSeed={S}, remoteSeed={R}, lastHostSeed={L}",
                _serverSeed ?? -1, _remoteSeed ?? -1, _lastHostSeed ?? -1);
        }
    }
}
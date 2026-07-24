using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using ModCore.Events;
using dc;
using dc.level;
using dc.pr;
using dc.tool;
using Hashlink.Virtuals;
using HaxeProxy.Runtime;
using ModCore.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using dc.haxe;
using Rand = dc.libs.Rand;

namespace DeadCellsMultiplayerMod
{
    internal partial class GameDataSync : IEventReceiver, IOnAdvancedModuleInitializing
    {
        static Serilog.ILogger? _log;
        static public int Seed;

        static public virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ _isTwitch = default!;
        static public bool _isCustom;
        static public bool _mode;

        static public LaunchMode _launch = default!;
        private static readonly object _sameRunRestartSync = new();
        private static bool _sameRunRestartPending;
        private static int _sameRunRestartSeed;
        private static readonly object _bossRuneLock = new();
        private static int? _remoteBossRune;
        private static int? _hostBossRune;
        private static readonly object _bossRuneHudRefreshLock = new();
        private static int? _pendingBossRuneHudValue;
        private static long _bossRuneHudRefreshUntilTickMs;
        private static long _nextBossRuneHudRefreshTickMs;
        private const long BossRuneHudRefreshDurationMs = 3000;
        private const long BossRuneHudRefreshIntervalMs = 150;
        private static bool _origHeroCosmeticsCaptured;
        private static string? _origHeroSkin;
        private static string? _origHeroHeadSkin;
        private static NetNode? _lastHeroSkinSyncNet;
        private static string? _lastHeroSkinSyncPayload;
        private static NetNode? _lastHeroHeadSkinSyncNet;
        private static string? _lastHeroHeadSkinSyncPayload;

        private static double? _remoteMobsHpMult;
        private static double? _remoteBossesHpMult;
        private static bool _origHpMultipliersSaved;
        private static double _origMobsHpMult;
        private static double _origBossesHpMult;
        private static bool _origBossRuneCaptured;
        private static int _origBossRune;
        private static bool _hasRemoteBossRune;
        private static bool _suppressDeathBroadcast;
        private static readonly object _levelSeedLock = new();
        private static string? _remoteLevelId;
        private static double? _remoteLevelSeed;
        private static readonly object _serializerSyncLock = new();
        private static int _remoteSerializerSeq;
        private static int _remoteSerializerUid;
        private static bool _hasRemoteSerializerSync;
        private static bool _hasRemoteSerializerValues;
        private static bool _localSerializerCaptured;
        private static int _localSerializerSeq;
        private static int _localSerializerUid;

        public GameDataSync(Serilog.ILogger log)
        {
            _log = log;
            EventSystem.AddReceiver(this);
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {
            entry.Logger.Information("\x1b[32m[[ModEntry.GameDataSync] Initializing GameDataSync...]\x1b[0m ");
        }


        

        public static void user_hook_new_game(Hook_User.orig_newGame orig,
        User self,
        int lvl,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ isTwitch,
        bool isCustom,
        bool mode,
        LaunchMode gdata)
        {
            var sameRunRestart = TryPeekSameRunRestartSeed(out var restartSeed);
            var effectiveStreamEnabled = isCustom;
            var effectiveCustomMode = mode;
            var effectiveLaunch = gdata;
            if (gdata is LaunchMode.NewGame newGame)
            {
                if (sameRunRestart)
                {
                    effectiveCustomMode = ResolveCurrentRunIsCustom();
                    effectiveStreamEnabled = ResolveCurrentRunStreamEnabled();
                    effectiveLaunch = new LaunchMode.NewGame(effectiveCustomMode, effectiveStreamEnabled);
                }
                else if (GameMenu.TryGetAuthoritativePendingNewGameLaunch(out var selectedCustom, out var selectedStreamEnabled))
                {
                    effectiveCustomMode = selectedCustom;
                    effectiveStreamEnabled = selectedStreamEnabled;
                    effectiveLaunch = new LaunchMode.NewGame(selectedCustom, selectedStreamEnabled);
                }
                else
                {
                    // Keep Normal Mode multiplayer runs deterministic unless Custom Mode
                    // explicitly authored a pending launch.
                    effectiveCustomMode = false;
                    effectiveStreamEnabled = false;
                }
            }

            isCustom = effectiveStreamEnabled;
            mode = effectiveCustomMode;
            gdata = effectiveLaunch;

            Seed = sameRunRestart ? restartSeed : lvl;
            ModEntry.me = null!;
            ModEntry.ResetClientSlots();
            ModEntry.kingInitialized = false;
            ModEntry._ghost = null!;
            ModEntry.ResetHeroCosmeticSendCache();
            ClearPendingBossRuneReloadState();
            _lastHeroSkinSyncNet = null;
            _lastHeroSkinSyncPayload = null;
            _lastHeroHeadSkinSyncNet = null;
            _lastHeroHeadSkinSyncPayload = null;
            var net = GameMenu.NetRef;
            var launchKind = GetLaunchKind(gdata);
            var nativeBossRushLaunch = IsBossRushLaunchKind(launchKind);
            var expectedBossRushLaunch = !sameRunRestart &&
                                         (nativeBossRushLaunch ||
                                          (net?.IsHost == true && GameMenu.HasPrecommittedHostBossRushLaunch()) ||
                                          (net != null && !net.IsHost && GameMenu.HasPendingRemoteBossRushLaunch()));
            if (expectedBossRushLaunch && !nativeBossRushLaunch)
            {
                // Some generated bindings expose the Boss Rush launch variant with a runtime name
                // that does not contain the expected token. The synchronized door precommit is a
                // stronger signal than the local type name, so normalize the wire identity here.
                launchKind = "dc.LaunchMode+BossRush";
            }
            var shouldSynchronizeSeed = !sameRunRestart && (gdata is LaunchMode.NewGame || expectedBossRushLaunch);
            if (net == null || !net.IsAlive)
                RestoreOriginalUserState(self, true);

            if (net != null && net.IsHost)
            {
                var reusedPrecommittedSeed = false;
                var seedSequence = 0;
                if (sameRunRestart)
                {
                    Seed = restartSeed;
                }
                else if (shouldSynchronizeSeed &&
                    GameMenu.TryConsumePrecommittedHostRunSeed(launchKind, out var precommittedSeed, out var precommittedSequence))
                {
                    Seed = precommittedSeed;
                    seedSequence = precommittedSequence;
                    reusedPrecommittedSeed = true;
                    _log?.Information(
                        "[NetMod] Reusing precommitted host seed seq={Sequence} seed={Seed} launch={LaunchKind}",
                        seedSequence,
                        Seed,
                        launchKind);
                }
                else if (shouldSynchronizeSeed && ShouldGenerateFreshHostSeed(gdata))
                {
                    Seed = GameMenu.ForceGenerateServerSeed("NewGame_hook");
                }
                else
                {
                    Seed = lvl;
                }

                SendBossRune(self, net);
                SendSerializerSync(net);
                if (shouldSynchronizeSeed)
                {
                    if (!reusedPrecommittedSeed)
                        seedSequence = GameMenu.RegisterHostRunSeed(Seed, launchKind, "user.newGame");

                    // Commit/ACK/execute is now the authoritative launch barrier. The legacy seed
                    // remains as a migration/debug packet, but cannot make a v0.8.90 client load by itself.
                    GameMenu.CommitHostRunLaunchFromHook(Seed, seedSequence, launchKind);
                    // Resending the same precommitted sequence is intentional: it refreshes the
                    // host cache and covers a client that completed its handshake during the intro.
                    net.SendSeed(seedSequence, Seed, launchKind);
                    GameMenu.MarkRunLaunchLoading(seedSequence, $"host_user.newGame:{launchKind}");
                }
            }
            else if (net != null)
            {
                if (sameRunRestart)
                {
                    Seed = restartSeed;
                }
                else if (shouldSynchronizeSeed &&
                    GameMenu.TryConsumeNextRemoteRunSeed(out var remoteSeed, out var remoteSequence, out var remoteLaunchKind))
                {
                    Seed = remoteSeed;
                    GameMenu.MarkRunLaunchLoading(remoteSequence, $"client_user.newGame:{remoteLaunchKind}");
                    if (!string.IsNullOrWhiteSpace(remoteLaunchKind) &&
                        !string.Equals(remoteLaunchKind, launchKind, StringComparison.Ordinal))
                    {
                        _log?.Warning(
                            "[NetMod] Host/client launch kind differs for seed seq={Sequence}: host={HostLaunch} client={ClientLaunch}",
                            remoteSequence,
                            remoteLaunchKind,
                            launchKind);
                    }
                }
                else if (GameMenu.TryGetPendingRemoteBossRushSeed(out var authoritativeBossRushSeed))
                {
                    // Authoritative host seed already received but not consumed via the coordinator
                    // (e.g. arrived a frame late). This is still the host's seed, not a local one.
                    Seed = authoritativeBossRushSeed;
                    _log?.Warning(
                        "[NetMod][BossRushSeed] Using already-received authoritative host Boss Rush seed {Seed} (coordinator consume missed)",
                        authoritativeBossRushSeed);
                }
                else if (shouldSynchronizeSeed)
                {
                    // Protocol 17: never silently generate a local Boss Rush / run on the client. The
                    // launch gate (GameMenu.TryBeginLocalBossRushLoad / structured auto-start) is meant
                    // to guarantee the authoritative seed is present before newGame runs, so reaching
                    // here is a hard desync. Keep the native seed only as an unavoidable last resort and
                    // surface it loudly instead of quietly diverging.
                    Seed = lvl;
                    _log?.Error(
                        "[NetMod][BossRushSeed] CRITICAL desync: no authoritative host seed available at newGame (bossRush={BossRush}); refusing to hide the failure. localSeed={LocalSeed}",
                        expectedBossRushLaunch,
                        lvl);
                    DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI.MultiplayerUI.PushSystemMessage(
                        GameMenu.Localize("Run failed to synchronize with your friend. Please return to the menu and retry."),
                        8.0,
                        1.0);
                }
                else
                {
                    // Non-synchronized nested launches (challenge rooms, daily, etc.) are intentionally local.
                    Seed = lvl;
                }
                if (TryGetRemoteBossRune(out var bossRune))
                {
                    ApplyRemoteBossRune(self, bossRune);
                }
                else
                {
                    _log?.Warning("[NetMod] Remote boss rune not received yet");
                }

                CaptureOriginalUserData(self, allowReplaceWhenBetter: true);
            }

            if (expectedBossRushLaunch)
            {
                _log?.Information(
                    "[NetMod][BossRushSeed] Applying authoritative seed role={Role} localSeed={LocalSeed} chosenSeed={ChosenSeed} launch={LaunchKind}",
                    net?.IsHost == true ? "host" : "client",
                    lvl,
                    Seed,
                    launchKind);
                BossSyncDiag.Trace(
                    "bossrush seed applied role={Role} chosenSeed={Seed} localSeed={LocalSeed} launch={Kind}",
                    BossSyncDiag.Role(net),
                    Seed,
                    lvl,
                    launchKind);
            }
            lvl = Seed;
            _isTwitch = isTwitch;
            _isCustom = isCustom;
            _mode = mode;
            _launch = gdata;
            self.pickDeathItem();
            SendHeroSkin(self, net);
            SendHeroHeadSkin(self, net);
            if (mode)
            {
                // Custom Mode GameData ctor calls CustomGameData.checkIntegrity(user) which
                // immediately touches user.itemMeta. Main.getGame loads User via Save.tryLoad,
                // so TitleScreen prep alone is not enough on the client auto-start path.
                if (!GameMenu.PrepareUserForCustomModeLaunch(self))
                {
                    _log?.Warning(
                        "[NetMod] Custom Mode newGame: User.itemMeta could not be prepared (role={Role})",
                        net?.IsHost == true ? "host" : "client");
                }
            }

            try
            {
                orig(self, lvl, isTwitch, isCustom, mode, gdata);
            }
            finally
            {
                GameMenu.ClearAuthoritativePendingNewGameLaunch();
                if (sameRunRestart)
                    ClearSameRunRestart();
            }

        }

        private static bool ShouldSynchronizeRunSeed(LaunchMode? launch)
        {
            // The initial full run needs a seed barrier. Boss Rush also needs one: the boss
            // sequence, modifiers and arena order all derive from the launch seed, so without a
            // shared seed each side fights different encounters. The historical double-load race
            // this gate used to prevent came from the client-side reconcile restart firing on an
            // unconsumed nested seed; that restart is now suppressed for Boss Rush kinds in
            // GameMenu.ReceiveHostRunSeed, so sharing the seed is safe. Challenge rooms, daily
            // modes and other nested launches stay local.
            lock (_sameRunRestartSync)
            {
                if (_sameRunRestartPending)
                    return false;
            }

            return launch is LaunchMode.NewGame || IsBossRushLaunch(launch);
        }

        private static bool ShouldGenerateFreshHostSeed(LaunchMode? launch)
        {
            return launch is LaunchMode.NewGame || IsBossRushLaunch(launch);
        }

        internal static bool IsBossRushLaunch(LaunchMode? launch)
        {
            return IsBossRushLaunchKind(GetLaunchKind(launch));
        }

        /// <summary>
        /// Launch kinds travel on the wire as the runtime type name of the LaunchMode variant
        /// (e.g. "dc.LaunchMode+BossRush"), so a substring check identifies the mode without a
        /// compile-time dependency on the generated binding shape.
        /// </summary>
        internal static bool IsBossRushLaunchKind(string? launchKind)
        {
            return !string.IsNullOrEmpty(launchKind) &&
                   launchKind.Contains("BossRush", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLaunchKind(LaunchMode? launch)
        {
            if (launch == null)
                return "unknown";

            try
            {
                return launch.GetType().FullName ?? launch.GetType().Name ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        internal static void BeginSameRunRestart(int seed)
        {
            lock (_sameRunRestartSync)
            {
                _sameRunRestartPending = true;
                _sameRunRestartSeed = seed;
            }
        }

        private static bool TryPeekSameRunRestartSeed(out int seed)
        {
            lock (_sameRunRestartSync)
            {
                if (_sameRunRestartPending)
                {
                    seed = _sameRunRestartSeed;
                    return true;
                }
            }

            seed = 0;
            return false;
        }

        private static void ClearSameRunRestart()
        {
            lock (_sameRunRestartSync)
            {
                _sameRunRestartPending = false;
                _sameRunRestartSeed = 0;
            }
        }

        internal static void CancelSameRunRestart()
        {
            ClearSameRunRestart();
        }

        internal static bool ResolveCurrentRunIsCustom()
        {
            try
            {
                var currentCustomGame = dc.pr.Game.Class.ME?.data?.cgData;
                if (currentCustomGame != null)
                    return true;
            }
            catch
            {
            }

            try
            {
                var mainGameData = dc.Main.Class.ME?.user?.mainGameData;
                if (mainGameData != null)
                    return mainGameData.isCustom;
            }
            catch
            {
            }

            return _isCustom;
        }

        internal static bool ResolveCurrentRunStreamEnabled()
        {
            try
            {
                var game = dc.pr.Game.Class.ME;
                if (game?.data != null)
                    return game.data._twitchMode;
            }
            catch
            {
            }

            if (_launch is LaunchMode.NewGame storedNewGame)
                return storedNewGame.Param1;

            return false;
        }

        internal static LaunchMode BuildSameRunRestartLaunchMode()
        {
            return new LaunchMode.NewGame(ResolveCurrentRunIsCustom(), ResolveCurrentRunStreamEnabled());
        }

        public static bool SwapToOriginalUserData(User user)
        {
            var swapped = false;
            if (_hasRemoteBossRune && _origBossRuneCaptured)
            {
                user.bossRuneActivated = _origBossRune;
                swapped = true;
            }

            return swapped;
        }

        public static bool RestoreOriginalUserState(User user, bool clearRemote)
        {
            var restored = false;
            if (_origBossRuneCaptured)
            {
                user.bossRuneActivated = _origBossRune;
                restored = true;
            }

            ApplyHeroCosmetics(user, _origHeroSkin, _origHeroHeadSkin);

            if (clearRemote)
            {
                _hasRemoteBossRune = false;
                _hasRemoteSerializerSync = false;
                _hasRemoteSerializerValues = false;
                _remoteSerializerSeq = 0;
                _remoteSerializerUid = 0;
                lock (_bossRuneLock)
                {
                    _remoteBossRune = null;
                }
                ClearPendingBossRuneReloadState();
                RestoreLocalSerializerSyncIfCaptured();
                _origHeroCosmeticsCaptured = false;
                _origHeroSkin = null;
                _origHeroHeadSkin = null;
                _origBossRuneCaptured = false;
                _origBossRune = 0;
                _lastHeroSkinSyncNet = null;
                _lastHeroSkinSyncPayload = null;
                _lastHeroHeadSkinSyncNet = null;
                _lastHeroHeadSkinSyncPayload = null;
            }

            return restored;
        }

        public static void CaptureOriginalUserData(User user, bool allowReplaceWhenBetter = false)
        {
            if (user != null &&
                (!_origHeroCosmeticsCaptured ||
                 (allowReplaceWhenBetter &&
                  (string.IsNullOrWhiteSpace(_origHeroSkin) || string.IsNullOrWhiteSpace(_origHeroHeadSkin)))))
            {
                _origHeroCosmeticsCaptured = true;
                _origHeroSkin = CleanSkin(user.heroSkin?.ToString());
                _origHeroHeadSkin = CleanSkin(user.heroHeadSkin?.ToString());
            }

            if (user != null && !_origBossRuneCaptured)
            {
                _origBossRuneCaptured = true;
                _origBossRune = user.bossRuneActivated;
            }
        }

        public static void RestoreRemoteUserData(User user)
        {
            if (TryGetRemoteBossRune(out var bossRune))
                ApplyRemoteBossRune(user, bossRune);
        }

        public static void TriggerRemoteDeath()
        {
            GameMenu.EnqueueCriticalMainThreadCoalesced("game:remote-death", () =>
            {
                if (ModEntry.IsLocalPlayerDowned())
                    return;

                _suppressDeathBroadcast = true;
                try
                {
                    ModEntry.me?.kill();
                }
                catch
                {
                }
                finally
                {
                    // kill()/onHeroDie normally consumes this synchronously. Never leave a stale
                    // suppression flag behind if the hero vanished or native death handling threw.
                    _suppressDeathBroadcast = false;
                }
            });
        }

        public static bool ConsumeSuppressDeathBroadcast()
        {
            if (!_suppressDeathBroadcast)
                return false;
            _suppressDeathBroadcast = false;
            return true;
        }

        public static void ReceiveLevelSeed(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            var sep = payload.IndexOf('|');
            if (sep <= 0 || sep >= payload.Length - 1)
                return;

            var levelId = payload[..sep];
            var seedText = payload[(sep + 1)..];
            if (string.IsNullOrWhiteSpace(levelId) || levelId.Length > 128)
                return;

            if (!double.TryParse(seedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seed) ||
                double.IsNaN(seed) || double.IsInfinity(seed))
                return;

            lock (_levelSeedLock)
            {
                _remoteLevelId = levelId;
                _remoteLevelSeed = seed;
            }
        }

        public static bool TryApplyRemoteLevelSeed(string levelId, Rand rng)
        {
            if (rng == null || string.IsNullOrWhiteSpace(levelId))
                return false;

            lock (_levelSeedLock)
            {
                if (_remoteLevelSeed.HasValue && string.Equals(_remoteLevelId, levelId, StringComparison.Ordinal))
                {
                    rng.seed = _remoteLevelSeed.Value;
                    // One level-seed packet belongs to one graph generation. Leaving it cached made
                    // a later visit to the same biome reuse an old seed before the new packet arrived.
                    _remoteLevelId = null;
                    _remoteLevelSeed = null;
                    return true;
                }
            }

            return false;
        }

        public static void SendLevelSeed(string levelId, Rand rng, NetNode? net)
        {
            if (net == null || !net.IsAlive || rng == null || string.IsNullOrWhiteSpace(levelId))
                return;

            GameMenu.PublishInitialLevelSeed(levelId, rng.seed, net);
            SendSerializerSync(net);
            net.SendLevelSeed(levelId, rng.seed);
        }

        public static void ReceiveSerializerSync(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            var parts = payload.Split('|');
            if (parts.Length < 2)
                return;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                return;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
                return;

            lock (_serializerSyncLock)
            {
                _remoteSerializerSeq = seq;
                _remoteSerializerUid = uid;
                _hasRemoteSerializerSync = true;
                _hasRemoteSerializerValues = true;
            }
        }

        public static bool TryApplyRemoteSerializerSync()
        {
            int seq;
            int uid;
            lock (_serializerSyncLock)
            {
                if (!_hasRemoteSerializerSync)
                    return false;

                seq = _remoteSerializerSeq;
                uid = _remoteSerializerUid;
                _hasRemoteSerializerSync = false;
            }

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return false;

                if (!_localSerializerCaptured)
                {
                    _localSerializerSeq = serializerClass.SEQ;
                    _localSerializerUid = serializerClass.UID;
                    _localSerializerCaptured = true;
                }

                serializerClass.SEQ = seq;
                serializerClass.UID = uid;
                _hasRemoteSerializerValues = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool SwapToLocalSerializerSync()
        {
            if (!_localSerializerCaptured)
                return false;

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return false;

                if (serializerClass.SEQ == _localSerializerSeq &&
                    serializerClass.UID == _localSerializerUid)
                {
                    return false;
                }

                serializerClass.SEQ = _localSerializerSeq;
                serializerClass.UID = _localSerializerUid;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void RestoreRemoteSerializerSync()
        {
            if (!_hasRemoteSerializerValues)
                return;

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return;

                serializerClass.SEQ = _remoteSerializerSeq;
                serializerClass.UID = _remoteSerializerUid;
            }
            catch
            {
            }
        }

        public static void SendSerializerSync(NetNode? net)
        {
            if (net == null || !net.IsAlive || !net.IsHost)
                return;

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return;

                net.SendSerializerSync(serializerClass.SEQ, serializerClass.UID);
            }
            catch
            {
            }
        }

        internal static int GetBossRuneInt(User? user)
        {
            return user == null ? 0 : GetEffectiveBossRune(user);
        }

        public static void SendBossRune(User self, NetNode? net)
        {
            if (self == null)
                return;

            var bossRune = GetEffectiveBossRune(self);
            lock (_bossRuneLock)
            {
                _hostBossRune = bossRune;
            }

            if (net == null || !net.IsAlive)
                return;

            net.SendBossRune(bossRune);
        }

        public static void ReceiveBossRune(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            if (!int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bossRune))
            {
                return;
            }

            int? previousRemote;
            lock (_bossRuneLock)
            {
                previousRemote = _remoteBossRune;
                _remoteBossRune = bossRune;
            }
            _hasRemoteBossRune = true;

            var net = GameMenu.NetRef;
            if (net != null && net.IsHost)
                return;

            // Lobby auto-start waits on HasRemoteBossRune; re-arm if seed/exec already arrived.
            GameMenu.NotifyClientLaunchPrerequisiteProgress();

            RequestBossRuneHudRefresh(bossRune);

            // The client must rebuild only when an established remote boss-rune value changes.
            // Treating the first sync as a change causes a redundant startup reload; skipping a real
            // change lets the client load the host graph against stale boss-cell state.
            if (previousRemote.HasValue && previousRemote.Value != bossRune)
                MarkPendingBossRuneReload(bossRune);
            // _log?.Information("[NetMod] Received remote boss rune {BossRune}", bossRune);

            GameMenu.EnqueueCriticalMainThreadCoalesced("game:boss-rune-apply", () =>
            {
                try
                {
                    var user = dc.Main.Class.ME?.user;
                    if (user != null)
                    {
                        ApplyRemoteBossRune(user, bossRune);
                        PumpBossRuneHudRefresh();
                    }
                }
                catch
                {
                }

                // The level graph and BOSSRUNE packets may arrive in either order. Re-evaluate after
                // applying the value so the coalesced, throttled reload path can run once both exist.
                try
                {
                    var n = GameMenu.NetRef;
                    if (n != null && n.IsAlive && !n.IsHost)
                        TryScheduleBossRuneReloadForCurrentLevel();
                }
                catch
                {
                }
            });

        }

        public static bool TryGetHostBossRune(out int bossRune)
        {
            lock (_bossRuneLock)
            {
                if (_hostBossRune.HasValue)
                {
                    bossRune = _hostBossRune.Value;
                    return true;
                }
            }

            bossRune = 0;
            return false;
        }

        public static bool TryGetRemoteBossRune(out int bossRune)
        {
            lock (_bossRuneLock)
            {
                if (_remoteBossRune.HasValue)
                {
                    bossRune = _remoteBossRune.Value;
                    return true;
                }
            }

            bossRune = 0;
            return false;
        }

        internal static bool HasRemoteBossRune()
        {
            lock (_bossRuneLock)
            {
                return _remoteBossRune.HasValue;
            }
        }

        internal static void SendCurrentHeroCosmetics(User? user, NetNode? net, bool force = false)
        {
            if (user == null || net == null || !net.IsAlive)
                return;

            if (force)
            {
                _lastHeroSkinSyncNet = null;
                _lastHeroSkinSyncPayload = null;
                _lastHeroHeadSkinSyncNet = null;
                _lastHeroHeadSkinSyncPayload = null;
            }

            SendHeroSkin(user, net);
            SendHeroHeadSkin(user, net);
        }

        public static void SaveOrigHpMultipliers()
        {
            if (_origHpMultipliersSaved)
                return;
            _origMobsHpMult = MultiplayerSettingsStorage.MobsHpMultiplier;
            _origBossesHpMult = MultiplayerSettingsStorage.BossesHpMultiplier;
            _origHpMultipliersSaved = true;
        }

        public static void RestoreOrigHpMultipliers()
        {
            if (!_origHpMultipliersSaved)
                return;
            MultiplayerSettingsStorage.MobsHpMultiplier = _origMobsHpMult;
            MultiplayerSettingsStorage.BossesHpMultiplier = _origBossesHpMult;
            _remoteMobsHpMult = null;
            _remoteBossesHpMult = null;
            _origHpMultipliersSaved = false;
        }

        public static void ReceiveHpMultipliers(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return;

            var parts = payload.Split('|');
            if (parts.Length < 2)
                return;

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var mobsMult))
                return;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var bossesMult))
                return;

            SaveOrigHpMultipliers();
            _remoteMobsHpMult = mobsMult;
            _remoteBossesHpMult = bossesMult;
            MultiplayerSettingsStorage.MobsHpMultiplier = mobsMult;
            MultiplayerSettingsStorage.BossesHpMultiplier = bossesMult;
        }

        public static void ReceiveHeroSkin(string skin)
        {
            
            var cleaned = CleanSkin(skin);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = "PrisonerDefault";

            ModEntry.SetRemoteSkin(cleaned);
            
        }


        public static void ReceiveHeroHeadSkin(string skin)
        {
            var cleaned = CleanSkin(skin);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = "BaseFlame";

            ModEntry.SetRemoteHeadSkin(cleaned);
        }

        private static void SendHeroSkin(User user, NetNode? net)
        {
            if (net == null || !net.IsAlive)
                return;

            try
            {
                var skin = CleanSkin(user?.heroSkin?.ToString());
                if (string.IsNullOrWhiteSpace(skin))
                    skin = "PrisonerDefault";

                if (ReferenceEquals(_lastHeroSkinSyncNet, net) &&
                    string.Equals(_lastHeroSkinSyncPayload, skin, StringComparison.Ordinal))
                {
                    return;
                }

                net.SendHeroSkin(skin);
                _lastHeroSkinSyncNet = net;
                _lastHeroSkinSyncPayload = skin;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send hero skin: {Message}", ex.Message);
            }
        }


        private static void SendHeroHeadSkin(User user, NetNode? net)
        {
            if (net == null || !net.IsAlive)
                return;

            try
            {
                var skin = CleanSkin(user?.heroHeadSkin?.ToString());
                if (string.IsNullOrWhiteSpace(skin))
                    skin = "BaseFlame";

                if (ReferenceEquals(_lastHeroHeadSkinSyncNet, net) &&
                    string.Equals(_lastHeroHeadSkinSyncPayload, skin, StringComparison.Ordinal))
                {
                    return;
                }

                net.SendHeroHeadSkin(skin);
                _lastHeroHeadSkinSyncNet = net;
                _lastHeroHeadSkinSyncPayload = skin;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to send hero skin: {Message}", ex.Message);
            }
        }

        private static string CleanSkin(string? skin)
        {
            if (string.IsNullOrEmpty(skin))
                return string.Empty;

            return skin.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        }

        private static void ApplyRemoteBossRune(User user, int bossRune)
        {
            CaptureOriginalUserData(user);

            try
            {
                user.br_setActivated(bossRune);
            }
            catch
            {
                user.bossRuneActivated = bossRune;
            }

            try
            {
                var gameData = user.game?.data;
                if (gameData?.cgData != null)
                    gameData.cgData.numBossCells = bossRune;
            }
            catch
            {
            }

            _hasRemoteBossRune = true;
            RequestBossRuneHudRefresh(bossRune);
        }

        internal static void RequestBossRuneHudRefreshFromRemoteState()
        {
            if (TryGetRemoteBossRune(out var bossRune))
                RequestBossRuneHudRefresh(bossRune);
        }

        private static void RequestBossRuneHudRefresh(int bossRune)
        {
            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive || net.IsHost)
                return;

            var now = Environment.TickCount64;
            lock (_bossRuneHudRefreshLock)
            {
                _pendingBossRuneHudValue = bossRune;
                _bossRuneHudRefreshUntilTickMs = now + BossRuneHudRefreshDurationMs;
                _nextBossRuneHudRefreshTickMs = 0;
            }
        }

        internal static void PumpBossRuneHudRefresh()
        {
            var net = GameMenu.NetRef;
            if (net == null || !net.IsAlive || net.IsHost)
            {
                ClearBossRuneHudRefresh();
                return;
            }

            int bossRune;
            var now = Environment.TickCount64;
            lock (_bossRuneHudRefreshLock)
            {
                if (!_pendingBossRuneHudValue.HasValue)
                    return;
                if (now > _bossRuneHudRefreshUntilTickMs)
                {
                    _pendingBossRuneHudValue = null;
                    return;
                }
                if (_nextBossRuneHudRefreshTickMs != 0 && now < _nextBossRuneHudRefreshTickMs)
                    return;

                bossRune = _pendingBossRuneHudValue.Value;
                _nextBossRuneHudRefreshTickMs = now + BossRuneHudRefreshIntervalMs;
            }

            var user = dc.Main.Class.ME?.user ?? ModEntry.me?._level?.game?.user;
            if (user != null)
            {
                try { user.br_setActivated(bossRune); }
                catch
                {
                    try { user.bossRuneActivated = bossRune; } catch { }
                }

                try
                {
                    var gameData = user.game?.data;
                    if (gameData?.cgData != null)
                        gameData.cgData.numBossCells = bossRune;
                }
                catch
                {
                }
            }

            TryRefreshBossRuneHud(bossRune);
        }

        private static void ClearBossRuneHudRefresh()
        {
            lock (_bossRuneHudRefreshLock)
            {
                _pendingBossRuneHudValue = null;
                _bossRuneHudRefreshUntilTickMs = 0;
                _nextBossRuneHudRefreshTickMs = 0;
            }
        }

        private static void TryRefreshBossRuneHud(int bossRune)
        {
            object? hud = null;
            try { hud = dc.pr.Game.Class.ME?.hud; } catch { }
            if (hud == null)
                return;

            // Re-showing is harmless and makes the vanilla HUD re-evaluate visibility after the
            // boss-cell selector closes. The reflection pass below only touches members whose names
            // explicitly identify the boss-rune/cell display, avoiding a full HUD teardown/rebuild.
            try { dc.pr.Game.Class.ME?.hud?.show(null); } catch { }

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            RefreshBossRuneHudObject(hud, bossRune, depth: 0, visited);
        }

        private static void RefreshBossRuneHudObject(
            object target,
            int bossRune,
            int depth,
            HashSet<object> visited)
        {
            if (target == null || depth > 2 || !visited.Add(target))
                return;

            var type = target.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var methodName in BossRuneHudRefreshMethodNames)
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(flags)
                        .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    try
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length == 0)
                        {
                            method.Invoke(target, null);
                            break;
                        }
                        if (parameters.Length == 1 && TryConvertBossRuneValue(bossRune, parameters[0].ParameterType, out var value))
                        {
                            method.Invoke(target, new[] { value });
                            break;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                foreach (var field in type.GetFields(flags))
                {
                    if (!IsBossRuneHudMemberName(field.Name))
                        continue;

                    object? value = null;
                    try { value = field.GetValue(target); } catch { }
                    if (TryConvertBossRuneValue(bossRune, field.FieldType, out var converted))
                    {
                        try { field.SetValue(target, converted); } catch { }
                        continue;
                    }

                    if (value != null && !IsSimpleBossRuneValueType(value.GetType()))
                        RefreshBossRuneHudObject(value, bossRune, depth + 1, visited);
                }
            }
            catch
            {
            }

            try
            {
                foreach (var property in type.GetProperties(flags))
                {
                    if (!IsBossRuneHudMemberName(property.Name) || property.GetIndexParameters().Length != 0)
                        continue;

                    if (property.CanWrite && TryConvertBossRuneValue(bossRune, property.PropertyType, out var converted))
                    {
                        try { property.SetValue(target, converted); } catch { }
                        continue;
                    }

                    if (!property.CanRead)
                        continue;

                    object? value = null;
                    try { value = property.GetValue(target); } catch { }
                    if (value != null && !IsSimpleBossRuneValueType(value.GetType()))
                        RefreshBossRuneHudObject(value, bossRune, depth + 1, visited);
                }
            }
            catch
            {
            }
        }

        private static readonly string[] BossRuneHudRefreshMethodNames =
        {
            "updateBossRune",
            "updateBossRunes",
            "updateBossCells",
            "refreshBossRune",
            "refreshBossRunes",
            "refreshBossCells",
            "updateBossCellDisplay",
            "refreshBossCellDisplay",
            "setBossRune",
            "setBossRunes",
            "setBossCells",
            "setNumBossCells",
            "setBossRuneActivated"
        };

        private static bool IsBossRuneHudMemberName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            return normalized.Contains("boss", StringComparison.Ordinal) &&
                   (normalized.Contains("rune", StringComparison.Ordinal) ||
                    normalized.Contains("cell", StringComparison.Ordinal));
        }

        private static bool IsSimpleBossRuneValueType(System.Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(decimal);
        }

        private static bool TryConvertBossRuneValue(int bossRune, System.Type targetType, out object? value)
        {
            value = null;
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            try
            {
                if (underlying == typeof(int)) value = bossRune;
                else if (underlying == typeof(long)) value = (long)bossRune;
                else if (underlying == typeof(short)) value = (short)bossRune;
                else if (underlying == typeof(byte)) value = (byte)System.Math.Clamp(bossRune, byte.MinValue, byte.MaxValue);
                else if (underlying == typeof(uint)) value = (uint)System.Math.Max(0, bossRune);
                else if (underlying == typeof(ulong)) value = (ulong)System.Math.Max(0, bossRune);
                else if (underlying == typeof(ushort)) value = (ushort)System.Math.Clamp(bossRune, ushort.MinValue, ushort.MaxValue);
                else return false;
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static int GetEffectiveBossRune(User user)
        {
            if (user == null)
                return 0;

            var fallback = ToInt(user.bossRuneActivated);

            // During active runs the currently selected BC is mirrored in cgData.numBossCells (>=0).
            // Prefer that when available so decreases are propagated too.
            try
            {
                var gameData = user.game?.data;
                if (gameData?.cgData != null)
                {
                    var cgBossRune = ToInt(gameData.cgData.numBossCells);
                    if (cgBossRune >= 0)
                        return cgBossRune;
                }
            }
            catch
            {
            }

            try
            {
                var activated = user.br_numActivated();
                // br_numActivated() can return 0 in scoring contexts when cgData is unavailable.
                if (activated == 0 && fallback > 0)
                    return fallback;
                return activated;
            }
            catch
            {
                return fallback;
            }
        }

        private static void RestoreLocalSerializerSyncIfCaptured()
        {
            if (!_localSerializerCaptured)
                return;

            try
            {
                var serializerClass = dc.hxbit.Serializer.Class;
                if (serializerClass == null)
                    return;

                serializerClass.SEQ = _localSerializerSeq;
                serializerClass.UID = _localSerializerUid;
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
    }
}

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
using System.Linq;
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
    internal partial class GameDataSync : IEventReceiver, IOnAdvancedModuleInitializing
    {
        static Serilog.ILogger? _log;
        static public int Seed;
        private static readonly bool EnableStoryManagerSync = false;

        // When false, host does not send PROGRESS (packed user) to clients.
        private static readonly bool EnableSendHostUserProgress = false;

        static public virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ _isTwitch = default!;
        static public bool _isCustom;
        static public bool _mode;

        static public LaunchMode _launch = default!;
        private static readonly object _bossRuneLock = new();
        private static int? _remoteBossRune;
        private static int? _hostBossRune;
        private static readonly object _bossRuneHudRefreshLock = new();
        private static int? _pendingBossRuneHudValue;
        private static long _bossRuneHudRefreshUntilTickMs;
        private static long _nextBossRuneHudRefreshTickMs;
        private const long BossRuneHudRefreshDurationMs = 3000;
        private const long BossRuneHudRefreshIntervalMs = 150;
        private static string? _remoteProgressPayload;
        public static string? HostProgressPayload;
        private static bool _origProgressCaptured;
        private static string? _origProgressPayload;
        private static bool _origHeroCosmeticsCaptured;
        private static string? _origHeroSkin;
        private static string? _origHeroHeadSkin;
        private static User? _cachedBuiltProgressPayloadUser;
        private static string? _cachedBuiltProgressPayload;
        private static NetNode? _lastProgressSyncNet;
        private static string? _lastProgressSyncPayload;
        private static NetNode? _lastHeroSkinSyncNet;
        private static string? _lastHeroSkinSyncPayload;
        private static NetNode? _lastHeroHeadSkinSyncNet;
        private static string? _lastHeroHeadSkinSyncPayload;

        private static string? _remoteCountersPayload;

        private static double? _remoteMobsHpMult;
        private static double? _remoteBossesHpMult;
        private static bool _origHpMultipliersSaved;
        private static double _origMobsHpMult;
        private static double _origBossesHpMult;
        public static string? HostCountersPayload;
        private static string? _remoteBlueprintsPayload;
        public static string? HostBlueprintsPayload;
        private static bool _hasRemoteCounters;
        private static bool _hasRemoteBlueprints;
        private static bool _origStoryCaptured;
        private static bool _origStoryWasNull;
        private static StoryManager? _origStory;
        private static StringMap? _origCounters;
        private static readonly Dictionary<string, int> _origCountersSnapshot = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, int> _origNpcProgressSnapshot = new();
        private static readonly Dictionary<string, int> _origLoreRoomRunIdsSnapshot = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _origVisitedLoreRoomsSnapshot = new(StringComparer.Ordinal);
        private static readonly List<int> _origPlannedLoresSnapshot = new();
        private static int _origStoryDataVersion;
        private static bool _sessionStoryCaptured;
        private static bool _sessionStoryWasNull;
        private static readonly Dictionary<string, int> _sessionCountersSnapshot = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, int> _sessionNpcProgressSnapshot = new();
        private static readonly Dictionary<string, int> _sessionLoreRoomRunIdsSnapshot = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _sessionVisitedLoreRoomsSnapshot = new(StringComparer.Ordinal);
        private static readonly List<int> _sessionPlannedLoresSnapshot = new();
        private static int _sessionStoryDataVersion;
        private static bool _origItemMetaCaptured;
        private static ItemMetaManager? _origItemMeta;
        private static ArrayObj? _origItemProgress;
        private static ArrayObj? _origPermanentItems;
        private static bool _origItemMetaWasNull;
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
        private static readonly Dictionary<string, int> _remoteCountersSnapshot = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, int> _remoteNpcProgressSnapshot = new();
        private static readonly Dictionary<string, int> _remoteLoreRoomRunIdsSnapshot = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _remoteVisitedLoreRoomsSnapshot = new(StringComparer.Ordinal);
        private static readonly HashSet<int> _remotePlannedLoresSnapshot = new();
        private static int _remoteStoryDataVersion;
        private static bool _hasRemoteStoryDataVersion;

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
            isCustom = false;
            mode = false;
            Seed = lvl;
            ModEntry.me = null!;
            ModEntry.ResetClientSlots();
            ModEntry.kingInitialized = false;
            ModEntry._ghost = null!;
            var net = GameMenu.NetRef;
            var launchKind = GetLaunchKind(gdata);
            var nativeBossRushLaunch = IsBossRushLaunchKind(launchKind);
            var expectedBossRushLaunch = nativeBossRushLaunch ||
                                         (net?.IsHost == true && GameMenu.HasPrecommittedHostBossRushLaunch()) ||
                                         (net != null && !net.IsHost && GameMenu.HasPendingRemoteBossRushLaunch());
            if (expectedBossRushLaunch && !nativeBossRushLaunch)
            {
                // Some generated bindings expose the Boss Rush launch variant with a runtime name
                // that does not contain the expected token. The synchronized door precommit is a
                // stronger signal than the local type name, so normalize the wire identity here.
                launchKind = "dc.LaunchMode+BossRush";
            }
            var shouldSynchronizeSeed = gdata is LaunchMode.NewGame || expectedBossRushLaunch;
            if (net == null || !net.IsAlive)
                RestoreOriginalUserState(self, true);

            if (net != null && net.IsHost)
            {
                var reusedPrecommittedSeed = false;
                var seedSequence = 0;
                if (shouldSynchronizeSeed &&
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
                if (shouldSynchronizeSeed &&
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
            orig(self, lvl, isTwitch, isCustom, mode, gdata);

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

        public static void MarkProgressPayloadDirty() { }

        private static string? GetCurrentProgressPayload(User user)
        {
            if (user == null)
                return null;

            if (!string.IsNullOrWhiteSpace(_cachedBuiltProgressPayload) &&
                ReferenceEquals(_cachedBuiltProgressPayloadUser, user))
            {
                return _cachedBuiltProgressPayload;
            }

            var payload = BuildProgressPayload(user);
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            _cachedBuiltProgressPayloadUser = user;
            _cachedBuiltProgressPayload = payload;
            return payload;
        }

        public static void ReceiveBlueprints(string payload, User? target = null)
        {
            _remoteBlueprintsPayload = null;
            _hasRemoteBlueprints = false;
        }

        public static void SendBlueprints(User user, NetNode? net)
        {
            HostBlueprintsPayload = null;
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
                _remoteProgressPayload = null;
                HostProgressPayload = null;
                _remoteCountersPayload = null;
                _remoteBlueprintsPayload = null;
                _hasRemoteCounters = false;
                _hasRemoteBlueprints = false;
                _hasRemoteBossRune = false;
                _remoteCountersSnapshot.Clear();
                _remoteNpcProgressSnapshot.Clear();
                _remoteLoreRoomRunIdsSnapshot.Clear();
                _remoteVisitedLoreRoomsSnapshot.Clear();
                _remotePlannedLoresSnapshot.Clear();
                _remoteStoryDataVersion = 0;
                _hasRemoteStoryDataVersion = false;
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
                _origProgressCaptured = false;
                _origProgressPayload = null;
                _origHeroCosmeticsCaptured = false;
                _origHeroSkin = null;
                _origHeroHeadSkin = null;
                _origStoryCaptured = false;
                _origStoryWasNull = false;
                _origStory = null;
                _origCounters = null;
                _origCountersSnapshot.Clear();
                _origNpcProgressSnapshot.Clear();
                _origLoreRoomRunIdsSnapshot.Clear();
                _origVisitedLoreRoomsSnapshot.Clear();
                _origPlannedLoresSnapshot.Clear();
                _origStoryDataVersion = 0;
                ClearSessionStory();
                _origItemMetaCaptured = false;
                _origItemMeta = null;
                _origItemProgress = null;
                _origPermanentItems = null;
                _origItemMetaWasNull = false;
                _origBossRuneCaptured = false;
                _origBossRune = 0;
                _lastProgressSyncNet = null;
                _lastProgressSyncPayload = null;
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

        public static void CaptureSessionStory(User user)
        {
            if (!EnableStoryManagerSync || user == null)
                return;

            _sessionStoryCaptured = true;
            CaptureStorySnapshot(
                user,
                _sessionCountersSnapshot,
                _sessionNpcProgressSnapshot,
                _sessionLoreRoomRunIdsSnapshot,
                _sessionVisitedLoreRoomsSnapshot,
                _sessionPlannedLoresSnapshot,
                out _sessionStoryWasNull,
                out _sessionStoryDataVersion);
        }

        public static void RestoreSessionStory(User user)
        {
            if (!EnableStoryManagerSync || !_sessionStoryCaptured || user == null)
                return;

            if (_sessionStoryWasNull && _sessionCountersSnapshot.Count == 0 && _sessionNpcProgressSnapshot.Count == 0 && _sessionStoryDataVersion == 0)
            {
                ClearUserStoryState(user);
                ClearSessionStory();
                return;
            }

            ApplyStoryState(
                user,
                _sessionCountersSnapshot,
                _sessionNpcProgressSnapshot,
                _sessionStoryDataVersion,
                _sessionLoreRoomRunIdsSnapshot,
                _sessionVisitedLoreRoomsSnapshot,
                _sessionPlannedLoresSnapshot);
            ClearSessionStory();
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

        public static void ReceiveCounters(string payload, User? target = null)
        {
            _remoteCountersPayload = null;
            _hasRemoteCounters = false;
            _remoteCountersSnapshot.Clear();
            _remoteNpcProgressSnapshot.Clear();
            _remoteLoreRoomRunIdsSnapshot.Clear();
            _remoteVisitedLoreRoomsSnapshot.Clear();
            _remotePlannedLoresSnapshot.Clear();
            _remoteStoryDataVersion = 0;
            _hasRemoteStoryDataVersion = false;
        }

        private static void SendCounters(User user, NetNode? net)
        {
            HostCountersPayload = null;
        }

        public static void SendHostStorySync(User user, NetNode? net)
        {
            HostCountersPayload = null;
        }

        public static void SendProgressSync(User user, NetNode? net)
        {
            HostProgressPayload = null;
        }

        public static void ReceiveProgressSync(string payload, User? target = null)
        {
            _remoteProgressPayload = null;
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

        private static void ForEachEscapedToken(string payload, Action<string> onToken)
        {
            if (string.IsNullOrEmpty(payload))
                return;

            var token = new StringBuilder();
            var escaped = false;
            for (var i = 0; i < payload.Length; i++)
            {
                var c = payload[i];
                if (escaped)
                {
                    token.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '|')
                {
                    onToken(token.ToString());
                    token.Clear();
                    continue;
                }

                token.Append(c);
            }

            if (escaped)
                token.Append('\\');
            onToken(token.ToString());
        }

        private static string EncodeToken(string value)
        {
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        }

        private static string? DecodeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return null;
            }
        }

        private static int ParseInt(string value, int fallback)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (value == "1")
                return true;
            if (value == "0")
                return false;
            if (bool.TryParse(value, out var parsed))
                return parsed;
            return fallback;
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

        private static void ClearSessionStory()
        {
            _sessionStoryCaptured = false;
            _sessionStoryWasNull = false;
            _sessionCountersSnapshot.Clear();
            _sessionNpcProgressSnapshot.Clear();
            _sessionLoreRoomRunIdsSnapshot.Clear();
            _sessionVisitedLoreRoomsSnapshot.Clear();
            _sessionPlannedLoresSnapshot.Clear();
            _sessionStoryDataVersion = 0;
        }

        private static void RestoreOriginalStory(User user, bool preserveLocalProgress)
        {
            var currentStory = user.story;
            Dictionary<string, int> countersToApply;
            Dictionary<int, int> npcProgressToApply;
            Dictionary<string, int> loreRoomRunIdsToApply;
            HashSet<string> visitedLoreRoomsToApply;
            List<int> plannedLoresToApply;
            int storyDataVersionToApply;
            if (preserveLocalProgress)
            {
                countersToApply = MergeCountersWithLocalProgress(currentStory);
                npcProgressToApply = MergeNpcProgressWithLocalProgress(currentStory);
                loreRoomRunIdsToApply = MergeLoreRoomRunIdsWithLocalProgress(currentStory);
                visitedLoreRoomsToApply = MergeVisitedLoreRoomsWithLocalProgress(currentStory);
                plannedLoresToApply = MergePlannedLoresWithLocalProgress(currentStory);
                storyDataVersionToApply = MergeStoryDataVersion(currentStory);
            }
            else
            {
                countersToApply = new Dictionary<string, int>(_origCountersSnapshot, StringComparer.Ordinal);
                npcProgressToApply = new Dictionary<int, int>(_origNpcProgressSnapshot);
                loreRoomRunIdsToApply = new Dictionary<string, int>(_origLoreRoomRunIdsSnapshot, StringComparer.Ordinal);
                visitedLoreRoomsToApply = new HashSet<string>(_origVisitedLoreRoomsSnapshot, StringComparer.Ordinal);
                plannedLoresToApply = new List<int>(_origPlannedLoresSnapshot);
                storyDataVersionToApply = _origStoryDataVersion;
            }

            if (_origStoryWasNull &&
                countersToApply.Count == 0 &&
                npcProgressToApply.Count == 0 &&
                loreRoomRunIdsToApply.Count == 0 &&
                visitedLoreRoomsToApply.Count == 0 &&
                plannedLoresToApply.Count == 0 &&
                storyDataVersionToApply == 0)
            {
                ClearUserStoryState(user);
                return;
            }

            ApplyStoryState(
                user,
                countersToApply,
                npcProgressToApply,
                storyDataVersionToApply,
                loreRoomRunIdsToApply,
                visitedLoreRoomsToApply,
                plannedLoresToApply);
        }

        private static Dictionary<string, int> MergeCountersWithLocalProgress(StoryManager? currentStory)
        {
            var merged = new Dictionary<string, int>(_origCountersSnapshot, StringComparer.Ordinal);
            var currentCounters = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyCountersToDictionary(currentStory?.counters, currentCounters);
            foreach (var kv in currentCounters)
            {
                if (!_remoteCountersSnapshot.TryGetValue(kv.Key, out var remoteValue) || remoteValue != kv.Value)
                    merged[kv.Key] = kv.Value;
            }

            return merged;
        }

        private static Dictionary<int, int> MergeNpcProgressWithLocalProgress(StoryManager? currentStory)
        {
            var merged = new Dictionary<int, int>(_origNpcProgressSnapshot);
            var currentNpcProgress = new Dictionary<int, int>();
            CopyNpcProgressToDictionary(currentStory?.npcProgresses, currentNpcProgress);
            foreach (var kv in currentNpcProgress)
            {
                if (!_remoteNpcProgressSnapshot.TryGetValue(kv.Key, out var remoteValue) || remoteValue != kv.Value)
                    merged[kv.Key] = kv.Value;
            }

            return merged;
        }

        private static int MergeStoryDataVersion(StoryManager? currentStory)
        {
            var merged = _origStoryDataVersion;
            var current = currentStory?.storyDataVersion ?? merged;
            if (!_hasRemoteStoryDataVersion || current != _remoteStoryDataVersion)
                merged = current;
            return merged;
        }

        private static Dictionary<string, int> MergeLoreRoomRunIdsWithLocalProgress(StoryManager? currentStory)
        {
            var merged = new Dictionary<string, int>(_origLoreRoomRunIdsSnapshot, StringComparer.Ordinal);
            var currentLore = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyStoryStringIntMapToDictionary(currentStory != null ? ((dynamic)currentStory).loreRoomRunIds : null, currentLore);
            foreach (var kv in currentLore)
            {
                if (!_remoteLoreRoomRunIdsSnapshot.TryGetValue(kv.Key, out var remoteValue) || remoteValue != kv.Value)
                    merged[kv.Key] = kv.Value;
            }

            return merged;
        }

        private static HashSet<string> MergeVisitedLoreRoomsWithLocalProgress(StoryManager? currentStory)
        {
            var merged = new HashSet<string>(_origVisitedLoreRoomsSnapshot, StringComparer.Ordinal);
            var currentVisited = new HashSet<string>(StringComparer.Ordinal);
            CopyStoryVisitedLoreRoomsToSet(currentStory != null ? ((dynamic)currentStory).visitedLoreRooms : null, currentVisited);
            foreach (var key in currentVisited)
            {
                if (!_remoteVisitedLoreRoomsSnapshot.Contains(key))
                    merged.Add(key);
            }

            return merged;
        }

        private static List<int> MergePlannedLoresWithLocalProgress(StoryManager? currentStory)
        {
            var mergedSet = new HashSet<int>(_origPlannedLoresSnapshot);
            var currentPlanned = new List<int>();
            CopyStoryPlannedLoresToList(currentStory?.plannedLores, currentPlanned);
            for (var i = 0; i < currentPlanned.Count; i++)
            {
                var planned = currentPlanned[i];
                if (!_remotePlannedLoresSnapshot.Contains(planned))
                    mergedSet.Add(planned);
            }

            var merged = new List<int>(mergedSet);
            merged.Sort();
            return merged;
        }

        private static ArrayObj MergeItemProgressWithLocalProgress(ArrayObj? currentItemProgress)
        {
            var merged = new Dictionary<string, ItemProgress>(StringComparer.Ordinal);
            var orig = _origItemProgress;
            if (orig != null)
            {
                for (int i = 0; i < orig.length; i++)
                {
                    var p = orig.getDyn(i) as ItemProgress;
                    if (p != null)
                    {
                        var id = p.itemId?.ToString();
                        if (!string.IsNullOrWhiteSpace(id))
                            merged[id] = p;
                    }
                }
            }
            if (currentItemProgress != null)
            {
                for (int i = 0; i < currentItemProgress.length; i++)
                {
                    var curr = currentItemProgress.getDyn(i) as ItemProgress;
                    if (curr == null)
                        continue;
                    var id = curr.itemId?.ToString();
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    if (!merged.TryGetValue(id, out var origP) || origP == null)
                    {
                        merged[id] = curr;
                        continue;
                    }
                    var currUnlocked = curr.unlocked;
                    var currInvested = ToInt(curr.investedCells);
                    var currIsNew = curr.isNew;
                    var origUnlocked = origP.unlocked;
                    var origInvested = ToInt(origP.investedCells);
                    var origIsNew = origP.isNew;
                    if (currUnlocked && !origUnlocked || currInvested > origInvested || currIsNew && !origIsNew)
                        merged[id] = curr;
                }
            }
            var arr = ArrayUtils.CreateDyn();
            foreach (var p in merged.Values)
                arr.array.pushDyn(p);
            return (ArrayObj)arr.array;
        }

        private static ArrayObj MergePermanentItemsWithLocalProgress(ArrayObj? currentPermanentItems)
        {
            var merged = new HashSet<string>(StringComparer.Ordinal);
            var orig = _origPermanentItems;
            if (orig != null)
            {
                for (int i = 0; i < orig.length; i++)
                {
                    var id = orig.getDyn(i)?.ToString();
                    if (!string.IsNullOrWhiteSpace(id))
                        merged.Add(id);
                }
            }
            if (currentPermanentItems != null)
            {
                for (int i = 0; i < currentPermanentItems.length; i++)
                {
                    var id = currentPermanentItems.getDyn(i)?.ToString();
                    if (!string.IsNullOrWhiteSpace(id))
                        merged.Add(id);
                }
            }
            var arr = ArrayUtils.CreateDyn();
            foreach (var id in merged)
                arr.array.pushDyn(id.AsHaxeString());
            return (ArrayObj)arr.array;
        }

        private static bool TryParseCountersPayloadV4(
            string payload,
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores,
            out int? storyDataVersion)
        {
            int? parsedStoryDataVersion = null;
            var isV4 = payload.Equals("V4", StringComparison.Ordinal) ||
                       payload.StartsWith("V4|", StringComparison.Ordinal);
            if (!isV4)
            {
                storyDataVersion = null;
                return false;
            }

            var plannedSet = new HashSet<int>();
            ForEachEscapedToken(payload, token =>
            {
                if (string.IsNullOrWhiteSpace(token) || token.Equals("V4", StringComparison.Ordinal))
                    return;

                if (token.StartsWith("C:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    var key = DecodeToken(parts[1]);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    counters[key] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("N:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var npcIndex))
                        return;

                    npcProgress[npcIndex] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("L:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    var key = DecodeToken(parts[1]);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    loreRoomRunIds[key] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("V:", StringComparison.Ordinal))
                {
                    var key = DecodeToken(token[2..]);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    visitedLoreRooms.Add(key);
                    return;
                }

                if (token.StartsWith("P:", StringComparison.Ordinal))
                {
                    if (int.TryParse(token[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var planned))
                    {
                        if (plannedSet.Add(planned))
                            plannedLores.Add(planned);
                    }
                    return;
                }

                if (token.StartsWith("S:", StringComparison.Ordinal))
                {
                    if (int.TryParse(token[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        parsedStoryDataVersion = parsed;
                    return;
                }
            });

            storyDataVersion = parsedStoryDataVersion;
            return true;
        }

        private static bool TryParseCountersPayloadV3(
            string payload,
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            out int? storyDataVersion)
        {
            int? parsedStoryDataVersion = null;
            var isV3 = payload.Equals("V3", StringComparison.Ordinal) ||
                       payload.StartsWith("V3|", StringComparison.Ordinal);
            if (!isV3)
            {
                storyDataVersion = null;
                return false;
            }

            ForEachEscapedToken(payload, token =>
            {
                if (string.IsNullOrWhiteSpace(token) || token.Equals("V3", StringComparison.Ordinal))
                    return;

                if (token.StartsWith("C:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    var key = DecodeToken(parts[1]);
                    if (string.IsNullOrWhiteSpace(key))
                        return;

                    counters[key] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("N:", StringComparison.Ordinal))
                {
                    var parts = token.Split(':', 3);
                    if (parts.Length < 3)
                        return;

                    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var npcIndex))
                        return;

                    npcProgress[npcIndex] = ParseInt(parts[2], 0);
                    return;
                }

                if (token.StartsWith("S:", StringComparison.Ordinal))
                {
                    if (int.TryParse(token[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        parsedStoryDataVersion = parsed;
                    return;
                }
            });

            storyDataVersion = parsedStoryDataVersion;
            return true;
        }

        private static void ParseLegacyCountersPayload(string payload, Dictionary<string, int> counters)
        {
            var key = new StringBuilder();
            var value = new StringBuilder();
            var inKey = true;
            var escaped = false;

            void commitPair()
            {
                if (key.Length <= 0)
                    return;

                var keyText = key.ToString();
                var valueText = value.ToString();
                counters[keyText] = ParseInt(valueText, 0);
            }

            for (var i = 0; i < payload.Length; i++)
            {
                var c = payload[i];
                if (escaped)
                {
                    if (inKey)
                        key.Append(c);
                    else
                        value.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (inKey && c == '=')
                {
                    inKey = false;
                    continue;
                }

                if (!inKey && c == '|')
                {
                    commitPair();
                    key.Clear();
                    value.Clear();
                    inKey = true;
                    continue;
                }

                if (inKey)
                    key.Append(c);
                else
                    value.Append(c);
            }

            commitPair();
        }

        private static void CaptureStorySnapshot(
            User user,
            Dictionary<string, int> countersTarget,
            Dictionary<int, int> npcProgressTarget,
            Dictionary<string, int> loreRoomRunIdsTarget,
            HashSet<string> visitedLoreRoomsTarget,
            List<int> plannedLoresTarget,
            out bool storyWasNull,
            out int storyDataVersion)
        {
            countersTarget.Clear();
            npcProgressTarget.Clear();
            loreRoomRunIdsTarget.Clear();
            visitedLoreRoomsTarget.Clear();
            plannedLoresTarget.Clear();

            var story = user.story;
            storyWasNull = story == null;
            if (story != null)
            {
                dynamic dynStory = story;
                CopyCountersToDictionary(story.counters, countersTarget);
                CopyNpcProgressToDictionary(story.npcProgresses, npcProgressTarget);
                CopyStoryStringIntMapToDictionary(dynStory.loreRoomRunIds, loreRoomRunIdsTarget);
                CopyStoryVisitedLoreRoomsToSet(dynStory.visitedLoreRooms, visitedLoreRoomsTarget);
                CopyStoryPlannedLoresToList(story.plannedLores, plannedLoresTarget);
                storyDataVersion = story.storyDataVersion;
                return;
            }

            storyDataVersion = 0;
            CopyCountersToDictionary(user.counters, countersTarget);
            CopyNpcProgressToDictionary(user.npcs, npcProgressTarget);
            if (countersTarget.Count > 0 || npcProgressTarget.Count > 0)
                storyWasNull = false;
        }

        private static bool HasAnyIncomingStoryData(
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores,
            int? storyDataVersion)
        {
            return counters.Count > 0 ||
                   npcProgress.Count > 0 ||
                   loreRoomRunIds.Count > 0 ||
                   visitedLoreRooms.Count > 0 ||
                   plannedLores.Count > 0 ||
                   (storyDataVersion ?? 0) != 0;
        }

        private static bool HasAnyStorySnapshotData(
            Dictionary<string, int> counters,
            Dictionary<int, int> npcProgress,
            Dictionary<string, int> loreRoomRunIds,
            HashSet<string> visitedLoreRooms,
            List<int> plannedLores,
            int storyDataVersion)
        {
            return counters.Count > 0 ||
                   npcProgress.Count > 0 ||
                   loreRoomRunIds.Count > 0 ||
                   visitedLoreRooms.Count > 0 ||
                   plannedLores.Count > 0 ||
                   storyDataVersion != 0;
        }

        private static bool HasAnyUserStoryData(User user)
        {
            if (HasAnyStoryData(user.story))
                return true;

            var legacyCounters = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyCountersToDictionary(user.counters, legacyCounters);
            if (legacyCounters.Count > 0)
                return true;

            var legacyNpcProgress = new Dictionary<int, int>();
            CopyNpcProgressToDictionary(user.npcs, legacyNpcProgress);
            return legacyNpcProgress.Count > 0;
        }

        private static bool HasAnyStoryData(StoryManager? story)
        {
            if (story == null)
                return false;

            dynamic dynStory = story;

            if (dynStory.storyDataVersion != 0)
                return true;

            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyCountersToDictionary(dynStory.counters, counters);
            if (counters.Count > 0)
                return true;

            var npcProgress = new Dictionary<int, int>();
            CopyNpcProgressToDictionary(dynStory.npcProgresses, npcProgress);
            if (npcProgress.Count > 0)
                return true;

            var loreRoomRunIds = new Dictionary<string, int>(StringComparer.Ordinal);
            CopyStoryStringIntMapToDictionary(dynStory.loreRoomRunIds, loreRoomRunIds);
            if (loreRoomRunIds.Count > 0)
                return true;

            var visitedLoreRooms = new HashSet<string>(StringComparer.Ordinal);
            CopyStoryVisitedLoreRoomsToSet(dynStory.visitedLoreRooms, visitedLoreRooms);
            if (visitedLoreRooms.Count > 0)
                return true;

            var plannedLores = new List<int>();
            CopyStoryPlannedLoresToList(dynStory.plannedLores, plannedLores);
            return plannedLores.Count > 0;
        }

        private static void CopyCountersToDictionary(StringMap? map, Dictionary<string, int> target)
        {
            target.Clear();
            if (map == null)
                return;

            try
            {
                var keys = map.keys();
                while (keys.hasNext.Invoke())
                {
                    var key = keys.next.Invoke();
                    if (key == null)
                        continue;

                    var keyText = key.ToString();
                    if (string.IsNullOrWhiteSpace(keyText))
                        continue;

                    target[keyText] = ToInt(map.get(key));
                }
            }
            catch
            {
            }
        }

        private static void CopyNpcProgressToDictionary(EnumValueMap? map, Dictionary<int, int> target)
        {
            target.Clear();
            if (map == null)
                return;

            try
            {
                var keys = map.keys();
                while (keys.hasNext.Invoke())
                {
                    var key = keys.next.Invoke();
                    if (key is not NpcId npcId)
                        continue;

                    target[(int)npcId.Index] = ToInt(map.get(key));
                }
            }
            catch
            {
            }
        }

        private static void CopyStoryStringIntMapToDictionary(dynamic? map, Dictionary<string, int> target)
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

                    target[key] = ToInt(map.get.Invoke(keyObj));
                }
            }
            catch
            {
            }
        }
    }
}

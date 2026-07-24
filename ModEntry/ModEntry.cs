
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using dc.en;
using dc.en.inter;
using dc.pr;
using dc.level;
using dc.hl.types;
using dc;
using dc.libs.heaps.slib;
using Rand = dc.libs.Rand;
using dc.ui.hud;
using dc.h2d;
using Hashlink.Virtuals;
using dc.tool;
using dc.tool.mainSkills;
using HaxeProxy.Runtime;
using dc.cine;
using dc.cine.coll;
using dc.cine.dlcp;
using dc.cine.kf;
using dc.cine.queen;
using CineHookInitialize;

using DeadCellsMultiplayerMod.Ghost.GhostBase;
using ModCore.Events;
using DeadCellsMultiplayerMod.Mobs.MobsSynchronization;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.MultiplayerModUI.Minimap;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using DeadCellsMultiplayerMod.MultiplayerModUI.LevelExit;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using DeadCellsMultiplayerMod.Tools.ModLang;
using DeadCellsMultiplayerMod.Tools;
using DeadCellsMultiplayerMod.KingHead;
using DeadCellsMultiplayerMod.Mobs.Levelinit;
using dc.en.inter.door;
using DeadCellsMultiplayerMod.Interaction;
using DeadCellsMultiplayerMod.UI;
using DeadCellsMultiplayerMod.AdvancedCoop;


namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry(ModInfo info) : ModBase(info),
        IOnGameEndInit,
        IOnHeroInit,
        IOnHeroUpdate,
        IOnFrameUpdate,
        IOnAfterLoadingCDB,
        IOnAdvancedModuleInitializing
    {
        public static ModEntry? Instance { get; private set; }
        private bool _ready;
        private static bool s_hooksInstalled;
        private static IDisposable? s_steamOverlayJoinCallback;
        private static IDisposable? s_steamRichPresenceJoinCallback;
        private static bool s_steamOverlayCallbackPending;
        private static Timer? s_steamCallbackPumpTimer;
        private static int s_steamOverlayCallbackRetryCount;
        private static bool s_steamApiReady;
        private static bool s_steamUnavailable;

        /// <summary>
        /// False only once we have POSITIVE evidence Steam cannot work here (native load failure,
        /// callback registration throwing, or repeated init failures) — e.g. no Steam client
        /// running, or a non-Steam install of the game. Optimistic before that so a working Steam
        /// setup is never downgraded just because init has not completed yet. When false, the
        /// menu uses the direct IP/LAN transport instead of the Steam lobby path.
        /// </summary>
        internal static bool IsSteamAvailable => !s_steamUnavailable;

        internal static void MarkSteamUnavailable(string reason)
        {
            if (s_steamUnavailable)
                return;

            s_steamUnavailable = true;
            Instance?.Logger.Information(
                "[NetMod][Steam] Steam features disabled ({Reason}). Direct IP/LAN hosting stays fully available.",
                reason);
        }
        private static string s_lastSteamLaunchCommand = string.Empty;
        private static string s_lastSteamLaunchConnectLobbyParam = string.Empty;
        private static ulong s_lastOverlayJoinLobbyId;
        private static long s_lastOverlayJoinTicks;
        private static long s_nextSteamLaunchPollTicks;
        private const int SteamOverlayCallbackMaxRetries = 600;
        private const int SteamOverlayLaunchPollIntervalMs = 500;
        private const int SteamOverlayJoinDedupMs = 3000;
        private NetRole _netRole = NetRole.None;
        public static NetNode? _net;

        public dc.pr.Game? game;

        public static GhostKing[] clients = new GhostKing[NetNode.MaxClientSlots];
        public static Kinghead?[] clientHeads = new Kinghead?[NetNode.MaxClientSlots];
        public static string?[] clientLabels = new string?[NetNode.MaxClientSlots];
        public static int[] clientIds = new int[NetNode.MaxClientSlots];
        public static string?[] clientSkins = new string?[NetNode.MaxClientSlots];
        public static string?[] clientHeadSkins = new string?[NetNode.MaxClientSlots];
        private static bool[] pendingClientHeadRecreate = new bool[NetNode.MaxClientSlots];
        private static string?[] clientLastBodyAnims = new string?[NetNode.MaxClientSlots];
        private static int?[] clientLastBodyAnimQueues = new int?[NetNode.MaxClientSlots];
        private static bool?[] clientLastBodyAnimGs = new bool?[NetNode.MaxClientSlots];
        private static string?[] clientLastHeadAnims = new string?[NetNode.MaxClientSlots];
        private static int[] clientLastDirs = new int[NetNode.MaxClientSlots];
        private static bool[] clientLastDownedOffsets = new bool[NetNode.MaxClientSlots];
        private static bool[] clientHeadDirty = new bool[NetNode.MaxClientSlots];
        private static long[] clientNextHeadFxTick = new long[NetNode.MaxClientSlots];
        private static long[] clientNextHeadRecreateTick = new long[NetNode.MaxClientSlots];
        public static Hero me = null!;
        public static GhostHero _ghost = null!;
        private Hero? _ghostOwnerHero;
        private dc.pr.Game? _ghostOwnerGame;
        private NetNode? _ghostBootstrapNet;

        private Hero? _debugPerkAppliedHero;
        private string _debugPerkAppliedId = string.Empty;
        private long _nextDebugPerkApplyTick;
        private ItemMetaManager? _debugExplorerRuneInjectedMeta;
        private bool _debugExplorerRuneInjectedByDebug;
        private const string ExplorerRunePermanentItemId = "ExploKey";
        /// <summary>Last successful minimap reveal signature (level + branch); cleared on level/wakeup.</summary>
        private string _debugExplorerRevealAppliedSignature = string.Empty;
        private long _nextDebugExplorerRevealRetryTick;
        private int _debugExplorerRevealAllCount;
        private const int MaxDebugExplorerRevealAllCalls = 3;

        private string? _lastAnimSent;
        private int? _lastAnimQueueSent;
        private bool? _lastAnimGSent;
        private bool _visualSyncFailureLogged;
        private long _suppressHeroAnimUntilTicks;
        private string? _lastSentHeroSkin;
        private string? _lastSentHeroHeadSkin;

        public static MiniMap miniMap = null!;

        public static bool kingInitialized = false;

        public string levelId = string.Empty;

        public static int remotePlayerId = -1;

        public string remoteSkin = string.Empty;
        public string remoteHeadSkin = string.Empty;

        public static ArrayDyn customHeads = null!;

        public InventItem inventItem = null!;
        private bool _inventorySyncGuard;
        private bool _localFakeDead;
        private bool _localDeathConversionInProgress;
        private bool _localExitPenaltyApplied;
        private long _localFakeDeadStartedTicks;
        private DeadBase? _localDeadCine;
        private double _localDownedX;
        private double _localDownedY;
        private double _localHeldX;
        private double _localHeldY;
        private string _localDownedLevelId = string.Empty;
        private long _nextReviveAttemptTicks;
        private long _nextDownedStateSendTicks;
        private long _postReviveLockUntilTicks;
        private double _postReviveLockX;
        private double _postReviveLockY;
        private int _reviveHoldTargetId;
        private long _reviveHoldStartedTicks;
        private const double ReviveUseDistancePx = 48.0;
        private const double ReviveAttemptCooldownSeconds = 0.2;
        private const double ReviveHoldSeconds = 0.7;
        private const double ReviveHomunculusBodyMaxDistancePx = 64.0;
        private const double ReviveRemotePositionValidationPx = 96.0;
        private const double RemoteDownedStateStaleSeconds = 6.0;
        private const double DownedStateResendSeconds = 0.4;
        private const double DownedHeadStateResendSeconds = 1.0 / 30.0;
        private const double DownedGhostBodyYOffsetPx = 40.0;
        private const double LocalReviveBodyYOffsetPx = 0.5;
        private const double PostRevivePositionLockSeconds = 0.0;
        private const string ReviveHintText = "Hold to revive.";
        private string _lastDoorMarkerLevelId = string.Empty;
        private int _lastDoorMarkerToken = int.MinValue;
        private string _localLastDoorMarkerLevelId = string.Empty;
        private int _localLastDoorMarkerToken = int.MinValue;

        private sealed class RemoteDownedState
        {
            public int UserId;
            public double X;
            public double Y;
            public bool HasHeadPosition;
            public double HeadX;
            public double HeadY;
            public bool HasHeadAnim;
            public string HeadAnim = string.Empty;
            public string LevelId = string.Empty;
            public long UpdatedAtTicks;
        }

        private sealed class RemoteDoorMarkerState
        {
            public int MarkerToken;
            public string LevelId = string.Empty;
            public long UpdatedAtTicks;
        }

        private readonly Dictionary<int, RemoteDownedState> _remoteDowned = new();
        private readonly Dictionary<int, RemoteDownedCorpse> _remoteDownedCines = new();
        private readonly HashSet<int> _downedAnnouncements = new();
        private readonly Dictionary<int, RemoteDoorMarkerState> _remoteLastDoorMarkers = new();
        private readonly Dictionary<int, long> _pendingClientDisposeTicks = new();
        private const double ClientDisposeTransitionSeconds = 0.28;
        private const double GhostHeadDormantUpdateSeconds = 0.20;
        private const double GhostHeadRecreateRetrySeconds = 0.25;

        private static readonly HashSet<string> BossLevelIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "BeholderPit", "Throne", "DeathArena", "DookuArena", "QueenArena", "Observatory",
            "SwampHeart", "CastleAlchemy", "Lighthouse", "LighthouseTop", "LighthouseBottom", "Giant",
            "DookuCastle", "RichterCastle", "BossRushZone", "Bridge", "TopClockTower", "GardenerStage"
        };

        internal static bool IsBossLevel(string? levelId)
        {
            if (string.IsNullOrWhiteSpace(levelId))
                return false;
            if (BossLevelIds.Contains(levelId))
                return true;

            // Do not make every boss/DLC update depend on a manually-maintained biome list. When
            // the current level contains a real boss Mob or one of the known boss-room triggers,
            // opt it into the same cinematic/downed protections. All Hashlink reads are contained.
            try
            {
                var level = me?._level;
                var currentLevelId = level?.map?.id?.ToString();
                if (level == null || !string.Equals(currentLevelId, levelId, StringComparison.OrdinalIgnoreCase))
                    return false;

                var entities = level.entities;
                if (entities != null)
                {
                    for (var i = 0; i < entities.length; i++)
                    {
                        if (entities.getDyn(i) is dc.en.Mob mob &&
                            global::DeadCellsMultiplayerMod.Mobs.Bosses.BossSyncHelpers.IsBossMob(mob))
                        {
                            return true;
                        }
                    }
                }

                var triggers = level.entitiesByClass?.get(HiddenTrigger.Class.__clid) as ArrayObj;
                if (triggers != null)
                {
                    for (var i = 0; i < triggers.length; i++)
                    {
                        if (triggers.getDyn(i) is not HiddenTrigger trigger)
                            continue;
                        var eventId = trigger.genericEventId?.ToString();
                        if (!string.IsNullOrWhiteSpace(eventId) && BossRoomGenericEventIds.Contains(eventId))
                            return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>Known boss-room genericEventIds from game's HiddenTrigger (set via marker.customId in level data).</summary>
        private static readonly HashSet<string> BossRoomGenericEventIds = new(StringComparer.Ordinal)
        {
            "roomDeath", "roomBeholder", "roomBerserk", "roomCollectorBoss", "roomDooku",
            "roomGardenerBoss", "roomGiant", "roomKingsHand", "roomQueen", "roomBehemoth",
            "roomMamaTick", "roomKingsHandAsKing"
        };

        private static bool IsBossRoomEventIdCandidate(string? eventId)
        {
            if (string.IsNullOrWhiteSpace(eventId))
                return false;
            if (BossRoomGenericEventIds.Contains(eventId))
                return true;

            // This broader fallback is used only while an actual recognized boss intro is active,
            // or after receiving that exact event id from the peer. It does not turn arbitrary
            // reward/story triggers into boss cinematics.
            return eventId.StartsWith("room", StringComparison.OrdinalIgnoreCase);
        }
        private static readonly HashSet<string> BossDeathCineTypeNames = new(StringComparer.Ordinal)
        {
            "BeholderDeath", "GiantDeath", "GiantDeath4", "KillKingCinem", "KillQueenCinem",
            "QueenDefeated", "KillDookuBeastCinem", "RichterDeath",
            "EndCollectorPreSmash", "SmashCinem", "EndCollectorPostSmash", "EndCollectorPostSmashKS"
        };
        // Mid-fight transformations, NOT room intros. They get the sync quarantine and the
        // control-unlock watchdog like intros, but must NEVER be network-replayed with a hero
        // position snap: FakeKillDooku replay teleported the second player over the changing
        // DookuBeastArena with no ground clamp - instant fall-off deaths and arena-state split.
        private static readonly HashSet<string> BossPhaseTransitionCineTypeNames = new(StringComparer.Ordinal)
        {
            "FakeKillDooku"
        };

        private static readonly HashSet<string> BossIntroCineTypeNames = new(StringComparer.Ordinal)
        {
            "EnterRoomBoss", "EnterRoomDeathBoss", "EnterRoomGardenerBoss", "EnterRoomQueenBoss",
            "EnterThroneRoom", "EnterThroneBossRush", "EnterThroneRoomAsKing", "EnterGiantRoom",
            "EnterModifiedGiantRoom", "EnterDualBehemoth", "EnterDookuBossRoom", "StartCollectorFight", "StartCollectorFightAlt",
            "MeetCollectorEnd"
        };
        private string? _lastBossCineSentLevelId;
        private long _lastBossCineSentTick;
        private const double BossCineSendCooldownSeconds = 2.0;
        private readonly Dictionary<string, long> _pendingBossCineApplyByLevel = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _suppressBossCineEchoByLevel = new(StringComparer.OrdinalIgnoreCase);
        // Legacy boss presentation completion is level-scoped. Boss Rush explicitly clears this
        // marker when a fresh native room trigger starts the next encounter.
        private readonly HashSet<string> _completedBossCineLevels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _appliedBossHeroTeleportLevels = new(StringComparer.OrdinalIgnoreCase);
        private const double BossCineApplyPendingTtlSeconds = 20.0;
        private const double BossCineEchoSuppressSeconds = 12.0;
        private const double BossHeroTeleportYOffsetPx = 20.0;
        private const double BossHeroTeleportEchoSuppressSeconds = 1.5;
        private int _suppressBossCineSendDepth;
        private long _suppressBossTriggerNetSendUntilTick;


        void IOnAfterLoadingCDB.OnAfterLoadingCDB(dc._Data_ cdb)
        {
            customHeads = cdb.customHead.all;
        }


        internal static void SetRemoteSkin(string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            instance.remoteSkin = string.IsNullOrWhiteSpace(skin)
                ? "PrisonerDefault"
                : skin.Replace("|", "/").Trim();
        }

        internal static void SetRemoteHeadSkin(string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            instance.remoteHeadSkin = NormalizeSkin(skin, "BaseFlame");
        }

        public static string GetClientLabel(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= clientLabels.Length)
                return string.Empty;

            return clientLabels[slotIndex] ?? string.Empty;
        }


        internal static GhostKing? GetPrimaryClient()
        {
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client != null && clientIds[i] > 0)
                    return client;
            }

            return clients.Length > 0 ? clients[0] : null;
        }

        internal static void ResetClientSlots()
        {
            for (int i = 0; i < clients.Length; i++)
            {
                var head = clientHeads[i];
                if (head != null)
                {
                    head.dispose();
                    clientHeads[i] = null;
                }
                clients[i] = null!;
                clientLabels[i] = null;
                clientIds[i] = 0;
                clientSkins[i] = null;
                clientHeadSkins[i] = null;
                pendingClientHeadRecreate[i] = false;
                clientLastBodyAnims[i] = null;
                clientLastBodyAnimQueues[i] = null;
                clientLastBodyAnimGs[i] = null;
                clientLastHeadAnims[i] = null;
                clientLastDirs[i] = 0;
                clientLastDownedOffsets[i] = false;
                rLastX[i] = 0;
                rLastY[i] = 0;
                ResetGhostHeadRuntimeState(i);
            }
        }

        private static string BuildRemoteLabel(int remoteId, string? username)
        {
            var clean = string.IsNullOrWhiteSpace(username) ? "Guest" : username.Trim();
            return clean;
        }


        public void OnGameEndInit()
        {
            _ready = true;
            GameMenu.SetRole(NetRole.None);
            s_steamOverlayCallbackPending = true;
            s_steamOverlayCallbackRetryCount = 0;
            _debugPerkAppliedHero = null;
            _debugPerkAppliedId = string.Empty;
            _nextDebugPerkApplyTick = 0;
            _debugExplorerRuneInjectedMeta = null;
            _debugExplorerRuneInjectedByDebug = false;
            _debugExplorerRevealAppliedSignature = string.Empty;
            _nextDebugExplorerRevealRetryTick = 0;
            _debugExplorerRevealAllCount = 0;
            TryEnsureSteamApiInitialized("OnGameEndInit", logFailure: true);
            TryParseConnectLobbyFromCommandLine();
        }
        public override void Initialize()
        {
            Instance = this;

            InitializeOptionalModule(
                DebugModuleId.MultiplayerModLang,
                "MultiplayerModLang",
                () => _ = new MultiplayerModLang(this));
            InitializeOptionalModule(
                DebugModuleId.CineHooks,
                "CineHooks",
                () => _ = new CineHooks());
            InitializeOptionalModule(
                DebugModuleId.MultiplayerUI,
                "MultiplayerUI",
                () => _ = new MultiplayerUI(this, 0));

            _ = new SettingsUI(this);

            InitializeOptionalModule(
                DebugModuleId.LevelInit,
                "Levelinit",
                () => _ = new Levelinit(info));
            InitializeOptionalModule(
                DebugModuleId.MobsSynchronization,
                "MobsSynchronization",
                () => _ = new MobsSynchronization(this));
            InitializeOptionalModule(
                DebugModuleId.MinimapReveal,
                "Minimapreveal",
                () => _ = new Minimapreveal());
            InitializeOptionalModule(
                DebugModuleId.LevelExitSync,
                "LevelExitSync",
                () => _ = new LevelExitSync(this));
            InitializeOptionalModule(
                DebugModuleId.InteractionSync,
                "InteractionSync",
                () => _ = new InteractionSync(this));
            InitializeOptionalModule(
                DebugModuleId.ConnectionUI,
                "ConnectionUI",
                () => ConnectionUI.Initialize(this));

            _ = new CoopAdvancedHardening(this);

            GameMenu.Initialize(Logger);
            s_steamOverlayCallbackPending = true;
            s_steamOverlayCallbackRetryCount = 0;
            EventSystem.BroadcastEvent<IOnAdvancedModuleInitializing, ModEntry>(this);

            void InitializeOptionalModule(DebugModuleId moduleId, string moduleName, Action init)
            {
                var isEnabled = !MultiplayerSettingsStorage.IsDebugSectionEnabled ||
                                MultiplayerSettingsStorage.IsModuleEnabled(moduleId);
                if (isEnabled)
                {
                    init();
                    return;
                }

                Logger.Information("[NetMod][Debug] Module disabled by settings: {Module}", moduleName);
            }
        }

        void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
        {
            if (s_hooksInstalled)
                return;

            s_hooksInstalled = true;
            entry.Logger.Information("\x1b[32m[[ModEntry] Initializing ModEntry...]\x1b[0m ");
            entry.Logger.Information("[NetMod] Source build: {SourceBuild}", BuildInfo.SourceMarker);
            entry.Logger.Information(
                "[NetMod][Protocol] version={ProtocolVersion} build={BuildVersion}",
                BuildInfo.NetworkProtocolVersion,
                BuildInfo.Version);
            entry.Logger.Information("[NetMod][FlintGuard] mode=local-vanilla-no-remote-runtime-preflight-powered-feedback-scan");
            entry.Logger.Information("[NetMod][BossCells] mode=working-v0.8.68-selector-reload-and-render-guard");
            entry.Logger.Information("[NetMod][BossRushLoad] mode=host-door-precommit-structured-seed-barrier");
            entry.Logger.Information("[NetMod][CurseGuard] mode=fake-death-revive-dive-safe");
            entry.Logger.Information("[NetMod][VanillaTransitions] mode=working-v0.8.68-typed-activateSubLevel-stack");
            entry.Logger.Information("[NetMod][Spectate] mode=automatic-while-downed-local-on-revive");
            entry.Logger.Information("[NetMod][ReviveAnchor] mode=confirmed-history-always-recover-no-levelmap");
            entry.Logger.Information("[NetMod][ElevatorSync] mode=v0.8.38-legacy-onstep-pulse-no-position-correction");
            entry.Logger.Information("[NetMod][DoorTransitionRollback] mode=working-v0.8.68-giant-door-and-return-teleporter-stack");
            entry.Logger.Information("[NetMod][RemoteWeaponReplay] mode=vanilla-native-with-transition-callback-retirement");
            entry.Logger.Information("[NetMod][Corpse] mode=vanilla-fall-fast-grounded-failsafe");
            entry.Logger.Information("[NetMod][MobTeleportSync] mode=safe-above-floor-landing");
            entry.Logger.Information("[NetMod][MobGrounding] mode=upward-only-safe-landing-jump-phase");
            entry.Logger.Information("[NetMod][MobSyncStack] mode=exact-uploaded-v0.8.68-working-stack");
            entry.Logger.Information("[NetMod][SteamCallbacks] mode=main-thread-only-no-timer");
            Hook_Game.init += Hook_gameinit;
            Hook_Hero.wakeup += hook_hero_wakeup;
            Hook_Hero.onLevelChanged += hook_level_changed;
            Hook_Game.activateSubLevel += Hook_Game_activateSubLevel;
            Hook_Level.onActivation += Hook_Level_onActivation_SubLevelRenderGuard;
            Hook_User.newGame += GameDataSync.user_hook_new_game;
            Hook_User.unserialize += Hook_User_unserialize;
            Hook_Game.onDispose += Hook_Game_onDispose;
            Hook__Save.save += Hook__Save_save;
            Hook_AnimManager.play += Hook_AnimManager_play;
            Hook_MiniMap.track += Hook_MiniMap_track;
            Hook__LevelStruct.get += Hook__LevelStruct_get;
            Hook_LevelGen.generateGraph += Hook_LevelGen_generateGraph;
            Hook_Boot.update += hook_boot_update;
            Hook_Game.pause += Hook_Game_pause;
            Hook_Hero.kill += Hook_Hero_kill;
            Hook_Hero.onDie += Hook_Hero_onDie;
            Hook_Hero.onDamage += Hook_Hero_onDamage;
            Hook_Hero.checkCursedWeaponHit += Hook_Hero_checkCursedWeaponHit;
            Hook_Hero.startDeathCine += Hook_Hero_startDeathCine;
            Hook_Hero.onHeroDie += Hook_Hero_onHeroDie;
            Hook_Hero.canBeHitBy += Hook_Hero_canBeHitBy;
            Hook_Game.hasCinematic += Hook_Game_hasCinematic;
            Hook_ZDoor.onActivate += Hook_ZDoor_onActivate;
            Hook_BossRushDoor.initGfx += Hook_BossRushDoor_initGfx;
            Hook_Hero.applySkin += Hook_Hero_applySkin;
            Hook_HeroHead.initCustomHead += Hook_HeroHead_initCustomHead;
            Hook_DiveAttack.onStart += Hook_DiveAttack_onStart;
            Hook_DiveAttack.onOwnerLand += Hook_DiveAttack_onOwnerLand;
            Hook_HiddenTrigger.trigger += Hook_HiddenTrigger_trigger;
            Hook__HeroDeath.__constructor__ += Hook__HeroDeath__constructor__;
            Hook__HeroDeathBase.__constructor__ += Hook__HeroDeathBase__constructor__;
            Hook__HeroDeathContinue.__constructor__ += Hook__HeroDeathContinue__constructor__;
            Hook__HeroDeathRespawn.__constructor__ += Hook__HeroDeathRespawn__constructor__;
            Hook__HeroDeathDLCP.__constructor__ += Hook__HeroDeathDLCP__constructor__;
            Hook__BeholderDeath.__constructor__ += Hook__BeholderDeath__constructor__;
            Hook__GiantDeath.__constructor__ += Hook__GiantDeath__constructor__;
            Hook__GiantDeath4.__constructor__ += Hook__GiantDeath4__constructor__;
            Hook__KillKingCinem.__constructor__ += Hook__KillKingCinem__constructor__;
            Hook__KillQueenCinem.__constructor__ += Hook__KillQueenCinem__constructor__;
            Hook__QueenDefeated.__constructor__ += Hook__QueenDefeated__constructor__;
            Hook__KillDookuBeastCinem.__constructor__ += Hook__KillDookuBeastCinem__constructor__;
            Hook__FakeKillDooku.__constructor__ += Hook__FakeKillDooku__constructor__;
            Hook__RichterDeath.__constructor__ += Hook__RichterDeath__constructor__;
            Hook__EndCollectorPreSmash.__constructor__ += Hook__EndCollectorPreSmash__constructor__;
            Hook__SmashCinem.__constructor__ += Hook__SmashCinem__constructor__;
            Hook__EndCollectorPostSmash.__constructor__ += Hook__EndCollectorPostSmash__constructor__;
            Hook__EndCollectorPostSmashKS.__constructor__ += Hook__EndCollectorPostSmashKS__constructor__;
            Ghost.KingWeaponHooks.Install();
        }


        private void Hook_Hero_applySkin(Hook_Hero.orig_applySkin orig, Hero self, dc.String skinId)
        {
            orig(self, skinId);
            try
            {
                if (_netRole == NetRole.None)
                    return;

                var net = _net;
                if (net == null || !net.IsAlive || me == null || self == null || !ReferenceEquals(self, me))
                    return;

                var rawSkin = dc.Main.Class.ME?.user?.heroSkin?.ToString();
                if (string.IsNullOrWhiteSpace(rawSkin))
                    rawSkin = self.getSkinInfo()?.consoleCmdId?.ToString();

                var skin = NormalizeSkin(rawSkin, "PrisonerDefault");

                if (string.Equals(_lastSentHeroSkin, skin, StringComparison.Ordinal))
                    return;

                net.SendHeroSkin(skin);
                _lastSentHeroSkin = skin;
            }
            catch (Exception ex)
            {
                LogVisualSyncFailureOnce("hero skin", ex);
            }
        }

        private void Hook_HeroHead_initCustomHead(Hook_HeroHead.orig_initCustomHead orig, HeroHead self)
        {
            orig(self);
            try
            {
                if (_netRole == NetRole.None)
                    return;

                var net = _net;
                if (net == null || !net.IsAlive || me == null || self == null)
                    return;

                var localHead = me.heroHead;
                if (localHead == null || !ReferenceEquals(self, localHead))
                    return;

                var skin = NormalizeSkin(dc.Main.Class.ME?.user?.heroHeadSkin?.ToString(), "BaseFlame");
                if (string.Equals(_lastSentHeroHeadSkin, skin, StringComparison.Ordinal))
                    return;

                net.SendHeroHeadSkin(skin);
                _lastSentHeroHeadSkin = skin;
            }
            catch (Exception ex)
            {
                LogVisualSyncFailureOnce("hero head skin", ex);
            }
        }

        private void Hook_ZDoor_onActivate(Hook_ZDoor.orig_onActivate orig, ZDoor self, Hero lp, bool mob)
        {
            var localMultiplayerActivation =
                _netRole != NetRole.None &&
                _net != null &&
                _net.IsAlive &&
                me != null &&
                lp != null &&
                ReferenceEquals(lp, me);

            if (localMultiplayerActivation)
            {
                var doorKey = self == null
                    ? "unknown"
                    : string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{self.cx}:{self.cy}");
                // Keep the proven entry behavior: the strongly typed Game.activateSubLevel hook
                // owns the complete native transition window, including return-to-entrance paths.
                CheckRemoteKingRenderSafety($"zdoor-entry-preflight:{doorKey}");
            }

            orig(self, lp, mob);

            if (localMultiplayerActivation)
            {
                SendCurrentRoomTarget(force: true);
                GameMenu.EnqueueMainThreadCoalesced("ghost:receive-coords", ReceiveGhostCoords);
            }
        }

        /// <summary>
        /// Client often lacks Boss Rush door animation frames until assets settle; HL throws "Unknown frame: bossRushDoor*".
        /// Only swallow those — rethrow everything else so unrelated bugs are not masked (and to avoid odd door state).
        /// </summary>
        private static bool IsBossRushDoorMissingFrameException(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message;
                if (string.IsNullOrWhiteSpace(msg))
                    continue;
                if (msg.IndexOf("Unknown frame", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (msg.IndexOf("bossRushDoor", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                return true;
            }

            return false;
        }

        /// <summary>
        /// At most one deferred <see cref="GameMenu.EnqueueMainThread"/> <c>orig(self)</c> per door instance.
        /// </summary>
        private static readonly ConditionalWeakTable<BossRushDoor, object> s_bossRushDoorGfxDeferredPending = new();

        /// <summary>
        /// Client-only: missing boss-rush door anim frames. Do not call <see cref="Assets.Class.lib.getLevel"/> for
        /// BossRushZone from this hook — it can run during level/entity init and corrupt unrelated HL state (casts such as
        /// <c>tool.CPoint</c> vs <c>level.LevelMap</c>).
        /// </summary>
        private void Hook_BossRushDoor_initGfx(Hook_BossRushDoor.orig_initGfx orig, BossRushDoor self)
        {
            try
            {
                orig(self);
            }
            catch (Exception ex)
            {
                if (_netRole != NetRole.Client || self == null || !IsBossRushDoorMissingFrameException(ex))
                    throw;

                string? bossRushType = null;
                try { bossRushType = self.bossRushType?.ToString(); } catch { }

                Logger.Warning("[NetMod] BossRushDoor.initGfx failed on client level={LevelId}: type={Type} ({Msg})",
                    levelId,
                    bossRushType ?? "null",
                    ex.Message);

                if (s_bossRushDoorGfxDeferredPending.TryGetValue(self, out _))
                {
                    Logger.Warning("[NetMod] BossRushDoor.initGfx second sync failure before deferred retry; clearing spr level={LevelId}", levelId);
                    try { self.spr = null; } catch (Exception ex2) { Logger.Warning(ex2, "[NetMod] BossRushDoor spr=null failed"); }
                    return;
                }

                s_bossRushDoorGfxDeferredPending.Add(self, new object());
                var localOrig = orig;
                var localSelf = self;
                GameMenu.EnqueueMainThread(() =>
                {
                    try
                    {
                        localOrig(localSelf);
                    }
                    catch (Exception ex2)
                    {
                        if (_netRole != NetRole.Client || !IsBossRushDoorMissingFrameException(ex2))
                        {
                            Logger.Warning(ex2, "[NetMod] BossRushDoor.initGfx deferred retry unexpected error");
                            return;
                        }

                        string? t = null;
                        try { t = localSelf.bossRushType?.ToString(); } catch { }
                        Logger.Warning("[NetMod] BossRushDoor.initGfx deferred retry still missing frames level={LevelId}: type={Type} ({Msg})",
                            levelId,
                            t ?? "null",
                            ex2.Message);
                        try { localSelf.spr = null; } catch (Exception ex3) { Logger.Warning(ex3, "[NetMod] BossRushDoor spr=null failed"); }
                    }
                    finally
                    {
                        // "Pending" is transient. Keeping this entry forever prevented a later
                        // legitimate room/asset rebuild from receiving its own safe retry.
                        s_bossRushDoorGfxDeferredPending.Remove(localSelf);
                    }
                });
            }
        }

        private void Hook_HiddenTrigger_trigger(Hook_HiddenTrigger.orig_trigger orig, HiddenTrigger self, Entity dh)
        {
            var senderLevelId = string.IsNullOrWhiteSpace(levelId) ? string.Empty : levelId.Trim();
            string? senderGenericEventId = null;
            double? senderPreX = null;
            double? senderPreY = null;
            int? senderPreDir = null;
            double? senderPostX = null;
            double? senderPostY = null;
            int? senderPostDir = null;
            var senderWasUsed = false;

            if (self != null)
            {
                senderGenericEventId = self.genericEventId?.ToString()?.Trim();
                try { senderWasUsed = self.used; } catch { }
            }

            if (dh != null && me != null && ReferenceEquals(dh, me))
                TryCaptureBossCineHeroPosition(me, out senderPreX, out senderPreY, out senderPreDir);

            orig(self, dh);

            if (_suppressBossCineSendDepth > 0)
                return;
            if (_suppressBossTriggerNetSendUntilTick != 0 &&
                Stopwatch.GetTimestamp() < _suppressBossTriggerNetSendUntilTick)
                return;
            if (_netRole == NetRole.None || _net == null || !_net.IsAlive)
                return;
            if (self == null || dh == null || me == null || !ReferenceEquals(dh, me))
                return;

            if (!BossLevelIds.Contains(senderLevelId))
                return;

            // Boss Rush can reuse one level for multiple native room triggers. Clear only the
            // legacy presentation marker for a genuinely fresh Boss Rush trigger; do not add a
            // sequence barrier, AI pause, forced victory, or client-side re-entry.
            if (!senderWasUsed && string.Equals(senderLevelId, "BossRushZone", StringComparison.OrdinalIgnoreCase))
            {
                ClearBossCineCompleted(senderLevelId);
                _appliedBossHeroTeleportLevels.Remove(senderLevelId);
                _lastBossCineSentLevelId = null;
                _lastBossCineSentTick = 0;
            }

            if (IsBossCineCompleted(senderLevelId) ||
                IsBossCineSendSuppressed(senderLevelId) ||
                HasAppliedBossHeroTeleport(senderLevelId))
                return;
            if (string.IsNullOrWhiteSpace(senderGenericEventId))
                return;
            if (!BossRoomGenericEventIds.Contains(senderGenericEventId))
                return;
            if (!DidBossTriggerStart(dc.pr.Game.Class.ME, self))
                return;

            TryCaptureBossCineHeroPosition(me, out senderPostX, out senderPostY, out senderPostDir);
            if (senderPreX.HasValue && senderPreY.HasValue)
                _net.SendBossHeroTeleport(senderPreX.Value, senderPreY.Value, senderPreDir ?? 0);

            if (TrySendBossCinePayload(BuildBossCinePayload(
                senderLevelId,
                senderGenericEventId,
                senderPreX,
                senderPreY,
                senderPreDir,
                senderPostX,
                senderPostY,
                senderPostDir)))
                MarkBossCineCompleted(senderLevelId);
        }

        private void Hook_User_unserialize(Hook_User.orig_unserialize orig, User self, dc.hxbit.Serializer v)
        {
            orig(self, v);
            if (_netRole == NetRole.Client)
                GameDataSync.CaptureOriginalUserData(self, allowReplaceWhenBetter: true);
        }

        private void Hook_Game_onDispose(Hook_Game.orig_onDispose orig, dc.pr.Game self)
        {
            if (_netRole == NetRole.Client)
            {
                var user = self?.user;
                if (user != null)
                    GameDataSync.SwapToOriginalUserData(user);
                GameDataSync.SwapToLocalSerializerSync();
            }

            orig(self);
        }

        private void Hook__Save_save(Hook__Save.orig_save orig, User u, bool onlyGameData)
        {
            // Never let multiplayer GhostKing / KingSkin avatars enter MSave. A persisted GhostKing
            // reloads through KingSkin.initGfx with a null cooldown map and fatals the game.
            try
            {
                var purged = GhostHero.PurgeGhostKingsFromCurrentGame();
                if (purged > 0)
                    Logger.Information("[NetMod] Purged {Count} GhostKing(s) before save", purged);
            }
            catch (Exception ex)
            {
                Logger.Warning("[NetMod] GhostKing pre-save purge failed: {Message}", ex.Message);
            }

            if (_netRole == NetRole.Host)
            {
                orig(u, onlyGameData);
                return;
            }

            if (_netRole == NetRole.Client)
            {
                var serializerSwapped = GameDataSync.SwapToLocalSerializerSync();
                try
                {
                    orig(u, onlyGameData);
                }
                finally
                {
                    if (serializerSwapped)
                        GameDataSync.RestoreRemoteSerializerSync();
                }
                return;
            }

            orig(u, onlyGameData);
        }



        private void Hook_Game_pause(Hook_Game.orig_pause orig, dc.pr.Game self)
        {
            // A live co-op session cannot pause only one simulation. Outside co-op, preserve the
            // normal single-player pause behavior instead of disabling pause just because the mod
            // is installed.
            var net = _net;
            if (_netRole == NetRole.None || net == null || !net.IsAlive)
                orig(self);
        }


        private void hook_boot_update(Hook_Boot.orig_update orig, Boot self, double dt)
        {
            orig(self, dt);
            PumpSteamCallbacksForOverlay();
            GameMenu.ProcessMainThreadQueue();
            GameMenu.HandleTextInputClipboardShortcuts();
            _ghost?.UpdateLabels();
            ProcessCameraSpectateInput();
            TickRemoteKingSubLevelTransitionGuard();
        }



        private LevelStruct Hook__LevelStruct_get(Hook__LevelStruct.orig_get orig,
        User user,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ l,
        Rand rng)
        {
            levelId = l.id.ToString();
            var net = _net;
            Logger.Information("[NetMod] _LevelStruct.get hook role={Role} level={LevelId}", _netRole, levelId);
            SendLevel(levelId);

            if (_netRole == NetRole.Host)
                GameDataSync.SendLevelSeed(levelId, rng, net);
            else if (_netRole == NetRole.Client)
            {
                GameDataSync.TryApplyRemoteSerializerSync();
                GameDataSync.TryApplyRemoteLevelSeed(levelId, rng);
            }

            var result = orig(user, l, rng);

            return result;
        }

        private RoomNode Hook_LevelGen_generateGraph(Hook_LevelGen.orig_generateGraph orig,
        LevelGen self,
        User user,
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ l,
        Rand rng)
        {
            var graphLevelId = l?.id?.ToString() ?? levelId ?? string.Empty;
            Logger.Information("[NetMod] LevelGen.generateGraph hook role={Role} level={LevelId}", _netRole, graphLevelId);

            var root = orig(self, user, l, rng);
            var graph = root?.@struct;

            if (_netRole == NetRole.Host)
            {
                GameDataSync.SendLevelGraph(graphLevelId, root, graph, rng, _net);
                var activeUser = user ?? game?.user ?? dc.Main.Class.ME?.user;
                if (activeUser != null)
                {
                    var currentRune = GameDataSync.GetBossRuneInt(activeUser);
                    if (!GameDataSync.TryGetHostBossRune(out var lastSent) || lastSent != currentRune)
                        GameDataSync.SendBossRune(activeUser, _net);
                }
            }
            else if (_netRole == NetRole.Client)
            {
                const int graphSyncWaitMs = 10000;
                if (GameDataSync.TryApplyRemoteLevelGraph(graphLevelId, graph, rng, graphSyncWaitMs, out var remoteRoot, out var reason))
                {
                    Logger.Information("[NetMod] Applied remote level graph+rand for {LevelId}", graphLevelId);
                    if (remoteRoot != null)
                        root = remoteRoot;
                }
                else
                {
                    Logger.Warning("[NetMod] Remote level graph not applied for {LevelId}: {Reason}", graphLevelId, reason);
                }
            }

            return root!;
        }


        private void Hook_MiniMap_track(Hook_MiniMap.orig_track orig, MiniMap self, Entity col, int? iconId, dc.String forcedIconColor, int? blink, bool? customTile, Tile text, dc.String itemKind, dc.String isInfectedFood)
        {

            miniMap = self;
            orig(self, col, iconId, forcedIconColor, blink, customTile, text, itemKind, isInfectedFood);
        }

        private AnimManager Hook_AnimManager_play(Hook_AnimManager.orig_play orig, AnimManager self, dc.String plays, int? queueAnim, bool? g)
        {
            // Vanilla animation must always run even if optional network/ghost state is temporarily
            // invalid. Running the mod-side sync first allowed a managed/Hashlink read failure to
            // suppress the real animation and escape from one of the game's hottest hooks.
            var result = orig(self, plays, queueAnim, g);
            if (plays == null)
                return result;

            try
            {
                var play = plays.ToString();
                if (string.IsNullOrWhiteSpace(play))
                    return result;

                if (me != null && me.spr?._animManager != null && ReferenceEquals(self, me.spr._animManager))
                {
                    if (!DeadCellsMultiplayerMod.Ghost.KingWeaponSupport.IsInKingContext &&
                        !IsAttackAnim(play))
                    {
                        SendHeroAnim(play, queueAnim, g);
                    }
                }

                if (me != null && me.heroHead?.customHeadSpr != null &&
                    ReferenceEquals(self, me.heroHead.customHeadSpr._animManager))
                {
                    SendHeadAnim(play);
                }
            }
            catch (Exception ex)
            {
                LogVisualSyncFailureOnce("animation", ex);
            }

            return result;
        }

        private void LogVisualSyncFailureOnce(string context, Exception ex)
        {
            if (_visualSyncFailureLogged)
                return;

            _visualSyncFailureLogged = true;
            Logger.Warning("[NetMod] Optional {Context} sync failed safely: {Message}", context, ex.Message);
        }


        private static bool IsAttackAnim(string anim)
        {
            if (string.IsNullOrWhiteSpace(anim)) return false;
            var a = anim.Trim();
            if (a.StartsWith("w_", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("attack", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("atk", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.IndexOf("shoot", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("charge", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("bow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("crossbow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("xbow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("block", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("parry", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("whip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            kingInitialized = false;
            _net?.ClearMobSyncQueues();
            _pendingBossCineApplyByLevel.Clear();
            _suppressBossCineEchoByLevel.Clear();
            _completedBossCineLevels.Clear();
            _appliedBossHeroTeleportLevels.Clear();
            _lastBossCineSentLevelId = null;
            _lastBossCineSentTick = 0;
            ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false, clearRemoteDownedTracking: false, clearDownedAnnouncements: false);
            me = self;
            me._targetable = true;
            orig(self, oldLevel);
            var currentLevelId = GetCurrentLevelId();
            if (!string.IsNullOrWhiteSpace(currentLevelId))
                SendLevel(currentLevelId);
            SendCurrentRoomTarget(force: true);
            // Do not clear again after vanilla onLevelChanged: by this point the host may already
            // have sent the first valid bootstrap chunks for the new level. Generation fencing
            // rejects old-level traffic without deleting the new-level state.
            EnsureHeroVisibilityAfterRoomChange(me);
            var keepRemoteRenderGuardHeld = IsRemoteKingTransitionGuardArmed;
            if (!keepRemoteRenderGuardHeld)
                FinishRemoteKingLevelTransition();
            if (_netRole == NetRole.None) return;
            var net = _net;
            var localId = net?.id ?? 0;
            _ghost = new GhostHero(localId, game!, me, Logger, this);
            _ghost.SetLabel(me, GameMenu.Username);
            _ghostOwnerHero = me;
            _ghostOwnerGame = game;
            _ghostBootstrapNet = net;
            if (!keepRemoteRenderGuardHeld)
            {
                for (int i = 0; i < clients.Length; i++)
                {
                    DisposeClientSlot(i, clearIdentity: false);
                    rLastX[i] = 0;
                    rLastY[i] = 0;
                    ResetGhostHeadRuntimeState(i);
                }

                GameMenu.EnqueueMainThreadCoalesced("ghost:receive-coords", ReceiveGhostCoords);
            }
            else
            {
                Logger.Information(
                    "[NetMod][RenderTransition] Hero.onLevelChanged kept remote shell quarantined until native Level.onActivation completed");
            }

            DrainRemoteCombatQueuesAfterLevelChange();
            MarkDiveNetGuardAfterSpawnOrRoomChange();

            _debugExplorerRevealAppliedSignature = string.Empty;
            _nextDebugExplorerRevealRetryTick = 0;
            _debugExplorerRevealAllCount = 0;
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            me = self;
            me._targetable = true;
            _debugExplorerRevealAppliedSignature = string.Empty;
            orig(self, lvl, cx, cy);
            EnsureHeroVisibilityAfterRoomChange(me);
            SendCurrentRoomTarget(force: true);
            SendEquippedWeapons(self.inventory);
            MarkDiveNetGuardAfterSpawnOrRoomChange();
        }


        public void Hook_gameinit(Hook_Game.orig_init orig, dc.pr.Game self)
        {
            game = self;
            orig(self);
        }

        public void OnHeroInit()
        {
            var localGame = game ?? dc.pr.Game.Class.ME;
            var hero = localGame?.hero ?? ModCore.Modules.Game.Instance?.HeroInstance;
            if (localGame != null && hero != null && IsHeroRuntimeReadyForCoop(localGame, hero))
            {
                me = hero;
                me._targetable = true;
                ApplyDebugHeroRuntimeOptions();
            }

            GameMenu.MarkInRun();
            ApplyDebugHeroRuntimeOptions();
        }

        public void OnFrameUpdate(double dt)
        {
            if (!_ready) return;
            var hitchStart = RuntimeHitchWatch.Start();
            PumpSteamCallbacksForOverlay();
            GameMenu.ProcessMainThreadQueue();
            CheckRemoteKingRenderSafety("frame");
            GameMenu.TickMenu(dt);
            DetectAndSendBossCine();
            ApplyReceivedBossHeroTeleport();
            ApplyReceivedBossCine();
            SuppressRemoteBossDeathCineIfNeeded();
            EnforceBossIntroCineControlUnlock();
            TraceActiveCineTypeChange();

            var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
            if (hitchMs >= RuntimeHitchWatch.ModFrameSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Logger,
                    "ModEntry.OnFrameUpdate",
                    hitchMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"role={_netRole} ready={(_ready ? 1 : 0)} remoteDowned={_remoteDowned.Count} pendingDispose={_pendingClientDisposeTicks.Count}"));
            }
        }
        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            if (!TryGetReadyLocalHero(out _, out var localHero))
                return;

            me = localHero;
            var hitchStart = RuntimeHitchWatch.Start();
            var stepStart = RuntimeHitchWatch.Start();
            ApplyDebugHeroRuntimeOptions();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.ApplyDebugHeroRuntimeOptions", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            TryRecoverMissedFakeDeathFromLife();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.TryRecoverMissedFakeDeathFromLife", stepStart, null);

            if (_netRole == NetRole.None || _net == null)
                return;

            stepStart = RuntimeHitchWatch.Start();
            EnsureCoopRuntimeBootstrap();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.EnsureCoopRuntimeBootstrap", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            TrySendCurrentDiveSkillInfoSnapshot();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.TrySendCurrentDiveSkillInfoSnapshot", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            SendCurrentRoomTarget(force: false);
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.SendCurrentRoomTarget", stepStart, null);

            if (!_localFakeDead)
            {
                stepStart = RuntimeHitchWatch.Start();
                SendHeroCoords();
                LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.SendHeroCoords", stepStart, null);
            }

            stepStart = RuntimeHitchWatch.Start();
            ReceiveGhostCoords();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.ReceiveGhostCoords", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            UpdateFakeDeathFlow(dt);
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.UpdateFakeDeathFlow", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            MaintainPostRevivePositionLock();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.MaintainPostRevivePositionLock", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            ReceiveGhostWeapons();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.ReceiveGhostWeapons", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            ReceiveGhostAttacks();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.ReceiveGhostAttacks", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            UpdateGhostWeapons();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.UpdateGhostWeapons", stepStart, null);

            stepStart = RuntimeHitchWatch.Start();
            UpdateGhostHeads();
            LogHeroUpdateStepIfSlow("ModEntry.OnHeroUpdate.UpdateGhostHeads", stepStart, null);

            var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
            if (hitchMs >= RuntimeHitchWatch.ModHeroSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Logger,
                    "ModEntry.OnHeroUpdate",
                    hitchMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"role={_netRole} localFakeDead={(_localFakeDead ? 1 : 0)} remoteDowned={_remoteDowned.Count} clients={clients.Length}"));
            }
        }

        private void LogHeroUpdateStepIfSlow(string key, long stepStart, string? details)
        {
            var stepMs = RuntimeHitchWatch.GetElapsedMilliseconds(stepStart);
            if (stepMs < RuntimeHitchWatch.ModHeroStepSlowThresholdMs)
                return;

            RuntimeHitchWatch.LogSlow(Logger, key, stepMs, details);
        }

        private bool Hook_Game_hasCinematic(Hook_Game.orig_hasCinematic orig, dc.pr.Game self)
        {
            if (IsModFakeDeathCine(self?.curCine))
                return false;

            return orig(self);
        }

        private bool Hook_Hero_canBeHitBy(Hook_Hero.orig_canBeHitBy orig, Hero self, dc.Entity by)
        {
            if (_netRole != NetRole.None && self != null)
            {
                try
                {
                    if (IsEntityDownedForCombat(self))
                        return false;
                    if (self.destroyed || self.life <= 0 || !self._targetable)
                        return false;
                }
                catch
                {
                    return false;
                }
            }

            return orig(self, by);
        }

        private static bool IsModFakeDeathCine(dc.GameCinematic? cine)
        {
            if (cine == null || cine.destroyed)
                return false;

            var type = cine.GetType();
            return type == typeof(DeadBase) || type == typeof(RemoteDownedCorpse);
        }

        internal void PrepareForContinueLaunch()
        {
            DisposeCoopGhostRuntime();
            // Continue keeps the live net session; discard old-world remote combat work
            // before the next load so stale dive/attack packets cannot replay into it.
            DrainRemoteCombatQueuesAfterLevelChange();
            ResetDoorMarkerState();
            FinishRemoteKingLevelTransition();
            s_remoteKingCreationBlockedUntilTicks = 0;
            s_subLevelRenderGuardArmed = false;
            try { _net?.ClearRemoteRoomMarkers(); } catch { }
            me = null!;
            game = null;
            kingInitialized = false;
            ResetHeroCosmeticSendCache();
            GameDataSync.ClearPendingBossRuneReloadState();
        }

        /// <summary>
        /// After Continue/LoadSave (and any path that clears <see cref="_ghost"/>), recreate the
        /// GhostHero factory and wait for remote coords to spawn a fresh GhostKing on the live level.
        /// </summary>
        private void EnsureCoopRuntimeBootstrap()
        {
            if (_netRole == NetRole.None)
                return;

            var net = _net;
            if (net == null || !TryGetReadyLocalHero(out var localGame, out var localHero))
                return;

            me = localHero;
            me._targetable = true;
            game = localGame;

            if (_ghost != null &&
                (!ReferenceEquals(_ghostOwnerHero, me) ||
                 !ReferenceEquals(_ghostOwnerGame, localGame)))
            {
                DisposeCoopGhostRuntime();
            }

            if (_ghost == null)
            {
                _ghost = new GhostHero(net.id, localGame, me, Logger, this);
                _ghost.SetLabel(me, GameMenu.Username);
                _ghostOwnerHero = me;
                _ghostOwnerGame = localGame;
                _ghostBootstrapNet = null;
                ResetDoorMarkerState();
                FinishRemoteKingLevelTransition();
                s_remoteKingCreationBlockedUntilTicks = 0;
                try { net.ClearRemoteRoomMarkers(); } catch { }
                Logger.Information(
                    "[NetMod] Coop ghost runtime bootstrapped for continue/level hero={HeroUid} level={LevelId}",
                    me.__uid,
                    me._level?.map?.id?.ToString() ?? "?");
            }

            if (ReferenceEquals(_ghostBootstrapNet, net))
                return;

            _ghostBootstrapNet = net;
            EnsureHeroVisibilityAfterRoomChange(me);
            var currentLevelId = GetCurrentLevelId();
            if (!string.IsNullOrWhiteSpace(currentLevelId))
                SendLevel(currentLevelId);
            SendCurrentRoomTarget(force: true);
            ResetLocalHeroPositionSendCache();
            SendHeroCoords();
            if (me.inventory != null)
                SendEquippedWeapons(me.inventory);
            if (net.IsHost)
                GameDataSync.SendBossRune(localGame.user, net);
            GameDataSync.SendCurrentHeroCosmetics(localGame.user, net);
            MarkDiveNetGuardAfterSpawnOrRoomChange();

            // Immediately rebuild remote kings from any already-buffered peer poses so Continue
            // does not wait for the next movement packet after a still-standing player loads in.
            try { ReceiveGhostCoords(); } catch { }
        }

        private bool TryGetReadyLocalHero(out dc.pr.Game localGame, out Hero localHero)
        {
            localGame = null!;
            localHero = null!;

            var candidateGame = game ?? dc.pr.Game.Class.ME;
            var candidateHero = candidateGame?.hero ?? ModCore.Modules.Game.Instance?.HeroInstance ?? me;
            if (candidateGame == null || candidateHero == null)
                return false;

            if (!IsHeroRuntimeReadyForCoop(candidateGame, candidateHero))
                return false;

            localGame = candidateGame;
            localHero = candidateHero;
            return true;
        }

        private static bool IsHeroRuntimeReadyForCoop(dc.pr.Game localGame, Hero hero)
        {
            try
            {
                if (localGame == null || hero == null || hero.destroyed)
                    return false;

                if (localGame.user == null || localGame.data == null || localGame.controller == null)
                    return false;

                var gameHero = localGame.hero;
                if (gameHero != null && !ReferenceEquals(gameHero, hero))
                    return false;

                var level = hero._level;
                if (level == null || level.map == null)
                    return false;

                if (level.game != null && !ReferenceEquals(level.game, localGame))
                    return false;

                var currentLevel = localGame.curLevel;
                if (currentLevel != null && currentLevel.map != null)
                {
                    var heroLevelId = level.map.id?.ToString();
                    var currentLevelId = currentLevel.map.id?.ToString();
                    if (!string.IsNullOrWhiteSpace(heroLevelId) &&
                        !string.IsNullOrWhiteSpace(currentLevelId) &&
                        !string.Equals(heroLevelId, currentLevelId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                if (!HasHeroEntityRuntimeInitialized(hero))
                    return false;

                var cooldown = hero.cd;
                if (cooldown == null || cooldown.fastCheck == null)
                    return false;

                return localGame.curCine == null;
            }
            catch
            {
                return false;
            }
        }

        internal static bool HasHeroEntityRuntimeInitialized(Hero? hero)
        {
            try
            {
                return hero != null && hero.awake && hero.initDone;
            }
            catch
            {
                return false;
            }
        }

        internal static void ResetHeroCosmeticSendCache()
        {
            try
            {
                Instance?.ResetLocalSkinSendCache();
            }
            catch
            {
            }
        }

    }
}

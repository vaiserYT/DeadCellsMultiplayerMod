using Hashlink.Proxy.DynamicAccess;
using System;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Events.Interfaces.Game.Save;
using Serilog;
using System.Net;
using System.Reflection;
using System.Collections;
using dc.en;
using dc.pr;
using dc.cine;
using ModCore.Utitities;
using ModCore.Events;

using dc.en.inter;
using dc.level;
using dc.hl.types;
using HaxeProxy.Runtime;
using dc;
using dc.shader;
using dc.libs.heaps.slib;
using dc.h3d.mat;
using Serilog.Core;
using dc.ui.hud;
using dc.haxe.io;
using dc.h2d;
using Hashlink.Virtuals;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry(ModInfo info) : ModBase(info),
        IOnGameEndInit,
        IOnHeroInit,
        IOnHeroUpdate,
        IOnFrameUpdate
    {
        public static ModEntry? Instance { get; private set; }
        private bool _ready;

        private NetRole _netRole = NetRole.None;
        private static NetNode? _net;


        public dc.pr.Game? game;

        public static KingSkin _companionKing = null;
        static Hero me = null;
        private static GhostHero? _ghost;

        private GameDataSync gds;

        private string? _lastAnimSent;
        private int? _lastAnimQueueSent;
        private bool? _lastAnimGSent;
        private string? _lastRemoteAnim;
        private int? _lastRemoteAnimQueue;
        private bool? _lastRemoteAnimG;
        private double _animResendElapsed;
        private const double AnimResendInterval = 0.4;

        public static MiniMap miniMap;

        public static bool kingInitialized = false;

        public string levelId;

        public static string remoteLevelId;

        private string remoteSkin;

        internal static void SetRemoteSkin(string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            instance.remoteSkin = string.IsNullOrWhiteSpace(skin)
                ? "PrisonerDefault"
                : skin.Replace("|", "/").Trim();
        }


        public void OnGameEndInit()
        {
            _ready = true;
            GameMenu.SetRole(NetRole.None);
        }

        public override void Initialize()
        {
            Instance = this;
            gds = new GameDataSync(Logger);
            GameMenu.Initialize(Logger);
            Hook_Game.init += Hook_gameinit;
            Logger.Debug("[NetMod] Hook_mygameinit attached");
            Hook_Hero.wakeup += hook_hero_wakeup;
            Logger.Debug("[NetMod] Hook_Hero.wakeup attached");
            Hook_Hero.onLevelChanged += hook_level_changed;
            Logger.Debug("[NetMod] Hook_Hero.onLevelChanged attached");
            Hook_User.newGame += GameDataSync.user_hook_new_game;
            Logger.Debug("[NetMod] Hook_User.newGame attached");
            Hook_LevelGen.generate += GameDataSync.hook_generate;
            Logger.Debug("[NetMod] Hook_LevelGen.generate attached");
            Hook_AnimManager.play += Hook_AnimManager_play;
            Logger.Debug("[NetMod] Hook_AnimManager.play attached");
            Hook_MiniMap.track += Hook_MiniMap_track;
            Logger.Debug("[NetMod] Hook_MiniMap.track attached");
            Hook_KingSkin.initGfx += Hook_KingSkin_initgfx;
            Logger.Debug("[NetMod] Hook_KingSkin.initGfx attached");
            Hook__LevelStruct.get += Hook__LevelStruct_get;
            Logger.Debug("[NetMod] Hook__LevelStruct.get attached");
        }

        private LevelStruct Hook__LevelStruct_get(Hook__LevelStruct.orig_get orig, 
        User user, 
        virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ l, 
        dc.libs.Rand rng)
        {

            levelId = l.id.ToString();
            
            SendLevel(levelId);
            return orig(user, l, rng);
        }



        private void Hook_KingSkin_initgfx(Hook_KingSkin.orig_initGfx orig, KingSkin self)
        {
            if (remoteSkin == null) remoteSkin = "PrisonerDefault";
            orig(self);
            dc.String group = "idle".AsHaxeString();
            SpriteLib heroLib = Assets.Class.getHeroLib(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));
            self.spr.lib = heroLib;
            Texture normalMapFromGroup = heroLib.getNormalMapFromGroup(group);
            int? dp_ROOM_MAIN_HERO = Const.Class.DP_ROOM_MAIN_HERO;
            self.initSprite(heroLib, group, 0.5, 0.5, dp_ROOM_MAIN_HERO, true, null, normalMapFromGroup);
            self.initColorMap(Cdb.Class.getSkinInfo(remoteSkin.AsHaxeString()));
            self.createLight(10, 10, 0, 1);
        }


        private void Hook_MiniMap_track(Hook_MiniMap.orig_track orig, MiniMap self, Entity col, int? iconId, dc.String forcedIconColor, int? blink, bool? customTile, Tile text, dc.String itemKind, dc.String isInfectedFood)
        {
            miniMap = self;
            orig(self, col, iconId, forcedIconColor, blink, customTile, text, itemKind, isInfectedFood);
        }

        private AnimManager Hook_AnimManager_play(Hook_AnimManager.orig_play orig, AnimManager self, dc.String plays, int? queueAnim, bool? g)
        {
            var play = plays.ToString();
            if (me?.spr?._animManager != null && ReferenceEquals(self, me.spr._animManager))
            {
                SendHeroAnim(play, queueAnim, g, force: true);
            }


            return orig(self, plays, queueAnim, g);
        }


        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            ReceiveGhostLevel();
            kingInitialized = false;
            me = self;
            Log.Debug($"Hero level map rooms {me._level.map.infos}");
            SendLevel(levelId);
            orig(self, oldLevel);
            if (_ghost == null) _ghost = new GhostHero(game, me, Logger);
            _ghost.SetLabel(me, GameMenu.Username);

            if (_companionKing == null)
            {
                _companionKing = _ghost.CreateGhostKing(me._level);
                if (levelId != remoteLevelId)
                {
                    _companionKing.destroy();
                    // _companionKing.dispose();
                    // _companionKing.disposeGfx();
                }
            }
            else
            {
                _companionKing = _ghost.CreateGhostKing(me._level);
                if (levelId != remoteLevelId)
                {
                    _companionKing.destroy();
                    // _companionKing.dispose();
                    // _companionKing.disposeGfx();
                }
            }
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            me = self;
            orig(self, lvl, cx, cy);
        }


        public void Hook_gameinit(Hook_Game.orig_init orig, dc.pr.Game self)
        {
            game = self;
            orig(self);
        }

        public void OnHeroInit()
        {
            GameMenu.MarkInRun();

        }

        public void OnFrameUpdate(double dt)
        {
            if (!_ready) return;
            GameMenu.TickMenu(dt);

        }



        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            // if(_companionKing != null) _ghost.TeleportByPixels(me.spr.x + 100, me.spr.y);
            SendHeroCoords();
            ReceiveGhostCoords();
            ReceiveGhostAnim();
            if (_lastAnimSent == "idle" || _lastAnimSent == "run" || _lastAnimSent == "jumpUp" || _lastAnimSent == "jumpDown" || _lastAnimSent == "crouch" || _lastAnimSent == "land" || _lastAnimSent == "rollStart" || _lastAnimSent == "rolling" || _lastAnimSent == "rollEnd")
            {
                ResendCurrentAnim(dt);
            }
            checkOnLevel();

        }

        public void checkOnLevel()
        {
            ReceiveGhostLevel();

            if (kingInitialized) return;
            var hero = me;
            if (hero == null || hero._level == null) return;
            if (string.IsNullOrWhiteSpace(remoteLevelId)) return;
            if (!string.Equals(levelId, remoteLevelId, StringComparison.Ordinal)) return;
            if (_companionKing == null || _ghost == null) return;
            _companionKing = _ghost.reInitKing(hero._level);
            kingInitialized = true;
        }


        private void SendLevel(string lvl)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            var hero = me;

            if (net == null) return;
            net.LevelSend(lvl);

        }


        private static void ReceiveGhostLevel()
        {
            var net = _net;
            if (net == null) return;

            if (!net.TryGetRemoteLevelId(out var remoteLevel))
                return;

            if (string.Equals(remoteLevelId, remoteLevel))
                return;

            remoteLevelId = remoteLevel;
        }


        double last_x, last_y;

        private void SendHeroCoords()
        {
            if (_netRole == NetRole.None) return;

            var net = _net;
            var hero = me;

            if (net == null || hero == null || _companionKing == null) return;
            if (hero.spr.x == last_x && hero.spr.y == last_y) return;

            net.TickSend(hero.spr.x, hero.spr.y);
            last_x = hero.spr.x;
            last_y = hero.spr.y;
        }


        public static double rLastX = 0, rLastY = 0;

        private void ReceiveGhostCoords()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || ghost == null) return;

            if (net.TryGetRemote(out var rx, out var ry))
            {
                ghost.TeleportByPixels(rx, ry);
                if (rx < rLastX)
                    _companionKing.dir = -1;
                if (rx > rLastX)
                    _companionKing.dir = 1;
                rLastX = rx;
                rLastY = ry;
            }
        }

        private void SendHeroAnim(string anim, int? queueAnim, bool? g, bool force = false)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(anim)) return;
            if (!force &&
                string.Equals(_lastAnimSent, anim, StringComparison.Ordinal) &&
                _lastAnimQueueSent == queueAnim &&
                _lastAnimGSent == g)
                return;

            net.SendAnim(anim, queueAnim, g);
            _lastAnimSent = anim;
            _lastAnimQueueSent = queueAnim;
            _lastAnimGSent = g;
            _animResendElapsed = 0;
        }

        private void ReceiveGhostAnim()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || ghost == null || _companionKing == null) return;

            if (net.TryGetRemoteAnim(out var anim, out var queueAnim, out var g) && !string.IsNullOrWhiteSpace(anim))
            {
                _lastRemoteAnim = anim;
                _lastRemoteAnimQueue = queueAnim;
                _lastRemoteAnimG = g;
                _ghost.PlayAnimation(anim, queueAnim, g);
            }
        }

        private void ResendCurrentAnim(double dt)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            double AnimResendInterval_local;
            if (net == null) return;
            if (string.IsNullOrWhiteSpace(_lastAnimSent)) return;
            if (_lastAnimSent == "idle")
            {
                AnimResendInterval_local = 1;
            }
            else AnimResendInterval_local = AnimResendInterval;

            _animResendElapsed += dt;
            if (_animResendElapsed < AnimResendInterval_local) return;

            net.SendAnim(_lastAnimSent, _lastAnimQueueSent, _lastAnimGSent);
            _animResendElapsed = 0;
        }




        private IPEndPoint BuildEndpoint(string ipText, int port)
        {
            if (port <= 0 || port > 65535) port = 1234;
            if (!IPAddress.TryParse(ipText, out var ip))
            {
                ip = IPAddress.Loopback;
            }
            return new IPEndPoint(ip, port);
        }

        public void StartHostFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartHostWithEndpoint(ep);
        }

        public void StartClientFromMenu(string ipText, int port)
        {
            var ep = BuildEndpoint(ipText, port);
            StartClientWithEndpoint(ep);
        }

        private void StartHostWithEndpoint(IPEndPoint ep)
        {
            try
            {
                _net?.Dispose();

                _net = NetNode.CreateHost(Logger, ep);
                _netRole = NetRole.Host;
                GameMenu.SetRole(_netRole);
                GameMenu.NetRef = _net;

                var lep = _net.ListenerEndpoint;
                if (lep != null)
                    Logger.Information($"[NetMod] Host listening at {lep.Address}:{lep.Port}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] Host start failed: {ex.Message}");
                _netRole = NetRole.None;
                _net = null;
                GameMenu.SetRole(_netRole);
            }
        }

        private void StartClientWithEndpoint(IPEndPoint ep)
        {
            try
            {
                _net?.Dispose();

                _net = NetNode.CreateClient(Logger, ep);
                _netRole = NetRole.Client;
                GameMenu.SetRole(_netRole);
                GameMenu.NetRef = _net;

                Logger.Information($"[NetMod] Client connecting to {ep.Address}:{ep.Port}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] Client start failed: {ex.Message}");
                _netRole = NetRole.None;
                _net = null;
                GameMenu.SetRole(_netRole);
            }
        }

        public void StopNetworkFromMenu()
        {
            try
            {
                _net?.Dispose();
            }
            catch { }
            _net = null;
            _netRole = NetRole.None;
            GameMenu.NetRef = null;
            GameMenu.SetRole(_netRole);
        }


    }
}

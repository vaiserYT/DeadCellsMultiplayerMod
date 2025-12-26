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

        public static string? _remoteLevelText;

        private NetRole _netRole = NetRole.None;
        private static NetNode? _net;


        public dc.pr.Game? game;

        public static Hero _companion = null;
        public static KingSkin _companionKing = null;
        static Hero me = null;

        int players_count = 2;

        private static GhostHero? _ghost;
        private bool _ghostPending;

        private GameDataSync gds;


        public Hero[] heroes = Array.Empty<Hero>();

        public Texture hero_nrmTex;

        public dc.String hero_group;

        public static string roomsMap;




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
            Hook_Game.init += Hook_mygameinit;
            Logger.Debug("[NetMod] Hook_mygameinit attached");
            Hook_Hero.wakeup += hook_hero_wakeup;
            Logger.Debug("[NetMod] Hook_Hero.wakeup attached");
            // Hook_Hero.onLevelChanged += hook_level_changed;
            // Logger.Debug("[NetMod] Hook_Hero.onLevelChanged attached");
            Hook__LevelTransition.gotoSub += hook_gotosub;
            Logger.Debug("[NetMod] Hook__LevelTransition.gotoSub attached");
            Hook_ZDoor.enter += Hook_ZDoor_enter;
            Logger.Debug("[NetMod] Hook_ZDoor.ente attached");
            Hook_LevelTransition.loadNewLevel += Hook__LevelTransition_loadnewlevel;
            Logger.Debug("[NetMod] Hook_LevelTransition.loadNewLevel attached");
            Hook_Game.initHero += Hook_Game_inithero;
            Logger.Debug("[NetMod] Hook_Game.initHero attached");
            Hook_Game.activateSubLevel += hook_game_activateSubLevel;
            Logger.Debug("[NetMod] Hook_Game.activateSubLevel attached");
            Hook__AfterZDoor.__constructor__ += Hook__AfterZDoor_intcompanion;
            Logger.Debug("[NetMod] Hook__AfterZDoor.__constructor__ attached");
            Hook_User.newGame += GameDataSync.user_hook_new_game;
            Logger.Debug("[NetMod] Hook_User.newGame attached");
            Hook_LevelGen.generate += GameDataSync.hook_generate;
            Logger.Debug("[NetMod] Hook_LevelGen.generate attached");
            Hook_Entity.initSprite += Hook_Entity_initSprite;
        }


        private void Hook_Entity_initSprite(Hook_Entity.orig_initSprite orig, Entity self, SpriteLib lib, dc.String group, double? xr, double? yr, int? layer, bool? lighted, double? depth, Texture nrmTex)
        {
            if(self == me)
            {
                // Logger.Warning($"lib={lib}|group={group}|xr={xr}|yr={yr}|layer={layer}lighted={lighted}|depth={depth}|nrmTex={nrmTex.toString()}");
                hero_group = group;
                hero_nrmTex = nrmTex;
            }
            orig(self, lib, group, xr, yr, layer, lighted, depth, nrmTex);
        }


        private void Hook__AfterZDoor_intcompanion(Hook__AfterZDoor.orig___constructor__ orig, AfterZDoor arg1, Hero hero)
        {
            hero = heroes[0];
            orig(arg1, hero);
        }

        private void hook_game_activateSubLevel(Hook_Game.orig_activateSubLevel orig, Game self, LevelMap linkId, int? shouldSave, Ref<bool> outAnim, Ref<bool> shouldSave2)
        {
            roomsMap = linkId.rooms.ToString();
            SendLevel(roomsMap);
            orig(self, linkId, shouldSave, outAnim, shouldSave2);
        }

        private Hero Hook_Game_inithero(Hook_Game.orig_initHero orig, Game self, Level cx, int cy, int from, UsableBody fromDeadBody, bool oldLevel, Level e)
        {
            return orig(self, cx, cy, from, fromDeadBody, oldLevel, e);
        }

        private void Hook__LevelTransition_loadnewlevel(Hook_LevelTransition.orig_loadNewLevel orig, LevelTransition self)
        {
            orig(self);
        }


        private void Hook_ZDoor_enter(Hook_ZDoor.orig_enter orig, ZDoor self, Hero h)
        {
            HlAction onComplete = new HlAction(() =>
            {
                var game = Game.Class.ME;
                game.subLevels = me._level.game.subLevels = _companion._level.game.subLevels;
                _LevelTransition levelTransition = LevelTransition.Class;
                if (me._level.map == _companion._level.map)
                {
                    self.destMap = self.destMap;
                }
                levelTransition.gotoSub(self.destMap, self.linkId);
            });
            UseZDoor useZDoor = new UseZDoor(_companion, self, onComplete);
            UseZDoor useZDoor2 = new UseZDoor(me, self, onComplete);
        }




        public LevelTransition hook_gotosub(Hook__LevelTransition.orig_gotoSub orig, dc.level.LevelMap map, int? linkId)
        {
            return orig(map, linkId);
        }





        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            self = heroes[0];
            orig(self, oldLevel);
            if(_netRole != NetRole.None) _companion.awake = false;
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            // if (Array.IndexOf(heroes, self) < 0)
            // {
            //     var newLen = heroes.Length + 1;
            //     Array.Resize(ref heroes, newLen);
            //     heroes[newLen - 1] = self;
            // }
            me = self;
            // Logger.Warning($"self: {self}");
            // Logger.Warning($"heroes[0].lastRoomId: {self.lastRoomId}");
            orig(self, lvl, cx, cy);
            if(_ghost == null) _ghost = new GhostHero(game, me, hero_nrmTex, hero_group);
            if (_netRole == NetRole.None) return;

            // if (heroes.Length < players_count && game != null && me != null)
            // {
            //     _companion = _ghost.CreateGhost(me._level);
            // }
        }


        public void Hook_mygameinit(Hook_Game.orig_init orig, dc.pr.Game self)
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
            // if (_companion == null) return;
            // _ghost.Teleport(me.cx - 5, me.cy, me.xr, me.yr);
            SendHeroCoords();
            ReceiveGhostCoords();
            // checkOnLevel();
            if(_ghost == null || me == null || _companionKing != null || me.cx == 0 || me.cy == 0) return; 
            _companionKing = _ghost.CreateGhostKing(me._level);
            Logger.Debug($"cx: {_companionKing.cx}, cy: {_companionKing.cy}");
            Logger.Debug($"hero cx: {me.cx}, hero cy: {me.cy}");

        }

        public static void checkOnLevel()
        {
            if(_companion == null || me == null) return;
            if(_companion.awake == true || me._level == null || _companion._level == null) return;
            ReceiveGhostLevel();
            if(roomsMap != _remoteLevelText) return;
            Level level = _companion.set_level(me._level);
            _companion.awake = false;
            me.awake = false;
            _companion.wakeup(level, me.cx, me.cy);
            me.wakeup(level, me.cx, me.cy);
            _companion.awake = true;
        }


        private void SendLevel(string lvl)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            var hero = me;

            if (net == null || hero == null || _companion == null) return;
            net.LevelSend(lvl);

        }


        private static void ReceiveGhostLevel()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || ghost == null || _companion == null) return;

            if (!net.TryGetRemoteLevelString(out var remoteLevel) || string.IsNullOrWhiteSpace(remoteLevel))
                return;

            if (string.Equals(_remoteLevelText, remoteLevel, StringComparison.Ordinal))
                return;

            _remoteLevelText = remoteLevel;
        }


        int last_cx, last_cy;

        private void SendHeroCoords()
        {
            if (_netRole == NetRole.None) return;

            var net = _net;
            var hero = me;

            if (net == null || hero == null || _companion == null) return;
            if (hero.cx == last_cx && hero.cy == last_cy) return;

            net.TickSend(hero.cx, hero.cy, hero.xr, hero.yr);
            last_cx = hero.cx;
            last_cy = hero.cy;

        }

        private void ReceiveGhostCoords()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || ghost == null) return;

            if (net.TryGetRemote(out var rcx, out var rcy, out var rxr, out var ryr))
            {
                ghost.TeleportKing(rcx, rcy, rxr, ryr);
            }
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

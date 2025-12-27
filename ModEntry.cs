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

        public static KingSkin _companionKing = null;
        static Hero me = null;
        private static GhostHero? _ghost;
        private bool _ghostPending;

        private GameDataSync gds;

        private string? _lastAnimSent;
        private int? _lastAnimQueueSent;
        private bool? _lastAnimGSent;
        private string? _lastRemoteAnim;
        private int? _lastRemoteAnimQueue;
        private bool? _lastRemoteAnimG;
        private double _animResendElapsed;
        private const double AnimResendInterval = 0.4;


        public static string roomsMap;

        public static SpriteLib heroLib; 
        public static dc.String heroGroup; 

        public static MiniMap miniMap;

        public static bool kingInitialized=false;

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
            Hook_Hero.onLevelChanged += hook_level_changed;
            Logger.Debug("[NetMod] Hook_Hero.onLevelChanged attached");
            Hook__LevelTransition.gotoSub += hook_gotosub;
            Logger.Debug("[NetMod] Hook__LevelTransition.gotoSub attached");
            Hook_Game.initHero += Hook_Game_inithero;
            Logger.Debug("[NetMod] Hook_Game.initHero attached");
            Hook_Game.activateSubLevel += hook_game_activateSubLevel;
            Logger.Debug("[NetMod] Hook_Game.activateSubLevel attached");
            Hook_User.newGame += GameDataSync.user_hook_new_game;
            Logger.Debug("[NetMod] Hook_User.newGame attached");
            Hook_LevelGen.generate += GameDataSync.hook_generate;
            Logger.Debug("[NetMod] Hook_LevelGen.generate attached");
            Hook__MiniMap.__constructor__ += Hook__MiniMap__constructor__;
            Logger.Debug("[NetMod] Hook__MiniMap.__constructor__ attached");
            Hook_AnimManager.play += Hook_AnimManager_play;
        }


        private AnimManager Hook_AnimManager_play(Hook_AnimManager.orig_play orig, AnimManager self, dc.String plays, int? queueAnim, bool? g)
        {
            var play = plays.ToString();
            // if(play == "idle" || play == "run" || play == "jumpUp" || play == "jumpDown" || play == "crouch" || play == "land" || play == "rollStart" || play == "rolling" || play == "rollEnd")
            // {
            //     Logger.Debug($"playes: {plays}; queueAnim: {queueAnim}");
            // }

            if (me?.spr?._animManager != null && ReferenceEquals(self, me.spr._animManager))
            {
                SendHeroAnim(play, queueAnim, g, force:true);
            }


            return orig(self, plays, queueAnim, g);
        }

        private void Hook__MiniMap__constructor__(Hook__MiniMap.orig___constructor__ orig, MiniMap p, dc.libs.Process lvl, Level fowPNG, dc.haxe.io.Bytes RGBReplace)
        {
            miniMap = p;
            orig(p, lvl, fowPNG, RGBReplace);
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


        public LevelTransition hook_gotosub(Hook__LevelTransition.orig_gotoSub orig, dc.level.LevelMap map, int? linkId)
        {
            return orig(map, linkId);
        }





        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            kingInitialized = false;   
            me = self;
            orig(self, oldLevel);
            if(_ghost == null) _ghost = new GhostHero(game, me);
            _ghost.SetLabel(me, GameMenu.Username);

            if(_companionKing == null)
            {
                _companionKing = _ghost.CreateGhostKing(me._level);
                kingInitialized = true;
                return;
            }
            
            ReceiveGhostLevel();
            if(roomsMap != _remoteLevelText || kingInitialized) return;
                _ghost.reInitKing(me._level);
                kingInitialized = true;
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            me = self;
            orig(self, lvl, cx, cy);
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
            ReceiveGhostAnim();
            GameMenu.TickMenu(dt);

        }



        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            // if(_companionKing != null) _ghost.TeleportByPixels(me.spr.x + 100, me.spr.y);
            SendHeroCoords();
            ReceiveGhostCoords();
            ReceiveGhostAnim();
            ResendCurrentAnim(dt);
            checkOnLevel();

        }

        // public void kingPlayAnim()
        // {
        //     _companionKing.spr._animManager.play
        // }

        public void checkOnLevel()
        {
            ReceiveGhostLevel();

            if(kingInitialized) return;
            if(roomsMap != _remoteLevelText) return;
            if(_companionKing == null || me == null || _ghost == null) return;
            _ghost.reInitKing(me._level);
            kingInitialized = true;
        }


        private void SendLevel(string lvl)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            var hero = me;

            // if (net == null || hero == null || _companionKing == null) return;
            net.LevelSend(lvl);

        }


        private static void ReceiveGhostLevel()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || ghost == null || _companionKing == null) return;

            if (!net.TryGetRemoteLevelString(out var remoteLevel) || string.IsNullOrWhiteSpace(remoteLevel))
                return;

            if (string.Equals(_remoteLevelText, remoteLevel, StringComparison.Ordinal))
                return;

            _remoteLevelText = remoteLevel;
            // Remote changed level; force a re-init pass so players already in the room can see the companion.
            kingInitialized = false;
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


        public static double rLastX=0, rLastY=0;

        private void ReceiveGhostCoords()
        {
            var net = _net;
            var ghost = _ghost;
            if (net == null || ghost == null) return;

            if (net.TryGetRemote(out var rx, out var ry))
            {
                ghost.TeleportByPixels(rx, ry);
                if(rx < rLastX)
                    _companionKing.dir = -1;
                if(rx > rLastX)
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
            if (net == null) return;
            if (string.IsNullOrWhiteSpace(_lastAnimSent)) return;

            _animResendElapsed += dt;
            if (_animResendElapsed < AnimResendInterval) return;

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

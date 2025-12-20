using Hashlink.Proxy.DynamicAccess;
using System;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Modules;
using ModCore.Events.Interfaces.Game.Save;
using Serilog;
using System.Net;
using System.Reflection;
using dc.en;
using dc.pr;
using ModCore.Storage;

using dc.tool.mod.script;
using dc.pow;
using ModCore.Utitities;

using dc.level;
using dc;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry(ModInfo info) : ModBase(info),
        IOnGameEndInit,
        IOnHeroInit,
        IOnHeroUpdate,
        IOnFrameUpdate,
        IOnAfterLoadingSave
    {
        public static ModEntry? Instance { get; private set; }
        private static byte[]? _pendingGameData;
        private static GameDataSync? _cachedGameDataSync;
        private bool _ready;

        private object? _lastLevelRef;
        private object? _lastGameRef;
        private string? _remoteLevelText;
        private string? _lastSentLevelId;




        private NetRole _netRole = NetRole.None;
        private NetNode? _net;

        private static int _beheadedWakeCounter;

        private bool _initialGhostSpawned;

        public bool isHeroSpawned = false;
        public dc.pr.Game? game;

        public Hero _companion = null;
        Hero me = null;

        int cnt = 0;
        int players_count = 2;

        private GhostHero? _ghost;

        public void OnGameEndInit()
        {
            _ready = true;
            _beheadedWakeCounter = 0;
            GameMenu.AllowGameDataHooks = false;
            GameMenu.SetRole(NetRole.None);
            var seed = GameMenu.ForceGenerateServerSeed("OnGameEndInit");
            Logger.Information("[NetMod] GameEndInit - ready (use menu) seed={Seed}", seed);
        }

        public override void Initialize()
        {
            Instance = this;
            GameMenu.Initialize(Logger);
            Hook_Game.init += Hook_mygameinit;
            Logger.Debug("[NetMod] Hook_mygameinit attached");
            Hook_Hero.wakeup += hook_hero_wakeup;
            Logger.Debug("[NetMod] Hook_Hero.wakeup attached");
            Hook_Hero.onLevelChanged += hook_level_changed;
            Logger.Debug("[NetMod] Hook_Hero.onLevelChanged attached");

            Hook__LevelStruct.get += Hook__LevelStruct_get;


        }

        
        LevelStruct Hook__LevelStruct_get
        (
            Hook__LevelStruct.orig_get orig, dc.User user, 
            Hashlink.Virtuals.virtual_baseLootLevel_biome_bonusTripleScrollAfterBC_cellBonus_dlc_doubleUps_eliteRoomChance_eliteWanderChance_flagsProps_group_icon_id_index_loreDescriptions_mapDepth_minGold_mobDensity_mobs_name_nextLevels_parallax_props_quarterUpsBC3_quarterUpsBC4_specificLoots_specificSubBiome_transitionTo_tripleUps_worldDepth_ l, 
            dc.libs.Rand rng
        )
        {  
            
            return orig(user, l, rng);
        }
        public void hook_level_changed(Hook_Hero.orig_onLevelChanged orig, Hero self, Level oldLevel)
        {
            orig(self, oldLevel);

            if (_netRole == NetRole.None) return;
            SendLevel();
            var remoteCurrentLevelId = _remoteLevelText;

            

            if (oldLevel != null && self._level.uniqId.ToString() == remoteCurrentLevelId)
            {
                _ghost?.SetLevel(self._level);
                _companion?.init();
                _companion?.initGfx();
                Logger.Debug($"Ghost set level = {self._level}");
            }
            Logger.Debug($"[NetMod] hook_level_changed.old_level = {oldLevel}");
        }


        public void hook_hero_wakeup(Hook_Hero.orig_wakeup orig, Hero self, Level lvl, int cx, int cy)
        {
            if (cnt == 0)
                me = self;

            orig(self, lvl, cx, cy);
            if (_netRole == NetRole.None) return;

            cnt++;

            if (cnt < players_count && game != null && me != null)
            {
                _ghost ??= new GhostHero(game, me);
                _companion = _ghost.CreateGhost();
                // _ghost.SetLabel("TEST");
                Logger.Debug($"[NetMod] Hook_Hero.wakeup created ghost = {_companion}");
            }

            if (cnt >= players_count)
                cnt = 0;
        }


        public void Hook_mygameinit(Hook_Game.orig_init orig, dc.pr.Game self)
        {
            game = self;
            orig(self);
        }



        public void OnAfterLoadingSave(dc.User data)
        {
            try
            {
                var gd = TryGetGameData(data);
                if (gd == null) return;
                var dto = GameMenu.BuildGameDataSync(gd);
                _cachedGameDataSync = dto;
                GameMenu.CacheGameDataSync(dto);
                Logger.Information("[NetMod] Full GameData captured after save load");
            }
            catch (Exception ex)
            {
                Logger.Error("OnAfterLoadingSave failed: " + ex);
            }
        }

        public void OnHeroInit()
        {
            GameMenu.AllowGameDataHooks = true;
            GameMenu.MarkInRun();
            try
            {
                object? gmObj = game ?? (object?)ModCore.Modules.Game.Instance;
                if (gmObj == null)
                {
                    Logger.Warning("[NetMod] OnHeroInit: game instance is null");
                    return;
                }

                Hero? capturedHero = null;
                try
                {
                    dynamic gmDyn = gmObj;
                    capturedHero = gmDyn.hero as Hero;
                }
                catch
                {
                }

                if (capturedHero != null)
                {
                    me = capturedHero;
                }

                _lastGameRef = gmObj;
                _initialGhostSpawned = false;

                if (me != null)
                {
                    string? heroTypeStr = null;
                    object? heroTeam = null;
                    try
                    {
                        dynamic h = DynamicAccessUtils.AsDynamic(me);
                        try { heroTypeStr = (string?)h.type; } catch { }
                        try { heroTeam = (object?)h.team; } catch { }
                        try { _lastLevelRef = (object?)h._level; } catch { }

                        _lastGameRef = ExtractGameFromLevel(_lastLevelRef) ?? gmObj ?? _lastGameRef;
                    }
                    catch { }

                    heroTypeStr ??= me.GetType().FullName;
                    Logger.Information("[NetMod] Hero captured (type={Type}, team={Team})",
                        heroTypeStr ?? "unknown",
                        heroTeam ?? "unknown");
                    TryInitialGhostSpawn();
                }
                else
                {
                    Logger.Information("[NetMod] Hero is null");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[NetMod] OnHeroInit error: {ex.Message}");
            }
        }





        public static void ApplyGameDataBytes(byte[] data)
        {
            try
            {
                var packet = new
                {
                    payload = string.Empty,
                    hx = Convert.ToBase64String(data),
                    extra = (object?)null,
                    hxbit = true
                };

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(packet);
                var obj = HaxeProxySerializer.Deserialize<dc.tool.GameData>(json);
                if (obj != null)
                {
                    GameMenu.ApplyFullGameData(obj);
                    Log.Information("[NetMod] Applied GameData binary sync.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("[NetMod] ApplyGameDataBytes failed: {Ex}", ex);
            }
        }

        public static void OnClientReceiveGameData(byte[] bytes)
        {
            ApplyGameDataBytes(bytes);
        }



        public void OnFrameUpdate(double dt)
        {
            if (!_ready) return;
            GameMenu.TickMenu(dt);
        }


        public int last_cx, last_cy;

        void IOnHeroUpdate.OnHeroUpdate(double dt)
        {
            SendHeroCoords();
            ReceiveGhostCoords();
            ReceiveGhostLevel();
        }


        private void SendLevel()
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            var hero = me;

            if (net == null || hero == null || _companion == null) return;
            net.LevelSend(hero._level.uniqId.ToString());

        }


        private void ReceiveGhostLevel()
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
                ghost.Teleport(rcx, rcy, rxr, ryr);
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

        private static object? ExtractGameFromLevel(object? levelObj)
        {
            if (levelObj == null) return null;
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = levelObj.GetType();
            return type.GetProperty("game", Flags)?.GetValue(levelObj) ??
                   type.GetField("game", Flags)?.GetValue(levelObj);
        }

        private void TryInitialGhostSpawn()
        {
            if (_netRole == NetRole.None) return;
            if (_initialGhostSpawned) return;
            if (me == null || _lastLevelRef == null || _lastGameRef == null) return;

            try
            {
                dynamic h = DynamicAccessUtils.AsDynamic(me);
                int cx = 0, cy = 0;
                double xr = 0.5, yr = 1;
                try { cx = (int)h.cx; } catch { }
                try { cy = (int)h.cy; } catch { }
                try { xr = (double)h.xr; } catch { }
                try { yr = (double)h.yr; } catch { }
                if (cx < 0 || cy < 0) return;

            }
            catch (Exception ex)
            {
                Logger.Warning("[NetMod] Initial ghost spawn failed: {Message}", ex.Message);
            }
        }

        private static dc.tool.GameData? TryGetGameData(dc.User data)
        {
            try
            {
                dynamic d = DynamicAccessUtils.AsDynamic(data);
                var gd = d?.gameData as dc.tool.GameData;
                if (gd != null) return gd;
            }
            catch { }

            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var type = data.GetType();
                var prop = type.GetProperty("gameData", flags);
                if (prop != null)
                {
                    var gd = prop.GetValue(data) as dc.tool.GameData;
                    if (gd != null) return gd;
                }

                var field = type.GetField("gameData", flags);
                if (field != null)
                {
                    var gd = field.GetValue(data) as dc.tool.GameData;
                    if (gd != null) return gd;
                }
            }
            catch { }

            return null;
        }

        public void ForceGhostEnterZDoor(int cx, int cy, string destMapId, int linkId)
        {
            if (_companion == null || game?.hero?._level == null) return;

            var zDoor = FindZDoorAtPosition(cx, cy);
            var ghostHero = _companion as Hero;

            if (zDoor != null && ghostHero != null)
            {
                zDoor.enter(ghostHero);
                Logger.Debug($"[NetMod] Ghost entered ZDoor at ({cx},{cy})");
            }
            else
            {
                Logger.Warning($"[NetMod] ZDoor not found at ({cx},{cy})");
            }
        }

        private ZDoor? FindZDoorAtPosition(int cx, int cy)
        {
            if (game?.hero?._level == null) return null;


            var entities = game.hero._level.entities;
            if (entities != null)
            {
                foreach (var entity in entities)
                {
                    if (entity is ZDoor zDoor && zDoor.cx == cx && zDoor.cy == cy)
                    {
                        return zDoor;
                    }
                }
            }
            return null;
        }
    }
}

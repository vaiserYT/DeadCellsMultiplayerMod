using Hashlink.Proxy.DynamicAccess;
using HaxeProxy.Runtime;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Mods;
using ModCore.Modules;
using ModCore.Events.Interfaces.Game.Save;
using Serilog;
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Linq;
using Newtonsoft.Json;

namespace DeadCellsMultiplayerMod
{
    public class ModEntry(ModInfo info) : ModBase(info),
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

        private HaxeObject? _heroRef;
        private object? _lastLevelRef;
        private object? _lastGameRef;

        private CompanionController? _companion;

        private NetRole _netRole = NetRole.None;
        private NetNode? _net;

        private int _lastSentCx = int.MinValue;
        private int _lastSentCy = int.MinValue;
        private double _lastSentXr = double.NaN;
        private double _lastSentYr = double.NaN;

        private double _accum;
        private bool _initialGhostSpawned;
        private double _ghostLogAccum;
        private const double GhostLogInterval = 5.0;
        public override void Initialize()
        {
            Instance = this;
            Logger.Information("[NetMod] Initialized");
            GameMenu.Initialize(Logger);
        }

        public void OnGameEndInit()
        {
            _ready = true;
            GameMenu.AllowGameDataHooks = false;
            GameMenu.SetRole(NetRole.None);
            var seed = GameMenu.ForceGenerateServerSeed("OnGameEndInit");
            Logger.Information("[NetMod] GameEndInit - ready (use menu) seed={Seed}", seed);
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
                var gm = Game.Instance;
                _heroRef = gm?.HeroInstance;
                _lastGameRef = gm;
                _initialGhostSpawned = false;
                _companion = new CompanionController(Logger);

                if (_heroRef != null)
                {
                    string? heroTypeStr = null;
                    try
                    {
                        dynamic h = DynamicAccessUtils.AsDynamic(_heroRef);
                        try { heroTypeStr = (string?)h.type; } catch { }
                        // У героя есть только _level, не level
                        try { _lastLevelRef = (object?)h._level; } catch { }
                        
                        // Получаем game из уровня или используем Game.Instance
                        _lastGameRef = ExtractGameFromLevel(_lastLevelRef) ?? Game.Instance ?? _lastGameRef;
                    }
                    catch { }

                    heroTypeStr ??= _heroRef.GetType().FullName;
                    Logger.Information("[NetMod] Hero captured (type={Type})", heroTypeStr ?? "unknown");
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

        public void OnHeroUpdate(double dt)
        {
            if (!_ready || _heroRef == null) return;

            var gm = Game.Instance;
            if (gm != null)
                _lastGameRef = gm;

            // шлём ≈10 Гц
            _accum += dt;
            if (_accum < 0.1) return;
            _accum = 0;

            int cx = 0, cy = 0;
            double xr = 0, yr = 0;
            object? levelObj = null;

            try
            {
                dynamic hero = DynamicAccessUtils.AsDynamic(_heroRef);
                cx = (int)hero.cx;
                cy = (int)hero.cy;
                xr = hero.xr;
                yr = hero.yr;

                levelObj = (object?)hero._level;
                if (!ReferenceEquals(levelObj, _lastLevelRef))
                {
                    _lastLevelRef = levelObj;
                    _companion?.ResetSpawnState();
                    _initialGhostSpawned = false;
                    Logger.Information("[NetMod] Level changed -> reset ghost");
                }

                // Получаем game из уровня или используем Game.Instance
                var gameObj = ExtractGameFromLevel(levelObj) ?? Game.Instance;
                if (gameObj != null)
                    _lastGameRef = gameObj;
            }
            catch (Exception ex)
            {
                // Логируем ошибку для отладки, но не падаем
                Logger.Warning($"[NetMod] OnHeroUpdate error: {ex.Message}");
                return;
            }

            TryInitialGhostSpawn();
            _ghostLogAccum += dt;
            if (_companion != null && _ghostLogAccum >= GhostLogInterval)
            {
                _ghostLogAccum = 0;
                _companion.TryLogGhostPosition();
            }

            // сеть: отправка только при изменениях
            if (_net != null && _net.IsAlive && _netRole != NetRole.None)
            {
                if (cx != _lastSentCx || cy != _lastSentCy || xr != _lastSentXr || yr != _lastSentYr)
                {
                    _lastSentCx = cx;
                    _lastSentCy = cy;
                    _lastSentXr = xr;
                    _lastSentYr = yr;
                    _net.TickSend(cx, cy, xr, yr);
                }
            }

            // появление удалённых координат — это триггер спавна призрака
            if (_net == null || !_net.IsAlive || _netRole == NetRole.None) return;
            if (!_net.TryGetRemote(out var rcx, out var rcy, out var rxr, out var ryr)) return;
            if (rcx < 0 || rcy < 0) return;

            _companion ??= new CompanionController(Logger);

            // спавним РАЗ на текущем уровне
            if (!_companion.IsSpawned)
            {
                if (_lastLevelRef != null && _lastGameRef != null && _heroRef != null)
                    _companion.EnsureSpawned(_heroRef, _lastLevelRef, _lastGameRef, cx, cy);
            }

            // и обновляем позицию
            _companion?.TeleportTo(rcx, rcy, rxr, ryr);
        }

        public static void OnClientReceiveGameData(byte[] bytes)
        {
            _pendingGameData = bytes;
            Log.Information("[NetMod] Client received GameData blob ({0} bytes)", bytes.Length);
            GameMenu.NotifyGameDataReceived();
            ApplyGameDataBytes(bytes);
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


        // GameDataSync helpers removed (using full hxbit path)

        public void OnFrameUpdate(double dt)
        {
            if (!_ready) return;

            GameMenu.TickMenu(dt);
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
            if (_initialGhostSpawned) return;
            if (_heroRef == null || _lastLevelRef == null || _lastGameRef == null) return;

            try
            {
                dynamic h = DynamicAccessUtils.AsDynamic(_heroRef);
                int cx = 0, cy = 0;
                double xr = 0.5, yr = 1;
                try { cx = (int)h.cx; } catch { }
                try { cy = (int)h.cy; } catch { }
                try { xr = (double)h.xr; } catch { }
                try { yr = (double)h.yr; } catch { }
                if (cx < 0 || cy < 0) return;

                _companion ??= new CompanionController(Logger);
                _companion.EnsureSpawned(_heroRef, _lastLevelRef, _lastGameRef, cx, cy);
                if (_companion.IsSpawned)
                {
                    _initialGhostSpawned = true;
                    Logger.Information("[NetMod] Initial ghost spawned at {Cx},{Cy}", cx, cy);
                }
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
    }
}

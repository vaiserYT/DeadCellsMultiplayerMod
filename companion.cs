using System;
using System.Linq;
using Serilog;
using System.Reflection;
using Hashlink.Proxy.DynamicAccess;

namespace DeadCellsMultiplayerMod
{
    /// <summary>
    /// Управляет РОВНО одним призраком (HeroGhost).
    /// Ничего не делает, пока нет удалённых координат.
    /// </summary>
    public sealed class CompanionController
    {
        private readonly ILogger _log;
        private readonly HeroGhost _ghost;
        private static readonly string[] PreferredEntityTypes =
        {
            // Prefer real hero class to keep full body/animations
            "en.Hero",
            "dc.en.Hero",
            "dc.en.hero.Beheaded",
            "dc.en.mob.BootlegHomunculus",
            "dc.en.mob._BootlegHomunculus"
        };

        private object? _lastLevelRef;
        private object? _lastGameRef;

        public bool IsSpawned => _ghost.IsSpawned;
        public bool TryLogGhostPosition()
        {
            return _ghost.TryLogCoords();
        }

        public CompanionController(ILogger log)
        {
            _log = log;
            _ghost = new HeroGhost(log);
        }

        public void ResetSpawnState()
        {
            _ghost.Reset();
            _lastLevelRef = null;
            _log.Information("[Companion] Reset");
        }

        /// <summary>
        /// Спавнит призрака ОДИН РАЗ на текущем уровне.
        /// Вызывать только после появления удалённых координат.
        /// </summary>
        public void EnsureSpawned(object heroRef, object? levelHint, object? gameHint, int spawnCx, int spawnCy)
        {
            if (_ghost.IsSpawned) return;
            if (heroRef == null) return;

            if (!_ghost.HasEntityType)
            {
                var forced = PreferredEntityTypes.Any(t => _ghost.TrySetEntityType(t));
                if (!forced)
                    _ghost.FindSuitableEntityType(heroRef);
            }

            object? levelObj = levelHint ?? _lastLevelRef;
            object? gameObj = gameHint ?? _lastGameRef;

            // ��?�?�'�?�?�>��?�?��? �?�?��?�? �?�?�?�?�?�?
            try
            {
                dynamic h = DynamicAccessUtils.AsDynamic(heroRef);
                // У героя есть только _level, не level
                try { levelObj = (object?)h._level ?? levelObj; } catch { }
                if (levelObj != null)
                    _lastLevelRef = levelObj;

                // Не пытаемся получить game из героя - его там нет
                if (gameObj == null && levelObj != null)
                {
                    var levelType = levelObj.GetType();
                    gameObj = levelType.GetProperty("game", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(levelObj) ??
                              levelType.GetField("game", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(levelObj);
                }

                if (gameObj != null)
                    _lastGameRef = gameObj;
            }
            catch { }

            levelObj ??= _lastLevelRef;
            gameObj ??= _lastGameRef;

            var ok = _ghost.Spawn(heroRef, levelObj, gameObj, spawnCx, spawnCy);
            if (!ok) _log.Warning("[Companion] Spawn failed");
        }
        public void TeleportTo(int cx, int cy, double xr, double yr)
        {
            if (!_ghost.IsSpawned) return;
            _ghost.TeleportTo(cx, cy, xr, yr);
        }
    }
}

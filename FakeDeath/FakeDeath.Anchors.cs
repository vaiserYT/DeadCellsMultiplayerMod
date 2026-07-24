using System;
using System.Diagnostics;
using dc.en;
using dc.tool.atk;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using ModCore.Modules;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        // Keep enough grounded history to recover a corpse even after a long pit fall or a
        // several-second trap sequence. The old 18-sample buffer only covered about two seconds,
        // which was too short for many off-map deaths.
        private const int SafeReviveAnchorHistorySize = 96;
        private const double SafeReviveAnchorSampleSeconds = 0.12;
        private const double SafeReviveAnchorPreferredAgeSeconds = 0.75;
        private const double SafeReviveAnchorMaxAgeSeconds = 12.0;
        private const double EnvironmentalDamageContextSeconds = 2.5;
        // A sample recorded immediately before or during contact with spikes/lava is not safe.
        // Quarantine new samples briefly after environmental damage and remove the recent tail.
        private const double SafeAnchorEnvironmentalQuarantineSeconds = 2.5;
        private const double SafeAnchorEnvironmentalPurgeSeconds = 2.25;
        // Downed bodies should never remain at the lethal coordinate. Prefer an older point far
        // enough away to be outside even a wide spike strip, then fall back to the oldest valid
        // same-room anchor rather than the death position.
        private const double HazardRecoveryPreferredAgeSeconds = 1.25;
        private const double HazardRecoveryFallbackAgeSeconds = 2.25;
        private const double HazardRecoveryMinDistancePx = 144.0;
        private const double HazardRecoveryMinDistanceSq = HazardRecoveryMinDistancePx * HazardRecoveryMinDistancePx;

        private readonly struct SafeReviveAnchorSample
        {
            public readonly double X;
            public readonly double Y;
            public readonly long Ticks;
            public readonly string LevelId;

            public SafeReviveAnchorSample(double x, double y, long ticks, string levelId)
            {
                X = x;
                Y = y;
                Ticks = ticks;
                LevelId = levelId ?? string.Empty;
            }
        }

        private readonly SafeReviveAnchorSample[] _safeReviveAnchors = new SafeReviveAnchorSample[SafeReviveAnchorHistorySize];
        private int _safeReviveAnchorCount;
        private int _safeReviveAnchorWriteIndex;
        private long _nextSafeReviveAnchorSampleTicks;
        private string _safeReviveAnchorLevelId = string.Empty;
        private bool _hasSafeAnchorMotionProbe;
        private double _lastSafeAnchorMotionProbeY;
        private string _safeAnchorMotionProbeLevelId = string.Empty;
        private bool _localDownedUsesRecoveryAnchor;
        private bool _lastLocalDamageWasEnvironmental;
        private long _lastLocalDamageContextTicks;


        private void RecordLocalDamageContext(AttackData? attack)
        {
            var now = Stopwatch.GetTimestamp();
            _lastLocalDamageContextTicks = now;
            _lastLocalDamageWasEnvironmental = IsEnvironmentalDamageSource(attack);
            if (_lastLocalDamageWasEnvironmental)
                InvalidateRecentSafeReviveAnchors(now, SafeAnchorEnvironmentalPurgeSeconds);
        }

        private void InvalidateRecentSafeReviveAnchors(long now, double seconds)
        {
            if (_safeReviveAnchorCount <= 0 || seconds <= 0.0)
                return;

            var cutoffTicks = now - (long)(Stopwatch.Frequency * seconds);
            for (var i = 0; i < _safeReviveAnchors.Length; i++)
            {
                var sample = _safeReviveAnchors[i];
                if (sample.Ticks > 0 && sample.Ticks >= cutoffTicks)
                    _safeReviveAnchors[i] = default;
            }
        }

        private static bool IsEnvironmentalDamageSource(AttackData? attack)
        {
            if (attack == null)
                return true;

            dc.Entity? source = null;
            try { source = attack.source; } catch { }
            if (source == null)
                return true;
            if (source is Mob || source is Hero || source is GhostKing)
                return false;

            try
            {
                var name = source.GetType().Name ?? string.Empty;
                if (name.IndexOf("Bullet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Projectile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Shot", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            catch
            {
            }

            return true;
        }

        private void UpdateLocalSafeReviveAnchor(Hero? hero)
        {
            if (hero == null || _localFakeDead || _localDeathConversionInProgress)
                return;

            try
            {
                if (hero.destroyed || hero.life <= 0 || hero._level == null || hero.spr == null)
                    return;
                if (hero.isOutOfGame)
                    return;
            }
            catch
            {
                return;
            }

            var level = GetCurrentLevelId();
            if (string.IsNullOrWhiteSpace(level))
                return;
            if (!string.Equals(_safeReviveAnchorLevelId, level, StringComparison.Ordinal))
                ResetSafeReviveAnchorHistory(level);

            var now = Stopwatch.GetTimestamp();
            if (_lastLocalDamageWasEnvironmental && _lastLocalDamageContextTicks > 0 &&
                now - _lastLocalDamageContextTicks <
                (long)(Stopwatch.Frequency * SafeAnchorEnvironmentalQuarantineSeconds))
            {
                return;
            }

            if (_nextSafeReviveAnchorSampleTicks != 0 && now < _nextSafeReviveAnchorSampleTicks)
                return;
            _nextSafeReviveAnchorSampleTicks = now +
                (long)(Stopwatch.Frequency * SafeReviveAnchorSampleSeconds);

            try
            {
                var verticalMotion = Math.Abs(hero.dy) + Math.Abs(hero.bdy);
                if (!double.IsFinite(verticalMotion) || verticalMotion > 0.45)
                    return;
            }
            catch
            {
            }

            double x;
            double y;
            if (!TryGetHeroLogicalPixelPosition(hero, out x, out y))
            {
                try
                {
                    x = hero.get_targetSprPosX();
                    y = hero.get_targetSprPosY();
                }
                catch
                {
                    try
                    {
                        x = hero.spr?.x ?? 0.0;
                        y = hero.spr?.y ?? 0.0;
                    }
                    catch
                    {
                        return;
                    }
                }
            }

            if (!TryProjectHeroPositionToSafeGround(hero, x, y, out var safeX, out var safeY))
                return;

            // Do not certify positions while a platform/elevator is carrying the hero vertically.
            // Hero.dy can remain near zero on moving platforms, so compare logical floor Y across
            // samples as a second independent stability check. The first stable probe after any
            // vertical movement is only observed; the following stable probe becomes an anchor.
            if (!_hasSafeAnchorMotionProbe ||
                !string.Equals(_safeAnchorMotionProbeLevelId, level, StringComparison.Ordinal))
            {
                _hasSafeAnchorMotionProbe = true;
                _lastSafeAnchorMotionProbeY = safeY;
                _safeAnchorMotionProbeLevelId = level;
                return;
            }

            var floorDeltaY = Math.Abs(safeY - _lastSafeAnchorMotionProbeY);
            _lastSafeAnchorMotionProbeY = safeY;
            if (!double.IsFinite(floorDeltaY) || floorDeltaY > 2.0)
                return;

            _safeReviveAnchors[_safeReviveAnchorWriteIndex] =
                new SafeReviveAnchorSample(safeX, safeY, now, level);
            _safeReviveAnchorWriteIndex = (_safeReviveAnchorWriteIndex + 1) % SafeReviveAnchorHistorySize;
            if (_safeReviveAnchorCount < SafeReviveAnchorHistorySize)
                _safeReviveAnchorCount++;
        }

        private void ResetSafeReviveAnchorHistory(string levelId)
        {
            Array.Clear(_safeReviveAnchors, 0, _safeReviveAnchors.Length);
            _safeReviveAnchorCount = 0;
            _safeReviveAnchorWriteIndex = 0;
            _nextSafeReviveAnchorSampleTicks = 0;
            _safeReviveAnchorLevelId = levelId ?? string.Empty;
            _hasSafeAnchorMotionProbe = false;
            _lastSafeAnchorMotionProbeY = 0.0;
            _safeAnchorMotionProbeLevelId = levelId ?? string.Empty;
        }

        private bool TryGetSafeReviveAnchor(string levelId, long now, bool preferOlderSample, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            if (_safeReviveAnchorCount <= 0 || string.IsNullOrWhiteSpace(levelId))
                return false;

            SafeReviveAnchorSample? newestValid = null;
            for (var i = 0; i < _safeReviveAnchorCount; i++)
            {
                var index = (_safeReviveAnchorWriteIndex - 1 - i + SafeReviveAnchorHistorySize) %
                            SafeReviveAnchorHistorySize;
                var sample = _safeReviveAnchors[index];
                if (sample.Ticks <= 0 ||
                    !string.Equals(sample.LevelId, levelId, StringComparison.Ordinal))
                {
                    continue;
                }

                var ageSeconds = (now - sample.Ticks) / (double)Stopwatch.Frequency;
                if (ageSeconds < 0.0 || ageSeconds > SafeReviveAnchorMaxAgeSeconds)
                    continue;

                newestValid ??= sample;
                if (!preferOlderSample || ageSeconds >= SafeReviveAnchorPreferredAgeSeconds)
                {
                    x = sample.X;
                    y = sample.Y;
                    return true;
                }
            }

            if (newestValid.HasValue)
            {
                x = newestValid.Value.X;
                y = newestValid.Value.Y;
                return true;
            }

            return false;
        }

        private bool TryGetHazardRecoveryAnchor(
            string levelId,
            long now,
            double deathX,
            double deathY,
            out double x,
            out double y)
        {
            x = 0.0;
            y = 0.0;
            if (_safeReviveAnchorCount <= 0 || string.IsNullOrWhiteSpace(levelId))
                return false;

            SafeReviveAnchorSample? agedFallback = null;
            SafeReviveAnchorSample? oldestValid = null;
            var hasFiniteDeathPosition = double.IsFinite(deathX) && double.IsFinite(deathY);

            // Search from newest to oldest. Prefer a point that is both old enough to pre-date the
            // hazard contact and far enough away that it is unlikely to still be inside the same
            // spike bed, pit edge, lava strip, or trap volume.
            for (var i = 0; i < _safeReviveAnchorCount; i++)
            {
                var index = (_safeReviveAnchorWriteIndex - 1 - i + SafeReviveAnchorHistorySize) %
                            SafeReviveAnchorHistorySize;
                var sample = _safeReviveAnchors[index];
                if (sample.Ticks <= 0 ||
                    !string.Equals(sample.LevelId, levelId, StringComparison.Ordinal))
                {
                    continue;
                }

                var ageSeconds = (now - sample.Ticks) / (double)Stopwatch.Frequency;
                if (ageSeconds < 0.0 || ageSeconds > SafeReviveAnchorMaxAgeSeconds)
                    continue;

                oldestValid = sample;
                if (!agedFallback.HasValue && ageSeconds >= HazardRecoveryFallbackAgeSeconds)
                    agedFallback = sample;

                var separatedFromDeath = true;
                if (hasFiniteDeathPosition)
                {
                    var dx = sample.X - deathX;
                    var dy = sample.Y - deathY;
                    separatedFromDeath = dx * dx + dy * dy >= HazardRecoveryMinDistanceSq;
                }

                if (ageSeconds >= HazardRecoveryPreferredAgeSeconds && separatedFromDeath)
                {
                    x = sample.X;
                    y = sample.Y;
                    return true;
                }
            }

            // A player can die while almost stationary on a trap, so distance may not produce a
            // candidate. In that case use an older grounded sample rather than leaving the corpse
            // in the hazard. The final fallback is the oldest still-valid same-room sample.
            var fallback = oldestValid ?? agedFallback;
            if (!fallback.HasValue)
                return false;

            x = fallback.Value.X;
            y = fallback.Value.Y;
            return true;
        }

        private bool TryGetLivingTeammateSafeAnchor(Hero hero, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            if (hero == null)
                return false;

            var net = _net;
            var localId = net?.id ?? 0;
            for (var i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client == null)
                    continue;

                try
                {
                    if (client.destroyed || client._level == null || client.spr == null)
                        continue;
                }
                catch
                {
                    continue;
                }

                var remoteId = clientIds[i];
                if (remoteId <= 0 || (localId > 0 && remoteId == localId) || IsRemotePlayerDowned(remoteId))
                    continue;

                double remoteX;
                double remoteY;
                if (!TryGetGhostLogicalPixelPosition(client, out remoteX, out remoteY))
                {
                    try
                    {
                        remoteX = client.get_targetSprPosX();
                        remoteY = client.get_targetSprPosY();
                    }
                    catch
                    {
                        try
                        {
                            remoteX = client.spr?.x ?? 0.0;
                            remoteY = client.spr?.y ?? 0.0;
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                if (double.IsFinite(remoteX) && double.IsFinite(remoteY))
                {
                    x = remoteX;
                    y = remoteY;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetHeroLogicalPixelPosition(Hero hero, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            if (hero == null)
                return false;

            try
            {
                x = (hero.cx + hero.xr) * 24.0;
                y = (hero.cy + hero.yr) * 24.0;
                return double.IsFinite(x) && double.IsFinite(y);
            }
            catch
            {
                x = 0.0;
                y = 0.0;
                return false;
            }
        }

        private static bool TryGetGhostLogicalPixelPosition(GhostKing king, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            if (king == null)
                return false;

            try
            {
                x = (king.cx + king.xr) * 24.0;
                y = (king.cy + king.yr) * 24.0;
                return double.IsFinite(x) && double.IsFinite(y);
            }
            catch
            {
                x = 0.0;
                y = 0.0;
                return false;
            }
        }

        // Do not call LevelMap.getGroundYr from the managed per-frame update path. On the current
        // DCCM/GameProxy combination that native bridge can receive the wrong HashLink receiver and
        // terminate the game with "Can't cast tool.CPoint to level.LevelMap". Safe anchors are
        // therefore selected only from finite, alive, same-level positions with low vertical motion.
        // The history itself provides the ground/reachability guarantee without touching LevelMap.
        private static bool TryProjectHeroPositionToSafeGround(Hero hero, double x, double y, out double safeX, out double safeY)
        {
            safeX = x;
            safeY = y;
            if (hero == null || !double.IsFinite(x) || !double.IsFinite(y))
                return false;

            try
            {
                if (hero.destroyed || hero._level == null || hero.spr == null || hero.isOutOfGame)
                    return false;

                var verticalMotion = Math.Abs(hero.dy) + Math.Abs(hero.bdy);
                if (!double.IsFinite(verticalMotion) || verticalMotion > 0.45)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private bool IsUnsafeLocalDeathPosition(Hero hero, double x, double y)
        {
            if (hero == null || !double.IsFinite(x) || !double.IsFinite(y))
                return true;

            try
            {
                if (hero.isOutOfGame)
                    return true;
            }
            catch
            {
            }

            try
            {
                var verticalMotion = Math.Abs(hero.dy) + Math.Abs(hero.bdy);
                if (double.IsFinite(verticalMotion) && verticalMotion > 0.65)
                    return true;
            }
            catch
            {
            }

            // Finite, in-game positions with low vertical motion are usable. Environmental
            // damage still selects an older history sample in ResolveLocalDownedAnchor.
            return false;
        }

        private void ResolveLocalDownedAnchor(Hero hero, double deathX, double deathY, out double downedX, out double downedY)
        {
            downedX = deathX;
            downedY = deathY;
            _localDownedUsesRecoveryAnchor = false;

            var now = Stopwatch.GetTimestamp();
            var level = GetCurrentLevelId();
            var recentEnvironmentalDamage = _lastLocalDamageWasEnvironmental &&
                _lastLocalDamageContextTicks > 0 &&
                now - _lastLocalDamageContextTicks <=
                (long)(Stopwatch.Frequency * EnvironmentalDamageContextSeconds);
            var unsafePosition = IsUnsafeLocalDeathPosition(hero, deathX, deathY);

            // Always prefer a confirmed earlier anchor. This is deliberately not limited to
            // deaths that were correctly classified as environmental: some spike/pit kill paths
            // bypass onDamage and only reach Hero.kill/onDie, which previously left the body at
            // the lethal coordinate. The history selection requires age and separation first,
            // then uses the oldest valid same-room sample as a guaranteed reachable fallback.
            if (TryGetHazardRecoveryAnchor(level, now, deathX, deathY, out var safeX, out var safeY))
            {
                downedX = safeX;
                downedY = safeY;
                _localDownedUsesRecoveryAnchor = true;
                Logger.Information(
                    "[NetMod][ReviveAnchor] selected prior safe downed anchor environmental={Environmental} unsafe={Unsafe} deathX={DeathX:0.0} deathY={DeathY:0.0} safeX={SafeX:0.0} safeY={SafeY:0.0}",
                    recentEnvironmentalDamage,
                    unsafePosition,
                    deathX,
                    deathY,
                    downedX,
                    downedY);
            }
            else if (TryGetLivingTeammateSafeAnchor(hero, out safeX, out safeY))
            {
                // Very early room deaths can occur before the local history contains a sample.
                // A living teammate is then the only known reachable in-room location.
                downedX = safeX;
                downedY = safeY;
                _localDownedUsesRecoveryAnchor = true;
                Logger.Information(
                    "[NetMod][ReviveAnchor] used living teammate fallback safeX={SafeX:0.0} safeY={SafeY:0.0}",
                    downedX,
                    downedY);
            }
            else if (!recentEnvironmentalDamage &&
                     !unsafePosition &&
                     TryProjectHeroPositionToSafeGround(hero, deathX, deathY, out var groundX, out var groundY))
            {
                // Last-resort only: no history and no living teammate. Keep a normal non-hazard
                // death where it occurred rather than manufacturing an unverified coordinate.
                downedX = groundX;
                downedY = groundY;
            }
            else if (TryGetSafeReviveAnchor(level, now, preferOlderSample: true, out safeX, out safeY))
            {
                downedX = safeX;
                downedY = safeY;
                _localDownedUsesRecoveryAnchor = true;
            }

            _lastLocalDamageWasEnvironmental = false;
            _lastLocalDamageContextTicks = 0;
        }
    }
}

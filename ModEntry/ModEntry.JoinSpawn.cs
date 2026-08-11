using System.Diagnostics;
using dc.en;
using dc.pr;

namespace DeadCellsMultiplayerMod
{
    /// <summary>
    /// Host-approved safe spawn for a player who joins a run that is already in progress.
    /// </summary>
    /// <remarks>
    /// A joining client rebuilds the host's level and then starts at that level's native entrance.
    /// That is correct for a coordinated transition (both players walk in together) but wrong for a
    /// mid-run join: the host may be far past one-way progression — an activated vine, a dropped
    /// ledge, a sealed door — so the joiner starts behind geometry it cannot pass and is softlocked.
    /// Ramparts is where this was reported, but nothing about it is Ramparts-specific.
    ///
    /// The fix places the joiner where the host is standing. That cell needs no collision
    /// heuristics to justify: a live hero occupies it, so by construction it is inside the level,
    /// inside a room, non-solid and reachable. The client still re-validates it against its OWN map
    /// before moving, which doubles as a world-agreement check — if the client has no room there,
    /// the two peers did not generate the same world, and teleporting would be strictly worse than
    /// staying at the entrance.
    ///
    /// Ordering matters and is enforced rather than timed: this only runs once the level exists,
    /// the hero is alive in it, and the authoritative level graph has already been applied (the
    /// graph wait happens earlier, inside LevelGen). Failure at any step leaves the native entrance
    /// untouched.
    /// </remarks>
    public partial class ModEntry
    {
        private bool _joinSpawnArmed;
        private long _joinSpawnArmedTicks;
        private string _joinSpawnLevelId = string.Empty;
        private long _nextJoinSpawnAttemptTicks;

        /// <summary>Give the level a moment to finish building before reading its collision data.</summary>
        private const double JoinSpawnRetryIntervalSeconds = 0.25;

        /// <summary>Stop trying rather than teleporting a player who has already started moving.</summary>
        private const double JoinSpawnGiveUpSeconds = 20.0;

        private void ArmJoinSpawnForCurrentLevel(string? levelId)
        {
            if (!RunLaunchFlow.TryConsumeMidRunJoinSpawn())
                return;

            _joinSpawnArmed = true;
            _joinSpawnArmedTicks = Stopwatch.GetTimestamp();
            _joinSpawnLevelId = levelId ?? string.Empty;
            _nextJoinSpawnAttemptTicks = 0;
            Logger.Information(
                "[NetMod][Session] Armed host-approved join spawn for level={Level}",
                _joinSpawnLevelId);
        }

        private void TryApplyHostApprovedJoinSpawn()
        {
            if (!_joinSpawnArmed)
                return;

            var net = _net;
            if (net == null || !net.IsAlive || net.IsHost)
            {
                _joinSpawnArmed = false;
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (_nextJoinSpawnAttemptTicks != 0 && now < _nextJoinSpawnAttemptTicks)
                return;
            _nextJoinSpawnAttemptTicks = now + (long)(Stopwatch.Frequency * JoinSpawnRetryIntervalSeconds);

            if (Stopwatch.GetElapsedTime(_joinSpawnArmedTicks).TotalSeconds >= JoinSpawnGiveUpSeconds)
            {
                _joinSpawnArmed = false;
                Logger.Warning(
                    "[NetMod][Session] Gave up on host-approved join spawn for level={Level}: " +
                    "no usable host anchor arrived. Keeping the native entrance.",
                    _joinSpawnLevelId);
                return;
            }

            // Never relocate the hero out from under a scripted sequence or a downed body. Both
            // own the hero's position, and moving it there produces exactly the stuck states this
            // is supposed to prevent. Keep waiting instead; the give-up timer bounds it.
            if (IsLocalBossIntroCineActive() || IsLocalPlayerDowned())
                return;

            var hero = me;
            if (hero == null)
                return;

            Level? level;
            string currentLevelId;
            try
            {
                level = hero._level;
                currentLevelId = level?.map?.id?.ToString() ?? string.Empty;
                if (level?.map == null || hero.destroyed || hero.life <= 0)
                    return;
            }
            catch
            {
                return;
            }

            if (!net.TryGetHostSpawnAnchor(out var anchor))
                return;

            // The anchor must describe the level this client is actually standing in.
            if (anchor.LevelId.Length > 0 &&
                currentLevelId.Length > 0 &&
                !string.Equals(anchor.LevelId, currentLevelId, StringComparison.Ordinal))
            {
                return;
            }

            // Validate against this client's own map. A missing room here means the worlds diverged.
            object? room;
            try
            {
                room = level!.map!.getRoomAt(anchor.Cx, anchor.Cy);
            }
            catch (Exception ex)
            {
                Logger.Warning("[NetMod][Session] Join spawn validation failed: {Message}", ex.Message);
                _joinSpawnArmed = false;
                return;
            }

            if (room == null)
            {
                Logger.Error(
                    "[NetMod][Session] WORLD DESYNC: host stands at {Cx}:{Cy} in {Level}, but this client has no room there. " +
                    "Keeping the native entrance instead of relocating.",
                    anchor.Cx,
                    anchor.Cy,
                    currentLevelId);
                _joinSpawnArmed = false;
                return;
            }

            try
            {
                hero.setPosCase(anchor.Cx, anchor.Cy, null, null);
                _joinSpawnArmed = false;
                Logger.Information(
                    "[NetMod][Session] Applied host-approved join spawn at {Cx}:{Cy} in {Level}",
                    anchor.Cx,
                    anchor.Cy,
                    currentLevelId);
                MarkDiveNetGuardAfterSpawnOrRoomChange();
                SendCurrentRoomTarget(force: true);
            }
            catch (Exception ex)
            {
                _joinSpawnArmed = false;
                Logger.Warning("[NetMod][Session] Join spawn relocation failed: {Message}", ex.Message);
            }
        }

        /// <summary>Host: publish the current hero cell so a joining client has a validated target.</summary>
        private void PublishHostSpawnAnchor()
        {
            var net = _net;
            if (net == null || !net.IsAlive || !net.IsHost)
                return;

            var now = Stopwatch.GetTimestamp();
            if (_nextHostSpawnAnchorTicks != 0 && now < _nextHostSpawnAnchorTicks)
                return;
            _nextHostSpawnAnchorTicks = now + (long)(Stopwatch.Frequency * HostSpawnAnchorIntervalSeconds);

            try
            {
                var hero = me;
                if (hero == null || hero.destroyed || hero.life <= 0)
                    return;

                var levelId = hero._level?.map?.id?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(levelId))
                    return;

                net.SendHostSpawnAnchor(hero.cx, hero.cy, levelId);
            }
            catch
            {
                // Anchor publishing is best-effort; never let it disturb the hero update.
            }
        }

        private long _nextHostSpawnAnchorTicks;
        private const double HostSpawnAnchorIntervalSeconds = 1.0;
    }
}

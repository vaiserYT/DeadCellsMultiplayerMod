using System;
using System.Collections.Generic;
using System.Diagnostics;
using dc.en;
using dc.cine;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using ModCore.Modules;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        // Cursed/environmental death hooks can run from inside attack/cooldown iteration. Constructing
        // DeadBase or GameOver re-entrantly from that stack can leave HashLink in a permanent main-loop
        // stall. Defer corpse-cinematic creation until a later normal frame and serialize flow updates.
        private const double LocalDeadCineCreateDelaySeconds = 0.12;
        private long _localDeadCineCreateAfterTicks;
        private bool _hasLocalDownedAnchor;
        private double _localDownedAnchorX;
        private double _localDownedAnchorY;
        private const double DownedCorpseMaxDriftPx = 96.0;
        private const double DownedCorpseMaxDriftSq = DownedCorpseMaxDriftPx * DownedCorpseMaxDriftPx;
        private readonly HashSet<int> _scratchActiveCorpseIds = new();
        private readonly List<int> _scratchStaleCorpseIds = new();
        // Environmental deaths can temporarily publish an invalid room marker or put the remote
        // GhostKing into the engine's out-of-game state. Keep a brief revive grace period so the
        // normal snapshot stream can reattach the same remote shell without requiring a sublevel
        // round-trip to rebuild it.
        private readonly Dictionary<int, long> _remoteReviveVisibilityGraceUntilTicks = new();
        private const double RemoteReviveVisibilityGraceSeconds = 3.0;


        private static bool IsVanillaHeroDeathCineActive()
        {
            try
            {
                var cine = dc.pr.Game.Class.ME?.curCine;
                return cine is HeroDeath ||
                       cine is HeroDeathBase ||
                       cine is HeroDeathContinue ||
                       cine is HeroDeathRespawn ||
                       cine is HeroDeathDLCP;
            }
            catch
            {
                return false;
            }
        }

        private bool ShouldSuppressVanillaHeroDeathCinematic(Hero? lostBody)
        {
            return _netRole != NetRole.None &&
                   _net != null &&
                   _net.IsAlive &&
                   me != null &&
                   lostBody != null &&
                   ReferenceEquals(lostBody, me);
        }

        private bool SuppressVanillaHeroDeathCinematic(Hero? lostBody, dc.GameCinematic? cine)
        {
            if (!ShouldSuppressVanillaHeroDeathCinematic(lostBody))
                return false;

            if (!_localFakeDead && lostBody != null && _net != null)
                EnterLocalFakeDeath(lostBody, _net);

            try
            {
                var game = dc.pr.Game.Class.ME;
                if (game != null && cine != null && ReferenceEquals(game.curCine, cine))
                    game.curCine = null;
            }
            catch
            {
            }

            // Constructor hooks run before the vanilla cinematic object is initialized. Calling
            // destroy/disposeImmediately on that half-constructed object can tear down unrelated
            // hero state. Simply skip the constructor; startDeathCine/kill are already redirected.
            return true;
        }

        private void Hook__HeroDeath__constructor__(Hook__HeroDeath.orig___constructor__ orig, HeroDeath e, Hero lostBody, bool fromMob)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody, fromMob);
        }

        private void Hook__HeroDeathBase__constructor__(Hook__HeroDeathBase.orig___constructor__ orig, HeroDeathBase e, Hero lostBody, bool mob)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody, mob);
        }

        private void Hook__HeroDeathContinue__constructor__(Hook__HeroDeathContinue.orig___constructor__ orig, HeroDeathContinue e, Hero lostBody, bool keepBody)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody, keepBody);
        }

        private void Hook__HeroDeathRespawn__constructor__(Hook__HeroDeathRespawn.orig___constructor__ orig, HeroDeathRespawn e, Hero lostBody)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody);
        }

        private void Hook__HeroDeathDLCP__constructor__(Hook__HeroDeathDLCP.orig___constructor__ orig, HeroDeathDLCP e, Hero lostBody, bool fromMob)
        {
            if (SuppressVanillaHeroDeathCinematic(lostBody, e))
                return;

            orig(e, lostBody, fromMob);
        }

        private void ApplyRemoteDownedGhostPositions(NetNode net)
        {
            if (net == null)
                return;

            if (_remoteDowned.Count == 0)
            {
                DisposeAllRemoteDownedCines();
                for (int i = 0; i < clients.Length; i++)
                {
                    var client = clients[i];
                    if (client != null)
                    {
                        try { client._targetable = true; } catch { }
                    }
                }
                return;
            }

            var localId = net.id;
            var localLevelId = GetCurrentLevelId();
            _scratchActiveCorpseIds.Clear();
            foreach (var state in _remoteDowned.Values)
            {
                if (state == null || state.UserId <= 0)
                    continue;
                if (!TryGetClientIndex(localId, state.UserId, out var index))
                {
                    DisposeRemoteDownedCine(state.UserId);
                    continue;
                }

                if (!string.IsNullOrEmpty(localLevelId) &&
                    !string.IsNullOrEmpty(state.LevelId) &&
                    !string.Equals(state.LevelId, localLevelId, StringComparison.Ordinal))
                {
                    DisposeRemoteDownedCine(state.UserId);
                    continue;
                }

                var client = clients[index];
                if (client == null)
                {
                    // A fall/lava snapshot can dispose the remote shell because its temporary room
                    // marker no longer matches. Recreate it immediately for the authoritative
                    // same-level downed body instead of waiting for a sublevel transition.
                    client = EnsureClientKingSlot(index);
                }
                if (client == null)
                {
                    DisposeRemoteDownedCine(state.UserId);
                    continue;
                }
                CancelPendingClientDispose(index);

                _scratchActiveCorpseIds.Add(state.UserId);

                // Never create a second corpse cinematic once the local player is also downed.
                // If this remote corpse already existed (the local player died second), keep and
                // update that single cinematic; otherwise the brief all-down state needs no new one.
                RemoteDownedCorpse? cine = null;
                if (_remoteDownedCines.TryGetValue(state.UserId, out var existingCine) && existingCine != null)
                    cine = existingCine;
                else if (!_localFakeDead)
                    cine = EnsureRemoteDownedCine(state, client);

                if (cine != null)
                {
                    try
                    {
                        cine.UpdateTarget(
                            state.X,
                            state.Y,
                            client.dir,
                            state.HasHeadPosition ? state.HeadX : null,
                            state.HasHeadPosition ? state.HeadY : null,
                            state.HasHeadAnim ? state.HeadAnim : null);
                    }
                    catch { DisposeRemoteDownedCine(state.UserId); }
                }

                try { client._targetable = false; } catch { }
                try { client.setPosPixel(state.X, state.Y - DownedGhostBodyYOffsetPx); } catch { }

                rLastX[index] = state.X;
                rLastY[index] = state.Y - DownedGhostBodyYOffsetPx;
            }

            if (_remoteDownedCines.Count > 0)
            {
                _scratchStaleCorpseIds.Clear();
                foreach (var pair in _remoteDownedCines)
                {
                    if (!_scratchActiveCorpseIds.Contains(pair.Key))
                        _scratchStaleCorpseIds.Add(pair.Key);
                }

                for (int i = 0; i < _scratchStaleCorpseIds.Count; i++)
                    DisposeRemoteDownedCine(_scratchStaleCorpseIds[i]);
            }
        }

        private bool IsRemoteDownedVisibleInCurrentLevel(int userId, string? localLevelId)
        {
            if (userId <= 0 || !_remoteDowned.TryGetValue(userId, out var state) || state == null)
                return false;

            if (string.IsNullOrWhiteSpace(localLevelId) || string.IsNullOrWhiteSpace(state.LevelId))
                return true;

            return string.Equals(localLevelId, state.LevelId, StringComparison.Ordinal);
        }

        private bool IsRemoteReviveVisibilityGraceActive(int userId)
        {
            if (userId <= 0 || !_remoteReviveVisibilityGraceUntilTicks.TryGetValue(userId, out var untilTicks))
                return false;

            if (Stopwatch.GetTimestamp() < untilTicks)
                return true;

            _remoteReviveVisibilityGraceUntilTicks.Remove(userId);
            return false;
        }

        private void BeginRemoteReviveVisibilityRecovery(
            int userId,
            int slot,
            GhostKing? client,
            double x,
            double y)
        {
            if (userId <= 0)
                return;

            _remoteReviveVisibilityGraceUntilTicks[userId] = Stopwatch.GetTimestamp() +
                (long)(Stopwatch.Frequency * RemoteReviveVisibilityGraceSeconds);
            _remoteLastDoorMarkers.Remove(userId);

            if (slot < 0 || slot >= clients.Length)
                return;

            CancelPendingClientDispose(slot);
            clientLastDownedOffsets[slot] = false;

            if (client == null)
                return;

            var unusable = false;
            try { unusable = client.destroyed; } catch { }
            try
            {
                if (!unusable && me?._level != null && client._level != null &&
                    !ReferenceEquals(client._level, me._level))
                {
                    unusable = true;
                }
            }
            catch
            {
            }
            try
            {
                if (!unusable && client.spr == null)
                    unusable = true;
            }
            catch
            {
            }

            if (unusable)
            {
                DisposeClientSlot(slot, clearIdentity: false);
                return;
            }

            RestoreRemoteKingRenderAfterRevive(slot, client, x, y, "down-state-up");
        }

        private void RestoreRemoteKingRenderAfterRevive(
            int slot,
            GhostKing client,
            double x,
            double y,
            string reason)
        {
            if (client == null || slot < 0 || slot >= clients.Length)
                return;

            try
            {
                if (double.IsFinite(x) && double.IsFinite(y))
                {
                    client.setPosPixel(x, y);
                    rLastX[slot] = x;
                    rLastY[slot] = y;
                }
            }
            catch
            {
            }

            var wasOutOfGame = false;
            try { wasOutOfGame = client.isOutOfGame; } catch { }
            try { client.lastOutOfGame = false; } catch { }
            try { client.isOutOfGame = false; } catch { }
            try { client.isOnScreen = true; } catch { }
            try
            {
                if (client.onScreenRecent < 1200.0)
                    client.onScreenRecent = 1200.0;
            }
            catch { }
            if (wasOutOfGame)
            {
                try { client.onOutOfGameChange(); } catch { }
            }
            try { client.visible = true; } catch { }
            try { client.spr?.set_visible(true); } catch { }
            try { client._targetable = true; } catch { }

            try { EnsureGhostKingRenderSafe(client, "remote-revive:" + reason, detachForTransition: false); } catch { }

            if (clientHeads[slot] == null || client.head == null)
                ScheduleGhostHeadRecreate(slot, immediate: true);
            MarkGhostHeadDirty(slot, immediate: true);
        }

        private RemoteDownedCorpse? EnsureRemoteDownedCine(RemoteDownedState state, GhostKing client)
        {
            if (state == null || client == null || me == null)
                return null;

            if (_remoteDownedCines.TryGetValue(state.UserId, out var existing))
            {
                if (existing != null)
                    return existing;

                _remoteDownedCines.Remove(state.UserId);
            }

            try
            {
                var previousCine = dc.pr.Game.Class.ME?.curCine;
                var created = new RemoteDownedCorpse(me, client, state.X, state.Y, client.dir, previousCine);
                _remoteDownedCines[state.UserId] = created;
                return created;
            }
            catch
            {
                _remoteDownedCines.Remove(state.UserId);
                return null;
            }
        }

        private void DisposeRemoteDownedCine(int userId)
        {
            if (!_remoteDownedCines.TryGetValue(userId, out var cine) || cine == null)
                return;

            _remoteDownedCines.Remove(userId);
            try { cine.destroy(); } catch { }
            try { cine.disposeImmediately(); } catch { }
        }

        private void DisposeAllRemoteDownedCines()
        {
            if (_remoteDownedCines.Count == 0)
                return;

            _scratchStaleCorpseIds.Clear();
            foreach (var id in _remoteDownedCines.Keys)
                _scratchStaleCorpseIds.Add(id);

            for (int i = 0; i < _scratchStaleCorpseIds.Count; i++)
                DisposeRemoteDownedCine(_scratchStaleCorpseIds[i]);
        }

        private void ShowReviveHintFor(int userId)
        {
            if (_remoteDownedCines.Count == 0)
                return;

            foreach (var pair in _remoteDownedCines)
            {
                var cine = pair.Value;
                if (cine == null)
                    continue;

                try
                {
                    if (pair.Key == userId)
                        cine.SetInteractionLabel(Localize(ReviveHintText));
                    else
                        cine.SetInteractionLabel(null);
                }
                catch
                {
                }
            }
        }

        private void ClearReviveHints()
        {
            if (_remoteDownedCines.Count == 0)
                return;

            foreach (var cine in _remoteDownedCines.Values)
            {
                if (cine == null)
                    continue;
                try { cine.SetInteractionLabel(null); } catch { }
            }
        }

        private void StartLocalDeadCine(Hero hero)
        {
            if (hero == null)
                return;

            if (_localDeadCine != null)
                return;

            try
            {
                _localDeadCine = new DeadBase(hero, ModEntry.GetPrimaryClient());
            }
            catch
            {
                _localDeadCine = null;
            }
        }

        private void StopLocalDeadCine()
        {
            var cine = _localDeadCine;
            _localDeadCine = null;
            if (cine == null)
                return;

            try { cine.destroy(); } catch { }
            try { cine.disposeImmediately(); } catch { }
        }

        private bool TryUpdateDownedPositionFromCorpse(double corpseX, double corpseY)
        {
            // Co-op corpses are pinned to the authoritative revive point. Never let a one-frame
            // corpse physics step move the gameplay anchor downward or through floor tiles.
            if (_localFakeDead && ShouldAnchorLocalDownedCorpse())
                return false;

            if (!double.IsFinite(corpseX) || !double.IsFinite(corpseY))
                return false;

            if (!_hasLocalDownedAnchor)
            {
                _localDownedAnchorX = _localDownedX;
                _localDownedAnchorY = _localDownedY;
                _hasLocalDownedAnchor = true;
            }

            var dx = corpseX - _localDownedAnchorX;
            var dy = corpseY - _localDownedAnchorY;
            var distSq = dx * dx + dy * dy;
            if (distSq > DownedCorpseMaxDriftSq)
                return false;

            _localDownedX = corpseX;
            _localDownedY = corpseY;
            _localHeldX = _localDownedX;
            _localHeldY = _localDownedY;
            _localDownedAnchorX = corpseX;
            _localDownedAnchorY = corpseY;
            return true;
        }

    }
}

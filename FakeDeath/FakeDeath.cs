using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using dc.en;
using dc.tool.atk;
using dc.tool.mainSkills;
using dc.ui;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using ModCore.Modules;
using HaxeProxy.Runtime;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private bool _allDownedGameOverShown;
        private bool _allDownedRestartQueued;
        private long _allDownedRestartAtTicks;
        private const double AllDownedGameOverDelaySeconds = 0.35;
        private bool _fakeDeathFlowInProgress;
        private bool _localDownedGravityCaptured;
        private bool _localDownedOriginalHasGravity = true;
        private readonly HashSet<int> _scratchRemoteActiveIds = new();
        private readonly List<int> _scratchStaleRemoteIds = new();

        private void Hook_Hero_onHeroDie(Hook_Hero.orig_onHeroDie orig, Hero self)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            var suppressBroadcast = GameDataSync.ConsumeSuppressDeathBroadcast();

            if (me != null &&
                ReferenceEquals(self, me) &&
                (_localFakeDead || _localDeathConversionInProgress))
            {
                // Prevent second onHeroDie pass from falling into vanilla death while local player is already downed.
                return;
            }

            if (suppressBroadcast)
            {
                if (_netRole != NetRole.None &&
                    net != null &&
                    me != null &&
                    ReferenceEquals(self, me) &&
                    !_localFakeDead)
                {
                    EnterLocalFakeDeath(self, net);
                }
                return;
            }

            if (_netRole != NetRole.None &&
                net != null &&
                me != null &&
                ReferenceEquals(self, me) &&
                !_localFakeDead)
            {
                EnterLocalFakeDeath(self, net);
                return;
            }

            orig(self);
        }

        private void Hook_Hero_kill(Hook_Hero.orig_kill orig, Hero self)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            if (CanConvertLocalHeroDeathToFakeDeath(self, net))
            {
                // Cursed/environmental kills can call Hero.kill while life is still positive.
                // Waiting for life<=0 lets vanilla tear down Hero.cd before startDeathCine, then
                // the revived co-op body crashes later when vanilla reads the missing fastCheck.
                // Intercept every real local kill before any vanilla death teardown begins.
                if (_localFakeDead || _localDeathConversionInProgress)
                    return;

                EnterLocalFakeDeath(self, net!);
                return;
            }

            orig(self);
        }

        private void Hook_Hero_onDie(Hook_Hero.orig_onDie orig, Hero self)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            if (CanConvertLocalHeroDeathToFakeDeath(self, net))
            {
                if (_localFakeDead || _localDeathConversionInProgress)
                    return;

                EnterLocalFakeDeath(self, net!);
                return;
            }

            orig(self);
        }

        private void Hook_Hero_onDamage(Hook_Hero.orig_onDamage orig, Hero self, AttackData disengageRatio)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            if (_netRole != NetRole.None && me != null && ReferenceEquals(self, me))
                RecordLocalDamageContext(disengageRatio);

            orig(self, disengageRatio);
        }

        private void Hook_Hero_checkCursedWeaponHit(Hook_Hero.orig_checkCursedWeaponHit orig, Hero self, AttackData a)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            if (_netRole != NetRole.None &&
                net != null &&
                me != null &&
                ReferenceEquals(self, me))
            {
                RecordLocalDamageContext(a);
                if (_localFakeDead)
                    return;

                // Hero.kill is intercepted before vanilla teardown, including cursed hits from
                // spikes and other environmental hazards. Do not mutate cooldown.fastCheck here:
                // checkCursedWeaponHit may run before the game has decided the hit is lethal.
                try
                {
                    orig(self, a);
                }
                catch (Exception ex)
                {
                    Logger.Warning("[NetMod][CurseGuard] vanilla cursed hit failed, entering fake death: {Message}", ex.Message);
                    EnterLocalFakeDeath(self, net);
                    return;
                }

                if (_localFakeDead)
                    return;

                if (ShouldEnterFakeDeathFromEarlyDeathHook(self, net) || IsVanillaHeroDeathCineActive())
                {
                    EnterLocalFakeDeath(self, net);
                }
                return;
            }

            orig(self, a);
        }

        private void AbortLocalDiveStateForFakeDeath(Hero? hero, string reason)
        {
            if (hero == null)
                return;

            try
            {
                var dive = hero.mainSkillsManager?.getMainSkill(DiveAttack.Class) as DiveAttack;
                if (dive != null)
                {
                    try { dive.cancel(); } catch { }
                    try { dive.end(); } catch { }
                }
            }
            catch
            {
            }

            // Never remove or manufacture entries in engine-owned cooldown.fastCheck maps.
            // A cursed environmental death can already be inside cooldown iteration; mutating the
            // map from a death hook risks a delayed HashLink null/type crash in the main loop.
        }

        private bool CanConvertLocalHeroDeathToFakeDeath(Hero? self, NetNode? net)
        {
            if (_netRole == NetRole.None || net == null || !net.IsAlive)
                return false;
            if (self == null || me == null || !ReferenceEquals(self, me))
                return false;

            try
            {
                if (self.destroyed || self._level == null || self.spr == null)
                    return false;
                if (self.maxLife <= 0)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private bool ShouldEnterFakeDeathFromEarlyDeathHook(Hero self, NetNode net)
        {
            if (self == null || net == null)
                return false;
            if (_localFakeDead)
                return false;
            if (me == null || !ReferenceEquals(self, me))
                return false;

            // Guard against spawn/initialization lifecycle where kill/onDie may fire transiently.
            try
            {
                if (self._level == null || self.spr == null)
                    return false;
                if (self.maxLife <= 0)
                    return false;
                if (self.life > 0)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void Hook_Hero_startDeathCine(Hook_Hero.orig_startDeathCine orig, Hero self)
        {
            if (IsDebugImmortalLocalHero(self))
            {
                ApplyDebugImmortalState(self);
                return;
            }

            var net = _net;
            if (CanConvertLocalHeroDeathToFakeDeath(self, net))
            {
                if (_localFakeDead || _localDeathConversionInProgress)
                    return;

                // Final fallback for unusual scripted deaths that bypass Hero.kill/onDie.
                EnterLocalFakeDeath(self, net!);
                return;
            }

            if (me != null && ReferenceEquals(self, me) && _localFakeDead)
                return;

            orig(self);
        }

        private void TryRecoverMissedFakeDeathFromLife()
        {
            var net = _net;
            var hero = me;
            if (_netRole == NetRole.None || net == null || hero == null)
                return;
            if (_localFakeDead)
                return;

            try
            {
                if (hero.destroyed || hero._level == null || hero.spr == null)
                    return;
                if (hero.maxLife <= 0)
                    return;
                if (hero.life > 0)
                    return;
            }
            catch
            {
                return;
            }

            EnterLocalFakeDeath(hero, net);
        }

        private void UpdateFakeDeathFlow(double dt)
        {
            // Network/down-state callbacks can indirectly pump another frame while a death cine is
            // being created or a restart is queued. Never run this state machine recursively.
            if (_fakeDeathFlowInProgress)
                return;

            _fakeDeathFlowInProgress = true;
            try
            {
                var net = _net;
                if (_netRole == NetRole.None || net == null || me == null)
                {
                    if (_localFakeDead || _remoteDowned.Count > 0)
                        ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
                    ClearReviveHints();
                    return;
                }

                if (!_localFakeDead)
                    UpdateLocalSafeReviveAnchor(me);

                ConsumeRemoteDownedStates(net);
                ConsumeReviveRequests(net);
                PruneRemoteDownedStates(net);
                ApplyRemoteDownedGhostPositions(net);

                if (_localFakeDead)
                {
                    ClearReviveHints();
                    MaintainLocalFakeDeath(net);
                    return;
                }

                UpdateReviveHintsByProximity();
                ProcessReviveHold(net);
            }
            finally
            {
                _fakeDeathFlowInProgress = false;
            }
        }

        private void ConsumeRemoteDownedStates(NetNode net)
        {
            if (!net.TryConsumePlayerDownStates(out var states))
                return;

            var combatStateChanged = false;
            try
            {
                var localId = net.id;
                if (localId <= 0)
                    return;
                for (int i = 0; i < states.Count; i++)
                {
                    var state = states[i];
                    if (state.UserId <= 0 || state.UserId == localId)
                        continue;

                    if (!state.IsDowned)
                    {
                        var revivedIdx = -1;
                        GhostKing? revivedClient = null;
                        if (TryGetClientIndex(localId, state.UserId, out revivedIdx))
                            revivedClient = clients[revivedIdx];

                        if (_remoteDowned.Remove(state.UserId))
                            combatStateChanged = true;
                        _downedAnnouncements.Remove(state.UserId);

                        // Dispose the corpse first: its onDispose restores the visibility state it
                        // captured at death time, which may already be false after a pit/lava fall.
                        // The explicit recovery below must therefore run after the cinematic is gone.
                        DisposeRemoteDownedCine(state.UserId);
                        BeginRemoteReviveVisibilityRecovery(
                            state.UserId,
                            revivedIdx,
                            revivedClient,
                            state.X,
                            state.Y);
                        continue;
                    }

                    _remoteReviveVisibilityGraceUntilTicks.Remove(state.UserId);

                    if (!_remoteDowned.TryGetValue(state.UserId, out var existing))
                    {
                        existing = new RemoteDownedState
                        {
                            UserId = state.UserId
                        };
                        _remoteDowned[state.UserId] = existing;
                        combatStateChanged = true;
                    }

                    if (_downedAnnouncements.Add(state.UserId))
                        NotifyRemotePlayerDowned(net, state.UserId);

                    existing.X = state.X;
                    existing.Y = state.Y;
                    existing.HasHeadPosition = state.HasHeadPosition;
                    existing.HeadX = state.HeadX;
                    existing.HeadY = state.HeadY;
                    existing.HasHeadAnim = state.HasHeadAnim;
                    existing.HeadAnim = state.HasHeadAnim ? (state.HeadAnim ?? string.Empty) : string.Empty;
                    existing.LevelId = state.LevelId ?? string.Empty;
                    existing.UpdatedAtTicks = Stopwatch.GetTimestamp();

                    if (TryGetClientIndex(localId, state.UserId, out var downedIdx))
                    {
                        var downedClient = clients[downedIdx];
                        if (downedClient != null)
                        {
                            try { downedClient._targetable = false; } catch { }
                        }
                    }
                }

                if (combatStateChanged)
                    global::DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.NotifyPlayerCombatStateChanged("remote-down-state");
            }
            finally
            {
                NetNode.ReleaseConsumedList(states);
            }
        }

        private void NotifyRemotePlayerDowned(NetNode net, int userId)
        {
            if (userId <= 0)
                return;

            try
            {
                var displayName = ResolveRemotePlayerDisplayName(net, userId);
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = $"Player {userId}";
                MultiplayerUI.PushSystemMessage(FormatLocalized("{0} fell!", displayName));
            }
            catch
            {
            }
        }

        private string ResolveRemotePlayerDisplayName(NetNode net, int userId)
        {
            if (net == null || userId <= 0)
                return string.Empty;

            if (net.TryGetRemoteUsername(userId, out var username) && !string.IsNullOrWhiteSpace(username))
                return username.Trim();

            if (TryGetClientIndex(net.id, userId, out var slot))
            {
                var label = GetClientLabel(slot);
                if (!string.IsNullOrWhiteSpace(label))
                    return label.Trim();
            }

            return string.Empty;
        }

        private void ConsumeReviveRequests(NetNode net)
        {
            if (!_localFakeDead)
            {
                if (net.TryConsumePlayerReviveRequests(out var ignoredRequests))
                    NetNode.ReleaseConsumedList(ignoredRequests);
                return;
            }

            if (!net.TryConsumePlayerReviveRequests(out var requests))
                return;

            try
            {
                var localId = net.id;
                for (int i = 0; i < requests.Count; i++)
                {
                    var req = requests[i];
                    if (req.TargetId != localId)
                        continue;

                    if (_localDeadCine == null || !_localDeadCine.IsHomunculusNearCorpse(ReviveHomunculusBodyMaxDistancePx))
                        continue;

                    if (!IsReviverPositionValidForLocalCorpse(net, req.ReviverId))
                        continue;

                    ReviveLocalPlayer(net);
                    return;
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(requests);
            }
        }

        private bool IsReviverPositionValidForLocalCorpse(NetNode net, int reviverId)
        {
            if (net == null || reviverId <= 0 || _localDeadCine == null)
                return false;

            // Position validation is intentionally best-effort. A missing snapshot should not make
            // a legitimate revive impossible, but a known cross-level or far-away reviver is rejected.
            if (!net.TryGetRemotePosition(reviverId, out var reviverX, out var reviverY, out var reviverLevelId))
                return true;

            var localLevelId = GetCurrentLevelId();
            if (!string.IsNullOrWhiteSpace(localLevelId) &&
                !string.IsNullOrWhiteSpace(reviverLevelId) &&
                !string.Equals(localLevelId, reviverLevelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!_localDeadCine.TryGetCorpsePixelPosition(out var corpseX, out var corpseY))
                return true;

            var dx = reviverX - corpseX;
            var dy = reviverY - corpseY;
            return dx * dx + dy * dy <=
                   ReviveRemotePositionValidationPx * ReviveRemotePositionValidationPx;
        }

        private void PruneRemoteDownedStates(NetNode net)
        {
            if (_remoteDowned.Count == 0)
                return;

            _scratchRemoteActiveIds.Clear();
            var localId = net.id;
            if (localId > 0)
                _scratchRemoteActiveIds.Add(localId);

            net.CopyRemoteUserIdsTo(_scratchRemoteActiveIds);

            for (int i = 0; i < clientIds.Length; i++)
            {
                var id = clientIds[i];
                if (id > 0)
                    _scratchRemoteActiveIds.Add(id);
            }

            var now = Stopwatch.GetTimestamp();
            var staleAfterTicks = (long)(Stopwatch.Frequency * RemoteDownedStateStaleSeconds);

            _scratchStaleRemoteIds.Clear();
            foreach (var pair in _remoteDowned)
            {
                var state = pair.Value;
                var timedOut = staleAfterTicks > 0 &&
                               state.UpdatedAtTicks > 0 &&
                               now - state.UpdatedAtTicks > staleAfterTicks;
                if (!_scratchRemoteActiveIds.Contains(pair.Key) || timedOut)
                    _scratchStaleRemoteIds.Add(pair.Key);
            }

            var combatStateChanged = false;
            for (int i = 0; i < _scratchStaleRemoteIds.Count; i++)
            {
                var staleId = _scratchStaleRemoteIds[i];
                if (TryGetClientIndex(localId, staleId, out var staleIdx))
                {
                    var staleClient = clients[staleIdx];
                    if (staleClient != null)
                    {
                        try { staleClient._targetable = true; } catch { }
                    }
                }

                DisposeRemoteDownedCine(staleId);
                _remoteReviveVisibilityGraceUntilTicks.Remove(staleId);
                if (_remoteDowned.Remove(staleId))
                    combatStateChanged = true;
                _downedAnnouncements.Remove(staleId);
            }

            if (combatStateChanged)
                global::DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.NotifyPlayerCombatStateChanged("remote-down-pruned");
        }

        private bool HasAliveRemoteTeammate(NetNode net)
        {
            var localId = net.id;
            _scratchRemoteActiveIds.Clear();
            net.CopyRemoteUserIdsTo(_scratchRemoteActiveIds, includePrimary: false);

            for (int i = 0; i < clientIds.Length; i++)
            {
                var id = clientIds[i];
                if (id > 0 && id != localId)
                    _scratchRemoteActiveIds.Add(id);
            }

            if (_scratchRemoteActiveIds.Count == 0)
            {
                if (_remoteDowned.Count > 0)
                    return false;

                if (net.IsHost)
                    return NetNode.ConnectedClientCount > 0;
                return net.IsAlive;
            }

            var localLevelId = GetCurrentLevelId();
            foreach (var id in _scratchRemoteActiveIds)
            {
                if (!_remoteDowned.TryGetValue(id, out var downed))
                    return true;

                // If teammate is tracked as downed on another level, treat them as alive.
                if (!string.Equals(localLevelId, downed.LevelId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private void CaptureAndFreezeLocalHeroPhysics(Hero hero)
        {
            if (hero == null)
                return;

            if (!_localDownedGravityCaptured)
            {
                try { _localDownedOriginalHasGravity = hero.hasGravity; }
                catch { _localDownedOriginalHasGravity = true; }
                _localDownedGravityCaptured = true;
            }

            try { hero.hasGravity = false; } catch { }
            try { hero.cancelVelocities(); } catch { }
            try { hero.dx = 0; } catch { }
            try { hero.dy = 0; } catch { }
            try { hero.bdx = 0; } catch { }
            try { hero.bdy = 0; } catch { }
        }

        private void RestoreLocalHeroPhysics(Hero? hero)
        {
            if (hero != null && _localDownedGravityCaptured)
            {
                try { hero.hasGravity = _localDownedOriginalHasGravity; } catch { }
                try { hero.cancelVelocities(); } catch { }
            }

            _localDownedGravityCaptured = false;
            _localDownedOriginalHasGravity = true;
        }

        private void EnterLocalFakeDeath(Hero hero, NetNode net)
        {
            if (hero == null || net == null || _localFakeDead || _localDeathConversionInProgress)
                return;

            _localDeathConversionInProgress = true;
            // Set this before cancelling skills so any re-entrant kill/onDie/startDeathCine call is
            // stopped before it can enter vanilla death teardown.
            _localFakeDead = true;
            try
            {
                AbortLocalDiveStateForFakeDeath(hero, "enter-fake-death");
                ResetAllDownedGameOverState();
                _localExitPenaltyApplied = false;
                _localFakeDeadStartedTicks = Stopwatch.GetTimestamp();
                _localDeadCineCreateAfterTicks = _localFakeDeadStartedTicks +
                    (long)(Stopwatch.Frequency * LocalDeadCineCreateDelaySeconds);
                double sprX, sprY;
                if (TryGetHeroLogicalPixelPosition(hero, out sprX, out sprY))
                {
                    // Logical hero coordinates are the feet/physics anchor. Corpse entities use the
                    // same coordinate model, so this avoids the old sprite-pivot mismatch that left
                    // a frozen body visibly hovering above the floor.
                }
                else if (hero.spr != null)
                {
                    sprX = hero.spr.x;
                    sprY = hero.spr.y;
                }
                else
                {
                    try
                    {
                        var cx = hero.cx;
                        var xr = hero.xr;
                        var cy = hero.cy;
                        var yr = hero.yr;
                        sprX = (cx + xr) * 24.0;
                        sprY = (cy + yr) * 24.0;
                    }
                    catch
                    {
                        sprX = 0;
                        sprY = 0;
                    }
                }
                ResolveLocalDownedAnchor(hero, sprX, sprY, out _localDownedX, out _localDownedY);
                _localHeldX = _localDownedX;
                _localHeldY = _localDownedY;
                _localDownedAnchorX = _localDownedX;
                _localDownedAnchorY = _localDownedY;
                _hasLocalDownedAnchor = true;
                _localDownedLevelId = GetCurrentLevelId();
                _nextDownedStateSendTicks = 0;
                _nextReviveAttemptTicks = 0;
                _postReviveLockUntilTicks = 0;

                try
                {
                    if (hero.life <= 0)
                        hero.life = 1;
                }
                catch { }

                try { hero._targetable = false; } catch { }
                CaptureAndFreezeLocalHeroPhysics(hero);
                global::DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.NotifyPlayerCombatStateChanged("local-player-downed");
                try { hero.lockControlsS(10.0); } catch { }
                try { hero.cancelSkillControlLock(); } catch { }
                SnapHeroToDownedPosition(hero, _localDownedX, _localDownedY, clampToGround: true);
                // Do not construct DeadBase from inside Hero.kill/onDie/checkCursedWeaponHit. The
                // next normal frame creates it after the engine finishes the lethal hit stack.

                SendLocalDownedState(net, isDowned: true, force: true);
            }
            catch (Exception ex)
            {
                // Do not fall back into vanilla death after conversion started. Preserve a minimal
                // alive/non-targetable body and let the normal fake-death maintenance recover it.
                Logger.Warning("[NetMod][DeathGuard] fake-death conversion recovered from error: {Message}", ex.Message);
                try { if (hero.life <= 0) hero.life = 1; } catch { }
                try { hero._targetable = false; } catch { }
            }
            finally
            {
                _localDeathConversionInProgress = false;
            }
        }

        private void MaintainLocalFakeDeath(NetNode net)
        {
            if (!_localFakeDead || me == null)
                return;

            var now = Stopwatch.GetTimestamp();
            try
            {
                if (me.life <= 0)
                    me.life = 1;
            }
            catch
            {
            }

            // Decide the all-down state before constructing another GameCinematic. When one
            // player already has a downed-corpse cinematic, creating a second local/remote death
            // cinematic on the same frame can corrupt the HashLink cinematic scheduler and close
            // the client without a managed exception.
            if (!HasAliveRemoteTeammate(net))
            {
                var graceTicks = (long)(Stopwatch.Frequency * 1.25);
                if (_localFakeDeadStartedTicks != 0 &&
                    now - _localFakeDeadStartedTicks < graceTicks)
                {
                    return;
                }

                HandleAllPlayersDowned(net);
                return;
            }

            if (_localDeadCine == null &&
                (_localDeadCineCreateAfterTicks == 0 || now >= _localDeadCineCreateAfterTicks))
            {
                StartLocalDeadCine(me);
            }

            if (_allDownedGameOverShown || _allDownedRestartQueued)
                ResetAllDownedGameOverState();

            CaptureAndFreezeLocalHeroPhysics(me);
            try { me.lockControlsS(0.25); } catch { }
            try { me.cancelSkillControlLock(); } catch { }
            try { me._targetable = false; } catch { }

            var cine = _localDeadCine;
            if (cine != null && cine.TryGetCorpsePixelPosition(out var corpseX, out var corpseY))
            {
                TryUpdateDownedPositionFromCorpse(corpseX, corpseY);
            }

            SnapHeroToDownedPosition(me, _localHeldX, _localHeldY, clampToGround: true);
            SendLocalDownedState(net, isDowned: true, force: false);
        }

        private void MaintainPostRevivePositionLock()
        {
            if (_localFakeDead || me == null)
                return;
            if (_postReviveLockUntilTicks == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            if (now >= _postReviveLockUntilTicks)
            {
                _postReviveLockUntilTicks = 0;
                return;
            }

            SnapHeroToDownedPosition(me, _postReviveLockX, _postReviveLockY);
        }

        private static void SnapHeroToDownedPosition(Hero hero, double x, double y, bool clampToGround = true)
        {
            if (hero == null || !double.IsFinite(x) || !double.IsFinite(y))
                return;

            // The target is already a validated history/teammate anchor. Avoid LevelMap ground
            // projection here because the native getGroundYr bridge is the startup-crash source.
            try { hero.setPosPixel(x, y); } catch { }
        }

        private void ReviveLocalPlayer(NetNode net)
        {
            if (me == null)
                return;

            ResetAllDownedGameOverState();
            var hero = me;
            _localFakeDead = false;
            _localExitPenaltyApplied = false;
            _localFakeDeadStartedTicks = 0;
            _localDeadCineCreateAfterTicks = 0;
            _nextDownedStateSendTicks = 0;
            _nextReviveAttemptTicks = 0;
            _localDownedLevelId = string.Empty;
            _hasLocalDownedAnchor = false;
            _localDownedAnchorX = 0;
            _localDownedAnchorY = 0;
            _localDownedUsesRecoveryAnchor = false;
            StopLocalDeadCine();
            RestoreLocalHeroPhysics(hero);

            var reviveX = _localDownedX;
            var reviveY = _localDownedY - LocalReviveBodyYOffsetPx;
            SnapHeroToDownedPosition(hero, reviveX, reviveY);
            try
            {
                _postReviveLockX = hero.get_targetSprPosX();
                _postReviveLockY = hero.get_targetSprPosY();
            }
            catch
            {
                _postReviveLockX = reviveX;
                _postReviveLockY = reviveY;
            }
            _postReviveLockUntilTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * PostRevivePositionLockSeconds);
            _localHeldX = _postReviveLockX;
            _localHeldY = _postReviveLockY;

            try { hero.cancelVelocities(); } catch { }
            try { hero.cancelSkillControlLock(); } catch { }
            try { hero.unlockControls(); } catch { }
            try { hero._targetable = true; } catch { }
            global::DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.NotifyPlayerCombatStateChanged("local-player-revived");

            try
            {
                var currentLife = hero.life;
                var maxLife = hero.maxLife;
                var targetLife = System.Math.Max(1, (int)System.Math.Ceiling(maxLife * 0.5));
                var healAmount = targetLife - currentLife;
                if (healAmount > 0)
                    hero.heal(healAmount);
                if (hero.life < targetLife)
                    hero.life = targetLife;
            }
            catch
            {
                try { hero.fullHeal(); } catch { }
            }

            SendLocalDownedState(net, isDowned: false, force: true);
        }

        private void ApplyLocalDownedExitPenaltyIfNeededCore()
        {
            if (!_localFakeDead || _localExitPenaltyApplied || me == null)
                return;

            _localExitPenaltyApplied = true;
            var hero = me;

            try { hero.spdComboKills = 0; } catch { }
            try { hero.perfectKillsCount = 0; } catch { }
            try { hero.goldCombo = 0; } catch { }

            try
            {
                var data = hero._level?.game?.data;
                if (data != null)
                {
                    data.killCount = 0;
                    data.corruptedHealingKillCount = 0;
                }
            }
            catch
            {
            }

            try
            {
                bool noStats = true;
                hero.tryToSubstractMoney(int.MaxValue, Ref<bool>.From(ref noStats));
            }
            catch
            {
                try
                {
                    var data = hero._level?.game?.data;
                    if (data != null)
                        data.money = 0;
                    hero.hudSetMoney(0);
                }
                catch
                {
                }
            }

            try
            {
                var inventory = hero.inventory;
                if (inventory != null)
                {
                    inventory.removeAll("BrutalityUp".AsHaxeString());
                    inventory.removeAll("SurvivalUp".AsHaxeString());
                    inventory.removeAll("TacticUp".AsHaxeString());
                }
            }
            catch
            {
            }

            try { hero.computeTiers(); } catch { }

            try
            {
                var data = hero._level?.game?.data;
                if (data != null)
                {
                    data.money = 0;
                    data.brutalityTier = hero.brutalityTier;
                    data.survivalTier = hero.survivalTier;
                    data.tacticTier = hero.tacticTier;
                }
            }
            catch
            {
            }
        }

        private void ProcessReviveHold(NetNode net)
        {
            if (me == null || _remoteDowned.Count == 0)
            {
                ResetReviveHold();
                ClearReviveHints();
                return;
            }

            var isHoldPressed = ReviveInput.IsReviveHoldInputDown(me);

            if (!isHoldPressed)
            {
                ResetReviveHold();
                return;
            }

            var nearest = FindNearestReviveTarget();
            if (nearest == null)
            {
                ResetReviveHold();
                return;
            }

            ShowReviveHintFor(nearest.UserId);
            var now = Stopwatch.GetTimestamp();
            var holdTicks = (long)(Stopwatch.Frequency * ReviveHoldSeconds);

            if (_reviveHoldTargetId != nearest.UserId)
            {
                _reviveHoldTargetId = nearest.UserId;
                _reviveHoldStartedTicks = now;
                return;
            }

            if (_reviveHoldStartedTicks == 0)
                _reviveHoldStartedTicks = now;

            if (now - _reviveHoldStartedTicks < holdTicks)
                return;

            if (_nextReviveAttemptTicks != 0 && now < _nextReviveAttemptTicks)
                return;

            if (!TryConsumeOneFlask(me))
            {
                ResetReviveHold();
                return;
            }

            net.SendPlayerReviveRequest(nearest.UserId);
            _nextReviveAttemptTicks = now + (long)(Stopwatch.Frequency * ReviveAttemptCooldownSeconds);
            ResetReviveHold();
            ClearReviveHints();
        }

        private void UpdateReviveHintsByProximity()
        {
            if (me == null || _remoteDowned.Count == 0)
            {
                ClearReviveHints();
                return;
            }

            var nearest = FindNearestReviveTarget();
            if (nearest == null)
            {
                ClearReviveHints();
                return;
            }

            ShowReviveHintFor(nearest.UserId);
        }

        private RemoteDownedState? FindNearestReviveTarget()
        {
            if (me == null || _remoteDowned.Count == 0)
                return null;

            var localLevelId = GetCurrentLevelId();
            RemoteDownedState? nearest = null;
            var x = me.spr?.x ?? 0;
            var y = me.spr?.y ?? 0;
            var bestDistSq = double.MaxValue;

            foreach (var state in _remoteDowned.Values)
            {
                if (state == null || state.UserId <= 0)
                    continue;

                if (!string.IsNullOrEmpty(localLevelId) &&
                    !string.IsNullOrEmpty(state.LevelId) &&
                    !string.Equals(state.LevelId, localLevelId, StringComparison.Ordinal))
                {
                    continue;
                }

                var dx = state.X - x;
                var dy = state.Y - y;
                var distSq = dx * dx + dy * dy;
                if (distSq > ReviveUseDistancePx * ReviveUseDistancePx)
                    continue;

                if (state.HasHeadPosition)
                {
                    var hdx = state.HeadX - state.X;
                    var hdy = state.HeadY - state.Y;
                    var headBodyDistSq = hdx * hdx + hdy * hdy;
                    var maxHeadBodySq = ReviveHomunculusBodyMaxDistancePx * ReviveHomunculusBodyMaxDistancePx * 16.0;
                    if (headBodyDistSq > maxHeadBodySq)
                        continue;
                }

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = state;
                }
            }

            return nearest;
        }

        private void ResetReviveHold()
        {
            _reviveHoldTargetId = 0;
            _reviveHoldStartedTicks = 0;
        }

        private bool TryConsumeOneFlask(Hero hero)
        {
            if (hero == null)
                return false;

            try
            {
                var manager = hero.mainSkillsManager;
                if (manager == null)
                    return false;

                var heal = manager.getMainSkill(Heal.Class) as Heal;
                if (heal == null)
                    return false;

                var current = heal.get_healings();
                if (current <= 0)
                    return false;

                var next = current - 1;
                if (next < 0)
                    next = 0;
                heal.set_healings(next);
                heal.setFlaskGlow();

                try
                {
                    var max = heal.get_maxHealings();
                    var hud = dc.ui.HUD.Class.ME;
                    hud?.setHealings(heal.get_healings(), max);
                }
                catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SendLocalDownedState(NetNode net, bool isDowned, bool force)
        {
            if (net == null || net.id <= 0)
                return;

            double? headX = null;
            double? headY = null;
            string? headAnim = null;
            if (isDowned && _localDeadCine != null && _localDeadCine.TryGetHomunculusPixelPosition(out var hx, out var hy))
            {
                headX = hx;
                headY = hy;
                _localDeadCine.TryGetHomunculusAnim(out headAnim);
            }

            var now = Stopwatch.GetTimestamp();
            var resend = (long)(Stopwatch.Frequency * DownedStateResendSeconds);
            if (isDowned && headX.HasValue && headY.HasValue)
            {
                var fastResend = (long)(Stopwatch.Frequency * DownedHeadStateResendSeconds);
                if (fastResend > 0 && (resend <= 0 || fastResend < resend))
                    resend = fastResend;
            }
            if (!force && _nextDownedStateSendTicks != 0 && now < _nextDownedStateSendTicks)
                return;

            var level = isDowned
                ? (!string.IsNullOrWhiteSpace(_localDownedLevelId) ? _localDownedLevelId : GetCurrentLevelId())
                : GetCurrentLevelId();
            var x = isDowned ? _localDownedX : (me?.spr?.x ?? _localDownedX);
            var y = isDowned ? _localDownedY : (me?.spr?.y ?? _localDownedY);

            net.SendPlayerDownState(isDowned, x, y, level, headX, headY, headAnim);
            _nextDownedStateSendTicks = now + resend;
        }

        private string GetCurrentLevelId()
        {
            try
            {
                var currentLevelId = me?._level?.map?.id?.ToString();
                if (!string.IsNullOrWhiteSpace(currentLevelId))
                    return currentLevelId.Trim();
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(levelId))
                return levelId.Trim();

            return string.Empty;
        }

        private void ResetFakeDeathState(
            bool unlockLocalHero,
            bool sendNetworkUpState,
            bool clearRemoteDownedTracking = true,
            bool clearDownedAnnouncements = true)
        {
            ResetAllDownedGameOverState();
            var wasFakeDead = _localFakeDead;
            var hadRemoteDowned = _remoteDowned.Count > 0;
            _localFakeDead = false;
            _localDeathConversionInProgress = false;
            _fakeDeathFlowInProgress = false;
            _localExitPenaltyApplied = false;
            _localFakeDeadStartedTicks = 0;
            _localDeadCineCreateAfterTicks = 0;
            StopLocalDeadCine();
            _localDownedX = 0;
            _localDownedY = 0;
            _localHeldX = 0;
            _localHeldY = 0;
            _localDownedLevelId = string.Empty;
            _nextReviveAttemptTicks = 0;
            _nextDownedStateSendTicks = 0;
            _postReviveLockUntilTicks = 0;
            _postReviveLockX = 0;
            _postReviveLockY = 0;
            _hasLocalDownedAnchor = false;
            _localDownedAnchorX = 0;
            _localDownedAnchorY = 0;
            _localDownedUsesRecoveryAnchor = false;
            _lastLocalDamageWasEnvironmental = false;
            _lastLocalDamageContextTicks = 0;
            RestoreLocalHeroPhysics(me);
            ResetSafeReviveAnchorHistory(string.Empty);
            ResetReviveHold();
            ClearReviveHints();
            if (clearRemoteDownedTracking)
                _remoteDowned.Clear();
            _remoteReviveVisibilityGraceUntilTicks.Clear();
            if (clearDownedAnnouncements)
                _downedAnnouncements.Clear();
            DisposeAllRemoteDownedCines();
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client != null)
                {
                    try { client._targetable = true; } catch { }
                }
            }

            if (unlockLocalHero && me != null)
            {
                try { me.cancelSkillControlLock(); } catch { }
                try { me.unlockControls(); } catch { }
                try { me._targetable = true; } catch { }
            }

            if (sendNetworkUpState && wasFakeDead && _net != null && _netRole != NetRole.None)
            {
                try { _net.SendPlayerDownState(false, me?.spr?.x ?? 0, me?.spr?.y ?? 0, GetCurrentLevelId()); } catch { }
            }

            if (wasFakeDead || hadRemoteDowned)
                global::DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.NotifyPlayerCombatStateChanged("fake-death-reset");
        }

        private void HandleAllPlayersDowned(NetNode net)
        {
            if (me == null || net == null)
                return;

            try
            {
                if (me.life <= 0)
                    me.life = 1;
            }
            catch
            {
            }

            var now = Stopwatch.GetTimestamp();

            // Do not create a new death cinematic after the last living player falls. At this point
            // each process may already own either the local or remote corpse cinematic. Keeping the
            // all-down transition lightweight avoids two GameCinematic objects fighting over curCine
            // while the host is scheduling a synchronized run restart.
            if (!_allDownedGameOverShown)
            {
                // Creating vanilla GameOver while both heroes are fake-dead can synchronously pause
                // the game/cinematic scheduler and leave the client permanently Not Responding.
                // Keep the presentation lightweight; the authoritative host queues the restart.
                try { MultiplayerUI.PushSystemMessage("All players are down."); } catch { }
                _allDownedGameOverShown = true;
                _allDownedRestartAtTicks = now + (long)(Stopwatch.Frequency * AllDownedGameOverDelaySeconds);
            }

            CaptureAndFreezeLocalHeroPhysics(me);
            try { me.lockControlsS(0.25); } catch { }
            try { me.cancelSkillControlLock(); } catch { }
            try { me._targetable = false; } catch { }

            var cine = _localDeadCine;
            if (cine != null && cine.TryGetCorpsePixelPosition(out var corpseX, out var corpseY))
            {
                TryUpdateDownedPositionFromCorpse(corpseX, corpseY);
            }
            SnapHeroToDownedPosition(me, _localHeldX, _localHeldY, clampToGround: true);
            SendLocalDownedState(net, isDowned: true, force: false);

            if (_allDownedRestartQueued || _netRole != NetRole.Host)
                return;

            if (_allDownedRestartAtTicks != 0 && now < _allDownedRestartAtTicks)
                return;

            _allDownedRestartQueued = true;
            LobbySession.QueueHostRestartFromDeath("all_players_downed");
        }

        private void ShowAllDownedGameOverLogo()
        {
            try
            {
                if (dc.ui.Console.Class.ME != null &&
                    dc.ui.Console.Class.ME.flags.exists(dc.ui.Console.Class.HIDE_UI))
                {
                    return;
                }
            }
            catch
            {
            }

            try
            {
                var existing = GameOver.Class.ME;
                if (existing != null)
                    return;
            }
            catch
            {
            }

            try
            {
                _ = new GameOver(Localize("Game Over").AsHaxeString(), true, null);
            }
            catch
            {
            }
        }

        private static string Localize(string message)
        {
            return GetText.Instance.GetString(message);
        }

        private static string FormatLocalized(string format, params object[] args)
        {
            var localizedFormat = Localize(format);
            try
            {
                return string.Format(CultureInfo.InvariantCulture, localizedFormat, args);
            }
            catch
            {
                return string.Format(CultureInfo.InvariantCulture, format, args);
            }
        }

        private void ResetAllDownedGameOverState()
        {
            _allDownedGameOverShown = false;
            _allDownedRestartQueued = false;
            _allDownedRestartAtTicks = 0;
        }

        private static void EnsureHeroVisibilityAfterRoomChange(Hero? hero)
        {
            if (hero == null)
                return;

            try
            {
                if (ModEntry.IsLocalPlayerDowned())
                    return;
            }
            catch
            {
            }

            try { hero.visible = true; } catch { }
            try
            {
                var head = hero.heroHead;
                if (head == null)
                    return;

                try { head.customHeadSpr?.set_visible(true); } catch { }
                try { head.customBackSpr?.set_visible(true); } catch { }
                try { head.headNormalSb?.set_visible(true); } catch { }
                try { head.headAddSb?.set_visible(true); } catch { }
                try { head.eye?.set_visible(true); } catch { }
            }
            catch
            {
            }
        }

        internal static void ResetDownedPlayersForRestart()
        {
            var instance = Instance;
            if (instance == null)
                return;

            try
            {
                // Broadcast the revived (not-downed) state on restart so the peer clears its stale
                // remote-downed tracking; otherwise the host keeps pinning this player as a corpse
                // and gates interactions (e.g. exit doors) as if still downed.
                instance.ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: true);
            }
            catch
            {
            }
        }
    }
}


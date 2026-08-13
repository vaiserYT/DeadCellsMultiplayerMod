using System.Diagnostics;
using System.Reflection;
using dc.en;
using dc.pr;
using ModCore.Utilities;
using dc.tool;
using HaxeProxy.Runtime;
using HaxeProxy.Runtime.Internals;
using HaxeProxy.Runtime.Internals.Cache;
using DeadCellsMultiplayerMod.Ghost.GhostBase;
using DeadCellsMultiplayerMod.KingHead;
using DeadCellsMultiplayerMod.Tools;

namespace DeadCellsMultiplayerMod
{
    public partial class ModEntry
    {
        private long _ignoreRemoteCombatUntilTicks;

        private bool IsRemoteCombatGraceActive()
        {
            return _ignoreRemoteCombatUntilTicks != 0 &&
                   Stopwatch.GetTimestamp() < _ignoreRemoteCombatUntilTicks;
        }

        private void UpdateGhostHeads()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            var main = dc.Main.Class.ME;
            if (main == null || main.user == null)
            {
                return;
            }
            var ftime = dc.pr.Game.Class.ME.ftime;
            var now = Stopwatch.GetTimestamp();
            var activeClients = 0;
            var recreatedHeads = 0;
            var updatedHeadFx = 0;
            var throttledHeads = 0;
            for (int i = 0; i < clientHeads.Length; i++)
            {
                var client = clients[i];
                if (client == null)
                {
                    pendingClientHeadRecreate[i] = false;
                    ResetGhostHeadRuntimeState(i);
                    continue;
                }
                activeClients++;

                var attemptedRecreate = false;
                if (pendingClientHeadRecreate[i] && now >= clientNextHeadRecreateTick[i])
                {
                    attemptedRecreate = true;
                    var recreateStart = RuntimeHitchWatch.Start();
                    RecreateClientHead(i);
                    if (clientHeads[i] != null)
                        recreatedHeads++;
                    if (perfEnabled)
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.UpdateGhostHeads.RecreateClientHead",
                            recreateStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"slot={i} remoteId={clientIds[i]} pending={CountPendingClientHeadRecreate()}"));
                }

                var head = clientHeads[i];
                if (head == null)
                {
                    var hasKnownHead = !string.IsNullOrWhiteSpace(client.RemoteHeadSkinId) ||
                                       !string.IsNullOrWhiteSpace(clientHeadSkins[i]);
                    if (!attemptedRecreate &&
                        (pendingClientHeadRecreate[i] || hasKnownHead) &&
                        now >= clientNextHeadRecreateTick[i])
                    {
                        var recreateStart = RuntimeHitchWatch.Start();
                        RecreateClientHead(i);
                        if (clientHeads[i] != null)
                            recreatedHeads++;
                        if (perfEnabled)
                            LogGhostRuntimeStepIfSlow(
                                "ModEntry.UpdateGhostHeads.RecreateClientHead",
                                recreateStart,
                                string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"slot={i} remoteId={clientIds[i]} pending={CountPendingClientHeadRecreate()}"));
                    }
                    continue;
                }

                if (!ShouldUpdateGhostHead(i, client, now))
                {
                    throttledHeads++;
                    continue;
                }

                var fxStart = RuntimeHitchWatch.Start();
                head.updateHeadFx(ftime);
                clientHeadDirty[i] = false;
                clientNextHeadFxTick[i] = IsGhostHeadHighPriority(client)
                    ? 0
                    : now + (long)(Stopwatch.Frequency * GhostHeadDormantUpdateSeconds);
                updatedHeadFx++;
                if (perfEnabled)
                    LogGhostRuntimeStepIfSlow(
                        "ModEntry.UpdateGhostHeads.HeadFx",
                        fxStart,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"slot={i} remoteId={clientIds[i]}"));
            }

            var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
            if (perfEnabled && hitchMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Logger,
                    "ModEntry.UpdateGhostHeads",
                    hitchMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"activeClients={activeClients} updatedHeadFx={updatedHeadFx} recreatedHeads={recreatedHeads} throttledHeads={throttledHeads} pendingRecreate={CountPendingClientHeadRecreate()}"));
            }
        }

        private static void ResetGhostHeadRuntimeState(int slot)
        {
            if (slot < 0 || slot >= clientHeadDirty.Length)
                return;

            clientHeadDirty[slot] = false;
            clientNextHeadFxTick[slot] = 0;
            clientNextHeadRecreateTick[slot] = 0;
        }

        private static void MarkGhostHeadDirty(int slot, bool immediate)
        {
            if (slot < 0 || slot >= clientHeadDirty.Length)
                return;

            clientHeadDirty[slot] = true;
            if (immediate)
                clientNextHeadFxTick[slot] = 0;
        }

        private void ScheduleGhostHeadRecreate(int slot, bool immediate)
        {
            if (slot < 0 || slot >= pendingClientHeadRecreate.Length)
                return;

            pendingClientHeadRecreate[slot] = true;
            clientNextHeadRecreateTick[slot] = immediate
                ? 0
                : Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * GhostHeadRecreateRetrySeconds);
            MarkGhostHeadDirty(slot, immediate: true);
        }

        private static bool IsGhostHeadHighPriority(GhostKing client)
        {
            if (client == null)
                return false;

            try
            {
                return client.visible && client.isOnScreen && !client.isOutOfGame;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldUpdateGhostHead(int slot, GhostKing client, long now)
        {
            if (slot < 0 || slot >= clientHeadDirty.Length)
                return false;

            if (IsGhostHeadHighPriority(client))
                return true;

            return clientHeadDirty[slot] || now >= clientNextHeadFxTick[slot];
        }



        private void SendLevel(string lvl)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null) return;

            int senderId = net.id;
            if (senderId <= 0) return;
            net.LevelSend(senderId, lvl);
        }

        private void SendRoomTarget(string? targetLevelId, int targetRoomId, bool force)
        {
            if (_netRole == NetRole.None)
                return;

            var net = _net;
            if (net == null || net.id <= 0)
                return;
            if (targetRoomId < 0)
                return;

            var effectiveLevelId = string.IsNullOrWhiteSpace(targetLevelId)
                ? GetCurrentLevelId()
                : targetLevelId.Trim();
            if (string.IsNullOrWhiteSpace(effectiveLevelId))
                return;

            if (!force &&
                string.Equals(_lastDoorMarkerLevelId, effectiveLevelId, StringComparison.Ordinal) &&
                _lastDoorMarkerToken == targetRoomId)
            {
                return;
            }

            net.SendRoomTarget(effectiveLevelId, targetRoomId);
            _lastDoorMarkerLevelId = effectiveLevelId;
            _lastDoorMarkerToken = targetRoomId;
        }

        private void SendCurrentRoomTarget(bool force)
        {
            if (!TryGetCurrentVisibilityContext(out var targetLevelId, out var branchToken))
                return;

            RegisterLocalDoorMarker(targetLevelId, branchToken);
            SendRoomTarget(targetLevelId, branchToken, force);
        }

        private bool TryGetCurrentVisibilityContext(out string levelContextId, out int branchToken)
        {
            levelContextId = GetCurrentLevelId();
            branchToken = 0;

            Level? currentLevel = me?._level;
            if (currentLevel == null)
                currentLevel = game?.curLevel;

            if (currentLevel == null)
                return !string.IsNullOrWhiteSpace(levelContextId);

            var liveLevelId = currentLevel.map?.id?.ToString();
            if (!string.IsNullOrWhiteSpace(liveLevelId))
                levelContextId = liveLevelId.Trim();

            if (string.IsNullOrWhiteSpace(levelContextId))
                return false;

            branchToken = ComputeLevelBranchToken(currentLevel, levelContextId);
            return branchToken >= 0;
        }

        private int ComputeLevelBranchToken(Level currentLevel, string levelContextId)
        {
            try
            {
                if (!currentLevel.isSubLevel)
                    return 0;
            }
            catch
            {
                return 0;
            }

            unchecked
            {
                try
                {
                    var ownerGame = currentLevel.game ?? game;
                    var subLevels = ownerGame?.subLevels;
                    if (subLevels == null)
                        return ComputeStablePositiveToken($"SUB|{levelContextId}");

                    int targetUid;
                    try
                    {
                        targetUid = currentLevel.__uid;
                    }
                    catch
                    {
                        return ComputeStablePositiveToken($"SUB|{levelContextId}");
                    }

                    for (int i = 0; i < subLevels.length; i++)
                    {
                        try
                        {
                            if (subLevels.getDyn(i) is not Level candidate)
                                continue;

                            if (ReferenceEquals(candidate, currentLevel))
                                return i + 1;

                            if (candidate.__uid == targetUid)
                                return i + 1;
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }
                catch
                {
                    return ComputeStablePositiveToken($"SUB|{levelContextId}");
                }

                return ComputeStablePositiveToken($"SUB|{levelContextId}");
            }
        }

        private static int ComputeStablePositiveToken(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return 0;

            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < key.Length; i++)
                {
                    hash ^= key[i];
                    hash *= 16777619;
                }

                var positive = (int)(hash & 0x7FFFFFFF);
                return positive == 0 ? 1 : positive;
            }
        }

        private void RegisterLocalDoorMarker(string? levelId, int markerToken)
        {
            if (markerToken < 0)
                return;

            _localLastDoorMarkerLevelId = string.IsNullOrWhiteSpace(levelId)
                ? string.Empty
                : levelId.Trim();
            _localLastDoorMarkerToken = markerToken;
        }

        double last_x, last_y;
        int lastDir;

        private void ResetLocalHeroPositionSendCache()
        {
            last_x = 0;
            last_y = 0;
            lastDir = 0;
        }

        private void SendHeroCoords()
        {
            if (_netRole == NetRole.None) return;
            if (_net == null || me == null) return;
            int dir = me.dir;

            // Always send X/Y/dir. Skipping unchanged frames let peer GhostKing physics drift
            // the remote Y while the local player stood still (no correction packets).
            var positionLevelId = me._level?.map?.id?.ToString() ?? GetCurrentLevelId();
            _net.TickSend(me.spr.x, me.spr.y, dir, positionLevelId);
            last_x = me.spr.x;
            last_y = me.spr.y;
            lastDir = dir;
        }

        public static double[] rLastX = new double[NetNode.MaxClientSlots];
        public static double[] rLastY = new double[NetNode.MaxClientSlots];

        internal static bool TryGetClientIndex(int localId, int remoteId, out int index)
        {
            index = -1;
            if (localId <= 0 || remoteId <= 0 || remoteId == localId)
                return false;

            var mapped = remoteId < localId ? remoteId - 1 : remoteId - 2;
            if (mapped < 0 || mapped >= clients.Length)
                return false;

            index = mapped;
            return true;
        }

        internal static void SetClientSkin(int remoteId, string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var cleaned = NormalizeSkin(skin, "PrisonerDefault");
            var prev = clientSkins[index];
            clientSkins[index] = cleaned;

            var client = clients[index];
            if (client != null && !IsRemoteKingTransitionActive)
            {
                if (!string.Equals(prev, cleaned, StringComparison.Ordinal) || client.spr == null)
                    client.ApplyRemoteSkin(cleaned);
            }
        }

        internal static void SetClientHeadSkin(int remoteId, string? skin)
        {
            var instance = Instance;
            if (instance == null)
                return;

            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var cleaned = NormalizeSkin(skin, "BaseFlame");
            var prev = clientHeadSkins[index];
            clientHeadSkins[index] = cleaned;

            var client = clients[index];
            if (client != null)
                client.RemoteHeadSkinId = cleaned;

            if (!IsRemoteKingTransitionActive &&
                (!string.Equals(prev, cleaned, StringComparison.Ordinal) || client?.head == null))
                instance.ScheduleGhostHeadRecreate(index, immediate: true);
        }

        private static string NormalizeSkin(string? skin, string defaultSkin)
        {
            return string.IsNullOrWhiteSpace(skin) ? defaultSkin : skin.Replace("|", "/").Trim();
        }

        private void RecreateClientHead(int slot)
        {
            if (IsRemoteKingTransitionActive)
                return;

            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            if (slot < 0 || slot >= clients.Length)
                return;

            var client = clients[slot];
            var localHero = me ?? ModCore.Modules.Game.Instance?.HeroInstance;
            var localLevel = localHero?._level;
            if (client == null || localHero == null || localLevel == null || client.spr == null)
            {
                ScheduleGhostHeadRecreate(slot, immediate: false);
                return;
            }

            var existing = clientHeads[slot];
            var hadExisting = existing != null;
            if (existing != null)
            {
                existing.dispose();
                clientHeads[slot] = null;
            }

            var desiredHead = NormalizeSkin(client.RemoteHeadSkinId, "BaseFlame");
            var previousGlobalHead = remoteHeadSkin;
            remoteHeadSkin = desiredHead;
            try
            {
                bool fromUI = false;
                var attachRoot = new dc.h2d.Object(client.spr);
                var newHead = new Kinghead(localHero, client, localLevel);
                newHead.init(localLevel, attachRoot, Ref<bool>.From(ref fromUI));
                clientHeads[slot] = newHead;
                client.head = newHead;
                pendingClientHeadRecreate[slot] = false;
                clientNextHeadRecreateTick[slot] = 0;
                MarkGhostHeadDirty(slot, immediate: true);
            }
            finally
            {
                remoteHeadSkin = previousGlobalHead;
            }

            if (perfEnabled)
                LogGhostRuntimeStepIfSlow(
                    "ModEntry.RecreateClientHead",
                    hitchStart,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"slot={slot} remoteId={clientIds[slot]} hadExisting={(hadExisting ? 1 : 0)} desiredHead={desiredHead}"));
        }

        private void ReceiveGhostCoords()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            var net = _net;
            var ghost = _ghost;
            if (net == null || me == null || ghost == null) return;

            // Buffer snapshots while native code replaces or resumes the display tree. Creating a
            // GhostKing in this window can attach it to the outgoing level and break the return path.
            if (IsRemoteKingTransitionActive || IsRemoteKingCreationBlocked)
                return;

            if (!net.TryConsumeRemoteSnapshot(out var remotes))
                return;

            try
            {
                var localId = net.id;
                var localLevelId = GetCurrentLevelId();
                if (string.IsNullOrWhiteSpace(localLevelId))
                    localLevelId = me._level?.map?.id?.ToString() ?? string.Empty;

                var createdSlots = 0;
                var updatedLabels = 0;
                var playedAnims = 0;
                var playedHeadAnims = 0;
                var disposedSlots = 0;

                foreach (var remote in remotes)
                {
                    var remoteStart = RuntimeHitchWatch.Start();
                    if (!TryGetClientIndex(localId, remote.Id, out var index))
                        continue;

                    remotePlayerId = remote.Id;
                    clientIds[index] = remote.Id;
                    ProcessRemoteDoorMarker(remote);
                    if (!ShouldKeepRemoteKingVisibleInRoom(remote, localLevelId))
                    {
                        QueueClientDisposeWithTransition(index);
                        disposedSlots++;
                        if (perfEnabled)
                            LogGhostRuntimeStepIfSlow(
                                "ModEntry.ReceiveGhostCoords.Remote",
                                remoteStart,
                                string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"remoteId={remote.Id} slot={index} disposed=1 anim={(remote.HasAnim ? 1 : 0)} headAnim={(remote.HasHeadAnim ? 1 : 0)}"));
                        continue;
                    }

                    CancelPendingClientDispose(index);

                    var hadClientBefore = clients[index] != null;
                    var client = EnsureClientKingSlot(index);
                    if (client == null)
                        continue;
                    if (!hadClientBefore)
                    {
                        createdSlots++;
                        MarkGhostHeadDirty(index, immediate: true);
                    }

                    var drawX = remote.X;
                    var drawY = remote.Y - 0.2d;
                    var useDownedOffset = false;
                    var headDirty = !hadClientBefore;
                    if (_remoteDowned.TryGetValue(remote.Id, out var downed))
                    {
                        var currentLevelId = GetCurrentLevelId();
                        if (string.IsNullOrEmpty(currentLevelId) ||
                            string.IsNullOrEmpty(downed.LevelId) ||
                            string.Equals(currentLevelId, downed.LevelId, StringComparison.Ordinal))
                        {
                            drawX = downed.X;
                            drawY = downed.Y;
                            useDownedOffset = true;
                            if (_remoteDownedCines.TryGetValue(remote.Id, out var downedCine) &&
                                downedCine != null &&
                                !downedCine.destroyed)
                            {
                                downedCine.UpdateTarget(drawX, drawY, remote.Dir);
                            }
                        }
                    }

                    if (useDownedOffset)
                        drawY -= DownedGhostBodyYOffsetPx;

                    var wasUsingDownedOffset = clientLastDownedOffsets[index];
                    if (wasUsingDownedOffset != useDownedOffset)
                    {
                        client._targetable = !useDownedOffset;
                        clientLastDownedOffsets[index] = useDownedOffset;
                        headDirty = true;
                    }

                    if (!useDownedOffset &&
                        (wasUsingDownedOffset || IsRemoteReviveVisibilityGraceActive(remote.Id)))
                    {
                        RestoreRemoteKingRenderAfterRevive(
                            index,
                            client,
                            drawX,
                            drawY,
                            wasUsingDownedOffset ? "snapshot-transition" : "snapshot-grace");
                    }

                    // Always re-apply remote Y (and X). GhostKing physics can drift between
                    // snapshots; skipping unchanged coords left peers floating/sinking.
                    var posChanged = rLastX[index] != drawX || rLastY[index] != drawY;
                    client.setPosPixel(drawX, drawY);
                    rLastX[index] = drawX;
                    rLastY[index] = drawY;
                    if (posChanged)
                        headDirty = true;

                    if (clientLastDirs[index] != remote.Dir)
                    {
                        client.dir = remote.Dir;
                        clientLastDirs[index] = remote.Dir;
                        headDirty = true;
                    }

                    var newLabel = BuildRemoteLabel(remote.Id, remote.Username);
                    if (!string.Equals(clientLabels[index], newLabel, StringComparison.Ordinal))
                    {
                        var labelStart = RuntimeHitchWatch.Start();
                        ghost.SetLabel(client, newLabel);
                        clientLabels[index] = newLabel;
                        updatedLabels++;
                        if (perfEnabled)
                            LogGhostRuntimeStepIfSlow(
                                "ModEntry.ReceiveGhostCoords.SetLabel",
                                labelStart,
                                string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"remoteId={remote.Id} slot={index} label={newLabel}"));
                    }

                    if (remote.HasAnim &&
                        !string.IsNullOrWhiteSpace(remote.Anim) &&
                        (!string.Equals(clientLastBodyAnims[index], remote.Anim, StringComparison.Ordinal) ||
                         clientLastBodyAnimQueues[index] != remote.AnimQueue ||
                         clientLastBodyAnimGs[index] != remote.AnimG))
                    {
                        var animStart = RuntimeHitchWatch.Start();
                        PlayGhostAnim(client, remote.Anim!, remote.AnimQueue, remote.AnimG);
                        clientLastBodyAnims[index] = remote.Anim;
                        clientLastBodyAnimQueues[index] = remote.AnimQueue;
                        clientLastBodyAnimGs[index] = remote.AnimG;
                        playedAnims++;
                        headDirty = true;
                        if (perfEnabled)
                            LogGhostRuntimeStepIfSlow(
                                "ModEntry.ReceiveGhostCoords.PlayGhostAnim",
                                animStart,
                                string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"remoteId={remote.Id} slot={index} anim={remote.Anim}"));
                    }
                    if (remote.HasHeadAnim &&
                        !string.IsNullOrWhiteSpace(remote.HeadAnim) &&
                        !string.Equals(clientLastHeadAnims[index], remote.HeadAnim, StringComparison.Ordinal))
                    {
                        var headAnimStart = RuntimeHitchWatch.Start();
                        PlayGhostHeadAnim(client, remote.HeadAnim);
                        clientLastHeadAnims[index] = remote.HeadAnim;
                        playedHeadAnims++;
                        headDirty = true;
                        if (perfEnabled)
                            LogGhostRuntimeStepIfSlow(
                                "ModEntry.ReceiveGhostCoords.PlayGhostHeadAnim",
                                headAnimStart,
                                string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"remoteId={remote.Id} slot={index} anim={remote.HeadAnim}"));
                    }

                    if (perfEnabled)
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.ReceiveGhostCoords.Remote",
                            remoteStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"remoteId={remote.Id} slot={index} created={(hadClientBefore ? 0 : 1)} downed={(useDownedOffset ? 1 : 0)} anim={(remote.HasAnim ? 1 : 0)} headAnim={(remote.HasHeadAnim ? 1 : 0)}"));

                    if (headDirty)
                        MarkGhostHeadDirty(index, immediate: true);
                }

                var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
                if (perfEnabled && hitchMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
                {
                    RuntimeHitchWatch.LogSlow(
                        Logger,
                        "ModEntry.ReceiveGhostCoords",
                        hitchMs,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"remotes={remotes.Count} createdSlots={createdSlots} updatedLabels={updatedLabels} playedAnims={playedAnims} playedHeadAnims={playedHeadAnims} disposedSlots={disposedSlots}"));
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(remotes);
            }
        }

        private bool ShouldKeepRemoteKingVisibleInRoom(NetNode.RemoteSnapshot remote, string localLevelId)
        {
            // A downed state carries an authoritative safe revive anchor. Pit/lava deaths can
            // briefly leave the normal room marker pointing at an invalid branch, so never dispose
            // the remote shell while that same-level corpse must remain visible and revivable.
            if (IsRemoteDownedVisibleInCurrentLevel(remote.Id, localLevelId))
                return true;

            // After revive, allow a short marker-settle window and force the existing shell visible.
            if (IsRemoteReviveVisibilityGraceActive(remote.Id))
                return true;

            if (!string.IsNullOrWhiteSpace(localLevelId) &&
                !string.IsNullOrWhiteSpace(remote.LevelId) &&
                !string.Equals(remote.LevelId, localLevelId, StringComparison.Ordinal))
            {
                return false;
            }

            // Room marker replication is noisy around Continue/LoadSave and level bootstrap and can
            // briefly diverge even when both players share the same map. Prefer level-only
            // visibility so a fresh GhostKing can spawn after continue instead of being disposed.
            return true;
        }

        private void ProcessRemoteDoorMarker(NetNode.RemoteSnapshot remote)
        {
            if (!remote.HasRoom ||
                !remote.RoomId.HasValue ||
                remote.RoomId.Value < 0 ||
                string.IsNullOrWhiteSpace(remote.RoomLevelId))
            {
                return;
            }

            var markerToken = remote.RoomId.Value;
            var markerLevelId = remote.RoomLevelId.Trim();
            if (string.IsNullOrWhiteSpace(markerLevelId))
                return;

            if (_remoteLastDoorMarkers.TryGetValue(remote.Id, out var last) &&
                last != null &&
                last.MarkerToken == markerToken &&
                string.Equals(last.LevelId, markerLevelId, StringComparison.Ordinal))
            {
                return;
            }

            _remoteLastDoorMarkers[remote.Id] = new RemoteDoorMarkerState
            {
                MarkerToken = markerToken,
                LevelId = markerLevelId,
                UpdatedAtTicks = Stopwatch.GetTimestamp()
            };

        }

        private void QueueClientDisposeWithTransition(int slot)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            var client = clients[slot];
            if (client == null)
            {
                DisposeClientSlot(slot, clearIdentity: false);
                return;
            }

            if (!_pendingClientDisposeTicks.TryGetValue(slot, out var startedAtTicks))
            {
                _pendingClientDisposeTicks[slot] = Stopwatch.GetTimestamp();
                client.spr?._animManager?.play("walkOut".AsHaxeString(), null, null);
                return;
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAtTicks).TotalSeconds;
            if (elapsed < ClientDisposeTransitionSeconds)
                return;

            DisposeClientSlot(slot, clearIdentity: false);
        }

        private void CancelPendingClientDispose(int slot)
        {
            _pendingClientDisposeTicks.Remove(slot);
        }

        private GhostKing? EnsureClientKingSlot(int slot)
        {
            var existingDuringTransition = slot >= 0 && slot < clients.Length ? clients[slot] : null;
            if (IsRemoteKingTransitionActive || IsRemoteKingCreationBlocked)
                return existingDuringTransition;

            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            if (slot < 0 || slot >= clients.Length)
                return null;

            var existing = clients[slot];
            if (existing != null)
                return existing;

            if (_ghost == null || me == null || me._level == null)
                return null;

            GhostKing created;
            try
            {
                created = _ghost.CreateGhostKing(me._level);
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    "[NetMod] Failed to create remote GhostKing slot={Slot} remoteId={RemoteId}: {Message}",
                    slot,
                    clientIds[slot],
                    ex.Message);
                return null;
            }

            clients[slot] = created;
            Logger.Information(
                "[NetMod] Created remote GhostKing slot={Slot} remoteId={RemoteId} level={LevelId}",
                slot,
                clientIds[slot],
                me._level.map?.id?.ToString() ?? "?");

            var knownSkin = clientSkins[slot];
            if (!string.IsNullOrWhiteSpace(knownSkin))
                created.ApplyRemoteSkin(knownSkin);

            var knownHead = clientHeadSkins[slot];
            created.RemoteHeadSkinId = NormalizeSkin(
                !string.IsNullOrWhiteSpace(knownHead) ? knownHead : remoteHeadSkin,
                "BaseFlame");
            RecreateClientHead(slot);
            MarkGhostHeadDirty(slot, immediate: true);

            if (!string.IsNullOrWhiteSpace(clientLabels[slot]))
                _ghost.SetLabel(created, clientLabels[slot]);

            ApplyCachedRemoteDiveSkillInfoIfAny(clientIds[slot], created);

            try
            {
                var net = LobbySession.NetRef;
                if (net != null && net.IsAlive && net.IsHost && clientIds[slot] > 0)
                {
                    global::DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization
                        .NotifyPlayerCombatStateChanged("remote-player-representation-recreated");
                }
            }
            catch
            {
            }

            if (perfEnabled)
                LogGhostRuntimeStepIfSlow(
                    "ModEntry.EnsureClientKingSlot",
                    hitchStart,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"slot={slot} remoteId={clientIds[slot]} created=1 skin={(string.IsNullOrWhiteSpace(knownSkin) ? 0 : 1)} head={(string.IsNullOrWhiteSpace(knownHead) ? 0 : 1)}"));

            return created;
        }

        private static MethodInfo? FindRuntimeParameterlessMethod(object process, string methodName)
        {
            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly;

            for (global::System.Type? type = process.GetType(); type != null; type = type.BaseType)
            {
                var method = type.GetMethod(
                    methodName,
                    flags,
                    binder: null,
                    types: global::System.Type.EmptyTypes,
                    modifiers: null);
                if (method != null)
                    return method;
            }

            return null;
        }

        private static bool TryDisposeRuntimeProcessImmediately(object? process)
        {
            if (process == null)
                return true;

            try
            {
                var destroy = FindRuntimeParameterlessMethod(process, "destroy");
                try { destroy?.Invoke(process, null); } catch { }

                var disposeImmediately =
                    FindRuntimeParameterlessMethod(process, "disposeImmediately");
                if (disposeImmediately == null)
                    return false;

                disposeImmediately.Invoke(process, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ObjFieldInfoCache _cachedProcessControllerField;
        private const int HomunculusEntityClassId = 17969;

        /// <summary>
        /// Assigns a live ControllerAccess to a mod-created Process (GameCinematic / ui.Process).
        /// Some native onDispose paths (and cines that assume Game.controller) read controller
        /// state during teardown. Vanilla process types install a controller during init();
        /// mod-created cines and UI processes may not, so giving the proxy a controller before
        /// dispose keeps those paths on the normal vanilla route.
        /// </summary>
        public static bool TryAssignProcessController(object? process, dc.pr.Game? game)
        {
            if (process == null || game == null)
                return false;

            try
            {
                var controller = game.controller;
                if (controller == null)
                    return false;

                HaxeProxyHelper.SetFieldById(
                    (HaxeProxyBase)(object)process,
                    controller,
                    "controller",
                    ref _cachedProcessControllerField);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ObjFieldInfoCache _cachedProcessDestroyedField;

        public static bool TryReadProcessDestroyed(object? process)
        {
            if (process == null)
                return false;

            try
            {
                var raw = HaxeProxyHelper.GetFieldById<object>(
                    (HaxeProxyBase)(object)process,
                    "destroyed",
                    ref _cachedProcessDestroyedField);
                if (raw is bool b)
                    return b;
                if (raw is int i)
                    return i != 0;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static ObjFieldInfoCache _cachedProcessControllerReadField;

        public static bool TryReadProcessControllerNull(object? process)
        {
            if (process == null)
                return true;

            try
            {
                var raw = HaxeProxyHelper.GetFieldById<object>(
                    (HaxeProxyBase)(object)process,
                    "controller",
                    ref _cachedProcessControllerReadField);
                return raw == null;
            }
            catch
            {
                return true;
            }
        }

        private static ObjFieldInfoCache _cachedProcessChildrenField;

        /// <summary>
        /// Homunculus.dispose always ends with `game.hero.controller.manualLock = false` with no
        /// null check. During Level.onDispose → runEntitiesGC that becomes
        /// `Null access .manualLock` whenever the live hero has no ControllerAccess (mid-run
        /// restart, half-inited puppet, or hero disposed earlier in the same GC pass). Dispose
        /// Homunculi first while we can still heal hero.controller, then strip them from the
        /// level collections so the native GC pass cannot hit the bad path.
        /// </summary>
        internal static void PrepareLevelProcessTeardown(Level? level, string context)
        {
            var game = level?.game ?? dc.pr.Game.Class.ME;
            try { LogProcessTeardownDiagnostics(context, game, level); } catch { }

            try { EnsureHeroControllerForTeardown(game); } catch { }
            try { SafeDisposeHomunculiForTeardown(level, game, context); } catch { }
        }

        private static void EnsureHeroControllerForTeardown(dc.pr.Game? game)
        {
            if (game == null)
                return;

            dc.en.Hero? hero = null;
            try { hero = game.hero; } catch { }
            if (hero == null)
                return;

            try
            {
                if (hero.controller != null)
                    return;
            }
            catch
            {
                return;
            }

            try
            {
                var bootController = dc.Boot.Class.ME?.controller;
                if (bootController == null)
                    return;

                hero.controller = bootController.createAccess("hero".AsHaxeString(), null);
                ModEntry.Instance?.Logger?.Warning(
                    "[ProcessTeardown] healed null hero.controller before level teardown");
            }
            catch (Exception ex)
            {
                ModEntry.Instance?.Logger?.Warning(
                    "[ProcessTeardown] failed to heal hero.controller: {Message}",
                    ex.Message);
            }
        }

        private static void SafeDisposeHomunculiForTeardown(
            Level? level,
            dc.pr.Game? game,
            string context)
        {
            if (level == null)
                return;

            var found = new HashSet<dc.en.Homunculus>();
            CollectHomunculi(level.entities, found);
            CollectHomunculi(level.qTreeEntities, found);
            CollectHomunculi(level.entitiesGC, found);
            CollectHomunculi(level.savedEntities, found);

            try
            {
                if (level.entitiesByClass?.get(HomunculusEntityClassId) is dc.hl.types.ArrayObj bucket)
                    CollectHomunculi(bucket, found);
            }
            catch
            {
            }

            if (found.Count == 0)
                return;

            EnsureHeroControllerForTeardown(game);

            var disposed = 0;
            foreach (var hom in found)
            {
                if (hom == null)
                    continue;

                try
                {
                    try { RemoveHomunculusFromLevelCollections(level, hom); } catch { }

                    try
                    {
                        if (!hom.destroyed)
                            hom.destroy();
                    }
                    catch
                    {
                    }

                    // Prefer native dispose only when hero.controller is safe; otherwise strip
                    // the entity without invoking the unconditional manualLock write.
                    var heroControllerSafe = false;
                    try { heroControllerSafe = game?.hero?.controller != null; } catch { }

                    if (heroControllerSafe)
                    {
                        try { hom.dispose(); } catch { }
                    }

                    disposed++;
                }
                catch
                {
                }
            }

            ModEntry.Instance?.Logger?.Warning(
                "[ProcessTeardown][{Context}] pre-disposed Homunculus count={Count}",
                context,
                disposed);
        }

        private static void CollectHomunculi(dc.hl.types.ArrayObj? entries, HashSet<dc.en.Homunculus> into)
        {
            if (entries == null)
                return;

            try
            {
                for (var i = 0; i < entries.length; i++)
                {
                    if (entries.getDyn(i) is dc.en.Homunculus hom)
                        into.Add(hom);
                }
            }
            catch
            {
            }
        }

        private static void RemoveHomunculusFromLevelCollections(Level level, dc.en.Homunculus hom)
        {
            try { level.entities?.remove(hom); } catch { }
            try { level.qTreeEntities?.remove(hom); } catch { }
            try { level.savedEntities?.remove(hom); } catch { }
            try { level.entitiesGC?.remove(hom); } catch { }

            try
            {
                if (level.entitiesByClass?.get(HomunculusEntityClassId) is dc.hl.types.ArrayObj bucket)
                    bucket.remove(hom);
            }
            catch
            {
            }
        }

        internal static void LogProcessTeardownDiagnostics(
            string context,
            dc.pr.Game? game,
            Level? level = null)
        {
            try
            {
                var log = ModEntry.Instance?.Logger;
                if (log == null)
                    return;

                var cine = game?.curCine;
                var hero = game?.hero;
                var heroControllerNull = true;
                try { heroControllerNull = hero?.controller == null; } catch { }

                var homunculusCount = 0;
                try
                {
                    if (level?.entitiesByClass?.get(HomunculusEntityClassId) is dc.hl.types.ArrayObj bucket)
                        homunculusCount = bucket.length;
                }
                catch
                {
                }

                log.Warning(
                    "[ProcessTeardown][{Context}] game={Game} gameControllerNull={GameCtrlNull} hero={Hero} heroControllerNull={HeroCtrlNull} homunculus={HomCount} curCine={Cine} curCineDestroyed={Destroyed} curCineControllerNull={ControllerNull}",
                    context,
                    game?.GetType().Name,
                    game?.controller == null,
                    hero?.GetType().Name,
                    heroControllerNull,
                    homunculusCount,
                    cine?.GetType().Name,
                    TryReadProcessDestroyed(cine),
                    TryReadProcessControllerNull(cine));

                var roots = dc.libs.Process.Class.ROOTS;
                if (roots == null)
                    return;

                var list = roots.array;
                var destroyedWithNullController = 0;
                if (list != null)
                {
                    for (int i = 0; i < list.Count; i++)
                        WalkProcessDiagnostics(list[i], 0, ref destroyedWithNullController);
                }

                log.Warning(
                    "[ProcessTeardown][{Context}] process-tree scan done; destroyedWithNullController={Count}",
                    context,
                    destroyedWithNullController);
            }
            catch
            {
            }
        }

        private static void WalkProcessDiagnostics(object? process, int depth, ref int destroyedCount)
        {
            if (process == null || depth > 12)
                return;

            try
            {
                if (TryReadProcessDestroyed(process) && TryReadProcessControllerNull(process))
                {
                    destroyedCount++;
                    ModEntry.Instance?.Logger?.Warning(
                        "[ProcessTeardown] destroyed+null-controller process depth={Depth} type={Type}",
                        depth,
                        process.GetType().FullName);
                }

                var childrenObj = HaxeProxyHelper.GetFieldById<object>(
                    (HaxeProxyBase)(object)process,
                    "children",
                    ref _cachedProcessChildrenField);
                if (childrenObj is not dc.hl.types.ArrayObj children)
                    return;

                var list = children.array;
                if (list == null)
                    return;

                for (int i = 0; i < list.Count; i++)
                    WalkProcessDiagnostics(list[i], depth + 1, ref destroyedCount);
            }
            catch
            {
            }
        }

        private void DisposeClientSlotForSubLevelTransition(int slot, bool clearIdentity)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            _pendingClientDisposeTicks.Remove(slot);

            var previousRemoteId = clientIds[slot];

            var head = clientHeads[slot];
            clientHeads[slot] = null;
            if (head != null && !TryDisposeRuntimeProcessImmediately(head))
            {
                try { head.dispose(); } catch { }
            }
            pendingClientHeadRecreate[slot] = false;
            ResetGhostHeadRuntimeState(slot);

            GhostKing? client = clients[slot];
            clients[slot] = null!;
            if (client != null)
            {
                try { client.PrepareForNetworkTransition(); } catch { }
                try { client.visible = false; } catch { }

                if (!TryDisposeRuntimeProcessImmediately(client))
                {
                    try { client.destroy(); } catch { }
                    try
                    {
                        EnsureGhostKingRenderSafe(
                            client,
                            "DisposeClientSlot.runtime-immediate-unavailable",
                            detachForTransition: true);
                    }
                    catch { }
                }
            }
            clientLastBodyAnims[slot] = null;
            clientLastBodyAnimQueues[slot] = null;
            clientLastBodyAnimGs[slot] = null;
            clientLastHeadAnims[slot] = null;
            clientLastDirs[slot] = 0;
            clientLastDownedOffsets[slot] = false;
            rLastX[slot] = 0;
            rLastY[slot] = 0;

            if (!clearIdentity)
                return;

            if (previousRemoteId > 0)
            {
                _remoteLastDoorMarkers.Remove(previousRemoteId);
                ClearCachedRemoteDiveSkillInfo(previousRemoteId);
            }

            clientIds[slot] = 0;
            clientLabels[slot] = null;
        }

        /// <summary>
        /// GhostHero.PurgeGhostKingsFromCurrentGame destroys GhostKing runtime entities before
        /// Dead Cells serializes a save. The purge happens outside the normal slot disposer, so
        /// the clients[] array can otherwise keep pointing at the destroyed GhostKing forever.
        /// That stale non-null slot prevents EnsureClientKingSlot from creating a replacement
        /// after save-triggering sublevel transitions such as the Giant door.
        ///
        /// Clear only runtime/render references here. Network identity, labels and cosmetics are
        /// deliberately retained so the next cached remote snapshot can rebuild the visual shell
        /// without treating the peer as a new player.
        /// </summary>
        internal void InvalidateRemoteKingRuntimeSlotsAfterSavePurge(string reason)
        {
            var invalidated = 0;

            for (var slot = 0; slot < clients.Length; slot++)
            {
                var client = clients[slot];
                var head = clientHeads[slot];
                if (client == null && head == null)
                    continue;

                _pendingClientDisposeTicks.Remove(slot);

                // The GhostKing itself was already destroyed by the save purge. Avoid calling its
                // disposal path a second time. The head is a separate runtime process, so retire it
                // defensively before dropping our reference.
                clientHeads[slot] = null;
                if (head != null)
                {
                    try
                    {
                        if (!TryDisposeRuntimeProcessImmediately(head))
                            head.dispose();
                    }
                    catch
                    {
                    }
                }

                clients[slot] = null!;
                pendingClientHeadRecreate[slot] = false;
                ResetGhostHeadRuntimeState(slot);

                clientLastBodyAnims[slot] = null;
                clientLastBodyAnimQueues[slot] = null;
                clientLastBodyAnimGs[slot] = null;
                clientLastHeadAnims[slot] = null;
                clientLastDirs[slot] = 0;
                clientLastDownedOffsets[slot] = false;
                rLastX[slot] = 0;
                rLastY[slot] = 0;
                invalidated++;
            }

            if (invalidated <= 0)
                return;

            Logger.Information(
                "[NetMod][GhostRender] invalidated {Count} purged remote runtime slot(s) reason={Reason}; identity retained for snapshot rebuild",
                invalidated,
                reason);

            // Do not queue a rebuild from inside Save.save. On "Quit to menu" the save is
            // immediately followed by Game/level disposal; the old queued rebuild recreated a
            // GhostKing in that teardown window and could later crash native skin/head cleanup
            // (notably Null access .heroHeadSkin). If gameplay continues, the normal hero update
            // calls ReceiveGhostCoords on the next live frame and recreates the shell safely.
        }

        private void DisposeClientSlot(int slot, bool clearIdentity)
        {
            if (slot < 0 || slot >= clients.Length)
                return;

            _pendingClientDisposeTicks.Remove(slot);

            var previousRemoteId = clientIds[slot];

            var head = clientHeads[slot];
            if (head != null)
            {
                head.dispose();
                clientHeads[slot] = null;
            }
            pendingClientHeadRecreate[slot] = false;
            ResetGhostHeadRuntimeState(slot);

            var client = clients[slot];
            if (client != null)
            {
                try { client.PrepareForNetworkTransition(); } catch { }
                client.destroy();
                client.dispose();
                client.disposeGfx();
            }
            clients[slot] = null!;
            clientLastBodyAnims[slot] = null;
            clientLastBodyAnimQueues[slot] = null;
            clientLastBodyAnimGs[slot] = null;
            clientLastHeadAnims[slot] = null;
            clientLastDirs[slot] = 0;
            clientLastDownedOffsets[slot] = false;
            rLastX[slot] = 0;
            rLastY[slot] = 0;

            if (!clearIdentity)
                return;

            if (previousRemoteId > 0)
            {
                _remoteLastDoorMarkers.Remove(previousRemoteId);
                ClearCachedRemoteDiveSkillInfo(previousRemoteId);
            }

            clientIds[slot] = 0;
            clientLabels[slot] = null;
        }

        private void ReceiveGhostWeapons()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            var net = _net;
            if (net == null || me == null) return;

            if (IsRemoteKingTransitionActive || IsRemoteCombatGraceActive())
            {
                if (net.TryConsumeRemoteWeaponSnapshots(out var guardedUpdates))
                    NetNode.ReleaseConsumedList(guardedUpdates);
                return;
            }

            if (!net.TryConsumeRemoteWeaponSnapshots(out var updates))
                return;

            try
            {
                var applied = 0;

                foreach (var update in updates)
                {
                    var updateStart = RuntimeHitchWatch.Start();
                    if (!TryApplyRemoteWeaponUpdate(update.Id, update.Kind, update.Slot, update.PermanentId, update.Ammo))
                        continue;
                    applied++;
                    if (perfEnabled)
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.ReceiveGhostWeapons.ApplyRemoteWeaponUpdate",
                            updateStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"remoteId={update.Id} slot={update.Slot} permanentId={update.PermanentId} ammo={(update.Ammo.HasValue ? update.Ammo.Value : -1)}"));
                }

                var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
                if (perfEnabled && hitchMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
                {
                    RuntimeHitchWatch.LogSlow(
                        Logger,
                        "ModEntry.ReceiveGhostWeapons",
                        hitchMs,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"updates={updates.Count} applied={applied}"));
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(updates);
            }
        }

        private void DrainRemoteCombatQueuesAfterLevelChange()
        {
            _ignoreRemoteCombatUntilTicks = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency * 0.65);

            var net = _net;
            if (net == null)
                return;

            if (net.TryConsumeRemoteWeaponSnapshots(out var weaponUpdates))
                NetNode.ReleaseConsumedList(weaponUpdates);
            if (net.TryConsumeRemoteAttacks(out var attacks))
                NetNode.ReleaseConsumedList(attacks);
        }

        private void ReceiveGhostAttacks()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            var net = _net;
            if (net == null || me == null) return;

            if (IsRemoteKingTransitionActive || IsRemoteCombatGraceActive() || IsLocalDiveNetGuardActive())
            {
                if (net.TryConsumeRemoteAttacks(out var guardedAttacks))
                    NetNode.ReleaseConsumedList(guardedAttacks);
                return;
            }

            if (!net.TryConsumeRemoteAttacks(out var attacks))
                return;

            try
            {
                var localId = net.id;
                var diveHandled = 0;
                var queuedAttacks = 0;
                foreach (var attack in attacks)
                {
                    var attackStart = RuntimeHitchWatch.Start();
                    if (TryHandleRemoteDiveAttack(attack, localId))
                    {
                        diveHandled++;
                        if (perfEnabled)
                            LogGhostRuntimeStepIfSlow(
                                "ModEntry.ReceiveGhostAttacks.Remote",
                                attackStart,
                                string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"remoteId={attack.Id} slot={attack.Slot} dive=1 action={attack.Action}"));
                        continue;
                    }

                    if (attack.Slot < 0 &&
                        (string.IsNullOrWhiteSpace(attack.Kind) ||
                         attack.Kind.StartsWith("__", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    if (!TryApplyRemoteWeaponUpdate(attack.Id, attack.Kind, attack.Slot, attack.PermanentId, attack.Ammo))
                        continue;
                    if (!TryGetClientIndex(localId, attack.Id, out var index))
                        continue;

                    var client = clients[index];
                    if (client?.kingWeaponsManager == null) continue;
                    if (attack.Action == RemoteAttackAction.Interrupt)
                    {
                        client.kingWeaponsManager.queueInterrupt(attack.Slot);
                    }
                    else
                    {
                        client.kingWeaponsManager.queueAttack(attack.Slot);
                    }

                    // Remote ATK changes GhostKing.spr outside the ANIM path. Drop the body-anim
                    // cache so a standing re-idle is not treated as a no-op.
                    clientLastBodyAnims[index] = null;
                    clientLastBodyAnimQueues[index] = null;
                    clientLastBodyAnimGs[index] = null;

                    queuedAttacks++;
                    if (perfEnabled)
                        LogGhostRuntimeStepIfSlow(
                            "ModEntry.ReceiveGhostAttacks.Remote",
                            attackStart,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"remoteId={attack.Id} slot={attack.Slot} dive=0 action={attack.Action} kind={attack.Kind ?? string.Empty}"));
                }

                var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
                if (perfEnabled && hitchMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
                {
                    RuntimeHitchWatch.LogSlow(
                        Logger,
                        "ModEntry.ReceiveGhostAttacks",
                        hitchMs,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"attacks={attacks.Count} diveHandled={diveHandled} queuedAttacks={queuedAttacks}"));
                }
            }
            finally
            {
                NetNode.ReleaseConsumedList(attacks);
            }
        }

        private void UpdateGhostWeapons()
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            var activeManagers = 0;
            for (int i = 0; i < clients.Length; i++)
            {
                var client = clients[i];
                if (client?.kingWeaponsManager == null) continue;
                activeManagers++;
                var managerStart = RuntimeHitchWatch.Start();
                client.kingWeaponsManager.update();
                if (perfEnabled)
                    LogGhostRuntimeStepIfSlow(
                        "ModEntry.UpdateGhostWeapons.Manager",
                        managerStart,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"slot={i} remoteId={clientIds[i]} shield={(client.kingWeaponsManager.IsShieldActive ? 1 : 0)}"));
            }

            var hitchMs = RuntimeHitchWatch.GetElapsedMilliseconds(hitchStart);
            if (perfEnabled && hitchMs >= RuntimeHitchWatch.GhostRuntimeSlowThresholdMs)
            {
                RuntimeHitchWatch.LogSlow(
                    Logger,
                    "ModEntry.UpdateGhostWeapons",
                    hitchMs,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"activeManagers={activeManagers} clients={clients.Length}"));
            }
        }

        private static int CountPendingClientHeadRecreate()
        {
            var count = 0;
            for (int i = 0; i < pendingClientHeadRecreate.Length; i++)
            {
                if (pendingClientHeadRecreate[i])
                    count++;
            }

            return count;
        }

        private void LogGhostRuntimeStepIfSlow(string key, long stepStart, string? details)
        {
            var stepMs = RuntimeHitchWatch.GetElapsedMilliseconds(stepStart);
            if (stepMs < RuntimeHitchWatch.GhostRuntimeStepSlowThresholdMs)
                return;

            RuntimeHitchWatch.LogSlow(Logger, key, stepMs, details);
        }

        private void PlayGhostAnim(GhostKing client, string anim, int? queueAnim, bool? g)
        {
            if (client?.spr?._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            if (DeadCellsMultiplayerMod.Ghost.KingWeaponSupport.IsUnsafeRemoteGhostAnimation(anim)) return;
            var shieldActive = client.kingWeaponsManager != null && client.kingWeaponsManager.IsShieldActive;
            if (shieldActive && ShouldLoopRemoteAnim(anim))
            {
                return;
            }

            if (anim.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                anim.IndexOf("shield", StringComparison.OrdinalIgnoreCase) >= 0 ||
                anim.IndexOf("parry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                anim.IndexOf("block", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            var animManager = client.spr._animManager;
            var current = client.spr.groupName;
            if(current != null && string.Equals(current.ToString(), anim, StringComparison.Ordinal))
                return;

            if (ShouldLoopRemoteAnim(anim))
            {
                if (!shieldActive)
                {
                    client.removeAllAffects(96);
                    client.removeAllAffects(98);
                    client.removeAllAffects(99);
                }
                animManager.play(anim.AsHaxeString(), null, null).loop(null);
                return;
            }
            animManager.play(anim.AsHaxeString(), queueAnim, g).stopOnLastFrame(Ref<bool>.Null);
        }

        private static bool ShouldLoopRemoteAnim(string anim)
        {
            if(string.IsNullOrWhiteSpace(anim)) return false;
            var a = anim.Trim();

            // Don't ever force-loop weapon/hold-ish states; those should be driven by weapon replication.
            if(IsAttackAnim(a)) return false;
            if(a.IndexOf("guard", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if(a.IndexOf("defend", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (a.StartsWith("idle", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("run", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.StartsWith("walk", StringComparison.OrdinalIgnoreCase)) return true;
            if (a.IndexOf("move", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("fall", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("land", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("climb", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("ladder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("crouch", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("volte", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (a.IndexOf("remain", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private void PlayGhostHeadAnim(GhostKing client, string anim)
        {
            if (client == null || client?.head == null || client?.head?.customHeadSpr._animManager == null) return;
            if (string.IsNullOrWhiteSpace(anim)) return;
            var animManager = client.head.customHeadSpr._animManager;
            animManager.play(anim.AsHaxeString(), null, null).loop(null);
            animManager.genSpeed = 0.4;
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
        }


        private void SendHeadAnim(string anim)
        {
            if (_netRole == NetRole.None) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(anim)) return;
            net.SendHeadAnim(anim);
        }

        private void SendEquippedWeapons(Inventory inv)
        {
            if (_netRole == NetRole.None || inv == null) return;
            var w0 = inv.getEquippedWeaponOn(0);
            if (w0 != null)
                SendInventoryWeapon(w0, 0);
            var w1 = inv.getEquippedWeaponOn(1);
            if (w1 != null)
                SendInventoryWeapon(w1, 1);
        }

        private void SendInventoryWeapon(InventItem item, int slot)
        {
            if (_netRole == NetRole.None) return;
            if (item == null) return;
            if (!TryGetWeaponKindId(item, out var kindId)) return;
            var net = _net;
            if (net == null || string.IsNullOrWhiteSpace(kindId)) return;
            net.SendInventoryWeapon(kindId!, slot, item.permanentId, GetWeaponAmmoForSync(item));
        }

        private static bool TryGetWeaponKindId(InventItem item, out string? kindId)
        {
            kindId = null;
            if (item == null) return false;
            var kind = item.kind;
            if (kind is InventItemKind.Weapon w)
            {
                kindId = w.Param0?.ToString();
                return !string.IsNullOrWhiteSpace(kindId);
            }
            return false;
        }

        private static int? GetWeaponAmmoForSync(InventItem? item)
        {
            if(item == null)
                return null;

            var maxAmmo = item.getMaxAmmo();
            if(maxAmmo <= 0)
                return null;

            var ammo = item.ammo;
            if(ammo < 0) ammo = 0;
            if(ammo > maxAmmo) ammo = maxAmmo;
            return ammo;
        }

        private static int GetWeaponSlot(Inventory inv, InventItem item)
        {
            if (inv == null || item == null) return -1;
            var id = item.permanentId;
            var w0 = inv.getEquippedWeaponOn(0);
            if (w0 != null && w0.permanentId == id) return 0;
            var w1 = inv.getEquippedWeaponOn(1);
            if (w1 != null && w1.permanentId == id) return 1;
            return item.posID;
        }

        private bool IsLocalInventory(Inventory self)
        {
            return me != null && self != null && ReferenceEquals(self, me.inventory);
        }

        private bool TryApplyRemoteWeaponUpdate(int remoteId, string? kindId, int slot, int permanentId, int? ammo = null)
        {
            if (string.IsNullOrWhiteSpace(kindId) || kindId.Length > 160 ||
                slot < -1 || slot > 1 || permanentId < 0)
                return false;

            try
            {
                ApplyRemoteWeaponUpdate(remoteId, kindId, slot, permanentId, ammo);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    ex,
                    "[NetMod][RemoteWeaponGuard] ignored invalid/unsafe remote weapon update remoteId={RemoteId} slot={Slot} kind={Kind}",
                    remoteId,
                    slot,
                    kindId ?? string.Empty);
                return false;
            }
        }

        private void ApplyRemoteWeaponUpdate(int remoteId, string? kindId, int slot, int permanentId, int? ammo = null)
        {
            var hitchStart = RuntimeHitchWatch.Start();
            var perfEnabled = RuntimeHitchWatch.Enabled;
            if (string.IsNullOrWhiteSpace(kindId)) return;
            if (slot < -1 || slot > 1 || permanentId < 0) return;
            var net = _net;
            var localId = net?.id ?? 0;
            if (!TryGetClientIndex(localId, remoteId, out var index))
                return;

            var client = clients[index];
            if (client?.inventory == null) return;

            var cleaned = kindId.Replace("|", "/").Trim();
            if (cleaned.Length == 0 || cleaned.Length > 160) return;

            var inv = client.inventory;
            var existing = permanentId != 0 ? inv.getByPermanentId(permanentId) : null;
            var currentSlotItem = slot >= 0 ? inv.getEquippedWeaponOn(slot) : null;
            if (slot >= 0 && IsRemoteWeaponStateMatch(currentSlotItem, cleaned, permanentId, ammo))
                return;

            if(existing == null && permanentId == 0)
            {
                if(IsWeaponKindMatch(currentSlotItem, cleaned))
                    existing = currentSlotItem;
                else if (slot < 0)
                {
                    var w0 = inv.getEquippedWeaponOn(0);
                    if(IsWeaponKindMatch(w0, cleaned))
                        existing = w0;
                    else
                    {
                        var w1 = inv.getEquippedWeaponOn(1);
                        if(IsWeaponKindMatch(w1, cleaned))
                            existing = w1;
                    }
                }
            }

            if (existing == null)
            {
                var newItem = new InventItem(new InventItemKind.Weapon(cleaned.AsHaxeString()));
                if (permanentId != 0)
                    newItem.permanentId = permanentId;
                if (slot >= 0)
                    newItem.posID = slot;
                _inventorySyncGuard = true;
                try
                {
                    if(currentSlotItem != null)
                        currentSlotItem.posID = -1;
                    inv.add(newItem);
                }
                finally
                {
                    _inventorySyncGuard = false;
                }
                existing = newItem;
            }
            else if(currentSlotItem != null &&
                    !ReferenceEquals(currentSlotItem, existing) &&
                    (currentSlotItem.permanentId == 0 ||
                     existing.permanentId == 0 ||
                     currentSlotItem.permanentId != existing.permanentId))
            {
                currentSlotItem.posID = -1;
            }

            if (slot >= 0)
                existing.posID = slot;

            var needsEquip = slot < 0 || !ReferenceEquals(currentSlotItem, existing);
            var needsAmmoUpdate = !DoesWeaponAmmoMatch(existing, ammo);
            if (!needsEquip && !needsAmmoUpdate)
                return;

            _inventorySyncGuard = true;
            try
            {
                if (needsEquip)
                    inv.equip(existing);
                if (needsAmmoUpdate)
                    ApplyRemoteWeaponAmmo(existing, ammo);
            }
            finally
            {
                _inventorySyncGuard = false;
            }

            // Detached ghost inventories are long-lived. Remove the item that was replaced in this
            // slot once it is no longer equipped anywhere, otherwise every weapon swap leaks another
            // InventItem into the ghost inventory for the rest of the run.
            TryRemoveSupersededRemoteWeapon(inv, currentSlotItem, existing);

            if (perfEnabled)
                LogGhostRuntimeStepIfSlow(
                    "ModEntry.ApplyRemoteWeaponUpdate",
                    hitchStart,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"remoteId={remoteId} slot={slot} permanentId={permanentId} ammo={(ammo.HasValue ? ammo.Value : -1)} kind={cleaned}"));
        }

        private static void TryRemoveSupersededRemoteWeapon(Inventory inv, InventItem? superseded, InventItem replacement)
        {
            if(inv == null || superseded == null || replacement == null || ReferenceEquals(superseded, replacement))
                return;

            try
            {
                if(ReferenceEquals(inv.getEquippedWeaponOn(0), superseded) ||
                   ReferenceEquals(inv.getEquippedWeaponOn(1), superseded))
                    return;

                inv.remove(superseded);
            }
            catch
            {
                // Cosmetic ghost cleanup must never affect the run.
            }
        }

        private void DisposeCoopGhostRuntime()
        {
            try
            {
                ResetFakeDeathState(unlockLocalHero: false, sendNetworkUpState: false);
            }
            catch
            {
            }

            for (int i = 0; i < clients.Length; i++)
            {
                try
                {
                    DisposeClientSlot(i, clearIdentity: true);
                }
                catch
                {
                }
            }

            ClearCoopGhostRuntimeRefs();
        }

        private void ClearCoopGhostRuntimeRefs()
        {
            var ghost = _ghost;
            _ghost = null!;
            _ghostOwnerHero = null;
            _ghostOwnerGame = null;
            _ghostBootstrapNet = null;
            _ = ghost;
        }

        internal void DisposeCoopGhostRuntimeForWorldTeardown(dc.pr.Game? disposingGame = null)
        {
            _ = disposingGame;

            try
            {
                ResetFakeDeathState(unlockLocalHero: false, sendNetworkUpState: false);
            }
            catch
            {
            }

            // A world teardown (restart / exit) must NOT use the triple-dispose path of
            // DisposeClientSlot (destroy+dispose+disposeGfx): it can leave a destroyed remote
            // GhostKing in the level's process tree, and the next frame's Process._dispose then
            // crashes on a null controller.manualLock. Use the same disposeImmediately-based path
            // the sub-level transition guard relies on.
            for (int i = 0; i < clients.Length; i++)
            {
                try
                {
                    DisposeClientSlotForSubLevelTransition(i, clearIdentity: true);
                }
                catch
                {
                }
            }

            ClearCoopGhostRuntimeRefs();
        }

        internal void HandleNetworkDisconnectGhostCleanup(NetRole role)
        {
            if (role == NetRole.Host)
            {
                var activeRemoteIds = new HashSet<int>();
                try { _net?.CopyRemoteUserIdsTo(activeRemoteIds, includePrimary: true); } catch { }

                for (int i = 0; i < clientIds.Length; i++)
                {
                    var remoteId = clientIds[i];
                    if (remoteId <= 0 || activeRemoteIds.Contains(remoteId))
                        continue;

                    try
                    {
                        // Phase 17: this client is gone from the network session. Purge its mob
                        // interest so it can never keep mobs "interested" (stale userIds otherwise
                        // kept the mob-sync Phase 16 relevance gate open until the next level reset).
                        global::DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.RemoveHostClientInterestForUser(remoteId);
                    }
                    catch
                    {
                    }

                    try
                    {
                        DisposeClientSlot(i, clearIdentity: true);
                    }
                    catch
                    {
                    }
                }

                return;
            }

            DisposeCoopGhostRuntime();
        }

        private static void ApplyRemoteWeaponAmmo(InventItem item, int? ammo)
        {
            if(item == null || !ammo.HasValue)
                return;

            var maxAmmo = item.getMaxAmmo();
            if(maxAmmo <= 0)
                return;

            var value = ammo.Value;
            if(value < 0) value = 0;
            if(value > maxAmmo) value = maxAmmo;
            item.ammo = value;
        }

        private static bool DoesWeaponAmmoMatch(InventItem? item, int? ammo)
        {
            if (item == null || !ammo.HasValue)
                return true;

            var maxAmmo = item.getMaxAmmo();
            if (maxAmmo <= 0)
                return true;

            var expected = ammo.Value;
            if (expected < 0)
                expected = 0;
            if (expected > maxAmmo)
                expected = maxAmmo;
            return item.ammo == expected;
        }

        private static bool IsRemoteWeaponStateMatch(InventItem? item, string expectedKindId, int expectedPermanentId, int? ammo)
        {
            if(item == null || !IsWeaponKindMatch(item, expectedKindId))
                return false;
            if(expectedPermanentId != 0 && item.permanentId != expectedPermanentId)
                return false;
            return DoesWeaponAmmoMatch(item, ammo);
        }

        private static bool IsWeaponKindMatch(InventItem? item, string expectedKindId)
        {
            if(item == null || string.IsNullOrWhiteSpace(expectedKindId))
                return false;
            if(!TryGetWeaponKindId(item, out var itemKindId) || string.IsNullOrWhiteSpace(itemKindId))
                return false;
            return string.Equals(itemKindId, expectedKindId, StringComparison.Ordinal);
        }

        private void ResetLocalSkinSendCache()
        {
            _lastSentHeroSkin = null;
            _lastSentHeroHeadSkin = null;
        }

        private void ResetDoorMarkerState()
        {
            _lastDoorMarkerLevelId = string.Empty;
            _lastDoorMarkerToken = int.MinValue;
            _localLastDoorMarkerLevelId = string.Empty;
            _localLastDoorMarkerToken = int.MinValue;
            _remoteLastDoorMarkers.Clear();
            _pendingClientDisposeTicks.Clear();
        }

        private void ResetNetworkState()
        {
            GameDataSync.RestoreOrigHpMultipliers();
            MainThreadPump.ClearPendingNetworkMainThreadActions();
            GameDataSync.ResetTransientNetworkState();
            global::DeadCellsMultiplayerMod.AdvancedCoop.CoopAdvancedHardening.ResetSessionState();
            try { global::DeadCellsMultiplayerMod.Mobs.MobsSynchronization.MobsSynchronization.ClearTrackingForLevelChange(); } catch { }
            ResetFakeDeathState(unlockLocalHero: true, sendNetworkUpState: false);
            ResetLocalSkinSendCache();
            ResetDoorMarkerState();
            _lastSentDiveInfoPayload = string.Empty;
            _remoteDiveInfoPayloadById.Clear();
            _lastLocalDiveStartSendTicks = 0;
            _lastLocalDiveLandSendTicks = 0;
            _lastDiveInfoScanTicks = 0;
        }
    }
}

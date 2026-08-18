using System.Globalization;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Mobs.MobsSynchronization;
using DeadCellsMultiplayerMod.PortableCore;

public sealed partial class NetNode
{
    public void TickSend(double cx, double cy, int dir, string? levelId = null)
    {
        if (!HasAnyConnection()) return;
        if (ID <= 0) return;
        var positionSequence = Interlocked.Increment(ref _nextPositionSequence);
        var line = BuildPosLine(ID, cx, cy, dir, levelId, positionSequence);
        _ = SendLineSafe(line);
    }


    internal void SendRunLaunchCommit(RunLaunchDescriptor descriptor, bool flush = false)
    {
        if (descriptor == null)
            return;

        var payload = RunLaunchWireCodec.EncodeCommitPayload(descriptor);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                var sequenceChanged = !_cachedHostRunLaunchSequence.HasValue ||
                                      _cachedHostRunLaunchSequence.Value != descriptor.Sequence;
                _cachedHostRunCommitPayload = payload;
                if (sequenceChanged)
                {
                    _cachedHostRunExecutePayload = null;
                    _cachedHostRunReadyPayload = null;
                }
                _cachedHostRunLaunchSequence = descriptor.Sequence;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information(
                "[NetNode][RunLaunch] Cached RUNCOMMIT seq={Sequence}: no connected client",
                descriptor.Sequence);
            return;
        }

        var line = $"{RunLaunchWireCodec.CommitTag}|{payload}";
        if (flush)
            SendControlAndFlush(line, 500);
        else
            SendRaw(line);
    }

    internal void SendRunLaunchAck(RunLaunchAck ack, bool flush = false)
    {
        if (ack == null || !HasAnyConnection())
            return;

        var line = RunLaunchWireCodec.BuildAckLine(ack);
        if (flush)
            SendControlAndFlush(line, 500);
        else
            SendRaw(line);
    }

    internal void SendRunLaunchQueued(RunLaunchQueued queued, bool flush = false)
    {
        // Client -> host confirmation. Not host-cached: the host is the only recipient and it is
        // already connected when the client queues its launch.
        if (queued == null || !HasAnyConnection())
            return;

        var line = RunLaunchWireCodec.BuildQueuedLine(queued);
        if (flush)
            SendControlAndFlush(line, 500);
        else
            SendRaw(line);
    }

    internal void SendRunLaunchExecute(RunLaunchExecute execute, bool flush = false)
    {
        if (execute == null)
            return;

        var payload = RunLaunchWireCodec.EncodeExecutePayload(execute);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostRunExecutePayload = payload;
                _cachedHostRunLaunchSequence = execute.Sequence;
            }
        }

        if (!HasAnyConnection())
            return;

        var line = $"{RunLaunchWireCodec.ExecuteTag}|{payload}";
        if (flush)
            SendControlAndFlush(line, 500);
        else
            SendRaw(line);
    }

    internal void SendRunLevelReady(RunLevelReady ready)
    {
        if (ready == null)
            return;

        var payload = RunLaunchWireCodec.EncodeReadyPayload(ready);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostRunReadyPayload = payload;
                _cachedHostRunLaunchSequence = ready.Sequence;
            }
        }

        if (HasAnyConnection())
            SendRaw($"{RunLaunchWireCodec.ReadyTag}|{payload}");
    }

    internal void SendRunLaunchCancel(RunLaunchCancel cancel, bool flush = false)
    {
        if (cancel == null)
            return;

        ClearCachedHostRunLaunch(cancel.Sequence);
        if (!HasAnyConnection())
            return;

        var line = RunLaunchWireCodec.BuildCancelLine(cancel);
        if (flush)
            SendControlAndFlush(line, 500);
        else
            SendRaw(line);
    }

    internal void ClearCachedHostRunLaunch(int sequence)
    {
        if (_role != NetRole.Host)
            return;

        lock (_hostCacheSync)
        {
            if (_cachedHostRunLaunchSequence.HasValue &&
                _cachedHostRunLaunchSequence.Value != sequence)
            {
                return;
            }

            _cachedHostRunCommitPayload = null;
            _cachedHostRunExecutePayload = null;
            _cachedHostRunReadyPayload = null;
            _cachedHostRunLaunchSequence = null;
            if (_cachedHostRunSeedSequence == sequence)
            {
                _cachedHostSeed = null;
                _cachedHostRunSeedSequence = null;
                _cachedHostLaunchKind = null;
            }
        }
    }

    public void LevelSend(int senderId, string lvl) => SendLevelId(senderId, lvl);

    public void SendSeed(int sequence, int seed, string launchKind)
    {
        var safeLaunchKind = (launchKind ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostSeed = seed;
                _cachedHostRunSeedSequence = sequence;
                _cachedHostLaunchKind = safeLaunchKind;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Cached run seed seq={Sequence} seed={Seed}: no connected client", sequence, seed);
            return;
        }
        var line = $"SEED|{sequence}|{seed}|{safeLaunchKind}\n";
        _ = SendLineSafe(line);
        _log.Information("[NetNode] Sent run seed seq={Sequence} seed={Seed} launch={LaunchKind}", sequence, seed, safeLaunchKind);
    }

    public void SendRunRestart(int seed)
    {
        if (!HasAnyConnection())
            return;

        var line = string.Create(CultureInfo.InvariantCulture, $"RESTART|{seed}\n");
        _ = SendLineSafe(line);
        _log.Information("[NetNode] Sent same-run restart seed {Seed}", seed);
    }

    public void SendSerializerSync(int seq, int uid)
    {
        if (_role != NetRole.Host)
            return;
        lock (_hostCacheSync)
        {
            _cachedHostSerializerSeq = seq;
            _cachedHostSerializerUid = uid;
        }
        if (!HasAnyConnection())
            return;

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"HXSYNC|{seq}|{uid}\n");
        _ = SendLineSafe(line);
    }

    public void SendUsername(string username)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending username: no connected client");
            return;
        }

        var safe = (username ?? "guest").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) safe = "guest";

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw("USER|" + idPart + safe);
        _log.Information("[NetNode] Sent username {Username}", safe);
    }

    public void SendReady(bool ready)
    {
        if (ID <= 0)
            return;

        if (!HasAnyConnection())
            return;

        _ = SendLineSafe(BuildReadyLine(ID, ready));
    }

    public void SendCoopState(string? coopId, bool hasContinueSave)
    {
        var safeCoopId = SanitizeProtocolToken(coopId, 128);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostCoopId = safeCoopId;
                _cachedHostHasContinueSave = hasContinueSave;
            }
        }

        if (!HasAnyConnection())
            return;

        var line = ID > 0
            ? BuildCoopStateLine(ID, safeCoopId, hasContinueSave)
            : $"COOPID|{safeCoopId}|{(hasContinueSave ? 1 : 0)}\n";
        _ = SendLineSafe(line);
        _log.Information(
            "[NetNode] Sent coop id state hasId={HasId} hasContinue={HasContinue}",
            !string.IsNullOrWhiteSpace(safeCoopId),
            hasContinueSave);
    }

    public void SendLaunchMode(
        int action,
        bool custom,
        bool streamEnabled,
        bool newCoopWorldPrepared,
        string? coopId,
        bool hostHasContinueSave)
    {
        if (_role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;

        var safeCoopId = SanitizeProtocolToken(coopId, 128);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"LAUNCHMODE|{action}|{(custom ? 1 : 0)}|{(streamEnabled ? 1 : 0)}|{(newCoopWorldPrepared ? 1 : 0)}|{safeCoopId}|{(hostHasContinueSave ? 1 : 0)}\n");
        _ = SendLineSafe(line);
    }

    public void SendBossRune(int bossRune)
    {
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostBossRune = bossRune;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending boss rune: no connected client");
            return;
        }

        var payload = bossRune.ToString(CultureInfo.InvariantCulture);
        SendRaw("BOSSRUNE|" + payload);
        // _log.Information("[NetNode] Sent boss rune {BossRune}", bossRune);
    }

    public void SendHpMultipliers()
    {
        var mobsMult = MultiplayerSettingsStorage.MobsHpMultiplier;
        var bossesMult = MultiplayerSettingsStorage.BossesHpMultiplier;
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostMobsHpMult = mobsMult;
                _cachedHostBossesHpMult = bossesMult;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending HP multipliers: no connected client");
            return;
        }

        var payload = $"{mobsMult.ToString(CultureInfo.InvariantCulture)}|{bossesMult.ToString(CultureInfo.InvariantCulture)}";
        SendRaw("HPMULT|" + payload);
    }

    public void SendLevelDesc(string json)
    {
        var safeJson = (json ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostLevelDescPayload = string.IsNullOrWhiteSpace(safeJson) ? null : safeJson;
            }
        }

        if (string.IsNullOrWhiteSpace(safeJson))
            return;

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending level desc: no connected client");
            return;
        }

        SendRaw("LDESC|" + safeJson);
        _log.Information("[NetNode] Sent LevelDesc payload");
    }

    /// <summary>Drops the oldest entry once a per-level cache exceeds its bound.</summary>
    private static void TrimHostLevelPayloadCacheLocked(Dictionary<string, string> cache)
    {
        while (cache.Count > MaxCachedHostLevelPayloads)
        {
            var oldest = default(string);
            foreach (var key in cache.Keys)
            {
                oldest = key;
                break;
            }

            if (oldest == null)
                break;
            cache.Remove(oldest);
        }
    }

    public void SendLevelSeed(string levelId, double seed)
    {
        var safeSeed = seed.ToString(CultureInfo.InvariantCulture);
        var safeId = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(safeId))
            return;

        // Same level ids are revisited on restart and nested/sublevel flows. A monotonic sequence
        // prevents a delayed previous LSEED from being consumed by the new generation.
        var sequence = _role == NetRole.Host ? Interlocked.Increment(ref _nextHostLevelSeedSequence) : 0L;
        var payload = sequence > 0
            ? $"{safeId}|{sequence.ToString(CultureInfo.InvariantCulture)}|{safeSeed}"
            : $"{safeId}|{safeSeed}";

        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostLevelSeedsByLevelId[safeId] = payload;
                TrimHostLevelPayloadCacheLocked(_cachedHostLevelSeedsByLevelId);
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Cached level seed seq={Sequence} for {LevelId}: no connected client", sequence, safeId);
            return;
        }

        SendRaw($"LSEED|{payload}");
        _log.Information("[NetNode] Sent level seed seq={Sequence} for {LevelId}", sequence, safeId);
    }

    public void RequestLevelSeed(string levelId)
    {
        if (_role != NetRole.Client || !HasAnyConnection() || string.IsNullOrWhiteSpace(levelId))
            return;

        var safeId = levelId.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(safeId))
            return;

        SendRaw("LSEEDREQ|" + safeId);
    }

    public void RequestLevelGraph(string levelId)
    {
        if (_role != NetRole.Client || !HasAnyConnection() || string.IsNullOrWhiteSpace(levelId))
            return;

        var safeId = levelId.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(safeId))
            return;

        SendRaw("LGRAPHREQ|" + safeId);
    }

    private void ResendCachedLevelSeed(string levelId)
    {
        if (_role != NetRole.Host || string.IsNullOrWhiteSpace(levelId))
            return;

        string? payload = null;
        lock (_hostCacheSync)
            _cachedHostLevelSeedsByLevelId.TryGetValue(levelId, out payload);

        if (!string.IsNullOrWhiteSpace(payload))
        {
            SendRaw("LSEED|" + payload);
            _log.Debug("[NetNode][LevelSync] Re-sent cached level seed for {LevelId}", levelId);
        }
    }

    private void ResendCachedLevelGraph(string levelId)
    {
        if (_role != NetRole.Host || string.IsNullOrWhiteSpace(levelId))
            return;

        string? payload = null;
        lock (_hostCacheSync)
            _cachedHostLevelGraphsByLevelId.TryGetValue(levelId, out payload);

        if (!string.IsNullOrWhiteSpace(payload))
        {
            SendRaw("LGRAPH|" + payload);
            _log.Debug("[NetNode][LevelSync] Re-sent cached level graph for {LevelId}", levelId);
        }
    }

    internal void ClearCachedGeneratedLevelStateForRestart()
    {
        if (_role != NetRole.Host)
            return;

        lock (_hostCacheSync)
        {
            _cachedHostLevelSeedsByLevelId.Clear();
            _cachedHostLevelGraphsByLevelId.Clear();
        }
    }

    public void SendLevelGraph(string levelId, string json)
    {
        var safeId = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (!string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(safeId))
        {
            lock (_hostCacheSync)
            {
                _cachedHostLevelGraphsByLevelId[safeId] = json;
                TrimHostLevelPayloadCacheLocked(_cachedHostLevelGraphsByLevelId);
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Cached level graph for {LevelId}: no connected client", safeId);
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
            return;

        SendRaw("LGRAPH|" + json);
        _log.Information("[NetNode] Sent level graph for {LevelId} ({Length} bytes)", safeId, json.Length);
    }

    public void SendGeneratePayload(string json)
    {
        var safeJson = (json ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostGeneratePayload = string.IsNullOrWhiteSpace(safeJson) ? null : safeJson;
            }
        }

        if (string.IsNullOrWhiteSpace(safeJson))
            return;

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode][RunLaunch] Cached generate payload: no connected client");
            return;
        }

        SendRaw("GEN|" + safeJson);
        _log.Information("[NetNode][RunLaunch] Sent Generate payload ({Length} bytes)", safeJson.Length);
    }

    /// <summary>
    /// Re-publishes the cached authoritative launch (GEN, boss rune, custom-mode data, RUNCOMMIT,
    /// SEED, RUNEXEC) to every connected client. Driven by the host launch beacon so a launch that
    /// was lost, arrived before the receiver was ready, or was rejected by a transient state race
    /// is repaired by the next beat instead of stranding the client in the lobby.
    /// Returns false when there is nothing committed to replay.
    /// </summary>
    internal bool TryResendCachedHostRunLaunch(out int sequence)
    {
        sequence = 0;
        if (_role != NetRole.Host)
            return false;

        string? generatePayload;
        int? bossRune;
        string? customGameData;
        string? commitPayload;
        string? executePayload;
        int? seed;
        int? seedSequence;
        string? launchKind;
        lock (_hostCacheSync)
        {
            generatePayload = _cachedHostGeneratePayload;
            bossRune = _cachedHostBossRune;
            customGameData = _cachedHostCustomGameDataPayload;
            commitPayload = _cachedHostRunCommitPayload;
            executePayload = _cachedHostRunExecutePayload;
            seed = _cachedHostSeed;
            seedSequence = _cachedHostRunSeedSequence;
            launchKind = _cachedHostLaunchKind;
            sequence = _cachedHostRunLaunchSequence ?? 0;
        }

        if (string.IsNullOrWhiteSpace(commitPayload))
            return false;
        if (!HasAnyConnection())
            return true;

        // Order matches the host's own first-time send order (GEN announces the launch mode, then
        // CGDATA delivers the rules that mode needs), so replaying it can only move the client
        // forward. Sending CGDATA first would let the GEN handler clear the custom-mode readiness
        // flag that the CGDATA handler had just satisfied, and the client would never arm.
        if (bossRune.HasValue)
            SendRaw($"BOSSRUNE|{bossRune.Value.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(generatePayload))
            SendRaw("GEN|" + generatePayload);
        if (!string.IsNullOrWhiteSpace(customGameData))
            SendRaw("CGDATA|" + customGameData);
        SendRaw($"{RunLaunchWireCodec.CommitTag}|{commitPayload}");
        if (seed.HasValue && seedSequence.HasValue)
            SendRaw($"SEED|{seedSequence.Value}|{seed.Value}|{launchKind ?? string.Empty}");
        if (!string.IsNullOrWhiteSpace(executePayload))
            SendRaw($"{RunLaunchWireCodec.ExecuteTag}|{executePayload}");

        return true;
    }

    public void SendCustomGameData(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending customGameData: no connected client");
            return;
        }

        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        lock (_hostCacheSync)
            _cachedHostCustomGameDataPayload = encoded;

        // Base64 keeps the payload on a single protocol line (game JSON is indented).
        SendRaw("CGDATA|" + encoded);
        _log.Information("[NetNode] Sent customGameData ({Length} chars, {Encoded} encoded)", json.Length, encoded.Length);
    }


    public void SendHP(double life, double maxLife, double lif, double bonusLife, double recover)
    {
        lock (_sync)
        {
            _localHpLife = (int)System.Math.Round(life, System.MidpointRounding.AwayFromZero);
            _localHpMaxLife = (int)System.Math.Round(maxLife, System.MidpointRounding.AwayFromZero);
            _localHpLif = (int)System.Math.Round(lif, System.MidpointRounding.AwayFromZero);
            _localHpBonusLife = (int)System.Math.Round(bonusLife, System.MidpointRounding.AwayFromZero);
            _localHpRecover = (int)System.Math.Round(recover, System.MidpointRounding.AwayFromZero);
            _hasLocalHpSnapshot = true;
        }

        if (!HasAnyConnection())
        {
            return;
        }
        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"HP|{idPart}{life}|{maxLife}|{lif}|{bonusLife}|{recover}");
    }

    public void SendLevelId(int senderId, string levelId)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = levelId.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"LEVEL|{senderId}|{safe}");
    }

    public void SendRoomTarget(string levelId, int roomId)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0 || roomId < 0)
            return;

        var safe = (levelId ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(safe))
            return;

        SendRaw($"ZROOM|{ID}|{safe}|{roomId}");
    }

    public void SendControlAndFlush(string payload, int timeoutMs = 250)
    {
        if (!HasAnyConnection())
            return;

        if (string.IsNullOrWhiteSpace(payload))
            return;

        var line = payload.EndsWith('\n') ? payload : payload + "\n";
        try
        {
            var task = SendLineSafe(line);
            if (!task.Wait(timeoutMs))
                _log.Warning("[NetNode] Timed out sending control line \"{Payload}\"", payload);
        }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Failed to send control line \"{Payload}\": {Message}", payload, ex.Message);
        }
    }


    public void SendHeadAnim(string anim)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = (anim ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"HEADANIM|{idPart}{safe}");
    }

    public void SendAnim(string anim, int? queueAnim = null, bool? g = null)
    {
        if (!HasAnyConnection())
        {
            return;
            
        }

        var safe = (anim ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) safe = "idle";
        var queuePart = queueAnim.HasValue ? queueAnim.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var gPart = g.HasValue ? (g.Value ? "1" : "0") : string.Empty;
        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"ANIM|{idPart}{safe}|{queuePart}|{gPart}");
    }

    public void SendInventoryWeapon(string kind, int slot, int permanentId, int? ammo = null)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = (kind ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) return;

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        if (ammo.HasValue)
            SendRaw($"INV|{idPart}{safe}|{slot}|{permanentId}|{ammo.Value}");
        else
            SendRaw($"INV|{idPart}{safe}|{slot}|{permanentId}");
    }

    public void SendAttack(string kind, int slot, int permanentId, int? ammo = null, RemoteAttackAction action = RemoteAttackAction.Attack)
    {
        if (!HasAnyConnection())
        {
            return;
        }

        var safe = (kind ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (safe.Length == 0) return;

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        var actionToken = AttackActionToToken(action);
        if (ammo.HasValue)
            SendRaw($"ATK|{idPart}{safe}|{slot}|{permanentId}|{ammo.Value}|{actionToken}");
        else
            SendRaw($"ATK|{idPart}{safe}|{slot}|{permanentId}|{actionToken}");
    }

    public void SendHeroSkin(string skin)
    {
        var safe = (skin ?? "PrisonerDefault").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "PrisonerDefault";

        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostHeroSkin = safe;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending hero skin: no connected client");
            return;
        }

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw("SKIN|" + idPart + safe);
        _log.Information("[NetNode] Sent hero skin {Skin}", safe);
    }

    public void SendHeroHeadSkin(string skin)
    {
        var safe = (skin ?? "PrisonerDefault").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "BaseFlame";

        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostHeroHeadSkin = safe;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending hero skin: no connected client");
            return;
        }

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw("HEAD|" + idPart + safe);
        _log.Information("[NetNode] Sent hero skin {Skin}", safe);
    }

    public void SendHeroDeath()
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending death: no connected client");
            return;
        }

        SendRaw("DIED");
        _log.Information("[NetNode] Sent hero death");
    }

    public void SendPlayerDownState(bool isDowned, double x, double y, string? levelId, double? headX = null, double? headY = null, string? headAnim = null)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var hasHead = isDowned && headX.HasValue && headY.HasValue;
        var hasAnim = hasHead && !string.IsNullOrWhiteSpace(headAnim);
        var state = new PlayerDownState(ID, isDowned, x, y, levelId ?? string.Empty, hasHead, headX ?? 0, headY ?? 0, hasAnim, headAnim);
        var line = BuildPlayerDownLine(state);
        _ = SendLineSafe(line);
    }

    public void SendPlayerReviveRequest(int targetId)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0 || targetId <= 0)
            return;

        var request = new PlayerReviveRequest(ID, targetId);
        var line = BuildPlayerReviveLine(request);
        _ = SendLineSafe(line);
    }

    public void SendMobStates(IReadOnlyList<MobStateSnapshot> states)
    {
        if (_role != NetRole.Host && _role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (states == null || states.Count == 0)
            return;

        // Prefer binary MOBSTATE2; text MOBSTATE only when disabled via DCCM_MOB_WIRE_TEXT=1
        // or when binary encoding fails.
        if (MobWireBinary.UseBinaryWire &&
            MobWireBinary.TryBuildMobStatesBinary(states, out var bin) &&
            bin != null)
        {
            var line = "MOBSTATE2|" + Convert.ToBase64String(bin) + "\n";
            MobSyncTrace.RecordWireSend("state", states.Count, line.Length);
            _ = SendLineSafe(line);
            return;
        }

        var textLine = MobWireCodec.BuildMobStatesLine(states);
        MobSyncTrace.RecordWireSend("state", states.Count, textLine.Length);
        _ = SendLineSafe(textLine);
    }

    public void SendMobMoves(IReadOnlyList<MobMoveSnapshot> moves)
    {
        if (_role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (moves == null || moves.Count == 0)
            return;

        var line = MobWireCodec.BuildMobMovesLine(moves);
        MobSyncTrace.RecordWireSend("move", moves.Count, line.Length);
        _ = SendLineSafe(line);
    }

    public void SendMobAttack(int mobIndex, string skillId, bool requiresTargetInArea, int? data, double x, double y, int targetUserId, int dir = 0, int generation = 0)
    {
        if (_role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (mobIndex < 0 || string.IsNullOrWhiteSpace(skillId))
            return;

        var attack = new MobAttack(mobIndex, skillId, requiresTargetInArea, data, x, y, targetUserId, dir, generation: generation);
        var line = MobWireCodec.BuildMobAttackLine(attack);
        _ = SendLineSafe(line);
    }

    /// <summary>Send event-based mob updates. Format: x, y, dir + events. Sent when something changes, not repeatedly.</summary>
    public void SendMobEvents(IReadOnlyList<MobEventUpdate> updates)
    {
        if (_role != NetRole.Host && _role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (updates == null || updates.Count == 0)
            return;

        var line = MobWireCodec.BuildMobEventsLine(updates);
        _ = SendLineSafe(line);
    }

    public void SendMobHit(int mobIndex, int hp, double x, double y, int generation = 0)
    {
        if (_role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"MOBHIT|{ID}|{mobIndex}|{hp}|{x}|{y}|{generation}");
        SendRaw(payload);
    }

    public void SendMobDie(int mobIndex, double x, double y, int generation = 0, string type = "")
    {
        if (_role != NetRole.Client && _role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var line = MobWireCodec.BuildMobDieLine(new MobDie(ID, mobIndex, x, y, generation, type));
        _ = SendLineSafe(line);
    }

    /// <summary>
    /// Host spawn table: NetId + type + spawn position so clients bind without using native/list ids.
    /// </summary>
    public void SendMobRegistry(int generation, IReadOnlyList<MobRegistryEntry> entries)
    {
        if (_role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (entries == null || entries.Count == 0)
            return;

        var line = MobWireCodec.BuildMobRegistryLine(generation, entries);
        _ = SendLineSafe(line);
    }

    public void SendMobDraw(int mobIndex, bool isOutOfGame, bool isOnScreen, int generation = 0)
    {
        if (_role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;
        if (mobIndex < 0)
            return;

        var line = MobWireCodec.BuildMobDrawLine(ID, mobIndex, isOutOfGame, isOnScreen, generation);
        _ = SendLineSafe(line);
    }

    public void SendMobDrawBatch(IReadOnlyList<MobDraw> draws)
    {
        if (_role != NetRole.Client)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;
        if (draws == null || draws.Count == 0)
            return;

        var line = MobWireCodec.BuildMobDrawLine(draws);
        _ = SendLineSafe(line);
    }

    public void SendExitReady(int doorCx, int doorCy, bool pressed, bool insideCircle, bool isOutOfGame, bool isOnScreen, string? levelId = null)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var state = new ExitReadyState(ID, doorCx, doorCy, pressed, insideCircle, isOutOfGame, isOnScreen, levelId);
        var line = BuildExitReadyLine(state);
        _ = SendLineSafe(line);
    }

    /// <summary>Host only: publish where a mid-run joiner should be placed (the host's own cell).</summary>
    public void SendHostSpawnAnchor(int cx, int cy, string? levelId)
    {
        if (_role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (string.IsNullOrWhiteSpace(levelId))
            return;

        var safeLevel = levelId
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        SendRaw(string.Create(CultureInfo.InvariantCulture, $"SPAWNANCHOR|{cx}|{cy}|{safeLevel}"));
    }

    /// <summary>Client: newest host spawn anchor, if one has arrived.</summary>
    public bool TryGetHostSpawnAnchor(out HostSpawnAnchor anchor)
    {
        lock (_sync)
        {
            if (_latestHostSpawnAnchor.HasValue)
            {
                anchor = _latestHostSpawnAnchor.Value;
                return true;
            }
        }

        anchor = default;
        return false;
    }

    /// <summary>Host only: publish the authoritative decision to run one level transition.</summary>
    public void SendExitTransitionCommit(long sequence, int doorCx, int doorCy, string? fromLevelId, string? destinationLevelId)
    {
        if (_role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (sequence <= 0)
            return;

        var commit = new ExitTransitionCommit(sequence, doorCx, doorCy, fromLevelId, destinationLevelId);
        _ = SendLineSafe(BuildExitCommitLine(commit));
        _log.Information(
            "[NetNode][ExitSync] Sent transition commit seq={Sequence} door={DoorCx}:{DoorCy} from={From} to={To}",
            sequence,
            doorCx,
            doorCy,
            fromLevelId ?? string.Empty,
            destinationLevelId ?? string.Empty);
    }

    public void SendBossCine(string levelId)
    {
        if (!HasAnyConnection())
            return;
        if (string.IsNullOrWhiteSpace(levelId))
            return;

        var safe = levelId.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        if (string.IsNullOrEmpty(safe))
            return;

        SendRaw($"BOSSCINE|{safe}");
    }

    public void SendBossHeroTeleport(double x, double y, int dir)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw(
            $"BOSSHEROTELE|{ID.ToString(CultureInfo.InvariantCulture)}|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{dir.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SendInterDoor(int userId, double x, double y, string action, bool broken, string levelId)
    {
        if (!HasAnyConnection())
            return;
        if (userId <= 0)
            return;
        if (string.IsNullOrWhiteSpace(action))
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"INTERDOOR|{userId}|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{action}|{(broken ? 1 : 0)}|{safeLevel}");
    }

    public void SendInterElevator(double x, double y)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw($"INTERELEV|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SendInterElevator(int userId, double x, double y, long sequence, string levelId)
    {
        if (!HasAnyConnection())
            return;
        if (userId <= 0)
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw(
            $"INTERELEV|{userId.ToString(CultureInfo.InvariantCulture)}|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{sequence.ToString(CultureInfo.InvariantCulture)}|{safeLevel}");
    }

    public void SendInterElevatorState(
        int userId,
        double anchorX,
        double anchorY,
        long sequence,
        double platformX,
        double platformY,
        bool moving,
        string levelId)
    {
        if (!HasAnyConnection())
            return;
        if (userId <= 0)
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw(
            $"INTERELEVSTATE|{userId.ToString(CultureInfo.InvariantCulture)}|{anchorX.ToString(CultureInfo.InvariantCulture)}|{anchorY.ToString(CultureInfo.InvariantCulture)}|{sequence.ToString(CultureInfo.InvariantCulture)}|{platformX.ToString(CultureInfo.InvariantCulture)}|{platformY.ToString(CultureInfo.InvariantCulture)}|{(moving ? 1 : 0)}|{safeLevel}");
    }

    public void SendInterPressurePlate(int userId, double x, double y, long sequence, string levelId)
    {
        if (!HasAnyConnection())
            return;
        if (userId <= 0)
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw(
            $"INTERPLATE|{userId.ToString(CultureInfo.InvariantCulture)}|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{sequence.ToString(CultureInfo.InvariantCulture)}|{safeLevel}");
    }

    public void SendInterTreasureChest(double x, double y, string levelId = "")
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"INTERCHEST|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{safeLevel}");
    }

    public void SendInterVineLadder(double x, double y, string levelId = "")
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"INTERVINELADDER|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{safeLevel}");
    }

    public void SendInterTeleport(double x, double y, string levelId = "")
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"INTERTELEPORT|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{safeLevel}");
    }

    public void SendInterBreakableGround(double x, double y, string levelId = "")
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"INTERBREAK|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{safeLevel}");
    }

    public void SendInterBossRuneUpdateCells(double x, double y, bool add, string levelId = "")
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"BOSSRUNE_UPDATE_CELLS|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{(add ? 1 : 0)}|{safeLevel}");
    }

    public void SendInterPortal(double x, double y, string action, string levelId = "")
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;
        if (string.IsNullOrWhiteSpace(action))
            return;

        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"INTERPORTAL|{action}|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{safeLevel}");
    }


    public void SendLobbyState(string username, string levelId, int seed, string progressSignature)
    {
        if (!HasAnyConnection())
            return;

        var safeUser = (username ?? "guest").Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        var safeLevel = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        var safeProgress = (progressSignature ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        var idPart = ID > 0 ? ID.ToString(CultureInfo.InvariantCulture) : "0";
        SendRaw($"LOBBYSTATE|{idPart}|{safeUser}|{safeLevel}|{seed.ToString(CultureInfo.InvariantCulture)}|{safeProgress}");
    }

    public void SendRuneProgress(string csvPermanentIds)
    {
        if (string.IsNullOrWhiteSpace(csvPermanentIds))
            return;

        var safe = csvPermanentIds.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
                _cachedHostRuneProgressPayload = safe;
        }

        // Cache even before a friend finishes connecting. Initial-state replay then puts the
        // host save's mobility/rune progression in front of GEN/RUNCOMMIT on every transport.
        if (!HasAnyConnection())
            return;

        SendRaw($"RUNEPROG|{safe}");
    }

    private void SendRaw(string payload)
    {
        var line = payload.EndsWith('\n') ? payload : payload + "\n";
        _ = SendLineSafe(line);
    }
}

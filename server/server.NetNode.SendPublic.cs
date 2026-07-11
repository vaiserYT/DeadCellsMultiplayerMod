using System.Globalization;
using DeadCellsMultiplayerMod;

public sealed partial class NetNode
{
    public void TickSend(double cx, double cy, int dir)
    {
        if (!HasAnyConnection()) return;
        if (ID <= 0) return;
        var line = BuildPosLine(ID, cx, cy, dir);
        _ = SendLineSafe(line);
    }

    public void LevelSend(int senderId, string lvl) => SendLevelId(senderId, lvl);

    public void SendSeed(int seed)
    {
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostSeed = seed;
            }
        }

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending seed {Seed}: no connected client", seed);
            return;
        }
        var line = $"SEED|{seed}\n";
        _ = SendLineSafe(line);
        _log.Information("[NetNode] Sent seed {Seed}", seed);
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

    public void SendCounters(string countersPayload)
    {
        return;
    }

    public void SendProgress(string progressPayload)
    {
        return;
    }

    public void SendBlueprints(string blueprintsPayload)
    {
        return;
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

    public void SendLevelSeed(string levelId, double seed)
    {
        var safeSeed = seed.ToString(CultureInfo.InvariantCulture);
        var safeId = (levelId ?? string.Empty).Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        var payload = $"{safeId}|{safeSeed}";
        if (_role == NetRole.Host)
        {
            lock (_hostCacheSync)
            {
                _cachedHostLevelSeedPayload = string.IsNullOrWhiteSpace(safeId) ? null : payload;
            }
        }

        if (string.IsNullOrWhiteSpace(safeId))
            return;

        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending level seed: no connected client");
            return;
        }

        SendRaw($"LSEED|{payload}");
        _log.Information("[NetNode] Sent level seed for {LevelId}", safeId);
    }

    public void SendLevelGraph(string levelId, string json)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending level graph: no connected client");
            lock (_hostCacheSync)
            {
                _cachedHostLevelGraphPayload = string.IsNullOrWhiteSpace(json) ? null : json;
                if (!string.IsNullOrWhiteSpace(levelId) && !string.IsNullOrWhiteSpace(json))
                    _cachedHostLevelGraphsByLevelId[levelId] = json;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
            return;

        lock (_hostCacheSync)
        {
            _cachedHostLevelGraphPayload = json;
            if (!string.IsNullOrWhiteSpace(levelId))
                _cachedHostLevelGraphsByLevelId[levelId] = json;
        }

        SendRaw("LGRAPH|" + json);
        _log.Information("[NetNode] Sent level graph ({Length} bytes)", json.Length);
    }

    public void SendGeneratePayload(string json)
    {
        if (!HasAnyConnection())
        {
            _log.Information("[NetNode] Skip sending generate payload: no connected client");
            return;
        }

        SendRaw("GEN|" + json);
        _log.Information("[NetNode] Sent Generate payload ({Length} bytes)", json.Length);
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

    public void SendChatMessage(string message)
    {
        if (!HasAnyConnection())
            return;

        var safe = SanitizeChatMessage(message);
        if (string.IsNullOrWhiteSpace(safe))
            return;

        var idPart = ID > 0 ? $"{ID}|" : string.Empty;
        SendRaw($"CHAT|{idPart}{safe}");
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

    public void SendKick()
    {
        if (!HasAnyConnection()) return;
        SendRaw("KICK");
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

        if (MobWireBinary.UseBinaryWire && MobWireBinary.TryBuildMobStatesBinary(states, out var bin) && bin != null)
        {
            var line = "MOBSTATE2|" + Convert.ToBase64String(bin) + "\n";
            _ = SendLineSafe(line);
            return;
        }

        var textLine = MobWireCodec.BuildMobStatesLine(states);
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
        _ = SendLineSafe(line);
    }

    public void SendMobCharges(IReadOnlyList<MobChargeSnapshot> charges)
    {
        if (_role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (charges == null || charges.Count == 0)
            return;

        var line = MobWireCodec.BuildMobChargesLine(charges);
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

    public void SendMobDie(int mobIndex, double x, double y, int generation = 0)
    {
        if (_role != NetRole.Client && _role != NetRole.Host)
            return;
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"MOBDIE|{ID}|{mobIndex}|{x}|{y}|{generation}");
        SendRaw(payload);
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

    public void SendExitReady(int doorCx, int doorCy, bool pressed, bool insideCircle, bool isOutOfGame, bool isOnScreen)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        var state = new ExitReadyState(ID, doorCx, doorCy, pressed, insideCircle, isOutOfGame, isOnScreen);
        var line = BuildExitReadyLine(state);
        _ = SendLineSafe(line);
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

    public void SendInterDoor(int userId, double x, double y, string action, bool broken)
    {
        if (!HasAnyConnection())
            return;
        if (userId <= 0)
            return;
        if (string.IsNullOrWhiteSpace(action))
            return;

        SendRaw($"INTERDOOR|{userId}|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{action}|{(broken ? 1 : 0)}");
    }

    public void SendInterElevator(double x, double y)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw($"INTERELEV|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SendInterPressurePlate(double x, double y)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw($"INTERPLATE|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SendInterTreasureChest(double x, double y)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw($"INTERCHEST|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SendInterVineLadder(double x, double y)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw($"INTERVINELADDER|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SendInterTeleport(double x, double y)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw($"INTERTELEPORT|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SendInterBreakableGround(double x, double y)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw($"INTERBREAK|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SendInterBossRuneUpdateCells(double x, double y, bool add)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;

        SendRaw($"BOSSRUNE_UPDATE_CELLS|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}|{(add ? 1 : 0)}");
    }

    public void SendInterPortal(double x, double y, string action)
    {
        if (!HasAnyConnection())
            return;
        if (ID <= 0)
            return;
        if (string.IsNullOrWhiteSpace(action))
            return;

        SendRaw($"INTERPORTAL|{action}|{x.ToString(CultureInfo.InvariantCulture)}|{y.ToString(CultureInfo.InvariantCulture)}");
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
        if (!HasAnyConnection())
            return;
        if (string.IsNullOrWhiteSpace(csvPermanentIds))
            return;

        var safe = csvPermanentIds.Replace("|", "/").Replace("\r", string.Empty).Replace("\n", string.Empty);
        SendRaw($"RUNEPROG|{safe}");
    }

    private void SendRaw(string payload)
    {
        var line = payload.EndsWith('\n') ? payload : payload + "\n";
        _ = SendLineSafe(line);
    }
}

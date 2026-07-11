using System.Globalization;
using System.Text;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Interaction;
using DeadCellsMultiplayerMod.AdvancedCoop;

public sealed partial class NetNode
{
    private bool TryHandleClientFastPathLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        try
        {
            if (line.StartsWith("HXSYNC|", StringComparison.Ordinal))
            {
                var payload = line["HXSYNC|".Length..];
                lock (_sync) _hasRemote = true;
                GameDataSync.ReceiveSerializerSync(payload);
                return true;
            }

            if (line.StartsWith("LSEED|", StringComparison.Ordinal))
            {
                var payload = line["LSEED|".Length..];
                lock (_sync) _hasRemote = true;
                GameDataSync.ReceiveLevelSeed(payload);
                return true;
            }

            if (line.StartsWith("LGRAPH|", StringComparison.Ordinal))
            {
                var payload = line["LGRAPH|".Length..];
                lock (_sync) _hasRemote = true;
                GameDataSync.ReceiveLevelGraph(payload);
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Warning("[NetNode] Fast-path line handling failed: {msg}", ex.Message);
        }

        return false;
    }

    private static bool TryReadBufferedLine(StringBuilder buffer, out string line)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\n')
                continue;

            line = buffer.ToString(0, i).Trim();
            buffer.Remove(0, i + 1);
            return true;
        }

        line = string.Empty;
        return false;
    }

    private bool HandleLine(string line, int? senderId, out string? forwardLine)
    {
        forwardLine = null;
        var forceSenderId = _role == NetRole.Host && senderId.HasValue;

        if (line.StartsWith("ID|"))
        {
            if (_role == NetRole.Host)
                return true;

            var part = line["ID|".Length..];
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
            {
                ID = parsedId;
                lock (_sync) _hasRemote = true;
                _log.Information("[NetNode] Assigned ID {Id}", ID);
            }
            return true;
        }

        if (line.StartsWith("WELCOME"))
        {
            lock (_sync) _hasRemote = true;
            return true;
        }

        if (line.StartsWith("HELLO"))
        {
            if (_role == NetRole.Client)
                return true;

            lock (_sync) _hasRemote = true;
            if (_role == NetRole.Host && _useSteamTransport && senderId.HasValue)
            {
                SteamClientConnection? steamConnection = null;
                lock (_clientsLock)
                {
                    _steamClients.TryGetValue(senderId.Value, out steamConnection);
                }

                if (steamConnection != null)
                    _ = Task.Run(() => SendInitialStateToSteamClient(steamConnection, forceSend: true));
            }
            return true;
        }

        if (line.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
        {
            lock (_sync) _hasRemote = true;
            return true;
        }

        if (line.StartsWith("SEED|"))
        {
            var partsSeed = line.Split('|');
            if (partsSeed.Length >= 2 && int.TryParse(partsSeed[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostSeed))
            {
                lock (_sync) _hasRemote = true;
                GameMenu.ReceiveHostRunSeed(hostSeed);
                _log.Information("[NetNode] Received host run seed {Seed}", hostSeed);
            }
            else
            {
                _log.Warning("[NetNode] Malformed SEED line: \"{line}\"");
            }
            return true;
        }

        if (line.StartsWith("HXSYNC|", StringComparison.Ordinal))
        {
            var payload = line["HXSYNC|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveSerializerSync(payload);
            return true;
        }

        if (line.StartsWith("BOSSRUNE|"))
        {
            var payload = line["BOSSRUNE|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveBossRune(payload);
            return true;
        }

        if (line.StartsWith("BOSSRUNE_UPDATE_CELLS|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["BOSSRUNE_UPDATE_CELLS|".Length..].Trim();
            var parts = payload.Split('|');
            if (parts.Length >= 3 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var addInt))
            {
                lock (_sync)
                {
                    _pendingBossRuneUpdateCells.Add(new InterBossRuneUpdateCellsEvent(x, y, addInt != 0));
                    _hasRemote = true;
                }
            }
            return true;
        }

        if (line.StartsWith("USER|"))
        {
            var payload = line["USER|".Length..];
            var effectiveId = ResolvePayloadId(payload, senderId, out var username);
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                int primaryId;
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Username = username;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                    primaryId = _primaryRemoteId;
                }

                if (effectiveId.Value == primaryId)
                    GameMenu.ReceiveRemoteUsername(username);

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildTaggedLine("USER", effectiveId.Value, username);
            }
            return true;
        }

        if (line.StartsWith("CHAT|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["CHAT|".Length..];
            ParseChatPayload(payload, out var parsedId, out var message);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;

            message = SanitizeChatMessage(message);
            if (effectiveId.HasValue && !string.IsNullOrWhiteSpace(message))
            {
                string? username;
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                    username = state.Username;
                    _pendingChatMessages.Add(new RemoteChatMessage(effectiveId.Value, username, message));
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildChatLine(effectiveId.Value, message);
            }

            return true;
        }


        if (line.StartsWith("LOBBYSTATE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["LOBBYSTATE|".Length..];
            lock (_sync) _hasRemote = true;
            CoopAdvancedHardening.ReceiveLobbyState(payload);
            if (_role == NetRole.Host && senderId.HasValue)
                forwardLine = line.EndsWith("\n", StringComparison.Ordinal) ? line : line + "\n";
            return true;
        }

        if (line.StartsWith("RUNEPROG|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["RUNEPROG|".Length..];
            lock (_sync) _hasRemote = true;
            CoopAdvancedHardening.ReceiveRuneProgress(payload);
            if (_role == NetRole.Host && senderId.HasValue)
                forwardLine = line.EndsWith("\n", StringComparison.Ordinal) ? line : line + "\n";
            return true;
        }

        if (line.StartsWith("HPMULT|"))
        {
            var payload = line["HPMULT|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveHpMultipliers(payload);
            return true;
        }

        if (line.StartsWith("LDESC|"))
        {
            var payload = line["LDESC|".Length..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveLevelDesc(payload);
            return true;
        }

        if (line.StartsWith("LSEED|"))
        {
            var payload = line["LSEED|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveLevelSeed(payload);
            return true;
        }

        if (line.StartsWith("LGRAPH|"))
        {
            var payload = line["LGRAPH|".Length..];
            lock (_sync) _hasRemote = true;
            GameDataSync.ReceiveLevelGraph(payload);
            return true;
        }

        if (line.StartsWith("SKIN|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["SKIN|".Length..];
            var effectiveId = ResolvePayloadId(payload, senderId, out var skin);
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                int primaryId;
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Skin = skin;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                    primaryId = _primaryRemoteId;
                }

                var skinId = effectiveId.Value;
                var skinValue = skin;
                GameMenu.EnqueueMainThreadCoalesced(string.Create(CultureInfo.InvariantCulture, $"net:skin:{skinId}"), () =>
                {
                    try
                    {
                        ModEntry.SetClientSkin(skinId, skinValue);
                        if (skinId == primaryId)
                            GameDataSync.ReceiveHeroSkin(skinValue);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[NetNode] Failed to handle hero skin: {msg}", ex.Message);
                    }
                });

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildTaggedLine("SKIN", effectiveId.Value, skin);
            }
            return true;
        }

        if (line.StartsWith("HEAD|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["HEAD|".Length..];
            var effectiveId = ResolvePayloadId(payload, senderId, out var skinHead);
            _log.Debug($"{skinHead}");
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                int primaryId;
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Head = skinHead;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                    primaryId = _primaryRemoteId;
                }

                var headId = effectiveId.Value;
                var headSkinValue = skinHead;
                GameMenu.EnqueueMainThreadCoalesced(string.Create(CultureInfo.InvariantCulture, $"net:head:{headId}"), () =>
                {
                    try
                    {
                        ModEntry.SetClientHeadSkin(headId, headSkinValue);
                        if (headId == primaryId)
                            GameDataSync.ReceiveHeroHeadSkin(headSkinValue);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[NetNode] Failed to handle hero skin: {msg}", ex.Message);
                    }
                });

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildTaggedLine("HEAD", effectiveId.Value, skinHead);
            }
            return true;
        }

        if (line.StartsWith("GEN|"))
        {
            var payload = line["GEN|".Length..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveGeneratePayload(payload);
            return true;
        }

        if (line.StartsWith("LEVEL|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["LEVEL|".Length..];
            var partsLevel = payload.Split(new[] { '|' }, 2);
            int? parsedId = null;
            string levelValue = payload;
            if (partsLevel.Length >= 2)
            {
                levelValue = partsLevel[1];
                if (int.TryParse(partsLevel[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevelRemoteId))
                {
                    parsedId = parsedLevelRemoteId;
                }
            }
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.LevelId = levelValue;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

            }
            return true;
        }

        if (line.StartsWith("ZROOM|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["ZROOM|".Length..];
            ParseRoomPayload(payload, out var parsedId, out var roomLevelValue, out var roomIdValue);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;

            if (effectiveId.HasValue && roomIdValue >= 0)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.RoomLevelId = roomLevelValue;
                    state.RoomId = roomIdValue;
                    state.HasRoom = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildRoomLine(effectiveId.Value, roomLevelValue, roomIdValue);
            }
            return true;
        }

        if (line.StartsWith("ANIM|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseAnimPayload(payload, out var parsedId, out var animName, out var q, out var gFlag);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Anim = animName;
                    state.AnimQueue = q;
                    state.AnimG = gFlag;
                    state.HasAnim = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildAnimLine(effectiveId.Value, animName, q, gFlag);
            }
            return true;
        }


        if (line.StartsWith("HEADANIM|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseHeadAnimPayload(payload, out var parsedId, out var animName);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.HeadAnim = animName;
                    state.HasHeadAnim = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildHeadAnimLine(effectiveId.Value, animName);
            }
            return true;
        }

        if (line.StartsWith("INV|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseWeaponPayload(payload, out var parsedId, out var kind, out var slot, out var permanentId, out var ammo);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.WeaponKind = kind;
                    state.WeaponSlot = slot;
                    state.WeaponPermanentId = permanentId;
                    state.WeaponAmmo = ammo ?? int.MinValue;
                    state.HasWeaponUpdate = true;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildWeaponLine("INV", effectiveId.Value, kind, slot, permanentId, ammo);
            }
            return true;
        }

        if (line.StartsWith("ATK|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseAttackPayload(payload, out var parsedId, out var kind, out var slot, out var permanentId, out var ammo, out var action);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    _pendingAttacks.Add(new RemoteAttack(effectiveId.Value, kind, slot, permanentId, ammo, action));
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildAttackLine(effectiveId.Value, kind, slot, permanentId, ammo, action);
            }
            return true;
        }

        if (line.StartsWith("HP|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line[(line.IndexOf('|') + 1)..];
            ParseHpPayload(payload, out var parsedId, out var life, out var maxLife, out var lif, out var bonusLife, out var recover);
            var effectiveId = parsedId ?? senderId;
            if (forceSenderId)
                effectiveId = senderId;
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.Life = life;
                    state.MaxLife = maxLife;
                    state.Lif = lif;
                    state.BonusLife = bonusLife;
                    state.Recover = recover;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildHpLine(effectiveId.Value, life, maxLife, lif, bonusLife, recover);
            }
            return true;
        }

        if (line.StartsWith("MOBSTATE2|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["MOBSTATE2|".Length..].TrimEnd('\r', '\n');
            var parsedStates = new List<MobStateSnapshot>();
            if (MobWireBinary.TryParseMobStatesBase64(payload, parsedStates))
            {
                lock (_sync)
                {
                    if (_role == NetRole.Host)
                    {
                        if (parsedStates.Count > 0)
                            _pendingMobStates.AddRange(parsedStates);
                    }
                    else
                    {
                        _pendingMobStates = parsedStates;
                    }

                    _hasRemote = true;
                }
            }

            return true;
        }

        if (line.StartsWith("MOBSTATE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["MOBSTATE|".Length..];
            var parsedStates = ParseMobStatesPayload(payload);
            lock (_sync)
            {
                if (_role == NetRole.Host)
                {
                    if (parsedStates.Count > 0)
                        _pendingMobStates.AddRange(parsedStates);
                }
                else
                {
                    _pendingMobStates = parsedStates;
                }
                _hasRemote = true;
            }
            return true;
        }

        if (line.StartsWith("MOBMOVE|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role != NetRole.Host)
            {
                var payload = line["MOBMOVE|".Length..];
                var parsedMoves = ParseMobMovesPayload(payload);
                lock (_sync)
                {
                    if (parsedMoves.Count > 0)
                    {
                        _pendingMobMoves.AddRange(parsedMoves);
                        _hasRemote = true;
                    }
                }
            }
            return true;
        }

        if (line.StartsWith("MOBCHARGE|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role != NetRole.Host)
            {
                var payload = line["MOBCHARGE|".Length..];
                var parsedCharges = ParseMobChargesPayload(payload);
                lock (_sync)
                {
                    if (parsedCharges.Count > 0)
                    {
                        _pendingMobCharges.AddRange(parsedCharges);
                        _hasRemote = true;
                    }
                }
            }
            return true;
        }

        if (line.StartsWith("MOBHIT|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role != NetRole.Host)
                return true;

            var payload = line["MOBHIT|".Length..];
            if (TryParseMobHitPayload(payload, senderId, forceSenderId, out var hit))
            {
                lock (_sync)
                {
                    _pendingMobHits.Add(hit);
                    _hasRemote = true;
                }
            }
            return true;
        }

        if (line.StartsWith("MOBDIE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["MOBDIE|".Length..];
            if (TryParseMobDiePayload(payload, senderId, forceSenderId, out var die))
            {
                lock (_sync)
                {
                    _pendingMobDies.Add(die);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = MobWireCodec.BuildMobDieLine(die);
            }
            return true;
        }

        if (line.StartsWith("MOBDRAW|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role != NetRole.Host)
                return true;

            var payload = line["MOBDRAW|".Length..];
            if (TryParseMobDrawPayload(payload, senderId, forceSenderId, out var draws))
            {
                lock (_sync)
                {
                    _pendingMobDraws.AddRange(draws);
                    _hasRemote = true;
                }
            }
            return true;
        }

        if (line.StartsWith("EXITREADY|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["EXITREADY|".Length..];
            if (TryParseExitReadyPayload(payload, senderId, forceSenderId, out var state))
            {
                lock (_sync)
                {
                    _pendingExitReadyStates.Add(state);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildExitReadyLine(state);
            }
            return true;
        }

        if (line.StartsWith("PDOWN|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["PDOWN|".Length..];
            if (TryParsePlayerDownPayload(payload, senderId, forceSenderId, out var state))
            {
                lock (_sync)
                {
                    _pendingPlayerDownStates.Add(state);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildPlayerDownLine(state);
            }
            return true;
        }

        if (line.StartsWith("PREVIVE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["PREVIVE|".Length..];
            if (TryParsePlayerRevivePayload(payload, senderId, forceSenderId, out var request))
            {
                lock (_sync)
                {
                    _pendingPlayerReviveRequests.Add(request);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildPlayerReviveLine(request);
            }
            return true;
        }

        if (line.StartsWith("BOSSCINE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["BOSSCINE|".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                var levelId = payload.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(levelId))
                {
                    lock (_sync)
                    {
                        _pendingBossCineLevelIds.Add(levelId);
                        _hasRemote = true;
                    }

                    if (_role == NetRole.Host && senderId.HasValue)
                        forwardLine = $"BOSSCINE|{levelId}\n";
                }
            }
            return true;
        }

        if (line.StartsWith("INTERDOOR|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERDOOR|".Length..];
            if (TryParseInterDoorPayload(payload, senderId, forceSenderId, out var ev))
            {
                lock (_sync)
                {
                    _pendingInterDoorEvents.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERDOOR|{ev.UserId}|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.Action}|{(ev.Broken ? 1 : 0)}\n";
            }
            return true;
        }

        if (line.StartsWith("INTERELEV|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERELEV|".Length..];
            if (TryParseInterElevatorPayload(payload, out var ev))
            {
                lock (_sync)
                {
                    _pendingInterElevatorEvents.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERELEV|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}\n";
            }
            return true;
        }

        if (line.StartsWith("INTERPLATE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERPLATE|".Length..];
            if (TryParseInterPressurePlatePayload(payload, out var ev))
            {
                lock (_sync)
                {
                    _pendingInterPressurePlateEvents.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERPLATE|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}\n";
            }
            return true;
        }

        if (line.StartsWith("INTERCHEST|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERCHEST|".Length..];
            if (TryParseInterTreasureChestPayload(payload, out var ev))
            {
                lock (_sync)
                {
                    _pendingInterTreasureChestEvents.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERCHEST|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}\n";
            }
            return true;
        }

        if (line.StartsWith("INTERVINELADDER|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERVINELADDER|".Length..];
            if (TryParseInterVineLadderPayload(payload, out var ev))
            {
                lock (_sync)
                {
                    _pendingInterVineLadderEvents.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERVINELADDER|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}\n";
            }
            return true;
        }

        if (line.StartsWith("INTERTELEPORT|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERTELEPORT|".Length..];
            if (TryParseInterTeleportPayload(payload, out var ev))
            {
                lock (_sync)
                {
                    _pendingInterTeleportEvents.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERTELEPORT|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}\n";
            }
            return true;
        }

        if (line.StartsWith("BOSSHEROTELE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["BOSSHEROTELE|".Length..];
            if (TryParseBossHeroTeleportPayload(payload, senderId, forceSenderId, out var ev))
            {
                lock (_sync)
                {
                    _pendingBossHeroTeleports.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                {
                    forwardLine =
                        $"BOSSHEROTELE|{ev.UserId.ToString(CultureInfo.InvariantCulture)}|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.Dir.ToString(CultureInfo.InvariantCulture)}\n";
                }
            }
            return true;
        }

        if (line.StartsWith("INTERBREAK|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERBREAK|".Length..];
            if (TryParseInterBreakableGroundPayload(payload, out var ev))
            {
                lock (_sync)
                {
                    _pendingInterBreakableGroundEvents.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERBREAK|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}\n";
            }
            return true;
        }

        if (line.StartsWith("INTERPORTAL|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERPORTAL|".Length..];
            if (TryParseInterPortalPayload(payload, out var ev))
            {
                lock (_sync)
                {
                    _pendingInterPortalEvents.Add(ev);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERPORTAL|{ev.Action}|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}\n";
            }
            return true;
        }

        if (line.StartsWith("MOBEVENT|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["MOBEVENT|".Length..];
            var parsed = ParseMobEventsPayload(payload);
            var effectiveUserId = forceSenderId && senderId.HasValue ? senderId.Value : (senderId ?? 0);
            var hasDieToForward = false;
            if (parsed.Count > 0)
            {
                lock (_sync)
                {
                    foreach (var u in parsed)
                    {
                        if (u.Events == null)
                            continue;
                        foreach (var ev in u.Events)
                        {
                            if (string.IsNullOrEmpty(ev))
                                continue;
                            if (ev.StartsWith("attack|", StringComparison.Ordinal) && _role != NetRole.Host)
                            {
                                if (TryParseMobAttackEvent(ev, u.Index, u.X, u.Y, u.Dir, u.Type, u.Generation, out var attack))
                                    _pendingMobAttacks.Add(attack);
                            }
                            else if (ev.StartsWith("hit|", StringComparison.Ordinal))
                            {
                                if (TryParseMobHitEvent(ev, u.Index, u.X, u.Y, effectiveUserId, u.Type, u.Generation, out var hit))
                                {
                                    _pendingMobHits.Add(hit);
                                }
                            }
                            else if (ev == "die")
                            {
                                var die = new MobDie(effectiveUserId, u.Index, u.X, u.Y, u.Generation);
                                _pendingMobDies.Add(die);
                                if (_role == NetRole.Host && senderId.HasValue)
                                    hasDieToForward = true;
                            }
                        }
                    }
                    _hasRemote = true;
                }
                if (hasDieToForward)
                    forwardLine = line;
            }
            return true;
        }

        if (line.StartsWith("MOBATK|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Host)
                return true;

            var payload = line["MOBATK|".Length..];
            if (TryParseMobAttackPayload(payload, out var attack))
            {
                lock (_sync)
                {
                    _pendingMobAttacks.Add(attack);
                    _hasRemote = true;
                }
            }
            return true;
        }

        if (line.StartsWith("DIED", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Host)
            {
                var _remoteId = senderId ?? 0;
                _log.Information("[NetNode] Remote hero died (id {Id})", _remoteId);
                forwardLine = "DIED\n";
            }
            GameDataSync.TriggerRemoteDeath();
            return true;
        }

        if (line.StartsWith("KICK"))
        {
            return false;
        }

        if (line.StartsWith("BYE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryParsePositionLine(line, senderId, out var remoteId, out var cx, out var cy, out var dir, out var hasDir))
        {
            if (forceSenderId && senderId.HasValue)
                remoteId = senderId.Value;
            int forwardDir = dir;
            lock (_sync)
            {
                var state = GetOrCreateRemoteLocked(remoteId);
                var prevX = state.X;
                var hadRemote = state.HasRemote;
                state.X = cx;
                state.Y = cy;
                if (hasDir)
                {
                    state.Dir = dir;
                }
                else if (hadRemote && cx != prevX)
                {
                    state.Dir = cx < prevX ? -1 : 1;
                }
                state.HasRemote = true;
                _hasRemote = true;
                if (_primaryRemoteId == 0)
                    _primaryRemoteId = remoteId;
                forwardDir = state.Dir;
            }
            if (_role == NetRole.Host && senderId.HasValue)
                forwardLine = BuildPosLine(remoteId, cx, cy, forwardDir);
        }

        return true;
    }
}

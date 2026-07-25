using System.Globalization;
using System.Text;
using DeadCellsMultiplayerMod;
using DeadCellsMultiplayerMod.Interaction;
using DeadCellsMultiplayerMod.AdvancedCoop;

public sealed partial class NetNode
{
    // Pending mob traffic can accumulate while the main thread is loading a level. Keep generous
    // bounded queues so a stalled or malformed peer cannot grow memory without limit. The oldest
    // entries are dropped first; generation fencing and newest-per-sync-id consumption make that
    // the correct failure mode for state/move snapshots.
    private const int PendingMobStateLimit = 8192;
    private const int PendingMobMoveLimit = 8192;
    private const int PendingMobHitLimit = 2048;
    private const int PendingMobDieLimit = 2048;
    private const int PendingMobAttackLimit = 2048;
    private const int PendingMobDrawLimit = 4096;
    private const int PendingNetworkLineLimit = 1024;
    private const int PendingAttackLimit = 1024;
    private const int PendingControlStateLimit = 256;
    private const int PendingInteractionLimit = 512;
    private const int PendingBossCineLimit = 64;
    private const int MaxProtocolLineChars = 1_100_000;
    private const int MaxIdentityFieldChars = 128;

    private static void AppendBoundedLocked<T>(List<T> target, IReadOnlyList<T> items, int maxCount)
    {
        if (items == null || items.Count == 0 || maxCount <= 0)
            return;

        var firstIncoming = Math.Max(0, items.Count - maxCount);
        var incomingCount = items.Count - firstIncoming;
        var overflow = target.Count + incomingCount - maxCount;
        if (overflow > 0)
        {
            if (overflow >= target.Count)
                target.Clear();
            else
                target.RemoveRange(0, overflow);
        }

        for (var i = firstIncoming; i < items.Count; i++)
            target.Add(items[i]);
    }

    private static void AddBoundedLocked<T>(List<T> target, T item, int maxCount)
    {
        if (maxCount <= 0)
            return;
        if (target.Count >= maxCount)
            target.RemoveRange(0, target.Count - maxCount + 1);
        target.Add(item);
    }

    /// <summary>
    /// Lines that can be fully handled on the network thread, without hopping to the game thread.
    /// </summary>
    /// <remarks>
    /// Mob traffic (MOBMOVE/MOBSTATE/MOBHIT/...) is by far the highest-volume part of the protocol
    /// and consists only of string parsing plus an append into a <c>_pending*</c> list under
    /// <c>_sync</c>. None of it touches the Haxe runtime. Routing it through the bounded
    /// main-thread action queue therefore bought nothing and cost a great deal: the parse ran on
    /// the render thread, and once that queue filled, back-pressure propagated all the way back
    /// into the socket read loop, so movement snapshots AND the reliable keyframes that repair
    /// them arrived late and bunched. That is invisible with two instances on one machine (the
    /// queue never fills) and severe over real latency and jitter.
    ///
    /// MobSync still consumes the pending lists on the game thread in its frame loop, and every
    /// mob packet carries a generation, so handling these lines earlier than surrounding protocol
    /// lines is safe: a snapshot for a level the peer has not built yet is rejected by the
    /// existing generation fence and re-sent by the host's periodic authoritative resync.
    /// </remarks>
    private bool TryHandleFastPathLine(string line, int? senderId)
    {
        if (IsSupersededNetworkSession())
            return true;
        if (string.IsNullOrEmpty(line))
            return false;

        if (TryHandleMobTrafficFastPath(line, senderId))
            return true;

        if (_role != NetRole.Client)
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

    private bool TryHandleMobTrafficFastPath(string line, int? senderId)
    {
        if (line.Length < 4 || !line.StartsWith("MOB", StringComparison.OrdinalIgnoreCase))
            return false;

        // A host relaying another peer's MOBEVENT die must keep its position in the ordered
        // forward stream, so that one case stays on the main-thread path.
        if (_role == NetRole.Host && line.StartsWith("MOBEVENT|", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var forceSenderId = _role == NetRole.Host && senderId.HasValue;
            if (!TryHandleMobTrafficLineCore(line, senderId, forceSenderId, out _))
                return false;

            NetNodeMobTrafficStats.RecordFastPathLine();
            return true;
        }
        catch (Exception ex)
        {
            // A malformed mob packet must never tear down the receive loop.
            _log.Warning("[NetNode] Mob fast-path line handling failed: {msg}", ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Shared handling for every MOB* protocol line. Pure parse plus a bounded enqueue; safe to run
    /// on either the network thread or the game thread.
    /// </summary>
    private bool TryHandleMobTrafficLineCore(string line, int? senderId, bool forceSenderId, out string? forwardLine)
    {
        forwardLine = null;

        if (line.StartsWith("MOBSTATE2|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["MOBSTATE2|".Length..].TrimEnd('\r', '\n');
            var parsedStates = new List<MobStateSnapshot>();
            if (MobWireBinary.TryParseMobStatesBase64(payload, parsedStates))
            {
                lock (_sync)
                {
                    // MOBSTATE packets are often split into multiple wire lines when a level has
                    // many mobs. The old client path replaced the pending list with each incoming
                    // packet, so after level transitions the client only consumed the final chunk
                    // of the host's bootstrap/full-resync. First Prison worked because it usually
                    // fit in one packet; bigger next levels did not. Always append and let
                    // TryConsumeMobStates swap/clear the accumulated frame batch. Generation checks
                    // in MobSync still reject stale old-level states.
                    if (parsedStates.Count > 0)
                        AppendBoundedLocked(_pendingMobStates, parsedStates, PendingMobStateLimit);

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
                // MOBSTATE packets can be chunked. Appending on the client is required so a
                // full next-level mob bootstrap is consumed as one accumulated frame batch instead
                // of keeping only the last chunk.
                if (parsedStates.Count > 0)
                    AppendBoundedLocked(_pendingMobStates, parsedStates, PendingMobStateLimit);
                _hasRemote = true;
            }
            return true;
        }

        if (line.StartsWith("MOBREG|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Host)
                return true;

            var payload = line["MOBREG|".Length..];
            if (TryParseMobRegistryPayload(payload, out var parsedRegistry) && parsedRegistry.Count > 0)
            {
                lock (_sync)
                {
                    AppendBoundedLocked(_pendingMobRegistry, parsedRegistry, PendingMobStateLimit);
                    _hasRemote = true;
                }
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
                        AppendBoundedLocked(_pendingMobMoves, parsedMoves, PendingMobMoveLimit);
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
                    AddBoundedLocked(_pendingMobHits, hit, PendingMobHitLimit);
                    _hasRemote = true;
                }
            }
            return true;
        }

        if (line.StartsWith("MOBDIE|", StringComparison.OrdinalIgnoreCase))
        {
            // Death is host-authoritative. A client may report damage intent, never a completed
            // death; do not enqueue or relay a spoofed/stale client MOBDIE to other peers.
            if (_role == NetRole.Host)
                return true;

            var payload = line["MOBDIE|".Length..];
            if (TryParseMobDiePayload(payload, senderId, forceSenderId, out var die))
            {
                lock (_sync)
                {
                    AddBoundedLocked(_pendingMobDies, die, PendingMobDieLimit);
                    _hasRemote = true;
                }

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
                    AppendBoundedLocked(_pendingMobDraws, draws, PendingMobDrawLimit);
                    _hasRemote = true;
                }
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
                                    AddBoundedLocked(_pendingMobAttacks, attack, PendingMobAttackLimit);
                            }
                            else if (ev.StartsWith("hit|", StringComparison.Ordinal))
                            {
                                if (TryParseMobHitEvent(ev, u.Index, u.X, u.Y, effectiveUserId, u.Type, u.Generation, out var hit))
                                {
                                    AddBoundedLocked(_pendingMobHits, hit, PendingMobHitLimit);
                                }
                            }
                            else if (ev == "die")
                            {
                                var die = new MobDie(effectiveUserId, u.Index, u.X, u.Y, u.Generation, u.Type);
                                AddBoundedLocked(_pendingMobDies, die, PendingMobDieLimit);
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
                    AddBoundedLocked(_pendingMobAttacks, attack, PendingMobAttackLimit);
                    _hasRemote = true;
                }
            }
            return true;
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

    private static string ClampProtocolText(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars <= 0)
            return string.Empty;
        return value.Length <= maxChars ? value : value[..maxChars];
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
                lock (_sync)
                {
                    _hasRemote = true;
                    _connectedClientCount = 1;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = 1;
                }
                _log.Information("[NetNode] Assigned ID {Id}", ID);
                if (!_useSteamTransport)
                {
                    GameMenu.EnqueueCriticalMainThreadCoalesced("net:client-connected", () =>
                    {
                        if (!IsCurrentNetworkSession())
                            return;
                        GameMenu.SetRole(_role);
                        GameMenu.NotifyRemoteConnected(_role);
                    });
                }
            }
            return true;
        }

        if (line.StartsWith("WELCOME", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Client && RejectIncompatiblePeer(line, "WELCOME"))
                return false;
            lock (_sync) _hasRemote = true;
            return true;
        }

        if (line.StartsWith("HELLO", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Client)
                return true;

            if (RejectIncompatiblePeer(line, "HELLO"))
                return false;

            if (_role == NetRole.Host && senderId.HasValue)
                CompleteHostHandshake(senderId.Value);

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

        if (line.StartsWith(RunLaunchWireCodec.CommitTag + "|", StringComparison.Ordinal))
        {
            var payload = line[(RunLaunchWireCodec.CommitTag.Length + 1)..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveRunLaunchCommitPayload(payload);
            return true;
        }

        if (line.StartsWith(RunLaunchWireCodec.AckTag + "|", StringComparison.Ordinal))
        {
            var payload = line[(RunLaunchWireCodec.AckTag.Length + 1)..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveRunLaunchAckPayload(payload);
            return true;
        }

        if (line.StartsWith(RunLaunchWireCodec.ExecuteTag + "|", StringComparison.Ordinal))
        {
            var payload = line[(RunLaunchWireCodec.ExecuteTag.Length + 1)..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveRunLaunchExecutePayload(payload);
            return true;
        }

        if (line.StartsWith(RunLaunchWireCodec.QueuedTag + "|", StringComparison.Ordinal))
        {
            var payload = line[(RunLaunchWireCodec.QueuedTag.Length + 1)..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveRunLaunchQueuedPayload(payload);
            return true;
        }

        if (line.StartsWith(RunLaunchWireCodec.ReadyTag + "|", StringComparison.Ordinal))
        {
            var payload = line[(RunLaunchWireCodec.ReadyTag.Length + 1)..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveRunLevelReadyPayload(payload);
            return true;
        }

        if (line.StartsWith(RunLaunchWireCodec.CancelTag + "|", StringComparison.Ordinal))
        {
            var payload = line[(RunLaunchWireCodec.CancelTag.Length + 1)..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveRunLaunchCancelPayload(payload);
            return true;
        }

        if (line.StartsWith("SEED|"))
        {
            var partsSeed = line.Split('|');
            if (partsSeed.Length >= 4 &&
                int.TryParse(partsSeed[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) &&
                int.TryParse(partsSeed[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostSeed))
            {
                var launchKind = partsSeed[3];
                lock (_sync) _hasRemote = true;
                GameMenu.ReceiveHostRunSeed(sequence, hostSeed, launchKind);
                _log.Information(
                    "[NetNode] Received host run seed seq={Sequence} seed={Seed} launch={LaunchKind}",
                    sequence,
                    hostSeed,
                    launchKind);
            }
            else
            {
                _log.Warning("[NetNode] Malformed SEED line: \"{line}\"");
            }
            return true;
        }

        if (line.StartsWith("RESTART|", StringComparison.Ordinal))
        {
            var payload = line["RESTART|".Length..];
            if (int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var restartSeed))
            {
                lock (_sync) _hasRemote = true;
                GameMenu.ReceiveHostRunRestart(restartSeed);
            }
            else
            {
                _log.Warning("[NetNode] Malformed RESTART line: \"{line}\"");
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
            if (TryParseInterBossRuneUpdateCellsPayload(payload, out var ev))
            {
                lock (_sync)
                {
                    AddBoundedLocked(_pendingBossRuneUpdateCells, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                {
                    forwardLine =
                        $"BOSSRUNE_UPDATE_CELLS|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{(ev.Add ? 1 : 0)}|{ev.LevelId}\n";
                }
            }
            return true;
        }

        if (line.StartsWith("USER|"))
        {
            var payload = line["USER|".Length..];
            var effectiveId = ResolvePayloadId(payload, senderId, out var username);
            username = ClampProtocolText(username, MaxIdentityFieldChars);
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

        if (line.StartsWith("READY|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["READY|".Length..];
            var parts = payload.Split('|');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedReadyId))
            {
                var effectiveId = forceSenderId ? senderId : parsedReadyId;
                if (effectiveId.HasValue)
                {
                    var ready = string.Equals(parts[1], "1", StringComparison.Ordinal);
                    lock (_sync)
                    {
                        var state = GetOrCreateRemoteLocked(effectiveId.Value);
                        state.Ready = ready;
                        state.HasRemote = true;
                        _hasRemote = true;
                        if (_primaryRemoteId == 0)
                            _primaryRemoteId = effectiveId.Value;
                    }

                    GameMenu.ReceiveRemoteReady(effectiveId.Value, ready);

                    if (_role == NetRole.Host && senderId.HasValue)
                        forwardLine = BuildReadyLine(effectiveId.Value, ready);
                }
            }

            return true;
        }

        if (line.StartsWith("COOPID|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["COOPID|".Length..];
            var effectiveId = ResolvePayloadId(payload, senderId, out var coopPayload);
            if (forceSenderId)
                effectiveId = senderId;

            ParseCoopStatePayload(coopPayload, out var coopId, out var hasContinueSave);
            if (effectiveId.HasValue)
            {
                lock (_sync)
                {
                    var state = GetOrCreateRemoteLocked(effectiveId.Value);
                    state.CoopId = coopId;
                    state.HasContinueSave = hasContinueSave;
                    state.HasRemote = true;
                    _hasRemote = true;
                    if (_primaryRemoteId == 0)
                        _primaryRemoteId = effectiveId.Value;
                }

                GameMenu.ReceiveRemoteCoopState(effectiveId.Value, coopId, hasContinueSave);

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildCoopStateLine(effectiveId.Value, coopId, hasContinueSave);
            }

            return true;
        }

        if (line.StartsWith("LAUNCHMODE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["LAUNCHMODE|".Length..];
            var parts = payload.Split('|');
            if (parts.Length >= 6 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var actionValue))
            {
                var custom = string.Equals(parts[1], "1", StringComparison.Ordinal);
                var streamEnabled = string.Equals(parts[2], "1", StringComparison.Ordinal);
                var newCoopWorldPrepared = string.Equals(parts[3], "1", StringComparison.Ordinal);
                var coopId = SanitizeProtocolToken(parts[4], 128);
                var hostHasContinueSave = string.Equals(parts[5], "1", StringComparison.Ordinal);

                lock (_sync)
                    _hasRemote = true;

                GameMenu.ReceiveLaunchMode(
                    actionValue,
                    custom,
                    streamEnabled,
                    newCoopWorldPrepared,
                    coopId,
                    hostHasContinueSave);
            }
            else
            {
                _log.Warning("[NetNode] Malformed LAUNCHMODE line: \"{line}\"", line);
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
            // Permanent progression belongs to the host's selected save. Clients receive that
            // state for the session, but their own save never imports unlocks into the host.
            if (_role != NetRole.Host)
                CoopAdvancedHardening.ReceiveRuneProgress(payload);
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
            skin = ClampProtocolText(skin, MaxIdentityFieldChars);
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
                    if (!IsCurrentNetworkSession())
                        return;
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
            skinHead = ClampProtocolText(skinHead, MaxIdentityFieldChars);
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
                    if (!IsCurrentNetworkSession())
                        return;
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

        if (line.StartsWith("CGDATA|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["CGDATA|".Length..];
            lock (_sync) _hasRemote = true;
            GameMenu.ReceiveCustomGameData(payload);
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
                levelValue = ClampProtocolText(partsLevel[1], MaxIdentityFieldChars);
                if (int.TryParse(partsLevel[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevelRemoteId))
                {
                    parsedId = parsedLevelRemoteId;
                }
            }
            levelValue = ClampProtocolText(levelValue, MaxIdentityFieldChars);
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
                    AddBoundedLocked(_pendingAttacks, new RemoteAttack(effectiveId.Value, kind, slot, permanentId, ammo, action), PendingAttackLimit);
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

        // Mob traffic is pure parse + enqueue and never touches the Haxe runtime, so it is handled
        // by one shared core that both this ordered main-thread path and the network-thread fast
        // path can call. Whichever path reaches a given line first is the only one that runs it.
        if (TryHandleMobTrafficLineCore(line, senderId, forceSenderId, out var mobForwardLine))
        {
            forwardLine = mobForwardLine;
            return true;
        }

        if (line.StartsWith("EXITREADY|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["EXITREADY|".Length..];
            if (TryParseExitReadyPayload(payload, senderId, forceSenderId, out var state))
            {
                lock (_sync)
                {
                    AddBoundedLocked(_pendingExitReadyStates, state, PendingControlStateLimit);
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
                    AddBoundedLocked(_pendingPlayerDownStates, state, PendingControlStateLimit);
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
                    AddBoundedLocked(_pendingPlayerReviveRequests, request, PendingControlStateLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = BuildPlayerReviveLine(request);
            }
            return true;
        }

        if (line.StartsWith("BOSSCINE|", StringComparison.OrdinalIgnoreCase))
        {
            // Boss-room presentation is host-authoritative. Never enqueue or relay a client-origin
            // intro back to the listen server/other peers; doing so can restart a live arena and
            // freeze both the host boss and every player behind a second cinematic lock.
            if (_role == NetRole.Host)
                return true;

            var payload = line["BOSSCINE|".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                var levelId = payload.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(levelId))
                {
                    lock (_sync)
                    {
                        AddBoundedLocked(_pendingBossCineLevelIds, levelId, PendingBossCineLimit);
                        _hasRemote = true;
                    }

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
                    AddBoundedLocked(_pendingInterDoorEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERDOOR|{ev.UserId}|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.Action}|{(ev.Broken ? 1 : 0)}|{ev.LevelId}\n";
            }
            return true;
        }

        if (line.StartsWith("INTERELEV|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERELEV|".Length..];
            if (TryParseInterElevatorPayload(payload, senderId, forceSenderId, out var ev))
            {
                lock (_sync)
                {
                    AddBoundedLocked(_pendingInterElevatorEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                {
                    forwardLine =
                        $"INTERELEV|{ev.UserId.ToString(CultureInfo.InvariantCulture)}|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.Sequence.ToString(CultureInfo.InvariantCulture)}|{ev.LevelId}\n";
                }
            }
            return true;
        }

        if (line.StartsWith("INTERELEVSTATE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERELEVSTATE|".Length..];
            if (TryParseInterElevatorStatePayload(payload, senderId, forceSenderId, out var ev))
            {
                lock (_sync)
                {
                    AddBoundedLocked(_pendingInterElevatorStateEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                {
                    forwardLine =
                        $"INTERELEVSTATE|{ev.UserId.ToString(CultureInfo.InvariantCulture)}|{ev.AnchorX.ToString(CultureInfo.InvariantCulture)}|{ev.AnchorY.ToString(CultureInfo.InvariantCulture)}|{ev.Sequence.ToString(CultureInfo.InvariantCulture)}|{ev.PlatformX.ToString(CultureInfo.InvariantCulture)}|{ev.PlatformY.ToString(CultureInfo.InvariantCulture)}|{(ev.Moving ? 1 : 0)}|{ev.LevelId}\n";
                }
            }
            return true;
        }

        if (line.StartsWith("INTERPLATE|", StringComparison.OrdinalIgnoreCase))
        {
            var payload = line["INTERPLATE|".Length..];
            if (TryParseInterPressurePlatePayload(payload, senderId, forceSenderId, out var ev))
            {
                lock (_sync)
                {
                    AddBoundedLocked(_pendingInterPressurePlateEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERPLATE|{ev.UserId.ToString(CultureInfo.InvariantCulture)}|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.Sequence.ToString(CultureInfo.InvariantCulture)}|{ev.LevelId}\n";
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
                    AddBoundedLocked(_pendingInterTreasureChestEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERCHEST|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.LevelId}\n";
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
                    AddBoundedLocked(_pendingInterVineLadderEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERVINELADDER|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.LevelId}\n";
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
                    AddBoundedLocked(_pendingInterTeleportEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERTELEPORT|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.LevelId}\n";
            }
            return true;
        }

        if (line.StartsWith("BOSSHEROTELE|", StringComparison.OrdinalIgnoreCase))
        {
            if (_role == NetRole.Host)
                return true;

            var payload = line["BOSSHEROTELE|".Length..];
            if (TryParseBossHeroTeleportPayload(payload, senderId, forceSenderId, out var ev))
            {
                lock (_sync)
                {
                    AddBoundedLocked(_pendingBossHeroTeleports, ev, PendingInteractionLimit);
                    _hasRemote = true;
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
                    AddBoundedLocked(_pendingInterBreakableGroundEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERBREAK|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.LevelId}\n";
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
                    AddBoundedLocked(_pendingInterPortalEvents, ev, PendingInteractionLimit);
                    _hasRemote = true;
                }

                if (_role == NetRole.Host && senderId.HasValue)
                    forwardLine = $"INTERPORTAL|{ev.Action}|{ev.X.ToString(CultureInfo.InvariantCulture)}|{ev.Y.ToString(CultureInfo.InvariantCulture)}|{ev.LevelId}\n";
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
                state.HasPosition = true;
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

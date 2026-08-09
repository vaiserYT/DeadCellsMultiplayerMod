using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using dc;
using dc.en;
using dc.hl.types;
using dc.pr;
using dc.tool;
using DeadCellsMultiplayerMod.Interface.ModuleInitializing;
using DeadCellsMultiplayerMod.MultiplayerModUI.lifeUI;
using HaxeProxy.Runtime;
using ModCore.Events;
using ModCore.Events.Interfaces.Game;
using ModCore.Events.Interfaces.Game.Hero;
using ModCore.Utilities;
using Serilog;

namespace DeadCellsMultiplayerMod.AdvancedCoop;

/// <summary>
/// Lobby heartbeat and permanent unlock progression layer on top of the multiplayer base.
/// This is not enemy/mob sync — combat entities are owned by <c>MobsSynchronization</c>.
/// It deliberately avoids constructing fake heroes/items during HeroInit.
/// </summary>
public sealed class CoopAdvancedHardening :
    IEventReceiver,
    IOnAdvancedModuleInitializing,
    IOnFrameUpdate,
    IOnHeroUpdate
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> PendingPermanentItems = new(StringComparer.OrdinalIgnoreCase);
    private static ILogger? _log;
    private static long _nextLobbyHeartbeatTicks;
    private static long _nextProgressSyncTicks;
    private static long _nextProgressFullResendTicks;
    private static long _nextHudStatusTicks;
    private static string _lastSentProgress = string.Empty;
    private static string _lastAppliedProgress = string.Empty;
    private static int _lastKnownRemoteCount = -1;
    private static bool _wasConnected;
    private static NetNode? _hudSessionNet;
    private static bool _connectedHudMessageShown;

    private const double LobbyHeartbeatSeconds = 0.50;
    private const double ProgressSyncSeconds = 1.50;
    private const double ProgressFullResendSeconds = 10.0;
    private const double HudStatusSeconds = 3.00;
    private const int MaxPermanentProgressItems = 4096;
    private const int MaxPermanentItemIdChars = 128;

    public CoopAdvancedHardening(ModEntry entry)
    {
        _log = entry.Logger;
        EventSystem.AddReceiver(this);
    }

    void IOnAdvancedModuleInitializing.OnAdvancedModuleInitializing(ModEntry entry)
    {
        _log = entry.Logger;
        entry.Logger.Information("\x1b[32m[[CoopAdvancedHardening] Initializing advanced co-op hardening...]\x1b[0m ");
    }

    void IOnFrameUpdate.OnFrameUpdate(double dt)
    {
        var net = GameMenu.NetRef;
        if (net == null || !net.IsAlive)
        {
            _wasConnected = false;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_nextLobbyHeartbeatTicks == 0 || now >= _nextLobbyHeartbeatTicks)
        {
            _nextLobbyHeartbeatTicks = now + SecondsToTicks(LobbyHeartbeatSeconds);
            SendLobbyHeartbeat(net);
            GameMenu.RefreshRoomStatusMenuIfVisible();
        }

        if (_nextProgressSyncTicks == 0 || now >= _nextProgressSyncTicks)
        {
            _nextProgressSyncTicks = now + SecondsToTicks(ProgressSyncSeconds);
            SendPermanentProgress(net);
            ApplyPendingPermanentProgress();
        }

        if (_nextHudStatusTicks == 0 || now >= _nextHudStatusTicks)
        {
            _nextHudStatusTicks = now + SecondsToTicks(HudStatusSeconds);
            PushConnectionHudStatus(net);
        }
    }

    void IOnHeroUpdate.OnHeroUpdate(double dt)
    {
        ApplyPendingPermanentProgress();
    }

    private static long SecondsToTicks(double seconds) => (long)(Stopwatch.Frequency * seconds);

    private static void SendLobbyHeartbeat(NetNode net)
    {
        try
        {
            var level = ModEntry.me?._level?.map?.id?.ToString() ?? ModEntry.Instance?.levelId ?? string.Empty;
            var seed = GameMenu.TryGetKnownSeed(out var knownSeed) ? knownSeed : 0;
            net.SendLobbyState(GameMenu.Username, level, seed, GetLocalPermanentProgressSignature());
        }
        catch (Exception ex)
        {
            _log?.Warning("[CoopAdvanced] Lobby heartbeat failed: {Message}", ex.Message);
        }
    }

    internal static void PrimeHostProgressSnapshot(NetNode? net)
    {
        if (net == null || !net.IsHost)
            return;

        try
        {
            var payload = BuildLocalPermanentProgressPayload();
            if (string.IsNullOrWhiteSpace(payload))
                return;

            // This may run before any friend is fully connected. NetNode caches the payload even
            // with zero peers so TCP and Steam initial-state replay can deliver host progression
            // before GEN/RUNCOMMIT and before the client's first procedural generation.
            _lastSentProgress = payload;
            _nextProgressFullResendTicks = Stopwatch.GetTimestamp() + SecondsToTicks(ProgressFullResendSeconds);
            net.SendRuneProgress(payload);
        }
        catch (Exception ex)
        {
            _log?.Warning("[CoopAdvanced] Initial host progress snapshot failed: {Message}", ex.Message);
        }
    }

    private static void SendPermanentProgress(NetNode net)
    {
        if (net == null || !net.IsHost)
            return;

        try
        {
            var payload = BuildLocalPermanentProgressPayload();
            if (string.IsNullOrWhiteSpace(payload))
                return;

            var now = Stopwatch.GetTimestamp();
            var changed = !string.Equals(payload, _lastSentProgress, StringComparison.Ordinal);
            var fullResendDue = _nextProgressFullResendTicks == 0 || now >= _nextProgressFullResendTicks;
            if (!changed && !fullResendDue)
                return;

            _lastSentProgress = payload;
            _nextProgressFullResendTicks = now + SecondsToTicks(ProgressFullResendSeconds);
            net.SendRuneProgress(payload);
        }
        catch (Exception ex)
        {
            _log?.Warning("[CoopAdvanced] Progress sync send failed: {Message}", ex.Message);
        }
    }

    private static void PushConnectionHudStatus(NetNode net)
    {
        try
        {
            if (!ReferenceEquals(_hudSessionNet, net))
            {
                _hudSessionNet = net;
                _connectedHudMessageShown = false;
                _lastKnownRemoteCount = -1;
                _wasConnected = false;
            }

            var connected = net.HasRemote;
            var remoteCount = net.IsHost ? NetNode.ConnectedClientCount : (connected ? 1 : 0);
            if (connected == _wasConnected && remoteCount == _lastKnownRemoteCount)
                return;

            var previousRemoteCount = _lastKnownRemoteCount;
            _wasConnected = connected;
            _lastKnownRemoteCount = remoteCount;
            if (connected)
            {
                // HasRemote can briefly flap while changing levels or while Steam renegotiates its
                // P2P route. Treat the notification as session state, not a heartbeat, so chat is
                // never flooded with repeated "connected" messages during an otherwise healthy run.
                if (!_connectedHudMessageShown || (net.IsHost && remoteCount > Math.Max(0, previousRemoteCount)))
                {
                    var status = net.IsHost
                        ? string.Format(CultureInfo.CurrentCulture, GameMenu.Localize("Co-op: {0} friend(s) connected"), remoteCount)
                        : GameMenu.Localize("Co-op: connected to host");
                    MultiplayerUI.PushSystemMessage(status, 4.0, 1.0);
                    _connectedHudMessageShown = true;
                }
            }
        }
        catch
        {
        }
    }

    public static void ReceiveLobbyState(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        // Live-room heartbeat only. Username updates are applied when the value actually changes
        // (ReceiveRemoteUsername is change-gated) so this path must not spam logs/UI refreshes.
        try
        {
            var parts = payload.Split('|');
            var nameIndex = parts.Length >= 2 ? 1 : 0;
            if (parts.Length > nameIndex && !string.IsNullOrWhiteSpace(parts[nameIndex]))
            {
                var name = parts[nameIndex].Trim();
                if (name.Length > 64)
                    name = name[..64];
                GameMenu.ReceiveRemoteUsername(name);
            }
        }
        catch (Exception ex)
        {
            _log?.Warning("[CoopAdvanced] Lobby state parse failed: {Message}", ex.Message);
        }
    }

    public static void ReceiveRuneProgress(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        lock (Sync)
        {
            var processed = 0;
            foreach (var raw in payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (processed++ >= MaxPermanentProgressItems || PendingPermanentItems.Count >= MaxPermanentProgressItems)
                    break;

                var id = SanitizePermanentItemId(raw);
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (!IsProgressPermanentItem(id))
                    continue;
                PendingPermanentItems.Add(id);
            }
        }

        // Initial-state replay deliberately sends RUNEPROG before GEN/RUNCOMMIT. Apply that
        // snapshot on the game thread immediately instead of waiting for the 1.5s heartbeat: on a
        // fresh/different save the client's first LevelGen can otherwise start before the host's
        // mobility/rune state is visible, recreating the old "same seed but only same save works"
        // race. The coalesced action is also safe when no User exists yet; the normal frame/hero
        // retry keeps the pending items until one becomes available.
        GameMenu.EnqueueCriticalMainThreadCoalesced(
            "coop:apply-host-progress",
            ApplyPendingPermanentProgress);
    }

    private static void ApplyPendingPermanentProgress()
    {
        // During level transitions no User may exist. Do not consume the only copy of a received
        // unlock packet until there is a valid destination to apply it to.
        var user = GetUser();
        if (user == null)
            return;

        string[] pending;
        lock (Sync)
        {
            if (PendingPermanentItems.Count == 0)
                return;
            pending = PendingPermanentItems.ToArray();
            PendingPermanentItems.Clear();
        }

        try
        {
            var meta = user.itemMeta ?? new ItemMetaManager(user);
            meta.itemProgress ??= (ArrayObj)ArrayUtils.CreateDyn().array;
            meta.permanentItems ??= (ArrayObj)ArrayUtils.CreateDyn().array;
            user.itemMeta = meta;

            var added = 0;
            foreach (var id in pending)
            {
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var hx = id.AsHaxeString();
                if (meta.hasPermanentItem(hx))
                    continue;
                if (meta.addPermanentItem(hx))
                    added++;
            }

            if (added > 0)
            {
                var sig = string.Join(",", pending.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                if (!string.Equals(sig, _lastAppliedProgress, StringComparison.Ordinal))
                {
                    _lastAppliedProgress = sig;
                    MultiplayerUI.PushSystemMessage(
                        string.Format(CultureInfo.CurrentCulture, GameMenu.Localize("Co-op progression synced: +{0} unlock(s)"), added),
                        6.0,
                        1.5);
                    _log?.Information("[CoopAdvanced] Applied {Count} synced permanent unlocks", added);
                }
            }
        }
        catch (Exception ex)
        {
            // Hashlink objects can be temporarily unavailable during a transition. Requeue instead
            // of losing progression permanently; the next Hero/Frame update will retry.
            lock (Sync)
            {
                foreach (var id in pending)
                {
                    if (PendingPermanentItems.Count >= MaxPermanentProgressItems)
                        break;
                    PendingPermanentItems.Add(id);
                }
            }
            _log?.Warning("[CoopAdvanced] Applying permanent progress failed: {Message}", ex.Message);
        }
    }

    private static string BuildLocalPermanentProgressPayload()
    {
        var ids = GetLocalPermanentProgressIds();
        return ids.Count == 0 ? string.Empty : string.Join(",", ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private static string GetLocalPermanentProgressSignature()
    {
        var ids = GetLocalPermanentProgressIds();
        return ids.Count.ToString(CultureInfo.InvariantCulture);
    }

    private static List<string> GetLocalPermanentProgressIds()
    {
        var result = new List<string>();
        var user = GetUser();
        var arr = user?.itemMeta?.permanentItems;
        if (arr == null)
            return result;

        try
        {
            for (int i = 0; i < arr.length; i++)
            {
                var id = SanitizePermanentItemId(arr.getDyn(i)?.ToString() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (!IsProgressPermanentItem(id))
                    continue;
                if (!result.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)))
                    result.Add(id);
            }
        }
        catch
        {
        }

        return result;
    }

    internal static void ResetSessionState()
    {
        lock (Sync)
        {
            PendingPermanentItems.Clear();
            _lastSentProgress = string.Empty;
            _lastAppliedProgress = string.Empty;
        }

        _nextLobbyHeartbeatTicks = 0;
        _nextProgressSyncTicks = 0;
        _nextProgressFullResendTicks = 0;
        _nextHudStatusTicks = 0;
        _lastKnownRemoteCount = -1;
        _wasConnected = false;
        _hudSessionNet = null;
        _connectedHudMessageShown = false;
    }

    private static User? GetUser()
    {
        try { if (ModEntry.me?._level?.game?.user != null) return ModEntry.me._level.game.user; } catch { }
        try { if (ModEntry.Instance?.game?.user != null) return ModEntry.Instance.game.user; } catch { }
        try { if (dc.pr.Game.Class.ME?.user != null) return dc.pr.Game.Class.ME.user; } catch { }
        try { if (dc.Main.Class.ME?.user != null) return dc.Main.Class.ME.user; } catch { }
        return null;
    }

    private static string SanitizePermanentItemId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return string.Empty;
        var safe = id.Trim().Replace("|", "/").Replace(",", ";").Replace("\r", string.Empty).Replace("\n", string.Empty);
        return safe.Length <= MaxPermanentItemIdChars ? safe : safe[..MaxPermanentItemIdChars];
    }

    private static bool IsProgressPermanentItem(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        // Mobility/rune/progression unlocks. Kept broad because Dead Cells internal IDs vary by version/DLC.
        var lower = id.ToLowerInvariant();
        return lower.Contains("rune") ||
               lower.Contains("key") ||
               lower.Contains("teleport") ||
               lower.Contains("spider") ||
               lower.Contains("belier") ||
               lower.Contains("ram") ||
               lower.Contains("gardener") ||
               lower.Contains("vine") ||
               lower.Contains("wall") ||
               lower.Contains("challenger") ||
               lower.Contains("homunculus") ||
               lower.Contains("explokey") ||
               lower.Contains("pokebomb") ||
               lower.Contains("mirror") ||
               lower.Contains("backpack") ||
               lower.Contains("armory") ||
               lower.Contains("recycling") ||
               lower.Contains("shopcategor");
    }
}

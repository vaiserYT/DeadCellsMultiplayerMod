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
/// Stability and progression layer built on top of the original multiplayer base.
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
    private static long _nextHudStatusTicks;
    private static string _lastSentProgress = string.Empty;
    private static string _lastAppliedProgress = string.Empty;
    private static int _lastKnownRemoteCount = -1;
    private static bool _wasConnected;

    private const double LobbyHeartbeatSeconds = 0.50;
    private const double ProgressSyncSeconds = 1.50;
    private const double HudStatusSeconds = 3.00;

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

    private static void SendPermanentProgress(NetNode net)
    {
        try
        {
            var payload = BuildLocalPermanentProgressPayload();
            if (string.IsNullOrWhiteSpace(payload))
                return;
            if (string.Equals(payload, _lastSentProgress, StringComparison.Ordinal))
                return;

            _lastSentProgress = payload;
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
            var connected = net.HasRemote;
            var remoteCount = net.IsHost ? NetNode.ConnectedClientCount : (connected ? 1 : 0);
            if (connected == _wasConnected && remoteCount == _lastKnownRemoteCount)
                return;

            _wasConnected = connected;
            _lastKnownRemoteCount = remoteCount;
            if (connected)
                MultiplayerUI.PushSystemMessage(net.IsHost ? $"Co-op: {remoteCount} friend(s) connected" : "Co-op: connected to host", 4.0, 1.0);
            else
                MultiplayerUI.PushSystemMessage(net.IsHost ? "Co-op: lobby open, waiting for friend" : "Co-op: waiting for host", 4.0, 1.0);
        }
        catch
        {
        }
    }

    public static void ReceiveLobbyState(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return;

        // Current build uses this mainly as a live-room heartbeat. The menu/connection UI already reads NetNode.HasRemote;
        // receiving this packet marks the remote alive in NetNode. Keep parsing intentionally loose for forward compatibility.
        try
        {
            var parts = payload.Split('|');
            var nameIndex = parts.Length >= 2 ? 1 : 0;
            if (parts.Length > nameIndex && !string.IsNullOrWhiteSpace(parts[nameIndex]))
                GameMenu.ReceiveRemoteUsername(parts[nameIndex].Trim());
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
            foreach (var raw in payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var id = SanitizePermanentItemId(raw);
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                if (!IsProgressPermanentItem(id))
                    continue;
                PendingPermanentItems.Add(id);
            }
        }
    }

    private static void ApplyPendingPermanentProgress()
    {
        string[] pending;
        lock (Sync)
        {
            if (PendingPermanentItems.Count == 0)
                return;
            pending = PendingPermanentItems.ToArray();
            PendingPermanentItems.Clear();
        }

        var user = GetUser();
        if (user == null)
            return;

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
                    MultiplayerUI.PushSystemMessage($"Co-op progression synced: +{added} unlock(s)", 6.0, 1.5);
                    _log?.Information("[CoopAdvanced] Applied {Count} synced permanent unlocks", added);
                }
            }
        }
        catch (Exception ex)
        {
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
        return id.Trim().Replace("|", "/").Replace(",", ";").Replace("\r", string.Empty).Replace("\n", string.Empty);
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

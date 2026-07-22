using System.Globalization;
using System.Text;
using DeadCellsMultiplayerMod.Interaction;
using DeadCellsMultiplayerMod.Mobs.MobsSynchronization;

public sealed partial class NetNode
{
    private static void ParseAnimPayload(string payload, out int? parsedId, out string animName, out int? queue, out bool? gFlag)
    {
        parsedId = null;
        animName = string.Empty;
        queue = null;
        gFlag = null;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            startIndex = 1;
        }

        if (parts.Length > startIndex)
            animName = parts[startIndex];

        if (parts.Length > startIndex + 1 &&
            int.TryParse(parts[startIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedQ))
            queue = parsedQ;

        if (parts.Length > startIndex + 2 && TryParseBool(parts[startIndex + 2], out var parsedBool))
            gFlag = parsedBool;
    }

    private static void ParseRoomPayload(string payload, out int? parsedId, out string levelId, out int roomId)
    {
        parsedId = null;
        levelId = string.Empty;
        roomId = -1;

        if (string.IsNullOrWhiteSpace(payload))
            return;

        var parts = payload.Split('|');
        if (parts.Length >= 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemoteId))
        {
            parsedId = parsedRemoteId;
            levelId = parts[1];
            _ = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out roomId);
            return;
        }

        if (parts.Length >= 2)
        {
            levelId = parts[0];
            _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out roomId);
        }
    }


    private static void ParseHeadAnimPayload(string payload, out int? parsedId, out string animName)
    {
        parsedId = null;
        animName = string.Empty;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            startIndex = 1;
        }

        if (parts.Length > startIndex)
            animName = parts[startIndex];
    }

    private static void ParseWeaponPayload(string payload, out int? parsedId, out string kind, out int slot, out int permanentId, out int? ammo)
    {
        parsedId = null;
        kind = string.Empty;
        slot = -1;
        permanentId = 0;
        ammo = null;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            startIndex = 1;
        }

        if (parts.Length > startIndex)
            kind = parts[startIndex];

        if (parts.Length > startIndex + 1 &&
            int.TryParse(parts[startIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSlot))
            slot = parsedSlot;

        if (parts.Length > startIndex + 2 &&
            int.TryParse(parts[startIndex + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPermanent))
            permanentId = parsedPermanent;

        if (parts.Length > startIndex + 3 &&
            int.TryParse(parts[startIndex + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAmmo))
            ammo = parsedAmmo;
    }

    private static void ParseAttackPayload(
        string payload,
        out int? parsedId,
        out string kind,
        out int slot,
        out int permanentId,
        out int? ammo,
        out RemoteAttackAction action)
    {
        ParseWeaponPayload(payload, out parsedId, out kind, out slot, out permanentId, out ammo);
        action = RemoteAttackAction.Attack;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            startIndex = 1;
        }

        var firstOptionalIndex = startIndex + 3;
        var actionIndex = -1;
        if (parts.Length > firstOptionalIndex)
        {
            if (int.TryParse(parts[firstOptionalIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                actionIndex = firstOptionalIndex + 1;
            else
                actionIndex = firstOptionalIndex;
        }

        if (actionIndex >= 0 && parts.Length > actionIndex)
            action = ParseAttackActionToken(parts[actionIndex]);
    }

    private static RemoteAttackAction ParseAttackActionToken(string? rawAction)
    {
        if (string.IsNullOrWhiteSpace(rawAction))
            return RemoteAttackAction.Attack;

        var action = rawAction.Trim();
        if (action.Equals("INT", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("INTERRUPT", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("I", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteAttackAction.Interrupt;
        }

        return RemoteAttackAction.Attack;
    }

    private static void ParseHpPayload(string payload, out int? parsedId, out int life, out int maxLife, out int lif, out int bonusLife, out int recover)
    {
        parsedId = null;
        life = 0;
        maxLife = 0;
        lif = 0;
        bonusLife = 0;
        recover = 0;

        var parts = payload.Split('|');
        var startIndex = 0;
        if (parts.Length >= 6 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            startIndex = 1;
        }

        if (parts.Length > startIndex &&
            int.TryParse(parts[startIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLife))
            life = parsedLife;
        if (parts.Length > startIndex + 1 &&
            int.TryParse(parts[startIndex + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMaxLife))
            maxLife = parsedMaxLife;
        if (parts.Length > startIndex + 2 &&
            int.TryParse(parts[startIndex + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLif))
            lif = parsedLif;
        if (parts.Length > startIndex + 3 &&
            int.TryParse(parts[startIndex + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBonusLife))
            bonusLife = parsedBonusLife;
        if (parts.Length > startIndex + 4 &&
            int.TryParse(parts[startIndex + 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRecover))
            recover = parsedRecover;
    }

    private static void ParseChatPayload(string payload, out int? parsedId, out string message)
    {
        parsedId = null;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
            return;

        var parts = payload.Split(new[] { '|' }, 2);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idValue))
        {
            parsedId = idValue;
            message = parts[1];
            return;
        }

        message = payload;
    }

    private static List<MobStateSnapshot> ParseMobStatesPayload(string payload)
    {
        var states = new List<MobStateSnapshot>();
        if (string.IsNullOrWhiteSpace(payload))
            return states;

        var entries = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split(',');
            if (parts.Length < 10)
                continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                continue;
            if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dir))
                continue;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var life))
                continue;
            if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxLife))
                continue;
            if (!int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation))
                continue;

            var animPayload = parts[7];
            var type = parts.Length > 8 ? parts[8] : string.Empty;
            var statePayload = parts.Length > 9 ? parts[9] : string.Empty;
            var time = parts.Length > 10 && double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt) ? pt : 0.0;
            var dx = parts.Length > 11 && double.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out var pdx) ? pdx : 0.0;
            var dy = parts.Length > 12 && double.TryParse(parts[12], NumberStyles.Float, CultureInfo.InvariantCulture, out var pdy) ? pdy : 0.0;

            states.Add(new MobStateSnapshot(index, x, y, dir, life, maxLife, animPayload, type, statePayload, generation, time, dx, dy));
        }

        return states;
    }

    private static List<MobMoveSnapshot> ParseMobMovesPayload(string payload)
    {
        var moves = new List<MobMoveSnapshot>();
        if (string.IsNullOrWhiteSpace(payload))
            return moves;

        var entries = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split(',');
            if (parts.Length < 6)
                continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                continue;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                continue;
            if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dir))
                continue;
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation))
                continue;

            var animPayload = parts[5];
            var time = parts.Length > 6 && double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var pt) ? pt : 0.0;
            var dx = parts.Length > 7 && double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var pdx) ? pdx : 0.0;
            var dy = parts.Length > 8 && double.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var pdy) ? pdy : 0.0;

            moves.Add(new MobMoveSnapshot(index, x, y, dir, animPayload, generation, time, dx, dy));
        }

        return moves;
    }

    private static List<MobChargeSnapshot> ParseMobChargesPayload(string payload)
    {
        var charges = new List<MobChargeSnapshot>();
        if (string.IsNullOrWhiteSpace(payload))
            return charges;

        var entries = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split(',');
            if (parts.Length < 3)
                continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;
            var generation = 0;
            var valueOffset = 1;
            if (parts.Length > 3 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedGeneration))
            {
                generation = parsedGeneration;
                valueOffset = 2;
            }

            var skillId = parts.Length > valueOffset ? parts[valueOffset] : string.Empty;
            if (!double.TryParse(parts.Length > valueOffset + 1 ? parts[valueOffset + 1] : "0", NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
                ratio = 0;

            charges.Add(new MobChargeSnapshot(index, skillId, ratio, generation));
        }

        return charges;
    }

    private static bool TryParseMobHitPayload(string payload, int? senderId, bool forceSenderId, out MobHit hit)
    {
        hit = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 5)
            return false;

        int parsedUserId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;

        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mobIndex))
            return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hp))
            return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        var generation = 0;
        var typeIndex = 5;
        if (parts.Length > 5 &&
            int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedGeneration))
        {
            generation = parsedGeneration;
            typeIndex = 6;
        }

        var type = parts.Length > typeIndex ? parts[typeIndex] : string.Empty;
        hit = new MobHit(parsedUserId, mobIndex, hp, x, y, type, generation);
        return true;
    }

    private static bool TryParseMobDiePayload(string payload, int? senderId, bool forceSenderId, out MobDie die)
    {
        die = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 4)
            return false;

        int parsedUserId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;

        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mobIndex))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        var generation = 0;
        if (parts.Length > 4)
            int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out generation);

        var type = string.Empty;
        if (parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]))
        {
            try
            {
                type = Encoding.UTF8.GetString(Convert.FromBase64String(parts[5]));
            }
            catch
            {
                type = string.Empty;
            }
        }

        die = new MobDie(parsedUserId, mobIndex, x, y, generation, type);
        return true;
    }

    private static bool TryParseBossVictoryPayload(string payload, out BossVictoryState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length != 2)
            return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation) ||
            generation <= 0)
        {
            return false;
        }
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var encounterId) ||
            encounterId <= 0)
        {
            return false;
        }

        state = new BossVictoryState(generation, encounterId);
        return true;
    }

    private static bool TryParseMobAttackPayload(string payload, out MobAttack attack)
    {
        attack = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split(',');
        if (parts.Length < 7)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mobIndex))
            return false;

        string skillId;
        try
        {
            skillId = Uri.UnescapeDataString(parts[1]);
        }
        catch
        {
            skillId = parts[1];
        }

        if (string.IsNullOrWhiteSpace(skillId))
            return false;

        var requiresTargetInArea = parts[2] == "1";
        var hasData = parts[3] == "1";

        int? data = null;
        if (hasData)
        {
            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedData))
                return false;
            data = parsedData;
        }

        if (!double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        var targetUserId = 0;
        if (parts.Length > 7)
            int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out targetUserId);

        var dir = 0;
        if (parts.Length > 8)
            int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out dir);

        var generation = 0;
        if (parts.Length > 9)
            int.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out generation);

        attack = new MobAttack(mobIndex, skillId, requiresTargetInArea, data, x, y, targetUserId, dir, generation: generation);
        return true;
    }

    /// <summary>Parse attack event: attack|skillId|blockSec|forcedDirSec|reqTarget|data|targetUid|dir[|attackSeq] (8-9 parts)</summary>
    private static bool TryParseMobAttackEvent(string ev, int index, double x, double y, int dir, string type, int generation, out MobAttack attack)
    {
        attack = default;
        if (string.IsNullOrEmpty(ev) || !ev.StartsWith("attack|", StringComparison.Ordinal))
            return false;

        var parts = ev.Split('|');
        if (parts.Length < 8)
            return false;

        string skillId;
        try
        {
            skillId = Uri.UnescapeDataString(parts[1]);
        }
        catch
        {
            skillId = parts[1];
        }

        if (string.IsNullOrWhiteSpace(skillId))
            return false;

        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var blockSec))
            blockSec = 0;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var forcedDirSec))
            forcedDirSec = 0;
        var requiresTargetInArea = parts[4] == "1";
        var dataVal = 0;
        int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out dataVal);
        int? data = dataVal != 0 ? dataVal : null;
        var targetUserId = 0;
        int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out targetUserId);
        var attackDir = 0;
        if (parts.Length > 7)
            int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out attackDir);

        // Phase 3 (optional): per-boss monotonic attack sequence for deterministic replay/reorder drop.
        var attackSeq = 0;
        if (parts.Length > 8)
            int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out attackSeq);

        attack = new MobAttack(index, skillId, requiresTargetInArea, data, x, y, targetUserId, attackDir != 0 ? attackDir : dir, blockSec, forcedDirSec, type ?? string.Empty, generation, attackSeq);
        return true;
    }

    /// <summary>Parse hit event: hit|life or hit|life|damageHint.</summary>
    private static bool TryParseMobHitEvent(string ev, int index, double x, double y, int userId, string? mobType, int generation, out MobHit hit)
    {
        hit = default;
        if (string.IsNullOrEmpty(ev) || !ev.StartsWith("hit|", StringComparison.Ordinal))
            return false;

        var parts = ev.Split('|');
        if (parts.Length < 2)
            return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var life))
            return false;

        var damageHint = 0.0;
        if (parts.Length > 2)
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out damageHint);

        hit = new MobHit(userId, index, life, x, y, mobType ?? string.Empty, generation, damageHint);
        return true;
    }

    /// <summary>Parse MOBEVENT payload. Format: idx,x,y,dir[,type]§event1§event2;idx2,x2,y2,dir2[,type2]§event1. Events use § separator (they contain |).</summary>
    private static List<MobEventUpdate> ParseMobEventsPayload(string payload)
    {
        const char EventSep = '\u00A7';
        var result = new List<MobEventUpdate>();
        if (string.IsNullOrWhiteSpace(payload))
            return result;

        var mobEntries = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in mobEntries)
        {
            var sepIndex = entry.IndexOf(EventSep);
            var basePart = sepIndex >= 0 ? entry[..sepIndex] : entry;
            var eventsPart = sepIndex >= 0 && sepIndex + 1 < entry.Length ? entry[(sepIndex + 1)..] : string.Empty;

            var baseParts = basePart.Split(',');
            if (baseParts.Length < 4)
                continue;

            if (!int.TryParse(baseParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                continue;
            if (!double.TryParse(baseParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                continue;
            if (!double.TryParse(baseParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                continue;
            if (!int.TryParse(baseParts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dir))
                continue;

            var generation = 0;
            var typeStart = 4;
            if (baseParts.Length >= 5 &&
                int.TryParse(baseParts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedGeneration))
            {
                generation = parsedGeneration;
                typeStart = 5;
            }

            var type = baseParts.Length > typeStart ? string.Join(",", baseParts.Skip(typeStart)) : string.Empty;

            var events = new List<string>();
            foreach (var ev in eventsPart.Split(EventSep, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrEmpty(ev))
                    events.Add(ev);
            }

            result.Add(new MobEventUpdate(index, x, y, dir, events, type, generation));
        }

        return result;
    }

    private static bool TryParseMobDrawPayload(string payload, int? senderId, bool forceSenderId, out List<MobDraw> draws)
    {
        draws = new List<MobDraw>();
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var entries = payload.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
            entries = new[] { payload };

        for (int i = 0; i < entries.Length; i++)
        {
            if (!TryParseSingleMobDrawPayload(entries[i], senderId, forceSenderId, out var draw))
                continue;

            draws.Add(draw);
        }

        return draws.Count > 0;
    }

    private static bool TryParseSingleMobDrawPayload(string payload, int? senderId, bool forceSenderId, out MobDraw draw)
    {
        draw = default;
        var parts = payload.Split('|');
        if (parts.Length < 4)
            return false;

        int parsedUserId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;

        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mobIndex))
            return false;
        if (!TryParseBool(parts[2], out var isOutOfGame))
            return false;
        if (!TryParseBool(parts[3], out var isOnScreen))
            return false;

        var generation = 0;
        if (parts.Length > 4)
            int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out generation);

        draw = new MobDraw(parsedUserId, mobIndex, isOutOfGame, isOnScreen, generation);
        return true;
    }

    private static bool TryParseExitReadyPayload(string payload, int? senderId, bool forceSenderId, out ExitReadyState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 7)
            return false;

        int parsedUserId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;

        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;
        if (parsedUserId <= 0)
            return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var doorCx))
            return false;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var doorCy))
            return false;
        if (!TryParseBool(parts[3], out var pressed))
            return false;
        if (!TryParseBool(parts[4], out var insideCircle))
            return false;
        if (!TryParseBool(parts[5], out var isOutOfGame))
            return false;
        if (!TryParseBool(parts[6], out var isOnScreen))
            return false;

        state = new ExitReadyState(parsedUserId, doorCx, doorCy, pressed, insideCircle, isOutOfGame, isOnScreen);
        return true;
    }

    private static bool TryParsePlayerDownPayload(string payload, int? senderId, bool forceSenderId, out PlayerDownState state)
    {
        state = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 5)
            return false;

        int parsedUserId;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedUserId))
            parsedUserId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            parsedUserId = senderId.Value;
        if (parsedUserId <= 0)
            return false;

        if (!TryParseBool(parts[1], out var isDowned))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        var levelId = parts[4] ?? string.Empty;
        levelId = levelId.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        if (levelId.Length == 0)
            levelId = string.Empty;

        var hasHeadPosition = false;
        var headX = 0d;
        var headY = 0d;
        var hasHeadAnim = false;
        string? headAnim = null;
        if (parts.Length >= 7 &&
            double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeadX) &&
            double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHeadY))
        {
            hasHeadPosition = true;
            headX = parsedHeadX;
            headY = parsedHeadY;

            if (parts.Length >= 8)
            {
                var parsedAnim = (parts[7] ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(parsedAnim))
                {
                    hasHeadAnim = true;
                    headAnim = parsedAnim;
                }
            }
        }

        state = new PlayerDownState(parsedUserId, isDowned, x, y, levelId, hasHeadPosition, headX, headY, hasHeadAnim, headAnim);
        return true;
    }

    private static bool TryParsePlayerRevivePayload(string payload, int? senderId, bool forceSenderId, out PlayerReviveRequest request)
    {
        request = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 2)
            return false;

        int reviverId;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out reviverId))
            reviverId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            reviverId = senderId.Value;
        if (reviverId <= 0)
            return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId))
            return false;
        if (targetId <= 0)
            return false;

        request = new PlayerReviveRequest(reviverId, targetId);
        return true;
    }

    private static bool TryParseInterDoorPayload(string payload, int? senderId, bool forceSenderId, out InterDoorEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 5)
            return false;

        int userId = 0;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out userId))
            userId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            userId = senderId.Value;
        if (userId <= 0)
            return false;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        var action = (parts[3] ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(action))
            return false;

        if (!TryParseBool(parts[4], out var broken))
            return false;

        var levelId = parts.Length >= 6
            ? (parts[5] ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim()
            : string.Empty;

        ev = new InterDoorEvent(userId, x, y, action, broken, levelId);
        return true;
    }

    private static bool TryParseInterElevatorPayload(
        string payload,
        int? senderId,
        bool forceSenderId,
        out InterElevatorEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 2)
            return false;

        // Backwards compatibility: old packets were simply X|Y.
        if (parts.Length == 2)
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var legacyX) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var legacyY))
            {
                return false;
            }

            ev = new InterElevatorEvent(senderId ?? 0, legacyX, legacyY, 0, string.Empty);
            return true;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
            userId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            userId = senderId.Value;
        if (userId <= 0)
            return false;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return false;
        }

        long sequence = 0;
        if (parts.Length >= 4)
            long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence);

        var levelId = parts.Length >= 5
            ? (parts[4] ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim()
            : string.Empty;

        ev = new InterElevatorEvent(userId, x, y, sequence, levelId);
        return true;
    }

    private static bool TryParseInterElevatorStatePayload(
        string payload,
        int? senderId,
        bool forceSenderId,
        out InterElevatorStateEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 7)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
            userId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            userId = senderId.Value;
        if (userId <= 0)
            return false;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var anchorX) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var anchorY) ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence) ||
            !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var platformX) ||
            !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var platformY) ||
            !TryParseBool(parts[6], out var moving))
        {
            return false;
        }

        var levelId = parts.Length >= 8
            ? (parts[7] ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim()
            : string.Empty;

        ev = new InterElevatorStateEvent(userId, anchorX, anchorY, sequence, platformX, platformY, moving, levelId);
        return true;
    }

    private static bool TryParseInterPressurePlatePayload(
        string payload,
        int? senderId,
        bool forceSenderId,
        out InterPressurePlateEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 2)
            return false;

        // Backwards compatibility: old packets were simply X|Y.
        if (parts.Length == 2)
        {
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var legacyX) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var legacyY))
            {
                return false;
            }

            ev = new InterPressurePlateEvent(senderId ?? 0, legacyX, legacyY, 0, string.Empty);
            return true;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
            userId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            userId = senderId.Value;
        if (userId <= 0)
            return false;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return false;
        }

        long sequence = 0;
        if (parts.Length >= 4)
            long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence);

        var levelId = parts.Length >= 5
            ? (parts[4] ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim()
            : string.Empty;

        ev = new InterPressurePlateEvent(userId, x, y, sequence, levelId);
        return true;
    }

    private static bool TryParseInterTreasureChestPayload(string payload, out InterTreasureChestEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 2)
            return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        ev = new InterTreasureChestEvent(x, y);
        return true;
    }

    private static bool TryParseInterVineLadderPayload(string payload, out InterVineLadderEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 2)
            return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        ev = new InterVineLadderEvent(x, y);
        return true;
    }

    private static bool TryParseInterTeleportPayload(string payload, out InterTeleportEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 2)
            return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        ev = new InterTeleportEvent(x, y);
        return true;
    }

    private static bool TryParseBossHeroTeleportPayload(string payload, int? senderId, bool forceSenderId, out BossHeroTeleportEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 4)
            return false;

        int userId;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out userId))
            userId = senderId ?? 0;
        if (forceSenderId && senderId.HasValue)
            userId = senderId.Value;
        if (userId <= 0)
            return false;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dir))
            return false;

        ev = new BossHeroTeleportEvent(userId, x, y, dir);
        return true;
    }

    private static bool TryParseInterBreakableGroundPayload(string payload, out InterBreakableGroundEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 2)
            return false;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        ev = new InterBreakableGroundEvent(x, y);
        return true;
    }

    private static bool TryParseInterPortalPayload(string payload, out InterPortalEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var parts = payload.Split('|');
        if (parts.Length < 3)
            return false;

        var action = parts[0]?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(action) || (action != "show" && action != "close"))
            return false;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return false;

        ev = new InterPortalEvent(x, y, action);
        return true;
    }

    private static bool TryParsePositionLine(string line, int? senderId, out int remoteId, out double rx, out double ry, out int dir, out bool hasDir)
    {
        remoteId = 0;
        rx = 0;
        ry = 0;
        dir = 0;
        hasDir = false;

        var parts = line.Split('|');
        if (parts.Length >= 4 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemoteIdWithDir) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cxWithDir) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cyWithDir) &&
            int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDir))
        {
            remoteId = parsedRemoteIdWithDir;
            rx = cxWithDir;
            ry = cyWithDir;
            dir = parsedDir < 0 ? -1 : parsedDir > 0 ? 1 : 0;
            hasDir = true;
            return true;
        }

        if (parts.Length >= 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRemoteId) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cx) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var cy))
        {
            remoteId = parsedRemoteId;
            rx = cx;
            ry = cy;
            return true;
        }

        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var cxFallback) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cyFallback) &&
            senderId.HasValue)
        {
            remoteId = senderId.Value;
            rx = cxFallback;
            ry = cyFallback;
            return true;
        }

        return false;
    }
    private static bool TryParseBool(string text, out bool value)
    {
        if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }
}

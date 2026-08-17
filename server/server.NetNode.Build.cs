using System.Globalization;
using DeadCellsMultiplayerMod.Network;


public sealed partial class NetNode
{
    private static string BuildTaggedLine(string tag, int id, string payload)
    {
        return $"{tag}|{id}|{payload}\n";
    }

    private static string BuildReadyLine(int id, bool ready)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"READY|{id}|{(ready ? 1 : 0)}\n");
    }

    private static string BuildCoopStateLine(int id, string? coopId, bool hasContinueSave)
    {
        var safeCoopId = SanitizeProtocolToken(coopId, 128);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"COOPID|{id}|{safeCoopId}|{(hasContinueSave ? 1 : 0)}\n");
    }

    private static string BuildAnimLine(int id, string animName, int? queue, bool? gFlag)
    {
        var queuePart = queue.HasValue ? queue.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var gPart = gFlag.HasValue ? (gFlag.Value ? "1" : "0") : string.Empty;
        return $"ANIM|{id}|{animName}|{queuePart}|{gPart}\n";
    }

    private static string BuildHeadAnimLine(int id, string animName)
    {
        return $"HEADANIM|{id}|{animName}\n";
    }

    private static string BuildRoomLine(int id, string levelId, int roomId)
    {
        var safeLevelId = (levelId ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"ZROOM|{id}|{safeLevelId}|{roomId}\n");
    }

    private static string BuildWeaponLine(string tag, int id, string kind, int slot, int permanentId, int? ammo)
    {
        if (ammo.HasValue)
            return $"{tag}|{id}|{kind}|{slot}|{permanentId}|{ammo.Value}\n";
        return $"{tag}|{id}|{kind}|{slot}|{permanentId}\n";
    }

    private static string BuildAttackLine(int id, string kind, int slot, int permanentId, int? ammo, RemoteAttackAction action)
    {
        var actionToken = AttackActionToToken(action);
        if (ammo.HasValue)
            return $"ATK|{id}|{kind}|{slot}|{permanentId}|{ammo.Value}|{actionToken}\n";
        return $"ATK|{id}|{kind}|{slot}|{permanentId}|{actionToken}\n";
    }

    private static string AttackActionToToken(RemoteAttackAction action)
    {
        return action == RemoteAttackAction.Interrupt ? "INT" : "ATK";
    }

    private static string BuildHpLine(int id, int life, int maxLife, int lif, int bonusLife, int recover)
    {
        return $"HP|{id}|{life}|{maxLife}|{lif}|{bonusLife}|{recover}\n";
    }

    private static string SanitizeProtocolToken(string? value, int maxLength)
    {
        return ProtocolWire.SanitizeToken(value, maxLength);
    }

    private bool TryBuildLocalHpLine(out string line)
    {
        lock (_sync)
        {
            if (!_hasLocalHpSnapshot)
            {
                line = string.Empty;
                return false;
            }

            var senderId = ID > 0 ? ID : 1;
            line = BuildHpLine(senderId, _localHpLife, _localHpMaxLife, _localHpLif, _localHpBonusLife, _localHpRecover);
            return true;
        }
    }

    private static string BuildExitCommitLine(ExitTransitionCommit commit)
    {
        static string Safe(string? value) => (value ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"EXITCOMMIT|{commit.Sequence}|{commit.DoorCx}|{commit.DoorCy}|{Safe(commit.FromLevelId)}|{Safe(commit.DestinationLevelId)}\n");
    }

    private static string BuildExitReadyLine(ExitReadyState state)
    {
        var safeLevelId = (state.LevelId ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"EXITREADY|{state.UserId}|{state.DoorCx}|{state.DoorCy}|{(state.Pressed ? 1 : 0)}|{(state.InsideCircle ? 1 : 0)}|{(state.IsOutOfGame ? 1 : 0)}|{(state.IsOnScreen ? 1 : 0)}|{safeLevelId}\n");
    }

    private static string BuildPlayerDownLine(PlayerDownState state)
    {
        var safeLevelId = (state.LevelId ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        var safeHeadAnim = (state.HeadAnim ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        if (state.HasHeadPosition)
        {
            if (state.HasHeadAnim && !string.IsNullOrWhiteSpace(safeHeadAnim))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"PDOWN|{state.UserId}|{(state.IsDowned ? 1 : 0)}|{state.X}|{state.Y}|{safeLevelId}|{state.HeadX}|{state.HeadY}|{safeHeadAnim}\n");
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"PDOWN|{state.UserId}|{(state.IsDowned ? 1 : 0)}|{state.X}|{state.Y}|{safeLevelId}|{state.HeadX}|{state.HeadY}\n");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"PDOWN|{state.UserId}|{(state.IsDowned ? 1 : 0)}|{state.X}|{state.Y}|{safeLevelId}\n");
    }

    private static string BuildPlayerReviveLine(PlayerReviveRequest request)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"PREVIVE|{request.ReviverId}|{request.TargetId}\n");
    }

    private static string BuildPosLine(int id, double cx, double cy, int dir, string? levelId = null, long positionSequence = 0)
    {
        var safeLevelId = (levelId ?? string.Empty)
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{id}|{cx}|{cy}|{dir}|{safeLevelId}|{positionSequence}\n");
    }
}

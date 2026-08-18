using System;
using System.Collections.Generic;

public sealed partial class NetNode
{
    public readonly struct RemoteSnapshot
    {
        public readonly int Id;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly string? LevelId;
        public readonly string? RoomLevelId;
        public readonly int? RoomId;
        public readonly bool HasRoom;
        public readonly string? Anim;
        public readonly int? AnimQueue;
        public readonly bool? AnimG;
        public readonly bool HasAnim;
        public readonly string? Username;
        public readonly string? HeadAnim;
        public readonly bool HasHeadAnim;

        public RemoteSnapshot(
            int id,
            double x,
            double y,
            int dir,
            string? levelId,
            string? roomLevelId,
            int? roomId,
            bool hasRoom,
            string? anim,
            int? animQueue,
            bool? animG,
            bool hasAnim,
            string? username,
            string? headAnim,
            bool hasHeadAnim)
        {
            Id = id;
            X = x;
            Y = y;
            Dir = dir;
            LevelId = levelId;
            RoomLevelId = roomLevelId;
            RoomId = roomId;
            HasRoom = hasRoom;
            Anim = anim;
            AnimQueue = animQueue;
            AnimG = animG;
            HasAnim = hasAnim;
            Username = username;
            HeadAnim = headAnim;
            HasHeadAnim = hasHeadAnim;
        }
    }

    public readonly struct RemoteWeaponSnapshot
    {
        public readonly int Id;
        public readonly string? Kind;
        public readonly int Slot;
        public readonly int PermanentId;
        public readonly int? Ammo;

        public RemoteWeaponSnapshot(int id, string? kind, int slot, int permanentId, int? ammo)
        {
            Id = id;
            Kind = kind;
            Slot = slot;
            PermanentId = permanentId;
            Ammo = ammo;
        }
    }

    public readonly struct RemoteAttack
    {
        public readonly int Id;
        public readonly string? Kind;
        public readonly int Slot;
        public readonly int PermanentId;
        public readonly int? Ammo;
        public readonly RemoteAttackAction Action;

        public RemoteAttack(int id, string? kind, int slot, int permanentId, int? ammo, RemoteAttackAction action)
        {
            Id = id;
            Kind = kind;
            Slot = slot;
            PermanentId = permanentId;
            Ammo = ammo;
            Action = action;
        }
    }

    public readonly struct RemoteHpSnapshot
    {
        public readonly int Id;
        public readonly int Life;
        public readonly int MaxLife;
        public readonly int Lif;
        public readonly int BonusLife;
        public readonly int Recover;
        public readonly string? Username;

        public RemoteHpSnapshot(int id, int life, int maxLife, int lif, int bonusLife, int recover, string? username)
        {
            Id = id;
            Life = life;
            MaxLife = maxLife;
            Lif = lif;
            BonusLife = bonusLife;
            Recover = recover;
            Username = username;
        }
    }

    public readonly struct RemoteUserSnapshot
    {
        public readonly int Id;
        public readonly string? Username;

        public RemoteUserSnapshot(int id, string? username)
        {
            Id = id;
            Username = username;
        }
    }

    public readonly struct MobStateSnapshot
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly int Life;
        public readonly int MaxLife;
        public readonly string AnimPayload;
        public readonly string Type;
        public readonly string StatePayload;
        public readonly double Time;
        public readonly double Dx;
        public readonly double Dy;

        public MobStateSnapshot(int index, double x, double y, int dir, int life, int maxLife, string animPayload, string type, string statePayload = "", int generation = 0, double time = 0.0, double dx = 0.0, double dy = 0.0)
        {
            Index = index;
            Generation = generation;
            X = x;
            Y = y;
            Dir = dir;
            Life = life;
            MaxLife = maxLife;
            AnimPayload = animPayload ?? string.Empty;
            Type = type ?? string.Empty;
            StatePayload = statePayload ?? string.Empty;
            Time = time;
            Dx = dx;
            Dy = dy;
        }
    }

    public readonly struct MobMoveSnapshot
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly string AnimPayload;
        public readonly double Time;
        public readonly double Dx;
        public readonly double Dy;

        public MobMoveSnapshot(int index, double x, double y, int dir, string animPayload, int generation = 0, double time = 0.0, double dx = 0.0, double dy = 0.0)
        {
            Index = index;
            Generation = generation;
            X = x;
            Y = y;
            Dir = dir;
            AnimPayload = animPayload ?? string.Empty;
            Time = time;
            Dx = dx;
            Dy = dy;
        }
    }

    public readonly struct MobHit
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly int Generation;
        public readonly int Hp;
        public readonly double X;
        public readonly double Y;
        public readonly string Type;
        public readonly double DamageHint;

        public MobHit(int userId, int mobIndex, int hp, double x, double y, string type = "", int generation = 0, double damageHint = 0.0)
        {
            UserId = userId;
            MobIndex = mobIndex;
            Generation = generation;
            Hp = hp;
            X = x;
            Y = y;
            Type = type ?? string.Empty;
            DamageHint = double.IsFinite(damageHint) && damageHint > 0.0 ? damageHint : 0.0;
        }
    }

    public readonly struct MobDie
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly int Generation;
        public readonly double X;
        public readonly double Y;
        public readonly string Type;

        public MobDie(int userId, int mobIndex, double x, double y, int generation = 0, string type = "")
        {
            UserId = userId;
            MobIndex = mobIndex;
            X = x;
            Y = y;
            Generation = generation;
            Type = type ?? string.Empty;
        }
    }

    public readonly struct MobRegistryEntry
    {
        public readonly int NetId;
        public readonly int Generation;
        public readonly string Type;
        public readonly double X;
        public readonly double Y;

        public MobRegistryEntry(int netId, int generation, string type, double x, double y)
        {
            NetId = netId;
            Generation = generation;
            Type = type ?? string.Empty;
            X = x;
            Y = y;
        }
    }

    public readonly struct MobAttack
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly string SkillId;
        public readonly bool RequiresTargetInArea;
        public readonly int? Data;
        public readonly double X;
        public readonly double Y;
        public readonly int TargetUserId;
        public readonly int Dir;
        public readonly double BlockSeconds;
        public readonly double ForcedDirSeconds;
        public readonly string Type;
        public readonly int AttackSeq;

        public MobAttack(int index, string skillId, bool requiresTargetInArea, int? data, double x, double y, int targetUserId, int dir = 0, double blockSeconds = 0, double forcedDirSeconds = 0, string type = "", int generation = 0, int attackSeq = 0)
        {
            Index = index;
            Generation = generation;
            SkillId = skillId ?? string.Empty;
            RequiresTargetInArea = requiresTargetInArea;
            Data = data;
            X = x;
            Y = y;
            TargetUserId = targetUserId;
            Dir = dir;
            BlockSeconds = blockSeconds;
            ForcedDirSeconds = forcedDirSeconds;
            Type = type ?? string.Empty;
            AttackSeq = attackSeq;
        }
    }

    public readonly struct MobEventUpdate
    {
        public readonly int Index;
        public readonly int Generation;
        public readonly double X;
        public readonly double Y;
        public readonly int Dir;
        public readonly IReadOnlyList<string> Events;
        public readonly string Type;

        public MobEventUpdate(int index, double x, double y, int dir, IReadOnlyList<string> events, string type = "", int generation = 0)
        {
            Index = index;
            Generation = generation;
            X = x;
            Y = y;
            Dir = dir;
            Events = events ?? Array.Empty<string>();
            Type = type ?? string.Empty;
        }
    }

    public readonly struct MobDraw
    {
        public readonly int UserId;
        public readonly int MobIndex;
        public readonly int Generation;
        public readonly bool IsOutOfGame;
        public readonly bool IsOnScreen;

        public MobDraw(int userId, int mobIndex, bool isOutOfGame, bool isOnScreen, int generation = 0)
        {
            UserId = userId;
            MobIndex = mobIndex;
            Generation = generation;
            IsOutOfGame = isOutOfGame;
            IsOnScreen = isOnScreen;
        }
    }

    public readonly struct ExitReadyState
    {
        public readonly int UserId;
        public readonly int DoorCx;
        public readonly int DoorCy;
        public readonly bool Pressed;
        public readonly bool InsideCircle;
        public readonly bool IsOutOfGame;
        public readonly bool IsOnScreen;
        public readonly string LevelId;

        public ExitReadyState(int userId, int doorCx, int doorCy, bool pressed, bool insideCircle, bool isOutOfGame, bool isOnScreen, string? levelId = null)
        {
            UserId = userId;
            DoorCx = doorCx;
            DoorCy = doorCy;
            Pressed = pressed;
            InsideCircle = insideCircle;
            IsOutOfGame = isOutOfGame;
            IsOnScreen = isOnScreen;
            LevelId = levelId ?? string.Empty;
        }
    }

    public readonly struct PlayerDownState
    {
        public readonly int UserId;
        public readonly bool IsDowned;
        public readonly double X;
        public readonly double Y;
        public readonly string LevelId;
        public readonly bool HasHeadPosition;
        public readonly double HeadX;
        public readonly double HeadY;
        public readonly bool HasHeadAnim;
        public readonly string? HeadAnim;

        public PlayerDownState(int userId, bool isDowned, double x, double y, string levelId, bool hasHeadPosition = false, double headX = 0, double headY = 0, bool hasHeadAnim = false, string? headAnim = null)
        {
            UserId = userId;
            IsDowned = isDowned;
            X = x;
            Y = y;
            LevelId = levelId ?? string.Empty;
            HasHeadPosition = hasHeadPosition;
            HeadX = headX;
            HeadY = headY;
            HasHeadAnim = hasHeadAnim;
            HeadAnim = hasHeadAnim ? (headAnim ?? string.Empty) : null;
        }
    }

    public readonly struct HostSpawnAnchor
    {
        public readonly int Cx;
        public readonly int Cy;
        public readonly string LevelId;

        public HostSpawnAnchor(int cx, int cy, string? levelId)
        {
            Cx = cx;
            Cy = cy;
            LevelId = levelId ?? string.Empty;
        }
    }

    public readonly struct ExitTransitionCommit
    {
        public readonly long Sequence;
        public readonly int DoorCx;
        public readonly int DoorCy;
        public readonly string FromLevelId;
        public readonly string DestinationLevelId;

        public ExitTransitionCommit(long sequence, int doorCx, int doorCy, string? fromLevelId, string? destinationLevelId)
        {
            Sequence = sequence;
            DoorCx = doorCx;
            DoorCy = doorCy;
            FromLevelId = fromLevelId ?? string.Empty;
            DestinationLevelId = destinationLevelId ?? string.Empty;
        }
    }

    public readonly struct PlayerReviveRequest
    {
        public readonly int ReviverId;
        public readonly int TargetId;

        public PlayerReviveRequest(int reviverId, int targetId)
        {
            ReviverId = reviverId;
            TargetId = targetId;
        }
    }
}

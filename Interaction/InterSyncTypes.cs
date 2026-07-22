namespace DeadCellsMultiplayerMod.Interaction;

public readonly struct InterDoorEvent
{
    public readonly int UserId;
    public readonly double X;
    public readonly double Y;
    public readonly string Action;
    public readonly bool Broken;
    public readonly string LevelId;

    public InterDoorEvent(int userId, double x, double y, string action, bool broken, string levelId = "")
    {
        UserId = userId;
        X = x;
        Y = y;
        Action = action ?? string.Empty;
        Broken = broken;
        LevelId = levelId ?? string.Empty;
    }
}

public readonly struct InterElevatorEvent
{
    public readonly int UserId;
    public readonly double X;
    public readonly double Y;
    public readonly long Sequence;
    public readonly string LevelId;

    public InterElevatorEvent(int userId, double x, double y, long sequence, string levelId = "")
    {
        UserId = userId;
        X = x;
        Y = y;
        Sequence = sequence;
        LevelId = levelId ?? string.Empty;
    }

    public InterElevatorEvent(double x, double y)
        : this(0, x, y, 0, string.Empty)
    {
    }
}

public readonly struct InterElevatorStateEvent
{
    public readonly int UserId;
    public readonly double AnchorX;
    public readonly double AnchorY;
    public readonly long Sequence;
    public readonly double PlatformX;
    public readonly double PlatformY;
    public readonly bool Moving;
    public readonly string LevelId;

    public InterElevatorStateEvent(
        int userId,
        double anchorX,
        double anchorY,
        long sequence,
        double platformX,
        double platformY,
        bool moving,
        string levelId = "")
    {
        UserId = userId;
        AnchorX = anchorX;
        AnchorY = anchorY;
        Sequence = sequence;
        PlatformX = platformX;
        PlatformY = platformY;
        Moving = moving;
        LevelId = levelId ?? string.Empty;
    }
}

public readonly struct InterPressurePlateEvent
{
    public readonly int UserId;
    public readonly double X;
    public readonly double Y;
    public readonly long Sequence;
    public readonly string LevelId;

    public InterPressurePlateEvent(int userId, double x, double y, long sequence, string levelId = "")
    {
        UserId = userId;
        X = x;
        Y = y;
        Sequence = sequence;
        LevelId = levelId ?? string.Empty;
    }

    public InterPressurePlateEvent(double x, double y)
        : this(0, x, y, 0, string.Empty)
    {
    }
}

public readonly struct InterTreasureChestEvent
{
    public readonly double X;
    public readonly double Y;

    public InterTreasureChestEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct InterVineLadderEvent
{
    public readonly double X;
    public readonly double Y;

    public InterVineLadderEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct InterTeleportEvent
{
    public readonly double X;
    public readonly double Y;

    public InterTeleportEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct BossHeroTeleportEvent
{
    public readonly int UserId;
    public readonly double X;
    public readonly double Y;
    public readonly int Dir;

    public BossHeroTeleportEvent(int userId, double x, double y, int dir)
    {
        UserId = userId;
        X = x;
        Y = y;
        Dir = dir;
    }
}

public readonly struct InterBreakableGroundEvent
{
    public readonly double X;
    public readonly double Y;

    public InterBreakableGroundEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct InterBossRuneUpdateCellsEvent
{
    public readonly double X;
    public readonly double Y;
    public readonly bool Add;

    public InterBossRuneUpdateCellsEvent(double x, double y, bool add)
    {
        X = x;
        Y = y;
        Add = add;
    }
}

public readonly struct InterPortalEvent
{
    public readonly double X;
    public readonly double Y;
    public readonly string Action;

    public InterPortalEvent(double x, double y, string action)
    {
        X = x;
        Y = y;
        Action = action ?? string.Empty;
    }
}

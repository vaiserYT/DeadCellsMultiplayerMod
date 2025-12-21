using System;
using dc.en.inter;
using ModCore.Events;

namespace DeadCellsMultiplayerMod.Interface;

[Event]
public interface IOnZDoorEntering
{
    void OnEnterZdoor(ZDoor zDoor);

}

public static class OnEnterZdoorExtensions
{
    public static void OnEnterZdoor(this IOnZDoorEntering listener, ZDoor zDoor)
    {
        listener.OnEnterZdoor(zDoor);
    }
}
[Event]
public interface IOnZDoorEntry
{
    void OnZDoorEntry();
}

public static class OnZDoorEntryExtensions
{

    public static void OnZDoorEntry(this IOnZDoorEntry listener, ZDoor zDoor)
    {
        listener.OnZDoorEntry(zDoor);
    }
}
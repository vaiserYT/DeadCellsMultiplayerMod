# v0.8.36 timed/no-hit reward-room crash fix

The v0.8.35 log proves the normal biome transition succeeded: the host loaded `T_PurpleGarden` and continued running. The later fatal is a separate sublevel activation path:

```
Game.activateSubLevel
Level.resume
Level.onActivation
Level.initRender
LevelDisp.render
Boot.tryRender
Null access .groupName
```

Timed and no-hit reward-room entrances use `ZDoor`. v0.8.35 only armed the KingSkin guard for the normal exit synchronization hooks, so no guard line appeared when the reward door was activated. v0.8.36 arms the guard before `ZDoor.onActivate` and leaves the remote KingSkin detached until native `Level.onActivation` has returned.

Expected log lines:

```
[NetMod][SubLevelGuard] armed reason=zdoor-activate:...
[NetMod][SubLevelGuard] activating target=...
[NetMod][SubLevelGuard] completed reason=level-onActivation:...
```

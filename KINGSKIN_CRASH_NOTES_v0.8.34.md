# v0.8.34 KingSkin transition crash notes

The recurring fatal is:

```
Game.loadMainLevel -> Boot.tryRender -> Null access .groupName
```

The multiplayer remote player is implemented as `GhostKing : KingSkin`. On the current Dead Cells build, one of the KingSkin-owned `HSprite` objects can reach the renderer with a null animation `groupName`, especially while the old level remains visible during the loading fade.

This build does not globally assign `string.Empty` to partially initialized vanilla sprites. Instead it:

1. Validates only multiplayer KingSkin sprites.
2. Attempts to restore the valid `idle` animation through the normal sprite APIs.
3. Detaches the old remote KingSkin render nodes before the transition begins.
4. Prevents network skin/head updates from recreating those render nodes until the local hero has entered the next level.

Expected log lines:

```
[NetMod] Source build: v0.8.34-kingskin-render-guard
[NetMod][KingSkinGuard] reason=exit-activate:... detached=True ...
```

If a null group is detected before the transition, the same diagnostic reports `bodyGroupBefore=<null>` and the resulting group after repair.

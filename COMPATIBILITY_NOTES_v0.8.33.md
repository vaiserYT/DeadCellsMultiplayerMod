# v0.8.33 Dead Cells v35.9 compatibility test

## Main change

The mod no longer replaces the full `dc.pr.Level.init` routine. It now calls the current game's original `Level.init` implementation and lets MobSync rebuild its registry from `entitiesPostCreate`.

The legacy copied initializer remains in the source for comparison but is never hooked or executed.

## Why

Dead Cells received a PC stability patch on 15 June 2026. The current executable identifies its source build as `client_20260616_101646`. The previous multiplayer initializer manually reconstructed hundreds of internal fields and called `Boot.tryRender()` repeatedly. That approach is brittle across game updates and matches the observed `Boot.tryRender -> Null access .groupName` transition fatal.

## Preserved features

- Advanced MobSync fixes from v0.8.32
- New Party HUD from v0.8.32
- v0.8.2 exit/door behavior
- Steam/TCP hosting and joining
- Revive and other merged systems

## Expected log

```text
[NetMod] Source build: v0.8.33-vanilla-level-init-compat
[NetMod][Compat] using vanilla Level.init for level=PrisonStart
```

The old custom initializer should not execute.

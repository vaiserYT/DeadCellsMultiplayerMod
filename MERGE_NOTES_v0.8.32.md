# v0.8.32 merge notes

This source intentionally uses the v0.8.2 rollback as the base.

## Kept from v0.8.2
- `UI/LevelExitSync.cs` unchanged.
- `ModEntry/ModEntry.cs` behavior unchanged except for a build-identification log line.
- Original level/door loading and ghost lifecycle.

## Ported from later stabilization builds
- MobSync client culled-mob safety gates.
- Deferred client mob death execution.
- Ghost-mob despawn echo.
- Teleport replay blacklist.
- More tolerant sync-id hit position validation.
- Duplicate network death protection.

## Party HUD
The newer bronze/segmented Party HUD is included, but its outer owner is a vanilla-managed `FlowBox`. The custom drawing surface is only an inner child, so HUD disposal owns the complete tree. Plate creation is delayed for 1.5 seconds after `HUD.initHero`.

## Test identity
The game log must contain:

`[NetMod] Source build: v0.8.32-stable-v082-mobsync-partyhud`

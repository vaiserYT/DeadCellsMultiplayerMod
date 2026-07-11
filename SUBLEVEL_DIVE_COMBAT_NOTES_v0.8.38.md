# v0.8.38 sublevel dive-combat guard

The v0.8.37 crash log showed that the `ZDoor` sublevel activation completed successfully.
Ten seconds later, `DiveAttack.onOwnerLand` reached `Mob.applyAttackResult` on an entity whose sprite animation group was null.

This build keeps the working render guard and adds combat-state isolation around sublevel changes:

- remote combat queues are drained before and after sublevel activation;
- remote dive replay is dropped during the transition and short post-activation grace period;
- dive damage is suppressed in no-combat `T_*` transition/passages;
- invalid entities are made non-targetable only while a dive hit is resolved in normal combat levels.

The visual landing/end path remains available; only unsafe area-hit resolution is skipped.

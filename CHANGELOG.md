# Changelog

## v0.8.38 - Sublevel exit dive-combat guard

- Prevents stale dive-attack hits after returning from reward-room sublevels.
- Drains queued remote combat packets before and after sublevel activation.
- Adds a short post-room-change combat replay grace period.
- Suppresses dive area-hit resolution in no-combat `T_*` transition/passages.
- Temporarily removes invalid sprite targets from dive hit resolution in combat biomes.
- Keeps the v0.8.36 transition, reward-door, MobSync, Party HUD, and menu-color fixes.

# v0.8.37 Blue multiplayer menu label

- Changes the main-menu **Play Multiplayer** text from white to a soft blue (`#7FD4FF`).
- Leaves all other menu entries and gameplay behavior unchanged.
- Keeps every transition, KingSkin, ZDoor, MobSync, Steam, revive, and Party HUD fix from v0.8.36.

# v0.8.36 ZDoor sublevel render guard

- Keeps the v0.8.35 KingSkin fix for normal biome transitions.
- Arms the KingSkin render guard before `ZDoor.onActivate`, which is used by the timed and no-hit reward-room doors.
- Keeps remote KingSkin sprites detached through `Level.resume -> Level.onActivation -> Level.initRender -> LevelDisp.render`.
- Disposes the detached remote ghost shell only after native sublevel rendering completes, then recreates it from the next network snapshot in the active room.
- Adds an eight-second safety timeout so a door that does not transition cannot leave the remote player hidden permanently.

# v0.8.35 KingSkin build fix

- Added the missing `ModCore.Utilities` namespace import required by the `AsHaxeString()` extension in `ModEntry.KingSkinRenderSafety.cs`.
- No runtime behavior changes from v0.8.34.

# v0.8.34 KingSkin render guard

- Targets the repeated `Game.loadMainLevel -> Boot.tryRender -> Null access .groupName` fatal at the multiplayer `GhostKing : KingSkin` render objects.
- Validates the remote KingSkin body, body clones, and custom head sprites after graphics initialization and during normal frames.
- Repairs missing animation groups through `AnimManager.play("idle")` or `HSprite.set(...)` instead of writing an empty group name into every registered vanilla entity.
- Hides and detaches all remote KingSkin render nodes immediately before a level transition, without destroying their gameplay entities during the fade.
- Blocks remote skin/head recreation while the old level is transitioning so network callbacks cannot reattach a new KingSkin sprite during `loadMainLevel`.
- Keeps the v0.8.33 vanilla `Level.init`, advanced MobSync fixes, new Party HUD, and stable v0.8.2 exit behavior.
- Adds `[NetMod][KingSkinGuard]` diagnostics showing the animation group before/after repair and whether transition detachment ran.

# v0.8.33 Dead Cells v35.9 vanilla Level.init compatibility

- Stops replacing the complete game `Level.init` method.
- Uses the current vanilla level initializer from the June 2026 game build.
- Removes all active manual `Boot.tryRender()` calls from the level initialization path.
- Keeps v0.8.32 advanced MobSync fixes, Party HUD, and v0.8.2 exit behavior.
- Adds compatibility logging for every initialized level.

# v0.8.32 stable v0.8.2 transition base + MobSync hardening + safe new Party HUD

- Keeps v0.8.2 `LevelExitSync.cs`, level loading, ghost lifecycle, and ModEntry transition behavior unchanged.
- Ports the later MobSync fixes: trusted sync-id hit distance, client culled-mob wake/replay gates, deferred client deaths, ghost-mob despawn echo, teleport-skill replay blacklist, and duplicate-death protection.
- Includes the newer bronze/name/segmented-percent Party HUD appearance.
- Rebuilds that HUD on a vanilla-managed outer `FlowBox` with an inner absolute-position panel, avoiding the loose root object that could survive HUD disposal.
- Delays custom party-label creation for 1.5 seconds after HUD initialization so cached HP cannot create font-backed labels during `Game.loadMainLevel`.
- The later mob-sync transition-quiesce code remains dormant because the stable v0.8.2 transition path never arms it.

## v0.8.2 - Dynamic Contains build fix

- Fixed C# dynamic dispatch compile error in `AdvancedCoop/CoopAdvancedHardening.cs`.
- Replaced `List<string>.Contains(id, StringComparer.OrdinalIgnoreCase)` with an explicit `Any(...)` + `string.Equals(...)` check.
- No gameplay/network changes from v0.8.1.

## 0.8.0 - Advanced stable co-op hardening

- Returned to the Vaiser-style base instead of the clean-room prototype.
- Disabled HeroInit debug perk/item injection that caused `InventItem/commonProps` crashes.
- Disabled dangerous debug hotkeys and cooldown-map writes during normal gameplay.
- Added automatic lobby heartbeat packets and automatic room-status refresh.
- Added Steam friend invite overlay button to host room UI.
- Simplified main menu co-op flow and room status lines.
- Added permanent rune/progression sync using `RUNEPROG` packets.
- Hardened host-authoritative mob death handling to clean up life=0 ghost mobs/elites.
- Added exit teleport failsafe for portals/exits/Ossuary-style transitions.
- Added x64/win-x64 project settings.


## v0.8.1 - User namespace build fix

- Fixed `User` namespace compile error in `AdvancedCoop/CoopAdvancedHardening.cs` by importing the generated `dc.User` type namespace.
- No gameplay/network changes from v0.8.0.

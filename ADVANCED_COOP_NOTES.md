# Advanced Co-op Hardening Notes — v0.8.0

This build goes back to the Vaiser-style base instead of the clean-room experiment, but it hardens the parts that were repeatedly causing crashes/desync.

## Main changes

- Disabled dangerous debug item/perk injection during `HeroInit`.
- Disabled old debug hotkeys that spawned mobs and wrote cooldown maps during normal play.
- Added `CoopAdvancedHardening`, a lightweight runtime layer for:
  - automatic lobby heartbeat packets,
  - automatic room-status refresh in the title menu,
  - permanent rune/progression unlock sync,
  - HUD connection status messages.
- Added Steam invite overlay support from the host room menu.
- Made the main co-op menu shorter and cleaner:
  - Host room
  - Join room
  - Save slot
  - Steam friends lobby
  - IP / VPN lobby
- Added room status lines for same-lobby visibility.
- Added an exit teleport failsafe. If one player is at an exit/portal and another player gets stuck, the stuck player is pulled to the exit after a delay.
- Hardened mob death handling so clients do not ignore host-authoritative death packets for mobs that already have `life == 0` but never despawned. This targets the “elite stays on client with no HP bar” bug.
- Added broad permanent progression sync for rune-like/progression-like permanent item IDs.

## Notes

This is source only. Build it locally with DCCM/MDK.

Recommended test order:

1. Remove older broken mod copies from `coremod/mods`.
2. Build v0.8.0.
3. Host Steam friends lobby.
4. Invite via the new Steam invite overlay button.
5. Confirm the room menu says same lobby.
6. Start run.
7. Test elite rune rooms, Ossuary transitions, and boss doors/cinematics.

Send the full console log if a crash still happens. The most important lines are `[NetMod]`, `[CoopAdvanced]`, `[MobsSync]`, `[ExitSync]`, and Hashlink exception stacks.

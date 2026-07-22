# Changelog

## 0.8.91

The largest update since the original public release.

### Multiplayer core
- Full run-launch handshake: host and clients commit the same seed, save slot, and launch kind before a run starts — in normal runs and Boss Rush.
- Dedicated multiplayer save slots with automatic protection: the active slot is backed up before every networked launch, and a save corrupted by a crash is detected and restored automatically on the next start.
- Steam is now optional. Direct IP / LAN hosting binds all interfaces and prints the exact address teammates should join; the menu falls back to direct IP automatically when Steam is unavailable. Port-forwarded and VPN-LAN play are supported.

### Enemy synchronization
- Teleporting and blinking enemies (common at higher Boss Cells) snap cleanly to their destination instead of drifting or stuttering afterward.
- Enemies that land on a different platform no longer freeze in place; they converge to the correct position reliably.
- Large position desyncs from dropped packets now self-correct instead of persisting.

### Boss fights
- The boss roster is playable in co-op, including Boss Rush and DLC bosses (the Servants, Dracula and his beast form, Death, and more).
- Boss intro and mid-fight transformation cinematics are co-op safe: each player's cinematic runs to completion, with a watchdog guaranteeing controls and camera are always released.
- Boss-owned projectiles and parts (thrown scythes, bat swarms, shurikens, tentacles) are cleaned up on both screens when they despawn.
- Boss identity is anchored so summoned adds can no longer break the boss's synchronization mid-fight.
- Post-kill victory sequences complete on both screens, and exit doors after a boss coordinate the whole party.

### World and interaction
- Doors, pressure plates, and progression objects are fully synchronized.
- Exit doors wait for the whole party and handle downed players correctly.

### Known limitations
- Flint may still crash the game in rare cases.
- Elevators are rideable but not yet fully synchronized.
- Enemies summoned mid-fight by Boss Rush modifiers synchronize on a best-effort basis.

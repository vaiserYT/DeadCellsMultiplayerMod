# DeadCellsCoopPlus — Stable Co-op v0.8.38

DeadCellsCoopPlus is a community-maintained fork of Vaiser’s original **Dead Cells Multiplayer Mod**, built with the **Dead Cells Core Modding API (DCCM)**.

This release keeps the stable v0.8.36 co-op fixes and gives the main-menu **Play Multiplayer** entry a soft blue highlight.

See `CHANGELOG.md` for the complete version history and `SUBLEVEL_REWARD_DOOR_NOTES_v0.8.36.md` for the latest transition fix.

---

<div align="center">

English • [Русский](README_ru.md)

</div>

## Latest release highlights

- Fixed normal biome transition crashes caused by invalid remote-player render state.
- Fixed timed and no-hit reward-room crashes through `ZDoor` sublevel transitions.
- Uses the current vanilla `Level.init` path for better compatibility with newer Dead Cells builds.
- Includes hardened host-authoritative enemy synchronization and cleanup.
- Includes the newer Party HUD with player name, segmented health, and percentage display.
- Supports Steam P2P and direct TCP hosting/joining.

## Features

- Real-time synchronization between two players
- Local TCP or Steam P2P multiplayer
- Host/client architecture
- Automatic game start for connected clients
- Camera spectate controls with keyboard or gamepad
- Boss HP scaling and boss-rune synchronization
- Enemy movement, damage, attack, death, and despawn synchronization
- Remote weapon, head, skin, and cosmetic synchronization
- Death, downed-state, revive, and restart handling
- Level generation and transition synchronization
- Timed/no-hit reward-room transition protection
- Multiplayer save slots
- Party HUD for the remote player

## Requirements

- **Dead Cells (PC)**
- **Dead Cells Core Modding API (DCCM)**
- Steam, a local network, or a compatible virtual LAN for online TCP play

## Installation

### 1. Install DCCM

For the Steam version of Dead Cells, follow the official DCCM installation guide:

https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop/

### 2. Install the mod

Copy the built mod folder into:

```text
Dead Cells/
└── coremod/
    └── mods/
        └── DeadCellsMultiplayerMod/
```

The installed folder should contain the compiled DLL, `modinfo.json`, and any required resource files produced by the project build.

### 3. Start through DCCM

Launch Dead Cells through DCCM. Configuration files are generated automatically on first launch.

## How to play

1. Start Dead Cells through DCCM on both computers.
2. Open **Play Multiplayer**.
3. The host chooses a Steam lobby or direct TCP host.
4. The second player joins through Steam, lobby code, or the host address.
5. Start a run after both players are connected.

Both players should use the same mod build. For v0.8.38, the game log should contain:

```text
[NetMod] Source build: v0.8.38-sublevel-dive-combat-guard
```

## v0.8.38 regression test checklist

- Prisoners’ Quarters to the passage area
- Passage forge and mutation area
- Timed reward door
- No-hit reward door
- Reward-room exit
- Passage to the next main biome

## Development status

- [x] Second-player remote character
- [x] World and level generation synchronization
- [x] Enemy and boss synchronization
- [x] Boss HP scaling and boss-rune synchronization
- [x] Death, revive, and restart synchronization
- [x] Weapon, head, skin, and cosmetic synchronization
- [x] Main biome transition crash protection
- [x] Timed/no-hit reward-room transition protection
- [x] Multiplayer saves and continue support
- [x] Camera spectate mode
- [x] Steam P2P connectivity
- [ ] Broader custom-mode support

## Reporting bugs

Include both players’ logs whenever possible:

- `last_error.txt`
- the latest DCCM game log
- exact reproduction steps
- whether the crash happened on host, client, or both
- the source-build line printed during startup

## Credits

- **Original project and core multiplayer implementation:** Vaiser / `vaiserYT`
  - https://github.com/vaiserYT/DeadCellsMultiplayerMod
- **Dead Cells Core Modding API:**
  - https://github.com/dead-cells-core-modding/core
- Community contributors and testers who helped reproduce crashes, verify synchronization, and test transitions.

## License

This project continues under the original MIT License. See `LICENSE` and `NOTICE.md`.

<div align="center">

English • [Русский](README_ru.md)
  
</div>
<h1>Dead Cells Multiplayer Mod</h1>

**DeadCellsMultiplayerMod** is a **multiplayer / co-op mod for Dead Cells**, built using the **Dead Cells Core Modding API (DCCM)**.

The mod adds **co-op / multiplayer gameplay** via a **local or virtual network**:  
one player hosts a server, another connects — and both players can **play through levels together in real time**.

---

## 🎮 Features

- ✅ Real-time synchronization between two players  
- ✅ Local TCP or Steam P2P multiplayer  
- ✅ Host / Client architecture  
- ✅ Automatic game start for connected clients  
- ✅ Camera spectate — cycle between players with `,` / `.` keys or gamepad  
- ✅ Boss HP scaling and boss rune sync  
- ✅ Client mob attack synchronization and interruption  
- ✅ Ghost weapon, head, and cosmetic sync  
- ✅ Death/revive handling and restart sync  
- ✅ Level graph reload sync (boss cell doors, level transitions)  
- ✅ Multiplayer save slots  

---

## ⭐ Support the Project

If you find this project interesting:
- ⭐ Star the repository  
- 🍴 Fork the project and experiment  

Every bit of feedback helps improve multiplayer support for **Dead Cells**.

---

## 🧰 Requirements

- **Dead Cells (PC)** (Update 35 / "The End is Near")
- **Dead Cells Core Modding API (DCCM)**
- **[.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **[Microsoft Visual C++ Redistributable 2015–2022 (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe)**
- Local network, Steam, or virtual LAN software (for online play)

---

## 📦 Installation

### Method A: Steam Workshop Users
1. Subscribe to **[DCCM Core Modding API](https://steamcommunity.com/sharedfiles/filedetails/?id=3633185550)**.
2. Subscribe to **[DeadCellsMultiplayerMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3657857836)**.
3. Steam downloads workshop files to your workshop cache folder (`steamapps/workshop/content/588650/`). To activate DCCM:
   - Navigate to your game folder (`steamapps/common/Dead Cells/`).
   - Copy `core`, `plugins`, and `ModCoreVersion.txt` from `<Steam>/steamapps/workshop/content/588650/3633185550/win-x64/content/` into a folder named **`coremod`** in your game root.
   - Copy all files from `<Steam>/steamapps/workshop/content/588650/3657857836/` into `<DeadCellsGameRoot>/coremod/mods/DeadCellsMultiplayerMod/`.
4. Launch the game using `<DeadCellsGameRoot>/coremod/core/host/startup/DeadCellsModding.exe`.

### Method B: Manual / Non-Steam Setup
1. Extract DCCM into your Dead Cells root folder so that `<DeadCellsGameRoot>/coremod/` exists.
2. Create the folder `<DeadCellsGameRoot>/coremod/mods/DeadCellsMultiplayerMod/` if it does not exist.
3. Extract the mod release contents into that folder (ensuring `modinfo.json` is directly inside).
4. Launch via `<DeadCellsGameRoot>/coremod/core/host/startup/DeadCellsModding.exe`.

> ⚠️ **Important Folder Rule:** The directory must be strictly named **`coremod`** (singular, lowercase). Naming it `coremods` will cause the launcher to exit immediately without an error.

*Having issues? See our comprehensive [Troubleshooting Guide](docs/TROUBLESHOOTING.md).*

---

## 🕹️ How to Play (Multiplayer)

### Steam P2P (Easiest)
1. Both players launch Dead Cells via `DeadCellsModding.exe`.
2. The Host clicks **Play Multiplayer** → **Host (Steam P2P)**.
3. Once the lobby screen appears with your lobby code:
   - Open the **Steam Overlay** (`Shift + Tab`).
   - Right-click your friend in the Steam friend list and select **Invite to Game** (or copy and share the lobby code).
4. The joining player accepts the invite via Steam chat or Steam profile.
5. When both players appear in the lobby:
   - The host selects a multiplayer save slot.
   - Click **Start Game** — both players will spawn together in the Prisoner's Quarters!

### Local Network / Direct TCP
1. Connect to the same local network or virtual LAN (e.g., **Radmin VPN**, **ZeroTier**, **Hamachi**).
2. Host clicks **Play Multiplayer** → **Host (TCP)** (default port: `1234`).
3. Client clicks **Play Multiplayer** → **Join (TCP)**:
   - Enter the Host's **Virtual LAN IP** (do not use `127.0.0.1` unless testing two clients on the same PC).
   - Enter port `1234` and click Connect.
4. Select a save slot and start the game.

> 💡 **Tip:** Ensure Windows Defender Firewall allows incoming connections on port `1234` for `DeadCellsModding.exe` and `deadcells_gl.exe`.

---

## 🧪 Development Status / TODO

- [x] Second player ghost  
- [x] World data synchronization  
- [x] Ghost animations  
- [x] Level generation sync  
- [x] Enemy synchronization  
- [x] Boss synchronization, HP scaling, boss rune sync  
- [x] Death handling and restart sync  
- [x] Player ghost weapon, head, and cosmetic sync  
- [x] Level graph reload (boss cells, transitions)  
- [x] Multiplayer save slots and continue  
- [x] Camera spectate mode  
- [x] Custom mode  
- [x] Steam P2P connectivity  

**Note:** Enemy sync uses host-owned NetIds (not native game entity ids). `AdvancedCoop` is lobby heartbeat + permanent unlock progression only — it does not sync enemies.

---

## 📜 Credits

- **Dead Cells Core Modding API (DCCM)**  
  https://github.com/dead-cells-core-modding/core

---

<!--
Keywords: Dead Cells multiplayer mod, Dead Cells co-op mod, Dead Cells online, DCCM mod, Dead Cells TCP multiplayer
-->

# 🔧 DeadCellsMultiplayerMod Troubleshooting & FAQ

This guide addresses common setup issues, launcher behaviors, network questions, and console messages encountered when running **DeadCellsMultiplayerMod**.

---

### 1. `DeadCellsModding.exe` closes or crashes immediately upon launch

* **Cause:** The folder in your Dead Cells directory is named `coremods` instead of `coremod`.
* **Technical Details:** DCCM's bootstrapper hardcodes a lookup for `coremod/core/host/DCCMShell.dll`. If the folder has an `s` at the end (`coremods`), the executable cannot locate the assembly and terminates silently without displaying an error dialog or log.
* **Fix:** Open your Dead Cells game directory (`steamapps/common/Dead Cells/`) and ensure the folder is named strictly **`coremod`** (singular, all lowercase).

---

### 2. "FATAL : SysError(Can't read save/MSave/user_*.dat)" in the Console

* **Symptom:** When opening the Save Selection menu or starting a multiplayer session, the debug console prints:
  ```text
  [Game] src/tool/File.hx:91: FATAL : Could not read save file.
  [Game] src/tool/File.hx:91: FATAL : SysError(Can't read save/MSave/user_1.dat)
  ```
* **Status:** **THIS IS NORMAL AND NOT A CRASH.**
* **Explanation:** Dead Cells is written in Haxe/HashLink. When querying save slots, the game attempts to read each slot file (`user_1.dat` through `user_6.dat`). If a slot does not exist on disk yet (because you haven't started a run in that slot), Haxe's file loader logs a fatal read error to the console. The game internally handles this exception and properly displays the slot as an "Empty Slot / New Game". You can safely ignore this message and continue.

---

### 3. Subscribed on Steam Workshop, but "Play Multiplayer" does not appear

* **Cause:** Clicking "Play" inside Steam executes the vanilla `deadcells.exe`. Vanilla Dead Cells only loads basic graphic/pak mods and does not load custom C# .NET runtimes or DCCM.
* **Fix:** You must start the game through the DCCM launcher:
  1. Make sure you copied the files from Steam Workshop cache into `<DeadCellsGameRoot>/coremod/` as described in the [README](../README.md#📦-installation).
  2. Launch `<DeadCellsGameRoot>/coremod/core/host/startup/DeadCellsModding.exe`.
  *(Optional: If you want Steam to launch the mod automatically when pressing Play, backup your original `deadcells.exe` and replace it with the launcher stub located in `coremod/core/host/startup/steam/deadcells.exe`).*

---

### 4. How to Host and Connect via Steam P2P

1. Both players must launch the game using `DeadCellsModding.exe`.
2. The **Host** clicks **Play Multiplayer** → **Host (Steam P2P)**.
3. Once in the lobby, the host opens the **Steam Overlay** (`Shift + Tab`).
4. Right-click your friend in your Steam Friend List and select **Invite to Game** (or copy and send the generated lobby code).
5. The joining player accepts the invite via Steam chat.
6. When both players appear in the lobby:
   - The host selects a multiplayer save slot.
   - Click **Start Game** — both players will spawn together in the Prisoner's Quarters.

---

### 5. Client Connection Timeout over Direct TCP

* **Incorrect IP:** When playing over the internet via a Virtual LAN (such as **Radmin VPN**, **ZeroTier**, or **Hamachi**), the joining client must enter the host's **Virtual LAN IP**, **NOT** `127.0.0.1` (which is localhost loopback).
* **Firewall Blocking:** Ensure that Windows Defender Firewall allows incoming connections on port `1234` for:
  - `DeadCellsModding.exe`
  - `deadcells_gl.exe`
  - `deadcells.exe`

---

### 6. Game crashes on startup with a .NET or VC++ error

* Make sure you have installed the **x64** versions of both prerequisites:
  - [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) *(Desktop or Console runtime)*
  - [Microsoft Visual C++ Redistributable 2015–2022 (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe)

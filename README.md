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

- **Dead Cells (PC)**
- **Dead Cells Core Modding API (DCCM)**
- Local network, Steam, or virtual LAN software (for online play)

---

## 📦 Installation

### 1️⃣ Install Dead Cells Core Modding API (DCCM)

If you are using the **Steam version** of the game, follow the official installation guide:

👉 [https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop/](https://dead-cells-core-modding.github.io/docs/docs/tutorial/install-workshop/)

This method will automatically install and keep DCCM up to date.

### 2️⃣ Install DeadCellsMultiplayerMod

If you are using the **Steam version** of the game:
1. Open [https://steamcommunity.com/sharedfiles/filedetails/?id=3657857836](https://steamcommunity.com/sharedfiles/filedetails/?id=3657857836)
2. Install the mod in one click.

If you are using a **non-Steam version** of Dead Cells (DCCM required):
1. Navigate to your **DCCM directory**
2. Create a folder named `mods` (if it doesn't exist)
3. Extract the **DeadCellsMultiplayerMod** folder into the `mods` directory

Example:
```
Your game path/
 └──coremod/
    └── mods/
        └── DeadCellsMultiplayerMod/
```

### 3️⃣ Run the game via DCCM

Start **Dead Cells** using **DCCM**.  
On the first launch, required configuration files will be generated automatically.

---

## 🕹️ How to Play (Multiplayer)

1. Launch the game via **DCCM**
2. Click **Play Multiplayer**
3. Choose **Host** or **Join**
4. Enter **IP address** and **port** (TCP) or connect via Steam
5. When the host starts the game, the client will automatically join the session

🌐 **For online play**, use one of the following:
- Hamachi  
- Radmin VPN  
- ZeroTier  
- Steam P2P (built-in)

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
- [ ] Custom mode  
- [x] Steam P2P connectivity  

---

## 📜 Credits

- **Dead Cells Core Modding API (DCCM)**  
  https://github.com/dead-cells-core-modding/core

---

<!--
Keywords: Dead Cells multiplayer mod, Dead Cells co-op mod, Dead Cells online, DCCM mod, Dead Cells TCP multiplayer
-->

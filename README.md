# DeadCellsMultiplayerMod

**DeadCellsMultiplayerMod** is a mod for **Dead Cells**, built on top of the **Dead Cells Core Modding API (DCCM)**.  
It adds **multiplayer support** via a local or virtual network: one player hosts a server, another connects — and you can explore levels together.

---

## 🚀 Features
- Real-time synchronization between two players  
- TCP-based local server embedded directly in the game  
- Simple client connection  
- Easy installation and setup  

---

## 📦 Installation

### 1️⃣ Install Dead Cells Core Modding API  
Download the latest release of DCCM:  
https://github.com/dead-cells-core-modding/core

And install it by insctruction on page

---

### 2️⃣ Run DCCM
Start the game using:
DCCM will generate the required files on the first launch.  
When the game loads — simply close it.

---

### 3️⃣ Install the mod
Extract the **DeadCellsMultiplayerMod** folder into mods folder in coremod:


---

### 4️⃣ Network configuration
Open `server.txt` inside the DeadCellsMultiplayerMod folder.

- **If you are the host:**
    write 127.0.0.1:your_port

- **If you are the client:**
    write host_ip:host_port

For internet play, use **Hamachi, Radmin VPN, ZeroTier**, or similar tools.

---

## 🕹 How to Play

| Action | Key |
|--------|-----|
| Start server (Host) | **F5** |
| Connect as client | **F6** |

1. Launch the game with the mod installed  
2. Host presses **F5** to start the server  
3. Client presses **F6** to connect  
4. Enjoy playing together!  

---

## TODO
- [x] Create the player's ghosts
- [x] Sync the world's seeds
- [ ] Better sprite for the ghost
- [ ] Add animation to the player's ghosts
- [ ] Sync the enemies
- [ ] Add death for the ghost


---

## 📜 Credits

- **Dead Cells Core Modding API (DCCM):**  
https://github.com/dead-cells-core-modding/core

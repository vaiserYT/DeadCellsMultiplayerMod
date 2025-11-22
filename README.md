# DeadCellsMultiplayerMod

**DeadCellsMultiplayerMod** is a mod for **Dead Cells**, built on top of the **Dead Cells Core Modding API (DCCM)**.  
It adds **multiplayer support** via a local or virtual network: one player hosts a server, another connects — and you can explore levels together.

---

## 🚀 Features
- Real-time synchronization between two players  
- UDP-based local server embedded directly in the game  
- Simple client connection  
- Very low latency  
- Easy installation and setup  

---

## 📦 Installation

### 1️⃣ Install Dead Cells Core Modding API  
Download the latest release of DCCM:  
https://github.com/dead-cells-core-modding/core

Create the following folder inside your game directory:

Extract the API release into this folder.

---

### 2️⃣ Run DCCM
Start the game using:
DCCM will generate the required files on the first launch.  
When the game loads — simply close it.

---

### 3️⃣ Install the mod
Extract the **DeadCellsMultiplayerMod** folder into mods folder:


---

### 4️⃣ Network configuration
Open `server.txt` inside the mod folder.

- **If you are the host:**

- **If you are the client:**

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

## 📜 Credits

- **Dead Cells Core Modding API (DCCM):**  
https://github.com/dead-cells-core-modding/core

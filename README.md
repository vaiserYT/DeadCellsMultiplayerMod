# DeadCellsMultiplayerMod
DeadCellsMultiplayerMod - dccm mod that add multiplayer in the game

#First steps:
1) Download dccm(dead cells core modding api)'s last realese - https://github.com/dead-cells-core-modding/core
2) Install dccm by insctruction on dccm page, but in short just create coremod folder in game directory and unzip realese in coremod folder.
3) Run the game with dccm, game_path\coremod\core\host\startup 
4) When the game starts, close it
5) put unzipped mod in mods folder
6) Change server.txt in mod folder:
    1) if you're the host just leave 127.0.0.1:your_port
    2) if you're the client write ip_from_host:host_port
    PS: for playing multiplayer you're should use Hamachi or Radmin or other stuff
7) Start the game.
8) The host should press f5 to start the server
9) The client shoult press f6 to connect
10) Start have fun

#Credits:
Dead Cells Core Modding Api - https://github.com/dead-cells-core-modding/core
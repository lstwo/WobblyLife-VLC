# Wobbly Life VLC

This client-side mod replaces the cinema screen with a VLC media player. 
This can be used to put local media files, external files through http, streams through rtmp and everything else VLC supports onto the screen in the cinema.

You do NOT have to be the host (i think) as the mod is client-sided and only replaces the screen for you.

I cannot guarantee that it will work on linux.

> [!NOTE]
> This mod might be a bit buggy because of VLC, especially for live streams. To fix any issues try one of the following things:
> 1. Go far enough away where the sound disappears to unload the chunk then go back.
> 2. Restart the game.

## Installation

Install BepInEx from [here](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.4/BepInEx_win_x64_5.4.23.4.zip) and copy the files within the zip file to your Wobbly Life folder,
so you have a BepInEx folder in the same place as the Wobbly Life.exe and Wobbly Life_Data folder and inside of it the core folder.

Create a plugins folder within the BepInEx folder (if it doesn't already exist) 
and extract the `wlVLC-vX.X.zip` you get from the [releases](https://github.com/lstwo/WobblyLife-VLC/releases) into the plugins folder.

> [!IMPORTANT]
> NOT the source code, so if the zip contains `.cs` files thats NOT the right one.

Then start your game.

## How To Configure

You can configure the mod using the config file at `<Wobbly Life>/BepInEx/config/wlVLC.cfg`, but i recommend installing one of these plugins so you can change it in-game:
- https://github.com/sinai-dev/BepInExConfigManager
- https://github.com/BepInEx/BepInEx.ConfigurationManager

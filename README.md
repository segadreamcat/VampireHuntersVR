# Vampire Hunters VR

vr mod for **Vampire Hunters** on pc. play the whole game in your headset with a gun or 10 in each hand

You have to own the game on steam

## what it does

- full vr with head tracking (steamvr / openvr)
- guns are in your hands and each one shoots where that controller points
- each hand gets its own crosshair
- controller rumbles when you shoot 
- smooth turn, and you walk the way your head is looking
- hud floats in front of you, menus show up on a big flat screen just like the normal game can remove hud in game menus though.
- laser pointer for menus, trigger clicks, right stick scrolls


tested on quest 3 through steamvr (steam link / air link / virtual desktop) with a rtx 3070

## what you need

1. Vampire Hunters (steam, windows)
2. SteamVR
3. BepInEx 6 **Bleeding Edge, Unity IL2CPP, win-x64** (i used build 788)
   get it here: https://builds.bepinex.dev/projects/bepinex_be
   grab the one called something like `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.XXX+XXXXXXX.zip`

## how to install

1. open the game folder: steam > right click Vampire Hunters > Manage > Browse local files
2. unzip BepInEx into that folder (`winhttp.dll` should be sitting right next to `Vampire Hunters.exe`)
3. launch the game once **with NO vr**, wait til the main menu, then quit. first run BepInEx sets itself up and it can take a min so just let it go
4. download `VampireHuntersVR-vX.Y.Z.zip` from [Releases](../../releases)
5. unzip it into the game folder. it should end up like this:
   ```
   Vampire Hunters/BepInEx/plugins/VampireHuntersVR/VampireHuntersVR.dll
   Vampire Hunters/BepInEx/plugins/VampireHuntersVR/openvr_api.dll
   ```
6. start steamvr, put the headset on, launch the game from steam. thats it

if the headset is just black put `-force-d3d11` in the games steam launch options

## controls (quest / touch)

| button | what it does |
|---|---|
| left stick | walk (forward is wherever you look) |
| right stick | smooth turn (in menus it scrolls) |
| right trigger | shoot / click in menus |
| left trigger | shoot left gun |
| A | jump (menus: enter) |
| B | reload |
| X | interact |
| Y | pause menu |
| left or right grip | Bloody Rush (the dash, "F" in the normal game) |
| left stick click | run / special |
| right stick click | level up |
| both stick clicks / F6 | recenter |
| F8 | vr on/off |
| F7 | flip the picture if its upside down |

menus: point the right hand laser at the screen and pull the trigger
shop cards: quick trigger pull buys it. lock and ban work same as the normal game

## settings

after you launch in vr once theres a file at `BepInEx/config/local.vampirehunters.vr.cfg`. open it with notepad. the good ones:

| setting | what it does |
|---|---|
| `ResolutionScale` | how sharp each eye is (lower = more fps) |
| `SmoothTurnMousePixels` | how fast you turn |
| `TurnMode` | `Smooth`, `Snap` or `Off` |
| `MoveRelativeTo` | `Head`, `LeftHand` or `Body` |
| `HandPitchOffset` | tilts the guns up or down vs your controller |
| `GripOffsetX/Y/Z`, `LeftGunOffsetX/Y/Z` | where the gun sits in your hand |
| `CrosshairSize` | crosshair size |
| `HapticStrength`, `HapticMinGap`, `HapticOnlyWhenTrigger` | rumble stuff |
| `MenuDistance`, `MenuWidth`, `MenuPointerSmoothing` | menu screen stuff |
| `NearClip` | hides stuff thats right up in your face (like your arms) |
| `[Keys]` section | what key each controller button presses |
| `MirrorToDesktop` | shows the game on your monitor too (slower) |

## if somethings wrong

- **buttons dont do anything:** the game window has to be the active one. the mod does it for you but sometimes windows blocks it, just click the game window once
- **picture upside down:** hit F7, or set `FlipMenuScreen = false` for menus
- **headset black:** use the `-force-d3d11` launch option and make sure steamvr is actually running
- **something broke:** open an issue and send `BepInEx/LogOutput.log`

## uninstall

delete `BepInEx/plugins/VampireHuntersVR`. if you want BepInEx gone too delete `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version` from the game folder

## building it yourself

its a BepInEx 6 IL2CPP plugin (C#, .NET 6). you have to build it against **your own** games interop dlls (BepInEx makes them on first run), theyre not in here

on windows with the .NET SDK 6 or newer:

```
dotnet build -c Release -p:GamePath="C:\Program Files (x86)\Steam\steamapps\common\Vampire Hunters"
```

you get `bin/Release/net6.0/VampireHuntersVR.dll`. copy that plus `openvr_api.dll` (from https://github.com/ValveSoftware/openvr, `bin/win64`) into `BepInEx/plugins/VampireHuntersVR/`

`tools/build-linux.sh` is the script i used while making it (csc against copied game dlls)

## license

mod code is MIT (see `LICENSE`)
`src/openvr_api.cs` and `openvr_api.dll` are from valves OpenVR SDK, BSD 3-Clause (see `OPENVR_LICENSE.txt`)
no game files in here

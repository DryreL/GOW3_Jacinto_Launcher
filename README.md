# Gears of War 3 Jacinto: Multipatch Launcher

An all-in-one community patch and launcher for **Gears of War 3 Project Jacinto v1.1.1** on PC. It fixes campaign soft-locks and crashes, scales the interface to any screen, adds online co-op through Steam, and lets you change settings and controls without editing files.

**Download:** [Nexus Mods](https://www.nexusmods.com/gearsofwar3/mods/27) or [GitHub Releases](https://github.com/DryreL/GOW3_Jacinto_Launcher/releases)

> Install it only on **Jacinto v1.1.1** (the campaign build). It is not compatible with v1.1.2 (the multiplayer build).

## Install

1. Unzip the package for your system anywhere.
2. Open `GoW3Jacinto_Launcher`. Choose your game folder if it asks.
3. Press **Install Multipatch**.

The launcher copies itself into the game folder and adds a desktop shortcut, so you can delete the download. You do not need to install .NET or PowerShell.

**Coming from v0.3.7:** do not uninstall first. Close the game and the old launcher, then open the new one. The game is updated, and your settings, keys, saves and name are kept.

**Uninstall:** press **Uninstall Multipatch** on Home. The game returns to clean v1.1.1 and your saves stay.

## Download from the command line

These scripts get the newest package for your system from GitHub Releases: Windows, Linux, Steam Deck or macOS. You need them once: after that, the launcher offers updates by itself.

- Windows: `Gears3_Multipatch_Launcher_Download.bat`, or `Gears3_Multipatch_Launcher_Download.ps1` in PowerShell
- Linux, Steam Deck, macOS: `bash Gears3_Multipatch_Launcher_Download.sh`

Add a platform to choose another package: `Windows_x64`, `Linux_x64`, `macOS_arm64` or `macOS_x64`.

## Requirements

| | |
|---|---|
| Game | Gears of War 3 Project Jacinto v1.1.1 |
| Windows | Windows 10 or 11, 64-bit |
| Linux and Steam Deck | 64-bit, with Proton, umu-launcher or Wine |
| macOS | Apple Silicon or Intel, with Wine or a compatible layer |
| Play on Steam | The Steam client must be running. Co-op players need the same JacintoNet version. |

32-bit systems are not supported, because the game itself is 64-bit.

## Features

### Online campaign co-op
JacintoNet carries the game's traffic over Steam P2P, so no port forwarding or VPN is needed. The host presses F8 in the game or uses the Steam overlay to invite a friend. This feature is experimental.

### Campaign fixes
- **Act 1:** "Homecoming" razor wire soft-lock and the slow motion that never ended in "Abandon Ship".
- **Act 2:** the Mother Corpser boss fight, restored, the "Wreckage" Brumak ending after a checkpoint reload, and the placeholder Brumak death text.
- **Act 3:** the Mercy mausoleum cinematic (Dom's tags and knife) and the "Objective Failed" bug moving to Act 4.
- **Act 4:** "Crater" Tower 2 elevator and cable car, and "Bon Voyage" weight plates and its empty last stage.
- **Act 5:** "Coast" stealth and its brightness hack, and the Tempest final boss soft-lock.
- The Insane difficulty now unlocks, and checkpoints and Load Game work.

### Display and interface
- Ultrawide and high-resolution fixes for menus and the "Objective Failed" screen.
- The HUD scales with the resolution. It can be turned off.
- Larger subtitles with Turkish and Ukrainian characters, and a crash fix for wide and high-resolution screens.
- Optional Vulkan renderer (DXVK) and borderless window on Windows.

### Input
- Keyboard or gamepad choice, with matching button icons.
- ESC and Enter work in menus where they did not before.
- Fixed the inverted analog stick and the "Loading Profile" hang. All keys can be rebound, Numpad included.

### Stability and performance
- Fixes for Bink and XAudio2 crashes.
- Texture streaming tuned for HD texture packs, and a higher frame rate ceiling.
- On-screen debug text and warnings removed.
- A Quit Game button in the main menu.

### Launcher
- Play Offline or Play on Steam from one installation.
- Graphics, audio, controls, player name, FOV and game language in one place.
- Verify, Repair and save backups. Automatic updates that keep your settings.
- Add to Steam, Discord presence, themes, and full keyboard and gamepad navigation.
- Interface in 8 languages. Community language packs are listed on the Recommended page.

## Reporting bugs

Use the [Bugs tab on Nexus Mods](https://www.nexusmods.com/gearsofwar3/mods/27?tab=bugs). The About page opens the launcher's log file, so you can attach it. For co-op problems, also send `GearGame\Logs\Launch.log` from both players.

## Building

See [DEVELOPMENT.md](DEVELOPMENT.md). The source builds without game files, but the first-install packages also need patch data made from a clean game, which is not in this repository.

## Credits

- **DryreL:** Multi Patch Tool, online co-op (JacintoNet), campaign fixes, launcher since v0.3.8
- **refresh50:** first launcher (v0.3.7), checkpoint and Load Game fix, Bink crash fix, missing objectives fix
- **LukeBoya:** Mother Corpser boss fight, Act 3 to Act 4 transition
- **Dlposn:** Loading Profile bypass, opening logos
- **HJ:** keyboard and mouse button icons
- **Grilex:** high-resolution subtitle font
- **pasha12071991:** fonts with Turkish and Ukrainian characters
- **wuxinwei110:** larger subtitle font for Traditional Chinese
- **Helios, Extinct, Casper:** the Jacinto build

The launcher's Credits page has the full list.

## Support

Donations are never required. This is a volunteer, non-profit project: you can [support it on Nexus Mods](https://www.nexusmods.com/gearsofwar3/mods/27?tab=description) if you want to.

## License and disclaimer

The launcher source is under the [MIT license](LICENSE). Game files are not included.

This is an unofficial community project with no connection to Epic Games, The Coalition or Microsoft.

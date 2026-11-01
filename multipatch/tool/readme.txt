Mod Page: https://www.nexusmods.com/gearsofwar3/mods/27
Author: DryreL

---	

[ ] WINDOWS / LINUX USERS:

- Create a new folder with that exact name Gears3_Multipatch_Tool
- Place all the extracted file inside it and then paste that folder into your Gears of War 3 folder.
- Run the bat file (on Windows) or sh file (on Linux).
- Have fun!

[ ] STEAMOS USERS;

SteamOS is an immutable (read-only) operating system based on Arch Linux, which is why standard package managers (apt, dnf, yay) and Microsoft’s standard distro installer pages don't work out of the box.

You do not need sudo, you do not need to disable read-only mode, and you don't need yay.

Here are the easiest ways to get it working on Steam Deck:

Method 1: Official Portable PowerShell (Recommended — 10 Seconds, No Root)
Microsoft provides official portable standalone tarballs of PowerShell for 64-bit Linux. You can download and extract it directly into your home folder with a single command. It will survive SteamOS system updates and requires no root privileges.

Switch to Desktop Mode on your Steam Deck.

Open Konsole (terminal) and paste this single line, then press Enter:bashmkdir -p ~/powershell && curl -L https://github.com/PowerShell/PowerShell/releases/download/v7.4.5/powershell-7.4.5-linux-x64.tar.gz | tar -xz -C ~/powershell

That's it! PowerShell is now installed in ~/powershell/pwsh.

Now, open your game folder in Konsole (or right-click empty space in your Gears 3 game folder in Dolphin file manager and choose "Open Terminal Here") and run:bashbash Gears3_Multipatch_Tool.sh (The script will automatically detect the portable PowerShell and launch the tool).

Method 2: Flatpak (Terminal install without root)
While Discover often filters out command-line tools, Flathub provides the official PowerShell Flatpak package. In Konsole, run:

bashflatpak install --user -y flathub com.microsoft.powershell
Once installed, you can simply run bash Gears3_Multipatch_Tool.sh from your game folder.

Method 3: The PC Transfer Alternative (No Terminal on Steam Deck)
If you have a Windows PC:

Keep the game folder on your PC first.

Run Gears3_Multipatch_Tool.bat to install the multi-patch on Windows.

Copy/transfer the patched game folder over to your Steam Deck (via USB drive, SD card, Warpinator, or local network share).

Because Gears of War 3 uses Unreal Engine 3, all config (.ini) and game (.upk / .gear) files are 100% identical between Windows and Steam Deck (Proton).

[ ] HOW TO UPGRADE?
- Without deleting anything, overwrite the files in the same folder by selecting the "replace" option.
- Open the Multipatch tool and install normally.
- If you have previously installed the patch, there must be a "Backups" folder inside the "_files" folder.
- Do not delete this; during the upgrade, it will first revert the previously applied patches before installing the new one.

[ ] REQUIREMENTS:

WINDOWS USERS;
- DirectX End-User Runtimes (June 2010) - https://www.microsoft.com/en-us/download/details.aspx?id=8109
- .NET Framework 2 and 3.5

PROTON / WINE USERS;

"Fatal error! __DW_D3D9_DwTriovizCreatePixelShader() 0x42e5e18c + 17 bytes [File=d:\dev\thirdpartystuff\trioviz\dwtriovizsdk.2011.04.07\dwtriovizsdk_x\src\dwtriovizshader\dwtriovizshadercompiler.cpp:26] [in X:\Games\Gears 3 Jacinto v1.1.1\Binaries\Win64\GearGame.exe]"

Looks like the game compiles its 3D-TV (Trioviz) shaders at runtime and needs D3DCOMPILER_43.dll + d3dx9_43.dll,
which are missing in your Proton prefix. These two DLLs normally come with Windows via the DirectX June 2010 Runtime. If they are missing or not working in Proton, a fatal error occurs when the DwTriovizCreatePixelShader shader fails to compile.

Try:
protontricks <appid> d3dcompiler_43 d3dx9_43
(or winetricks), then launch again.

<AppID> is the ID listed by protontricks if the game was added to Steam as a non-Steam game.

If using Winetricks, the same two packages (winetricks d3dcompiler_43 d3dx9_43) should be installed to the same prefix.
If that doesn't solve the issue, you can try the PROTON_USE_WINED3D=1 launch option.

---

[ ] SPLIT-SCREEN CO-OP (two controllers):

- Choose Gamepad (not Keyboard & Mouse) in the installer when playing split-screen with two controllers.
  The Keyboard & Mouse choice changes the menu buttons Back / Start / X to Escape / Enter / E, so a gamepad
  player cannot use those buttons in the menus while it is installed.
- Every connected controller joins automatically in the main menu (max. 2 players). Unplug the second
  controller (or turn off Steam Input virtual controllers) to play alone.
- The first controller is always Player 1 and controls the main menu (only its profile is saved on PC:
  campaign progress and settings). The second controller only picks its own line on the difficulty screen.
- Player 2 always plays on Player 1's difficulty.

---

[ ] ONLINE CO-OP THROUGH STEAM (experimental):

- The installer asks 'Enable online co-op through Steam' first (ENTER = yes). Both players need it and the same
  Multi Patch Tool version. Steam must be running. No port forwarding or VPN is needed.
- Add Binaries\Win64\GearGame.exe to Steam (Games > Add a Non-Steam Game) and start the game from Steam,
  so the Steam overlay (Shift+Tab) works. Your friends see you playing 'Spacewar' (Valve's test app).
- Host: start the Campaign, then press F8 (or Shift+Tab > right-click a friend > Invite to game).
- Friend: have the game running in the main menu, then accept the invite. The game joins automatically.
  Accepting an invite while the game is closed starts the real Spacewar instead.
- The joining player plays on the host's difficulty.
- Your Steam status shows the act and chapter (e.g. 'Act IV, Chapter 2: Crater'). A friend with a different
  game build (GearGame.u) cannot join; the reason is written to JacintoNet.log.
- Known: inviting while the host is still in the main menu (party lobby) can leave the friend's textures
  grey after the Campaign starts. Start the Campaign first, then invite. Rejoining fixes it.
- Log: GearGame\Logs\JacintoNet.log
- Linux / Proton: add WINEDLLOVERRIDES="wsock32=n,b" %command% to the launch options (untested).
- Answering N to the Steam question: the Campaign still runs a listen server, so LAN co-op works without
  Steam (host starts the Campaign, the friend types in the console: open <host IP>:7777).
- OFFLINE MODE: after N the installer asks 'Switch the Campaign to full offline mode?' (ENTER = no).
  Yes turns the listen server off: the game opens no network port and nobody can join your Campaign
  (no LAN co-op, no Steam invites). Split-screen co-op on the same PC still works.
  To get co-op back, run the installer again and answer N to the offline question.

---

* This patch was downloaded from NexusMods.
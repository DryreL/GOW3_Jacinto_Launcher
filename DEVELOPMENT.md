# Gears of War 3 Jacinto Launcher: development notes

Avalonia on .NET 10, self-contained, one window. `legacy-wpf/` holds the previous WPF sources for reference and is not compiled.

## Commands

```bash
dotnet run --project tests/LauncherSelfTests -c Release
```

```bash
dotnet publish GoW3Jacinto_Launcher.csproj -c Release -r win-x64 -o publish/win-x64
```

Other runtime IDs: `linux-x64`, `osx-arm64`, `osx-x64`.

## Multipatch patch data

The launcher installs the Multipatch without PowerShell, from `multipatch.jcpatch`: a zip of what a clean Jacinto 1.1.1 needs in order to become the patched game (binary deltas and whole files, each with its expected SHA-256). Game-derived data stays out of this repository; only the package carries it.

```bash
powershell -File tools/Build-Patchset.ps1 -CleanGame "<clean Jacinto 1.1.1>" 
```

```bash
Build.bat
```

`Build-Patchset.ps1` runs the PowerShell Multi Patch Tool on a disposable copy, builds the patch data from it, installs that with the C# engine on a second copy and fails unless every game file is byte-identical. For a new fix, change the tool or a patched copy of the game and run it again; `GoW3Jacinto_Launcher --build-patchset <clean> <patched> <output>` is the underlying step.

Behaviour: binary files must match their expected original hash, or nothing is changed. Text files (INI, localization) that differ are replaced, with the player's copy kept in `tools/jacinto-originals`. The patch data records the build PC's display, so the installer re-applies the resolution for the current screen. Uninstall removes only files named in the patch data, so saves stay. Headless forms: `--install`, `--verify`, `--repair`, `--uninstall`.

`Build.bat` asks for a platform (1-4, or 5 for all) and writes one zip per platform plus `Gears3_Multipatch_Launcher_v<version>_Source.zip` to `dist/`. The patch data lives in `patch-data/` (not committed) and is embedded in the player executable with `-p:JacintoEmbedPatch=true`. A `multipatch.jcpatch` beside the executable wins over the embedded one. Each zip holds one executable, `INSTALL_TUTORIAL.txt`, `changelog.txt` and `LICENSE`.

`changelog.txt` is the player changelog: add new versions on top. It and `assets/credits.txt` are shown in the launcher.

The HD Textures Pack needs no order: it shares no binary file with the patch data, and `HdTextureCaps.cs` applies its cap rule after install and before every launch.

The VRAM profile is 4 GB and the drive profile SSD, the same values the previous hidden PowerShell install always answered.

## Multipatch sources

`multipatch/tool` is the PowerShell Multi Patch Tool with its fixes and JacintoNet; `multipatch/reference-package` is the package `Build-Patchset.ps1` runs. Both live in this folder so one checkout holds everything. Only scripts and source code are committed: maps, packages, DLLs, the Steamworks SDK and other game-derived files are ignored, because this repository is public.

## Translations

`languages.csv` holds all launcher text: one column per language, the first row is the code, the second the name shown in the list. It is built into the launcher. A `languages.csv` beside the executable is read on top of it, so a translator can fix a text or add a column for a new language without building anything. Empty cells fall back to English.

To start a new language, run `python tools/l10n_audit.py --new hu Magyar`. It writes `languages-hu.csv`: every line in English beside an empty `hu` column. The translator fills that one column and puts the file beside the launcher, which reads every `languages-<code>.csv` there; nothing else changes. To ship it, paste the column into `languages.csv`.

## Discord

Discord lists `geargame.exe` as Gears of War 4 (`/api/v9/applications/detectable`), and its Gears of War 3 entry has no executable. The launcher therefore starts a copy named `GearsOfWar3.exe`, removed on uninstall, and sets the activity with the Gears of War 3 application id (`DiscordPresence.cs`).

## Releases and updates

Pushing a `v*` tag builds, signs when configured, and makes a draft GitHub Release with `Gears3_Multipatch_Launcher_v<version>_<platform>.zip` and `SHA256SUMS.txt`. The zips hold the patch data: `Release.bat` first puts `dist/multipatch.jcpatch` on the `patch-data` prerelease, CI downloads it from there and checks its SHA-256, and `Release.bat` publishes the draft when CI is done. The patch data is not committed to the repository. Pull request builds skip it. The launcher reads the latest release at startup and from Settings > Check for updates, shows its notes on the Home page and offers the update when the version is newer. Set `<Version>` in the csproj before tagging. Updates replace only the launcher folder, not the patch data.

CI has no patch data, so these update zips hold a launcher without it. They update installed launchers but cannot install the Multipatch. New players need the zips from `Build.bat`: attach them to the release by hand. `JACINTO_RELEASES_URL` replaces the GitHub address for testing.

## Sentry and Aptabase

Set the keys at build time, in one of two ways:

- `keys.local.props` (not committed) sets `JacintoSentryDsn` and `JacintoAptabaseKey` for local builds. Sentry reports go to GlitchTip, which uses the same DSN format: `https://<key>@glitchtip.dryrel.com/<project>`.
- Set environment variables or CI secrets of the same name (`SENTRY_DSN`, `APTABASE_APP_KEY` in `.github/workflows/build.yml`).

An empty key turns that service off. `JACINTO_SENTRY_DSN` and `JACINTO_APTABASE_KEY` override them at run time for testing. Users opt out in Settings, and `DO_NOT_TRACK=1` is honored.

Events: `app_started`, `game_launch` (mode), `settings_applied` (kind, graphics quality), `multipatch_install` (result), `launcher_update` (version). Sentry messages have the game folder, home folder and user name replaced before sending.

Game crashes: `GameCrash.cs` looks in `GearGame/Logs` when the game exits and when the launcher starts. A new `*.dmp` or a fatal line in `Launch.log` becomes a Sentry event tagged `source=game`, with the exception code and address from the dump and the last 150 log lines. The player name, Steam IDs, IP addresses and file paths are replaced first. Dump files are never sent. Crashes from before the launcher's first start are ignored.

## Platform support

| Launcher runs on | Game starts through | State |
|---|---|---|
| Windows x64 | direct | Install and settings tested on a copy of the game; game start not tested |
| Proton / Wine (Windows build) | direct | Not tested |
| Linux x64, SteamOS (native) | `umu-run`, Proton, then `wine` | Launcher UI and settings tested in WSL; game start not tested |
| macOS arm64 / x64 | `wine` or a runner set in Settings | Builds only; not run |

32-bit is not offered. The patched game is `Binaries/Win64/GearGame.exe`, so every machine that can run it is 64-bit. .NET also has no 32-bit Linux or macOS runtime.

## Code signing

GitHub cannot issue a Windows code signing certificate.

- **Artifact attestations** are free and already in the workflow. They prove a file came from this repository's CI (`gh attestation verify`). Windows SmartScreen and antivirus engines do not read them.
- **[SignPath Foundation](https://signpath.org/)** gives open-source projects a free Authenticode certificate; the certificate names SignPath Foundation as publisher. The repository is already public. Still needed:
  1. Add an OSI license file to the repository, and make sure every committed file may be released under it. `legacy-wpf/` holds decompiled sources and BAML of the earlier launcher: remove it if its authors have not agreed.
  2. Turn on MFA for everyone with write access, and apply at https://signpath.org/apply.
  3. In SignPath, create the project with slug `GOW3_Jacinto_Launcher` and the `release-signing` policy, and link this repository as a trusted build system.
  4. Add the `SIGNPATH_API_TOKEN` secret and `SIGNPATH_ORGANIZATION_ID` variable in GitHub. The workflow's signing step then runs by itself on every `v*` tag.

  Signing covers the launcher only. `wsock32.dll` and `GOW3Helper.dll` are not built here, so they cannot go through this pipeline until their sources are in the repository.
- **macOS**: on every `v*` tag, CI signs with a Developer ID, adds the secure timestamp and notarizes (`tools/Sign-macOS.sh`) once these five GitHub secrets exist. Without them the build stays unsigned and users run `xattr -dr com.apple.quarantine` on the folder.

  | Secret | Value |
  |---|---|
  | `MACOS_CERTIFICATE_P12` | A "Developer ID Application" certificate with its private key, exported from Keychain Access as `.p12`, then base64: `base64 -i cert.p12 \| pbcopy` |
  | `MACOS_CERTIFICATE_PASSWORD` | The password chosen at that export |
  | `APPLE_ID` | The Apple Account email of the developer account |
  | `APPLE_APP_PASSWORD` | An app-specific password from account.apple.com, not the account password |
  | `APPLE_TEAM_ID` | The 10-character Team ID from developer.apple.com/account |

  The launcher is a bare executable, which cannot hold a stapled ticket: Gatekeeper checks it online on first launch.

## Antivirus findings

VirusTotal, looked up by SHA-256 on 2026-11-01:

| File | Result | SHA-256 |
|---|---|---|
| `wsock32.dll` (JacintoNet) | 1/71, Kaspersky `VHO:Trojan.Win32.Yephiler.gen` | `DA49D8AF60BB5FA9AEB2C1C1CF47549D96C28F2EEF2341E59FFF6E616EE4E1D9` |
| `GOW3Helper.dll` | 2/68, MaxSecure and SecureAge | `0D4C055BA7BA68343D6915B5E0F34CE439BB11A34C434094049D3CE9D0372DB3` |
| Old launcher (`.dll`, `.exe`, bundle) | 0 | |

The Yephiler detection is on `wsock32.dll`, not the launcher, so no launcher change can clear it. The file is a Winsock proxy that imports `GetAsyncKeyState` (the F8 invite key) and `VirtualProtect`, and Kaspersky describes the Yephiler family as a keylogging spy trojan. A heuristic match on that shape is likely, though only Kaspersky can confirm the cause.

Since JacintoNet 0.3.6 rebuilt on 2026-11-01 (`C49177AA...`), F8 uses `RegisterHotKey`, held only while the game has focus, and `GetAsyncKeyState` is gone. `VirtualProtect` remains: the static C runtime imports it. Whether the detection clears is unconfirmed until the new file is scanned. If it remains, report it at https://opentip.kaspersky.com/. A rebuild changes the hash, so a verdict does not carry over to the next build unless the file is signed.

Launcher choices that keep its own score clean: no PowerShell at all, no runtime C# compilation, no Native AOT, full version metadata. Re-scan every release build, because the new binaries have no reputation yet.

The player asked for one portable file, so the three native graphics libraries unpack to `%TEMP%\.net` on first start. Unpacking at startup is a pattern some antivirus heuristics dislike. If scans get worse, set `IncludeNativeLibrariesForSelfExtract` to false in the csproj and ship the libraries beside the executable.

## Online count

A release that changes the fixes needs the patch data too, because the repository does not hold it: run `tools\Build-Package.ps1`, then attach `dist\multipatch.jcpatch` and `dist\multipatch.jcpatch.sha256` with `gh release upload <tag>`, before players start updating. A release without them updates the launcher only. Players who install for the first time need the zip from `dist`, which has the patch data inside; the zips the workflow publishes do not.

Interface text lives in `languages.csv`: a `key` column names each line for translators, then one column per language. Code and pages use the English text, so it is the `en` column that finds a row. `python tools/l10n_audit.py` lists every line on screen that some language does not translate; product names, key names and console commands are not meant to be translated.

Aptabase has no read API for an app key, and its dashboard cookie must not ship in a public executable. The count at the bottom of the menu comes from `tools/presence_server.py`: run it behind HTTPS on a server and set `JacintoPresenceUrl` in `keys.local.props`. Without it the label stays hidden. Launchers with telemetry turned off neither ask nor count. Open launchers call it every 30 seconds and say so when they close; a crashed one drops out after 90 seconds. The total counts each IP address once and stores only a salted hash: keep `presence_salt.txt` and `presence_seen.txt` beside the server, or the total starts again.

## Clean game for patch data

`tools/Build-Patchset.ps1` needs the game exactly as the original archive holds it. A file the tool adds that is already there looks unchanged and silently stays out of the patch data, so the script refuses a copy that holds one.

## Publishing

Commit, run `Build.bat` (5. All), then `Release.bat`. It asks before pushing, tags the csproj version, waits for CI (which makes a draft release), uploads the `dist` packages and the patch data, sets the notes from `changelog.txt` and publishes. `tools\Publish-Release.ps1 -DryRun` checks everything and pushes nothing.


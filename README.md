<p align="center">
  <img src="src/GameProfileManager/Assets/GpmBranding/PNG/gpm_dark_256.png" alt="GPM icon" width="96" height="96">
</p>

<h1 align="center">GPM — Game Profile Manager</h1>

<p align="center">Per-game performance profiles and display-aware graphics configuration snapshots for Windows 11.</p>

<p align="center">
  <a href="README.md"><strong>English</strong></a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <a href="https://github.com/ToPChik2505/GameProfileManager/releases">Download</a> ·
  <a href="https://github.com/ToPChik2505/GameProfileManager/issues">Issues</a>
</p>

<p align="center">
  <a href="https://github.com/ToPChik2505/GameProfileManager/actions/workflows/installer.yml"><img src="https://github.com/ToPChik2505/GameProfileManager/actions/workflows/installer.yml/badge.svg" alt="Windows build"></a>
</p>

## Features

- Set per-game FPS limits through RTSS and manage supported NVIDIA profile options through NVIDIA DRS.
- Save graphics configuration snapshots from files and selected registry values, assign them to displays, and optionally switch snapshots using the effective-display rules.
- Keep safety copies and recover interrupted multi-file operations. GPM does not close games and will not capture or apply configuration while a game is running or a file conflict is unresolved.
- Manage games imported from Steam or Epic, or added by executable; use the WinUI 3 interface with gamepad support.
- Use PCGamingWiki search without credentials, or configure an optional bot password for structured GameData results. The password is stored in Windows Credential Manager.

## Requirements

- Windows 11 x64.
- NVIDIA driver for NVIDIA profile settings; RTSS installed and running for RTSS features.

Download and run the installer from [GitHub Releases](https://github.com/Vladislav2505/GameProfileManager/releases).

## Quick start

1. Add a game and confirm its executable.
2. Create a profile, assign it to the game, then apply it.
3. To switch graphics settings by display, add the game's configuration files, save a snapshot for each setup, assign snapshots to displays, and enable automatic switching.

Only selected configuration sources are managed. Automatic switching uses GPM's effective-display selection and does not detect which monitor contains the game window. If files change outside GPM and do not match a saved snapshot, switching stops and preserves a safety copy.

## Data and backups

Profiles and settings are stored in %LOCALAPPDATA%\GameProfileManager\profiles.db; existing JSON backup workflows remain supported. Graphics snapshots and transaction recovery data are stored under %LOCALAPPDATA%\GameProfileManager\game-configs and %LOCALAPPDATA%\GameProfileManager\transactions. Graphics data has a separate ZIP export/import.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run from the repository root:

```powershell
dotnet publish .\src\GameProfileManager\GameProfileManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o .\artifacts\publish
```

Run GameProfileManager.exe from the complete publish folder. The SDL3 controller library has its own license at [SDL3-LICENSE.txt](src/GameProfileManager/Native/SDL3-LICENSE.txt). The repository does not currently include a project-wide license.

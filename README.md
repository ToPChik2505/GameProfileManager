<p align="center">
  <img src="src/GameProfileManager/Assets/GpmBranding/PNG/gpm_dark_256.png" alt="GPM icon" width="96" height="96">
</p>

<h1 align="center">GPM — Game Profile Manager</h1>

<p align="center">Per-game FPS limits and NVIDIA settings through RTSS and NVIDIA DRS on Windows 11.</p>

<p align="center">
  <a href="README.md"><strong>English</strong></a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <a href="https://github.com/Vladislav2505/GameProfileManager/releases">Download</a> ·
  <a href="https://github.com/Vladislav2505/GameProfileManager/issues">Issues</a>
</p>

<p align="center">
  <a href="https://github.com/Vladislav2505/GameProfileManager/actions/workflows/installer.yml"><img src="https://github.com/Vladislav2505/GameProfileManager/actions/workflows/installer.yml/badge.svg" alt="Windows build"></a>
</p>

GPM manages profiles for individual game executables. It saves the original RTSS and NVIDIA settings before applying a profile, reads the result back, and can restore those original values.

## Features

- Set an RTSS FPS limit and per-game NVIDIA G-SYNC/fixed-refresh and V-Sync options.
- Assign one profile per game or different profiles for selected displays.
- Import games from Steam and Epic Games, or add an executable manually.
- Check applied settings, restore originals, and export a backup of GPM data.
- Use a WinUI 3 interface with controller support, system tray, and light/dark themes.

## Requirements and installation

- Windows 11 x64 with an NVIDIA GPU and compatible driver.
- [RivaTuner Statistics Server (RTSS)](https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/) installed and running when applying profiles.
- Administrator approval when starting the current version of GPM.

Download the installer from [GitHub Releases](https://github.com/Vladislav2505/GameProfileManager/releases). It includes the required .NET and Windows App SDK runtime components. If GPM cannot find RTSS, select its installation directory in **Settings**.

## Quick start

1. Add a game in **My games** and confirm its executable.
2. Create or edit a profile, then assign it to the game.
3. Select **Apply profile to game** and review the result. Use **Diagnostics** to check settings without changing them.

Assigning a profile saves a choice in GPM; **Apply** writes to RTSS and NVIDIA DRS. Automatic application is optional. Readback confirms saved settings, not actual in-game FPS or VRR operation. A running game may need to restart before driver changes take effect.

GPM stores its data in `%LOCALAPPDATA%\GameProfileManager\profiles.db` and preserves existing 1.x data. Exit GPM completely, including its tray icon, before upgrading. You can export a JSON backup in **Settings**.

## Limitations

GPM supports NVIDIA and RTSS; it does not launch games or change Windows display settings. It does not detect which monitor a game window occupies. RTSS identifies profiles by executable filename, so GPM blocks ambiguous writes when different games share that name. The **Graphics configurations** page is a preview; game configuration file snapshots are not implemented.

## Build from source

On Windows 11, install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run from the repository root:

```powershell
dotnet build .\GameProfileManager.slnx -c Release
dotnet publish .\src\GameProfileManager\GameProfileManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o .\artifacts\publish
```

Run `GameProfileManager.exe` from the complete `artifacts\publish` directory; the EXE needs its accompanying files. To build the installer, install [Inno Setup](https://jrsoftware.org/isdl.php) and run `.\scripts\build-installer.ps1`.

## Support and license

Report bugs in [Issues](https://github.com/Vladislav2505/GameProfileManager/issues), including the GPM, Windows, NVIDIA driver, and RTSS versions and steps to reproduce. For source changes, follow [AGENTS.md](AGENTS.md).

No project-wide license is currently included. Public source access alone does not grant redistribution or modification rights. Bundled SDL3 has a [separate license](src/GameProfileManager/Native/SDL3-LICENSE.txt).

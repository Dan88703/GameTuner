# GameTuner

**Automatic game optimizer for Windows laptops.**

GameTuner analyzes your hardware, recommends the right graphics preset, tunes
game config files, and switches Windows into gaming mode the moment a game
starts — then puts everything back when you quit. Live temperatures, GPU load
and an FPS counter show what is actually happening.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0078D4)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-lightgrey)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

---

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [How to use](#how-to-use)
- [Admin mode: temperatures and FPS](#admin-mode-temperatures-and-fps)
- [Is it safe with anti-cheat?](#is-it-safe-with-anti-cheat)
- [Troubleshooting](#troubleshooting)
- [Architecture](#architecture)
- [Adding a new game](#adding-a-new-game)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

---

## Features

### Hardware analysis
- Detects CPU, GPU(s) with real VRAM (read from the registry, not the 4 GB-capped WMI value), RAM, SSD/HDD, screen resolution and refresh rate.
- Scores GPU, CPU and RAM and turns them into one **performance tier**: Low / Medium / High / Ultra.
- Explains bottlenecks: integrated graphics, too little RAM, HDD, laptop running on battery, dual-GPU setup.
- Works with **AMD, Intel and NVIDIA** — including integrated Radeon / Iris Xe graphics.

### Game presets
- Edits config files directly for **Counter-Strike 2**, **Minecraft Java** and **GTA V**.
- Every change is **backed up first**; one click rolls it back. If anything fails mid-write, the change is undone automatically.
- Only keys that already exist in the config are changed — nothing unknown is ever added.
- Universal advice for every other game: preset, resolution, upscaler (DLSS / FSR / XeSS) and FPS cap based on your screen's refresh rate.

### Auto mode
When a known game starts, GameTuner automatically:
- switches to the maximum-performance power plan,
- turns on Windows Game Mode,
- raises the game's process priority,
- optionally closes apps you choose (browser, OneDrive, …),
- starts recording FPS and sensor data.

When the game exits, the power plan is restored and a **session summary** is saved.

Any game can be added by its process name — for example Fortnite or Genshin Impact (see [How to use](#how-to-use)).

### Live monitoring
- CPU and GPU load, RAM usage, battery and charger state — **no admin rights needed**.
- CPU / GPU temperatures and clocks — in admin mode, via LibreHardwareMonitor.
- **FPS and 1% lows** via Intel PresentMon (works on any GPU vendor).

### Session history
The last 50 play sessions: duration, average FPS, 1% low, peak temperatures, average CPU/GPU load.

### Tray app
Lives in the notification area, can start with Windows, and shows a notification when auto mode kicks in.

---

## Requirements

| | |
|---|---|
| OS | Windows 10 (1809+) or Windows 11, x64 |
| To run a release | Nothing — the installer is self-contained |
| To build from source | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Optional | Visual Studio 2022/2026 with the *WinUI application development* workload |

---

## Getting started

### Install a release

Download `GameTuner-x.y.z-setup.exe` or the portable zip from
[Releases](https://github.com/Dan88703/GameTuner/releases).
The installer is per-user and does not need admin rights.

### Build from source

```powershell
git clone https://github.com/Dan88703/GameTuner.git
cd GameTuner

dotnet test tests/GameTuner.Core.Tests     # run unit tests
dotnet run --project src/GameTuner.App      # build and start the app
```

Release build (self-contained, nothing to install on the target PC):

```powershell
dotnet publish src/GameTuner.App -c Release -r win-x64 --self-contained true -p:Platform=x64 -o artifacts/publish
```

Pushing a tag such as `v1.0.0` triggers GitHub Actions to build the installer
and publish a release (`.github/workflows/release.yml`).

---

## How to use

> **Note:** the interface is currently in Russian; English localization is on the [roadmap](#roadmap).
> Names below are translated: *Overview* = «Обзор», *Games* = «Игры», *Optimization* = «Оптимизация», *Settings* = «Настройки».

1. **Overview** — check the detected hardware and the recommended graphics tier.
2. **Games** — pick a preset (or keep *Auto*), expand *What will be changed*, and click **Apply**. **Undo** restores the previous config.
3. **Optimization** — boost power manually, toggle Game Mode, close heavy background apps.
4. **Settings** — configure auto mode, autostart, tray behaviour and FPS capture.

### Adding your own games to auto mode

CS2, Minecraft and GTA V are detected automatically. For any other game, add
its **process name** in *Settings → Other games* (comma-separated, `.exe` is optional):

```
FortniteClient-Win64-Shipping, GenshinImpact
```

To find a process name: start the game, open **Task Manager → Details** and
look for the game's `.exe`. Use the game itself, not its launcher or anti-cheat
helper (for Fortnite: `FortniteClient-Win64-Shipping`, not `FortniteLauncher`
or `..._EAC_EOS`).

---

## Admin mode: temperatures and FPS

GameTuner runs as a normal user by default. Two features need administrator
rights, and are enabled with **Restart as administrator** (Overview or Settings):

| Feature | Why admin is needed |
|---|---|
| CPU / GPU temperatures and clocks | LibreHardwareMonitor reads hardware sensors through a kernel driver |
| FPS counter | PresentMon listens to Windows ETW graphics events |

**PresentMon** is not bundled. On first use, *Settings → Download PresentMon*
fetches the official x64 build from
[GameTechDev/PresentMon releases](https://github.com/GameTechDev/PresentMon/releases)
into `%LOCALAPPDATA%\GameTuner\tools`. It works with AMD, Intel and NVIDIA GPUs.

---

## Is it safe with anti-cheat?

GameTuner does **not** inject into games, read game memory or modify game files
while a game is running:

- Config presets are applied only while the game is **closed** (GameTuner refuses otherwise).
- FPS is measured from the outside through standard Windows ETW events — the same method used by AMD, NVIDIA and Intel overlay tools.
- Raising priority may be refused for games protected by anti-cheat (e.g. Fortnite with Easy Anti-Cheat). That is expected; the other auto-mode steps still work.

---

## Troubleshooting

| Problem | Solution |
|---|---|
| Build error *"file is being used by another process: GameTuner.Core.dll"* | GameTuner is still running (maybe in the tray). Right-click the tray icon → **Exit**, or end `GameTuner.exe` in Task Manager. |
| Game is not detected | Make sure the exact process name is in *Settings → Other games* and click outside the text box to save. Detection runs every 3 seconds. |
| FPS shows a status instead of a number | Read the status text under FPS: it says whether PresentMon is missing, needs admin, is waiting for frames, or stopped (with its exit code). Restart the game after switching to admin mode. |
| *"Temperature sensor unavailable"* | LibreHardwareMonitor's driver could not load (often blocked by Windows security). CPU/GPU load still works. |
| Something else | Logs are in `%LOCALAPPDATA%\GameTuner\logs\`. Please attach the latest log to an issue. |

---

## Architecture

```
src/
  GameTuner.Core/              UI-independent logic (class library, fully testable)
    Hardware/                  WMI + registry hardware scan
    Performance/               GPU/CPU/RAM scoring → PerformanceTier
    Configs/                   Pure text editors: Valve KeyValues, key:value, XML
    Games/                     GameProfile base class + CS2 / Minecraft / GTA V
    Backup/                    Timestamped backups with manifest, restore, pruning
    Tuning/                    Power plans, Game Mode, processes, elevation
    Monitoring/                Sensor loop (basic + LibreHardwareMonitor), PresentMon FPS
    Automation/                Game watcher, auto mode, session history
    Settings/                  JSON settings, autostart
  GameTuner.App/               WinUI 3 app: MVVM (CommunityToolkit), DI, Serilog, Win32 tray icon
tests/
  GameTuner.Core.Tests/        xUnit tests: scoring, config editors, FPS math, backups
installer/                     Inno Setup script
.github/workflows/             CI (tests + build) and release pipeline
```

Key design decisions:

- **Core has no UI dependency.** All logic lives in `GameTuner.Core`, so it can be tested without WinUI.
- **Config editors work on strings, not files** — pure functions that are easy to unit-test.
- **Backup before every write**, automatic rollback on failure.
- **One sensor loop** owns all hardware access (LibreHardwareMonitor is not thread-safe); UI and auto mode only read the latest snapshot.
- **Polling instead of WMI process events** for game detection — WMI events would require admin rights.
- **Own tray icon on the Win32 API** instead of a third-party package — WinUI 3 has no tray control.

### Data locations

All data is per user in `%LOCALAPPDATA%\GameTuner\`:

| Path | Content |
|---|---|
| `settings.json` | User settings |
| `sessions.json` | Play session history |
| `Backups\` | Config backups (last 20 per game) |
| `logs\` | Daily log files (last 7 days) |
| `tools\PresentMon.exe` | FPS capture tool (downloaded on demand) |

---

## Adding a new game

1. Create a class in `src/GameTuner.Core/Games/` that derives from `GameProfile`.
2. Implement:
   - `FindConfigFiles()` — where the config lives,
   - `GetSettings(tier)` — values for Low / Medium / High / Ultra,
   - `Edit()` — pick one of the editors in `ConfigEditors` (or add a new format).
3. Register it in `ServiceCollectionExtensions.AddGameTunerCore()`.
4. Add a unit test with a small sample of the config file.

---

## Roadmap

- [ ] Built-in profiles for Fortnite, Genshin Impact, Valorant, Apex Legends
- [ ] Game profiles as JSON files (add games without recompiling)
- [ ] Automatic detection of installed games (Steam, Epic, EA, Xbox)
- [ ] In-game FPS overlay
- [ ] English / Polish / Russian UI localization

---

## Contributing

Contributions are welcome — especially new game profiles. See
[CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

MIT — see [LICENSE](LICENSE).
Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

*Made by [Daniil Khlyshchankou](https://github.com/Dan88703).*

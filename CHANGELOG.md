# Changelog

All notable changes to GameTuner are listed here.
The project follows [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`.

## [2.0.0] — 2026-11-25

### Added
- **"Improve my laptop" button** on the Overview page with a **health score** (0–100).
  A health check rates ~12 areas — free space, junk, antivirus, autostart, GPU driver age,
  uptime, idle temperature, RAM, Game Mode, quiet mode, GPU scheduling, HDD — and
  one click applies only the safe fixes (junk cleanup, Game Mode, quiet mode, Defender update).
  Everything else is shown as advice with links (e.g. to the AMD / NVIDIA / Intel driver pages).
- **Security page.**
  - **Microsoft Defender** front-end: protection status (or the third-party antivirus in use),
    signature update, quick and full scan with cancel, list of found threats, removal through Defender.
    GameTuner never decides on its own what is malware.
  - **Autostart review**: all Run keys and Startup folders, publisher from the digital signature,
    warnings for missing files, files in Temp, script hosts, encoded PowerShell, unsigned files in AppData.
    Entries are switched on/off exactly like Task Manager does (StartupApproved), so nothing is deleted.

## [1.4.1] — 2026-11-24

### Fixed
- **CPU temperature missing in admin mode.** LibreHardwareMonitor 0.9.5+ reads CPU sensors through the
  PawnIO driver, which must be installed separately. GameTuner now detects a missing PawnIO and shows
  a "Download PawnIO" button in Settings, instead of a vague "sensors unavailable".

### Added
- **ACPI temperature fallback** — when the precise CPU sensor isn't available (no admin, no PawnIO),
  the laptop's ACPI thermal zone is used (marked "ACPI"). Fake constant values are filtered out.
- Temperature charts and the Overview page explain exactly what is missing.

## [1.4.0] — 2026-11-24

### Added
- **Laptop / desktop detection** from the SMBIOS chassis type (with a battery fallback).
  Shown on the Overview page; laptop-only hints (charger, "only on battery" quiet mode) adapt to desktops.
  A UPS "battery" no longer makes a desktop look like a laptop.
- **30 popular games detected out of the box** by auto mode — Fortnite, Valorant, Genshin Impact,
  Dota 2, Apex Legends, League of Legends, Roblox, Rainbow Six Siege, PUBG, Rust and more —
  so a fresh install on another PC needs no setup.
- README: installing GameTuner on other computers.

## [1.3.0] — 2026-11-24

### Added
- **Disk cleanup page** — scan first, see the size of every category, tick what to clean,
  and get the real freed space (measured on drive C:).
  - Temporary files (user and Windows, older than 1 day), Windows Update download cache,
    Delivery Optimization cache, browser caches (Chrome, Edge, Firefox, Opera, Brave — cache only),
    crash dumps and error reports, Recycle Bin, GPU shader cache, Windows component store (DISM).
  - Safety: only files inside known junk folders; symlinks/junctions are never followed;
    locked files are skipped; browser caches are skipped while the browser is open;
    Recycle Bin, shader cache and DISM are opt-in.
  - System folders need admin mode; they stay visible but disabled otherwise.

## [1.2.0] — 2026-11-23

### Added
- **Quiet mode outside games** — when no game is running, CPU turbo boost is turned off
  (and, in "Always" mode, Windows power mode is set to *Best power efficiency*):
  the laptop runs cooler, quieter and lasts longer on battery.
  - Modes: *Off*, *Only on battery*, *Always when no game is running*.
  - Switches off automatically while a game runs, while an app from the
    *needs full power* list runs, or with the **Full power for now** button.
  - Original power settings are saved before any change and restored on game start,
    on exit, and even after a crash (on the next start).

## [1.1.0] — 2026-11-23

### Added
- **Charts page** — live charts (last 1 / 5 / 10 minutes) for CPU, GPU and RAM load, CPU/GPU temperature and FPS.
- **Session charts** — every game session longer than a minute now stores its load / temperature / FPS timeline; pick a past session on the Charts page to see it.
- Overheating hint when temperatures go above 80 °C / 90 °C.
- **Desktop shortcut** toggle in Settings.
- `install-local.ps1` — builds GameTuner and installs it for the current user with a desktop shortcut.
- Autostart entry is refreshed on every start, so it always points to the current exe.

### Fixed
- FPS capture: PresentMon is started with `--no_console_stats`, and its exit code / error is shown instead of an endless "waiting for frames".
- CPU temperature showed `0 °C` when the sensor driver could not load — now shown as "sensor unavailable".

## [1.0.0] — 2026-11-22

First release: hardware analysis and performance tier, presets for CS2 / Minecraft / GTA V with backups,
auto mode, live monitoring, PresentMon FPS, session history, tray icon, installer and CI.

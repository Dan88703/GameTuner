# Changelog

All notable changes to GameTuner are listed here.
The project follows [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`.

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

# Changelog

All notable changes to GameTuner are listed here.
The project follows [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`.

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

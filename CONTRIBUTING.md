# Contributing

Thanks for wanting to help!

1. Fork the repo and create a branch: `git checkout -b feature/my-game-profile`.
2. Keep logic in `GameTuner.Core` and UI in `GameTuner.App`. Core must not reference WinUI.
3. Add or update tests in `tests/GameTuner.Core.Tests` — `dotnet test` must pass.
4. Code and comments in English. Explain *why* in comments for non-obvious code.
5. Open a pull request describing what changed and how you tested it.

## Reporting a bug

Please attach the log from `%LOCALAPPDATA%\GameTuner\logs\` and your hardware
(CPU, GPU, RAM) from the Dashboard page.

## Game profiles

New game profiles are the most welcome contribution. Please include:
- where the config file lives,
- which keys you change per tier and why (FPS impact),
- a small sample of the config file for the unit test (no personal data).

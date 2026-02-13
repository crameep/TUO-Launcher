# TUO-Launcher

Cross-platform launcher for [crameep's TazUO](https://github.com/crameep/TazUO) build. Built with Avalonia UI and .NET 9.

## Download

Grab the latest release for your platform: **[Download](https://github.com/crameep/TUO-Launcher/releases/latest)**

## Features

### Update Channels

- **Stable** — Tagged releases that have been tested on the bleeding edge
- **Bleeding Edge** — Auto-built from the `dev` branch with the latest features and fixes
- **Feature Branch** — Per-branch builds for testing specific changes before they hit dev

### Profile Management

- Multiple profiles for different accounts and servers
- Encrypted credential storage
- Server presets (UO Memento, Insane UO, Eventine, UO Alive, and more)
- Import from ClassicUO Launcher
- Per-profile plugins, auto-login, and reconnect settings

### Cross-Platform

- Windows x64, Linux x64, macOS x64, macOS ARM64
- macOS `.app` bundle with code signing
- No installation required — fully portable

### Self-Update

- Automatic launcher updates with rollback on failure
- Client auto-update with channel selection

## Building from Source

```bash
# Build
dotnet build

# Publish for current platform
dotnet publish -c Release
```

Requires .NET 9 SDK.

## Links

- [TazUO Client](https://github.com/crameep/TazUO)
- [Discord](https://discord.gg/QvqzkB95G4)

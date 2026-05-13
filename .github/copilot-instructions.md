# Copilot Instructions

## Build & Publish

Build (Debug, x64):
```
dotnet build .\PulsarBattery\PulsarBattery.csproj -c Debug -p:Platform=x64
```

Publish single-file executable (Release, x64):
```
dotnet publish .\PulsarBattery\PulsarBattery.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```
Output: `PulsarBattery\bin\Publish\win-x64\PulsarBattery.exe`

There are no automated tests in this repository.

## Architecture

Two-project solution (`PulsarBattery.slnx`):

- **`PulsarBattery.Device`** — Platform-agnostic class library. Communicates with Pulsar mice via HID using [HidSharp](https://github.com/IntergatedCircuits/HidSharp). Contains:
  - `IHidBackend` — interface all device backends implement
  - `X2ClBackend`, `X2V1Backend` — concrete backends per mouse model
  - `HidHelpers` — shared HID read/write/parse utilities
  - `DeviceBatteryStatus` — immutable record returned by backends (`Percentage`, `IsCharging`, `Model`)

- **`PulsarBattery`** — WinUI 3 unpackaged desktop app (x64 only). Key types:
  - `PulsarBatteryReader` — tries each `IHidBackend` in order under a `GlobalReadLock`; returns the first successful read
  - `BatteryMonitor` — background polling loop; respects workstation lock state, poll interval, and alert cooldown
  - `MainViewModel` — drives all UI state; uses manual `INotifyPropertyChanged` (not CommunityToolkit `[ObservableProperty]`)
  - `AppSettingsService` — static service wrapping `AppSettings`; thread-safe via a `lock (Gate)` guard
  - `AppSettings` — immutable `record` persisted as JSON to `%LOCALAPPDATA%\PulsarBattery\settings.json` via `SettingsStore`
  - `TrayIcon` — system tray integration via `H.NotifyIcon.WinUI`
  - `App.MainWindow` — static property, set in `OnLaunched`; required for pickers, dialogs, and tray icon initialization

## Key Conventions

### Adding a new device backend
1. Add a new class to `PulsarBattery.Device` that implements `IHidBackend`.
2. Register it in `PulsarBatteryReader._backends` array — backends are tried in order.

### Settings
- `AppSettings` is an immutable record. Mutate via `AppSettingsService.Update(s => s with { ... })`.
- `AppSettings.Sanitize()` enforces valid ranges and must be called before persisting.
- Settings are **not** stored in `ApplicationData.Current.LocalSettings` — the app is unpackaged.

### Threading
- UI updates from background threads: `DispatcherQueue.TryEnqueue(() => { ... })`.
- HID reads are serialized via `PulsarBatteryReader.GlobalReadLock` (static `object` lock).
- `BatteryMonitor` runs on a `Task.Run` background thread; never touches UI directly.

### App lifecycle
- `--background` / `--tray` launch args skip showing the window on startup.
- `App.ExitApplication()` sets `IsExitRequested = true` before closing — use this flag in `Closed` handlers to distinguish tray-minimize from true exit.
- No dependency injection container is used; services are instantiated directly.

### Platform constraints
- x64 only (`<Platforms>x64</Platforms>`). Do not add AnyCPU or x86 targets.
- Targets `net10.0-windows10.0.22000.0`; minimum version is 22000 (Windows 11 21H2).
- `WindowsAppSDKSelfContained` is `true` — the Windows App SDK runtime is bundled into the exe.

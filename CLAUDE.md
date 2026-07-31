# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Battery level indicator for Pulsar wireless mice (X2 CrazyLight, X2 V1). WinUI 3 unpackaged desktop app with a tray icon; reads battery state over HID.

## Build & Run

```
dotnet build .\PulsarBattery\PulsarBattery.csproj -c Debug -p:Platform=x64
dotnet publish .\PulsarBattery\PulsarBattery.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

Publish output: `PulsarBattery\bin\Publish\win-x64\PulsarBattery.exe` (single file, self-contained runtime + Windows App SDK).

`PulsarBattery.exe --background` (or `--tray`) starts without showing the window.

There are **no automated tests** in this repository — verify changes by building and running the app. CI (`.github/workflows/build.yml`) only runs `dotnet publish`; `release.yml` fires on `v*` tags and rewrites the version in `app.manifest` and `Package.appxmanifest` from the tag.

## Architecture

Two projects (`PulsarBattery.slnx`):

**`PulsarBattery.Device`** — platform-agnostic class library, HID via [HidSharp](https://github.com/IntergatedCircuits/HidSharp). `IHidBackend` (per-model backend), `X2ClBackend` / `X2V1Backend`, `HidHelpers` (shared read/write/parse), `DeviceBatteryStatus` (immutable record: `Percentage`, `IsCharging`, `Model`).

**`PulsarBattery`** — WinUI 3 app, x64 only. No DI container; services are instantiated directly.

- `PulsarBatteryReader` — tries each `IHidBackend` in `_backends` order, returns first success. All reads serialized through the static `GlobalReadLock`.
- `BatteryMonitor` — background `Task.Run` loop (5s tick). Owns its own reader, caches the last good status for 10 min, and only raises notifications. Never touches UI. Uses `GetForegroundWindow() == 0` as a workstation-lock heuristic, with a separate alert threshold while locked.
- `MainViewModel` — a *second* polling loop, on a `DispatcherTimer`, driving all UI state plus history logging. Manual `INotifyPropertyChanged` (not CommunityToolkit `[ObservableProperty]`).
- `AppSettingsService` — static wrapper over `AppSettings`, thread-safe via `lock (Gate)`.
- `TrayIcon` — `H.NotifyIcon.WinUI`; created in `OnLaunched` after the window exists.
- `App.MainWindow` — static, set in `OnLaunched`; required for pickers, dialogs, tray init.

Both polling loops read the device independently — that's intentional, and `GlobalReadLock` is what keeps them from colliding.

### Adding a device backend
1. Implement `IHidBackend` in `PulsarBattery.Device`.
2. Register it in `PulsarBatteryReader._backends` (order = probe order).

### Settings & storage
- `AppSettings` is an immutable `record`. Mutate via `AppSettingsService.Update(s => s with { ... })`.
- `AppSettings.Sanitize()` clamps ranges and normalizes `Language`; must run before persisting.
- Persisted as JSON under `%LOCALAPPDATA%\PulsarBattery\` (`settings.json` via `SettingsStore`, `history.json` via `HistoryStore`, written atomically through a `.tmp` + `File.Move`). **Not** `ApplicationData.Current.LocalSettings` — the app is unpackaged.
- `SelfInstallService` copies the exe to `%LOCALAPPDATA%\Programs\PulsarBattery` and relaunches with `--cleanup-source-exe`; `StartupRegistrationService` writes the `HKCU\...\CurrentVersion\Run` value with `--background`.

### JSON serialization
Release builds set `PublishTrimmed=true`, so **all** serialization must go through the source-generated contexts in `Tools/JsonContext.cs` (`SettingsJsonContext`, `CompactJsonContext`). Adding a new serialized type means adding a `[JsonSerializable]` attribute there — reflection-based `JsonSerializer` overloads will break the trimmed build at runtime.

### Localization
- The **English string is the key**. `Loc.T("Retry")` looks it up and falls back to the key itself.
- Translations live in `Strings\en-US.json` / `Strings\de-DE.json`, embedded as resources (`PulsarBattery.Strings.<locale>.json`). New user-facing text must be added to *both* files.
- In XAML use `Controls\TranslatedTextBlock` (its `Text` is translated on set, and mirrored to `AutomationProperties.Name`); in code-behind use `Loc.T`.
- Adding a locale: add the file, its `EmbeddedResource` entry in the csproj, and the locale to `LocalizationService.SupportedLocales`. `LocalizationService.Initialize` runs after settings load so a saved `Language` override wins; null follows the system UI culture.

### Threading
- UI updates from background threads: `DispatcherQueue.TryEnqueue(...)`.
- HID reads: serialized by `PulsarBatteryReader.GlobalReadLock`.

### App lifecycle
- `App.ExitApplication()` sets `IsExitRequested` before closing — `Closed` handlers use this flag to tell tray-minimize from a real exit.

### Platform / project constraints
- x64 only (`<Platforms>x64</Platforms>`); do not add AnyCPU or x86.
- Targets `net10.0-windows10.0.22000.0` (min Windows 11 21H2). `WindowsAppSDKSelfContained` is `true`.
- Files under `Pages\` are wired up manually in the csproj: each needs both a `<None Update>` entry with `<Generator>MSBuild:Compile</Generator>` **and** a `<Page Include>` entry, otherwise `InitializeComponent` is never generated.

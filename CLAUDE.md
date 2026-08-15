# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Battery indicator and settings tool for Pulsar wireless mice. WinUI 3 unpackaged desktop app with a tray icon; reads status and supported settings over HID.

## Build & Run

```
dotnet build .\PulsarBattery\PulsarBattery.csproj -c Debug -p:Platform=x64
dotnet test .\PulsarBattery.Device.Tests\PulsarBattery.Device.Tests.csproj -c Debug
dotnet publish .\PulsarBattery\PulsarBattery.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

Publish output: `PulsarBattery\bin\Publish\win-x64\PulsarBattery.exe` (single file, self-contained runtime + Windows App SDK).

`PulsarBattery.exe --background` (or `--tray`) starts without showing the window.

Protocol, catalog, checksum and DPI-codec tests live in `PulsarBattery.Device.Tests`. CI (`.github/workflows/build.yml`) runs them before publishing; `release.yml` fires on `v*` tags and rewrites the version in `app.manifest` and `Package.appxmanifest` from the tag.

## Architecture

Three projects (`PulsarBattery.slnx`):

**`PulsarBattery.Device`**: platform-agnostic class library, HID via [HidSharp](https://github.com/IntergatedCircuits/HidSharp). `IHidBackend` (per-protocol backend), `CmouseLegacyBackend` / `X2V1Backend` / `Sonix64Backend`, `HidHelpers` (shared read/write/parse), `DeviceStatus` (immutable status record).

**`PulsarBattery`**: WinUI 3 app, x64 only. No DI container; services are instantiated directly.

**`PulsarBattery.Device.Tests`**: xUnit golden-frame, strict-parser, catalog and DPI round-trip tests. It never opens HID hardware.

- `PulsarBatteryReader`: tries each `IHidBackend` in `_backends` order, returns first success. All reads serialized through the static `GlobalReadLock`.
- `BatteryMonitor`: background `Task.Run` loop (5s tick). Owns its own reader, caches the last good status for 10 min, and only raises notifications. Never touches UI. Uses `GetForegroundWindow() == 0` as a workstation-lock heuristic, with a separate alert threshold while locked.
- `MainViewModel`: a *second* polling loop, on a `DispatcherTimer`, driving all UI state plus history logging. Manual `INotifyPropertyChanged` (not CommunityToolkit `[ObservableProperty]`).
- `AppSettingsService`: static wrapper over `AppSettings`, thread-safe via `lock (Gate)`.
- `TrayIcon`: `H.NotifyIcon.WinUI`; created in `OnLaunched` after the window exists.
- `App.MainWindow`: static, set in `OnLaunched`; required for pickers, dialogs, tray init.

Both polling loops read the device independently. That is intentional, and `GlobalReadLock` is what keeps them from colliding.

### Adding a device backend
1. Implement `IHidBackend` in `PulsarBattery.Device`.
2. Register it in `PulsarBatteryReader._backends` (order = probe order).

### Settings & storage
- `AppSettings` is an immutable `record`. Mutate via `AppSettingsService.Update(s => s with { ... })`.
- `AppSettings.Sanitize()` clamps ranges and normalizes `Language`; must run before persisting.
- Persisted as JSON under `%LOCALAPPDATA%\PulsarBattery\` (`settings.json` via `SettingsStore`, `history.json` via `HistoryStore`, written atomically through a `.tmp` + `File.Move`). **Not** `ApplicationData.Current.LocalSettings`, because the app is unpackaged.
- `SelfInstallService` copies the exe to `%LOCALAPPDATA%\Programs\PulsarBattery` and relaunches with `--cleanup-source-exe`; `StartupRegistrationService` writes the `HKCU\...\CurrentVersion\Run` value with `--background`.

### JSON serialization
Release builds set `PublishTrimmed=true`, so **all** serialization must go through the source-generated contexts in `Tools/JsonContext.cs` (`SettingsJsonContext`, `CompactJsonContext`). Adding a new serialized type means adding a `[JsonSerializable]` attribute there. Reflection-based `JsonSerializer` overloads will break the trimmed build at runtime.

### Localization
- The **English string is the key**. `Loc.T("Retry")` looks it up and falls back to the key itself.
- Translations live in `Strings\en-US.json` / `Strings\de-DE.json`, embedded as resources (`PulsarBattery.Strings.<locale>.json`). New user-facing text must be added to *both* files.
- In XAML use `Controls\TranslatedTextBlock` (its `Text` is translated on set, and mirrored to `AutomationProperties.Name`); in code-behind use `Loc.T`.
- Adding a locale: add the file, its `EmbeddedResource` entry in the csproj, and the locale to `LocalizationService.SupportedLocales`. `LocalizationService.Initialize` runs after settings load so a saved `Language` override wins; null follows the system UI culture.

### Page layout conventions
Every page uses the same shell: `ScrollViewer` → `Grid Padding="32,24,32,32"` → `StackPanel Spacing="24" MaxWidth="1000"` → sections of `StackPanel Spacing="4"` headed by a `BodyStrongTextBlockStyle` `TranslatedTextBlock`. Settings rows are CommunityToolkit `SettingsCard` / `SettingsExpander` (`CommunityToolkit.WinUI.Controls.SettingsControls` is the only toolkit package). Card `Header`/`Description` are set from `ApplyLocalization()`, not XAML. Colors are always `{ThemeResource}`; `PulsarCardStyle` (App.xaml) is the one `StaticResource`.

Two spacing traps, both hit in production and both documented inline where they apply:
- **Page titles carry `Margin="0,0,0,-8"`.** `TitleTextBlockStyle` puts a 28px font in a 36px line height, so ~8px of slack sits below the glyphs and a `Spacing="24"` panel optically reads ~33px under the title but ~25px between sections. The negative margin cancels it. Do not remove it.
- **A closed `InfoBar` still consumes `StackPanel.Spacing`.** `IsOpen="False"` collapses the template to 0px but leaves `Visibility="Visible"`, so the parent still allots a full gap. Three closed bars cost 72px on `MouseSettingsPage`. Bind `Visibility` to the same flag as `IsOpen` (`MouseSettingsPage.BoolToVisibility` via `x:Bind`). `SettingsPage.InstallInfoBar` still has this, worth ~12px.

`SettingsPage.xaml` holds page-local `SettingsSectionHeaderStyle` and `SettingsNumberBoxStyle` in `Page.Resources`. The NumberBox style must stay unbased: WindowsAppSDK ships no `DefaultNumberBoxStyle` key. Put shared styles in `App.xaml`, never in a new `.xaml` under `Pages\` (see the csproj dual-entry rule below).

### Change-handler guards
Pages are **not** cached (no `NavigationCacheMode` anywhere), so every visit reconstructs the page, and `NavigationView` has `SelectionFollowsFocus="Enabled"` so keyboard focus alone triggers that. `AppSettingsService.Update` then persists on a 750 ms debounce with no visible confirmation, so a stray handler silently corrupts `settings.json`.

The house pattern, in `SettingsPage` and `MouseSettingsPage`: a `_isUpdating*` latch **initialised to `true`**, cleared at the end of `Loaded`, and **re-armed in `Unloaded`**; every handler early-returns while it is set. A constructor-scoped guard is not enough, because WinUI raises `SelectionChanged` / `Toggled` both when a control is templated and again when the page is torn down.

Consequences worth keeping:
- Populate selection state in `Loaded`, not the constructor. `ViewModel` (`DataContext as MainViewModel`) is null in the constructor and only resolves by `Loading`. `ApplyLocalization()` is the exception and stays in the constructor so cards are not blank through first layout.
- Never persist from a null or untagged selection. `LanguageComboBox` items are declared in XAML and the "Auto" item carries `Tag="auto"`, a UI-only sentinel mapping to a null `AppSettings.Language`; a null `Tag` made "user picked Auto" indistinguishable from "fired with no selection" and wiped the saved language.
- `StartWithWindowsToggle` has **no** `IsOn` binding. A `Mode=OneWay` x:Bind pushes on load and raises `Toggled`, re-entering the autostart install flow. `IsOn` is only ever set through the latched `ApplyStartWithWindowsToggleState`.

### Threading
- UI updates from background threads: `DispatcherQueue.TryEnqueue(...)`.
- HID reads: serialized by `PulsarBatteryReader.GlobalReadLock`.

### App lifecycle
- `App.ExitApplication()` sets `IsExitRequested` before closing, so `Closed` handlers use this flag to tell tray-minimize from a real exit.

### Code style
- **No em dashes**, in code, comments, XML docs, commit messages or docs. Use a colon, a full stop, or parentheses instead.
- Do not HTML-escape inside `//` comments. `List<T>` is written literally; `&lt;` belongs only in `///` XML docs.

### Comments
**Comment only what the code cannot say, in at most two lines.**

A comment earns its place when it records *why*: a non-obvious constraint, a platform bug being worked around, a measured hardware behaviour, a decision that looks wrong until explained, or a trap that would otherwise get "cleaned up" (the negative title margin, the unbased NumberBox style, the guard latches above).

Delete on sight:
- Anything a competent developer already knows. `// ignore` on an empty catch, `// retry`, `// best-effort X`, `// fall through to Y`, `// Exact match`.
- Narration of the next statement. `// Build device info line`, `// Check if minimize to tray is enabled`.
- Section-divider banners. `<!-- Monitoring Section -->` above a header that already reads "Monitoring".
- `<exception>` docs that restate a `throw` visible three lines below, and `<param>` docs that only repeat the parameter name.

**Two lines is the cap, including XML docs.** When condensing, keep the finding and drop the narrative: state the behaviour and what the code does about it, not how it was discovered. The 9-line note on the dongle reporting 0% became two lines that still name the behaviour, the consequence (a false low-battery alert) and the exception (0% while charging is real).

Keep, whatever the length pressure: bit and frame layouts, register addresses and quirks, and anything only re-derivable with the hardware in hand. Prefer one line per `<param>` on records.

**Verification data belongs in tests, not comments.** Golden samples such as `07 07 00 47 = 400 DPI` live in `PulsarBattery.Device.Tests`; check they are covered there before removing them from a comment.

### Platform / project constraints
- x64 only (`<Platforms>x64</Platforms>`); do not add AnyCPU or x86.
- Targets `net10.0-windows10.0.22000.0` (min Windows 11 21H2). `WindowsAppSDKSelfContained` is `true`.
- Files under `Pages\` are wired up manually in the csproj: each needs both a `<None Update>` entry with `<Generator>MSBuild:Compile</Generator>` **and** a `<Page Include>` entry, otherwise `InitializeComponent` is never generated.

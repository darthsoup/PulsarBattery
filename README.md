# Pulsar Battery

Pulsar Battery is a small Windows app that keeps an eye on the battery of your Pulsar wireless mouse. It sits in the system tray, shows the current charge, and warns you before the battery runs out. For newer mice it can also change the mouse settings, so no extra software is needed.

![alt text](screenshot.jpg)

## Features

- Battery level, charging state and device info at a glance
- Change mouse settings like DPI and polling rate directly from the app
- Notifications when the battery runs low
- Tray icon showing the current battery percentage
- Battery history over time
- Optional start with Windows
- Available in English and German

## Supported Devices

| Device or family | VID | PID matching | Protocol | Settings support |
|---|---|---|---|---|
| Pulsar cMouse V1.31 CID-87 family | `0x3710` | 50 candidate PIDs | 17-byte reports | Read and write for 125 profiles; MID 1 hardware-verified |
| X2 V1 | `0x25A7` | `0xFA7B` wired, `0xFA7C` dongle | 17-byte reports | Read-only settings |
| X2 V3 eS | `0x3710` | `0x3406` wired, `0x5403` dongle | 64-byte feature reports | Read and write |

The cMouse entry is a snapshot of the devices listed by Pulsar cMouse V1.31, not a claim that every Pulsar mouse is supported. It contains 125 CID `0x57` / MID profiles and 50 candidate USB PIDs (the 49 `USB_PID` entries plus the `0x5406` 8K receiver). Several editions share the same underlying mouse, and shared receivers mean that a PID alone cannot identify the paired model. Pulsar Battery confirms each candidate with the protocol's CID/MID identification command before selecting its profile.

For these cMouse 17-byte profiles, Pulsar Battery can read and write polling rate, debounce, Motion Sync, active DPI and DPI stage, LOD, angle snapping, ripple control, and sleep time. The catalog includes the three DPI layouts shipped for the Pulsar XS-1, PAW3950, and PAW3955 sensor groups. MID 1, the X2 CrazyLight target used during development, is hardware-verified. Write support for the other 124 profiles is derived from Pulsar cMouse V1.31 and is enabled but not yet verified on matching hardware; the settings page shows this distinction explicitly. Sensor-specific DPI and LOD validation remains active for every profile.

Before the first cMouse write to each detected HID device path and identified CID/MID in an app run, Pulsar Battery saves a complete settings snapshot under `%LocalAppData%\PulsarBattery\DeviceBackups`: the full core region `0x0000..0x00FF` and, for PAW3955 profiles, the full extended DPI region `0x1B00..0x1B23`. Each backup contains the raw region files, a metadata manifest, SHA-256 hashes for every region, and a separate manifest hash. Files are flushed and verified in a private temporary directory before the whole directory is published with one atomic rename. If either reading or persisting this snapshot fails, no EEPROM write is attempted. After that checkpoint, each affected block is still captured in memory before any changes, and every write requires an exact acknowledgement and readback or is rolled back.

Product artwork is independent of the cMouse installation as well. Pulsar Battery uses a curated model/CID/MID mapping to public images on Pulsar's official storefront CDN, downloads only validated PNG files over HTTPS, and caches them under `%LocalAppData%\PulsarBattery\DeviceImages`. The packaged artwork remains available as an offline fallback. No image file is copied from or loaded out of cMouse; discontinued or not-yet-published editions use the closest matching family artwork when an exact official image is unavailable.

The `0x5403` "8K Dongle" is a shared Pulsar accessory (also used by the X3 family), so other mice paired to it may work as well. Devices speaking the 64-byte protocol get the full Mouse settings page, firmware version, and live polling rate. The X2 V3 eS protocol was reverse-engineered for this project since eS models have no official software.

The installed cMouse V1.31 binary does not provide that 64-byte settings implementation: its normal CID-87 settings path is fixed at 17-byte output/input reports, while its firmware updater uses 17- and 49-byte transfers. The separate X2 V3 eS backend therefore remains its own protocol family rather than being inferred from cMouse.

## Build (Visual Studio)

Prereqs:
- .NET 10 SDK (or a Visual Studio installation with .NET 10 support).
- Visual Studio workload: "Desktop development with .NET".
- Windows SDK 10.0.22000.0 or newer; Windows 11 21H2 or newer.

Steps:
1. Open `PulsarBattery.slnx`.
2. Set configuration to `Debug` and platform to `x64`.
3. Set `PulsarBattery` as the startup project, then run.

## Standalone publish (single-file)

The publish profiles are set up to bundle the .NET runtime and Windows App SDK into the app and produce a single main `.exe`.

From Visual Studio:
1. Right-click `PulsarBattery` > Publish.
2. Select the `win-x64` profile and Publish.
3. Output is in `PulsarBattery\bin\Publish\win-x64\`.

From CLI:
```
dotnet publish .\PulsarBattery\PulsarBattery.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```
Output is in `PulsarBattery\bin\Publish\win-x64\` with `PulsarBattery.exe`.

Protocol tests do not access HID hardware and can be run separately:

```
dotnet test .\PulsarBattery.Device.Tests\PulsarBattery.Device.Tests.csproj -c Release
```

## Startup (tray only)

To start the app in the background (tray only) without opening the window:

```
PulsarBattery.exe --background
```

If you want it to run on login, create a shortcut in the Windows Startup folder and add `--background` (or `--tray`) to the shortcut target.

Startup folder:
```
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
```

## Related Projects

- [pulsar-x3-python](https://github.com/jonkristian/pulsar-x3-python/) inspired this project.
- [SimplePulsarBatteryNotification](https://github.com/Elehiggle/SimplePulsarBatteryNotification) base project as python application. It also documents how the mouse was debugged to determine the data format.
- [Bibimbap](https://github.com/amassias/Bibimbap) documents and validates the same cMouse 17-byte protocol on X2 CrazyLight hardware.
- [OpenPulsar](https://github.com/Andalrick/OpenPulsar) is a Linux alternative.

## Contributing

Contributions are welcome! If you find any issues or have suggestions for improvements, please open an issue or submit a pull request.

## License

This project is licensed under the MIT License. See LICENSE.md for details.

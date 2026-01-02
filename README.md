# Pulsar Battery

This project is a simple battery level indicator for Pulsar mice using the hidapi library. It provides a visual representation of the battery status and alerts the user when the battery is low.

## Supported Devices

- X2 CrazyLight
- X2 v1

## Build (Visual Studio)

Prereqs:
- Visual Studio 2022 (17.8+).
- Workload: "Desktop development with .NET".
- Windows 10/11 SDK (10.0.19041.0 or newer).

Steps:
1. Open `PulsarBattery.sln`.
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
dotnet publish .\PulsarBattery\PulsarBattery.csproj -c Release -p:Platform=x86 -p:PublishProfile=win-x86
dotnet publish .\PulsarBattery\PulsarBattery.csproj -c Release -p:Platform=ARM64 -p:PublishProfile=win-arm64
```
Output is in e.g. `PulsarBattery\bin\Publish\win-x64\` with `PulsarBattery.exe`.

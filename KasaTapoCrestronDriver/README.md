# Kasa/Tapo Crestron Driver

`KasaTapoCrestronDriver` is a Crestron Home platform driver for TP-Link Kasa and Tapo devices. It discovers supported devices, publishes them as managed child devices, and exposes supported lights, strips, and optionally plugs through Crestron Home.

## Features

- Discovers TP-Link Kasa devices on the local network.
- Discovers Tapo devices when optional Tapo account credentials are configured.
- Publishes supported bulbs, light strips, and plugs as managed child devices.
- Optionally exposes plugs as light entities for lighting loads.
- Supports light brightness, color, and color-temperature capabilities when available from the device.
- Persists discovered managed-device metadata so child devices can be republished quickly after a reload.
- Starts child device physical connections in the background so Crestron child configuration callbacks return quickly during driver reloads.

## Configuration

The platform driver exposes these configuration items in Crestron Home:

- **Tapo User Name** and **Tapo Password**: optional Tapo credentials. Leave blank to discover only local Kasa devices.
- **Discovery Timeout (Seconds)**: timeout for discovery and per-device connection attempts.
- **Treat Plugs As Lights**: exposes supported smart plugs as light entities.
- **Enable Light Polling**: enables polling for external light-state changes.
- **Light Poll Interval (Seconds)**: polling interval used when light polling is enabled.
- **Sensor/Button Poll Interval (Seconds)**: reserved for future sensor and button entities.

## Build

From the repository root:

```powershell
dotnet build .\KasaTapoCrestronDriver\KasaTapoCrestronDriver\KasaTapoCrestronDriver.csproj -c Release
```

The project targets Crestron Home driver runtime requirements and uses the Crestron DeviceDrivers DevKit package plus `KasaTapoClient` for TP-Link communication.

## Release notes

### 1.0.001.0001

- Improved driver reload behavior by making child-device activation nonblocking from Crestron child configuration callbacks.
- Republished cached managed child devices early during startup so installed child devices can reattach after reload.
- Kept physical device connection and state initialization in the background, allowing the platform driver to come online while child devices finish connecting.
- Added logging around discovery, cache seeding, child configuration status, activation, and startup connection timing.

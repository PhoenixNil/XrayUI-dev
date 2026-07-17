<img width="2172" height="724" alt="image" src="https://github.com/user-attachments/assets/ea4d4a40-76cd-48f5-abc5-ce3bc07d6f3c" />

<h1 align="center">XrayUI</h1>
A native Windows GUI client for the Xray core, built with <a style="text-decoration:none" href="https://docs.microsoft.com/windows/apps/winui">WinUI</a>. Designed to be a fast and lightweight proxy client.
test1

## Features

- Support Shadowsocks, VMess, VLESS, Trojan, Hysteria2, WireGuard and Chain Proxy
- TUN mode
- Subscription import and update
- AI Unlock Status Detection
- Custom routing rules with geoip / geosite
- Auto-start on boot, auto-connect
- Theme and protocol color customization

## UI Preview
<img width="1465" height="982" alt="image" src="https://github.com/user-attachments/assets/ff288102-d874-4ecb-87dd-0a9d880cc1cf" />

## Download

Download the latest release [here](https://github.com/PhoenixNil/XrayUI-dev/releases/latest).

## Getting Started

> [!NOTE]
> Building XrayUI requires [Visual Studio](https://visualstudio.microsoft.com/vs/) and Windows 10 version 1809 or later. Publishing the application also requires the Rust toolchain. If this is your first time building a WinUI 3 application with the Windows App SDK, follow the [installation instructions](https://learn.microsoft.com/windows/apps/get-started/start-here).

Choose either Visual Studio or PowerShell to build the project.

### Option 1: Visual Studio

1. Open `XrayUI-dev.slnx` in Visual Studio.
2. Select the target platform, such as `x64` or `ARM64`.
3. Build the solution.

### Option 2: PowerShell

#### x64

```powershell
dotnet build -c Release -p:Platform=x64
dotnet publish -c Release -r win-x64 -p:Platform=x64
```

#### ARM64

```powershell
dotnet build -c Release -p:Platform=ARM64
dotnet publish -c Release -r win-arm64 -p:Platform=ARM64
```






##  Thanks


<p>
  <a href="https://linux.do">
    <img src="https://img.shields.io/badge/LinuxDo-community-1f6feb" alt="LinuxDo">
  </a>
</p>

## License

Apache License 2.0.

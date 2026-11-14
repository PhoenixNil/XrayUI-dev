<img width="2172" height="724" alt="image" src="https://github.com/user-attachments/assets/ea4d4a40-76cd-48f5-abc5-ce3bc07d6f3c" />

<h1 align="center">XrayUI</h1>
A native Windows GUI client for the Xray core, built with <a style="text-decoration:none" href="https://docs.microsoft.com/windows/apps/winui">WinUI</a>. Designed to be a fast and lightweight proxy client.


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
> Building XrayUI requires the [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0), WinUI build prerequisites, and Windows 10 version 1809 or later. Command-line builds use the installed `dotnet` SDK. For building and debugging inside [Visual Studio](https://visualstudio.microsoft.com/vs/), check the supported Visual Studio version on the SDK download page. Publishing the application also requires the Rust toolchain. If this is your first time building a WinUI 3 application with the Windows App SDK, follow the [installation instructions](https://learn.microsoft.com/windows/apps/get-started/start-here).

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

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

- **Committers and reviewers**: [@PhoenixNil](https://github.com/PhoenixNil)
- **Approvers**: [@PhoenixNil](https://github.com/PhoenixNil)
- **Privacy Policy**: This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it (such as user-configured proxy connections, subscription updates, latency/AI unlock checks, and update checks against GitHub Releases and `www.xrayui.site/changelog.json`). No telemetry, analytics, or personal data is collected or transmitted to the developer.

## License

Apache License 2.0.

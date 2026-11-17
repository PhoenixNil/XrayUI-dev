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


  <a href="https://signpath.io" target="_blank" rel="noreferrer noopener">
  <img  src="https://github.com/user-attachments/assets/1c7544dc-f76b-40db-b686-0357152dc90c"  width="260" />
  </a>

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

- **Committers and reviewers**: [@PhoenixNil](https://github.com/PhoenixNil)
- **Approvers**: [@PhoenixNil](https://github.com/PhoenixNil)
### Third-party services

The features below contact services that are not operated by the developer. Each request reveals the IP address of the connection (the proxy server's address when routed through the proxy) and standard HTTP request metadata to that service. XrayUI sends no account credentials or personal data with them. Their handling of this data is governed by their own privacy policies.

- **Google** — latency tests request `www.gstatic.com/generate_204`, and the AI unlock check contacts `gemini.google.com`. [Privacy policy](https://policies.google.com/privacy)
- **OpenAI** — the AI unlock check contacts `api.openai.com`. [Privacy policy](https://openai.com/policies/privacy-policy/)
- **Anthropic** — the AI unlock check contacts `api.anthropic.com`. [Privacy policy](https://www.anthropic.com/legal/privacy)

## License

Apache License 2.0.

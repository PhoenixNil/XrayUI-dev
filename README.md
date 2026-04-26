# XrayUI

A native Windows GUI client for the Xray core, built with WinUI 3.

## Features

- Shadowsocks, VMess, VLESS, Trojan, Hysteria2
- TUN mode
- Subscription import and update
- Custom routing rules with geoip / geosite
- Auto-start on boot, auto-connect
- Theme and protocol color customization

## Build

Requires .NET 8 SDK and Windows 10 1809 or later.

    dotnet build -c Release
    dotnet publish -c Release -r win-x64

## License

Apache License 2.0.

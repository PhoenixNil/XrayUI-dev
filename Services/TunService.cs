using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using XrayUI.Helpers;

namespace XrayUI.Services;

/// <summary>
/// TUN 模式相关服务，负责网络接口检测和 TUN 路由配置
/// </summary>
public class TunService
{
    private readonly string _engineDirectory;
    private const int OutboundInterfaceCacheSeconds = 30;
    private string? _cachedOutboundInterface;
    private DateTimeOffset _cachedOutboundInterfaceAt;

    /// <summary>默认 TUN 接口名称（必须与 XrayConfigBuilder.BuildTunInbound 中的 name 字段一致）</summary>
    public string DefaultTunInterfaceName => "xray-tun";

    /// <summary>默认 TUN 接口地址（CIDR 格式）</summary>
    public string DefaultTunInterfaceAddress => "10.255.0.1/24";

    /// <summary>DNS 服务器列表（防 DNS 泄漏）</summary>
    public string[] DnsServers => ["223.5.5.5", "119.29.29.29"];

    /// <summary>TUN 网关地址（从 CIDR 提取）</summary>
    public string TunGateway => DefaultTunInterfaceAddress.Split('/')[0];

    public TunService()
    {
        _engineDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "engine");
    }

    /// <summary>
    /// 检测主要的出站网络接口名称
    /// </summary>
    public string? DetectOutboundInterface(bool forceRefresh = false)
    {
        try
        {
            if (!forceRefresh && TryGetCachedOutboundInterface(out var cached))
                return cached;

            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                    && !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    && !ni.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                    && !ni.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase)
                    && !ni.Description.Contains("TUN", StringComparison.OrdinalIgnoreCase))
                .Select(ni => (Interface: ni, Props: ni.GetIPProperties()))
                .OrderByDescending(x => x.Props.GatewayAddresses.Count)
                .ThenByDescending(x => x.Interface.Speed)
                .ToList();

            // 优先选择有 IPv4 网关的接口（主要上网接口）
            var primary = interfaces.FirstOrDefault(x =>
                x.Props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));

            var detected = primary.Interface?.Name ?? interfaces.FirstOrDefault().Interface?.Name;
            CacheOutboundInterface(detected);
            return detected;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TunService] 检测网络接口失败: {ex.Message}");
            return null;
        }
    }

    private bool TryGetCachedOutboundInterface(out string? cached)
    {
        cached = _cachedOutboundInterface;
        if (string.IsNullOrWhiteSpace(cached))
            return false;

        var age = DateTimeOffset.UtcNow - _cachedOutboundInterfaceAt;
        return age <= TimeSpan.FromSeconds(OutboundInterfaceCacheSeconds);
    }

    private void CacheOutboundInterface(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        _cachedOutboundInterface = value;
        _cachedOutboundInterfaceAt = DateTimeOffset.UtcNow;
    }

    /// <summary>检查 wintun.dll 是否存在</summary>
    public bool IsWintunAvailable()
    {
        var wintunPath = Path.Combine(_engineDirectory, "wintun.dll");
        var exists = File.Exists(wintunPath);
        Debug.WriteLine($"[TunService] wintun.dll 路径: {wintunPath}, 存在: {exists}");
        return exists;
    }

    /// <summary>获取 wintun.dll 的预期路径（用于错误提示）</summary>
    public string GetExpectedWintunPath() => Path.Combine(_engineDirectory, "wintun.dll");

    /// <summary>获取所有可用的网络接口列表</summary>
    public string[] GetAvailableInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(ni => ni.Name)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>获取当前默认网关地址</summary>
    public string? GetDefaultGateway()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

            foreach (var ni in interfaces)
            {
                var gateway = ni.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (gateway != null)
                    return gateway.Address.ToString();
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TunService] 获取默认网关失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取 TUN 接口的索引号。
    /// 按优先级检测：接口名称 → 描述 → TUN 地址段 IP（兜底）。
    /// xray-core 在 Windows 上通过 wintun 创建接口，接口名由配置的 name 字段决定；
    /// 若名称不匹配，则用 IP 10.255.0.x 兜底查找。
    /// </summary>
    public int? GetTunInterfaceIndex(string tunInterfaceName = "xray-tun")
    {
        try
        {
            var tunInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni =>
                    // 1. 接口连接名精确匹配
                    ni.Name.Equals(tunInterfaceName, StringComparison.OrdinalIgnoreCase)
                    // 2. 适配器描述包含名称（wintun 描述格式："{name} Tunnel #N"）
                    || ni.Description.Contains(tunInterfaceName, StringComparison.OrdinalIgnoreCase)
                    // 3. 兜底：接口持有我们配置的 TUN 地址段 IP
                    || ni.GetIPProperties().UnicastAddresses
                        .Any(a => a.Address.ToString() == TunGateway));

            if (tunInterface != null)
            {
                var ipv4Props = tunInterface.GetIPProperties().GetIPv4Properties();
                Debug.WriteLine($"[TunService] 找到 TUN 接口: Name={tunInterface.Name}, Desc={tunInterface.Description}, Index={ipv4Props.Index}");
                return ipv4Props.Index;
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TunService] 获取 TUN 接口索引失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取已创建的 TUN 接口的实际网关 IP（优先读取接口真实地址，兜底用配置值）。
    /// </summary>
    public string GetActualTunGateway(string tunInterfaceName = "xray-tun")
    {
        try
        {
            var tunInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni =>
                    ni.Name.Equals(tunInterfaceName, StringComparison.OrdinalIgnoreCase)
                    || ni.Description.Contains(tunInterfaceName, StringComparison.OrdinalIgnoreCase)
                    || ni.GetIPProperties().UnicastAddresses
                        .Any(a => a.Address.ToString() == TunGateway));

            if (tunInterface != null)
            {
                var ipv4 = tunInterface.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (ipv4 != null)
                {
                    Debug.WriteLine($"[TunService] 实际 TUN 网关: {ipv4.Address}");
                    return ipv4.Address.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TunService] 读取 TUN 接口 IP 失败: {ex.Message}");
        }
        return TunGateway; // 兜底使用配置值
    }

    /// <summary>
    /// 设置 TUN 路由（将所有流量路由到 TUN 接口，服务器和 DNS 地址保持直连）
    /// </summary>
    /// <param name="serverAddress">代理服务器地址（需要保持直连，不走 TUN）</param>
    public bool SetupTunRoutes(string serverAddress)
    {
        try
        {
            var originalGateway = GetDefaultGateway();
            if (string.IsNullOrEmpty(originalGateway))
            {
                Debug.WriteLine("[TunService] 无法获取原始网关，跳过路由设置");
                return false;
            }

            var tunInterfaceIndex = GetTunInterfaceIndex();
            if (tunInterfaceIndex == null)
            {
                Debug.WriteLine("[TunService] 无法获取 TUN 接口索引，跳过路由设置");
                return false;
            }

            // 读取实际 TUN 网关 IP（可能与配置值不同）
            var actualTunGateway = GetActualTunGateway();

            Debug.WriteLine($"[TunService] 原始网关: {originalGateway}");
            Debug.WriteLine($"[TunService] 代理服务器: {serverAddress}");
            Debug.WriteLine($"[TunService] TUN 接口索引: {tunInterfaceIndex}");
            Debug.WriteLine($"[TunService] TUN 网关: {actualTunGateway}");

            if (!IsSafeRouteTarget(serverAddress))
            {
                Debug.WriteLine("[TunService] 服务器地址包含非法字符，已阻止路由设置");
                return false;
            }

            var commands = new List<string>
            {
                // 代理服务器直连（绕过 TUN，防止代理连接回路）
                $"add {serverAddress} mask 255.255.255.255 {originalGateway} metric 5",
            };

            // DNS 服务器直连（防止 DNS 查询进入 TUN 形成回路）
            foreach (var dns in DnsServers)
            {
                if (IsSafeRouteTarget(dns))
                    commands.Add($"add {dns} mask 255.255.255.255 {originalGateway} metric 5");
            }

            // 0.0.0.0/1 和 128.0.0.0/1 共同覆盖全部 IP 空间，优先级高于默认路由
            commands.Add($"add 0.0.0.0 mask 128.0.0.0 {actualTunGateway} metric 5 if {tunInterfaceIndex}");
            commands.Add($"add 128.0.0.0 mask 128.0.0.0 {actualTunGateway} metric 5 if {tunInterfaceIndex}");

            if (!RunRouteCommands(commands))
            {
                Debug.WriteLine("[TunService] TUN 路由设置失败");
                return false;
            }

            Debug.WriteLine("[TunService] TUN 路由设置完成");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TunService] 设置 TUN 路由失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>清理 TUN 路由（包括代理服务器直连路由和 DNS 直连路由）</summary>
    public void CleanupTunRoutes(string? serverAddress)
    {
        try
        {
            var commands = new List<string>
            {
                "delete 0.0.0.0 mask 128.0.0.0",
                "delete 128.0.0.0 mask 128.0.0.0"
            };

            if (!string.IsNullOrEmpty(serverAddress) && IsSafeRouteTarget(serverAddress))
                commands.Add($"delete {serverAddress}");

            // 清理 DNS 服务器直连路由
            foreach (var dns in DnsServers)
            {
                if (IsSafeRouteTarget(dns))
                    commands.Add($"delete {dns} mask 255.255.255.255");
            }

            RunRouteCommands(commands);
            Debug.WriteLine("[TunService] TUN 路由清理完成");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TunService] 清理 TUN 路由失败: {ex.Message}");
        }
    }

    private static bool IsSafeRouteTarget(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch is '&' or '|' or '>' or '<' or '^')
                return false;
        }

        return true;
    }

    private bool RunRouteCommands(IReadOnlyList<string> commands)
    {
        if (commands.Count == 0)
            return true;

        return AdminHelper.IsAdministrator()
            ? RunRouteCommandsDirect(commands)
            : RunRouteCommandsElevated(commands);
    }

    private bool RunRouteCommandsDirect(IReadOnlyList<string> commands)
    {
        var success = true;
        foreach (var command in commands)
            success &= RunRouteCommandInternal(command);
        return success;
    }

    private bool RunRouteCommandsElevated(IReadOnlyList<string> commands)
    {
        var commandLine = string.Join(" & ", commands.Select(cmd => $"route {cmd}"));
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var psi = new ProcessStartInfo
        {
            FileName = cmdPath,
            Arguments = "/c " + commandLine,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return false;

            process.WaitForExit(5000);
            Debug.WriteLine($"[TunService] route 批处理退出代码: {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Debug.WriteLine("[TunService] 管理员授权被取消");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TunService] route 执行失败: {ex.Message}");
            return false;
        }
    }

    private bool RunRouteCommandInternal(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "route",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(2000); // 2s 以适应 CLR 进程退出回调的时间限制
            Debug.WriteLine($"[TunService] route {arguments} - 退出代码: {process?.ExitCode}");
            return process?.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TunService] route 命令执行失败: {ex.Message}");
            return false;
        }
    }
}
